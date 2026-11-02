using System.Buffers;
using System.Runtime.Versioning;

namespace RecursiveAI.OneLite.Storage.Vfs.Browser;

/// <summary>
/// The Origin Private File System for a dedicated worker, over <c>FileSystemSyncAccessHandle</c>: reads, writes, truncation, size and flush are
/// plain synchronous calls, so the pager's synchronous driver works unchanged.
/// </summary>
/// <remarks>
/// <para><b>Opening is the one asynchronous step.</b> A sync access handle can only be created with an awaited call. So a database is
/// <i>prepared</i> first: <see cref="PrepareAsync"/> (or <c>OpenAsync</c>, which prepares) opens handles for the database, its <c>-journal</c> and its
/// <c>-wal</c> file. A synchronous <c>Open</c> of one of those paths then hands out a view of the prepared handle. A synchronous <c>Open</c> of a path
/// that was not prepared throws MISUSE.</para>
/// <para><b>Existence.</b> A prepared file is created on the platform even when it is logically absent, so a file exists exactly when it is not empty
/// (an empty database, journal or WAL is the same as none for SQLite). <c>Delete</c> of a journal or WAL truncates it and flushes.</para>
/// <para><b>One context per file.</b> The platform lets only one context hold a file's sync handle; a second <see cref="PrepareAsync"/> fails with BUSY
/// until the first has closed its last connection. Inside a context, connections to one database share the handles and are arbitrated by lock levels
/// kept in this object (a worker cannot wait on a Web Lock). Cross-context exclusion therefore covers the whole file, not each lock level;
/// per-level cross-context locks are what <see cref="OpfsAsyncVfs"/> offers.</para>
/// <para>There is no shared memory: WAL mode keeps its wal-index in the heap (one connection per origin). Temporary files live in memory.</para>
/// </remarks>
[SupportedOSPlatform("browser")]
public sealed class OpfsSyncVfs : VfsBase
{
    private readonly MemoryVfs _temp = new("opfs-sync-temp");
    private readonly Dictionary<string, Group> _groups = new(StringComparer.Ordinal);

    private OpfsSyncVfs()
    {
    }

    /// <summary>Loads the browser storage module if needed and creates the file system. Only a dedicated worker can use it.</summary>
    /// <param name="moduleUrl">Where <c>onelite-opfs.js</c> is; null for the package's default (see <see cref="BrowserStorage.LoadAsync"/>).</param>
    /// <returns>The file system.</returns>
    /// <exception cref="VfsException">CANTOPEN when this context cannot create sync access handles (it is not a dedicated worker, or has no OPFS).</exception>
    public static async Task<OpfsSyncVfs> CreateAsync(string? moduleUrl = null)
    {
        await OpfsJs.EnsureLoadedAsync(moduleUrl).ConfigureAwait(false);
        string caps = OpfsJs.CapabilitiesText();
        if (!caps.Contains("opfs=true", StringComparison.Ordinal) || !caps.Contains("syncAccessHandle=true", StringComparison.Ordinal) || !caps.Contains("worker=true", StringComparison.Ordinal))
        {
            throw new VfsException(VfsCodes.CantOpen, "this context cannot create sync access handles; run in a dedicated worker (" + caps + ")");
        }

        return new OpfsSyncVfs();
    }

    /// <inheritdoc/>
    public override string Name => "opfs-sync";

    /// <inheritdoc/>
    public override VfsCaps Caps => VfsCaps.Sync;

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

    /// <summary>
    /// Opens the sync access handles of a database and its journal and WAL, so that synchronous <c>Open</c> can follow. Preparing a database this
    /// object has already prepared is free.
    /// </summary>
    /// <param name="path">The database path.</param>
    /// <param name="ct">Cancellation, observed between the three opens.</param>
    /// <returns>A task that completes when the handles are open.</returns>
    /// <exception cref="VfsException">BUSY when another context holds one of the files; CANTOPEN or an I/O error otherwise.</exception>
    public async ValueTask PrepareAsync(string path, CancellationToken ct = default)
    {
        string db = FullPathname(path);
        if (_groups.ContainsKey(db))
        {
            return;
        }

        var opened = new List<int>(3);
        try
        {
            string[] names = [db, db + "-journal", db + "-wal"];
            foreach (string n in names)
            {
                ct.ThrowIfCancellationRequested();
                opened.Add(await OpfsJs.SOpen(n, true).ConfigureAwait(false));
            }
        }
        catch (Exception e)
        {
            foreach (int id in opened)
            {
                OpfsJs.SClose(id);
            }

            if (e is OperationCanceledException)
            {
                throw;
            }

            VfsException v = OpfsJs.Map(e, VfsCodes.CantOpen, VfsCodes.CantOpen);
            throw new VfsException(v.ExtendedCode, $"unable to open database file: '{path}': {v.Message}", e);
        }

        _groups[db] = new Group(db, opened[0], opened[1], opened[2]);
    }

    /// <inheritdoc/>
    public override async ValueTask<IVfsFile> OpenAsync(string path, VfsOpenOptions flags, CancellationToken ct = default)
    {
        if ((flags & VfsOpenOptions.MainDb) == 0)
        {
            return Open(path, flags);
        }

        await PrepareAsync(path, ct).ConfigureAwait(false);
        try
        {
            return Open(path, flags);
        }
        catch (VfsException)
        {
            // The open was refused (no such file, exclusive create of an existing one): do not keep the handles that were opened for it.
            if (_groups.TryGetValue(FullPathname(path), out Group? g) && g.Refs <= 0)
            {
                g.CloseHandles();
                _groups.Remove(g.Path);
            }

            throw;
        }
    }

    /// <inheritdoc/>
    public override IVfsFile Open(string path, VfsOpenOptions flags)
    {
        const VfsOpenOptions memoryKinds = VfsOpenOptions.TempDb | VfsOpenOptions.TempJournal | VfsOpenOptions.SubJournal;
        if (path.Length == 0 || (flags & memoryKinds) != 0)
        {
            return _temp.Open(path, flags);
        }

        string full = FullPathname(path);
        (Group g, SyncData d) = Find(full) ?? throw new VfsException(VfsCodes.Misuse, $"'{path}' was not prepared; await PrepareAsync first (a sync access handle can only be opened asynchronously)");
        bool create = (flags & VfsOpenOptions.Create) != 0;
        // A database with a connection open exists even before its first byte is written, as a file created by an open would.
        bool present = d.Size() > 0 || (ReferenceEquals(d, g.Db) && g.Refs > 0);
        if (present)
        {
            if (create && (flags & VfsOpenOptions.Exclusive) != 0)
            {
                throw new VfsException(VfsCodes.CantOpen, $"unable to open database file: '{path}' already exists");
            }
        }
        else if (!create)
        {
            throw new VfsException(VfsCodes.CantOpen, $"unable to open database file: '{path}' does not exist");
        }

        if ((flags & VfsOpenOptions.MainDb) != 0)
        {
            g.Refs++;
        }

        return new OpfsSyncFile(this, g, d, flags);
    }

    /// <inheritdoc/>
    public override bool Access(string path, AccessMode mode)
    {
        (Group g, SyncData d)? f = Find(FullPathname(path));
        if (f == null)
        {
            // Without a prepared handle the platform can only answer asynchronously.
            throw new VfsException(VfsCodes.Misuse, $"'{path}' was not prepared; use AccessAsync");
        }

        return f.Value.d.Size() > 0;
    }

    /// <inheritdoc/>
    public override async ValueTask<bool> AccessAsync(string path, AccessMode mode, CancellationToken ct = default)
    {
        string full = FullPathname(path);
        if (Find(full) is { } f)
        {
            return f.Item2.Size() > 0;
        }

        try
        {
            return await OpfsJs.ExistsNonEmpty(full).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not VfsException and not OperationCanceledException)
        {
            throw OpfsJs.Map(e, VfsCodes.IoErrAccess, VfsCodes.IoErrAccess);
        }
    }

    /// <inheritdoc/>
    public override void Delete(string path, bool syncDirectory)
    {
        if (Find(FullPathname(path)) is not { } f)
        {
            throw new VfsException(VfsCodes.Misuse, $"'{path}' was not prepared; use DeleteAsync");
        }

        if (f.Item2.Size() == 0)
        {
            throw new VfsException(VfsCodes.IoErrDeleteNoEnt, $"file '{path}' does not exist");
        }

        f.Item2.Truncate(0);
        f.Item2.Flush();
    }

    /// <inheritdoc/>
    public override async ValueTask DeleteAsync(string path, bool syncDirectory, CancellationToken ct = default)
    {
        string full = FullPathname(path);
        if (Find(full) != null)
        {
            Delete(path, syncDirectory);
            return;
        }

        try
        {
            await OpfsJs.RemoveFile(full).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not VfsException and not OperationCanceledException)
        {
            throw OpfsJs.Map(e, VfsCodes.IoErrDelete, VfsCodes.IoErrDeleteNoEnt);
        }
    }

    /// <summary>Closes every handle this object holds. Connections still open lose their files; close them first.</summary>
    public void Dispose()
    {
        foreach (Group g in _groups.Values)
        {
            g.CloseHandles();
        }

        _groups.Clear();
    }

    private (Group, SyncData)? Find(string full)
    {
        if (_groups.TryGetValue(full, out Group? g))
        {
            return (g, g.Db);
        }

        if (full.EndsWith("-journal", StringComparison.Ordinal) && _groups.TryGetValue(full[..^"-journal".Length], out g))
        {
            return (g, g.Journal);
        }

        if (full.EndsWith("-wal", StringComparison.Ordinal) && _groups.TryGetValue(full[..^"-wal".Length], out g))
        {
            return (g, g.Wal);
        }

        return null;
    }

    private void Release(Group g)
    {
        if (--g.Refs <= 0)
        {
            g.CloseHandles();
            _groups.Remove(g.Path);
        }
    }

    /// <summary>The handle of one file.</summary>
    private sealed class SyncData(int id)
    {
        internal int Id { get; } = id;

        internal long Size() => OpfsJs.SSize(Id);

        internal void Truncate(long size) => OpfsJs.STruncate(Id, size);

        internal void Flush() => OpfsJs.SFlush(Id);
    }

    /// <summary>A database with its journal and WAL, the lock table of its connections, and a count of connections.</summary>
    private sealed class Group
    {
        internal Group(string path, int db, int journal, int wal)
        {
            Path = path;
            Db = new SyncData(db);
            Journal = new SyncData(journal);
            Wal = new SyncData(wal);
            Locks = new LocalLockTable();
        }

        internal string Path { get; }

        internal SyncData Db { get; }

        internal SyncData Journal { get; }

        internal SyncData Wal { get; }

        internal LocalLockTable Locks { get; }

        internal int Refs { get; set; }

        internal void CloseHandles()
        {
            foreach (SyncData d in (ReadOnlySpan<SyncData>)[Db, Journal, Wal])
            {
                try
                {
                    OpfsJs.SClose(d.Id);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // Closing is best effort; the platform releases the handle with the worker.
                    _ = e;
                }
            }
        }
    }

    private sealed class OpfsSyncFile : VfsFileBase
    {
        private readonly OpfsSyncVfs _vfs;
        private readonly Group _group;
        private readonly SyncData _data;
        private readonly VfsOpenOptions _flags;
        private readonly LevelLocks _levels;
        private bool _disposed;

        internal OpfsSyncFile(OpfsSyncVfs vfs, Group group, SyncData data, VfsOpenOptions flags)
        {
            _vfs = vfs;
            _group = group;
            _data = data;
            _flags = flags;
            _levels = new LevelLocks(group.Locks, group.Path);
        }

        public override VfsCaps Caps => (_flags & VfsOpenOptions.ReadOnly) != 0 ? VfsCaps.Sync | VfsCaps.ReadOnly : VfsCaps.Sync;

        public override int SectorSize => 4096;

        public override DeviceCharacteristics Device => DeviceCharacteristics.None;

        public override int Read(long offset, Span<byte> dst)
        {
            if (dst.Length == 0)
            {
                return 0;
            }

            try
            {
                return OpfsJs.SRead(_data.Id, offset, dst);
            }
            catch (Exception e) when (e is not VfsException)
            {
                throw OpfsJs.Map(e, VfsCodes.IoErrRead, VfsCodes.IoErrRead);
            }
        }

        public override void Write(long offset, ReadOnlySpan<byte> src)
        {
            CheckWritable();
            if (src.Length == 0)
            {
                return;
            }

            // The runtime hands a Span to JavaScript, not a ReadOnlySpan: go through a pooled copy.
            byte[] buf = ArrayPool<byte>.Shared.Rent(src.Length);
            try
            {
                src.CopyTo(buf);
                OpfsJs.SWrite(_data.Id, offset, buf.AsSpan(0, src.Length));
            }
            catch (Exception e) when (e is not VfsException)
            {
                throw OpfsJs.Map(e, VfsCodes.IoErrWrite, VfsCodes.IoErrWrite);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        public override void Sync(SyncMode mode)
        {
            try
            {
                _data.Flush();
            }
            catch (Exception e) when (e is not VfsException)
            {
                throw OpfsJs.Map(e, VfsCodes.IoErrFsync, VfsCodes.IoErrFsync);
            }
        }

        // The platform has no directory to flush.
        public override void SyncDirectory()
        {
        }

        public override void Truncate(long size)
        {
            CheckWritable();
            try
            {
                _data.Truncate(size);
            }
            catch (Exception e) when (e is not VfsException)
            {
                throw OpfsJs.Map(e, VfsCodes.IoErrTruncate, VfsCodes.IoErrTruncate);
            }
        }

        public override long FileSize()
        {
            try
            {
                return _data.Size();
            }
            catch (Exception e) when (e is not VfsException)
            {
                throw OpfsJs.Map(e, VfsCodes.IoErrFstat, VfsCodes.IoErrFstat);
            }
        }

        public override bool Lock(LockLevel level)
        {
            if ((_flags & VfsOpenOptions.ReadOnly) != 0 && level > LockLevel.Shared)
            {
                return false;
            }

            return Done(_levels.LockAsync(level));
        }

        public override void Unlock(LockLevel level) => Done(_levels.UnlockAsync(level));

        public override bool CheckReservedLock() => Done(_levels.CheckReservedAsync());

        public override void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _levels.ReleaseAllNow();
            if ((_flags & VfsOpenOptions.MainDb) != 0)
            {
                _vfs.Release(_group);
            }
        }

        // The local lock table completes at once, so these tasks are always finished when they come back.
        private static T Done<T>(ValueTask<T> t) => t.IsCompleted ? t.GetAwaiter().GetResult() : throw new VfsException(VfsCodes.IoErrLock, "a local lock did not complete at once");

        private static void Done(ValueTask t)
        {
            if (!t.IsCompleted)
            {
                throw new VfsException(VfsCodes.IoErrLock, "a local lock did not complete at once");
            }

            t.GetAwaiter().GetResult();
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
