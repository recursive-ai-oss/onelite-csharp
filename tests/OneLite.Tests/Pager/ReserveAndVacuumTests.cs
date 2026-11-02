using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;
using PagerClass = RecursiveAI.OneLite.Storage.Pager.Pager;

namespace RecursiveAI.OneLite.Tests.Pager;

/// <summary>
/// The reserve-then-effect guarantee (ReservePages, ReservePtrmap), the largest-root-page header field, and the commit-time relocation of an
/// auto-vacuum database, driven by a toy relocator that understands chains of overflow-style pages.
/// </summary>
public class ReserveAndVacuumTests
{
    private const int PageSize = 512;

    private static PagerOptions AvOpts(JournalMode jm = JournalMode.Delete, int cache = 100, int yield = 0, bool av = true) =>
        new() { PageSize = PageSize, JournalMode = jm, Synchronous = SynchronousMode.Full, CacheSize = cache, YieldEvery = yield, AutoVacuum = av };

    private static void Drive(PagerClass p, Func<PagerStatus> op)
    {
        PagerStatus s;
        while ((s = op()) == PagerStatus.Pending)
        {
            p.ExecuteSync();
        }

        if (s == PagerStatus.Error)
        {
            throw p.Error!;
        }
    }

    /// <summary>Allocates <paramref name="pages"/> pages (with pointer-map entries when auto-vacuum), frees <paramref name="free"/> of them, commits.</summary>
    private static MemoryVfs BuildFreelistDatabase(int pages, int free, bool av)
    {
        var vfs = new MemoryVfs();
        using PagerClass p = PagerClass.Open(vfs, Kit.Db, AvOpts(av: av));
        p.BeginReadSync();
        p.BeginWriteSync();
        PageFrame p1 = p.GetSync(1);
        p.Release(p1);
        var made = new List<uint>();
        for (int i = 0; i < pages; i++)
        {
            PageFrame f = p.AllocateSync();
            made.Add(f.PageNumber);
            p.Release(f);
            if (av)
            {
                Drive(p, () => p.PtrmapPut(made[^1], PtrmapType.BTree, 1));
            }
        }

        var rnd = new Random(5);
        foreach (uint pg in made.OrderBy(_ => rnd.Next()).Take(free))
        {
            p.FreeSync(pg);
            if (av)
            {
                Drive(p, () => p.PtrmapPut(pg, PtrmapType.FreePage, 0));
            }
        }

        p.CommitSync();
        p.EndReadSync();
        return vfs;
    }

    [Theory]
    [InlineData(JournalMode.Delete, true, 0, 3)]
    [InlineData(JournalMode.Delete, true, 1, 3)]
    [InlineData(JournalMode.Truncate, true, 2, 8)]
    [InlineData(JournalMode.Persist, false, 3, 3)]
    [InlineData(JournalMode.Delete, false, 1, 20)]
    [InlineData(JournalMode.Memory, true, 1, 3)]
    public void AfterReserveAllocateAndFreeNeverSuspend(JournalMode jm, bool av, int yieldEvery, int cache)
    {
        MemoryVfs inner = BuildFreelistDatabase(1500, 1100, av);
        var fault = new FaultVfs(inner, new FaultOptions { SectorSize = 512 });
        using PagerClass p = PagerClass.Open(fault, Kit.Db, AvOpts(jm, cache, yieldEvery, av));
        p.BeginReadSync();
        p.BeginWriteSync();

        // Pages the "caller" will free: it has read them and holds them while it reserves.
        var held = new List<PageFrame>();
        for (uint pg = 600; pg < 604; pg++)
        {
            if (av && p.IsPtrmapPage(pg))
            {
                continue;
            }

            held.Add(p.GetSync(pg));
        }

        const int Allocations = 300;
        Drive(p, () => p.ReservePages(Allocations, held.Count));
        if (av)
        {
            foreach (PageFrame f in held)
            {
                Drive(p, () => p.ReservePtrmap(f.PageNumber));
            }
        }

        long opsAfterReserve = fault.OpCount;
        foreach (PageFrame f in held)
        {
            p.Release(f);
        }

        // The effect phase: every call must answer at once.
        var got = new List<uint>();
        for (int i = 0; i < Allocations; i++)
        {
            PagerStatus s = p.Allocate(out PageFrame? fr);
            Assert.Equal(PagerStatus.Ok, s);
            got.Add(fr!.PageNumber);
            p.Release(fr);
            if (av)
            {
                // The pointer-map page of each page it handed out was set aside by ReservePages.
                Assert.Equal(PagerStatus.Ok, p.PtrmapPut(fr.PageNumber, PtrmapType.BTree, 1));
                Assert.Equal(PagerStatus.Ok, p.PtrmapGet(fr.PageNumber, out PtrmapType t, out uint parent));
                Assert.Equal((PtrmapType.BTree, 1u), (t, parent));
            }
        }

        foreach (PageFrame f in held)
        {
            Assert.Equal(PagerStatus.Ok, p.Free(f.PageNumber));
            if (av)
            {
                Assert.Equal(PagerStatus.Ok, p.PtrmapPut(f.PageNumber, PtrmapType.FreePage, 0));
            }
        }

        Assert.Equal(got.Count, got.Distinct().Count());
        Assert.Equal(opsAfterReserve, fault.OpCount); // no journal record had to be written in the effect phase
        p.RollbackSync();
        p.EndReadSync();
    }

    [Fact]
    public void ReservingMoreThanTheJournalBufferHoldsStillNeverWritesInTheEffectPhase()
    {
        MemoryVfs inner = BuildFreelistDatabase(1500, 1400, av: false);
        var fault = new FaultVfs(inner, new FaultOptions { SectorSize = 512 });
        using PagerClass p = PagerClass.Open(fault, Kit.Db, AvOpts(JournalMode.Delete, 50, 0, av: false));
        p.BeginReadSync();
        p.BeginWriteSync();
        Drive(p, () => p.ReservePages(1200, 0));
        long ops = fault.OpCount;
        for (int i = 0; i < 1200; i++)
        {
            Assert.Equal(PagerStatus.Ok, p.Allocate(out PageFrame? f));
            p.Release(f!);
        }

        Assert.Equal(ops, fault.OpCount);
        p.RollbackSync();
        p.EndReadSync();
    }

    [Fact]
    public void LargestRootPageIsReadAndWrittenAndSurvivesRollbackAndCommit()
    {
        var vfs = new MemoryVfs();
        using (PagerClass p = PagerClass.Open(vfs, Kit.Db, AvOpts()))
        {
            p.BeginReadSync();
            p.BeginWriteSync();
            Assert.Equal(1u, p.LargestRootPage);
            Drive(p, () => p.SetLargestRootPage(5));
            Assert.Equal(5u, p.LargestRootPage);
            p.CommitSync();
            p.BeginWriteSync();
            Drive(p, () => p.SetLargestRootPage(9));
            p.RollbackSync();
            Assert.Equal(5u, p.LargestRootPage);
            p.EndReadSync();
        }

        using PagerClass q = PagerClass.Open(vfs, Kit.Db, AvOpts());
        q.BeginReadSync();
        Assert.Equal(5u, q.LargestRootPage);
        q.EndReadSync();

        using PagerClass plain = PagerClass.Open(BuildFreelistDatabase(10, 0, av: false), Kit.Db, AvOpts(av: false));
        plain.BeginReadSync();
        plain.BeginWriteSync();
        Assert.Equal(0u, plain.LargestRootPage);
        Assert.Throws<OneLiteException>(() => plain.SetLargestRootPage(3));
        plain.RollbackSync();
    }

    // ---- relocation ----------------------------------------------------------------------------------------------------------------------

    private const int Chains = 6;
    private const int ChainLength = 24;

    /// <summary>
    /// Root pages (pointer-map type RootPage) each start a chain of overflow-style pages: bytes 0-3 of a page hold the next page's number, bytes
    /// 4-7 a marker. The chains' pages are allocated interleaved, so they are scattered; whole chains are then freed.
    /// </summary>
    private static (MemoryVfs Vfs, int[] Live) BuildChains(int[] freed)
    {
        var vfs = new MemoryVfs();
        using PagerClass p = PagerClass.Open(vfs, Kit.Db, AvOpts());
        p.BeginReadSync();
        p.BeginWriteSync();
        p.Release(p.GetSync(1));
        var root = new uint[Chains];
        var chain = new uint[Chains, ChainLength];
        for (int c = 0; c < Chains; c++)
        {
            PageFrame f = AllocateMapped(p, PtrmapType.RootPage, 0, out root[c]);
            BinaryPrimitives.WriteUInt32BigEndian(f.Data.AsSpan(4), (uint)(c << 16));
            p.Release(f);
        }

        for (int i = 0; i < ChainLength; i++)
        {
            for (int c = 0; c < Chains; c++)
            {
                uint prev = i == 0 ? root[c] : chain[c, i - 1];
                PageFrame f = AllocateMapped(p, i == 0 ? PtrmapType.Overflow1 : PtrmapType.Overflow2, prev, out chain[c, i]);
                BinaryPrimitives.WriteUInt32BigEndian(f.Data.AsSpan(4), (uint)((c << 16) | (i + 1)));
                p.Release(f);
                PageFrame pf = p.GetSync(prev);
                p.MakeWritableSync(pf);
                BinaryPrimitives.WriteUInt32BigEndian(pf.Data, chain[c, i]);
                p.Release(pf);
            }
        }

        foreach (int c in freed)
        {
            var all = new List<uint> { root[c] };
            for (int i = 0; i < ChainLength; i++)
            {
                all.Add(chain[c, i]);
            }

            foreach (uint pg in all)
            {
                p.FreeSync(pg);
                Drive(p, () => p.PtrmapPut(pg, PtrmapType.FreePage, 0));
            }
        }

        p.CommitSync();
        p.EndReadSync();
        return (vfs, Enumerable.Range(0, Chains).Where(c => !freed.Contains(c)).ToArray());
    }

    private static PageFrame AllocateMapped(PagerClass p, PtrmapType type, uint parent, out uint page)
    {
        PageFrame f = p.AllocateSync();
        page = f.PageNumber;
        uint pg = page;
        Drive(p, () => p.PtrmapPut(pg, type, parent));
        return f;
    }

    /// <summary>Follows a chain from its root and returns the pages, checking markers and the pointer map.</summary>
    private static List<uint> Walk(PagerClass p, uint root, int chainNo)
    {
        var pages = new List<uint>();
        PageFrame r = p.GetSync(root);
        uint next = BinaryPrimitives.ReadUInt32BigEndian(r.Data);
        Assert.Equal((uint)(chainNo << 16), BinaryPrimitives.ReadUInt32BigEndian(r.Data.AsSpan(4)));
        p.Release(r);
        PtrmapType rootType = 0;
        uint rootParent = 9;
        Drive(p, () => p.PtrmapGet(root, out rootType, out rootParent));
        Assert.Equal((PtrmapType.RootPage, 0u), (rootType, rootParent));
        uint parent = root;
        for (int i = 0; i < ChainLength; i++)
        {
            Assert.NotEqual(0u, next);
            pages.Add(next);
            PtrmapType t = 0;
            uint par = 0;
            uint pg = next;
            Drive(p, () => p.PtrmapGet(pg, out t, out par));
            Assert.Equal(i == 0 ? PtrmapType.Overflow1 : PtrmapType.Overflow2, t);
            Assert.Equal(parent, par);
            PageFrame f = p.GetSync(next);
            Assert.Equal((uint)((chainNo << 16) | (i + 1)), BinaryPrimitives.ReadUInt32BigEndian(f.Data.AsSpan(4)));
            parent = next;
            next = BinaryPrimitives.ReadUInt32BigEndian(f.Data);
            p.Release(f);
        }

        Assert.Equal(0u, next);
        return pages;
    }

    /// <summary>Knows how to repair references to a moved page in a world of chains. Re-entrant by hand, like the real one will be.</summary>
    private sealed class ChainRelocator : IPageRelocator
    {
        private int _step;
        private PageFrame? _frame;
        public int Moves;

        public PagerStatus Relocate(PagerClass pager, uint oldPage, uint newPage, PtrmapType type, uint parent)
        {
            for (; ; )
            {
                switch (_step)
                {
                    case 0:
                    {
                        if (type is not (PtrmapType.Overflow1 or PtrmapType.Overflow2))
                        {
                            throw new InvalidOperationException("the toy world has only chain pages to move");
                        }

                        PagerStatus s = pager.Get(parent, GetOptions.None, out _frame);
                        if (s != PagerStatus.Ok)
                        {
                            return s;
                        }

                        _step = 1;
                        break;
                    }

                    case 1:
                    {
                        PagerStatus s = pager.MakeWritable(_frame!);
                        if (s != PagerStatus.Ok)
                        {
                            return s;
                        }

                        Assert.Equal(oldPage, BinaryPrimitives.ReadUInt32BigEndian(_frame!.Data));
                        BinaryPrimitives.WriteUInt32BigEndian(_frame.Data, newPage);
                        pager.Release(_frame);
                        _frame = null;
                        _step = 2;
                        break;
                    }

                    case 2:
                    {
                        PagerStatus s = pager.Get(newPage, GetOptions.None, out _frame);
                        if (s != PagerStatus.Ok)
                        {
                            return s;
                        }

                        _step = 3;
                        break;
                    }

                    default:
                    {
                        uint next = BinaryPrimitives.ReadUInt32BigEndian(_frame!.Data);
                        if (next != 0)
                        {
                            PagerStatus s = pager.PtrmapPut(next, PtrmapType.Overflow2, newPage);
                            if (s != PagerStatus.Ok)
                            {
                                return s;
                            }
                        }

                        pager.Release(_frame);
                        _frame = null;
                        _step = 0;
                        Moves++;
                        return PagerStatus.Ok;
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(new[] { 1, 3 }, 0, JournalMode.Delete)]
    [InlineData(new[] { 0, 2, 4 }, 3, JournalMode.Delete)]
    [InlineData(new[] { 5 }, 2, JournalMode.Persist)]
    [InlineData(new[] { 0, 1, 2, 3, 4 }, 1, JournalMode.Truncate)]
    [InlineData(new int[0], 0, JournalMode.Delete)]
    public void CommitMovesFreePagesToTheEndAndRepairsReferences(int[] freed, int yieldEvery, JournalMode jm)
    {
        (MemoryVfs vfs, int[] live) = BuildChains(freed);
        var reloc = new ChainRelocator();
        using (PagerClass p = PagerClass.Open(vfs, Kit.Db, AvOpts(jm, 100, yieldEvery)))
        {
            p.SetRelocator(reloc);
            p.BeginReadSync();
            p.BeginWriteSync();
            PageFrame f1 = p.GetSync(1);
            p.MakeWritableSync(f1); // something to commit
            p.Release(f1);
            p.CommitSync();
            p.EndReadSync();
        }

        CheckChains(vfs, live);
        if (freed.Length == 5)
        {
            Assert.True(reloc.Moves > 0);
        }
    }

    // Opens the database afresh and checks every property the commit must have established.
    private static void CheckChains(MemoryVfs vfs, int[] live)
    {
        using PagerClass q = PagerClass.Open(vfs, Kit.Db, AvOpts());
        q.BeginReadSync();
        int used = 1 + live.Length * (1 + ChainLength);
        uint expected = 0;
        for (int counted = 0; counted < used;)
        {
            expected++;
            if (expected >= 2 && (expected - 2) % (PageSize / 5 + 1) == 0)
            {
                continue; // a pointer-map page
            }

            counted++;
        }

        Assert.Equal(expected, q.PageCount);
        Assert.Equal((long)expected * PageSize, Kit.ReadFile(vfs, Kit.Db).Length);
        Assert.Equal(0u, ReadHeaderField(q, DatabaseHeader.FreelistTrunkOffset));
        Assert.Equal(0u, ReadHeaderField(q, DatabaseHeader.FreelistCountOffset));
        Assert.Equal(expected, ReadHeaderField(q, DatabaseHeader.DatabaseSizeOffset));

        // Every page is page 1, a pointer-map page, or reachable from exactly one live root.
        var seen = new HashSet<uint> { 1 };
        foreach (int c in live)
        {
            uint root = (uint)(3 + c);
            Assert.True(root <= expected, "roots stay where they were");
            Assert.True(seen.Add(root));
            foreach (uint pg in Walk(q, root, c))
            {
                Assert.True(pg <= expected, $"chain page {pg} lies beyond the final size {expected}");
                Assert.True(seen.Add(pg), $"page {pg} is reachable twice");
            }
        }

        for (uint pg = 1; pg <= expected; pg++)
        {
            Assert.True(q.IsPtrmapPage(pg) || seen.Contains(pg), $"page {pg} is neither in use nor a pointer-map page: the commit leaked it");
        }

        q.EndReadSync();
    }

    private static byte[] ZeroFreeLeaves(byte[] image)
    {
        byte[] c = (byte[])image.Clone();
        uint trunk = BinaryPrimitives.ReadUInt32BigEndian(c.AsSpan(32));
        for (int guard = 0; trunk != 0 && guard < 1000 && (long)trunk * PageSize <= c.Length; guard++)
        {
            int at = (int)(trunk - 1) * PageSize;
            uint leaves = BinaryPrimitives.ReadUInt32BigEndian(c.AsSpan(at + 4));
            uint next = BinaryPrimitives.ReadUInt32BigEndian(c.AsSpan(at));
            for (uint i = 0; i < leaves && 8 + 4 * (int)i + 4 <= PageSize; i++)
            {
                uint leaf = BinaryPrimitives.ReadUInt32BigEndian(c.AsSpan(at + 8 + 4 * (int)i));
                if (leaf >= 1 && (long)leaf * PageSize <= c.Length)
                {
                    Array.Clear(c, (int)(leaf - 1) * PageSize, PageSize);
                }
            }

            trunk = next;
        }

        return c;
    }

    private static uint ReadHeaderField(PagerClass p, int offset)
    {
        PageFrame f = p.GetSync(1);
        uint v = DatabaseHeader.GetUInt32(f.Data, offset);
        p.Release(f);
        return v;
    }

    /// <summary>Power cut at every storage call of the vacuuming commit: afterwards the database is the old or the new image.</summary>
    [Theory]
    [InlineData(JournalMode.Delete, SynchronousMode.Full)]
    [InlineData(JournalMode.Persist, SynchronousMode.Normal)]
    public void CrashDuringTheVacuumingCommitLeavesTheOldOrTheNewImage(JournalMode jm, SynchronousMode sm)
    {
        (MemoryVfs built, int[] live) = BuildChains([0, 2, 4]);
        byte[] oldImg = Kit.ReadFile(built, Kit.Db);
        var fault = new FaultVfs(built, new FaultOptions { SectorSize = 512 });
        using (PagerClass p = PagerClass.Open(fault, Kit.Db, new PagerOptions { PageSize = PageSize, AutoVacuum = true, JournalMode = jm, Synchronous = sm, CacheSize = 100 }))
        {
            p.SetRelocator(new ChainRelocator());
            p.BeginReadSync();
            p.BeginWriteSync();
            PageFrame f1 = p.GetSync(1);
            p.MakeWritableSync(f1);
            p.Release(f1);
            p.CommitSync();
            p.EndReadSync();
        }

        byte[] newImg = Kit.ReadFile(fault.Inner, Kit.Db);
        Assert.NotEqual(oldImg.Length, newImg.Length);
        int images = 0;
        for (long k = 0; k <= fault.OpCount; k++)
        {
            foreach (CrashPolicy policy in new[] { CrashPolicy.DropUnsynced, CrashPolicy.KeepAll, new CrashPolicy { Seed = 3, Reorder = true }, new CrashPolicy { Seed = 4, Reorder = false }, new CrashPolicy { Seed = 9, TearSectors = true, TornFill = TornFill.Random } })
            {
                MemoryVfs m = fault.CrashImage(k, policy).ToMemoryVfs();
                using (PagerClass q = PagerClass.Open(m, Kit.Db, AvOpts(jm)))
                {
                    q.BeginReadSync();
                    q.EndReadSync();
                }

                byte[] got = Kit.ReadFile(m, Kit.Db);
                // A free leaf page holds nothing a rollback needs (the format leaves its content open), so it is not compared.
                Assert.True(ZeroFreeLeaves(got).AsSpan().SequenceEqual(ZeroFreeLeaves(oldImg)) || got.AsSpan().SequenceEqual(newImg), $"crash after {k} ops ({policy.Mode}/{policy.Seed}): neither the old nor the new image ({Kit.Describe(got, oldImg)} from old, {Kit.Describe(got, newImg)} from new)");
                images++;
            }
        }

        Assert.True(images > 50);
        CheckChains(built, live);
    }
}
