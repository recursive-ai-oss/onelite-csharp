using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using RecursiveAI.OneLite.Extensions.Fts3;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Fuzz;

/// <summary>
/// FTS3/FTS4 fuzz targets, as self-contained mutation loops:
///   fts3-query [--seconds N] [--seed S] [--out DIR]   random and mutated query strings through the FTS3 query parser, for every tokenizer
///   fts3-data  [--seconds N] [--seed S] [--out DIR]   hostile bytes written into the shadow tables (_segments, _segdir, _content, _docsize, _stat)
///                                                     of a valid index, then queries, auxiliary functions, special commands and integrity-check
///   fts3-data --replay SEED ITER                      re-runs one iteration of a campaign (deterministic per seed and iteration)
/// A typed <see cref="OneLiteException"/> is the right answer to hostile input; anything else, or a call that stays busy for 20 s, is a finding.
/// </summary>
internal static class Fts3Target
{
    private static long s_started;
    private static string s_what = "";

    public static int Run(string[] args)
    {
        int seconds = 60, seed = 1;
        string outDir = "fts3-out";
        long replay = -1;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seconds": seconds = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--seed": seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--out": outDir = args[++i]; break;
                case "--replay": seed = int.Parse(args[++i], CultureInfo.InvariantCulture); replay = long.Parse(args[++i], CultureInfo.InvariantCulture); break;
                default: Console.Error.WriteLine("unknown option " + args[i]); return 2;
            }
        }

        Directory.CreateDirectory(outDir);
        new Thread(Watchdog) { IsBackground = true, Name = "watchdog" }.Start();
        return args[0] == "fts3-query" ? QueryLoop(seconds, seed, outDir) : DataLoop(seconds, seed, outDir, replay);
    }

    private static void Watchdog()
    {
        while (true)
        {
            Thread.Sleep(1000);
            long t = Volatile.Read(ref s_started);
            if (t != 0 && Stopwatch.GetTimestamp() - t > 20 * Stopwatch.Frequency)
            {
                Console.WriteLine("FINDING hang (20 s): " + s_what);
                Environment.Exit(3);
            }
        }
    }

    private static void Begin(string what)
    {
        s_what = what;
        Volatile.Write(ref s_started, Stopwatch.GetTimestamp());
    }

    // ---- query parser ------------------------------------------------------------------------------------------------

    private static readonly string[][] s_configs =
    [
        ["a", "b", "c"],
        ["a", "b", "tokenize=porter"],
        ["a", "b", "tokenize=simple"],
        ["a", "b", "tokenize=unicode61", "prefix=2,3"],
        ["a", "b", "notindexed=b", "languageid=lid"],
    ];

    private static readonly string[] s_pieces =
    [
        "alpha", "beta", "gamma", "running", "x", "y", "a", "b", "c", "a:", "b:", "col:", "AND", "OR", "NOT", "NEAR", "NEAR/", "NEAR/3", "NEAR/99999999999", "near", "(", ")", ":", "-", "*", "\"", "\"\"",
        "\"alpha beta\"", "\"a b\"*", "\"x y z\"", "a:\"b c\"", "-a", "-\"a b\"", "a*", "\u00e9clair", "\u4e2d\u6587", "0", "42", "99999999999999999999", " ", "  ", "\t", "\n", "'", "=", "\0", "\ud800", "(((", ")))", "OR OR",
    ];

    private static string RandomQuery(Random r)
    {
        var sb = new StringBuilder();
        int n = 1 + r.Next(12);
        for (int i = 0; i < n; i++)
        {
            sb.Append(s_pieces[r.Next(s_pieces.Length)]);
            if (r.Next(3) != 0)
            {
                sb.Append(' ');
            }
        }

        string q = sb.ToString();
        if (r.Next(4) == 0 && q.Length > 0)
        {
            var c = q.ToCharArray();
            for (int k = 1 + r.Next(3); k > 0; k--)
            {
                c[r.Next(c.Length)] = (char)(r.Next(3) == 0 ? r.Next(0x10000) : r.Next(32, 127));
            }

            q = new string(c);
        }

        return q;
    }

    private static int QueryLoop(int seconds, int seed, string outDir)
    {
        var configs = s_configs.Select(a => Fts3Config.Parse("main", "t", true, [.. a])).ToList();
        int findings = 0;
        long execs = 0, typed = 0, ok = 0;
        foreach (string unit in new[] { "(", "-", "a OR ", "a NEAR ", "\"", "a:(" })
        {
            foreach (int depth in new[] { 10, 100, 1000, 10000, 100000 })
            {
                string q = string.Concat(Enumerable.Repeat(unit, depth)) + "a" + string.Concat(Enumerable.Repeat(")", depth));
                findings += Probe(configs[0], q, outDir, ref execs, ref typed, ref ok);
            }
        }

        var r = new Random(seed);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            findings += Probe(configs[r.Next(configs.Count)], RandomQuery(r), outDir, ref execs, ref typed, ref ok);
        }

        Console.WriteLine($"fts3-query seed {seed}: {execs} inputs ({ok} parsed, {typed} typed errors), {findings} findings, {sw.Elapsed.TotalSeconds:F0} s");
        return findings == 0 ? 0 : 1;
    }

    private static int Probe(Fts3Config cfg, string q, string outDir, ref long execs, ref long typed, ref long ok)
    {
        execs++;
        Begin("query " + (q.Length > 200 ? q[..200] : q));
        try
        {
            Fts3Query.Parse(cfg, q, -1);
            Fts3Query.Parse(cfg, q, 1);
            ok++;
            return 0;
        }
        catch (OneLiteException)
        {
            typed++;
            return 0;
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(outDir, "query-" + execs + ".txt"), q);
            Console.WriteLine($"FINDING query {e.GetType().Name}: {e.Message} (input saved as query-{execs}.txt)");
            return 1;
        }
        finally
        {
            Volatile.Write(ref s_started, 0);
        }
    }

    // ---- shadow tables -----------------------------------------------------------------------------------------------

    private static readonly string[] s_create =
    [
        "CREATE VIRTUAL TABLE t USING fts3(a, b)",
        "CREATE VIRTUAL TABLE t USING fts4(a, b, prefix='2,3')",
        "CREATE VIRTUAL TABLE t USING fts4(a, b, tokenize=porter, order=desc)",
        "CREATE VIRTUAL TABLE t USING fts4(a, b, languageid='lid', matchinfo=fts3)",
        "CREATE VIRTUAL TABLE t USING fts4(content='', a, b)",
    ];

    private static readonly string[] s_words = ["alpha", "beta", "gamma", "delta", "running", "runs", "cats", "overdue", "invoice", "paid", "x1", "\u00e9clair"];

    private static readonly string[] s_queries =
    [
        "SELECT docid FROM t WHERE t MATCH 'alpha'",
        "SELECT docid, offsets(t) FROM t WHERE t MATCH 'alpha OR beta'",
        "SELECT docid, snippet(t, '[', ']', '..', -1, 6) FROM t WHERE t MATCH 'run* -cats'",
        "SELECT docid, hex(matchinfo(t, 'pcnalxyb')) FROM t WHERE t MATCH '\"alpha beta\"'",
        "SELECT docid FROM t WHERE t MATCH 'alpha NEAR/3 beta'",
        "SELECT docid FROM t WHERE t MATCH 'a:alpha (gamma OR delta)'",
        "SELECT * FROM t",
        "SELECT * FROM t WHERE docid = 3",
        "SELECT count(*) FROM t",
        "INSERT INTO t(t) VALUES('integrity-check')",
        "INSERT INTO t(t) VALUES('optimize')",
        "INSERT INTO t(t) VALUES('merge=2,3')",
        "INSERT INTO t(t) VALUES('automerge=4')",
        "INSERT INTO t(t) VALUES('rebuild')",
        "INSERT INTO t(docid, a, b) VALUES (500, 'alpha beta', 'gamma delta')",
        "DELETE FROM t WHERE docid = 2",
        "UPDATE t SET a = 'zzz alpha' WHERE docid = 4",
        "SELECT optimize(t) FROM t WHERE t MATCH 'alpha'",
        "DROP TABLE t",
    ];

    private static byte[] BuildImage(int cfg, int seed)
    {
        var mem = new MemoryVfs();
        var rnd = new Random(seed);
        using (var db = Database.Open("f.db", new DatabaseOptions { Vfs = mem, PageSize = 512 }))
        using (Connection c = db.Connect())
        {
            Exec(c, s_create[cfg]);
            bool lang = cfg == 3;
            for (int i = 1; i <= 60; i++)
            {
                var sb = new StringBuilder();
                for (int k = 0; k < 4 + rnd.Next(30); k++)
                {
                    sb.Append(s_words[rnd.Next(s_words.Length)]).Append(' ');
                }

                string a = sb.ToString();
                Exec(c, $"INSERT INTO t(docid, a, b{(lang ? ", lid" : "")}) VALUES ({i}, '{a}', '{a[..Math.Min(a.Length, 20)]} {s_words[i % s_words.Length]}'{(lang ? ", " + i % 3 : "")})");
            }
        }

        return mem.Export("f.db")!;
    }

    private static void Exec(Connection c, string sql)
    {
        using Statement st = c.Prepare(sql);
        while (st.Step())
        {
        }
    }

    private static byte[] MutateBytes(byte[] src, Random r)
    {
        byte[] d = (byte[])src.Clone();
        if (d.Length == 0)
        {
            return [(byte)r.Next(256)];
        }

        for (int k = 1 + r.Next(4); k > 0; k--)
        {
            switch (r.Next(8))
            {
                case 0: d[r.Next(d.Length)] ^= (byte)(1 << r.Next(8)); break;
                case 1: d[r.Next(d.Length)] = (byte)r.Next(256); break;
                case 2: d[r.Next(d.Length)] = 0xFF; break;
                case 3: d[r.Next(d.Length)] = 0; break;
                case 4: Array.Resize(ref d, r.Next(d.Length + 1)); if (d.Length == 0) { return d; } break;
                case 5:
                    {
                        // a 9-byte varint or an oversized value where a length, a delta or a count is expected
                        int at = r.Next(d.Length);
                        for (int j = at; j < Math.Min(d.Length, at + 1 + r.Next(9)); j++)
                        {
                            d[j] = 0xFF;
                        }

                        break;
                    }
                case 6:
                    {
                        int at = r.Next(d.Length), n = Math.Min(d.Length - at, 1 + r.Next(16));
                        var o = new byte[d.Length + n];
                        Array.Copy(d, 0, o, 0, at);
                        var ins = new byte[n];
                        r.NextBytes(ins);
                        Array.Copy(ins, 0, o, at, n);
                        Array.Copy(d, at, o, at + n, d.Length - at);
                        d = o;
                        break;
                    }
                default:
                    {
                        int a = r.Next(d.Length), b = r.Next(d.Length), n = Math.Min(Math.Min(d.Length - a, d.Length - b), 1 + r.Next(32));
                        Array.Copy(d, a, d, b, n);
                        break;
                    }
            }
        }

        return d;
    }

    private static int DataLoop(int seconds, int seed, string outDir, long replay)
    {
        var images = new byte[s_create.Length][];
        for (int i = 0; i < images.Length; i++)
        {
            images[i] = BuildImage(i, seed + i);
        }

        long execs = 0, typed = 0, clean = 0;
        int findings = 0;
        var sw = Stopwatch.StartNew();
        for (long iter = replay >= 0 ? replay : 0; replay >= 0 ? iter == replay : sw.Elapsed.TotalSeconds < seconds; iter++)
        {
            var r = new Random(unchecked(seed * 1000003 + (int)iter));
            int cfg = r.Next(images.Length);
            execs++;
            var log = new StringBuilder();
            try
            {
                var mem = new MemoryVfs();
                mem.Import("f.db", images[cfg]);
                using var db = Database.Open("f.db", new DatabaseOptions { Vfs = mem, PageSize = 512 });
                using Connection c = db.Connect();
                Mutate(c, r, log);
                bool anyError = false;
                int n = 3 + r.Next(6);
                for (int q = 0; q < n; q++)
                {
                    string sql = s_queries[r.Next(s_queries.Length - 1)];
                    log.Append(sql).Append(";\n");
                    Begin($"seed {seed} iter {iter}: {sql}");
                    try
                    {
                        Exec(c, sql);
                    }
                    catch (OneLiteException)
                    {
                        anyError = true;
                    }
                    finally
                    {
                        Volatile.Write(ref s_started, 0);
                    }
                }

                if (anyError)
                {
                    typed++;
                }
                else
                {
                    clean++;
                }
            }
            catch (OneLiteException)
            {
                typed++;
            }
            catch (Exception e)
            {
                findings++;
                File.WriteAllText(Path.Combine(outDir, $"data-{seed}-{iter}.txt"), log + "\n" + e);
                Console.WriteLine($"FINDING data seed {seed} iter {iter}: {e.GetType().Name}: {e.Message}\n" + string.Join("\n", (e.StackTrace ?? "").Split('\n').Take(5)));
            }
        }

        Console.WriteLine($"fts3-data seed {seed}: {execs} iterations ({typed} with a typed error, {clean} clean), {findings} findings, {sw.Elapsed.TotalSeconds:F0} s");
        return findings == 0 ? 0 : 1;
    }

    // Replaces one to four shadow-table values with mutated bytes or numbers, noting the edit in <paramref name="log"/>.
    private static void Mutate(Connection c, Random r, StringBuilder log)
    {
        // table, key column, mutable columns
        (string Table, string Key, string[] Cols)[] targets =
        [
            ("t_segments", "blockid", ["block"]),
            ("t_segdir", "idx", ["start_block", "leaves_end_block", "end_block", "root"]),
            ("t_docsize", "docid", ["size"]),
            ("t_stat", "id", ["value"]),
            ("t_content", "docid", ["c0a", "c1b"]),
        ];
        int edits = 1 + r.Next(4);
        for (int e = 0; e < edits; e++)
        {
            (string table, string keyCol, string[] cols) = targets[r.Next(10) < 4 ? 0 : r.Next(10) < 6 ? 1 : r.Next(targets.Length)];
            string valCol = cols[r.Next(cols.Length)];
            var keys = new List<object>();
            try
            {
                using Statement st = c.Prepare($"SELECT {keyCol} FROM {table}");
                while (st.Step())
                {
                    keys.Add(st.GetFieldValue<object>(0));
                }
            }
            catch (OneLiteException)
            {
                continue;
            }

            if (keys.Count == 0)
            {
                continue;
            }

            object key = keys[r.Next(keys.Count)];
            try
            {
                byte[] cur;
                using (Statement g = c.Prepare($"SELECT {valCol} FROM {table} WHERE {keyCol} = ?1"))
                {
                    Bind(g, 1, key);
                    cur = g.Step() ? ToBytes(g) : [];
                }

                using Statement u = c.Prepare($"UPDATE {table} SET {valCol} = ?2 WHERE {keyCol} = ?1");
                Bind(u, 1, key);
                if (valCol is "block" or "root" or "size" or "value")
                {
                    u.Bind(2, MutateBytes(cur, r));
                }
                else
                {
                    u.Bind(2, r.Next(3) == 0 ? long.MaxValue : r.Next(-5, 100000));
                }

                log.Append($"-- mutate {table}.{valCol} {keyCol}={key}\n");
                u.Step();
            }
            catch (OneLiteException)
            {
                // a column that doesn't exist in this configuration
            }
        }
    }

    private static void Bind(Statement st, int i, object key)
    {
        if (key is long l)
        {
            st.Bind(i, l);
        }
        else if (key is byte[] b)
        {
            st.Bind(i, b);
        }
        else
        {
            st.Bind(i, Convert.ToString(key, CultureInfo.InvariantCulture) ?? "");
        }
    }

    private static byte[] ToBytes(Statement st) => st.GetFieldValue<object>(0) switch
    {
        byte[] b => b,
        long l => BitConverter.GetBytes(l),
        string s => Encoding.UTF8.GetBytes(s),
        _ => [],
    };
}
