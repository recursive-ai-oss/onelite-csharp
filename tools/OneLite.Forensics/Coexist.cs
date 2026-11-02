using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Oracle;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Forensics;

namespace RecursiveAI.OneLite.ForensicsTool;

/// <summary>
/// Forensic reads beside live shells. Part 1: a shell holds an EXCLUSIVE lock (an open write transaction) while we read repeatedly; its COMMIT must
/// then succeed and the rows must be the state before it. Part 2: a shell commits one row per transaction in WAL mode with the default automatic
/// checkpoint while we read in a loop; each accepted pass must show a state some commit produced (log rows == counter == the highest row).
/// </summary>
internal static class Coexist
{
    public static int Run(string[] args)
    {
        string work = args.SkipWhile(x => x != "--work").Skip(1).FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "forensic-coexist");
        int seconds = int.Parse(args.SkipWhile(x => x != "--seconds").Skip(1).FirstOrDefault() ?? "30", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(work);
        int bad = Exclusive(work);
        bad += Wal(work, seconds);
        return bad == 0 ? 0 : 1;
    }

    private static int Exclusive(string work)
    {
        string db = Path.Combine(work, "excl.db");
        foreach (string f in new[] { "", "-wal", "-shm", "-journal" })
        {
            File.Delete(db + f);
        }

        Common.Sql(db, "create table t(a integer primary key, b text);\ninsert into t(b) values('one'),('two'),('three');\n");
        using Process p = Proc.Start(OraclePaths.Cli, [db], redirectIn: true);
        p.StandardInput.Write("begin exclusive;\ninsert into t(b) values('uncommitted');\nselect 'locked';\n");
        p.StandardInput.Flush();
        string line = p.StandardOutput.ReadLine() ?? "";
        int ok = 0, wrong = 0, refused = 0;
        for (int i = 0; i < 200; i++)
        {
            try
            {
                using ForensicDatabase f = ForensicDatabase.Open(db);
                ForensicSnapshot s = f.OpenSnapshot();
                string r = Common.RenderSnapshot(s, ["t"]);
                _ = s.Carve();
                if (r == "t|1|1|'one'\nt|2|2|'two'\nt|3|3|'three'\n")
                {
                    ok++;
                }
                else
                {
                    wrong++;
                }
            }
            catch (Exception e) when (e is OneLiteException { Code: ResultCode.Corrupt } or NotADatabaseException)
            {
                refused++;
            }
        }

        p.StandardInput.Write("commit;\nselect 'committed', count(*) from t;\n");
        p.StandardInput.Close();
        string rest = p.StandardOutput.ReadToEnd();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        bool committed = rest.Contains("committed|4", StringComparison.Ordinal) && err.Length == 0 && p.ExitCode == 0;
        Console.WriteLine($"exclusive: shell said '{line}'; reads ok={ok} wrong={wrong} refused={refused}; shell commit succeeded={committed} stderr='{err.Trim()}'");
        return wrong + refused + (committed ? 0 : 1) + (line == "locked" ? 0 : 1);
    }

    private static int Wal(string work, int seconds)
    {
        string db = Path.Combine(work, "walw.db");
        foreach (string f in new[] { "", "-wal", "-shm", "-journal" })
        {
            File.Delete(db + f);
        }

        Common.Sql(db, "pragma journal_mode=wal;\ncreate table log(i integer primary key, v text);\ncreate table counter(n integer);\ninsert into counter values(0);\n");
        // The writer: one transaction per row. busy_timeout 0 so any BUSY caused by a reader would show at once.
        var sb = new StringBuilder("pragma busy_timeout=0;\n");
        int commits = 6000;
        for (int i = 1; i <= commits; i++)
        {
            sb.Append($"begin;\ninsert into log values({i}, '{new string((char)('a' + (i % 26)), 40 + (i % 50))}');\nupdate counter set n={i};\ncommit;\n");
        }

        string script = Path.Combine(work, "writer.sql");
        File.WriteAllText(script, sb.ToString());
        using Process w = Proc.Start("/bin/sh", ["-c", $"'{OraclePaths.Cli}' '{db}' < '{script}'"]);
        var sw = Stopwatch.StartNew();
        int passes = 0, accepted = 0, rejected = 0, consistent = 0, inconsistent = 0, torn = 0;
        long lastN = 0;
        while (!w.HasExited && sw.Elapsed.TotalSeconds < seconds)
        {
            passes++;
            try
            {
                using ForensicDatabase f = ForensicDatabase.Open(db);
                ForensicSnapshot s = f.OpenSnapshot();
                var log = s.ReadRows(s.Tables.First(t => t.Name == "log"));
                var counter = s.ReadRows(s.Tables.First(t => t.Name == "counter"));
                _ = s.Carve();
                bool changed = f.HasChangedSinceOpen();
                if (changed)
                {
                    rejected++;
                    continue;
                }

                accepted++;
                long n = (long)counter[0].Values[0]!;
                bool good = log.Count == n && (n == 0 || log[^1].Rowid == n);
                if (good)
                {
                    consistent++;
                    lastN = Math.Max(lastN, n);
                }
                else
                {
                    inconsistent++;
                    Console.WriteLine($"  inconsistent: log rows {log.Count}, counter {n}, last rowid {(log.Count > 0 ? log[^1].Rowid : -1)}");
                }
            }
            catch (ForensicChangedException)
            {
                torn++;
            }
            catch (Exception e) when (e is OneLiteException { Code: ResultCode.Corrupt } or NotADatabaseException)
            {
                // A pass that meets a frame mid-rewrite may see a page that never was; count it with the torn ones, but say so.
                torn++;
            }
        }

        if (!w.HasExited)
        {
            w.WaitForExit(TimeSpan.FromMinutes(5));
        }

        string err = w.StandardError.ReadToEnd();
        string outp = w.StandardOutput.ReadToEnd();
        Console.WriteLine($"wal: passes={passes} accepted={accepted} consistent={consistent} inconsistent={inconsistent} rejectedByHasChangedSinceOpen={rejected} tornOrRefused={torn}; highest commit seen={lastN}; writer exit={w.ExitCode} stderr='{err.Trim()}' stdout='{outp.Trim()}'");
        return inconsistent + (err.Contains("locked", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
    }
}
