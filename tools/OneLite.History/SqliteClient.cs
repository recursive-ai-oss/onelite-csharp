using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RecursiveAI.OneLite.History;

/// <summary>Which client behaviour to build over a native sqlite3 library.</summary>
public enum SqliteClientKind
{
    /// <summary>Plain transactions, as SQLite defines them. Single writer: serializable.</summary>
    Strict,

    /// <summary>A deliberately weaker shim: reads come from a snapshot, writes are buffered, and at commit a key is rejected if it changed since the snapshot (first committer wins). Snapshot isolation, not serializable.</summary>
    SnapshotShim,

    /// <summary>A deliberately broken shim: like <see cref="SnapshotShim"/> but with no validation at commit, so concurrent writers overwrite each other (lost updates).</summary>
    NoValidationShim,
}

/// <summary>A connection to a database through the native library under test.</summary>
internal sealed class SqliteConn : IDisposable
{
    private nint _db;

    private SqliteConn(nint db) => _db = db;

    public static SqliteConn Open(string path, int busyMs)
    {
        var rc = SqliteNative.Open(path, out var db, SqliteNative.OpenReadWrite | SqliteNative.OpenCreate, 0);
        if (rc != 0)
        {
            throw new IOException("sqlite3_open_v2 failed with code " + rc);
        }

        SqliteNative.ExtendedResultCodes(db, 1);
        SqliteNative.BusyTimeout(db, busyMs);
        return new SqliteConn(db);
    }

    public int Exec(string sql) => SqliteNative.Exec(_db, sql, 0, 0, 0);

    public nint Prepare(string sql)
    {
        var rc = SqliteNative.Prepare(_db, sql, -1, out var stmt, 0);
        if (rc != 0)
        {
            throw new IOException("sqlite3_prepare_v2 failed with code " + rc + " for: " + sql);
        }

        return stmt;
    }

    /// <summary>Runs <c>SELECT v ... WHERE k = ?1</c>. Returns the result code (0 on success); <paramref name="text"/> is null when no row or a NULL.</summary>
    public static int SelectOne(nint stmt, int key, out string? text, out long number)
    {
        text = null;
        number = 0;
        SqliteNative.BindInt64(stmt, 1, key);
        var rc = SqliteNative.Step(stmt);
        if (rc == SqliteNative.Row)
        {
            text = SqliteNative.ColumnText(stmt, 0);
            number = SqliteNative.ColumnInt64(stmt, 0);
            rc = SqliteNative.Step(stmt);
        }

        // Reset returns the code of the last step if it failed; a clean statement returns 0.
        var rr = SqliteNative.Reset(stmt);
        if (rc == SqliteNative.Done)
        {
            return 0;
        }

        return rr != 0 ? rr : rc;
    }

    public static int RunWrite(nint stmt, int key, long value)
    {
        SqliteNative.BindInt64(stmt, 1, key);
        SqliteNative.BindInt64(stmt, 2, value);
        var rc = SqliteNative.Step(stmt);
        var rr = SqliteNative.Reset(stmt);
        return rc == SqliteNative.Done ? 0 : (rr != 0 ? rr : rc);
    }

    public static int RunWriteText(nint stmt, int key, string text)
    {
        SqliteNative.BindInt64(stmt, 1, key);
        SqliteNative.BindText(stmt, 2, text, -1, -1);
        var rc = SqliteNative.Step(stmt);
        var rr = SqliteNative.Reset(stmt);
        return rc == SqliteNative.Done ? 0 : (rr != 0 ? rr : rc);
    }

    public static void Finalize(nint stmt) => SqliteNative.Finalize(stmt);

    public void Dispose()
    {
        if (_db != 0)
        {
            SqliteNative.Close(_db);
            _db = 0;
        }
    }
}

/// <summary>Creates clients over a native sqlite3 library, one connection each, all on one database file.</summary>
public sealed class SqliteClientFactory : IHistoryClientFactory
{
    private readonly string _db;
    private readonly bool _immediate;
    private readonly string _journal;
    private readonly SqliteClientKind _kind;
    private readonly int _think;

    /// <summary>Creates the factory.</summary>
    /// <param name="libraryPath">Path of the sqlite3 shared library.</param>
    /// <param name="dbPath">Database file (created if absent).</param>
    /// <param name="immediate">True for <c>BEGIN IMMEDIATE</c>, false for a deferred <c>BEGIN</c>.</param>
    /// <param name="journalMode">The journal mode set on the file, for example <c>wal</c> or <c>delete</c>.</param>
    /// <param name="kind">The client behaviour.</param>
    /// <param name="shimThinkMs">For the shim kinds: milliseconds a client waits between its last read and its commit. It widens the window in which transactions overlap, so the weaker isolation shows up in short runs. Default 1.</param>
    public SqliteClientFactory(string libraryPath, string dbPath, bool immediate, string journalMode, SqliteClientKind kind, int shimThinkMs = 1)
    {
        _think = shimThinkMs;
        SqliteNative.SetLibraryPath(libraryPath);
        _db = dbPath;
        _immediate = immediate;
        _journal = journalMode;
        _kind = kind;
    }

    /// <summary>The version string of the loaded library.</summary>
    public static string LibraryVersion() => SqliteNative.LibVersion();

    /// <inheritdoc/>
    public void Prepare()
    {
        using var c = SqliteConn.Open(_db, 30000);
        if (c.Exec("PRAGMA journal_mode=" + _journal) != 0
            || c.Exec("CREATE TABLE IF NOT EXISTS l(k INTEGER PRIMARY KEY, v TEXT NOT NULL)") != 0
            || c.Exec("CREATE TABLE IF NOT EXISTS r(k INTEGER PRIMARY KEY, v INTEGER NOT NULL)") != 0)
        {
            throw new IOException("could not prepare the database");
        }
    }

    /// <inheritdoc/>
    public IHistoryClient Create(int worker)
    {
        var conn = SqliteConn.Open(_db, 30000);
        if (_journal.Equals("wal", StringComparison.OrdinalIgnoreCase))
        {
            conn.Exec("PRAGMA synchronous=NORMAL");
        }

        return _kind == SqliteClientKind.Strict ? new StrictClient(conn, _immediate) : new ShimClient(conn, _kind == SqliteClientKind.SnapshotShim, _think);
    }

    internal static int[] ParseList(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var parts = text.Split(',');
        var r = new int[parts.Length];
        for (var i = 0; i < r.Length; i++)
        {
            r[i] = int.Parse(parts[i], CultureInfo.InvariantCulture);
        }

        return r;
    }

    internal static string FormatList(int[] snap, List<int> own)
    {
        var sb = new StringBuilder();
        foreach (var x in snap)
        {
            if (sb.Length > 0)
            {
                sb.Append(',');
            }

            sb.Append(x.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var x in own)
        {
            if (sb.Length > 0)
            {
                sb.Append(',');
            }

            sb.Append(x.ToString(CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    private static bool IsBusy(int rc) => (rc & 0xff) is SqliteNative.ResultBusy or SqliteNative.ResultLocked;

    // The straightforward client: each operation is one statement inside a real SQLite transaction.
    private sealed class StrictClient : IHistoryClient
    {
        private readonly SqliteConn _c;
        private readonly string _begin;
        private readonly nint _readList;
        private readonly nint _appendList;
        private readonly nint _readReg;
        private readonly nint _writeReg;
        private bool _open;

        public StrictClient(SqliteConn c, bool immediate)
        {
            _c = c;
            _begin = immediate ? "BEGIN IMMEDIATE" : "BEGIN";
            _readList = c.Prepare("SELECT v FROM l WHERE k = ?1");
            _appendList = c.Prepare("INSERT INTO l(k, v) VALUES(?1, CAST(?2 AS TEXT)) ON CONFLICT(k) DO UPDATE SET v = v || ',' || ?2");
            _readReg = c.Prepare("SELECT v FROM r WHERE k = ?1");
            _writeReg = c.Prepare("INSERT INTO r(k, v) VALUES(?1, ?2) ON CONFLICT(k) DO UPDATE SET v = ?2");
        }

        public OpStatus Begin()
        {
            var rc = _c.Exec(_begin);
            if (rc != 0)
            {
                return OpStatus.Fail; // BEGIN IMMEDIATE timed out on the busy handler; nothing started.
            }

            _open = true;
            return OpStatus.Ok;
        }

        public OpStatus ReadList(int key, out int[] list)
        {
            var rc = SqliteConn.SelectOne(_readList, key, out var text, out _);
            list = rc == 0 ? ParseList(text) : [];
            return rc == 0 ? OpStatus.Ok : Failed();
        }

        public OpStatus ReadRegister(int key, out int value)
        {
            var rc = SqliteConn.SelectOne(_readReg, key, out _, out var n);
            value = (int)n;
            return rc == 0 ? OpStatus.Ok : Failed();
        }

        public OpStatus Write(int key, int value) => SqliteConn.RunWrite(_writeReg, key, value) == 0 ? OpStatus.Ok : Failed();

        public OpStatus Append(int key, int value) => SqliteConn.RunWrite(_appendList, key, value) == 0 ? OpStatus.Ok : Failed();

        public OpStatus Commit()
        {
            var rc = _c.Exec("COMMIT");
            if (rc == 0)
            {
                _open = false;
                return OpStatus.Ok;
            }

            // A busy COMMIT leaves the transaction open and uncommitted; anything else is not known to have failed cleanly.
            var busy = IsBusy(rc);
            Abort();
            return busy ? OpStatus.Fail : OpStatus.Info;
        }

        public void Abort()
        {
            if (_open)
            {
                _c.Exec("ROLLBACK");
                _open = false;
            }
        }

        public void Dispose()
        {
            Abort();
            SqliteConn.Finalize(_readList);
            SqliteConn.Finalize(_appendList);
            SqliteConn.Finalize(_readReg);
            SqliteConn.Finalize(_writeReg);
            _c.Dispose();
        }

        // An error before COMMIT cannot have committed anything.
        private OpStatus Failed()
        {
            Abort();
            return OpStatus.Fail;
        }
    }

    // Reads run in a snapshot transaction; writes are buffered and applied in a second, short transaction at commit.
    private sealed class ShimClient : IHistoryClient
    {
        private sealed class ListBuf
        {
            public string? SnapText;
            public int[] Snap = [];
            public readonly List<int> Own = [];
        }

        private sealed class RegBuf
        {
            public int Snap;
            public int? Own;
        }

        private readonly SqliteConn _c;
        private readonly bool _validate;
        private readonly nint _readList;
        private readonly nint _putList;
        private readonly nint _readReg;
        private readonly nint _writeReg;
        private readonly Dictionary<int, ListBuf> _lists = [];
        private readonly Dictionary<int, RegBuf> _regs = [];
        private readonly int _think;
        private bool _open;

        public ShimClient(SqliteConn c, bool validate, int thinkMs)
        {
            _think = thinkMs;
            _c = c;
            _validate = validate;
            _readList = c.Prepare("SELECT v FROM l WHERE k = ?1");
            _putList = c.Prepare("INSERT INTO l(k, v) VALUES(?1, ?2) ON CONFLICT(k) DO UPDATE SET v = ?2");
            _readReg = c.Prepare("SELECT v FROM r WHERE k = ?1");
            _writeReg = c.Prepare("INSERT INTO r(k, v) VALUES(?1, ?2) ON CONFLICT(k) DO UPDATE SET v = ?2");
        }

        public OpStatus Begin()
        {
            _lists.Clear();
            _regs.Clear();
            if (_c.Exec("BEGIN") != 0)
            {
                return OpStatus.Fail;
            }

            _open = true;
            return OpStatus.Ok;
        }

        private OpStatus EnsureList(int key, out ListBuf buf)
        {
            if (_lists.TryGetValue(key, out buf!))
            {
                return OpStatus.Ok;
            }

            buf = new ListBuf();
            var rc = SqliteConn.SelectOne(_readList, key, out var text, out _);
            if (rc != 0)
            {
                return Failed();
            }

            buf.SnapText = text;
            buf.Snap = ParseList(text);
            _lists[key] = buf;
            return OpStatus.Ok;
        }

        private OpStatus EnsureReg(int key, out RegBuf buf)
        {
            if (_regs.TryGetValue(key, out buf!))
            {
                return OpStatus.Ok;
            }

            buf = new RegBuf();
            var rc = SqliteConn.SelectOne(_readReg, key, out _, out var n);
            if (rc != 0)
            {
                return Failed();
            }

            buf.Snap = (int)n;
            _regs[key] = buf;
            return OpStatus.Ok;
        }

        public OpStatus ReadList(int key, out int[] list)
        {
            list = [];
            var s = EnsureList(key, out var b);
            if (s != OpStatus.Ok)
            {
                return s;
            }

            list = new int[b.Snap.Length + b.Own.Count];
            b.Snap.CopyTo(list, 0);
            b.Own.CopyTo(list, b.Snap.Length);
            return OpStatus.Ok;
        }

        public OpStatus ReadRegister(int key, out int value)
        {
            value = 0;
            var s = EnsureReg(key, out var b);
            if (s == OpStatus.Ok)
            {
                value = b.Own ?? b.Snap;
            }

            return s;
        }

        public OpStatus Write(int key, int value)
        {
            var s = EnsureReg(key, out var b);
            if (s == OpStatus.Ok)
            {
                b.Own = value;
            }

            return s;
        }

        public OpStatus Append(int key, int value)
        {
            var s = EnsureList(key, out var b);
            if (s == OpStatus.Ok)
            {
                b.Own.Add(value);
            }

            return s;
        }

        public OpStatus Commit()
        {
            if (_think > 0)
            {
                System.Threading.Thread.Sleep(_think);
            }

            var writes = false;
            foreach (var l in _lists.Values)
            {
                writes |= l.Own.Count > 0;
            }

            foreach (var r in _regs.Values)
            {
                writes |= r.Own is not null;
            }

            _c.Exec("ROLLBACK"); // end the snapshot; SQLite has nothing to commit for a read transaction
            _open = false;
            if (!writes)
            {
                return OpStatus.Ok;
            }

            if (_c.Exec("BEGIN IMMEDIATE") != 0)
            {
                return OpStatus.Fail;
            }

            _open = true;
            foreach (var (key, l) in _lists)
            {
                if (l.Own.Count == 0)
                {
                    continue;
                }

                if (_validate)
                {
                    if (SqliteConn.SelectOne(_readList, key, out var now, out _) != 0)
                    {
                        return Failed();
                    }

                    if (now != l.SnapText)
                    {
                        Abort();
                        return OpStatus.Fail;
                    }
                }

                if (SqliteConn.RunWriteText(_putList, key, FormatList(l.Snap, l.Own)) != 0)
                {
                    return Failed();
                }
            }

            foreach (var (key, r) in _regs)
            {
                if (r.Own is not { } v)
                {
                    continue;
                }

                if (_validate)
                {
                    if (SqliteConn.SelectOne(_readReg, key, out _, out var now) != 0)
                    {
                        return Failed();
                    }

                    if ((int)now != r.Snap)
                    {
                        Abort();
                        return OpStatus.Fail;
                    }
                }

                if (SqliteConn.RunWrite(_writeReg, key, v) != 0)
                {
                    return Failed();
                }
            }

            var rc = _c.Exec("COMMIT");
            if (rc == 0)
            {
                _open = false;
                return OpStatus.Ok;
            }

            var busy = IsBusy(rc);
            Abort();
            return busy ? OpStatus.Fail : OpStatus.Info;
        }

        public void Abort()
        {
            if (_open)
            {
                _c.Exec("ROLLBACK");
                _open = false;
            }
        }

        public void Dispose()
        {
            Abort();
            SqliteConn.Finalize(_readList);
            SqliteConn.Finalize(_putList);
            SqliteConn.Finalize(_readReg);
            SqliteConn.Finalize(_writeReg);
            _c.Dispose();
        }

        private OpStatus Failed()
        {
            Abort();
            return OpStatus.Fail;
        }
    }
}
