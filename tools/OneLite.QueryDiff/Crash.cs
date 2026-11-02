using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneLite;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.QueryDiff;

// querydiff crash: crash images of the pinned sqlite3 as a writer, read back by OneLite and by sqlite3.
//
//   crash --mode journal|wal --images N [--seed S] [--jobs J] [--work DIR] [--keep]
//
// A writer (the sqlite3 shell running a long script of multi-page transactions, and in WAL mode checkpoints of every kind) is killed with SIGKILL
// after a random delay. The files it leaves (database, rollback journal or WAL, and in WAL mode the image with and without -shm) are the image.
// Each image is copied three times: one for OneLite, one for the sqlite3 shell, one that nobody reads. Then
//   1. both read every table read-only; rows, order and error text must be the same;
//   2. OneLite must not have changed the database, journal or WAL (SHA-256 before and after);
//   3. sqlite3 opens OneLite's copy normally: integrity_check is ok, and its .dump equals the dump of the untouched copy;
//   4. OneLite reads the recovered copy again and must agree with sqlite3 about it.
internal static class Crash
{
    private sealed class Tally
    {
        public int Images;
        public int Variants;
        public int Hot;
        public int WalFrames;
        public int WithoutShm;
        public int Tables;
        public int Identical;
        public int IdenticalErrors;
        public int ReadDiffs;
        public int Modified;
        public int IntegrityBad;
        public int DumpDiffs;
        public int AfterDiffs;
        public readonly List<string> Reports = [];
    }

    public static int Run(List<string> args)
    {
        string mode = "journal", work = Path.Combine(Path.GetTempPath(), "onelite-crash");
        int images = 100, seed = 1, jobs = 4;
        bool keep = false;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--mode": mode = args[++i]; break;
                case "--images": images = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--seed": seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--jobs": jobs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--work": work = args[++i]; break;
                case "--keep": keep = true; break;
                default: Console.Error.WriteLine("crash: unknown option " + args[i]); return 2;
            }
        }

        Directory.CreateDirectory(work);
        var t = new Tally();
        Parallel.For(0, images, new ParallelOptions { MaxDegreeOfParallelism = jobs }, n =>
        {
            string dir = Path.Combine(work, $"{mode}-{seed}-{n}");
            try
            {
                OneImage(mode, seed, n, dir, t, keep);
            }
            finally
            {
                if (!keep)
                {
                    TryDelete(dir);
                }
            }
        });

        foreach (string r in t.Reports.Take(40))
        {
            Console.WriteLine(r);
        }

        Console.WriteLine($"crash {mode} seed={seed}: {t.Images} writers killed, {t.Variants} images read ({t.Hot} with a journal, {t.WalFrames} with WAL frames, {t.WithoutShm} without -shm); " +
            $"{t.Tables} table reads: {t.Identical} identical ({t.IdenticalErrors} identical errors), {t.ReadDiffs} differences; {t.Modified} images changed by OneLite; " +
            $"sqlite3 afterwards: {t.IntegrityBad} integrity failures, {t.DumpDiffs} dump differences; recovered re-read differences {t.AfterDiffs}");
        return t.ReadDiffs + t.Modified + t.IntegrityBad + t.DumpDiffs + t.AfterDiffs == 0 ? 0 : 1;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
            // Left behind in the work directory.
        }
    }

    private static void OneImage(string mode, int seed, int n, string dir, Tally t, bool keep)
    {
        Directory.CreateDirectory(dir);
        var rnd = new Random(unchecked(seed * 1000003 + n));
        bool wal = mode == "wal";
        string jm = wal ? "wal" : new[] { "delete", "truncate", "persist" }[rnd.Next(3)];
        int[] sizes = [512, 1024, 2048, 4096, 8192, 16384];
        int ps = sizes[rnd.Next(sizes.Length)];
        int av = rnd.Next(4) == 0 ? rnd.Next(1, 3) : 0;
        string enc = rnd.Next(5) == 0 ? new[] { "UTF-16le", "UTF-16be" }[rnd.Next(2)] : "UTF-8";
        int rows = rnd.Next(300, 3000);
        string db = Path.Combine(dir, "w.db");
        var setup = new StringBuilder();
        setup.Append($"PRAGMA page_size={ps}; PRAGMA encoding='{enc}'; PRAGMA auto_vacuum={av}; PRAGMA journal_mode={jm};\n");
        setup.Append("CREATE TABLE a(id INTEGER PRIMARY KEY, k TEXT, v INTEGER, b BLOB); CREATE INDEX ai ON a(k); CREATE TABLE c(x, y); CREATE INDEX cx ON c(x, y);\n");
        setup.Append($"WITH RECURSIVE s(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM s WHERE n<{rows}) INSERT INTO a SELECT n, 'k'||(n*7919%1000), n, zeroblob(n%{ps * 2 + 10}) FROM s;\n");
        setup.Append("INSERT INTO c SELECT id, k FROM a WHERE id%3=0;\n");
        Proc.Result sr = SqliteCli.Run(db, setup.ToString());
        if (sr.ExitCode != 0)
        {
            throw new InvalidOperationException("setup failed: " + sr.Stdout + sr.Stderr);
        }

        // The writer: many transactions touching many pages; in WAL mode with checkpoints of every mode between them.
        var script = new StringBuilder();
        script.Append(wal ? $"PRAGMA synchronous={(rnd.Next(2) == 0 ? "FULL" : "NORMAL")}; PRAGMA wal_autocheckpoint={rnd.Next(5, 200)};\n" : "PRAGMA synchronous=FULL;\n");
        string[] ckpt = ["PASSIVE", "FULL", "RESTART", "TRUNCATE"];
        for (int i = 0; i < 4000; i++)
        {
            int m = rnd.Next(3, 40);
            script.Append($"BEGIN; UPDATE a SET v=v+{i}, b=zeroblob((id+{i})%{ps + 50}) WHERE id%{m}={i % m}; ");
            script.Append($"INSERT INTO a(k,v,b) VALUES('n{i}',{i},zeroblob({rnd.Next(0, ps * 3)})); ");
            script.Append($"DELETE FROM a WHERE id%{rnd.Next(40, 90)}={i % 40} AND id>{rows / 2}; ");
            script.Append("INSERT INTO c SELECT v, k FROM a ORDER BY id DESC LIMIT 3; COMMIT;\n");
            if (wal && rnd.Next(25) == 0)
            {
                script.Append($"PRAGMA wal_checkpoint({ckpt[rnd.Next(4)]});\n");
            }
        }

        var psi = new ProcessStartInfo(OraclePaths.Cli) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-batch");
        psi.ArgumentList.Add("-init");
        psi.ArgumentList.Add("/dev/null");
        psi.ArgumentList.Add(db);
        using var p = Process.Start(psi)!;
        Task feed = Task.Run(() =>
        {
            try
            {
                p.StandardInput.Write(script.ToString());
                p.StandardInput.Close();
            }
            catch (IOException)
            {
                // The writer was killed first.
            }
        });
        Task drain = Task.Run(() => p.StandardOutput.ReadToEnd());
        Task drain2 = Task.Run(() => p.StandardError.ReadToEnd());
        Thread.Sleep(rnd.Next(40, 700));
        try
        {
            p.Kill(true);
        }
        catch (InvalidOperationException)
        {
            // Already finished.
        }

        p.WaitForExit();
        Task.WaitAll(feed, drain, drain2);

        string[] sides = ["-journal", "-wal", "-shm"];
        long journalLen = File.Exists(db + "-journal") ? new FileInfo(db + "-journal").Length : 0;
        long walLen = File.Exists(db + "-wal") ? new FileInfo(db + "-wal").Length : 0;
        lock (t)
        {
            t.Images++;
            if (journalLen > 0)
            {
                t.Hot++;
            }

            if (walLen > 32)
            {
                t.WalFrames++;
            }
        }

        // Variants: the image as left; in WAL mode also without its -shm.
        var variants = new List<(string Name, bool Shm)> { ("as-left", true) };
        if (wal && File.Exists(db + "-shm"))
        {
            variants.Add(("no-shm", false));
        }

        foreach ((string name, bool shm) in variants)
        {
            string vdir = Path.Combine(dir, name);
            string[] copies = ["ref", "our", "ctl"];
            foreach (string c in copies)
            {
                Directory.CreateDirectory(Path.Combine(vdir, c));
                File.Copy(db, Path.Combine(vdir, c, "w.db"));
                foreach (string s in sides)
                {
                    if (File.Exists(db + s) && (shm || s != "-shm"))
                    {
                        File.Copy(db + s, Path.Combine(vdir, c, "w.db" + s));
                    }
                }
            }

            CheckVariant(t, Path.Combine(vdir, "ref", "w.db"), Path.Combine(vdir, "our", "w.db"), Path.Combine(vdir, "ctl", "w.db"), $"{mode}-{seed}-{n}/{name} (ps={ps} av={av} {enc} {jm})", shm);
        }
    }

    private static string Hash(string db)
    {
        var sb = new StringBuilder();
        foreach (string s in new[] { "", "-journal", "-wal" })
        {
            sb.Append(File.Exists(db + s) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(db + s))) : "-").Append('|');
        }

        return sb.ToString();
    }

    private static void CheckVariant(Tally t, string refDb, string ourDb, string ctlDb, string label, bool shm)
    {
        List<string> names = TableList(refDb, out string? listError);
        if (listError != null)
        {
            names = ["sqlite_master"];
        }

        var queries = names.Select(x => "SELECT * FROM " + Introspect.Q(x)).ToList();
        Outcome[] theirs = ShellRunner.RunBatch(refDb, queries);
        string before = Hash(ourDb);
        var local = new List<string>();
        int tables = 0, same = 0, bothErr = 0, diffs = 0;
        using (var d = TryOpen(ourDb, local, label))
        {
            if (d == null)
            {
                diffs++;
            }
            else
            {
                using Connection c = d.Connect();
                for (int i = 0; i < queries.Count; i++)
                {
                    Outcome ours = OursRunner.Run(c, queries[i]);
                    string? diff = Compare.Diff(ours, theirs[i], true);
                    tables++;
                    if (diff is null)
                    {
                        same++;
                        if (ours.HasError)
                        {
                            bothErr++;
                        }
                    }
                    else
                    {
                        diffs++;
                        local.Add($"DIFF {label}: {queries[i]}: {diff}");
                    }
                }
            }
        }

        bool modified = Hash(ourDb) != before;
        if (modified)
        {
            local.Add($"MODIFIED {label}: OneLite changed the database, journal or WAL");
        }

        // sqlite3 now opens OneLite's copy for real (recovery runs): it must be sound and equal to what sqlite3 makes of the untouched copy.
        string integ = SqliteCli.Run(ourDb, "PRAGMA integrity_check;").Stdout.Trim();
        bool bad = integ != "ok";
        if (bad)
        {
            local.Add($"INTEGRITY {label}: {integ[..Math.Min(integ.Length, 150)]}");
        }

        string dumpOur = SqliteCli.Dump(ourDb);
        string dumpCtl = SqliteCli.Dump(ctlDb);
        bool dumpDiff = dumpOur != dumpCtl;
        if (dumpDiff)
        {
            local.Add($"DUMP {label}: sqlite3's dump of OneLite's copy differs from its dump of the untouched copy");
        }

        // The recovered copy (WAL checkpointed and removed on close, journal rolled back): OneLite and the shell must still agree.
        int afterDiffs = 0;
        List<string> reNames = TableList(ourDb, out string? reError);
        if (reError == null)
        {
            var reQueries = reNames.Select(x => "SELECT * FROM " + Introspect.Q(x)).ToList();
            Outcome[] reTheirs = ShellRunner.RunBatch(ourDb, reQueries);
            using var d2 = TryOpen(ourDb, local, label + " (recovered)");
            if (d2 == null)
            {
                afterDiffs++;
            }
            else
            {
                using Connection c2 = d2.Connect();
                for (int i = 0; i < reQueries.Count; i++)
                {
                    string? diff = Compare.Diff(OursRunner.Run(c2, reQueries[i]), reTheirs[i], true);
                    if (diff != null)
                    {
                        afterDiffs++;
                        local.Add($"AFTER {label}: {reQueries[i]}: {diff}");
                    }
                }
            }
        }

        lock (t)
        {
            t.Variants++;
            if (!shm)
            {
                t.WithoutShm++;
            }

            t.Tables += tables;
            t.Identical += same;
            t.IdenticalErrors += bothErr;
            t.ReadDiffs += diffs;
            t.Modified += modified ? 1 : 0;
            t.IntegrityBad += bad ? 1 : 0;
            t.DumpDiffs += dumpDiff ? 1 : 0;
            t.AfterDiffs += afterDiffs;
            t.Reports.AddRange(local);
        }
    }

    private static Database? TryOpen(string path, List<string> local, string label)
    {
        try
        {
            return Database.OpenReadOnly(path, Qd.Options);
        }
        catch (OneLiteException e)
        {
            local.Add($"OPEN {label}: {e.Message}");
            return null;
        }
    }

    private static List<string> TableList(string db, out string? error)
    {
        error = null;
        Proc.Result r = Proc.Run("/bin/sh", ["-c", "exec \"$0\" \"$@\" 2>&1", OraclePaths.Cli, "-batch", "-init", "/dev/null", "-readonly", db, "select name from sqlite_master where type='table' and sql not like 'CREATE VIRTUAL%' order by rowid"]);
        if (r.Stdout.Contains("rror", StringComparison.Ordinal))
        {
            error = r.Stdout.Trim();
            return [];
        }

        return [.. r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)];
    }
}
