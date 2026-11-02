using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Tests.Vfs;

/// <summary>Allocation checks (bytes allocated on the calling thread) and the sync/async fallback rules of the base classes.</summary>
public sealed class VfsBehaviourTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-vfsb-" + Guid.NewGuid().ToString("N"));

    public VfsBehaviourTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void FileBackendSyncReadWriteAllocateNothingPerCall()
    {
        using IVfsFile f = FileVfs.Instance.Open(Path.Combine(_dir, "alloc.db"), VfsOpenOptions.MainDb | VfsOpenOptions.ReadWrite | VfsOpenOptions.Create);
        var page = new byte[4096];
        var dst = new byte[4096];
        for (int i = 0; i < 16; i++)
        {
            f.Write(i * 4096L, page);
        }

        Assert.Equal(0, MinAllocatedPerPass(f, page, dst));
    }

    // One-time costs (tiered JIT, lazily created runtime objects) can land inside any one pass; a per-call allocation lands in every pass.
    private static long MinAllocatedPerPass(IVfsFile f, byte[] page, byte[] dst)
    {
        Exercise(f, page, dst, 200);
        long best = long.MaxValue;
        for (int pass = 0; pass < 6; pass++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Exercise(f, page, dst, 2000);
            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return best;
    }

    private static void Exercise(IVfsFile f, byte[] page, byte[] dst, int rounds)
    {
        for (int r = 0; r < rounds; r++)
        {
            long off = (r & 15) * 4096L;
            if (f.Read(off, dst) != 4096)
            {
                throw new InvalidOperationException("short read");
            }

            f.Write(off, page);
            if (f.FileSize() != 16 * 4096)
            {
                throw new InvalidOperationException("size");
            }

            if (!f.Lock(LockLevel.Shared))
            {
                throw new InvalidOperationException("lock");
            }

            f.Unlock(LockLevel.None);
        }
    }

    [Fact]
    public void MemoryBackendSyncReadWriteAllocateNothingPerCall()
    {
        using IVfsFile f = new MemoryVfs().Open("m.db", VfsOpenOptions.MainDb | VfsOpenOptions.ReadWrite | VfsOpenOptions.Create);
        var page = new byte[4096];
        var dst = new byte[4096];
        for (int i = 0; i < 16; i++)
        {
            f.Write(i * 4096L, page);
        }

        Assert.Equal(0, MinAllocatedPerPass(f, page, dst));
    }

    [Fact]
    public void FileLocksAreSharedPerFileAcrossManyConnections()
    {
        string p = Path.Combine(_dir, "many.db");
        var files = new List<IVfsFile>();
        for (int i = 0; i < 20; i++)
        {
            files.Add(FileVfs.Instance.Open(p, VfsOpenOptions.MainDb | VfsOpenOptions.ReadWrite | VfsOpenOptions.Create));
            Assert.True(files[i].Lock(LockLevel.Shared));
        }

        Assert.False(files[0].Lock(LockLevel.Exclusive));   // 19 other readers
        for (int i = 1; i < 20; i++)
        {
            files[i].Dispose();
        }

        Assert.True(files[0].Lock(LockLevel.Exclusive));
        files[0].Dispose();
        Assert.True(new FileInfo(p).Exists);
    }

    [Fact]
    public async Task ConcurrentConnectionsNeverBothGetReserved()
    {
        string p = Path.Combine(_dir, "race.db");
        int winners = 0;
        using var go = new ManualResetEventSlim();
        CancellationToken ct = TestContext.Current.CancellationToken;
        var tasks = new List<Task>();
        var files = new List<IVfsFile>();
        for (int i = 0; i < 8; i++)
        {
            IVfsFile f = FileVfs.Instance.Open(p, VfsOpenOptions.MainDb | VfsOpenOptions.ReadWrite | VfsOpenOptions.Create);
            files.Add(f);
            tasks.Add(Task.Run(() =>
            {
                go.Wait();
                if (f.Lock(LockLevel.Shared) && f.Lock(LockLevel.Reserved))
                {
                    Interlocked.Increment(ref winners);
                }
            }, ct));
        }

        go.Set();
        await Task.WhenAll(tasks);
        Assert.Equal(1, winners);
        foreach (IVfsFile f in files)
        {
            f.Dispose();
        }
    }

    // ---- fallback rules (the two driver rules of the VFS contract) ----

    /// <summary>A backend that only has the async flavour, like a browser storage backend: it completes after a real suspension.</summary>
    private sealed class AsyncOnlyFile : VfsFileBase
    {
        private readonly bool _blockingOk;
        private byte[] _data = [];

        public AsyncOnlyFile(bool blockingOk)
        {
            _blockingOk = blockingOk;
        }

        private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // A real suspension. Where blocking is allowed the continuation runs on a short-lived dedicated thread (not the pool: the synchronous
        // driver blocks its own thread while it waits). Where it is not allowed the call stays suspended until the file is disposed, so the
        // refusal does not depend on how fast another thread is.
        private Task Suspend()
        {
            if (!_blockingOk)
            {
                return _never.Task;
            }

            var tcs = new TaskCompletionSource();
            var t = new Thread(() => tcs.SetResult()) { IsBackground = true };
            t.Start();
            return tcs.Task;
        }

        public override VfsCaps Caps => VfsCaps.Async | (_blockingOk ? VfsCaps.BlockingOk : 0);

        public override int SectorSize => 512;

        public override DeviceCharacteristics Device => DeviceCharacteristics.None;

        public override async ValueTask<int> ReadAsync(long offset, Memory<byte> dst, CancellationToken ct = default)
        {
            await Suspend();
            int n = (int)Math.Max(0, Math.Min(dst.Length, _data.Length - offset));
            _data.AsMemory((int)offset, n).CopyTo(dst);
            return n;
        }

        public override async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> src, CancellationToken ct = default)
        {
            await Suspend();
            if (_data.Length < offset + src.Length)
            {
                Array.Resize(ref _data, (int)offset + src.Length);
            }

            src.CopyTo(_data.AsMemory((int)offset));
        }

        public override async ValueTask SyncAsync(SyncMode mode, CancellationToken ct = default) => await Suspend();

        public override async ValueTask SyncDirectoryAsync(CancellationToken ct = default) => await Suspend();

        public override async ValueTask TruncateAsync(long size, CancellationToken ct = default)
        {
            await Suspend();
            Array.Resize(ref _data, (int)size);
        }

        public override async ValueTask<long> FileSizeAsync(CancellationToken ct = default)
        {
            await Suspend();
            return _data.Length;
        }

        public override async ValueTask<bool> LockAsync(LockLevel level, CancellationToken ct = default)
        {
            await Suspend();
            return true;
        }

        public override async ValueTask UnlockAsync(LockLevel level, CancellationToken ct = default) => await Suspend();

        public override async ValueTask<bool> CheckReservedLockAsync(CancellationToken ct = default)
        {
            await Suspend();
            return false;
        }

        public override void Dispose()
        {
            _never.TrySetResult();
        }
    }

    [Fact]
    public void AsyncOnlyBackendServesTheSyncDriverOnlyWhenBlockingIsOk()
    {
        using var ok = new AsyncOnlyFile(blockingOk: true);
        ok.Write(0, new byte[] { 1, 2, 3, 4 });
        var buf = new byte[4];
        Assert.Equal(4, ok.Read(0, buf));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, buf);
        Assert.Equal(4, ok.FileSize());
        ok.Sync(SyncMode.Normal);
        ok.Truncate(2);
        Assert.True(ok.Lock(LockLevel.Shared));

        using var refuse = new AsyncOnlyFile(blockingOk: false);
        VfsException e = Assert.Throws<VfsException>(() => refuse.Sync(SyncMode.Normal));
        Assert.Equal(VfsCodes.Misuse, e.Code);
        Assert.Contains("async driver", e.Message, StringComparison.Ordinal);
        e = Assert.Throws<VfsException>(() => refuse.FileSize());
        Assert.Equal(VfsCodes.Misuse, e.Code);
    }

    [Fact]
    public async Task SyncOnlyBackendServesTheAsyncDriverInline()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IVfsFile f = new MemoryVfs().Open("x", VfsOpenOptions.ReadWrite | VfsOpenOptions.Create);
        ValueTask w = f.WriteAsync(0, new byte[] { 9 }, ct);
        Assert.True(w.IsCompletedSuccessfully);          // no suspension: the sync method ran inline
        await w;
        ValueTask<int> r = f.ReadAsync(0, new byte[1], ct);
        Assert.True(r.IsCompletedSuccessfully);
        Assert.Equal(1, await r);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await f.SyncAsync(SyncMode.Normal, cts.Token));
    }

    [Fact]
    public async Task FileBackendAsyncReadsAndWritesWork()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IVfsFile f = await FileVfs.Instance.OpenAsync(Path.Combine(_dir, "af.db"), VfsOpenOptions.ReadWrite | VfsOpenOptions.Create, ct);
        var src = new byte[10000];
        new Random(5).NextBytes(src);
        await f.WriteAsync(100, src, ct);
        var dst = new byte[10000];
        Assert.Equal(10000, await f.ReadAsync(100, dst, ct));
        Assert.Equal(src, dst);
        Assert.Equal(0, await f.ReadAsync(100000, dst, ct));
    }
}
