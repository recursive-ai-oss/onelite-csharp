using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.SqlCompare;

/// <summary>How the pinned library classified one statement text.</summary>
public enum PrepClass
{
    /// <summary>It prepared, or the text held no statement.</summary>
    Prepares,

    /// <summary>A tokenizer or grammar error: <c>near "X": syntax error</c>, <c>incomplete input</c>, <c>unrecognized token</c>, or another error the grammar's own actions raise.</summary>
    Syntax,

    /// <summary>It parsed, then failed (name resolution, semantic checks).</summary>
    Other,
}

/// <summary>The outcome of <c>sqlite3_prepare_v2</c> on the first statement of a text (c3ref/prepare, c3ref/errcode).</summary>
public readonly record struct PrepResult(PrepClass Class, int Code, int Offset, string Message, int TailOffset);

/// <summary>Prepares statements against an empty in-memory database of the pinned native library.</summary>
public sealed unsafe partial class Prepared : IDisposable
{
    private const string Lib = "e_sqlite3";
    private static int s_init;
    private IntPtr _db;

    internal static void EnsureResolver() => Init();

    private static void Init()
    {
        if (System.Threading.Interlocked.Exchange(ref s_init, 1) == 0)
        {
            NativeLibrary.SetDllImportResolver(typeof(Prepared).Assembly, (name, _, _) => name == Lib ? NativeLibrary.Load(OraclePaths.NativeLibrary) : IntPtr.Zero);
        }
    }

    [LibraryImport(Lib, EntryPoint = "sqlite3_open_v2", StringMarshalling = StringMarshalling.Utf8)] private static partial int Open(string f, out IntPtr db, int flags, IntPtr vfs);
    [LibraryImport(Lib, EntryPoint = "sqlite3_close_v2")] private static partial int Close(IntPtr db);
    [LibraryImport(Lib, EntryPoint = "sqlite3_prepare_v2")] private static partial int PrepareV2(IntPtr db, byte* sql, int n, out IntPtr stmt, out byte* tail);
    [LibraryImport(Lib, EntryPoint = "sqlite3_finalize")] private static partial int Finalize(IntPtr stmt);
    [LibraryImport(Lib, EntryPoint = "sqlite3_step")] private static partial int Step(IntPtr stmt);
    [LibraryImport(Lib, EntryPoint = "sqlite3_column_count")] private static partial int ColumnCount(IntPtr stmt);
    [LibraryImport(Lib, EntryPoint = "sqlite3_column_type")] private static partial int ColumnType(IntPtr stmt, int i);
    [LibraryImport(Lib, EntryPoint = "sqlite3_column_text")] private static partial IntPtr ColumnText(IntPtr stmt, int i);
    [LibraryImport(Lib, EntryPoint = "sqlite3_errmsg")] private static partial IntPtr ErrMsg(IntPtr db);
    [LibraryImport(Lib, EntryPoint = "sqlite3_extended_errcode")] private static partial int ExtErr(IntPtr db);
    [LibraryImport(Lib, EntryPoint = "sqlite3_error_offset")] private static partial int ErrOffset(IntPtr db);
    [LibraryImport(Lib, EntryPoint = "sqlite3_exec", StringMarshalling = StringMarshalling.Utf8)] private static partial int Exec(IntPtr db, string sql, IntPtr cb, IntPtr arg, IntPtr err);
    [LibraryImport(Lib, EntryPoint = "sqlite3_libversion")] private static partial IntPtr LibVersion();
    [LibraryImport(Lib, EntryPoint = "sqlite3_db_config")] private static partial int DbConfig(IntPtr db, int op, int onoff, IntPtr outp);

    /// <summary>Opens a private in-memory database.</summary>
    public Prepared()
    {
        Init();
        if (Open(":memory:", out _db, 0x06, IntPtr.Zero) != 0)
        {
            throw new InvalidOperationException("cannot open the native library");
        }

        // The pinned shell is built with DQS=0; the library defaults differ, and the setting only moves errors after parsing.
        _ = DbConfig(_db, 1013, 0, IntPtr.Zero);
        _ = DbConfig(_db, 1014, 0, IntPtr.Zero);
        _ = Exec(_db, "create table t(a,b,c); create table u(a,b,c); create view v as select * from t;", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>The library version string.</summary>
    public static string Version { get { Init(); return Marshal.PtrToStringUTF8(LibVersion())!; } }

    /// <summary>Runs the statement text (UTF-8) through prepare and classifies the result.</summary>
    public PrepResult Prepare(ReadOnlySpan<byte> utf8, int start = 0)
    {
        byte[] buf = new byte[utf8.Length + 1];
        utf8.CopyTo(buf);
        fixed (byte* p = buf)
        {
            int rc = PrepareV2(_db, p + start, utf8.Length - start, out IntPtr stmt, out byte* tail);
            int tailOff = (int)(tail - p);
            if (stmt != IntPtr.Zero)
            {
                _ = Finalize(stmt);
            }

            if (rc == 0)
            {
                return new PrepResult(PrepClass.Prepares, 0, -1, "", tailOff);
            }

            string msg = Marshal.PtrToStringUTF8(ErrMsg(_db))!;
            int off = ErrOffset(_db);
            bool syntax = msg.StartsWith("near \"", StringComparison.Ordinal) && msg.EndsWith("\": syntax error", StringComparison.Ordinal)
                || msg == "incomplete input" || msg.StartsWith("unrecognized token: ", StringComparison.Ordinal)
                || ParseTimeMessage(msg);
            return new PrepResult(syntax ? PrepClass.Syntax : PrepClass.Other, ExtErr(_db), off, msg, tailOff);
        }
    }

    /// <summary>
    /// Runs a statement that reads no tables and returns its first row as <c>type:text</c> per column, or <c>error: message</c>, or <c>none</c> when
    /// it prepares to nothing or returns no row. Used to check that two spellings of an expression mean the same.
    /// </summary>
    public string Eval(string sql)
    {
        byte[] u = Encoding.UTF8.GetBytes(sql);
        byte[] buf = new byte[u.Length + 1];
        u.CopyTo(buf, 0);
        fixed (byte* p = buf)
        {
            int rc = PrepareV2(_db, p, u.Length, out IntPtr stmt, out _);
            if (rc != 0)
            {
                return "error: " + Marshal.PtrToStringUTF8(ErrMsg(_db));
            }

            if (stmt == IntPtr.Zero)
            {
                return "none";
            }

            try
            {
                rc = Step(stmt);
                if (rc == 100)
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < ColumnCount(stmt); i++)
                    {
                        sb.Append(ColumnType(stmt, i)).Append(':').Append(Marshal.PtrToStringUTF8(ColumnText(stmt, i))).Append(';');
                    }

                    return sb.ToString();
                }

                return rc == 101 ? "none" : "error: " + Marshal.PtrToStringUTF8(ErrMsg(_db));
            }
            finally
            {
                _ = Finalize(stmt);
            }
        }
    }

    /// <summary>Convenience over a string.</summary>
    public PrepResult Prepare(string sql) => Prepare(Encoding.UTF8.GetBytes(sql));

    // Messages the grammar's own actions raise before the statement is complete; filled in from black-box probing.
    private static bool ParseTimeMessage(string msg) => ParseTimeMessages.Is(msg);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_db != IntPtr.Zero)
        {
            _ = Close(_db);
            _db = IntPtr.Zero;
        }
    }
}

/// <summary>The messages that count as parse-time errors even though they are not syntax errors.</summary>
internal static class ParseTimeMessages
{
    internal static bool Is(string msg)
        => msg.StartsWith("Expression tree is too large", StringComparison.Ordinal)
        || msg.StartsWith("too many terms in compound SELECT", StringComparison.Ordinal)
        || msg.StartsWith("too many SQL variables", StringComparison.Ordinal)
        || msg.StartsWith("variable number must be between", StringComparison.Ordinal)
        || msg == "statement too long";
}
