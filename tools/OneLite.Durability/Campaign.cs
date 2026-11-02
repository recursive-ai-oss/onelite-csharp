using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RecursiveAI.OneLite.Oracle;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Durability;

/// <summary>The built-in writer process: the pinned native library running the <see cref="Workload"/> until it is killed.</summary>
internal static class Writer
{
    /// <summary>
    /// <c>writer DB LEDGER JOURNAL_MODE SYNCHRONOUS [ledger-early]</c>. Prints <c>READY</c> once the database is open and set up, then commits
    /// transactions forever, appending each number to LEDGER only after its COMMIT returned.
    /// </summary>
    public static int Run(string[] a)
    {
        string dbPath = a[0], ledger = a[1], mode = a[2], sync = a[3];
        bool earlyLedger = a.Length > 4 && a[4] == "ledger-early";
        using SqliteDb db = SqliteDb.Open(dbPath, busyTimeoutMs: 30_000);
        db.Exec($"PRAGMA journal_mode={mode}; PRAGMA synchronous={sync};");
        db.Exec("BEGIN;\n" + Workload.Schema + "\nCOMMIT;");
        long next = long.Parse(db.Scalar("select n from counter where id = 1")!, CultureInfo.InvariantCulture) + 1;
        using SqliteStatement insert = db.Prepare(Workload.InsertSql);
        Console.Out.WriteLine("READY");
        Console.Out.Flush();
        using var led = new FileStream(ledger, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        for (long i = next; ; i++)
        {
            if (earlyLedger)
            {
                // Deliberate bug for proving the checker catches it: acknowledge before committing.
                byte[] early = System.Text.Encoding.ASCII.GetBytes(i.ToString(CultureInfo.InvariantCulture) + "\n");
                led.Write(early, 0, early.Length);
                led.Flush();
            }
            Workload.Run(db, insert, i);
            if (earlyLedger)
            {
                continue;
            }
            // The ledger line is written with one write() after COMMIT returned. A process kill cannot lose a write the
            // kernel has accepted; a power cut could, which the fault-injecting VFS tests cover instead.
            byte[] line = System.Text.Encoding.ASCII.GetBytes(i.ToString(CultureInfo.InvariantCulture) + "\n");
            led.Write(line, 0, line.Length);
            led.Flush();
        }
    }
}

/// <summary>The kill campaign: start a writer, kill it at a random moment, check the file, repeat.</summary>
internal static class Campaign
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static int Run(string[] args)
    {
        string mode = "wal", sync = "full", work = Path.Combine(Path.GetTempPath(), "onelite-durability-" + Environment.ProcessId);
        string? writerCmd = null, jsonOut = null, fault = null;
        bool attach = false;
        int kills = 1000, episode = 25, maxDelay = 300;
        int seed = 1;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--mode": mode = args[++i]; break;
                case "--sync": sync = args[++i]; break;
                case "--kills": kills = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--episode": episode = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--seed": seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--max-delay-ms": maxDelay = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--work": work = args[++i]; break;
                case "--writer": writerCmd = args[++i]; break;
                case "--json": jsonOut = args[++i]; break;
                case "--fault": fault = args[++i]; break;
                case "--attach": attach = true; break;
                default: Console.Error.WriteLine("campaign: unknown option " + args[i]); return 2;
            }
        }
        string header = PinnedSqlite.Header();
        Console.Write(header);
        Directory.CreateDirectory(work);
        work = Path.GetFullPath(work);
        var rnd = new Random(seed);
        string db = Path.Combine(work, "kill.db"), aux = Path.Combine(work, "kill-aux.db"), ledger = Path.Combine(work, "ledger.txt"), chkDir = Path.Combine(work, "chk");
        Directory.CreateDirectory(chkDir);

        int corrupt = 0, lost = 0, partial = 0, phantom = 0, hot = 0, startFailures = 0, noProgress = 0;
        long acked = 0;
        var failures = new List<string>();
        var sw = Stopwatch.StartNew();
        long seen = 0;   // the highest transaction a previous check found in this episode: a later check must never see fewer
        for (int k = 0; k < kills; k++)
        {
            if (k % episode == 0)
            {
                PinnedSqlite.DeleteFileSet(db);
                if (attach)
                {
                    PinnedSqlite.DeleteFileSet(aux);
                    foreach (string mj in Directory.GetFiles(work, "kill.db-mj*"))
                    {
                        File.Delete(mj);
                    }
                }

                File.Delete(ledger);
                seen = 0;
            }
            long before = LastLedger(ledger);

            List<string> cmd = writerCmd == null ? BuiltIn(db, ledger, mode, sync, fault) : [.. writerCmd.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Replace("{aux}", aux, StringComparison.Ordinal).Replace("{db}", db, StringComparison.Ordinal).Replace("{ledger}", ledger, StringComparison.Ordinal).Replace("{journal}", mode, StringComparison.Ordinal).Replace("{sync}", sync, StringComparison.Ordinal))];
            using Process p = Proc.Start(cmd[0], cmd.Skip(1));
            Task<string> err = p.StandardError.ReadToEndAsync();
            // Most kills land after the writer is set up and running; some land during start-up and recovery.
            bool early = rnd.Next(10) == 0;
            bool ready = false;
            if (!early)
            {
                Task<string?> line = p.StandardOutput.ReadLineAsync();
                ready = line.Wait(TimeSpan.FromSeconds(60)) && line.Result == "READY";
                if (!ready)
                {
                    startFailures++;
                    failures.Add($"kill {k}: writer did not report READY: {Trim(err, p)}");
                }
            }
            int delay = early ? rnd.Next(20, 150) : rnd.Next(4) == 0 ? rnd.Next(0, 6) : rnd.Next(0, maxDelay + 1);
            System.Threading.Thread.Sleep(delay);
            // Only the process this loop started is killed. SIGKILL: no handler runs, nothing is flushed or cleaned up.
            if (!p.HasExited)
            {
                p.Kill();
            }
            p.WaitForExit();

            long acknowledged = LastLedger(ledger);
            acked += acknowledged - before;
            if (acknowledged == before && !early)
            {
                noProgress++;
            }
            bool hotNow = (File.Exists(db + "-journal") && new FileInfo(db + "-journal").Length > 0) || (File.Exists(db + "-wal") && new FileInfo(db + "-wal").Length > 0);
            if (attach)
            {
                hotNow |= File.Exists(aux + "-journal") && new FileInfo(aux + "-journal").Length > 0;
            }

            if (hotNow)
            {
                hot++;
            }

            Workload.Verdict v;
            if (attach)
            {
                v = JudgeAttached(db, aux, k % 2 == 0);
            }
            else
            {
                // Alternate: check a copy (the next writer then has to do the recovery itself) or check in place.
                string target = db;
                if (k % 2 == 0)
                {
                    target = Path.Combine(chkDir, "copy.db");
                    PinnedSqlite.CopyFileSet(db, target);
                }

                // OneLite's own recovery and B-tree check, on a copy of the same crash image, before the pinned library touches it.
                string oursPath = Path.Combine(chkDir, "ours.db");
                PinnedSqlite.CopyFileSet(db, oursPath);
                string? ourProblem = OurIntegrity(oursPath);
                PinnedSqlite.DeleteFileSet(oursPath);
                string? keep = Environment.GetEnvironmentVariable("ONELITE_KEEP_IMAGES");
                if (ourProblem != null && keep != null)
                {
                    // The crash image as the killed writer left it, for a closer look.
                    Directory.CreateDirectory(keep);
                    PinnedSqlite.CopyFileSet(db, Path.Combine(keep, $"{mode}-{sync}-kill{k}.db"));
                }
                try
                {
                    v = Workload.Check(target);
                }
                catch (SqliteException e)
                {
                    v = new Workload.Verdict(-1, [$"cannot open: {e.Message} (extended code {e.ExtendedCode})"]);
                }
                if (target != db)
                {
                    PinnedSqlite.DeleteFileSet(target);
                }
                if (ourProblem != null)
                {
                    v.Violations.Add("integrity_check (OneLite): " + ourProblem);
                }

            }

            if (v.Violations.Count > 0)
            {
                bool isCorrupt = v.Violations.Any(x => x.StartsWith("integrity_check", StringComparison.Ordinal) || x.StartsWith("cannot open", StringComparison.Ordinal) || x.StartsWith("partial schema", StringComparison.Ordinal));
                if (isCorrupt)
                {
                    corrupt++;
                }
                else
                {
                    partial++;
                }
                failures.Add($"kill {k}: ledger {acknowledged}, found {v.LastTxn}: {string.Join("; ", v.Violations)}");
            }
            else if (v.LastTxn < Math.Max(acknowledged, seen))
            {
                lost++;
                failures.Add($"kill {k}: ledger acknowledged {acknowledged} (earlier checks saw {seen}) but the file holds {v.LastTxn}");
            }
            else if (v.LastTxn > Math.Max(acknowledged, seen) + 1)
            {
                // At most one commit can be missing from the ledger per kill: the one whose COMMIT returned just before the kill.
                phantom++;
                failures.Add($"kill {k}: the file holds {v.LastTxn}, more than one past the ledger's {acknowledged} and the earlier checks' {seen}");
            }
            if (v.Violations.Count == 0)
            {
                seen = Math.Max(seen, v.LastTxn);
            }
            if ((k + 1) % 100 == 0)
            {
                Console.WriteLine($"  {k + 1}/{kills} kills, {acked} acknowledged commits, failures {failures.Count}, {sw.Elapsed.TotalSeconds:F0}s");
            }
        }
        var summary = new
        {
            oracle = header,
            mode,
            synchronous = sync,
            kills,
            seed,
            episode,
            maxDelayMs = maxDelay,
            acknowledgedCommits = acked,
            killsWithHotJournalOrWal = hot,
            killsWithNoCommitProgress = noProgress,
            corruptions = corrupt,
            lostAcknowledgedCommits = lost,
            partialCommitsVisible = partial,
            commitsBeyondLedger = phantom,
            writerStartFailures = startFailures,
            failures,
            seconds = sw.Elapsed.TotalSeconds,
        };
        Console.WriteLine($"mode={mode} synchronous={sync} kills={kills} acknowledged={acked} hot-at-kill={hot}: corruptions {corrupt}, lost acknowledged commits {lost}, partial commits visible {partial}, commits beyond ledger {phantom}, start failures {startFailures}");
        foreach (string f in failures.Take(20))
        {
            Console.WriteLine("  FAIL " + f);
        }
        if (jsonOut != null)
        {
            File.WriteAllText(jsonOut, JsonSerializer.Serialize(summary, JsonOptions) + "\n");
        }
        PinnedSqlite.DeleteFileSet(db);
        try
        {
            Directory.Delete(work, recursive: true);
        }
        catch (IOException)
        {
            // scratch
        }
        return corrupt + lost + partial + phantom + startFailures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Judges the two databases of an ATTACH writer where the killed writer left them (a copy would name journals and a super-journal at the old
    /// paths). One run lets OneLite recover and check both before the pinned library looks, the next the other way round. Both must hold the same
    /// transactions 1..M: that is what an atomic multi-database commit means.
    /// </summary>
    private static Workload.Verdict JudgeAttached(string db, string aux, bool oursFirst)
    {
        var bad = new List<string>();
        if (oursFirst)
        {
            foreach (string f in new[] { db, aux })
            {
                if (OurIntegrity(f) is { } problem)
                {
                    bad.Add("integrity_check (OneLite): " + f + ": " + problem);
                }
            }
        }

        Workload.Verdict a, b;
        try
        {
            a = Workload.Check(db);
            b = Workload.Check(aux);
        }
        catch (SqliteException e)
        {
            return new Workload.Verdict(-1, [$"cannot open: {e.Message} (extended code {e.ExtendedCode})", .. bad]);
        }

        bad.AddRange(a.Violations);
        bad.AddRange(b.Violations.Select(x => "aux: " + x));
        if (bad.Count == 0 && a.LastTxn != b.LastTxn)
        {
            bad.Add($"partial multi-database commit: main holds transaction {a.LastTxn}, the attached database {b.LastTxn}");
        }

        if (!oursFirst && bad.Count == 0)
        {
            foreach (string f in new[] { db, aux })
            {
                if (OurIntegrity(f) is { } problem)
                {
                    bad.Add("integrity_check (OneLite): " + f + ": " + problem);
                }
            }
        }

        return new Workload.Verdict(a.LastTxn, bad);
    }

    private static string Trim(Task<string> err, Process p) => p.HasExited && err.IsCompleted ? err.Result.Trim() : "(still running)";

    private static List<string> BuiltIn(string db, string ledger, string mode, string sync, string? fault)
    {
        var args = new List<string> { "writer", db, ledger, mode, sync };
        if (fault != null)
        {
            args.Add(fault);
        }
        (string exe, List<string> a) = Proc.Self(args);
        a.Insert(0, exe);
        return a;
    }

    private static long LastLedger(string ledger)
    {
        if (!File.Exists(ledger))
        {
            return 0;
        }
        // Every line is "<n>\n" written whole; a torn last line (not possible after a clean write()) would be ignored.
        string[] lines = File.ReadAllText(ledger).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? 0 : long.Parse(lines[^1], CultureInfo.InvariantCulture);
    }

    /// <summary>Opens the file with OneLite's storage engine (recovering a hot journal or the WAL as any opener would) and checks every B-tree.</summary>
    private static string? OurIntegrity(string path)
    {
        try
        {
            using StorageEngine eng = StorageEngine.Open(new FileVfs(), path, new PagerOptions());
            eng.BeginRead();
            try
            {
                if (eng.Pager.PageCount == 0)
                {
                    // A file the writer was killed before it wrote anything to: an empty database, valid as it is.
                    return null;
                }

                BtContext ctx = eng.Context;
                var trees = new List<BTree> { BTree.Table(ctx, 1) };
                foreach (SchemaEntry e in SchemaLite.Read(ctx, eng.Pager.ExecuteSync))
                {
                    if (e.RootPage == 0)
                    {
                        continue;
                    }

                    // A WITHOUT ROWID table (the FTS5 index table is one) is stored as an index B-tree.
                    bool withoutRowid = e.Type == "table" && e.Sql != null && e.Sql.Contains("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase);
                    trees.Add(e.Type == "table" && !withoutRowid ? BTree.Table(ctx, e.RootPage) : BTree.Index(ctx, e.RootPage, KeyInfo.Binary(ctx.Encoding, 0)));
                }

                var chk = new BtIntegrity(ctx, eng.Pager.ExecuteSync);
                return chk.Check(trees) ? null : string.Join("; ", chk.Problems.Take(3));
            }
            finally
            {
                eng.EndRead();
            }
        }
        catch (OneLiteException e)
        {
            return "cannot open: " + e.Message;
        }
    }
}
