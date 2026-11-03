using System;
using System.IO;
using System.Linq;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;
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
    /// <summary>
    /// A storage error in the middle of a savepoint-free single-row insert, inside an explicit transaction, rolls the whole transaction back (the shell
    /// does the same when a write fails with an I/O error: the rows of the earlier statements are gone, the connection is back in autocommit mode,
    /// and the file passes integrity_check).
    /// </summary>
    [Theory]
    [InlineData(JournalMode.Delete, 1)]
    [InlineData(JournalMode.Delete, 3)]
    [InlineData(JournalMode.Delete, 9)]
    [InlineData(JournalMode.Wal, 1)]
    [InlineData(JournalMode.Wal, 2)]
    public void AWriteErrorInsideATransactionRollsBackTheWholeTransaction(JournalMode jm, int nthWrite)
    {
        var fault = new FaultVfs(new MemoryVfs());
        var opts = new DatabaseOptions { Vfs = fault, PageSize = 512, JournalMode = jm, CacheSize = 4 };
        using (Database db = Database.Open("f.db", opts))
        using (Connection c = db.Connect())
        {
            Exec(c, "create table t(id integer primary key, a text)");
            Exec(c, "insert into t select value * 100, hex(randomblob(60)) from generate_series(1, 4000)");
        }

        // A fresh connection starts with a cold cache: the inserts below miss, and a miss over the cache limit pushes dirty pages to the journal or WAL.
        using (Database db = Database.Open("f.db", opts))
        using (Connection c = db.Connect())
        {
            Exec(c, "pragma cache_size = 4");
            fault.AddRule(new FaultRule { Call = FaultCall.Write, Nth = 1 + (int)CountWrites(fault) + nthWrite - 1, ExtendedCode = VfsCodes.IoErrWrite });
            Exec(c, "begin");
            Exec(c, "insert into t values(-1, 'first')");
            bool failed = false;
            for (int i = 0; i < 3000 && !failed; i++)
            {
                try
                {
                    Exec(c, "insert into t values(" + (1 + (i * 7919 % 200003) * 2) + ", hex(randomblob(60)))");
                }
                catch (OneLiteException e)
                {
                    failed = true;
                    Assert.Equal(10, e.ExtendedCode & 0xff);
                }
            }

            if (!failed)
            {
                // The WAL commit writes its frames in one go and no page was pushed out before it, so the failure shows up at COMMIT.
                Assert.Equal(JournalMode.Wal, jm);
                Assert.Equal(10, Assert.Throws<OneLiteException>(() => Exec(c, "commit")).ExtendedCode & 0xff);
            }
            Assert.True(c.AutoCommit, "the failed transaction should be over");
            Assert.Equal(4000, Count(c, "select count(*) from t"));
            Assert.Equal("ok", Text(c, "pragma integrity_check"));
            Exec(c, "insert into t values(-2, 'later')");
            Assert.Equal(4001, Count(c, "select count(*) from t"));
        }

        using (Database db = Database.Open("f.db", opts with { Vfs = fault.Inner }))
        using (Connection c = db.Connect())
        {
            Assert.Equal(4001, Count(c, "select count(*) from t"));
            Assert.Equal(0, Count(c, "select count(*) from t where id = -1"));
            Assert.Equal("ok", Text(c, "pragma integrity_check"));
        }
    }

    private static long CountWrites(FaultVfs v)
    {
        long n = 0;
        foreach (FaultOp o in v.Ops)
        {
            if (o.Kind == FaultOpKind.Write)
            {
                n++;
            }
        }

        return n;
    }

    private static string Text(Connection c, string sql)
    {
        using Statement st = c.Prepare(sql);
        Assert.True(st.Step());
        return st.GetString(0)!;
    }
}
