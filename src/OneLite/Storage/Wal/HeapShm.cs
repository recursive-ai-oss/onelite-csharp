using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Storage.Wal;

/// <summary>
/// A wal-index kept in this connection's own memory, for backends without shared memory (walformat §3: a read-only connection does the same).
/// Only one connection can use it, so its locks always succeed; other connections to the same file are not coordinated with.
/// </summary>
internal sealed class HeapShm : IVfsShm
{
    private readonly List<ShmRegion> _regions = [];

    /// <inheritdoc />
    public ShmRegion Map(int region, bool extend)
    {
        while (_regions.Count <= region)
        {
            if (!extend)
            {
                return ShmRegion.Empty;
            }

            _regions.Add(new ShmRegion(new byte[IVfsShm.RegionSize], null));
        }

        return _regions[region];
    }

    /// <inheritdoc />
    public ValueTask<ShmRegion> MapAsync(int region, bool extend, CancellationToken ct = default) => new(Map(region, extend));

    /// <inheritdoc />
    public bool Lock(int offset, int count, ShmLockOptions flags) => true;

    /// <inheritdoc />
    public ValueTask<bool> LockAsync(int offset, int count, ShmLockOptions flags, CancellationToken ct = default) => new(true);

    /// <inheritdoc />
    public void Barrier() => Interlocked.MemoryBarrier();

    /// <inheritdoc />
    public void Unmap(bool deleteFile) => _regions.Clear();
}
