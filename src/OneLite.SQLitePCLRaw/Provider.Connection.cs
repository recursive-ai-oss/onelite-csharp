using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using SQLitePCL;

namespace RecursiveAI.OneLite.SQLitePCLRaw;

/// <summary>
/// An <see cref="ISQLite3Provider"/> that runs SQLitePCLRaw's <c>sqlite3_*</c> calls on OneLite's engine instead of a native library.
/// <para>
/// <c>Microsoft.Data.Sqlite</c> and Entity Framework Core use SQLitePCLRaw for every database call; install this provider with
/// <see cref="OneLiteBatteries.Init"/> instead of a <c>bundle_e_sqlite3</c> package and they run on OneLite. Result codes, extended codes and
/// error messages are the engine's, which match SQLite's. The calls that cannot be honoured (loadable extensions, snapshots, serialize,
/// <c>sqlite3_exec</c> with a row callback, the trace and profile callbacks) fail with a code and message; docs/adapter.md lists them.
/// </para>
/// <para>
/// Handles are table indexes, not addresses. A connection is used from one thread at a time, as with SQLite's default threading mode.
/// </para>
/// </summary>
public sealed partial class OneLiteSqlite3Provider : ISQLite3Provider
{
    /// <summary>The SQLite release this adapter reports (and is compatible with): <c>sqlite3_libversion()</c>.</summary>
    public const string SqliteVersion = "3.53.4";

    /// <summary><c>sqlite3_libversion_number()</c> for <see cref="SqliteVersion"/>.</summary>
    public const int SqliteVersionNumber = 3053004;

    private const string SourceId = "2026-07-24 19:02:57 bf7c7f30031888f4e796e429ab3978879485813aaca6f641c7b33e4e09459bcc";

    private const int Row = 100;
    private const int Done = 101;

    // Open flags (c3ref open).
    private const int OpenReadOnly = 0x1;
    private const int OpenReadWrite = 0x2;
    private const int OpenCreate = 0x4;
    private const int OpenUri = 0x40;
    private const int OpenMemory = 0x80;

    /// <inheritdoc />
    public string GetNativeLibraryName() => "OneLite (managed)";

    // ------------------------------------------------------------------ open and close

    /// <inheritdoc />
    public int sqlite3_open(utf8z filename, out IntPtr db) => sqlite3_open_v2(filename, out db, OpenReadWrite | OpenCreate, default);

    /// <inheritdoc />
    public int sqlite3_open_v2(utf8z filename, out IntPtr db, int flags, utf8z vfs)
    {
        var d = new DbState();
        d.Id = Handles.Add(d);
        db = d.Id;
        try
        {
            OpenInto(d, filename.utf8_to_string() ?? string.Empty, flags);
            d.Ok();
            return 0;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // SQLite hands back a handle even when the open fails, so that errmsg() can say why; the caller closes it.
            return d.Fail(e);
        }
    }

    private static readonly ConcurrentDictionary<string, SharedMemory> s_sharedMemory = new(StringComparer.Ordinal);

    private sealed class SharedMemory
    {
        internal Database Database = null!;
        internal int Users;
    }

    private static void OpenInto(DbState d, string name, int flags)
    {
        bool uri = (flags & OpenUri) != 0;
        bool readOnly = (flags & OpenReadOnly) != 0;
        bool create = (flags & OpenCreate) != 0 && !readOnly;
        bool memory = (flags & OpenMemory) != 0;
        bool shared = false;
        string path = name;
        if (uri && name.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            path = ParseUri(name, ref readOnly, ref create, ref memory, ref shared);
        }

        if (path.Length == 0 || path == ":memory:")
        {
            memory = true;
        }

        d.Path = path;
        d.Memory = memory;
        d.ReadOnly = readOnly;
        Database db;
        if (memory)
        {
            if (shared && path.Length > 0 && path != ":memory:")
            {
                string key = path;
                SharedMemory sm = s_sharedMemory.GetOrAdd(key, _ => new SharedMemory { Database = Database.Open("file:" + Uri.EscapeDataString(key) + "?mode=memory&cache=shared") });
                lock (sm)
                {
                    sm.Users++;
                }

                d.SharedKey = key;
                db = sm.Database;
            }
            else
            {
                db = Database.Open(":memory:");
            }

            d.Path = path.Length == 0 ? string.Empty : path;
        }
        else if (readOnly)
        {
            db = Database.OpenReadOnly(path);
        }
        else
        {
            if (!create && !File.Exists(path))
            {
                throw new OneLiteException(ResultCode.CantOpen, "unable to open database file");
            }

            db = Database.Open(path);
        }

        d.Database = db;
        try
        {
            d.Connection = db.Connect();
        }
        catch
        {
            ReleaseDatabase(d);
            throw;
        }
    }

    private static void ReleaseDatabase(DbState d)
    {
        if (d.SharedKey is { } key && s_sharedMemory.TryGetValue(key, out SharedMemory? sm))
        {
            bool last;
            lock (sm)
            {
                last = --sm.Users <= 0;
            }

            if (last)
            {
                s_sharedMemory.TryRemove(key, out _);
                sm.Database.Dispose();
            }
        }
        else
        {
            d.Database?.Dispose();
        }

        d.Database = null;
        d.SharedKey = null;
    }

    /// <summary>Resolves a <c>file:</c> URI (c3ref open, "URI filenames"): the path and the <c>mode</c> and <c>cache</c> query parameters.</summary>
    private static string ParseUri(string uri, ref bool readOnly, ref bool create, ref bool memory, ref bool shared)
    {
        string rest = uri[5..];
        string query = string.Empty;
        int q = rest.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0)
        {
            query = rest[(q + 1)..];
            rest = rest[..q];
        }

        int hash = rest.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            rest = rest[..hash];
        }

        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            int slash = rest.IndexOf('/', 2);
            rest = slash < 0 ? string.Empty : rest[slash..];
        }

        string path = Uri.UnescapeDataString(rest);
        foreach (string kv in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = kv.IndexOf('=', StringComparison.Ordinal);
            string k = eq < 0 ? kv : kv[..eq];
            string v = eq < 0 ? string.Empty : Uri.UnescapeDataString(kv[(eq + 1)..]);
            if (k == "mode")
            {
                switch (v)
                {
                    case "ro":
                        readOnly = true;
                        create = false;
                        break;
                    case "rw":
                        create = false;
                        break;
                    case "rwc":
                        create = true;
                        break;
                    case "memory":
                        memory = true;
                        break;
                    default:
                        throw new OneLiteException(ResultCode.Misuse, "no such access mode: " + v);
                }
            }
            else if (k == "cache")
            {
                shared = v == "shared";
            }
        }

        return path;
    }

    private static DbState? Db(sqlite3? h) => h is null ? null : Handles.Get<DbState>(h.DangerousGetHandle());

    private static StmtState? Stmt(sqlite3_stmt? h) => h is null ? null : Handles.Get<StmtState>(h.DangerousGetHandle());

    /// <inheritdoc />
    public int sqlite3_close_v2(IntPtr db) => Close(db, force: true);

    /// <inheritdoc />
    public int sqlite3_close(IntPtr db) => Close(db, force: false);

    private static int Close(IntPtr id, bool force)
    {
        DbState? d = Handles.Get<DbState>(id);
        if (d is null)
        {
            return 0;
        }

        if (!force && (d.Statements.Count > 0 || d.Backups > 0 || d.Blobs > 0))
        {
            return d.Fail(ResultCode.Busy, "unable to close due to unfinalized statements or unfinished backups");
        }

        d.Closed = true;
        foreach (StmtState s in d.Statements.ToArray())
        {
            FinalizeStatement(s);
        }

        try
        {
            d.Connection?.Dispose();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            d.Fail(e);
        }

        d.Connection = null;
        ReleaseDatabase(d);
        Handles.Remove(id);
        return 0;
    }

    /// <inheritdoc />
    public int sqlite3_enable_shared_cache(int enable) => 0;

    /// <inheritdoc />
    public void sqlite3_interrupt(sqlite3 db) => Db(db)?.Connection?.Interrupt();

    /// <inheritdoc />
    public int sqlite3__vfs__delete(utf8z vfs, utf8z pathname, int syncDir)
    {
        try
        {
            string? f = pathname.utf8_to_string();
            if (!string.IsNullOrEmpty(f))
            {
                File.Delete(f);
            }

            return 0;
        }
        catch (IOException)
        {
            return ResultCode.IoErrDelete;
        }
        catch (UnauthorizedAccessException)
        {
            return ResultCode.IoErrDelete;
        }
    }

    /// <inheritdoc />
    public int sqlite3_threadsafe() => 1;

    /// <inheritdoc />
    public utf8z sqlite3_libversion() => utf8z.FromString(SqliteVersion);

    /// <inheritdoc />
    public int sqlite3_libversion_number() => SqliteVersionNumber;

    /// <inheritdoc />
    public utf8z sqlite3_sourceid() => utf8z.FromString(SourceId);

    /// <inheritdoc />
    public long sqlite3_memory_used() => GC.GetTotalMemory(false);

    /// <inheritdoc />
    public long sqlite3_memory_highwater(int resetFlag) => GC.GetTotalMemory(false);

    /// <inheritdoc />
    public long sqlite3_soft_heap_limit64(long n) => 0;

    /// <inheritdoc />
    public long sqlite3_hard_heap_limit64(long n) => 0;

    /// <inheritdoc />
    public int sqlite3_status(int op, out int current, out int highwater, int resetFlag)
    {
        current = 0;
        highwater = 0;
        return 0;
    }

    /// <inheritdoc />
    public int sqlite3_initialize() => 0;

    /// <inheritdoc />
    public int sqlite3_shutdown() => 0;

    // ------------------------------------------------------------------ connection information

    /// <inheritdoc />
    public int sqlite3_db_readonly(sqlite3 db, utf8z dbName)
    {
        DbState? d = Db(db);
        if (d?.Connection is null)
        {
            return -1;
        }

        string name = dbName.utf8_to_string() ?? "main";
        try
        {
            return DatabaseNames(d).Exists(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) ? (name.Equals("main", StringComparison.OrdinalIgnoreCase) ? (d.ReadOnly ? 1 : 0) : AttachedReadOnly(d, name)) : -1;
        }
        catch (OneLiteException)
        {
            return -1;
        }
    }

    private static int AttachedReadOnly(DbState d, string name) => d.ReadOnly ? 1 : 0;

    private static List<string> DatabaseNames(DbState d)
    {
        var names = new List<string>();
        using Statement st = d.Conn.Prepare("PRAGMA database_list");
        while (st.Step())
        {
            names.Add(st.GetString(1) ?? string.Empty);
        }

        return names;
    }

    /// <inheritdoc />
    public utf8z sqlite3_db_filename(sqlite3 db, utf8z att)
    {
        DbState? d = Db(db);
        if (d?.Connection is null)
        {
            return default;
        }

        string name = att.utf8_to_string() ?? "main";
        if (d.Memory && string.Equals(name, "main", StringComparison.OrdinalIgnoreCase))
        {
            return utf8z.FromString(string.Empty);
        }

        try
        {
            using Statement st = d.Conn.Prepare("PRAGMA database_list");
            while (st.Step())
            {
                if (string.Equals(st.GetString(1), name, StringComparison.OrdinalIgnoreCase))
                {
                    string file = st.GetString(2) ?? string.Empty;
                    return utf8z.FromString(file);
                }
            }
        }
        catch (OneLiteException)
        {
        }

        return default;
    }

    /// <inheritdoc />
    public utf8z sqlite3_errmsg(sqlite3 db)
    {
        DbState? d = Db(db);
        if (d is null)
        {
            return utf8z.FromString("out of memory");
        }

        return utf8z.FromString(d.Message ?? (d.Code == Row ? "another row available" : d.Code == Done ? "no more rows available" : "not an error"));
    }

    /// <inheritdoc />
    public long sqlite3_last_insert_rowid(sqlite3 db) => Db(db)?.Connection?.LastInsertedRowid ?? 0;

    /// <inheritdoc />
    public int sqlite3_changes(sqlite3 db) => (int)(Db(db)?.Connection?.ChangesCount ?? 0);

    /// <inheritdoc />
    public int sqlite3_total_changes(sqlite3 db) => (int)(Db(db)?.Connection?.TotalChangesCount ?? 0);

    /// <inheritdoc />
    public int sqlite3_get_autocommit(sqlite3 db) => Db(db)?.Connection?.AutoCommit == false ? 0 : 1;

    /// <inheritdoc />
    public int sqlite3_busy_timeout(sqlite3 db, int ms)
    {
        DbState? d = Db(db);
        if (d?.Connection is null)
        {
            return ResultCode.Misuse;
        }

        try
        {
            d.Exec("PRAGMA busy_timeout=" + DbState.Int(Math.Max(ms, 0)));
            d.Ok();
            return 0;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return d.Fail(e);
        }
    }

    /// <inheritdoc />
    public int sqlite3_extended_result_codes(sqlite3 db, int onoff)
    {
        DbState? d = Db(db);
        if (d is null)
        {
            return ResultCode.Misuse;
        }

        d.Extended = onoff != 0;
        return 0;
    }

    /// <inheritdoc />
    public int sqlite3_errcode(sqlite3 db)
    {
        DbState? d = Db(db);
        return d is null ? ResultCode.NoMem : d.Extended ? d.ExtCode : d.Code;
    }

    /// <inheritdoc />
    public int sqlite3_extended_errcode(sqlite3 db)
    {
        DbState? d = Db(db);
        return d is null ? ResultCode.NoMem : d.ExtCode;
    }

    /// <inheritdoc />
    public utf8z sqlite3_errstr(int rc) => utf8z.FromString(ErrorString(rc));

    /// <summary>The English text of a result code (c3ref errstr); extended codes give the text of their primary code.</summary>
    internal static string ErrorString(int rc) => (rc & 0xFF) switch
    {
        0 => "not an error",
        1 => "SQL logic error",
        3 => "access permission denied",
        4 => "query aborted",
        5 => "database is locked",
        6 => "database table is locked",
        7 => "out of memory",
        8 => "attempt to write a readonly database",
        9 => "interrupted",
        10 => "disk I/O error",
        11 => "database disk image is malformed",
        12 => "unknown operation",
        13 => "database or disk is full",
        14 => "unable to open database file",
        15 => "locking protocol",
        16 => "table contains no data",
        17 => "database schema has changed",
        18 => "string or blob too big",
        19 => "constraint failed",
        20 => "datatype mismatch",
        21 => "bad parameter or other API misuse",
        22 => "large file support is disabled",
        23 => "authorization denied",
        24 => "not used",
        25 => "column index out of range",
        26 => "file is not a database",
        27 => "notification message",
        28 => "warning message",
        100 => "another row available",
        101 => "no more rows available",
        _ => "unknown error",
    };

    /// <inheritdoc />
    public int sqlite3_limit(sqlite3 db, int id, int newVal)
    {
        DbState? d = Db(db);
        if (d is null || id < 0 || id >= d.Limits.Length)
        {
            return -1;
        }

        int old = d.Limits[id];
        if (newVal >= 0)
        {
            // The hard upper bound is the compile-time default (c3ref limit: a larger value is silently lowered to it).
            d.Limits[id] = Math.Min(newVal, DbState.HardLimit(id));
        }

        return old;
    }

    /// <inheritdoc />
    public int sqlite3_config(int op) => ResultCode.Error;

    /// <inheritdoc />
    public int sqlite3_config(int op, int val) => ResultCode.Error;

    /// <inheritdoc />
    public int sqlite3_db_config(sqlite3 db, int op, utf8z val) => ResultCode.Error;

    /// <inheritdoc />
    public int sqlite3_db_config(sqlite3 db, int op, int val, out int result)
    {
        result = 0;
        DbState? d = Db(db);
        if (d?.Connection is null)
        {
            return ResultCode.Misuse;
        }

        // SQLITE_DBCONFIG_ENABLE_FKEY (1002), ENABLE_TRIGGER (1003), ENABLE_LOAD_EXTENSION (1005), DEFENSIVE (1010), DQS_DML (1013), DQS_DDL (1014).
        try
        {
            switch (op)
            {
                case 1002:
                    if (val >= 0)
                    {
                        d.Exec("PRAGMA foreign_keys=" + (val != 0 ? "ON" : "OFF"));
                    }

                    result = int.Parse(d.Scalar("PRAGMA foreign_keys") ?? "0", CultureInfo.InvariantCulture);
                    return 0;
                case 1005:
                    result = 0;
                    return 0;
                default:
                    return ResultCode.Error;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return d.Fail(e);
        }
    }

    /// <inheritdoc />
    public int sqlite3_db_config(sqlite3 db, int op, IntPtr ptr, int int0, int int1) => ResultCode.Error;

    /// <inheritdoc />
    public int sqlite3_enable_load_extension(sqlite3 db, int enable)
    {
        DbState? d = Db(db);
        if (enable == 0)
        {
            return 0;
        }

        return d?.Fail(ResultCode.Error, "load_extension is not supported by OneLite; register managed extensions through the engine API") ?? ResultCode.Misuse;
    }

    /// <inheritdoc />
    public int sqlite3_load_extension(sqlite3 db, utf8z zFile, utf8z zProc, out utf8z pzErrMsg)
    {
        pzErrMsg = utf8z.FromString("not authorized");
        return Db(db)?.Fail(ResultCode.Error, "not authorized") ?? ResultCode.Misuse;
    }

    /// <inheritdoc />
    public int sqlite3_win32_set_directory(int typ, utf8z path) => ResultCode.Error;

    /// <inheritdoc />
    public int sqlite3_keyword_count() => 0;

    /// <inheritdoc />
    public int sqlite3_keyword_name(int i, out string name)
    {
        name = string.Empty;
        return ResultCode.Error;
    }

    /// <inheritdoc />
    public IntPtr sqlite3_serialize(sqlite3 db, utf8z schema, out long size, int flags)
    {
        size = 0;
        Db(db)?.Fail(ResultCode.Error, "sqlite3_serialize is not supported by OneLite");
        return IntPtr.Zero;
    }

    /// <inheritdoc />
    public int sqlite3_deserialize(sqlite3 db, utf8z schema, IntPtr data, long szDb, long szBuf, int flags)
        => Db(db)?.Fail(ResultCode.Error, "sqlite3_deserialize is not supported by OneLite") ?? ResultCode.Misuse;

    // ------------------------------------------------------------------ encryption

    /// <inheritdoc />
    public int sqlite3_key(sqlite3 db, ReadOnlySpan<byte> key) => ApplyKey(db, null, key, rekey: false);

    /// <inheritdoc />
    public int sqlite3_key_v2(sqlite3 db, utf8z dbname, ReadOnlySpan<byte> key) => ApplyKey(db, dbname.utf8_to_string(), key, rekey: false);

    /// <inheritdoc />
    public int sqlite3_rekey(sqlite3 db, ReadOnlySpan<byte> key) => ApplyKey(db, null, key, rekey: true);

    /// <inheritdoc />
    public int sqlite3_rekey_v2(sqlite3 db, utf8z dbname, ReadOnlySpan<byte> key) => ApplyKey(db, dbname.utf8_to_string(), key, rekey: true);

    private static int ApplyKey(sqlite3 db, string? schema, ReadOnlySpan<byte> key, bool rekey)
    {
        DbState? d = Db(db);
        if (d?.Connection is null)
        {
            return ResultCode.Misuse;
        }

        try
        {
            // SQLCipher's API is "PRAGMA key"; a key that is not valid UTF-8 goes in as the raw-key blob form.
            string literal;
            try
            {
                literal = DbState.QuoteLiteral(new UTF8Encoding(false, true).GetString(key));
            }
            catch (ArgumentException)
            {
                literal = "\"x'" + Convert.ToHexString(key) + "'\"";
            }

            string prefix = string.IsNullOrEmpty(schema) ? string.Empty : DbState.QuoteIdentifier(schema) + ".";
            d.Exec("PRAGMA " + prefix + (rekey ? "rekey" : "key") + "=" + literal);
            return d.Ok();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return d.Fail(e);
        }
    }

    // ------------------------------------------------------------------ memory

    /// <inheritdoc />
    public IntPtr sqlite3_malloc(int n) => System.Runtime.InteropServices.Marshal.AllocHGlobal(Math.Max(n, 1));

    /// <inheritdoc />
    public IntPtr sqlite3_malloc64(long n) => System.Runtime.InteropServices.Marshal.AllocHGlobal((nint)Math.Max(n, 1));

    /// <inheritdoc />
    public void sqlite3_free(IntPtr p)
    {
        if (p != IntPtr.Zero)
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(p);
        }
    }

    /// <inheritdoc />
    public int sqlite3_stricmp(IntPtr p, IntPtr q) => throw new NotSupportedException("sqlite3_stricmp takes native C strings; OneLite has none");

    /// <inheritdoc />
    public int sqlite3_strnicmp(IntPtr p, IntPtr q, int n) => throw new NotSupportedException("sqlite3_strnicmp takes native C strings; OneLite has none");

    /// <inheritdoc />
    public int sqlite3_compileoption_used(utf8z sql) => CompileOptions.IndexOf(Normalize(sql.utf8_to_string())) >= 0 ? 1 : 0;

    /// <inheritdoc />
    public utf8z sqlite3_compileoption_get(int n) => (uint)n < (uint)CompileOptions.Count ? utf8z.FromString(CompileOptions[n]) : default;

    private static string Normalize(string? s) => s is null ? string.Empty : s.StartsWith("SQLITE_", StringComparison.OrdinalIgnoreCase) ? s[7..] : s;

    // The options of the pinned release that describe behaviour OneLite reproduces.
    private static readonly List<string> CompileOptions =
    [
        "ENABLE_COLUMN_METADATA", "ENABLE_FTS3", "ENABLE_FTS4", "ENABLE_FTS5", "ENABLE_RTREE", "ENABLE_MATH_FUNCTIONS", "ENABLE_DBSTAT_VTAB",
        "THREADSAFE=1", "MAX_VARIABLE_NUMBER=32766",
    ];

    /// <inheritdoc />
    public int sqlite3_complete(utf8z sql) => IsComplete(sql.utf8_to_string() ?? string.Empty) ? 1 : 0;

    /// <inheritdoc />
    public int sqlite3_wal_autocheckpoint(sqlite3 db, int n)
    {
        DbState? d = Db(db);
        if (d?.Connection is null)
        {
            return ResultCode.Misuse;
        }

        try
        {
            d.Exec("PRAGMA wal_autocheckpoint=" + DbState.Int(n));
            return d.Ok();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return d.Fail(e);
        }
    }

    /// <inheritdoc />
    public int sqlite3_wal_checkpoint(sqlite3 db, utf8z dbName) => sqlite3_wal_checkpoint_v2(db, dbName, 0, out _, out _);

    /// <inheritdoc />
    public int sqlite3_wal_checkpoint_v2(sqlite3 db, utf8z dbName, int eMode, out int logSize, out int framesCheckPointed)
    {
        logSize = -1;
        framesCheckPointed = -1;
        DbState? d = Db(db);
        if (d?.Connection is null)
        {
            return ResultCode.Misuse;
        }

        string mode = eMode switch { 1 => "FULL", 2 => "RESTART", 3 => "TRUNCATE", _ => "PASSIVE" };
        try
        {
            string? name = dbName.utf8_to_string();
            string prefix = string.IsNullOrEmpty(name) ? string.Empty : DbState.QuoteIdentifier(name) + ".";
            using Statement st = d.Conn.Prepare("PRAGMA " + prefix + "wal_checkpoint(" + mode + ")");
            if (st.Step())
            {
                int busy = (int)st.GetInt64(0);
                logSize = (int)st.GetInt64(1);
                framesCheckPointed = (int)st.GetInt64(2);
                if (busy != 0)
                {
                    return d.Fail(ResultCode.Busy, "database is locked");
                }
            }

            return d.Ok();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return d.Fail(e);
        }
    }
}
