using System.Diagnostics;
using System.Globalization;
using System.Text;
using RecursiveAI.OneLite;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.QueryDiff;

// querydiff live: a sqlite3 process commits while OneLite reads the same file in a loop.
//
//   live [--commits N] [--mode wal|delete] [--ps N] [--rows K] [--work DIR] [--readers R]
//
// The table c(id, v, pad) has K rows, each wide enough to fill a page. Every transaction of the writer sets v of all rows to the next
// counter value, so a consistent snapshot shows K rows with one value, and the value only grows from one read to the next. In WAL mode the
// writer also runs checkpoints of every kind between transactions. The reader (R threads, each with its own connection) must never see a
// mixed or shrinking state; running into BUSY is allowed in rollback mode (counted), a torn snapshot or any other error never is.
internal static class Live
{
    public static int Run(List<string> args)
    {
        int commits = 2000, ps = 1024, rows = 12, readers = 2;
        string mode = "wal", work = Path.Combine(Path.GetTempPath(), "onelite-live");
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--commits": commits = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--mode": mode = args[++i]; break;
                case "--ps": ps = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--rows": rows = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--readers": readers = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--work": work = args[++i]; break;
                default: Console.Error.WriteLine("live: unknown option " + args[i]); return 2;
            }
        }

        bool wal = mode == "wal";
        string dir = Path.Combine(work, $"live-{mode}-{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        string db = Path.Combine(dir, "live.db");
        int padLen = Math.Max(16, ps * 2 / 3);
        string setup = $"PRAGMA page_size={ps}; PRAGMA journal_mode={(wal ? "wal" : "delete")};\n" +
            $"CREATE TABLE c(id INTEGER PRIMARY KEY, v INTEGER, pad BLOB);\n" +
            $"WITH RECURSIVE s(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM s WHERE n<{rows}) INSERT INTO c SELECT n, 0, zeroblob({padLen}) FROM s;\n";
        Proc.Result sr = SqliteCli.Run(db, setup);
        if (sr.ExitCode != 0)
        {
            Console.Error.WriteLine("setup failed: " + sr.Stdout + sr.Stderr);
            return 2;
        }

        var script = new StringBuilder();
        script.Append(".timeout 20000\nPRAGMA synchronous=").Append("FULL").Append(";\n");
        string[] ck = ["PASSIVE", "FULL", "RESTART", "TRUNCATE"];
        var rnd = new Random(7);
        for (int i = 1; i <= commits; i++)
        {
            script.Append($"BEGIN IMMEDIATE; UPDATE c SET v={i}; COMMIT;\n");
            if (wal && i % 97 == 0)
            {
                script.Append($"PRAGMA wal_checkpoint({ck[rnd.Next(4)]});\n");
            }
        }

        var psi = new ProcessStartInfo(OraclePaths.Cli) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-batch");
        psi.ArgumentList.Add("-init");
        psi.ArgumentList.Add("/dev/null");
        psi.ArgumentList.Add(db);
        using var writer = Process.Start(psi)!;
        Task drainOut = Task.Run(() => writer.StandardOutput.ReadToEnd());
        Task drainErr = Task.Run(() => writer.StandardError.ReadToEnd());
        var sw = Stopwatch.StartNew();
        Task feed = Task.Run(() =>
        {
            writer.StandardInput.Write(script.ToString());
            writer.StandardInput.Close();
        });

        var results = new (long Reads, long Busy, long Torn, long Back, long Other, long Max, long Changes, string First)[readers];
        var threads = new Thread[readers];
        for (int r = 0; r < readers; r++)
        {
            int id = r;
            threads[r] = new Thread(() => results[id] = Reader(db, rows, writer));
            threads[r].Start();
        }

        foreach (Thread th in threads)
        {
            th.Join();
        }

        writer.WaitForExit();
        Task.WaitAll(feed, drainOut, drainErr);
        string finalCheck = SqliteCli.Run(db, "SELECT min(v)||','||max(v)||','||count(*) FROM c; PRAGMA integrity_check;").Stdout.Trim().Replace('\n', ' ');
        long reads = results.Sum(x => x.Reads), busy = results.Sum(x => x.Busy), torn = results.Sum(x => x.Torn), back = results.Sum(x => x.Back), other = results.Sum(x => x.Other);
        foreach (var x in results.Where(x => x.First.Length > 0))
        {
            Console.WriteLine("first problem: " + x.First);
        }

        Console.WriteLine($"live {mode}: writer exit {writer.ExitCode} after {sw.Elapsed.TotalSeconds:F1}s for {commits} commits (ps={ps}, {rows} rows); {readers} readers made {reads} reads, " +
            $"highest value seen {results.Max(x => x.Max)} ({results.Sum(x => x.Changes)} changes of snapshot observed); torn {torn}, went backwards {back}, other errors {other}, busy {busy}; final state {finalCheck}");
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
            // Left behind.
        }

        bool ok = torn == 0 && back == 0 && other == 0 && writer.ExitCode == 0 && finalCheck.EndsWith("ok", StringComparison.Ordinal) && finalCheck.StartsWith($"{commits},{commits},{rows}", StringComparison.Ordinal);
        return ok ? 0 : 1;
    }

    private static (long Reads, long Busy, long Torn, long Back, long Other, long Max, long Changes, string First) Reader(string db, int rows, Process writer)
    {
        long reads = 0, busy = 0, torn = 0, back = 0, other = 0, last = -1, max = 0, changes = 0;
        string first = "";
        var opts = new DatabaseOptions { Busy = n => n < 20000 ? 1 : -1 };
        using Database d = Database.OpenReadOnly(db, opts);
        using Connection c = d.Connect();
        while (!writer.HasExited)
        {
            try
            {
                long lo = long.MaxValue, hi = long.MinValue, n = 0;
                using (Statement st = c.Prepare("SELECT v FROM c"))
                {
                    while (st.Step())
                    {
                        long v = st.GetInt64(0);
                        lo = Math.Min(lo, v);
                        hi = Math.Max(hi, v);
                        n++;
                    }
                }

                reads++;
                if (n != rows || lo != hi)
                {
                    torn++;
                    first = first.Length > 0 ? first : $"read {reads}: {n} rows, values {lo}..{hi}";
                }
                else if (hi < last)
                {
                    back++;
                    first = first.Length > 0 ? first : $"read {reads}: value {hi} after {last}";
                }
                else
                {
                    if (hi != last)
                    {
                        changes++;
                    }

                    last = hi;
                    max = Math.Max(max, hi);
                }
            }
            catch (OneLiteException e) when (e.Code == ResultCode.Busy)
            {
                busy++;
            }
            catch (OneLiteException e)
            {
                other++;
                first = first.Length > 0 ? first : $"read {reads}: {e.Code}/{e.ExtendedCode} {e.Message}";
            }
        }

        return (reads, busy, torn, back, other, max, changes, first);
    }
}
