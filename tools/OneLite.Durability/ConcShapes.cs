using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneLite.Oracle;
using RecursiveAI.OneLite.Storage.Pager;

namespace RecursiveAI.OneLite.Durability;

/// <summary>
/// Indicative throughput of <c>BEGIN CONCURRENT</c> against <c>BEGIN IMMEDIATE</c> for transactions with a body that takes time.
/// <c>conc-shapes --shape update|insert|mixed [--mode concurrent|immediate] [--writers W] [--body-us B] [--sync full|normal] [--seconds S]
/// [--rows N] [--hot-pct P] [--isolate thread|process] [--work DIR]</c>
/// <list type="bullet">
/// <item><c>update</c>: a table of N rows; writer w reads 10 rows and updates 5 rows inside its own contiguous key range (its own leaf pages).</item>
/// <item><c>insert</c>: writer w inserts 5 rows with explicit keys into its own key range of one table (its own right edge of the key space, so its own leaf pages).</item>
/// <item><c>mixed</c>: as <c>update</c>, and P percent of the transactions also update one shared hot row.</item>
/// </list>
/// The body time B is a busy loop of B microseconds, half between the reads and the updates and half after them, standing in for application work.
/// Each line of output is one run; the timing columns are per transaction attempt: body (BEGIN to just before COMMIT) and commit (the COMMIT statement, lock wait included).
/// </summary>
public static class ConcShapes
{
    private const string Pad = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";

    private sealed record Config(string Shape, bool Concurrent, int Writers, int BodyUs, string Sync, int Seconds, int Rows, int HotPct, string Db);

    private sealed record Result(long Commits, long Aborts, double BodyMs, double CommitMs, long Attempts);

    private static Dictionary<string, string> Parse(string[] a)
    {
        Dictionary<string, string> o = [];
        for (int i = 0; i + 1 < a.Length; i += 2)
        {
            o[a[i].TrimStart('-')] = a[i + 1];
        }

        return o;
    }

    private static DatabaseOptions Opts(string sync) => new()
    {
        JournalMode = JournalMode.Wal,
        ConcurrentWrites = true,
        Synchronous = sync == "full" ? SynchronousMode.Full : SynchronousMode.Normal,
        BusyTimeoutMs = 60000,
        ConcurrentCommitHoldLimitMs = 0,
    };

    public static int Run(string[] a)
    {
        Dictionary<string, string> o = Parse(a);
        string shape = o.GetValueOrDefault("shape", "update");
        bool concurrent = o.GetValueOrDefault("mode", "concurrent") == "concurrent";
        int writers = int.Parse(o.GetValueOrDefault("writers", "4"), CultureInfo.InvariantCulture);
        int bodyUs = int.Parse(o.GetValueOrDefault("body-us", "100"), CultureInfo.InvariantCulture);
        string sync = o.GetValueOrDefault("sync", "normal");
        int seconds = int.Parse(o.GetValueOrDefault("seconds", "5"), CultureInfo.InvariantCulture);
        int rows = int.Parse(o.GetValueOrDefault("rows", "400000"), CultureInfo.InvariantCulture);
        int hot = int.Parse(o.GetValueOrDefault("hot-pct", "0"), CultureInfo.InvariantCulture);
        string isolate = o.GetValueOrDefault("isolate", "thread");
        string work = o.GetValueOrDefault("work") ?? Path.Combine(Path.GetTempPath(), "onelite-conc-shapes-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(work);

        // The big table is built once per (shape, rows) and copied for every run, so setup is not paid per measurement.
        string template = Path.Combine(work, $"template-{(shape == "insert" ? "insert" : "update")}-{rows}.db");
        if (!File.Exists(template))
        {
            Build(template, shape, rows);
        }

        string db = Path.Combine(work, $"run-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.db");
        foreach (string f in new[] { db, db + "-wal", db + "-shm" })
        {
            File.Delete(f);
        }

        File.Copy(template, db);
        var cfg = new Config(shape, concurrent, writers, bodyUs, sync, seconds, rows, hot, db);
        var results = new List<Result>();
        if (isolate == "process")
        {
            long startAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 3000;
            var procs = new List<Process>();
            for (int w = 1; w <= writers; w++)
            {
                (string exe, List<string> args) = Proc.Self(["conc-shapes-writer", db, shape, concurrent ? "c" : "i", w.ToString(CultureInfo.InvariantCulture),
                    writers.ToString(CultureInfo.InvariantCulture), bodyUs.ToString(CultureInfo.InvariantCulture), sync, seconds.ToString(CultureInfo.InvariantCulture),
                    rows.ToString(CultureInfo.InvariantCulture), hot.ToString(CultureInfo.InvariantCulture), startAt.ToString(CultureInfo.InvariantCulture)]);
                procs.Add(Proc.Start(exe, args));
            }

            foreach (Process p in procs)
            {
                string line = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                string[] f = line.Split(' ');
                results.Add(new Result(long.Parse(f[0], CultureInfo.InvariantCulture), long.Parse(f[1], CultureInfo.InvariantCulture),
                    double.Parse(f[2], CultureInfo.InvariantCulture), double.Parse(f[3], CultureInfo.InvariantCulture), long.Parse(f[4], CultureInfo.InvariantCulture)));
                p.Dispose();
            }
        }
        else
        {
            long deadline = Stopwatch.GetTimestamp() + ((seconds + 1) * Stopwatch.Frequency);
            using var ready = new CountdownEvent(writers);
            using var go = new ManualResetEventSlim();
            var tasks = new List<Task<Result>>();
            for (int w = 1; w <= writers; w++)
            {
                int me = w;
                tasks.Add(Task.Factory.StartNew(() =>
                {
                    using Database d = Database.Open(db, Opts(sync));
                    using Connection c = d.Connect();
                    ready.Signal();
                    go.Wait();
                    return Worker(c, cfg, me, Stopwatch.GetTimestamp() + (seconds * Stopwatch.Frequency));
                }, TaskCreationOptions.LongRunning));
            }

            ready.Wait();
            _ = deadline;
            go.Set();
            Task.WaitAll([.. tasks]);
            foreach (Task<Result> t in tasks)
            {
                results.Add(t.Result);
            }
        }

        Report(cfg, isolate, results);
        return 0;
    }

    public static int ChildWriter(string[] a)
    {
        string db = a[0], shape = a[1];
        bool concurrent = a[2] == "c";
        int me = int.Parse(a[3], CultureInfo.InvariantCulture), writers = int.Parse(a[4], CultureInfo.InvariantCulture), bodyUs = int.Parse(a[5], CultureInfo.InvariantCulture);
        string sync = a[6];
        int seconds = int.Parse(a[7], CultureInfo.InvariantCulture), rows = int.Parse(a[8], CultureInfo.InvariantCulture), hot = int.Parse(a[9], CultureInfo.InvariantCulture);
        long startAt = long.Parse(a[10], CultureInfo.InvariantCulture);
        var cfg = new Config(shape, concurrent, writers, bodyUs, sync, seconds, rows, hot, db);
        using Database d = Database.Open(db, Opts(sync));
        using Connection c = d.Connect();
        long wait = startAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (wait > 0)
        {
            Thread.Sleep((int)wait);
        }

        Result r = Worker(c, cfg, me, Stopwatch.GetTimestamp() + (seconds * Stopwatch.Frequency));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{r.Commits} {r.Aborts} {r.BodyMs} {r.CommitMs} {r.Attempts}"));
        return 0;
    }

    private static void Build(string path, string shape, int rows)
    {
        foreach (string f in new[] { path, path + "-wal", path + "-shm" })
        {
            File.Delete(f);
        }

        using (Database d = Database.Open(path, new DatabaseOptions { JournalMode = JournalMode.Wal, Synchronous = SynchronousMode.Off }))
        using (Connection c = d.Connect())
        {
            Exec(c, "CREATE TABLE t(id INTEGER PRIMARY KEY, v INT NOT NULL, pad TEXT)");
            Exec(c, "CREATE TABLE hot(id INTEGER PRIMARY KEY, n INT NOT NULL)");
            Exec(c, "INSERT INTO hot VALUES (1, 0)");
            if (shape != "insert")
            {
                Exec(c, "BEGIN");
                Exec(c, $"INSERT INTO t SELECT value, 0, '{Pad}' FROM generate_series(1, {rows})");
                Exec(c, "COMMIT");
            }

            Exec(c, "PRAGMA wal_checkpoint(TRUNCATE)");
        }

        File.Delete(path + "-wal");
        File.Delete(path + "-shm");
    }

    private static void Spin(long ticks)
    {
        if (ticks <= 0)
        {
            return;
        }

        long end = Stopwatch.GetTimestamp() + ticks;
        while (Stopwatch.GetTimestamp() < end)
        {
            Thread.SpinWait(20);
        }
    }

    private static Result Worker(Connection c, Config cfg, int me, long deadline)
    {
        long halfBody = cfg.BodyUs * Stopwatch.Frequency / 2_000_000;
        int per = cfg.Rows / cfg.Writers;
        long lo = ((long)(me - 1) * per) + 1;
        // Insert shape: writer w owns the keys from w * 10^9 upward, so every writer appends to its own part of the key space.
        long insertBase = me * 1_000_000_000L;
        var rnd = new Random(me * 7919);
        using Statement begin = c.Prepare(cfg.Concurrent ? "BEGIN CONCURRENT" : "BEGIN IMMEDIATE");
        using Statement commit = c.Prepare("COMMIT");
        using Statement rollback = c.Prepare("ROLLBACK");
        using Statement read = c.Prepare("SELECT sum(v) FROM t WHERE id >= ?1 AND id < ?1 + 10");
        using Statement upd = c.Prepare("UPDATE t SET v = v + 1 WHERE id >= ?1 AND id < ?1 + 5");
        using Statement ins = c.Prepare("INSERT INTO t VALUES (?1, 1, ?2)");
        using Statement hotUpd = c.Prepare("UPDATE hot SET n = n + 1 WHERE id = 1");
        long commits = 0, aborts = 0, attempts = 0, bodyTicks = 0, commitTicks = 0, seq = 0;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            attempts++;
            long t0 = Stopwatch.GetTimestamp();
            long t1 = t0;
            try
            {
                Run(begin);
                if (cfg.Shape == "insert")
                {
                    Spin(halfBody);
                    for (int k = 0; k < 5; k++)
                    {
                        ins.Bind(1, insertBase + seq + k);
                        ins.Bind(2, Pad);
                        Run(ins);
                    }

                    Spin(halfBody);
                }
                else
                {
                    long key = lo + rnd.NextInt64(per - 20);
                    read.Bind(1, key);
                    Run(read);
                    Spin(halfBody);
                    upd.Bind(1, lo + rnd.NextInt64(per - 20));
                    Run(upd);
                    if (cfg.Shape == "mixed" && rnd.Next(100) < cfg.HotPct)
                    {
                        Run(hotUpd);
                    }

                    Spin(halfBody);
                }

                t1 = Stopwatch.GetTimestamp();
                bodyTicks += t1 - t0;
                Run(commit);
                commitTicks += Stopwatch.GetTimestamp() - t1;
                commits++;
                seq += 5;
            }
            catch (OneLiteException e) when (e.Code == ResultCode.Busy)
            {
                aborts++;
                commitTicks += Stopwatch.GetTimestamp() - t1;
                if (!c.AutoCommit)
                {
                    Run(rollback);
                }
            }
        }

        double ms = 1000.0 / Stopwatch.Frequency;
        return new Result(commits, aborts, bodyTicks * ms, commitTicks * ms, attempts);
    }

    private static void Run(Statement st)
    {
        try
        {
            while (st.Step())
            {
            }
        }
        finally
        {
            st.Reset();
        }
    }

    private static void Exec(Connection c, string sql)
    {
        using Statement st = c.Prepare(sql);
        while (st.Step())
        {
        }
    }

    private static void Report(Config cfg, string isolate, List<Result> rs)
    {
        long commits = 0, aborts = 0, attempts = 0;
        double body = 0, commit = 0;
        foreach (Result r in rs)
        {
            commits += r.Commits;
            aborts += r.Aborts;
            attempts += r.Attempts;
            body += r.BodyMs;
            commit += r.CommitMs;
        }

        double perAttempt = Math.Max(1, attempts);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"conc-shapes shape={cfg.Shape} mode={(cfg.Concurrent ? "concurrent" : "immediate")} writers={cfg.Writers} body_us={cfg.BodyUs} sync={cfg.Sync} hot_pct={cfg.HotPct} isolate={isolate} secs={cfg.Seconds} " +
            $"commits_s={commits / (double)cfg.Seconds:F0} aborts_s={aborts / (double)cfg.Seconds:F0} abort_pct={100.0 * aborts / perAttempt:F1} body_ms={body / perAttempt:F3} commit_ms={commit / perAttempt:F3} cores={Environment.ProcessorCount}"));
    }
}
