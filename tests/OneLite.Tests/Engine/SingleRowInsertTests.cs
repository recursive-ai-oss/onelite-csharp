using System;
using System.IO;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// A single-row INSERT inside a transaction runs without a savepoint of its own; a statement that fails must still leave exactly the
/// rows of the statements before it, and a later ROLLBACK TO must still undo the rows after its savepoint.
/// </summary>
public sealed class SingleRowInsertTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-sri-" + Guid.NewGuid().ToString("N"));

    public SingleRowInsertTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static void Exec(Connection c, string sql)
    {
        using Statement st = c.Prepare(sql);
        while (st.Step())
        {
        }
    }

    private static long Count(Connection c, string sql)
    {
        using Statement st = c.Prepare(sql);
        Assert.True(st.Step());
        return st.GetInt64(0);
    }

    private static string Fail(Connection c, string sql)
    {
        OneLiteException e = Assert.Throws<OneLiteException>(() => Exec(c, sql));
        return e.Message;
    }

    [Fact]
    public void AFailedInsertInsideATransactionKeepsTheEarlierRows()
    {
        using var db = Database.Open(Path.Combine(_dir, "a.db"));
        using Connection c = db.Connect();
        Exec(c, "create table t(id integer primary key, a integer not null, b text unique)");
        Exec(c, "begin");
        Exec(c, "insert into t values(1, 1, 'x')");
        Assert.Equal("UNIQUE constraint failed: t.id", Fail(c, "insert into t values(1, 2, 'y')"));
        Assert.Equal("NOT NULL constraint failed: t.a", Fail(c, "insert into t values(2, null, 'z')"));
        Assert.Equal("UNIQUE constraint failed: t.b", Fail(c, "insert into t values(3, 3, 'x')"));
        Exec(c, "insert into t values(4, 4, 'w')");
        Assert.Equal(2, Count(c, "select count(*) from t"));
        Exec(c, "commit");
        Assert.Equal(2, Count(c, "select count(*) from t"));
        Assert.Equal(5, Count(c, "select sum(id) from t"));
    }

    [Fact]
    public void ASavepointStillUndoesInsertsMadeAfterIt()
    {
        using var db = Database.Open(Path.Combine(_dir, "b.db"));
        using Connection c = db.Connect();
        Exec(c, "create table t(id integer primary key, a integer)");
        Exec(c, "begin");
        for (int i = 1; i <= 300; i++)
        {
            Exec(c, "insert into t values(" + i + ", " + i + ")");
        }

        Exec(c, "savepoint s");
        for (int i = 301; i <= 600; i++)
        {
            Exec(c, "insert into t values(" + i + ", " + i + ")");
        }

        Assert.Equal(600, Count(c, "select count(*) from t"));
        Exec(c, "rollback to s");
        Assert.Equal(300, Count(c, "select count(*) from t"));
        Exec(c, "insert into t values(301, 0)");
        Exec(c, "release s");
        Exec(c, "commit");
        Assert.Equal(301, Count(c, "select count(*) from t"));
    }

    [Fact]
    public void ABoundTextRebindUsesTheNewBytes()
    {
        using var db = Database.Open(Path.Combine(_dir, "c.db"));
        using Connection c = db.Connect();
        Exec(c, "create table t(id integer primary key, b text)");
        Exec(c, "begin");
        using (Statement ins = c.Prepare("insert into t values(?1, ?2)"))
        {
            string[] texts = ["aaaa", "bbbb", "cc", "dddd", "é"];
            for (int i = 0; i < texts.Length; i++)
            {
                ins.Bind(1, i + 1);
                ins.Bind(2, texts[i]);
                while (ins.Step())
                {
                }

                ins.Reset();
            }
        }

        Exec(c, "commit");
        using Statement q = c.Prepare("select group_concat(b, '|') from (select b from t order by id)");
        Assert.True(q.Step());
        Assert.Equal("aaaa|bbbb|cc|dddd|é", q.GetString(0));
    }
}
