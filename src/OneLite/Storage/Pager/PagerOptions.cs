using RecursiveAI.OneLite.Storage.Crypto;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Storage.Pager;

/// <summary>Settings for a <see cref="Pager"/>.</summary>
public sealed class PagerOptions
{
    /// <summary>Page size for a new (empty) database; an existing database's own size wins. Power of two, 512 to 65536. Default 4096.</summary>
    public int PageSize { get; init; } = 4096;

    /// <summary>Reserved bytes per page for a new database (0 to 255). Default 0.</summary>
    public int ReservedBytes { get; init; }

    /// <summary>Text encoding of a new database. Default UTF-8.</summary>
    public TextEncoding Encoding { get; init; } = TextEncoding.Utf8;

    /// <summary>Create a new database with auto-vacuum pointer-map pages. Default false.</summary>
    public bool AutoVacuum { get; init; }

    /// <summary>With <see cref="AutoVacuum"/>: incremental rather than full. Default false.</summary>
    public bool IncrementalVacuum { get; init; }

    /// <summary>
    /// <c>PRAGMA cache_size</c>: a positive number is pages, a negative number is KiB of page memory. Default -2000 (about 2 MB).
    /// The limit is soft: pinned pages and (when a spill is impossible) dirty pages may exceed it.
    /// </summary>
    public int CacheSize { get; init; } = -2000;

    /// <summary>The rollback journal mode. Default <see cref="JournalMode.Delete"/>.</summary>
    public JournalMode JournalMode { get; init; } = JournalMode.Delete;

    /// <summary><c>PRAGMA synchronous</c>. Default <see cref="SynchronousMode.Full"/>.</summary>
    public SynchronousMode Synchronous { get; init; } = SynchronousMode.Full;

    /// <summary><c>PRAGMA fullfsync</c>: use the strongest sync the platform offers. Default false.</summary>
    public bool FullFsync { get; init; }

    /// <summary>Open the database read-only. Default false.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>
    /// The busy policy. Called with the number of retries so far when a lock is busy; returns the milliseconds to sleep before trying again,
    /// or a negative number to give up with <c>SQLITE_BUSY</c>. Default: give up at once.
    /// </summary>
    public Func<int, int>? Busy { get; init; }

    /// <summary>
    /// The longest a <c>BEGIN CONCURRENT</c> commit may hold the WAL write lock while it validates and replays, in milliseconds. Past it the commit aborts with
    /// <c>SQLITE_BUSY_TIMEOUT</c> before anything is written to the log. Zero or negative: no limit. Default 5000. The phases that write and sync the log are not cut short.
    /// </summary>
    public int ConcurrentCommitHoldLimitMs { get; init; } = 5000;

    /// <summary>How long, in microseconds, a <c>BEGIN CONCURRENT</c> commit retries a busy WAL write lock with only a yield between tries before it asks <see cref="Busy"/>; 0 or null: no spin.</summary>
    internal Func<int>? BusySpinBudgetUs { get; init; }

    /// <summary>Told how many milliseconds (rounded up) the spin used, so they count against the busy timeout.</summary>
    internal Action<int>? BusySpun { get; init; }

    /// <summary>Test hook: every n-th cache hit becomes a suspend point (<see cref="IoKind.Yield"/>). 0 (the default) turns it off.</summary>
    public int YieldEvery { get; init; }

    /// <summary>
    /// <c>PRAGMA wal_autocheckpoint</c>: after a commit in WAL mode, a passive checkpoint runs once the WAL holds at least this many frames. Zero or negative
    /// turns it off. Default 1000.
    /// </summary>
    public int WalAutoCheckpoint { get; init; } = 1000;

    /// <summary>
    /// <c>PRAGMA journal_size_limit</c> in bytes: after the WAL is reset, a file larger than this is cut back to it (never below the frames just written).
    /// Negative means no limit. Default -1.
    /// </summary>
    public long JournalSizeLimit { get; init; } = -1;

    /// <summary>
    /// The key of an encrypted (SQLCipher-compatible) database, or null for a plain one. The pager keeps its own copy, so the caller may dispose
    /// theirs at once. With a key set, <see cref="PageSize"/> and <see cref="ReservedBytes"/> are ignored: the page size comes from
    /// <see cref="Cipher"/> and the reserve from the cipher. A wrong key, or a file that is not encrypted, fails the first read with
    /// <c>SQLITE_NOTADB</c>, "file is not a database". Not supported in the browser build (<c>SQLITE_ERROR</c> at open).
    /// </summary>
    public CipherKey? Key { get; init; }

    /// <summary>
    /// A callback that supplies the key the first time the pager needs it (instead of <see cref="Key"/>). The pager owns the returned key and
    /// disposes it when it closes. Called at most once per pager, on the thread that drives the pager.
    /// </summary>
    public Func<CipherKey>? KeyProvider { get; init; }

    /// <summary>
    /// The cipher parameters (page size, KDF and HMAC digests, KDF iterations, plaintext header) of an encrypted database. They are not stored in the
    /// file, so they must match the ones the file was written with. Default <see cref="CipherSettings.V4"/>. Ignored without a key.
    /// </summary>
    public CipherSettings? Cipher { get; init; }

    /// <summary>
    /// The 16-byte salt of a database with a plaintext header (<c>cipher_plaintext_header_size</c>), which the file does not carry; the application
    /// keeps it (<see cref="Pager.CipherSalt"/> of the pager that created the file). Not needed otherwise, or when the key itself carries a salt.
    /// </summary>
    public byte[]? CipherSalt { get; init; }
}

/// <summary>A cached page. <see cref="Data"/> is the whole page image; edit it only after <see cref="Pager.MakeWritable"/>.</summary>
public sealed class PageFrame
{
    internal int Pins;
    internal bool Dirty;
    internal bool Referenced;

    // The page is dirty and its original is in the journal, but that part of the journal is not yet durable: writing the page to the database
    // file now would break atomiccommit §3.7.
    internal bool NeedSync;
    internal int SavedLevel;
    internal Pager? Owner;

    internal PageFrame(int pageSize)
    {
        Data = new byte[pageSize];
    }

    /// <summary>The page number (1-based).</summary>
    public uint PageNumber { get; internal set; }

    /// <summary>The page image, exactly one page long. Valid while the frame is pinned.</summary>
    public byte[] Data { get; }

    /// <summary>True if the page was modified in the open write transaction and not yet written to the database.</summary>
    public bool IsDirty => Dirty;
}
