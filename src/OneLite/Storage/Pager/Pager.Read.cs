using System.Text;
using RecursiveAI.OneLite.Storage.Vfs;
using RecursiveAI.OneLite.Storage.Wal;

namespace RecursiveAI.OneLite.Storage.Pager;

public sealed partial class Pager
{
    private JournalMode _journalMode;
    private SynchronousMode _sync;
    private bool _dbModified;

    private int _rdStep;
    private bool _walProbed;
    private OneLiteException? _probeError;
    private IVfsFile? _hotFile;
    private readonly byte[] _hdrBuf = new byte[DatabaseHeader.Size];

    // hot-journal inspection and playback state
    private int _ppStep;
    private IVfsFile? _pbFile;
    private long _pbSize;
    private long _pbEnd;
    private JournalFormat.Header _pbFirst;
    private bool _pbHot;
    private bool _pbStale;
    private string? _pbSuper;
    private int _pbNameLen;
    private uint _pbNameSum;
    private readonly byte[] _pbHdr = new byte[JournalFormat.HeaderFields];
    private readonly byte[] _pbTail = new byte[16];
    private byte[]? _pbName;
    private byte[]? _pbRec;
    private int _pbStep;
    private long _pbSegOff;
    private JournalFormat.Header _pbSeg;
    private long _pbRecStart;
    private long _pbRecCount;
    private long _pbRecIdx;
    private uint _pbPgno;
    // The wal-index snapshot under which page 1's header fields (database size, change counter, encoding, ...) were last parsed; valid until a write
    // transaction begins or the log is reopened.
    private WalIndexHeader _hdrSnap;
    private bool _hdrSnapOk;
    private int _fjStep;
    private int _mbIdx;

    /// <summary>
    /// Starts a read transaction: takes SHARED, rolls back a hot journal if there is one (atomiccommit §4, lockingv3 "hot journal"), reads and
    /// validates page 1, and drops cached pages if another process changed the file since the last transaction.
    /// </summary>
    /// <returns>The status; see the step contract.</returns>
    /// <exception cref="OneLiteException">Misuse; the pager is disposed.</exception>
    public PagerStatus BeginRead()
    {
        Guard();
        if (_state != TxState.None)
        {
            return PagerStatus.Ok;
        }

        for (; ; )
        {
            switch (_rdStep)
            {
                case 0:
                {
                    PagerStatus s = TakeLock(LockLevel.Shared);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    // In WAL mode SHARED is held for as long as the database is open and there is no rollback journal to look for (walformat 2.3.1).
                    _rdStep = _wal != null ? 20 : 1;
                    break;
                }

                case 20:
                {
                    PagerStatus s = WalBeginReadStep();
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    if (_ccStep == 4)
                    {
                        // A concurrent commit rebasing inside a group reads the log including commits that are appended but not published yet.
                        GroupAdoptTail();
                    }

                    if (_hdrSnapOk && _hasHeader && _cipher is null && _wal!.Snap == _hdrSnap)
                    {
                        // The snapshot is the one page 1 was parsed under: nothing it holds can have changed, so the size and header reads are
                        // skipped. A page number up to the database size is always in the log or the file, which is all _fileBytes is asked about.
                        _fileBytes = Math.Max(_fileBytes, (long)_dbSize * _pageSize);
                        _origSize = _dbSize;
                        _rdStep = 0;
                        _state = TxState.Read;
                        return PagerStatus.Ok;
                    }

                    _rdStep = 6;
                    break;
                }

                case 21:
                {
                    PagerStatus s = WalOpenStep();
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _rdStep = 20;
                    break;
                }

                case 1:
                    if (!Io(ResumeState.HotJournalAccess, new IoRequest { Kind = IoKind.Access, Path = _journalPath, Cancellable = true }))
                    {
                        return PagerStatus.Pending;
                    }

                    if (_comp.Status != 0)
                    {
                        return FailIo(_comp.Status, "checking for a journal failed");
                    }

                    _rdStep = _comp.Value == 0 ? 6 : 2;
                    break;
                case 2:
                    if (!Io(ResumeState.HotJournalReserved, new IoRequest { Kind = IoKind.CheckReservedLock, File = _dbFile, Cancellable = true }))
                    {
                        return PagerStatus.Pending;
                    }

                    if (_comp.Status != 0)
                    {
                        return FailIo(_comp.Status, "checking for a reserved lock failed");
                    }

                    // lockingv3 §4.0: a journal is hot only if no other process holds a RESERVED or stronger lock.
                    _rdStep = _comp.Value == 1 ? 6 : 3;
                    break;
                case 3:
                    if (!Io(ResumeState.HotJournalOpen, new IoRequest { Kind = IoKind.Open, Path = _journalPath, Arg = (int)(VfsOpenOptions.ReadWrite | VfsOpenOptions.MainJournal), Cancellable = true }))
                    {
                        return PagerStatus.Pending;
                    }

                    if (_comp.Status != 0)
                    {
                        _rdStep = 6;
                        break;
                    }

                    _hotFile = _comp.File;
                    _pbFile = _hotFile;
                    _ppStep = 0;
                    _rdStep = 4;
                    break;
                case 4:
                {
                    PagerStatus s = PlaybackPrepare(checkSuper: true);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    if (_pbHot)
                    {
                        _rdStep = 5;
                    }
                    else
                    {
                        _hotFile?.Dispose();
                        _hotFile = null;
                        _pbFile = null;
                        _rdStep = _pbStale ? 14 : 6;
                    }

                    break;
                }

                case 14:
                    // The super-journal is gone: the multi-file commit had passed its commit point, so this journal is stale and must not be played back.
                    if (!Io(ResumeState.HotJournalSuper, new IoRequest { Kind = IoKind.Delete, Path = _journalPath, Arg = 0 }))
                    {
                        return PagerStatus.Pending;
                    }

                    _rdStep = 6;
                    break;
                case 5:
                    if (_opts.ReadOnly)
                    {
                        _hotFile?.Dispose();
                        _hotFile = null;
                        return Fail(ResultCode.ReadOnlyRollback, "a hot journal needs recovery but the database is read-only");
                    }

                    if (!Io(ResumeState.HotJournalExclusive, new IoRequest { Kind = IoKind.Lock, File = _dbFile, Arg = (int)LockLevel.Exclusive, Cancellable = true }))
                    {
                        return PagerStatus.Pending;
                    }

                    if (_comp.Status != 0)
                    {
                        _hotFile?.Dispose();
                        _hotFile = null;
                        return FailIo(_comp.Status, "locking for recovery failed");
                    }

                    if (_comp.Value == 1)
                    {
                        _lock = LockLevel.Exclusive;
                        _busyCount = 0;
                        _rdStep = 10;
                        break;
                    }

                    // Another process holds SHARED. Waiting while holding SHARED ourselves could deadlock against a process doing the same,
                    // so give up SHARED, wait per the busy policy and start over.
                    _rdStep = 8;
                    break;
                case 8:
                    if (!Io(ResumeState.HotJournalRelease, new IoRequest { Kind = IoKind.Unlock, File = _dbFile, Arg = (int)LockLevel.None }))
                    {
                        return PagerStatus.Pending;
                    }

                    _lock = LockLevel.None;
                    _hotFile?.Dispose();
                    _hotFile = null;
                    {
                        int ms = _opts.Busy?.Invoke(_busyCount++) ?? -1;
                        if (ms < 0)
                        {
                            _busyCount = 0;
                            return Fail(ResultCode.Busy, "a hot journal needs recovery but the database is in use");
                        }

                        _sleepMs = ms;
                    }

                    _rdStep = 9;
                    break;
                case 9:
                    if (!Io(ResumeState.LockSleep, new IoRequest { Kind = IoKind.Sleep, Length = _sleepMs, Cancellable = true }))
                    {
                        return PagerStatus.Pending;
                    }

                    _rdStep = 0;
                    break;
                case 10:
                    // Someone else may have recovered between our check and the lock: look again before touching the database.
                    if (!Io(ResumeState.HotJournalRecheck, new IoRequest { Kind = IoKind.Access, Path = _journalPath }))
                    {
                        return PagerStatus.Pending;
                    }

                    if (_comp.Status != 0 || _comp.Value == 0)
                    {
                        _hotFile?.Dispose();
                        _hotFile = null;
                        _rdStep = 12;
                        break;
                    }

                    _pbStep = 0;
                    _pbSegOff = 0;
                    _rdStep = 11;
                    break;
                case 11:
                {
                    PagerStatus s = RunPlayback(ref _hotFile);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    DropAllFrames();
                    _hasHeader = false;
                    ForeignChanges++;
                    _changeCounter = 0;
                    _rdStep = 12;
                    break;
                }

                case 12:
                    if (!Io(ResumeState.PlaybackUnlock, new IoRequest { Kind = IoKind.Unlock, File = _dbFile, Arg = (int)LockLevel.Shared }))
                    {
                        return PagerStatus.Pending;
                    }

                    _lock = LockLevel.Shared;
                    _rdStep = 6;
                    break;
                case 6:
                    if (!Io(ResumeState.OpenFileSize, new IoRequest { Kind = IoKind.FileSize, File = _dbFile, Cancellable = true }))
                    {
                        return PagerStatus.Pending;
                    }

                    if (_comp.Status != 0)
                    {
                        return FailIo(_comp.Status, "getting the database size failed");
                    }

                    _fileBytes = _comp.Value;
                    if (_fileBytes == 0 && !(_wal != null && _wal.ReadMx > 0))
                    {
                        // An empty file is a database with no pages yet (fileformat2 §1.3).
                        if (_cipher is not null)
                        {
                            OpenCodecForNewFile();
                        }

                        if (_hasHeader)
                        {
                            DropAllFrames();
                        }

                        _hasHeader = false;
                        _dbSize = 0;
                        _origSize = 0;
                        _changeCounter = 0;
                        _rdStep = 0;
                        _state = TxState.Read;
                        return PagerStatus.Ok;
                    }

                    _rdStep = 13;
                    break;
                case 22:
                    if (!Io(ResumeState.WalProbe, new IoRequest { Kind = IoKind.Access, Path = WalPath, Cancellable = true }))
                    {
                        return PagerStatus.Pending;
                    }

                    if (_comp.Status != 0 || _comp.Value == 0)
                    {
                        _walProbed = false;
                        return FailWith(_probeError!);
                    }

                    _rdStep = 21;
                    break;
                case 13:
                {
                    if (!Io(ResumeState.OpenRead1, new IoRequest { Kind = IoKind.Read, File = Page1File(out long page1At), Offset = page1At, Length = _cipher is null ? _hdrBuf.Length : _pageSize, Data = _cipher is null ? _hdrBuf : _cbuf!, Cancellable = true }))
                    {
                        return PagerStatus.Pending;
                    }

                    if (_comp.Status != 0)
                    {
                        return FailIo(_comp.Status, "reading the database header failed");
                    }

                    ReadOnlySpan<byte> hdrBytes = _hdrBuf.AsSpan(0, (int)Math.Min(_comp.Value, _hdrBuf.Length));
                    if (_cipher is not null)
                    {
                        // The header is inside the encrypted page 1: the first decode is what proves the key (a wrong key or an unencrypted
                        // file fails here with SQLITE_NOTADB).
                        if (_comp.Value < _pageSize)
                        {
                            Array.Clear(_cbuf!, (int)_comp.Value, _pageSize - (int)_comp.Value);
                        }

                        PagerStatus c = OpenCodecForFile(_cbuf!);
                        if (c == PagerStatus.Ok)
                        {
                            _plain1 ??= new byte[_pageSize];
                            c = DecodeStored(1, _plain1);
                        }

                        if (c != PagerStatus.Ok)
                        {
                            if (_wal == null && !_walProbed && _error is { Code: ResultCode.NotADb })
                            {
                                // Page 1 of the database file may be older than the log: after a rekey and before the next checkpoint it is stored
                                // under the previous key. SQLite opens a WAL that exists before it reads page 1, so do the same and read it through the log.
                                _probeError = _error;
                                _walProbed = true;
                                _rdStep = 22;
                                break;
                            }

                            return c;
                        }

                        hdrBytes = _plain1.AsSpan(0, DatabaseHeader.Size);
                    }

                    DatabaseHeader h;
                    try
                    {
                        h = DatabaseHeader.Parse(hdrBytes);
                    }
                    catch (OneLiteException e)
                    {
                        ClearProgress();
                        _error = e;
                        return PagerStatus.Error;
                    }

                    if (_cipher is not null && (h.PageSize != _pageSize || h.ReservedBytes < _codec!.ReserveSize))
                    {
                        // The page authenticated, so the settings are right; a header that disagrees with them is damage.
                        return FailCorrupt($"page 1: the header says {h.PageSize}-byte pages with {h.ReservedBytes} reserved bytes, the cipher needs {_pageSize} and {_codec!.ReserveSize}");
                    }

                    if (h.PageSize != _pageSize || h.ReservedBytes != _reserved)
                    {
                        // The header, not the options, says how big the pages are (fileformat2 section 1.3). This has to happen before the log is
                        // opened: its frames are located, and its index checked, by page size.
                        _pageSize = h.PageSize;
                        _reserved = h.ReservedBytes;
                        DropAllFrames();
                    }

                    if (h.ReadVersion == 2 && _wal == null)
                    {
                        // The header says WAL: open the -wal and the wal-index, take a snapshot, and read the header again through it (walformat 3).
                        _rdStep = 21;
                        break;
                    }

                    if (h.ReadVersion != 2 && _wal != null)
                    {
                        return FailCorrupt("the database header no longer says WAL while a WAL connection is open");
                    }

                    if (_hasHeader && h.ChangeCounter != _changeCounter)
                    {
                        ForeignChanges++;
                        DropCleanFrames();
                    }

                    _walProbed = false;
                    _hasHeader = true;
                    _changeCounter = h.ChangeCounter;
                    _encoding = h.Encoding;
                    _autoVacuum = h.AutoVacuum;
                    _largestRoot = h.LargestRootPage;
                    _incremental = h.IncrementalVacuum;
                    _dbSize = _wal != null && _wal.Snap.MxFrame > 0 ? _wal.Snap.NPage : h.SizeIsValid ? h.DatabaseSizeInHeader : (uint)Math.Min(uint.MaxValue, (_fileBytes + _pageSize - 1) / _pageSize);
                    _origSize = _dbSize;
                    _hdrSnapOk = _wal != null && _cipher is null;
                    if (_hdrSnapOk)
                    {
                        _hdrSnap = _wal!.Snap;
                    }

                    _rdStep = 0;
                    _state = TxState.Read;
                    return PagerStatus.Ok;
                }

                default:
                    throw new InvalidOperationException("bad read step");
            }
        }
    }

    /// <summary>Ends a read transaction and releases the file lock. All pages must have been released. Not allowed in a write transaction.</summary>
    /// <returns>The status.</returns>
    public PagerStatus EndRead()
    {
        Guard();
        if (_state == TxState.Write)
        {
            throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "commit or roll back before ending the read transaction");
        }

        if (_wal != null)
        {
            return WalEndReadStep();
        }

        _rdStep = 0;
        if (_lock != LockLevel.None)
        {
            if (!Io(ResumeState.EndReadUnlock, new IoRequest { Kind = IoKind.Unlock, File = _dbFile, Arg = (int)LockLevel.None }))
            {
                return PagerStatus.Pending;
            }

            _lock = LockLevel.None;
        }

        _state = TxState.None;
        return PagerStatus.Ok;
    }

    // ---- hot journal inspection and playback (atomiccommit §4; fileformat2 "The Rollback Journal") -------------------------------

    /// <summary>
    /// Reads the journal header and trailer. Sets <c>_pbHot</c> (header valid, record count non-zero, super-journal present if named) and
    /// <c>_pbStale</c> (it names a super-journal that no longer exists, so the transaction had committed).
    /// </summary>
    private PagerStatus PlaybackPrepare(bool checkSuper)
    {
        IVfsFile? f = _pbFile;
        for (; ; )
        {
            switch (_ppStep)
            {
                case 0:
                    _pbHot = false;
                    _pbStale = false;
                    _pbSuper = null;
                    if (!Io(ResumeState.HotJournalSize, new IoRequest { Kind = IoKind.FileSize, File = f, Cancellable = true }))
                    {
                        return PagerStatus.Pending;
                    }

                    if (_comp.Status != 0)
                    {
                        return FailIo(_comp.Status, "getting the journal size failed");
                    }

                    _pbSize = _comp.Value;
                    if (_pbSize < JournalFormat.HeaderFields)
                    {
                        _ppStep = 0;
                        return PagerStatus.Ok;
                    }

                    _ppStep = 1;
                    break;
                case 1:
                    if (!Io(ResumeState.HotJournalHeader, new IoRequest { Kind = IoKind.Read, File = f, Offset = 0, Length = _pbHdr.Length, Data = _pbHdr, Cancellable = true }))
                    {
                        return PagerStatus.Pending;
                    }

                    if (_comp.Status != 0)
                    {
                        return FailIo(_comp.Status, "reading the journal header failed");
                    }

                    if (_comp.Value < _pbHdr.Length || !JournalFormat.TryParseHeader(_pbHdr, out _pbFirst) || _pbFirst.RecordCount == 0)
                    {
                        // fileformat2: a journal is valid only with a valid header; a zero count is a journal whose header was never finished.
                        _ppStep = 0;
                        return PagerStatus.Ok;
                    }

                    _pbEnd = _pbSize;
                    _ppStep = 5;
                    break;
                case 5:
                    // A hostile header can claim a database of terabytes; playback would then write and truncate that far. A real journal's
                    // initial size is close to the database's current size, so a claim far beyond it is refused.
                    if (!Io(ResumeState.HotJournalSize, new IoRequest { Kind = IoKind.FileSize, File = _dbFile, Cancellable = true }))
                    {
                        return PagerStatus.Pending;
                    }

                    if (_comp.Status != 0)
                    {
                        return FailIo(_comp.Status, "getting the database size failed");
                    }

                    if ((long)_pbFirst.InitialPages * _pbFirst.PageSize > _comp.Value + (1L << 30))
                    {
                        return Fail(ResultCode.Corrupt, $"the journal says the database had {_pbFirst.InitialPages} pages of {_pbFirst.PageSize} bytes, far more than the {_comp.Value} bytes it has");
                    }

                    _ppStep = 2;
                    break;
                case 2:
                    if (!Io(ResumeState.HotJournalTail, new IoRequest { Kind = IoKind.Read, File = f, Offset = _pbSize - 16, Length = 16, Data = _pbTail, Cancellable = true }))
                    {
                        return PagerStatus.Pending;
                    }

                    if (_comp.Status != 0)
                    {
                        return FailIo(_comp.Status, "reading the journal trailer failed");
                    }

                    if (_comp.Value == 16 && JournalFormat.TryParseTrailerTail(_pbTail, _pbSize, out _pbNameLen, out _pbNameSum))
                    {
                        _ppStep = 3;
                        break;
                    }

                    _pbHot = true;
                    _ppStep = 0;
                    return PagerStatus.Ok;
                case 3:
                    _pbName ??= new byte[JournalFormat.MaxSuperJournalName];
                    if (!Io(ResumeState.HotJournalName, new IoRequest { Kind = IoKind.Read, File = f, Offset = _pbSize - JournalFormat.TrailerSize(_pbNameLen) + 4, Length = _pbNameLen, Data = _pbName, Cancellable = true }))
                    {
                        return PagerStatus.Pending;
                    }

                    if (_comp.Status != 0)
                    {
                        return FailIo(_comp.Status, "reading the super-journal name failed");
                    }

                    {
                        uint sum = 0;
                        for (int i = 0; i < _pbNameLen; i++)
                        {
                            sum += _pbName[i];
                        }

                        if (_comp.Value != _pbNameLen || sum != _pbNameSum)
                        {
                            // Not a trailer after all (page data that happens to end in the magic); treat the whole file as records.
                            _pbHot = true;
                            _ppStep = 0;
                            return PagerStatus.Ok;
                        }
                    }

                    _pbSuper = System.Text.Encoding.UTF8.GetString(_pbName, 0, _pbNameLen);
                    _pbEnd = _pbSize - JournalFormat.TrailerSize(_pbNameLen);
                    if (!checkSuper)
                    {
                        _pbHot = true;
                        _ppStep = 0;
                        return PagerStatus.Ok;
                    }

                    _ppStep = 4;
                    break;
                default:
                    if (!Io(ResumeState.HotJournalSuper, new IoRequest { Kind = IoKind.Access, Path = _pbSuper, Cancellable = true }))
                    {
                        return PagerStatus.Pending;
                    }

                    if (_comp.Status != 0)
                    {
                        return FailIo(_comp.Status, "checking the super-journal failed");
                    }

                    _pbHot = _comp.Value == 1;
                    _pbStale = !_pbHot;
                    _ppStep = 0;
                    return PagerStatus.Ok;
            }
        }
    }

    /// <summary>
    /// Plays the journal back into the database (atomiccommit §4.2): for every segment, every record whose checksum is good is written to its
    /// page; the first bad record ends the playback; then the database is cut to its initial size, synced, and the journal retired by the
    /// journal mode. Idempotent: it can be run again from the start after a crash. Caller holds EXCLUSIVE and has run PlaybackPrepare.
    /// </summary>
    private PagerStatus RunPlayback(ref IVfsFile? journal)
    {
        for (; ; )
        {
            switch (_pbStep)
            {
                case 0:
                {
                    long segOff = _pbSegOff;
                    if (segOff + JournalFormat.HeaderFields > _pbEnd)
                    {
                        _pbStep = 20;
                        break;
                    }

                    if (!Io(ResumeState.PlaybackHeader, new IoRequest { Kind = IoKind.Read, File = journal, Offset = segOff, Length = _pbHdr.Length, Data = _pbHdr }))
                    {
                        return PagerStatus.Pending;
                    }

                    if (_comp.Status != 0)
                    {
                        return FailIo(_comp.Status, "reading a journal header failed");
                    }

                    if (_comp.Value < _pbHdr.Length || !JournalFormat.TryParseHeader(_pbHdr, out _pbSeg) || _pbSeg.PageSize != _pbFirst.PageSize || _pbSeg.SectorSize != _pbFirst.SectorSize)
                    {
                        _pbStep = 20;
                        break;
                    }

                    _pbRecStart = segOff + _pbSeg.SectorSize;
                    long recSize = _pbSeg.PageSize + 8L;
                    long fit = Math.Max(0, (_pbEnd - _pbRecStart) / recSize);
                    _pbRecCount = _pbSeg.RecordCount == JournalFormat.CountToEnd ? fit : Math.Min(_pbSeg.RecordCount, fit);
                    if (_pbRecCount == 0)
                    {
                        _pbStep = 20;
                        break;
                    }

                    _pbRec ??= new byte[_pbFirst.PageSize + 8];
                    _pbRecIdx = 0;
                    _pbStep = 1;
                    break;
                }

                case 1:
                {
                    long recSize = _pbSeg.PageSize + 8L;
                    if (_pbRecIdx >= _pbRecCount)
                    {
                        long end = _pbRecStart + _pbRecCount * recSize;
                        long sector = _pbSeg.SectorSize;
                        _pbSegOff = (end + sector - 1) / sector * sector;
                        _pbStep = 0;
                        break;
                    }

                    if (!Io(ResumeState.PlaybackRecord, new IoRequest { Kind = IoKind.Read, File = journal, Offset = _pbRecStart + _pbRecIdx * recSize, Length = (int)recSize, Data = _pbRec }))
                    {
                        return PagerStatus.Pending;
                    }

                    if (_comp.Status != 0)
                    {
                        return FailIo(_comp.Status, "reading a journal record failed");
                    }

                    if (_comp.Value < recSize)
                    {
                        _pbStep = 20;
                        break;
                    }

                    int ps = _pbSeg.PageSize;
                    uint stored = Be32(_pbRec.AsSpan(4 + ps));
                    if (JournalFormat.Checksum(_pbSeg.Nonce, _pbRec.AsSpan(4, ps)) != stored)
                    {
                        // A torn or stale record: nothing after it can be trusted (the database was not yet changed past this point).
                        _pbStep = 20;
                        break;
                    }

                    _pbPgno = Be32(_pbRec);
                    if (_pbPgno == 0 || _pbPgno > _pbFirst.InitialPages || _pbPgno == (uint)(0x40000000L / ps) + 1)
                    {
                        _pbRecIdx++;
                        break;
                    }

                    _pbStep = 2;
                    break;
                }

                case 2:
                {
                    int ps = _pbSeg.PageSize;
                    if (!Io(ResumeState.PlaybackWritePage, new IoRequest { Kind = IoKind.Write, File = _dbFile, Offset = (long)(_pbPgno - 1) * ps, Length = ps, Data = _pbRec.AsMemory(4, ps) }))
                    {
                        return PagerStatus.Pending;
                    }

                    if (_comp.Status != 0)
                    {
                        return FailIo(_comp.Status, $"restoring page {_pbPgno} failed");
                    }

                    _pbRecIdx++;
                    _pbStep = 1;
                    break;
                }

                case 20:
                    if (!Io(ResumeState.PlaybackTruncate, new IoRequest { Kind = IoKind.Truncate, File = _dbFile, Offset = (long)_pbFirst.InitialPages * _pbFirst.PageSize }))
                    {
                        return PagerStatus.Pending;
                    }

                    if (_comp.Status != 0)
                    {
                        return FailIo(_comp.Status, "truncating the database failed");
                    }

                    _fileBytes = (long)_pbFirst.InitialPages * _pbFirst.PageSize;
                    _pbStep = 21;
                    break;
                case 21:
                    if (_sync != SynchronousMode.Off)
                    {
                        if (!Io(ResumeState.PlaybackSyncDb, new IoRequest { Kind = IoKind.Sync, File = _dbFile, Arg = (int)SyncKind() }))
                        {
                            return PagerStatus.Pending;
                        }

                        if (_comp.Status != 0)
                        {
                            return FailIo(_comp.Status, "syncing the database failed");
                        }
                    }

                    _pbStep = 22;
                    break;
                default:
                {
                    PagerStatus s = FinalizeJournal(ref journal);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _pbStep = 0;
                    _pbSegOff = 0;
                    _dbModified = false;
                    return PagerStatus.Ok;
                }
            }
        }
    }

    private SyncMode SyncKind() => _opts.FullFsync ? SyncMode.Full : SyncMode.Normal;

    /// <summary>
    /// Retires a journal after commit, rollback or recovery according to the journal mode (fileformat2): DELETE removes the file,
    /// TRUNCATE cuts it to zero length, PERSIST overwrites the start of its header with zeros. The reference syncs after TRUNCATE and
    /// PERSIST except at synchronous=OFF (TRUNCATE: FULL and above only), and syncs the directory after DELETE only at EXTRA; we also do it at FULL so that an acknowledged commit survives power loss (docs/durability.md).
    /// </summary>
    private PagerStatus FinalizeJournal(ref IVfsFile? file, bool hasSuperJournal = false)
    {
        // A journal that names a super-journal is truncated even in PERSIST mode (observed in the reference): once the super-journal is gone, a
        // zeroed header would be the only sign of the commit, and the name at the end of a stale journal could not be told from old data.
        JournalMode mode = _journalMode == JournalMode.Persist && hasSuperJournal ? JournalMode.Truncate
            : _journalMode is JournalMode.Truncate or JournalMode.Persist ? _journalMode : JournalMode.Delete;
        if (file == null && mode != JournalMode.Delete)
        {
            return PagerStatus.Ok;
        }

        for (; ; )
        {
            switch (mode)
            {
                case JournalMode.Delete:
                    if (_fjStep == 0)
                    {
                        file?.Dispose();
                        file = null;
                        _fjStep = 1;
                    }

                    if (!Io(ResumeState.FinalizeDelete, new IoRequest { Kind = IoKind.Delete, Path = _journalPath, Arg = _sync >= SynchronousMode.Full ? 1 : 0 }))
                    {
                        return PagerStatus.Pending;
                    }

                    _fjStep = 0;
                    if (_comp.Status != 0 && _comp.Status != ResultCode.IoErrDeleteNoEnt)
                    {
                        return FailIo(_comp.Status, "deleting the journal failed");
                    }

                    return PagerStatus.Ok;
                case JournalMode.Truncate:
                    if (_fjStep == 0)
                    {
                        if (!Io(ResumeState.FinalizeTruncate, new IoRequest { Kind = IoKind.Truncate, File = file, Offset = 0 }))
                        {
                            return PagerStatus.Pending;
                        }

                        if (_comp.Status != 0)
                        {
                            _fjStep = 0;
                            return FailIo(_comp.Status, "truncating the journal failed");
                        }

                        _fjStep = 1;
                    }

                    if (_sync >= SynchronousMode.Full)
                    {
                        if (!Io(ResumeState.FinalizeSync, new IoRequest { Kind = IoKind.Sync, File = file, Arg = (int)SyncKind() }))
                        {
                            return PagerStatus.Pending;
                        }

                        if (_comp.Status != 0)
                        {
                            _fjStep = 0;
                            return FailIo(_comp.Status, "syncing the journal failed");
                        }
                    }

                    _fjStep = 0;
                    file?.Dispose();
                    file = null;
                    return PagerStatus.Ok;
                default:
                    if (_fjStep == 0)
                    {
                        _zeroHeader ??= new byte[JournalFormat.HeaderFields];
                        if (!Io(ResumeState.FinalizeZeroHeader, new IoRequest { Kind = IoKind.Write, File = file, Offset = 0, Length = JournalFormat.HeaderFields, Data = _zeroHeader }))
                        {
                            return PagerStatus.Pending;
                        }

                        if (_comp.Status != 0)
                        {
                            _fjStep = 0;
                            return FailIo(_comp.Status, "clearing the journal header failed");
                        }

                        _fjStep = 1;
                    }

                    if (_sync != SynchronousMode.Off)
                    {
                        if (!Io(ResumeState.FinalizeSync, new IoRequest { Kind = IoKind.Sync, File = file, Arg = (int)SyncKind() }))
                        {
                            return PagerStatus.Pending;
                        }

                        if (_comp.Status != 0)
                        {
                            _fjStep = 0;
                            return FailIo(_comp.Status, "syncing the journal failed");
                        }
                    }

                    _fjStep = 0;
                    file?.Dispose();
                    file = null;
                    return PagerStatus.Ok;
            }
        }
    }

    private byte[]? _zeroHeader;
}
