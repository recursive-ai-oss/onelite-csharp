using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneLite.Oracle;
using RecursiveAI.OneLite.Storage.Pager;

namespace RecursiveAI.OneLite.Durability;

/// <summary>
/// Kill campaign for concurrent writers (docs/concurrency.md). Several writer processes run <c>BEGIN CONCURRENT</c> transactions on one WAL file
/// and are all killed with SIGKILL at a random moment, again and again on the same file. After every kill the pinned shell recovers the file and
/// checks it: <c>integrity_check</c>, every acknowledged commit present, and the invariants that tie one transaction's writes together
/// (so a half-replayed transaction shows).
/// <c>conc-kill [--kills N] [--writers W] [--threads T] [--seed S] [--max-delay-ms MS] [--sync full|normal] [--work DIR]</c>;
/// <c>conc-writer DB LEDGER SRC SYNC [THREADS]</c> is the child; with THREADS above 1 it runs that many connections in one process.
/// </summary>
public static class ConcKill
{
    private const string Schema =
        "CREATE TABLE IF NOT EXISTS ev(id INTEGER PRIMARY KEY, src INT NOT NULL, n INT NOT NULL, hot INT NOT NULL, UNIQUE(src, n));" +
        "CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY, src INT NOT NULL, n INT NOT NULL);" +
        "CREATE TABLE IF NOT EXISTS acc(id INTEGER PRIMARY KEY, total INT NOT NULL, cnt INT NOT NULL);";

    public static int Writer(string[] a)
    {
        string db = a[0], ledgerPath = a[1];
        int src = int.Parse(a[2], CultureInfo.InvariantCulture);
        SynchronousMode sync = a[3] == "full" ? SynchronousMode.Full : SynchronousMode.Normal;
        int threads = a.Length > 4 ? int.Parse(a[4], CultureInfo.InvariantCulture) : 1;
        using var ledger = new FileStream(ledgerPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var ready = new CountdownEvent(threads);
        var gate = new object();
        for (int t = 1; t < threads; t++)
        {
            // More than one connection in the process, as an application with several threads has: their commits can share a WAL sync.
            int s2 = src * 100 + t;
            new Thread(() => WriterLoop(db, ledger, gate, s2, sync, ready)) { IsBackground = true }.Start();
        }

        if (threads > 1)
        {
            var main = new Thread(() => WriterLoop(db, ledger, gate, src * 100, sync, ready)) { IsBackground = true };
            main.Start();
            ready.Wait();
            Console.Out.WriteLine("READY");
            Console.Out.Flush();
            main.Join();
            return 0;
        }

        WriterLoop(db, ledger, gate, src, sync, ready, announce: true);
        return 0;
    }

    private static void WriterLoop(string db, FileStream ledger, object gate, int src, SynchronousMode sync, CountdownEvent ready, bool announce = false)
    {
        var opts = new DatabaseOptions { JournalMode = JournalMode.Wal, ConcurrentWrites = true, Synchronous = sync, BusyTimeoutMs = 20000, WalAutoCheckpoint = 200 };
        using Database database = Database.Open(db, opts);
        using Connection c = database.Connect();
        Exec(c, "PRAGMA journal_mode=WAL");
        foreach (string s in Schema.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            Exec(c, s);
        }

        Exec(c, "INSERT OR IGNORE INTO acc VALUES (0, 0, 0)");
        Exec(c, $"INSERT OR IGNORE INTO acc VALUES ({src}, 0, 0)");
        long n = Scalar(c, $"SELECT coalesce(max(n), 0) FROM ev WHERE src = {src}") + 1;
        if (announce)
        {
            Console.Out.WriteLine("READY");
            Console.Out.Flush();
        }
        else
        {
            ready.Signal();
        }

        var rnd = new Random(src * 7919 + Environment.ProcessId);
        for (; ; )
        {
            int hot = n % 4 == 0 ? 1 : 0;
            try
            {
                Exec(c, "BEGIN CONCURRENT");
                if (hot == 1)
                {
                    Exec(c, "UPDATE acc SET total = total + 1, cnt = cnt + 1 WHERE id = 0");
                }
                else
                {
                    _ = Scalar(c, "SELECT total FROM acc WHERE id = 0");
                }

                Exec(c, $"INSERT INTO ev(src, n, hot) VALUES ({src}, {n}, {hot})");
                Exec(c, $"INSERT INTO audit(src, n) VALUES ({src}, {n})");
                if (rnd.Next(5) == 0)
                {
                    // A statement that fails and a savepoint that is rolled back, so those paths are in the log too.
                    try
                    {
                        Exec(c, $"INSERT INTO ev(src, n, hot) VALUES ({src}, {n}, 0)");
                    }
                    catch (OneLiteException)
                    {
                    }

                    Exec(c, "SAVEPOINT s1");
                    Exec(c, $"INSERT INTO audit(src, n) VALUES ({src}, -1)");
                    Exec(c, "ROLLBACK TO s1");
                    Exec(c, "RELEASE s1");
                }

                Exec(c, $"UPDATE acc SET total = total + {n}, cnt = cnt + 1 WHERE id = {src}");
                Exec(c, "COMMIT");
            }
            catch (OneLiteException e) when (e.Code == ResultCode.Busy)
            {
                if (!c.AutoCommit)
                {
                    Exec(c, "ROLLBACK");
                }

                continue;
            }

            byte[] line = System.Text.Encoding.ASCII.GetBytes($"{src} {n}\n");
            lock (gate)
            {
                ledger.Write(line, 0, line.Length);
                ledger.Flush();
            }

            n++;
        }
    }

    private static void Exec(Connection c, string sql)
    {
        using Statement st = c.Prepare(sql);
        while (st.Step())
        {
        }
    }

    private static long Scalar(Connection c, string sql)
    {
        using Statement st = c.Prepare(sql);
        return st.Step() ? st.GetInt64(0) : 0;
    }

    public static int Run(string[] a)
    {
        Dictionary<string, string> o = [];
        for (int i = 0; i + 1 < a.Length; i += 2)
        {
            o[a[i].TrimStart('-')] = a[i + 1];
        }

        int kills = int.Parse(o.GetValueOrDefault("kills", "100"), CultureInfo.InvariantCulture);
        int writers = int.Parse(o.GetValueOrDefault("writers", "3"), CultureInfo.InvariantCulture);
        int seed = int.Parse(o.GetValueOrDefault("seed", "1"), CultureInfo.InvariantCulture);
        int maxDelay = int.Parse(o.GetValueOrDefault("max-delay-ms", "400"), CultureInfo.InvariantCulture);
        string sync = o.GetValueOrDefault("sync", "normal");
        int threads = int.Parse(o.GetValueOrDefault("threads", "1"), CultureInfo.InvariantCulture);
        string work = o.GetValueOrDefault("work") ?? Path.Combine(Path.GetTempPath(), "onelite-conc-kill-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(work);
        string db = Path.Combine(work, "kill.db");
        // The file is made once, up front: several processes converting a fresh file to WAL and creating its tables at the same moment is not what is being killed here.
        using (Database setup = Database.Open(db, new DatabaseOptions { JournalMode = JournalMode.Wal, ConcurrentWrites = true, BusyTimeoutMs = 20000 }))
        using (Connection sc = setup.Connect())
        {
            foreach (string s in Schema.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                Exec(sc, s);
            }
        }

        var rnd = new Random(seed);
        var failures = new List<string>();
        long ackedTotal = 0;
        int landed = 0;
        for (int k = 1; k <= kills; k++)
        {
            var procs = new List<Process>();
            var ledgers = new List<string>();
            for (int w = 1; w <= writers; w++)
            {
                string ledger = Path.Combine(work, $"ledger-{w}.txt");
                ledgers.Add(ledger);
                (string exe, List<string> args) = Proc.Self(["conc-writer", db, ledger, w.ToString(CultureInfo.InvariantCulture), sync, threads.ToString(CultureInfo.InvariantCulture)]);
                procs.Add(Proc.Start(exe, args));
            }

            bool allReady = true;
            foreach (Process p in procs)
            {
                Task<string?> line = p.StandardOutput.ReadLineAsync();
                if (!line.Wait(TimeSpan.FromSeconds(90)) || line.Result != "READY")
                {
                    allReady = false;
                }
            }

            if (!allReady)
            {
                failures.Add($"kill {k}: a writer did not report READY");
            }

            System.Threading.Thread.Sleep(rnd.Next(4) == 0 ? rnd.Next(0, 10) : rnd.Next(0, maxDelay + 1));
            foreach (Process p in procs)
            {
                if (!p.HasExited)
                {
                    p.Kill();
                    landed++;
                }
            }

            foreach (Process p in procs)
            {
                p.WaitForExit();
                p.Dispose();
            }

            string verdict = Verify(db, ledgers, writers, out long acked);
            ackedTotal = acked;
            if (verdict.Length > 0)
            {
                failures.Add($"kill {k}: {verdict}");
                if (failures.Count >= 5)
                {
                    break;
                }
            }

            if (k % 25 == 0)
            {
                Console.WriteLine($"{k} kills, {ackedTotal} acknowledged commits, {failures.Count} failures");
            }
        }

        Console.WriteLine($"conc-kill: {kills} rounds, {landed} writer processes killed, {ackedTotal} acknowledged commits, {failures.Count} failures");
        foreach (string f in failures)
        {
            Console.WriteLine("FAIL " + f);
        }

        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Concurrent writers (threads, one connection each) and a <c>sqlite3</c> process writing with plain BEGIN on the same WAL file at once.
    /// Afterwards: the content equals the sum of the acknowledged commits, both sides pass integrity_check, no rowid is repeated.
    /// <c>conc-coexist [--commits N] [--writers W] [--sync full|normal] [--work DIR]</c>: N commits in all, a third of them by the shell.
    /// </summary>
    public static int Coexist(string[] a)
    {
        Dictionary<string, string> o = [];
        for (int i = 0; i + 1 < a.Length; i += 2)
        {
            o[a[i].TrimStart('-')] = a[i + 1];
        }

        int commits = int.Parse(o.GetValueOrDefault("commits", "3000"), CultureInfo.InvariantCulture);
        int writers = int.Parse(o.GetValueOrDefault("writers", "4"), CultureInfo.InvariantCulture);
        SynchronousMode sync = o.GetValueOrDefault("sync", "normal") == "full" ? SynchronousMode.Full : SynchronousMode.Normal;
        string work = o.GetValueOrDefault("work") ?? Path.Combine(Path.GetTempPath(), "onelite-conc-coexist-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(work);
        string db = Path.Combine(work, "coexist.db");
        var opts = new DatabaseOptions { JournalMode = JournalMode.Wal, ConcurrentWrites = true, Synchronous = sync, BusyTimeoutMs = 60000 };
        using (Database setup = Database.Open(db, opts))
        using (Connection sc = setup.Connect())
        {
            foreach (string s in Schema.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                Exec(sc, s);
            }

            for (int w = 0; w <= writers + 1; w++)
            {
                Exec(sc, $"INSERT OR IGNORE INTO acc VALUES ({w}, 0, 0)");
            }
        }

        int shellSrc = writers + 1;
        int shellCommits = commits / 3;
        int ours = commits - shellCommits;
        string script = Path.Combine(work, "shell.sql");
        using (var sw = new StreamWriter(script))
        {
            sw.WriteLine(".timeout 60000");
            sw.WriteLine("PRAGMA synchronous=" + (sync == SynchronousMode.Full ? "FULL" : "NORMAL") + ";");
            for (int n = 1; n <= shellCommits; n++)
            {
                sw.WriteLine($"BEGIN; INSERT INTO ev(src, n, hot) VALUES ({shellSrc}, {n}, 0); INSERT INTO audit(src, n) VALUES ({shellSrc}, {n}); UPDATE acc SET total = total + {n}, cnt = cnt + 1 WHERE id = {shellSrc}; COMMIT;");
            }
        }

        (string exe, List<string> args) = (OraclePaths.Cli, new List<string> { "-batch", "-init", "/dev/null", db });
        using Process shell = Proc.Start(exe, args, redirectIn: true);
        Task<string> shellOut = shell.StandardOutput.ReadToEndAsync();
        Task<string> shellErr = shell.StandardError.ReadToEndAsync();
        shell.StandardInput.Write(File.ReadAllText(script));
        shell.StandardInput.Close();

        var tasks = new List<Task<(int Done, int Aborts)>>();
        for (int w = 1; w <= writers; w++)
        {
            int src = w;
            int mine = ours / writers + (w <= ours % writers ? 1 : 0);
            tasks.Add(Task.Run(() =>
            {
                using Database d = Database.Open(db, opts);
                using Connection c = d.Connect();
                int aborts = 0;
                for (int n = 1; n <= mine; n++)
                {
                    for (; ; )
                    {
                        try
                        {
                            Exec(c, "BEGIN CONCURRENT");
                            Exec(c, $"INSERT INTO ev(src, n, hot) VALUES ({src}, {n}, 0)");
                            Exec(c, $"INSERT INTO audit(src, n) VALUES ({src}, {n})");
                            Exec(c, $"UPDATE acc SET total = total + {n}, cnt = cnt + 1 WHERE id = {src}");
                            Exec(c, "COMMIT");
                            break;
                        }
                        catch (OneLiteException e) when (e.Code == ResultCode.Busy)
                        {
                            aborts++;
                            if (!c.AutoCommit)
                            {
                                Exec(c, "ROLLBACK");
                            }
                        }
                    }
                }

                return (mine, aborts);
            }));
        }

        int done = 0, abortsTotal = 0;
        foreach (Task<(int Done, int Aborts)> t in tasks)
        {
            done += t.Result.Done;
            abortsTotal += t.Result.Aborts;
        }

        shell.WaitForExit();
        string so = shellOut.Result, se = shellErr.Result;
        var problems = new List<string>();
        if (shell.ExitCode != 0 || se.Length > 0 || so.Contains("Error", StringComparison.Ordinal))
        {
            problems.Add("the shell reported: " + (se + so).Trim());
        }

        // Count what is in the file, from both sides.
        long rows = 0;
        using (Database d = Database.Open(db, opts))
        using (Connection c = d.Connect())
        {
            rows = Scalar(c, "SELECT count(*) FROM ev");
            using Statement ic = c.Prepare("PRAGMA integrity_check");
            ic.Step();
            if (ic.GetString(0) != "ok")
            {
                problems.Add("integrity_check (OneLite): " + ic.GetString(0));
            }
        }

        Proc.Result v = SqliteCli.Run(db,
            "PRAGMA integrity_check;\n" +
            "SELECT 'rows', count(*), count(DISTINCT id), count(DISTINCT src || '/' || n) FROM ev;\n" +
            "SELECT 'bad', a.id FROM acc a WHERE a.id > 0 AND (a.total != coalesce((SELECT sum(n) FROM ev WHERE src = a.id), 0) OR a.cnt != (SELECT count(*) FROM ev WHERE src = a.id) OR a.cnt != (SELECT count(*) FROM audit WHERE src = a.id));\n");
        string[] lines = v.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || lines[0] != "ok")
        {
            problems.Add("integrity_check (sqlite3): " + string.Join("; ", lines.Take(4)));
        }

        foreach (string l in lines.Skip(1))
        {
            string[] f = l.Split('|');
            if (f[0] == "rows" && !(f[1] == f[2] && f[1] == f[3] && long.Parse(f[1], CultureInfo.InvariantCulture) == commits))
            {
                problems.Add($"rows: {l}, expected {commits} distinct");
            }
            else if (f[0] == "bad")
            {
                problems.Add("a source's totals do not match its rows: " + l);
            }
        }

        Console.WriteLine($"conc-coexist: {done} concurrent commits by {writers} writers ({abortsTotal} aborted and retried) + {shellCommits} plain-BEGIN commits by the shell; {rows} rows; {problems.Count} problems");
        foreach (string p in problems)
        {
            Console.WriteLine("PROBLEM " + p);
        }

        return problems.Count == 0 ? 0 : 1;
    }

    // The shell opens the file, which recovers the log, and judges what it finds.
    private static string Verify(string db, List<string> ledgers, int writers, out long acked)
    {
        acked = 0;
        if (!File.Exists(db))
        {
            return string.Empty;
        }

        Proc.Result r = SqliteCli.Run(db,
            ".timeout 20000\nPRAGMA integrity_check;\n" +
            "SELECT 'total', a.id FROM acc a WHERE a.id > 0 AND a.total != coalesce((SELECT sum(n) FROM ev WHERE src = a.id), 0);\n" +
            "SELECT 'cnt', a.id FROM acc a WHERE a.id > 0 AND a.cnt != (SELECT count(*) FROM ev WHERE src = a.id);\n" +
            "SELECT 'audit', (SELECT count(*) FROM ev) - (SELECT count(*) FROM audit);\n" +
            "SELECT 'auditneg', count(*) FROM audit WHERE n < 0;\n" +
            "SELECT 'hot', (SELECT total FROM acc WHERE id = 0) - (SELECT count(*) FROM ev WHERE hot = 1);\n" +
            "SELECT 'rows', src, n FROM ev;\n");
        if (r.ExitCode != 0)
        {
            return "shell failed: " + r.Stderr.Trim() + r.Stdout.Trim();
        }

        string[] lines = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || lines[0] != "ok")
        {
            return "integrity_check: " + string.Join("; ", lines.Take(5));
        }

        var present = new HashSet<(int, long)>();
        foreach (string l in lines.Skip(1))
        {
            string[] f = l.Split('|');
            switch (f[0])
            {
                case "total":
                case "cnt":
                    return $"invariant {f[0]} broken for writer {f[1]}";
                case "audit" when f[1] != "0":
                    return "ev and audit differ by " + f[1];
                case "auditneg" when f[1] != "0":
                    return "a rolled-back savepoint's audit row is visible";
                case "hot" when f[1] != "0":
                    return "the shared counter and the hot rows differ by " + f[1];
                case "rows":
                    present.Add((int.Parse(f[1], CultureInfo.InvariantCulture), long.Parse(f[2], CultureInfo.InvariantCulture)));
                    break;
            }
        }

        foreach (string ledger in ledgers)
        {
            if (!File.Exists(ledger))
            {
                continue;
            }

            foreach (string l in File.ReadLines(ledger))
            {
                string[] f = l.Split(' ');
                if (f.Length != 2)
                {
                    continue;
                }

                acked++;
                if (!present.Contains((int.Parse(f[0], CultureInfo.InvariantCulture), long.Parse(f[1], CultureInfo.InvariantCulture))))
                {
                    return $"acknowledged commit lost: writer {f[0]} n={f[1]}";
                }
            }
        }

        _ = writers;
        return string.Empty;
    }

    /// <summary>
    /// Indicative throughput: W threads, one connection each, run small write transactions (a read, five inserts, an update of their own row) for a fixed time,
    /// with <c>BEGIN CONCURRENT</c> or with <c>BEGIN IMMEDIATE</c> (a single writer at a time). The work inside a transaction is the point: with concurrent
    /// writers it overlaps, with a single writer it does not.
    /// <c>conc-throughput [--mode concurrent|immediate] [--writers W] [--seconds S] [--sync full|normal|off] [--work DIR]</c>
    /// </summary>
    public static int Throughput(string[] a)
    {
        Dictionary<string, string> o = [];
        for (int i = 0; i + 1 < a.Length; i += 2)
        {
            o[a[i].TrimStart('-')] = a[i + 1];
        }

        bool concurrent = o.GetValueOrDefault("mode", "concurrent") == "concurrent";
        int writers = int.Parse(o.GetValueOrDefault("writers", "4"), CultureInfo.InvariantCulture);
        int seconds = int.Parse(o.GetValueOrDefault("seconds", "5"), CultureInfo.InvariantCulture);
        string sy = o.GetValueOrDefault("sync", "normal");
        SynchronousMode sync = sy == "full" ? SynchronousMode.Full : sy == "off" ? SynchronousMode.Off : SynchronousMode.Normal;
        string work = o.GetValueOrDefault("work") ?? Path.Combine(Path.GetTempPath(), "onelite-conc-tp-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(work);
        string db = Path.Combine(work, "tp.db");
        foreach (string f in new[] { db, db + "-wal", db + "-shm" })
        {
            File.Delete(f);
        }

        var opts = new DatabaseOptions { JournalMode = JournalMode.Wal, ConcurrentWrites = true, Synchronous = sync, BusyTimeoutMs = 60000 };
        using (Database setup = Database.Open(db, opts))
        using (Connection sc = setup.Connect())
        {
            Exec(sc, "CREATE TABLE ev(id INTEGER PRIMARY KEY, src INT NOT NULL, n INT NOT NULL, pad TEXT)");
            Exec(sc, "CREATE INDEX evs ON ev(src, n)");
            Exec(sc, "CREATE TABLE acc(id INTEGER PRIMARY KEY, total INT NOT NULL)");
            for (int w = 1; w <= writers; w++)
            {
                Exec(sc, $"INSERT INTO acc VALUES ({w}, 0)");
            }
        }

        long deadline = System.Diagnostics.Stopwatch.GetTimestamp() + (seconds * System.Diagnostics.Stopwatch.Frequency);
        var tasks = new List<Task<(long Commits, long Aborts)>>();
        for (int w = 1; w <= writers; w++)
        {
            int src = w;
            tasks.Add(Task.Run(() =>
            {
                using Database d = Database.Open(db, opts);
                using Connection c = d.Connect();
                long commits = 0, aborts = 0;
                string begin = concurrent ? "BEGIN CONCURRENT" : "BEGIN IMMEDIATE";
                while (System.Diagnostics.Stopwatch.GetTimestamp() < deadline)
                {
                    try
                    {
                        Exec(c, begin);
                        _ = Scalar(c, $"SELECT total FROM acc WHERE id = {src}");
                        for (int k = 0; k < 5; k++)
                        {
                            Exec(c, $"INSERT INTO ev(src, n, pad) VALUES ({src}, {commits * 5 + k}, 'xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx')");
                        }

                        Exec(c, $"UPDATE acc SET total = total + 1 WHERE id = {src}");
                        Exec(c, "COMMIT");
                        commits++;
                    }
                    catch (OneLiteException e) when (e.Code == ResultCode.Busy)
                    {
                        aborts++;
                        if (!c.AutoCommit)
                        {
                            Exec(c, "ROLLBACK");
                        }
                    }
                }

                return (commits, aborts);
            }));
        }

        Task.WaitAll([.. tasks]);
        long total = 0, ab = 0;
        foreach (Task<(long Commits, long Aborts)> t in tasks)
        {
            total += t.Result.Commits;
            ab += t.Result.Aborts;
        }

        Console.WriteLine($"conc-throughput: mode={(concurrent ? "concurrent" : "immediate")} writers={writers} sync={sy} {seconds}s: {total} commits ({total / (double)seconds:F0}/s), {ab} aborts, cores={Environment.ProcessorCount}");
        return 0;
    }
}
