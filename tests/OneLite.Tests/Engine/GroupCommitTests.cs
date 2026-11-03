using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>Group commit: concurrent commits of one process share WAL syncs at synchronous=FULL and lose nothing (docs/wal.md).</summary>
public sealed class GroupCommitTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-group-" + Guid.NewGuid().ToString("N"));

    public GroupCommitTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static void Run(Connection c, string sql)
    {
        try
        {
            using Statement st = c.Prepare(sql);
            while (st.Step())
            {
            }
        }
        catch (OneLiteException e) when ((e.ExtendedCode & 0xFF) != ResultCode.Busy)
        {
            throw new InvalidOperationException($"{sql}: {e.ExtendedCode} {e.Message} / {e.Detail}", e);
        }
    }

    private static string One(Connection c, string sql)
    {
        using Statement st = c.Prepare(sql);
        return st.Step() ? st.GetString(0) ?? "" : "";
    }

    private static string All(Connection c, string sql)
    {
        using Statement st = c.Prepare(sql);
        var rows = new List<string>();
        while (st.Step())
        {
            rows.Add(st.GetString(0) ?? "");
        }

        return string.Join(" | ", rows.Take(8));
    }

    private static DatabaseOptions Opts(IVfs vfs, bool group, SynchronousMode sync = SynchronousMode.Full) => new()
    {
        Vfs = vfs,
        JournalMode = JournalMode.Wal,
        ConcurrentWrites = true,
        Synchronous = sync,
        BusyTimeoutMs = 30000,
        ConcurrentGroupCommit = group,
    };

    private (int Commits, int WalSyncs) Workload(string name, bool group, int writers, int perWriter)
    {
        string path = Path.Combine(_dir, name);
        var vfs = new FaultVfs(FileVfs.Instance);
        using (Database d0 = Database.Open(path, Opts(vfs, group)))
        using (Connection c0 = d0.Connect())
        {
            Run(c0, "CREATE TABLE t(id INTEGER PRIMARY KEY, w INT, pad TEXT)");
        }

        int syncsBefore = CountWalSyncs(vfs);
        int committed = 0;
        var tasks = new List<Task>();
        for (int w = 1; w <= writers; w++)
        {
            int me = w;
            tasks.Add(Task.Factory.StartNew(() =>
            {
                using Database d = Database.Open(path, Opts(vfs, group));
                using Connection c = d.Connect();
                for (int i = 0; i < perWriter; i++)
                {
                    for (; ; )
                    {
                        try
                        {
                            Run(c, "BEGIN CONCURRENT");
                            Run(c, $"INSERT INTO t VALUES({me * 100000 + i}, {me}, zeroblob(200))");
                            Run(c, "COMMIT");
                            Interlocked.Increment(ref committed);
                            break;
                        }
                        catch (OneLiteException e) when ((e.ExtendedCode & 0xFF) == ResultCode.Busy)
                        {
                            try
                            {
                                Run(c, "ROLLBACK");
                            }
                            catch (OneLiteException)
                            {
                                // Already ended by the failed commit.
                            }
                        }
                    }
                }
            }, TaskCreationOptions.LongRunning));
        }

        Task.WaitAll([.. tasks]);
        int syncs = CountWalSyncs(vfs) - syncsBefore;

        using (Database d = Database.Open(path, Opts(FileVfs.Instance, group)))
        using (Connection c = d.Connect())
        {
            string ic = All(c, "PRAGMA integrity_check");
            Assert.Equal("ok", ic);
            Assert.Equal((writers * perWriter).ToString(), One(c, "SELECT count(*) FROM t"));
            for (int w = 1; w <= writers; w++)
            {
                Assert.Equal(perWriter.ToString(), One(c, $"SELECT count(*) FROM t WHERE w={w}"));
            }
        }

        return (committed, syncs);
    }

    private static int CountWalSyncs(FaultVfs vfs) => vfs.Ops.Count(o => o.Kind == FaultOpKind.Sync && o.Path.EndsWith("-wal", StringComparison.Ordinal));

    [Fact]
    public void CommitsShareSyncsAndAllArrive()
    {
        (int commits, int syncs) = Workload("g.db", group: true, writers: 8, perWriter: 40);
        Assert.Equal(320, commits);
        Assert.True(syncs < commits, $"{syncs} WAL syncs for {commits} commits");
    }

    [Fact]
    public void WithoutGroupCommitEveryCommitSyncs()
    {
        (int commits, int syncs) = Workload("n.db", group: false, writers: 4, perWriter: 20);
        Assert.Equal(80, commits);
        Assert.True(syncs >= commits, $"{syncs} WAL syncs for {commits} commits");
    }

    [Fact]
    public void NormalSynchronousIsUnchanged()
    {
        string path = Path.Combine(_dir, "nm.db");
        var vfs = new FaultVfs(FileVfs.Instance);
        using Database d = Database.Open(path, Opts(vfs, group: true, SynchronousMode.Normal));
        using Connection c = d.Connect();
        Run(c, "CREATE TABLE t(id INTEGER PRIMARY KEY)");
        int before = CountWalSyncs(vfs);
        Run(c, "BEGIN CONCURRENT");
        Run(c, "INSERT INTO t VALUES(1)");
        Run(c, "COMMIT");
        Assert.Equal("1", One(c, "SELECT count(*) FROM t"));
        Assert.Equal(before, CountWalSyncs(vfs));
    }

    [Fact]
    public async Task AFailedSyncFailsTheCommitsItCoveredAndNothingElse()
    {
        string path = Path.Combine(_dir, "f.db");
        var vfs = new FaultVfs(FileVfs.Instance);
        using (Database d0 = Database.Open(path, Opts(vfs, true)))
        using (Connection c0 = d0.Connect())
        {
            Run(c0, "CREATE TABLE t(id INTEGER PRIMARY KEY, w INT, pad TEXT)");
        }

        // The rule is armed once all four writers are connected, and covers a run of consecutive syncs: a sync that covers no commit (a leader's extra pass
        // with nobody joined, or a closing checkpoint, whose failure is correctly ignored) must not make the test depend on thread timing.
        using var ready = new Barrier(4, _ =>
        {
            int first = CountSyncs(vfs) + 3;
            for (int k = 0; k < 6; k++)
            {
                vfs.AddRule(new FaultRule { Call = FaultCall.Sync, Nth = first + k, ExtendedCode = VfsCodes.IoErrFsync });
            }
        });
        int failed = 0;
        var tasks = new List<Task>();
        for (int w = 1; w <= 4; w++)
        {
            int me = w;
            tasks.Add(Task.Factory.StartNew(() =>
            {
                using Database d = Database.Open(path, Opts(vfs, true));
                using Connection c = d.Connect();
                ready.SignalAndWait();
                for (int i = 0; i < 15; i++)
                {
                    for (; ; )
                    {
                        try
                        {
                            Run(c, "BEGIN CONCURRENT");
                            Run(c, $"INSERT INTO t VALUES({me * 1000 + i}, {me}, zeroblob(200))");
                            Run(c, "COMMIT");
                            break;
                        }
                        catch (Exception e) when (e is OneLiteException || e is InvalidOperationException)
                        {
                            if (!(e is OneLiteException ox && (ox.ExtendedCode & 0xFF) == ResultCode.Busy))
                            {
                                Interlocked.Increment(ref failed);
                            }

                            try
                            {
                                if (!c.AutoCommit)
                                {
                                    Run(c, "ROLLBACK");
                                }
                            }
                            catch (Exception)
                            {
                                // Already ended by the failed commit.
                            }
                        }
                    }
                }
            }, TaskCreationOptions.LongRunning));
        }

        await Task.WhenAll(tasks);
        Assert.True(failed >= 1, "the injected sync failure did not reach any commit");
        using Database v = Database.Open(path, Opts(FileVfs.Instance, true));
        using Connection vc = v.Connect();
        Assert.Equal("ok", All(vc, "PRAGMA integrity_check"));
        Assert.Equal("60", One(vc, "SELECT count(*) FROM t"));
    }

    [Fact]
    public void PlainTransactionsAreUnchangedByTheGroupCommitDefault()
    {
        Assert.False(new DatabaseOptions().ConcurrentWrites);
        Assert.True(new DatabaseOptions().ConcurrentGroupCommit);
        // With group commit on, BEGIN IMMEDIATE and autocommit write take the same WAL syncs as with it off: one per commit, nothing shared.
        int[] syncs = new int[2];
        for (int k = 0; k < 2; k++)
        {
            string path = Path.Combine(_dir, "p" + k + ".db");
            var vfs = new FaultVfs(FileVfs.Instance);
            using Database d = Database.Open(path, Opts(vfs, group: k == 1));
            using Connection c = d.Connect();
            Run(c, "CREATE TABLE t(id INTEGER PRIMARY KEY)");
            int before = CountWalSyncs(vfs);
            for (int i = 0; i < 10; i++)
            {
                Run(c, "BEGIN IMMEDIATE");
                Run(c, $"INSERT INTO t VALUES({i})");
                Run(c, "COMMIT");
                Run(c, $"INSERT INTO t VALUES({100 + i})");
            }

            syncs[k] = CountWalSyncs(vfs) - before;
            Assert.Equal("20", One(c, "SELECT count(*) FROM t"));
        }

        Assert.Equal(syncs[0], syncs[1]);
        Assert.True(syncs[1] >= 20);
    }

    [Fact]
    public async Task TheAsyncDriverSharesSyncsToo()
    {
        string path = Path.Combine(_dir, "a.db");
        var vfs = new FaultVfs(FileVfs.Instance);
        using (Database d0 = Database.Open(path, Opts(vfs, true)))
        using (Connection c0 = d0.Connect())
        {
            Run(c0, "CREATE TABLE t(id INTEGER PRIMARY KEY, w INT, pad TEXT)");
        }

        int before = CountWalSyncs(vfs);
        var tasks = new List<Task>();
        for (int w = 1; w <= 8; w++)
        {
            int me = w;
            tasks.Add(Task.Run(async () =>
            {
                using Database d = Database.Open(path, Opts(vfs, true));
                using Connection c = d.Connect();
                for (int i = 0; i < 25; i++)
                {
                    for (; ; )
                    {
                        try
                        {
                            await StepAllAsync(c, "BEGIN CONCURRENT");
                            await StepAllAsync(c, $"INSERT INTO t VALUES({me * 1000 + i}, {me}, zeroblob(200))");
                            await StepAllAsync(c, "COMMIT");
                            break;
                        }
                        catch (OneLiteException e) when ((e.ExtendedCode & 0xFF) == ResultCode.Busy)
                        {
                            if (!c.AutoCommit)
                            {
                                await StepAllAsync(c, "ROLLBACK");
                            }
                        }
                    }
                }
            }, TestContext.Current.CancellationToken));
        }

        await Task.WhenAll(tasks);
        int syncs = CountWalSyncs(vfs) - before;
        Assert.True(syncs < 200, $"{syncs} WAL syncs for 200 commits");
        using Database v = Database.Open(path, Opts(FileVfs.Instance, true));
        using Connection vc = v.Connect();
        Assert.Equal("ok", All(vc, "PRAGMA integrity_check"));
        Assert.Equal("200", One(vc, "SELECT count(*) FROM t"));
    }

    private static async Task StepAllAsync(Connection c, string sql)
    {
        using Statement st = c.Prepare(sql);
        while (await st.StepAsync())
        {
        }
    }

    private static int CountSyncs(FaultVfs vfs) => vfs.Ops.Count(o => o.Kind == FaultOpKind.Sync);
}
