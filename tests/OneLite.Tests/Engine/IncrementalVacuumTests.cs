using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// PRAGMA incremental_vacuum(N) against the pinned shell: the number of rows it returns, the page and freelist counts afterwards, the dump, and
/// integrity_check in both directions.
/// </summary>
public sealed class IncrementalVacuumTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-incvac-" + Guid.NewGuid().ToString("N"));

    public IncrementalVacuumTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string P(string name) => Path.Combine(_dir, name);

    private static void Need() => Assert.SkipUnless(File.Exists(OraclePaths.Cli), "the pinned oracle is not installed");

    // Rows with an index, one overflowing blob table that is dropped again (many free pages in the middle and at the end), and deletes inside the table.
    private static string Fill(int av, int pageSize, string mode) =>
        $"PRAGMA page_size={pageSize}; PRAGMA auto_vacuum={av}; PRAGMA journal_mode={mode};" +
        "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT); CREATE INDEX tb ON t(b); CREATE TABLE big(x);" +
        "WITH RECURSIVE r(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM r WHERE i<1500) INSERT INTO t SELECT i, hex(randomblob(30 + i % 60)) FROM r;" +
        "INSERT INTO big VALUES(zeroblob(60000)); INSERT INTO big VALUES(zeroblob(30000));" +
        "CREATE TABLE w(k TEXT PRIMARY KEY, v) WITHOUT ROWID; INSERT INTO w SELECT 'k' || value, zeroblob(value % 900) FROM generate_series(1, 200);" +
        "DELETE FROM t WHERE a % 3 <> 0; DROP TABLE big; DELETE FROM w WHERE length(v) % 4 = 0;";

    private static long Num(string path, string pragma) =>
        long.Parse(SqliteCli.Run(path, $"pragma {pragma};").Stdout.Trim(), CultureInfo.InvariantCulture);

    private static int Rows(Connection c, string sql)
    {
        using Statement s = c.Prepare(sql);
        int n = 0;
        while (s.Step())
        {
            if (sql.Contains("incremental_vacuum", StringComparison.Ordinal))
            {
                Assert.Equal(0, s.ColumnCount);
            }

            n++;
        }

        return n;
    }

    public static IEnumerable<TheoryDataRow<int, string, string>> Shapes() =>
    [
        new(512, "delete", "(1)"), new(512, "delete", "(7)"), new(512, "wal", "(40)"), new(1024, "delete", ""), new(4096, "delete", "(3)"),
        new(4096, "wal", "(1000)"), new(4096, "truncate", "(0)"), new(8192, "delete", "(-5)"), new(2048, "delete", "(100000)"), new(65536, "delete", "(2)"),
    ];

    [Theory]
    [MemberData(nameof(Shapes))]
    public void FreesTheSamePagesAsTheShell(int pageSize, string mode, string arg)
    {
        Need();
        string theirs = P("t.db"), ours = P("o.db");
        SqliteCli.ApplyScript(theirs, Fill(2, pageSize, mode));
        File.Copy(theirs, ours);
        long before = Num(ours, "freelist_count");
        Assert.True(before > 0);
        string sql = "PRAGMA incremental_vacuum" + arg;
        Proc.Result rs = SqliteCli.Run(theirs, sql + ";");
        long theirRows = rs.Stdout.Length; // one empty line per page freed
        using (Database d = Database.Open(ours))
        using (Connection c = d.Connect())
        {
            int rows = Rows(c, sql);
            Assert.Equal(theirRows, rows);
        }

        Assert.Equal(Num(theirs, "page_count"), Num(ours, "page_count"));
        Assert.Equal(Num(theirs, "freelist_count"), Num(ours, "freelist_count"));
        Assert.True(PinnedSqlite.IntegrityOk(ours, out string detail), detail);
        Assert.Equal(SqliteCli.Dump(theirs), SqliteCli.Dump(ours));
    }

    [Fact]
    public void TheShellsFileGoesThroughUsAndBack()
    {
        Need();
        string f = P("rt.db");
        SqliteCli.ApplyScript(f, Fill(2, 1024, "delete"));
        long free0 = Num(f, "freelist_count");
        using (Database d = Database.Open(f))
        using (Connection c = d.Connect())
        {
            Assert.Equal(5, Rows(c, "PRAGMA incremental_vacuum(5)"));
        }

        Assert.Equal(free0 - 5, Num(f, "freelist_count"));
        SqliteCli.Run(f, "pragma incremental_vacuum(5);");
        Assert.Equal(free0 - 10, Num(f, "freelist_count"));
        using (Database d = Database.Open(f))
        using (Connection c = d.Connect())
        {
            Assert.Equal((int)(free0 - 10), Rows(c, "PRAGMA incremental_vacuum"));
            // the file is whole again: a write after the vacuum finds consistent free-page accounting
            using Statement s = c.Prepare("INSERT INTO t(b) SELECT b FROM t WHERE a < 200");
            while (s.Step())
            {
            }
        }

        Assert.Equal(0, Num(f, "freelist_count"));
        Assert.True(PinnedSqlite.IntegrityOk(f, out string detail), detail);
    }

    [Fact]
    public void OurFileVacuumedByTheShell()
    {
        Need();
        string f = P("our.db");
        using (Database d = Database.Open(f))
        using (Connection c = d.Connect())
        {
            foreach (string stmt in Fill(2, 4096, "delete").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                Rows(c, stmt);
            }
        }

        long free0 = Num(f, "freelist_count");
        Assert.True(free0 > 0);
        SqliteCli.Run(f, "pragma incremental_vacuum(4);");
        Assert.Equal(free0 - 4, Num(f, "freelist_count"));
        SqliteCli.Run(f, "pragma incremental_vacuum;");
        Assert.Equal(0, Num(f, "freelist_count"));
        Assert.True(PinnedSqlite.IntegrityOk(f, out string detail), detail);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void NoEffectUnlessIncremental(int av)
    {
        Need();
        string theirs = P("n.db"), ours = P("no.db");
        SqliteCli.ApplyScript(theirs, Fill(av, 4096, "delete"));
        File.Copy(theirs, ours);
        using (Database d = Database.Open(ours))
        using (Connection c = d.Connect())
        {
            Assert.Equal(0, Rows(c, "PRAGMA incremental_vacuum(3)"));
        }

        Assert.Equal(Num(theirs, "page_count"), Num(ours, "page_count"));
        Assert.Equal(Num(theirs, "freelist_count"), Num(ours, "freelist_count"));
    }

    [Fact]
    public void RollsBackWithTheTransaction()
    {
        Need();
        string f = P("rb.db");
        SqliteCli.ApplyScript(f, Fill(2, 1024, "delete"));
        long free0 = Num(f, "freelist_count"), pages0 = Num(f, "page_count");
        using (Database d = Database.Open(f))
        using (Connection c = d.Connect())
        {
            Rows(c, "BEGIN");
            Assert.Equal(6, Rows(c, "PRAGMA incremental_vacuum(6)"));
            Rows(c, "ROLLBACK");
        }

        Assert.Equal(free0, Num(f, "freelist_count"));
        Assert.Equal(pages0, Num(f, "page_count"));
        Assert.True(PinnedSqlite.IntegrityOk(f, out string detail), detail);
    }

    [Fact]
    public void ReadOnlyAndQueryOnlyAreRefused()
    {
        Need();
        string f = P("ro.db");
        SqliteCli.ApplyScript(f, Fill(2, 4096, "delete"));
        using Database d = Database.Open(f);
        using Connection c = d.Connect();
        Rows(c, "PRAGMA query_only=1");
        OneLiteException e = Assert.Throws<OneLiteException>(() => Rows(c, "PRAGMA incremental_vacuum(2)"));
        Assert.Equal("attempt to write a readonly database", e.Message);
    }
}
