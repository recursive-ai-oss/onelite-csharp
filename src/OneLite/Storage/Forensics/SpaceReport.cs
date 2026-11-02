using System.Linq;
using RecursiveAI.OneLite.Storage.BTree;

namespace RecursiveAI.OneLite.Storage.Forensics;

/// <summary>What a page is used for in a snapshot.</summary>
public enum PageKind : byte
{
    /// <summary>Neither the database file nor the log holds the page (the declared size is larger than the data).</summary>
    Unavailable = 0,

    /// <summary>Interior page of a table B-tree.</summary>
    TableInterior,

    /// <summary>Leaf page of a table B-tree.</summary>
    TableLeaf,

    /// <summary>Interior page of an index B-tree.</summary>
    IndexInterior,

    /// <summary>Leaf page of an index B-tree.</summary>
    IndexLeaf,

    /// <summary>Overflow page of a cell.</summary>
    Overflow,

    /// <summary>Freelist trunk page.</summary>
    FreelistTrunk,

    /// <summary>Freelist leaf page.</summary>
    FreelistLeaf,

    /// <summary>Pointer-map page (auto-vacuum databases).</summary>
    PointerMap,

    /// <summary>The page that holds file offset 0x40000000; it is never used.</summary>
    LockByte,

    /// <summary>Reachable from no root and not on the freelist: a lost page. Its content is often an old B-tree page.</summary>
    Lost,

    /// <summary>Internal: not classified yet.</summary>
    Unclassified = 255,
}

/// <summary>Kinds of free space inside a B-tree page (fileformat2 §1.6).</summary>
public enum FreeKind : byte
{
    /// <summary>Unallocated space between the cell pointer array and the cell content area.</summary>
    Unallocated,

    /// <summary>A freeblock on the page's freeblock chain.</summary>
    Freeblock,
}

/// <summary>A run of free bytes inside a B-tree page.</summary>
public readonly struct FreeRegion
{
    internal FreeRegion(uint page, int offset, int length, FreeKind kind)
    {
        Page = page;
        Offset = offset;
        Length = length;
        Kind = kind;
    }

    /// <summary>Page number.</summary>
    public uint Page { get; }

    /// <summary>Offset of the first byte within the page.</summary>
    public int Offset { get; }

    /// <summary>Length in bytes.</summary>
    public int Length { get; }

    /// <summary>Unallocated gap or freeblock.</summary>
    public FreeKind Kind { get; }
}

/// <summary>Where every page of a snapshot belongs, and where the free bytes are.</summary>
public sealed class SpaceReport
{
    internal PageKind[] Kinds = [];
    internal Dictionary<uint, uint> OwnerRoot = [];

    /// <summary>Number of pages examined (the snapshot's page count, limited to pages the files can hold).</summary>
    public int PagesExamined => Kinds.Length;

    /// <summary>The kind of page <paramref name="pgno"/> (<see cref="PageKind.Unavailable"/> outside the examined range).</summary>
    public PageKind KindOf(uint pgno) => pgno >= 1 && pgno <= Kinds.Length ? Kinds[pgno - 1] : PageKind.Unavailable;

    /// <summary>Freelist trunk pages in chain order.</summary>
    public List<uint> FreelistTrunks { get; } = [];

    /// <summary>Freelist leaf pages in the order the trunks list them.</summary>
    public List<uint> FreelistLeaves { get; } = [];

    /// <summary>The freelist page count the header claims.</summary>
    public uint HeaderFreelistCount { get; internal set; }

    /// <summary>Pages that are on no root's tree and not on the freelist.</summary>
    public List<uint> LostPages { get; } = [];

    /// <summary>Unallocated gaps and freeblocks of every reachable B-tree page.</summary>
    public List<FreeRegion> FreeRegions { get; } = [];

    /// <summary>Sum of the "fragmented free bytes" counters of the B-tree page headers (bytes in fragments of 1 to 3 bytes).</summary>
    public long FragmentedBytes { get; internal set; }

    /// <summary>Bytes in the database file past the end of the last page of this snapshot.</summary>
    public long TrailingBytes { get; internal set; }

    /// <summary>File offset where the trailing bytes begin.</summary>
    public long TrailingOffset { get; internal set; }

    /// <summary>Structural problems met while walking: loops, out-of-range pointers, bad counts. The walk goes on past them.</summary>
    public List<string> Anomalies { get; } = [];

    internal void Note(string s)
    {
        if (Anomalies.Count < 1000)
        {
            Anomalies.Add(s);
        }
    }
}

internal static class SpaceAnalyzer
{
    public static SpaceReport Analyze(ForensicSnapshot snap)
    {
        var rep = new SpaceReport();
        int usable = snap.UsableSize;
        long avail = snap.AvailablePages;
        // A hostile log can name page numbers in the billions; the page table is limited to 16M pages (16 MiB) and the rest is reported.
        const int MaxClassified = 1 << 24;
        int n = (int)Math.Min(snap.PageCount, Math.Min(avail, MaxClassified));
        if (Math.Min(snap.PageCount, avail) > MaxClassified)
        {
            rep.Note($"pages above {MaxClassified} are not classified");
        }

        var kinds = new PageKind[n];
        Array.Fill(kinds, PageKind.Unclassified);
        rep.Kinds = kinds;
        byte[] p1 = snap.GetPageBytes(1)!;
        var ctx = new BtContext(new ZeroPages(snap), snap.Encoding);

        for (uint p = 1; p <= n; p++)
        {
            long start = (long)(p - 1) * snap.PageSize;
            if (start <= 0x40000000L && start + snap.PageSize > 0x40000000L)
            {
                kinds[p - 1] = PageKind.LockByte;
            }
        }

        if (Pg.U32(p1, 52) != 0)
        {
            // fileformat2 §1.7: the first pointer-map page is page 2; each covers usable/5 pages and is followed by those pages.
            long per = (usable / 5) + 1;
            for (long p = 2; p <= n; p += per)
            {
                if (kinds[p - 1] == PageKind.Unclassified)
                {
                    kinds[p - 1] = PageKind.PointerMap;
                }
            }
        }

        Freelist(snap, rep, kinds, p1, usable, n);

        var roots = new List<uint> { 1 };
        try
        {
            foreach (ForensicTable t in snap.Tables)
            {
                roots.Add(t.RootPage);
            }
        }
        catch (OneLiteException e) when (e.Code == ResultCode.Corrupt)
        {
            rep.Note($"schema unreadable: {e.Detail}");
        }

        foreach (uint root in roots.Distinct())
        {
            Walk(snap, rep, ctx, kinds, root, usable, n);
        }

        for (uint p = 1; p <= n; p++)
        {
            if (kinds[p - 1] == PageKind.Unclassified)
            {
                kinds[p - 1] = snap.GetPageBytes(p) == null ? PageKind.Unavailable : PageKind.Lost;
                if (kinds[p - 1] == PageKind.Lost)
                {
                    rep.LostPages.Add(p);
                }
            }
        }

        long declaredEnd = (long)snap.PageCount * snap.PageSize;
        if (snap.Database.DatabaseFileLength > declaredEnd)
        {
            rep.TrailingOffset = declaredEnd;
            rep.TrailingBytes = snap.Database.DatabaseFileLength - declaredEnd;
        }

        return rep;
    }

    private static void Freelist(ForensicSnapshot snap, SpaceReport rep, PageKind[] kinds, byte[] p1, int usable, int n)
    {
        rep.HeaderFreelistCount = Pg.U32(p1, 36);
        uint trunk = Pg.U32(p1, 32);
        int guard = 0;
        int maxLeaves = (usable / 4) - 2;
        while (trunk != 0)
        {
            if (trunk > n || kinds[trunk - 1] != PageKind.Unclassified || ++guard > n)
            {
                rep.Note($"freelist trunk {trunk} is out of range or already used (loop or damaged chain)");
                break;
            }

            byte[]? d = snap.GetPageBytes(trunk);
            if (d == null)
            {
                rep.Note($"freelist trunk {trunk} is not present");
                break;
            }

            kinds[trunk - 1] = PageKind.FreelistTrunk;
            rep.FreelistTrunks.Add(trunk);
            uint count = Pg.U32(d, 4);
            if (count > maxLeaves)
            {
                rep.Note($"freelist trunk {trunk} claims {count} leaves; at most {maxLeaves} fit");
                count = (uint)maxLeaves;
            }

            for (int i = 0; i < count; i++)
            {
                uint leaf = Pg.U32(d, 8 + (4 * i));
                if (leaf == 0 || leaf > n || kinds[leaf - 1] != PageKind.Unclassified)
                {
                    rep.Note($"freelist trunk {trunk} lists leaf {leaf}, which is out of range or already used");
                    continue;
                }

                kinds[leaf - 1] = PageKind.FreelistLeaf;
                rep.FreelistLeaves.Add(leaf);
            }

            trunk = Pg.U32(d, 0);
        }

        long listed = rep.FreelistTrunks.Count + rep.FreelistLeaves.Count;
        if (listed != rep.HeaderFreelistCount)
        {
            rep.Note($"the header counts {rep.HeaderFreelistCount} freelist pages but the chain lists {listed}");
        }
    }

    private static void Walk(ForensicSnapshot snap, SpaceReport rep, BtContext ctx, PageKind[] kinds, uint root, int usable, int n)
    {
        var stack = new Stack<uint>();
        stack.Push(root);
        int budget = n + 8;
        while (stack.Count > 0 && budget-- > 0)
        {
            uint pg = stack.Pop();
            if (pg == 0 || pg > n)
            {
                rep.Note($"pointer to page {pg} is outside the examined range (root {root})");
                continue;
            }

            if (kinds[pg - 1] != PageKind.Unclassified)
            {
                if (kinds[pg - 1] is not (PageKind.LockByte))
                {
                    rep.Note($"page {pg} is reached again from root {root} (already {kinds[pg - 1]})");
                }

                continue;
            }

            byte[]? d = snap.GetPageBytes(pg);
            if (d == null)
            {
                kinds[pg - 1] = PageKind.Unavailable;
                continue;
            }

            int h = pg == 1 ? 100 : 0;
            byte type = d[h];
            if (!Pg.ValidType(type))
            {
                rep.Note($"page {pg} (root {root}) has page type byte {type}");
                continue;
            }

            kinds[pg - 1] = type switch
            {
                Pg.TableInterior => PageKind.TableInterior,
                Pg.TableLeaf => PageKind.TableLeaf,
                Pg.IndexInterior => PageKind.IndexInterior,
                _ => PageKind.IndexLeaf,
            };
            rep.OwnerRoot[pg] = root;
            int ncell = Pg.NCell(d, h);
            int ptrEnd = Pg.PtrArray(type, h) + (2 * ncell);
            int cs = Pg.ContentStart(d, h);
            if (ptrEnd > usable || cs < ptrEnd || cs > usable)
            {
                rep.Note($"page {pg}: cell pointer array end {ptrEnd} or content start {cs} is outside [{Pg.PtrArray(type, h)}, {usable}]");
                continue;
            }

            if (cs > ptrEnd)
            {
                rep.FreeRegions.Add(new FreeRegion(pg, ptrEnd, cs - ptrEnd, FreeKind.Unallocated));
            }

            rep.FragmentedBytes += Pg.Frag(d, h);
            // fileformat2 §1.6: freeblocks form a chain of increasing offsets; each starts with the next offset and its own size.
            int fb = Pg.FirstFree(d, h);
            int prev = 0;
            int hops = 0;
            while (fb != 0)
            {
                if (fb < cs || fb > usable - 4 || fb <= prev || ++hops > usable / 4)
                {
                    rep.Note($"page {pg}: freeblock offset {fb} is out of order or outside the content area");
                    break;
                }

                int size = Pg.U16(d, fb + 2);
                if (size < 4 || fb + size > usable)
                {
                    rep.Note($"page {pg}: freeblock at {fb} has size {size}");
                    break;
                }

                rep.FreeRegions.Add(new FreeRegion(pg, fb, size, FreeKind.Freeblock));
                prev = fb;
                fb = Pg.U16(d, fb);
            }

            bool leaf = Pg.IsLeaf(type);
            for (int i = 0; i < ncell; i++)
            {
                int off = Pg.CellOffset(d, h, type, i);
                if (off < ptrEnd || off > usable - 4)
                {
                    rep.Note($"page {pg}: cell {i} offset {off} is outside the content area");
                    continue;
                }

                CellInfo ci;
                try
                {
                    ci = ctx.ParseCell(pg, type, d, off, usable);
                }
                catch (OneLiteException e) when (e.Code == ResultCode.Corrupt)
                {
                    rep.Note($"page {pg}: cell {i}: {e.Detail}");
                    continue;
                }

                if (!leaf)
                {
                    stack.Push(ci.Child);
                }

                if (ci.Overflow != 0)
                {
                    Overflow(snap, rep, kinds, ci, root, usable, n);
                }
            }

            if (!leaf)
            {
                stack.Push(Pg.RightPtr(d, h));
            }
        }
    }

    private static void Overflow(ForensicSnapshot snap, SpaceReport rep, PageKind[] kinds, CellInfo ci, uint root, int usable, int n)
    {
        long remaining = ci.PayloadSize - ci.Local;
        uint pg = ci.Overflow;
        long pages = (remaining + usable - 5) / (usable - 4);
        for (long i = 0; i < pages && pg != 0; i++)
        {
            if (pg > n || kinds[pg - 1] != PageKind.Unclassified)
            {
                rep.Note($"overflow chain of root {root} reaches page {pg}, which is out of range or already used");
                return;
            }

            byte[]? d = snap.GetPageBytes(pg);
            if (d == null)
            {
                kinds[pg - 1] = PageKind.Unavailable;
                return;
            }

            kinds[pg - 1] = PageKind.Overflow;
            rep.OwnerRoot[pg] = root;
            pg = Pg.U32(d, 0);
        }
    }

    /// <summary>A page source used only so a <see cref="BtContext"/> can parse cells; it serves no pages.</summary>
    private sealed class ZeroPages(ForensicSnapshot s) : IBtPageSource
    {
        public int PageSize => s.PageSize;

        public int UsableSize => s.UsableSize;

        public uint PageCount => s.PageCount;

        public bool AutoVacuum => false;

        public Exception? LastError => null;

        public uint LargestRootPage { get => 0; set => throw new NotSupportedException(); }

        public BtStatus GetPage(uint pgno, out PageHandle? page) => throw new NotSupportedException();

        public void Release(PageHandle page) => throw new NotSupportedException();

        public BtStatus MakeWritable(PageHandle page) => throw new NotSupportedException();

        public BtStatus Reserve(int allocations, int frees) => throw new NotSupportedException();

        public BtStatus ReservePtrmap(uint pgno) => throw new NotSupportedException();

        public PageHandle Allocate() => throw new NotSupportedException();

        public void Free(uint pgno) => throw new NotSupportedException();

        public void PtrmapPut(uint pgno, byte type, uint parent) => throw new NotSupportedException();
    }
}
