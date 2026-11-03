using Microsoft.Win32.SafeHandles;

namespace RecursiveAI.OneLite.Storage.Vfs;

/// <summary>An open file of <see cref="FileVfs"/>. Reads and writes go straight to <see cref="RandomAccess"/> (pread/pwrite) with no per-call allocation on the synchronous path.</summary>
internal sealed class FileVfsFile : VfsFileBase
{
    private readonly string _path;
    private readonly FileEntry _entry;
    private readonly VfsOpenOptions _flags;
    private readonly LockHolder _holder = new();
    private FileShm? _shm;
    private bool _disposed;

    public FileVfsFile(string path, FileEntry entry, VfsOpenOptions flags)
    {
        _path = path;
        _entry = entry;
        _flags = flags;
    }

    private bool IsMain => (_flags & VfsOpenOptions.MainDb) != 0;

    private bool ReadOnlyFile => (_flags & (VfsOpenOptions.ReadWrite | VfsOpenOptions.Create)) == 0;

    private SafeFileHandle Handle => _entry.Handle;

    /// <summary>The OS identity of the open file (<see cref="NativeFile.FileIdentity"/>), or null.</summary>
    internal string? OsIdentity => NativeFile.FileIdentity(Handle);

    public override VfsCaps Caps => FileVfs.FileCaps | (ReadOnlyFile ? VfsCaps.ReadOnly : 0);

    // unix VFS defaults of the pinned release: 4096-byte sectors, powersafe overwrite on.
    public override int SectorSize => 4096;

    public override DeviceCharacteristics Device => DeviceCharacteristics.PowersafeOverwrite;

    public override IVfsShm? Shm => IsMain ? (_shm ??= new FileShm(_path)) : null;

    public override int Read(long offset, Span<byte> dst)
    {
        try
        {
            int total = 0;
            while (total < dst.Length)
            {
                int n = RandomAccess.Read(Handle, dst[total..], offset + total);
                if (n == 0)
                {
                    break;
                }

                total += n;
            }

            return total;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            throw new VfsException(VfsCodes.IoErrRead, $"read of '{_path}' failed: {e.Message}", e);
        }
    }

    public override async ValueTask<int> ReadAsync(long offset, Memory<byte> dst, CancellationToken ct = default)
    {
        try
        {
            if (!Handle.IsAsync)
            {
                return Read(offset, dst.Span); // handles opened with open(2) are synchronous; a local read has nothing to wait on
            }

            int total = 0;
            while (total < dst.Length)
            {
                int n = await RandomAccess.ReadAsync(Handle, dst[total..], offset + total, ct).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                total += n;
            }

            return total;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            throw new VfsException(VfsCodes.IoErrRead, $"read of '{_path}' failed: {e.Message}", e);
        }
    }

    public override void Write(long offset, ReadOnlySpan<byte> src)
    {
        CheckWritable();
        try
        {
            RandomAccess.Write(Handle, src, offset);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            throw WriteError(e);
        }
    }

    public override async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> src, CancellationToken ct = default)
    {
        CheckWritable();
        try
        {
            if (!Handle.IsAsync)
            {
                RandomAccess.Write(Handle, src.Span, offset);
                return;
            }

            await RandomAccess.WriteAsync(Handle, src, offset, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            throw WriteError(e);
        }
    }

    private void CheckWritable()
    {
        if (ReadOnlyFile)
        {
            throw new VfsException(VfsCodes.ReadOnly, $"attempt to write a readonly file '{_path}'");
        }
    }

    private VfsException WriteError(Exception e)
    {
        // ENOSPC is 28 on Unix and the runtime keeps the errno in HResult; Windows has ERROR_DISK_FULL (0x70) and ERROR_HANDLE_DISK_FULL (0x27).
        int low = e.HResult & 0xFFFF;
        bool full = OperatingSystem.IsWindows() ? low is 0x70 or 0x27 : e.HResult == 28;
        return new VfsException(full ? VfsCodes.Full : VfsCodes.IoErrWrite, $"write to '{_path}' failed: {e.Message}", e);
    }

    public override void Sync(SyncMode mode)
    {
        try
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                NativeFile.UnixFlush((int)Handle.DangerousGetHandle(), mode);
            }
            else
            {
                RandomAccess.FlushToDisk(Handle);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            throw new VfsException(VfsCodes.IoErrFsync, $"sync of '{_path}' failed: {e.Message}", e);
        }
    }

    public override void SyncDirectory()
    {
        // atomiccommit §3: after creating a journal the directory entry must be durable too.
        NativeFile.SyncDirectory(Path.GetDirectoryName(_path));
    }

    public override void Truncate(long size)
    {
        CheckWritable();
        try
        {
            RandomAccess.SetLength(Handle, size);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            throw new VfsException(VfsCodes.IoErrTruncate, $"truncate of '{_path}' failed: {e.Message}", e);
        }
    }

    public override long FileSize()
    {
        try
        {
            return RandomAccess.GetLength(Handle);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            throw new VfsException(VfsCodes.IoErrFstat, $"size of '{_path}' unavailable: {e.Message}", e);
        }
    }

    public override bool Lock(LockLevel level)
    {
        if (!IsMain)
        {
            return true; // journals, WAL and temporary files take no locks (tempfiles)
        }

        if (level > LockLevel.Shared && ReadOnlyFile)
        {
            return false;
        }

        return _entry.Locks.TryLock(_holder, level);
    }

    public override void Unlock(LockLevel level)
    {
        if (IsMain)
        {
            _entry.Locks.Unlock(_holder, level);
        }
    }

    public override bool CheckReservedLock() => IsMain && _entry.Locks.CheckReserved();

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shm?.Unmap(false);
        if (IsMain)
        {
            _entry.Locks.Unlock(_holder, LockLevel.None);
        }

        if (_entry.Shared)
        {
            FileVfs.Release(_entry);
        }
        else
        {
            _entry.CloseAll();
        }
    }
}

/// <summary>
/// The wal-index of a database file: a <c>-shm</c> file with 32 KiB mapped regions and eight lock slots at bytes 120-127, used by this process and
/// by any other process that has the database open (walformat). The first user in a process opens and maps; the last one closes.
/// </summary>
internal sealed class FileShm : IVfsShm
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ShmFileEntry> Entries = new(StringComparer.Ordinal);

    private readonly string _shmPath;
    private readonly ShmHolder _holder = new();
    private ShmFileEntry? _entry;

    public FileShm(string dbPath)
    {
        _shmPath = dbPath + "-shm";
    }

    private ShmFileEntry Attach()
    {
        if (_entry != null)
        {
            return _entry;
        }

        lock (Gate)
        {
            if (!Entries.TryGetValue(_shmPath, out ShmFileEntry? e))
            {
                e = new ShmFileEntry(_shmPath);
                Entries[_shmPath] = e;
            }

            e.Users++;
            _entry = e;
            return e;
        }
    }

    public ShmRegion Map(int region, bool extend)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(region);
        return Attach().Map(region, extend);
    }

    public ValueTask<ShmRegion> MapAsync(int region, bool extend, CancellationToken ct = default) => Flavours.Inline(() => Map(region, extend), ct);

    public bool Lock(int offset, int count, ShmLockOptions flags) => Attach().Locks.TryLock(_holder, offset, count, flags);

    public ValueTask<bool> LockAsync(int offset, int count, ShmLockOptions flags, CancellationToken ct = default) => Flavours.Inline(() => Lock(offset, count, flags), ct);

    public void Barrier() => ShmAccessor.Barrier();

    public void Unmap(bool deleteFile)
    {
        ShmFileEntry? e = _entry;
        if (e == null)
        {
            return;
        }

        _entry = null;
        e.Locks.ReleaseAll(_holder);
        lock (Gate)
        {
            if (--e.Users > 0)
            {
                return;
            }

            Entries.Remove(_shmPath);
            e.Dispose();
            if (deleteFile)
            {
                try
                {
                    File.Delete(_shmPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new VfsException(VfsCodes.IoErrDelete, $"cannot delete '{_shmPath}': {ex.Message}", ex);
                }
            }
        }
    }
}

/// <summary>The process-wide state of one open <c>-shm</c> file.</summary>
internal sealed class ShmFileEntry : IDisposable
{
    // The unix VFS's "dead man switch" byte, just after the eight slots. A process holds it shared for as long as it has the file open, so the
    // first process to get it exclusively knows nobody else is using the file and that its content is stale.
    private const long DeadManSwitchByte = ShmLockState.FirstSlotByte + IVfsShm.LockSlots;

    private readonly FileStream _file;
    private readonly Dictionary<int, ShmRegion> _regions = [];

    public ShmFileEntry(string path)
    {
        SafeFileHandle h;
        try
        {
            SafeFileHandle? native = OperatingSystem.IsWindows() ? null : NativeFile.OpenForLocking(path, true, true, false, out _);
            h = native ?? File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, FileOptions.None);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new VfsException(VfsCodes.IoErrShmOpen, $"cannot open shared memory '{path}': {e.Message}", e);
        }

        // The stream is only the owner of the descriptor the mapped views are made from; the locks are taken on the same descriptor.
        _file = new FileStream(h, FileAccess.ReadWrite, 1, isAsync: false);
        IRangeLocks os = FileEntry.RangeLocksFor(h, true);
        Locks = new ShmLockState(os);
        try
        {
            if (os.Try(DeadManSwitchByte, 1, RangeLockKind.Write))
            {
                // Nobody else has the file open, so whatever wal-index it holds is stale: start it empty (the first reader rebuilds it from the WAL).
                RandomAccess.SetLength(h, 3);
            }

            // Another process opening the file at the same moment holds the byte exclusively while it clears the file (lockingv3-style
            // transient), so a refusal here means "try again shortly", not failure; give it up to about a second.
            bool shared = os.Try(DeadManSwitchByte, 1, RangeLockKind.Read);
            for (int attempt = 0; !shared && attempt < 1000; attempt++)
            {
                Thread.Sleep(1);
                shared = os.Try(DeadManSwitchByte, 1, RangeLockKind.Read);
            }

            if (!shared)
            {
                throw new VfsException(VfsCodes.IoErrShmOpen, $"cannot take the in-use lock on shared memory '{path}'");
            }
        }
        catch
        {
            _file.Dispose();
            throw;
        }
    }

    public int Users { get; set; }

    public ShmLockState Locks { get; }

    public ShmRegion Map(int region, bool extend)
    {
        lock (_regions)
        {
            if (_regions.TryGetValue(region, out ShmRegion? r))
            {
                return r;
            }

            if (!extend && RandomAccess.GetLength(_file.SafeFileHandle) < ((long)region + 1) * IVfsShm.RegionSize)
            {
                return ShmRegion.Empty;
            }

            try
            {
                r = ShmAccessor.MapFileRegion(_file, region);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                throw new VfsException(VfsCodes.IoErrShmMap, $"cannot map shared memory region {region}: {e.Message}", e);
            }

            _regions[region] = r;
            return r;
        }
    }

    public void Dispose()
    {
        lock (_regions)
        {
            foreach (ShmRegion r in _regions.Values)
            {
                r.Release();
            }

            _regions.Clear();
        }

        _file.Dispose();
    }
}
