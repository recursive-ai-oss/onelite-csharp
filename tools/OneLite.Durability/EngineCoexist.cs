using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using RecursiveAI.OneLite.Oracle;
using RecursiveAI.OneLite.Storage.Pager;

namespace RecursiveAI.OneLite.Durability;

/// <summary>
/// Live coexistence under contention: OneLite's SQL engine and separate pinned <c>sqlite3</c> processes write one file at the same time, each side
/// committing N transactions with busy handling, while readers on both sides keep checking that every transaction is visible whole.
/// A transaction of writer <c>w</c> numbered <c>s</c> inserts three rows into <c>log</c> whose values add up to <c>s*10+1000</c> and sets
/// <c>counter.n</c> of <c>w</c> to <c>s</c>. A reader in one read transaction must find, for every writer, the counter equal to the highest
/// <c>s</c> in <c>log</c> and exactly three rows with the right sum for every <c>s</c> up to it. At the end the file passes <c>integrity_check</c>
/// and holds every transaction of both writers.
/// <c>engine-coexist [--commits N] [--journal delete|wal] [--sync full|normal] [--ps N] [--work DIR]</c>
/// </summary>
internal static class EngineCoexist
{
    private const string Schema = "CREATE TABLE IF NOT EXISTS log(id INTEGER PRIMARY KEY, who TEXT NOT NULL, seq INTEGER NOT NULL, v INTEGER NOT NULL);"
        + "CREATE INDEX IF NOT EXISTS log_ws ON log(who, seq);"
        + "CREATE TABLE IF NOT EXISTS counter(who TEXT PRIMARY KEY, n INTEGER NOT NULL);";

    private static string Txn(string who, long seq)
        => string.Create(CultureInfo.InvariantCulture,
            $"BEGIN IMMEDIATE;INSERT INTO log(who,seq,v) VALUES ('{who}',{seq},{seq}),('{who}',{seq},1000),('{who}',{seq},{seq * 9});INSERT OR REPLACE INTO counter VALUES ('{who}',{seq});COMMIT;\n");

    public static int Run(string[] args)
    {
        int commits = 5000, ps = 4096;
        string journal = "delete", sync = "full";
        string work = Path.Combine(Path.GetTempPath(), "onelite-engine-coexist-" + Environment.ProcessId);
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--commits": commits = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--journal": journal = args[++i]; break;
                case "--sync": sync = args[++i]; break;
                case "--ps": ps = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--work": work = args[++i]; break;
                default: throw new ArgumentException("unknown option " + args[i]);
            }
        }

        Directory.CreateDirectory(work);
        string db = Path.Combine(work, "live.db");
        foreach (string suffix in new[] { string.Empty, "-journal", "-wal", "-shm" })
        {
            File.Delete(db + suffix);
        }

        SqliteCli.Run(db, "PRAGMA page_size=" + ps + ";PRAGMA journal_mode=" + journal + ";" + Schema);
        var problems = new List<string>();
        var stop = false;
        long ourReads = 0;

        // sqlite3 writer: one process fed the whole script.
        var sb = new StringBuilder(".timeout 30000\nPRAGMA synchronous=" + sync + ";\n");
        for (long s = 1; s <= commits; s++)
        {
            sb.Append(Txn("sqlite", s));
        }

        Process writer = Proc.Start(OraclePaths.Cli, [db], redirectIn: true);
        writer.StandardInput.Write(sb.ToString());
        writer.StandardInput.Close();
        var wout = writer.StandardOutput.ReadToEndAsync();
        var werr = writer.StandardError.ReadToEndAsync();

        // sqlite3 reader: the same consistency check, written in SQL, repeated until told to stop.
        string check = "SELECT (SELECT count(*) FROM (SELECT who, seq FROM log GROUP BY who, seq HAVING count(*) <> 3 OR sum(v) <> seq * 10 + 1000)) "
            + "+ (SELECT count(*) FROM counter c WHERE c.n <> (SELECT max(seq) FROM log WHERE who = c.who));";
        var rsb = new StringBuilder(".timeout 30000\n");
        int shellReads = Math.Max(200, commits / 5);
        for (int i = 0; i < shellReads; i++)
        {
            rsb.Append("BEGIN;").Append(check).Append("COMMIT;\n");
        }

        Process reader = Proc.Start(OraclePaths.Cli, [db], redirectIn: true);
        reader.StandardInput.Write(rsb.ToString());
        reader.StandardInput.Close();
        var rout = reader.StandardOutput.ReadToEndAsync();
        var rerr = reader.StandardError.ReadToEndAsync();

        var opts = new DatabaseOptions
        {
            Synchronous = Enum.Parse<SynchronousMode>(sync, true),
            BusyTimeoutMs = 30000,
            JournalMode = Enum.Parse<JournalMode>(journal, true),
        };

        // OneLite reader thread
        var readerThread = new Thread(() =>
        {
            try
            {
                using Database d = Database.Open(db, opts);
                using Connection c = d.Connect();
                while (!Volatile.Read(ref stop))
                {
                    string? bad = OurCheck(c);
                    Interlocked.Increment(ref ourReads);
                    if (bad is not null)
                    {
                        lock (problems)
                        {
                            problems.Add("OneLite reader: " + bad);
                        }

                        return;
                    }
                }
            }
            catch (OneLiteException e)
            {
                lock (problems)
                {
                    problems.Add("OneLite reader failed: " + e.Message + " [" + e.ExtendedCode + "]");
                }
            }
        });
        readerThread.Start();

        // OneLite writer, on this thread
        int ourBusy = 0;
        try
        {
            using Database d = Database.Open(db, opts);
            using Connection c = d.Connect();
            for (long s = 1; s <= commits; s++)
            {
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        Exec(c, "BEGIN IMMEDIATE");
                        Exec(c, string.Create(CultureInfo.InvariantCulture, $"INSERT INTO log(who,seq,v) VALUES ('one',{s},{s}),('one',{s},1000),('one',{s},{s * 9})"));
                        Exec(c, string.Create(CultureInfo.InvariantCulture, $"INSERT OR REPLACE INTO counter VALUES ('one',{s})"));
                        Exec(c, "COMMIT");
                        break;
                    }
                    catch (OneLiteException e) when (e.Code == ResultCode.Busy && attempt < 5)
                    {
                        ourBusy++;
                        if (!c.AutoCommit)
                        {
                            Exec(c, "ROLLBACK");
                        }
                    }
                }
            }
        }
        catch (OneLiteException e)
        {
            lock (problems)
            {
                problems.Add("OneLite writer failed: " + e.Message + " [" + e.ExtendedCode + "] " + e.Detail);
            }
        }

        writer.WaitForExit();
        reader.WaitForExit();
        Volatile.Write(ref stop, true);
        readerThread.Join();
        string wo = wout.Result + werr.Result;
        if (writer.ExitCode != 0 || wo.Contains("Error", StringComparison.Ordinal))
        {
            problems.Add("sqlite3 writer: " + wo.Trim().Split('\n').FirstOrDefault());
        }

        string ro = rout.Result + rerr.Result;
        string[] lines = ro.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Any(l => l != "0"))
        {
            problems.Add("sqlite3 reader: " + lines.First(l => l != "0"));
        }

        string integrity = SqliteCli.Run(db, "PRAGMA integrity_check;").Stdout.Trim();
        if (integrity != "ok")
        {
            problems.Add("integrity_check: " + integrity);
        }

        string total = SqliteCli.Run(db, "SELECT count(*) FROM log;").Stdout.Trim();
        if (total != (6L * commits).ToString(CultureInfo.InvariantCulture))
        {
            problems.Add("log has " + total + " rows, expected " + 6L * commits);
        }

        Console.WriteLine($"engine-coexist journal={journal} sync={sync} ps={ps}: {commits} commits by OneLite ({ourBusy} busy retries) and {commits} by sqlite3, "
            + $"{ourReads} OneLite reads and {lines.Length} sqlite3 reads checked, {problems.Count} problems");
        foreach (string p in problems)
        {
            Console.WriteLine("  " + p);
        }

        if (problems.Count == 0)
        {
            Directory.Delete(work, true);
        }

        return problems.Count == 0 ? 0 : 1;
    }

    private static void Exec(Connection c, string sql)
    {
        using Statement s = c.Prepare(sql);
        while (s.Step())
        {
        }
    }

    /// <summary>One read transaction: every writer's counter equals its highest seq, and each seq up to it has three rows with the right sum.</summary>
    private static string? OurCheck(Connection c)
    {
        Exec(c, "BEGIN");
        try
        {
            var rows = new Dictionary<(string, long), (int N, long Sum)>();
            using (Statement s = c.Prepare("SELECT who, seq, v FROM log"))
            {
                while (s.Step())
                {
                    var key = (s.GetString(0)!, s.GetInt64(1));
                    rows.TryGetValue(key, out (int N, long Sum) cur);
                    rows[key] = (cur.N + 1, cur.Sum + s.GetInt64(2));
                }
            }

            foreach (((string who, long seq), (int n, long sum)) in rows)
            {
                if (n != 3 || sum != (seq * 10) + 1000)
                {
                    return $"{who} txn {seq} has {n} rows summing {sum}";
                }
            }

            using Statement cs = c.Prepare("SELECT who, n FROM counter");
            while (cs.Step())
            {
                string who = cs.GetString(0)!;
                long n = cs.GetInt64(1);
                long max = rows.Keys.Where(k => k.Item1 == who).Select(k => k.Item2).DefaultIfEmpty(0).Max();
                if (n != max)
                {
                    return $"counter of {who} is {n}, log's highest is {max}";
                }
            }

            return null;
        }
        finally
        {
            Exec(c, "COMMIT");
        }
    }
}
