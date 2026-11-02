using System.Buffers.Binary;
using System.Security.Cryptography;
using RecursiveAI.OneLite.Storage.Vfs;
using RecursiveAI.OneLite.Storage.Wal;

namespace RecursiveAI.OneLite.Storage.Pager;

// WAL recovery, checkpoint, closing and switching between WAL and the rollback journals (sqlite.org wal, walformat section 3, pragma journal_mode).
public sealed partial class Pager
{
    private CheckpointMode _ckMode;
    private byte[]? _ckPage;
    private JournalMode _jmTarget;
    private OneLiteException? _jmSaved;

    // ---- recovery: rebuild the wal-index from the WAL (walformat 3) ---------------------------------------------------------------

    // Called when the wal-index header is unreadable. Holds every lock but read lock 0, so nobody reads or writes the log meanwhile. If another
    // process holds any of them it is recovering (or using the index): sleep one tick and let the caller look again.
    private IEnumerable<IoRequest> WalRecoverOp()
    {
        WalConnection w = _wal!;
        WalIndex index = w.Index;
        int ps = _pageSize;
        bool locked = false;
        if (!index.IsHeap)
        {
            foreach (IoRequest r in WalLock(WalIndex.WriteLock, 3, true, false))
            {
                yield return r;
            }

            if (_wFailCode != 0)
            {
                yield break;
            }

            if (!_wGot)
            {
                yield return Rq(ResumeState.WalSleep, IoKind.Sleep, length: 1, cancellable: true);
                yield break;
            }

            foreach (IoRequest r in WalLock(WalIndex.ReadLock0 + 1, WalIndex.ReadSlots - 1, true, false))
            {
                yield return r;
            }

            if (_wFailCode != 0)
            {
                yield break;
            }

            if (!_wGot)
            {
                foreach (IoRequest r in WalUnlock(WalIndex.WriteLock, 3, true))
                {
                    yield return r;
                }

                yield return Rq(ResumeState.WalSleep, IoKind.Sleep, length: 1, cancellable: true);
                yield break;
            }

            locked = true;
        }

        // Another process may have finished the job while we were getting the locks.
        if (!index.TryReadHeader(out _))
        {
            long size = 0;
            if (w.File != null)
            {
                yield return Rq(ResumeState.WalFileSize, IoKind.FileSize, w.File);
                if (_comp.Status != 0)
                {
                    WFail(_comp.Status, "getting the WAL size failed");
                    yield break;
                }

                size = _comp.Value;
            }

            var wh = default(WalHeader);
            bool have = false;
            if (size >= WalFormat.HeaderSize)
            {
                yield return new IoRequest { Kind = IoKind.Read, Phase = ResumeState.WalReadHeader, File = w.File, Offset = 0, Length = WalFormat.HeaderSize, Data = _wHdrBuf, Cancellable = true };
                if (_comp.Status != 0)
                {
                    WFail(_comp.Status, "reading the WAL header failed");
                    yield break;
                }

                have = _comp.Value == WalFormat.HeaderSize && WalHeader.TryParse(_wHdrBuf, out wh) && wh.PageSize == ps;
            }

            uint mx = 0;
            uint nPage = 0;
            uint lc0 = 0;
            uint lc1 = 0;
            if (have)
            {
                bool big = wh.BigEndianChecksums;
                uint c0 = wh.Checksum1;
                uint c1 = wh.Checksum2;
                lc0 = c0;
                lc1 = c1;
                int frameSize = ps + WalFormat.FrameHeaderSize;
                long maxFrames = Math.Min((size - WalFormat.HeaderSize) / frameSize, int.MaxValue);
                if (_wFrameBuf == null || _wFrameBuf.Length != frameSize)
                {
                    _wFrameBuf = new byte[frameSize];
                }

                for (uint frame = 1; frame <= maxFrames; frame++)
                {
                    yield return new IoRequest { Kind = IoKind.Read, Phase = ResumeState.WalReadFrame, File = w.File, Offset = WalFormat.FrameOffset(frame, ps), Length = frameSize, Data = _wFrameBuf, Cancellable = true };
                    if (_comp.Status != 0)
                    {
                        WFail(_comp.Status, "reading a WAL frame failed");
                        yield break;
                    }

                    if (_comp.Value < frameSize)
                    {
                        break;
                    }

                    // fileformat2 4.1: a frame is valid if its salts match the header's and its checksum continues the chain.
                    ReadOnlySpan<byte> fb = _wFrameBuf;
                    uint pgno = BinaryPrimitives.ReadUInt32BigEndian(fb);
                    uint commitSize = BinaryPrimitives.ReadUInt32BigEndian(fb[4..]);
                    if (pgno == 0 || BinaryPrimitives.ReadUInt32BigEndian(fb[8..]) != wh.Salt1 || BinaryPrimitives.ReadUInt32BigEndian(fb[12..]) != wh.Salt2)
                    {
                        break;
                    }

                    WalFormat.Checksum(big, fb[..8], ref c0, ref c1);
                    WalFormat.Checksum(big, fb.Slice(WalFormat.FrameHeaderSize, ps), ref c0, ref c1);
                    if (c0 != BinaryPrimitives.ReadUInt32BigEndian(fb[16..]) || c1 != BinaryPrimitives.ReadUInt32BigEndian(fb[20..]))
                    {
                        break;
                    }

                    foreach (IoRequest r in WalMap(WalIndex.TableOf(frame)))
                    {
                        yield return r;
                    }

                    if (_wFailCode != 0)
                    {
                        yield break;
                    }

                    if (!index.Append(frame, pgno))
                    {
                        break;
                    }

                    if (commitSize != 0)
                    {
                        mx = frame;
                        nPage = commitSize;
                        lc0 = c0;
                        lc1 = c1;
                    }
                }

                // Frames after the last commit frame belong to a transaction that never committed.
                index.Cleanup(mx);
            }
            else
            {
                index.Cleanup(0);
            }

            var hdr = default(WalIndexHeader);
            hdr.W0 = WalFormat.FormatVersion;
            hdr.W2 = 1;
            hdr.SetIdentity(have ? wh.BigEndianChecksums : !BitConverter.IsLittleEndian, ps);
            hdr.W4 = mx;
            hdr.W5 = nPage;
            hdr.SetFrameChecksum(lc0, lc1);
            if (have)
            {
                hdr.SetSalts(SaltValue(wh.Salt1), SaltValue(wh.Salt2));
            }
            else
            {
                hdr.SetSalts(BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)), BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)));
            }

            w.Seq = have ? wh.CheckpointSequence : 0;
            w.Hdr = hdr;
            index.ResetCheckpointInfo(mx);
            index.WriteHeader(ref hdr);
        }

        if (locked)
        {
            foreach (IoRequest r in WalUnlock(WalIndex.ReadLock0 + 1, WalIndex.ReadSlots - 1, true))
            {
                yield return r;
            }

            foreach (IoRequest r in WalUnlock(WalIndex.WriteLock, 3, true))
            {
                yield return r;
            }
        }
    }

    // ---- checkpoint (fileformat2 4.3; walformat 2.3) ----------------------------------------------------------------------------

    /// <summary>
    /// <c>PRAGMA wal_checkpoint</c>: copies frames of the WAL into the database file. Needs WAL mode (the pager learns it from the first read transaction) and no transaction in progress; in any other
    /// case the result is (not busy, -1, -1). See <see cref="LastCheckpoint"/> for the outcome. A passive checkpoint never waits for a reader or writer;
    /// the others wait as far as the busy policy allows and report <see cref="CheckpointResult.Busy"/> when it gives up.
    /// </summary>
    /// <param name="mode">The kind of checkpoint.</param>
    /// <returns>The step status; call again after running a pending request.</returns>
    public PagerStatus Checkpoint(CheckpointMode mode)
    {
        Guard();
        if (_wal == null)
        {
            LastCheckpoint = new CheckpointResult(false, -1, -1);
            return PagerStatus.Ok;
        }

        if (_state != TxState.None)
        {
            return Fail(ResultCode.Locked, "a checkpoint cannot run inside a transaction");
        }

        if (_wckStep == 0)
        {
            _ckMode = mode;
            _wOp = WalCheckpointOp().GetEnumerator();
            _wckStep = 1;
        }

        PagerStatus s = WalPump();
        if (s != PagerStatus.Ok)
        {
            return s;
        }

        _wckStep = 0;
        return PagerStatus.Ok;
    }

    private IEnumerable<IoRequest> WalCheckpointOp()
    {
        WalConnection w = _wal!;
        WalIndex index = w.Index;
        CheckpointMode mode = _ckMode;
        int ps = _pageSize;
        bool busy = false;
        bool holdWrite = false;
        bool writeBusy = false;
        uint mxFrame = 0;
        uint safe = 0;
        bool restarted = false;
        LastCheckpoint = new CheckpointResult(false, 0, 0);

        // The wal-index has to exist before the checkpoint takes its locks: building it takes them all.
        bool haveHeader = false;
        for (int attempt = 0; attempt < 5 && !haveHeader; attempt++)
        {
            foreach (IoRequest r in WalMap(0))
            {
                yield return r;
            }

            if (_wFailCode != 0)
            {
                yield break;
            }

            haveHeader = index.TryReadHeader(out _);
            if (!haveHeader)
            {
                foreach (IoRequest r in WalRecoverOp())
                {
                    yield return r;
                }

                if (_wFailCode != 0)
                {
                    yield break;
                }
            }
        }

        if (!haveHeader)
        {
            WFail(ResultCode.Corrupt, "the wal-index header is unreadable");
            yield break;
        }

        foreach (IoRequest r in WalLock(WalIndex.CheckpointLock, 1, true, false))
        {
            yield return r;
        }

        if (_wFailCode != 0)
        {
            yield break;
        }

        if (!_wGot)
        {
            LastCheckpoint = new CheckpointResult(true, (int)WalFrameCount, 0);
            yield break;
        }

        if (mode != CheckpointMode.Passive)
        {
            foreach (IoRequest r in WalLock(WalIndex.WriteLock, 1, true, true))
            {
                yield return r;
            }

            if (_wFailCode != 0)
            {
                yield break;
            }

            holdWrite = _wGot;
            if (!_wGot)
            {
                // A writer holds the write lock: copy what a passive checkpoint can, and report BUSY for the part that needed the lock
                // (observed on the reference: the log size and the frames copied are still reported).
                writeBusy = true;
                mode = CheckpointMode.Passive;
            }
        }

        WalIndexHeader h = default;
        if (!busy)
        {
            if (!index.TryReadHeader(out h))
            {
                WFail(ResultCode.BusyRecovery, "the wal-index changed under the checkpoint");
                yield break;
            }

            if (h.PageSize != ps)
            {
                WFail(ResultCode.Corrupt, "the wal-index page size differs from the database's");
                yield break;
            }

            for (int t = 1; t < WalIndex.RegionsFor(h.MxFrame); t++)
            {
                foreach (IoRequest r in WalMap(t))
                {
                    yield return r;
                }

                if (_wFailCode != 0)
                {
                    yield break;
                }
            }

            mxFrame = h.MxFrame;
            for (int attempt = 0; ; attempt++)
            {
                // A reader holding read lock n (read mark m) may still need the database file as it was for the frames beyond m (walformat 2.3.1).
                safe = mxFrame;
                for (int i = 0; i < WalIndex.ReadSlots; i++)
                {
                    uint m = i == 0 ? 0 : index.ReadMark(i);
                    if (m >= safe)
                    {
                        continue;
                    }

                    foreach (IoRequest r in WalLock(WalIndex.ReadLock0 + i, 1, true, false))
                    {
                        yield return r;
                    }

                    if (_wFailCode != 0)
                    {
                        yield break;
                    }

                    if (_wGot)
                    {
                        if (i > 0)
                        {
                            index.SetReadMark(i, WalIndex.ReadMarkNotUsed);
                        }

                        foreach (IoRequest r in WalUnlock(WalIndex.ReadLock0 + i, 1, true))
                        {
                            yield return r;
                        }
                    }
                    else
                    {
                        safe = m;
                    }
                }

                uint nb = index.NBackfill;
                if (safe > nb && w.File != null)
                {
                    if (_sync != SynchronousMode.Off)
                    {
                        // The log is durable before the database is overwritten from it.
                        foreach (IoRequest r in WalSyncOp(ResumeState.CkptSyncWal))
                        {
                            yield return r;
                        }

                        if (_wFailCode != 0)
                        {
                            yield break;
                        }
                    }

                    index.NBackfillAttempted = safe;
                    var latest = new Dictionary<uint, uint>();
                    for (uint f = safe; f > nb; f--)
                    {
                        uint pg = index.PageOf(f);
                        if (pg != 0 && pg <= h.NPage)
                        {
                            latest.TryAdd(pg, f);
                        }
                    }

                    uint[] pages = [.. latest.Keys];
                    Array.Sort(pages);
                    _ckPage ??= new byte[ps];
                    if (_ckPage.Length != ps)
                    {
                        _ckPage = new byte[ps];
                    }

                    foreach (uint pg in pages)
                    {
                        yield return new IoRequest { Kind = IoKind.Read, Phase = ResumeState.CkptReadFrame, File = w.File, Offset = WalFormat.FrameOffset(latest[pg], ps) + WalFormat.FrameHeaderSize, Length = ps, Data = _ckPage, Cancellable = true };
                        if (_comp.Status != 0 || _comp.Value < ps)
                        {
                            WFail(_comp.Status, "reading a WAL frame for the checkpoint failed");
                            yield break;
                        }

                        yield return new IoRequest { Kind = IoKind.Write, Phase = ResumeState.CkptWriteDb, File = _dbFile, Offset = (long)(pg - 1) * ps, Length = ps, Data = _ckPage };
                        if (_comp.Status != 0)
                        {
                            WFail(_comp.Status, "writing a page during the checkpoint failed");
                            yield break;
                        }
                    }

                    if (safe == mxFrame)
                    {
                        // Everything is in the database: cut it to the size of the last commit.
                        yield return Rq(ResumeState.CkptTruncateDb, IoKind.FileSize, _dbFile);
                        long target = (long)h.NPage * ps;
                        if (_comp.Status == 0 && _comp.Value != target)
                        {
                            yield return Rq(ResumeState.CkptTruncateDb, IoKind.Truncate, _dbFile, offset: target);
                            if (_comp.Status != 0)
                            {
                                WFail(_comp.Status, "truncating the database failed");
                                yield break;
                            }
                        }
                    }

                    if (_sync != SynchronousMode.Off)
                    {
                        yield return Rq(ResumeState.CkptSyncDb, IoKind.Sync, _dbFile, arg: (int)SyncKind());
                        if (_comp.Status != 0)
                        {
                            WFail(_comp.Status, "syncing the database failed");
                            yield break;
                        }
                    }

                    index.NBackfill = safe;
                }

                if (safe == mxFrame || mode == CheckpointMode.Passive)
                {
                    break;
                }

                // FULL and beyond wait for the readers that still use older snapshots; the write lock keeps new ones from falling behind.
                int ms = _opts.Busy?.Invoke(attempt) ?? -1;
                if (ms < 0)
                {
                    busy = true;
                    break;
                }

                yield return Rq(ResumeState.WalSleep, IoKind.Sleep, length: ms, cancellable: true);
            }

            if (!busy && mode >= CheckpointMode.Restart && holdWrite && safe == mxFrame)
            {
                // RESTART and TRUNCATE: wait until no reader uses the log, then start it over (and for TRUNCATE empty the file).
                for (int attempt = 0; ; attempt++)
                {
                    foreach (IoRequest r in WalLock(WalIndex.ReadLock0 + 1, WalIndex.ReadSlots - 1, true, false))
                    {
                        yield return r;
                    }

                    if (_wFailCode != 0)
                    {
                        yield break;
                    }

                    if (_wGot)
                    {
                        break;
                    }

                    int ms = _opts.Busy?.Invoke(attempt) ?? -1;
                    if (ms < 0)
                    {
                        busy = true;
                        break;
                    }

                    yield return Rq(ResumeState.WalSleep, IoKind.Sleep, length: ms, cancellable: true);
                }

                if (!busy)
                {
                    if (mxFrame > 0)
                    {
                        w.Hdr = h;
                        WalRestartHeader(w);
                        restarted = true;
                    }

                    if (mode == CheckpointMode.Truncate && w.File != null)
                    {
                        yield return Rq(ResumeState.CkptTruncateWal, IoKind.Truncate, w.File, offset: 0);
                        if (_comp.Status != 0)
                        {
                            WFail(_comp.Status, "truncating the WAL failed");
                            yield break;
                        }
                    }

                    foreach (IoRequest r in WalUnlock(WalIndex.ReadLock0 + 1, WalIndex.ReadSlots - 1, true))
                    {
                        yield return r;
                    }
                }
            }
        }

        if (holdWrite)
        {
            foreach (IoRequest r in WalUnlock(WalIndex.WriteLock, 1, true))
            {
                yield return r;
            }
        }

        foreach (IoRequest r in WalUnlock(WalIndex.CheckpointLock, 1, true))
        {
            yield return r;
        }

        LastCheckpoint = mode == CheckpointMode.Truncate && restarted ? new CheckpointResult(false, 0, 0) : new CheckpointResult(busy || writeBusy, (int)mxFrame, (int)safe);
    }

    // ---- closing ------------------------------------------------------------------------------------------------------------------------

    // The last connection to leave checkpoints the log and removes the -wal and -shm files (walformat 1.4, 2.3.2). Runs inside Dispose on the sync
    // driver; every failure is ignored because the files then simply stay, which is what a crash would have left.
    private void WalCloseSync()
    {
        WalConnection? w = _wal;
        if (w == null)
        {
            return;
        }

        try
        {
            if (_state == TxState.Write)
            {
                return;
            }

            if (w.ReadSlot >= 0)
            {
                w.Index.Shm.Lock(WalIndex.ReadLock0 + w.ReadSlot, 1, ShmLockOptions.Unlock | ShmLockOptions.Shared);
                w.ReadSlot = -1;
            }

            if (w.ReadOnly)
            {
                // A read-only connection never checkpoints or removes files (wal "Read-Only Databases"); the shared memory is released with the database file.
                w.File?.Dispose();
                return;
            }

            if (_dbFile == null || !_dbFile.Lock(LockLevel.Exclusive))
            {
                return;
            }

            _lock = LockLevel.Exclusive;
            _state = TxState.None;
            _ckMode = CheckpointMode.Passive;
            _wOp = WalCheckpointOp().GetEnumerator();
            RunSync(WalPump);
            if (LastCheckpoint.LogFrames == LastCheckpoint.CheckpointedFrames && !LastCheckpoint.Busy)
            {
                w.Index.Shm.Unmap(true);
                w.File?.Dispose();
                _vfs.Delete(WalPath, false);
                _wal = null;
            }
        }
        catch (Exception e) when (e is VfsException or OneLiteException)
        {
            // Leave the files for the next opener.
        }
        finally
        {
            WalDropOp();

            // Every way out gives the log's handle back (a connection that was not the last one used to keep it until finalization).
            try
            {
                w.File?.Dispose();
            }
            catch (VfsException)
            {
                // Closing is best effort.
            }
        }
    }

    // ---- journal_mode: into and out of WAL --------------------------------------------------------------------------------------

    /// <summary>
    /// <c>PRAGMA journal_mode</c>. Between rollback-journal modes this only changes a setting. Into WAL it commits a header with the file format
    /// versions (bytes 18 and 19) set to 2 under the rollback journal, then opens the log; out of WAL it needs the only connection (the EXCLUSIVE
    /// lock), checkpoints, deletes the <c>-wal</c> and <c>-shm</c> files and commits the versions back to 1 (walformat 2.3.2). Needs a read transaction
    /// and no write transaction. If another connection is using the database out of WAL fails with <c>SQLITE_BUSY</c> and the database stays in WAL mode.
    /// </summary>
    /// <param name="target">The new mode.</param>
    /// <returns>The step status; <see cref="JournalMode"/> holds the resulting mode.</returns>
    public PagerStatus ChangeJournalMode(JournalMode target)
    {
        Guard();
        if (_wjmStep == 0)
        {
            if (_state != TxState.Read)
            {
                throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "changing the journal mode needs a read transaction and no write transaction");
            }

            if (target == JournalMode)
            {
                return PagerStatus.Ok;
            }

            if (target != JournalMode.Wal && _wal == null)
            {
                _journalMode = target;
                return PagerStatus.Ok;
            }

            _jmTarget = target;
            _wjmStep = target == JournalMode.Wal ? 10 : 30;
        }

        return target == JournalMode.Wal ? EnterWal() : LeaveWal();
    }

    private PagerStatus EnterWal()
    {
        for (; ; )
        {
            switch (_wjmStep)
            {
                case 10:
                {
                    PagerStatus s = BeginWrite();
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _wjmStep = 11;
                    break;
                }

                case 11:
                {
                    PagerStatus s = GetCore(1, GetOptions.None, out PageFrame? f);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _cmFrame = f;
                    _wjmStep = 12;
                    break;
                }

                case 12:
                {
                    PagerStatus s = MakeWritable(_cmFrame!);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    // fileformat2 1.3: bytes 18 and 19 are the file format write and read versions; 2 means WAL.
                    _cmFrame!.Data[18] = 2;
                    _cmFrame.Data[19] = 2;
                    _cmFrame.Pins--;
                    _cmFrame = null;
                    _wjmStep = 13;
                    break;
                }

                case 13:
                {
                    PagerStatus s = Commit();
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _wjmStep = 14;
                    break;
                }

                case 14:
                {
                    PagerStatus s = WalOpenStep();
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _wjmStep = 15;
                    break;
                }

                default:
                {
                    PagerStatus s = WalBeginReadStep();
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _wjmStep = 0;
                    return PagerStatus.Ok;
                }
            }
        }
    }

    private PagerStatus LeaveWal()
    {
        for (; ; )
        {
            switch (_wjmStep)
            {
                case 30:
                    if (_wal!.ReadSlot >= 0)
                    {
                        _wOp ??= WalUnlock(WalIndex.ReadLock0 + _wal.ReadSlot, 1, false).GetEnumerator();
                        PagerStatus s = WalPump();
                        if (s != PagerStatus.Ok)
                        {
                            return s;
                        }

                        _wal.ReadSlot = -1;
                    }

                    _wjmStep = 31;
                    break;
                case 31:
                {
                    PagerStatus s = TakeLock(LockLevel.Exclusive);
                    if (s == PagerStatus.Error)
                    {
                        _jmSaved = _error;
                        _wjmStep = 90;
                        break;
                    }

                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _ckMode = CheckpointMode.Passive;
                    _wOp = WalCheckpointOp().GetEnumerator();
                    _wjmStep = 32;
                    break;
                }

                case 32:
                {
                    PagerStatus s = WalPump();
                    if (s == PagerStatus.Error)
                    {
                        _jmSaved = _error;
                        _wjmStep = 91;
                        break;
                    }

                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    if (LastCheckpoint.Busy || LastCheckpoint.CheckpointedFrames != LastCheckpoint.LogFrames)
                    {
                        _jmSaved = new OneLiteException(ResultCode.Busy, Msg(ResultCode.Busy), "the WAL could not be checkpointed completely");
                        _wjmStep = 91;
                        break;
                    }

                    // The log is empty of anything not in the database: remove it (shared memory first, as the reference does).
                    _wal!.Index.Shm.Unmap(true);
                    _wal.File?.Dispose();
                    _wjmStep = 33;
                    break;
                }

                case 33:
                    if (!Io(ResumeState.WalCloseDelete, new IoRequest { Kind = IoKind.Delete, Path = WalPath, Arg = 0 }))
                    {
                        return PagerStatus.Pending;
                    }

                    _wal = null;
                    _source = null;
                    _journalMode = _jmTarget;
                    _wjmStep = 34;
                    break;
                case 34:
                {
                    PagerStatus s = BeginWrite();
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _wjmStep = 35;
                    break;
                }

                case 35:
                {
                    PagerStatus s = GetCore(1, GetOptions.None, out PageFrame? f);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _cmFrame = f;
                    _wjmStep = 36;
                    break;
                }

                case 36:
                {
                    PagerStatus s = MakeWritable(_cmFrame!);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _cmFrame!.Data[18] = 1;
                    _cmFrame.Data[19] = 1;
                    _cmFrame.Pins--;
                    _cmFrame = null;
                    _wjmStep = 37;
                    break;
                }

                case 37:
                {
                    PagerStatus s = Commit();
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _wjmStep = 0;
                    return PagerStatus.Ok;
                }

                case 91:
                    // Could not leave WAL: give the exclusive lock back and rejoin as a reader.
                    if (!Io(ResumeState.EndReadUnlock, new IoRequest { Kind = IoKind.Unlock, File = _dbFile, Arg = (int)LockLevel.Shared }))
                    {
                        return PagerStatus.Pending;
                    }

                    _lock = LockLevel.Shared;
                    _wjmStep = 90;
                    break;
                default:
                {
                    PagerStatus s = WalBeginReadStep();
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _wjmStep = 0;
                    _error = _jmSaved;
                    return PagerStatus.Error;
                }
            }
        }
    }
}
