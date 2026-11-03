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
            if (ic != "ok") { File.WriteAllText("/x/alera/scratch/onelite/csharp/u53/tmp/ic.txt", ic); }
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
}
