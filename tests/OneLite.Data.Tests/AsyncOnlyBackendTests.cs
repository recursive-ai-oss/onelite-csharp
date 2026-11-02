using System.Data.Common;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;
using Xunit;

namespace RecursiveAI.OneLite.Data.Tests;

/// <summary>
/// A backend with only asynchronous calls (<see cref="StrictAsyncVfs"/>) from a cold cache, end to end: the engine, ADO.NET and the happy path open, prepare,
/// query, write, cancel and close without a single synchronous backend call.
/// </summary>
public sealed class AsyncOnlyBackendTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DatabaseOptions Opts(IVfs vfs) => new() { Vfs = vfs, JournalMode = JournalMode.Delete, ForeignKeys = true };

    private static async Task<MemoryVfs> Populated(int rows = 500)
    {
        var mem = new MemoryVfs();
        await using Database db = await Database.OpenAsync("cold.db", Opts(mem), Ct);
        await db.ExecuteScriptAsync("create table t(a integer primary key, b text); create index tb on t(b)", Ct);
        await db.WriteAsync(async tx =>
        {
            for (int i = 1; i <= rows; i++)
            {
                await tx.ExecuteAsync($"insert into t values ({i}, {"row " + i})", Ct);
            }
        }, Ct);
        return mem;
    }

    [Fact]
    public async Task EnginePreparesAndStepsColdWithoutSyncCalls()
    {
        var strict = new StrictAsyncVfs(await Populated());
        Database db = Database.Open("cold.db", Opts(strict));
        await using Connection c = await db.ConnectAsync(Ct);
        await using (Statement st = await c.PrepareAsync("select count(*), max(b) from t", Ct))
        {
            Assert.True(await st.StepAsync(Ct));
            Assert.Equal(500, st.GetFieldValue<long>(0));
            Assert.Equal("row 99", st.GetFieldValue<string>(1));
            Assert.False(await st.StepAsync(Ct));
        }

        await using (Statement w = await c.PrepareAsync("update t set b = b || '!' where a <= 10", Ct))
        {
            Assert.False(await w.StepAsync(Ct));
        }

        await using (Statement st = await c.PrepareAsync("select b from t where a = 3", Ct))
        {
            Assert.True(await st.StepAsync(Ct));
            Assert.Equal("row 3!", st.GetFieldValue<string>(0));
            await st.ResetAsync();
        }

        Assert.Equal(0, strict.SyncCalls);
        Assert.True(strict.AsyncCalls > 10);
    }

    [Fact]
    public async Task SyncPrepareOnAColdAsyncOnlyBackendStillFailsAsDocumented()
    {
        var strict = new StrictAsyncVfs(await Populated(5));
        Database db = Database.Open("cold.db", Opts(strict));
        await using Connection c = await db.ConnectAsync(Ct);
        var e = Assert.Throws<OneLiteException>(() => c.Prepare("select * from t"));
        Assert.True(strict.SyncCalls > 0);
        Assert.NotNull(e);
    }

    [Fact]
    public async Task OpenReadOnlyAsyncChecksExistenceWithoutSyncCalls()
    {
        var strict = new StrictAsyncVfs(await Populated(5));
        using Database db = await Database.OpenReadOnlyAsync("cold.db", Opts(strict), Ct);
        await using Connection c = await db.ConnectAsync(Ct);
        await using Statement st = await c.PrepareAsync("select count(*) from t", Ct);
        Assert.True(await st.StepAsync(Ct));
        Assert.Equal(5, st.GetFieldValue<long>(0));
        OneLiteException e = await Assert.ThrowsAsync<OneLiteException>(async () => await Database.OpenReadOnlyAsync("missing.db", Opts(strict), Ct));
        Assert.Equal(ResultCode.CantOpen, e.Code);
        Assert.Equal(0, strict.SyncCalls);
    }

    [Fact]
    public async Task HappyPathReadsAndWritesColdWithoutSyncCalls()
    {
        var strict = new StrictAsyncVfs(await Populated());
        await using (Database db = await Database.OpenAsync("cold.db", Opts(strict), Ct))
        {
            Assert.Equal(500, await db.ScalarAsync<long>($"select count(*) from t", Ct));
            List<string> rows = await db.QueryAsync<string>($"select b from t where a % 100 = 0 order by a", Ct);
            Assert.Equal(5, rows.Count);
            await db.WriteAsync(async tx =>
            {
                await tx.ExecuteAsync($"insert into t values (1000, {"new"})", Ct);
                await tx.ExecuteAsync($"delete from t where a < 10", Ct);
            }, Ct);
            await db.MigrateAsync(Ct, "create table m(x)", "insert into m values (1)");
            Assert.Equal(492, await db.ScalarAsync<long>($"select count(*) from t", Ct));
        }

        Assert.Equal(0, strict.SyncCalls);

        // The commit reached the storage and the file is healthy.
        await using Database again = await Database.OpenAsync("cold.db", Opts(new StrictAsyncVfs(strict.Inner)), Ct);
        OneLiteException ic = await Assert.ThrowsAsync<OneLiteException>(async () => await again.ScalarAsync<string>($"pragma integrity_check", Ct));
        Assert.Equal(ResultCode.Misuse, ic.Code);
        Assert.Equal(492, await again.ScalarAsync<long>($"select count(*) from t", Ct));
    }

    [Fact]
    public async Task AdoNetOpensQueriesWritesAndClosesWithoutSyncCalls()
    {
        var strict = new StrictAsyncVfs(await Populated());
        OneLiteConnection.RegisterVfs("strict-ado", strict);
        await using (var conn = new OneLiteConnection("Data Source=cold.db;Vfs=strict-ado"))
        {
            await conn.OpenAsync(Ct);
            await using DbCommand count = conn.CreateCommand();
            count.CommandText = "select count(*) from t";
            Assert.Equal(500L, await count.ExecuteScalarAsync(Ct));

            await using (DbTransaction tx = await conn.BeginTransactionAsync(Ct))
            {
                await using DbCommand ins = conn.CreateCommand();
                ins.CommandText = "insert into t values (2000, 'x'); insert into t values (2001, 'y')";
                Assert.Equal(2, await ins.ExecuteNonQueryAsync(Ct));
                await tx.CommitAsync(Ct);
            }

            await using DbCommand sel = conn.CreateCommand();
            sel.CommandText = "select a, b from t order by a desc";
            await using (DbDataReader r = await sel.ExecuteReaderAsync(Ct))
            {
                Assert.True(await r.ReadAsync(Ct));
                Assert.Equal(2001L, r.GetInt64(0));
                // Closing with rows unread must still release the read lock through the backend's asynchronous unlock.
            }

            await conn.CloseAsync();
        }

        Assert.Equal(0, strict.SyncCalls);

        // A reader that was closed early left no lock behind: a second connection can commit at once.
        var other = new StrictAsyncVfs(strict.Inner);
        await using Database db = await Database.OpenAsync("cold.db", Opts(other), Ct);
        await db.ExecuteAsync($"insert into t values (3000, 'z')", Ct);
        Assert.Equal(0, other.SyncCalls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task CancellingAColdPrepareReleasesTheReadLock(int cancelAtCall)
    {
        var mem = await Populated(50);
        var strict = new StrictAsyncVfs(mem);
        var cts = new CancellationTokenSource();
        strict.CancelSource = cts;
        Database db = Database.Open("cold.db", Opts(strict));
        await using Connection c = await db.ConnectAsync(Ct);
        strict.CancelAtCall = strict.AsyncCalls + cancelAtCall;
        try
        {
            await using Statement st = await c.PrepareAsync("select count(*) from t", cts.Token);
            Assert.True(await st.StepAsync(cts.Token));
        }
        catch (OperationCanceledException)
        {
        }

        // Another connection can take the write lock (a leaked SHARED lock would make this BUSY) and the cancelled connection still works.
        var other = new StrictAsyncVfs(mem);
        await using (Database wdb = await Database.OpenAsync("cold.db", Opts(other), Ct))
        {
            Assert.Equal(1, await wdb.ExecuteAsync($"insert into t values (9000, 'w')", Ct));
        }

        strict.CancelAtCall = 0;
        await using Statement again = await c.PrepareAsync("select count(*) from t", Ct);
        Assert.True(await again.StepAsync(Ct));
        Assert.Equal(51, again.GetFieldValue<long>(0));
        Assert.Equal(0, strict.SyncCalls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task CancellingAColdStepReleasesTheReadLock(int cancelAtCall)
    {
        MemoryVfs mem = await Populated(50);
        var strict = new StrictAsyncVfs(mem);
        var cts = new CancellationTokenSource();
        strict.CancelSource = cts;
        Database db = Database.Open("cold.db", Opts(strict));
        await using Connection c = await db.ConnectAsync(Ct);
        await using Statement st = await c.PrepareAsync("select count(*) from t", Ct);
        strict.CancelAtCall = strict.AsyncCalls + cancelAtCall;
        bool cancelled = false;
        try
        {
            Assert.True(await st.StepAsync(cts.Token));
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        if (!cancelled)
        {
            // The call count was past the end of the step: it finished with its read lock held, as a running statement does. End it before the other writer.
            await st.ResetAsync();
        }

        // A cancelled step must not have left a lock behind: another connection commits at once.
        var other = new StrictAsyncVfs(mem);
        await using (Database wdb = await Database.OpenAsync("cold.db", Opts(other), Ct))
        {
            Assert.Equal(1, await wdb.ExecuteAsync($"insert into t values (9000, 'w')", Ct));
            Assert.Equal(51, await wdb.ScalarAsync<long>($"select count(*) from t", Ct));
        }

        strict.CancelAtCall = 0;
        await st.ResetAsync();
        Assert.True(await st.StepAsync(Ct));
        Assert.Equal(51, st.GetFieldValue<long>(0));
        Assert.Equal(0, strict.SyncCalls);
    }
}
