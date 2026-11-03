using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.Cli.Tests;

/// <summary>Runs <c>onelite</c> in process and the pinned <c>sqlite3</c> shell as a child process, with the same arguments and input.</summary>
internal static class Harness
{
    public static bool OracleAvailable => File.Exists(OraclePaths.Cli);

    public static string Scratch { get; } = Directory.CreateTempSubdirectory("onelite-cli-").FullName;

    public sealed record Outcome(int Exit, string Stdout, string Stderr);

    public static Outcome Ours(IEnumerable<string> args, string stdin = "")
    {
        using var inp = new MemoryStream(Encoding.UTF8.GetBytes(stdin));
        using var o = new MemoryStream();
        using var e = new MemoryStream();
        int rc = Program.Run([.. args], inp, o, e, false);
        return new Outcome(rc, Encoding.UTF8.GetString(o.ToArray()), Encoding.UTF8.GetString(e.ToArray()));
    }

    public static Outcome Oracle(IEnumerable<string> args, string stdin = "")
    {
        Proc.Result r = Proc.Run(OraclePaths.Cli, args, stdin, timeout: TimeSpan.FromMinutes(2));
        return new Outcome(r.ExitCode, r.Stdout, r.Stderr);
    }

    /// <summary>Our stderr without the "did you mean" hint lines OneLite adds.</summary>
    public static string WithoutHints(string stderr)
        => string.Join('\n', stderr.Split('\n').Where(l => !l.StartsWith("  did you mean", StringComparison.Ordinal)));

    public static void AssertSame(Outcome expected, Outcome actual, string what)
    {
        Assert.True(expected.Stdout == actual.Stdout, $"{what}: stdout differs\n--- sqlite3\n{Clip(expected.Stdout)}\n--- onelite\n{Clip(actual.Stdout)}");
        Assert.True(expected.Stderr.TrimEnd() == WithoutHints(actual.Stderr).TrimEnd(), $"{what}: stderr differs\n--- sqlite3\n{Clip(expected.Stderr)}\n--- onelite\n{Clip(actual.Stderr)}");
        Assert.True(expected.Exit == actual.Exit, $"{what}: exit code {expected.Exit} vs {actual.Exit}");
    }

    private static string Clip(string s) => s.Length > 1500 ? s[..1500] + "..." : s;

    /// <summary>Compares a run of both shells on the same arguments and standard input.</summary>
    public static void Compare(string what, IEnumerable<string> args, string stdin = "")
    {
        string[] a = [.. args];
        AssertSame(Oracle(a, stdin), Ours(a, stdin), what);
    }

    /// <summary>True if the pinned shell's <c>PRAGMA integrity_check</c> says ok.</summary>
    public static bool IntegrityOk(string db, out string detail)
    {
        detail = Oracle([db, "pragma integrity_check"]).Stdout.Trim();
        return detail == "ok";
    }

    public static string PathIn(string name) => Path.Combine(Scratch, name);

    /// <summary>Makes a database file by running a script in the pinned shell.</summary>
    public static string MakeDb(string name, string script)
    {
        string path = PathIn(name);
        File.Delete(path);
        Proc.Result r = Proc.Run(OraclePaths.Cli, [path], script);
        Assert.True(r.ExitCode == 0, "could not build " + name + ": " + r.Stderr);
        return path;
    }

}

/// <summary>Seeded tables holding every storage class and the awkward text: quotes, commas, newlines, tabs, Unicode, long reals, blobs.</summary>
internal static class GeneratedData
{
    private static readonly string[] Texts =
    [
        "", "a", "hello world", "x,y", "q\"uote", "it's", "line1\nline2", "crlf\r\nx", "tab\there", "\u00e9\u00e8", "\u65e5\u672c\u8a9e", " lead", "trail ",
        "a|b", "null", "0", "1.5", "\\back", "<b>&amp;</b>", "emoji \U0001F600", "ctl\u0001\u0002x", "caf\u00e9 \u00fcber", "a;b", "[brackets]", "{\"j\":1}",
    ];

    private static readonly double[] Reals =
    [
        0.1, 0.5, 1.0, -1.5, 3.14159265358979, 1e15, 1e16, 1e17, 123456789.123456789, 1e-5, 1.5e-7, 1e300, -1e300, 2.2250738585072014e-308,
        5e-324, 0.30000000000000004, 100.0, 1e21, 12345678901234567890.0, 4.35,
    ];

    private static readonly long[] Ints = [0, 1, -1, 42, 255, 65536, 2147483647, -2147483648, 9223372036854775807, long.MinValue, 1000000, 123456789012];

    private static readonly string[] Blobs = ["", "00", "0102", "41424344", "e29882", "41"];

    private static string Text(Random rnd)
    {
        string t = Texts[rnd.Next(Texts.Length)];
        var parts = new List<string>();
        var cur = new StringBuilder();
        foreach (char ch in t)
        {
            if (ch < 32)
            {
                if (cur.Length > 0)
                {
                    parts.Add("'" + cur.ToString().Replace("'", "''", StringComparison.Ordinal) + "'");
                    cur.Clear();
                }

                parts.Add("char(" + (int)ch + ")");
            }
            else
            {
                cur.Append(ch);
            }
        }

        if (cur.Length > 0 || parts.Count == 0)
        {
            parts.Add("'" + cur.ToString().Replace("'", "''", StringComparison.Ordinal) + "'");
        }

        return string.Join("||", parts);
    }

    private static string Literal(Random rnd, int kind) => kind switch
    {
        0 => "NULL",
        1 => Ints[rnd.Next(Ints.Length)].ToString(CultureInfo.InvariantCulture),
        2 => Reals[rnd.Next(Reals.Length)].ToString("R", CultureInfo.InvariantCulture),
        3 => Text(rnd),
        _ => "x'" + Blobs[rnd.Next(Blobs.Length)] + "'",
    };

    /// <summary>A script creating tables <c>t</c> (no declared types) and <c>w</c> (integer key, text, real) with seeded rows.</summary>
    public static string Script(int seed)
    {
        var rnd = new Random(seed);
        var sb = new StringBuilder("create table t(a,b,c,d,e);\n");
        int n = rnd.Next(0, 40);
        for (int i = 0; i < n; i++)
        {
            sb.Append("insert into t values(").Append(string.Join(",", Enumerable.Range(0, 5).Select(_ => Literal(rnd, rnd.Next(5))))).Append(");\n");
        }

        sb.Append("create table w(x integer primary key, y text, z real);\n");
        int m = rnd.Next(0, 15);
        for (int i = 0; i < m; i++)
        {
            sb.Append("insert into w values(").Append(i + 1).Append(',').Append(Literal(rnd, 3)).Append(',').Append(Literal(rnd, 2)).Append(");\n");
        }

        return sb.ToString();
    }
}
