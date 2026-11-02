using System.Collections.Generic;
using System.IO;
using System.Linq;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>PRAGMA optimize and ANALYZE against the pinned shell: the sqlite_stat1 rows left behind and the tables the pragma picks.</summary>
public sealed class OptimizeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-optimize-" + Guid.NewGuid().ToString("N"));

    public OptimizeTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string P(string name) => Path.Combine(_dir, name);

    private static void Need() => Assert.SkipUnless(File.Exists(OraclePaths.Cli), "the pinned oracle is not installed");

    private static string Rows(string table, string from, int n, string expr = "i") =>
        $"WITH RECURSIVE r(i) AS (SELECT {from} UNION ALL SELECT i+1 FROM r WHERE i<{n}) INSERT INTO {table} SELECT {expr} FROM r;";

    // Every kind of table: indexed, partial and expression indexes, WITHOUT ROWID, no index, empty with and without an index.
    private static string Schema(int pageSize) =>
        $"PRAGMA page_size={pageSize};" +
        "CREATE TABLE t(a, b, c); CREATE INDEX ta ON t(a); CREATE INDEX tbc ON t(b, c);" +
        "CREATE TABLE p(x, y); CREATE INDEX py ON p(y) WHERE y > 3; CREATE INDEX px ON p(lower(x));" +
        "CREATE TABLE w(k PRIMARY KEY, v) WITHOUT ROWID; CREATE INDEX wv ON w(v);" +
        "CREATE TABLE n1(x); CREATE TABLE n2(x); CREATE TABLE e1(x); CREATE INDEX e1x ON e1(x); CREATE TABLE e2(x);" +
        "CREATE TABLE i1(x); CREATE INDEX i1x ON i1(x); CREATE TABLE u(x UNIQUE, y TEXT COLLATE NOCASE); CREATE INDEX uy ON u(y);" +
        Rows("t", "1", 300, "i % 7, i % 5, i") + Rows("p", "1", 40, "'K' || i % 3, i") + Rows("w", "1", 120, "i, i % 9") +
        Rows("n1", "1", 50) + Rows("n2", "1", 3) + Rows("i1", "1", 1) + Rows("u", "1", 70, "i, 'v' || i % 4");

    private static string Stat(string path) => SqliteCli.Run(path, "select tbl || '|' || coalesce(idx, '') || '|' || stat from sqlite_stat1 order by 1;").Stdout;

    private static string Ours(string path, string script, bool collectAnalyzeRows)
    {
        var sb = new System.Text.StringBuilder();
        using Database d = Database.Open(path);
        using Connection c = d.Connect();
        foreach (string stmt in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using Statement s = c.Prepare(stmt);
            while (s.Step())
            {
                if (collectAnalyzeRows && s.ColumnCount > 0 && s.GetString(0) is { } text && text.StartsWith("ANALYZE", StringComparison.Ordinal))
                {
                    sb.Append(text).Append('\n');
                }
            }
        }

        return Sorted(sb.ToString());
    }

    private static string Theirs(string path, string script)
    {
        string o = SqliteCli.Run(path, script + ";").Stdout;
        return Sorted(string.Join('\n', o.Split('\n').Where(l => l.StartsWith("ANALYZE", StringComparison.Ordinal))) + "\n");
    }

    // The shell fills sqlite_stat1 in the order of its internal table hash; the rows are the same set in another order.
    private static string Norm(string dump)
    {
        List<string> lines = [.. dump.Split((char)10)];
        List<string> stat = [.. lines.Where(l => l.StartsWith("INSERT INTO sqlite_stat1", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
        int first = lines.FindIndex(l => l.StartsWith("INSERT INTO sqlite_stat1", StringComparison.Ordinal));
        if (first < 0)
        {
            return dump;
        }

        lines.RemoveAll(l => l.StartsWith("INSERT INTO sqlite_stat1", StringComparison.Ordinal));
        lines.InsertRange(first, stat);
        return string.Join((char)10, lines);
    }

    private static string Sorted(string s) => string.Join('\n', s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));

    public static IEnumerable<TheoryDataRow<int, string>> Cases() =>
    [
        new(4096, "PRAGMA optimize"), new(1024, "PRAGMA optimize"), new(512, "PRAGMA optimize(0x10002)"), new(4096, "PRAGMA optimize(0x10002)"),
        new(8192, "PRAGMA main.optimize"), new(4096, "PRAGMA optimize(2)"), new(4096, "PRAGMA optimize(0x10003)"), new(4096, "PRAGMA optimize(-2)"),
        new(65536, "ANALYZE"), new(4096, "ANALYZE t"), new(4096, "ANALYZE ta"), new(1024, "ANALYZE main"), new(4096, "ANALYZE w"), new(4096, "ANALYZE p"),
        new(4096, "PRAGMA optimize(0)"), new(4096, "PRAGMA optimize(1)"),
        // a second pass: after the first the tables have statistics, and some grow tenfold
        new(4096, "PRAGMA optimize; " + Rows("t", "1000", 1400, "i % 11, i % 13, i") + " SELECT count(*) FROM t WHERE a = 3; PRAGMA optimize"),
        new(4096, "ANALYZE; " + Rows("n2", "100", 1000) + Rows("n1", "100", 2000) + "PRAGMA optimize(0x10002)"),
        new(4096, "ANALYZE; DELETE FROM t WHERE c > 20; SELECT count(*) FROM t WHERE b = 3; PRAGMA optimize"),
        new(4096, "ANALYZE; " + Rows("i1", "10", 200) + " SELECT * FROM i1 WHERE x = 5; SELECT * FROM e1 WHERE x = 1; PRAGMA optimize"),
        new(4096, "ANALYZE; CREATE INDEX tc ON t(c); PRAGMA optimize"),
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public void LeavesTheShellsStatistics(int pageSize, string script)
    {
        Need();
        string theirs = P("t.db"), ours = P("o.db");
        SqliteCli.ApplyScript(theirs, Schema(pageSize));
        File.Copy(theirs, ours);
        SqliteCli.Run(theirs, script + ";");
        _ = Ours(ours, script, false);
        Assert.Equal(Stat(theirs), Stat(ours));
        Assert.True(PinnedSqlite.IntegrityOk(ours, out string detail), detail);
        Assert.Equal(Norm(SqliteCli.Dump(theirs)), Norm(SqliteCli.Dump(ours)));
    }

    public static IEnumerable<TheoryDataRow<string>> Debug() =>
    [
        new("PRAGMA optimize(0xffff)"), new("PRAGMA optimize(0x1ffff)"), new("PRAGMA optimize(-1)"), new("PRAGMA optimize(3)"), new("PRAGMA optimize(0x10003)"),
        new("ANALYZE; PRAGMA optimize(0xffff)"),
        new("ANALYZE; " + Rows("t", "1000", 5000, "i % 11, i % 13, i") + " PRAGMA optimize(0x1ffff)"),
        new("ANALYZE; " + Rows("t", "1000", 5000, "i % 11, i % 13, i") + " SELECT count(*) FROM t WHERE a = 3; PRAGMA optimize(0xffff)"),
        new("ANALYZE; " + Rows("t", "1000", 5000, "i % 11, i % 13, i") + " SELECT count(*) FROM t WHERE c > 3; PRAGMA optimize(0xffff)"),
        new("ANALYZE; " + Rows("t", "1000", 5000, "i % 11, i % 13, i") + " SELECT count(*) FROM t WHERE c + 0 = 3; PRAGMA optimize(0xffff)"),
        new("ANALYZE; " + Rows("w", "1000", 5000, "i, i") + " SELECT count(*) FROM w WHERE k = 3; PRAGMA optimize(0xffff)"),
        new("ANALYZE; DELETE FROM u; PRAGMA optimize(0x1ffff)"),
        new("CREATE VIEW vv AS SELECT * FROM t; CREATE TABLE \"q\"\"r s\"(x); CREATE INDEX qx ON \"q\"\"r s\"(x); INSERT INTO \"q\"\"r s\" VALUES(1); PRAGMA optimize(0xffff)"),
    ];

    [Theory]
    [MemberData(nameof(Debug))]
    public void PicksTheShellsTables(string script)
    {
        Need();
        string theirs = P("t.db"), ours = P("o.db");
        SqliteCli.ApplyScript(theirs, Schema(4096));
        File.Copy(theirs, ours);
        Assert.Equal(Theirs(theirs, script), Ours(ours, script, true));
    }

    [Fact]
    public void AReadOnlyRunTakesNoWriteLock()
    {
        Need();
        string f = P("ro.db");
        SqliteCli.ApplyScript(f, "CREATE TABLE t(a); CREATE INDEX ta ON t(a); INSERT INTO t VALUES(1); ANALYZE; ");
        using Database d = Database.OpenReadOnly(f);
        using Connection c = d.Connect();
        using Statement s = c.Prepare("PRAGMA optimize");
        while (s.Step())
        {
        }
    }

    [Fact]
    public void AnalysisThatIsNeededCannotRunReadOnly()
    {
        Need();
        string f = P("ro2.db");
        SqliteCli.ApplyScript(f, "CREATE TABLE t(a); CREATE INDEX ta ON t(a); INSERT INTO t VALUES(1);");
        string shell = SqliteCli.Run(f, "PRAGMA optimize;", "-readonly").Stderr.Trim();
        using Database d = Database.OpenReadOnly(f);
        using Connection c = d.Connect();
        using Statement s = c.Prepare("PRAGMA optimize");
        OneLiteException e = Assert.Throws<OneLiteException>(() => { while (s.Step()) { } });
        Assert.Contains(e.Message, shell, StringComparison.Ordinal);
    }
}
