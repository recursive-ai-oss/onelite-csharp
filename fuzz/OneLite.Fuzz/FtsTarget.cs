using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using RecursiveAI.OneLite.Extensions.Fts5;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Fuzz;

/// <summary>
/// FTS5 fuzz targets, as self-contained mutation loops:
///   fts-query [--seconds N] [--seed S] [--out DIR]   random and mutated query strings through the FTS5 query parser, for every detail mode
///   fts-data  [--seconds N] [--seed S] [--out DIR]   hostile bytes written into the shadow tables (_data, _idx, _docsize, _config) of a valid
///                                                    index, then queries, auxiliary functions, special commands and integrity-check
///   fts-data --replay SEED ITER                      re-runs one iteration of a campaign (the loop is deterministic per seed and iteration)
/// A typed <see cref="OneLiteException"/> is the right answer to hostile input; anything else, or a call that stays busy for 20 s, is a finding.
/// </summary>
internal static class FtsTarget
{
    private static long s_started;
    private static string s_what = "";

    public static int Run(string[] args)
    {
        int seconds = 60, seed = 1;
        string outDir = "fts-out";
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
        var dog = new Thread(Watchdog) { IsBackground = true, Name = "watchdog" };
        dog.Start();
        return args[0] == "fts-query" ? QueryLoop(seconds, seed, outDir) : DataLoop(seconds, seed, outDir, replay);
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
        ["a", "b", "c", "detail=column"],
        ["a", "b", "c", "detail=none"],
        ["x UNINDEXED", "y", "tokenize='porter unicode61'", "prefix='2 3'"],
        ["a", "b", "tokenize='trigram'"],
        ["a", "b", "tokenize='ascii'", "columnsize=0"],
    ];

    private static readonly string[] s_pieces =
    [
        "alpha", "beta", "gamma", "running", "x", "y", "a", "b", "c", "col", "AND", "OR", "NOT", "NEAR", "near", "NEAR(", "(", ")", "{", "}", ":", ",", "+", "*", "^", "-", "\"", "\"\"",
        "\"alpha beta\"", "\"a \"\"quoted\"\" phrase\"", "a:", "{a b}:", "-a:", "NEAR(a b, 3)", "NEAR(a b)", "NEAR(a b, x)", "NEAR(\"a b\" c, 1)", "\u00e9clair", "\u4e2d\u6587", "0", "42",
        "99999999999999999999", " ", "  ", "\t", "\n", "'", "=", "<", "!", "\0", "\ud800", "\udc00", "a*", "^a", "a + b", "* a", "alpha*beta", "(((", ")))",
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
        var configs = new List<Fts5Config>();
        foreach (string[] a in s_configs)
        {
            configs.Add(Fts5Config.Parse("main", "t", [.. a]));
        }

        int findings = 0;
        long execs = 0, typed = 0, ok = 0;

        // nesting: deep parentheses, NOT chains and NEAR must end in a typed error or a result, never a stack overflow
        foreach (string unit in new[] { "(", "NOT ", "a AND ", "a OR ", "NEAR(", "\"", "{", "-", "a + " })
        {
            foreach (int depth in new[] { 10, 100, 1000, 10000, 100000 })
            {
                string q = string.Concat(System.Linq.Enumerable.Repeat(unit, depth)) + "a" + string.Concat(System.Linq.Enumerable.Repeat(")", depth));
                findings += Probe(configs[0], q, outDir, ref execs, ref typed, ref ok);
            }
        }

        var r = new Random(seed);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            string q = RandomQuery(r);
            findings += Probe(configs[r.Next(configs.Count)], q, outDir, ref execs, ref typed, ref ok);
        }

        Console.WriteLine($"fts-query seed {seed}: {execs} inputs ({ok} parsed, {typed} typed errors), {findings} findings, {sw.Elapsed.TotalSeconds:F0} s");
        return findings == 0 ? 0 : 1;
    }

    private static int Probe(Fts5Config cfg, string q, string outDir, ref long execs, ref long typed, ref long ok)
    {
        execs++;
        Begin("query " + (q.Length > 200 ? q[..200] : q));
        try
        {
            QueryParser.Parse(cfg, q, -1);
            if (cfg.ColumnCount > 1)
            {
                QueryParser.Parse(cfg, q, 1);
            }

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
        "CREATE VIRTUAL TABLE t USING fts5(a, b)",
        "CREATE VIRTUAL TABLE t USING fts5(a, b, detail=column, prefix='2 3')",
        "CREATE VIRTUAL TABLE t USING fts5(a, b, detail=none)",
        "CREATE VIRTUAL TABLE t USING fts5(a, b, tokenize='porter unicode61', content='')",
        "CREATE VIRTUAL TABLE t USING fts5(a, b, tokenize='trigram')",
    ];

    private static readonly string[] s_words = ["alpha", "beta", "gamma", "delta", "running", "runs", "cats", "overdue", "invoice", "paid", "x1", "\u00e9clair"];

    private static readonly string[] s_queries =
    [
        "SELECT rowid FROM t WHERE t MATCH 'alpha'",
        "SELECT rowid, bm25(t) FROM t WHERE t MATCH 'alpha OR beta' ORDER BY rank",
        "SELECT rowid, highlight(t, 0, '[', ']'), snippet(t, 1, '<', '>', '...', 6) FROM t WHERE t MATCH 'run* NOT cats'",
        "SELECT rowid FROM t WHERE t MATCH '\"alpha beta\"'",
        "SELECT rowid FROM t WHERE t MATCH 'NEAR(alpha beta, 4)'",
        "SELECT rowid FROM t WHERE t MATCH 'a : (alpha OR gamma) AND ^beta'",
        "SELECT * FROM t",
        "SELECT * FROM t WHERE rowid = 3",
        "SELECT count(*) FROM t",
        "INSERT INTO t(t) VALUES('integrity-check')",
        "INSERT INTO t(t, rank) VALUES('integrity-check', 1)",
        "INSERT INTO t(t) VALUES('optimize')",
        "INSERT INTO t(t, rank) VALUES('merge', 2)",
        "INSERT INTO t(t, rank) VALUES('automerge', 4)",
        "INSERT INTO t(t) VALUES('rebuild')",
        "INSERT INTO t(rowid, a, b) VALUES (500, 'alpha beta', 'gamma delta')",
        "DELETE FROM t WHERE rowid = 2",
        "UPDATE t SET a = 'zzz alpha' WHERE rowid = 4",
        "CREATE VIRTUAL TABLE v1 USING fts5vocab(t, 'row')",
        "CREATE VIRTUAL TABLE v2 USING fts5vocab(t, 'col')",
        "CREATE VIRTUAL TABLE v3 USING fts5vocab(t, 'instance')",
        "SELECT * FROM v1",
        "SELECT * FROM v2",
        "SELECT * FROM v3",
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
            for (int i = 1; i <= 40; i++)
            {
                var sb = new StringBuilder();
                for (int k = 0; k < 4 + rnd.Next(30); k++)
                {
                    sb.Append(s_words[rnd.Next(s_words.Length)]).Append(' ');
                }

                string a = sb.ToString();
                Exec(c, $"INSERT INTO t(rowid, a, b) VALUES ({i}, '{a}', '{a[..Math.Min(a.Length, 20)]} {s_words[i % s_words.Length]}')");
                if (i % 13 == 0)
                {
                    Exec(c, "INSERT INTO t(t, rank) VALUES('merge', 8)");
                }
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
            switch (r.Next(9))
            {
                case 0: d[r.Next(d.Length)] ^= (byte)(1 << r.Next(8)); break;
                case 1: d[r.Next(d.Length)] = (byte)r.Next(256); break;
                case 2: d[r.Next(d.Length)] = 0xFF; break;
                case 3: d[r.Next(d.Length)] = 0; break;
                case 4: Array.Resize(ref d, r.Next(d.Length + 1)); if (d.Length == 0) { return d; } break;
                case 5:
                    {
                        // a varint of 9 bytes or an oversized value where a length or an offset is expected
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
                        var ins = new byte[n];
                        r.NextBytes(ins);
                        var o = new byte[d.Length + n];
                        Array.Copy(d, 0, o, 0, at);
                        Array.Copy(ins, 0, o, at, n);
                        Array.Copy(d, at, o, at + n, d.Length - at);
                        d = o;
                        break;
                    }
                case 7:
                    {
                        // copy one region over another
                        int a = r.Next(d.Length), b = r.Next(d.Length), n = Math.Min(Math.Min(d.Length - a, d.Length - b), 1 + r.Next(32));
                        Array.Copy(d, a, d, b, n);
                        break;
                    }
                default:
                    {
                        // two bytes at the page-header positions of a leaf: first-term offset and footer offset
                        if (d.Length >= 4)
                        {
                            d[r.Next(4)] = (byte)r.Next(256);
                        }

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
                Console.WriteLine($"FINDING data seed {seed} iter {iter}: {e.GetType().Name}: {e.Message}\n" + string.Join("\n", (e.StackTrace ?? "").Split('\n')[..Math.Min(5, (e.StackTrace ?? "").Split('\n').Length)]));
            }
        }

        Console.WriteLine($"fts-data seed {seed}: {execs} iterations ({typed} with a typed error, {clean} clean), {findings} findings, {sw.Elapsed.TotalSeconds:F0} s");
        return findings == 0 ? 0 : 1;
    }

    // Replaces one to four shadow-table values with mutated bytes, keeping the statements that did it in <paramref name="log"/>.
    private static void Mutate(Connection c, Random r, StringBuilder log)
    {
        int edits = 1 + r.Next(4);
        for (int e = 0; e < edits; e++)
        {
            int which = r.Next(10);
            string table = which < 6 ? "t_data" : which < 8 ? "t_idx" : which < 9 ? "t_docsize" : "t_config";
            string keyCol = table == "t_data" ? "id" : table == "t_idx" ? "term" : table == "t_docsize" ? "id" : "k";
            string valCol = table == "t_data" ? "block" : table == "t_idx" ? "pgno" : table == "t_docsize" ? "sz" : "v";
            // pick the n-th row; docsize and the config table may not exist for every configuration (content='' has docsize; detail=none too)
            var rows = new List<(object Key, byte[]? Blob, long? Num)>();
            try
            {
                using Statement st = c.Prepare($"SELECT {keyCol}, {valCol} FROM {table}");
                while (st.Step())
                {
                    object k0 = st.GetFieldValue<object>(0);
                    rows.Add((k0, null, null));
                }
            }
            catch (OneLiteException)
            {
                continue;
            }

            if (rows.Count == 0)
            {
                continue;
            }

            (object key, _, _) = rows[r.Next(rows.Count)];
            byte[] cur;
            using (Statement g = c.Prepare($"SELECT {valCol} FROM {table} WHERE {keyCol} = ?1"))
            {
                Bind(g, 1, key);
                cur = g.Step() ? ToBytes(g) : [];
            }

            using Statement u = c.Prepare($"UPDATE {table} SET {valCol} = ?2 WHERE {keyCol} = ?1");
            Bind(u, 1, key);
            if (table == "t_data" || table == "t_docsize")
            {
                u.Bind(2, MutateBytes(cur, r));
            }
            else if (r.Next(2) == 0)
            {
                u.Bind(2, r.Next(3) == 0 ? long.MaxValue : r.Next(-5, 100000));
            }
            else
            {
                u.Bind(2, MutateBytes(cur, r));
            }

            log.Append($"-- mutate {table} {keyCol}={key}\n");
            u.Step();
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
