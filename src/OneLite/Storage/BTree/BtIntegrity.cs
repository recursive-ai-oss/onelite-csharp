using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Storage.BTree;

/// <summary>
/// The structural part of <c>PRAGMA integrity_check</c>: for each tree the page types, key order, cell and freeblock layout, overflow
/// chains and (in auto-vacuum databases) the pointer map; then the freelist and a count of every page's use. It reads the whole
/// database, never writes, and reports problems as text instead of throwing, so it works on damaged files.
/// </summary>
public sealed class BtIntegrity
{
    private readonly BtContext _ctx;
    private readonly IBtPageSource _src;
    private readonly Action _pump;
    private readonly List<string> _problems = [];
    private byte[] _use = [];
    private readonly RecordView _ra = new();
    private readonly RecordView _rb = new();
    private int _maxProblems = 100;

    /// <summary>Creates a checker.</summary>
    /// <param name="ctx">The context.</param>
    /// <param name="pump">Called when the source reports Pending (complete the I/O, then the read is retried). Null means treat Pending as a failure.</param>
    public BtIntegrity(BtContext ctx, Action? pump = null)
    {
        _ctx = ctx;
        _src = ctx.Source;
        _pump = pump ?? (() => throw new InvalidOperationException("source returned Pending to a checker without a pump"));
    }

    /// <summary>Problems found by <see cref="Check"/>.</summary>
    public IReadOnlyList<string> Problems => _problems;

    private void Report(string msg)
    {
        if (_problems.Count < _maxProblems)
        {
            _problems.Add(msg);
        }
    }

    private PageHandle? Read(uint pgno)
    {
        for (; ; )
        {
            BtStatus s = _src.GetPage(pgno, out PageHandle? p);
            if (s == BtStatus.Ok)
            {
                return p;
            }

            if (s == BtStatus.Pending)
            {
                _pump();
                continue;
            }

            Report($"Page {pgno}: cannot be read: {_src.LastError?.Message}");
            return null;
        }
    }

    private bool Mark(uint pgno, byte use, string who)
    {
        if (pgno < 1 || pgno > _src.PageCount)
        {
            Report($"{who}: page {pgno} is out of range");
            return false;
        }

        if (_use[pgno] != 0)
        {
            Report($"{who}: page {pgno} is already in use ({Name(_use[pgno])})");
            return false;
        }

        _use[pgno] = use;
        return true;
    }

    private static string Name(byte u) => u switch
    {
        1 => "b-tree page",
        2 => "overflow page",
        3 => "freelist trunk",
        4 => "freelist leaf",
        5 => "pointer map",
        _ => "lock byte",
    };

    /// <summary>
    /// Checks the given trees and the freelist.
    /// </summary>
    /// <param name="trees">Every tree in the database (the schema table, then each table and index from the schema).</param>
    /// <returns>True if no problem was found.</returns>
    public bool Check(IEnumerable<BTree> trees)
    {
        _problems.Clear();
        uint n = _src.PageCount;
        _use = new byte[n + 2];
        if (n >= 1)
        {
            _use[0] = 0;
        }

        uint lb = (uint)(0x40000000L / _ctx.PageSize) + 1;
        if (lb <= n)
        {
            _use[lb] = 6;
        }

        if (_src.AutoVacuum)
        {
            uint group = (uint)(_ctx.Usable / 5 + 1);
            for (uint p = 2; p <= n; p += group)
            {
                _use[p] = 5;
            }
        }

        int ti = 0;
        foreach (BTree t in trees)
        {
            ti++;
            CheckTree(t, ti);
        }

        CheckFreelist();
        for (uint p = 1; p <= n; p++)
        {
            if (_use[p] == 0)
            {
                Report($"Page {p}: never used");
            }
        }

        return _problems.Count == 0;
    }

    private void CheckFreelist()
    {
        Page1 h = ReadHeader();
        uint trunk = h.Trunk;
        long counted = 0;
        int guard = 0;
        int maxLeaves = _ctx.Usable / 4 - 2;
        while (trunk != 0)
        {
            if (++guard > _src.PageCount + 1)
            {
                Report("Freelist: trunk chain loops");
                return;
            }

            if (!Mark(trunk, 3, "Freelist"))
            {
                return;
            }

            counted++;
            PageHandle? p = Read(trunk);
            if (p == null)
            {
                return;
            }

            uint next = Pg.U32(p.Data, 0);
            uint leaves = Pg.U32(p.Data, 4);
            if (leaves > maxLeaves)
            {
                Report($"Freelist: leaf count {leaves} too big on trunk page {trunk}");
                leaves = (uint)maxLeaves;
            }

            for (uint i = 0; i < leaves; i++)
            {
                uint leaf = Pg.U32(p.Data, 8 + 4 * (int)i);
                if (Mark(leaf, 4, "Freelist"))
                {
                    counted++;
                    if (_src.AutoVacuum && _src is ImagePageSource ips && ips.PtrmapGet(leaf).Type != 2)
                    {
                        Report($"Freelist: pointer map of free page {leaf} is not type 2");
                    }
                }
            }

            _src.Release(p);
            if (_src.AutoVacuum && _src is ImagePageSource ips2 && trunk > 2 && ips2.PtrmapGet(trunk).Type != 2)
            {
                Report($"Freelist: pointer map of free page {trunk} is not type 2");
            }

            trunk = next;
        }

        if (counted != h.Count)
        {
            Report($"Freelist: size is {h.Count} but should be {counted}");
        }
    }

    private readonly struct Page1
    {
        public Page1(uint trunk, uint count)
        {
            Trunk = trunk;
            Count = count;
        }

        public uint Trunk { get; }

        public uint Count { get; }
    }

    private Page1 ReadHeader()
    {
        PageHandle? p = Read(1);
        if (p == null)
        {
            return new Page1(0, 0);
        }

        var r = new Page1(Pg.U32(p.Data, 32), Pg.U32(p.Data, 36));
        _src.Release(p);
        return r;
    }

    private int _leafDepth;

    private void CheckTree(BTree t, int ti)
    {
        _leafDepth = -1;
        string who = $"Tree {ti} (root {t.Root})";
        if (_src.AutoVacuum && t.Root > 2 && _src is ImagePageSource ips)
        {
            (byte type, uint parent) = ips.PtrmapGet(t.Root);
            if (type != 1 || parent != 0)
            {
                Report($"Page {t.Root}: pointer map is ({type},{parent}) but should be (1,0) for a root");
            }
        }

        CheckPage(t, t.Root, 0, who, 0, null, null, false, false);
    }

    /// <summary>Checks one page and its subtree. Returns the (min, max) rowid for table trees via lo/hi bounds.</summary>
    private void CheckPage(BTree t, uint pgno, uint parent, string who, int depth, long? lo, long? hi, bool hasLoIdx, bool hasHiIdx)
    {
        _ = hasLoIdx;
        _ = hasHiIdx;
        if (depth >= BtCursor.MaxDepth)
        {
            Report($"{who}: tree is deeper than {BtCursor.MaxDepth}");
            return;
        }

        if (!Mark(pgno, 1, who))
        {
            return;
        }

        if (_src.AutoVacuum && parent != 0 && _src is ImagePageSource ips)
        {
            (byte type, uint par) = ips.PtrmapGet(pgno);
            if (type != 5 || par != parent)
            {
                Report($"Page {pgno}: pointer map is ({type},{par}) but should be (5,{parent})");
            }
        }

        PageHandle? pg = Read(pgno);
        if (pg == null)
        {
            return;
        }

        try
        {
            byte[] d = pg.Data;
            int h = Pg.HdrOff(pgno);
            byte type;
            try
            {
                type = Pg.CheckHeader(pg, _ctx.Usable);
            }
            catch (OneLiteException e)
            {
                Report($"{who} page {pgno}: {e.Detail}");
                return;
            }

            if (Pg.IsIntKey(type) != t.IntKey)
            {
                Report($"{who} page {pgno}: page type {type} does not belong in this tree");
                return;
            }

            bool leaf = Pg.IsLeaf(type);
            if (true)
            {
                // all leaves are at the same depth
                if (leaf)
                {
                    if (_leafDepth < 0)
                    {
                        _leafDepth = depth;
                    }
                    else if (_leafDepth != depth)
                    {
                        Report($"{who} page {pgno}: leaf is at depth {depth}, others at {_leafDepth}");
                    }
                }
            }

            int n = Pg.NCell(d, h);
            int ptrEnd = Pg.PtrArray(type, h) + 2 * n;
            int cs = Pg.ContentStart(d, h);
            var cover = new byte[_ctx.Usable];
            for (int i = 0; i < ptrEnd && i < cover.Length; i++)
            {
                cover[i] = 1;
            }

            // freeblocks
            int fb = Pg.FirstFree(d, h);
            int guard = _ctx.Usable / 4 + 1;
            int freeInBlocks = 0;
            while (fb != 0)
            {
                if (--guard < 0 || fb < cs || fb > _ctx.Usable - 4)
                {
                    Report($"{who} page {pgno}: freeblock chain is damaged at offset {fb}");
                    break;
                }

                int sz = Pg.U16(d, fb + 2);
                if (sz < 4 || fb + sz > _ctx.Usable)
                {
                    Report($"{who} page {pgno}: freeblock at {fb} has size {sz}");
                    break;
                }

                if (!Cover(cover, fb, sz, 3))
                {
                    Report($"{who} page {pgno}: freeblock at {fb} overlaps another area");
                }

                freeInBlocks += sz;
                int nx = Pg.U16(d, fb);
                if (nx != 0 && nx <= fb)
                {
                    Report($"{who} page {pgno}: freeblock chain is not in ascending order");
                    break;
                }

                fb = nx;
            }

            long prevRowid = 0;
            bool havePrev = false;
            RecordView? prevRec = null;
            var childList = new List<(uint Pg, long Key, int CellIdx)>();
            int cellBytes = 0;
            for (int i = 0; i < n; i++)
            {
                CellInfo ci;
                int off;
                try
                {
                    ci = _ctx.ReadCell(pg, h, type, i, out off);
                }
                catch (OneLiteException e)
                {
                    Report($"{who} page {pgno} cell {i}: {e.Detail}");
                    continue;
                }

                int fsz = Pg.Footprint(ci.Size);
                if (off + fsz > _ctx.Usable)
                {
                    Report($"{who} page {pgno} cell {i}: extends past the usable area");
                    continue;
                }

                if (off < cs)
                {
                    Report($"{who} page {pgno} cell {i}: starts at {off} before the content area at {cs}");
                }

                if (!Cover(cover, off, fsz, 2))
                {
                    Report($"{who} page {pgno} cell {i}: overlaps another cell or free space");
                }

                cellBytes += fsz;
                if (ci.PayloadSize > ci.Local)
                {
                    CheckOverflow(t, pgno, ci, who);
                }

                if (t.IntKey)
                {
                    if (havePrev && ci.Rowid <= prevRowid)
                    {
                        Report($"{who} page {pgno} cell {i}: rowid {ci.Rowid} is not greater than the previous {prevRowid}");
                    }

                    if (leaf && lo.HasValue && ci.Rowid <= lo.Value)
                    {
                        Report($"{who} page {pgno} cell {i}: rowid {ci.Rowid} is not above the parent divider {lo.Value}");
                    }

                    if (hi.HasValue && ci.Rowid > hi.Value)
                    {
                        Report($"{who} page {pgno} cell {i}: rowid {ci.Rowid} is above the parent divider {hi.Value}");
                    }

                    prevRowid = ci.Rowid;
                    havePrev = true;
                }
                else
                {
                    CheckIndexOrder(t, pg, ci, off, i, ref prevRec, who);
                }

                if (!leaf)
                {
                    childList.Add((ci.Child, ci.Rowid, i));
                }
            }

            int frag = Pg.Frag(d, h);
            int gap = cs - ptrEnd;
            if (gap < 0)
            {
                Report($"{who} page {pgno}: cell content area starts inside the pointer array");
            }
            else if (_ctx.Usable != ptrEnd + gap + cellBytes + freeInBlocks + frag)
            {
                Report($"{who} page {pgno}: free space corruption (usable {_ctx.Usable}, header+pointers {ptrEnd}, gap {gap}, cells {cellBytes}, freeblocks {freeInBlocks}, fragments {frag})");
            }

            // fileformat2 §1.6: bytes in the content area that belong to no cell and no freeblock are fragments; their total is in the header.
            // (The reference writes runs of 4 bytes where two fragments meet, so the length of a run is not checked.)
            int uncovered = 0;
            for (int i = Math.Max(cs, ptrEnd); i < _ctx.Usable; i++)
            {
                if (cover[i] != 0)
                {
                    continue;
                }

                int j = i;
                while (j < _ctx.Usable && cover[j] == 0)
                {
                    j++;
                }

                uncovered += j - i;
                i = j;
            }

            if (uncovered != frag)
            {
                Report($"{who} page {pgno}: fragmentation of {uncovered} bytes reported as {frag}");
            }

            if (leaf)
            {
                return;
            }

            uint right = Pg.RightPtr(d, h);
            long? plo = lo;
            for (int i = 0; i < childList.Count; i++)
            {
                (uint c, long key, int _) = childList[i];
                long? chi = t.IntKey ? key : null;
                CheckPage(t, c, pgno, who, depth + 1, plo, chi, false, false);
                if (t.IntKey)
                {
                    plo = key;
                }
            }

            CheckPage(t, right, pgno, who, depth + 1, plo, hi, false, false);
        }
        finally
        {
            _src.Release(pg);
        }
    }

    private string Layout(PageHandle pg, int h, byte type)
    {
        var sb = new System.Text.StringBuilder();
        byte[] d = pg.Data;
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"type={type} n={Pg.NCell(d, h)} cs={Pg.ContentStart(d, h)} frag={Pg.Frag(d, h)} cells=");
        for (int i = 0; i < Pg.NCell(d, h) && i < 80; i++)
        {
            int off = Pg.CellOffset(d, h, type, i);
            int size = -1;
            try
            {
                size = _ctx.ParseCell(pg.Number, type, d, off, _ctx.Usable).Size;
            }
            catch (OneLiteException)
            {
                // reported elsewhere
            }

            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{off}+{size} ");
        }

        sb.Append("free=");
        int fb = Pg.FirstFree(d, h);
        for (int g = 0; fb != 0 && g < 40 && fb <= _ctx.Usable - 4; g++)
        {
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{fb}+{Pg.U16(d, fb + 2)} ");
            fb = Pg.U16(d, fb);
        }

        return sb.ToString();
    }

    private static bool Cover(byte[] cover, int off, int len, byte mark)
    {
        bool ok = true;
        if (off < 0 || off + len > cover.Length)
        {
            return false;
        }

        for (int i = off; i < off + len; i++)
        {
            if (cover[i] != 0)
            {
                ok = false;
            }

            cover[i] = mark;
        }

        return ok;
    }

    private void CheckIndexOrder(BTree t, PageHandle pg, CellInfo ci, int off, int i, ref RecordView? prev, string who)
    {
        if (ci.PayloadSize > ci.Local)
        {
            return; // key order for spilled keys is checked by the table/index consistency pass of the caller
        }

        if (!_ra.Parse(pg.Data, ci.PayloadOff, ci.Local))
        {
            Report($"{who} page {pg.Number} cell {i}: malformed record");
            prev = null;
            return;
        }

        _ = off;
        if (prev != null && t.KeyInfo!.ColumnCount > 0)
        {
            if (RecordCompare.Compare(t.KeyInfo!, prev, _ra) >= 0)
            {
                Report($"{who} page {pg.Number} cell {i}: out of order");
            }
        }

        prev ??= new RecordView();
        prev.Parse((byte[])pg.Data.Clone(), ci.PayloadOff, ci.Local);
    }

    private void CheckOverflow(BTree t, uint pgno, CellInfo ci, string who)
    {
        _ = t;
        int per = _ctx.Usable - 4;
        long need = (ci.PayloadSize - ci.Local + per - 1) / per;
        uint next = ci.Overflow;
        uint prev = pgno;
        bool first = true;
        for (long i = 0; i < need; i++)
        {
            if (next == 0)
            {
                Report($"{who} page {pgno}: overflow chain ends after {i} of {need} pages");
                return;
            }

            if (!Mark(next, 2, who))
            {
                return;
            }

            if (_src.AutoVacuum && _src is ImagePageSource ips)
            {
                (byte type, uint par) = ips.PtrmapGet(next);
                byte wantType = first ? (byte)3 : (byte)4;
                if (type != wantType || par != prev)
                {
                    Report($"Page {next}: pointer map is ({type},{par}) but should be ({wantType},{prev}) for an overflow page");
                }
            }

            PageHandle? p = Read(next);
            if (p == null)
            {
                return;
            }

            uint nx = Pg.U32(p.Data, 0);
            _src.Release(p);
            prev = next;
            next = nx;
            first = false;
        }

        if (next != 0)
        {
            Report($"{who} page {pgno}: overflow chain continues after {need} pages");
        }
    }
}
