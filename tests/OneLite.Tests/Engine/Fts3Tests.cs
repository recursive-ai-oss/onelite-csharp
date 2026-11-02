using System;
using System.IO;
using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Oracle;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>FTS3 and FTS4: query results, auxiliary functions, special commands and error text against the pinned shell, and file interop both ways.</summary>
public sealed class Fts3Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-fts3-" + Guid.NewGuid().ToString("N"));

    public Fts3Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("ONELITE_KEEP") != "1")
        {
            Directory.Delete(_dir, true);
        }
    }

    private static void NeedOracle() => Assert.SkipUnless(File.Exists(OraclePaths.Cli), "the pinned sqlite3 shell is not installed");

    private static readonly string[] Words =
    [
        "alpha", "beta", "gamma", "delta", "run", "running", "ran", "jump", "jumped", "quick", "brown", "fox", "lazy", "dog", "the", "a", "caf\u00e9",
        "na\u00efve", "\u00dcber", "stra\u00dfe", "\u00e9clair", "\u4e2d\u6587", "Zoe", "foo-bar", "3.14", "42", "caresses", "ponies", "relational", "happiness",
    ];

    private static string Rows(int cols, int n = 60)
    {
        var rnd = new Random(77);
        var sb = new StringBuilder();
        for (int i = 1; i <= n; i++)
        {
            sb.Append("INSERT INTO t(docid");
            for (int c = 0; c < cols; c++)
            {
                sb.Append(", c").Append(c);
            }

            sb.Append(") VALUES(").Append(i * 3);
            for (int c = 0; c < cols; c++)
            {
                var d = new StringBuilder();
                int len = rnd.Next(0, 14);
                for (int w = 0; w < len; w++)
                {
                    d.Append(Words[rnd.Next(Words.Length)]).Append(rnd.Next(5) == 0 ? ", " : " ");
                }

                sb.Append(", '").Append(d.ToString().Replace("'", "''").TrimEnd()).Append('\'');
            }

            sb.Append(");");
        }

        return sb.ToString();
    }

    private static string Setup(string module, string options, int cols = 3) =>
        "CREATE VIRTUAL TABLE t USING " + module + "(" + string.Join(", ", Enumerable.Range(0, cols).Select(c => "c" + c)) + (options.Length > 0 ? ", " + options : "") + ");" + Rows(cols);

    private static string[] Queries(string match) =>
    [
        $"SELECT docid FROM t WHERE t MATCH '{match}' ORDER BY docid",
        $"SELECT docid, offsets(t) FROM t WHERE t MATCH '{match}' ORDER BY docid",
        $"SELECT docid, snippet(t) FROM t WHERE t MATCH '{match}' ORDER BY docid",
        $"SELECT docid, snippet(t, '[', ']', '..', -1, 6) FROM t WHERE t MATCH '{match}' ORDER BY docid",
        $"SELECT docid, hex(matchinfo(t)) FROM t WHERE t MATCH '{match}' ORDER BY docid",
    ];

    private (string Ours, string Shell) Both(string setup, params string[] queries)
    {
        string o = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"), s = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");
        var ours = new StringBuilder();
        var shell = new StringBuilder();
        using (var db = Database.Open(o))
        using (Connection c = db.Connect())
        {
            Assert.Equal(string.Empty, Script(db, setup));
            foreach (string q in queries)
            {
                ours.Append(RTreeTests.Ours(c, q));
            }
        }

        Assert.Equal(string.Empty, RTreeTests.Shell(s, setup));
        foreach (string q in queries)
        {
            shell.Append(RTreeTests.Shell(s, q));
        }

        File.WriteAllText(Path.Combine(_dir, "ours.txt"), ours.ToString());
        File.WriteAllText(Path.Combine(_dir, "shell.txt"), shell.ToString());
        return (ours.ToString(), shell.ToString());
    }

    private static string Script(Database db, string sql)
    {
        try
        {
            using Connection c = db.Connect();
            Runner.Script(c, sql);
            return string.Empty;
        }
        catch (OneLiteException e)
        {
            return "Error: " + e.Message + "\n";
        }
    }

    [Theory]
    [InlineData("fts3", "")]
    [InlineData("fts3", "tokenize=porter")]
    [InlineData("fts3", "tokenize=unicode61 \"remove_diacritics=0\"")]
    [InlineData("fts4", "")]
    [InlineData("fts4", "prefix='2,3', matchinfo=fts3")]
    [InlineData("fts4", "order=desc, notindexed=c2")]
    [InlineData("fts4", "tokenize=unicode61 \"tokenchars=-\"")]
    public void QueriesAndAuxiliaryFunctionsMatchTheShell(string module, string options)
    {
        NeedOracle();
        string[] matches = ["run*", "alpha beta", "alpha OR fox -dog", "\"the quick\"", "c1:jump*", "alpha NEAR beta", "alpha NEAR/3 the", "-gamma", "caf\u00e9", "\"jump\" \"fox\"", "alpha OR", "\"unterminated", "c9:fox"];
        var qs = matches.SelectMany(Queries).Concat(
        [
            "SELECT count(*) FROM t",
            "SELECT c0 FROM t WHERE docid = 30",
            "SELECT docid, matchinfo(t, 'nalx') IS NOT NULL FROM t WHERE t MATCH 'the' ORDER BY docid",
            "SELECT docid, hex(matchinfo(t, 'n')) FROM t WHERE t MATCH 'the'",
            "INSERT INTO t(t) VALUES('integrity-check')",
            "UPDATE t SET c1 = 'changed text here' WHERE docid % 7 = 0",
            "UPDATE t SET docid = docid WHERE docid % 9 = 0",
            "DELETE FROM t WHERE docid % 5 = 0",
            "INSERT INTO t(t) VALUES('optimize')",
            "INSERT INTO t(t) VALUES('rebuild')",
            "SELECT docid FROM t WHERE t MATCH 'changed' ORDER BY docid",
            "SELECT count(*) FROM t WHERE t MATCH 'run*'",
            "INSERT INTO t(t) VALUES('integrity-check')",
        ]).ToArray();
        (string ours, string shell) = Both(Setup(module, options), qs);
        Assert.Equal(Normalize(shell), Normalize(ours));
    }

    private static string Normalize(string s) => string.Join('\n', s.Split('\n').Where(l => !l.StartsWith("  ", StringComparison.Ordinal) && !l.Contains("error here ---", StringComparison.Ordinal)));

    [Fact]
    public void ExternalAndContentlessTablesMatchTheShell()
    {
        NeedOracle();
        string setup = "CREATE TABLE d(a, b); INSERT INTO d VALUES('alpha beta', 'x y'), ('beta gamma', 'y z'), ('delta', 'alpha');" +
            "CREATE VIRTUAL TABLE e USING fts4(content=d, a, b); INSERT INTO e(e) VALUES('rebuild');" +
            "CREATE VIRTUAL TABLE c USING fts4(content='', a, b); INSERT INTO c(docid, a, b) VALUES(1, 'alpha beta', 'x'), (2, 'beta', 'alpha');";
        (string ours, string shell) = Both(setup, "SELECT rowid FROM e WHERE e MATCH 'alpha' ORDER BY rowid", "SELECT a, b FROM e WHERE e MATCH 'b:alpha'",
            "SELECT docid FROM c WHERE c MATCH 'alpha'", "SELECT a FROM c WHERE c MATCH 'alpha'", "INSERT INTO e(e) VALUES('integrity-check')",
            "DELETE FROM c WHERE docid = 1", "INSERT INTO c(c, docid, a, b) VALUES('delete', 2, 'beta', 'alpha')", "SELECT docid FROM c WHERE c MATCH 'beta'",
            "INSERT INTO c(c) VALUES('integrity-check')", "SELECT * FROM c", "UPDATE c SET a = 'x'");
        Assert.Equal(Normalize(shell), Normalize(ours));
    }

    [Fact]
    public void Fts4AuxMatchesTheShell()
    {
        NeedOracle();
        string setup = "CREATE VIRTUAL TABLE t USING fts4(a, b, prefix='2'); INSERT INTO t VALUES('hello world hello', 'world peace'), ('hello there', ''), ('hello', 'hello hello x');" +
            "CREATE VIRTUAL TABLE v USING fts4aux(t); CREATE VIRTUAL TABLE t5 USING fts4(a, languageid='lid');" +
            "INSERT INTO t5(a, lid) VALUES('hello world hello', 0), ('hello there', 1), ('zz yy', 1); CREATE VIRTUAL TABLE v5 USING fts4aux(t5);" +
            "CREATE VIRTUAL TABLE temp.v6 USING fts4aux(main, t5); CREATE VIRTUAL TABLE v3 USING fts4aux(nosuch);" +
            "CREATE VIRTUAL TABLE t7 USING fts4(a, b, c, languageid='lid', prefix='2'); INSERT INTO t7(a, b, c, lid) VALUES('x y', 'y z', 'z', 0), ('aa', 'bb', '', 1), ('aaa', '', '', 2);" +
            "CREATE VIRTUAL TABLE v7 USING fts4aux(t7); CREATE VIRTUAL TABLE t8 USING fts4(a, b, c); INSERT INTO t8 VALUES('p', 'q', ''), ('r', '', 'r');CREATE VIRTUAL TABLE v8 USING fts4aux(t8);";
        (string ours, string shell) = Both(setup, "SELECT * FROM v", "SELECT typeof(col), col FROM v LIMIT 3", "SELECT * FROM v WHERE term = 'hello'", "SELECT * FROM v WHERE term LIKE 'h%' AND col = '*'",
            "SELECT * FROM v WHERE col = 1", "SELECT rowid, term FROM v LIMIT 3", "SELECT * FROM v ORDER BY term DESC LIMIT 4", "SELECT * FROM v5", "SELECT * FROM v5 WHERE languageid = 1",
            "SELECT * FROM v5 WHERE languageid = 1 AND term = 'zz'", "SELECT * FROM v5 WHERE languageid = 5", "SELECT * FROM v7", "SELECT * FROM v7 WHERE languageid = 1", "SELECT * FROM v7 WHERE languageid = 2", "SELECT * FROM v8", "SELECT term FROM v8 ORDER BY term", "SELECT term, col FROM v8 ORDER BY term, col", "SELECT term, col FROM v8 ORDER BY term LIMIT 3", "SELECT * FROM temp.v6 WHERE languageid = 1", "SELECT * FROM v3",
            "CREATE VIRTUAL TABLE v2 USING fts4aux(main, t)", "CREATE VIRTUAL TABLE v4 USING fts4aux(t, 5)", "INSERT INTO v VALUES(1, 2, 3, 4)",
            "DELETE FROM t WHERE docid = 2", "SELECT * FROM v", "UPDATE t SET a = 'zz' WHERE docid = 1", "SELECT * FROM v", "INSERT INTO t(t) VALUES('optimize')", "SELECT * FROM v",
            "DROP TABLE t", "SELECT * FROM v");
        Assert.Equal(Normalize(shell), Normalize(ours));
    }

    [Fact]
    public void LanguageIdsAndMergeCommandsMatchTheShell()
    {
        NeedOracle();
        var setup = new StringBuilder("CREATE VIRTUAL TABLE t USING fts4(a, languageid='lid', prefix='2');");
        for (int i = 1; i <= 40; i++)
        {
            setup.Append($"INSERT INTO t(docid, a, lid) VALUES({i}, '{Words[i % Words.Length]} {Words[(i * 7) % Words.Length]} run{i % 3}', {i % 3});");
        }

        (string ours, string shell) = Both(setup.ToString(), "SELECT docid FROM t WHERE t MATCH 'run1' AND lid = 1 ORDER BY docid", "SELECT docid FROM t WHERE t MATCH 'ru*' AND lid = 2 ORDER BY docid",
            "SELECT docid FROM t WHERE t MATCH 'run1' ORDER BY docid", "INSERT INTO t(t) VALUES('integrity-check')", "INSERT INTO t(t) VALUES('merge=2,4')", "INSERT INTO t(t) VALUES('automerge=4')",
            "INSERT INTO t(t) VALUES('integrity-check')", "SELECT docid FROM t WHERE t MATCH 'ru*' AND lid = 0 ORDER BY docid", "UPDATE t SET lid = 5 WHERE docid = 3", "SELECT docid FROM t WHERE t MATCH 'run0' AND lid = 5",
            "INSERT INTO t(t) VALUES('bogus')");
        Assert.Equal(Normalize(shell), Normalize(ours));
    }

    [Fact]
    public void TheFileWeWriteIsAcceptedByTheShellAndTheShellsFileByUs()
    {
        NeedOracle();
        string o = Path.Combine(_dir, "ours.db"), s = Path.Combine(_dir, "shell.db");
        string setup = Setup("fts4", "prefix='2,3'") + "UPDATE t SET c1 = 'changed text here' WHERE docid % 7 = 0; DELETE FROM t WHERE docid % 5 = 0; INSERT INTO t(t) VALUES('optimize');";
        using (var db = Database.Open(o))
        {
            Assert.Equal(string.Empty, Script(db, setup));
        }

        Assert.Equal(string.Empty, RTreeTests.Shell(s, setup));
        const string q = "SELECT docid FROM t WHERE t MATCH 'run*' ORDER BY docid";
        Assert.Equal(string.Empty, RTreeTests.Shell(o, "INSERT INTO t(t) VALUES('integrity-check')"));
        Assert.Equal("ok\n", RTreeTests.Shell(o, "PRAGMA integrity_check"));
        Assert.Equal(RTreeTests.Shell(s, q), RTreeTests.Shell(o, q));
        using (var db = Database.Open(s))
        using (Connection c = db.Connect())
        {
            Assert.Equal(string.Empty, RTreeTests.Ours(c, "INSERT INTO t(t) VALUES('integrity-check')"));
            Assert.Equal(RTreeTests.Shell(s, q), RTreeTests.Ours(c, q));
            Assert.Equal(string.Empty, Script(db, "INSERT INTO t(docid, c0) VALUES(1000, 'run runner'); DELETE FROM t WHERE docid = 3; INSERT INTO t(t) VALUES('merge=2,3')"));
        }

        Assert.Equal(string.Empty, RTreeTests.Shell(s, "INSERT INTO t(t) VALUES('integrity-check')"));
        Assert.Equal("ok\n", RTreeTests.Shell(s, "PRAGMA integrity_check"));
    }

    /// <summary>compress= and uncompress= name SQL functions; hex and unhex stand in for a compressor, since both engines have them built in.</summary>
    [Fact]
    public void CompressedColumnsMatchTheShell()
    {
        NeedOracle();
        string setup = "CREATE VIRTUAL TABLE t USING fts4(a, b, compress=hex, uncompress=unhex); INSERT INTO t(docid, a, b) VALUES(1, 'hello world', 'foo bar'), (2, 'hello there', 'baz');";
        (string ours, string shell) = Both(setup, "SELECT docid FROM t WHERE t MATCH 'hello'", "SELECT docid, a, b FROM t WHERE t MATCH 'foo'", "SELECT docid, typeof(a), a FROM t",
            "SELECT docid, snippet(t), offsets(t) FROM t WHERE t MATCH 'hello'", "SELECT docid, hex(c0a), hex(c1b) FROM t_content", "UPDATE t SET a = 'changed text' WHERE docid = 1",
            "SELECT docid FROM t WHERE t MATCH 'changed'", "INSERT INTO t(t) VALUES('rebuild')", "SELECT docid FROM t WHERE t MATCH 'hello'", "INSERT INTO t(t) VALUES('integrity-check')",
            "DELETE FROM t WHERE docid = 2", "SELECT docid, a FROM t", "CREATE VIRTUAL TABLE u USING fts4(a, compress=hex)", "CREATE VIRTUAL TABLE v USING fts4(a, uncompress=unhex)",
            "CREATE VIRTUAL TABLE w USING fts4(a, compress=nosuch, uncompress=nosuch)", "INSERT INTO w VALUES('x')");
        Assert.Equal(Normalize(shell), Normalize(ours));
    }

    [Fact]
    public void RowidSetToItsOwnValueSucceedsInTheEngineAndInFts()
    {
        using var db = Database.Open(Path.Combine(_dir, "r.db"));
        using Connection c = db.Connect();
        Runner.Script(c, "CREATE VIRTUAL TABLE t USING fts3(a); INSERT INTO t(docid, a) VALUES(5, 'x y'); UPDATE t SET docid = 5, a = 'x z' WHERE docid = 5");
        Assert.Equal("5\n", RTreeTests.Ours(c, "SELECT docid FROM t WHERE t MATCH 'z'"));
    }

    /// <summary>Long chains of AND, OR and NEAR hit the reference's expression-depth limit (4095 phrases, 13 NEAR operands) with its own error, and never recurse without bound.</summary>
    [Theory]
    [InlineData("a OR ", 4094)]
    [InlineData("a OR ", 4095)]
    [InlineData("a ", 4094)]
    [InlineData("a ", 4095)]
    [InlineData("a OR b ", 2047)]
    [InlineData("a OR b ", 2048)]
    [InlineData("a NEAR ", 12)]
    [InlineData("a NEAR ", 13)]
    [InlineData("a OR ", 20000)]
    public void LongChainsHitTheDepthLimitLikeTheShell(string unit, int n)
    {
        NeedOracle();
        string setup = "CREATE VIRTUAL TABLE t USING fts3(a); INSERT INTO t VALUES('a b')";
        string q = $"SELECT count(*) FROM t WHERE t MATCH '{string.Concat(Enumerable.Repeat(unit, n))}a'";
        (string ours, string shell) = Both(setup, q);
        Assert.Equal(Normalize(shell), Normalize(ours));
    }
}
