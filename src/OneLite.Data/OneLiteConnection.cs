using System.Collections.Concurrent;
using OneLiteDatabase = RecursiveAI.OneLite.Database;
using System.Data;
using System.Data.Common;
using RecursiveAI.OneLite.Storage.Crypto;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Data;

/// <summary>
/// An ADO.NET connection to a OneLite database. <see cref="OpenAsync(CancellationToken)"/> and every <c>...Async</c> method on the commands and readers of this
/// connection really are asynchronous: they await the storage layer's I/O instead of blocking a thread. A connection is not thread-safe; use one per thread.
/// </summary>
public sealed class OneLiteConnection : DbConnection
{
    private static readonly ConcurrentDictionary<string, SharedMemoryDatabase> s_shared = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, RecursiveAI.OneLite.Storage.Vfs.IVfs> s_vfs = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a storage backend (a <see cref="IVfs"/>) under a name, so that a connection string can select it with <c>Vfs=name</c>.
    /// The data source is then a path in that backend. Registering a name again replaces the backend for connections opened afterwards.
    /// </summary>
    /// <param name="name">The name used in <c>Vfs=</c>.</param>
    /// <param name="vfs">The backend.</param>
    public static void RegisterVfs(string name, IVfs vfs)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(vfs);
        s_vfs[name] = vfs;
    }

    private string _connectionString = string.Empty;
    private OneLiteConnectionStringBuilder? _builder;
    private OneLiteDatabase? _db;
    private Connection? _conn;
    private SharedMemoryDatabase? _sharedMemory;
    private ConnectionState _state = ConnectionState.Closed;
    private OneLiteTransaction? _transaction;
    private readonly List<OneLiteDataReader> _readers = [];

    /// <summary>Creates a closed connection.</summary>
    public OneLiteConnection()
    {
    }

    /// <summary>Creates a closed connection with a connection string.</summary>
    /// <param name="connectionString">See <see cref="OneLiteConnectionStringBuilder"/>.</param>
    public OneLiteConnection(string connectionString) => ConnectionString = connectionString;

    private sealed class SharedMemoryDatabase(MemoryVfs vfs)
    {
        internal MemoryVfs Vfs { get; } = vfs;

        internal int Users;
    }

    /// <summary>The engine connection while open.</summary>
    internal Connection Engine => _conn ?? throw new InvalidOperationException("the connection is not open");

    /// <summary>The open transaction of this connection, or null.</summary>
    internal OneLiteTransaction? CurrentTransaction => _transaction;

    internal void SetTransaction(OneLiteTransaction? t) => _transaction = t;

    internal void Track(OneLiteDataReader r) => _readers.Add(r);

    internal void Untrack(OneLiteDataReader r) => _readers.Remove(r);

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString
    {
        get => _connectionString;
        set
        {
            if (_state != ConnectionState.Closed)
            {
                throw new InvalidOperationException("the connection string cannot be changed while the connection is open");
            }

            _connectionString = value ?? string.Empty;
            _builder = null;
        }
    }

    /// <inheritdoc />
    public override string Database => "main";

    /// <inheritdoc />
    public override string DataSource => (_builder ??= new OneLiteConnectionStringBuilder(_connectionString)).DataSource;

    /// <summary>The pinned SQLite release whose behaviour OneLite follows.</summary>
    public override string ServerVersion => "3.53.4";

    /// <inheritdoc />
    public override ConnectionState State => _state;

    /// <inheritdoc />
    protected override bool CanRaiseEvents => false;

    /// <inheritdoc />
    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException("a OneLite connection has one main database; use ATTACH");

    // Set by Create(deferExistsCheck: true) for a read-only open: the caller opens it with OpenReadOnlyAsync, which awaits the existence check.
    private (string Source, DatabaseOptions Options)? _readOnlyOpen;

    private (OneLiteDatabase Db, SharedMemoryDatabase? Shared) Create(bool deferExistsCheck = false)
    {
        OneLiteConnectionStringBuilder b = _builder ??= new OneLiteConnectionStringBuilder(_connectionString);
        string source = b.DataSource;
        OneLiteOpenMode mode = b.Mode;
        bool memory = mode == OneLiteOpenMode.Memory || source == ":memory:" || source.Length == 0;
        if (source.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            int q = source.IndexOf('?', StringComparison.Ordinal);
            string query = q >= 0 ? source[(q + 1)..] : string.Empty;
            source = source[5..(q >= 0 ? q : source.Length)];
            memory |= query.Contains("mode=memory", StringComparison.OrdinalIgnoreCase);
        }

        var options = new DatabaseOptions
        {
            BusyTimeoutMs = Math.Max(0, b.DefaultTimeout) * 1000,
            ForeignKeys = b.ForeignKeys ?? false,
            Key = b.Password is { Length: > 0 } pw ? CipherKey.Parse(pw) : null,
        };

        if (b.Vfs is { Length: > 0 } vfsName)
        {
            if (!s_vfs.TryGetValue(vfsName, out IVfs? custom))
            {
                throw new ArgumentException($"no storage backend is registered as '{vfsName}'; call OneLiteConnection.RegisterVfs first");
            }

            var withVfs = new DatabaseOptions { Vfs = custom, BusyTimeoutMs = options.BusyTimeoutMs, ForeignKeys = options.ForeignKeys, Key = options.Key };
            if (mode == OneLiteOpenMode.ReadOnly && deferExistsCheck)
            {
                _readOnlyOpen = (source, withVfs);
                return (null!, null);
            }

            return (mode == OneLiteOpenMode.ReadOnly ? OneLiteDatabase.OpenReadOnly(source, withVfs) : OneLiteDatabase.Open(source, withVfs), null);
        }

        if (memory)
        {
            bool shared = b.Cache == OneLiteCacheMode.Shared && source.Length > 0 && source != ":memory:";
            SharedMemoryDatabase sm;
            if (shared)
            {
                lock (s_shared)
                {
                    sm = s_shared.GetOrAdd(source, _ => new SharedMemoryDatabase(new MemoryVfs()));
                    sm.Users++;
                }
            }
            else
            {
                sm = new SharedMemoryDatabase(new MemoryVfs()) { Users = 1 };
            }

            return (OneLiteDatabase.Open("memory.db", new DatabaseOptions
            {
                Vfs = sm.Vfs,
                BusyTimeoutMs = options.BusyTimeoutMs,
                ForeignKeys = options.ForeignKeys,
                Key = options.Key,
            }), sm);
        }

        switch (mode)
        {
            case OneLiteOpenMode.ReadOnly:
                return (OneLiteDatabase.OpenReadOnly(source, options), null);
            case OneLiteOpenMode.ReadWrite:
                if (!FileVfs.Instance.Access(source, AccessMode.Exists))
                {
                    throw new OneLiteException(ResultCode.CantOpen, "unable to open database file");
                }

                return (OneLiteDatabase.Open(source, options), null);
            default:
                return (OneLiteDatabase.Open(source, options), null);
        }
    }

    private static void Release(OneLiteDatabase? db, SharedMemoryDatabase? shared, string? sharedName)
    {
        db?.Dispose();
        if (shared is not null && sharedName is not null)
        {
            lock (s_shared)
            {
                if (--shared.Users <= 0)
                {
                    s_shared.TryRemove(sharedName, out _);
                }
            }
        }
    }

    private void Opened(OneLiteDatabase db, Connection conn, SharedMemoryDatabase? shared)
    {
        _db = db;
        _conn = conn;
        _sharedMemory = shared;
        _state = ConnectionState.Open;
    }

    /// <inheritdoc />
    public override void Open()
    {
        if (_state == ConnectionState.Open)
        {
            return;
        }

        if (_connectionString.Length == 0)
        {
            throw new InvalidOperationException("Open requires the ConnectionString property to be set");
        }

        OneLiteDatabase? db = null;
        SharedMemoryDatabase? sm = null;
        try
        {
            (db, sm) = Create();
            Opened(db, db.Connect(), sm);
        }
        catch (OneLiteException e)
        {
            Release(db, sm, SharedNameFor(sm));
            throw new OneLiteDataException(e);
        }
    }

    private string? SharedNameFor(SharedMemoryDatabase? sm) => sm is not null && _builder?.Cache == OneLiteCacheMode.Shared ? _builder.DataSource : null;

    /// <inheritdoc />
    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (_state == ConnectionState.Open)
        {
            return;
        }

        if (_connectionString.Length == 0)
        {
            throw new InvalidOperationException("Open requires the ConnectionString property to be set");
        }

        OneLiteDatabase? db = null;
        SharedMemoryDatabase? sm = null;
        try
        {
            _readOnlyOpen = null;
            (db, sm) = Create(deferExistsCheck: true);
            if (_readOnlyOpen is { } ro)
            {
                db = await OneLiteDatabase.OpenReadOnlyAsync(ro.Source, ro.Options, cancellationToken).ConfigureAwait(false);
            }

            Opened(db, await db.ConnectAsync(cancellationToken).ConfigureAwait(false), sm);
        }
        catch (OneLiteException e)
        {
            Release(db, sm, SharedNameFor(sm));
            throw new OneLiteDataException(e);
        }
    }

    /// <inheritdoc />
    public override void Close()
    {
        if (_state == ConnectionState.Closed)
        {
            return;
        }

        foreach (OneLiteDataReader r in _readers.ToArray())
        {
            r.Dispose();
        }

        if (_transaction is { } t)
        {
            t.Dispose();
        }

        string? name = SharedNameFor(_sharedMemory);
        _conn?.Dispose();
        Release(_db, _sharedMemory, name);
        _conn = null;
        _db = null;
        _sharedMemory = null;
        _state = ConnectionState.Closed;
    }

    /// <inheritdoc />
    public override async Task CloseAsync()
    {
        if (_state == ConnectionState.Closed)
        {
            return;
        }

        foreach (OneLiteDataReader r in _readers.ToArray())
        {
            await r.DisposeAsync().ConfigureAwait(false);
        }

        if (_transaction is { } t)
        {
            await t.DisposeAsync().ConfigureAwait(false);
        }

        string? name = SharedNameFor(_sharedMemory);
        if (_conn is { } engine)
        {
            await engine.DisposeAsync().ConfigureAwait(false);
        }

        Release(_db, _sharedMemory, name);
        _conn = null;
        _db = null;
        _sharedMemory = null;
        _state = ConnectionState.Closed;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Starts a transaction: <c>BEGIN DEFERRED</c> by default, or <c>BEGIN IMMEDIATE</c> when <paramref name="deferred"/> is false.</summary>
    /// <param name="deferred">True (the default of <see cref="BeginTransaction(IsolationLevel)"/>) for <c>BEGIN DEFERRED</c>: the first statement decides whether it reads or writes. False for <c>BEGIN IMMEDIATE</c>.</param>
    /// <returns>The transaction.</returns>
    public OneLiteTransaction BeginTransaction(bool deferred) => BeginTransaction(IsolationLevel.Unspecified, deferred);

    /// <summary>Starts a transaction.</summary>
    /// <param name="isolationLevel">Serializable and Unspecified use SQLite's default; ReadUncommitted is also accepted. Others throw.</param>
    /// <param name="deferred">True for <c>BEGIN DEFERRED</c>, false for <c>BEGIN IMMEDIATE</c>.</param>
    /// <returns>The transaction.</returns>
    public OneLiteTransaction BeginTransaction(IsolationLevel isolationLevel, bool deferred)
    {
        if (_state != ConnectionState.Open)
        {
            throw new InvalidOperationException("BeginTransaction requires an open connection");
        }

        if (_transaction is not null)
        {
            throw new InvalidOperationException("OneLiteConnection does not support nested transactions.");
        }

        if (isolationLevel is not (IsolationLevel.Unspecified or IsolationLevel.Serializable or IsolationLevel.ReadCommitted or IsolationLevel.ReadUncommitted))
        {
            throw new ArgumentException($"isolation level {isolationLevel} is not supported", nameof(isolationLevel));
        }

        var t = new OneLiteTransaction(this, isolationLevel);
        t.Begin(deferred);
        return t;
    }

    /// <inheritdoc />
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => BeginTransaction(isolationLevel, true);

    /// <summary>Starts a deferred transaction (<c>BEGIN DEFERRED</c>); SQLite's transactions are serializable whatever the level.</summary>
    /// <param name="isolationLevel">Unspecified, Serializable, ReadCommitted or ReadUncommitted.</param>
    /// <returns>The transaction.</returns>
    public new OneLiteTransaction BeginTransaction(IsolationLevel isolationLevel) => (OneLiteTransaction)base.BeginTransaction(isolationLevel);

    /// <summary>Starts a deferred transaction.</summary>
    /// <returns>The transaction.</returns>
    public new OneLiteTransaction BeginTransaction() => (OneLiteTransaction)base.BeginTransaction();

    /// <inheritdoc />
    protected override DbCommand CreateDbCommand() => new OneLiteCommand { Connection = this, Transaction = _transaction };

    /// <inheritdoc />
    public override bool CanCreateBatch => true;

    /// <inheritdoc />
    protected override DbBatch CreateDbBatch() => new OneLiteBatch { Connection = this };

    /// <summary>Creates a command.</summary>
    /// <returns>The command.</returns>
    public new OneLiteCommand CreateCommand() => (OneLiteCommand)base.CreateCommand();
}
