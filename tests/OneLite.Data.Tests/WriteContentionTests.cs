using System.Data;
using System.Data.Common;
using RecursiveAI.OneLite.Storage.Pager;
using Xunit;

namespace RecursiveAI.OneLite.Data.Tests;

/// <summary>
/// A write that cannot get the EXCLUSIVE lock at commit because another connection is reading must fail with SQLITE_BUSY, on every surface
/// (lockingv3, c3ref/busy_handler). It used to return success with zero changes and store nothing.
/// </summary>
public sealed class WriteContentionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-contention-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private static readonly DatabaseOptions s_rollback = new() { JournalMode = JournalMode.Delete };

    public WriteContentionTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "t.db");
        using var db = Database.Open(_path);
        using Connection c = db.Connect();
        foreach (string sql in new[] { "CREATE TABLE t(x INTEGER)", "INSERT INTO t VALUES (1),(2),(3)" })
        {
            using Statement s = c.Prepare(sql);
            s.Step();
        }
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private static void AssertBusy(OneLiteDataException e)
    {
        Assert.Equal(5, e.SqliteErrorCode);
        Assert.Contains("database is locked", e.Message, StringComparison.Ordinal);
    }

    private static long Count(OneLiteConnection c)
    {
        using var cmd = new OneLiteCommand("SELECT count(*) FROM t", c);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public void AutocommitExecuteNonQueryIsBusyWhileAReaderIsOpen()
    {
        using var reader = new OneLiteConnection("Data Source=" + _path + ";Default Timeout=0");
        using var writer = new OneLiteConnection("Data Source=" + _path + ";Default Timeout=0");
        reader.Open();
        writer.Open();
        using var sel = new OneLiteCommand("SELECT x FROM t", reader);
        using DbDataReader rd = sel.ExecuteReader();
        Assert.True(rd.Read());

        foreach (string sql in new[] { "INSERT INTO t VALUES (9)", "UPDATE t SET x = x + 10 WHERE x = 1", "DELETE FROM t WHERE x = 2" })
        {
            using var cmd = new OneLiteCommand(sql, writer);
            AssertBusy(Assert.Throws<OneLiteDataException>(() => cmd.ExecuteNonQuery()));
        }

        rd.Close();
        Assert.Equal(3, Count(writer));
        using var ok = new OneLiteCommand("INSERT INTO t VALUES (9)", writer);
        Assert.Equal(1, ok.ExecuteNonQuery());
        Assert.Equal(4, Count(reader));
    }

    [Fact]
    public async Task AsyncExecuteNonQueryIsBusyWhileAReaderIsOpen()
    {
        await using var reader = new OneLiteConnection("Data Source=" + _path + ";Default Timeout=0");
        await using var writer = new OneLiteConnection("Data Source=" + _path + ";Default Timeout=0");
        await reader.OpenAsync(TestContext.Current.CancellationToken);
        await writer.OpenAsync(TestContext.Current.CancellationToken);
        await using var sel = new OneLiteCommand("SELECT x FROM t", reader);
        await using var rd = await sel.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await rd.ReadAsync(TestContext.Current.CancellationToken));

        await using var cmd = new OneLiteCommand("INSERT INTO t VALUES (9)", writer);
        AssertBusy(await Assert.ThrowsAsync<OneLiteDataException>(async () => await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)));
        await rd.CloseAsync();
        Assert.Equal(3, Count(writer));
    }

    [Fact]
    public void TheBusyTimeoutIsHonouredBeforeTheErrorIsReported()
    {
        using var reader = new OneLiteConnection("Data Source=" + _path);
        using var writer = new OneLiteConnection("Data Source=" + _path + ";Default Timeout=1");
        reader.Open();
        writer.Open();
        using var sel = new OneLiteCommand("SELECT x FROM t", reader);
        using DbDataReader rd = sel.ExecuteReader();
        Assert.True(rd.Read());
        using var cmd = new OneLiteCommand("INSERT INTO t VALUES (9)", writer);
        long start = Environment.TickCount64;
        AssertBusy(Assert.Throws<OneLiteDataException>(() => cmd.ExecuteNonQuery()));
        Assert.InRange(Environment.TickCount64 - start, 900, 5000);
    }

    [Fact]
    public void CommitIsBusyAndCanBeRetriedAfterTheReaderIsDone()
    {
        using var reader = new OneLiteConnection("Data Source=" + _path + ";Default Timeout=0");
        using var writer = new OneLiteConnection("Data Source=" + _path + ";Default Timeout=0");
        reader.Open();
        writer.Open();
        using var sel = new OneLiteCommand("SELECT x FROM t", reader);
        using DbDataReader rd = sel.ExecuteReader();
        Assert.True(rd.Read());

        using OneLiteTransaction tx = writer.BeginTransaction();
        using var ins = new OneLiteCommand("INSERT INTO t VALUES (9)", writer) { Transaction = tx };
        Assert.Equal(1, ins.ExecuteNonQuery());
        AssertBusy(Assert.Throws<OneLiteDataException>(() => tx.Commit()));

        rd.Close();
        tx.Commit();
        Assert.Equal(4, Count(reader));
    }

    [Fact]
    public async Task HappyPathWriteIsBusyAndNotStored()
    {
        using var a = Database.OpenDefault(_path, s_rollback);
        using var b = Database.OpenDefault(_path, s_rollback);
        using Connection holder = a.Connect();
        using Statement r = holder.Prepare("SELECT x FROM t");
        Assert.True(r.Step());

        var e = Assert.Throws<OneLiteException>(() => b.Execute($"INSERT INTO t VALUES ({9})"));
        Assert.Equal(ResultCode.Busy, e.Code);
        Assert.Equal("database is locked", e.Message);
        r.Reset();
        Assert.Equal(3, (await b.QueryAsync<long>($"SELECT count(*) FROM t", TestContext.Current.CancellationToken))[0]);
    }

    [Fact]
    public async Task WriteAsyncRetriesBusyThenReportsIt()
    {
        using var a = Database.OpenDefault(_path, s_rollback);
        using var b = Database.OpenDefault(_path, s_rollback);
        using Connection holder = a.Connect();
        using Statement r = holder.Prepare("SELECT x FROM t");
        Assert.True(r.Step());

        var e = await Assert.ThrowsAsync<OneLiteException>(() => b.WriteAsync(async tx => await tx.ExecuteAsync($"INSERT INTO t VALUES ({9})", TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
        Assert.Equal(ResultCode.Busy, e.Code);
        r.Reset();
        await b.WriteAsync(async tx => await tx.ExecuteAsync($"INSERT INTO t VALUES ({9})", TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(4, (await b.QueryAsync<long>($"SELECT count(*) FROM t", TestContext.Current.CancellationToken))[0]);
    }
}
