using System;
using System.IO;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// <c>PRAGMA max_page_count</c> stops file growth with SQLITE_FULL (pragma page). The expected outcomes were observed on the pinned reference shell:
/// a failed single-row INSERT undoes the whole explicit transaction, a failed multi-row INSERT only its own statement.
/// </summary>
public sealed class MaxPageCountTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-maxpages-" + Guid.NewGuid().ToString("N"));

    public MaxPageCountTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static string One(Connection c, string sql)
    {
        using Statement st = c.Prepare(sql);
        return st.Step() ? st.GetString(0) ?? "" : "";
    }

    private static void Run(Connection c, string sql)
    {
        using Statement st = c.Prepare(sql);
        while (st.Step())
        {
        }
    }

    private Connection Open(string mode)
    {
        Database d = Database.Open(Path.Combine(_dir, mode + ".db"));
        Connection c = d.Connect();
        Run(c, $"PRAGMA journal_mode={mode}");
        Run(c, "CREATE TABLE t(a INTEGER PRIMARY KEY, b)");
        Run(c, "CREATE TABLE u(x)");
        Run(c, "INSERT INTO t VALUES(1,'x')");
        Run(c, "PRAGMA max_page_count=3");
        return c;
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("wal")]
    public void FullInASingleRowInsertUndoesTheWholeTransaction(string mode)
    {
        using Connection c = Open(mode);
        Run(c, "BEGIN");
        Run(c, "INSERT INTO t VALUES(2,'y')");
        OneLiteException e = Assert.Throws<OneLiteException>(() => Run(c, "INSERT INTO t VALUES(3,zeroblob(20000))"));
        Assert.Equal(ResultCode.Full, e.ExtendedCode & 0xFF);
        Assert.True(c.AutoCommit);
        Assert.Equal("1", One(c, "SELECT count(*) FROM t"));
        Assert.Throws<OneLiteException>(() => Run(c, "COMMIT"));
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("wal")]
    public void FullInAMultiRowInsertUndoesOnlyTheStatement(string mode)
    {
        using Connection c = Open(mode);
        Run(c, "BEGIN");
        Run(c, "INSERT INTO t VALUES(2,'y')");
        OneLiteException e = Assert.Throws<OneLiteException>(() => Run(c, "INSERT INTO t SELECT 100+value, zeroblob(2000) FROM generate_series(1,20)"));
        Assert.Equal(ResultCode.Full, e.ExtendedCode & 0xFF);
        Assert.False(c.AutoCommit);
        Assert.Equal("2", One(c, "SELECT count(*) FROM t"));
        Run(c, "COMMIT");
        Assert.Equal("2", One(c, "SELECT count(*) FROM t"));
    }

    [Fact]
    public void TheLimitCannotGoBelowTheCurrentSizeAndRoundTrips()
    {
        using Connection c = Open("delete");
        Assert.Equal("3", One(c, "PRAGMA max_page_count=2"));
        Assert.Equal("100", One(c, "PRAGMA max_page_count=100"));
    }
}
