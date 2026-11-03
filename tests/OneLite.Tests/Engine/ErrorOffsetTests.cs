using System;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// The byte offset of the token a rejected statement points at (c3ref error_offset) for errors found while resolving names. The expected values were
/// observed on the pinned reference library; -1 is where it reports no position (join ON clauses, an INSERT's RETURNING, table errors).
/// </summary>
public sealed class ErrorOffsetTests : IDisposable
{
    private readonly Database _db = Database.Open(":memory:");
    private readonly Connection _c;

    public ErrorOffsetTests()
    {
        _c = _db.Connect();
        foreach (string s in new[] { "CREATE TABLE t(a,b)", "CREATE TABLE u(x)", "CREATE VIEW vv AS SELECT a FROM t" })
        {
            using Statement st = _c.Prepare(s);
            st.Step();
        }
    }

    public void Dispose()
    {
        _c.Dispose();
        _db.Dispose();
    }

    [Theory]
    [InlineData("SELECT zz FROM t", "no such column: zz", 7)]
    [InlineData("SELECT a, zz FROM t", "no such column: zz", 10)]
    [InlineData("SELECT t.zz FROM t", "no such column: t.zz", 7)]
    [InlineData("SELECT main.nosuch.a FROM t", "no such column: main.nosuch.a", 7)]
    [InlineData("SELECT a FROM t ORDER BY zz", "no such column: zz", 25)]
    [InlineData("SELECT 'é', zz FROM t", "no such column: zz", 13)]
    [InlineData("UPDATE t SET b=zz", "no such column: zz", 15)]
    [InlineData("UPDATE t SET b=1 RETURNING zz", "no such column: zz", 27)]
    [InlineData("INSERT INTO t VALUES(1,2) RETURNING zz", "no such column: zz", -1)]
    [InlineData("SELECT a FROM t JOIN u ON zz", "no such column: zz", -1)]
    [InlineData("SELECT a FROM t JOIN u ON x=1 WHERE zz", "no such column: zz", 36)]
    [InlineData("UPDATE t SET zz=1", "no such column: zz", -1)]
    [InlineData("SELECT sum(count(a)) FROM t", "misuse of aggregate function count()", 11)]
    [InlineData("SELECT a FROM t WHERE sum(a) > 1", "misuse of aggregate function sum()", 22)]
    [InlineData("UPDATE t SET a = sum(a)", "misuse of aggregate function sum()", 17)]
    [InlineData("SELECT row_number() FROM t", "misuse of window function row_number()", 7)]
    [InlineData("SELECT nofunc(a) FROM t", "no such function: nofunc", 7)]
    [InlineData("SELECT abs(a, a) FROM t", "wrong number of arguments to function abs()", 7)]
    [InlineData("SELECT a FROM t t1 JOIN t t1", "ambiguous column name: a", 7)]
    [InlineData("SELECT * FROM t, t", "ambiguous column name: main.t.a", 7)]
    [InlineData("SELECT a FROM t WHERE a = (1,2)", "row value misused", 22)]
    [InlineData("SELECT a FROM t WHERE (1,2) = a", "row value misused", -1)]
    [InlineData("CREATE TABLE t(a)", "table t already exists", 13)]
    [InlineData("CREATE VIEW vv AS SELECT 1", "view vv already exists", 12)]
    [InlineData("VACUUM nosuch", "unknown database nosuch", 7)]
    [InlineData("CREATE TABLE nosuchdb.q(a)", "unknown database nosuchdb", 13)]
    [InlineData("SELECT a FROM nosuch", "no such table: nosuch", -1)]
    public void ResolveErrorsCarryTheReferenceOffset(string sql, string? message, int offset)
    {
        OneLiteException e = Assert.Throws<OneLiteException>(() =>
        {
            using Statement st = _c.Prepare(sql);
            while (st.Step())
            {
            }
        });
        Assert.Equal(message, e.Message);
        Assert.Equal(offset, e.ErrorOffset);
    }
}
