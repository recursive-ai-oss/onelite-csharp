using System;
using System.Threading.Tasks;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;
using RecursiveAI.OneLite.Storage.Vfs.Browser;

namespace RecursiveAI.OneLite.BrowserTests;

/// <summary>The VFS behaviour that applies to the OPFS backends: read, write, truncate, size, sync, try-locks, the driver rules of docs/vfs.md.</summary>
internal static class VfsTests
{
    private const VfsOpenOptions CreateDb = VfsOpenOptions.ReadWrite | VfsOpenOptions.Create | VfsOpenOptions.MainDb;

    public static void Register()
    {
        foreach ((string kind, string where) in new[] { ("async", "any"), ("sync", "worker") })
        {
            string k = kind;
            void Add(string name, Func<Backend, Task> body) =>
                Registry.Add($"{k}.vfs.{name}", where, async () =>
                {
                    using Backend b = await Backend.Create(k).ConfigureAwait(false);
                    await body(b).ConfigureAwait(false);
                });

            Add("read-write", ReadWrite);
            Add("write-past-end-zero-fills", GapFill);
            Add("truncate", Truncate);
            Add("sync-and-reopen", SyncReopen);
            Add("write-run", WriteRun);
            Add("large-write", LargeWrite);
            Add("open-access-delete", OpenAccessDelete);
            Add("read-only-open", ReadOnlyOpen);
            Add("nested-directories", Nested);
            Add("temp-files-in-memory", TempFiles);
            Add("lock-levels", LockLevels);
            Add("check-reserved-lock", CheckReserved);
            Add("driver-rules", DriverRules);
        }

        // The sync backend's own rules and the second flavour of locks.
        Registry.Add("sync.vfs.sync-calls-inline", "worker", SyncCallsInline);
        Registry.Add("sync.vfs.second-prepare-after-release", "worker", PrepareAfterRelease);
        Registry.Add("async.vfs.sync-driver-refused", "any", AsyncRefusesSyncPager);
    }

    private static byte[] Pattern(int length, int seed)
    {
        byte[] b = new byte[length];
        for (int i = 0; i < length; i++)
        {
            b[i] = (byte)((i * 31 + seed * 17 + (i >> 8)) & 0xFF);
        }

        return b;
    }

    private static async Task<byte[]> ReadAll(IVfsFile f, long offset, int length)
    {
        byte[] buf = new byte[length];
        int n = await f.ReadAsync(offset, buf).ConfigureAwait(false);
        return buf.AsMemory(0, n).ToArray();
    }

    private static async Task<IVfsFile> Create(Backend b, string path) => await b.Vfs.OpenAsync(path, CreateDb).ConfigureAwait(false);

    private static async Task Close(Backend b, IVfsFile f)
    {
        f.Dispose();
        await b.Settle().ConfigureAwait(false);
    }

    private static async Task ReadWrite(Backend b)
    {
        string path = Backend.Path("rw");
        IVfsFile f = await Create(b, path).ConfigureAwait(false);
        byte[] a = Pattern(4096, 1), c = Pattern(1000, 2);
        await f.WriteAsync(0, a).ConfigureAwait(false);
        await f.WriteAsync(4096, c).ConfigureAwait(false);
        Check.Equal(5096L, await f.FileSizeAsync().ConfigureAwait(false), "size after two writes");
        Check.BytesEqual(a, await ReadAll(f, 0, 4096).ConfigureAwait(false), "first block");
        Check.BytesEqual(c, await ReadAll(f, 4096, 1000).ConfigureAwait(false), "second block");
        byte[] tail = await ReadAll(f, 4000, 2000).ConfigureAwait(false);
        Check.Equal(1096, tail.Length, "short read at the end of the file");
        Check.BytesEqual(a.AsSpan(4000), tail.AsSpan(0, 96), "short read first part");
        Check.Equal(0, (await ReadAll(f, 6000, 10).ConfigureAwait(false)).Length, "read past the end");
        await f.WriteAsync(100, Pattern(50, 9)).ConfigureAwait(false);
        Check.BytesEqual(Pattern(50, 9), await ReadAll(f, 100, 50).ConfigureAwait(false), "overwrite in the middle");
        Check.Equal(5096L, await f.FileSizeAsync().ConfigureAwait(false), "size unchanged by overwrite");
        await Close(b, f).ConfigureAwait(false);
    }

    private static async Task GapFill(Backend b)
    {
        IVfsFile f = await Create(b, Backend.Path("gap")).ConfigureAwait(false);
        await f.WriteAsync(10000, Pattern(100, 3)).ConfigureAwait(false);
        Check.Equal(10100L, await f.FileSizeAsync().ConfigureAwait(false), "size");
        byte[] gap = await ReadAll(f, 0, 10000).ConfigureAwait(false);
        Check.Equal(10000, gap.Length, "gap length");
        Check.True(Array.TrueForAll(gap, x => x == 0), "the gap reads as zeros");
        await Close(b, f).ConfigureAwait(false);
    }

    private static async Task Truncate(Backend b)
    {
        IVfsFile f = await Create(b, Backend.Path("trunc")).ConfigureAwait(false);
        await f.WriteAsync(0, Pattern(8192, 4)).ConfigureAwait(false);
        await f.TruncateAsync(100).ConfigureAwait(false);
        Check.Equal(100L, await f.FileSizeAsync().ConfigureAwait(false), "size after shrinking");
        await f.TruncateAsync(5000).ConfigureAwait(false);
        Check.Equal(5000L, await f.FileSizeAsync().ConfigureAwait(false), "size after growing");
        byte[] all = await ReadAll(f, 0, 5000).ConfigureAwait(false);
        Check.BytesEqual(Pattern(100, 4), all.AsSpan(0, 100), "kept bytes");
        Check.True(Array.TrueForAll(all[100..], x => x == 0), "grown bytes are zeros");
        await f.TruncateAsync(0).ConfigureAwait(false);
        Check.Equal(0L, await f.FileSizeAsync().ConfigureAwait(false), "size after emptying");
        await Close(b, f).ConfigureAwait(false);
    }

    private static async Task SyncReopen(Backend b)
    {
        string path = Backend.Path("reopen");
        IVfsFile f = await Create(b, path).ConfigureAwait(false);
        byte[] data = Pattern(20000, 5);
        await f.WriteAsync(0, data).ConfigureAwait(false);
        await f.SyncAsync(SyncMode.Full).ConfigureAwait(false);
        await Close(b, f).ConfigureAwait(false);
        IVfsFile g = await b.Vfs.OpenAsync(path, VfsOpenOptions.ReadWrite | VfsOpenOptions.MainDb).ConfigureAwait(false);
        Check.Equal(20000L, await g.FileSizeAsync().ConfigureAwait(false), "size after reopen");
        Check.BytesEqual(data, await ReadAll(g, 0, 20000).ConfigureAwait(false), "content after reopen");
        await Close(b, g).ConfigureAwait(false);
    }

    private static async Task WriteRun(Backend b)
    {
        IVfsFile f = await Create(b, Backend.Path("run")).ConfigureAwait(false);
        ReadOnlyMemory<byte>[] pages = [Pattern(1024, 1), Pattern(1024, 2), Pattern(1024, 3)];
        await f.WriteRunAsync(2048, pages).ConfigureAwait(false);
        Check.Equal(5120L, await f.FileSizeAsync().ConfigureAwait(false), "size after a run");
        for (int i = 0; i < 3; i++)
        {
            Check.BytesEqual(pages[i].ToArray(), await ReadAll(f, 2048 + (i * 1024), 1024).ConfigureAwait(false), $"page {i} of the run");
        }

        await Close(b, f).ConfigureAwait(false);
    }

    private static async Task LargeWrite(Backend b)
    {
        IVfsFile f = await Create(b, Backend.Path("large")).ConfigureAwait(false);
        byte[] data = Pattern(3 * 1024 * 1024 + 17, 6);
        await f.WriteAsync(0, data).ConfigureAwait(false);
        Check.BytesEqual(data, await ReadAll(f, 0, data.Length).ConfigureAwait(false), "3 MiB round trip");
        await Close(b, f).ConfigureAwait(false);
    }

    private static async Task OpenAccessDelete(Backend b)
    {
        string path = Backend.Path("rules");
        Check.True(!await b.Vfs.AccessAsync(path, AccessMode.Exists).ConfigureAwait(false), "a new path does not exist");
        VfsException e = await Check.Throws<VfsException>(async () => (await b.Vfs.OpenAsync(path, VfsOpenOptions.ReadWrite | VfsOpenOptions.MainDb).ConfigureAwait(false)).Dispose(), "open without Create").ConfigureAwait(false);
        Check.Equal(VfsCodes.CantOpen, e.Code, "open of a missing file is CANTOPEN");
        await b.Settle().ConfigureAwait(false);

        IVfsFile f = await Create(b, path).ConfigureAwait(false);
        await f.WriteAsync(0, Pattern(10, 1)).ConfigureAwait(false);
        await f.SyncAsync(SyncMode.Normal).ConfigureAwait(false);
        Check.True(await b.Vfs.AccessAsync(path, AccessMode.Exists).ConfigureAwait(false), "it exists once written");
        e = await Check.Throws<VfsException>(async () => (await b.Vfs.OpenAsync(path, CreateDb | VfsOpenOptions.Exclusive).ConfigureAwait(false)).Dispose(), "exclusive create of an existing file").ConfigureAwait(false);
        Check.Equal(VfsCodes.CantOpen, e.Code, "exclusive create of an existing file is CANTOPEN");
        await Close(b, f).ConfigureAwait(false);

        await b.Vfs.DeleteAsync(path, true).ConfigureAwait(false);
        Check.True(!await b.Vfs.AccessAsync(path, AccessMode.Exists).ConfigureAwait(false), "deleted");
        e = await Check.Throws<VfsException>(() => b.Vfs.DeleteAsync(path, false).AsTask(), "delete of a missing file").ConfigureAwait(false);
        Check.Equal(VfsCodes.IoErrDeleteNoEnt, e.ExtendedCode, "delete of a missing file is IOERR_DELETE_NOENT");
    }

    private static async Task ReadOnlyOpen(Backend b)
    {
        string path = Backend.Path("ro");
        IVfsFile f = await Create(b, path).ConfigureAwait(false);
        await f.WriteAsync(0, Pattern(100, 1)).ConfigureAwait(false);
        await f.SyncAsync(SyncMode.Normal).ConfigureAwait(false);
        await Close(b, f).ConfigureAwait(false);
        IVfsFile r = await b.Vfs.OpenAsync(path, VfsOpenOptions.ReadOnly | VfsOpenOptions.MainDb).ConfigureAwait(false);
        Check.True((r.Caps & VfsCaps.ReadOnly) != 0, "read-only capability");
        Check.BytesEqual(Pattern(100, 1), await ReadAll(r, 0, 100).ConfigureAwait(false), "read-only read");
        VfsException e = await Check.Throws<VfsException>(() => r.WriteAsync(0, Pattern(1, 1)).AsTask(), "write on a read-only file").ConfigureAwait(false);
        Check.Equal(VfsCodes.ReadOnly, e.Code, "write on a read-only file is READONLY");
        Check.True(await r.LockAsync(LockLevel.Shared).ConfigureAwait(false), "a read-only file takes a shared lock");
        Check.True(!await r.LockAsync(LockLevel.Reserved).ConfigureAwait(false), "a read-only file refuses RESERVED");
        await r.UnlockAsync(LockLevel.None).ConfigureAwait(false);
        await Close(b, r).ConfigureAwait(false);
    }

    private static async Task Nested(Backend b)
    {
        string path = $"deep{Environment.TickCount64 % 100000}/a/b/c.db";
        IVfsFile f = await Create(b, path).ConfigureAwait(false);
        await f.WriteAsync(0, Pattern(300, 1)).ConfigureAwait(false);
        await f.SyncAsync(SyncMode.Normal).ConfigureAwait(false);
        await Close(b, f).ConfigureAwait(false);
        string listing = await BrowserStorage.ListAsync(path[..path.LastIndexOf('/')]).ConfigureAwait(false);
        Check.True(listing.Contains("c.db:300", StringComparison.Ordinal), "the file is in its directory (" + listing.Replace('\n', ' ') + ")");
    }

    private static async Task TempFiles(Backend b)
    {
        IVfsFile t = await b.Vfs.OpenAsync("", VfsOpenOptions.ReadWrite | VfsOpenOptions.Create | VfsOpenOptions.TempDb | VfsOpenOptions.DeleteOnClose).ConfigureAwait(false);
        await t.WriteAsync(0, Pattern(500, 1)).ConfigureAwait(false);
        Check.BytesEqual(Pattern(500, 1), await ReadAll(t, 0, 500).ConfigureAwait(false), "temp file content");
        t.Dispose();
    }

    /// <summary>lockingv3 §2.0 and §3.0, in one context: SHARED is shared, one RESERVED, PENDING keeps readers out, EXCLUSIVE needs the readers gone.</summary>
    private static async Task LockLevels(Backend b)
    {
        string path = Backend.Path("locks");
        IVfsFile a = await Create(b, path).ConfigureAwait(false);
        IVfsFile c = await b.Vfs.OpenAsync(path, VfsOpenOptions.ReadWrite | VfsOpenOptions.MainDb).ConfigureAwait(false);
        await a.WriteAsync(0, Pattern(10, 1)).ConfigureAwait(false);

        Check.True(await a.LockAsync(LockLevel.Shared).ConfigureAwait(false), "A SHARED");
        Check.True(await c.LockAsync(LockLevel.Shared).ConfigureAwait(false), "C SHARED beside A");
        Check.True(await a.LockAsync(LockLevel.Reserved).ConfigureAwait(false), "A RESERVED");
        Check.True(!await c.LockAsync(LockLevel.Reserved).ConfigureAwait(false), "C cannot also be RESERVED");
        Check.True(await a.CheckReservedLockAsync().ConfigureAwait(false), "A sees its own RESERVED");
        Check.True(await c.CheckReservedLockAsync().ConfigureAwait(false), "C sees A's RESERVED");

        Check.True(!await a.LockAsync(LockLevel.Exclusive).ConfigureAwait(false), "A cannot be EXCLUSIVE while C reads");
        // A is now PENDING: new readers are refused (lockingv3 §3.0).
        IVfsFile d = await b.Vfs.OpenAsync(path, VfsOpenOptions.ReadWrite | VfsOpenOptions.MainDb).ConfigureAwait(false);
        Check.True(!await d.LockAsync(LockLevel.Shared).ConfigureAwait(false), "a new reader is refused while a writer is PENDING");
        Check.True(await c.LockAsync(LockLevel.Shared).ConfigureAwait(false), "the existing reader keeps its lock");

        await c.UnlockAsync(LockLevel.None).ConfigureAwait(false);
        Check.True(await a.LockAsync(LockLevel.Exclusive).ConfigureAwait(false), "A EXCLUSIVE once the reader left");
        Check.True(!await d.LockAsync(LockLevel.Shared).ConfigureAwait(false), "no reader beside EXCLUSIVE");

        await a.UnlockAsync(LockLevel.Shared).ConfigureAwait(false);
        Check.True(await d.LockAsync(LockLevel.Shared).ConfigureAwait(false), "readers are admitted once the writer is back to SHARED");
        Check.True(!await d.CheckReservedLockAsync().ConfigureAwait(false), "RESERVED was released with the downgrade");
        await a.UnlockAsync(LockLevel.None).ConfigureAwait(false);
        await d.UnlockAsync(LockLevel.None).ConfigureAwait(false);

        // Everything is free again.
        Check.True(await c.LockAsync(LockLevel.Shared).ConfigureAwait(false) && await c.LockAsync(LockLevel.Exclusive).ConfigureAwait(false), "C reaches EXCLUSIVE on a free file");
        await c.UnlockAsync(LockLevel.None).ConfigureAwait(false);
        a.Dispose();
        c.Dispose();
        d.Dispose();
        await b.Settle().ConfigureAwait(false);
    }

    private static async Task CheckReserved(Backend b)
    {
        string path = Backend.Path("chkres");
        IVfsFile a = await Create(b, path).ConfigureAwait(false);
        IVfsFile c = await b.Vfs.OpenAsync(path, VfsOpenOptions.ReadWrite | VfsOpenOptions.MainDb).ConfigureAwait(false);
        Check.True(!await c.CheckReservedLockAsync().ConfigureAwait(false), "nobody holds RESERVED");
        await a.LockAsync(LockLevel.Shared).ConfigureAwait(false);
        await a.LockAsync(LockLevel.Reserved).ConfigureAwait(false);
        Check.True(await c.CheckReservedLockAsync().ConfigureAwait(false), "A holds RESERVED");
        await a.UnlockAsync(LockLevel.None).ConfigureAwait(false);
        Check.True(!await c.CheckReservedLockAsync().ConfigureAwait(false), "released");
        a.Dispose();
        c.Dispose();
        await b.Settle().ConfigureAwait(false);
    }

    /// <summary>docs/vfs.md: a backend declares what it has; the driver that does not match is refused with MISUSE.</summary>
    private static async Task DriverRules(Backend b)
    {
        string path = Backend.Path("rules");
        IVfsFile f = await Create(b, path).ConfigureAwait(false);
        if (b.Sync)
        {
            Check.Equal(VfsCaps.Sync, b.Vfs.Caps, "the worker backend is Sync");
            Check.True((f.Caps & VfsCaps.Async) == 0, "and not Async");
            f.Write(0, Pattern(10, 1));
            Check.Equal(10L, f.FileSize(), "sync size");
            Check.True(f.Lock(LockLevel.Shared), "sync lock");
            f.Unlock(LockLevel.None);
            f.Sync(SyncMode.Normal);
        }
        else
        {
            Check.Equal(VfsCaps.Async, b.Vfs.Caps, "the main-thread backend is Async only");
            const string msg = "backend requires the async driver";
            Action[] sync =
            [
                () => f.Read(0, new byte[1]),
                () => f.Write(0, new byte[1]),
                () => f.Sync(SyncMode.Normal),
                () => f.Truncate(0),
                () => _ = f.FileSize(),
                () => _ = f.Lock(LockLevel.Shared),
                () => _ = f.CheckReservedLock(),
                () => b.Vfs.Open(path, VfsOpenOptions.ReadWrite),
                () => _ = b.Vfs.Access(path, AccessMode.Exists),
                () => b.Vfs.Delete(path, false),
            ];
            for (int i = 0; i < sync.Length; i++)
            {
                Action call = sync[i];
                VfsException e = await Check.Throws<VfsException>(() =>
                {
                    call();
                    return Task.CompletedTask;
                }, "synchronous call " + i).ConfigureAwait(false);
                Check.Equal(VfsCodes.Misuse, e.ExtendedCode, "synchronous call " + i + " is MISUSE");
                Check.Equal(msg, e.Message, "synchronous call " + i + " message");
            }

            // Releasing locks never needs to wait, so closing code can do it synchronously.
            await f.LockAsync(LockLevel.Shared).ConfigureAwait(false);
            f.Unlock(LockLevel.None);
        }

        await Close(b, f).ConfigureAwait(false);
    }

    private static async Task SyncCallsInline()
    {
        using Backend b = await Backend.Create("sync").ConfigureAwait(false);
        string path = Backend.Path("inline");
        // A synchronous Open of a path that was never prepared cannot create a handle: that takes an awaited call.
        VfsException e = await Check.Throws<VfsException>(() =>
        {
            b.Vfs.Open(path, CreateDb);
            return Task.CompletedTask;
        }, "unprepared open").ConfigureAwait(false);
        Check.Equal(VfsCodes.Misuse, e.ExtendedCode, "unprepared open is MISUSE");
        await ((OpfsSyncVfs)b.Vfs).PrepareAsync(path).ConfigureAwait(false);
        IVfsFile f = b.Vfs.Open(path, CreateDb);
        f.Write(0, Pattern(64, 1));
        ValueTask<int> r = f.ReadAsync(0, new byte[64]);
        Check.True(r.IsCompletedSuccessfully && r.Result == 64, "an async call on the sync backend completes inline");
        ValueTask<bool> l = f.LockAsync(LockLevel.Shared);
        Check.True(l.IsCompletedSuccessfully && l.Result, "an async lock on the sync backend completes inline");
        f.Unlock(LockLevel.None);
        f.Dispose();
    }

    /// <summary>The platform lets one context hold the handle: while a connection is open, no other prepare in this context's file set can reuse it; after the last close it can.</summary>
    private static async Task PrepareAfterRelease()
    {
        using Backend b = await Backend.Create("sync").ConfigureAwait(false);
        string path = Backend.Path("again");
        IVfsFile f = await Create(b, path).ConfigureAwait(false);
        f.Write(0, Pattern(32, 1));
        f.Dispose();
        using Backend b2 = await Backend.Create("sync").ConfigureAwait(false);
        IVfsFile g = await Create(b2, path).ConfigureAwait(false);
        Check.Equal(32L, g.FileSize(), "content survives a release and a new prepare");
        g.Dispose();
    }

    private static async Task AsyncRefusesSyncPager()
    {
        using Backend b = await Backend.Create("async").ConfigureAwait(false);
        OneLiteException e = await Check.Throws<OneLiteException>(() =>
        {
            Pager.Open(b.Vfs, Backend.Path("nopager"));
            return Task.CompletedTask;
        }, "sync pager on the async backend").ConfigureAwait(false);
        Check.Equal(ResultCode.Misuse, e.ExtendedCode & 0xFF, "the sync pager is refused with MISUSE");
    }
}
