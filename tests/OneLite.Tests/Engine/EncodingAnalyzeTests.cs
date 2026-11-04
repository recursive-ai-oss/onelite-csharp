using System.IO;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// <c>PRAGMA encoding</c> before the first table (the pragma page: the encoding can be chosen only while the database is still empty) and the row
/// order of sqlite_stat1 after ANALYZE, both checked against the pinned shell.
/// </summary>
public sealed class EncodingAnalyzeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-enc-" + Guid.NewGuid().ToString("N"));

    public EncodingAnalyzeTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string P(string name) => Path.Combine(_dir, name);

    private static void Need() => Assert.SkipUnless(File.Exists(OraclePaths.Cli), "the pinned oracle is not installed");

    private static void Script(string path, string sql)
    {
        using Database d = Database.Open(path);
        using Connection c = d.Connect();
        string rest = sql;
        while (!string.IsNullOrWhiteSpace(rest))
        {
            using Statement s = c.Prepare(rest);
            rest = s.Tail;
            while (s.Step())
            {
            }
        }
    }

    private static string Pragma(string path, string name) => SqliteCli.Run(path, "pragma " + name + ";").Stdout.Trim();

    private const string Body =
        "CREATE TABLE t(id INTEGER PRIMARY KEY, a TEXT, b BLOB); CREATE INDEX ta ON t(a); " +
        "INSERT INTO t VALUES(1,'caf\u00e9 \u00fcber',x'00ff'),(2,'\u65e5\u672c\u8a9e',NULL),(3,'plain',x''),(4,'\U0001F600 emoji',zeroblob(5000)); " +
        "CREATE VIEW v AS SELECT a FROM t; CREATE TRIGGER tr AFTER INSERT ON t BEGIN SELECT 1; END;";

    [Theory]
    [InlineData("UTF-16le", "UTF-16le")]
    [InlineData("UTF-16be", "UTF-16be")]
    [InlineData("UTF-16", "UTF-16le")]
    [InlineData("utf16be", "UTF-16be")]
    [InlineData("UTF-8", "UTF-8")]
    [InlineData("utf8", "UTF-8")]
    public void EncodingChosenBeforeTheFirstTableIsInTheFile(string asked, string expected)
    {
        Need();
        string ours = P("o-" + asked + ".db"), theirs = P("t-" + asked + ".db");
        string script = $"PRAGMA encoding='{asked}'; PRAGMA page_size=2048; " + Body + " PRAGMA encoding;";
        Script(ours, script);
        SqliteCli.ApplyScript(theirs, script);
        Assert.Equal(expected, Pragma(ours, "encoding"));
        Assert.Equal(Pragma(theirs, "encoding"), Pragma(ours, "encoding"));
        Assert.Equal(Pragma(theirs, "page_size"), Pragma(ours, "page_size"));
        Assert.True(PinnedSqlite.IntegrityOk(ours, out string detail), detail);
        Assert.Equal(SqliteCli.Dump(theirs), SqliteCli.Dump(ours));
    }

    [Fact]
    public void EncodingAfterTheFirstTableIsIgnoredAndAnUnknownNameIsAnErrorOnAFreshConnection()
    {
        Need();
        string path = P("fixed.db");
        Script(path, "CREATE TABLE t(a); PRAGMA encoding='UTF-16le'; INSERT INTO t VALUES('x');");
        Assert.Equal("UTF-8", Pragma(path, "encoding"));

        string empty = P("empty.db");
        OneLiteException e = Assert.Throws<OneLiteException>(() => Script(empty, "PRAGMA encoding='UTF-32';"));
        Assert.Equal("unsupported encoding: UTF-32", e.Message);
        Assert.Equal(ResultCode.Error, e.ExtendedCode);
    }

    [Fact]
    public void TheDatabaseOptionStillSetsTheEncoding()
    {
        Need();
        string path = P("opt.db");
        using (Database d = Database.Open(path, new DatabaseOptions { Encoding = Storage.TextEncoding.Utf16Be }))
        using (Connection c = d.Connect())
        {
            using Statement s = c.Prepare("CREATE TABLE t(a); INSERT INTO t VALUES('\u00e9');");
            while (s.Step())
            {
            }
        }

        Assert.Equal("UTF-16be", Pragma(path, "encoding"));
    }

    // ANALYZE of every table lists the tables as the reference's schema hash does; the rows are the same either way, the rowids follow the order.
    [Theory]
    [InlineData("alpha")]
    [InlineData("alpha,beta")]
    [InlineData("users,orders,items")]
    [InlineData("t1,g,z")]
    [InlineData("zeta,eta,theta")]
    public void AnalyzeOfUpToThreeTablesWritesTheStatRowsInTheReferenceOrder(string tables)
    {
        Need();
        string sql = "";
        foreach (string t in tables.Split(','))
        {
            sql += $"CREATE TABLE {t}(x, y); CREATE INDEX {t}_x ON {t}(x); CREATE INDEX {t}_y ON {t}(y); INSERT INTO {t} VALUES(1,2),(1,3),(2,3);";
        }

        string ours = P("a-o.db"), theirs = P("a-t.db");
        string script = sql + "ANALYZE; ";
        Script(ours, script);
        SqliteCli.ApplyScript(theirs, script);
        Assert.Equal(SqliteCli.Dump(theirs), SqliteCli.Dump(ours));
        // a second ANALYZE, a single table and a single index on the analysed file
        string first = tables.Split(',')[0];
        foreach (string again in new[] { "ANALYZE;", $"ANALYZE {first};", $"ANALYZE {first}_y;" })
        {
            Script(ours, again);
            SqliteCli.ApplyScript(theirs, again);
            Assert.Equal(SqliteCli.Dump(theirs), SqliteCli.Dump(ours));
        }
    }

    [Fact]
    public void AnalyzeListsAWithoutRowidTablesIndexesNewestFirstWithThePrimaryKeyLast()
    {
        Need();
        string script = "CREATE TABLE w(k PRIMARY KEY, v, u) WITHOUT ROWID; CREATE INDEX wv ON w(v); CREATE INDEX wu ON w(u); " +
            "INSERT INTO w VALUES(1,1,1),(2,1,2),(3,2,2); CREATE TABLE r(a UNIQUE, b); CREATE INDEX rb ON r(b); INSERT INTO r VALUES(1,1),(2,1); ANALYZE;";
        string ours = P("w-o.db"), theirs = P("w-t.db");
        Script(ours, script);
        SqliteCli.ApplyScript(theirs, script);
        Assert.Equal(SqliteCli.Dump(theirs), SqliteCli.Dump(ours));
    }
}
