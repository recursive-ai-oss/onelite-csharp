using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace RecursiveAI.OneLite.SQLitePCLRaw;

/// <summary>
/// SQLitePCLRaw hands the provider native-style pointers (<see cref="IntPtr"/>) for connections, statements, backups and blobs.
/// This table gives every managed object a distinct non-zero pointer value and finds it again; nothing here is a real address.
/// </summary>
internal static class Handles
{
    private static readonly ConcurrentDictionary<nint, object> s_objects = new();
    private static long s_next;

    internal static nint Add(object o)
    {
        nint id = (nint)(Interlocked.Increment(ref s_next) << 4);
        s_objects[id] = o;
        return id;
    }

    internal static T? Get<T>(nint id) where T : class => id != 0 && s_objects.TryGetValue(id, out object? o) ? o as T : null;

    internal static void Remove(nint id) => s_objects.TryRemove(id, out _);
}

/// <summary>The state SQLite keeps in a <c>sqlite3*</c>: the connection, the last error, hooks, limits and the statements still open.</summary>
internal sealed class DbState
{
    internal nint Id;
    internal Database? Database;
    internal Connection? Connection;
    internal string Path = string.Empty;
    internal bool ReadOnly;
    internal bool Memory;
    internal bool ExtensionsEnabled;
    internal bool Extended;
    internal bool Closed;
    internal string? SharedKey;
    internal int Backups;
    internal int Blobs;
    internal int Code;
    internal int ExtCode;
    internal string? Message;
    internal int ErrorOffset = -1;
    internal readonly List<StmtState> Statements = [];
    internal readonly int[] Limits = DefaultLimits();

    // Application callbacks.
    internal SQLitePCL.delegate_progress? Progress;
    internal object? ProgressUser;
    internal int ProgressEvery;

    /// <summary>The connection, or the SQLITE_MISUSE a closed one gives.</summary>
    internal Connection Conn => Connection ?? throw new OneLiteException(ResultCode.Misuse, "bad parameter or other API misuse");

    private static int[] DefaultLimits() =>
    [
        1_000_000_000, // SQLITE_LIMIT_LENGTH
        1_000_000_000, // SQL_LENGTH
        2000,          // COLUMN
        1000,          // EXPR_DEPTH
        500,           // COMPOUND_SELECT
        250_000_000,   // VDBE_OP
        1000,          // FUNCTION_ARG
        10,            // ATTACHED
        50_000,        // LIKE_PATTERN_LENGTH
        32766,         // VARIABLE_NUMBER
        1000,          // TRIGGER_DEPTH
        0,             // WORKER_THREADS
    ];

    /// <summary>The compile-time maximum of a limit category; <c>sqlite3_limit</c> never raises a limit above it.</summary>
    internal static int HardLimit(int id) => id switch
    {
        0 or 1 => 1_000_000_000,
        2 => 2000,
        3 => 1000,
        4 => 500,
        5 => 250_000_000,
        6 => 1000,
        7 => 10,
        8 => 50_000,
        9 => 32766,
        10 => 1000,
        _ => 8,
    };

    internal static string QuoteIdentifier(string s) => "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>Records success: errcode 0, "not an error".</summary>
    internal int Ok()
    {
        Code = 0;
        ExtCode = 0;
        Message = null;
        ErrorOffset = -1;
        return 0;
    }

    /// <summary>Records a failure and returns the code the caller sees (the extended code only when the application asked for them).</summary>
    internal int Fail(int extendedCode, string message, int errorOffset = -1)
    {
        ExtCode = extendedCode;
        Code = extendedCode & 0xFF;
        Message = message;
        ErrorOffset = errorOffset;
        return Extended ? extendedCode : Code;
    }

    internal int Fail(Exception e)
    {
        switch (e)
        {
            case OneLiteException oe:
                return Fail(oe.ExtendedCode, oe.Message, oe.ErrorOffset);
            case OperationCanceledException:
                return Fail(ResultCode.Interrupt, "interrupted");
            case ObjectDisposedException:
                return Fail(ResultCode.Misuse, "bad parameter or other API misuse");
            case Storage.Vfs.VfsException ve:
                return Fail(ve.ExtendedCode, ve.Message);
            default:
                return Fail(ResultCode.Error, e.Message);
        }
    }

    /// <summary>The value a call returns for a result that was recorded already.</summary>
    internal int Return(int extendedCode) => Extended ? extendedCode : extendedCode & 0xFF;

    internal static string QuoteLiteral(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";

    internal static string Int(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>Runs a statement that returns no rows or ignores them (a PRAGMA used as a setter).</summary>
    internal void Exec(string sql)
    {
        using Statement st = Conn.Prepare(sql);
        while (st.Step())
        {
        }
    }

    /// <summary>Runs a statement and returns its first column of the first row as text, or null.</summary>
    internal string? Scalar(string sql)
    {
        using Statement st = Conn.Prepare(sql);
        return st.Step() ? st.GetString(0) : null;
    }

    internal static string Utf8(ReadOnlySpan<byte> s)
    {
        int nul = s.IndexOf((byte)0);
        return Encoding.UTF8.GetString(nul >= 0 ? s[..nul] : s);
    }
}

/// <summary>The state SQLite keeps in a <c>sqlite3_stmt*</c>.</summary>
internal sealed class StmtState
{
    internal nint Id;
    internal DbState Db = null!;
    internal Statement Statement = null!;
    internal string Sql = string.Empty;

    /// <summary>A row is current (<c>sqlite3_data_count</c> is the column count).</summary>
    internal bool HasRow;

    /// <summary>The statement was stepped and has neither finished nor been reset (<c>sqlite3_stmt_busy</c>).</summary>
    internal bool Active;

    /// <summary>The extended code of the last failed step; <c>sqlite3_reset</c> and <c>sqlite3_finalize</c> report it.</summary>
    internal int StepError;

    internal string? StepMessage;
    internal bool Finalized;

    /// <summary>One reusable zero-terminated copy of the text per result column, for <c>sqlite3_column_text</c>.</summary>
    internal byte[]?[] TextBuffers = [];
}

/// <summary>SQLitePCLRaw's <c>utf8z</c> wraps a zero-terminated byte sequence; these build one over managed memory.</summary>
internal static class Utf8Z
{
    /// <summary>A zero-terminated copy of <paramref name="payload"/> as a byte array (the terminator is the last byte).</summary>
    internal static byte[] Terminated(ReadOnlySpan<byte> payload)
    {
        var buf = new byte[payload.Length + 1];
        payload.CopyTo(buf);
        return buf;
    }

    /// <summary>A zero-terminated copy of <paramref name="payload"/>.</summary>
    internal static SQLitePCL.utf8z Copy(ReadOnlySpan<byte> payload)
    {
        var buf = new byte[payload.Length + 1];
        payload.CopyTo(buf);
        return SQLitePCL.utf8z.FromSpan(buf);
    }

    /// <summary>As <see cref="Copy"/>, reusing <paramref name="buffer"/> when it is big enough. The result is valid until the buffer is used again.</summary>
    internal static SQLitePCL.utf8z Reuse(ref byte[]? buffer, ReadOnlySpan<byte> payload)
    {
        if (buffer is null || buffer.Length < payload.Length + 1)
        {
            buffer = new byte[Math.Max(payload.Length + 1, 32)];
        }

        payload.CopyTo(buffer);
        buffer[payload.Length] = 0;
        return SQLitePCL.utf8z.FromSpan(new ReadOnlySpan<byte>(buffer, 0, payload.Length + 1));
    }
}
