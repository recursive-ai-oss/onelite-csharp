using System.Buffers.Binary;

namespace RecursiveAI.OneLite.Storage.Pager;

/// <summary>
/// The B-tree side of auto-vacuum relocation. At commit the pager moves every in-use page that lies beyond the final size of the database
/// into a free page below it. The pager can copy a page, but only the B-tree layer knows which bytes elsewhere refer to it (a parent's child
/// pointer, a cell's first-overflow-page number, an overflow page's next-page number, and the pointer-map entries of the moved page's own
/// children), so it supplies this callback.
/// </summary>
public interface IPageRelocator
{
    /// <summary>
    /// Called after the pager has copied the content of page <paramref name="oldPage"/> verbatim into page <paramref name="newPage"/> (which is writable
    /// and journaled) and recorded <paramref name="newPage"/> in the pointer map with the same <paramref name="type"/> and <paramref name="parent"/>.
    /// The implementation makes every reference to <paramref name="oldPage"/> refer to <paramref name="newPage"/> instead (fileformat2 §1.7):
    /// <list type="bullet">
    /// <item><see cref="PtrmapType.BTree"/>: the child pointer in the parent page; and for each child of the moved page (and each first overflow
    /// page of its cells), the pointer-map entry's parent becomes <paramref name="newPage"/>.</item>
    /// <item><see cref="PtrmapType.Overflow1"/>: the first-overflow-page number in the cell of the parent page.</item>
    /// <item><see cref="PtrmapType.Overflow2"/>: the next-page number in the parent (previous overflow) page; and the pointer-map entry of the page
    /// after the moved one, if any.</item>
    /// </list>
    /// It uses the pager's <c>Get</c>/<c>MakeWritable</c> and <see cref="Pager.PtrmapPut"/> for that, and may return
    /// <see cref="PagerStatus.Pending"/> like any pager operation: it is then called again with the same arguments, so it must keep its
    /// progress itself. Root pages are never relocated (the pager reports a corrupt database if one lies beyond the final size). Returning
    /// <see cref="PagerStatus.Error"/> aborts the commit.
    /// </summary>
    /// <param name="pager">The pager doing the commit.</param>
    /// <param name="oldPage">The page being emptied; it is cut off the end of the file afterwards.</param>
    /// <param name="newPage">The free page that now holds the content.</param>
    /// <param name="type">The pointer-map type of the page.</param>
    /// <param name="parent">The pointer-map parent of the page, which has not moved in this call.</param>
    /// <returns>The status.</returns>
    PagerStatus Relocate(Pager pager, uint oldPage, uint newPage, PtrmapType type, uint parent);
}

public sealed partial class Pager
{
    private IPageRelocator? _relocator;
    private int _avStep;
    private List<uint>? _avFree;
    private HashSet<uint>? _avTrunks;
    private uint _avCount;
    private uint _avTrunk;
    private uint _avVisited;
    private uint _avFinal;
    private uint _avLast;
    private int _avTarget;
    private uint _avTo;
    private PtrmapType _avType;
    private uint _avParent;
    private PageFrame? _avFromFrame;
    private PageFrame? _avToFrame;
    private bool _avExplicit;
    private bool _avActive;
    private uint _avLimit;
    private uint _avReleased;
    private uint _avRemaining;
    private List<uint>? _avSurv;
    private int _avSi;

    /// <summary>
    /// Installs the B-tree layer's relocation callback. With one, a commit in a full auto-vacuum database (auto-vacuum on, incremental flag
    /// off) moves the free pages to the end of the file and truncates them away, as the format requires (fileformat2 §1.7). Without one the
    /// free pages stay where they are: the file is valid but larger.
    /// </summary>
    /// <param name="relocator">The callback, or null.</param>
    public void SetRelocator(IPageRelocator? relocator) => _relocator = relocator;

    // How many pages the file has once every free page is gone: the pages in use, plus the pointer-map pages and lock-byte page that lie
    // among them (fileformat2 §1.7: no pointer-map page ends the file, and the lock-byte page is skipped).
    private uint FinalSize(uint used)
    {
        uint f = used;
        for (int i = 0; i < 64; i++)
        {
            uint ptrmaps = 0;
            if (_autoVacuum && f >= 2)
            {
                uint group = (uint)(UsableSize / 5) + 1;
                ptrmaps = (f - 2) / group + 1;
            }

            uint next = used + ptrmaps + (f >= LockBytePage ? 1u : 0u);
            if (next == f)
            {
                break;
            }

            f = next;
        }

        while (f > 1 && (IsPtrmapPage(f) || f == LockBytePage))
        {
            f--;
        }

        return f;
    }

    // Commit.AutoVacuumRelocate. Reads the freelist, works out the final size, and moves each in-use page past it (highest first) into the lowest
    // free page; then the freelist is empty and the database is cut to the final size. Every page that is cut off and holds information a
    // rollback needs (in-use pages, pointer-map pages, freelist trunks) is saved in the journal first. Re-entrant; the only state kept across a
    // Pending is in the _av* fields.
    private PagerStatus AutoVacuumCommit()
    {
        for (; ; )
        {
            switch (_avStep)
            {
                case 0:
                {
                    // At commit only a full auto-vacuum database is vacuumed; PRAGMA incremental_vacuum asks for it in an incremental one.
                    if (!_autoVacuum || _incremental != _avExplicit || _relocator == null || _dbSize < 3)
                    {
                        return PagerStatus.Ok;
                    }

                    PagerStatus s = GetCore(1, GetOptions.None, out PageFrame? p1);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _avCount = DatabaseHeader.GetUInt32(p1!.Data, DatabaseHeader.FreelistCountOffset);
                    _avTrunk = DatabaseHeader.GetUInt32(p1.Data, DatabaseHeader.FreelistTrunkOffset);
                    p1.Pins--;
                    if (_avCount == 0)
                    {
                        return PagerStatus.Ok;
                    }

                    // A commit empties the freelist; the pragma gives back at most the requested number of pages (pragma incremental_vacuum).
                    _avReleased = _avExplicit ? Math.Min(_avLimit, _avCount) : _avCount;
                    _avRemaining = _avCount - _avReleased;
                    _avFree = [];
                    _avTrunks = [];
                    _avVisited = 0;
                    _avStep = 1;
                    break;
                }

                case 1:
                {
                    // Read the whole freelist: a trunk, then its leaves, then the next trunk.
                    while (_avTrunk != 0)
                    {
                        if (_avTrunk < 2 || _avTrunk > _dbSize || _avTrunk == LockBytePage || _avVisited >= _avCount)
                        {
                            return FailCorrupt($"the freelist has a bad trunk page {_avTrunk} or more pages than the header's count {_avCount}");
                        }

                        PagerStatus s = GetCore(_avTrunk, GetOptions.None, out PageFrame? t);
                        if (s != PagerStatus.Ok)
                        {
                            return s;
                        }

                        uint leaves = Be32(t!.Data.AsSpan(4));
                        if (leaves > (uint)MaxTrunkLeaves || _avVisited + 1 + leaves > _avCount)
                        {
                            t.Pins--;
                            return FailCorrupt($"freelist trunk page {_avTrunk} lists {leaves} leaves, more than the freelist has");
                        }

                        _avFree!.Add(_avTrunk);
                        _avTrunks!.Add(_avTrunk);
                        for (uint i = 0; i < leaves; i++)
                        {
                            uint leaf = Be32(t.Data.AsSpan(8 + 4 * (int)i));
                            if (leaf < 2 || leaf > _dbSize || leaf == LockBytePage || IsPtrmapPage(leaf))
                            {
                                t.Pins--;
                                return FailCorrupt($"freelist trunk page {_avTrunk} lists page {leaf}, which cannot be a free page");
                            }

                            _avFree.Add(leaf);
                        }

                        _avVisited += 1 + leaves;
                        uint next = Be32(t.Data);
                        t.Pins--;
                        _avTrunk = next;
                    }

                    if (_avVisited != _avCount)
                    {
                        return FailCorrupt($"the freelist holds {_avVisited} pages but the header says {_avCount}");
                    }

                    _avFree!.Sort();
                    for (int i = 1; i < _avFree.Count; i++)
                    {
                        if (_avFree[i] == _avFree[i - 1])
                        {
                            return FailCorrupt($"page {_avFree[i]} is on the freelist twice");
                        }
                    }

                    // Pages of the file that hold something (the lock-byte page and pointer-map pages are not counted among the free ones).
                    uint used = _dbSize - _avCount;
                    uint holes = _dbSize >= LockBytePage ? 1u : 0u;
                    if (_autoVacuum && _dbSize >= 2)
                    {
                        holes += (_dbSize - 2) / ((uint)(UsableSize / 5) + 1) + 1;
                    }

                    if (used < holes + 1)
                    {
                        return FailCorrupt("the freelist is larger than the database");
                    }

                    _avFinal = FinalSize(used - holes + _avRemaining);
                    _avLast = _dbSize;
                    _avTarget = 0;
                    _avStep = 2;
                    break;
                }

                case 2:
                {
                    // Next page past the final size, from the end; the lock-byte page and free leaves are simply dropped.
                    while (_avLast > _avFinal && (_avLast == LockBytePage || IsFreeLeaf(_avLast)))
                    {
                        _avLast--;
                    }

                    if (_avLast <= _avFinal)
                    {
                        _avStep = 20;
                        break;
                    }

                    // A pointer-map page or a free trunk is cut off with its content: save the content for rollback first.
                    _avStep = IsPtrmapPage(_avLast) || _avTrunks!.Contains(_avLast) ? 3 : 4;
                    break;
                }

                case 3:
                {
                    PagerStatus s = GetCore(_avLast, GetOptions.None, out PageFrame? f);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    PagerStatus j = EnsureJournaled(f!);
                    f!.Pins--;
                    if (j != PagerStatus.Ok)
                    {
                        return j;
                    }

                    _avLast--;
                    _avStep = 2;
                    break;
                }

                case 4:
                {
                    // An in-use page: find out what it is.
                    PagerStatus s = PtrmapGet(_avLast, out _avType, out _avParent);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    if (_avType == PtrmapType.FreePage || _avType == PtrmapType.RootPage)
                    {
                        return FailCorrupt(_avType == PtrmapType.FreePage
                            ? $"page {_avLast} is marked free in the pointer map but is not on the freelist"
                            : $"root page {_avLast} lies beyond the final size of the database ({_avFinal} pages)");
                    }

                    // The lowest free page at or below the final size. Free pages are never in use, so the list holds enough of them.
                    while (_avTarget < _avFree!.Count && _avFree[_avTarget] > _avFinal)
                    {
                        _avTarget++;
                    }

                    if (_avTarget >= _avFree.Count)
                    {
                        return FailCorrupt("auto-vacuum found no free page to move a page into");
                    }

                    _avTo = _avFree[_avTarget];
                    _avStep = 5;
                    break;
                }

                case 5:
                {
                    PagerStatus s = GetCore(_avLast, GetOptions.None, out PageFrame? f);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _avFromFrame = f;
                    _avStep = 6;
                    break;
                }

                case 6:
                {
                    // The page leaves the file, so what it holds must be in the journal.
                    PagerStatus s = EnsureJournaled(_avFromFrame!);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _avStep = 7;
                    break;
                }

                case 7:
                {
                    // A free trunk still holds the freelist's structure, which a rollback needs; a free leaf holds nothing (like Allocate).
                    bool trunk = _avTrunks!.Contains(_avTo);
                    if (!trunk && _avTo <= _origSize)
                    {
                        _jBits.Set(_avTo);
                    }

                    PagerStatus s = GetCore(_avTo, trunk ? GetOptions.None : GetOptions.NoContent, out PageFrame? f);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _avToFrame = f;
                    _avStep = 8;
                    break;
                }

                case 8:
                {
                    PagerStatus s = MakeWritable(_avToFrame!);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    Buffer.BlockCopy(_avFromFrame!.Data, 0, _avToFrame!.Data, 0, _pageSize);
                    _avFromFrame.Pins--;
                    _avToFrame.Pins--;
                    _avFromFrame = _avToFrame = null;
                    _avStep = 9;
                    break;
                }

                case 9:
                {
                    PagerStatus s = PtrmapPut(_avTo, _avType, _avParent);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _avStep = 10;
                    break;
                }

                case 10:
                {
                    PagerStatus s = _relocator!.Relocate(this, _avLast, _avTo, _avType, _avParent);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _avTarget++;
                    _avLast--;
                    _avStep = 2;
                    break;
                }

                case 20:
                {
                    // The free pages that were not cut off or used as targets stay on the freelist; every target was taken in ascending order, so they
                    // are the rest of the sorted list. A commit leaves none.
                    _avSurv = [];
                    for (int i = _avTarget; i < _avFree!.Count; i++)
                    {
                        if (_avFree[i] <= _avFinal)
                        {
                            _avSurv.Add(_avFree[i]);
                        }
                    }

                    if (_avSurv.Count != _avRemaining)
                    {
                        return FailCorrupt($"auto-vacuum left {_avSurv.Count} free pages but {_avRemaining} were expected: the pointer map and the freelist disagree");
                    }

                    _avSi = 0;
                    _avStep = _avSurv.Count == 0 ? 30 : 21;
                    break;
                }

                case 21:
                {
                    // Rewrite the freelist (fileformat2 §1.5): trunk pages chained through their first four bytes, each listing up to MaxTrunkLeaves leaves.
                    // The surviving free pages are laid out as trunk, its leaves, next trunk, its leaves, and so on.
                    if (_avSi >= _avSurv!.Count)
                    {
                        _avStep = 30;
                        break;
                    }

                    uint tp = _avSurv[_avSi];
                    bool wasTrunk = _avTrunks!.Contains(tp);
                    if (!wasTrunk && tp <= _origSize)
                    {
                        _jBits.Set(tp);
                    }

                    PagerStatus s = GetCore(tp, wasTrunk ? GetOptions.None : GetOptions.NoContent, out PageFrame? f);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _avToFrame = f;
                    _avStep = 22;
                    break;
                }

                case 22:
                {
                    PagerStatus s = MakeWritable(_avToFrame!);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    int leaves = Math.Min(MaxTrunkLeaves, _avSurv!.Count - _avSi - 1);
                    byte[] d = _avToFrame!.Data;
                    Array.Clear(d, 0, _pageSize);
                    int nextAt = _avSi + 1 + leaves;
                    DatabaseHeader.SetUInt32(d, 0, nextAt < _avSurv.Count ? _avSurv[nextAt] : 0);
                    DatabaseHeader.SetUInt32(d, 4, (uint)leaves);
                    for (int i = 0; i < leaves; i++)
                    {
                        DatabaseHeader.SetUInt32(d, 8 + 4 * i, _avSurv[_avSi + 1 + i]);
                    }

                    _avToFrame.Pins--;
                    _avToFrame = null;
                    _avSi = nextAt;
                    _avStep = 21;
                    break;
                }

                default:
                {
                    // 30: the database is _avFinal pages and the freelist is rebuilt (or empty).
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

                    DatabaseHeader.SetUInt32(p1!.Data, DatabaseHeader.FreelistTrunkOffset, _avSurv is { Count: > 0 } ? _avSurv[0] : 0);
                    DatabaseHeader.SetUInt32(p1.Data, DatabaseHeader.FreelistCountOffset, _avRemaining);
                    p1.Pins--;
                    _dbSize = _avFinal;
                    DropFramesBeyond(_avFinal);
                    _avFree = null;
                    _avTrunks = null;
                    _avSurv = null;
                    _avStep = 0;
                    return PagerStatus.Ok;
                }
            }
        }
    }

    /// <summary>
    /// <c>PRAGMA incremental_vacuum(N)</c>: in an incremental auto-vacuum database, gives up to <paramref name="pages"/> free pages back by moving pages from the
    /// end of the file into free pages nearer the start and cutting the file short (pragma incremental_vacuum). Needs a write transaction. Does
    /// nothing in other modes or when nothing is free.
    /// </summary>
    /// <param name="pages">How many pages to free; 0 means the whole freelist. More than the freelist holds also means all of it.</param>
    /// <param name="released">When the result is <see cref="PagerStatus.Ok"/>, how many pages left the freelist.</param>
    /// <returns>The status; repeat the same call after <see cref="PagerStatus.Pending"/>.</returns>
    public PagerStatus IncrementalVacuumStep(uint pages, out uint released)
    {
        released = 0;
        if (!_avActive)
        {
            _avActive = true;
            _avExplicit = true;
            _avLimit = pages == 0 ? uint.MaxValue : pages;
            _avReleased = 0;
            _avRemaining = 0;
        }

        PagerStatus s = AutoVacuumCommit();
        if (s == PagerStatus.Pending)
        {
            return s;
        }

        if (s == PagerStatus.Ok)
        {
            released = _avReleased;
        }
        else
        {
            _avStep = 0;
            _avFree = null;
        }

        _avActive = false;
        _avExplicit = false;
        return s;
    }

    /// <summary>True if the database is in incremental auto-vacuum mode (header offset 64).</summary>
    public bool IncrementalVacuum => _autoVacuum && _incremental;

    /// <summary>
    /// Chooses the auto-vacuum mode of a database that has no pages yet (the file is created in that mode). Does nothing once a page exists.
    /// </summary>
    /// <param name="autoVacuum">Pointer-map pages on or off.</param>
    /// <param name="incremental">With <paramref name="autoVacuum"/>: incremental rather than full.</param>
    /// <returns>True if applied.</returns>
    public bool TrySetAutoVacuum(bool autoVacuum, bool incremental)
    {
        if (_dbSize != 0 || _state == TxState.Write && _dirty.Count != 0)
        {
            return false;
        }

        _autoVacuum = autoVacuum;
        _incremental = autoVacuum && incremental;
        _largestRoot = autoVacuum ? 1u : 0u;
        return true;
    }

    /// <summary>
    /// Switches an auto-vacuum database between full and incremental mode by flipping the header flag at offset 64 (the format allows it at
    /// any time, fileformat2 §1.3.12). Needs a write transaction.
    /// </summary>
    /// <param name="incremental">The new flag.</param>
    /// <returns>The status; repeat after <see cref="PagerStatus.Pending"/>.</returns>
    public PagerStatus SetIncrementalFlag(bool incremental)
    {
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

        DatabaseHeader.SetUInt32(p1!.Data, DatabaseHeader.IncrementalVacuumOffset, incremental ? 1u : 0u);
        p1.Pins--;
        _incremental = incremental;
        return PagerStatus.Ok;
    }

    // ---- replacing the whole database (VACUUM) ---------------------------------------------------------------------------------

    private bool _rcActive;
    private uint _rcIdx;
    private uint _rcOld;

    /// <summary>
    /// Replaces every page of the database inside the open write transaction: page <c>i</c> becomes <c>pages[i-1]</c>, and the database
    /// ends up exactly <c>pages.Count</c> pages long. Pages above the new size are journaled (rollback mode) so that a rollback or a crash
    /// restores them, then cut off at commit. The caller built a consistent image: header, B-trees, pointer map and an empty freelist.
    /// </summary>
    /// <param name="pages">The new pages, each exactly one page long; at least page 1.</param>
    /// <param name="autoVacuum">The image's auto-vacuum flag (header offset 52 non-zero).</param>
    /// <param name="incremental">The image's incremental flag (header offset 64).</param>
    /// <returns>The status; repeat the same call after <see cref="PagerStatus.Pending"/>.</returns>
    public PagerStatus ReplaceContents(IReadOnlyList<byte[]> pages, bool autoVacuum, bool incremental)
    {
        ArgumentNullException.ThrowIfNull(pages);
        Guard();
        if (_state != TxState.Write || pages.Count == 0)
        {
            throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "ReplaceContents needs a write transaction and at least page 1");
        }

        uint n = (uint)pages.Count;
        if (!_rcActive)
        {
            _rcActive = true;
            _rcOld = _dbSize;
            _rcIdx = 0;
            if (n > _dbSize)
            {
                _dbSize = n;
            }
        }

        // In WAL mode a page above the new size has nothing to protect, so only the pages that stay are written.
        uint top = _wal != null ? n : Math.Max(n, _rcOld);
        while (_rcIdx < top)
        {
            uint pn = _rcIdx + 1;
            if (pn == LockBytePage)
            {
                _rcIdx++;
                continue;
            }

            PagerStatus s = GetCore(pn, pn <= _rcOld ? GetOptions.None : GetOptions.NoContent, out PageFrame? f);
            if (s != PagerStatus.Ok)
            {
                return s;
            }

            PagerStatus w = MakeWritable(f!);
            if (w != PagerStatus.Ok)
            {
                f!.Pins--;
                return w;
            }

            if (pn <= n)
            {
                byte[] src = pages[(int)pn - 1];
                if (src.Length != _pageSize)
                {
                    f!.Pins--;
                    throw new ArgumentException("every replacement page must be one page long", nameof(pages));
                }

                Buffer.BlockCopy(src, 0, f!.Data, 0, _pageSize);
            }

            f!.Pins--;
            _rcIdx++;
        }

        _dbSize = n;
        _autoVacuum = autoVacuum;
        _incremental = autoVacuum && incremental;
        _largestRoot = DatabaseHeader.GetUInt32(pages[0], DatabaseHeader.LargestRootOffset);
        DropFramesBeyond(n);
        _rcActive = false;
        return PagerStatus.Ok;
    }

    private bool IsFreeLeaf(uint page) => !IsPtrmapPage(page) && _avFree!.BinarySearch(page) >= 0 && !_avTrunks!.Contains(page);

    // Forgets cached pages numbered above the database's new size (they are cut off the file at commit and must never be read back).
    private void DropFramesBeyond(uint size)
    {
        foreach (PageFrame f in _frames)
        {
            if (f.PageNumber > size && f.Pins == 0)
            {
                _cache.Remove(f.PageNumber);
                if (f.Dirty)
                {
                    _dirty.Remove(f);
                    f.Dirty = false;
                }

                f.PageNumber = 0;
                _freeFrames.Push(f);
            }
        }
    }
}
