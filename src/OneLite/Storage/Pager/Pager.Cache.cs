using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Storage.Pager;

public sealed partial class Pager
{
    private readonly Dictionary<uint, PageFrame> _cache = [];
    private readonly List<PageFrame> _frames = [];
    private readonly Stack<PageFrame> _freeFrames = new();
    private readonly List<PageFrame> _dirty = [];
    private int _hand;
    private int _cacheLimitSetting;

    private PageFrame? _getFrame;
    private int _spStep;
    private PageFrame? _spFrame;
    private bool _spillNone;

    private int CacheLimitPages
    {
        get
        {
            long n = _cacheLimitSetting >= 0 ? _cacheLimitSetting : (-(long)_cacheLimitSetting * 1024) / (_pageSize + 64);
            return (int)Math.Clamp(n, 3, int.MaxValue);
        }
    }

    /// <summary>
    /// Pins a page and returns it. A hit allocates nothing. The caller must <see cref="Release"/> the frame.
    /// </summary>
    /// <param name="pageNumber">The page, 1 to <see cref="PageCount"/>.</param>
    /// <param name="flags">Options.</param>
    /// <param name="frame">The pinned frame when the result is <see cref="PagerStatus.Ok"/>.</param>
    /// <returns>The status; see the step contract.</returns>
    /// <exception cref="OneLiteException"><c>SQLITE_MISUSE</c> outside a transaction or while a request is pending.</exception>
    public PagerStatus Get(uint pageNumber, GetOptions flags, out PageFrame? frame)
    {
        frame = null;
        Guard();
        if (_state == TxState.None)
        {
            throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "Get outside a transaction");
        }

        if (_yieldEvery > 0)
        {
            if (!_yieldArmed && !_hasComp && _getFrame == null && ++_hits % _yieldEvery == 0)
            {
                _yieldArmed = true;
            }

            if (_yieldArmed)
            {
                if (!Io(ResumeState.Yield, new IoRequest { Kind = IoKind.Yield, Cancellable = true }))
                {
                    return PagerStatus.Pending;
                }

                _yieldArmed = false;
            }
        }

        return GetCore(pageNumber, flags, out frame);
    }

    // The re-entrant part of Get (also used by the pager's own page accesses).
    private GetOptions _getFlags;

    private PagerStatus GetCore(uint pageNumber, GetOptions flags, out PageFrame? frame)
    {
        frame = null;
        if (_getFrame != null && _getFrame.PageNumber != pageNumber)
        {
            // A read for another page was started and has completed, but the caller repeats a longer sequence of calls (a reserve chain
            // restarts from its first call) and asks for this page first. Finish that read into the cache, then serve this page.
            PagerStatus fs = GetCore(_getFrame.PageNumber, _getFlags, out PageFrame? finished);
            if (fs != PagerStatus.Ok)
            {
                return fs;
            }

            finished!.Pins--;
        }

        if (_getFrame == null)
        {
            _getFlags = flags;
            if (pageNumber == 1 && _dbSize == 0 && _state == TxState.Write)
            {
                return InitNewDatabase(out frame);
            }

            if (pageNumber == 0 || pageNumber > _dbSize || pageNumber == LockBytePage)
            {
                return FailCorrupt($"page {pageNumber} is outside the database ({_dbSize} pages) or is the lock-byte page");
            }

            if (_cache.TryGetValue(pageNumber, out PageFrame? hit))
            {
                hit.Pins++;
                hit.Referenced = true;
                frame = hit;
                return PagerStatus.Ok;
            }

            PagerStatus s = ObtainFrame(out PageFrame? fresh);
            if (s != PagerStatus.Ok)
            {
                return s;
            }

            fresh!.PageNumber = pageNumber;
            _getFrame = fresh;
        }

        PageFrame f = _getFrame!;
        bool needRead = (flags & GetOptions.NoContent) == 0 && (f.PageNumber <= PagesInFile() || (_wal != null && _wal.Locate(f.PageNumber, _pageSize, out _)));
        if (needRead)
        {
            IVfsFile? file = _dbFile;
            long off = (long)(f.PageNumber - 1) * _pageSize;
            ResumeState phase = ResumeState.GetReadDb;
            if (_source != null && _source.TryLocate(f.PageNumber, out IVfsFile? sf, out long so))
            {
                file = sf;
                off = so;
                phase = ResumeState.GetReadSource;
            }

            if (!Io(phase, new IoRequest { Kind = IoKind.Read, File = file, Offset = off, Length = _pageSize, Data = _cipher is null ? f.Data : _cbuf!, Cancellable = true }))
            {
                return PagerStatus.Pending;
            }

            if (_comp.Status != 0)
            {
                ReleaseLoading(f);
                return FailIo(_comp.Status, $"reading page {f.PageNumber} failed");
            }

            int got = (int)_comp.Value;
            if (_cipher is not null)
            {
                // A short read is zero-padded and then fails its MAC, as a damaged page does.
                if (got < _pageSize)
                {
                    Array.Clear(_cbuf!, got, _pageSize - got);
                }

                PagerStatus d = DecodeStored(f.PageNumber, f.Data);
                if (d != PagerStatus.Ok)
                {
                    ReleaseLoading(f);
                    return d;
                }
            }
            else if (got < _pageSize)
            {
                Array.Clear(f.Data, got, _pageSize - got);
            }
        }
        else
        {
            Array.Clear(f.Data);
        }

        _getFrame = null;
        f.Pins = 1;
        f.Dirty = false;
        f.NeedSync = false;
        f.Referenced = true;
        f.SavedLevel = 0;
        _cache[f.PageNumber] = f;
        frame = f;
        return PagerStatus.Ok;
    }

    // A zero-length file becomes a database at its first write: page 1 gets the header and an empty schema-table leaf (fileformat2 §1.3, §1.6).
    private PagerStatus InitNewDatabase(out PageFrame? frame)
    {
        frame = null;
        PagerStatus s = ObtainFrame(out PageFrame? f);
        if (s != PagerStatus.Ok)
        {
            return s;
        }

        Array.Clear(f!.Data);
        DatabaseHeader.WriteNew(f.Data, _pageSize, _reserved, _encoding, _autoVacuum, _incremental);
        f.PageNumber = 1;
        f.Pins = 1;
        f.Referenced = true;
        f.Dirty = true;
        f.SavedLevel = _spCur;
        _dirty.Add(f);
        _cache[1] = f;
        _dbSize = 1;
        frame = f;
        return PagerStatus.Ok;
    }

    // Pages the database file is known to hold (a page past the end of the file reads as zeros without I/O).
    private uint PagesInFile() => _pageSize == 0 ? 0 : (uint)Math.Min(uint.MaxValue, (_fileBytes + _pageSize - 1) / _pageSize);

    private void ReleaseLoading(PageFrame f)
    {
        _getFrame = null;
        f.PageNumber = 0;
        f.Pins = 0;
        _freeFrames.Push(f);
    }

    /// <summary>Unpins a frame returned by <see cref="Get"/> or <see cref="Allocate"/>.</summary>
    /// <param name="frame">The frame.</param>
    public void Release(PageFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Owner != this)
        {
            throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "the page belongs to another pager");
        }

        if (frame.Pins <= 0)
        {
            throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), $"page {frame.PageNumber} released more often than it was pinned");
        }

        frame.Pins--;
    }

    private PagerStatus ObtainFrame(out PageFrame? frame)
    {
        frame = null;
        for (; ; )
        {
            if (_freeFrames.Count > 0)
            {
                frame = _freeFrames.Pop();
                return PagerStatus.Ok;
            }

            // A reserve call (Pager.Freelist.cs) must never push out a page an earlier reserve call made sure is cached, so it grows the
            // cache past its soft limit instead of evicting.
            int limit = _reserving ? int.MaxValue : CacheLimitPages;
            if (_frames.Count < limit)
            {
                frame = NewFrame();
                return PagerStatus.Ok;
            }

            int n = _frames.Count;
            for (int i = 0; i < 2 * n; i++)
            {
                if (_hand >= n)
                {
                    _hand = 0;
                }

                PageFrame v = _frames[_hand++];
                if (v.Pins != 0 || v.Dirty || v.PageNumber == 0)
                {
                    continue;
                }

                if (v.Referenced)
                {
                    v.Referenced = false;
                    continue;
                }

                _cache.Remove(v.PageNumber);
                v.PageNumber = 0;
                frame = v;
                return PagerStatus.Ok;
            }

            if (_state == TxState.Write && !_spillNone)
            {
                PagerStatus s = Spill();
                if (s != PagerStatus.Ok)
                {
                    return s;
                }

                if (!_spillNone)
                {
                    continue;
                }
            }

            _spillNone = false;
            frame = NewFrame();
            return PagerStatus.Ok;
        }
    }

    private PageFrame NewFrame()
    {
        var f = new PageFrame(_pageSize) { Owner = this };
        _frames.Add(f);
        return f;
    }

    // Forgets every cached page (the file changed under us, or the page size changed). Pinned frames must not exist.
    private void DropAllFrames()
    {
        _cache.Clear();
        _dirty.Clear();
        _freeFrames.Clear();
        if (_frames.Count != 0 && _frames[0].Data.Length != _pageSize)
        {
            _frames.Clear();
        }
        else
        {
            foreach (PageFrame f in _frames)
            {
                f.PageNumber = 0;
                f.Pins = 0;
                f.Dirty = false;
                f.Referenced = false;
                f.SavedLevel = 0;
#if CHECKED
                Array.Fill(f.Data, (byte)0xDB);
#endif
                _freeFrames.Push(f);
            }
        }

        _hand = 0;
        _getFrame = null;
    }

    // Drops cached pages that are not dirty (the database file may have changed).
    private void DropCleanFrames()
    {
        foreach (PageFrame f in _frames)
        {
            if (f.PageNumber != 0 && !f.Dirty && f.Pins == 0)
            {
                _cache.Remove(f.PageNumber);
                f.PageNumber = 0;
                _freeFrames.Push(f);
            }
        }
    }

    // ---- spill (suspend-point notes: Spill.*) ------------------------------------------------------------------------------------

    /// <summary>
    /// Writes one dirty, unpinned page to the database file to make room (rollback modes). The database file may only be written under
    /// EXCLUSIVE, and only after the journal that protects the overwritten pages is durable (atomiccommit §3.7), so the first spill syncs
    /// the journal and takes the exclusive lock. If a reader holds SHARED the lock is busy and the call fails with SQLITE_BUSY, as the
    /// reference does (docs/durability.md).
    /// </summary>
    private PagerStatus Spill()
    {
        if (_wal != null)
        {
            return SpillWal();
        }

        for (; ; )
        {
            switch (_spStep)
            {
                case 0:
                    // Prefer a page whose journal record is already durable: one journal sync then covers many spills (atomiccommit §3.7 only
                    // needs a page's own record synced before the page is overwritten).
                    _spFrame = null;
                    PageFrame? any = null;
                    foreach (PageFrame d in _dirty)
                    {
                        if (d.Pins == 0)
                        {
                            any ??= d;
                            if (!d.NeedSync)
                            {
                                _spFrame = d;
                                break;
                            }
                        }
                    }

                    if (_spFrame == null && any == null)
                    {
                        _spillNone = true;
                        return PagerStatus.Ok;
                    }

                    if (_spFrame != null)
                    {
                        _spStep = 2;
                        break;
                    }

                    _spFrame = any;
                    _spStep = 1;
                    break;
                case 1:
                {
                    PagerStatus s = SyncJournal(superName: null);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _spStep = 2;
                    break;
                }

                case 2:
                {
                    PagerStatus s = TakeLock(LockLevel.Exclusive);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _spStep = 3;
                    break;
                }

                default:
                {
                    PageFrame f = _spFrame!;
                    if (_cipher is not null && !Resuming(ResumeState.SpillWriteDbPage))
                    {
                        EncodeForWrite(f.PageNumber, f.Data, _ebuf!);
                    }

                    if (!Io(ResumeState.SpillWriteDbPage, new IoRequest { Kind = IoKind.Write, File = _dbFile, Offset = (long)(f.PageNumber - 1) * _pageSize, Length = _pageSize, Data = _cipher is null ? f.Data : _ebuf! }))
                    {
                        return PagerStatus.Pending;
                    }

                    if (_comp.Status != 0)
                    {
                        return FailIo(_comp.Status, $"writing page {f.PageNumber} failed");
                    }

                    _dbModified = true;
                    f.Dirty = false;
                    _dirty.Remove(f);
                    _fileBytes = Math.Max(_fileBytes, (long)f.PageNumber * _pageSize);
                    _spStep = 0;
                    _spFrame = null;
                    return PagerStatus.Ok;
                }
            }
        }
    }
}
