using System;
using System.Collections.Generic;
using System.Linq;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;
using RecursiveAI.OneLite.Storage.Wal;
using RecursiveAI.OneLite.Tests.Pager;
using PagerClass = RecursiveAI.OneLite.Storage.Pager.Pager;

namespace RecursiveAI.OneLite.Tests.Wal;

/// <summary>
/// Crash sweeps for WAL mode on the fault-injecting VFS. One run does several commits with a full checkpoint, a restart of the log, a truncating
/// checkpoint and a clean close; every storage call is a crash point, under several loss policies. After recovery by a fresh pager the database
/// must read as exactly one commit's image (wal: a transaction is either wholly visible or not at all), and under synchronous=FULL with only
/// unsynced data lost, never an older image than the last acknowledged commit.
/// </summary>
public class WalDurabilityTests
{
    private readonly ITestOutputHelper _out;

    public WalDurabilityTests(ITestOutputHelper output) => _out = output;

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

    private static byte[] Logical(PagerClass p)
    {
        var all = new List<byte>();
        for (uint n = 2; n <= p.PageCount; n++)
        {
            all.AddRange(WalKit.Page(p, n));
        }

        return [.. all];
    }

    [Theory]
    [InlineData(SynchronousMode.Full, 512, 100)]
    [InlineData(SynchronousMode.Normal, 512, 100)]
    [InlineData(SynchronousMode.Full, 1024, 100)]
    [InlineData(SynchronousMode.Full, 512, 6)]
    public void CrashAtEveryPointLeavesOneCommitsImage(SynchronousMode sm, int ps, int cache)
    {
        var fault = new FaultVfs(WalKit.PrepareWal(16, ps), new FaultOptions { SectorSize = 512 });
        var images = new List<byte[]>();
        var acks = new List<long>();
        using (PagerClass p = PagerClass.Open(fault, Kit.Db, WalKit.Opts(ps, sm, cache, autoCheckpoint: 0)))
        {
            p.BeginReadSync();
            images.Add(Logical(p));
            acks.Add(0);
            void Commit(IEnumerable<uint> pages, int gen)
            {
                WalKit.Write(p, pages, gen);
                images.Add(Logical(p));
                acks.Add(fault.OpCount);
            }

            Commit(Kit.Range(2, 5), 1);
            Commit(Kit.Range(4, 6), 2);
            p.EndReadSync();
            p.CheckpointSync(CheckpointMode.Full);
            p.BeginReadSync();
            Commit([3u, 4u, 5u, 15u, 16u], 3);
            p.EndReadSync();
            p.CheckpointSync(CheckpointMode.Truncate);
            p.BeginReadSync();
            Commit(Kit.Range(7, 4), 4);
            p.EndReadSync();
        }

        long points = fault.OpCount;
        int policies = Policies().Count();
        var seen = new int[images.Count];
        for (long k = 0; k <= points; k++)
        {
            foreach (CrashPolicy policy in Policies())
            {
                MemoryVfs m = fault.CrashImage(k, policy).ToMemoryVfs();
                byte[] got;
                using (PagerClass q = PagerClass.Open(m, Kit.Db, WalKit.Opts(ps, sm, autoCheckpoint: 0)))
                {
                    q.BeginReadSync();
                    got = Logical(q);
                    q.EndReadSync();
                }

                int which = images.FindIndex(i => i.AsSpan().SequenceEqual(got));
                Assert.True(which >= 0, $"crash after {k} of {points} ops, policy {policy.Mode}/seed {policy.Seed}/reorder {policy.Reorder}/tear {policy.TearSectors}: the database is not any commit's image");
                seen[which]++;
                if (sm == SynchronousMode.Full && policy.Mode == CrashMode.DropUnsynced)
                {
                    int acked = acks.Select((a, g) => (a, g)).Where(t => t.a <= k).Max(t => t.g);
                    Assert.True(which >= acked, $"acknowledged commit {acked} lost after {k} ops: recovered commit {which}");
                }
            }
        }

        Console.Error.WriteLine($"{sm}/{ps}/cache {cache}: {points + 1} crash points x {policies} policies = {(points + 1) * policies} images; recovered commit counts {string.Join(",", seen)}");
    }

    /// <summary>A cached read through the wal-index must not allocate: the lookup is arithmetic over the mapped hash table.</summary>
    [Fact]
    public void CachedReadsInWalModeAllocateNothing()
    {
        MemoryVfs vfs = WalKit.PrepareWal(16);
        using var p = PagerClass.Open(vfs, Kit.Db, WalKit.Opts(autoCheckpoint: 0));
        p.BeginReadSync();
        WalKit.Write(p, Kit.Range(2, 8), 1);
        p.EndReadSync();
        p.BeginReadSync();
        for (uint n = 1; n <= 12; n++)
        {
            p.Release(p.GetSync(n));
        }

        // The synchronous wrappers allocate a closure per call; the pager's own Get does not.
        // Under a loaded parallel test run the runtime can allocate on this thread for its own reasons, so the smallest of three
        // repetitions is the loop's own cost.
        int bad = 0;
        long delta = long.MaxValue;
        for (int rep = 0; rep < 3; rep++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                for (uint n = 1; n <= 12; n++)
                {
                    if (p.Get(n, GetOptions.None, out PageFrame? f) != PagerStatus.Ok)
                    {
                        bad++;
                        continue;
                    }

                    p.Release(f!);
                }
            }

            delta = Math.Min(delta, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        p.EndReadSync();
        Assert.Equal(0, bad);
        Assert.Equal(0, delta);
    }
}
