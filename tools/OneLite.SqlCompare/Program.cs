using System;
using System.IO;
using System.Text;

namespace RecursiveAI.OneLite.SqlCompare;

/// <summary>Compares the OneLite SQL parser with the pinned native library. Modes: probe, corpus, gen, fuzz.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: sqlcompare probe <file> | ...");
            return 2;
        }

        switch (args[0])
        {
            case "probe": return Probe(args[1]);
            case "check": return Check(args[1]);
            case "slt": return Corpus.Slt(args[1..]);
            case "eval": return Eval(args[1]);
            case "depth": return Depth.Run(args[1..]);
            case "alloc": return Alloc.Run(args[1..]);
            case "gen": return Corpus.Generate(args[1..]);
            case "schema": return Corpus.Schema(args[1..]);
            default: Console.Error.WriteLine("unknown mode"); return 2;
        }
    }

    /// <summary>Evaluates each record of a file (separated by %% lines) natively and prints the first row.</summary>
    private static int Eval(string path)
    {
        using var p = new Prepared();
        string text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (string rec in text.Split("\n%%\n"))
        {
            string sql = rec.TrimEnd('\n');
            if (sql.Length > 0)
            {
                Console.WriteLine($"{Escape(sql)}  ==>  {p.Eval(sql)}");
            }
        }

        return 0;
    }

    /// <summary>Prints the native outcome for each record of a file (records are separated by a line holding only %%).</summary>
    private static int Probe(string path)
    {
        using var p = new Prepared();
        Console.WriteLine("native " + Prepared.Version);
        string text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (string rec in text.Split("\n%%\n"))
        {
            string sql = rec.TrimEnd('\n');
            if (rec.Length == 0)
            {
                continue;
            }

            PrepResult r = p.Prepare(sql);
            Console.WriteLine($"{Escape(sql)}  ==>  {r.Class} off={r.Offset} tail={r.TailOffset} {r.Message}");
        }

        return 0;
    }

    /// <summary>Compares a probe file (records separated by %% lines) and prints only the differences.</summary>
    private static int Check(string path)
    {
        using var c = new Checker();
        string text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        int total = 0;
        foreach (string rec in text.Split("\n%%\n"))
        {
            string sql = rec.TrimEnd('\n');
            if (rec.Length == 0)
            {
                continue;
            }

            total++;
            string? d = c.Compare(sql);
            if (d != null)
            {
                Console.WriteLine(d);
            }
        }

        Console.WriteLine($"{total} statements, {c.Mismatches} mismatches, {c.Masked} masked (native: {c.NativeCounts[0]} prepare, {c.NativeCounts[1]} syntax, {c.NativeCounts[2]} other)");
        return c.Mismatches == 0 ? 0 : 1;
    }

    internal static string Escape(string s) => (s.Length > 90 ? s[..60] + "...[" + s.Length + " chars]..." + s[^20..] : s).Replace("\n", "\\n", StringComparison.Ordinal);
}
