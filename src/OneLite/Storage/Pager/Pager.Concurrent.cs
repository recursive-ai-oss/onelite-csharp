using RecursiveAI.OneLite.Storage.Vfs;
using RecursiveAI.OneLite.Storage.Wal;

namespace RecursiveAI.OneLite.Storage.Pager;

// Concurrent write transactions (docs/concurrency.md). The transaction runs on its WAL snapshot without the write lock; its changes stay in the
// page cache. At commit the pager takes the WAL write lock. If nobody committed in the meantime the cached pages are written as usual.
// Otherwise the pages are dropped, the pager moves to the latest snapshot while still holding the lock, and the engine's replay hook re-runs
// the transaction's trace there; then the ordinary WAL commit writes the result.
public sealed partial class Pager
{
    private bool _concurrent;
    private int _ccStep;
    private bool _ccStale;
    private OneLiteException? _ccConflict;

    /// <summary>The trace of the open concurrent transaction (savepoint boundaries are recorded here); null for ordinary transactions.</summary>
    internal BTree.ConcTrace? Trace { get; set; }

    /// <summary>Runs the transaction's trace on the latest snapshot after a rebase; set by the engine. Returns Ok, Pending (called again) or Error (the pager error is set by the caller of the hook).</summary>
    internal Func<PagerStatus>? ConcurrentReplay { get; set; }

    /// <summary>The error a failed replay reports; the hook sets it before returning Error.</summary>
    internal OneLiteException? ConcurrentConflict { set => _ccConflict = value; }

    /// <summary>
    /// The <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> value after which a replay that holds the write lock gives up (0: no limit). Set when the
    /// lock is taken, from <see cref="PagerOptions.ConcurrentCommitHoldLimitMs"/>. The clock only decides whether to abort; it never decides file content.
    /// </summary>
    internal long ConcurrentHoldDeadline { get; private set; }

    /// <summary>True between <see cref="BeginWriteConcurrent"/> and the end of the transaction.</summary>
    public bool IsConcurrent => _concurrent || _ccStep != 0;

    /// <summary>
    /// Starts a write transaction without taking the WAL write lock: the changes stay in memory until <see cref="Commit"/>. Only in WAL mode.
    /// </summary>
    /// <returns>The status.</returns>
    /// <exception cref="OneLiteException"><c>SQLITE_MISUSE</c> outside a read transaction.</exception>
    public PagerStatus BeginWriteConcurrent()
    {
        Guard();
        if (_state == TxState.None)
        {
            throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "BeginWriteConcurrent outside a read transaction");
        }

        if (_state == TxState.Write)
        {
            return PagerStatus.Ok;
        }

        if (_wal == null)
        {
            return Fail(ResultCode.Error, "BEGIN CONCURRENT needs the database to be in WAL mode");
        }

        if (_opts.ReadOnly || _wal.ReadOnly)
        {
            return Fail(ResultCode.ReadOnly, "the database is read-only");
        }

        ResetWriteState();
        _origSize = _dbSize;
        _largestRootAtBegin = LargestRootPage;
        _autoVacuumAtBegin = _autoVacuum;
        _incrementalAtBegin = _incremental;
        _concurrent = true;
        _ccStep = 0;
        _state = TxState.Write;
        return PagerStatus.Ok;
    }

    private void DiscardDirty()
    {
        foreach (PageFrame f in _dirty)
        {
            _cache.Remove(f.PageNumber);
            f.PageNumber = 0;
            f.Dirty = false;
            f.Pins = 0;
            _freeFrames.Push(f);
        }

        _dirty.Clear();
        _dbSize = _origSize;
        _largestRoot = _largestRootAtBegin;
        _autoVacuum = _autoVacuumAtBegin;
        _incremental = _incrementalAtBegin;
        EndRekey(committed: false);
        ResetWriteState();
    }

    /// <summary>True after a concurrent commit was given up part way (cancelled): the transaction's work and the write lock are gone and it can only be rolled back.</summary>
    internal bool ConcurrentAbandoned { get; set; }

    // The caller walked away from a concurrent commit (cancelled it, or reset the statement) between its steps. The lock goes now, not
    // when somebody remembers to roll back; the cached changes go with it, so the transaction cannot be committed any more.
    private void ConcAbandon()
    {
        if (_ccStep == 0 || _ccStep == 9)
        {
            return;
        }

        if (_ccStep == 1)
        {
            _ccStep = 0;
            return;
        }

        WalConnection w = _wal!;
        DiscardDirty();
        try
        {
            if ((_wHeldExcl & (1 << WalIndex.WriteLock)) != 0)
            {
                w.Index.Shm.Lock(WalIndex.WriteLock, 1, ShmLockOptions.Unlock | ShmLockOptions.Exclusive);
                _wHeldExcl &= ~(1 << WalIndex.WriteLock);
            }
        }
        catch (VfsException)
        {
            // Unlocking cannot fail in the backends here; if one does, the lock goes with the handle.
        }

        w.Writing = false;
        w.Hdr = w.Snap;
        _state = w.ReadSlot >= 0 ? TxState.Read : TxState.None;
        _concurrent = false;
        _ccStep = 0;
        ConcurrentAbandoned = true;
    }

    private IEnumerable<IoRequest> ConcLockOp()
    {
        WalConnection w = _wal!;
        foreach (IoRequest r in WalLock(WalIndex.WriteLock, 1, true, true, spin: true))
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

        // From here the lock belongs to the transaction: a failure further on releases it in Rollback.
        w.Writing = true;
        w.RestartChecked = false;
        w.Restarted = false;
        ConcurrentHoldDeadline = _opts.ConcurrentCommitHoldLimitMs > 0
            ? System.Diagnostics.Stopwatch.GetTimestamp() + (_opts.ConcurrentCommitHoldLimitMs * System.Diagnostics.Stopwatch.Frequency / 1000)
            : 0;
        _ccStale = !w.Index.TryReadHeader(out WalIndexHeader cur) || cur != w.Snap;
        if (!_ccStale)
        {
            w.Hdr = cur;
        }
    }

    private PagerStatus CommitConcurrent()
    {
        for (; ; )
        {
            switch (_ccStep)
            {
                case 0:
                    if (_dirty.Count == 0 && !_dbModified && Trace is not { Wrote: true })
                    {
                        // Nothing to write: no lock, no log.
                        ResetWriteState();
                        _concurrent = false;
                        _state = TxState.Read;
                        return PagerStatus.Ok;
                    }

                    _ccStep = 1;
                    break;
                case 1:
                {
                    _wOp ??= ConcLockOp().GetEnumerator();
                    PagerStatus s = WalPump();
                    if (s != PagerStatus.Ok)
                    {
                        if (s == PagerStatus.Error)
                        {
                            _ccStep = 0;
                        }

                        return s;
                    }

                    if (_ccStale && Trace is { OpaqueReason: { } why })
                    {
                        _ccConflict = new OneLiteException(ResultCode.BusySnapshot, Msg(ResultCode.BusySnapshot), "another transaction committed since this one began, and it " + why + " (so it cannot be replayed row by row)");
                        _ccStep = 20;
                        break;
                    }

                    if (_ccStale && ConcurrentReplay is null)
                    {
                        _ccConflict = new OneLiteException(ResultCode.BusySnapshot, Msg(ResultCode.BusySnapshot), "another transaction committed since this one began");
                        _ccStep = 20;
                        break;
                    }

                    _concurrent = false;
                    _ccStep = _ccStale ? 2 : 9;
                    break;
                }

                case 2:
                    // Rebase: the cached pages belong to the old snapshot. Drop them and the read mark, and read the latest snapshot while
                    // the write lock keeps everybody else from moving it.
                    DiscardDirty();
                    _state = TxState.Read;
                    _ccStep = 3;
                    break;
                case 3:
                {
                    PagerStatus s = WalEndReadStep();
                    if (s == PagerStatus.Error)
                    {
                        _ccConflict = _error as OneLiteException;
                        _ccStep = 20;
                        break;
                    }

                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    _ccStep = 4;
                    break;
                }

                case 4:
                {
                    PagerStatus s = BeginRead();
                    if (s == PagerStatus.Error)
                    {
                        _ccConflict = _error as OneLiteException;
                        _ccStep = 20;
                        break;
                    }

                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }

                    WalConnection w = _wal!;
                    w.Hdr = w.Snap;
                    w.Writing = true;
                    w.RestartChecked = false;
                    w.Restarted = false;
                    ResetWriteState();
                    _origSize = _dbSize;
                    _largestRootAtBegin = LargestRootPage;
                    _autoVacuumAtBegin = _autoVacuum;
                    _incrementalAtBegin = _incremental;
                    _state = TxState.Write;
                    _ccStep = 5;
                    break;
                }

                case 5:
                {
                    PagerStatus s = ConcurrentReplay!();
                    if (s == PagerStatus.Pending)
                    {
                        return s;
                    }

                    if (s == PagerStatus.Error)
                    {
                        _ccStep = 20;
                        break;
                    }

                    _ccStep = 9;
                    break;
                }

                case 9:
                {
                    PagerStatus s = CommitWal();
                    if (s != PagerStatus.Ok)
                    {
                        if (s == PagerStatus.Error)
                        {
                            _ccStep = 0;
                            _concurrent = false;
                        }

                        return s;
                    }

                    _ccStep = 0;
                    return PagerStatus.Ok;
                }

                case 20:
                {
                    // A conflict (or a replay that failed): give up the lock now, so a transaction that cannot succeed does not hold it while
                    // the application decides what to do. The pager ends in a read transaction on the latest snapshot.
                    _concurrent = false;
                    _state = TxState.Write;
                    PagerStatus s = RollbackWal();
                    if (s == PagerStatus.Pending)
                    {
                        return s;
                    }

                    if (_wal!.ReadSlot < 0)
                    {
                        // The rebase gave up the old read mark and failed before taking a new one.
                        _state = TxState.None;
                    }

                    OneLiteException err = _ccConflict ?? new OneLiteException(ResultCode.BusySnapshot, Msg(ResultCode.BusySnapshot), "another transaction committed since this one began");
                    _ccConflict = null;
                    _concurrent = false;
                    _ccStep = 0;
                    return FailWith(err);
                }

                default:
                    throw new InvalidOperationException("bad concurrent commit step");
            }
        }
    }
}
