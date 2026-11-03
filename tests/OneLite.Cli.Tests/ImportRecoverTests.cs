using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.Cli.Tests;

public class ImportTests
{
    private static readonly (string Name, string Content)[] Files =
    [
        ("mixed.csv", "a,b,c\r\n1,\"x,y\",\r\n2,\"multi\nline\",z\r\n3,\"q\"\"uote\",\n4,5\n6,7,8,9\n,,\n\"\",\" sp \",\n"),
        ("simple.csv", "a,b\n1,2\n"),
        ("crlf.csv", "\"a\",\"b c\"\r\n\"x\"\"y\",\"line\r\nbreak\"\r\n,\r\n"),
        ("semi.csv", "a;b\n1;2\n"),
        ("blank.csv", "h1,h2\n\n1,2\n"),
        ("unterminated.csv", "x,y\n1,\"unterminated\n"),
        ("stray.csv", "a,b\n  1 , 2 \n\"q\" x,3\n"),
        ("pipes.txt", "A|B\n1|\"2\"\n"),
        ("bom.csv", "\ufeffa,b\n1,2\n"),
        ("dups.csv", "a,a,\n1,2,3\n"),
        ("nonl.csv", "a,b\n1,2"),
        ("trailingsep.csv", "a,b,\n1,2,\n"),
    ];

    [Fact]
    public void ImportGivesTheSameTablesAsTheShell()
    {
        Assert.SkipUnless(Harness.OracleAvailable, "the pinned sqlite3 is not installed");
        foreach ((string name, string content) in Files)
        {
            string path = Harness.PathIn(name);
            File.WriteAllText(path, content);
            foreach (string opt in new[] { "--csv", "--csv --skip 1", "", "--ascii" })
            {
                string cmd = $".import {opt} {path} t";
                Harness.Compare($"{name} [{opt}]", [":memory:", cmd, ".schema", ".mode quote", "select * from t"]);
            }
        }
    }

    [Fact]
    public void ImportIntoAnExistingTableAndMissingFile()
    {
        Assert.SkipUnless(Harness.OracleAvailable, "the pinned sqlite3 is not installed");
        string path = Harness.PathIn("exist.csv");
        File.WriteAllText(path, "a,b\n1,2\n3,4\n");
        Harness.Compare("existing", [":memory:", "create table t(x,y)", $".import --csv {path} t", ".mode quote", "select * from t"]);
        Harness.Compare("existing skip", [":memory:", "create table t(x,y)", $".import --csv --skip 1 {path} t", ".mode quote", "select * from t"]);
        Harness.Compare("existing narrower", [":memory:", "create table t(x)", $".import --csv {path} t", ".mode quote", "select * from t"]);
        Harness.Compare("missing", [":memory:", ".import --csv /nonexistent/file.csv t"]);
        string empty = Harness.PathIn("empty.csv");
        File.WriteAllText(empty, string.Empty);
        Harness.Compare("empty", [":memory:", $".import --csv {empty} t", ".schema"]);
    }
}

public class RecoverTests
{
    [Fact]
    public void RecoverLoadsCleanlyRecoversEveryLiveRowAndNeverTouchesTheFile()
    {
        Assert.SkipUnless(Harness.OracleAvailable, "the pinned sqlite3 is not installed");
        var script = new System.Text.StringBuilder("pragma secure_delete=off;\ncreate table users(id integer primary key, name text, email text, score real, bio blob);\ncreate table log(ts int, msg text);\ncreate index ix on users(name);\ncreate table tmp(x,y);\n");
        var rnd = new Random(5);
        for (int i = 1; i <= 800; i++)
        {
            script.Append(System.FormattableString.Invariant($"insert into users values({i},'user{i}','u{i}@example.com',{rnd.NextDouble() * 100:R},x'{string.Concat(Enumerable.Repeat("ab", rnd.Next(0, 40)))}');\n"));
        }

        for (int i = 0; i < 300; i++)
        {
            script.Append($"insert into log values({i},'message number {i} with some text');\n");
        }

        for (int i = 0; i < 400; i++)
        {
            script.Append($"insert into tmp values({i},'temporary row {i} to be dropped');\n");
        }

        script.Append("delete from users where id%3=0;\ndelete from log where ts<100;\ndrop table tmp;\nupdate users set name='changed'||id where id%7=0;\n");
        string db = Harness.MakeDb("recover.db", script.ToString());
        string before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(db)));
        string[] filesBefore = Directory.GetFiles(Harness.Scratch, "recover.db*").OrderBy(f => f, StringComparer.Ordinal).ToArray();

        Harness.Outcome rec = Harness.Ours([db, ".recover"]);
        Assert.Equal(0, rec.Exit);
        Assert.Equal(before, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(db))));
        Assert.Equal(filesBefore, Directory.GetFiles(Harness.Scratch, "recover.db*").OrderBy(f => f, StringComparer.Ordinal).ToArray());
        Assert.Contains("recovered 734 live rows", rec.Stderr, StringComparison.Ordinal);
        Assert.Contains("carved", rec.Stderr, StringComparison.Ordinal);

        string loaded = Harness.PathIn("recovered.db");
        File.Delete(loaded);
        Harness.Outcome load = Harness.Oracle([loaded], rec.Stdout);
        Assert.Equal(string.Empty, load.Stderr);
        Assert.True(Harness.IntegrityOk(loaded, out string detail), detail);

        // every live row is back, unchanged
        Harness.Outcome live = Harness.Oracle([db, "select * from users order by id", "select * from log order by ts"]);
        Harness.Outcome got = Harness.Oracle([loaded, "select * from users order by id", "select * from log order by ts"]);
        Assert.Equal(live.Stdout, got.Stdout);

        // and rows carved from free space are kept apart, with their source and confidence
        Harness.Outcome carved = Harness.Oracle([loaded, "select count(*), min(confidence) >= 0.5 from lost_and_found"]);
        Assert.True(int.Parse(carved.Stdout.Split('|')[0], System.Globalization.CultureInfo.InvariantCulture) > 100, carved.Stdout);
        Assert.EndsWith("|1", carved.Stdout.TrimEnd(), StringComparison.Ordinal);
        Assert.Equal(before, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(db))));
    }
}

public class ImportFuzzTests
{
    /// <summary>Random text over the characters that matter to the reader: the same file imported by both shells must give the same table and the same warnings.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void RandomDelimitedTextImportsLikeTheShell(int seed)
    {
        Assert.SkipUnless(Harness.OracleAvailable, "the pinned sqlite3 is not installed");
        var rnd = new Random(seed);
        string alphabet = "ab,\"\n\r ;|\t1";
        string path = Harness.PathIn("fuzz" + seed + ".csv");
        for (int i = 0; i < 150; i++)
        {
            var sb = new System.Text.StringBuilder();
            int n = rnd.Next(0, 40);
            for (int k = 0; k < n; k++)
            {
                sb.Append(alphabet[rnd.Next(alphabet.Length)]);
            }

            File.WriteAllText(path, sb.ToString());
            foreach (string opt in new[] { "--csv", "--csv --skip 1", "" })
            {
                Harness.Compare($"fuzz {seed}/{i} [{opt}] {sb.ToString().Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal)}",
                    [":memory:", $".import {opt} {path} t", ".mode quote", "select * from t"]);
            }
        }
    }
}

public class RecoverHostileFileTests
{
    [Fact]
    public void DamagedFilesAreReportedNotCrashedOn()
    {
        string good = Harness.MakeDb("hostile-src.db", "create table t(a,b);\n" + string.Concat(Enumerable.Range(0, 500).Select(i => $"insert into t values({i},'row {i} with some padding text to fill pages');\n")) + "delete from t where a%2=0;\n");
        byte[] bytes = File.ReadAllBytes(good);
        var rnd = new Random(7);
        for (int i = 0; i < 40; i++)
        {
            byte[] copy = (byte[])bytes.Clone();
            switch (i % 4)
            {
                case 0:
                    for (int k = 0; k < 20; k++)
                    {
                        copy[100 + rnd.Next(copy.Length - 100)] ^= (byte)(1 << rnd.Next(8));
                    }

                    break;
                case 1:
                    Array.Resize(ref copy, rnd.Next(1, copy.Length));
                    break;
                case 2:
                    rnd.NextBytes(copy.AsSpan(rnd.Next(100, copy.Length - 64), 64));
                    break;
                default:
                    rnd.NextBytes(copy.AsSpan(0, 120));
                    break;
            }

            string path = Harness.PathIn("hostile.db");
            File.WriteAllBytes(path, copy);
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(copy));
            Harness.Outcome r = Harness.Ours([path, ".recover"]);
            Assert.True(r.Exit is 0 or 1, "unexpected exit code " + r.Exit);
            Assert.DoesNotContain("Unhandled exception", r.Stderr, StringComparison.Ordinal);
            Assert.Equal(hash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
            Assert.False(File.Exists(path + "-wal") || File.Exists(path + "-shm") || File.Exists(path + "-journal"));
        }
    }
}
