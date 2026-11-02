using System.Buffers.Binary;

namespace RecursiveAI.OneLite.Storage.Pager;

/// <summary>Kinds of pointer-map entries (fileformat2 §1.7).</summary>
public enum PtrmapType : byte
{
    /// <summary>A B-tree root page (parent is 0).</summary>
    RootPage = 1,

    /// <summary>A freelist page (parent is 0).</summary>
    FreePage = 2,

    /// <summary>The first page of an overflow chain; the parent is the B-tree page holding the cell.</summary>
    Overflow1 = 3,

    /// <summary>A later page of an overflow chain; the parent is the previous overflow page.</summary>
    Overflow2 = 4,

    /// <summary>A non-root B-tree page; the parent is its parent page.</summary>
    BTree = 5,
}

public sealed partial class Pager
{
    private int _alStep;
    private int _fpStep;
    private int _pmStep;
    private int _rpStep;
    private PageFrame? _alP1;
    private PageFrame? _alTrunk;
    private PageFrame? _alOut;
    private PageFrame? _fpFrame;
    private PageFrame? _pmFrame;
    private uint _alPage;
    private uint _rpTrunk;
    private long _rpWant;
    private int _rpTrunks;
    private int _rpExtra;
    private int _rpPmIdx;
    private uint _rpTrunkPage;
    private readonly List<uint> _rpPm = [];
    private int _rmStep;
    private int _rjStep;
    private bool _ajOpenOnly;
    private bool _reserving;

    // What the reserve calls of the current phase have promised: journal records that will fit in the buffer without a flush, and free frames
    // so that no eviction (and so no read or spill) is needed. The phase ends, for these counters, when an allocation, free or pointer-map write
    // completes; the next reserve call starts counting again.
    private int _rsvRecords;
    private int _rsvFrames;
    private bool _rsvStale;

    private void UnpinTemps()
    {
        foreach (PageFrame? f in new[] { _alP1, _alTrunk, _alOut, _fpFrame, _pmFrame })
        {
            if (f != null && f.Pins > 0)
            {
                f.Pins--;
            }
        }

        _alP1 = _alTrunk = _alOut = _fpFrame = _pmFrame = null;
    }

    // The caller's effect phase is over: the next reserve call counts from zero. Without it, operations that allocate and free nothing would pile
    // up promises (and cached spare frames) for the whole transaction.
    internal void EndReservePhase() => _rsvStale = true;

    private int MaxTrunkLeaves => UsableSize / 4 - 2;

    // The pager picks the next page number for file growth, creating pointer-map pages that fall on the way (fileformat2 §1.7).
    private PagerStatus ExtendOne(out PageFrame? frame)
    {
        frame = null;
        for (; ; )
        {
            uint next = _dbSize + 1;
            if (next == LockBytePage)
            {
                // fileformat2 §1.3: the lock-byte page is counted but never used; it stays a hole in the file.
                _dbSize = next;
                continue;
            }

            if (IsPtrmapPage(next))
            {
                _dbSize = next;
                PagerStatus ps = GetCore(next, GetOptions.NoContent, out PageFrame? pmap);
                if (ps != PagerStatus.Ok)
                {
                    _dbSize = next - 1;
                    return ps;
                }

                PagerStatus pw = MakeWritable(pmap!);
                if (pw != PagerStatus.Ok)
                {
                    pmap!.Pins--;
                    _dbSize = next - 1;
                    return pw;
                }

                pmap!.Pins--;
                continue;
            }

            _dbSize = next;
            PagerStatus s = GetCore(next, GetOptions.NoContent, out PageFrame? f);
            if (s != PagerStatus.Ok)
            {
                _dbSize = next - 1;
                return s;
            }

            Array.Clear(f!.Data);
            PagerStatus w = MakeWritable(f);
            if (w != PagerStatus.Ok)
            {
                f.Pins--;
                _dbSize = next - 1;
                return w;
            }

            frame = f;
            return PagerStatus.Ok;
        }
    }

    /// <summary>
    /// Returns a zeroed, writable, pinned page. It comes from the freelist (a leaf first, then an emptied trunk; fileformat2 §1.5), or the
    /// file grows by one page, skipping the lock-byte page and creating pointer-map pages on the way. Re-entrant. A page taken from the
    /// freelist that was a leaf when the transaction began is not journaled (it carries no information to restore).
    /// </summary>
    /// <param name="frame">The new page, pinned, when the result is Ok.</param>
    /// <returns>The status.</returns>
    public PagerStatus Allocate(out PageFrame? frame)
    {
        frame = null;
        Guard();
        RequireWrite();
        for (; ; )
        {
            switch (_alStep)
            {
                case 0:
                {
                    PagerStatus s = GetCore(1, GetOptions.None, out PageFrame? p1);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _alP1 = p1;
                    uint trunk = DatabaseHeader.GetUInt32(p1!.Data, DatabaseHeader.FreelistTrunkOffset);
                    if (trunk == 0)
                    {
                        _alStep = 10;
                    }
                    else
                    {
                        _alPage = trunk;
                        _alStep = 1;
                    }

                    break;
                }

                case 10:
                {
                    PagerStatus s = ExtendOne(out PageFrame? f);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _alP1!.Pins--;
                    _alP1 = null;
                    _alStep = 0;
                    _rsvStale = true;
                    frame = f;
                    return PagerStatus.Ok;
                }

                case 1:
                {
                    PagerStatus s = GetCore(_alPage, GetOptions.None, out PageFrame? t);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _alTrunk = t;
                    uint leaves = Be32(t!.Data.AsSpan(4));
                    if (_alPage < 2 || leaves > (uint)MaxTrunkLeaves)
                    {
                        return FailCorrupt($"freelist trunk page {_alPage} lists {leaves} leaves (at most {MaxTrunkLeaves})");
                    }

                    _alStep = 2;
                    break;
                }

                case 2:
                {
                    PagerStatus s = MakeWritable(_alTrunk!);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _alStep = 3;
                    break;
                }

                case 3:
                {
                    PagerStatus s = MakeWritable(_alP1!);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    Span<byte> t = _alTrunk!.Data;
                    Span<byte> h = _alP1!.Data;
                    uint leaves = BinaryPrimitives.ReadUInt32BigEndian(t[4..]);
                    uint total = DatabaseHeader.GetUInt32(h, DatabaseHeader.FreelistCountOffset);
                    if (total == 0)
                    {
                        return FailCorrupt("the freelist is non-empty but the header says it has no pages");
                    }

                    DatabaseHeader.SetUInt32(h, DatabaseHeader.FreelistCountOffset, total - 1);
                    if (leaves > 0)
                    {
                        // Take the last leaf.
                        uint leaf = BinaryPrimitives.ReadUInt32BigEndian(t[(8 + 4 * (int)(leaves - 1))..]);
                        if (leaf < 2 || leaf > _dbSize || leaf == LockBytePage || leaf == _alPage)
                        {
                            return FailCorrupt($"freelist trunk page {_alPage} lists page {leaf}, which is not a page of the database");
                        }

                        BinaryPrimitives.WriteUInt32BigEndian(t[4..], leaves - 1);
                        _alPage = leaf;
                        _alStep = 4;
                    }
                    else
                    {
                        // An emptied trunk: the next trunk becomes the head and this page is the one allocated.
                        DatabaseHeader.SetUInt32(h, DatabaseHeader.FreelistTrunkOffset, BinaryPrimitives.ReadUInt32BigEndian(t));
                        _alOut = _alTrunk;
                        _alTrunk = null;
                        _alStep = 6;
                    }

                    break;
                }

                case 4:
                {
                    // A leaf from the freelist: no content is read for it; and if it was a leaf when the transaction began, nothing is journaled.
                    if (_alPage <= _origSize && !_jBits.Get(_alPage))
                    {
                        _jBits.Set(_alPage);
                    }

                    PagerStatus s = GetCore(_alPage, GetOptions.NoContent, out PageFrame? f);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _alOut = f;
                    _alStep = 6;
                    break;
                }

                default:
                {
                    PageFrame f = _alOut!;
                    PagerStatus s = MakeWritable(f);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    Array.Clear(f.Data);

                    // The pins on page 1 and the trunk are released; the allocated page's pin goes to the caller.
                    _alP1!.Pins--;
                    if (_alTrunk != null)
                    {
                        _alTrunk.Pins--;
                    }

                    _alP1 = null;
                    _alTrunk = null;
                    _alOut = null;
                    _alStep = 0;
                    _rsvStale = true;
                    frame = f;
                    return PagerStatus.Ok;
                }
            }
        }
    }

    /// <summary>
    /// Puts a page on the freelist (fileformat2 §1.5): as a leaf of the first trunk, or, if there is no trunk or it is full, as the new first
    /// trunk. The page's content is saved in the journal first if it is part of the original database. The caller must not use the page
    /// afterwards, and must have released its own pin on it.
    /// </summary>
    /// <param name="pageNumber">The page to free (2 or more).</param>
    /// <returns>The status.</returns>
    public PagerStatus Free(uint pageNumber)
    {
        Guard();
        RequireWrite();
        for (; ; )
        {
            switch (_fpStep)
            {
                case 0:
                {
                    PagerStatus s = GetCore(pageNumber, GetOptions.None, out PageFrame? f);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    if (pageNumber < 2)
                    {
                        f!.Pins--;
                        return FailCorrupt("page 1 cannot be freed");
                    }

                    _fpFrame = f;
                    _fpStep = 1;
                    break;
                }

                case 1:
                {
                    // Its current content is the original (or it is already journaled); keep it for rollback and for savepoints before the page is reused.
                    PagerStatus s = EnsureJournaled(_fpFrame!);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    SaveForSavepoint(_fpFrame!);
                    _fpStep = 2;
                    break;
                }

                case 2:
                {
                    PagerStatus s = GetCore(1, GetOptions.None, out PageFrame? p1);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _alP1 = p1;
                    _alPage = DatabaseHeader.GetUInt32(p1!.Data, DatabaseHeader.FreelistTrunkOffset);
                    _fpStep = _alPage == 0 ? 5 : 3;
                    break;
                }

                case 3:
                {
                    PagerStatus s = GetCore(_alPage, GetOptions.None, out PageFrame? t);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _alTrunk = t;
                    uint leaves = Be32(t!.Data.AsSpan(4));
                    if (leaves > (uint)MaxTrunkLeaves)
                    {
                        return FailCorrupt($"freelist trunk page {_alPage} lists {leaves} leaves (at most {MaxTrunkLeaves})");
                    }

                    // Room in the first trunk: add a leaf; otherwise the freed page itself becomes the new first trunk.
                    _fpStep = leaves < (uint)MaxTrunkLeaves ? 4 : 5;
                    break;
                }

                case 4:
                {
                    PagerStatus s = MakeWritable(_alTrunk!);
                    if (s == PagerStatus.Ok)
                    {
                        s = MakeWritable(_alP1!);
                    }

                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    Span<byte> t = _alTrunk!.Data;
                    uint leaves = BinaryPrimitives.ReadUInt32BigEndian(t[4..]);
                    BinaryPrimitives.WriteUInt32BigEndian(t[(8 + 4 * (int)leaves)..], pageNumber);
                    BinaryPrimitives.WriteUInt32BigEndian(t[4..], leaves + 1);
                    AddFreeCount(+1);
                    return FinishFree();
                }

                default:
                {
                    PagerStatus s = MakeWritable(_fpFrame!);
                    if (s == PagerStatus.Ok)
                    {
                        s = MakeWritable(_alP1!);
                    }

                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    Span<byte> h = _alP1!.Data;
                    Array.Clear(_fpFrame!.Data);
                    BinaryPrimitives.WriteUInt32BigEndian(_fpFrame.Data, DatabaseHeader.GetUInt32(h, DatabaseHeader.FreelistTrunkOffset));
                    DatabaseHeader.SetUInt32(h, DatabaseHeader.FreelistTrunkOffset, pageNumber);
                    AddFreeCount(+1);
                    return FinishFree();
                }
            }
        }
    }

    private void AddFreeCount(int delta)
    {
        Span<byte> h = _alP1!.Data;
        DatabaseHeader.SetUInt32(h, DatabaseHeader.FreelistCountOffset, unchecked(DatabaseHeader.GetUInt32(h, DatabaseHeader.FreelistCountOffset) + (uint)delta));
    }

    private PagerStatus FinishFree()
    {
        _fpFrame!.Pins--;
        _alP1!.Pins--;
        if (_alTrunk != null)
        {
            _alTrunk.Pins--;
        }

        _fpFrame = _alP1 = _alTrunk = null;
        _fpStep = 0;
        _rsvStale = true;
        return PagerStatus.Ok;
    }

    private void RequireWrite()
    {
        if (_state != TxState.Write)
        {
            throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "this call needs a write transaction");
        }
    }

    /// <summary>
    /// The prefetch half of "reserve, then effect" (the pager's suspend-point rules). When this returns <see cref="PagerStatus.Ok"/>, the next
    /// <paramref name="allocations"/> calls to <see cref="Allocate"/> and <paramref name="frees"/> calls to <see cref="Free"/> return Ok without
    /// ever returning <see cref="PagerStatus.Pending"/>, as does <see cref="SetLargestRootPage"/>. Making that true:
    /// <list type="bullet">
    /// <item>page 1 and every freelist trunk those calls can touch (the trunks that cover the allocations, plus the one after them that a free would
    /// then use) are loaded and stay cached, because while reserving the cache grows past its soft limit instead of evicting;</item>
    /// <item>enough free frames are set aside for the pages to be allocated (and the pointer-map pages the file may grow by), so the effect phase
    /// evicts nothing, reads nothing and cannot spill;</item>
    /// <item>the rollback journal is opened if the transaction will need it, and the journal buffer has room for every record the calls can add, so
    /// saving original pages never has to write.</item>
    /// </list>
    /// Not covered: a page passed to <see cref="Free"/> must already be in the cache (the caller has just read or written it; it keeps it there
    /// by holding a pin until the reserve call returns), or have been saved in the journal already. Pointer-map entries need
    /// <see cref="ReservePtrmap"/> for each page whose entry will be read or written. Calls made in the same phase add up. Each call that
    /// completes is a new phase for the next reserve.
    /// </summary>
    /// <param name="allocations">How many pages will be allocated.</param>
    /// <param name="frees">How many pages will be freed.</param>
    /// <returns>The status; re-enter with the same arguments after <see cref="PagerStatus.Pending"/>.</returns>
    /// <exception cref="OneLiteException"><c>SQLITE_MISUSE</c> outside a write transaction; <c>SQLITE_CORRUPT</c> if the freelist is damaged.</exception>
    public PagerStatus ReservePages(int allocations, int frees = 0)
    {
        Guard();
        RequireWrite();
        ArgumentOutOfRangeException.ThrowIfNegative(allocations);
        ArgumentOutOfRangeException.ThrowIfNegative(frees);
        _reserving = true;
        try
        {
            return ReservePagesCore(allocations, frees);
        }
        finally
        {
            _reserving = false;
        }
    }

    private PagerStatus ReservePagesCore(int allocations, int frees)
    {
        for (; ; )
        {
            switch (_rpStep)
            {
                case 0:
                {
                    PagerStatus s = GetCore(1, GetOptions.None, out PageFrame? p1);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    if (_rsvStale)
                    {
                        _rsvRecords = 0;
                        _rsvFrames = 0;
                        _rsvStale = false;
                    }

                    _rpTrunk = DatabaseHeader.GetUInt32(p1!.Data, DatabaseHeader.FreelistTrunkOffset);
                    _rpWant = allocations;
                    _rpExtra = frees > 0 ? 1 : 0;
                    _rpTrunks = 0;
                    _rpPmIdx = 0;
                    _rpPm.Clear();
                    p1.Pins--;
                    _rpStep = 1;
                    break;
                }

                case 1:
                {
                    // Walk the trunks the allocations would use up, then one more for the frees that follow.
                    if (_rpTrunk == 0 || (_rpWant <= 0 && _rpExtra <= 0))
                    {
                        _rpStep = 2;
                        break;
                    }

                    if (_rpTrunks > _dbSize)
                    {
                        return FailCorrupt("the freelist trunk chain does not end");
                    }

                    PagerStatus s = GetCore(_rpTrunk, GetOptions.None, out PageFrame? t);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _rpTrunkPage = _rpTrunk;
                    uint leaves = Be32(t!.Data.AsSpan(4));
                    if (leaves > (uint)MaxTrunkLeaves)
                    {
                        t.Pins--;
                        return FailCorrupt($"freelist trunk page {_rpTrunk} lists {leaves} leaves (at most {MaxTrunkLeaves})");
                    }

                    if (_rpWant > 0)
                    {
                        _rpWant -= (long)leaves + 1;
                    }
                    else
                    {
                        _rpExtra--;
                    }

                    if (_autoVacuum)
                    {
                        // Allocating a page means writing its pointer-map entry, so the pointer-map pages of every page these trunks can give out are needed too.
                        NotePtrmapPage(_rpTrunkPage);
                        for (uint i = 0; i < leaves; i++)
                        {
                            NotePtrmapPage(Be32(t.Data.AsSpan(8 + 4 * (int)i)));
                        }
                    }

                    _rpTrunks++;
                    _rpTrunkPage = 0;
                    _rpTrunk = Be32(t.Data);
                    t.Pins--;
                    break;
                }

                case 2:
                {
                    if (_autoVacuum && _rpWant > 0)
                    {
                        // What the freelist cannot supply comes from growing the file; its pointer-map pages must be cached too (new ones are created in memory).
                        long n = Math.Min(_rpWant, 1L << 20);
                        for (long i = 1; i <= n; i++)
                        {
                            NotePtrmapPage((uint)Math.Min(uint.MaxValue, _dbSize + i));
                        }
                    }

                    // Records the effect can add: page 1, each trunk, the freed pages, a pointer-map page per page, and some margin.
                    long records = 1 + _rpTrunks + _rpPm.Count + 2L * ((long)allocations + frees) + 2;
                    long grow = _autoVacuum ? (allocations / Math.Max(1, UsableSize / 5)) + 2 : 0;
                    _rsvRecords = (int)Math.Min(int.MaxValue / 2, _rsvRecords + records);
                    _rsvFrames = (int)Math.Min(int.MaxValue / 2, _rsvFrames + allocations + grow);
                    _rpStep = 3;
                    break;
                }

                case 3:
                {
                    while (_rpPmIdx < _rpPm.Count)
                    {
                        PagerStatus s = GetCore(_rpPm[_rpPmIdx], GetOptions.None, out PageFrame? f);
                        if (s != PagerStatus.Ok)
                        {
                            return s;
                        }

                        f!.Pins--;
                        _rpPmIdx++;
                    }

                    _rpStep = 4;
                    break;
                }

                case 4:
                {
                    PagerStatus s = ReserveJournal();
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    while (_freeFrames.Count < _rsvFrames)
                    {
                        _freeFrames.Push(NewFrame());
                    }

                    _rpStep = 0;
                    return PagerStatus.Ok;
                }

                default:
                    throw new InvalidOperationException("bad reserve step");
            }
        }
    }

    // Remembers the pointer-map page of a page, if the file has it already (a pointer-map page the file will grow to is created in memory).
    private void NotePtrmapPage(uint page)
    {
        if (page < 3 || IsPtrmapPage(page))
        {
            return;
        }

        uint map = PtrmapPageOf(page);
        if (map <= _dbSize && (_rpPm.Count == 0 || _rpPm[^1] != map) && !_rpPm.Contains(map))
        {
            _rpPm.Add(map);
        }
    }

    /// <summary>
    /// Makes the journal ready for the records the reserve calls of this phase promised: opens it if this transaction will journal pages, and
    /// leaves room in the buffer (flushing it, or growing it, which are the only things that can write).
    /// </summary>
    private PagerStatus ReserveJournal()
    {
        for (; ; )
        {
            switch (_rjStep)
            {
                case 0:
                    // a WAL database has no rollback journal to reserve for
                    if (_wal != null || _journalMode is JournalMode.Memory or JournalMode.Off || _origSize == 0)
                    {
                        return PagerStatus.Ok;
                    }

                    _rjStep = 1;
                    break;
                case 1:
                    if (_jfile == null)
                    {
                        _ajOpenOnly = true;
                        PagerStatus s = AppendJournalRecord(null!);
                        if (s != PagerStatus.Ok)
                        {
                            return s;
                        }
                    }

                    _rjStep = 2;
                    break;
                case 2:
                {
                    long need = (long)_rsvRecords * RecordSize + 3L * _jSector;
                    if (_jbuf.Length - _jbufLen < need)
                    {
                        PagerStatus s = FlushJournalBuffer();
                        if (s != PagerStatus.Ok)
                        {
                            return s;
                        }

                        if (_jbuf.Length < need)
                        {
                            _jbuf = new byte[(int)Math.Min(int.MaxValue / 2, need)];
                        }
                    }

                    _rjStep = 0;
                    return PagerStatus.Ok;
                }

                default:
                    throw new InvalidOperationException("bad journal reserve step");
            }
        }
    }

    /// <summary>
    /// Makes the pointer-map page that holds the entry of <paramref name="pageNumber"/> available, so that <see cref="PtrmapGet"/> and
    /// <see cref="PtrmapPut"/> for that page return Ok without <see cref="PagerStatus.Pending"/> (see <see cref="ReservePages"/>). A page
    /// whose entry lives on a pointer-map page the file does not have yet (it will be created when the file grows) needs nothing. A no-op
    /// in a database without auto-vacuum and for pages that have no entry.
    /// </summary>
    /// <param name="pageNumber">The page whose pointer-map entry will be read or written.</param>
    /// <returns>The status; re-enter with the same argument after <see cref="PagerStatus.Pending"/>.</returns>
    public PagerStatus ReservePtrmap(uint pageNumber)
    {
        Guard();
        RequireWrite();
        if (!_autoVacuum || pageNumber < 3 || IsPtrmapPage(pageNumber))
        {
            return PagerStatus.Ok;
        }

        _reserving = true;
        try
        {
            for (; ; )
            {
                switch (_rmStep)
                {
                    case 0:
                    {
                        if (_rsvStale)
                        {
                            _rsvRecords = 0;
                            _rsvFrames = 0;
                            _rsvStale = false;
                        }

                        uint pmap = PtrmapPageOf(pageNumber);
                        if (pmap <= _dbSize)
                        {
                            PagerStatus s = GetCore(pmap, GetOptions.None, out PageFrame? f);
                            if (s != PagerStatus.Ok)
                            {
                                return s;
                            }

                            f!.Pins--;
                        }

                        _rsvRecords = Math.Min(int.MaxValue / 2, _rsvRecords + 1);
                        _rmStep = 1;
                        break;
                    }

                    default:
                    {
                        PagerStatus s = ReserveJournal();
                        if (s != PagerStatus.Ok)
                        {
                            return s;
                        }

                        while (_freeFrames.Count < _rsvFrames)
                        {
                            _freeFrames.Push(NewFrame());
                        }

                        _rmStep = 0;
                        return PagerStatus.Ok;
                    }
                }
            }
        }
        finally
        {
            _reserving = false;
        }
    }

    // ---- the header's largest-root-page field ------------------------------------------------------------------------------------------

    /// <summary>
    /// The largest root B-tree page of an auto-vacuum database (header offset 52; fileformat2 §1.3.11), or 0 when the database is not
    /// auto-vacuum. A new root page must be numbered above it. Valid after <see cref="BeginRead"/>; reading it never touches storage.
    /// </summary>
    public uint LargestRootPage => _cache.TryGetValue(1, out PageFrame? p1) ? DatabaseHeader.GetUInt32(p1.Data, DatabaseHeader.LargestRootOffset) : _largestRoot;

    /// <summary>
    /// Writes the largest-root-page field. Only an auto-vacuum database has one: the call is a misuse on any other (a non-zero value would
    /// turn the database into an auto-vacuum one, which needs pointer-map pages). Needs page 1 in the cache and writable, which
    /// <see cref="ReservePages"/> arranges, or the call returns <see cref="PagerStatus.Pending"/> like any page access.
    /// </summary>
    /// <param name="page">The new value, 1 or more.</param>
    /// <returns>The status.</returns>
    /// <exception cref="OneLiteException"><c>SQLITE_MISUSE</c> outside a write transaction, on a database that is not auto-vacuum, or for 0.</exception>
    public PagerStatus SetLargestRootPage(uint page)
    {
        Guard();
        RequireWrite();
        if (!_autoVacuum || page == 0)
        {
            throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "the largest root page is only set on an auto-vacuum database, and is never 0");
        }

        PagerStatus s = GetCore(1, GetOptions.None, out PageFrame? p1);
        if (s != PagerStatus.Ok)
        {
            return s;
        }

        PagerStatus w = MakeWritable(p1!);
        if (w != PagerStatus.Ok)
        {
            p1!.Pins--;
            return w;
        }

        DatabaseHeader.SetUInt32(p1!.Data, DatabaseHeader.LargestRootOffset, page);
        p1.Pins--;
        _largestRoot = page;
        return PagerStatus.Ok;
    }

    // ---- pointer map (fileformat2 §1.7) --------------------------------------------------------------------------------------------

    /// <summary>True if <paramref name="pageNumber"/> is a pointer-map page of this auto-vacuum database.</summary>
    /// <param name="pageNumber">The page.</param>
    /// <returns>True for pointer-map pages.</returns>
    public bool IsPtrmapPage(uint pageNumber)
    {
        if (!_autoVacuum || pageNumber < 2)
        {
            return false;
        }

        return PtrmapPageOf(pageNumber) == pageNumber;
    }

    /// <summary>The pointer-map page that holds the entry for <paramref name="pageNumber"/> (or is that page itself).</summary>
    /// <param name="pageNumber">A page of an auto-vacuum database (2 or more).</param>
    /// <returns>The pointer-map page number.</returns>
    public uint PtrmapPageOf(uint pageNumber)
    {
        // fileformat2 §1.7: page 2 is the first pointer-map page; each holds usable/5 five-byte entries and covers the pages after it.
        uint perPage = (uint)(UsableSize / 5);
        return ((pageNumber - 2) / (perPage + 1)) * (perPage + 1) + 2;
    }

    /// <summary>Reads the pointer-map entry of a page.</summary>
    /// <param name="pageNumber">The page (3 or more, not itself a pointer-map page).</param>
    /// <param name="type">The entry type.</param>
    /// <param name="parent">The parent page number.</param>
    /// <returns>The status.</returns>
    public PagerStatus PtrmapGet(uint pageNumber, out PtrmapType type, out uint parent)
    {
        type = 0;
        parent = 0;
        Guard();
        if (!_autoVacuum || pageNumber < 3 || IsPtrmapPage(pageNumber))
        {
            return FailCorrupt($"page {pageNumber} has no pointer-map entry");
        }

        uint pmap = PtrmapPageOf(pageNumber);
        PagerStatus s = GetCore(pmap, GetOptions.None, out PageFrame? f);
        if (s != PagerStatus.Ok)
        {
            return s;
        }

        int off = 5 * (int)(pageNumber - pmap - 1);
        byte t = f!.Data[off];
        parent = Be32(f.Data.AsSpan(off + 1));
        f.Pins--;
        if (t is < 1 or > 5)
        {
            return FailCorrupt($"pointer-map page {pmap} entry for page {pageNumber} has type {t}");
        }

        type = (PtrmapType)t;
        return PagerStatus.Ok;
    }

    /// <summary>Writes the pointer-map entry of a page (no-op if the stored entry is already equal).</summary>
    /// <param name="pageNumber">The page.</param>
    /// <param name="type">The entry type.</param>
    /// <param name="parent">The parent page number.</param>
    /// <returns>The status.</returns>
    public PagerStatus PtrmapPut(uint pageNumber, PtrmapType type, uint parent)
    {
        Guard();
        RequireWrite();
        if (!_autoVacuum || pageNumber < 3 || IsPtrmapPage(pageNumber))
        {
            return FailCorrupt($"page {pageNumber} has no pointer-map entry");
        }

        uint pmap = PtrmapPageOf(pageNumber);
        if (_pmStep == 0)
        {
            PagerStatus g = GetCore(pmap, GetOptions.None, out PageFrame? f);
            if (g != PagerStatus.Ok)
            {
                return g;
            }

            _pmFrame = f;
            _pmStep = 1;
        }

        int off = 5 * (int)(pageNumber - pmap - 1);
        Span<byte> d = _pmFrame!.Data;
        if (d[off] == (byte)type && BinaryPrimitives.ReadUInt32BigEndian(d[(off + 1)..]) == parent)
        {
            _pmFrame.Pins--;
            _pmFrame = null;
            _pmStep = 0;
            _rsvStale = true;
            return PagerStatus.Ok;
        }

        PagerStatus s = MakeWritable(_pmFrame);
        if (s != PagerStatus.Ok)
        {
            return s;
        }

        d[off] = (byte)type;
        BinaryPrimitives.WriteUInt32BigEndian(d[(off + 1)..], parent);
        _pmFrame.Pins--;
        _pmFrame = null;
        _pmStep = 0;
        _rsvStale = true;
        return PagerStatus.Ok;
    }
}
