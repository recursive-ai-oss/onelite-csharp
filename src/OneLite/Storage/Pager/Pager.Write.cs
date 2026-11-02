using System.Buffers.Binary;
using System.Security.Cryptography;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Storage.Pager;

public sealed partial class Pager
{
    private sealed class Savepoint
    {
        public uint DbSize;
        public int EntryStart;

        // Frames remember the savepoint that last copied them by this number, not by level: a level is reused by every statement.
        public int Serial;
    }

    private uint _largestRootAtBegin;
    private IVfsFile? _jfile;
    private byte[] _jbuf = [];
    private int _jbufLen;
    private long _jWriteOffset;
    private long _jSegHeaderOffset;
    private uint _jSegCount;
    private uint _jNonce;
    private int _jSector;
    private bool _jNeedNewSegment = true;
    private bool _jUnsynced;
    private bool _jDirSynced;
    private bool _jTrailerDone;
    private long _jStaleEnd;
    private readonly PageBitmap _jBits = new();
    private readonly List<(uint Page, byte[] Data)> _jMem = [];
    private readonly byte[] _h12 = new byte[12];

    private byte[]? _guard;
    private int _ajStep;
    private int _sjStep;
    private int _rbStep;
    private int _rsIdx;
    private int _spLevel;
    private int _spSerialCounter;
    private int _spCur;
    private readonly List<Savepoint> _sps = [];
    private readonly Stack<Savepoint> _spObjects = new();
    private readonly List<(uint Page, byte[] Data)> _spEntries = [];
    private readonly Stack<byte[]> _spPool = new();

    /// <summary>
    /// Starts a write transaction: takes the RESERVED lock (lockingv3 §3.0). The journal file is created when the first page needs saving.
    /// Requires a read transaction.
    /// </summary>
    /// <returns>The status.</returns>
    /// <exception cref="OneLiteException"><c>SQLITE_MISUSE</c> outside a read transaction; the call fails with <c>SQLITE_READONLY</c> on a read-only database and <c>SQLITE_BUSY</c> if another connection holds RESERVED or more.</exception>
    public PagerStatus BeginWrite() => BeginWrite(false);

    /// <summary>As <see cref="BeginWrite()"/>; <paramref name="exclusive"/> also takes the EXCLUSIVE lock in rollback mode (BEGIN EXCLUSIVE), keeping readers out.</summary>
    /// <param name="exclusive">True for BEGIN EXCLUSIVE.</param>
    /// <returns>The status.</returns>
    public PagerStatus BeginWrite(bool exclusive) => BeginWrite(exclusive, false);

    /// <summary>
    /// As <see cref="BeginWrite(bool)"/>. With <paramref name="noWait"/> a RESERVED lock another connection holds is reported as BUSY at once
    /// instead of running the busy policy while this connection keeps its SHARED lock: a writer that is itself waiting for the readers to leave
    /// would never get them to, so the caller lets go of the read transaction first and waits without holding anything (c3ref busy_handler).
    /// </summary>
    /// <param name="exclusive">True for BEGIN EXCLUSIVE.</param>
    /// <param name="noWait">True to skip the busy policy for the RESERVED lock.</param>
    /// <returns>The status.</returns>
    public PagerStatus BeginWrite(bool exclusive, bool noWait)
    {
        Guard();
        if (_state == TxState.None)
        {
            throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "BeginWrite outside a read transaction");
        }

        if (_state == TxState.Write)
        {
            // A write has already begun. Observed on the reference: BEGIN EXCLUSIVE here takes no further lock (it then fails with "cannot start
            // a transaction within a transaction", and other connections can still read).
            return PagerStatus.Ok;
        }

        if (_opts.ReadOnly)
        {
            return Fail(ResultCode.ReadOnly, "the database is read-only");
        }

        PagerStatus s = _wal != null ? WalBeginWriteStep() : TakeLock(LockLevel.Reserved, !noWait);
        if (s != PagerStatus.Ok)
        {
            return s;
        }

        if (exclusive && _wal == null)
        {
            // lang_transaction: in rollback mode EXCLUSIVE keeps readers out from BEGIN on; in WAL mode it is the same as IMMEDIATE.
            s = TakeLock(LockLevel.Exclusive);
            if (s != PagerStatus.Ok)
            {
                return s;
            }
        }

        ResetWriteState();
        _origSize = _dbSize;
        _largestRootAtBegin = LargestRootPage;
        _autoVacuumAtBegin = _autoVacuum;
        _incrementalAtBegin = _incremental;
        _state = TxState.Write;
        return PagerStatus.Ok;
    }

    private void ResetWriteState()
    {
        _rcActive = false;
        _jBits.Clear();
        _rsvRecords = 0;
        _rsvFrames = 0;
        _rsvStale = false;
        ReleaseMem(_jMem);
        ReleaseSavepointEntries();
        _sps.Clear();
        _spLevel = 0;
        _spCur = 0;
        _jbufLen = 0;
        _jWriteOffset = 0;
        _jSegCount = 0;
        _jNeedNewSegment = true;
        _jUnsynced = false;
        _jDirSynced = false;
        _jTrailerDone = false;
        _dbModified = false;
        _dirty.Clear();
        foreach (PageFrame f in _frames)
        {
            f.Dirty = false;
            f.SavedLevel = 0;
        }
    }

    private static void ReleaseMem(List<(uint Page, byte[] Data)> list) => list.Clear();

    private void ReleaseSavepointEntries()
    {
        foreach ((uint _, byte[] data) in _spEntries)
        {
            _spPool.Push(data);
        }

        _spEntries.Clear();
    }

    private byte[] RentCopy(byte[] src)
    {
        byte[] b = _spPool.Count > 0 && _spPool.Peek().Length == src.Length ? _spPool.Pop() : new byte[src.Length];
        Buffer.BlockCopy(src, 0, b, 0, src.Length);
        return b;
    }

    /// <summary>
    /// Prepares a pinned page for modification. Call it before every batch of edits: it saves the page's original content in the journal
    /// (once per transaction, and only for pages that existed when the transaction began; freelist leaf pages are not saved,
    /// fileformat2 §3), saves it for any open savepoint, and marks it dirty. When nothing is left to do it is a cheap check.
    /// </summary>
    /// <param name="frame">A pinned frame.</param>
    /// <returns>The status.</returns>
    /// <exception cref="OneLiteException"><c>SQLITE_MISUSE</c> outside a write transaction.</exception>
    public PagerStatus MakeWritable(PageFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Guard();
        if (_state != TxState.Write)
        {
            throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "MakeWritable outside a write transaction");
        }

        if (frame.Dirty && frame.SavedLevel == _spCur)
        {
            return PagerStatus.Ok;
        }

        return MakeWritableCore(frame);
    }

    private PagerStatus MakeWritableCore(PageFrame frame)
    {
        PagerStatus s = EnsureJournaled(frame);
        if (s != PagerStatus.Ok)
        {
            return s;
        }

        SaveForSavepoint(frame);
        if (!frame.Dirty)
        {
            frame.Dirty = true;
            _dirty.Add(frame);
        }

        return PagerStatus.Ok;
    }

    // Saves the page's original content in the journal if the transaction has not done so and the page existed when it began.
    private PagerStatus EnsureJournaled(PageFrame frame)
    {
        if (_wal != null)
        {
            // WAL mode has no journal: the original stays in the database file or in an older frame (wal).
            return PagerStatus.Ok;
        }

        uint pgno = frame.PageNumber;
        if (pgno > _origSize || _jBits.Get(pgno))
        {
            return PagerStatus.Ok;
        }

        switch (_journalMode)
        {
            case JournalMode.Off:
                _jBits.Set(pgno);
                return PagerStatus.Ok;
            case JournalMode.Memory:
                _jMem.Add((pgno, (byte[])frame.Data.Clone()));
                _jBits.Set(pgno);
                return PagerStatus.Ok;
            default:
                return AppendJournalRecord(frame);
        }
    }

    // Keeps a copy for the innermost open savepoint the first time the page changes (or is freed) inside it.
    private void SaveForSavepoint(PageFrame frame)
    {
        if (_spLevel > 0 && frame.SavedLevel != _spCur && frame.PageNumber <= _sps[_spLevel - 1].DbSize)
        {
            _spEntries.Add((frame.PageNumber, RentCopy(frame.Data)));
        }

        frame.SavedLevel = _spCur;
    }

    // ---- the journal writer (fileformat2 "The Rollback Journal"; docs/durability.md) ---------------------------------------------

    private int RecordSize => _pageSize + 8;

    private PagerStatus AppendJournalRecord(PageFrame frame)
    {
        for (; ; )
        {
            switch (_ajStep)
            {
                case 0:
                    if (_jfile == null)
                    {
                        if (!Io(ResumeState.JournalOpen, new IoRequest { Kind = IoKind.Open, Path = _journalPath, Arg = (int)(VfsOpenOptions.ReadWrite | VfsOpenOptions.Create | VfsOpenOptions.MainJournal), Cancellable = true }))
                        {
                            return PagerStatus.Pending;
                        }

                        if (_comp.Status != 0)
                        {
                            return FailIo(_comp.Status, "creating the journal failed");
                        }

                        _jfile = _comp.File;
                        InitJournalState();
                        _ajStep = _journalMode == JournalMode.Persist ? 2 : 1;
                        break;
                    }

                    _ajStep = 1;
                    break;
                case 2:
                    // A PERSIST journal is reused and may hold stale segments from an older transaction after what we write now.
                    if (!Io(ResumeState.JournalOpen, new IoRequest { Kind = IoKind.FileSize, File = _jfile, Cancellable = true }))
                    {
                        return PagerStatus.Pending;
                    }

                    if (_comp.Status != 0)
                    {
                        return FailIo(_comp.Status, "getting the journal size failed");
                    }

                    _jStaleEnd = _comp.Value;
                    _ajStep = 1;
                    break;
                case 1:
                {
                    if (_ajOpenOnly)
                    {
                        // ReserveJournal only wanted the file open (and, in PERSIST mode, measured).
                        _ajOpenOnly = false;
                        _ajStep = 0;
                        return PagerStatus.Ok;
                    }

                    int need = RecordSize + (_jNeedNewSegment ? 2 * _jSector : 0);
                    if (_jbufLen + need > _jbuf.Length)
                    {
                        PagerStatus s = FlushJournalBuffer();
                        if (s != PagerStatus.Ok)
                        {
                            return s;
                        }
                    }

                    if (_jNeedNewSegment)
                    {
                        StartSegment();
                    }

                    Span<byte> rec = _jbuf.AsSpan(_jbufLen, RecordSize);
                    BinaryPrimitives.WriteUInt32BigEndian(rec, frame.PageNumber);
                    if (_cipher is null)
                    {
                        frame.Data.CopyTo(rec[4..]);
                        BinaryPrimitives.WriteUInt32BigEndian(rec[(4 + _pageSize)..], JournalFormat.Checksum(_jNonce, frame.Data));
                    }
                    else
                    {
                        // The record holds the encoded page and its checksum runs over those stored bytes (observed on the reference's journals).
                        EncodeForJournal(frame.PageNumber, frame.Data, rec.Slice(4, _pageSize));
                        BinaryPrimitives.WriteUInt32BigEndian(rec[(4 + _pageSize)..], JournalFormat.Checksum(_jNonce, rec.Slice(4, _pageSize)));
                    }

                    _jbufLen += RecordSize;
                    _jSegCount++;
                    _jUnsynced = true;
                    frame.NeedSync = true;
                    _jBits.Set(frame.PageNumber);
                    _ajStep = 0;
                    return PagerStatus.Ok;
                }

                default:
                    throw new InvalidOperationException("bad journal append step");
            }
        }
    }

    private void InitJournalState()
    {
        // fileformat2: the reference treats a powersafe-overwrite device as having 512-byte sectors (observed; docs/durability.md).
        int sector = _dbFile!.Device.HasFlag(DeviceCharacteristics.PowersafeOverwrite) ? 512 : Math.Clamp(_dbFile.SectorSize, 512, 65536);
        _jSector = sector;
        int want = Math.Max(65536, 4 * (RecordSize + 2 * sector));
        if (_jbuf.Length < want)
        {
            _jbuf = new byte[want];
        }

        _jbufLen = 0;
        _jWriteOffset = 0;
        _jNeedNewSegment = true;
        _jUnsynced = false;
        _jDirSynced = false;
        _jTrailerDone = false;
        _jStaleEnd = 0;
    }

    // Appends a zero-padded header block for a new segment to the buffer (atomiccommit §3.5: the header sits alone in a sector).
    private void StartSegment()
    {
        long abs = _jWriteOffset + _jbufLen;
        int pad = (int)((_jSector - (abs % _jSector)) % _jSector);
        _jbuf.AsSpan(_jbufLen, pad + _jSector).Clear();
        _jSegHeaderOffset = abs + pad;
        bool off = _sync == SynchronousMode.Off;

        // Observed: every segment header carries its own random nonce; its records are checksummed with it.
        _jNonce = unchecked((uint)RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue));

        // Without syncs there is no later header rewrite, so the count is "to the end of the file" from the start.
        JournalFormat.WriteHeader(_jbuf.AsSpan(_jbufLen + pad, _jSector), withMagic: off, off ? JournalFormat.CountToEnd : 0, _jNonce, _origSize, _jSector, _pageSize);
        _jbufLen += pad + _jSector;
        _jSegCount = 0;
        _jNeedNewSegment = false;
    }

    private PagerStatus FlushJournalBuffer()
    {
        if (_jbufLen == 0)
        {
            return PagerStatus.Ok;
        }

        if (!Io(ResumeState.JournalFlush, new IoRequest { Kind = IoKind.Write, File = _jfile, Offset = _jWriteOffset, Length = _jbufLen, Data = _jbuf, Cancellable = _journalFlushCancellable }))
        {
            return PagerStatus.Pending;
        }

        if (_comp.Status != 0)
        {
            return FailIo(_comp.Status, "writing the journal failed");
        }

        _jWriteOffset += _jbufLen;
        _jbufLen = 0;
        return PagerStatus.Ok;
    }

    private bool _journalFlushCancellable = true;

    /// <summary>
    /// Makes the journal's content durable before the database is touched (atomiccommit §3.6-3.8). At synchronous=FULL/EXTRA: sync the records,
    /// sync the directory (first time), write the record count into the header, sync again. At NORMAL the count is written before one sync.
    /// At OFF nothing is synced. With <paramref name="superName"/> the super-journal trailer is added first (atomiccommit §5.3).
    /// </summary>
    private PagerStatus SyncJournal(string? superName)
    {
        if (_jfile == null || _journalMode is JournalMode.Memory or JournalMode.Off || (!_jUnsynced && (superName == null || _jTrailerDone)))
        {
            return PagerStatus.Ok;
        }

        for (; ; )
        {
            switch (_sjStep)
            {
                case 0:
                    if (superName != null && !_jTrailerDone)
                    {
                        byte[] name = System.Text.Encoding.UTF8.GetBytes(superName);
                        int size = JournalFormat.TrailerSize(name.Length);
                        if (_jbufLen + size > _jbuf.Length)
                        {
                            PagerStatus f = FlushJournalBuffer();
                            if (f != PagerStatus.Ok)
                            {
                                return f;
                            }
                        }

                        JournalFormat.WriteTrailer(_jbuf.AsSpan(_jbufLen, size), LockBytePage, name);
                        _jbufLen += size;
                        _jTrailerDone = true;
                    }

                    _sjStep = 1;
                    break;
                case 1:
                {
                    PagerStatus f = FlushJournalBuffer();
                    if (f != PagerStatus.Ok)
                    {
                        return f;
                    }

                    if (_jTrailerDone && _journalMode == JournalMode.Persist && _jStaleEnd > _jWriteOffset)
                    {
                        // atomiccommit §5: a reader finds the super-journal's name at the very end of the journal, so a reused (PERSIST) journal is cut
                        // right after the name (observed: the reference truncates it there before it writes the header).
                        if (!Io(ResumeState.JournalFlush, new IoRequest { Kind = IoKind.Truncate, File = _jfile, Offset = _jWriteOffset }))
                        {
                            return PagerStatus.Pending;
                        }

                        if (_comp.Status != 0)
                        {
                            return FailIo(_comp.Status, "truncating the journal failed");
                        }

                        _jStaleEnd = 0;
                    }

                    long at = (_jWriteOffset + _jSector - 1) / _jSector * _jSector;
                    if (at < _jStaleEnd)
                    {
                        // Put an empty sector where the next segment would start, so an old segment further on is never mistaken for ours.
                        _guard ??= new byte[65536];
                        if (!Io(ResumeState.JournalFlush, new IoRequest { Kind = IoKind.Write, File = _jfile, Offset = at, Length = _jSector, Data = _guard }))
                        {
                            return PagerStatus.Pending;
                        }

                        if (_comp.Status != 0)
                        {
                            return FailIo(_comp.Status, "writing the journal failed");
                        }
                    }

                    _sjStep = _sync == SynchronousMode.Off ? 9 : _sync == SynchronousMode.Normal ? 2 : 4;
                    break;
                }

                case 2:
                    // NORMAL: header (magic + count) then one sync.
                    PutHeader12();
                    if (!Io(ResumeState.JournalHeader, new IoRequest { Kind = IoKind.Write, File = _jfile, Offset = _jSegHeaderOffset, Length = 12, Data = _h12 }))
                    {
                        return PagerStatus.Pending;
                    }

                    if (_comp.Status != 0)
                    {
                        return FailIo(_comp.Status, "writing the journal header failed");
                    }

                    _sjStep = 3;
                    break;
                case 3:
                    if (!Io(ResumeState.JournalSync1, new IoRequest { Kind = IoKind.Sync, File = _jfile, Arg = (int)SyncKind() }))
                    {
                        return PagerStatus.Pending;
                    }

                    if (_comp.Status != 0)
                    {
                        return FailIo(_comp.Status, "syncing the journal failed");
                    }

                    _sjStep = 7;
                    break;
                case 4:
                    // FULL and EXTRA: records, sync, directory, header, sync.
                    if (!Io(ResumeState.JournalSync1, new IoRequest { Kind = IoKind.Sync, File = _jfile, Arg = (int)SyncKind() }))
                    {
                        return PagerStatus.Pending;
                    }

                    if (_comp.Status != 0)
                    {
                        return FailIo(_comp.Status, "syncing the journal failed");
                    }

                    _sjStep = 5;
                    break;
                case 5:
                    if (!_jDirSynced)
                    {
                        if (!Io(ResumeState.JournalDirSync, new IoRequest { Kind = IoKind.SyncDirectory, File = _jfile }))
                        {
                            return PagerStatus.Pending;
                        }

                        if (_comp.Status != 0)
                        {
                            return FailIo(_comp.Status, "syncing the directory failed");
                        }

                        _jDirSynced = true;
                    }

                    _sjStep = 6;
                    break;
                case 6:
                    PutHeader12();
                    if (!Io(ResumeState.JournalHeader, new IoRequest { Kind = IoKind.Write, File = _jfile, Offset = _jSegHeaderOffset, Length = 12, Data = _h12 }))
                    {
                        return PagerStatus.Pending;
                    }

                    if (_comp.Status != 0)
                    {
                        return FailIo(_comp.Status, "writing the journal header failed");
                    }

                    _sjStep = 8;
                    break;
                case 8:
                    if (!Io(ResumeState.JournalSync2, new IoRequest { Kind = IoKind.Sync, File = _jfile, Arg = (int)SyncKind() }))
                    {
                        return PagerStatus.Pending;
                    }

                    if (_comp.Status != 0)
                    {
                        return FailIo(_comp.Status, "syncing the journal failed");
                    }

                    _sjStep = 9;
                    break;
                case 7:
                    if (!_jDirSynced)
                    {
                        if (!Io(ResumeState.JournalDirSync, new IoRequest { Kind = IoKind.SyncDirectory, File = _jfile }))
                        {
                            return PagerStatus.Pending;
                        }

                        if (_comp.Status != 0)
                        {
                            return FailIo(_comp.Status, "syncing the directory failed");
                        }

                        _jDirSynced = true;
                    }

                    _sjStep = 9;
                    break;
                default:
                    _sjStep = 0;
                    _jUnsynced = false;
                    foreach (PageFrame d in _dirty)
                    {
                        d.NeedSync = false;
                    }

                    _jNeedNewSegment = true;
                    return PagerStatus.Ok;
            }
        }
    }

    private void PutHeader12()
    {
        JournalFormat.Magic.CopyTo(_h12);
        BinaryPrimitives.WriteUInt32BigEndian(_h12.AsSpan(8), _jSegCount);
    }

    // ---- savepoints and statement journals (tempfiles; kept in memory) -----------------------------------------------------------

    /// <summary>
    /// Opens a savepoint (or a statement journal: a statement is a savepoint that is released or rolled back when it ends). Pages modified
    /// afterwards are saved so <see cref="RollbackSavepoint"/> can restore them. The saved copies are kept in memory.
    /// </summary>
    /// <returns>The savepoint's level, starting at 1 for the outermost.</returns>
    public int OpenSavepoint()
    {
        Guard();
        if (_state != TxState.Write)
        {
            throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "savepoints need a write transaction");
        }

        Savepoint sp = _spObjects.Count > 0 ? _spObjects.Pop() : new Savepoint();
        sp.DbSize = _dbSize;
        sp.EntryStart = _spEntries.Count;
        sp.Serial = ++_spSerialCounter;
        _sps.Add(sp);
        _spLevel = _sps.Count;
        _spCur = sp.Serial;
        return _spLevel;
    }

    /// <summary>Releases the savepoint and every one opened after it. Its changes stay part of the enclosing transaction.</summary>
    /// <param name="level">A level returned by <see cref="OpenSavepoint"/>.</param>
    public void ReleaseSavepoint(int level)
    {
        Guard();
        CheckLevel(level);
        for (int i = level - 1; i < _sps.Count; i++)
        {
            _spObjects.Push(_sps[i]);
        }

        _sps.RemoveRange(level - 1, _sps.Count - (level - 1));
        _spLevel = level - 1;
        _spCur = _spLevel > 0 ? _sps[_spLevel - 1].Serial : 0;
        if (_spLevel == 0)
        {
            ReleaseSavepointEntries();
        }
    }

    private void CheckLevel(int level)
    {
        if (level < 1 || level > _sps.Count)
        {
            throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "no such savepoint level " + level);
        }
    }

    /// <summary>
    /// Undoes every change made since the savepoint was opened. The savepoint stays open (as <c>ROLLBACK TO</c> does) and savepoints opened
    /// after it are dropped. Re-entrant: it may need a frame, and so may suspend.
    /// </summary>
    /// <param name="level">A level returned by <see cref="OpenSavepoint"/>.</param>
    /// <returns>The status.</returns>
    public PagerStatus RollbackSavepoint(int level)
    {
        Guard();
        CheckLevel(level);
        Savepoint sp = _sps[level - 1];
        if (_rsIdx == 0)
        {
            _rsIdx = _spEntries.Count;
            if (_dbSize < sp.DbSize)
            {
                _dbSize = sp.DbSize;
            }
        }

        while (_rsIdx > sp.EntryStart)
        {
            (uint page, byte[] data) = _spEntries[_rsIdx - 1];
            PagerStatus s = GetCore(page, GetOptions.NoContent, out PageFrame? f);
            if (s != PagerStatus.Ok)
            {
                return s;
            }

            Buffer.BlockCopy(data, 0, f!.Data, 0, data.Length);
            if (!f.Dirty)
            {
                f.Dirty = true;
                _dirty.Add(f);
            }

            f.Pins--;
            _rsIdx--;
        }

        for (int i = _spEntries.Count - 1; i >= sp.EntryStart; i--)
        {
            _spPool.Push(_spEntries[i].Data);
        }

        _spEntries.RemoveRange(sp.EntryStart, _spEntries.Count - sp.EntryStart);
        _sps.RemoveRange(level, _sps.Count - level);
        _dbSize = sp.DbSize;
        _spLevel = level;
        _spCur = sp.Serial;
        int outer = level > 1 ? _sps[level - 2].Serial : 0;
        foreach (PageFrame f in _frames)
        {
            if (f.SavedLevel >= sp.Serial)
            {
                f.SavedLevel = outer;
            }
        }

        _rsIdx = 0;
        return PagerStatus.Ok;
    }

    // ---- rollback (atomiccommit §4) -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Ends the write transaction, undoing it. If no database page was written yet, the cached changes are dropped and the journal retired;
    /// otherwise the journal is played back under the EXCLUSIVE lock already held. Afterwards the pager is in a read transaction holding SHARED.
    /// With <see cref="JournalMode.Off"/> pages written by a spill cannot be restored.
    /// </summary>
    /// <returns>The status.</returns>
    public PagerStatus Rollback()
    {
        Guard();
        if (_state != TxState.Write)
        {
            return PagerStatus.Ok;
        }

        if (_wal != null)
        {
            return RollbackWal();
        }

        for (; ; )
        {
            switch (_rbStep)
            {
                case 0:
                    _rbStep = _dbModified && _journalMode is not (JournalMode.Off) ? (_journalMode == JournalMode.Memory ? 20 : 10) : 1;
                    break;
                case 10:
                    _pbFile = _jfile;
                    _ppStep = 0;
                    _rbStep = 11;
                    break;
                case 11:
                {
                    // Records still in the buffer protect pages that were never overwritten; only the flushed file matters.
                    PagerStatus s = PlaybackPrepare(checkSuper: false);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _pbStep = 0;
                    _pbSegOff = 0;
                    if (!_pbHot)
                    {
                        return Fail(ResultCode.Corrupt, "the journal that protects the changed database is unreadable");
                    }

                    _rbStep = 12;
                    break;
                }

                case 12:
                {
                    PagerStatus s = RunPlayback(ref _jfile);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    DropAllFrames();
                    _rbStep = 3;
                    break;
                }

                case 20:
                {
                    // journal_mode=MEMORY: restore from the in-memory copies, newest first is not needed (each page is saved once).
                    while (_mbIdx < _jMem.Count)
                    {
                        (uint page, byte[] data) = _jMem[_mbIdx];
                        if (_cipher is not null && !Resuming(ResumeState.PlaybackWritePage))
                        {
                            _codec!.Encode(page, data, _ebuf!);
                        }

                        if (!Io(ResumeState.PlaybackWritePage, new IoRequest { Kind = IoKind.Write, File = _dbFile, Offset = (long)(page - 1) * _pageSize, Length = _pageSize, Data = _cipher is null ? data : _ebuf! }))
                        {
                            return PagerStatus.Pending;
                        }

                        if (_comp.Status != 0)
                        {
                            return FailIo(_comp.Status, $"restoring page {page} failed");
                        }

                        _mbIdx++;
                    }

                    _rbStep = 21;
                    break;
                }

                case 21:
                    if (!Io(ResumeState.PlaybackTruncate, new IoRequest { Kind = IoKind.Truncate, File = _dbFile, Offset = (long)_origSize * _pageSize }))
                    {
                        return PagerStatus.Pending;
                    }

                    if (_comp.Status != 0)
                    {
                        return FailIo(_comp.Status, "truncating the database failed");
                    }

                    _fileBytes = (long)_origSize * _pageSize;
                    _rbStep = 22;
                    break;
                case 22:
                    if (_sync != SynchronousMode.Off)
                    {
                        if (!Io(ResumeState.PlaybackSyncDb, new IoRequest { Kind = IoKind.Sync, File = _dbFile, Arg = (int)SyncKind() }))
                        {
                            return PagerStatus.Pending;
                        }
                    }

                    _mbIdx = 0;
                    DropAllFrames();
                    _rbStep = 3;
                    break;
                case 1:
                {
                    // The database file was never written: dropping the cached changes is the whole rollback.
                    foreach (PageFrame f in _dirty)
                    {
                        _cache.Remove(f.PageNumber);
                        f.PageNumber = 0;
                        f.Dirty = false;
                        f.Pins = 0;
                        _freeFrames.Push(f);
                    }

                    _dirty.Clear();
                    PagerStatus s = _jfile != null || _fjStep != 0 ? FinalizeJournal(ref _jfile) : PagerStatus.Ok;
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _rbStep = 3;
                    break;
                }

                case 3:
                    _dbSize = _origSize;
                    _largestRoot = _largestRootAtBegin;
                    _autoVacuum = _autoVacuumAtBegin;
                    _incremental = _incrementalAtBegin;
                    if (_dbModified)
                    {
                        _dbModified = false;
                    }

                    EndRekey(committed: false);
                    ResetWriteState();
                    _rbStep = 4;
                    break;
                default:
                    if (_lock > LockLevel.Shared)
                    {
                        if (!Io(ResumeState.CommitUnlock, new IoRequest { Kind = IoKind.Unlock, File = _dbFile, Arg = (int)LockLevel.Shared }))
                        {
                            return PagerStatus.Pending;
                        }

                        _lock = LockLevel.Shared;
                    }

                    _rbStep = 0;
                    _state = TxState.Read;
                    return PagerStatus.Ok;
            }
        }
    }

    // Forgets all half-finished operations (after an error, an abandon, or when an operation completes).
    private void ClearProgress()
    {
        _rdStep = 0;
        _walProbed = false;
        _ppStep = 0;
        _pbStep = 0;
        _pbSegOff = 0;
        _fjStep = 0;
        _spStep = 0;
        _ajStep = 0;
        _sjStep = 0;
        _rbStep = 0;
        _rsIdx = 0;
        _mbIdx = 0;
        _lockSleeping = false;
        _yieldArmed = false;
        _cmStep = 0;
        // A commit that failed (BUSY at the EXCLUSIVE lock, an I/O error) must start over from the beginning after Rollback: stamping page 1 and
        // syncing the journal are part of it.
        _cmPhase = 0;
        _cmWrite = 0;
        _cmFinish = 0;
        _alStep = 0;
        _fpStep = 0;
        _pmStep = 0;
        _rpStep = 0;
        _rmStep = 0;
        _rjStep = 0;
        _avStep = 0;
        _avActive = false;
        _avExplicit = false;
        _avSurv = null;
        _ajOpenOnly = false;
        _reserving = false;
        _avFree = null;
        UnpinTemps();
        _spillNone = false;
        if (_getFrame != null)
        {
            ReleaseLoading(_getFrame);
        }

        _hotFile?.Dispose();
        _hotFile = null;
        WalClearProgress();
    }
}

/// <summary>
/// A set of page numbers: a growable bit array for ordinary numbers and a hash set above <see cref="DenseLimit"/>, so a hostile header that
/// claims billions of pages cannot make a transaction allocate a bit array of that size.
/// </summary>
internal sealed class PageBitmap
{
    // 2^24 pages is a 2 MiB array and covers a 64 GiB database at 4 KiB pages.
    private const uint DenseLimit = 1u << 24;
    private ulong[] _bits = new ulong[16];
    private HashSet<uint>? _sparse;

    public bool Get(uint page)
    {
        if (page >= DenseLimit)
        {
            return _sparse != null && _sparse.Contains(page);
        }

        int w = (int)(page >> 6);
        return w < _bits.Length && (_bits[w] & (1UL << (int)(page & 63))) != 0;
    }

    public void Set(uint page)
    {
        if (page >= DenseLimit)
        {
            (_sparse ??= []).Add(page);
            return;
        }

        int w = (int)(page >> 6);
        if (w >= _bits.Length)
        {
            Array.Resize(ref _bits, Math.Max(w + 1, _bits.Length * 2));
        }

        _bits[w] |= 1UL << (int)(page & 63);
    }

    public void Clear()
    {
        Array.Clear(_bits);
        _sparse?.Clear();
    }
}
