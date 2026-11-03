using System;
using System.IO;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// A statement that is bound, stepped and reset on a cached page allocates nothing, and a program kept for the next prepare of the same text is
/// used only while the schema it was compiled against is still the connection's.
/// </summary>
public sealed class StatementReuseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-reuse-" + Guid.NewGuid().ToString("N"));

    public StatementReuseTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static void Run(Connection c, string sql)
    {
        using Statement s = c.Prepare(sql);
        while (s.Step())
        {
        }
    }

    private static string One(Connection c, string sql)
    {
        using Statement s = c.Prepare(sql);
        return s.Step() ? s.GetString(0) ?? "NULL" : "none";
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ACachedPointLookupAllocatesNothingPerExecution(bool wal, bool viaIndex)
    {
        string path = Path.Combine(_dir, "a.db");
        using var db = Database.Open(path, new DatabaseOptions { CacheSize = 10000 });
        using Connection c = db.Connect();
        if (wal)
        {
            Run(c, "PRAGMA journal_mode=wal");
        }

        Run(c, "CREATE TABLE t(id INTEGER PRIMARY KEY, a INTEGER, b TEXT)");
        Run(c, "CREATE INDEX t_a ON t(a)");
        Run(c, "BEGIN");
        using (Statement ins = c.Prepare("INSERT INTO t VALUES(?1, ?2, ?3)"))
        {
            for (int i = 1; i <= 2000; i++)
            {
                ins.Bind(1, i);
                ins.Bind(2, i * 7L);
                ins.Bind(3, "row " + i);
                while (ins.Step())
                {
                }

                ins.Reset();
            }
        }

        Run(c, "COMMIT");

        using Statement st = c.Prepare(viaIndex ? "SELECT id FROM t WHERE a = ?1" : "SELECT b FROM t WHERE id = ?1");

        long Body()
        {
            long sum = 0;
            for (int i = 1; i <= 2000; i++)
            {
                st.Bind(1, viaIndex ? i * 7L : i);
                while (st.Step())
                {
                    sum += viaIndex ? st.GetInt64(0) : st.GetTextUtf8(0).Length;
                }

                st.Reset();
            }

            return sum;
        }

        Body();
        Body();
        long best = long.MaxValue;
        for (int rep = 0; rep < 5; rep++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.True(Body() != 0);
            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.True(best == 0, "2000 executions allocated " + best + " B");
    }

    [Fact]
    public void APreparedAgainTextSeesSchemaChangesMadeByThisConnection()
    {
        using var db = Database.Open(Path.Combine(_dir, "b.db"));
        using Connection c = db.Connect();
        Run(c, "CREATE TABLE t(a, b)");
        Run(c, "INSERT INTO t VALUES(1, 2)");
        Assert.Equal("1", One(c, "SELECT a FROM t"));
        Assert.Equal("1", One(c, "SELECT a FROM t"));

        // The same text now means something else: the column is gone.
        Run(c, "DROP TABLE t");
        Run(c, "CREATE TABLE t(x, y)");
        Run(c, "INSERT INTO t VALUES(5, 6)");
        OneLiteException e = Assert.Throws<OneLiteException>(() => One(c, "SELECT a FROM t"));
        Assert.Contains("no such column: a", e.Message);

        // A temp table shadows the main one for statements prepared after it exists.
        Assert.Equal("5", One(c, "SELECT x FROM t"));
        Run(c, "CREATE TEMP TABLE t(x, y)");
        Run(c, "INSERT INTO temp.t VALUES(50, 60)");
        Assert.Equal("50", One(c, "SELECT x FROM t"));
        Run(c, "DROP TABLE temp.t");
        Assert.Equal("5", One(c, "SELECT x FROM t"));

        // An added column is visible to the text that used to fail, and `*` widens.
        Run(c, "ALTER TABLE t ADD COLUMN z DEFAULT 7");
        Assert.Equal("7", One(c, "SELECT z FROM t"));
        using (Statement s = c.Prepare("SELECT * FROM t"))
        {
            Assert.Equal(3, s.ColumnCount);
        }

        using (Statement s = c.Prepare("SELECT * FROM t"))
        {
            Assert.Equal(3, s.ColumnCount);
        }

        Run(c, "CREATE INDEX t_x ON t(x)");
        Assert.Equal("5", One(c, "SELECT x FROM t WHERE x = 5"));
        Run(c, "DROP INDEX t_x");
        Assert.Equal("5", One(c, "SELECT x FROM t WHERE x = 5"));
    }

    [Fact]
    public void APreparedAgainTextSeesSchemaChangesMadeByAnotherConnection()
    {
        string path = Path.Combine(_dir, "c.db");
        using var db = Database.Open(path);
        using Connection c1 = db.Connect();
        using Connection c2 = db.Connect();
        Run(c1, "CREATE TABLE t(a)");
        Run(c1, "INSERT INTO t VALUES(1)");
        Assert.Equal("1", One(c1, "SELECT * FROM t"));
        Run(c2, "ALTER TABLE t ADD COLUMN b DEFAULT 9");
        using Statement s = c1.Prepare("SELECT * FROM t");
        Assert.True(s.Step());
        Assert.Equal(2, s.ColumnCount);
        Assert.Equal("9", s.GetString(1));
    }

    [Fact]
    public void TwoLiveStatementsOfTheSameTextRunIndependently()
    {
        using var db = Database.Open(Path.Combine(_dir, "d.db"));
        using Connection c = db.Connect();
        Run(c, "CREATE TABLE t(a)");
        Run(c, "INSERT INTO t VALUES(1),(2),(3)");
        using (Statement warm = c.Prepare("SELECT a FROM t ORDER BY a"))
        {
            while (warm.Step())
            {
            }
        }

        using Statement s1 = c.Prepare("SELECT a FROM t ORDER BY a");
        using Statement s2 = c.Prepare("SELECT a FROM t ORDER BY a");
        Assert.True(s1.Step());
        Assert.True(s1.Step());
        Assert.True(s2.Step());
        Assert.Equal(2, s1.GetInt64(0));
        Assert.Equal(1, s2.GetInt64(0));
    }

    [Fact]
    public void TheForeignKeySettingAtPrepareTimeDecidesWhatADeleteDoes()
    {
        using var db = Database.Open(Path.Combine(_dir, "e.db"));
        using Connection c = db.Connect();
        Run(c, "CREATE TABLE p(id INTEGER PRIMARY KEY)");
        Run(c, "CREATE TABLE ch(pid REFERENCES p(id) ON DELETE CASCADE)");
        Run(c, "INSERT INTO p VALUES(1)");
        Run(c, "INSERT INTO ch VALUES(1)");
        Run(c, "PRAGMA foreign_keys=ON");
        Run(c, "DELETE FROM p WHERE id = 2");
        Run(c, "PRAGMA foreign_keys=OFF");
        Run(c, "DELETE FROM p WHERE id = 1");
        Assert.Equal("1", One(c, "SELECT count(*) FROM ch"));
        Run(c, "INSERT INTO p VALUES(1)");
        Run(c, "PRAGMA foreign_keys=ON");
        Run(c, "DELETE FROM p WHERE id = 1");
        Assert.Equal("0", One(c, "SELECT count(*) FROM ch"));
    }

    [Fact]
    public void AttachingADatabaseChangesWhatANameMeans()
    {
        string other = Path.Combine(_dir, "g2.db");
        using (var db2 = Database.Open(other))
        using (Connection c2 = db2.Connect())
        {
            Run(c2, "CREATE TABLE only_there(v)");
            Run(c2, "INSERT INTO only_there VALUES('x')");
        }

        using var db = Database.Open(Path.Combine(_dir, "g.db"));
        using Connection c = db.Connect();
        Run(c, "CREATE TABLE m(v)");
        Assert.Throws<OneLiteException>(() => One(c, "SELECT v FROM only_there"));
        Run(c, "ATTACH '" + other.Replace("'", "''", StringComparison.Ordinal) + "' AS o");
        Assert.Equal("x", One(c, "SELECT v FROM only_there"));
        Run(c, "DETACH o");
        Assert.Throws<OneLiteException>(() => One(c, "SELECT v FROM only_there"));
    }

    [Fact]
    public void ABoundValueDoesNotSurviveIntoThePreparedAgainStatement()
    {
        using var db = Database.Open(Path.Combine(_dir, "h.db"));
        using Connection c = db.Connect();
        Run(c, "CREATE TABLE t(a)");
        for (int round = 0; round < 3; round++)
        {
            using (Statement s = c.Prepare("SELECT ?1, ?2"))
            {
                Assert.True(s.Step());
                Assert.True(s.IsNull(0));
                Assert.True(s.IsNull(1));
                s.Reset();
                s.Bind(1, "text");
                s.Bind(2, 42);
                Assert.True(s.Step());
                Assert.Equal("text", s.GetString(0));
                Assert.Equal(42, s.GetInt64(1));
            }
        }
    }

    [Fact]
    public void AStatementPreparedForEachRowWritesAndRollsBackLikeAnyOther()
    {
        using var db = Database.Open(Path.Combine(_dir, "i.db"));
        using Connection c = db.Connect();
        Run(c, "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT)");
        Run(c, "CREATE INDEX t_v ON t(v)");
        Run(c, "BEGIN");
        for (int i = 1; i <= 200; i++)
        {
            using Statement s = c.Prepare("INSERT INTO t(v) VALUES(?1)");
            s.Bind(1, "v" + (i % 10));
            Assert.False(s.Step());
        }

        Assert.Equal("200", One(c, "SELECT count(*) FROM t"));
        Assert.Equal("20", One(c, "SELECT count(*) FROM t WHERE v = 'v3'"));
        Run(c, "ROLLBACK");
        Assert.Equal("0", One(c, "SELECT count(*) FROM t"));
        for (int i = 0; i < 50; i++)
        {
            using Statement s = c.Prepare("INSERT INTO t(v) VALUES(?1)");
            s.Bind(1, "w");
            Assert.False(s.Step());
        }

        Assert.Equal("50", One(c, "SELECT count(*) FROM t WHERE v = 'w'"));
        Assert.Equal("1", One(c, "SELECT min(id) FROM t"));
    }

    [Fact]
    public void AStatementKeptOpenWhileAnotherOfTheSameTextIsFinalizedIsUnaffected()
    {
        using var db = Database.Open(Path.Combine(_dir, "j.db"));
        using Connection c = db.Connect();
        Run(c, "CREATE TABLE t(a)");
        Run(c, "INSERT INTO t VALUES(1),(2),(3)");
        using Statement live = c.Prepare("SELECT a FROM t ORDER BY a");
        Assert.True(live.Step());
        for (int i = 0; i < 3; i++)
        {
            using Statement other = c.Prepare("SELECT a FROM t ORDER BY a");
            Assert.True(other.Step());
            Assert.Equal(1, other.GetInt64(0));
        }

        Assert.True(live.Step());
        Assert.Equal(2, live.GetInt64(0));
    }
}
