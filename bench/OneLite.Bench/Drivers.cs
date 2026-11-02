using System.Data.Common;
using System.Text;
using RecursiveAI.OneLite.Data;

namespace RecursiveAI.OneLite.Bench;

/// <summary>A prepared statement as the workloads see it. Parameters are 1-based and bound by position (the SQL names them @p1, @p2, ... in order of first use).</summary>
internal interface IStmt : IDisposable
{
    void Bind(int i, long v);

    void Bind(int i, double v);

    void Bind(int i, string v);

    void BindBlob(int i, byte[] v);

    /// <summary>Advances to the next row; false once the statement is done (it is then reset and can be bound again).</summary>
    bool Step();

    long Int(int i);

    double Real(int i);

    /// <summary>The UTF-8 length (engine) or UTF-16 length (app level) of a text column; the workloads use it only as a checksum term for both sides alike, via the byte count.</summary>
    int TextLength(int i);

    /// <summary>Runs a statement that returns no rows to completion and resets it.</summary>
    void Exec();

    /// <summary>Abandons a half-read statement so it can be bound and stepped again.</summary>
    void Reset();
}

/// <summary>One open database on one of the four stacks.</summary>
internal interface IDriver : IDisposable
{
    string Label { get; }

    void Exec(string sql);

    IStmt Prepare(string sql);
}

internal static class Drivers
{
    private static AdoDriver OpenMds(string path, string? vecExt)
    {
        var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path + ";Pooling=False");
        c.Open();
        if (vecExt is not null)
        {
            c.EnableExtensions(true);
            c.LoadExtension(vecExt);
        }

        return new AdoDriver(c, "Microsoft.Data.Sqlite");
    }

    public static IDriver Open(string level, string side, string path, string? vecExt)
        => (level, side) switch
        {
            ("engine", "onelite") => new OneLiteEngine(path),
            ("engine", "native") => new NativeEngine(path, vecExt),
            ("app", "onelite") => new AdoDriver(new OneLiteConnection("Data Source=" + path), "OneLite.Data"),
            ("app", "native") => OpenMds(path, vecExt),
            _ => throw new ArgumentException("unknown level/side " + level + "/" + side),
        };
}

// ---- engine level, OneLite --------------------------------------------------------------------------------------

internal sealed class OneLiteEngine : IDriver
{
    private readonly Database _db;
    private readonly Connection _conn;

    public OneLiteEngine(string path)
    {
        _db = Database.Open(path);
        _conn = _db.Connect();
    }

    public string Label => "OneLite Statement.Step";

    public void Exec(string sql)
    {
        using Statement s = _conn.Prepare(sql);
        while (s.Step())
        {
        }
    }

    public IStmt Prepare(string sql) => new Stmt(_conn.Prepare(sql));

    public void Dispose()
    {
        _conn.Dispose();
        _db.Dispose();
    }

    private sealed class Stmt(Statement s) : IStmt
    {
        public void Bind(int i, long v) => s.Bind(i, v);

        public void Bind(int i, double v) => s.Bind(i, v);

        public void Bind(int i, string v) => s.Bind(i, v);

        public void BindBlob(int i, byte[] v) => s.Bind(i, v);

        public bool Step()
        {
            if (s.Step())
            {
                return true;
            }

            s.Reset();
            return false;
        }

        public long Int(int i) => s.GetInt64(i);

        public double Real(int i) => s.GetDouble(i);

        public int TextLength(int i) => s.GetTextUtf8(i).Length;

        public void Exec()
        {
            while (s.Step())
            {
            }

            s.Reset();
        }

        public void Reset() => s.Reset();

        public void Dispose() => s.Dispose();
    }
}

// ---- engine level, the pinned native library through raw P/Invoke -----------------------------------------------

internal sealed class NativeEngine : IDriver
{
    private static readonly nint Transient = -1;
    private nint _db;

    public NativeEngine(string path, string? vecExt)
    {
        int rc = Native.sqlite3_open_v2(path, out _db, Native.OpenReadWrite | Native.OpenCreate, 0);
        if (rc != Native.Ok)
        {
            throw new InvalidOperationException("native open failed: " + rc);
        }

        if (vecExt is not null)
        {
            _ = Native.sqlite3_enable_load_extension(_db, 1);
            rc = Native.sqlite3_load_extension(_db, vecExt, null, 0);
            if (rc != Native.Ok)
            {
                throw new InvalidOperationException("native load_extension failed: " + Native.Error(_db));
            }
        }
    }

    public string Label => "native sqlite3_step";

    public void Exec(string sql)
    {
        using IStmt s = Prepare(sql);
        s.Exec();
    }

    public IStmt Prepare(string sql)
    {
        int rc = Native.sqlite3_prepare_v2(_db, sql, -1, out nint st, 0);
        if (rc != Native.Ok)
        {
            throw new InvalidOperationException("native prepare failed: " + Native.Error(_db) + " in " + sql);
        }

        return new Stmt(_db, st);
    }

    public void Dispose()
    {
        if (_db != 0)
        {
            _ = Native.sqlite3_close_v2(_db);
            _db = 0;
        }
    }

    private sealed class Stmt(nint db, nint st) : IStmt
    {
        private void Check(int rc)
        {
            if (rc != Native.Ok)
            {
                throw new InvalidOperationException("native bind failed: " + Native.Error(db));
            }
        }

        public void Bind(int i, long v) => Check(Native.sqlite3_bind_int64(st, i, v));

        public void Bind(int i, double v) => Check(Native.sqlite3_bind_double(st, i, v));

        public void Bind(int i, string v)
        {
            Span<byte> buf = v.Length <= 120 ? stackalloc byte[v.Length * 3] : new byte[Encoding.UTF8.GetMaxByteCount(v.Length)];
            int n = Encoding.UTF8.GetBytes(v, buf);
            Check(Native.sqlite3_bind_text(st, i, buf[..n], n, Transient));
        }

        public void BindBlob(int i, byte[] v) => Check(Native.sqlite3_bind_blob(st, i, v, v.Length, Transient));

        public bool Step()
        {
            int rc = Native.sqlite3_step(st);
            if (rc == Native.Row)
            {
                return true;
            }

            _ = Native.sqlite3_reset(st);
            if (rc != Native.Done)
            {
                throw new InvalidOperationException("native step failed: " + Native.Error(db));
            }

            return false;
        }

        public long Int(int i) => Native.sqlite3_column_int64(st, i);

        public double Real(int i) => Native.sqlite3_column_double(st, i);

        public int TextLength(int i)
        {
            _ = Native.sqlite3_column_text(st, i);
            return Native.sqlite3_column_bytes(st, i);
        }

        public void Exec()
        {
            int rc;
            while ((rc = Native.sqlite3_step(st)) == Native.Row)
            {
            }

            _ = Native.sqlite3_reset(st);
            if (rc != Native.Done)
            {
                throw new InvalidOperationException("native step failed: " + Native.Error(db));
            }
        }

        public void Reset() => _ = Native.sqlite3_reset(st);

        public void Dispose() => _ = Native.sqlite3_finalize(st);
    }
}

// ---- app level: one ADO.NET implementation drives both OneLite.Data and Microsoft.Data.Sqlite -------------------

internal sealed class AdoDriver : IDriver
{
    private readonly DbConnection _conn;

    public AdoDriver(DbConnection conn, string label)
    {
        _conn = conn;
        Label = label;
        if (_conn.State != System.Data.ConnectionState.Open)
        {
            _conn.Open();
        }
    }

    public string Label { get; }

    public void Exec(string sql)
    {
        using DbCommand c = _conn.CreateCommand();
        c.CommandText = sql;
        // A PRAGMA that returns a row (journal_mode) still runs to completion through the reader.
        using DbDataReader r = c.ExecuteReader();
        while (r.Read())
        {
        }
    }

    public IStmt Prepare(string sql) => new Stmt(_conn, sql);

    public void Dispose() => _conn.Dispose();

    private sealed class Stmt : IStmt
    {
        private readonly DbCommand _cmd;
        private readonly List<DbParameter> _ps = [];
        private DbDataReader? _r;

        public Stmt(DbConnection conn, string sql)
        {
            _cmd = conn.CreateCommand();
            _cmd.CommandText = sql;
            // Parameters are created on first bind; p1.. in order.
        }

        private void Set(int i, object v)
        {
            while (_ps.Count < i)
            {
                DbParameter p = _cmd.CreateParameter();
                p.ParameterName = "@p" + (_ps.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                _cmd.Parameters.Add(p);
                _ps.Add(p);
            }

            _ps[i - 1].Value = v;
        }

        public void Bind(int i, long v) => Set(i, v);

        public void Bind(int i, double v) => Set(i, v);

        public void Bind(int i, string v) => Set(i, v);

        public void BindBlob(int i, byte[] v) => Set(i, v);

        public bool Step()
        {
            _r ??= _cmd.ExecuteReader();
            if (_r.Read())
            {
                return true;
            }

            Reset();
            return false;
        }

        public long Int(int i) => _r!.GetInt64(i);

        public double Real(int i) => _r!.GetDouble(i);

        public int TextLength(int i) => Encoding.UTF8.GetByteCount(_r!.GetString(i));

        public void Exec() => _cmd.ExecuteNonQuery();

        public void Reset()
        {
            _r?.Dispose();
            _r = null;
        }

        public void Dispose()
        {
            Reset();
            _cmd.Dispose();
        }
    }
}
