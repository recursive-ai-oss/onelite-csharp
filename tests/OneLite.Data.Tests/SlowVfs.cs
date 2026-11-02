using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Data.Tests;

/// <summary>
/// A storage backend whose asynchronous reads really are asynchronous: each one yields (or waits for <see cref="Hold"/>) before it reads from the memory
/// backend underneath. Synchronous calls go straight through, so schema loading and writes work as usual.
/// </summary>
internal sealed class SlowVfs(MemoryVfs inner, bool asyncOnly = false) : VfsBase
{
    private int _asyncReads;
    private int _incompleteReads;
    private int _enteredReads;

    public override string Name => "slow";

    internal bool AsyncOnly => asyncOnly;

    public override VfsCaps Caps => asyncOnly ? VfsCaps.Async : VfsCaps.Sync | VfsCaps.Async | VfsCaps.BlockingOk;

    /// <summary>While set, asynchronous reads wait for it (or for their token).</summary>
    public TaskCompletionSource? Hold { get; set; }

    public int AsyncReads => Volatile.Read(ref _asyncReads);

    /// <summary>How many asynchronous reads returned a task that was not yet complete.</summary>
    public int IncompleteReads => Volatile.Read(ref _incompleteReads);

    /// <summary>How many asynchronous reads have started.</summary>
    public int EnteredReads => Volatile.Read(ref _enteredReads);

    public void ResetCounters()
    {
        Volatile.Write(ref _asyncReads, 0);
        Volatile.Write(ref _incompleteReads, 0);
        Volatile.Write(ref _enteredReads, 0);
    }

    public override IVfsFile Open(string path, VfsOpenOptions flags) => new SlowFile(this, inner.Open(path, flags));

    public override bool Access(string path, AccessMode mode) => inner.Access(path, mode);

    public override void Delete(string path, bool syncDirectory) => inner.Delete(path, syncDirectory);

    public override string FullPathname(string path) => inner.FullPathname(path);

    private sealed class SlowFile(SlowVfs owner, IVfsFile file) : VfsFileBase
    {
        public override VfsCaps Caps => owner.Caps | (file.Caps & VfsCaps.SharedMemory);

        public override int SectorSize => file.SectorSize;

        public override DeviceCharacteristics Device => file.Device;

        public override IVfsShm? Shm => file.Shm;

        public override int Read(long offset, Span<byte> dst) => owner.AsyncOnly ? base.Read(offset, dst) : file.Read(offset, dst);

        public override void Write(long offset, ReadOnlySpan<byte> src) => file.Write(offset, src);

        public override void Sync(SyncMode mode) => file.Sync(mode);

        public override void SyncDirectory() => file.SyncDirectory();

        public override void Truncate(long size) => file.Truncate(size);

        public override long FileSize() => file.FileSize();

        public override bool Lock(LockLevel level) => file.Lock(level);

        public override void Unlock(LockLevel level) => file.Unlock(level);

        public override bool CheckReservedLock() => file.CheckReservedLock();

        public override void Dispose() => file.Dispose();

        public override ValueTask<int> ReadAsync(long offset, Memory<byte> dst, CancellationToken ct = default)
        {
            Interlocked.Increment(ref owner._asyncReads);
            ValueTask<int> t = ReadSlow(offset, dst, ct);
            if (!t.IsCompleted)
            {
                Interlocked.Increment(ref owner._incompleteReads);
            }

            return t;
        }

        private async ValueTask<int> ReadSlow(long offset, Memory<byte> dst, CancellationToken ct)
        {
            Interlocked.Increment(ref owner._enteredReads);
            if (owner.Hold is { } hold)
            {
                await hold.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await Task.Yield();
            }

            return file.Read(offset, dst.Span);
        }
    }
}
