using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Data.Tests;

/// <summary>
/// A storage backend that has only asynchronous calls, as browser storage does: every synchronous method fails with MISUSE ("backend requires the async
/// driver") even when it could answer at once, and every asynchronous method yields before it does its work. It is a stricter stand-in than
/// <see cref="SlowVfs"/>, which still lets writes, locks and opens through synchronously.
/// </summary>
internal sealed class StrictAsyncVfs(MemoryVfs inner) : VfsBase
{
    private int _syncCalls;

    public MemoryVfs Inner => inner;

    public override string Name => "strict-async";

    public override VfsCaps Caps => VfsCaps.Async;

    /// <summary>How many synchronous calls were refused.</summary>
    public int SyncCalls => Volatile.Read(ref _syncCalls);

    /// <summary>How many asynchronous calls were served.</summary>
    public int AsyncCalls { get; private set; }

    internal void Refuse()
    {
        Interlocked.Increment(ref _syncCalls);
        throw new VfsException(VfsCodes.Misuse, "backend requires the async driver");
    }

    /// <summary>Cancels <see cref="CancelSource"/> when this many asynchronous calls have been made (0: never).</summary>
    public int CancelAtCall { get; set; }

    public CancellationTokenSource? CancelSource { get; set; }

    internal async ValueTask Hop(CancellationToken ct = default)
    {
        AsyncCalls++;
        if (AsyncCalls == CancelAtCall)
        {
            CancelSource?.Cancel();
        }

        await Task.Yield();
        ct.ThrowIfCancellationRequested();
    }

    public override IVfsFile Open(string path, VfsOpenOptions flags)
    {
        Refuse();
        return null!;
    }

    public override async ValueTask<IVfsFile> OpenAsync(string path, VfsOpenOptions flags, CancellationToken ct = default)
    {
        await Hop(ct);
        return new StrictFile(this, inner.Open(path, flags));
    }

    public override bool Access(string path, AccessMode mode)
    {
        Refuse();
        return false;
    }

    public override async ValueTask<bool> AccessAsync(string path, AccessMode mode, CancellationToken ct = default)
    {
        await Hop(ct);
        return inner.Access(path, mode);
    }

    public override void Delete(string path, bool syncDirectory) => Refuse();

    public override async ValueTask DeleteAsync(string path, bool syncDirectory, CancellationToken ct = default)
    {
        await Hop(ct);
        inner.Delete(path, syncDirectory);
    }

    public override string FullPathname(string path) => inner.FullPathname(path);

    public override void Sleep(int ms) => Refuse();

    public override async ValueTask SleepAsync(int ms, CancellationToken ct = default)
    {
        await Hop(ct);
        await Task.Delay(ms, ct);
    }

    private sealed class StrictFile(StrictAsyncVfs owner, IVfsFile file) : VfsFileBase
    {
        public override VfsCaps Caps => VfsCaps.Async;

        public override int SectorSize => file.SectorSize;

        public override DeviceCharacteristics Device => file.Device;

        public override int Read(long offset, Span<byte> dst)
        {
            owner.Refuse();
            return 0;
        }

        public override async ValueTask<int> ReadAsync(long offset, Memory<byte> dst, CancellationToken ct = default)
        {
            await owner.Hop(ct);
            return file.Read(offset, dst.Span);
        }

        public override void Write(long offset, ReadOnlySpan<byte> src) => owner.Refuse();

        public override async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> src, CancellationToken ct = default)
        {
            await owner.Hop(ct);
            file.Write(offset, src.Span);
        }

        public override void Sync(SyncMode mode) => owner.Refuse();

        public override async ValueTask SyncAsync(SyncMode mode, CancellationToken ct = default)
        {
            await owner.Hop(ct);
            file.Sync(mode);
        }

        public override void SyncDirectory() => owner.Refuse();

        public override async ValueTask SyncDirectoryAsync(CancellationToken ct = default)
        {
            await owner.Hop(ct);
            file.SyncDirectory();
        }

        public override void Truncate(long size) => owner.Refuse();

        public override async ValueTask TruncateAsync(long size, CancellationToken ct = default)
        {
            await owner.Hop(ct);
            file.Truncate(size);
        }

        public override long FileSize()
        {
            owner.Refuse();
            return 0;
        }

        public override async ValueTask<long> FileSizeAsync(CancellationToken ct = default)
        {
            await owner.Hop(ct);
            return file.FileSize();
        }

        public override bool Lock(LockLevel level)
        {
            owner.Refuse();
            return false;
        }

        public override async ValueTask<bool> LockAsync(LockLevel level, CancellationToken ct = default)
        {
            await owner.Hop(ct);
            return file.Lock(level);
        }

        public override void Unlock(LockLevel level) => owner.Refuse();

        public override async ValueTask UnlockAsync(LockLevel level, CancellationToken ct = default)
        {
            await owner.Hop(ct);
            file.Unlock(level);
        }

        public override bool CheckReservedLock()
        {
            owner.Refuse();
            return false;
        }

        public override async ValueTask<bool> CheckReservedLockAsync(CancellationToken ct = default)
        {
            await owner.Hop(ct);
            return file.CheckReservedLock();
        }

        public override void Dispose() => file.Dispose();
    }
}
