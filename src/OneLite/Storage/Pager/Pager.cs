using System.Buffers.Binary;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Storage.Pager;

/// <summary>
/// The page manager for one database file: page cache, rollback journal, locking, commit and recovery (fileformat2, atomiccommit, lockingv3).
/// Every call that can touch storage returns <see cref="PagerStatus"/> and is re-entrant: on <see cref="PagerStatus.Pending"/> run
/// <see cref="PendingIo"/> (<see cref="ExecuteSync"/> or <see cref="ExecuteAsync"/>) and call again with the same arguments.
/// A pager is used by one thread at a time.
/// </summary>
public sealed partial class Pager : IDisposable, IAsyncDisposable
{
    private enum TxState : byte
    {
        None,
        Read,
        Write,
    }

    private readonly IVfs _vfs;
    private readonly string _path;
    private readonly string _journalPath;
    private readonly PagerOptions _opts;
    private IVfsFile? _dbFile;
    private TxState _state;
    private LockLevel _lock;

    private int _pageSize;
    private int _reserved;
    private uint _dbSize;
    private uint _origSize;
    private long _fileBytes;
    private uint _changeCounter;
    private bool _autoVacuum;
    private bool _incremental;
    private bool _autoVacuumAtBegin;
    private bool _incrementalAtBegin;
    private uint _largestRoot;
    private bool _hasHeader;
    private TextEncoding _encoding = TextEncoding.Utf8;
    private IPageSource? _source;

    private bool _pending;
    private bool _hasComp;
    private ResumeState _compPhase;
    private IoRequest _req;
    private IoCompletion _comp;
    private OneLiteException? _error;
    private bool _disposed;
    private int _busyCount;
    private bool _lockSleeping;
    private int _yieldEvery;
    private int _hits;
    private bool _yieldArmed;

    private Pager(IVfs vfs, string path, IVfsFile file, PagerOptions opts)
    {
        _vfs = vfs;
        _path = path;
        _journalPath = path + "-journal";
        _dbFile = file;
        _opts = opts;
        _yieldEvery = opts.YieldEvery;
        _pageSize = opts.PageSize;
        _reserved = opts.ReservedBytes;
        _encoding = opts.Encoding;
        _autoVacuum = opts.AutoVacuum;
        _incremental = opts.IncrementalVacuum;
        _largestRoot = opts.AutoVacuum ? 1u : 0u;
        _journalMode = opts.JournalMode;
        _sync = opts.Synchronous;
        _cacheLimitSetting = opts.CacheSize;
        InitCipher(opts);
    }

    /// <summary>Opens the database file (creating an empty one unless read-only). No lock is taken and nothing is read until <see cref="BeginRead"/>.</summary>
    /// <param name="vfs">The storage backend.</param>
    /// <param name="path">The database path; the journal is <c>path + "-journal"</c>.</param>
    /// <param name="options">Settings; null means the defaults.</param>
    /// <returns>The pager.</returns>
    /// <exception cref="OneLiteException"><c>SQLITE_CANTOPEN</c> (or the backend's extended code) if the file cannot be opened; <c>SQLITE_MISUSE</c> for invalid options.</exception>
    public static Pager Open(IVfs vfs, string path, PagerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        ArgumentNullException.ThrowIfNull(path);
        options ??= new PagerOptions();
        ValidateOptions(options);
        VfsOpenOptions flags = (options.ReadOnly ? VfsOpenOptions.ReadOnly : VfsOpenOptions.ReadWrite | VfsOpenOptions.Create) | VfsOpenOptions.MainDb;
        IVfsFile file;
        try
        {
            file = vfs.Open(path, flags);
        }
        catch (VfsException e)
        {
            throw new OneLiteException(e.ExtendedCode, Msg(e.ExtendedCode), e.Message, e);
        }

        return new Pager(vfs, path, file, options);
    }

    /// <summary>Asynchronous twin of <see cref="Open"/>.</summary>
    /// <param name="vfs">The storage backend.</param>
    /// <param name="path">The database path.</param>
    /// <param name="options">Settings; null means the defaults.</param>
    /// <param name="ct">Cancellation, observed before the open.</param>
    /// <returns>The pager.</returns>
    public static async ValueTask<Pager> OpenAsync(IVfs vfs, string path, PagerOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        ArgumentNullException.ThrowIfNull(path);
        options ??= new PagerOptions();
        ValidateOptions(options);
        VfsOpenOptions flags = (options.ReadOnly ? VfsOpenOptions.ReadOnly : VfsOpenOptions.ReadWrite | VfsOpenOptions.Create) | VfsOpenOptions.MainDb;
        IVfsFile file;
        try
        {
            file = await vfs.OpenAsync(path, flags, ct).ConfigureAwait(false);
        }
        catch (VfsException e)
        {
            throw new OneLiteException(e.ExtendedCode, Msg(e.ExtendedCode), e.Message, e);
        }

        return new Pager(vfs, path, file, options);
    }

    private static void ValidateOptions(PagerOptions o)
    {
        ValidateCipherOptions(o);
        if (o.Key != null || o.KeyProvider != null)
        {
            return;
        }

        if (DatabaseHeader.DecodePageSize(o.PageSize == 65536 ? 1 : o.PageSize) == 0 || o.ReservedBytes is < 0 or > 255 || o.PageSize - o.ReservedBytes < 480)
        {
            throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "page size must be a power of two from 512 to 65536 and leave at least 480 usable bytes");
        }
    }

    /// <summary>Page size in bytes. For a database not yet read this is the configured size for a new database.</summary>
    public int PageSize => _pageSize;

    /// <summary>Usable bytes per page (page size minus reserved bytes).</summary>
    public int UsableSize => _pageSize - _reserved;

    /// <summary>Reserved bytes at the end of each page.</summary>
    public int ReservedBytes => _reserved;

    /// <summary>The database size in pages as of the current transaction (0 for a new database).</summary>
    public uint PageCount => _dbSize;

    /// <summary>True if the database has pointer-map pages (auto-vacuum).</summary>
    public bool AutoVacuum => _autoVacuum;

    /// <summary>The text encoding recorded in the header.</summary>
    public TextEncoding DatabaseEncoding => _encoding;

    /// <summary>The current journal mode: <see cref="JournalMode.Wal"/> while the database is in WAL mode, otherwise the rollback journal mode.</summary>
    public JournalMode JournalMode => _wal != null ? JournalMode.Wal : _journalMode;

    /// <summary>The current <c>synchronous</c> setting.</summary>
    public SynchronousMode Synchronous => _sync;

    /// <summary>The page number of the lock-byte page (the page containing file offset 0x40000000), which is never used; 0 if the file can never be that large.</summary>
    public uint LockBytePage => (uint)(0x40000000L / _pageSize) + 1;

    /// <summary>True while a read or write transaction is open.</summary>
    public bool InTransaction => _state != TxState.None;

    /// <summary>True while a write transaction is open.</summary>
    public bool InWriteTransaction => _state == TxState.Write || _ccStep != 0;

    /// <summary>The error from the last call that returned <see cref="PagerStatus.Error"/>.</summary>
    public OneLiteException? Error => _error;

    /// <summary>The outstanding request. Valid while a call has returned <see cref="PagerStatus.Pending"/>.</summary>
    public ref readonly IoRequest PendingIo => ref _req;

    /// <summary>The database file lock currently held.</summary>
    public LockLevel LockLevel => _lock;

    /// <summary>Installs (or removes with null) the WAL layer's page source (the WAL seam).</summary>
    /// <param name="source">The source.</param>
    public void SetPageSource(IPageSource? source) => _source = source;

    /// <summary>Changes <c>synchronous</c>; effective from the next commit. Not allowed inside a write transaction.</summary>
    /// <param name="mode">The new setting.</param>
    public void SetSynchronous(SynchronousMode mode)
    {
        GuardNoWrite();
        _sync = mode;
    }

    /// <summary>Changes the journal mode between transactions.</summary>
    /// <param name="mode">The new mode.</param>
    public void SetJournalMode(JournalMode mode)
    {
        GuardNoWrite();
        if (mode == JournalMode.Wal || _wal != null)
        {
            throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "switching into or out of WAL mode needs I/O: use ChangeJournalMode");
        }

        _journalMode = mode;
    }

    /// <summary>Changes <c>cache_size</c> (pages if positive, KiB if negative).</summary>
    /// <param name="size">The size.</param>
    public void SetCacheSize(int size) => _cacheLimitSetting = size;

    /// <summary>
    /// Sets the page size and reserved bytes of a database that has no pages yet. After the first page exists the size is fixed
    /// (changing it is a VACUUM, which is not the pager's job).
    /// </summary>
    /// <param name="pageSize">The page size.</param>
    /// <param name="reservedBytes">Reserved bytes per page.</param>
    /// <returns>True if applied.</returns>
    public bool TrySetPageSize(int pageSize, int reservedBytes)
    {
        if (_cipher != null || _dbSize != 0 || _state == TxState.Write && _dirty.Count != 0 || DatabaseHeader.DecodePageSize(pageSize == 65536 ? 1 : pageSize) == 0 ||
            reservedBytes is < 0 or > 255 || pageSize - reservedBytes < 480)
        {
            return false;
        }

        if (pageSize != _pageSize)
        {
            _pageSize = pageSize;
            DropAllFrames();
        }

        _reserved = reservedBytes;
        return true;
    }

    /// <summary>Closes the files. A write transaction in progress is abandoned without rollback; call <see cref="Rollback"/> first if the file may have been modified.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_wal != null)
        {
            WalCloseSync();
        }

        _disposed = true;
        try
        {
            _jfile?.Dispose();
            _dbFile?.Unlock(LockLevel.None);
            _dbFile?.Dispose();
        }
        catch (VfsException)
        {
            // Closing is best effort: the locks vanish with the descriptor anyway.
        }

        _jfile = null;
        _dbFile = null;
        DisposeCipher();
    }

    /// <summary>
    /// Fails with MISUSE unless this pager's backend can answer a synchronous call that has to wait: operations that run to completion inside one VM
    /// instruction (the integrity check, VACUUM, changing the journal mode, checkpoints) cannot suspend, so on a backend with only asynchronous calls
    /// they report "backend requires the async driver" instead of failing half way (docs/engine.md).
    /// </summary>
    /// <param name="what">The operation, for the message.</param>
    /// <exception cref="OneLiteException">SQLITE_MISUSE on a backend with only asynchronous calls.</exception>
    internal void RequireBlockingBackend(string what)
    {
        if ((_vfs.Caps & (VfsCaps.Sync | VfsCaps.BlockingOk)) == 0)
        {
            throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), what + ": backend requires the async driver");
        }
    }

    /// <summary>
    /// Asynchronous twin of <see cref="Dispose"/>: releases the file lock by awaiting the backend, so a backend with only asynchronous calls (browser storage)
    /// really drops its lock. A WAL connection on a backend without synchronous calls closes without the checkpoint <see cref="Dispose"/> would run
    /// (the log stays in place and the next connection recovers it, wal "Checkpointing").
    /// </summary>
    /// <returns>A task that completes when the files are closed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_wal != null && (_vfs.Caps & VfsCaps.Sync) != 0)
        {
            WalCloseSync();
        }

        _disposed = true;
        try
        {
            _jfile?.Dispose();
            if (_dbFile != null)
            {
                await _dbFile.UnlockAsync(LockLevel.None, CancellationToken.None).ConfigureAwait(false);
                _dbFile.Dispose();
            }
        }
        catch (VfsException)
        {
            // Closing is best effort, as in Dispose.
        }

        _jfile = null;
        _dbFile = null;
        DisposeCipher();
    }

    // ---- request plumbing ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Re-entry helper. If the completion of a request issued by <paramref name="phase"/> is waiting, consumes it and returns true;
    /// otherwise records <paramref name="r"/> as the pending request and returns false (the caller returns Pending).
    /// </summary>
    private bool Io(ResumeState phase, in IoRequest r)
    {
        if (_hasComp && _compPhase == ResumeState.Yield && phase != ResumeState.Yield)
        {
            // A Yield is a suspension point with nothing behind it, injected by the test hook. A caller that repeats a longer sequence of calls
            // after it (a reserve chain restarts from its first call) reaches other I/O before the call that asked for the Yield; the Yield
            // is then moot, and the Get that armed it asks again.
            _hasComp = false;
        }

        if (_hasComp)
        {
            if (_compPhase != phase)
            {
                throw new InvalidOperationException($"pager re-entered in state {phase} but the completion belongs to {_compPhase}");
            }

            _hasComp = false;
            return true;
        }

        _req = r;
        _req.Phase = phase;
        _pending = true;
        return false;
    }

    private bool _extYieldArmed;
    private int _extHits;

    /// <summary>
    /// Re-entry helper for the engine's own temporary files (the sorter's runs): the same request slot, the same sync and async drivers and the
    /// same test yields as page reads. Returns true with the completion when the request has run; otherwise the request is pending and the
    /// caller returns Pending, to call again with the same arguments.
    /// </summary>
    internal bool ExternalIo(ResumeState phase, in IoRequest request, out IoCompletion completion)
    {
        completion = default;
        if (_yieldEvery > 0)
        {
            if (!_extYieldArmed && !_hasComp && ++_extHits % _yieldEvery == 0)
            {
                _extYieldArmed = true;
            }

            if (_extYieldArmed)
            {
                if (!Io(ResumeState.Yield, new IoRequest { Kind = IoKind.Yield, Cancellable = true }))
                {
                    return false;
                }

                _extYieldArmed = false;
            }
        }

        if (!Io(phase, request))
        {
            return false;
        }

        completion = _comp;
        return true;
    }

    /// <summary>Feeds the result of <see cref="PendingIo"/> back. Legal only while a request is pending.</summary>
    /// <param name="completion">The result.</param>
    public void Complete(in IoCompletion completion)
    {
        if (!_pending)
        {
            throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "no I/O request is pending");
        }

        _comp = completion;
        _compPhase = _req.Phase;
        _hasComp = true;
        _pending = false;
    }

    /// <summary>Drops the outstanding request without completing it (the caller will not re-enter the operation; call <see cref="Rollback"/> next if in a write transaction).</summary>
    public void Abandon()
    {
        _pending = false;
        _hasComp = false;
        ClearProgress();
    }

    private void Guard()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pending)
        {
            throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "an I/O request is pending: run it and call the same operation again");
        }
    }

    private void GuardNoWrite()
    {
        if (_state == TxState.Write)
        {
            throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "not allowed inside a write transaction");
        }
    }

    private PagerStatus Fail(int extended, string detail, Exception? inner = null)
    {
        _error = new OneLiteException(extended, Msg(extended), detail, inner);
        _pending = false;
        _hasComp = false;
        ClearProgress();
        return PagerStatus.Error;
    }

    // For a layer above the pager (the B-tree's relocation callback) that found the file damaged mid-operation.
    internal PagerStatus FailWith(OneLiteException error)
    {
        _error = error;
        _pending = false;
        _hasComp = false;
        ClearProgress();
        return PagerStatus.Error;
    }

    // The backend's exception for the request that just failed, kept so the error the caller sees carries it as its inner exception (a remote file that
    // changed, say, is a typed exception); the result code and message are the same either way.
    private VfsException? _ioFailure;

    private PagerStatus FailIo(int status, string what)
    {
        VfsException? cause = _ioFailure;
        _ioFailure = null;
        return cause is null ? Fail(status == 0 ? ResultCode.IoErr : status, what) : Fail(status == 0 ? ResultCode.IoErr : status, what + ": " + cause.Message, cause);
    }

    private PagerStatus FailCorrupt(string detail) => Fail(ResultCode.Corrupt, detail);

    internal static string Msg(int code) => (code & 0xFF) switch
    {
        ResultCode.Busy => "database is locked",
        ResultCode.Locked => "database table is locked",
        ResultCode.IoErr => "disk I/O error",
        ResultCode.Corrupt => "database disk image is malformed",
        ResultCode.Full => "database or disk is full",
        ResultCode.CantOpen => "unable to open database file",
        ResultCode.NotADb => "file is not a database",
        ResultCode.Misuse => "bad parameter or other API misuse",
        ResultCode.ReadOnly => "attempt to write a readonly database",
        ResultCode.Interrupt => "interrupted",
        ResultCode.Perm => "access permission denied",
        ResultCode.Protocol => "locking protocol",
        _ => "SQL logic error",
    };

    private ReadOnlyMemory<byte>[] _runBuffer = [];

    /// <summary>Runs <see cref="PendingIo"/> on the VFS and feeds the result back (the sync driver's step).</summary>
    public void ExecuteSync()
    {
        if (!_pending)
        {
            throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "no I/O request is pending");
        }

        IoRequest r = _req;
        IoCompletion c = default;
        _ioFailure = null;
        try
        {
            switch (r.Kind)
            {
                case IoKind.Yield:
                    break;
                case IoKind.Read:
                    c.Value = r.File!.Read(r.Offset, r.Data.Span[..r.Length]);
                    break;
                case IoKind.Write:
                    r.File!.Write(r.Offset, r.Data.Span[..r.Length]);
                    break;
                case IoKind.WriteRun:
                    r.File!.WriteRun(r.Offset, r.Run.AsSpan(0, r.RunCount));
                    break;
                case IoKind.Sync:
                    r.File!.Sync((SyncMode)r.Arg);
                    break;
                case IoKind.SyncDirectory:
                    r.File!.SyncDirectory();
                    break;
                case IoKind.Truncate:
                    r.File!.Truncate(r.Offset);
                    break;
                case IoKind.FileSize:
                    c.Value = r.File!.FileSize();
                    break;
                case IoKind.Open:
                    c.File = _vfs.Open(r.Path!, (VfsOpenOptions)r.Arg);
                    break;
                case IoKind.Close:
                    r.File!.Dispose();
                    break;
                case IoKind.Delete:
                    _vfs.Delete(r.Path!, r.Arg != 0);
                    break;
                case IoKind.Access:
                    c.Value = _vfs.Access(r.Path!, AccessMode.Exists) ? 1 : 0;
                    break;
                case IoKind.Lock:
                    c.Value = r.File!.Lock((LockLevel)r.Arg) ? 1 : 0;
                    break;
                case IoKind.Unlock:
                    r.File!.Unlock((LockLevel)r.Arg);
                    break;
                case IoKind.CheckReservedLock:
                    c.Value = r.File!.CheckReservedLock() ? 1 : 0;
                    break;
                case IoKind.Sleep:
                    _vfs.Sleep(r.Length);
                    break;
                case IoKind.ShmMap:
                    c.Region = r.Shm!.Map((int)r.Offset, r.Arg != 0);
                    break;
                case IoKind.ShmLock:
                    c.Value = r.Shm!.Lock((int)r.Offset, r.Length, (ShmLockOptions)r.Arg) ? 1 : 0;
                    break;
                default:
                    throw new InvalidOperationException("unknown request " + r.Kind);
            }
        }
        catch (VfsException e)
        {
            c.Status = e.ExtendedCode == 0 ? ResultCode.IoErr : e.ExtendedCode;
            _ioFailure = e;
        }

        Complete(c);
    }

    /// <summary>Runs <see cref="PendingIo"/> on the VFS and feeds the result back (the async driver's step).</summary>
    /// <param name="ct">Cancellation. Observed only by requests that are safe to abandon (reads, sleeps, lock attempts); writes and syncs run to completion.</param>
    /// <returns>A task completing when the result has been fed back.</returns>
    public async ValueTask ExecuteAsync(CancellationToken ct = default)
    {
        if (!_pending)
        {
            throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "no I/O request is pending");
        }

        IoRequest r = _req;
        IoCompletion c = default;
        _ioFailure = null;
        CancellationToken t = r.Cancellable ? ct : CancellationToken.None;
        try
        {
            switch (r.Kind)
            {
                case IoKind.Yield:
                    await Task.Yield();
                    break;
                case IoKind.Read:
                    c.Value = await r.File!.ReadAsync(r.Offset, r.Data[..r.Length], t).ConfigureAwait(false);
                    break;
                case IoKind.Write:
                    await r.File!.WriteAsync(r.Offset, r.Data[..r.Length], t).ConfigureAwait(false);
                    break;
                case IoKind.WriteRun:
                    await r.File!.WriteRunAsync(r.Offset, r.Run.AsMemory(0, r.RunCount), t).ConfigureAwait(false);
                    break;
                case IoKind.Sync:
                    await r.File!.SyncAsync((SyncMode)r.Arg, t).ConfigureAwait(false);
                    break;
                case IoKind.SyncDirectory:
                    await r.File!.SyncDirectoryAsync(t).ConfigureAwait(false);
                    break;
                case IoKind.Truncate:
                    await r.File!.TruncateAsync(r.Offset, t).ConfigureAwait(false);
                    break;
                case IoKind.FileSize:
                    c.Value = await r.File!.FileSizeAsync(t).ConfigureAwait(false);
                    break;
                case IoKind.Open:
                    c.File = await _vfs.OpenAsync(r.Path!, (VfsOpenOptions)r.Arg, t).ConfigureAwait(false);
                    break;
                case IoKind.Close:
                    r.File!.Dispose();
                    break;
                case IoKind.Delete:
                    await _vfs.DeleteAsync(r.Path!, r.Arg != 0, t).ConfigureAwait(false);
                    break;
                case IoKind.Access:
                    c.Value = await _vfs.AccessAsync(r.Path!, AccessMode.Exists, t).ConfigureAwait(false) ? 1 : 0;
                    break;
                case IoKind.Lock:
                    c.Value = await r.File!.LockAsync((LockLevel)r.Arg, t).ConfigureAwait(false) ? 1 : 0;
                    break;
                case IoKind.Unlock:
                    await r.File!.UnlockAsync((LockLevel)r.Arg, t).ConfigureAwait(false);
                    break;
                case IoKind.CheckReservedLock:
                    c.Value = await r.File!.CheckReservedLockAsync(t).ConfigureAwait(false) ? 1 : 0;
                    break;
                case IoKind.Sleep:
                    await _vfs.SleepAsync(r.Length, t).ConfigureAwait(false);
                    break;
                case IoKind.ShmMap:
                    c.Region = await r.Shm!.MapAsync((int)r.Offset, r.Arg != 0, t).ConfigureAwait(false);
                    break;
                case IoKind.ShmLock:
                    c.Value = await r.Shm!.LockAsync((int)r.Offset, r.Length, (ShmLockOptions)r.Arg, t).ConfigureAwait(false) ? 1 : 0;
                    break;
                default:
                    throw new InvalidOperationException("unknown request " + r.Kind);
            }
        }
        catch (VfsException e)
        {
            c.Status = e.ExtendedCode == 0 ? ResultCode.IoErr : e.ExtendedCode;
            _ioFailure = e;
        }

        Complete(c);
    }

    // ---- lock helper -------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Raises the database lock to <paramref name="level"/>, running the busy policy between attempts (lockingv3). Lock moves are try-locks;
    /// waiting is a Sleep request here, so the wait is a suspend point and cancellable.
    /// </summary>
    private PagerStatus TakeLock(LockLevel level, bool wait = true)
    {
        for (; ; )
        {
            if (_lockSleeping)
            {
                if (!Io(ResumeState.LockSleep, new IoRequest { Kind = IoKind.Sleep, Length = _sleepMs, Cancellable = true }))
                {
                    return PagerStatus.Pending;
                }

                _lockSleeping = false;
            }

            if (_lock >= level)
            {
                return PagerStatus.Ok;
            }

            if (!Io(ResumeState.LockRequest, new IoRequest { Kind = IoKind.Lock, File = _dbFile, Arg = (int)level, Cancellable = true }))
            {
                return PagerStatus.Pending;
            }

            if (_comp.Status != 0)
            {
                return FailIo(_comp.Status, "lock failed");
            }

            if (_comp.Value == 1)
            {
                _lock = level;
                _busyCount = 0;
                return PagerStatus.Ok;
            }

            int ms = wait ? _opts.Busy?.Invoke(_busyCount++) ?? -1 : -1;
            if (ms < 0)
            {
                _busyCount = 0;
                return Fail(ResultCode.Busy, "could not get the " + level + " lock");
            }

            _sleepMs = ms;
            _lockSleeping = true;
        }
    }

    private int _sleepMs;

    /// <summary>
    /// Waits <paramref name="ms"/> milliseconds as a suspend point (a Sleep request). Re-enter after Pending with the same argument. Used by the
    /// layer above to back off between whole attempts, after it has let go of its locks.
    /// </summary>
    /// <param name="ms">The delay in milliseconds.</param>
    /// <returns>Ok when the wait is over, Pending while it runs.</returns>
    public PagerStatus SleepStep(int ms)
    {
        if (!Io(ResumeState.LockSleep, new IoRequest { Kind = IoKind.Sleep, Length = ms, Cancellable = true }))
        {
            return PagerStatus.Pending;
        }

        return PagerStatus.Ok;
    }

    private static uint Be32(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadUInt32BigEndian(s);
}
