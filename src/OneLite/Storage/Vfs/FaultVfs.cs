namespace RecursiveAI.OneLite.Storage.Vfs;

/// <summary>
/// Wraps any VFS, passes every call through, and records the calls that change storage so a test can ask: "if the power had gone after
/// call n, what would the next start find?". It is the injection point of the durability tests.
/// <para>
/// <b>Model.</b> Each file has a durable image (what was there at its last sync that the device honoured) and a list of unsynced changes.
/// A crash keeps the durable image and any subset of the unsynced changes, cut into device sectors, in any order, optionally torn
/// (sqlite.org atomiccommit §2). Creating and deleting files are changes too, durable after a directory sync (see <see cref="FaultOptions"/>).
/// <see cref="CrashPolicy"/> chooses the subset from a seed, so a failing case replays exactly; <see cref="EnumerateCrashImages"/> lists them all for tiny workloads.
/// </para>
/// <para>
/// <b>Use.</b> Run the workload on a wrapped <see cref="MemoryVfs"/> (or file VFS). To stop it at a point, call <see cref="HaltAfter"/> first: the next call throws
/// <see cref="PowerLossException"/>. Then take <see cref="CrashImage(long, CrashPolicy)"/> at the point you want (it can be any point up to <see cref="OpCount"/>,
/// so one run serves every crash point), load it with <see cref="Storage.Vfs.CrashImage.ToMemoryVfs"/> and run recovery on it.
/// The log holds copies of all written bytes; use it for small workloads.
/// </para>
/// </summary>
public sealed class FaultVfs : VfsBase
{
    private readonly object _gate = new();
    private readonly FaultOptions _options;
    private readonly List<LogEntry> _log = [];
    private readonly List<FaultOp> _ops = [];
    private readonly List<FaultRule> _rules = [];
    private readonly Dictionary<FaultCall, int> _counts = [];
    private readonly Dictionary<string, int> _live = new(StringComparer.Ordinal);
    private readonly HashSet<int> _lostSyncs = [];
    private int _nextNode = 1;
    private int _syncCalls;
    private long _haltAfter = -1;

    /// <summary>Wraps <paramref name="inner"/>.</summary>
    public FaultVfs(IVfs inner, FaultOptions? options = null)
    {
        Inner = inner;
        _options = options ?? new FaultOptions();
        if (_options.SectorSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SectorSize must be positive.");
        }
    }

    /// <summary>The wrapped VFS.</summary>
    public IVfs Inner { get; }

    /// <inheritdoc/>
    public override string Name => "fault(" + Inner.Name + ")";

    /// <inheritdoc/>
    public override VfsCaps Caps => Inner.Caps;

    /// <inheritdoc/>
    public override int CurrentTimeMs => Inner.CurrentTimeMs;

    /// <summary>The number of storage-changing calls so far; the highest valid crash point.</summary>
    public long OpCount
    {
        get
        {
            lock (_gate)
            {
                return _ops.Count;
            }
        }
    }

    /// <summary>A snapshot of the logged calls.</summary>
    public IReadOnlyList<FaultOp> Ops
    {
        get
        {
            lock (_gate)
            {
                return [.. _ops];
            }
        }
    }

    /// <summary>Makes every call after the first <paramref name="opCount"/> logged calls throw <see cref="PowerLossException"/>. Use -1 to switch it off.</summary>
    public void HaltAfter(long opCount)
    {
        lock (_gate)
        {
            _haltAfter = opCount;
        }
    }

    /// <summary>Adds an injected failure.</summary>
    public void AddRule(FaultRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        lock (_gate)
        {
            _rules.Add(rule);
        }
    }

    /// <summary>Makes the <paramref name="nth"/> sync call (counting from 1 over the whole VFS) report success without making anything durable.</summary>
    public void LoseSync(int nth)
    {
        lock (_gate)
        {
            _lostSyncs.Add(nth);
        }
    }

    // ---- IVfs ----

    /// <inheritdoc/>
    public override string FullPathname(string path) => Inner.FullPathname(path);

    /// <inheritdoc/>
    public override IVfsFile Open(string path, VfsOpenOptions flags)
    {
        Begin(FaultCall.Open, 0, out _);
        string full = path.Length == 0 ? string.Empty : Inner.FullPathname(path);
        bool track = path.Length != 0 && (flags & VfsOpenOptions.DeleteOnClose) == 0;
        bool existed = track && Inner.Access(full, AccessMode.Exists);
        IVfsFile file = Inner.Open(path, flags);
        return Wrap(file, full, track, existed, existed ? ReadAll(file) : null);
    }

    /// <inheritdoc/>
    public override async ValueTask<IVfsFile> OpenAsync(string path, VfsOpenOptions flags, CancellationToken ct = default)
    {
        Begin(FaultCall.Open, 0, out _);
        string full = path.Length == 0 ? string.Empty : Inner.FullPathname(path);
        bool track = path.Length != 0 && (flags & VfsOpenOptions.DeleteOnClose) == 0;
        bool existed = track && await Inner.AccessAsync(full, AccessMode.Exists, ct).ConfigureAwait(false);
        IVfsFile file = await Inner.OpenAsync(path, flags, ct).ConfigureAwait(false);
        byte[]? content = null;
        if (existed)
        {
            content = new byte[await file.FileSizeAsync(ct).ConfigureAwait(false)];
            int n = await file.ReadAsync(0, content, ct).ConfigureAwait(false);
            Array.Resize(ref content, n);
        }

        return Wrap(file, full, track, existed, content);
    }

    private static byte[] ReadAll(IVfsFile file)
    {
        var buf = new byte[file.FileSize()];
        int n = file.Read(0, buf);
        Array.Resize(ref buf, n);
        return buf;
    }

    private FaultFile Wrap(IVfsFile file, string full, bool track, bool existed, byte[]? content)
    {
        int node = 0;
        if (track)
        {
            lock (_gate)
            {
                if (!_live.TryGetValue(full, out node))
                {
                    node = _nextNode++;
                    _live[full] = node;
                    if (existed)
                    {
                        _log.Add(new LogEntry(-1, LogKind.Baseline, node, full, 0, 0, content, false, false));
                    }
                    else
                    {
                        AddOp(FaultOpKind.Create, full, node, 0, 0, null, false, false);
                    }
                }
            }
        }

        return new FaultFile(this, file, full, node);
    }

    /// <inheritdoc/>
    public override bool Access(string path, AccessMode mode) => Inner.Access(path, mode);

    /// <inheritdoc/>
    public override ValueTask<bool> AccessAsync(string path, AccessMode mode, CancellationToken ct = default) => Inner.AccessAsync(path, mode, ct);

    /// <inheritdoc/>
    public override void Delete(string path, bool syncDirectory)
    {
        Begin(FaultCall.Delete, 0, out _);
        Inner.Delete(path, syncDirectory);
        RecordDelete(path, syncDirectory);
    }

    /// <inheritdoc/>
    public override async ValueTask DeleteAsync(string path, bool syncDirectory, CancellationToken ct = default)
    {
        Begin(FaultCall.Delete, 0, out _);
        await Inner.DeleteAsync(path, syncDirectory, ct).ConfigureAwait(false);
        RecordDelete(path, syncDirectory);
    }

    private void RecordDelete(string path, bool syncDirectory)
    {
        string full = Inner.FullPathname(path);
        lock (_gate)
        {
            if (_live.Remove(full, out int node))
            {
                AddOp(FaultOpKind.Delete, full, node, 0, 0, null, false, syncDirectory);
            }
        }
    }

    /// <inheritdoc/>
    public override void Sleep(int ms) => Inner.Sleep(ms);

    /// <inheritdoc/>
    public override ValueTask SleepAsync(int ms, CancellationToken ct = default) => Inner.SleepAsync(ms, ct);

    // ---- call bookkeeping, used by FaultFile ----

    /// <summary>Counts a call, applies the halt and the injected failures. Returns a failure to throw after the caller has done its partial work, or null.</summary>
    internal void Begin(FaultCall call, int writeLength, out FaultRule? failure)
    {
        failure = null;
        lock (_gate)
        {
            if (_haltAfter >= 0 && _ops.Count >= _haltAfter)
            {
                throw new PowerLossException();
            }

            _counts.TryGetValue(call, out int n);
            _counts[call] = ++n;
            foreach (FaultRule r in _rules)
            {
                if (r.Call == call && (r.Nth == n || (r.Persistent && n >= r.Nth)))
                {
                    failure = r;
                    break;
                }
            }
        }

        if (failure != null && !(call == FaultCall.Write && failure.PartialBytes > 0 && writeLength > 0))
        {
            throw Failure(failure);
        }
    }

    internal static VfsException Failure(FaultRule rule) => new(rule.ExtendedCode, $"injected failure of {rule.Call} #{rule.Nth}");

    /// <summary>Decides whether the next sync is lost; counts it.</summary>
    internal bool NextSyncLost()
    {
        lock (_gate)
        {
            _syncCalls++;
            return _options.LoseAllSyncs || _lostSyncs.Contains(_syncCalls);
        }
    }

    internal void RecordWrite(string path, int node, long offset, ReadOnlySpan<byte> data)
    {
        if (node == 0)
        {
            return;
        }

        lock (_gate)
        {
            AddOp(FaultOpKind.Write, path, node, offset, data.Length, data.ToArray(), false, false);
        }
    }

    internal void RecordTruncate(string path, int node, long size)
    {
        if (node != 0)
        {
            lock (_gate)
            {
                AddOp(FaultOpKind.Truncate, path, node, 0, size, null, false, false);
            }
        }
    }

    internal void RecordSync(string path, int node, bool lost)
    {
        if (node != 0)
        {
            lock (_gate)
            {
                AddOp(FaultOpKind.Sync, path, node, 0, 0, null, lost, false);
            }
        }
    }

    internal void RecordSyncDirectory(string path)
    {
        lock (_gate)
        {
            AddOp(FaultOpKind.SyncDirectory, path, 0, 0, 0, null, false, false);
        }
    }

    private void AddOp(FaultOpKind kind, string path, int node, long offset, long size, byte[]? data, bool lost, bool syncDirectory)
    {
        long index = _ops.Count;
        _ops.Add(new FaultOp(index, kind, path, node, offset, size, data, lost, syncDirectory));
        _log.Add(new LogEntry(index, (LogKind)(int)kind, node, path, offset, size, data, lost, syncDirectory));
    }

    // ---- crash images ----

    /// <summary>The files a restart would find if the power went after <paramref name="afterOps"/> logged calls.</summary>
    public CrashImage CrashImage(long afterOps, CrashPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return BuildModel(afterOps).Realize(policy, afterOps);
    }

    /// <summary>The files a restart would find if the power went now.</summary>
    public CrashImage CrashImage(CrashPolicy policy) => CrashImage(OpCount, policy);

    /// <summary>
    /// Every distinct set of files a crash after <paramref name="afterOps"/> calls can leave when each unsynced sector write, truncate and
    /// directory change survives or not independently (surviving changes apply in issue order, so no reordering of overlapping writes and no torn sectors; use
    /// <see cref="CrashPolicy"/> for those). Throws if more than <paramref name="maxUnits"/> units are unsynced.
    /// </summary>
    public IEnumerable<CrashImage> EnumerateCrashImages(long afterOps, int maxUnits = 16) => BuildModel(afterOps).Enumerate(afterOps, maxUnits);

    private CrashModel BuildModel(long afterOps)
    {
        List<LogEntry> prefix;
        lock (_gate)
        {
            if (afterOps < 0 || afterOps > _ops.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(afterOps), $"There are {_ops.Count} logged calls.");
            }

            prefix = _log.FindAll(e => e.Index < afterOps);
        }

        return CrashModel.Replay(prefix, _options);
    }
}

internal enum LogKind
{
    Create,
    Write,
    Truncate,
    Sync,
    SyncDirectory,
    Delete,
    Baseline,
}

internal sealed class LogEntry
{
    public LogEntry(long index, LogKind kind, int node, string path, long offset, long size, byte[]? data, bool lost, bool syncDirectory)
    {
        Index = index;
        Kind = kind;
        Node = node;
        Path = path;
        Offset = offset;
        Size = size;
        Data = data;
        Lost = lost;
        SyncDirectory = syncDirectory;
    }

    public long Index { get; }

    public LogKind Kind { get; }

    public int Node { get; }

    public string Path { get; }

    public long Offset { get; }

    public long Size { get; }

    public byte[]? Data { get; }

    public bool Lost { get; }

    public bool SyncDirectory { get; }
}

/// <summary>A file handed out by <see cref="FaultVfs"/>.</summary>
internal sealed class FaultFile : VfsFileBase
{
    private readonly FaultVfs _vfs;
    private readonly IVfsFile _inner;
    private readonly string _path;
    private readonly int _node;

    public FaultFile(FaultVfs vfs, IVfsFile inner, string path, int node)
    {
        _vfs = vfs;
        _inner = inner;
        _path = path;
        _node = node;
    }

    public override VfsCaps Caps => _inner.Caps;

    public override int SectorSize => _inner.SectorSize;

    public override DeviceCharacteristics Device => _inner.Device;

    public override IVfsShm? Shm => _inner.Shm;

    public override int Read(long offset, Span<byte> dst)
    {
        _vfs.Begin(FaultCall.Read, 0, out _);
        return _inner.Read(offset, dst);
    }

    public override ValueTask<int> ReadAsync(long offset, Memory<byte> dst, CancellationToken ct = default)
    {
        _vfs.Begin(FaultCall.Read, 0, out _);
        return _inner.ReadAsync(offset, dst, ct);
    }

    public override void Write(long offset, ReadOnlySpan<byte> src)
    {
        _vfs.Begin(FaultCall.Write, src.Length, out FaultRule? failure);
        if (failure != null)
        {
            int part = Math.Min(failure.PartialBytes, src.Length);
            _inner.Write(offset, src[..part]);
            _vfs.RecordWrite(_path, _node, offset, src[..part]);
            throw FaultVfs.Failure(failure);
        }

        _inner.Write(offset, src);
        _vfs.RecordWrite(_path, _node, offset, src);
    }

    public override async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> src, CancellationToken ct = default)
    {
        _vfs.Begin(FaultCall.Write, src.Length, out FaultRule? failure);
        if (failure != null)
        {
            int part = Math.Min(failure.PartialBytes, src.Length);
            await _inner.WriteAsync(offset, src[..part], ct).ConfigureAwait(false);
            _vfs.RecordWrite(_path, _node, offset, src.Span[..part]);
            throw FaultVfs.Failure(failure);
        }

        await _inner.WriteAsync(offset, src, ct).ConfigureAwait(false);
        _vfs.RecordWrite(_path, _node, offset, src.Span);
    }

    // A write run is one storage call, so it is one crash point; the torn-write model cuts it into sectors.
    public override void WriteRun(long offset, ReadOnlySpan<ReadOnlyMemory<byte>> pages) => Write(offset, Join(pages));

    public override ValueTask WriteRunAsync(long offset, ReadOnlyMemory<ReadOnlyMemory<byte>> pages, CancellationToken ct = default) => WriteAsync(offset, Join(pages.Span), ct);

    private static byte[] Join(ReadOnlySpan<ReadOnlyMemory<byte>> pages)
    {
        int total = 0;
        foreach (ReadOnlyMemory<byte> p in pages)
        {
            total += p.Length;
        }

        var all = new byte[total];
        int at = 0;
        foreach (ReadOnlyMemory<byte> p in pages)
        {
            p.CopyTo(all.AsMemory(at));
            at += p.Length;
        }

        return all;
    }

    public override void Sync(SyncMode mode)
    {
        _vfs.Begin(FaultCall.Sync, 0, out _);
        bool lost = _vfs.NextSyncLost();
        _inner.Sync(mode);
        _vfs.RecordSync(_path, _node, lost);
    }

    public override async ValueTask SyncAsync(SyncMode mode, CancellationToken ct = default)
    {
        _vfs.Begin(FaultCall.Sync, 0, out _);
        bool lost = _vfs.NextSyncLost();
        await _inner.SyncAsync(mode, ct).ConfigureAwait(false);
        _vfs.RecordSync(_path, _node, lost);
    }

    public override void SyncDirectory()
    {
        _vfs.Begin(FaultCall.SyncDirectory, 0, out _);
        _inner.SyncDirectory();
        _vfs.RecordSyncDirectory(_path);
    }

    public override async ValueTask SyncDirectoryAsync(CancellationToken ct = default)
    {
        _vfs.Begin(FaultCall.SyncDirectory, 0, out _);
        await _inner.SyncDirectoryAsync(ct).ConfigureAwait(false);
        _vfs.RecordSyncDirectory(_path);
    }

    public override void Truncate(long size)
    {
        _vfs.Begin(FaultCall.Truncate, 0, out _);
        _inner.Truncate(size);
        _vfs.RecordTruncate(_path, _node, size);
    }

    public override async ValueTask TruncateAsync(long size, CancellationToken ct = default)
    {
        _vfs.Begin(FaultCall.Truncate, 0, out _);
        await _inner.TruncateAsync(size, ct).ConfigureAwait(false);
        _vfs.RecordTruncate(_path, _node, size);
    }

    public override long FileSize()
    {
        _vfs.Begin(FaultCall.FileSize, 0, out _);
        return _inner.FileSize();
    }

    public override ValueTask<long> FileSizeAsync(CancellationToken ct = default)
    {
        _vfs.Begin(FaultCall.FileSize, 0, out _);
        return _inner.FileSizeAsync(ct);
    }

    public override bool Lock(LockLevel level)
    {
        _vfs.Begin(FaultCall.Lock, 0, out _);
        return _inner.Lock(level);
    }

    public override ValueTask<bool> LockAsync(LockLevel level, CancellationToken ct = default)
    {
        _vfs.Begin(FaultCall.Lock, 0, out _);
        return _inner.LockAsync(level, ct);
    }

    public override void Unlock(LockLevel level) => _inner.Unlock(level);

    public override ValueTask UnlockAsync(LockLevel level, CancellationToken ct = default) => _inner.UnlockAsync(level, ct);

    public override bool CheckReservedLock() => _inner.CheckReservedLock();

    public override ValueTask<bool> CheckReservedLockAsync(CancellationToken ct = default) => _inner.CheckReservedLockAsync(ct);

    public override void Dispose() => _inner.Dispose();
}
