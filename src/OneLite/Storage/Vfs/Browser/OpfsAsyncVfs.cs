using System.Runtime.Versioning;

namespace RecursiveAI.OneLite.Storage.Vfs.Browser;

/// <summary>
/// The Origin Private File System for code that may not block: the page's main thread, or any context without
/// <c>FileSystemSyncAccessHandle</c>. Reads use <c>FileSystemFileHandle.getFile()</c>; writes go through <c>createWritable()</c>. Locks are Web Locks.
/// </summary>
/// <remarks>
/// <para>Every call is asynchronous. A synchronous call (other than <c>Unlock</c> and <c>Dispose</c>, which only start a release) throws
/// <see cref="VfsException"/> with MISUSE, "backend requires the async driver" (docs/vfs.md). Use <c>Pager.OpenAsync</c> and the pager's
/// <c>...Async</c> methods.</para>
/// <para>Durability: see docs/browser.md. A write is stored only when its writable stream is closed, and <c>Sync</c> closes it. The File System
/// Standard does not say the close reaches stable storage; a killed tab keeps what a completed close stored.</para>
/// <para>There is no shared memory: WAL mode keeps its wal-index in the heap, which makes it a single-connection mode (one connection per origin).</para>
/// </remarks>
[SupportedOSPlatform("browser")]
public sealed class OpfsAsyncVfs : VfsBase
{
    private const string AsyncOnly = "backend requires the async driver";

    private readonly MemoryVfs _temp = new("opfs-temp");
    private readonly IWebLockProvider _locks = new WebLockProvider();
    private readonly List<Task> _closing = [];

    private OpfsAsyncVfs()
    {
    }

    /// <summary>Loads the browser storage module if needed and creates the file system.</summary>
    /// <param name="moduleUrl">Where <c>onelite-opfs.js</c> is; null for the package's default (see <see cref="BrowserStorage.LoadAsync"/>).</param>
    /// <returns>The file system.</returns>
    /// <exception cref="VfsException">CANTOPEN when this context has no Origin Private File System or no Web Locks.</exception>
    public static async Task<OpfsAsyncVfs> CreateAsync(string? moduleUrl = null)
    {
        await OpfsJs.EnsureLoadedAsync(moduleUrl).ConfigureAwait(false);
        string caps = OpfsJs.CapabilitiesText();
        if (!caps.Contains("opfs=true", StringComparison.Ordinal) || !caps.Contains("webLocks=true", StringComparison.Ordinal))
        {
            throw new VfsException(VfsCodes.CantOpen, "this context has no Origin Private File System or no Web Locks (" + caps + ")");
        }

        return new OpfsAsyncVfs();
    }

    /// <inheritdoc/>
    public override string Name => "opfs-async";

    /// <inheritdoc/>
    public override VfsCaps Caps => VfsCaps.Async;

    /// <summary>Strips a leading <c>/</c> or <c>./</c>: every path is relative to the origin's private root, and <c>/</c> separates directories.</summary>
    /// <param name="path">A database path.</param>
    /// <returns>The normalised path.</returns>
    public override string FullPathname(string path)
    {
        ReadOnlySpan<char> p = path;
        while (p.Length > 0 && (p[0] == '/' || p.StartsWith("./", StringComparison.Ordinal)))
        {
            p = p[0] == '/' ? p[1..] : p[2..];
        }

        return p.ToString();
    }

    /// <inheritdoc/>
    public override IVfsFile Open(string path, VfsOpenOptions flags) => throw new VfsException(VfsCodes.Misuse, AsyncOnly);

    /// <inheritdoc/>
    public override bool Access(string path, AccessMode mode) => throw new VfsException(VfsCodes.Misuse, AsyncOnly);

    /// <inheritdoc/>
    public override void Delete(string path, bool syncDirectory) => throw new VfsException(VfsCodes.Misuse, AsyncOnly);

    /// <inheritdoc/>
    public override async ValueTask<IVfsFile> OpenAsync(string path, VfsOpenOptions flags, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        const VfsOpenOptions memoryKinds = VfsOpenOptions.TempDb | VfsOpenOptions.TempJournal | VfsOpenOptions.SubJournal;
        if (path.Length == 0 || (flags & memoryKinds) != 0)
        {
            // tempfiles: these are never looked at by another connection and need no durability.
            return _temp.Open(path, flags);
        }

        await DrainAsync().ConfigureAwait(false);
        string full = FullPathname(path);
        bool create = (flags & VfsOpenOptions.Create) != 0;
        bool exclusive = create && (flags & VfsOpenOptions.Exclusive) != 0;
        int id;
        try
        {
            id = await OpfsJs.AOpen(full, create, exclusive).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not VfsException and not OperationCanceledException)
        {
            VfsException v = OpfsJs.Map(e, VfsCodes.CantOpen, VfsCodes.CantOpen);
            throw new VfsException(v.ExtendedCode, $"unable to open database file: '{path}': {v.Message}", e);
        }

        return new OpfsAsyncFile(this, id, full, flags, _locks);
    }

    /// <inheritdoc/>
    public override async ValueTask<bool> AccessAsync(string path, AccessMode mode, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await DrainAsync().ConfigureAwait(false);
        try
        {
            return await OpfsJs.Exists(FullPathname(path)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not VfsException and not OperationCanceledException)
        {
            throw OpfsJs.Map(e, VfsCodes.IoErrAccess, VfsCodes.IoErrAccess);
        }
    }

    /// <inheritdoc/>
    public override async ValueTask DeleteAsync(string path, bool syncDirectory, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await DrainAsync().ConfigureAwait(false);
        try
        {
            await OpfsJs.RemoveFile(FullPathname(path)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not VfsException and not OperationCanceledException)
        {
            throw OpfsJs.Map(e, VfsCodes.IoErrDelete, VfsCodes.IoErrDeleteNoEnt);
        }
    }

    /// <summary>Waits until every file closed by <c>Dispose</c> has finished closing. Dispose cannot await, so the close runs in the background; this keeps the next open or delete from racing it.</summary>
    /// <returns>A task that completes when no close is outstanding.</returns>
    public async ValueTask DrainAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_closing)
            {
                _closing.RemoveAll(t => t.IsCompleted);
                if (_closing.Count == 0)
                {
                    return;
                }

                pending = [.. _closing];
            }

            foreach (Task t in pending)
            {
                try
                {
                    await t.ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // A failed close has nothing left to do; the data was synced (or not) before.
                    _ = e;
                }
            }
        }
    }

    internal void TrackClose(Task t)
    {
        lock (_closing)
        {
            _closing.Add(t);
        }
    }

    private sealed class WebLockProvider : IWebLockProvider
    {
        public async ValueTask<int> TryAcquireAsync(string name, bool exclusive)
        {
            try
            {
                return await OpfsJs.LockTry(name, exclusive).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not VfsException and not OperationCanceledException)
            {
                throw OpfsJs.Map(e, VfsCodes.IoErrLock, VfsCodes.IoErrLock);
            }
        }

        public async ValueTask ReleaseAsync(int handle)
        {
            try
            {
                await OpfsJs.LockRelease(handle).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not VfsException and not OperationCanceledException)
            {
                throw OpfsJs.Map(e, VfsCodes.IoErrUnlock, VfsCodes.IoErrUnlock);
            }
        }

        public void ReleaseNow(int handle)
        {
            // The release is requested here, synchronously; the returned task only reports when the lock manager has finished with it.
            _ = OpfsJs.LockRelease(handle).ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
        }
    }

    private sealed class OpfsAsyncFile : VfsFileBase
    {
        private readonly OpfsAsyncVfs _vfs;
        private readonly int _id;
        private readonly string _path;
        private readonly VfsOpenOptions _flags;
        private readonly LevelLocks _levels;
        private bool _disposed;

        internal OpfsAsyncFile(OpfsAsyncVfs vfs, int id, string path, VfsOpenOptions flags, IWebLockProvider locks)
        {
            _vfs = vfs;
            _id = id;
            _path = path;
            _flags = flags;
            _levels = new LevelLocks(locks, "onelite:" + path);
        }

        public override VfsCaps Caps => (_flags & VfsOpenOptions.ReadOnly) != 0 ? VfsCaps.Async | VfsCaps.ReadOnly : VfsCaps.Async;

        // A conservative sector: the journal header is padded to it (atomiccommit), and a larger value only costs a few bytes.
        public override int SectorSize => 4096;

        public override DeviceCharacteristics Device => DeviceCharacteristics.None;

        public override int Read(long offset, Span<byte> dst) => throw new VfsException(VfsCodes.Misuse, AsyncOnly);

        public override void Write(long offset, ReadOnlySpan<byte> src) => throw new VfsException(VfsCodes.Misuse, AsyncOnly);

        public override void WriteRun(long offset, ReadOnlySpan<ReadOnlyMemory<byte>> pages) => throw new VfsException(VfsCodes.Misuse, AsyncOnly);

        public override void Sync(SyncMode mode) => throw new VfsException(VfsCodes.Misuse, AsyncOnly);

        public override void Truncate(long size) => throw new VfsException(VfsCodes.Misuse, AsyncOnly);

        public override long FileSize() => throw new VfsException(VfsCodes.Misuse, AsyncOnly);

        public override bool Lock(LockLevel level) => throw new VfsException(VfsCodes.Misuse, AsyncOnly);

        public override bool CheckReservedLock() => throw new VfsException(VfsCodes.Misuse, AsyncOnly);

        // OPFS has no directory entries to flush apart from the file itself.
        public override void SyncDirectory()
        {
        }

        public override ValueTask SyncDirectoryAsync(CancellationToken ct = default) => default;

        public override void Unlock(LockLevel level)
        {
            if (level == LockLevel.None)
            {
                _levels.ReleaseAllNow();
            }
            else
            {
                throw new VfsException(VfsCodes.Misuse, AsyncOnly);
            }
        }

        public override async ValueTask<int> ReadAsync(long offset, Memory<byte> dst, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (dst.Length == 0)
            {
                return 0;
            }

            try
            {
                int n = await OpfsJs.ARead(_id, offset, dst.Length).ConfigureAwait(false);
                if (n > 0)
                {
                    OpfsJs.ATake(_id, dst.Span[..n]);
                }

                return n;
            }
            catch (Exception e) when (e is not VfsException and not OperationCanceledException)
            {
                throw OpfsJs.Map(e, VfsCodes.IoErrRead, VfsCodes.IoErrRead);
            }
        }

        public override async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> src, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CheckWritable();
            await WriteBytes(offset, src.ToArray()).ConfigureAwait(false);
        }

        public override async ValueTask WriteRunAsync(long offset, ReadOnlyMemory<ReadOnlyMemory<byte>> pages, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CheckWritable();

            // One hop into JavaScript for the whole run.
            int total = 0;
            for (int i = 0; i < pages.Length; i++)
            {
                total = checked(total + pages.Span[i].Length);
            }

            byte[] all = new byte[total];
            int at = 0;
            for (int i = 0; i < pages.Length; i++)
            {
                pages.Span[i].Span.CopyTo(all.AsSpan(at));
                at += pages.Span[i].Length;
            }

            await WriteBytes(offset, all).ConfigureAwait(false);
        }

        public override async ValueTask SyncAsync(SyncMode mode, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await OpfsJs.AFlush(_id).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not VfsException and not OperationCanceledException)
            {
                throw OpfsJs.Map(e, VfsCodes.IoErrFsync, VfsCodes.IoErrFsync);
            }
        }

        public override async ValueTask TruncateAsync(long size, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CheckWritable();
            try
            {
                await OpfsJs.ATruncate(_id, size).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not VfsException and not OperationCanceledException)
            {
                throw OpfsJs.Map(e, VfsCodes.IoErrTruncate, VfsCodes.IoErrTruncate);
            }
        }

        public override async ValueTask<long> FileSizeAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await OpfsJs.ASize(_id).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not VfsException and not OperationCanceledException)
            {
                throw OpfsJs.Map(e, VfsCodes.IoErrFstat, VfsCodes.IoErrFstat);
            }
        }

        public override ValueTask<bool> LockAsync(LockLevel level, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if ((_flags & VfsOpenOptions.ReadOnly) != 0 && level > LockLevel.Shared)
            {
                return new ValueTask<bool>(false);
            }

            return _levels.LockAsync(level);
        }

        public override ValueTask UnlockAsync(LockLevel level, CancellationToken ct = default) => _levels.UnlockAsync(level);

        public override ValueTask<bool> CheckReservedLockAsync(CancellationToken ct = default) => _levels.CheckReservedAsync();

        public override void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _levels.ReleaseAllNow();
            Task close = OpfsJs.AClose(_id);
            _vfs.TrackClose(close);
            if ((_flags & VfsOpenOptions.DeleteOnClose) != 0)
            {
                string path = _path;
                _vfs.TrackClose(close.ContinueWith(_ => OpfsJs.RemoveFile(path), TaskScheduler.Default).Unwrap());
            }
        }

        private async ValueTask WriteBytes(long offset, byte[] data)
        {
            try
            {
                await OpfsJs.AWrite(_id, offset, new ArraySegment<byte>(data)).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not VfsException and not OperationCanceledException)
            {
                throw OpfsJs.Map(e, VfsCodes.IoErrWrite, VfsCodes.IoErrWrite);
            }
        }

        private void CheckWritable()
        {
            if ((_flags & VfsOpenOptions.ReadOnly) != 0)
            {
                throw new VfsException(VfsCodes.ReadOnly, "attempt to write a readonly database");
            }
        }
    }
}
