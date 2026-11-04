using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Oracle;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Durability;

/// <summary>
/// Live coexistence in rollback mode: OneLite's storage engine and a separate pinned <c>sqlite3</c> process take turns committing to one file
/// (lockingv3: both use the same POSIX byte-range locks). Turn <c>s</c> inserts row <c>s</c>, deletes row <c>s-6</c> and sets the counter; each
/// side starts its turn by checking that it sees exactly the other side's last state. Every 25th round runs one of three lock scenarios whose
/// BUSY result lockingv3 predicts; any other BUSY, any wrong row, or a failed <c>integrity_check</c> fails the run.
/// <c>coexist [--commits N] [--journal delete|truncate|persist] [--ps N] [--av 0|1|2] [--work DIR]</c>: N rounds are N commits by each side.
/// </summary>
internal static class Coexist
{
    private static byte[] Payload(long seq)
    {
        var b = new byte[40 + (int)(seq * 37 % 1500)];
        for (int i = 0; i < b.Length; i++)
        {
            b[i] = (byte)((seq * 131) + (i * 7));
        }

        return b;
    }

    private static string Row(long seq, string who) => seq.ToString(CultureInfo.InvariantCulture) + "|" + who + "|" + seq.ToString(CultureInfo.InvariantCulture) + "|" + Convert.ToHexString(Payload(seq));

    public static int Run(string[] args)
    {
        int commits = 1000, ps = 1024, av = 0;
        string journal = "delete";
        string work = Path.Combine(Path.GetTempPath(), "onelite-coexist-" + Environment.ProcessId);
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--commits": commits = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--journal": journal = args[++i]; break;
                case "--ps": ps = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--av": av = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--work": work = args[++i]; break;
                default: Console.Error.WriteLine("coexist: unknown option " + args[i]); return 2;
            }
        }

        // Every expectation below is a rollback-mode one (a reader's SHARED lock blocks a commit, a writer's RESERVED lock blocks the other writer). In WAL
        // a reader never blocks a commit, so WAL is checked by engine-coexist and the WAL kill runs instead (docs/interop-and-durability.md).
        if (!journal.Equals("delete", StringComparison.OrdinalIgnoreCase) && !journal.Equals("truncate", StringComparison.OrdinalIgnoreCase)
            && !journal.Equals("persist", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("coexist: --journal " + journal + " is not a rollback journal mode; use delete, truncate or persist (WAL: engine-coexist --journal wal)");
            return 2;
        }

        Directory.CreateDirectory(work);
        string db = Path.Combine(work, "live.db");
        PinnedSqlite.DeleteFileSet(db);
        var failures = new List<string>();
        int busyPlanned = 0, busyUnplanned = 0, integrityChecks = 0;
        using var lite = new Shell(db);
        lite.Run($"PRAGMA page_size={ps}; PRAGMA auto_vacuum={av};");
        lite.Run($"PRAGMA journal_mode={journal};");
        lite.Run("CREATE TABLE log(seq INTEGER PRIMARY KEY, who TEXT NOT NULL, n INTEGER NOT NULL, payload BLOB NOT NULL); CREATE TABLE counter(id INTEGER PRIMARY KEY, n INTEGER); INSERT INTO counter VALUES(1, 0);");
        lite.Run(".timeout 0");

        using StorageEngine eng = StorageEngine.Open(new FileVfs(), db, new PagerOptions { JournalMode = Enum.Parse<JournalMode>(journal, true), Synchronous = SynchronousMode.Full, CacheSize = 500 });
        var sw = Stopwatch.StartNew();
        long seq = 0;
        for (int round = 1; round <= commits; round++)
        {
            int scenario = round % 25 == 0 ? (round / 25 % 3) + 1 : 0;

            // ---- OneLite's turn: seq+1 -------------------------------------------------------------------------------------------
            long mine = seq + 1;
            if (scenario == 1)
            {
                // sqlite3 holds a SHARED lock; we may read and take RESERVED, but our commit needs EXCLUSIVE: BUSY (lockingv3 §3)
                lite.Run("BEGIN; SELECT count(*) FROM log;");
                try
                {
                    OurTurn(eng, mine, failures, commitAttempt: true);
                    failures.Add($"round {round}: commit succeeded while sqlite3 held SHARED");
                }
                catch (OneLiteException e) when (e.Code == ResultCode.Busy)
                {
                    busyPlanned++;
                }

                lite.Run("COMMIT;");
            }
            else if (scenario == 2)
            {
                // sqlite3 holds RESERVED: we may read the committed state but not take RESERVED
                lite.Run("BEGIN IMMEDIATE;");
                eng.BeginRead();
                CheckState(eng, mine, failures, "OneLite", round);
                try
                {
                    eng.BeginWrite();
                    failures.Add($"round {round}: BeginWrite succeeded while sqlite3 held RESERVED");
                }
                catch (OneLiteException e) when (e.Code == ResultCode.Busy)
                {
                    busyPlanned++;
                }

                eng.EndRead();
                lite.Run("COMMIT;");
            }
            else if (scenario == 3)
            {
                // we hold RESERVED: sqlite3 may read but BEGIN IMMEDIATE must say "database is locked"
                eng.BeginRead();
                eng.BeginWrite();
                string o = lite.Run("BEGIN IMMEDIATE;");
                if (!o.Contains("database is locked", StringComparison.Ordinal))
                {
                    failures.Add($"round {round}: sqlite3 BEGIN IMMEDIATE while we held RESERVED said '{o.Trim()}'");
                }
                else
                {
                    busyPlanned++;
                }

                string rd = lite.Run("SELECT count(*) FROM log;");
                if (rd.Contains("locked", StringComparison.Ordinal))
                {
                    failures.Add($"round {round}: sqlite3 could not read while we held RESERVED");
                }

                eng.Rollback();
                eng.EndRead();
            }

            try
            {
                OurTurn(eng, mine, failures, commitAttempt: false);
            }
            catch (OneLiteException e) when (e.Code == ResultCode.Busy)
            {
                busyUnplanned++;
                failures.Add($"round {round}: unexpected BUSY in our turn: {e.Message} {e.Detail}");
                break;
            }

            seq = mine;

            // ---- sqlite3's turn: seq+1 -------------------------------------------------------------------------------------------
            long theirs = seq + 1;
            string want = string.Join("\n", Enumerable.Range((int)Math.Max(1, seq - 5), (int)Math.Min(seq, 6)).Select(k => "R|" + Row(k, k % 2 == 1 ? "ours" : "lite")));
            string expectCounter = "C|" + seq.ToString(CultureInfo.InvariantCulture);
            string outp = lite.Run(
                "BEGIN IMMEDIATE;"
                + "SELECT 'R|'||seq||'|'||who||'|'||n||'|'||hex(payload) FROM log ORDER BY seq;"
                + "SELECT 'C|'||n FROM counter;"
                + $"INSERT INTO log VALUES({theirs},'lite',{theirs},X'{Convert.ToHexString(Payload(theirs))}');"
                + $"DELETE FROM log WHERE seq = {theirs - 6};"
                + $"UPDATE counter SET n = {theirs};"
                + "COMMIT;");
            string[] lines = outp.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
            string gotRows = string.Join("\n", lines.Where(l => l.StartsWith("R|", StringComparison.Ordinal)));
            if (gotRows != want || !lines.Contains(expectCounter) || lines.Any(l => l.Contains("rror", StringComparison.Ordinal) || l.Contains("locked", StringComparison.Ordinal)))
            {
                failures.Add($"round {round}: sqlite3 saw a state other than our last commit ({seq}): rows [{string.Join(",", lines.Where(l => l.StartsWith("R|", StringComparison.Ordinal)).Select(l => l.Split('|')[1] + (gotRows.Contains(l) && want.Contains(l) ? "" : "!")))}], wanted [{string.Join(",", want.Split('\n').Select(l => l.Split('|')[1]))}], other lines: {string.Join(" / ", lines.Where(l => !l.StartsWith("R|", StringComparison.Ordinal)).Select(Trunc))}");
                break;
            }

            seq = theirs;

            if (round % 50 == 0 || round == commits)
            {
                string ic = lite.Run("PRAGMA integrity_check;").Trim();
                integrityChecks++;
                if (ic != "ok")
                {
                    failures.Add($"round {round}: sqlite3 integrity_check: {Trunc(ic)}");
                    break;
                }
            }

            if (failures.Count > 0)
            {
                break;
            }
        }

        string final = lite.Run("PRAGMA integrity_check;").Trim();
        if (final != "ok")
        {
            failures.Add("final integrity_check: " + Trunc(final));
        }

        Console.WriteLine($"journal={journal} ps={ps} av={av}: {commits} commits each side ({2 * commits} total), planned BUSY outcomes {busyPlanned}, unplanned {busyUnplanned}, sqlite3 integrity checks {integrityChecks + 1}, failures {failures.Count}, {sw.Elapsed.TotalSeconds:F1}s");
        foreach (string f in failures.Take(10))
        {
            Console.WriteLine("  FAIL " + f);
        }

        PinnedSqlite.DeleteFileSet(db);
        return failures.Count == 0 ? 0 : 1;
    }

    private static string Trunc(string s) => s.Length > 400 ? s[..400] : s;

    /// <summary>Checks that the committed state is exactly what the other side left: rows seq-6..seq-1 and the counter.</summary>
    private static void CheckState(StorageEngine eng, long mine, List<string> failures, string who, int round)
    {
        long last = mine - 1;
        List<SchemaEntry> schema = SchemaLite.Read(eng.Context, eng.Pager.ExecuteSync);
        BTree log = BTree.Table(eng.Context, schema.First(e => e.Name == "log").RootPage);
        BTree counter = BTree.Table(eng.Context, schema.First(e => e.Name == "counter").RootPage);
        var got = new List<string>();
        using (BtCursor c = log.OpenCursor())
        {
            var rv = new RecordView();
            Ok(eng, c, c.First);
            while (c.State == CursorState.Valid)
            {
                Ok(eng, c, c.LoadPayload);
                if (!c.ParseRecord(rv))
                {
                    failures.Add($"round {round}: {who} found a malformed record");
                    return;
                }

                got.Add(c.Rowid.ToString(CultureInfo.InvariantCulture) + "|" + TextCodec.Decode(rv.GetBytes(1), eng.Context.Encoding) + "|" + rv.GetInt64(2).ToString(CultureInfo.InvariantCulture) + "|" + Convert.ToHexString(rv.GetBytes(3)));
                Ok(eng, c, c.Next);
            }
        }

        var want = Enumerable.Range((int)Math.Max(1, last - 5), (int)Math.Min(last, 6)).Select(k => Row(k, k % 2 == 1 ? "ours" : "lite")).ToList();
        if (!got.SequenceEqual(want))
        {
            failures.Add($"round {round}: {who} saw rows [{string.Join(",", got.Select(g => g.Split('|')[0]))}], wanted [{string.Join(",", want.Select(g => g.Split('|')[0]))}]");
        }

        using BtCursor cc = counter.OpenCursor();
        int cmp = 1;
        Ok(eng, cc, () => cc.MoveToRowid(1, out cmp));
        Ok(eng, cc, cc.LoadPayload);
        var rv2 = new RecordView();
        if (cmp != 0 || !cc.ParseRecord(rv2) || rv2.GetInt64(1) != last)
        {
            failures.Add($"round {round}: {who} saw the wrong counter");
        }

        if (round % 50 == 0)
        {
            var trees = new List<BTree> { BTree.Table(eng.Context, 1), log, counter };
            var chk = new BtIntegrity(eng.Context, eng.Pager.ExecuteSync);
            if (!chk.Check(trees))
            {
                failures.Add($"round {round}: our integrity check: {string.Join("; ", chk.Problems.Take(3))}");
            }
        }
    }

    private static void Ok(StorageEngine eng, BtCursor c, Func<BtStatus> op)
    {
        if (eng.Run(op) != BtStatus.Ok)
        {
            throw c.Error ?? new InvalidOperationException("B-tree call failed");
        }
    }

    /// <summary>One OneLite commit: verify the other side's state, insert row <paramref name="mine"/>, delete row mine-6, bump the counter.</summary>
    private static void OurTurn(StorageEngine eng, long mine, List<string> failures, bool commitAttempt)
    {
        eng.BeginRead();
        bool ok = false;
        try
        {
            CheckState(eng, mine, failures, "OneLite", (int)((mine + 1) / 2));
            List<SchemaEntry> schema = SchemaLite.Read(eng.Context, eng.Pager.ExecuteSync);
            BTree log = BTree.Table(eng.Context, schema.First(e => e.Name == "log").RootPage);
            BTree counter = BTree.Table(eng.Context, schema.First(e => e.Name == "counter").RootPage);
            eng.BeginWrite();
            using BtCursor c = log.OpenCursor();
            using BtCursor cc = counter.OpenCursor();
            TextEncoding enc = eng.Context.Encoding;
            byte[] rec = RecordWriter.ToArray([Mem.Null, Mem.FromText("ours", enc), Mem.FromInt(mine), Mem.FromBlob(Payload(mine))]);
            Ok(eng, c, () => c.Insert(mine, rec));
            if (mine - 6 >= 1)
            {
                int cmp = 1;
                Ok(eng, c, () => c.MoveToRowid(mine - 6, out cmp));
                if (cmp == 0)
                {
                    Ok(eng, c, c.Delete);
                }
            }

            byte[] cnt = RecordWriter.ToArray([Mem.Null, Mem.FromInt(mine)]);
            Ok(eng, cc, () => cc.Insert(1, cnt));
            c.Reset();
            cc.Reset();
            try
            {
                eng.Commit();
            }
            catch (OneLiteException) when (commitAttempt)
            {
                eng.Rollback();
                throw;
            }

            ok = true;
        }
        finally
        {
            if (ok || commitAttempt)
            {
                eng.EndRead();
            }
        }
    }

    /// <summary>A long-lived pinned <c>sqlite3</c> shell fed statements on standard input; standard error is folded into the output.</summary>
    private sealed class Shell : IDisposable
    {
        private const string Marker = "@@done@@";
        private readonly Process _p;
        private readonly BlockingCollection<string> _lines = [];

        public Shell(string db)
        {
            var psi = new ProcessStartInfo("/bin/sh") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("exec \"$0\" \"$@\" 2>&1");
            psi.ArgumentList.Add(OraclePaths.Cli);
            psi.ArgumentList.Add(db);
            _p = Process.Start(psi)!;
            _p.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    _lines.Add(e.Data);
                }
            };
            _p.BeginOutputReadLine();
        }

        public string Run(string sql)
        {
            _p.StandardInput.WriteLine(sql);
            _p.StandardInput.WriteLine(".print " + Marker);
            _p.StandardInput.Flush();
            var sb = new StringBuilder();
            for (; ; )
            {
                if (!_lines.TryTake(out string? line, 60_000))
                {
                    throw new TimeoutException("sqlite3 did not answer: " + sb);
                }

                if (line == Marker)
                {
                    return sb.ToString();
                }

                sb.AppendLine(line);
            }
        }

        public void Dispose()
        {
            try
            {
                _p.StandardInput.WriteLine(".quit");
                _p.StandardInput.Close();
                if (!_p.WaitForExit(5000))
                {
                    _p.Kill(true);
                }
            }
            catch (IOException)
            {
            }

            _p.Dispose();
            _lines.Dispose();
        }
    }
}
