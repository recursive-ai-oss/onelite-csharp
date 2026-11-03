using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Storage.BTree;

/// <summary>Where a cursor stands.</summary>
public enum CursorState : byte
{
    /// <summary>Not positioned (before the first call, after running off either end, or after the tree changed under it with no way to restore).</summary>
    Invalid,

    /// <summary>Positioned on an entry, with its root-to-entry path pinned.</summary>
    Valid,

    /// <summary>Positioned logically on a key, with no pages pinned (the tree was changed by another cursor of the connection). The next call restores the position.</summary>
    Saved,

    /// <summary>An operation failed; <see cref="BtCursor.Error"/> says why. <see cref="BtCursor.Reset"/> clears it.</summary>
    Fault,
}

/// <summary>A B-tree handle: a root page, and for index trees the key ordering.</summary>
public sealed class BTree
{
    private BTree(BtContext ctx, uint root, bool intKey, KeyInfo? key)
    {
        Context = ctx;
        Root = root;
        IntKey = intKey;
        KeyInfo = key;
    }

    /// <summary>The context (page source and geometry).</summary>
    public BtContext Context { get; }

    /// <summary>The root page.</summary>
    public uint Root { get; internal set; }

    /// <summary>True for a rowid table (a B+-tree keyed by a 64-bit rowid); false for an index or WITHOUT ROWID table (a B-tree keyed by records).</summary>
    public bool IntKey { get; }

    /// <summary>How an index tree orders its records; null for a rowid table.</summary>
    public KeyInfo? KeyInfo { get; }

    /// <summary>A rowid table rooted at <paramref name="root"/>.</summary>
    public static BTree Table(BtContext ctx, uint root) => new(ctx, root, true, null);

    /// <summary>An index (or WITHOUT ROWID table) rooted at <paramref name="root"/>.</summary>
    public static BTree Index(BtContext ctx, uint root, KeyInfo key) => new(ctx, root, false, key);

    /// <summary>Opens a cursor on this tree.</summary>
    public BtCursor OpenCursor() => new(this);
}

/// <summary>
/// A cursor over one B-tree. Every operation is a restartable state machine: on <see cref="BtStatus.Pending"/> the caller completes
/// the source's outstanding I/O and repeats the same call with the same arguments. See docs/btree.md for the suspend points.
/// </summary>
public sealed partial class BtCursor : IDisposable
{
    /// <summary>The deepest tree a cursor accepts (fileformat2 limits trees by page count; deeper means a loop or damage).</summary>
    public const int MaxDepth = 20;

    private enum Op : byte { None, Seek, Step, First, Restore }

    private readonly BtContext _ctx;
    private readonly IBtPageSource _src;
    private readonly PageHandle?[] _pages = new PageHandle?[MaxDepth];
    private readonly int[] _idx = new int[MaxDepth];
    private readonly RecordView _rv = new();
    private int _level = -1;
    private Op _op;
    private uint _nextPgno;
    private int _bsLo, _bsHi;
    private bool _bsInit;
    private bool _descendLeft;
    private int _skip;

    /// <summary>
    /// True when <see cref="Restore"/> could not find the saved entry and left the cursor on its neighbour (the next <see cref="Next"/> or
    /// <see cref="Previous"/> must not skip over it). A reader of the row sees no row then, as for a row deleted under a pending statement.
    /// </summary>
    public bool IsOnNeighbour => State == CursorState.Valid && _skip != 0;

    // Payload gathering (overflow chains). Progress survives a Pending.
    private byte[] _scratch = Array.Empty<byte>();
    private bool _gActive, _gDone;
    private uint _gPgno;
    private int _gOff, _gCopied, _gPages;
    private uint _gNext;

    // Saved position.
    private long _savedRowid;
    private byte[] _savedKey = Array.Empty<byte>();
    private int _savedKeyLen;
    private Mem[] _savedMems = Array.Empty<Mem>();
    private bool _savedMemsReady;

    internal BtCursor(BTree tree)
    {
        Tree = tree;
        _ctx = tree.Context;
        _src = _ctx.Source;
        _ctx.Register(this);
    }

    /// <summary>The tree this cursor walks.</summary>
    public BTree Tree { get; }

    /// <summary>Where the cursor stands.</summary>
    public CursorState State { get; internal set; }

    /// <summary>The failure behind <see cref="CursorState.Fault"/> or the last <see cref="BtStatus.Error"/>.</summary>
    public Exception? Error { get; private set; }

    /// <summary>Releases every pinned page and returns the cursor to <see cref="CursorState.Invalid"/>, abandoning any operation in progress.</summary>
    public void Reset()
    {
        TraceReset(TraceOp.Reset);
        ReleasePath();
        _op = Op.None;
        _wop = WOp.None;
        _gActive = false;
        State = CursorState.Invalid;
        Error = null;
        ReleasePins();
    }

    /// <summary>The context the cursor was opened in.</summary>
    internal BtContext Context => _ctx;

    /// <summary>
    /// Puts the cursor away for reuse: pages released, no longer told about changes to the tree. <see cref="Unpark"/> makes it live again. A parked cursor
    /// holds nothing of the database, so it may wait while the connection does other things.
    /// </summary>
    internal void Park()
    {
        Reset();
        _ctx.Unregister(this);
    }

    /// <summary>Registers a parked cursor again.</summary>
    internal void Unpark() => _ctx.Register(this);

    /// <summary>Releases the cursor's pages and unregisters it.</summary>
    public void Dispose()
    {
        TraceReset(TraceOp.Dispose);
        Reset();
        _ctx.Unregister(this);
    }

    internal void Invalidate()
    {
        ReleasePath();
        ReleasePins();
        _op = Op.None;
        _wop = WOp.None;
        State = CursorState.Invalid;
    }

    private void ReleasePath()
    {
        for (int i = _level; i >= 0; i--)
        {
            if (_pages[i] != null)
            {
                _src.Release(_pages[i]!);
                _pages[i] = null;
            }
        }

        _level = -1;
        _gActive = false;
    }

    private BtStatus Fail(Exception e)
    {
        ReleasePath();
        ReleasePins();
        _op = Op.None;
        _wop = WOp.None;
        State = CursorState.Fault;
        Error = e is SourceFailure sf ? sf.Inner : e;
        return BtStatus.Error;
    }

    private BtStatus Get(uint pgno, out PageHandle? pg)
    {
        BtStatus s = _src.GetPage(pgno, out pg);
        if (s == BtStatus.Error)
        {
            throw new SourceFailure(_src.LastError ?? new InvalidOperationException("page source failed"));
        }

        return s;
    }

    /// <summary>Fetches <c>_nextPgno</c> and pushes it on the path. Returns Pending if the page is not available yet.</summary>
    private BtStatus Fetch()
    {
        if (_level + 1 >= MaxDepth)
        {
            throw OneLiteException.Corrupt(_level >= 0 ? _pages[_level]!.Number : Tree.Root, "tree is deeper than the limit (a loop in the child pointers)");
        }

        BtStatus s = Get(_nextPgno, out PageHandle? pg);
        if (s != BtStatus.Ok)
        {
            return s;
        }

        try
        {
            byte type = Pg.CheckHeader(pg!, _ctx.Usable);
            if (Pg.IsIntKey(type) != Tree.IntKey)
            {
                throw OneLiteException.Corrupt(pg!.Number, "page type does not match its tree (table page in an index or the reverse)");
            }
        }
        catch
        {
            _src.Release(pg!);
            throw;
        }

        _pages[++_level] = pg;
        _idx[_level] = 0;
        _nextPgno = 0;
        _bsInit = false;
        return BtStatus.Ok;
    }

    private uint ChildOf(int level, int i)
    {
        PageHandle pg = _pages[level]!;
        int h = Pg.HdrOff(pg.Number);
        byte type = pg.Data[h];
        int n = Pg.NCell(pg.Data, h);
        uint child;
        if (i < n)
        {
            _ = _ctx.ReadCell(pg, h, type, i, out int off);
            child = Pg.U32(pg.Data, off);
        }
        else
        {
            child = Pg.RightPtr(pg.Data, h);
        }

        if (child < 2)
        {
            throw OneLiteException.Corrupt(pg.Number, $"child pointer {child} is not a valid page");
        }

        return child;
    }

    // ---------------------------------------------------------------- seek

    /// <summary>
    /// Positions a rowid-table cursor on <paramref name="rowid"/> or next to it (Seek.Root, Seek.Descend).
    /// </summary>
    /// <param name="rowid">The rowid to find.</param>
    /// <param name="cmp">0 if the cursor is on the rowid; negative if it is on the largest entry smaller than the rowid; positive if on the smallest entry larger. For an empty tree the cursor is <see cref="CursorState.Invalid"/> and <paramref name="cmp"/> is -1.</param>
    /// <returns>The status; on <see cref="BtStatus.Pending"/> repeat the call.</returns>
    public BtStatus MoveToRowid(long rowid, out int cmp)
    {
        cmp = 0;
        if (!Tree.IntKey)
        {
            throw new InvalidOperationException("MoveToRowid on an index tree");
        }

        BtStatus st = SeekGuard(rowid, default, 0, out cmp);
        if (_ctx.Trace is { Active: true } tr)
        {
            TraceSeek(tr, st, rowid, default, 0, cmp);
        }

        return st;
    }

    /// <summary>
    /// Positions an index cursor on the entry equal to <paramref name="key"/> or next to it (Seek.Root, Seek.Descend, Seek.CompareOverflow).
    /// </summary>
    /// <param name="key">The key columns to compare. Pass the same span on every repeat after Pending.</param>
    /// <param name="prefixResult">The result when all key columns equal an entry's: 0 for exact search, -1 to land after all entries with this prefix, 1 to land before them.</param>
    /// <param name="cmp">As for <see cref="MoveToRowid"/>.</param>
    /// <returns>The status.</returns>
    public BtStatus MoveToKey(ReadOnlySpan<Mem> key, int prefixResult, out int cmp)
    {
        cmp = 0;
        if (Tree.IntKey)
        {
            throw new InvalidOperationException("MoveToKey on a rowid table");
        }

        BtStatus st = SeekGuard(0, key, prefixResult, out cmp);
        if (_ctx.Trace is { Active: true } tr)
        {
            TraceSeek(tr, st, 0, key, prefixResult, cmp);
        }

        return st;
    }

    private BtStatus SeekGuard(long rowid, ReadOnlySpan<Mem> key, int prefix, out int cmp)
    {
        cmp = 0;
        try
        {
            if (_op != Op.Seek)
            {
                ReleasePath();
                State = CursorState.Invalid;
                _nextPgno = Tree.Root;
                _op = Op.Seek;
                _skip = 0;
            }

            return SeekCore(rowid, key, prefix, out cmp);
        }
        catch (Exception e) when (e is OneLiteException or SourceFailure)
        {
            return Fail(e);
        }
    }

    private BtStatus SeekCore(long rowid, ReadOnlySpan<Mem> key, int prefix, out int cmp)
    {
        cmp = 0;
        for (; ; )
        {
            if (_nextPgno != 0)
            {
                BtStatus f = Fetch();
                if (f != BtStatus.Ok)
                {
                    return f;
                }
            }

            PageHandle pg = _pages[_level]!;
            byte[] d = pg.Data;
            int h = Pg.HdrOff(pg.Number);
            byte type = d[h];
            int n = Pg.NCell(d, h);
            bool leaf = Pg.IsLeaf(type);
            if (!_bsInit)
            {
                _bsLo = 0;
                _bsHi = n;
                _bsInit = true;
            }

            if (Tree.IntKey)
            {
                while (_bsLo < _bsHi)
                {
                    int mid = (_bsLo + _bsHi) >> 1;
                    CellInfo ci = _ctx.ReadCell(pg, h, type, mid, out _);
                    if (ci.Rowid < rowid)
                    {
                        _bsLo = mid + 1;
                    }
                    else
                    {
                        _bsHi = mid;
                    }
                }

                int i = _bsLo;
                if (!leaf)
                {
                    _idx[_level] = i;
                    _nextPgno = ChildOf(_level, i);
                    continue;
                }

                if (n == 0)
                {
                    return FinishSeek(false, 0, -1, out cmp);
                }

                if (i < n)
                {
                    long at = _ctx.ReadCell(pg, h, type, i, out _).Rowid;
                    return FinishSeek(true, i, at == rowid ? 0 : 1, out cmp);
                }

                return FinishSeek(true, n - 1, -1, out cmp);
            }

            KeyInfo ki = Tree.KeyInfo!;
            while (_bsLo < _bsHi)
            {
                int mid = (_bsLo + _bsHi) >> 1;
                CellInfo ci = _ctx.ReadCell(pg, h, type, mid, out int off);
                BtStatus gs = PayloadOf(pg, ci, off, out byte[] buf, out int boff);
                if (gs != BtStatus.Ok)
                {
                    return gs;
                }

                if (!_rv.Parse(buf, boff, (int)ci.PayloadSize))
                {
                    throw OneLiteException.Corrupt(pg.Number, $"cell {mid} holds a malformed record");
                }

                int c = RecordCompare.Compare(ki, _rv, key, prefix);
                if (c == 0)
                {
                    return FinishSeek(true, mid, 0, out cmp);
                }

                if (c < 0)
                {
                    _bsLo = mid + 1;
                }
                else
                {
                    _bsHi = mid;
                }
            }

            int j = _bsLo;
            if (!leaf)
            {
                _idx[_level] = j;
                _nextPgno = ChildOf(_level, j);
                continue;
            }

            if (n == 0)
            {
                return FinishSeek(false, 0, -1, out cmp);
            }

            return j < n ? FinishSeek(true, j, 1, out cmp) : FinishSeek(true, n - 1, -1, out cmp);
        }
    }

    private BtStatus FinishSeek(bool found, int idx, int c, out int cmp)
    {
        cmp = c;
        _op = Op.None;
        _gActive = false;
        if (!found)
        {
            // An empty tree: the cursor is not on an entry, but the (empty) root stays pinned so an insert can use it.
            State = CursorState.Invalid;
            _idx[_level] = 0;
            return BtStatus.Ok;
        }

        _idx[_level] = idx;
        State = CursorState.Valid;
        return BtStatus.Ok;
    }

    // ---------------------------------------------------------------- payload

    /// <summary>
    /// Gives access to the whole payload of a cell, gathering an overflow chain into the cursor's scratch buffer
    /// if it has one (Overflow.Walk). Restartable: progress is kept in the cursor.
    /// </summary>
    private BtStatus PayloadOf(PageHandle pg, in CellInfo ci, int cellOff, out byte[] buf, out int off)
    {
        if (ci.PayloadSize <= ci.Local)
        {
            buf = pg.Data;
            off = ci.PayloadOff;
            return BtStatus.Ok;
        }

        buf = _scratch;
        off = 0;
        if (_gActive && (_gPgno != pg.Number || _gOff != cellOff))
        {
            _gActive = false;
        }

        if (!_gActive)
        {
            _gActive = true;
            _gDone = false;
            _gPgno = pg.Number;
            _gOff = cellOff;
            EnsureScratch(ci.Local);
            Buffer.BlockCopy(pg.Data, ci.PayloadOff, _scratch, 0, ci.Local);
            _gCopied = ci.Local;
            _gNext = ci.Overflow;
            _gPages = 0;
        }

        while (!_gDone)
        {
            if (_gCopied >= ci.PayloadSize)
            {
                _gDone = true;
                break;
            }

            if (_gNext == 0)
            {
                throw OneLiteException.Corrupt(pg.Number, "overflow chain ends before the payload does");
            }

            if (++_gPages > _src.PageCount)
            {
                throw OneLiteException.Corrupt(pg.Number, "overflow chain is longer than the database (a loop)");
            }

            BtStatus s = Get(_gNext, out PageHandle? opg);
            if (s != BtStatus.Ok)
            {
                _gPages--;
                return s;
            }

            int chunk = (int)Math.Min(_ctx.Usable - 4, ci.PayloadSize - _gCopied);
            EnsureScratch(_gCopied + chunk);
            Buffer.BlockCopy(opg!.Data, 4, _scratch, _gCopied, chunk);
            _gCopied += chunk;
            _gNext = Pg.U32(opg.Data, 0);
            _src.Release(opg);
        }

        buf = _scratch;
        return BtStatus.Ok;
    }

    private void EnsureScratch(int need)
    {
        if (need > _scratch.Length)
        {
            Array.Resize(ref _scratch, Math.Max(need, Math.Min(_scratch.Length * 2, int.MaxValue / 2)));
        }
    }

    private CellInfo CurrentCell(out PageHandle pg, out int off)
    {
        if (State != CursorState.Valid)
        {
            throw new InvalidOperationException("cursor is not on an entry");
        }

        pg = _pages[_level]!;
        int h = Pg.HdrOff(pg.Number);
        return _ctx.ReadCell(pg, h, pg.Data[h], _idx[_level], out off);
    }

    /// <summary>The rowid of the current entry (rowid tables only).</summary>
    public long Rowid
    {
        get
        {
            if (!Tree.IntKey)
            {
                throw new InvalidOperationException("Rowid on an index tree");
            }

            return CurrentCell(out _, out _).Rowid;
        }
    }

    /// <summary>The total payload size of the current entry in bytes.</summary>
    public long PayloadSize => CurrentCell(out _, out _).PayloadSize;

    /// <summary>
    /// Loads the whole payload of the current entry (following its overflow chain). The suspend point is one overflow page.
    /// After <see cref="BtStatus.Ok"/> use <see cref="Payload"/> or <see cref="ParseRecord"/>.
    /// </summary>
    public BtStatus LoadPayload()
    {
        try
        {
            CellInfo ci = CurrentCell(out PageHandle pg, out int off);
            return PayloadOf(pg, ci, off, out _, out _);
        }
        catch (Exception e) when (e is OneLiteException or SourceFailure)
        {
            return Fail(e);
        }
    }

    /// <summary>The payload of the current entry. Valid after <see cref="LoadPayload"/> returned Ok, until the cursor moves or changes the tree. A payload with no overflow is a view of the page itself.</summary>
    public ReadOnlySpan<byte> Payload
    {
        get
        {
            CellInfo ci = CurrentCell(out PageHandle pg, out int off);
            if (ci.PayloadSize <= ci.Local)
            {
                ReadOnlySpan<byte> local = new(pg.Data, ci.PayloadOff, ci.Local);
                TracePayload(local);
                return local;
            }

            if (!_gActive || !_gDone || _gPgno != pg.Number || _gOff != off)
            {
                throw new InvalidOperationException("payload has overflow pages: call LoadPayload first");
            }

            ReadOnlySpan<byte> whole = new(_scratch, 0, (int)ci.PayloadSize);
            TracePayload(whole);
            return whole;
        }
    }

    /// <summary>Parses the current payload as a record into <paramref name="view"/> (no allocation once the view is warm). Returns false if the record is malformed.</summary>
    public bool ParseRecord(RecordView view)
    {
        CellInfo ci = CurrentCell(out PageHandle pg, out int off);
        if (ci.PayloadSize <= ci.Local)
        {
            TracePayload(new ReadOnlySpan<byte>(pg.Data, ci.PayloadOff, ci.Local));
            return view.Parse(pg.Data, ci.PayloadOff, ci.Local);
        }

        if (!_gActive || !_gDone || _gPgno != pg.Number || _gOff != off)
        {
            throw new InvalidOperationException("payload has overflow pages: call LoadPayload first");
        }

        TracePayload(new ReadOnlySpan<byte>(_scratch, 0, (int)ci.PayloadSize));
        return view.Parse(_scratch, 0, (int)ci.PayloadSize);
    }

    // ---------------------------------------------------------------- walking

    /// <summary>Moves to the first entry (Seek.Root, Seek.Descend through leftmost children). The cursor is <see cref="CursorState.Invalid"/> afterwards if the tree is empty.</summary>
    public BtStatus First() => TraceMove(TraceOp.First, Edge(true));

    /// <summary>Moves to the last entry.</summary>
    public BtStatus Last() => TraceMove(TraceOp.Last, Edge(false));

    private BtStatus Edge(bool left)
    {
        try
        {
            if (_op != Op.First)
            {
                ReleasePath();
                State = CursorState.Invalid;
                _nextPgno = Tree.Root;
                _descendLeft = left;
                _op = Op.First;
                _skip = 0;
            }

            BtStatus s = DescendLoop(true);
            if (s == BtStatus.Ok)
            {
                _op = Op.None;
            }

            return s;
        }
        catch (Exception e) when (e is OneLiteException or SourceFailure)
        {
            return Fail(e);
        }
    }

    /// <summary>Walks down from <c>_nextPgno</c> to the leftmost or rightmost leaf entry.</summary>
    private BtStatus DescendLoop(bool rootMayBeEmpty)
    {
        while (_nextPgno != 0)
        {
            BtStatus f = Fetch();
            if (f != BtStatus.Ok)
            {
                return f;
            }

            PageHandle pg = _pages[_level]!;
            int h = Pg.HdrOff(pg.Number);
            byte[] d = pg.Data;
            int n = Pg.NCell(d, h);
            if (Pg.IsLeaf(d[h]))
            {
                if (n == 0)
                {
                    if (_level == 0 && rootMayBeEmpty)
                    {
                        ReleasePath();
                        State = CursorState.Invalid;
                        return BtStatus.Ok;
                    }

                    throw OneLiteException.Corrupt(pg.Number, "empty leaf page below the root");
                }

                _idx[_level] = _descendLeft ? 0 : n - 1;
                State = CursorState.Valid;
                return BtStatus.Ok;
            }

            if (_descendLeft)
            {
                _idx[_level] = 0;
                _nextPgno = ChildOf(_level, 0);
            }
            else
            {
                _idx[_level] = n;
                _nextPgno = ChildOf(_level, n);
            }
        }

        State = CursorState.Valid;
        return BtStatus.Ok;
    }

    /// <summary>Moves to the next entry in key order (Step.Restore, Step.Leaf, Step.Ascend, Step.Descend, Step.VisitInterior). Afterwards <see cref="State"/> is <see cref="CursorState.Invalid"/> if there was none.</summary>
    public BtStatus Next() => TraceMove(TraceOp.Next, Step(true));

    /// <summary>Moves to the previous entry in key order.</summary>
    public BtStatus Previous() => TraceMove(TraceOp.Previous, Step(false));

    private BtStatus Step(bool forward)
    {
        try
        {
            if (_op != Op.Step)
            {
                if (State == CursorState.Saved)
                {
                    BtStatus r = RestoreCore();
                    if (r != BtStatus.Ok)
                    {
                        return r;
                    }

                    int sk = _skip;
                    _skip = 0;
                    if (State != CursorState.Valid || (forward && sk > 0) || (!forward && sk < 0))
                    {
                        return BtStatus.Ok;
                    }
                }

                if (State == CursorState.Fault)
                {
                    return BtStatus.Error;
                }

                if (State != CursorState.Valid)
                {
                    return BtStatus.Ok;
                }

                _gActive = false;
                _op = Op.Step;
                _nextPgno = 0;
                if (!StepLocal(forward))
                {
                    _op = Op.None;
                    return BtStatus.Ok;
                }
            }

            BtStatus s = DescendLoop(false);
            if (s == BtStatus.Ok)
            {
                _op = Op.None;
            }

            return s;
        }
        catch (Exception e) when (e is OneLiteException or SourceFailure)
        {
            return Fail(e);
        }
    }

    /// <summary>The request-free part of a step. Returns true if a descent into another subtree is needed (<c>_nextPgno</c> set).</summary>
    private bool StepLocal(bool forward)
    {
        PageHandle pg = _pages[_level]!;
        int h = Pg.HdrOff(pg.Number);
        byte[] d = pg.Data;
        bool leaf = Pg.IsLeaf(d[h]);
        int n = Pg.NCell(d, h);
        if (forward)
        {
            if (!leaf)
            {
                // Standing on an index entry in an interior page: the next entry is the leftmost of the subtree to its right.
                _idx[_level]++;
                _nextPgno = ChildOf(_level, _idx[_level]);
                _descendLeft = true;
                return true;
            }

            if (_idx[_level] + 1 < n)
            {
                _idx[_level]++;
                return false;
            }

            while (true)
            {
                if (_level == 0)
                {
                    ReleasePath();
                    State = CursorState.Invalid;
                    return false;
                }

                _src.Release(_pages[_level]!);
                _pages[_level--] = null;
                PageHandle up = _pages[_level]!;
                int uh = Pg.HdrOff(up.Number);
                int c = _idx[_level];
                int un = Pg.NCell(up.Data, uh);
                if (c < un)
                {
                    if (Tree.IntKey)
                    {
                        _idx[_level] = c + 1;
                        _nextPgno = ChildOf(_level, c + 1);
                        _descendLeft = true;
                        return true;
                    }

                    return false; // standing on interior entry c
                }
            }
        }

        if (!leaf)
        {
            // Standing on an index entry in an interior page: the previous entry is the rightmost of its left subtree.
            _nextPgno = ChildOf(_level, _idx[_level]);
            _descendLeft = false;
            return true;
        }

        if (_idx[_level] > 0)
        {
            _idx[_level]--;
            return false;
        }

        while (true)
        {
            if (_level == 0)
            {
                ReleasePath();
                State = CursorState.Invalid;
                return false;
            }

            _src.Release(_pages[_level]!);
            _pages[_level--] = null;
            int c = _idx[_level];
            if (c > 0)
            {
                if (Tree.IntKey)
                {
                    _idx[_level] = c - 1;
                    _nextPgno = ChildOf(_level, c - 1);
                    _descendLeft = false;
                    return true;
                }

                _idx[_level] = c - 1; // standing on interior entry c-1
                return false;
            }
        }
    }

    // ---------------------------------------------------------------- saved positions

    /// <summary>
    /// Converts a valid position to a key and unpins the path (spec §5.6). Request-free: for an index entry whose key has overflow pages,
    /// <see cref="LoadPayload"/> must have completed first (the writers do this in their reserve phase).
    /// </summary>
    internal void Save()
    {
        if (State != CursorState.Valid)
        {
            return;
        }

        CellInfo ci = CurrentCell(out PageHandle pg, out int off);
        if (Tree.IntKey)
        {
            _savedRowid = ci.Rowid;
        }
        else
        {
            ReadOnlySpan<byte> p;
            if (ci.PayloadSize <= ci.Local)
            {
                p = new ReadOnlySpan<byte>(pg.Data, ci.PayloadOff, ci.Local);
            }
            else
            {
                if (!_gActive || !_gDone || _gPgno != pg.Number || _gOff != off)
                {
                    throw new InvalidOperationException("Save of an index entry with overflow needs its payload loaded first");
                }

                p = new ReadOnlySpan<byte>(_scratch, 0, (int)ci.PayloadSize);
            }

            if (_savedKey.Length < p.Length)
            {
                _savedKey = new byte[p.Length];
            }

            p.CopyTo(_savedKey);
            _savedKeyLen = p.Length;
            _savedMemsReady = false;
        }

        ReleasePath();
        State = CursorState.Saved;
    }

    /// <summary>True when <see cref="Save"/> would need to read overflow pages first.</summary>
    internal bool NeedsPayloadForSave
    {
        get
        {
            if (State != CursorState.Valid || Tree.IntKey)
            {
                return false;
            }

            CellInfo ci = CurrentCell(out PageHandle pg, out int off);
            return ci.PayloadSize > ci.Local && !(_gActive && _gDone && _gPgno == pg.Number && _gOff == off);
        }
    }

    private BtStatus RestoreCore()
    {
        if (_op != Op.Restore)
        {
            _op = Op.Restore;
            ReleasePath();
            _nextPgno = Tree.Root;
            _bsInit = false;
            _savedMemsReady = Tree.IntKey || _savedMemsReady;
        }

        if (!Tree.IntKey && !_savedMemsReady)
        {
            var rv = new RecordView();
            if (!rv.Parse(_savedKey, 0, _savedKeyLen))
            {
                throw OneLiteException.Corrupt(Tree.Root, "saved key is not a record");
            }

            if (_savedMems.Length < rv.ColumnCount)
            {
                _savedMems = new Mem[rv.ColumnCount];
            }

            for (int i = 0; i < rv.ColumnCount; i++)
            {
                _savedMems[i] = default;
                rv.Load(i, ref _savedMems[i]);
            }

            _savedMemsLen = rv.ColumnCount;
            _savedMemsReady = true;
        }

        BtStatus s = Tree.IntKey
            ? SeekCore(_savedRowid, default, 0, out int cmp)
            : SeekCore(0, new ReadOnlySpan<Mem>(_savedMems, 0, _savedMemsLen), 0, out cmp);
        if (s != BtStatus.Ok)
        {
            return s;
        }

        _op = Op.None;
        _skip = State == CursorState.Valid ? cmp : 0;
        return BtStatus.Ok;
    }

    private int _savedMemsLen;

    /// <summary>
    /// Brings a <see cref="CursorState.Saved"/> cursor back to a position (the entry itself, or the neighbour the next
    /// <see cref="Next"/> or <see cref="Previous"/> must not skip over). A no-op for other states.
    /// </summary>
    public BtStatus Restore()
    {
        if (State != CursorState.Saved)
        {
            return BtStatus.Ok;
        }

        try
        {
            return RestoreCore();
        }
        catch (Exception e) when (e is OneLiteException or SourceFailure)
        {
            return Fail(e);
        }
    }
}

/// <summary>Carries a failure reported by the page source through the cursor's code paths; the cursor reports the inner exception.</summary>
internal sealed class SourceFailure : Exception
{
    public SourceFailure(Exception inner) : base(inner.Message, inner)
    {
        Inner = inner;
    }

    public Exception Inner { get; }
}
