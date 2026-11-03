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

/// <summary>
/// Crash sweep for group commit: several connections commit concurrently under synchronous=FULL on a storage layer that records every call; the file is
/// cut after every few calls under several loss policies and recovered. Every commit that had returned before the cut must be there, no commit may be
/// there in part, and the log must stay a prefix of each writer's commits (docs/wal.md, group commit).
/// </summary>
public sealed class GroupCommitCrashTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-groupcrash-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_dir, "g.db");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private const int Writers = 4;
    private const int PerWriter = 5;

    private static DatabaseOptions Opts(IVfs vfs) => new()
    {
        Vfs = vfs,
        PageSize = 1024,
        JournalMode = JournalMode.Wal,
        Synchronous = SynchronousMode.Full,
        ConcurrentWrites = true,
        BusyTimeoutMs = 30000,
        CacheSize = 400,
    };

    private static void Exec(Connection c, string sql)
    {
        using Statement s = c.Prepare(sql);
        while (s.Step())
        {
        }
    }

    private static IEnumerable<CrashPolicy> Policies()
    {
        yield return CrashPolicy.DropUnsynced;
        yield return CrashPolicy.KeepAll;
        for (ulong seed = 1; seed <= 4; seed++)
        {
            yield return new CrashPolicy { Seed = seed, Reorder = true };
            yield return new CrashPolicy { Seed = seed, Reorder = false };
        }

        yield return new CrashPolicy { Seed = 9, TearSectors = true, TornFill = TornFill.Random };
    }

    // The writers' commits found in an image, or the reason it is wrong.
    private string Check(MemoryVfs image, HashSet<(int W, int I)> mustHave, out int found)
    {
        found = 0;
        using Database d = Database.Open(Db, Opts(image));
        using Connection c = d.Connect();
        using (Statement ic = c.Prepare("PRAGMA integrity_check"))
        {
            ic.Step();
            string r = ic.GetString(0) ?? "";
            if (r != "ok")
            {
                return "integrity_check: " + r;
            }
        }

        var t = new HashSet<(int, int)>();
        var u = new HashSet<(int, int)>();
        using (Statement s = c.Prepare("SELECT w, i FROM t"))
        {
            while (s.Step())
            {
                t.Add(((int)s.GetInt64(0), (int)s.GetInt64(1)));
            }
        }

        using (Statement s = c.Prepare("SELECT w, i FROM u"))
        {
            while (s.Step())
            {
                u.Add(((int)s.GetInt64(0), (int)s.GetInt64(1)));
            }
        }

        if (!t.SetEquals(u))
        {
            return "a commit is there in part";
        }

        for (int w = 1; w <= Writers; w++)
        {
            int n = t.Count(x => x.Item1 == w);
            for (int i = 0; i < n; i++)
            {
                if (!t.Contains((w, i)))
                {
                    return $"writer {w} has commit {n - 1} but not {i}";
                }
            }
        }

        found = t.Count;
        foreach ((int w, int i) in mustHave)
        {
            if (!t.Contains((w, i)))
            {
                return $"commit ({w},{i}) had returned before the cut but is missing";
            }
        }

        return "";
    }

    [Fact]
    public async Task ConcurrentCommitsSurviveACutAtAnyCall()
    {
        Directory.CreateDirectory(_dir);
        IVfs inner = FileVfs.Instance;
        using (Database d = Database.Open(Db, Opts(inner)))
        using (Connection c = d.Connect())
        {
            Exec(c, "CREATE TABLE t(w INT, i INT, pad TEXT, PRIMARY KEY(w, i))");
            Exec(c, "CREATE TABLE u(w INT, i INT, PRIMARY KEY(w, i))");
        }

        var fault = new FaultVfs(inner, new FaultOptions { SectorSize = 512 });
        long start = fault.OpCount;
        var acks = new List<(int W, int I, long Ops)>();
        var tasks = new List<Task>();
        using var ready = new CountdownEvent(Writers);
        using var go = new ManualResetEventSlim();
        for (int w = 1; w <= Writers; w++)
        {
            int me = w;
            tasks.Add(Task.Factory.StartNew(() =>
            {
                using Database d = Database.Open(Db, Opts(fault));
                using Connection c = d.Connect();
                ready.Signal();
                go.Wait();
                for (int i = 0; i < PerWriter; i++)
                {
                    for (; ; )
                    {
                        try
                        {
                            Exec(c, "BEGIN CONCURRENT");
                            Exec(c, $"INSERT INTO t VALUES({me}, {i}, zeroblob(300))");
                            Exec(c, $"INSERT INTO u VALUES({me}, {i})");
                            Exec(c, "COMMIT");
                            break;
                        }
                        catch (OneLiteException e) when (e.Code == ResultCode.Busy)
                        {
                            if (!c.AutoCommit)
                            {
                                Exec(c, "ROLLBACK");
                            }
                        }
                    }

                    long ops = fault.OpCount;
                    lock (acks)
                    {
                        acks.Add((me, i, ops));
                    }
                }
            }, TaskCreationOptions.LongRunning));
        }

        ready.Wait(TestContext.Current.CancellationToken);
        go.Set();
        await Task.WhenAll(tasks);
        long end = fault.OpCount;
        Assert.Equal(Writers * PerWriter, acks.Count);

        int images = 0;
        long step = Math.Max(1, (end - start) / 80);
        for (long k = start; k <= end; k = k == end ? end + 1 : Math.Min(end, k + step))
        {
            var must = new HashSet<(int, int)>(acks.Where(a => a.Ops <= k).Select(a => (a.W, a.I)));
            foreach (CrashPolicy policy in Policies())
            {
                string why = Check(fault.CrashImage(k, policy).ToMemoryVfs(), must, out _);
                Assert.True(why.Length == 0, $"cut after {k} of {end} calls, policy {policy.Mode}/seed {policy.Seed}/reorder {policy.Reorder}: {why}");
                images++;
            }
        }

        string last = Check(fault.CrashImage(end, CrashPolicy.DropUnsynced).ToMemoryVfs(), new HashSet<(int, int)>(acks.Select(a => (a.W, a.I))), out int all);
        Assert.Equal("", last);
        Assert.Equal(Writers * PerWriter, all);
        int syncs = fault.Ops.Count(o => o.Index >= start && o.Kind == FaultOpKind.Sync);
        output.WriteLine($"{syncs} syncs for {Writers * PerWriter} commits, {end - start} storage calls, {images} crash images, all {all} commits present at the end");
    }
}
