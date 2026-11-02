using System.Data.Common;
using RecursiveAI.OneLite.Storage.Vfs;
using Xunit;

namespace RecursiveAI.OneLite.Data.Tests;

/// <summary>Real async: over a backend whose reads complete asynchronously, a cold query returns an incomplete task, and cancelling it leaves the connection usable.</summary>
public sealed class AsyncTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<MemoryVfs> Populated()
    {
        var mem = new MemoryVfs();
        await using Database db = await Database.OpenAsync("slow.db", new DatabaseOptions { Vfs = mem }, Ct);
        await db.ExecuteScriptAsync("create table t(a integer primary key, b text)", Ct);
        await db.WriteAsync(async tx =>
        {
            for (int i = 1; i <= 2000; i++)
            {
                await tx.ExecuteAsync($"insert into t values ({i}, {"row " + i})", Ct);
            }
        }, Ct);
        return mem;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 2000 && !condition(); i++)
        {
            await Task.Delay(5, Ct);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task ColdHappyPathQueryAwaitsTheBackend()
    {
        var slow = new SlowVfs(await Populated());
        await using Database db = await Database.OpenAsync("slow.db", new DatabaseOptions { Vfs = slow }, Ct);
        slow.ResetCounters();
        Task<List<string>> q = db.QueryAsync<string>($"select b from t where a % 100 = 0 order by a", Ct).AsTask();
        List<string> rows = await q;
        Assert.Equal(20, rows.Count);
        Assert.Equal("row 100", rows[0]);
        Assert.True(slow.AsyncReads > 0, "the cold cache must have gone to the backend");
        Assert.True(slow.IncompleteReads > 0, "ReadAsync must return an incomplete task on a cache miss");
        // Warm now: only the check of the file's change counter (page 1) still goes to the backend.
        int cold = slow.AsyncReads;
        slow.ResetCounters();
        Assert.Equal(20, (await db.QueryAsync<string>($"select b from t where a % 100 = 0 order by a", Ct)).Count);
        Assert.True(slow.AsyncReads <= 1 && slow.AsyncReads < cold);
    }

    /// <summary>Full-text 3/4 tables read their shadow tables through nested statements on the sync driver, which an async-only backend cannot serve from a cold cache: the query fails with SQLITE_MISUSE instead of hanging or returning wrong rows.</summary>
    [Fact]
    public async Task Fts4OnAnAsyncOnlyBackendFailsCleanlyWhenCold()
    {
        var mem = new MemoryVfs();
        await using (Database w = await Database.OpenAsync("f.db", new DatabaseOptions { Vfs = mem }, Ct))
        {
            await w.ExecuteScriptAsync("create virtual table d using fts4(a); create table plain(x)", Ct);
            await w.WriteAsync(async tx =>
            {
                for (int i = 1; i <= 300; i++)
                {
                    await tx.ExecuteAsync($"insert into d values ({"alpha beta gamma " + i})", Ct);
                }
            }, Ct);
        }

        var slow = new SlowVfs(mem, asyncOnly: true);
        await using Database db = await Database.OpenAsync("f.db", new DatabaseOptions { Vfs = slow }, Ct);
        OneLiteException? failure = null;
        try
        {
            Assert.Equal(300L, (await db.QueryAsync<long>($"select count(*) from d where d match {"alpha"}", Ct))[0]);
        }
        catch (OneLiteException e)
        {
            failure = e;
        }

        if (failure is not null)
        {
            Assert.Equal(21, failure.ExtendedCode & 0xff); // SQLITE_MISUSE
        }
    }

    [Fact]
    public async Task CancellingMidQueryThrowsAndLeavesTheConnectionUsable()
    {
        var slow = new SlowVfs(await Populated());
        await using Database db = await Database.OpenAsync("slow.db", new DatabaseOptions { Vfs = slow }, Ct);
        slow.ResetCounters();
        slow.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Task<List<long>> q = db.QueryAsync<long>($"select a from t order by a", cts.Token).AsTask();
        await WaitUntil(() => slow.EnteredReads > 0);
        Assert.False(q.IsCompleted);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => q);

        slow.Hold = null;
        List<long> again = await db.QueryAsync<long>($"select a from t order by a", Ct);
        Assert.Equal(2000, again.Count);
    }

    [Fact]
    public async Task AdoReaderReadsAsyncallyAndCancels()
    {
        var slow = new SlowVfs(await Populated());
        OneLiteConnection.RegisterVfs("slow-ado", slow);
        await using var conn = new OneLiteConnection("Data Source=slow.db;Vfs=slow-ado");
        await conn.OpenAsync(Ct);
        slow.ResetCounters();
        await using (DbCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "select a, b from t where a <= 50 order by a";
            await using DbDataReader r = await cmd.ExecuteReaderAsync(Ct);
            int n = 0;
            while (await r.ReadAsync(Ct))
            {
                n++;
            }

            Assert.Equal(50, n);
        }

        Assert.True(slow.IncompleteReads > 0);

        // Cancel a cold scan while it waits for the backend.
        await using var conn2 = new OneLiteConnection("Data Source=slow.db;Vfs=slow-ado");
        await conn2.OpenAsync(Ct);
        slow.ResetCounters();
        slow.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using DbCommand cmd2 = conn2.CreateCommand();
        cmd2.CommandText = "select count(*) from t";
        Task<object?> scalar = cmd2.ExecuteScalarAsync(cts.Token);
        await WaitUntil(() => slow.EnteredReads > 0);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scalar);
        slow.Hold = null;
        Assert.Equal(2000L, await cmd2.ExecuteScalarAsync(Ct));
    }
}
