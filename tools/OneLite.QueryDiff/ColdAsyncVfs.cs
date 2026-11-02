using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.QueryDiff;

/// <summary>
/// The local file system seen as a backend that has only asynchronous calls and whose every call really suspends (it yields to the scheduler before it
/// does its work), as browser storage does. The differential tool runs the engine over it to prove the async driver gives the sync driver's results:
/// a synchronous call is refused with MISUSE, so any code path that still does synchronous I/O shows up.
/// </summary>
internal sealed class ColdAsyncVfs : VfsBase
{
    private long _calls;

    public override string Name => "cold-async";

    public override VfsCaps Caps => VfsCaps.Async;

    /// <summary>How many asynchronous calls were served.</summary>
    internal long Calls => Interlocked.Read(ref _calls);

    private async ValueTask Hop(CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
    }

    private static T Refuse<T>()
        => throw new VfsException(VfsCodes.Misuse, "backend requires the async driver");

    public override string FullPathname(string path) => FileVfs.Instance.FullPathname(path);

    public override IVfsFile Open(string path, VfsOpenOptions flags) => Refuse<IVfsFile>();

    public override async ValueTask<IVfsFile> OpenAsync(string path, VfsOpenOptions flags, CancellationToken ct = default)
    {
        await Hop(ct).ConfigureAwait(false);
        return new File(this, FileVfs.Instance.Open(path, flags));
    }

    public override bool Access(string path, AccessMode mode) => Refuse<bool>();

    public override async ValueTask<bool> AccessAsync(string path, AccessMode mode, CancellationToken ct = default)
    {
        await Hop(ct).ConfigureAwait(false);
        return FileVfs.Instance.Access(path, mode);
    }

    public override void Delete(string path, bool syncDirectory) => Refuse<int>();

    public override async ValueTask DeleteAsync(string path, bool syncDirectory, CancellationToken ct = default)
    {
        await Hop(ct).ConfigureAwait(false);
        FileVfs.Instance.Delete(path, syncDirectory);
    }

    public override void Sleep(int ms) => Refuse<int>();

    private sealed class File(ColdAsyncVfs owner, IVfsFile inner) : VfsFileBase
    {
        public override VfsCaps Caps => VfsCaps.Async | (inner.Caps & VfsCaps.SharedMemory);

        public override int SectorSize => inner.SectorSize;

        public override DeviceCharacteristics Device => inner.Device;

        public override IVfsShm? Shm => inner.Shm;

        public override int Read(long offset, Span<byte> dst) => Refuse<int>();

        public override async ValueTask<int> ReadAsync(long offset, Memory<byte> dst, CancellationToken ct = default)
        {
            await owner.Hop(ct).ConfigureAwait(false);
            return inner.Read(offset, dst.Span);
        }

        public override void Write(long offset, ReadOnlySpan<byte> src) => Refuse<int>();

        public override async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> src, CancellationToken ct = default)
        {
            await owner.Hop(ct).ConfigureAwait(false);
            inner.Write(offset, src.Span);
        }

        public override void Sync(SyncMode mode) => Refuse<int>();

        public override async ValueTask SyncAsync(SyncMode mode, CancellationToken ct = default)
        {
            await owner.Hop(ct).ConfigureAwait(false);
            inner.Sync(mode);
        }

        public override void SyncDirectory() => Refuse<int>();

        public override async ValueTask SyncDirectoryAsync(CancellationToken ct = default)
        {
            await owner.Hop(ct).ConfigureAwait(false);
            inner.SyncDirectory();
        }

        public override void Truncate(long size) => Refuse<int>();

        public override async ValueTask TruncateAsync(long size, CancellationToken ct = default)
        {
            await owner.Hop(ct).ConfigureAwait(false);
            inner.Truncate(size);
        }

        public override long FileSize() => Refuse<long>();

        public override async ValueTask<long> FileSizeAsync(CancellationToken ct = default)
        {
            await owner.Hop(ct).ConfigureAwait(false);
            return inner.FileSize();
        }

        public override bool Lock(LockLevel level) => Refuse<bool>();

        public override async ValueTask<bool> LockAsync(LockLevel level, CancellationToken ct = default)
        {
            await owner.Hop(ct).ConfigureAwait(false);
            return inner.Lock(level);
        }

        public override void Unlock(LockLevel level) => Refuse<int>();

        public override async ValueTask UnlockAsync(LockLevel level, CancellationToken ct = default)
        {
            await owner.Hop(ct).ConfigureAwait(false);
            inner.Unlock(level);
        }

        public override bool CheckReservedLock() => Refuse<bool>();

        public override async ValueTask<bool> CheckReservedLockAsync(CancellationToken ct = default)
        {
            await owner.Hop(ct).ConfigureAwait(false);
            return inner.CheckReservedLock();
        }

        public override void Dispose() => inner.Dispose();
    }
}
