namespace RecursiveAI.OneLite.Storage.Vfs;

/// <summary>
/// The storage layer's lowest level: names files, opens them and deletes them. Every call has a synchronous and an asynchronous flavour; a
/// backend declares which it implements in <see cref="Caps"/> (see <see cref="VfsBase"/> for the fallback rules). Paths are those of the backend.
/// </summary>
public interface IVfs
{
    /// <summary>The VFS name.</summary>
    string Name { get; }

    /// <summary>The flavours and features this backend has.</summary>
    VfsCaps Caps { get; }

    /// <summary>Opens or creates a file. An empty path asks for a new uniquely named temporary file.</summary>
    /// <exception cref="VfsException">CANTOPEN (or an extended form) if the file cannot be opened or created.</exception>
    IVfsFile Open(string path, VfsOpenOptions flags);

    /// <summary>Asynchronous form of <see cref="Open"/>.</summary>
    ValueTask<IVfsFile> OpenAsync(string path, VfsOpenOptions flags, CancellationToken ct = default);

    /// <summary>Tests a path. Never opens the file, so it cannot disturb locks held on it.</summary>
    bool Access(string path, AccessMode mode);

    /// <summary>Asynchronous form of <see cref="Access"/>.</summary>
    ValueTask<bool> AccessAsync(string path, AccessMode mode, CancellationToken ct = default);

    /// <summary>Deletes a file. If <paramref name="syncDirectory"/> is true the directory entry removal is made durable before returning.</summary>
    /// <exception cref="VfsException">IOERR_DELETE_NOENT if the file does not exist; IOERR_DELETE for other failures.</exception>
    void Delete(string path, bool syncDirectory);

    /// <summary>Asynchronous form of <see cref="Delete"/>.</summary>
    ValueTask DeleteAsync(string path, bool syncDirectory, CancellationToken ct = default);

    /// <summary>Turns a path into the absolute form the backend uses as the file's identity. Pure; never suspends.</summary>
    string FullPathname(string path);

    /// <summary>A millisecond clock for the busy handler and timestamps. It never decides file content.</summary>
    int CurrentTimeMs { get; }

    /// <summary>Waits. The synchronous driver blocks the thread.</summary>
    void Sleep(int ms);

    /// <summary>Asynchronous form of <see cref="Sleep"/>.</summary>
    ValueTask SleepAsync(int ms, CancellationToken ct = default);
}

/// <summary>
/// An open file. A call that fails throws <see cref="VfsException"/> carrying the extended result code. Locks are try-locks: they return
/// at once and the caller runs the busy loop. Implementations are safe to use from one thread at a time per file object.
/// </summary>
public interface IVfsFile : IDisposable
{
    /// <summary>The flavours this file has.</summary>
    VfsCaps Caps { get; }

    /// <summary>The device's atomic write unit in bytes (sqlite.org atomiccommit).</summary>
    int SectorSize { get; }

    /// <summary>What the pager may assume about the storage.</summary>
    DeviceCharacteristics Device { get; }

    /// <summary>Reads up to <c>dst.Length</c> bytes at <paramref name="offset"/>. Returns the bytes read; fewer than asked means end of file, and the caller zero-fills the tail.</summary>
    int Read(long offset, Span<byte> dst);

    /// <summary>Asynchronous form of <see cref="Read"/>.</summary>
    ValueTask<int> ReadAsync(long offset, Memory<byte> dst, CancellationToken ct = default);

    /// <summary>Writes all of <paramref name="src"/> at <paramref name="offset"/>, extending the file (a gap reads as zeros) or throws.</summary>
    /// <exception cref="VfsException">IOERR_WRITE or FULL.</exception>
    void Write(long offset, ReadOnlySpan<byte> src);

    /// <summary>Asynchronous form of <see cref="Write"/>.</summary>
    ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> src, CancellationToken ct = default);

    /// <summary>Writes the buffers back to back starting at <paramref name="offset"/>. The same as one <see cref="Write"/> per buffer unless the backend can gather.</summary>
    void WriteRun(long offset, ReadOnlySpan<ReadOnlyMemory<byte>> pages);

    /// <summary>Asynchronous form of <see cref="WriteRun"/>.</summary>
    ValueTask WriteRunAsync(long offset, ReadOnlyMemory<ReadOnlyMemory<byte>> pages, CancellationToken ct = default);

    /// <summary>Makes everything written so far durable (sqlite.org atomiccommit §3). Backends that cannot tell data-only from full flushes treat both as the stronger one.</summary>
    /// <exception cref="VfsException">IOERR_FSYNC.</exception>
    void Sync(SyncMode mode);

    /// <summary>Asynchronous form of <see cref="Sync"/>.</summary>
    ValueTask SyncAsync(SyncMode mode, CancellationToken ct = default);

    /// <summary>Makes the directory entry of this file durable, as SQLite does after creating a journal. A backend that cannot may do nothing; each backend documents which.</summary>
    void SyncDirectory();

    /// <summary>Asynchronous form of <see cref="SyncDirectory"/>.</summary>
    ValueTask SyncDirectoryAsync(CancellationToken ct = default);

    /// <summary>Sets the file length. Growing reads back as zeros.</summary>
    /// <exception cref="VfsException">IOERR_TRUNCATE.</exception>
    void Truncate(long size);

    /// <summary>Asynchronous form of <see cref="Truncate"/>.</summary>
    ValueTask TruncateAsync(long size, CancellationToken ct = default);

    /// <summary>The file length in bytes.</summary>
    long FileSize();

    /// <summary>Asynchronous form of <see cref="FileSize"/>.</summary>
    ValueTask<long> FileSizeAsync(CancellationToken ct = default);

    /// <summary>
    /// Tries to raise this file's lock to <paramref name="level"/> following sqlite.org lockingv3. Returns false for BUSY, leaving the level the
    /// attempt reached: a failed move to <see cref="LockLevel.Exclusive"/> may leave the holder at <see cref="LockLevel.Pending"/>, which keeps new readers out.
    /// </summary>
    bool Lock(LockLevel level);

    /// <summary>Asynchronous form of <see cref="Lock"/>.</summary>
    ValueTask<bool> LockAsync(LockLevel level, CancellationToken ct = default);

    /// <summary>Lowers the lock to <paramref name="level"/> (<see cref="LockLevel.Shared"/> or <see cref="LockLevel.None"/>). Never reports BUSY.</summary>
    void Unlock(LockLevel level);

    /// <summary>Asynchronous form of <see cref="Unlock"/>.</summary>
    ValueTask UnlockAsync(LockLevel level, CancellationToken ct = default);

    /// <summary>Is any connection, here or in another process, holding RESERVED or higher. Used to decide whether a hot journal is live.</summary>
    bool CheckReservedLock();

    /// <summary>Asynchronous form of <see cref="CheckReservedLock"/>.</summary>
    ValueTask<bool> CheckReservedLockAsync(CancellationToken ct = default);

    /// <summary>The shared memory of the WAL index, or null if this file's backend has none (then WAL is single-process).</summary>
    IVfsShm? Shm { get; }
}

/// <summary>The wal-index's shared memory: 32 KiB regions and eight lock slots (sqlite.org walformat). Locks are try-locks.</summary>
public interface IVfsShm
{
    /// <summary>The size of one region, 32768 bytes.</summary>
    public const int RegionSize = 32768;

    /// <summary>The number of lock slots, 8: write, checkpoint, recover and five read marks.</summary>
    public const int LockSlots = 8;

    /// <summary>Returns region <paramref name="region"/>. If it does not exist and <paramref name="extend"/> is false, returns <see cref="ShmRegion.Empty"/>.</summary>
    /// <exception cref="VfsException">IOERR_SHMMAP or IOERR_SHMSIZE.</exception>
    ShmRegion Map(int region, bool extend);

    /// <summary>Asynchronous form of <see cref="Map"/>.</summary>
    ValueTask<ShmRegion> MapAsync(int region, bool extend, CancellationToken ct = default);

    /// <summary>Takes or releases <paramref name="count"/> slots starting at slot <paramref name="offset"/> all at once. Returns false for BUSY, holding none of them.</summary>
    bool Lock(int offset, int count, ShmLockOptions flags);

    /// <summary>Asynchronous form of <see cref="Lock"/>.</summary>
    ValueTask<bool> LockAsync(int offset, int count, ShmLockOptions flags, CancellationToken ct = default);

    /// <summary>A full memory barrier: orders this connection's shared-memory reads and writes against other processes.</summary>
    void Barrier();

    /// <summary>Releases this connection's use of the shared memory. If it was the last user in this process and <paramref name="deleteFile"/> is true the shared-memory file is removed.</summary>
    void Unmap(bool deleteFile);
}
