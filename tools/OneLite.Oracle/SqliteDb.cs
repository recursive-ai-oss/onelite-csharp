using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace RecursiveAI.OneLite.Oracle;

/// <summary>An error from the native library, with the extended result code and message text (rescode, c3ref/errcode).</summary>
public sealed class SqliteException : Exception
{
    /// <summary>The extended result code.</summary>
    public int ExtendedCode { get; }

    /// <summary>The primary result code (the low eight bits of the extended code).</summary>
    public int Code => ExtendedCode & 0xff;

    /// <summary>Creates the exception.</summary>
    public SqliteException(int extendedCode, string message) : base(message) => ExtendedCode = extendedCode;
}

/// <summary>A connection through the pinned native library. Not thread-safe.</summary>
public sealed class SqliteDb : IDisposable
{
    private IntPtr _db;

    private SqliteDb(IntPtr db) => _db = db;

    /// <summary>Opens (and with <paramref name="create"/> creates) a database file. Extended result codes are on.</summary>
    public static SqliteDb Open(string path, bool readOnly = false, bool create = true, int busyTimeoutMs = 10_000)
    {
        Native.EnsureLoaded();
        int flags = readOnly ? Native.OpenReadOnly : Native.OpenReadWrite | (create ? Native.OpenCreate : 0);
        int rc = Native.Open(path, out IntPtr db, flags, IntPtr.Zero);
        if (rc != 0)
        {
            string msg = Native.Utf8(Native.ErrMsg(db)) ?? "open failed";
            int ext = db == IntPtr.Zero ? rc : Native.ExtendedErrCode(db);
            if (db != IntPtr.Zero)
            {
                _ = Native.Close(db);
            }
            throw new SqliteException(ext, msg + " (" + path + ")");
        }
        _ = Native.ExtendedResultCodes(db, 1);
        _ = Native.BusyTimeout(db, busyTimeoutMs);
        return new SqliteDb(db);
    }

    /// <summary>The library's <c>sqlite3_libversion()</c>.</summary>
    public static string LibVersion { get { Native.EnsureLoaded(); return Native.Utf8(Native.LibVersion())!; } }

    /// <summary>The library's <c>sqlite3_sourceid()</c>.</summary>
    public static string SourceId { get { Native.EnsureLoaded(); return Native.Utf8(Native.SourceId())!; } }

    internal IntPtr Handle => _db != IntPtr.Zero ? _db : throw new ObjectDisposedException(nameof(SqliteDb));

    internal SqliteException LastError(int fallbackCode)
    {
        int c = Native.ExtendedErrCode(Handle);
        return new SqliteException(c != 0 ? c : fallbackCode, Native.Utf8(Native.ErrMsg(Handle)) ?? "error");
    }

    /// <summary>Runs one or more statements, discarding rows. Throws <see cref="SqliteException"/>.</summary>
    public void Exec(string sql)
    {
        int rc = Native.Exec(Handle, sql, IntPtr.Zero, IntPtr.Zero, out IntPtr err);
        if (rc != 0)
        {
            string msg = Native.Utf8(err) ?? "error";
            if (err != IntPtr.Zero)
            {
                Native.Free(err);
            }
            throw new SqliteException(Native.ExtendedErrCode(Handle), msg);
        }
    }

    /// <summary>Prepares one statement.</summary>
    public SqliteStatement Prepare(string sql)
    {
        int rc = Native.Prepare(Handle, sql, -1, out IntPtr stmt, IntPtr.Zero);
        if (rc != 0)
        {
            throw LastError(rc);
        }
        return new SqliteStatement(this, stmt);
    }

    /// <summary>
    /// Runs a multi-statement script one statement at a time and renders every result in the canonical form: per statement a
    /// <c>cols</c> line (when it returns columns), a <c>row</c> line per row, then <c>end</c>; an error ends the script with an
    /// <c>error</c> line carrying the extended result code and message text.
    /// </summary>
    public unsafe void RenderScript(string sql, StringBuilder output)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(sql);
        fixed (byte* start = utf8.Length == 0 ? new byte[1] : utf8)
        {
            byte* p = start;
            byte* end = start + utf8.Length;
            while (p < end)
            {
                int rc = Native.PrepareBytes(Handle, p, (int)(end - p), out IntPtr stmt, out byte* tail);
                if (rc != 0)
                {
                    SqliteException e = LastError(rc);
                    output.Append("error\t").Append(e.ExtendedCode).Append('\t').Append(e.Message).Append('\n');
                    return;
                }
                p = tail;
                if (stmt == IntPtr.Zero)
                {
                    continue;   // only whitespace or a comment
                }
                using var st = new SqliteStatement(this, stmt);
                int n = st.ColumnCount;
                if (n > 0)
                {
                    var names = new string[n];
                    for (int i = 0; i < n; i++)
                    {
                        names[i] = st.ColumnName(i);
                    }
                    output.Append("cols\t").Append(string.Join('\t', names)).Append('\n');
                }
                try
                {
                    while (st.Step())
                    {
                        var vals = new string[n];
                        for (int i = 0; i < n; i++)
                        {
                            vals[i] = Canonical.Value(st, i);
                        }
                        output.Append("row\t").Append(Canonical.Row(vals)).Append('\n');
                    }
                }
                catch (SqliteException e)
                {
                    output.Append("error\t").Append(e.ExtendedCode).Append('\t').Append(e.Message).Append('\n');
                    return;
                }
                output.Append("end\n");
            }
        }
    }

    /// <summary>Runs a query and returns every row as canonical value strings (see <see cref="Canonical"/>).</summary>
    public List<string[]> QueryCanonical(string sql)
    {
        var rows = new List<string[]>();
        using SqliteStatement st = Prepare(sql);
        while (st.Step())
        {
            var row = new string[st.ColumnCount];
            for (int i = 0; i < row.Length; i++)
            {
                row[i] = Canonical.Value(st, i);
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Runs a query and returns the first column of the first row as text (null for SQL NULL or no row).</summary>
    public string? Scalar(string sql)
    {
        using SqliteStatement st = Prepare(sql);
        return st.Step() ? st.TextOrNull(0) : null;
    }

    /// <summary>Closes the connection (<c>sqlite3_close_v2</c>).</summary>
    public void Dispose()
    {
        if (_db != IntPtr.Zero)
        {
            _ = Native.Close(_db);
            _db = IntPtr.Zero;
        }
    }
}

/// <summary>A prepared statement. Dispose finalizes it.</summary>
public sealed class SqliteStatement : IDisposable
{
    private static readonly IntPtr s_transient = new(-1);
    private readonly SqliteDb _db;
    private IntPtr _stmt;

    internal SqliteStatement(SqliteDb db, IntPtr stmt)
    {
        _db = db;
        _stmt = stmt;
    }

    /// <summary>Number of result columns.</summary>
    public int ColumnCount => Native.ColumnCount(_stmt);

    /// <summary>Name of result column <paramref name="i"/>.</summary>
    public string ColumnName(int i) => Native.Utf8(Native.ColumnName(_stmt, i)) ?? "";

    /// <summary>Storage class of column <paramref name="i"/> in the current row: 1 INTEGER, 2 FLOAT, 3 TEXT, 4 BLOB, 5 NULL.</summary>
    public int ColumnType(int i) => Native.ColumnType(_stmt, i);

    /// <summary>Integer value of column <paramref name="i"/>.</summary>
    public long GetInt64(int i) => Native.ColumnInt64(_stmt, i);

    /// <summary>Floating-point value of column <paramref name="i"/>.</summary>
    public double GetDouble(int i) => Native.ColumnDouble(_stmt, i);

    /// <summary>The UTF-8 bytes of a TEXT column, or the bytes of a BLOB column.</summary>
    public byte[] Bytes(int i)
    {
        IntPtr p = ColumnType(i) == Native.TypeText ? Native.ColumnText(_stmt, i) : Native.ColumnBlob(_stmt, i);
        return CopyOut(p, Native.ColumnBytes(_stmt, i));
    }

    /// <summary>The column as text, or null for SQL NULL.</summary>
    public string? TextOrNull(int i)
    {
        if (ColumnType(i) == Native.TypeNull)
        {
            return null;
        }
        IntPtr p = Native.ColumnText(_stmt, i);   // converts numbers and blobs to text
        return Encoding.UTF8.GetString(CopyOut(p, Native.ColumnBytes(_stmt, i)));
    }

    private static byte[] CopyOut(IntPtr p, int n)
    {
        if (p == IntPtr.Zero || n <= 0)
        {
            return [];
        }
        var b = new byte[n];
        Marshal.Copy(p, b, 0, n);
        return b;
    }

    /// <summary>Steps once. Returns true for a row, false when done; throws on error (including BUSY).</summary>
    public bool Step()
    {
        int rc = Native.Step(_stmt);
        if (rc == Native.Row)
        {
            return true;
        }
        if (rc == Native.Done)
        {
            return false;
        }
        throw _db.LastError(rc);
    }

    /// <summary>Resets the statement for re-execution (bindings are kept).</summary>
    public void Reset() => _ = Native.Reset(_stmt);

    /// <summary>Binds NULL to parameter <paramref name="i"/> (1-based).</summary>
    public void BindNull(int i) => Check(Native.BindNull(_stmt, i));

    /// <summary>Binds an integer to parameter <paramref name="i"/> (1-based).</summary>
    public void Bind(int i, long v) => Check(Native.BindInt64(_stmt, i, v));

    /// <summary>Binds a float to parameter <paramref name="i"/> (1-based).</summary>
    public void Bind(int i, double v) => Check(Native.BindDouble(_stmt, i, v));

    /// <summary>Binds text (UTF-8, copied) to parameter <paramref name="i"/> (1-based). The text must not be empty.</summary>
    public void Bind(int i, string v)
    {
        byte[] b = Encoding.UTF8.GetBytes(v);
        Check(Native.BindText(_stmt, i, ref b[0], b.Length, s_transient));
    }

    /// <summary>Binds a blob (copied) to parameter <paramref name="i"/> (1-based). The blob must not be empty.</summary>
    public void Bind(int i, byte[] v) => Check(Native.BindBlob(_stmt, i, ref v[0], v.Length, s_transient));

    private void Check(int rc)
    {
        if (rc != 0)
        {
            throw _db.LastError(rc);
        }
    }

    /// <summary>Finalizes the statement.</summary>
    public void Dispose()
    {
        if (_stmt != IntPtr.Zero)
        {
            _ = Native.Finalize(_stmt);
            _stmt = IntPtr.Zero;
        }
    }
}
