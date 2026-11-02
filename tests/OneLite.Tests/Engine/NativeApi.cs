using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>The pinned native library's backup and incremental-blob calls, used as the oracle for result codes and messages (c3ref pages).</summary>
internal static partial class NativeApi
{
    private const string Lib = "e_sqlite3";
    private static int s_set;

    internal static bool Available => File.Exists(OraclePaths.NativeLibrary);

    internal static void Ensure()
    {
        if (System.Threading.Interlocked.Exchange(ref s_set, 1) == 0)
        {
            NativeLibrary.SetDllImportResolver(typeof(NativeApi).Assembly, Resolve);
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
        => name == Lib ? NativeLibrary.Load(OraclePaths.NativeLibrary) : IntPtr.Zero;

    [LibraryImport(Lib, EntryPoint = "sqlite3_open_v2", StringMarshalling = StringMarshalling.Utf8)] internal static partial int Open(string file, out IntPtr db, int flags, IntPtr vfs);
    [LibraryImport(Lib, EntryPoint = "sqlite3_close_v2")] internal static partial int Close(IntPtr db);
    [LibraryImport(Lib, EntryPoint = "sqlite3_exec", StringMarshalling = StringMarshalling.Utf8)] internal static partial int Exec(IntPtr db, string sql, IntPtr cb, IntPtr arg, out IntPtr err);
    [LibraryImport(Lib, EntryPoint = "sqlite3_free")] internal static partial void Free(IntPtr p);
    [LibraryImport(Lib, EntryPoint = "sqlite3_errmsg")] private static partial IntPtr ErrMsgPtr(IntPtr db);
    [LibraryImport(Lib, EntryPoint = "sqlite3_extended_errcode")] internal static partial int ExtErr(IntPtr db);
    [LibraryImport(Lib, EntryPoint = "sqlite3_extended_result_codes")] internal static partial int ExtendedCodes(IntPtr db, int on);
    [LibraryImport(Lib, EntryPoint = "sqlite3_backup_init", StringMarshalling = StringMarshalling.Utf8)] internal static partial IntPtr BackupInit(IntPtr dest, string destName, IntPtr src, string srcName);
    [LibraryImport(Lib, EntryPoint = "sqlite3_backup_step")] internal static partial int BackupStep(IntPtr b, int n);
    [LibraryImport(Lib, EntryPoint = "sqlite3_backup_finish")] internal static partial int BackupFinish(IntPtr b);
    [LibraryImport(Lib, EntryPoint = "sqlite3_backup_remaining")] internal static partial int BackupRemaining(IntPtr b);
    [LibraryImport(Lib, EntryPoint = "sqlite3_backup_pagecount")] internal static partial int BackupPageCount(IntPtr b);
    [LibraryImport(Lib, EntryPoint = "sqlite3_blob_open", StringMarshalling = StringMarshalling.Utf8)] internal static partial int BlobOpen(IntPtr db, string schema, string table, string column, long rowid, int flags, out IntPtr blob);
    [LibraryImport(Lib, EntryPoint = "sqlite3_blob_close")] internal static partial int BlobClose(IntPtr blob);
    [LibraryImport(Lib, EntryPoint = "sqlite3_blob_bytes")] internal static partial int BlobBytes(IntPtr blob);
    [LibraryImport(Lib, EntryPoint = "sqlite3_blob_read")] internal static partial int BlobRead(IntPtr blob, ref byte buf, int n, int offset);
    [LibraryImport(Lib, EntryPoint = "sqlite3_blob_write")] internal static partial int BlobWrite(IntPtr blob, ref byte buf, int n, int offset);
    [LibraryImport(Lib, EntryPoint = "sqlite3_blob_reopen")] internal static partial int BlobReopen(IntPtr blob, long rowid);

    internal static string ErrMsg(IntPtr db) => Marshal.PtrToStringUTF8(ErrMsgPtr(db)) ?? "";

    internal static IntPtr OpenRw(string path)
    {
        Ensure();
        Assert.Equal(0, Open(path, out IntPtr db, 0x46, IntPtr.Zero));
        _ = ExtendedCodes(db, 1);
        return db;
    }

    internal static void Run(IntPtr db, string sql)
    {
        int rc = Exec(db, sql, IntPtr.Zero, IntPtr.Zero, out IntPtr err);
        Assert.True(rc == 0, sql + " -> " + rc + " " + ErrMsg(db));
        if (err != IntPtr.Zero)
        {
            Free(err);
        }
    }
}
