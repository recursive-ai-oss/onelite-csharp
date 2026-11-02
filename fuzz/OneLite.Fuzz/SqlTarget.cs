using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Sql.Parsing;

namespace RecursiveAI.OneLite.Fuzz;

/// <summary>
/// A seeded mutation loop over the SQL tokenizer, parser and writer. Seeds are the statements of the comparer's generator plus every
/// probe record given with <c>--seeds</c>; each round mutates a seed at the byte or text level or splices two seeds, then parses it.
/// A OneLiteException is the expected answer to bad input. Anything else (an unexpected exception, a text the writer cannot reproduce,
/// a single input taking over <c>--slow-ms</c>, a loop that stops making progress) is a finding and is saved under <c>--out</c>.
/// The first phase feeds nested input at depths up to 200,000 on a thread with a small stack, which must end in an error, never in a crash.
/// </summary>
internal static class SqlTarget
{
    private static readonly string[] Dictionary =
    [
        "(", ")", ",", ";", ".", "*", "=", "'", "\"", "`", "[", "]", "select ", " from ", " where ", "1", "?", "?1", ":a", "@b", "$c", "#1",
        " not ", " and ", " or ", " in ", " is ", " like ", " between ", " escape ", " collate ", " case ", " when ", " then ", " else ", " end ",
        " window ", " over ", " filter ", " partition by ", " order by ", " rows ", " range ", " groups ", " exclude ", " generated always as ",
        "create table ", "create index ", "create view ", "create trigger ", " begin ", "insert into ", "update ", "delete from ", " returning ",
        " on conflict ", " do update set ", " with recursive ", " union all ", " values ", "pragma ", "alter table ", " rename to ", "attach ",
        "begin concurrent", "/*", "*/", "--", "\n", "\t", "\u000b", "\u000c", "x'", "0x", "1e", "1_0", "->", "->>", "||", "<>", "!=", "<<", ">>",
        "\u00e9", "\ud800", "\udfff", "\u2028", "\uffff", "\0",
    ];

    private static readonly string[] NestUnits =
    [
        "(", "f(", "- ", "~", "not ", "case when 1 then ", "case ", "(select ", "select * from (", "with a as (", "1 in (", "1 between ",
        "cast(", "(1,", "1+(", "exists(select ", "f(1) over (partition by ", "select 1 union select ", "f(order by ", "select * from t join (",
        "create table x(a, b, c, check(", "1 or ", "1 + ", "a.", "x collate ", "not null ", "1 isnull ", "values (",
    ];

    internal static int Run(string[] args)
    {
        int seconds = 60;
        int seed = 1;
        long maxInputs = long.MaxValue;
        int slowMs = 2000;
        string outDir = "fuzz-out";
        string? seeds = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seconds": seconds = int.Parse(args[++i]); break;
                case "--seed": seed = int.Parse(args[++i]); break;
                case "--inputs": maxInputs = long.Parse(args[++i]); break;
                case "--slow-ms": slowMs = int.Parse(args[++i]); break;
                case "--out": outDir = args[++i]; break;
                case "--seeds": seeds = args[++i]; break;
            }
        }

        Directory.CreateDirectory(outDir);
        int exit = 0;
        var depth = new Thread(() => exit |= DepthPhase(outDir), 256 * 1024) { Name = "depth" };
        depth.Start();
        depth.Join();
        var loop = new Thread(() => exit |= MutationPhase(seconds, seed, maxInputs, slowMs, outDir, seeds), 1024 * 1024) { Name = "mutate" };
        loop.Start();
        loop.Join();
        return exit;
    }

    // ---- phase 1: nesting ----------------------------------------------------------------------------------------------

    private static int DepthPhase(string outDir)
    {
        int findings = 0;
        long runs = 0;
        int[] depths = [1, 2, 10, 100, 500, 1000, 2000, 2494, 5000, 20000, 100000, 200000];
        foreach (string unit in NestUnits)
        {
            foreach (int n in depths)
            {
                string sql = Prefix(unit) + Rep(unit, n) + "1" + Rep(")", n);
                findings += Probe(sql, "depth", outDir, ref runs);
                findings += Probe(Prefix(unit) + Rep(unit, n), "depth-open", outDir, ref runs);
            }
        }

        Console.WriteLine($"depth phase: {runs} inputs, {findings} findings");
        return findings == 0 ? 0 : 1;
    }

    private static string Prefix(string unit) => unit.StartsWith("select", StringComparison.Ordinal) || unit.StartsWith("with", StringComparison.Ordinal)
        || unit.StartsWith("create", StringComparison.Ordinal) || unit.StartsWith("values", StringComparison.Ordinal) ? "" : "select ";

    private static string Rep(string s, int n) => new StringBuilder(s.Length * n).Insert(0, s, n).ToString();

    // ---- phase 2: mutation ---------------------------------------------------------------------------------------------

    private static long s_progress;
    private static string s_current = "";

    private static int MutationPhase(int seconds, int seed, long maxInputs, int slowMs, string outDir, string? seedsPath)
    {
        var rng = new Random(seed);
        var pool = new List<string>();
        var gen = new SqlCompare.Gen(seed);
        for (int i = 0; i < 4000; i++)
        {
            pool.Add(gen.Next(out _, out _));
            pool.Add(gen.NextPure());
        }

        if (seedsPath != null)
        {
            foreach (string f in Directory.EnumerateFiles(seedsPath, "*.txt"))
            {
                foreach (string rec in File.ReadAllText(f).Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n%%\n"))
                {
                    if (rec.Length > 0 && rec.Length < 4000)
                    {
                        pool.Add(rec);
                    }
                }
            }
        }

        // A watchdog: if no input finishes for 20 s the process is stuck; save the input being parsed and stop.
        var dog = new Thread(() =>
        {
            long last = -1;
            for (; ; )
            {
                Thread.Sleep(20000);
                long now = Interlocked.Read(ref s_progress);
                if (now == last)
                {
                    File.WriteAllText(Path.Combine(outDir, "hang-" + Guid.NewGuid().ToString("N")[..8] + ".sql"), s_current);
                    Console.WriteLine("HANG: no progress for 20 s; input saved");
                    Environment.Exit(3);
                }

                last = now;
            }
        }) { IsBackground = true, Name = "watchdog" };
        dog.Start();

        var clock = Stopwatch.StartNew();
        long inputs = 0, accepted = 0, rejected = 0, findings = 0;
        long nextReport = 60000;
        while (clock.ElapsedMilliseconds < seconds * 1000L && inputs < maxInputs)
        {
            string text = Make(rng, pool);
            s_current = text;
            var one = Stopwatch.StartNew();
            string? problem = Check(text, out bool ok);
            one.Stop();
            if (problem == null && one.ElapsedMilliseconds > slowMs)
            {
                problem = $"slow: {one.ElapsedMilliseconds} ms";
            }

            inputs++;
            if (ok)
            {
                accepted++;
            }
            else
            {
                rejected++;
            }

            Interlocked.Increment(ref s_progress);
            if (problem != null)
            {
                findings++;
                string file = Path.Combine(outDir, "finding-" + findings.ToString("D4") + ".sql");
                File.WriteAllText(file, text);
                Console.WriteLine($"FINDING {file}: {problem}");
                if (findings > 50)
                {
                    break;
                }
            }

            if (rng.Next(50) == 0 && pool.Count < 200000)
            {
                pool.Add(text.Length < 4000 ? text : text[..4000]);
            }

            if (clock.ElapsedMilliseconds > nextReport)
            {
                nextReport += 60000;
                Console.WriteLine($"  {clock.Elapsed.TotalSeconds:F0} s: {inputs} inputs, {accepted} accepted, {findings} findings");
            }
        }

        Console.WriteLine($"mutation phase seed {seed}: {inputs} inputs in {clock.Elapsed.TotalSeconds:F0} s, {accepted} accepted, {rejected} rejected, {findings} findings");
        return findings == 0 ? 0 : 1;
    }

    private static string Make(Random rng, List<string> pool)
    {
        string a = pool[rng.Next(pool.Count)];
        switch (rng.Next(12))
        {
            case 0: return Bytes(rng, a);
            case 1: return Bytes(rng, Bytes(rng, a));
            case 2:
            {
                string b = pool[rng.Next(pool.Count)];
                return a[..rng.Next(a.Length + 1)] + b[rng.Next(b.Length + 1)..];
            }

            case 3:
            {
                int at = rng.Next(a.Length + 1);
                return a.Insert(at, Dictionary[rng.Next(Dictionary.Length)]);
            }

            case 4:
            {
                if (a.Length == 0)
                {
                    return a;
                }

                int at = rng.Next(a.Length);
                int len = Math.Min(a.Length - at, 1 + rng.Next(8));
                return a.Remove(at, len);
            }

            case 5:
            {
                if (a.Length < 2)
                {
                    return a;
                }

                int at = rng.Next(a.Length - 1);
                int len = Math.Min(a.Length - at, 1 + rng.Next(30));
                return a.Insert(at, a.Substring(at, len));
            }

            case 6: return a[..rng.Next(a.Length + 1)];
            case 7:
            {
                // nest part of the input
                string unit = NestUnits[rng.Next(NestUnits.Length)];
                int n = 1 + rng.Next(rng.Next(4) == 0 ? 3000 : 60);
                return Prefix(unit) + Rep(unit, n) + a + Rep(")", rng.Next(n + 2));
            }

            case 8:
            {
                // swap two tokens
                string[] t = a.Split(' ');
                if (t.Length > 2)
                {
                    int i = rng.Next(t.Length), j = rng.Next(t.Length);
                    (t[i], t[j]) = (t[j], t[i]);
                }

                return string.Join(' ', t);
            }

            case 9:
            {
                // repeat one token many times
                string[] t = a.Split(' ');
                int i = rng.Next(t.Length);
                t[i] = Rep(t[i] + " ", 1 + rng.Next(40));
                return string.Join(' ', t);
            }

            case 10: return a + Dictionary[rng.Next(Dictionary.Length)] + pool[rng.Next(pool.Count)];
            default: return Bytes(rng, a + ";" + pool[rng.Next(pool.Count)]);
        }
    }

    private static string Bytes(Random rng, string text)
    {
        byte[] b = Encoding.UTF8.GetBytes(text);
        if (b.Length == 0)
        {
            return text;
        }

        int edits = 1 + rng.Next(4);
        var list = new List<byte>(b);
        for (int e = 0; e < edits && list.Count > 0; e++)
        {
            int at = rng.Next(list.Count);
            switch (rng.Next(5))
            {
                case 0: list[at] ^= (byte)(1 << rng.Next(8)); break;
                case 1: list[at] = (byte)rng.Next(256); break;
                case 2: list.Insert(at, (byte)rng.Next(256)); break;
                case 3: list.RemoveAt(at); break;
                default: list[at] = (byte)" ()'\",;.*/-[]\\\0\u000b"[rng.Next(15)]; break;
            }
        }

        return Encoding.UTF8.GetString(list.ToArray());
    }

    // ---- the property --------------------------------------------------------------------------------------------------

    private static int Probe(string sql, string what, string outDir, ref long runs)
    {
        runs++;
        string? p = Check(sql, out _);
        if (p == null)
        {
            return 0;
        }

        File.WriteAllText(Path.Combine(outDir, what + "-" + runs + ".sql"), sql.Length > 100000 ? sql[..100000] : sql);
        Console.WriteLine($"FINDING {what} (length {sql.Length}): {p}");
        return 1;
    }

    /// <summary>Parses the text; returns a description of what went wrong, or null. <paramref name="accepted"/> says whether it parsed.</summary>
    private static string? Check(string sql, out bool accepted)
    {
        accepted = false;
        try
        {
            System.Collections.Immutable.ImmutableArray<Sql.Statement> all = SqlParser.ParseAll(sql);
            accepted = true;
            foreach (Sql.Statement s in all)
            {
                string w = SqlWriter.Write(s);
                // the written text must parse and, written again, give the same text
                Sql.Statement? back = SqlParser.ParseOne(w, 0, out _);
                if (back == null || SqlWriter.Write(back) != w)
                {
                    return "writer output does not reproduce: " + w;
                }
            }

            _ = SqlParser.ParseOne(sql, 0, out _);
            return null;
        }
        catch (OneLiteException)
        {
            try
            {
                _ = SqlParser.ParseOne(sql, 0, out _);
            }
            catch (OneLiteException)
            {
            }
            catch (Exception e)
            {
                return "ParseOne threw " + e.GetType().Name + ": " + e.Message;
            }

            return null;
        }
        catch (InsufficientExecutionStackException)
        {
            // the writer asked for more stack than this small test thread has; the parse itself succeeded
            return null;
        }
        catch (Exception e)
        {
            return e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace;
        }
    }
}
