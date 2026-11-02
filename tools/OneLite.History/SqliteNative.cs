using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RecursiveAI.OneLite.History;

/// <summary>
/// The few sqlite3 entry points the native test client needs, declared from the C API reference (sqlite.org/c3ref).
/// The library is loaded from a path given at run time; this is a test tool, not part of the shipped packages.
/// </summary>
internal static partial class SqliteNative
{
    private const string Lib = "sqlite3-under-test";
    private static string? _path;

    public const int Row = 100;
    public const int Done = 101;
    public const int OpenReadWrite = 0x2;
    public const int OpenCreate = 0x4;
    public const int OpenFullMutex = 0x10000;
    public const int ResultBusy = 5;
    public const int ResultLocked = 6;

    /// <summary>Points the P/Invoke resolver at the library file. Must be called once before any other call.</summary>
    public static void SetLibraryPath(string path)
    {
        if (_path is not null)
        {
            return;
        }

        _path = path;
        NativeLibrary.SetDllImportResolver(typeof(SqliteNative).Assembly, Resolve);
    }

    private static nint Resolve(string name, Assembly asm, DllImportSearchPath? search)
        => name == Lib && _path is not null ? NativeLibrary.Load(_path) : 0;

    [LibraryImport(Lib, EntryPoint = "sqlite3_libversion")]
    private static partial nint LibVersionRaw();

    public static string LibVersion() => Marshal.PtrToStringUTF8(LibVersionRaw()) ?? "";

    [LibraryImport(Lib, EntryPoint = "sqlite3_open_v2", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Open(string filename, out nint db, int flags, nint vfs);

    [LibraryImport(Lib, EntryPoint = "sqlite3_close_v2")]
    public static partial void Close(nint db);

    [LibraryImport(Lib, EntryPoint = "sqlite3_extended_result_codes")]
    public static partial void ExtendedResultCodes(nint db, int on);

    [LibraryImport(Lib, EntryPoint = "sqlite3_busy_timeout")]
    public static partial void BusyTimeout(nint db, int ms);

    [LibraryImport(Lib, EntryPoint = "sqlite3_exec", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Exec(nint db, string sql, nint callback, nint arg, nint errmsg);

    [LibraryImport(Lib, EntryPoint = "sqlite3_prepare_v2", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Prepare(nint db, string sql, int nByte, out nint stmt, nint tail);

    [LibraryImport(Lib, EntryPoint = "sqlite3_step")]
    public static partial int Step(nint stmt);

    [LibraryImport(Lib, EntryPoint = "sqlite3_reset")]
    public static partial int Reset(nint stmt);

    [LibraryImport(Lib, EntryPoint = "sqlite3_finalize")]
    public static partial void Finalize(nint stmt);

    [LibraryImport(Lib, EntryPoint = "sqlite3_bind_int64")]
    public static partial void BindInt64(nint stmt, int index, long value);

    [LibraryImport(Lib, EntryPoint = "sqlite3_bind_null")]
    public static partial void BindNull(nint stmt, int index);

    // SQLITE_TRANSIENT is the pointer value -1: SQLite copies the text before the call returns.
    [LibraryImport(Lib, EntryPoint = "sqlite3_bind_text", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void BindText(nint stmt, int index, string text, int nByte, nint destructor);

    [LibraryImport(Lib, EntryPoint = "sqlite3_column_type")]
    public static partial int ColumnType(nint stmt, int col);

    [LibraryImport(Lib, EntryPoint = "sqlite3_column_int64")]
    public static partial long ColumnInt64(nint stmt, int col);

    [LibraryImport(Lib, EntryPoint = "sqlite3_column_text")]
    private static partial nint ColumnTextRaw(nint stmt, int col);

    public static string? ColumnText(nint stmt, int col) => Marshal.PtrToStringUTF8(ColumnTextRaw(stmt, col));
}
