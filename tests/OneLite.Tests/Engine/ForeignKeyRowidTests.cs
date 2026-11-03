using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// last_insert_rowid() after an INSERT that fails its immediate foreign key. A one-row INSERT ... VALUES is checked before the row is written, so the
/// old value stays; other forms write the row first. Expected values recorded from the pinned sqlite3 3.53.4.
/// </summary>
public sealed class ForeignKeyRowidTests
{
    private static long Last(Connection c)
    {
        using Statement s = c.Prepare("select last_insert_rowid()");
        s.Step();
        return s.GetInt64(0);
    }

    private static void Fails(Connection c, string sql)
    {
        using Statement s = c.Prepare(sql);
        OneLiteException e = Assert.Throws<OneLiteException>(() => { while (s.Step()) { } });
        Assert.Equal("FOREIGN KEY constraint failed", e.Message);
    }

    private static void Ok(Connection c, string sql)
    {
        using Statement s = c.Prepare(sql);
        while (s.Step())
        {
        }
    }

    [Fact]
    public void OnlyAOneRowValuesInsertLeavesTheOldRowid()
    {
        using Database db = Database.Open(":memory:");
        using Connection c = db.Connect();
        Ok(c, "pragma foreign_keys=on");
        Ok(c, "create table p(id integer primary key)");
        Ok(c, "create table c(id integer primary key, p references p)");
        Ok(c, "create table e(id integer primary key, q references e)");
        Ok(c, "insert into c values(5, null)");
        Fails(c, "insert into c values(9, 77)");
        Assert.Equal(5, Last(c));
        Fails(c, "insert into c values(12, null), (13, 88)");
        Assert.Equal(13, Last(c));
        Fails(c, "insert into c select 20, 77");
        Assert.Equal(20, Last(c));
        Fails(c, "insert into c values(21, 77) returning id");
        Assert.Equal(21, Last(c));
        Fails(c, "insert into c values(22, 77) on conflict do nothing");
        Assert.Equal(21, Last(c));
        Fails(c, "insert into e values(40, 41)");
        Assert.Equal(21, Last(c));
    }
}
