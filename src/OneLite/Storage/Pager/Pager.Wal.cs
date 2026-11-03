using System.Buffers.Binary;
using System.Security.Cryptography;
using RecursiveAI.OneLite.Storage.Vfs;
using RecursiveAI.OneLite.Storage.Wal;

namespace RecursiveAI.OneLite.Storage.Pager;

// WAL mode (sqlite.org wal, walformat, fileformat2 section 4). The state machines here are written as iterators that yield one I/O request at a
// time; WalPump feeds each request through the pager's request plumbing (Io), so every wait is a suspend point exactly like the rollback-journal
// code, and a completed request is consumed when the operation is entered again. docs/wal.md lists the states.
public sealed partial class Pager
{
    private WalConnection? _wal;

    private IEnumerator<IoRequest>? _wOp;
    private IoRequest _wCur;
    private bool _wHave;
    private int _wFailCode;
    private string? _wFailDetail;
    private bool _wGot;
    private int _wHeldShared;
    private int _wHeldExcl;

    private WalIndexHeader _walLastSnap;
    private bool _walLastSnapValid;
    private bool _walCommitted;
    private int _walAutoCheckpoint = int.MinValue;
    private long _walSizeLimit = long.MinValue;

    private int _wbrStep;
    private int _wbwStep;
    private int _wcStep;
    private int _wsStep;
    private int _wrbStep;
    private int _werStep;
    private int _wopStep;
    private int _wckStep;
    private int _wjmStep;

    private PageFrame[] _wBatch = [];
    private int _wBatchCount;
    private PageFrame? _wSpillFrame;
    private byte[]? _wFrameBuf;
    private readonly byte[] _wHdrBuf = new byte[WalFormat.HeaderSize];

    /// <summary>The outcome of the last <see cref="Checkpoint"/>.</summary>
    public CheckpointResult LastCheckpoint { get; private set; }

    /// <summary>
    /// <c>PRAGMA wal_autocheckpoint</c>: after a transaction that committed in WAL mode ends, a passive checkpoint runs if the WAL holds at least
    /// this many frames. Zero or negative disables it. Starts at <see cref="PagerOptions.WalAutoCheckpoint"/>.
    /// </summary>
    public int WalAutoCheckpoint
    {
        get => _walAutoCheckpoint != int.MinValue ? _walAutoCheckpoint : _opts.WalAutoCheckpoint;
        set => _walAutoCheckpoint = value;
    }

    /// <summary><c>PRAGMA journal_size_limit</c> in bytes (negative: no limit); see <see cref="PagerOptions.JournalSizeLimit"/>.</summary>
    public long JournalSizeLimit
    {
        get => _walSizeLimit != long.MinValue ? _walSizeLimit : _opts.JournalSizeLimit;
        set => _walSizeLimit = value;
    }

    /// <summary>True while the database is open in WAL mode.</summary>
    public bool IsWal => _wal != null;

    /// <summary>The number of frames in the WAL as of this connection's snapshot (0 outside WAL mode).</summary>
    public uint WalFrameCount => _wal == null ? 0 : _wal.Writing ? _wal.Hdr.MxFrame : _wal.Snap.MxFrame;

    // ---- the page source: where in the WAL a page lives ---------------------------------------------------------------------------

    private string WalPath => _path + "-wal";

    private IVfsFile? Page1File(out long offset)
    {
        offset = 0;
        if (_wal != null && _wal.Locate(1, _pageSize, out long at))
        {
            offset = at;
            return _wal.File;
        }

        return _dbFile;
    }

    private sealed class WalSource : IPageSource
    {
        private readonly Pager _p;

        public WalSource(Pager p)
        {
            _p = p;
        }

        public bool TryLocate(uint pageNumber, out IVfsFile? file, out long offset)
        {
            WalConnection? w = _p._wal;
            if (w != null && w.Locate(pageNumber, _p._pageSize, out offset))
            {
                file = w.File;
                return true;
            }

            file = null;
            offset = 0;
            return false;
        }
    }

    // ---- driving an iterator -------------------------------------------------------------------------------------------------------

    private static IoRequest Rq(ResumeState phase, IoKind kind, IVfsFile? file = null, long offset = 0, int length = 0, int arg = 0, bool cancellable = false) =>
        new() { Kind = kind, Phase = phase, File = file, Offset = offset, Length = length, Arg = arg, Cancellable = cancellable };

    private bool WFail(int extended, string detail)
    {
        if (_wFailCode == 0)
        {
            _wFailCode = extended == 0 ? ResultCode.IoErr : extended;
            _wFailDetail = detail;
        }

        return true;
    }

    // Runs _wOp until it finishes (Ok), needs a request (Pending) or fails (Error). Locks the failed operation still held are released.
    private PagerStatus WalPump()
    {
        for (; ; )
        {
            if (_wHave)
            {
                if (!Io(_wCur.Phase, _wCur))
                {
                    return PagerStatus.Pending;
                }

                _wHave = false;
            }

            bool more = _wOp!.MoveNext();
            if (_wFailCode != 0)
            {
                int code = _wFailCode;
                string detail = _wFailDetail ?? "WAL operation failed";
                _wFailCode = 0;
                _wFailDetail = null;
                WalDropOp();
                WalReleaseHeldSync();
                return Fail(code, detail);
            }

            if (!more)
            {
                WalDropOp();
                return PagerStatus.Ok;
            }

            _wCur = _wOp.Current;
            _wHave = true;
        }
    }

    private void WalDropOp()
    {
        _wOp?.Dispose();
        _wOp = null;
        _wHave = false;
    }

    // After a failure or an abandoned operation: give back the wal-index locks that were held only for that operation. Releasing never fails or blocks.
    private void WalReleaseHeldSync()
    {
        WalConnection? w = _wal;
        if (w == null)
        {
            return;
        }

        try
        {
            // The read lock of the snapshot and the write lock of a write transaction belong to the transaction, not to the failed operation.
            int keepShared = w.ReadSlot >= 0 ? 1 << (WalIndex.ReadLock0 + w.ReadSlot) : 0;
            int keepExcl = w.Writing ? 1 << WalIndex.WriteLock : 0;
            for (int i = 0; i < WalIndex.ReadLock0 + WalIndex.ReadSlots; i++)
            {
                if ((_wHeldExcl & 1 << i) != 0 && (keepExcl & 1 << i) == 0)
                {
                    w.Index.Shm.Lock(i, 1, ShmLockOptions.Unlock | ShmLockOptions.Exclusive);
                    _wHeldExcl &= ~(1 << i);
                }

                if ((_wHeldShared & 1 << i) != 0 && (keepShared & 1 << i) == 0)
                {
                    w.Index.Shm.Lock(i, 1, ShmLockOptions.Unlock | ShmLockOptions.Shared);
                    _wHeldShared &= ~(1 << i);
                }
            }
        }
        catch (VfsException)
        {
            // Unlocking cannot fail in the backends here; if one does, the locks go with the handle.
        }
    }

    private void WalClearProgress()
    {
        WalDropOp();
        _wbrStep = _wbwStep = _wcStep = _wsStep = _wrbStep = _werStep = _wopStep = _wckStep = _wjmStep = 0;
        _wFailCode = 0;
        _wFailDetail = null;
    }

    // ---- small iterators: map, lock, unlock ---------------------------------------------------------------------------------------

    private IEnumerable<IoRequest> WalMap(int region)
    {
        WalIndex index = _wal!.Index;
        if (index.HasRegion(region))
        {
            yield break;
        }

        yield return new IoRequest { Kind = IoKind.ShmMap, Phase = ResumeState.WalShmMap, Shm = index.Shm, Offset = region, Arg = 1, Cancellable = true };
        if (_comp.Status != 0 && _opts.ReadOnly && !index.IsHeap)
        {
            // The -shm file cannot be opened for writing. If one exists (it is read-only, or its directory is), read the log with a private index,
            // which gives the answers a shared one would. If none exists and none can be made, a read-only connection cannot start the
            // wal-index: SQLITE_CANTOPEN when there is a log to read, SQLITE_READONLY_DIRECTORY when there is nothing.
            yield return new IoRequest { Kind = IoKind.Access, Phase = ResumeState.WalShmMap, Path = _path + "-shm", Cancellable = true };
            if (_comp.Status == 0 && _comp.Value != 0)
            {
                _wal!.Index = new WalIndex(new HeapShm(), true);
                _walLastSnapValid = false;
                foreach (IoRequest r in WalMap(region))
                {
                    yield return r;
                }

                yield break;
            }

            if (_wal!.File != null)
            {
                WFail(ResultCode.CantOpen, "unable to open the wal-index of a read-only database");
            }
            else
            {
                WFail(ResultCode.ReadOnlyDirectory, "attempt to write a readonly database");
            }

            yield break;
        }

        if (_comp.Status != 0)
        {
            WFail(_comp.Status, "mapping the wal-index failed");
            yield break;
        }

        if (_comp.Region == null || _comp.Region.IsEmpty)
        {
            WFail(ResultCode.IoErr, "the wal-index region could not be mapped");
            yield break;
        }

        index.SetRegion(region, _comp.Region);
    }

    // Takes slots [offset, offset+count) all or none. With wait, the busy policy decides how long to keep trying. Sets _wGot.
    // With spin, a busy lock is first retried for a short time with only a yield in between (BusySpinBudgetUs), before the busy policy is asked: a writer that
    // is about to replay a few microseconds of work holds the lock for far less than the policy's first delay of a millisecond. The time spent counts against
    // busy_timeout (BusySpun), and a user busy handler is called, from its first retry, only after the spin.
    private IEnumerable<IoRequest> WalLock(int offset, int count, bool exclusive, bool wait, bool spin = false)
    {
        IVfsShm shm = _wal!.Index.Shm;
        ShmLockOptions flags = ShmLockOptions.Lock | (exclusive ? ShmLockOptions.Exclusive : ShmLockOptions.Shared);
        long spinStart = 0;
        long spinBudget = spin && wait ? (_opts.BusySpinBudgetUs?.Invoke() ?? 0) * System.Diagnostics.Stopwatch.Frequency / 1_000_000 : 0;
        for (int tries = 0; ; tries++)
        {
            yield return new IoRequest { Kind = IoKind.ShmLock, Phase = ResumeState.WalShmLock, Shm = shm, Offset = offset, Length = count, Arg = (int)flags, Cancellable = true };
            if (_comp.Status != 0)
            {
                _wGot = false;
                WFail(_comp.Status, "locking the wal-index failed");
                yield break;
            }

            if (_comp.Value == 1)
            {
                for (int i = offset; i < offset + count; i++)
                {
                    if (exclusive)
                    {
                        _wHeldExcl |= 1 << i;
                    }
                    else
                    {
                        _wHeldShared |= 1 << i;
                    }
                }

                _wGot = true;
                yield break;
            }

            if (spinBudget > 0)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (spinStart == 0)
                {
                    spinStart = now;
                }

                if (now - spinStart < spinBudget)
                {
                    tries--;
                    yield return Rq(ResumeState.WalSleep, IoKind.Sleep, length: 0, cancellable: true);
                    continue;
                }

                spinBudget = 0;
                _opts.BusySpun?.Invoke((int)((now - spinStart) * 1000 / System.Diagnostics.Stopwatch.Frequency) + 1);
            }

            int ms = wait ? _opts.Busy?.Invoke(tries) ?? -1 : -1;
            if (ms < 0)
            {
                _wGot = false;
                yield break;
            }

            yield return Rq(ResumeState.WalSleep, IoKind.Sleep, length: ms, cancellable: true);
        }
    }

    private IEnumerable<IoRequest> WalUnlock(int offset, int count, bool exclusive)
    {
        yield return new IoRequest
        {
            Kind = IoKind.ShmLock,
            Phase = ResumeState.WalShmLock,
            Shm = _wal!.Index.Shm,
            Offset = offset,
            Length = count,
            Arg = (int)(ShmLockOptions.Unlock | (exclusive ? ShmLockOptions.Exclusive : ShmLockOptions.Shared)),
        };
        for (int i = offset; i < offset + count; i++)
        {
            if (exclusive)
            {
                _wHeldExcl &= ~(1 << i);
            }
            else
            {
                _wHeldShared &= ~(1 << i);
            }
        }
    }

    // ---- opening the WAL --------------------------------------------------------------------------------------------------------------

    private PagerStatus WalOpenStep()
    {
        _wOp ??= WalOpenOp().GetEnumerator();
        return WalPump();
    }

    private IEnumerable<IoRequest> WalOpenOp()
    {
        // A read-only connection opens the log the way a read-write one does (creating an empty one if none exists), because it takes part in the
        // wal-index locking like any reader: wal "Read-Only Databases". Only when the file or its directory forbids writing does it fall back to
        // reading the log alone.
        VfsOpenOptions flags = VfsOpenOptions.Wal | VfsOpenOptions.ReadWrite | VfsOpenOptions.Create;
        yield return new IoRequest { Kind = IoKind.Open, Phase = ResumeState.WalOpen, Path = WalPath, Arg = (int)flags, Cancellable = true };
        IVfsFile? file = null;
        if (_comp.Status == 0)
        {
            file = _comp.File;
        }
        else if (_opts.ReadOnly)
        {
            yield return new IoRequest { Kind = IoKind.Open, Phase = ResumeState.WalOpen, Path = WalPath, Arg = (int)(VfsOpenOptions.Wal | VfsOpenOptions.ReadOnly), Cancellable = true };
            if (_comp.Status == 0)
            {
                file = _comp.File;
            }
        }
        else
        {
            WFail(_comp.Status, "opening the write-ahead log failed");
            yield break;
        }

        // A backend with no shared memory works from a private index rebuilt from the log (walformat section 3).
        IVfsShm? shm = _dbFile!.Shm;
        var w = new WalConnection(file, new WalIndex(shm ?? new HeapShm(), shm == null), _opts.ReadOnly);
        _wal = w;
        _hdrSnapOk = false;
        _source = new WalSource(this);
        _walLastSnapValid = false;
    }

    // ---- read transactions -------------------------------------------------------------------------------------------------------------

    private PagerStatus WalBeginReadStep()
    {
        if (_wOp == null)
        {
            if (WalTryBeginReadFast())
            {
                return WalSnapshotTaken();
            }

            _wOp = WalBeginReadOp().GetEnumerator();
        }

        PagerStatus s = WalPump();
        return s != PagerStatus.Ok ? s : WalSnapshotTaken();
    }

    // The common start of a read transaction on a backend that answers synchronously: the log is mapped, the header is consistent and a read slot is
    // free to take without waiting. It takes the same steps as WalBeginReadOp in the same order, without the iterator machinery (no allocation); any
    // irregularity (a region to map, a busy lock, a mark to publish, a failure) returns false having taken nothing, and the general path runs.
    private bool WalTryBeginReadFast()
    {
        if (_yieldEvery > 0 || (_vfs.Caps & VfsCaps.Sync) == 0)
        {
            return false;
        }

        WalConnection w = _wal!;
        WalIndex index = w.Index;
        if (!index.HasRegion(0) || !index.TryReadHeader(out WalIndexHeader h) || h.PageSize != _pageSize)
        {
            return false;
        }

        for (int t = 1; t < WalIndex.RegionsFor(h.MxFrame); t++)
        {
            if (!index.HasRegion(t))
            {
                return false;
            }
        }

        uint nb = index.NBackfill;
        if (nb > h.MxFrame)
        {
            return false;
        }

        int slot = -1;
        try
        {
            if (nb == h.MxFrame)
            {
                // Everything is in the database file: promise not to read the WAL (read lock 0).
                if (index.Shm.Lock(WalIndex.ReadLock0, 1, ShmLockOptions.Lock | ShmLockOptions.Shared))
                {
                    slot = 0;
                    _wHeldShared |= 1 << WalIndex.ReadLock0;
                }
            }

            if (slot < 0)
            {
                int best = -1;
                uint bestMark = 0;
                for (int i = 1; i < WalIndex.ReadSlots; i++)
                {
                    uint m = index.ReadMark(i);
                    if (m != WalIndex.ReadMarkNotUsed && m <= h.MxFrame && (best < 0 || m > bestMark))
                    {
                        best = i;
                        bestMark = m;
                    }
                }

                // A mark that must be published first goes the general way.
                if (best < 0 || bestMark < h.MxFrame)
                {
                    return false;
                }

                if (!index.Shm.Lock(WalIndex.ReadLock0 + best, 1, ShmLockOptions.Lock | ShmLockOptions.Shared))
                {
                    return false;
                }

                _wHeldShared |= 1 << (WalIndex.ReadLock0 + best);
                if (index.ReadMark(best) != bestMark)
                {
                    ReleaseShared(index, WalIndex.ReadLock0 + best);
                    return false;
                }

                slot = best;
            }
        }
        catch (VfsException)
        {
            return false;
        }

        // The header must still be the one the slot was chosen for; otherwise a writer or checkpointer moved things under us.
        if (!index.TryReadHeader(out WalIndexHeader h2) || h2 != h || (slot == 0 && index.NBackfill != h.MxFrame))
        {
            ReleaseShared(index, WalIndex.ReadLock0 + slot);
            return false;
        }

        w.ReadSlot = slot;
        w.Snap = h;
        w.ReadMx = slot == 0 ? 0 : h.MxFrame;
        return true;
    }

    private void ReleaseShared(WalIndex index, int lockSlot)
    {
        try
        {
            index.Shm.Lock(lockSlot, 1, ShmLockOptions.Unlock | ShmLockOptions.Shared);
        }
        catch (VfsException)
        {
            // Unlocking cannot fail in the backends here; if one does, the locks go with the handle.
        }

        _wHeldShared &= ~(1 << lockSlot);
    }

    private PagerStatus WalSnapshotTaken()
    {
        WalConnection w = _wal!;
        if (!_walLastSnapValid || _walLastSnap != w.Snap)
        {
            // Another connection committed or the log was restarted: pages cached from an older snapshot may be stale.
            DropCleanFrames();
            if (_walLastSnapValid)
            {
                ForeignChanges++;
            }

            _walLastSnap = w.Snap;
            _walLastSnapValid = true;
        }

        return PagerStatus.Ok;
    }

    // walformat 2.3.1: pick a read lock whose mark promises the checkpointer will not copy frames this snapshot still needs.
    private IEnumerable<IoRequest> WalBeginReadOp()
    {
        WalConnection w = _wal!;
        WalIndex index = w.Index;
        for (int attempt = 0; ; attempt++)
        {
            if (attempt >= 100)
            {
                WFail(ResultCode.BusyRecovery, "the wal-index did not become consistent");
                yield break;
            }

            if (attempt > 5)
            {
                yield return Rq(ResumeState.WalSleep, IoKind.Sleep, length: 1, cancellable: true);
            }

            foreach (IoRequest r in WalMap(0))
            {
                yield return r;
            }

            if (_wFailCode != 0)
            {
                yield break;
            }

            // Mapping may have replaced a shared index by a private one (a -shm file that cannot be opened for writing).
            index = w.Index;
            if (!index.TryReadHeader(out WalIndexHeader h))
            {
                foreach (IoRequest r in WalRecoverOp())
                {
                    yield return r;
                }

                if (_wFailCode != 0)
                {
                    yield break;
                }

                continue;
            }

            if (h.PageSize != _pageSize)
            {
                WFail(ResultCode.Corrupt, $"the wal-index says the page size is {h.PageSize} but the database says {_pageSize}");
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

            uint nb = index.NBackfill;
            if (nb > h.MxFrame)
            {
                continue;
            }

            int slot = -1;
            if (nb == h.MxFrame)
            {
                // Everything is in the database file: promise not to read the WAL (read lock 0).
                foreach (IoRequest r in WalLock(WalIndex.ReadLock0, 1, false, false))
                {
                    yield return r;
                }

                if (_wFailCode != 0)
                {
                    yield break;
                }

                if (_wGot)
                {
                    slot = 0;
                }
            }

            if (slot < 0)
            {
                int best = -1;
                uint bestMark = 0;
                for (int i = 1; i < WalIndex.ReadSlots; i++)
                {
                    uint m = index.ReadMark(i);
                    if (m != WalIndex.ReadMarkNotUsed && m <= h.MxFrame && (best < 0 || m > bestMark))
                    {
                        best = i;
                        bestMark = m;
                    }
                }

                if (best < 0 || bestMark < h.MxFrame)
                {
                    // Publish this snapshot's frame count in a slot no reader holds.
                    for (int i = 1; i < WalIndex.ReadSlots; i++)
                    {
                        uint m = index.ReadMark(i);
                        if (i == best || (m != WalIndex.ReadMarkNotUsed && m > h.MxFrame))
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
                            index.SetReadMark(i, h.MxFrame);
                            foreach (IoRequest r in WalUnlock(WalIndex.ReadLock0 + i, 1, true))
                            {
                                yield return r;
                            }

                            best = i;
                            bestMark = h.MxFrame;
                            break;
                        }
                    }
                }

                if (best >= 0)
                {
                    foreach (IoRequest r in WalLock(WalIndex.ReadLock0 + best, 1, false, false))
                    {
                        yield return r;
                    }

                    if (_wFailCode != 0)
                    {
                        yield break;
                    }

                    if (_wGot)
                    {
                        if (index.ReadMark(best) == bestMark)
                        {
                            slot = best;
                        }
                        else
                        {
                            foreach (IoRequest r in WalUnlock(WalIndex.ReadLock0 + best, 1, false))
                            {
                                yield return r;
                            }
                        }
                    }
                }
            }

            if (slot < 0)
            {
                continue;
            }

            // The header must still be the one the slot was chosen for; otherwise a writer or checkpointer moved things under us.
            if (!index.TryReadHeader(out WalIndexHeader h2) || h2 != h || (slot == 0 && index.NBackfill != h.MxFrame))
            {
                foreach (IoRequest r in WalUnlock(WalIndex.ReadLock0 + slot, 1, false))
                {
                    yield return r;
                }

                continue;
            }

            w.ReadSlot = slot;
            w.Snap = h;
            w.ReadMx = slot == 0 ? 0 : h.MxFrame;
            yield break;
        }
    }

    private PagerStatus WalEndReadStep()
    {
        for (; ; )
        {
            switch (_werStep)
            {
                case 0:
                    if (_wal!.ReadSlot >= 0 && _wOp == null && _yieldEvery == 0 && (_vfs.Caps & VfsCaps.Sync) != 0)
                    {
                        // Releasing a lock is immediate on a synchronous backend: no request, no iterator.
                        ReleaseShared(_wal.Index, WalIndex.ReadLock0 + _wal.ReadSlot);
                        _wal.ReadSlot = -1;
                    }

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

                    _state = TxState.None;
                    _werStep = 1;
                    break;
                case 1:
                {
                    bool run = _walCommitted && WalAutoCheckpoint > 0 && _wal!.Index.TryReadHeader(out WalIndexHeader h) && h.MxFrame >= (uint)WalAutoCheckpoint;
                    _walCommitted = false;
                    if (!run)
                    {
                        _werStep = 0;
                        return PagerStatus.Ok;
                    }

                    _ckMode = CheckpointMode.Passive;
                    _werStep = 2;
                    break;
                }

                default:
                {
                    // After the commit, not part of it: a failure here does not undo the transaction, so it is not reported.
                    _wOp ??= WalCheckpointOp().GetEnumerator();
                    PagerStatus s = WalPump();
                    if (s == PagerStatus.Pending)
                    {
                        return s;
                    }

                    if (s == PagerStatus.Error)
                    {
                        _error = null;
                        WalClearProgress();
                    }

                    _werStep = 0;
                    _state = TxState.None;
                    return PagerStatus.Ok;
                }
            }
        }
    }

    // ---- write transactions --------------------------------------------------------------------------------------------------------

    private PagerStatus WalBeginWriteStep()
    {
        if (_wal!.ReadOnly)
        {
            return Fail(ResultCode.ReadOnly, "the database is read-only");
        }

        _wOp ??= WalBeginWriteOp().GetEnumerator();
        return WalPump();
    }

    private IEnumerable<IoRequest> WalBeginWriteOp()
    {
        WalConnection w = _wal!;
        foreach (IoRequest r in WalLock(WalIndex.WriteLock, 1, true, true))
        {
            yield return r;
        }

        if (_wFailCode != 0)
        {
            yield break;
        }

        if (!_wGot)
        {
            WFail(ResultCode.Busy, "the write lock of the WAL is held by another connection");
            yield break;
        }

        // The write lock is only useful if nobody committed since this read transaction started.
        if (!w.Index.TryReadHeader(out WalIndexHeader cur) || cur != w.Snap)
        {
            foreach (IoRequest r in WalUnlock(WalIndex.WriteLock, 1, true))
            {
                yield return r;
            }

            WFail(ResultCode.BusySnapshot, "the database changed after this transaction began reading it");
            yield break;
        }

        w.Hdr = cur;
        w.Writing = true;
        w.RestartChecked = false;
        w.Restarted = false;
    }

    private static uint SaltValue(uint raw) => BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(raw) : raw;

    // Starts a new log: salt-1 + 1, a fresh salt-2, nothing backfilled, no frames (wal "WAL Reset"; walformat 2.1.2).
    private void WalRestartHeader(WalConnection w)
    {
        uint s1 = unchecked(SaltValue(w.Hdr.Salt0) + 1);
        uint s2 = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
        w.Hdr.SetSalts(SaltValue(s1), SaltValue(s2));
        w.Hdr.W4 = 0;
        w.Hdr.SetFrameChecksum(0, 0);
        w.Hdr.W2 = unchecked(w.Hdr.W2 + 1);
        w.Seq = unchecked(w.Seq + 1);
        w.Index.ResetCheckpointInfo(0);
        w.Index.WriteHeader(ref w.Hdr);
        w.Snap = w.Hdr;
        w.ReadMx = 0;
        _walLastSnapValid = false;
    }

    // Appends the frames in _wBatch[0.._wBatchCount) to the WAL. With commit set the last one is a commit frame carrying the database size, the
    // log is synced as synchronous demands and the new header is published: that publication is the commit point (walformat 2.1.1).
    private IEnumerable<IoRequest> WalAppendOp(bool commit)
    {
        WalConnection w = _wal!;
        int ps = _pageSize;
        int count = _wBatchCount;

        if (!w.RestartChecked)
        {
            w.RestartChecked = true;
            // The log may start over only if everything in it is in the database and no reader still uses it: we hold read lock 0 and can take
            // read locks 1 to 4 exclusively.
            if (w.ReadSlot == 0 && w.Hdr.MxFrame > 0 && w.Index.NBackfill == w.Hdr.MxFrame)
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
                    WalRestartHeader(w);
                    w.Restarted = true;
                    foreach (IoRequest r in WalUnlock(WalIndex.ReadLock0 + 1, WalIndex.ReadSlots - 1, true))
                    {
                        yield return r;
                    }
                }
            }
        }

        uint last = w.Hdr.MxFrame + (uint)count;
        for (int t = 0; t < WalIndex.RegionsFor(last); t++)
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

        bool big = w.Hdr.BigEndianChecksums;
        uint c0;
        uint c1;
        uint salt1 = SaltValue(w.Hdr.Salt0);
        uint salt2 = SaltValue(w.Hdr.Salt1);
        if (w.Hdr.MxFrame == 0)
        {
            // An empty log gets its header first, and the header is made durable before any frame can depend on it.
            var wh = new WalHeader { Magic = big ? WalFormat.MagicBigEndian : WalFormat.MagicLittleEndian, PageSize = ps, CheckpointSequence = w.Seq, Salt1 = salt1, Salt2 = salt2 };
            wh.Write(_wHdrBuf);
            yield return new IoRequest { Kind = IoKind.Write, Phase = ResumeState.WalWriteHeader, File = w.File, Offset = 0, Length = WalFormat.HeaderSize, Data = _wHdrBuf };
            if (_comp.Status != 0)
            {
                WFail(_comp.Status, "writing the WAL header failed");
                yield break;
            }

            if (_sync != SynchronousMode.Off)
            {
                foreach (IoRequest r in WalSyncOp(ResumeState.WalSyncHeader))
                {
                    yield return r;
                }

                if (_wFailCode != 0)
                {
                    yield break;
                }
            }

            c0 = wh.Checksum1;
            c1 = wh.Checksum2;
        }
        else
        {
            c0 = w.Hdr.FrameChecksum0;
            c1 = w.Hdr.FrameChecksum1;
        }

        var fh = new byte[WalFormat.FrameHeaderSize * count];
        var run = new ReadOnlyMemory<byte>[2 * count];
        for (int i = 0; i < count; i++)
        {
            PageFrame f = _wBatch[i];
            uint frame = w.Hdr.MxFrame + 1 + (uint)i;
            Span<byte> h = fh.AsSpan(WalFormat.FrameHeaderSize * i, WalFormat.FrameHeaderSize);
            BinaryPrimitives.WriteUInt32BigEndian(h, f.PageNumber);
            BinaryPrimitives.WriteUInt32BigEndian(h[4..], commit && i == count - 1 ? _dbSize : 0);
            BinaryPrimitives.WriteUInt32BigEndian(h[8..], salt1);
            BinaryPrimitives.WriteUInt32BigEndian(h[12..], salt2);
            WalFormat.Checksum(big, h[..8], ref c0, ref c1);
            byte[] stored = f.Data;
            if (_cipher is not null)
            {
                // The frame holds the encoded page and the WAL checksum covers those stored bytes (observed on the reference's WAL files).
                stored = new byte[ps];
                EncodeForWrite(f.PageNumber, f.Data, stored);
            }

            WalFormat.Checksum(big, stored, ref c0, ref c1);
            BinaryPrimitives.WriteUInt32BigEndian(h[16..], c0);
            BinaryPrimitives.WriteUInt32BigEndian(h[20..], c1);
            run[2 * i] = fh.AsMemory(WalFormat.FrameHeaderSize * i, WalFormat.FrameHeaderSize);
            run[2 * i + 1] = stored;
            if (!w.Index.Append(frame, f.PageNumber))
            {
                WFail(ResultCode.Corrupt, "the wal-index hash table is damaged");
                yield break;
            }
        }

        yield return new IoRequest { Kind = IoKind.WriteRun, Phase = ResumeState.WalAppend, File = w.File, Offset = WalFormat.FrameOffset(w.Hdr.MxFrame + 1, ps), Run = run, RunCount = run.Length };
        if (_comp.Status != 0)
        {
            WFail(_comp.Status, "appending to the WAL failed");
            yield break;
        }

        w.Hdr.W4 = last;
        w.Hdr.SetFrameChecksum(c0, c1);
        if (!commit)
        {
            yield break;
        }

        // A grouped commit leaves the sync and the publication to GroupAckOp, which may share them with other commits (docs/wal.md).
        bool grouped = _grpRole != GroupRole.None;
        if (!grouped && _sync >= SynchronousMode.Full)
        {
            foreach (IoRequest r in WalSyncOp(ResumeState.WalSync))
            {
                yield return r;
            }

            if (_wFailCode != 0)
            {
                yield break;
            }
        }

        w.Hdr.W5 = _dbSize;
        w.Hdr.W2 = unchecked(w.Hdr.W2 + 1);
        if (grouped)
        {
            w.Hdr.Seal();
        }
        else
        {
            w.Index.WriteHeader(ref w.Hdr);
            w.Snap = w.Hdr;
            w.ReadMx = w.Hdr.MxFrame;
        }

        // journal_size_limit: after a restart, a log file longer than the limit is cut back, but never into the frames just written.
        long limit = JournalSizeLimit;
        if (w.Restarted && limit >= 0)
        {
            yield return Rq(ResumeState.WalFileSize, IoKind.FileSize, w.File);
            if (_comp.Status == 0)
            {
                long keep = Math.Max(limit, WalFormat.FrameOffset(w.Hdr.MxFrame + 1, ps));
                if (_comp.Value > keep)
                {
                    yield return Rq(ResumeState.WalSizeLimit, IoKind.Truncate, w.File, offset: keep);
                }
            }
        }

        if (grouped)
        {
            foreach (IoRequest r in GroupAckOp())
            {
                yield return r;
            }
        }
    }

    // Syncs the WAL; the first sync of a connection under FULL also syncs the directory so that the new file's entry is durable.
    private IEnumerable<IoRequest> WalSyncOp(ResumeState phase)
    {
        WalConnection w = _wal!;
        yield return Rq(phase, IoKind.Sync, w.File, arg: (int)SyncKind());
        if (_comp.Status != 0)
        {
            WFail(_comp.Status, "syncing the WAL failed");
            yield break;
        }

        if (!w.DirSynced && _sync >= SynchronousMode.Full)
        {
            w.DirSynced = true;
            yield return Rq(ResumeState.WalDirSync, IoKind.SyncDirectory, w.File);
            if (_comp.Status != 0)
            {
                WFail(_comp.Status, "syncing the directory failed");
            }
        }
    }

    private IEnumerable<IoRequest> WalEndWriteOp(bool rollback)
    {
        WalConnection w = _wal!;
        GroupRole role = _grpRole;
        if (role == GroupRole.Anchor)
        {
            // Let the commits of this generation finish before the lock goes back, whether this one committed or not.
            foreach (IoRequest r in GroupDrainOp())
            {
                yield return r;
            }
        }
        else if (role == GroupRole.Follower)
        {
            GroupLeaveFollower();
        }

        if (rollback)
        {
            // Frames after the published ones are dead: take their hash entries out so the next append starts clean. (A follower leaves that to the
            // anchor, which cleans up after the whole generation; the frames of commits still being synced are not dead.)
            if (w.Index.TryReadHeader(out WalIndexHeader cur))
            {
                if (role != GroupRole.Follower)
                {
                    w.Index.Cleanup(cur.MxFrame);
                }
                if (cur != w.Snap)
                {
                    w.Snap = cur;
                    w.ReadMx = w.ReadSlot == 0 ? 0 : cur.MxFrame;
                    _walLastSnapValid = false;
                }
            }
            else if (role != GroupRole.Follower)
            {
                w.Index.Cleanup(w.Snap.MxFrame);
            }
        }

        w.Hdr = w.Snap;
        w.Writing = false;
        if (role == GroupRole.Follower)
        {
            // The write lock is the anchor's.
            yield break;
        }

        foreach (IoRequest r in WalUnlock(WalIndex.WriteLock, 1, true))
        {
            yield return r;
        }

        if (role == GroupRole.Anchor)
        {
            GroupCloseGeneration();
        }
    }

    private PagerStatus SpillWal()
    {
        for (; ; )
        {
            switch (_wsStep)
            {
                case 0:
                {
                    _wSpillFrame = null;
                    foreach (PageFrame d in _dirty)
                    {
                        if (d.Pins == 0)
                        {
                            _wSpillFrame = d;
                            break;
                        }
                    }

                    if (_wSpillFrame == null)
                    {
                        _spillNone = true;
                        return PagerStatus.Ok;
                    }

                    if (_wBatch.Length < 1)
                    {
                        _wBatch = new PageFrame[1];
                    }

                    _wBatch[0] = _wSpillFrame;
                    _wBatchCount = 1;
                    _wOp = WalAppendOp(commit: false).GetEnumerator();
                    _wsStep = 1;
                    break;
                }

                default:
                {
                    // A page that goes into the WAL before the commit is invisible to everyone else: the published frame count does not cover it.
                    PagerStatus s = WalPump();
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    PageFrame f = _wSpillFrame!;
                    f.Dirty = false;
                    _dirty.Remove(f);
                    _dbModified = true;
                    _wal!.Spilled = true;
                    _wSpillFrame = null;
                    _wsStep = 0;
                    return PagerStatus.Ok;
                }
            }
        }
    }

    private PagerStatus CommitWal()
    {
        for (; ; )
        {
            switch (_wcStep)
            {
                case 0:
                    _wcStep = _dirty.Count == 0 && !_dbModified ? 6 : 10;
                    break;
                case 10:
                {
                    PagerStatus s = AutoVacuumCommit();
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _wcStep = 1;
                    break;
                }

                case 1:
                {
                    PagerStatus s = GetCore(1, GetOptions.None, out PageFrame? f);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _cmFrame = f;
                    _wcStep = 2;
                    break;
                }

                case 2:
                {
                    // The header's size and version-valid-for must describe this commit; the change counter is not used for WAL (fileformat2 1.3).
                    Span<byte> p1 = _cmFrame!.Data;
                    uint counter = DatabaseHeader.GetUInt32(p1, DatabaseHeader.ChangeCounterOffset);
                    bool stale = DatabaseHeader.GetUInt32(p1, DatabaseHeader.DatabaseSizeOffset) != _dbSize || DatabaseHeader.GetUInt32(p1, DatabaseHeader.VersionValidForOffset) != counter;
                    if (stale || _cmFrame.Dirty)
                    {
                        PagerStatus s = MakeWritable(_cmFrame);
                        if (s != PagerStatus.Ok)
                        {
                            return s;
                        }

                        DatabaseHeader.SetUInt32(p1, DatabaseHeader.DatabaseSizeOffset, _dbSize);
                        DatabaseHeader.SetUInt32(p1, DatabaseHeader.VersionValidForOffset, counter);
                    }

                    _changeCounter = counter;
                    _cmFrame.Pins--;
                    _cmFrame = null;
                    _wcStep = 3;
                    break;
                }

                case 3:
                {
                    _dirty.Sort(ByPage);
                    while (_dirty.Count > 0 && _dirty[^1].PageNumber > _dbSize)
                    {
                        // A page past the end of the database after the commit (the file shrank) is not written.
                        PageFrame gone = _dirty[^1];
                        _dirty.RemoveAt(_dirty.Count - 1);
                        _cache.Remove(gone.PageNumber);
                        gone.PageNumber = 0;
                        gone.Dirty = false;
                        gone.Pins = 0;
                        _freeFrames.Push(gone);
                    }

                    if (_dirty.Count == 0)
                    {
                        _wcStep = 6;
                        break;
                    }

                    if (_wBatch.Length < _dirty.Count)
                    {
                        _wBatch = new PageFrame[_dirty.Count];
                    }

                    _dirty.CopyTo(_wBatch);
                    _wBatchCount = _dirty.Count;
                    _wOp = WalAppendOp(commit: true).GetEnumerator();
                    _wcStep = 4;
                    break;
                }

                case 4:
                {
                    PagerStatus s = WalPump();
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    foreach (PageFrame f in _dirty)
                    {
                        f.Dirty = false;
                    }

                    Array.Clear(_wBatch, 0, _wBatchCount);
                    _hasHeader = true;
                    _origSize = _dbSize;
                    EndRekey(committed: true);
                    ResetWriteState();
                    _walCommitted = true;
                    _wcStep = 6;
                    break;
                }

                case 6:
                    ResetWriteState();
                    _wOp = WalEndWriteOp(rollback: false).GetEnumerator();
                    _wcStep = 7;
                    break;
                default:
                {
                    PagerStatus s = WalPump();
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _walLastSnap = _wal!.Snap;
                    _walLastSnapValid = true;
                    _wcStep = 0;
                    _state = TxState.Read;
                    return PagerStatus.Ok;
                }
            }
        }
    }

    private PagerStatus RollbackWal()
    {
        for (; ; )
        {
            switch (_wrbStep)
            {
                case 0:
                {
                    // Dropping the cached changes is the whole rollback of the data: nothing of this transaction reached anything others can see.
                    foreach (PageFrame f in _dirty)
                    {
                        _cache.Remove(f.PageNumber);
                        f.PageNumber = 0;
                        f.Dirty = false;
                        f.Pins = 0;
                        _freeFrames.Push(f);
                    }

                    _dirty.Clear();
                    if (_wal!.Spilled)
                    {
                        // Pages that were pushed into the WAL by a cache spill are clean in the cache but belong to the abandoned transaction.
                        DropCleanFrames();
                        _wal.Spilled = false;
                    }

                    _dbSize = _origSize;
                    _largestRoot = _largestRootAtBegin;
                    _autoVacuum = _autoVacuumAtBegin;
                    _incremental = _incrementalAtBegin;
                    EndRekey(committed: false);
                    ResetWriteState();
                    if (_concurrent)
                    {
                        // A concurrent transaction that never took the write lock has nothing to release.
                        _concurrent = false;
                        _ccStep = 0;
                        _wrbStep = 0;
                        _state = TxState.Read;
                        return PagerStatus.Ok;
                    }

                    _wOp = WalEndWriteOp(rollback: true).GetEnumerator();
                    _wrbStep = 1;
                    break;
                }

                default:
                {
                    PagerStatus s = WalPump();
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _wrbStep = 0;
                    _state = TxState.Read;
                    return PagerStatus.Ok;
                }
            }
        }
    }
}
