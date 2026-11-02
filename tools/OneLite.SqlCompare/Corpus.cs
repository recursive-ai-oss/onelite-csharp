using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using RecursiveAI.OneLite.Oracle;
using RecursiveAI.OneLite.SqlLogicTest;

namespace RecursiveAI.OneLite.SqlCompare;

/// <summary>Statement sources: the sqllogictest scripts and the sqlite_schema text of database files.</summary>
internal static unsafe partial class Corpus
{
    private const string Lib = "e_sqlite3";

    [LibraryImport(Lib, EntryPoint = "sqlite3_open_v2", StringMarshalling = StringMarshalling.Utf8)] private static partial int Open(string f, out IntPtr db, int flags, IntPtr vfs);
    [LibraryImport(Lib, EntryPoint = "sqlite3_close_v2")] private static partial int Close(IntPtr db);
    [LibraryImport(Lib, EntryPoint = "sqlite3_prepare_v2", StringMarshalling = StringMarshalling.Utf8)] private static partial int Prepare(IntPtr db, string sql, int n, out IntPtr stmt, IntPtr tail);
    [LibraryImport(Lib, EntryPoint = "sqlite3_step")] private static partial int Step(IntPtr stmt);
    [LibraryImport(Lib, EntryPoint = "sqlite3_finalize")] private static partial int Finalize(IntPtr stmt);
    [LibraryImport(Lib, EntryPoint = "sqlite3_column_text")] private static partial IntPtr ColumnText(IntPtr stmt, int i);

    /// <summary>Compares every statement of the sqllogictest scripts under a directory. Options: --shard i/n, --limit N, --out file.</summary>
    internal static int Slt(string[] args)
    {
        string dir = args[0];
        (int shard, int shards) = (0, 1);
        long limit = long.MaxValue;
        string? outPath = null;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--shard":
                    string[] p = args[++i].Split('/');
                    shard = int.Parse(p[0]);
                    shards = int.Parse(p[1]);
                    break;
                case "--limit":
                    limit = long.Parse(args[++i]);
                    break;
                case "--out":
                    outPath = args[++i];
                    break;
            }
        }

        string[] files = Directory.EnumerateFiles(dir, "*.test", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).Where((_, idx) => idx % shards == shard).ToArray();
        return Run(files.SelectMany(f => SltStatements(f)), limit, outPath, $"{files.Length} scripts, shard {shard}/{shards}");
    }

    private static IEnumerable<string> SltStatements(string file)
    {
        using var r = new StreamReader(file);
        foreach (Record rec in ScriptParser.Parse(r))
        {
            if (rec.Sql.Length > 0)
            {
                yield return rec.Sql;
            }
        }
    }

    /// <summary>Compares the sql column of sqlite_schema in each database file (opened immutable so nothing is recovered or written).</summary>
    internal static int Schema(string[] args)
    {
        IEnumerable<string> All()
        {
            foreach (string a in args)
            {
                string[] files = Directory.Exists(a) ? Directory.EnumerateFiles(a, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("-journal", StringComparison.Ordinal) && !f.EndsWith("-wal", StringComparison.Ordinal) && !f.EndsWith("-shm", StringComparison.Ordinal)).OrderBy(f => f, StringComparer.Ordinal).ToArray() : [a];
                foreach (string f in files)
                {
                    foreach (string s in SchemaSql(f))
                    {
                        yield return s;
                    }
                }
            }
        }

        return Run(All(), long.MaxValue, null, "schema sql");
    }

    private static List<string> SchemaSql(string file)
    {
        var list = new List<string>();
        Prepared.EnsureResolver();
        if (Open("file:" + Uri.EscapeDataString(file).Replace("%2F", "/", StringComparison.Ordinal) + "?immutable=1", out IntPtr db, 0x01 | 0x40, IntPtr.Zero) != 0)
        {
            return list;
        }

        try
        {
            if (Prepare(db, "select sql from sqlite_schema where sql is not null", -1, out IntPtr st, IntPtr.Zero) == 0)
            {
                while (Step(st) == 100)
                {
                    string? s = Marshal.PtrToStringUTF8(ColumnText(st, 0));
                    if (s != null)
                    {
                        list.Add(s);
                    }
                }

                _ = Finalize(st);
            }
        }
        finally
        {
            _ = Close(db);
        }

        return list;
    }

    private static int Run(IEnumerable<string> statements, long limit, string? outPath, string what)
    {
        using var c = new Checker { RoundTrip = Environment.GetEnvironmentVariable("SQLCOMPARE_ROUNDTRIP") == "1" };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long total = 0, unique = 0;
        var kinds = new Dictionary<string, int>();
        using StreamWriter? o = outPath == null ? null : new StreamWriter(outPath);
        foreach (string sql in statements)
        {
            total++;
            if (!seen.Add(sql))
            {
                continue;
            }

            if (++unique > limit)
            {
                break;
            }

            string? d = c.Compare(sql);
            if (d != null)
            {
                string k = d[..d.IndexOf(']', StringComparison.Ordinal)];
                kinds[k] = kinds.GetValueOrDefault(k) + 1;
                if (kinds[k] <= 30)
                {
                    Console.WriteLine(d);
                }

                o?.WriteLine(d);
            }
        }

        Console.WriteLine($"{what}: {total} statements, {unique} unique; native: {c.NativeCounts[0]} prepare, {c.NativeCounts[1]} syntax, {c.NativeCounts[2]} other; round trips {c.RoundTrips}, masked {c.Masked}, mismatches {c.Mismatches} ({string.Join(", ", kinds.Select(kv => kv.Key + "]=" + kv.Value))})");
        return c.Mismatches == 0 ? 0 : 1;
    }

    /// <summary>Generated statements: --seed S --count N [--out file]. Every accepted statement is also round-tripped.</summary>
    internal static int Generate(string[] args)
    {
        int seed = 1;
        long count = 100000;
        string? outPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seed": seed = int.Parse(args[++i]); break;
                case "--count": count = long.Parse(args[++i]); break;
                case "--out": outPath = args[++i]; break;
            }
        }

        using var c = new Checker { RoundTrip = true };
        var gen = new Gen(seed);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stats = new SortedDictionary<string, long[]>(StringComparer.Ordinal);
        var kinds = new Dictionary<string, int>();
        using StreamWriter? o = outPath == null ? null : new StreamWriter(outPath);
        long unique = 0;
        for (long n = 0; n < count; n++)
        {
            bool pure = n % 4 == 3;
            string sql = pure ? gen.NextPure() : gen.Next(out _, out _);
            string kind = pure ? "expr-eval" : gen.LastKind;
            bool mutated = !pure && gen.LastMutated;
            if (!seen.Add(sql))
            {
                continue;
            }

            unique++;
            long before = c.Mismatches;
            string? d = c.Compare(sql);
            if (pure && d == null)
            {
                d = c.CompareSemantics(sql);
            }

            // [kind+mutation] -> total, native accepts, native syntax error, native other, we accept, mismatches
            string key = kind + (mutated ? "+mut" : "");
            if (!stats.TryGetValue(key, out long[]? s))
            {
                stats[key] = s = new long[6];
            }

            s[0]++;
            s[1 + (int)c.LastNative]++;
            if (c.LastAccepted)
            {
                s[4]++;
            }

            if (d != null)
            {
                s[5]++;
                string k = d[..d.IndexOf(']', StringComparison.Ordinal)];
                kinds[k] = kinds.GetValueOrDefault(k) + 1;
                if (kinds[k] <= 25)
                {
                    Console.WriteLine(d);
                }

                o?.WriteLine(d);
                o?.WriteLine("    full: " + sql.Replace("\n", "\\n", StringComparison.Ordinal));
            }
        }

        Console.WriteLine($"gen seed {seed}: {count} generated, {unique} unique; native: prepare/syntax/other; ours accepted; mismatches");
        long[] tot = new long[6];
        foreach ((string k, long[] v) in stats)
        {
            Console.WriteLine($"  {k,-24} {v[0],8} {v[1],8} {v[2],8} {v[3],8} {v[4],8} {v[5],6}");
            for (int j = 0; j < 6; j++)
            {
                tot[j] += v[j];
            }
        }

        Console.WriteLine($"  {"TOTAL",-24} {tot[0],8} {tot[1],8} {tot[2],8} {tot[3],8} {tot[4],8} {tot[5],6}; round trips {c.RoundTrips}, semantic checks {c.SemanticChecks}, masked {c.Masked}, mismatches {c.Mismatches} ({string.Join(", ", kinds.Select(kv => kv.Key + "]=" + kv.Value))})");
        return c.Mismatches == 0 ? 0 : 1;
    }
}
