using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Storage.BTree;

/// <summary>A page being edited, with the cells that did not fit (the overflow list of the balance step).</summary>
internal sealed class Edit
{
    public PageHandle Pg;
    public int H;
    public byte Type;
    public readonly List<(int Idx, byte[] Bytes)> Ovfl = [];

    public Edit(PageHandle pg)
    {
        Pg = pg;
        H = Storage.BTree.Pg.HdrOff(pg.Number);
        Type = pg.Data[H];
    }

    /// <summary>Points a pooled edit at another page.</summary>
    public void Reset(PageHandle pg)
    {
        Pg = pg;
        H = Storage.BTree.Pg.HdrOff(pg.Number);
        Type = pg.Data[H];
        Ovfl.Clear();
    }

    public int PhysCount => Storage.BTree.Pg.NCell(Pg.Data, H);

    public int Count => PhysCount + Ovfl.Count;
}

/// <summary>Writes: insert, delete and balance (docs/btree.md). Reserve first, then an effect phase that never suspends.</summary>
public sealed partial class BtCursor
{
    private enum WOp : byte { None, Insert, Delete }

    private WOp _wop;
    private int _iph, _icmp;
    private int _dph, _delLevel, _delOvflCount;
    private bool _delPred, _delPredStarted, _underfull;
    private long _delRowid;
    private uint _delOvflNext;
    private byte[] _delKey = Array.Empty<byte>();
    private int _delKeyLen;
    private readonly List<PageHandle> _pins = [];
    private readonly Edit?[] _edits = new Edit?[MaxDepth + 1];
    private byte[] _gbuf = Array.Empty<byte>();
    private int[] _goff = Array.Empty<int>();
    private int[] _glen = Array.Empty<int>();
    private int _gcount, _gbytes;

    // ------------------------------------------------------------ pins and helpers

    private void ReleasePins()
    {
        foreach (PageHandle p in _pins)
        {
            _src.Release(p);
        }

        _pins.Clear();
        for (int i = 0; i < _edits.Length; i++)
        {
            ReleaseEdit(i);
        }
    }

    private PageHandle? FindPinned(uint pgno)
    {
        for (int i = 0; i <= _level; i++)
        {
            if (_pages[i] != null && _pages[i]!.Number == pgno)
            {
                return _pages[i];
            }
        }

        foreach (PageHandle p in _pins)
        {
            if (p.Number == pgno)
            {
                return p;
            }
        }

        return null;
    }

    private void Unpin(PageHandle pg)
    {
        for (int i = 0; i <= _level; i++)
        {
            if (ReferenceEquals(_pages[i], pg))
            {
                _src.Release(pg);
                _pages[i] = null;
                ReleaseEdit(i);
                return;
            }
        }

        if (_pins.Remove(pg))
        {
            _src.Release(pg);
        }
    }

    private BtStatus Writable(PageHandle pg)
    {
        BtStatus s = _src.MakeWritable(pg);
        if (s == BtStatus.Error)
        {
            throw new SourceFailure(_src.LastError ?? new InvalidOperationException("page source failed"));
        }

        return s;
    }

    private BtStatus PinPage(uint pgno, bool writable, out PageHandle? pg)
    {
        pg = FindPinned(pgno);
        if (pg == null)
        {
            BtStatus s = Get(pgno, out pg);
            if (s != BtStatus.Ok)
            {
                return s;
            }

            try
            {
                byte type = Pg.CheckHeader(pg!, _ctx.Usable);
                if (Pg.IsIntKey(type) != Tree.IntKey)
                {
                    throw OneLiteException.Corrupt(pgno, "page type does not match its tree");
                }
            }
            catch
            {
                _src.Release(pg!);
                throw;
            }

            _pins.Add(pg!);
        }

        return writable ? Writable(pg!) : BtStatus.Ok;
    }

    private void StartWrite(WOp op)
    {
        _wop = op;
        _op = Op.None;
        _gActive = false;
    }

    // Edits are pooled per cursor so that an insert that does not balance allocates nothing.
    private readonly Stack<Edit> _editPool = new();

    private Edit EditAt(int lvl)
    {
        Edit? e = _edits[lvl];
        if (e == null)
        {
            if (_editPool.Count > 0)
            {
                e = _editPool.Pop();
                e.Reset(_pages[lvl]!);
            }
            else
            {
                e = new Edit(_pages[lvl]!);
            }

            _edits[lvl] = e;
        }

        return e;
    }

    private void ReleaseEdit(int lvl)
    {
        Edit? e = _edits[lvl];
        if (e != null)
        {
            _edits[lvl] = null;
            if (_editPool.Count < 2 * MaxDepth)
            {
                _editPool.Push(e);
            }
        }
    }

    private bool OthersNeedLoad(out BtStatus status)
    {
        status = BtStatus.Ok;
        List<BtCursor> all = _ctx.Cursors;
        for (int i = 0; i < all.Count; i++)
        {
            BtCursor c = all[i];
            if (!ReferenceEquals(c, this) && c.Tree.Root == Tree.Root && c.NeedsPayloadForSave)
            {
                status = c.LoadPayload();
                if (status == BtStatus.Error)
                {
                    c.State = CursorState.Invalid;
                    status = BtStatus.Ok;
                    continue;
                }

                if (status == BtStatus.Pending)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void SaveOthers()
    {
        List<BtCursor> all = _ctx.Cursors;
        for (int i = 0; i < all.Count; i++)
        {
            BtCursor c = all[i];
            if (!ReferenceEquals(c, this) && c.Tree.Root == Tree.Root && c.State == CursorState.Valid)
            {
                c.Save();
            }
        }
    }

    // ------------------------------------------------------------ page-level edits

    private void GetLogical(Edit e, int logical, out byte[] buf, out int off, out int len)
    {
        int before = 0;
        foreach ((int idx, byte[] bytes) in e.Ovfl)
        {
            if (idx == logical)
            {
                buf = bytes;
                off = 0;
                len = bytes.Length;
                return;
            }

            if (idx < logical)
            {
                before++;
            }
        }

        CellInfo ci = _ctx.ReadCell(e.Pg, e.H, e.Type, logical - before, out off);
        buf = e.Pg.Data;
        len = ci.Size;
    }

    // Kept out of InsertCell so the closure is only built on the rare path that needs it.
    private int DefragmentAndAllocate(Edit e, int need)
    {
        uint pgno = e.Pg.Number;
        byte type = e.Type;
        int u = _ctx.Usable;
        Pg.Defragment(pgno, e.Pg.Data, e.H, u, _ctx.Scratch, (b, c) => _ctx.CellSizeAt(pgno, type, b, c));
        return Pg.Allocate(pgno, e.Pg.Data, e.H, u, need);
    }

    private void InsertCell(Edit e, int idx, byte[] cell, int off, int len)
    {
        byte[] d = e.Pg.Data;
        int h = e.H;
        int u = _ctx.Usable;
        if (e.Ovfl.Count == 0)
        {
            int n = Pg.NCell(d, h);
            if (idx < 0 || idx > n)
            {
                throw new InvalidOperationException("cell index out of range");
            }

            int need = Pg.Footprint(len);
            int o = Pg.Allocate(e.Pg.Number, d, h, u, need);
            if (o < 0 && Pg.FreeBytes(e.Pg.Number, d, h, u) >= need + 2)
            {
                o = DefragmentAndAllocate(e, need);
            }

            if (o >= 0)
            {
                Buffer.BlockCopy(cell, off, d, o, len);
                if (len < 4)
                {
                    Array.Clear(d, o + len, 4 - len);
                }

                int pa = Pg.PtrArray(e.Type, h);
                Buffer.BlockCopy(d, pa + 2 * idx, d, pa + 2 * idx + 2, 2 * (n - idx));
                Pg.PutU16(d, pa + 2 * idx, o);
                Pg.PutU16(d, h + 3, n + 1);
                _ctx.PtrmapForCell(e.Pg.Number, e.Type, d, o, len);
                LayoutCheck(e, $"insert idx={idx} len={len} at={o}");
                return;
            }
        }

        if (e.Ovfl.Count > 0 && idx <= e.Ovfl[^1].Idx)
        {
            throw new InvalidOperationException("cells must be added in ascending order once a page has overflowed");
        }

        var copy = new byte[len];
        Buffer.BlockCopy(cell, off, copy, 0, len);
        e.Ovfl.Add((idx, copy));
        _ctx.PtrmapForCell(e.Pg.Number, e.Type, copy, 0, len);
    }

    private void DropCell(Edit e, int idx)
    {
        if (e.Ovfl.Count != 0)
        {
            throw new InvalidOperationException("DropCell on a page with overflow cells");
        }

        byte[] d = e.Pg.Data;
        int h = e.H;
        int n = Pg.NCell(d, h);
        CellInfo ci = _ctx.ReadCell(e.Pg, h, e.Type, idx, out int off);
        Pg.FreeSpace(e.Pg.Number, d, h, _ctx.Usable, off, ci.Size);
        int pa = Pg.PtrArray(e.Type, h);
        Buffer.BlockCopy(d, pa + 2 * (idx + 1), d, pa + 2 * idx, 2 * (n - idx - 1));
        Pg.PutU16(d, h + 3, n - 1);
        LayoutCheck(e, $"drop idx={idx} off={off} size={ci.Size}");
        if (n - 1 == 0)
        {
            // An empty page is renormalized so no freeblocks remain.
            Pg.SetContentStart(d, h, _ctx.Usable);
            Pg.PutU16(d, h + 1, 0);
            d[h + 7] = 0;
        }
    }

    [System.Diagnostics.Conditional("LAYOUTCHECK")]
    private void LayoutCheck(Edit e, string what)
    {
        byte[] d = e.Pg.Data;
        int h = e.H;
        int n = Pg.NCell(d, h);
        int u = _ctx.Usable;
        int ptrEnd = Pg.PtrArray(e.Type, h) + 2 * n;
        int cs = Pg.ContentStart(d, h);
        var cover = new byte[u];
        for (int i = 0; i < n; i++)
        {
            int o = Pg.CellOffset(d, h, e.Type, i);
            int sz = Pg.Footprint(_ctx.ParseCell(e.Pg.Number, e.Type, d, o, u).Size);
            for (int k = o; k < o + sz; k++)
            {
                cover[k]++;
            }
        }

        int fb = Pg.FirstFree(d, h);
        while (fb != 0)
        {
            int sz = Pg.U16(d, fb + 2);
            for (int k = fb; k < fb + sz; k++)
            {
                cover[k]++;
            }

            fb = Pg.U16(d, fb);
        }

        int unc = 0;
        for (int i = cs; i < u; i++)
        {
            if (cover[i] > 1)
            {
                throw new InvalidOperationException($"layout: overlap at {i} after {what}");
            }

            if (cover[i] == 0)
            {
                int j = i;
                while (j < u && cover[j] == 0)
                {
                    j++;
                }

                unc += j - i;
                i = j;
            }
        }

        if (unc != Pg.Frag(d, h))
        {
            throw new InvalidOperationException($"layout: fragments {unc} != header {Pg.Frag(d, h)} after {what}");
        }
    }

    private void SetRightPtr(Edit e, uint child)
    {
        Pg.PutU32(e.Pg.Data, e.H + 8, child);
        if (_src.AutoVacuum)
        {
            _src.PtrmapPut(child, 5, e.Pg.Number);
        }
    }

    // ------------------------------------------------------------ reserve

    private static (int Start, int Count) Window(int parentCells, int ci)
    {
        int nCh = parentCells + 1;
        int nSib = Math.Min(3, nCh);
        return (Math.Clamp(ci - 1, 0, nCh - nSib), nSib);
    }

    private int MaxDivider => Tree.IntKey ? 13 : _ctx.MaxIndexCell;

    private bool ParentMay(PageHandle parent, int lvl)
    {
        int h = Pg.HdrOff(parent.Number);
        int free = Pg.FreeBytes(parent.Number, parent.Data, h, _ctx.Usable);
        int n = Pg.NCell(parent.Data, h);
        int grow = 4 * (MaxDivider + 2);
        int shrink = 2 * (MaxDivider + 2);
        if (!_underfull)
        {
            return free < grow;
        }

        if (lvl == 0)
        {
            return free < grow || n <= 2;
        }

        return free < grow || free + shrink > 2 * _ctx.Usable / 3 || n <= 2;
    }

    private BtStatus ReservePtrmapFor(PageHandle pg)
    {
        if (!_src.AutoVacuum)
        {
            return BtStatus.Ok;
        }

        int h = Pg.HdrOff(pg.Number);
        byte type = pg.Data[h];
        int n = Pg.NCell(pg.Data, h);
        for (int i = 0; i < n; i++)
        {
            CellInfo ci = _ctx.ReadCell(pg, h, type, i, out _);
            if (!Pg.IsLeaf(type) && ci.Child >= 2)
            {
                BtStatus s = _src.ReservePtrmap(ci.Child);
                if (s != BtStatus.Ok)
                {
                    return s;
                }
            }

            if (ci.Overflow >= 2)
            {
                BtStatus s = _src.ReservePtrmap(ci.Overflow);
                if (s != BtStatus.Ok)
                {
                    return s;
                }
            }
        }

        if (!Pg.IsLeaf(type))
        {
            uint rp = Pg.RightPtr(pg.Data, h);
            if (rp >= 2)
            {
                return _src.ReservePtrmap(rp);
            }
        }

        return BtStatus.Ok;
    }

    /// <summary>
    /// The conservative reserve of spec §5.3: pins and makes writable every page a balance cascade starting at <paramref name="startLvl"/>
    /// can touch, then reserves the pages it can allocate and free. Idempotent: pins already held are found, not re-fetched.
    /// </summary>
    private BtStatus ReserveChain(int startLvl, bool startMay, int forceLevel, int extraAllocs, int extraFrees)
    {
        int allocs = extraAllocs, frees = extraFrees;
        bool may = startMay;
        for (int lvl = startLvl; lvl >= 0; lvl--)
        {
            bool here = may || lvl == forceLevel;
            if (!here)
            {
                if (forceLevel >= 0 && lvl > forceLevel)
                {
                    continue;
                }

                break;
            }

            BtStatus s = Writable(_pages[lvl]!);
            if (s != BtStatus.Ok)
            {
                return s;
            }

            if (lvl == 0)
            {
                // Deepening the root moves its cells into a new child (one page), which is then balanced like any other page: its cells
                // may need up to two more pages (a single sibling grows to at most three).
                allocs += 3;
                frees += 1;
                PageHandle root = _pages[0]!;
                s = ReservePtrmapFor(root);
                if (s != BtStatus.Ok)
                {
                    return s;
                }

                int rh = Pg.HdrOff(root.Number);
                if (!Pg.IsLeaf(root.Data[rh]) && Pg.NCell(root.Data, rh) <= 2)
                {
                    uint child = Pg.RightPtr(root.Data, rh);
                    if (child >= 2)
                    {
                        s = PinPage(child, false, out _);
                        if (s != BtStatus.Ok)
                        {
                            return s;
                        }

                        s = _src.ReservePtrmap(child);
                        if (s != BtStatus.Ok)
                        {
                            return s;
                        }
                    }
                }

                break;
            }

            PageHandle parent = _pages[lvl - 1]!;
            s = Writable(parent);
            if (s != BtStatus.Ok)
            {
                return s;
            }

            int ph = Pg.HdrOff(parent.Number);
            int pn = Pg.NCell(parent.Data, ph);
            (int start, int nSib) = Window(pn, _idx[lvl - 1]);
            for (int j = 0; j < nSib; j++)
            {
                uint pgno = ChildOf(lvl - 1, start + j);
                s = PinPage(pgno, true, out PageHandle? sp);
                if (s != BtStatus.Ok)
                {
                    return s;
                }

                s = ReservePtrmapFor(sp!);
                if (s != BtStatus.Ok)
                {
                    return s;
                }

                // a sibling may be emptied and freed, which writes its own pointer-map entry (type 2)
                s = _src.ReservePtrmap(pgno);
                if (s != BtStatus.Ok)
                {
                    return s;
                }
            }

            // A balance over nSib siblings ends with at most nSib + 2 pages (fileformat2 §1.6 leaves the split rule to the writer).
            allocs += nSib + 2;
            frees += nSib;
            may = ParentMay(parent, lvl - 1);
        }

        return _src.Reserve(allocs, frees);
    }

    // ------------------------------------------------------------ insert

    /// <summary>
    /// Inserts a row into a rowid table, replacing any row with the same rowid. Suspend points: the seek (Seek.*), Insert.ReserveOverflow and
    /// Insert.ReserveBalance (page reads, journal originals, freelist trunks); then Insert.Apply, which never suspends. Pass the same
    /// arguments on every repeat after <see cref="BtStatus.Pending"/>. Afterwards the cursor is <see cref="CursorState.Saved"/> on the new row.
    /// </summary>
    /// <param name="rowid">The rowid.</param>
    /// <param name="payload">The record (up to 2^31-1 bytes).</param>
    public BtStatus Insert(long rowid, ReadOnlySpan<byte> payload)
    {
        if (!Tree.IntKey)
        {
            throw new InvalidOperationException("rowid insert into an index tree");
        }

        return InsertGuard(rowid, payload, default);
    }

    /// <summary>
    /// Inserts an entry into an index (or WITHOUT ROWID) tree. The entry must not already exist; the tree does not check uniqueness beyond ordering.
    /// </summary>
    /// <param name="record">The entry as a record.</param>
    /// <param name="key">The same entry unpacked, for ordering.</param>
    public BtStatus Insert(ReadOnlySpan<byte> record, ReadOnlySpan<Mem> key)
    {
        if (Tree.IntKey)
        {
            throw new InvalidOperationException("index insert into a rowid table");
        }

        return InsertGuard(0, record, key);
    }

    private BtStatus InsertGuard(long rowid, ReadOnlySpan<byte> payload, ReadOnlySpan<Mem> key)
    {
        try
        {
            if (_wop != WOp.Insert)
            {
                if (Tree.IntKey)
                {
                    _ctx.Changed?.Invoke(Tree.Root, rowid);
                }

                StartWrite(WOp.Insert);
                _iph = 0;
            }

            for (; ; )
            {
                switch (_iph)
                {
                    case 0:
                        {
                            BtStatus st = SeekGuard(rowid, key, 0, out int cmp);
                            if (st != BtStatus.Ok)
                            {
                                return st;
                            }

                            _icmp = cmp;
                            if (Tree.IntKey && State == CursorState.Valid && cmp == 0)
                            {
                                _iph = 1;
                                _dph = 0;
                            }
                            else
                            {
                                if (State == CursorState.Valid && !Pg.IsLeaf(_pages[_level]!.Data[Pg.HdrOff(_pages[_level]!.Number)]))
                                {
                                    throw new InvalidOperationException("the entry already exists in the index");
                                }

                                _iph = 2;
                            }

                            break;
                        }
                    case 1:
                        {
                            // An existing row with this rowid is removed first, then the new row is inserted by a fresh seek.
                            BtStatus st = DeleteStep();
                            if (st != BtStatus.Ok)
                            {
                                return st;
                            }

                            _iph = 0;
                            break;
                        }
                    case 2:
                        {
                            BtStatus st = ReserveInsert(rowid, payload);
                            if (st != BtStatus.Ok)
                            {
                                return st;
                            }

                            _iph = 3;
                            break;
                        }
                    default:
                        ApplyInsert(rowid, payload, key);
                        _src.EndEffect();
                        _wop = WOp.None;
                        _iph = 0;
                        return BtStatus.Ok;
                }
            }
        }
        catch (Exception e) when (e is OneLiteException or SourceFailure)
        {
            return Fail(e);
        }
        catch (Exception e) when (IsStructuralFault(e))
        {
            return Fail(OneLiteException.Corrupt(Tree.Root, "a page of the tree is inconsistent: " + e.Message));
        }
    }

    /// <summary>
    /// Writes edit pages whose layout was only checked in part (fileformat2 §1.6 gives the rules, a hostile file breaks them anywhere).
    /// Any bounds or consistency fault raised while editing such a page is corruption of the file, not a caller error.
    /// </summary>
    private static bool IsStructuralFault(Exception e) =>
        e is InvalidOperationException or ArgumentException or IndexOutOfRangeException or NullReferenceException or OverflowException;

    private int NewCellSize(long rowid, int payloadLen, out int local, out int nOvfl)
    {
        bool tl = Tree.IntKey;
        local = _ctx.LocalSize(payloadLen, tl);
        nOvfl = payloadLen > local ? (int)(((long)payloadLen - local + _ctx.Usable - 5) / (_ctx.Usable - 4)) : 0;
        int size = Varint.Length((ulong)payloadLen) + local + (nOvfl > 0 ? 4 : 0);
        if (tl)
        {
            size += Varint.Length((ulong)rowid);
        }

        return size;
    }

    private BtStatus ReserveInsert(long rowid, ReadOnlySpan<byte> payload)
    {
        _underfull = false;
        if (OthersNeedLoad(out BtStatus st))
        {
            return st;
        }

        PageHandle leaf = _pages[_level]!;
        st = Writable(leaf);
        if (st != BtStatus.Ok)
        {
            return st;
        }

        int h = Pg.HdrOff(leaf.Number);
        byte type = leaf.Data[h];
        if (!Pg.IsLeaf(type))
        {
            throw OneLiteException.Corrupt(leaf.Number, "insert position is not on a leaf");
        }

        int cellSize = NewCellSize(rowid, payload.Length, out _, out int nOvfl);
        int need = Pg.Footprint(cellSize) + 2;
        int free = Pg.FreeBytes(leaf.Number, leaf.Data, h, _ctx.Usable);
        return ReserveChain(_level, free < need, -1, nOvfl, 0);
    }

    private uint WriteOverflowChain(ReadOnlySpan<byte> payload, int local)
    {
        int per = _ctx.Usable - 4;
        int rem = payload.Length - local;
        int count = (rem + per - 1) / per;
        var pages = new PageHandle[count];
        for (int i = 0; i < count; i++)
        {
            pages[i] = _src.Allocate();
        }

        int pos = local;
        for (int i = 0; i < count; i++)
        {
            byte[] d = pages[i].Data;
            Pg.PutU32(d, 0, i + 1 < count ? pages[i + 1].Number : 0);
            int chunk = Math.Min(per, payload.Length - pos);
            payload.Slice(pos, chunk).CopyTo(d.AsSpan(4));
            pos += chunk;
            if (i > 0 && _src.AutoVacuum)
            {
                _src.PtrmapPut(pages[i].Number, 4, pages[i - 1].Number);
            }
        }

        uint first = pages[0].Number;
        foreach (PageHandle p in pages)
        {
            _src.Release(p);
        }

        return first;
    }

    private byte[] _cellBuf = [];

    private void ApplyInsert(long rowid, ReadOnlySpan<byte> payload, ReadOnlySpan<Mem> key)
    {
        _ = key;
        _underfull = false;
        SaveOthers();
        PageHandle leaf = _pages[_level]!;
        byte type = leaf.Data[Pg.HdrOff(leaf.Number)];
        int local = _ctx.LocalSize(payload.Length, Tree.IntKey);
        uint ovfl = payload.Length > local ? WriteOverflowChain(payload, local) : 0;
        // One buffer per cursor, grown once: the cell is copied into the page (or into its own overflow copy) before this method returns.
        int maxCell = _ctx.MaxCell(type);
        if (_cellBuf.Length < maxCell)
        {
            _cellBuf = new byte[maxCell];
        }

        byte[] cell = _cellBuf;
        int len = _ctx.BuildCell(type, rowid, 0, payload, ovfl, cell);
        int idx = State == CursorState.Valid ? (_icmp < 0 ? _idx[_level] + 1 : _idx[_level]) : 0;
        Edit e = EditAt(_level);
        InsertCell(e, idx, cell, 0, len);
        BalanceUp(_level, -1);
        if (Tree.IntKey)
        {
            _savedRowid = rowid;
        }
        else
        {
            if (_savedKey.Length < payload.Length)
            {
                _savedKey = new byte[payload.Length];
            }

            payload.CopyTo(_savedKey);
            _savedKeyLen = payload.Length;
            _savedMemsReady = false;
        }

        ReleasePath();
        ReleasePins();
        State = CursorState.Saved;
    }

    // ------------------------------------------------------------ delete

    /// <summary>
    /// Deletes the entry the cursor is on. Suspend points: Delete.PredecessorDescend (index entries in interior pages), Delete.OverflowFree
    /// (one overflow page per step) and the reserve. Afterwards the cursor is <see cref="CursorState.Saved"/> on the deleted key, so
    /// <see cref="Next"/> and <see cref="Previous"/> continue without skipping or repeating a row.
    /// </summary>
    public BtStatus Delete()
    {
        try
        {
            if (_wop != WOp.Delete)
            {
                if (State == CursorState.Saved)
                {
                    BtStatus r = RestoreCore();
                    if (r != BtStatus.Ok)
                    {
                        return r;
                    }

                    if (_skip != 0)
                    {
                        throw new InvalidOperationException("the entry to delete no longer exists");
                    }
                }

                if (State != CursorState.Valid)
                {
                    throw new InvalidOperationException("Delete needs a cursor on an entry");
                }

                if (Tree.IntKey)
                {
                    _ctx.Changed?.Invoke(Tree.Root, Rowid);
                }

                StartWrite(WOp.Delete);
                _dph = 0;
            }

            BtStatus s = DeleteStep();
            if (s == BtStatus.Ok)
            {
                _wop = WOp.None;
            }

            return s;
        }
        catch (Exception e) when (e is OneLiteException or SourceFailure)
        {
            return Fail(e);
        }
        catch (Exception e) when (IsStructuralFault(e))
        {
            return Fail(OneLiteException.Corrupt(Tree.Root, "a page of the tree is inconsistent: " + e.Message));
        }
    }

    private BtStatus DeleteStep()
    {
        for (; ; )
        {
            switch (_dph)
            {
                case 0:
                    {
                        CellInfo ci = CurrentCell(out _, out _);
                        _delLevel = _level;
                        _delPred = false;
                        _delPredStarted = false;
                        _delRowid = ci.Rowid;
                        _delOvflNext = ci.Overflow;
                        _delOvflCount = ci.PayloadSize > ci.Local ? (int)((ci.PayloadSize - ci.Local + _ctx.Usable - 5) / (_ctx.Usable - 4)) : 0;
                        if (_delOvflCount > _src.PageCount)
                        {
                            // A chain cannot have more pages than the file; trusting the claimed size would reserve (and journal) that many pages.
                            throw OneLiteException.Corrupt(_pages[_level]!.Number, "the cell's payload needs more overflow pages than the database has");
                        }
                        _dph = 1;
                        break;
                    }
                case 1:
                    {
                        if (!Tree.IntKey)
                        {
                            CellInfo ci = CurrentCell(out PageHandle pg, out int off);
                            BtStatus st = PayloadOf(pg, ci, off, out byte[] buf, out int boff);
                            if (st != BtStatus.Ok)
                            {
                                return st;
                            }

                            int plen = (int)ci.PayloadSize;
                            if (_delKey.Length < plen)
                            {
                                _delKey = new byte[plen];
                            }

                            Buffer.BlockCopy(buf, boff, _delKey, 0, plen);
                            _delKeyLen = plen;
                        }

                        _dph = 2;
                        break;
                    }
                case 2:
                    {
                        PageHandle p = _pages[_delLevel]!;
                        if (!Pg.IsLeaf(p.Data[Pg.HdrOff(p.Number)]))
                        {
                            if (!_delPredStarted)
                            {
                                _nextPgno = ChildOf(_delLevel, _idx[_delLevel]);
                                _descendLeft = false;
                                _delPredStarted = true;
                            }

                            BtStatus st = DescendLoop(false);
                            if (st != BtStatus.Ok)
                            {
                                return st;
                            }

                            _delPred = true;
                        }

                        _dph = 3;
                        break;
                    }
                case 3:
                    {
                        if (OthersNeedLoad(out BtStatus st))
                        {
                            return st;
                        }

                        _dph = 4;
                        break;
                    }
                case 4:
                    {
                        BtStatus st = ReserveDelete();
                        if (st != BtStatus.Ok)
                        {
                            return st;
                        }

                        _dph = 5;
                        break;
                    }
                case 5:
                    {
                        while (_delOvflCount > 0)
                        {
                            if (_delOvflNext == 0)
                            {
                                throw OneLiteException.Corrupt(_pages[_delLevel]!.Number, "overflow chain ends early");
                            }

                            BtStatus st = Get(_delOvflNext, out PageHandle? op);
                            if (st != BtStatus.Ok)
                            {
                                return st;
                            }

                            uint next = Pg.U32(op!.Data, 0);
                            _src.Release(op);
                            st = _src.ReservePtrmap(_delOvflNext);
                            if (st != BtStatus.Ok)
                            {
                                return st;
                            }

                            _src.Free(_delOvflNext);
                            _delOvflNext = next;
                            _delOvflCount--;
                        }

                        _dph = 6;
                        break;
                    }
                default:
                    ApplyDelete();
                    _src.EndEffect();
                    _dph = 0;
                    return BtStatus.Ok;
            }
        }
    }

    private BtStatus ReserveDelete()
    {
        _underfull = true;
        PageHandle leaf = _pages[_level]!;
        BtStatus s = Writable(leaf);
        if (s != BtStatus.Ok)
        {
            return s;
        }

        int h = Pg.HdrOff(leaf.Number);
        byte type = leaf.Data[h];
        int n = Pg.NCell(leaf.Data, h);
        int free = Pg.FreeBytes(leaf.Number, leaf.Data, h, _ctx.Usable);
        int removed;
        if (_delPred)
        {
            CellInfo pc = _ctx.ReadCell(leaf, h, type, n - 1, out _);
            removed = Pg.Footprint(pc.Size) + 2;
            s = Writable(_pages[_delLevel]!);
            if (s != BtStatus.Ok)
            {
                return s;
            }
        }
        else
        {
            CellInfo c = _ctx.ReadCell(leaf, h, type, _idx[_level], out _);
            removed = Pg.Footprint(c.Size) + 2;
        }

        bool may = _level > 0 && (n - 1 == 0 || free + removed > 2 * _ctx.Usable / 3);
        // A replaced interior entry can grow by the child pointer plus the difference of the two keys, so the interior page may overflow.
        return ReserveChain(_level, may, _delPred ? _delLevel : -1, 0, _delOvflCount);
    }

    private void ApplyDelete()
    {
        _underfull = true;
        SaveOthers();
        if (!_delPred)
        {
            Edit leaf = EditAt(_level);
            DropCell(leaf, _idx[_level]);
            BalanceUp(_level, -1);
        }
        else
        {
            Edit leaf = EditAt(_level);
            int li = leaf.PhysCount - 1;
            CellInfo pc = _ctx.ReadCell(leaf.Pg, leaf.H, leaf.Type, li, out int poff);
            Edit pe = EditAt(_delLevel);
            CellInfo old = _ctx.ReadCell(pe.Pg, pe.H, pe.Type, _idx[_delLevel], out int ooff);
            var repl = new byte[4 + pc.Size];
            Pg.PutU32(repl, 0, old.Child);
            Buffer.BlockCopy(leaf.Pg.Data, poff, repl, 4, pc.Size);
            DropCell(leaf, li);
            DropCell(pe, _idx[_delLevel]);
            InsertCell(pe, _idx[_delLevel], repl, 0, repl.Length);
            BalanceUp(_level, _delLevel);
        }

        if (!Tree.IntKey)
        {
            if (_savedKey.Length < _delKeyLen)
            {
                _savedKey = new byte[_delKeyLen];
            }

            Buffer.BlockCopy(_delKey, 0, _savedKey, 0, _delKeyLen);
            _savedKeyLen = _delKeyLen;
            _savedMemsReady = false;
        }
        else
        {
            _savedRowid = _delRowid;
        }

        ReleasePath();
        ReleasePins();
        State = CursorState.Saved;
    }

    // ------------------------------------------------------------ balance (effect phase; never suspends)

    private void BalanceUp(int lvl, int mustVisit)
    {
        int u = _ctx.Usable;
        while (lvl >= 0)
        {
            Edit e = EditAt(lvl);
            if (lvl == 0)
            {
                if (e.Ovfl.Count > 0)
                {
                    DeepenRoot(e);
                    lvl = 1;
                    continue;
                }

                if (_underfull && !Pg.IsLeaf(e.Type) && e.PhysCount == 0)
                {
                    ShrinkRoot(e);
                }

                return;
            }

            bool needs = e.Ovfl.Count > 0
                || (_underfull && (e.PhysCount == 0 || Pg.FreeBytes(e.Pg.Number, e.Pg.Data, e.H, u) > 2 * u / 3));
            if (!needs)
            {
                if (mustVisit >= 0 && lvl > mustVisit)
                {
                    lvl--;
                    continue;
                }

                return;
            }

            BalanceNonRoot(EditAt(lvl - 1), _idx[lvl - 1], e);
            lvl--;
        }
    }

    /// <summary>The tree grows a level: the root's cells move to a new child, which is then split (fileformat2 §1.6: the root page number never changes).</summary>
    private void DeepenRoot(Edit root)
    {
        if (_level + 1 >= MaxDepth)
        {
            throw OneLiteException.Corrupt(root.Pg.Number, "tree is too deep to grow");
        }

        PageHandle child = _src.Allocate();
        byte[] cd = child.Data;
        Pg.Zero(cd, 0, _ctx.Usable, root.Type);
        var ce = new Edit(child);
        int total = root.Count;
        for (int i = 0; i < total; i++)
        {
            GetLogical(root, i, out byte[] b, out int o, out int l);
            InsertCell(ce, i, b, o, l);
        }

        if (!Pg.IsLeaf(root.Type))
        {
            Pg.PutU32(cd, 8, Pg.RightPtr(root.Pg.Data, root.H));
            if (_src.AutoVacuum)
            {
                _src.PtrmapPut(Pg.RightPtr(cd, 0), 5, child.Number);
            }
        }

        byte newType = Tree.IntKey ? Pg.TableInterior : Pg.IndexInterior;
        Pg.Zero(root.Pg.Data, root.H, _ctx.Usable, newType);
        root.Ovfl.Clear();
        root.Type = newType;
        Pg.PutU32(root.Pg.Data, root.H + 8, child.Number);
        if (_src.AutoVacuum)
        {
            _src.PtrmapPut(child.Number, 5, root.Pg.Number);
        }

        for (int i = _level; i >= 1; i--)
        {
            _pages[i + 1] = _pages[i];
            _idx[i + 1] = _idx[i];
            _edits[i + 1] = _edits[i];
        }

        _level++;
        _pages[1] = child;
        _idx[0] = 0;
        _edits[1] = ce;
        Deepened++;
    }

    private void ShrinkRoot(Edit root)
    {
        uint cpgno = Pg.RightPtr(root.Pg.Data, root.H);
        PageHandle? cp = FindPinned(cpgno) ?? throw new InvalidOperationException("root's only child was not reserved");
        var ce = new Edit(cp);
        int u = _ctx.Usable;
        int n = ce.PhysCount;
        long used = 0;
        for (int i = 0; i < n; i++)
        {
            CellInfo ci = _ctx.ReadCell(cp, ce.H, ce.Type, i, out _);
            used += Pg.Footprint(ci.Size) + 2;
        }

        if (used + Pg.HeaderSize(ce.Type) > u - root.H)
        {
            return;
        }

        byte[] rd = root.Pg.Data;
        Pg.Zero(rd, root.H, u, ce.Type);
        var re = new Edit(root.Pg);
        for (int i = 0; i < n; i++)
        {
            GetLogical(ce, i, out byte[] b, out int o, out int l);
            InsertCell(re, i, b, o, l);
        }

        if (!Pg.IsLeaf(ce.Type))
        {
            SetRightPtr(re, Pg.RightPtr(cp.Data, ce.H));
        }

        uint freed = cp.Number;
        Unpin(cp);
        _src.Free(freed);
        _edits[0] = re;
        Shrunk++;
    }

    /// <summary>Balance events seen by this cursor, by kind (for the interop counters).</summary>
    public int Deepened { get; private set; }

    /// <summary>Number of times the root absorbed its only child.</summary>
    public int Shrunk { get; private set; }

    /// <summary>Number of sibling redistributions.</summary>
    public int Rebalanced { get; private set; }

    /// <summary>Number of pages added by redistributions (splits).</summary>
    public int Splits { get; private set; }

    /// <summary>Number of pages removed by redistributions (merges).</summary>
    public int Merges { get; private set; }

    private void AddGathered(byte[] b, int o, int l)
    {
        if (_gbytes + l > _gbuf.Length)
        {
            Array.Resize(ref _gbuf, Math.Max(_gbytes + l, _gbuf.Length * 2));
        }

        if (_gcount + 1 > _goff.Length)
        {
            Array.Resize(ref _goff, Math.Max(16, _goff.Length * 2));
            Array.Resize(ref _glen, _goff.Length);
        }

        Buffer.BlockCopy(b, o, _gbuf, _gbytes, l);
        _goff[_gcount] = _gbytes;
        _glen[_gcount] = l;
        _gcount++;
        _gbytes += l;
    }

    private int GSize(int i) => Pg.Footprint(_glen[i]) + 2;

    private void BalanceNonRoot(Edit parent, int ci, Edit self)
    {
        int u = _ctx.Usable;
        // A delete that replaces an interior entry by a longer one can leave the parent with an overflow cell while its child is rebalanced
        // (the parent is balanced right after). Its cells are then read by logical position.
        byte[] pd = parent.Pg.Data;
        int pn = parent.Count;
        (int start, int nSib) = Window(pn, ci);
        var sib = new Edit[nSib];
        bool foundSelf = false;
        for (int j = 0; j < nSib; j++)
        {
            int cj = start + j;
            uint pgno;
            if (cj < pn)
            {
                GetLogical(parent, cj, out byte[] pb0, out int po0, out _);
                pgno = Pg.U32(pb0, po0);
            }
            else
            {
                pgno = Pg.RightPtr(pd, parent.H);
            }

            if (pgno == self.Pg.Number)
            {
                sib[j] = self;
                foundSelf = true;
            }
            else
            {
                PageHandle sp = FindPinned(pgno) ?? throw new InvalidOperationException("sibling was not reserved");
                sib[j] = new Edit(sp);
            }

            if (sib[j].Type != self.Type)
            {
                throw OneLiteException.Corrupt(pgno, "sibling pages have different types");
            }
        }

        if (!foundSelf)
        {
            throw OneLiteException.Corrupt(parent.Pg.Number, "child is not where the path says");
        }

        byte type = self.Type;
        bool leaf = Pg.IsLeaf(type);
        bool intkey = Pg.IsIntKey(type);
        _gcount = 0;
        _gbytes = 0;
        uint lastRight = 0;
        for (int s = 0; s < nSib; s++)
        {
            Edit e = sib[s];
            int cnt = e.Count;
            for (int li = 0; li < cnt; li++)
            {
                GetLogical(e, li, out byte[] b, out int o, out int l);
                AddGathered(b, o, l);
            }

            uint rp = leaf ? 0 : Pg.RightPtr(e.Pg.Data, e.H);
            if (s == nSib - 1)
            {
                lastRight = rp;
                break;
            }

            GetLogical(parent, start + s, out byte[] dbuf, out int doff, out int dsize);
            if (intkey && leaf)
            {
                continue;
            }

            if (leaf)
            {
                AddGathered(dbuf, doff + 4, dsize - 4);
            }
            else
            {
                var tmp = new byte[dsize];
                Buffer.BlockCopy(dbuf, doff, tmp, 0, dsize);
                Pg.PutU32(tmp, 0, rp);
                AddGathered(tmp, 0, tmp.Length);
            }
        }

        int n = _gcount;
        int skip = intkey && leaf ? 0 : 1;
        int cap = u - Pg.HeaderSize(type);
        var gs = new List<int>();
        var ge = new List<int>();
        int pos = 0;
        long sizeAll = 0;
        for (int i = 0; i < n; i++)
        {
            sizeAll += GSize(i);
        }

        if (n == 0)
        {
            gs.Add(0);
            ge.Add(0);
        }
        else
        {
            while (true)
            {
                long rem = 0;
                for (int i = pos; i < n; i++)
                {
                    rem += GSize(i);
                }

                if (rem <= cap)
                {
                    gs.Add(pos);
                    ge.Add(n);
                    break;
                }

                int used = 0, e2 = pos, maxEnd = n - 1 - skip;
                while (e2 < maxEnd && used + GSize(e2) <= cap)
                {
                    used += GSize(e2);
                    e2++;
                }

                if (e2 == pos)
                {
                    throw new InvalidOperationException("a cell does not fit an empty page");
                }

                gs.Add(pos);
                ge.Add(e2);
                pos = e2 + skip;
            }
        }

        int k = gs.Count;
        // Even the groups out, right to left, moving whole cells (Comer: redistribute between neighbours).
        for (int pass = 0; pass < 4; pass++)
        {
            bool moved = false;
            for (int g = k - 2; g >= 0; g--)
            {
                while (true)
                {
                    long sl = 0, sr = 0;
                    for (int i = gs[g]; i < ge[g]; i++)
                    {
                        sl += GSize(i);
                    }

                    for (int i = gs[g + 1]; i < ge[g + 1]; i++)
                    {
                        sr += GSize(i);
                    }

                    if (ge[g] - gs[g] <= 1)
                    {
                        break;
                    }

                    // Move the boundary one cell left. With a promoted divider the left group's last cell becomes the divider and the old divider joins the right group.
                    int last = ge[g] - 1;
                    long nl = sl - GSize(last);
                    long nr = sr + GSize(skip == 1 ? ge[g] : last);
                    if (nr > cap || Math.Max(nl, nr) >= Math.Max(sl, sr))
                    {
                        break;
                    }

                    ge[g] = last;
                    gs[g + 1] = last + skip;
                    moved = true;
                }
            }

            if (!moved)
            {
                break;
            }
        }

        // pages for the groups
        var pgs = new PageHandle[k];
        for (int g = 0; g < k; g++)
        {
            if (g < nSib)
            {
                pgs[g] = sib[g].Pg;
            }
            else
            {
                pgs[g] = _src.Allocate();
                _pins.Add(pgs[g]);
            }
        }

        // dividers for the parent, built from the gathered cells before the pages are rewritten
        var div = new byte[Math.Max(0, k - 1)][];
        for (int g = 0; g < k - 1; g++)
        {
            byte[] dc;
            if (intkey && leaf)
            {
                CellInfo lc = _ctx.ParseCell(0, type, _gbuf, _goff[ge[g] - 1], _goff[ge[g] - 1] + _glen[ge[g] - 1]);
                dc = new byte[4 + Varint.Length((ulong)lc.Rowid)];
                Varint.Write(dc.AsSpan(4), (ulong)lc.Rowid);
            }
            else if (leaf)
            {
                dc = new byte[4 + _glen[ge[g]]];
                Buffer.BlockCopy(_gbuf, _goff[ge[g]], dc, 4, _glen[ge[g]]);
            }
            else
            {
                dc = new byte[_glen[ge[g]]];
                Buffer.BlockCopy(_gbuf, _goff[ge[g]], dc, 0, dc.Length);
            }

            Pg.PutU32(dc, 0, pgs[g].Number);
            div[g] = dc;
        }

        var rights = new uint[k];
        for (int g = 0; g < k; g++)
        {
            rights[g] = leaf ? 0 : g < k - 1 ? Pg.U32(_gbuf, _goff[ge[g]]) : lastRight;
        }

        for (int g = 0; g < k; g++)
        {
            PageHandle pg = pgs[g];
            byte[] d = pg.Data;
            Pg.Zero(d, 0, u, type);
            var ne = new Edit(pg);
            for (int i = gs[g]; i < ge[g]; i++)
            {
                InsertCell(ne, i - gs[g], _gbuf, _goff[i], _glen[i]);
            }

            if (ne.Ovfl.Count != 0)
            {
                throw new InvalidOperationException("balance produced an overfull page");
            }

            if (!leaf)
            {
                SetRightPtr(ne, rights[g]);
            }

            if (_src.AutoVacuum)
            {
                _src.PtrmapPut(pg.Number, 5, parent.Pg.Number);
            }
        }

        if (parent.Ovfl.Count != 0)
        {
            ReplaceDividersOfOverflowedParent(parent, start, nSib, pgs[k - 1].Number, div);
            FinishBalance(nSib, k, sib);
            return;
        }

        // parent: replace the dividers of the window
        for (int j = 0; j < nSib - 1; j++)
        {
            DropCell(parent, start);
        }

        if (start < parent.PhysCount)
        {
            _ = _ctx.ReadCell(parent.Pg, parent.H, parent.Type, start, out int co);
            Pg.PutU32(pd, co, pgs[k - 1].Number);
        }
        else
        {
            SetRightPtr(parent, pgs[k - 1].Number);
        }

        for (int g = 0; g < k - 1; g++)
        {
            InsertCell(parent, start + g, div[g], 0, div[g].Length);
        }

        FinishBalance(nSib, k, sib);
    }

    private void FinishBalance(int nSib, int k, Edit[] sib)
    {
        Rebalanced++;
        if (k > nSib)
        {
            Splits += k - nSib;
        }

        for (int g = k; g < nSib; g++)
        {
            Merges++;
            uint pgno = sib[g].Pg.Number;
            Unpin(sib[g].Pg);
            _src.Free(pgno);
        }
    }

    /// <summary>The rare case of <see cref="BalanceNonRoot"/>: the parent holds an overflow cell, so its cells are taken out, edited as a list and put back.</summary>
    private void ReplaceDividersOfOverflowedParent(Edit parent, int start, int nSib, uint lastChild, byte[][] div)
    {
        var cells = new List<byte[]>();
        int count = parent.Count;
        for (int i = 0; i < count; i++)
        {
            GetLogical(parent, i, out byte[] b, out int o, out int l);
            var c = new byte[l];
            Buffer.BlockCopy(b, o, c, 0, l);
            cells.Add(c);
        }

        uint right = Pg.RightPtr(parent.Pg.Data, parent.H);
        cells.RemoveRange(start, nSib - 1);
        if (start < cells.Count)
        {
            Pg.PutU32(cells[start], 0, lastChild);
        }
        else
        {
            right = lastChild;
        }

        for (int g = 0; g < div.Length; g++)
        {
            cells.Insert(start + g, div[g]);
        }

        Pg.Zero(parent.Pg.Data, parent.H, _ctx.Usable, parent.Type);
        parent.Ovfl.Clear();
        for (int i = 0; i < cells.Count; i++)
        {
            InsertCell(parent, i, cells[i], 0, cells[i].Length);
        }

        SetRightPtr(parent, right);
    }
}
