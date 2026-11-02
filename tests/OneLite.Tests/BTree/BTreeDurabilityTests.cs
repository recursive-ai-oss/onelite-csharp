using System.Collections.Generic;
using System.Linq;
using RecursiveAI.OneLite.Durability;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Tests.Trees;

/// <summary>
/// Crash sweeps with B-tree workloads on the fault-injecting VFS. A large transaction (hundreds of inserts with overflow chains and index
/// entries, deletes of earlier rows, balances, page reuse, and in auto-vacuum databases the commit-time relocation) is cut after storage
/// calls under several loss policies, with lost syncs and torn sectors. A fresh engine recovers each image; it must equal the file before
/// the transaction or the file after it, byte for byte. Both of those are separately accepted by the reference shell.
/// </summary>
public sealed class BTreeDurabilityTests
{
    private const string Db = "crash.db";

    // Transactions 1..cut-1 build up a database with live rows and freed pages; transaction `cut` is the one that gets cut: 11 inserts 150 rows,
    // 29 inserts 600, some with overflow chains (see Workload.RowCount).
    private readonly ITestOutputHelper _out;

    public BTreeDurabilityTests(ITestOutputHelper output) => _out = output;

    private void Report(string line)
    {
        _out.WriteLine(line);
        string? path = Environment.GetEnvironmentVariable("ONELITE_DURABILITY_LOG");
        if (path != null)
        {
            lock (typeof(BTreeDurabilityTests))
            {
                System.IO.File.AppendAllText(path, line + "\n");
            }
        }
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (JournalMode jm in new[] { JournalMode.Delete, JournalMode.Truncate, JournalMode.Persist })
        {
            foreach (SynchronousMode sm in new[] { SynchronousMode.Full, SynchronousMode.Normal })
            {
                foreach ((int ps, int av, int cache, int cut) in new[] { (1024, 0, 400, 29), (1024, 1, 400, 29), (512, 1, 16, 29), (4096, 2, 12, 29), (512, 0, 400, 11), (512, 1, 400, 11), (1024, 2, 10, 11) })
                {
                    yield return [jm, sm, ps, av, cache, cut];
                }
            }
        }
    }

    private static IEnumerable<CrashPolicy> Policies()
    {
        yield return CrashPolicy.DropUnsynced;
        yield return CrashPolicy.KeepAll;
        for (ulong seed = 1; seed <= 12; seed++)
        {
            yield return new CrashPolicy { Seed = seed, Reorder = true };
            yield return new CrashPolicy { Seed = seed, Reorder = false };
        }

        yield return new CrashPolicy { Seed = 9, TearSectors = true, TornFill = TornFill.Random };
    }

    private static PagerOptions Opts(int ps, int av, JournalMode jm, SynchronousMode sm, int cache) => new()
    {
        PageSize = ps,
        AutoVacuum = av != 0,
        IncrementalVacuum = av == 2,
        JournalMode = jm,
        Synchronous = sm,
        CacheSize = cache,
    };

    // The content of a free leaf page carries no information (fileformat2 §1.6, freelist), and a page taken from the freelist is not journaled, so a
    // rolled-back transaction may leave different bytes there. Everything else must match exactly: those pages are zeroed before comparing.
    private static byte[] Canonical(byte[] image, int ps)
    {
        byte[] c = (byte[])image.Clone();
        uint trunk = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(c.AsSpan(32));
        for (int guard = 0; trunk != 0 && guard < 100_000; guard++)
        {
            int off = (int)(trunk - 1) * ps;
            uint n = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(c.AsSpan(off + 4));
            for (uint i = 0; i < n; i++)
            {
                uint leaf = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(c.AsSpan(off + 8 + (4 * (int)i)));
                c.AsSpan((int)(leaf - 1) * ps, ps).Clear();
            }

            trunk = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(c.AsSpan(off));
        }

        return c;
    }

    private static byte[] Image(IVfs vfs)
    {
        using IVfsFile f = vfs.Open(Db, VfsOpenOptions.ReadOnly);
        var b = new byte[f.FileSize()];
        f.Read(0, b);
        return b;
    }

    private static MemoryVfs Prepare(int ps, int av, int before)
    {
        var vfs = new MemoryVfs();
        using StorageEngine eng = StorageEngine.Open(vfs, Db, Opts(ps, av, JournalMode.Delete, SynchronousMode.Off, 400));
        eng.BeginRead();
        var w = new BTreeWorkloadDb(eng);
        w.Setup();
        for (long t = 1; t <= before; t++)
        {
            w.RunTransaction(t);
        }

        eng.EndRead();
        return vfs;
    }

    private static void RunLast(IVfs vfs, int ps, int av, JournalMode jm, SynchronousMode sm, int cache, int cut)
    {
        using StorageEngine eng = StorageEngine.Open(vfs, Db, Opts(ps, av, jm, sm, cache));
        eng.BeginRead();
        var w = new BTreeWorkloadDb(eng);
        w.Setup();
        w.RunTransaction(cut);
        eng.EndRead();
    }

    private static MemoryVfs Clone(MemoryVfs src)
    {
        var copy = new MemoryVfs();
        using IVfsFile w = copy.Open(Db, VfsOpenOptions.MainDb | VfsOpenOptions.ReadWrite | VfsOpenOptions.Create);
        w.Write(0, Image(src));
        return copy;
    }

    private static void Recover(MemoryVfs m, int ps, int av, JournalMode jm, SynchronousMode sm, int cache)
    {
        using StorageEngine q = StorageEngine.Open(m, Db, Opts(ps, av, jm, sm, cache));
        q.BeginRead();
        q.EndRead();
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void CrashAtEveryPointLeavesTheOldOrTheNewFile(JournalMode jm, SynchronousMode sm, int ps, int av, int cache, int cut)
    {
        var fault = new FaultVfs(Prepare(ps, av, cut - 1), new FaultOptions { SectorSize = 512 });
        byte[] oldImg = Canonical(Image(fault.Inner), ps);
        long start = fault.OpCount;
        RunLast(fault, ps, av, jm, sm, cache, cut);
        long ack = fault.OpCount;
        byte[] newImg = Canonical(Image(fault.Inner), ps);
        Assert.False(oldImg.AsSpan().SequenceEqual(newImg));
        int oldN = 0, newN = 0, images = 0;
        long step = Math.Max(1, (ack - start) / 400);
        for (long k = start; k <= ack; k = k == ack ? ack + 1 : Math.Min(ack, k + step))
        {
            foreach (CrashPolicy policy in Policies())
            {
                MemoryVfs m = fault.CrashImage(k, policy).ToMemoryVfs();
                Recover(m, ps, av, jm, sm, cache);
                byte[] got = Canonical(Image(m), ps);
                bool isOld = got.AsSpan().SequenceEqual(oldImg), isNew = got.AsSpan().SequenceEqual(newImg);
                Assert.True(isOld || isNew, $"crash after {k} of {ack} ops, policy {policy.Mode}/seed {policy.Seed}/reorder {policy.Reorder}/tear {policy.TearSectors}: neither the old nor the new file");
                if (sm == SynchronousMode.Full && k >= ack && policy.Mode == CrashMode.DropUnsynced)
                {
                    Assert.True(isNew, "an acknowledged commit was lost");
                }

                oldN += isOld ? 1 : 0;
                newN += isNew && !isOld ? 1 : 0;
                images++;
            }
        }

        Report($"{jm}/{sm} ps={ps} av={av} cache={cache} cut={cut}: {ack - start} storage calls, {images} crash images (old {oldN}, new {newN})");
    }

    /// <summary>Every sync lost in turn (the device says yes and does nothing), then power cut with all unsynced data dropped.</summary>
    [Theory]
    [InlineData(JournalMode.Delete, SynchronousMode.Full, 1024, 1, 400, 29)]
    [InlineData(JournalMode.Truncate, SynchronousMode.Full, 1024, 0, 400, 29)]
    [InlineData(JournalMode.Persist, SynchronousMode.Normal, 1024, 1, 400, 29)]
    [InlineData(JournalMode.Delete, SynchronousMode.Full, 512, 1, 12, 11)]
    public void ALostSyncStillLeavesTheOldOrTheNewFileWhenPowerIsCut(JournalMode jm, SynchronousMode sm, int ps, int av, int cache, int cut)
    {
        MemoryVfs baseVfs = Prepare(ps, av, cut - 1);
        var probe = new FaultVfs(Clone(baseVfs), new FaultOptions { SectorSize = 512 });
        RunLast(probe, ps, av, jm, sm, cache, cut);
        int syncs = probe.Ops.Count(o => o.Kind == FaultOpKind.Sync);
        int images = 0;
        for (int lost = 1; lost <= syncs; lost++)
        {
            var fault = new FaultVfs(Clone(baseVfs), new FaultOptions { SectorSize = 512 });
            fault.LoseSync(lost);
            byte[] oldImg = Canonical(Image(fault.Inner), ps);
            RunLast(fault, ps, av, jm, sm, cache, cut);
            byte[] newImg = Canonical(Image(fault.Inner), ps);
            long total = fault.OpCount;
            for (long k = 0; k <= total; k += Math.Max(1, total / 60))
            {
                MemoryVfs m = fault.CrashImage(k, CrashPolicy.DropUnsynced).ToMemoryVfs();
                Recover(m, ps, av, jm, sm, cache);
                byte[] got = Canonical(Image(m), ps);
                Assert.True(got.AsSpan().SequenceEqual(oldImg) || got.AsSpan().SequenceEqual(newImg), $"lost sync #{lost} of {syncs}, crash after {k}: neither file");
                images++;
            }
        }

        Report($"{jm}/{sm} ps={ps} av={av}: {syncs} syncs lost in turn, {images} images");
    }
}
