using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RecursiveAI.OneLite.Oracle;

/// <summary>
/// The pinned native SQLite library, called directly. Signatures come from the C API reference pages (c3ref). This is a
/// test oracle: it is only ever loaded from the pinned, hash-verified file, and only tool and test projects use it.
/// </summary>
internal static partial class Native
{
    internal const string Lib = "e_sqlite3";

    // c3ref/c_open_readonly: open flags and result codes used by the tools.
    internal const int OpenReadOnly = 0x01, OpenReadWrite = 0x02, OpenCreate = 0x04;
    internal const int Row = 100, Done = 101;
    // c3ref/c_blob: fundamental datatypes.
    internal const int TypeInteger = 1, TypeFloat = 2, TypeText = 3, TypeBlob = 4, TypeNull = 5;

    private static int s_resolverSet;

    internal static void EnsureLoaded()
    {
        if (System.Threading.Interlocked.Exchange(ref s_resolverSet, 1) == 0)
        {
            NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
        => name == Lib ? NativeLibrary.Load(OraclePaths.NativeLibrary) : IntPtr.Zero;

    [LibraryImport(Lib, EntryPoint = "sqlite3_libversion")] internal static partial IntPtr LibVersion();
    [LibraryImport(Lib, EntryPoint = "sqlite3_sourceid")] internal static partial IntPtr SourceId();

    [LibraryImport(Lib, EntryPoint = "sqlite3_open_v2", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Open(string filename, out IntPtr db, int flags, IntPtr vfs);

    [LibraryImport(Lib, EntryPoint = "sqlite3_close_v2")] internal static partial int Close(IntPtr db);
    [LibraryImport(Lib, EntryPoint = "sqlite3_extended_result_codes")] internal static partial int ExtendedResultCodes(IntPtr db, int on);
    [LibraryImport(Lib, EntryPoint = "sqlite3_busy_timeout")] internal static partial int BusyTimeout(IntPtr db, int ms);
    [LibraryImport(Lib, EntryPoint = "sqlite3_errmsg")] internal static partial IntPtr ErrMsg(IntPtr db);
    [LibraryImport(Lib, EntryPoint = "sqlite3_extended_errcode")] internal static partial int ExtendedErrCode(IntPtr db);
    [LibraryImport(Lib, EntryPoint = "sqlite3_free")] internal static partial void Free(IntPtr p);
    [LibraryImport(Lib, EntryPoint = "sqlite3_changes")] internal static partial int Changes(IntPtr db);

    [LibraryImport(Lib, EntryPoint = "sqlite3_exec", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Exec(IntPtr db, string sql, IntPtr callback, IntPtr arg, out IntPtr errmsg);

    [LibraryImport(Lib, EntryPoint = "sqlite3_prepare_v2", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Prepare(IntPtr db, string sql, int nByte, out IntPtr stmt, IntPtr tail);

    // The same call over raw UTF-8 bytes, so a script can be prepared one statement at a time (the tail is a pointer into the buffer).
    [LibraryImport(Lib, EntryPoint = "sqlite3_prepare_v2")]
    internal static unsafe partial int PrepareBytes(IntPtr db, byte* sql, int nByte, out IntPtr stmt, out byte* tail);

    [LibraryImport(Lib, EntryPoint = "sqlite3_step")] internal static partial int Step(IntPtr stmt);
    [LibraryImport(Lib, EntryPoint = "sqlite3_reset")] internal static partial int Reset(IntPtr stmt);
    [LibraryImport(Lib, EntryPoint = "sqlite3_finalize")] internal static partial int Finalize(IntPtr stmt);
    [LibraryImport(Lib, EntryPoint = "sqlite3_column_count")] internal static partial int ColumnCount(IntPtr stmt);
    [LibraryImport(Lib, EntryPoint = "sqlite3_column_name")] internal static partial IntPtr ColumnName(IntPtr stmt, int i);
    [LibraryImport(Lib, EntryPoint = "sqlite3_column_type")] internal static partial int ColumnType(IntPtr stmt, int i);
    [LibraryImport(Lib, EntryPoint = "sqlite3_column_int64")] internal static partial long ColumnInt64(IntPtr stmt, int i);
    [LibraryImport(Lib, EntryPoint = "sqlite3_column_double")] internal static partial double ColumnDouble(IntPtr stmt, int i);
    [LibraryImport(Lib, EntryPoint = "sqlite3_column_blob")] internal static partial IntPtr ColumnBlob(IntPtr stmt, int i);
    [LibraryImport(Lib, EntryPoint = "sqlite3_column_text")] internal static partial IntPtr ColumnText(IntPtr stmt, int i);
    [LibraryImport(Lib, EntryPoint = "sqlite3_column_bytes")] internal static partial int ColumnBytes(IntPtr stmt, int i);

    [LibraryImport(Lib, EntryPoint = "sqlite3_bind_null")] internal static partial int BindNull(IntPtr stmt, int i);
    [LibraryImport(Lib, EntryPoint = "sqlite3_bind_int64")] internal static partial int BindInt64(IntPtr stmt, int i, long v);
    [LibraryImport(Lib, EntryPoint = "sqlite3_bind_double")] internal static partial int BindDouble(IntPtr stmt, int i, double v);
    // The last argument is SQLITE_TRANSIENT (-1): SQLite copies the bytes before the call returns.
    [LibraryImport(Lib, EntryPoint = "sqlite3_bind_blob")] internal static partial int BindBlob(IntPtr stmt, int i, ref byte data, int n, IntPtr destructor);
    [LibraryImport(Lib, EntryPoint = "sqlite3_bind_text")] internal static partial int BindText(IntPtr stmt, int i, ref byte data, int n, IntPtr destructor);

    internal static string? Utf8(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
}
