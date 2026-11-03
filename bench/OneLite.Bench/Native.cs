using System.Reflection;
using System.Runtime.InteropServices;

namespace RecursiveAI.OneLite.Bench;

/// <summary>
/// Raw P/Invoke of the pinned native SQLite library, used only by the engine rows: it is the reference side of the
/// "Statement.Step against sqlite3_step" comparison. The library path comes from the command line, so nothing here
/// depends on where the library is installed.
/// </summary>
internal static partial class Native
{
    private const string Lib = "bench_native_sqlite";
    public const int Ok = 0;
    public const int Row = 100;
    public const int Done = 101;
    public const int OpenReadWrite = 2;
    public const int OpenCreate = 4;

    private static string? s_path;

    /// <summary>Points the P/Invoke resolver at the library file. Call once before the first native call.</summary>
    public static void Configure(string path)
    {
        s_path = path;
        NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);
    }

    private static nint Resolve(string name, Assembly asm, DllImportSearchPath? search)
        => name == Lib && s_path is not null ? NativeLibrary.Load(s_path) : 0;

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int sqlite3_open_v2(string file, out nint db, int flags, nint vfs);

    [LibraryImport(Lib)]
    public static partial int sqlite3_close_v2(nint db);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int sqlite3_prepare_v2(nint db, string sql, int n, out nint stmt, nint tail);

    [LibraryImport(Lib)]
    public static partial int sqlite3_step(nint stmt);

    [LibraryImport(Lib)]
    public static partial int sqlite3_reset(nint stmt);

    [LibraryImport(Lib)]
    public static partial int sqlite3_finalize(nint stmt);

    [LibraryImport(Lib)]
    public static partial int sqlite3_bind_int64(nint stmt, int i, long v);

    [LibraryImport(Lib)]
    public static partial int sqlite3_bind_double(nint stmt, int i, double v);

    [LibraryImport(Lib)]
    public static partial int sqlite3_bind_text(nint stmt, int i, ReadOnlySpan<byte> v, int n, nint destructor);

    [LibraryImport(Lib)]
    public static partial int sqlite3_bind_blob(nint stmt, int i, ReadOnlySpan<byte> v, int n, nint destructor);

    [LibraryImport(Lib)]
    public static partial long sqlite3_column_int64(nint stmt, int i);

    [LibraryImport(Lib)]
    public static partial double sqlite3_column_double(nint stmt, int i);

    [LibraryImport(Lib)]
    public static partial nint sqlite3_column_text(nint stmt, int i);

    [LibraryImport(Lib)]
    public static partial int sqlite3_column_bytes(nint stmt, int i);

    [LibraryImport(Lib)]
    public static partial nint sqlite3_errmsg(nint db);

    [LibraryImport(Lib)]
    public static partial nint sqlite3_libversion();

    [LibraryImport(Lib)]
    public static partial int sqlite3_enable_load_extension(nint db, int on);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int sqlite3_load_extension(nint db, string file, string? entry, nint errmsg);

    [LibraryImport("libc")]
    public static partial int getrusage(int who, [In, Out] long[] usage);

    public static string Version() => Marshal.PtrToStringUTF8(sqlite3_libversion()) ?? "?";

    public static string Error(nint db) => Marshal.PtrToStringUTF8(sqlite3_errmsg(db)) ?? "?";
}
