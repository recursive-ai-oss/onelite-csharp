namespace RecursiveAI.OneLite.Storage.Vfs;

/// <summary>What a VFS or file can do. A backend declares the flavours it implements; the driver is checked against them when a database is opened.</summary>
[Flags]
public enum VfsCaps
{
    /// <summary>No capabilities.</summary>
    None = 0,
    /// <summary>The synchronous methods are implemented and do not require an asynchronous wait.</summary>
    Sync = 1,
    /// <summary>The asynchronous methods are implemented.</summary>
    Async = 2,
    /// <summary>The backend can map the database file into memory.</summary>
    Mmap = 4,
    /// <summary>The backend provides the shared memory of the WAL index (<see cref="IVfsFile.Shm"/> is not null).</summary>
    SharedMemory = 8,
    /// <summary>Locks are visible to other processes using the same file (needed to share a file with other SQLite-compatible processes).</summary>
    MultiProcessLocks = 16,
    /// <summary>The backend cannot be written.</summary>
    ReadOnly = 32,
    /// <summary>An async-only backend may be driven by the synchronous driver by blocking the calling thread (a normal thread, not a browser main thread).</summary>
    BlockingOk = 64,
}

/// <summary>The five lock levels of the database file (sqlite.org lockingv3 §2.0). Levels are ordered: a higher level includes the rights of a lower one.</summary>
public enum LockLevel : byte
{
    /// <summary>No lock.</summary>
    None = 0,
    /// <summary>Many holders may read; nobody may write.</summary>
    Shared = 1,
    /// <summary>One holder intends to write; readers may continue.</summary>
    Reserved = 2,
    /// <summary>The writer waits for readers to leave; no new readers are admitted.</summary>
    Pending = 3,
    /// <summary>Sole access for writing the database file.</summary>
    Exclusive = 4,
}

/// <summary>How hard <see cref="IVfsFile.Sync"/> must push data to the device (sqlite.org c3ref io_methods).</summary>
[Flags]
public enum SyncMode : byte
{
    /// <summary>An ordinary flush.</summary>
    Normal = 1,
    /// <summary>The strongest flush the platform has (on macOS, a full flush through the drive cache).</summary>
    Full = 2,
    /// <summary>Only the file content needs to reach the device, not its metadata, where the backend can tell the difference.</summary>
    DataOnly = 4,
}

/// <summary>Properties of the storage under a file that the pager may rely on (sqlite.org atomiccommit, c3ref io_methods).</summary>
[Flags]
public enum DeviceCharacteristics
{
    /// <summary>Nothing may be assumed beyond the documented sector model.</summary>
    None = 0,
    /// <summary>Appending data never damages bytes already in the file.</summary>
    SafeAppend = 1,
    /// <summary>Overwriting part of a sector never damages the rest of that sector if power fails.</summary>
    PowersafeOverwrite = 2,
    /// <summary>The file never changes while it is open.</summary>
    Immutable = 4,
    /// <summary>Writes reach the device in the order they were issued.</summary>
    SequentialWrites = 8,
}

/// <summary>How a file is opened and what it is for. The role flags (<see cref="MainDb"/> and below) tell the backend which locking and durability rules apply.</summary>
[Flags]
public enum VfsOpenOptions
{
    /// <summary>No flags.</summary>
    None = 0,
    /// <summary>Open for reading only.</summary>
    ReadOnly = 1,
    /// <summary>Open for reading and writing.</summary>
    ReadWrite = 2,
    /// <summary>Create the file if it does not exist (requires <see cref="ReadWrite"/>).</summary>
    Create = 4,
    /// <summary>With <see cref="Create"/>: fail if the file already exists.</summary>
    Exclusive = 8,
    /// <summary>Delete the file when it is closed.</summary>
    DeleteOnClose = 16,
    /// <summary>The main database file. The only role that takes locks and owns a shared-memory file.</summary>
    MainDb = 0x100,
    /// <summary>The rollback journal of a main database.</summary>
    MainJournal = 0x200,
    /// <summary>A temporary database (sorter or transient B-tree storage).</summary>
    TempDb = 0x400,
    /// <summary>A temporary journal.</summary>
    TempJournal = 0x800,
    /// <summary>A statement sub-journal.</summary>
    SubJournal = 0x1000,
    /// <summary>A super-journal.</summary>
    SuperJournal = 0x2000,
    /// <summary>A write-ahead log.</summary>
    Wal = 0x4000,
}

/// <summary>What <see cref="IVfs.Access"/> asks.</summary>
public enum AccessMode : byte
{
    /// <summary>Does the path exist.</summary>
    Exists,
    /// <summary>Does the path exist and can it be read and written.</summary>
    ReadWrite,
}

/// <summary>A request on the eight lock slots of the shared-memory (<c>-shm</c>) file (sqlite.org walformat).</summary>
[Flags]
public enum ShmLockOptions
{
    /// <summary>Release the slots.</summary>
    Unlock = 1,
    /// <summary>Acquire the slots.</summary>
    Lock = 2,
    /// <summary>A shared (read) hold.</summary>
    Shared = 4,
    /// <summary>An exclusive (write) hold.</summary>
    Exclusive = 8,
}

/// <summary>SQLite result codes the storage layer reports, primary and extended (sqlite.org rescode). Extended codes carry the primary code in their low byte.</summary>
public static class VfsCodes
{
    /// <summary>SQLITE_PERM.</summary>
    public const int Perm = 3;

    /// <summary>SQLITE_BUSY.</summary>
    public const int Busy = 5;

    /// <summary>SQLITE_READONLY.</summary>
    public const int ReadOnly = 8;

    /// <summary>SQLITE_IOERR.</summary>
    public const int IoErr = 10;

    /// <summary>SQLITE_FULL.</summary>
    public const int Full = 13;

    /// <summary>SQLITE_CANTOPEN.</summary>
    public const int CantOpen = 14;

    /// <summary>SQLITE_MISUSE.</summary>
    public const int Misuse = 21;

    /// <summary>SQLITE_IOERR_READ.</summary>
    public const int IoErrRead = 266;

    /// <summary>SQLITE_IOERR_SHORT_READ.</summary>
    public const int IoErrShortRead = 522;

    /// <summary>SQLITE_IOERR_WRITE.</summary>
    public const int IoErrWrite = 778;

    /// <summary>SQLITE_IOERR_FSYNC.</summary>
    public const int IoErrFsync = 1034;

    /// <summary>SQLITE_IOERR_DIR_FSYNC.</summary>
    public const int IoErrDirFsync = 1290;

    /// <summary>SQLITE_IOERR_TRUNCATE.</summary>
    public const int IoErrTruncate = 1546;

    /// <summary>SQLITE_IOERR_FSTAT.</summary>
    public const int IoErrFstat = 1802;

    /// <summary>SQLITE_IOERR_UNLOCK.</summary>
    public const int IoErrUnlock = 2058;

    /// <summary>SQLITE_IOERR_RDLOCK.</summary>
    public const int IoErrRdLock = 2314;

    /// <summary>SQLITE_IOERR_DELETE.</summary>
    public const int IoErrDelete = 2570;

    /// <summary>SQLITE_IOERR_ACCESS.</summary>
    public const int IoErrAccess = 3338;

    /// <summary>SQLITE_IOERR_CHECKRESERVEDLOCK.</summary>
    public const int IoErrCheckReservedLock = 3594;

    /// <summary>SQLITE_IOERR_LOCK.</summary>
    public const int IoErrLock = 3850;

    /// <summary>SQLITE_IOERR_CLOSE.</summary>
    public const int IoErrClose = 4106;

    /// <summary>SQLITE_IOERR_SHMOPEN.</summary>
    public const int IoErrShmOpen = 4618;

    /// <summary>SQLITE_IOERR_SHMSIZE.</summary>
    public const int IoErrShmSize = 4874;

    /// <summary>SQLITE_IOERR_SHMLOCK.</summary>
    public const int IoErrShmLock = 5130;

    /// <summary>SQLITE_IOERR_SHMMAP.</summary>
    public const int IoErrShmMap = 5386;

    /// <summary>SQLITE_IOERR_DELETE_NOENT.</summary>
    public const int IoErrDeleteNoEnt = 5898;

    /// <summary>SQLITE_CANTOPEN_ISDIR.</summary>
    public const int CantOpenIsDir = 526;

}

/// <summary>A storage call failed. Carries the SQLite extended result code the same failure reports through the C API.</summary>
public class VfsException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="extendedCode">The extended result code, for example <see cref="VfsCodes.IoErrWrite"/>.</param>
    /// <param name="message">What failed, in words; the pager adds the file name.</param>
    /// <param name="inner">The underlying exception, if any.</param>
    public VfsException(int extendedCode, string message, Exception? inner = null) : base(message, inner)
    {
        ExtendedCode = extendedCode;
    }

    /// <summary>The extended result code.</summary>
    public int ExtendedCode { get; }

    /// <summary>The primary result code (the low byte of <see cref="ExtendedCode"/>).</summary>
    public int Code => ExtendedCode & 0xFF;
}
