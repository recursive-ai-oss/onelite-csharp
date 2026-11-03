using System.Linq;
using RecursiveAI.OneLite.Storage;
using System.Buffers.Binary;
using RecursiveAI.OneLite.Planning;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Sql.Parsing;
using SqlStatement = RecursiveAI.OneLite.Sql.Statement;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Crypto;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Storage.Vfs;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite;

/// <summary>Settings for opening a database.</summary>
public sealed record DatabaseOptions
{
    /// <summary>The storage backend. Default: the local file system.</summary>
    public IVfs? Vfs { get; init; }

    /// <summary>
    /// The busy policy: called with the number of retries so far when another process holds a conflicting lock; returns the milliseconds to
    /// wait before trying again, or a negative number to fail with SQLITE_BUSY. Default: fail at once.
    /// </summary>
    public Func<int, int>? Busy { get; init; }

    /// <summary>
    /// The page cache size as in <c>PRAGMA cache_size</c>: positive for pages, negative for KiB. Default -2000. A sort also takes its memory budget
    /// from it: up to eight times this (16 MiB by default; at least 256 KiB, at most the larger of this and 32 MiB) before it spills runs to a
    /// temporary file.
    /// </summary>
    public int CacheSize { get; init; } = -2000;

    /// <summary>Test hook: every n-th page-cache hit becomes a suspend point (I/O kind Yield), so every resume path runs. 0 turns it off.</summary>
    public int YieldEvery { get; init; }

    /// <summary>
    /// The key of an encrypted (SQLCipher-compatible) database, or null for a plain file. A wrong key, or a file that is not encrypted, fails the
    /// first read with SQLITE_NOTADB, "file is not a database". The database keeps its own copy of the key.
    /// </summary>
    public CipherKey? Key { get; init; }

    /// <summary>The cipher parameters the file was written with (page size, digests, iterations); default <see cref="CipherSettings.V4"/>. Used only with a <see cref="Key"/>.</summary>
    public CipherSettings? Cipher { get; init; }

    /// <summary>The page size of a database file created by this connection (a power of two from 512 to 65536). Default 4096. Ignored for an existing file.</summary>
    public int PageSize { get; init; } = 4096;

    /// <summary>The text encoding of a database file created by this connection. Default UTF-8. Ignored for an existing file.</summary>
    public TextEncoding Encoding { get; init; } = TextEncoding.Utf8;

    /// <summary>Create the file with auto-vacuum pointer-map pages (full mode, or incremental with <see cref="IncrementalVacuum"/>). Ignored for an existing file.</summary>
    public bool AutoVacuum { get; init; }

    /// <summary>With <see cref="AutoVacuum"/>: incremental rather than full.</summary>
    public bool IncrementalVacuum { get; init; }

    /// <summary>The journal mode (<c>PRAGMA journal_mode</c>). Default delete. <see cref="JournalMode.Wal"/> switches the database file to WAL mode when a connection opens; a database already in WAL mode stays in it.</summary>
    public JournalMode JournalMode { get; init; } = JournalMode.Delete;

    /// <summary><c>PRAGMA synchronous</c>. Default full.</summary>
    public SynchronousMode Synchronous { get; init; } = SynchronousMode.Full;

    /// <summary><c>PRAGMA foreign_keys</c> for new connections. Default off, as in SQLite.</summary>
    public bool ForeignKeys { get; init; }

    /// <summary>How long to keep retrying a lock another connection holds, in milliseconds (<c>PRAGMA busy_timeout</c>). Default 0: give up at once unless <see cref="Busy"/> says otherwise.</summary>
    public int BusyTimeoutMs { get; init; }

    /// <summary>
    /// Accept <c>BEGIN CONCURRENT</c> (row-level concurrent writers, docs/concurrency.md). Default off: with it off the statement is a syntax error, as in SQLite.
    /// The database must be in WAL mode when the transaction begins.
    /// </summary>
    public bool ConcurrentWrites { get; init; }

    /// <summary>
    /// The longest a <c>BEGIN CONCURRENT</c> commit may hold the WAL write lock while it validates and replays, in milliseconds (docs/concurrency.md).
    /// Past it the commit fails with <c>SQLITE_BUSY_TIMEOUT</c> before anything is written to the log. Zero or negative: no limit. Default 5000.
    /// </summary>
    public int ConcurrentCommitHoldLimitMs { get; init; } = 5000;

    /// <summary>
    /// Let <c>BEGIN CONCURRENT</c> commits made through connections of this process share one WAL sync when <c>synchronous</c> is FULL or EXTRA
    /// (docs/concurrency.md, group commit). A commit is still acknowledged only after a sync that covers its frames, and readers see it only after that sync.
    /// Default true; it has no effect at lower <c>synchronous</c> levels, for plain transactions, or for connections in other processes.
    /// </summary>
    public bool ConcurrentGroupCommit { get; init; } = true;

    /// <summary>Pages after which a commit in WAL mode checkpoints (<c>PRAGMA wal_autocheckpoint</c>). Default 1000.</summary>
    public int WalAutoCheckpoint { get; init; } = 1000;
}

/// <summary>
/// A database file opened for reading. Create connections with <see cref="Connect"/>.
/// This build opens databases read-only, as <c>SQLITE_OPEN_READONLY</c> does: the database file and an existing journal or log are never written, and an existing
/// file is never deleted. Like the reference, a read-only connection to a WAL-mode database takes part in the wal-index locking, which may create an empty
/// <c>-wal</c> and the <c>-shm</c> file next to the database (docs/engine.md, "Opening and locking"). To read a database without touching anything, use the forensic reader.
/// </summary>
public sealed partial class Database : IDisposable, IAsyncDisposable
{
    private readonly string _path;
    private readonly DatabaseOptions _options;
    private readonly IVfs _vfs;
    private bool _readOnly = true;

    /// <summary>True for a private in-memory database (inmemorydb): each connection gets a memory VFS of its own, unless the URI asked for a shared cache.</summary>
    private readonly bool _memory;

    private readonly bool _memoryShared;

    private Database(string path, DatabaseOptions options)
    {
        _path = path;
        _options = options;
        _vfs = options.Vfs ?? FileVfs.Instance;
        if (options.Vfs is null && IsMemoryName(path, out _memoryShared))
        {
            // sqlite.org/inmemorydb.html: ":memory:", "file::memory:" and "file:name?mode=memory" are in-memory; an empty name is a private temporary
            // database (here also held in memory). Nothing is created on disk for any of them.
            _memory = true;
            _path = "memory.db";
            _vfs = new MemoryVfs();
        }
    }

    private static bool IsMemoryName(string path, out bool shared)
    {
        shared = false;
        if (path.Length == 0 || path == ":memory:")
        {
            return true;
        }

        if (!path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string rest = path[5..];
        int q = rest.IndexOf('?', StringComparison.Ordinal);
        string query = q < 0 ? string.Empty : rest[(q + 1)..];
        string name = q < 0 ? rest : rest[..q];
        bool mem = name == ":memory:" || name.StartsWith("//", StringComparison.Ordinal) && name.EndsWith("/:memory:", StringComparison.Ordinal);
        foreach (string kv in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            mem |= kv == "mode=memory";
            shared |= kv == "cache=shared";
        }

        return mem;
    }

    /// <summary>Opens a database file for reading only (the equivalent of SQLITE_OPEN_READONLY).</summary>
    /// <param name="path">The database file.</param>
    /// <param name="options">Settings; null means the defaults.</param>
    /// <returns>The database.</returns>
    /// <exception cref="OneLiteException">SQLITE_CANTOPEN if the file does not exist.</exception>
    public static Database OpenReadOnly(string path, DatabaseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        options ??= new DatabaseOptions();
        var db = new Database(path, options);
        if (!db._memory && !db._vfs.Access(path, AccessMode.Exists))
        {
            throw new OneLiteException(ResultCode.CantOpen, "unable to open database file");
        }

        return db;
    }

    /// <summary>
    /// Asynchronous twin of <see cref="OpenReadOnly"/>: awaits the backend for the existence check and opens the connection the happy-path methods use
    /// (<c>QueryAsync</c>, <c>ScalarAsync</c>, <c>Table&lt;T&gt;</c>, <c>SearchAsync</c>), so it works on a backend that has only asynchronous calls.
    /// </summary>
    /// <param name="path">The database file.</param>
    /// <param name="options">Settings; null means the defaults.</param>
    /// <param name="ct">Observed while the check waits for I/O.</param>
    /// <returns>The database.</returns>
    /// <exception cref="OneLiteException">SQLITE_CANTOPEN if the file does not exist or the backend cannot open it.</exception>
    public static async ValueTask<Database> OpenReadOnlyAsync(string path, DatabaseOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        var db = new Database(path, options ?? new DatabaseOptions());
        if (!await db.ExistsAsync(ct).ConfigureAwait(false))
        {
            throw new OneLiteException(ResultCode.CantOpen, "unable to open database file");
        }

        try
        {
            // The happy-path methods run on this connection; opening it here lets them work over a backend that has only asynchronous calls.
            db._primary = await db.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            db.Dispose();
            throw;
        }

        return db;
    }

    /// <summary>True if the database file exists (always true for an in-memory database).</summary>
    private async ValueTask<bool> ExistsAsync(CancellationToken ct)
        => _memory || await _vfs.AccessAsync(_path, AccessMode.Exists, ct).ConfigureAwait(false);

    /// <summary>Opens a database file for reading and writing, creating it when it does not exist (SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE).</summary>
    /// <param name="path">The database file.</param>
    /// <param name="options">Settings; null means the defaults.</param>
    /// <returns>The database.</returns>
    public static Database Open(string path, DatabaseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        options ??= new DatabaseOptions();
        return new Database(path, options) { _readOnly = false };
    }

    /// <summary>Opens a connection. Each connection has its own page cache and locks; use one connection from one thread at a time.</summary>
    /// <returns>The connection.</returns>
    /// <exception cref="OneLiteException">SQLITE_CANTOPEN (or an extended code) if the file cannot be opened.</exception>
    public Connection Connect()
    {
        Connection conn = NewConnection();
        if (_options.JournalMode == JournalMode.Wal && !_readOnly && !_memory)
        {
            // WAL is a property of the file, not of the connection: switch the database over, as PRAGMA journal_mode=WAL does.
            using Statement st = conn.Prepare("PRAGMA journal_mode=WAL");
            while (st.Step())
            {
            }
        }

        return conn;
    }

    /// <summary>
    /// Opens a connection like <see cref="Connect"/>, awaiting the VFS for the I/O of switching a new database over to WAL.
    /// </summary>
    /// <param name="ct">Observed while the connection waits for I/O.</param>
    /// <returns>The connection.</returns>
    /// <exception cref="OneLiteException">SQLITE_CANTOPEN (or an extended code) if the file cannot be opened.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled while the connection waited for I/O.</exception>
    public async ValueTask<Connection> ConnectAsync(CancellationToken ct = default)
    {
        Connection conn = await NewConnectionAsync(ct).ConfigureAwait(false);
        if (_options.JournalMode == JournalMode.Wal && !_readOnly && !_memory)
        {
            using Statement st = conn.Prepare("PRAGMA journal_mode=WAL");
            while (await st.StepAsync(ct).ConfigureAwait(false))
            {
            }
        }

        return conn;
    }

    private Connection NewConnection()
    {
        (PagerOptions po, IVfs vfs, BusyPolicy busy) = ConnectionParts();
        return Wrap(StorageEngine.Open(vfs, _path, po), vfs, busy);
    }

    private async ValueTask<Connection> NewConnectionAsync(CancellationToken ct)
    {
        (PagerOptions po, IVfs vfs, BusyPolicy busy) = ConnectionParts();
        return Wrap(await StorageEngine.OpenAsync(vfs, _path, po, ct).ConfigureAwait(false), vfs, busy);
    }

    private (PagerOptions Options, IVfs Vfs, BusyPolicy Busy) ConnectionParts()
    {
        var busy = new BusyPolicy(_options.Busy, _options.BusyTimeoutMs);
        var po = new PagerOptions
        {
            ReadOnly = _readOnly && !_memory,
            PageSize = _options.PageSize,
            Encoding = _options.Encoding,
            AutoVacuum = _options.AutoVacuum,
            IncrementalVacuum = _options.IncrementalVacuum,
            JournalMode = _options.JournalMode == JournalMode.Wal ? JournalMode.Delete : _options.JournalMode,
            Synchronous = _options.Synchronous,
            WalAutoCheckpoint = _options.WalAutoCheckpoint,
            ConcurrentCommitHoldLimitMs = _options.ConcurrentCommitHoldLimitMs,
            GroupCommit = _options.ConcurrentGroupCommit,
            CacheSize = _options.CacheSize,
            Busy = busy.Next,
            BusySpinBudgetUs = busy.SpinBudgetUs,
            BusySpun = busy.Spun,
            YieldEvery = _options.YieldEvery,
            Key = _options.Key,
            Cipher = _options.Cipher,
        };
        // A private in-memory database belongs to its connection; only a shared-cache URI shares one between connections.
        IVfs vfs = _memory && !_memoryShared ? new MemoryVfs() : _vfs;
        return (po, vfs, busy);
    }

    private Connection Wrap(StorageEngine engine, IVfs vfs, BusyPolicy busy)
    {
        return new Connection(engine)
        {
            ForeignKeysEnabled = _options.ForeignKeys,
            ConcurrentEnabled = _options.ConcurrentWrites,
            OpenedReadOnly = _readOnly,
            Busy = busy,
            SortVfs = vfs,

            SortMemoryLimit = SortBudget(_options.CacheSize),
        };
    }

    /// <summary>
    /// How much a sort keeps in memory before it spills runs to a temporary file: eight times the page cache, so the default 2 MiB cache
    /// sorts about 16 MiB in memory; never less than 256 KiB, and no more than the cache itself or 32 MiB, whichever is larger.
    /// </summary>
    private static long SortBudget(int cacheSize)
    {
        long cache = cacheSize < 0 ? -(long)cacheSize * 1024 : (long)cacheSize * 4096;
        return Math.Max(256L * 1024, Math.Min(cache * 8, Math.Max(cache, 32L * 1024 * 1024)));
    }

    /// <summary>Closes the connection the happy-path methods use, if there is one. Connections from <see cref="Connect"/> belong to the caller.</summary>
    public void Dispose() => DisposePrimary();

    /// <summary>Closes the happy-path connection, rolling back an open transaction.</summary>
    /// <returns>A task that completes when the connection is closed.</returns>
    public ValueTask DisposeAsync() => DisposePrimaryAsync();
}

/// <summary>
/// One connection to a database: a pager, the cached schema and the statements prepared on it. Not thread-safe.
/// A connection reads the file only while a statement is running (or while it loads the schema), under the SHARED lock.
/// </summary>
public sealed partial class Connection : IDisposable, IAsyncDisposable
{
    private readonly StorageEngine _engine;
    private readonly Pager _pager;
    private PagerPageSource? _source;
    private BtContext? _ctx;
    private Catalog? _catalog;
    private int _readers;
    private int _rs;
    private uint _newCookie;
    private SchemaLoader? _loader;
    private bool _disposed;

    private IVfs? _sortVfs;

    /// <summary>
    /// The VFS the sorter opens its temporary files through. Always null for an encrypted database: sort runs hold whole rows, and a temporary
    /// file is not encrypted, so an encrypted database sorts in memory whatever the memory limit (docs/encryption-review.md).
    /// </summary>
    internal IVfs? SortVfs
    {
        get => _pager.IsEncrypted ? null : _sortVfs;
        set => _sortVfs = value;
    }

    /// <summary>Bytes of sort data kept in memory before a run is written out.</summary>
    /// <summary>The VFS that files named in SQL (ATTACH, VACUUM INTO) are opened through.</summary>
    internal IVfs AuxiliaryVfs => _sortVfs ?? FileVfs.Instance;

    internal long SortMemoryLimit { get; set; } = 8L * 1024 * 1024;
    private readonly List<Statement> _statements = [];

    // The connection owns one storage engine (pager, page source, B-tree context). The SQL layer drives its transactions step by step so that
    // a cache miss can suspend; a write path begins write transactions on the same engine.
    internal Connection(StorageEngine engine)
    {
        _engine = engine;
        engine.Changed = (root, row) => BlobChanged(0, root, row);
        _pager = engine.Pager;
        _source = engine.Source;
    }

    internal Pager Pager => _pager;

    internal PagerPageSource? Source => _source;

    internal BtContext? Context => _ctx;

    internal Catalog? Catalog => _catalog;

    internal Exception? PagerError => _pager.Error;

    /// <summary>Compiles the first SQL statement of <paramref name="sql"/>.</summary>
    /// <param name="sql">SQL text. Only the first statement is compiled; the rest is <see cref="Statement.Tail"/>.</param>
    /// <returns>The prepared statement.</returns>
    /// <exception cref="OneLiteException">SQLITE_ERROR with SQLite's message for syntax errors, unknown tables, columns and functions, and for anything this build does not run ("unsupported: ..."); SQLITE_NOTADB, SQLITE_CORRUPT, SQLITE_BUSY or I/O codes if the schema cannot be read.</exception>
    public Statement Prepare(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var st = new Statement(this, sql);
        _statements.Add(st);
        return st;
    }

    /// <summary>
    /// Compiles the first SQL statement of <paramref name="sql"/> like <see cref="Prepare(string)"/>, awaiting the VFS for the I/O of loading the schema, so it works on
    /// a backend that has only asynchronous calls. When the schema is already loaded no I/O happens and the task is complete.
    /// </summary>
    /// <param name="sql">SQL text. Only the first statement is compiled; the rest is <see cref="Statement.Tail"/>.</param>
    /// <param name="ct">Observed while the schema load waits for I/O; cancelling ends its read transaction and throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>The prepared statement.</returns>
    /// <exception cref="OneLiteException">As <see cref="Prepare(string)"/>.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled while the schema load waited for I/O.</exception>
    public ValueTask<Statement> PrepareAsync(string sql, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_catalog is not null)
        {
            return new ValueTask<Statement>(Prepare(sql));
        }

        return PrepareSlowAsync(sql, ct);
    }

    private async ValueTask<Statement> PrepareSlowAsync(string sql, CancellationToken ct)
    {
        // Compile with synchronous schema loads forbidden: a statement that needs the schema throws CatalogNotLoadedException before it does anything
        // else, the schema is then loaded by awaiting the VFS, and the compile runs again against the loaded catalog.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            _syncSchemaLoadForbidden = true;
            try
            {
                return Prepare(sql);
            }
            catch (CatalogNotLoadedException)
            {
            }
            finally
            {
                _syncSchemaLoadForbidden = false;
            }

            await EnsureCatalogAsync(ct).ConfigureAwait(false);
        }

        return Prepare(sql);
    }

    private bool _syncSchemaLoadForbidden;
    private bool _deferAbortRead;
    private bool _endReadOwed;

    /// <summary>Thrown inside the compile of <see cref="PrepareAsync(string, CancellationToken)"/> when the schema must be loaded first; never escapes.</summary>
    private sealed class CatalogNotLoadedException : Exception
    {
    }

    /// <summary>Loads the schema if it is not loaded, awaiting the VFS.</summary>
    internal async ValueTask EnsureCatalogAsync(CancellationToken ct)
    {
        if (_catalog != null)
        {
            return;
        }

        for (; ; )
        {
            RunResult r = ReloadSchemaStep();
            if (r == RunResult.Done)
            {
                return;
            }

            try
            {
                await _pager.ExecuteAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Like sqlite3_interrupt: drop the request and end the read transaction the load began.
                _pager.Abandon();
                if (_rs > 0)
                {
                    ReaderDone();
                }

                _rs = 0;
                _loader = null;

                // The read transaction may be half begun (the lock taken, page 1 not yet read): end it either way.
                if (_readers == 0)
                {
                    await WithAsyncAbort(AbortRead).ConfigureAwait(false);
                }

                throw;
            }
        }
    }

    /// <summary>Ends a read transaction that an abort left owed, awaiting the VFS (see <see cref="AbortRead"/>).</summary>
    internal async ValueTask FlushAbortReadAsync()
    {
        if (!_endReadOwed)
        {
            return;
        }

        _endReadOwed = false;
        try
        {
            await _pager.EndReadAsync().ConfigureAwait(false);
        }
        catch (OneLiteException)
        {
            // As in AbortRead: a lock that cannot be released goes with the file handle.
        }
    }

    /// <summary>Runs <paramref name="work"/> with read-transaction aborts deferred, then ends the read transaction by awaiting the VFS.</summary>
    internal async ValueTask WithAsyncAbort(Action work)
    {
        _deferAbortRead = true;
        try
        {
            work();
        }
        finally
        {
            _deferAbortRead = false;
        }

        await FlushAbortReadAsync().ConfigureAwait(false);
    }

    /// <summary>Compiles the first SQL statement of <paramref name="sql"/>, optionally with a counter in each table loop so <see cref="Statement.Plan"/> reports actual row counts.</summary>
    /// <param name="sql">SQL text; only the first statement is compiled.</param>
    /// <param name="collectActualRows">True to count the rows each loop yields while the statement runs. The counting costs one instruction per loop start and per row; statements prepared without it carry none.</param>
    /// <returns>The prepared statement.</returns>
    /// <exception cref="OneLiteException">As <see cref="Prepare(string)"/>.</exception>
    public Statement Prepare(string sql, bool collectActualRows)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var st = new Statement(this, sql, collectActualRows ? PlanMode.Count : PlanMode.Off);
        _statements.Add(st);
        return st;
    }

    /// <summary>
    /// Returns the plan of the first statement of <paramref name="sql"/>. With <paramref name="analyze"/> the statement is also run (with no values bound, so parameters are NULL)
    /// and the plan carries actual row and loop counts. A statement that changes the database runs inside a savepoint that is rolled back, so analyzing leaves no trace;
    /// a statement that cannot run in a savepoint (VACUUM, PRAGMA) is not run and its plan has no counts.
    /// </summary>
    /// <param name="sql">SQL text.</param>
    /// <param name="analyze">True to run the statement and count rows.</param>
    /// <returns>The plan, or null when the statement has none (schema changes, pragmas, transaction control).</returns>
    /// <exception cref="OneLiteException">The statement does not compile, or fails when analyzed.</exception>
    public QueryPlan? Explain(string sql, bool analyze = false)
    {
        using Statement st = Prepare(sql, analyze);
        if (analyze && st.Plan is not null)
        {
            if (st.IsReadOnly)
            {
                while (st.Step())
                {
                }
            }
            else
            {
                RunInSavepoint(st);
            }
        }

        return st.Plan;
    }

    /// <summary>The asynchronous twin of <see cref="Explain(string, bool)"/>: the same result, with statements stepped by <see cref="Statement.StepAsync"/>.</summary>
    /// <param name="sql">SQL text.</param>
    /// <param name="analyze">True to run the statement and count rows.</param>
    /// <param name="ct">Cancels the run at its next suspension point.</param>
    /// <returns>The plan, or null when the statement has none.</returns>
    /// <exception cref="OneLiteException">The statement does not compile, or fails when analyzed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async ValueTask<QueryPlan?> ExplainAsync(string sql, bool analyze = false, CancellationToken ct = default)
    {
        using Statement st = Prepare(sql, analyze);
        if (analyze && st.Plan is not null)
        {
            if (st.IsReadOnly)
            {
                while (await st.StepAsync(ct).ConfigureAwait(false))
                {
                }
            }
            else
            {
                await RunInSavepointAsync(st, ct).ConfigureAwait(false);
            }
        }

        return st.Plan;
    }

    private void RunInSavepoint(Statement st)
    {
        if (!AnalyzeCanUseSavepoint(st))
        {
            return;
        }

        RunNoRows("SAVEPOINT onelite_explain");
        try
        {
            while (st.Step())
            {
            }
        }
        finally
        {
            RunNoRows("ROLLBACK TO onelite_explain");
            RunNoRows("RELEASE onelite_explain");
        }
    }

    private async ValueTask RunInSavepointAsync(Statement st, CancellationToken ct)
    {
        if (!AnalyzeCanUseSavepoint(st))
        {
            return;
        }

        RunNoRows("SAVEPOINT onelite_explain");
        try
        {
            while (await st.StepAsync(ct).ConfigureAwait(false))
            {
            }
        }
        finally
        {
            RunNoRows("ROLLBACK TO onelite_explain");
            RunNoRows("RELEASE onelite_explain");
        }
    }

    // Only INSERT, UPDATE and DELETE carry plans among the statements that write; they all run fine inside a savepoint.
    private static bool AnalyzeCanUseSavepoint(Statement st) => st.Plan is not null;

    private void RunNoRows(string sql)
    {
        using Statement s = Prepare(sql);
        while (s.Step())
        {
        }
    }

    internal void Forget(Statement s) => _statements.Remove(s);

    // ---- programs kept between prepares ----------------------------------------------------------------------------------------------
    // Preparing the same text again is the common case (a statement per call, a command per query). When a statement is finalized its program goes
    // back here, and the next prepare of the same text takes it instead of parsing and planning again. A program is taken out of the table, so it
    // belongs to one statement at a time, exactly as a freshly compiled one does. It is used again only if everything compilation read is unchanged:
    // the connection still holds the same schema object (every schema change builds a new one) at the same cookie, with the same foreign-key
    // setting and text encoding. Anything else compiles again. A program that read other connection state (virtual tables, the statistics log
    // behind PRAGMA optimize) is never kept. c3ref/prepare: the schema is checked when the statement runs, which the kept program still does.
    private const int ProgramTableLimit = 32;
    private readonly Dictionary<string, RetiredStatement> _programs = new(StringComparer.Ordinal);

    /// <summary>Set by the compiler when it reads connection state a kept program could not account for.</summary>
    internal bool CompileTaint;
    private bool _noCover;

    /// <summary>The tables the compile in progress added to <see cref="StatsUsed"/>; a kept program adds them again when it is taken.</summary>
    internal readonly List<string> CompileStatsTables = [];

    internal bool TakeProgram(string sql, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RetiredStatement? retired)
    {
        if (_programs.Count != 0 && _programs.Remove(sql, out RetiredStatement? e))
        {
            VmProgram p = e.Program;
            Catalog? cat = _catalog;
            if (cat is not null && ReferenceEquals(p.CompiledAgainst, cat) && p.SchemaCookie == cat.Cookie && p.ForeignKeysAtCompile == ForeignKeysEnabled
                && p.Encoding == (_ctx?.Encoding ?? TextEncoding.Utf8))
            {
                if (p.StatsTables is { } st)
                {
                    foreach (string name in st)
                    {
                        StatsUsed.Add(name);
                    }
                }

                retired = e;
                return true;
            }
        }

        retired = null;
        return false;
    }

    /// <summary>True if a finalized statement with this program would be kept for the next prepare of its text.</summary>
    internal bool CanKeep(VmProgram program) => program.Reusable && ReferenceEquals(program.CompiledAgainst, _catalog);

    internal void KeepProgram(string sql, RetiredStatement retired)
    {
        if (_programs.Count >= ProgramTableLimit && !_programs.ContainsKey(sql))
        {
            using Dictionary<string, RetiredStatement>.Enumerator it = _programs.GetEnumerator();
            it.MoveNext();
            _programs.Remove(it.Current.Key);
        }

        _programs[sql] = retired;
    }

    private void MarkReusable(VmProgram p, Catalog cat)
    {
        p.CompiledAgainst = cat;
        p.ForeignKeysAtCompile = ForeignKeysEnabled;
        bool ok = !CompileTaint && !PragmaSettings.WritableSchema;
        foreach (CursorSpec c in p.Cursors)
        {
            if (c.Module is not null || c.Table?.Virtual is not null)
            {
                ok = false;
            }
        }

        p.Reusable = ok;
        p.StatsTables = CompileStatsTables.Count == 0 ? null : [.. CompileStatsTables];
    }

    /// <summary>Compiles one statement (parse, then name resolution against the schema).</summary>
    internal VmProgram CompileSql(string sql, out string tail, out bool empty, PlanMode mode = PlanMode.Off)
    {
        try
        {
            try
            {
                return CompileSqlCore(sql, out tail, out empty, mode);
            }
            catch (CoverMissException)
            {
                // A loop that reads an index alone was asked for a column the index lacks: compile again, reading the table too.
                _noCover = true;
                try
                {
                    return CompileSqlCore(sql, out tail, out empty, mode);
                }
                finally
                {
                    _noCover = false;
                }
            }
        }
        catch (OneLiteException e) when (e.Code == ResultCode.Error && _catalog != null
            && (e.Message.StartsWith("no such ", StringComparison.Ordinal) || e.Message.Contains(" already exists", StringComparison.Ordinal)))
        {
            // A name that is missing (or taken) in the schema this connection loaded earlier may be there in the file now: another connection can
            // have changed it since. The schema is read again and the statement compiled once more, as the reference does when a statement
            // fails to compile and the schema cookie has moved. If the file cannot be read right now (another connection holds a lock), the
            // original error stands, as it does there.
            if (!TryReloadSchema())
            {
                throw;
            }

            return CompileSqlCore(sql, out tail, out empty, mode);
        }
    }

    // Reads the schema again; the loaded one is replaced only if that works. False if the file cannot be read now (a lock held by another
    // connection): the schema already loaded stays in use.
    private bool TryReloadSchema()
    {
        Catalog? old = _catalog;
        try
        {
            for (; ; )
            {
                RunResult r = ReloadSchemaStep();
                if (r == RunResult.Done)
                {
                    break;
                }

                _pager.ExecuteSync();
            }
        }
        catch (OneLiteException e) when (e.Code == ResultCode.Busy)
        {
            _catalog = old;
            return false;
        }

        if (!ReferenceEquals(old, _catalog))
        {
            DisconnectVirtuals(old);
        }

        return true;
    }

    private VmProgram CompileSqlCore(string sql, out string tail, out bool empty, PlanMode mode)
    {
        SqlStatement? stmt = SqlParser.ParseOne(sql, 0, out int end, ConcurrentEnabled ? ParserOptions.WithConcurrent : null);
        tail = end >= sql.Length ? string.Empty : sql[end..];
        empty = stmt is null;
        if (stmt is null)
        {
            return new VmProgram { Code = [new Instr { Op = Op.Halt }], RegisterCount = 1 };
        }

        if (stmt is BeginStatement or CommitStatement or RollbackStatement or SavepointStatement or ReleaseStatement)
        {
            VmProgram cp = new Compiler(_catalog ?? new Catalog(), sql, TextEncoding.Utf8).CompileControl(stmt);
            cp.Encoding = _ctx?.Encoding ?? TextEncoding.Utf8;
            return cp;
        }

        if (stmt is PragmaStatement pr)
        {
            // The key pragmas and the cipher settings come before the first read of an encrypted file, so they must not load the schema.
            string pn = pr.Name.Text.ToLowerInvariant();
            if (!(pn.StartsWith("cipher_", StringComparison.Ordinal) || pn is "key" or "hexkey" or "rekey" or "hexrekey" or "kdf_iter"))
            {
                EnsureCatalogSync();
            }

            VmProgram pp = new Compiler(_catalog ?? new Catalog(), sql, _ctx?.Encoding ?? TextEncoding.Utf8) { DatabaseFile = _pager.FullPath, TempOpen = PragmaSettings.TempOpen || _temp != null, Encrypted = _pager.IsEncrypted }.CompilePragma(pr);
            pp.SchemaCookie = _catalog?.Cookie ?? 0;
            if (pr.Schema is { } psch && string.Equals(psch.Text, "temp", StringComparison.OrdinalIgnoreCase))
            {
                PragmaSettings.TempOpen = true;
            }

            return pp;
        }

        if (stmt is AttachStatement or DetachStatement)
        {
            VmProgram ap = new Compiler(_catalog ?? new Catalog(), sql, TextEncoding.Utf8).CompileAttach(stmt);
            ap.Encoding = _ctx?.Encoding ?? TextEncoding.Utf8;
            return ap;
        }

        if (stmt is InsertStatement or UpdateStatement or DeleteStatement or CreateStatement or DropStatement or AlterTableStatement or ReindexStatement or AnalyzeStatement or VacuumStatement)
        {
            return CompileWrite(stmt, sql, mode);
        }

        if (stmt is not SelectStatement sel)
        {
            throw new OneLiteException(ResultCode.Error, "unsupported: " + Describe(stmt));
        }

        if (UsesTable(sel))
        {
            EnsureCatalogSync();
        }
        else
        {
            // Even a SELECT without FROM runs in the database's text encoding (hex('a') of a UTF-16 database is 6100), so the
            // header is read; a file that is not a database leaves the default, as the reference does for such a query.
            try
            {
                EnsureCatalogSync();
            }
            catch (OneLiteException)
            {
                // Not a readable database: the expression is evaluated as UTF-8.
            }
        }

        Catalog cat = _catalog ?? new Catalog();
        TextEncoding enc = _ctx?.Encoding ?? TextEncoding.Utf8;
        var compiler = new Compiler(cat, sql, enc) { NoCover = _noCover };
        if (mode != PlanMode.Off)
        {
            compiler.StartPlan("SELECT", mode == PlanMode.Count);
        }

        CompileTaint = false;
        CompileStatsTables.Clear();
        VmProgram p = compiler.CompileSelect(sel);
        p.Plan = compiler.FinishPlan();
        p.PlanCounterCount = p.Plan?.CounterCount ?? 0;
        p.SchemaCookie = cat.Cookie;
        p.Encoding = enc;
        if (mode == PlanMode.Off)
        {
            MarkReusable(p, cat);
        }

        return p;
    }

    private static bool UsesTable(SelectStatement s)
        => s.Cores.Length != 1 || s.Cores[0] is not SimpleSelect { From: null };

    private static string Describe(SqlStatement s) => s switch
    {
        InsertStatement => "INSERT",
        UpdateStatement => "UPDATE",
        DeleteStatement => "DELETE",
        CreateStatement => "CREATE",
        DropStatement => "DROP",
        PragmaStatement => "PRAGMA",
        ExplainStatement => "EXPLAIN",
        BeginStatement or CommitStatement or RollbackStatement or SavepointStatement or ReleaseStatement => "transaction control",
        _ => s.GetType().Name.Replace("Statement", string.Empty, StringComparison.Ordinal),
    };

    // ------------------------------------------------------------------ read transactions

    internal PagerStatus BeginReadStep()
    {
        if (BackupDestinationHeld)
        {
            throw new OneLiteException(ResultCode.Locked, "database table is locked");
        }

        PagerStatus s = _pager.BeginRead();
        if (s == PagerStatus.Ok)
        {
            if (_ctx == null || !GeometryMatches())
            {
                // The page size and text encoding are known only once page 1 has been read (fileformat2 §1.3).
                _engine.EnsureContext();
                _ctx = _engine.Context;
            }
        }
        else if (s == PagerStatus.Error)
        {
            try
            {
                _pager.EndReadSync();
            }
            catch (OneLiteException)
            {
                // The original error is the one to report.
            }
        }

        return s;
    }

    /// <summary>Rebuilds the B-tree context after the pager's geometry changed under it (pragma encoding on a database with no pages).</summary>
    internal void RefreshContext()
    {
        _engine.EnsureContext();
        _ctx = _engine.Context;
    }

    private TextEncoding Enc() => (TextEncoding)(byte)_pager.DatabaseEncoding;

    internal bool GeometryMatches()
        => _ctx != null && _ctx.PageSize == _pager.PageSize && _ctx.Usable == _pager.UsableSize && _ctx.Encoding == Enc();

    internal PagerStatus EndReadStep() => _readers > 1 ? PagerStatus.Ok : _pager.EndRead();

    /// <summary>Statements and transactions currently holding a read on the database (the caller included).</summary>
    internal int ActiveReaders => _readers;

    /// <summary>The auto-vacuum mode (0 none, 1 full, 2 incremental) that the next VACUUM applies, or -1 for none chosen.</summary>
    internal int PendingAutoVacuum { get; set; } = -1;

    internal void ReaderStarted() => _readers++;

    internal void ReaderDone()
    {
        if (--_readers == 0 && Attached.Count > 0)
        {
            EndAttachedReads();
        }
    }

    /// <summary>Ends the read transaction at once, abandoning anything in flight. Used by Reset and Dispose.</summary>
    internal void AbortRead()
    {
        if (_readers > 1)
        {
            return;
        }

        try
        {
            _pager.Abandon();
            if (_deferAbortRead)
            {
                _endReadOwed = true;
                return;
            }

            _pager.EndReadSync();
        }
        catch (OneLiteException)
        {
            // Nothing more can be done for a lock that cannot be released; the file lock goes with the descriptor.
        }
    }

    /// <summary>Reads the schema cookie (fileformat2 §1.3.8) from page 1 of the snapshot.</summary>
    internal BtStatus ReadCookie(out uint cookie)
    {
        BtStatus r = ReadMainCookie(out cookie);
        if (r == BtStatus.Ok && Attached.Count > 0)
        {
            cookie = unchecked(cookie + AttachedCookies());
        }

        return r;
    }

    private BtStatus ReadMainCookie(out uint cookie)
    {
        cookie = TempEpoch;
        if (_pager.PageCount == 0)
        {
            return BtStatus.Ok;
        }

        BtStatus s = _source!.GetPage(1, out PageHandle? h);
        if (s != BtStatus.Ok)
        {
            return s;
        }

        cookie = unchecked(BinaryPrimitives.ReadUInt32BigEndian(h!.Data.AsSpan(40, 4)) + TempEpoch);
        _source.Release(h);
        return BtStatus.Ok;
    }

    internal KeyInfo MakeKeyInfo(CursorSpec spec)
    {
        IndexInfo idx = spec.Index!;
        TextEncoding enc = _ctx!.Encoding;
        // fileformat2 §1.6: an index entry ends in the rowid, or for a WITHOUT ROWID table in the primary key columns the index lacks; the table's own
        // tree is keyed on the primary key alone.
        int tail = 1;
        if (spec.Table.WithoutRowid)
        {
            tail = 0;
            if (!ReferenceEquals(idx, spec.Table.PrimaryKey))
            {
                foreach (int pc in spec.Table.PrimaryKey!.Columns)
                {
                    if (Array.IndexOf(idx.Columns, pc) < 0)
                    {
                        tail++;
                    }
                }
            }
        }

        int n = idx.Columns.Length + tail;
        var colls = new CollationFunction?[n];
        bool[]? desc = null;
        if (idx.Desc.Length > 0 && Array.IndexOf(idx.Desc, true) >= 0)
        {
            desc = new bool[n];
            Array.Copy(idx.Desc, desc, idx.Desc.Length);
        }
        for (int i = 0; i < idx.Columns.Length; i++)
        {
            string? name = idx.Collations[i];
            if (name is not null && AsciiComparer.Instance.Equals(name, "NOCASE"))
            {
                colls[i] = Collations.NoCase(enc);
            }
            else if (name is not null && AsciiComparer.Instance.Equals(name, "RTRIM"))
            {
                colls[i] = Collations.RTrim(enc);
            }
            else if (name is not null && UserCollation(name) is { } user)
            {
                colls[i] = user;
            }
        }

        return new KeyInfo(enc, colls, desc);
    }

    // ------------------------------------------------------------------ schema

    /// <summary>One step of (re)loading the schema; resumable. Returns Done, Pending or Error.</summary>
    internal RunResult ReloadSchemaStep()
    {
        try
        {
            for (; ; )
            {
                switch (_rs)
                {
                    case 0:
                    {
                        PagerStatus s = BeginReadStep();
                        if (s == PagerStatus.Pending)
                        {
                            return RunResult.Pending;
                        }

                        if (s == PagerStatus.Error)
                        {
                            throw _pager.Error!;
                        }

                        ReaderStarted();
                        _rs = 1;
                        break;
                    }

                    case 1:
                    {
                        BtStatus s = ReadCookie(out _newCookie);
                        if (s == BtStatus.Pending)
                        {
                            return RunResult.Pending;
                        }

                        if (s == BtStatus.Error)
                        {
                            throw Machine.Translate(_source!.LastError!);
                        }

                        _loader = _pager.PageCount == 0 ? null : new SchemaLoader(_ctx!);
                        _rs = 2;
                        break;
                    }

                    case 2:
                    {
                        if (_loader is null)
                        {
                            var empty = new Catalog { Cookie = _newCookie };
                            TableInfo m = SchemaBuilder.SchemaTable();
                            empty.Tables["sqlite_master"] = m;
                            empty.Tables["sqlite_schema"] = m;
                            MergeTemp(empty);
                            MergeAttached(empty);
                            empty.Owner = this;
                            _catalog = empty;
                            _rs = 3;
                            break;
                        }

                        BtStatus s = _loader.Step(out Catalog? cat);
                        if (s == BtStatus.Pending)
                        {
                            return RunResult.Pending;
                        }

                        if (s == BtStatus.Error)
                        {
                            throw Machine.Translate(_loader.Error ?? _source!.LastError ?? new InvalidOperationException("schema load failed"));
                        }

                        cat!.Cookie = _newCookie;
                        MergeTemp(cat);
                        MergeAttached(cat);
                        cat!.Owner = this;
                        _catalog = cat;
                        _loader = null;
                        _rs = 3;
                        break;
                    }

                    default:
                    {
                        PagerStatus s = EndReadStep();
                        if (s == PagerStatus.Pending)
                        {
                            return RunResult.Pending;
                        }

                        ReaderDone();
                        _rs = 0;
                        if (s == PagerStatus.Error)
                        {
                            throw _pager.Error!;
                        }

                        return RunResult.Done;
                    }
                }
            }
        }
        catch (Exception e) when (e is OneLiteException or SourceFailure)
        {
            if (_rs > 0)
            {
                ReaderDone();
                AbortRead();
            }

            _rs = 0;
            _loader = null;
            throw Machine.Translate(e);
        }
    }

    /// <summary>The schema cookie the last stale check saw in the file; a catalog with this cookie is current and need not be read again.</summary>
    internal uint FileCookie;

    /// <summary>True while a schema load is part-way (it must be resumed, not skipped, even though the new catalog is already installed).</summary>
    internal bool SchemaReloading => _rs != 0;

    /// <summary>True when a statement that changed the schema should rebuild the catalog itself (only for a plain main database with nothing temporary or attached in the way).</summary>
    internal bool ReloadAfterDdl => _catalog is not null && _temp is null && Attached.Count == 0;

    /// <summary>Drops the old catalog's virtual-table connections before a reload that replaces it.</summary>
    internal void BeginSchemaRefresh()
    {
        DisconnectVirtuals(_catalog);
        _catalog = null;
    }

    internal void InvalidateSchema()
    {
        DisconnectVirtuals(_catalog);
        _catalog = null;
        if (_temp != null)
        {
            TempEpoch++;
        }
    }

    // The temp database (lang_naming): created the first time something needs it.
    private TempDb? _temp;

    /// <summary>Bumped whenever the temp schema may have changed; folded into the schema cookie so prepared statements notice.</summary>
    internal uint TempEpoch;

    internal TempDb? Temp => _temp;

    /// <summary>The temp database, created on first use. It joins the open transaction: a savepoint for every one already open.</summary>
    internal TempDb GetTemp()
    {
        if (_temp is null)
        {
            _temp = new TempDb(_ctx?.Encoding ?? TextEncoding.Utf8);
            foreach (SavepointEntry e in Savepoints)
            {
                e.TempLevel = _temp.Pager.OpenSavepoint();
            }
        }

        return _temp;
    }

    internal BtContext TempContext => GetTemp().Context;

    /// <summary>The context that holds the trees of database <paramref name="db"/> (0 main, 1 temp).</summary>
    internal BtContext ContextOf(int db) => db >= 2 ? AttachedBySlot(db)!.Context : db == 1 ? GetTemp().Context : _ctx!;

    // Adds the temp schema to a freshly loaded main catalog: temp objects shadow main ones of the same name (lang_naming), and a temp
    // trigger may sit on a main table.
    private void MergeTemp(Catalog cat)
    {
        cat.MainTables = new Dictionary<string, TableInfo>(cat.Tables, AsciiComparer.Instance);
        TableInfo tm = cat.TempMaster;
        cat.TempTables["sqlite_temp_master"] = tm;
        cat.TempTables["sqlite_temp_schema"] = tm;
        cat.Tables["sqlite_temp_master"] = tm;
        cat.Tables["sqlite_temp_schema"] = tm;
        cat.TempTables["sqlite_master"] = tm;
        cat.TempTables["sqlite_schema"] = tm;
        if (_temp is null || _temp.Pager.PageCount == 0)
        {
            return;
        }

        var loader = new SchemaLoader(_temp.Context, 1);
        Catalog? tc = null;
        BtStatus s = loader.Step(out tc);
        if (s == BtStatus.Error)
        {
            throw Machine.Translate(loader.Error ?? _temp.Source.LastError ?? new InvalidOperationException("temp schema load failed"));
        }

        cat.TempMaster = tc!.TempMaster;
        cat.TempTables["sqlite_temp_master"] = cat.TempMaster;
        cat.TempTables["sqlite_temp_schema"] = cat.TempMaster;
        cat.Tables["sqlite_temp_master"] = cat.TempMaster;
        cat.Tables["sqlite_temp_schema"] = cat.TempMaster;
        cat.TempTables["sqlite_master"] = cat.TempMaster;
        cat.TempTables["sqlite_schema"] = cat.TempMaster;
        cat.TempRows.AddRange(tc.TempRows);
        foreach ((string name, TableInfo t) in tc.Tables)
        {
            if (!name.StartsWith("sqlite_temp_", StringComparison.Ordinal))
            {
                cat.TempTables[name] = t;
                cat.Tables[name] = t;
            }
        }

        foreach ((string name, TriggerInfo tr) in tc.Triggers)
        {
            cat.Triggers["temp\0" + name] = tr;
            if (tr.OnMain && cat.MainTables.TryGetValue(tr.Table, out TableInfo? mt))
            {
                // A temp trigger on a main table: the table is copied so the main catalog's own list stays as stored.
                // Observed: they fire before the table's own triggers, oldest first (the list is walked from its end, newest first).
                TableInfo copy = mt.CopyForTriggers();
                copy.HasTriggers = true;
                copy.Triggers.Insert(mt.Triggers.Count(x => x.Db == 0), tr);
                foreach (KeyValuePair<string, TableInfo> kv in cat.MainTables.Where(kv => ReferenceEquals(kv.Value, mt)).ToList())
                {
                    cat.MainTables[kv.Key] = copy;
                    if (cat.Tables.TryGetValue(kv.Key, out TableInfo? cur) && ReferenceEquals(cur, mt))
                    {
                        cat.Tables[kv.Key] = copy;
                    }
                }
            }
        }
    }

    /// <summary>Notes that the temp schema changed (DDL on it, or a rollback that undid DDL).</summary>
    internal void TempSchemaChanged()
    {
        TempEpoch++;
        DisconnectVirtuals(_catalog);
        _catalog = null;
    }

    internal void EnsureCatalogSync()
    {
        if (_catalog != null)
        {
            return;
        }

        if (_syncSchemaLoadForbidden)
        {
            throw new CatalogNotLoadedException();
        }

        for (; ; )
        {
            RunResult r = ReloadSchemaStep();
            if (r == RunResult.Done)
            {
                return;
            }

            _pager.ExecuteSync();
        }
    }

    /// <summary>
    /// Closes the connection like <see cref="Dispose"/>, awaiting the VFS to release the file lock, so it works on a backend that has only asynchronous calls.
    /// </summary>
    /// <returns>A task that completes when the connection is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        foreach (Statement s in _statements.ToArray())
        {
            await s.DisposeAsync().ConfigureAwait(false);
        }

        _disposed = true;
        foreach (AttachedDb a in Attached)
        {
            a.Dispose();
        }

        _temp?.Dispose();
        await _engine.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Closes the connection after finalizing its statements. Releases the file lock.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (Statement s in _statements.ToArray())
        {
            s.Dispose();
        }

        _disposed = true;
        foreach (AttachedDb a in Attached)
        {
            a.Dispose();
        }

        _temp?.Dispose();
        _engine.Dispose();
    }
}
