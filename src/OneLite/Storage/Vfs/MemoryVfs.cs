namespace RecursiveAI.OneLite.Storage.Vfs;

/// <summary>
/// An in-process VFS. Files live in memory and last as long as the <see cref="MemoryVfs"/> object; connections in the same process share locks
/// and the wal-index like connections in different processes share them for the file backend. Both flavours are native: the asynchronous methods
/// complete before they return. Nothing is durable, so <see cref="IVfsFile.Sync"/> does nothing. Paths are compared as given (ordinal), after
/// <see cref="FullPathname"/>, which only normalises a leading <c>./</c>.
/// </summary>
public sealed class MemoryVfs : VfsBase
{
    private readonly Dictionary<string, MemoryFileData> _files = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _tempCounter;

    /// <summary>Creates an empty in-memory VFS.</summary>
    /// <param name="name">The VFS name; defaults to <c>memory</c>.</param>
    public MemoryVfs(string name = "memory")
    {
        Name = name;
    }

    /// <inheritdoc/>
    public override string Name { get; }

    /// <inheritdoc/>
    public override VfsCaps Caps => VfsCaps.Sync | VfsCaps.Async | VfsCaps.SharedMemory | VfsCaps.BlockingOk;

    /// <inheritdoc/>
    public override string FullPathname(string path) => path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path;

    /// <inheritdoc/>
    public override IVfsFile Open(string path, VfsOpenOptions flags)
    {
        bool temp = path.Length == 0;
        string key = temp ? "\0temp-" + Interlocked.Increment(ref _tempCounter) : FullPathname(path);
        lock (_gate)
        {
            if (!_files.TryGetValue(key, out MemoryFileData? data))
            {
                if ((flags & VfsOpenOptions.Create) == 0 && !temp)
                {
                    throw new VfsException(VfsCodes.CantOpen, $"unable to open database file: '{path}' does not exist");
                }

                data = new MemoryFileData(key);
                _files[key] = data;
            }
            else if ((flags & (VfsOpenOptions.Create | VfsOpenOptions.Exclusive)) == (VfsOpenOptions.Create | VfsOpenOptions.Exclusive))
            {
                throw new VfsException(VfsCodes.CantOpen, $"unable to open database file: '{path}' already exists");
            }

            return new MemoryFile(this, data, key, flags | (temp ? VfsOpenOptions.DeleteOnClose | VfsOpenOptions.ReadWrite | VfsOpenOptions.Create : 0));
        }
    }

    /// <inheritdoc/>
    public override bool Access(string path, AccessMode mode)
    {
        lock (_gate)
        {
            return _files.ContainsKey(FullPathname(path));
        }
    }

    /// <inheritdoc/>
    public override void Delete(string path, bool syncDirectory)
    {
        lock (_gate)
        {
            if (!_files.Remove(FullPathname(path)))
            {
                throw new VfsException(VfsCodes.IoErrDeleteNoEnt, $"file '{path}' does not exist");
            }
        }
    }

    /// <summary>Creates or replaces a file with the given content. For setting up tests and for restoring a saved image.</summary>
    public void Import(string path, ReadOnlySpan<byte> content)
    {
        lock (_gate)
        {
            string key = FullPathname(path);
            var data = new MemoryFileData(key);
            data.Write(0, content);
            _files[key] = data;
        }
    }

    /// <summary>Returns a copy of a file's content, or null if it does not exist.</summary>
    public byte[]? Export(string path)
    {
        lock (_gate)
        {
            return _files.TryGetValue(FullPathname(path), out MemoryFileData? d) ? d.ToArray() : null;
        }
    }

    /// <summary>The paths of all files, ordered.</summary>
    public string[] List()
    {
        lock (_gate)
        {
            var r = new List<string>();
            foreach (string k in _files.Keys)
            {
                if (k.Length == 0 || k[0] != '\0')
                {
                    r.Add(k);
                }
            }

            r.Sort(StringComparer.Ordinal);
            return [.. r];
        }
    }

    internal void Remove(MemoryFileData data)
    {
        lock (_gate)
        {
            if (_files.TryGetValue(data.Key, out MemoryFileData? cur) && ReferenceEquals(cur, data))
            {
                _files.Remove(data.Key);
            }
        }
    }

    internal MemoryFileData GetOrCreateShmFile(string dbKey)
    {
        string key = dbKey + "-shm";
        lock (_gate)
        {
            if (!_files.TryGetValue(key, out MemoryFileData? d))
            {
                d = new MemoryFileData(key);
                _files[key] = d;
            }

            return d;
        }
    }
}

internal sealed class MemoryFileData
{
    private byte[] _buf = [];

    // Bytes of _buf that hold file content; the file reads as zeros from here up to Length, so growing a file with SetLength costs nothing
    // (a hostile journal can claim a huge database size).
    private int _filled;

    public MemoryFileData(string key)
    {
        Key = key;
        Locks = new FileLockState(NoRangeLocks.Instance);
    }

    public string Key { get; }

    public object Gate { get; } = new();

    public long Length { get; private set; }

    public FileLockState Locks { get; }

    public MemoryShm? Shm { get; set; }

    public int Read(long offset, Span<byte> dst)
    {
        lock (Gate)
        {
            if (offset >= Length)
            {
                return 0;
            }

            int n = (int)Math.Min(dst.Length, Length - offset);
            int have = (int)Math.Max(0, Math.Min(n, _filled - offset));
            if (have > 0)
            {
                _buf.AsSpan((int)offset, have).CopyTo(dst);
            }

            dst.Slice(have, n - have).Clear();
            return n;
        }
    }

    public void Write(long offset, ReadOnlySpan<byte> src)
    {
        lock (Gate)
        {
            long end = checked(offset + src.Length);
            if (end > int.MaxValue - 64)
            {
                throw new VfsException(VfsCodes.Full, "the in-memory file is too large");
            }

            EnsureCapacity((int)end);
            if (offset > _filled)
            {
                _buf.AsSpan(_filled, (int)(offset - _filled)).Clear(); // a gap reads as zeros
            }

            src.CopyTo(_buf.AsSpan((int)offset));
            _filled = Math.Max(_filled, (int)end);
            if (end > Length)
            {
                Length = end;
            }
        }
    }

    public void SetLength(long size)
    {
        lock (Gate)
        {
            if (size > int.MaxValue - 64)
            {
                throw new VfsException(VfsCodes.Full, "the in-memory file is too large");
            }

            Length = size;
            _filled = (int)Math.Min(_filled, size);
        }
    }

    public byte[] ToArray()
    {
        lock (Gate)
        {
            var all = new byte[(int)Length];
            _buf.AsSpan(0, _filled).CopyTo(all);
            return all;
        }
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _buf.Length)
        {
            return;
        }

        int cap = Math.Max(needed, Math.Min(_buf.Length * 2, int.MaxValue - 64));
        Array.Resize(ref _buf, Math.Max(cap, 4096));
    }
}

/// <summary>Shared memory for the wal-index of one in-memory file.</summary>
internal sealed class MemoryShm
{
    private readonly List<byte[]> _regions = [];
    private readonly MemoryFileData _file;
    private int _users;

    public MemoryShm(MemoryFileData file)
    {
        _file = file;
        Locks = new ShmLockState(NoRangeLocks.Instance);
    }

    public ShmLockState Locks { get; }

    public void Attach()
    {
        lock (_regions)
        {
            _users++;
        }
    }

    /// <returns>True if this was the last user.</returns>
    public bool Detach()
    {
        lock (_regions)
        {
            return --_users <= 0;
        }
    }

    public ShmRegion Map(int region, bool extend)
    {
        lock (_regions)
        {
            if (region >= _regions.Count)
            {
                if (!extend)
                {
                    return ShmRegion.Empty;
                }

                while (_regions.Count <= region)
                {
                    _regions.Add(new byte[IVfsShm.RegionSize]);
                }

                _file.SetLength((long)_regions.Count * IVfsShm.RegionSize);
            }

            return new ShmRegion(_regions[region], null);
        }
    }

    public void Clear()
    {
        lock (_regions)
        {
            _regions.Clear();
        }
    }
}

internal sealed class MemoryFile : VfsFileBase, IVfsShm
{
    private readonly MemoryVfs _vfs;
    private readonly MemoryFileData _data;
    private readonly string _key;
    private readonly VfsOpenOptions _flags;
    private readonly LockHolder _holder = new();
    private readonly ShmHolder _shmHolder = new();
    private MemoryShm? _shm;
    private bool _disposed;

    public MemoryFile(MemoryVfs vfs, MemoryFileData data, string key, VfsOpenOptions flags)
    {
        _vfs = vfs;
        _data = data;
        _key = key;
        _flags = flags;
    }

    public override VfsCaps Caps => (_flags & VfsOpenOptions.ReadWrite) == 0 && (_flags & VfsOpenOptions.Create) == 0
        ? VfsCaps.Sync | VfsCaps.Async | VfsCaps.SharedMemory | VfsCaps.BlockingOk | VfsCaps.ReadOnly
        : VfsCaps.Sync | VfsCaps.Async | VfsCaps.SharedMemory | VfsCaps.BlockingOk;

    public override int SectorSize => 512;

    public override DeviceCharacteristics Device => DeviceCharacteristics.PowersafeOverwrite | DeviceCharacteristics.SafeAppend | DeviceCharacteristics.SequentialWrites;

    public override IVfsShm? Shm => (_flags & VfsOpenOptions.MainDb) != 0 ? this : null;

    public override int Read(long offset, Span<byte> dst) => _data.Read(offset, dst);

    public override void Write(long offset, ReadOnlySpan<byte> src)
    {
        if ((Caps & VfsCaps.ReadOnly) != 0)
        {
            throw new VfsException(VfsCodes.IoErrWrite, "attempt to write a readonly database");
        }

        _data.Write(offset, src);
    }

    public override void Sync(SyncMode mode)
    {
    }

    public override void SyncDirectory()
    {
    }

    public override void Truncate(long size) => _data.SetLength(size);

    public override long FileSize() => _data.Length;

    public override bool Lock(LockLevel level) => !(level > LockLevel.Shared && (Caps & VfsCaps.ReadOnly) != 0) && _data.Locks.TryLock(_holder, level);

    public override void Unlock(LockLevel level) => _data.Locks.Unlock(_holder, level);

    public override bool CheckReservedLock() => _data.Locks.CheckReserved();

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _data.Locks.Unlock(_holder, LockLevel.None);
        Unmap(false);
        if ((_flags & VfsOpenOptions.DeleteOnClose) != 0)
        {
            _vfs.Remove(_data);
        }
    }

    // IVfsShm
    private MemoryShm Attach()
    {
        if (_shm == null)
        {
            MemoryFileData shmFile = _vfs.GetOrCreateShmFile(_key);
            lock (shmFile.Gate)
            {
                shmFile.Shm ??= new MemoryShm(shmFile);
                _shm = shmFile.Shm;
            }

            _shm.Attach();
        }

        return _shm;
    }

    ShmRegion IVfsShm.Map(int region, bool extend) => Attach().Map(region, extend);

    ValueTask<ShmRegion> IVfsShm.MapAsync(int region, bool extend, CancellationToken ct) => Flavours.Inline(() => Attach().Map(region, extend), ct);

    bool IVfsShm.Lock(int offset, int count, ShmLockOptions flags) => Attach().Locks.TryLock(_shmHolder, offset, count, flags);

    ValueTask<bool> IVfsShm.LockAsync(int offset, int count, ShmLockOptions flags, CancellationToken ct) => Flavours.Inline(() => Attach().Locks.TryLock(_shmHolder, offset, count, flags), ct);

    void IVfsShm.Barrier() => ShmAccessor.Barrier();

    public void Unmap(bool deleteFile)
    {
        MemoryShm? shm = _shm;
        if (shm == null)
        {
            return;
        }

        _shm = null;
        shm.Locks.ReleaseAll(_shmHolder);
        if (shm.Detach() && deleteFile)
        {
            shm.Clear();
            _vfs.Remove(_vfs.GetOrCreateShmFile(_key));
        }
    }

    void IVfsShm.Unmap(bool deleteFile) => Unmap(deleteFile);
}
