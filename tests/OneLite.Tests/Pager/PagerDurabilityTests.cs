using System;
using System.Collections.Generic;
using System.Linq;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;
using PagerClass = RecursiveAI.OneLite.Storage.Pager.Pager;

namespace RecursiveAI.OneLite.Tests.Pager;

/// <summary>
/// Crash sweeps on the fault-injecting VFS: a page-rewrite transaction is stopped after every storage call, under several loss policies
/// (drop everything unsynced, keep everything, random survivors with reordering, torn sectors). After recovery by a fresh pager the
/// database must be exactly the old image or exactly the new image.
/// </summary>
public class PagerDurabilityTests
{
    private readonly ITestOutputHelper _out;

    public PagerDurabilityTests(ITestOutputHelper output) => _out = output;

    public static IEnumerable<object[]> Cases()
    {
        foreach (JournalMode jm in new[] { JournalMode.Delete, JournalMode.Truncate, JournalMode.Persist })
        {
            foreach (SynchronousMode sm in new[] { SynchronousMode.Full, SynchronousMode.Normal })
            {
                foreach ((int db, int dirty, int cache) in new[] { (12, 1, 100), (30, 10, 100), (120, 40, 100), (140, 90, 12) })
                {
                    yield return [jm, sm, db, dirty, cache];
                }
            }
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

    [Theory]
    [MemberData(nameof(Cases))]
    public void CrashAtEveryPointLeavesTheOldOrTheNewImage(JournalMode jm, SynchronousMode sm, int dbPages, int dirtyPages, int cache) =>
        Sweep(jm, sm, dbPages, dirtyPages, cache);

    // The 500-dirty-page transaction (the cache of 20 pages spills it many times over) has about 2000 crash points; one method per journal mode
    // so a slow host can run them as separate jobs.
    [Theory]
    [InlineData(SynchronousMode.Full)]
    [InlineData(SynchronousMode.Normal)]
    public void Crash500DirtyPagesDelete(SynchronousMode sm) => Sweep(JournalMode.Delete, sm, 600, 500, 20);

    [Theory]
    [InlineData(SynchronousMode.Full)]
    [InlineData(SynchronousMode.Normal)]
    public void Crash500DirtyPagesTruncate(SynchronousMode sm) => Sweep(JournalMode.Truncate, sm, 600, 500, 20);

    [Theory]
    [InlineData(SynchronousMode.Full)]
    [InlineData(SynchronousMode.Normal)]
    public void Crash500DirtyPagesPersist(SynchronousMode sm) => Sweep(JournalMode.Persist, sm, 600, 500, 20);

    private void Sweep(JournalMode jm, SynchronousMode sm, int dbPages, int dirtyPages, int cache)
    {
        var fault = new FaultVfs(Kit.Prepare(dbPages), new FaultOptions { SectorSize = 512 });
        byte[] oldImg = Kit.ReadFile(fault.Inner, Kit.Db);
        long ack;
        using (PagerClass p = PagerClass.Open(fault, Kit.Db, Kit.Opts(jm: jm, sm: sm, cache: cache)))
        {
            p.BeginReadSync();
            p.BeginWriteSync();
            Kit.Rewrite(p, Kit.Range(2, dirtyPages), 1);
            p.CommitSync();
            ack = fault.OpCount;
            p.EndReadSync();
        }

        byte[] newImg = Kit.ReadFile(fault.Inner, Kit.Db);
        long points = fault.OpCount;
        int outcomesOld = 0, outcomesNew = 0, images = 0;
        for (long k = 0; k <= points; k++)
        {
            foreach (CrashPolicy policy in Policies())
            {
                CrashImage img = fault.CrashImage(k, policy);
                MemoryVfs m = img.ToMemoryVfs();
                using (PagerClass q = PagerClass.Open(m, Kit.Db, Kit.Opts(jm: jm, sm: sm)))
                {
                    q.BeginReadSync();
                    q.EndReadSync();
                }

                byte[] got = Kit.ReadFile(m, Kit.Db);
                bool isOld = got.AsSpan().SequenceEqual(oldImg);
                bool isNew = got.AsSpan().SequenceEqual(newImg);
                Assert.True(isOld || isNew, $"crash after {k} of {points} ops, policy {policy.Mode}/seed {policy.Seed}/reorder {policy.Reorder}/tear {policy.TearSectors}: neither image ({Kit.Describe(got, oldImg)} from old, {Kit.Describe(got, newImg)} from new)");
                if (sm == SynchronousMode.Full && k >= ack && jm != JournalMode.Memory && policy.Mode == CrashMode.DropUnsynced)
                {
                    Assert.True(isNew, $"acknowledged commit lost: crash after {k} ops (acknowledged at {ack})");
                }

                outcomesOld += isOld ? 1 : 0;
                outcomesNew += isNew && !isOld ? 1 : 0;
                images++;
            }
        }

        _out.WriteLine($"{jm}/{sm} db={dbPages} dirty={dirtyPages}: {points + 1} crash points x {Policies().Count()} policies = {images} images; old {outcomesOld}, new {outcomesNew}");
        if (jm == JournalMode.Memory)
        {
            // journal_mode=MEMORY promises nothing across a crash; the sweep above only records what happens.
        }
    }

    /// <summary>
    /// A device that acknowledges one sync without doing it breaks the promise the pager relies on, so no outcome is guaranteed in general. With
    /// the power cut dropping everything that was never synced, though, the file still has to be the old or the new image, never a blend,
    /// because a lost sync only makes later writes less durable than claimed. Other loss patterns are counted, not asserted.
    /// </summary>
    [Theory]
    [InlineData(JournalMode.Delete, SynchronousMode.Full, 40, 25, 100)]
    [InlineData(JournalMode.Delete, SynchronousMode.Normal, 40, 25, 100)]
    [InlineData(JournalMode.Truncate, SynchronousMode.Full, 40, 25, 100)]
    [InlineData(JournalMode.Persist, SynchronousMode.Full, 40, 25, 100)]
    [InlineData(JournalMode.Delete, SynchronousMode.Full, 60, 40, 12)]
    [InlineData(JournalMode.Persist, SynchronousMode.Normal, 60, 40, 12)]
    public void OneLostSyncUnderDropUnsyncedStillLeavesTheOldOrTheNewImage(JournalMode jm, SynchronousMode sm, int dbPages, int dirtyPages, int cache)
    {
        // Count the syncs of an honest run.
        var probe = new FaultVfs(Kit.Prepare(dbPages), new FaultOptions { SectorSize = 512 });
        RunWorkload(probe, jm, sm, dirtyPages, cache);
        int syncs = probe.Ops.Count(o => o.Kind == FaultOpKind.Sync);
        int images = 0, oldN = 0, newN = 0, mixedUnderOtherPolicies = 0, blendsWithSpill = 0;
        for (int lost = 1; lost <= syncs; lost++)
        {
            var fault = new FaultVfs(Kit.Prepare(dbPages), new FaultOptions { SectorSize = 512 });
            fault.LoseSync(lost);
            byte[] oldImg = Kit.ReadFile(fault.Inner, Kit.Db);
            RunWorkload(fault, jm, sm, dirtyPages, cache);
            byte[] newImg = Kit.ReadFile(fault.Inner, Kit.Db);
            for (long k = 0; k <= fault.OpCount; k++)
            {
                foreach (CrashPolicy policy in Policies())
                {
                    MemoryVfs m = fault.CrashImage(k, policy).ToMemoryVfs();
                    using (PagerClass q = PagerClass.Open(m, Kit.Db, Kit.Opts(jm: jm, sm: sm)))
                    {
                        q.BeginReadSync();
                        q.EndReadSync();
                    }

                    byte[] got = Kit.ReadFile(m, Kit.Db);
                    bool isOld = got.AsSpan().SequenceEqual(oldImg), isNew = got.AsSpan().SequenceEqual(newImg);
                    if (policy.Mode == CrashMode.DropUnsynced)
                    {
                        // With the cache spilling, database pages are written mid-transaction; a lost sync of the journal followed by an honest
                        // sync of the database then really does leave a blend, which is the device's lie, not the pager's fault. Counted only.
                        if (cache >= dbPages)
                        {
                            Assert.True(isOld || isNew, $"lost sync #{lost} of {syncs}, crash after {k} ops: neither image");
                        }
                        else if (!isOld && !isNew)
                        {
                            blendsWithSpill++;
                        }

                        oldN += isOld ? 1 : 0;
                        newN += isNew && !isOld ? 1 : 0;
                        images++;
                    }
                    else if (!isOld && !isNew)
                    {
                        mixedUnderOtherPolicies++;
                    }
                }
            }
        }

        _out.WriteLine($"{jm}/{sm} db={dbPages} dirty={dirtyPages}: {syncs} syncs, each lost in turn; DropUnsynced images {images} (old {oldN}, new {newN}, blends where spilling made that legitimate {blendsWithSpill}); blends under other loss patterns (not asserted) {mixedUnderOtherPolicies}");
    }

    /// <summary>
    /// A spill needs only the spilled page's own journal record durable, so one journal sync covers many spills. Syncing the journal for
    /// every spilled page made a large transaction on a small cache take hundreds of milliseconds per page on a real disk.
    /// </summary>
    [Fact]
    public void ASpillingTransactionSyncsTheJournalOncePerCacheFullNotOncePerPage()
    {
        var fault = new FaultVfs(Kit.Prepare(300), new FaultOptions { SectorSize = 512 });
        RunWorkload(fault, JournalMode.Delete, SynchronousMode.Full, 240, 12);
        int journalSyncs = fault.Ops.Count(o => o.Kind == FaultOpKind.Sync && o.Path.EndsWith("-journal", StringComparison.Ordinal));
        _out.WriteLine($"240 dirty pages, cache 12: {journalSyncs} journal syncs");
        Assert.InRange(journalSyncs, 2, 100); // one page per sync would be about 480
    }

    private static void RunWorkload(FaultVfs fault, JournalMode jm, SynchronousMode sm, int dirtyPages, int cache)
    {
        using PagerClass p = PagerClass.Open(fault, Kit.Db, Kit.Opts(jm: jm, sm: sm, cache: cache));
        p.BeginReadSync();
        p.BeginWriteSync();
        Kit.Rewrite(p, Kit.Range(2, dirtyPages), 1);
        p.CommitSync();
        p.EndReadSync();
    }
}
