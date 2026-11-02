using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Tests.Vfs;

/// <summary>Behaviour every VFS backend must share. Derived classes supply the backend.</summary>
public abstract class VfsContractTests : IDisposable
{
    protected const VfsOpenOptions Db = VfsOpenOptions.MainDb | VfsOpenOptions.ReadWrite | VfsOpenOptions.Create;

    protected abstract IVfs Vfs { get; }

    protected abstract string PathOf(string name);

    public virtual void Dispose() => GC.SuppressFinalize(this);

    private static byte[] Bytes(int n, byte seed)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++)
        {
            b[i] = (byte)(seed + i);
        }

        return b;
    }

    [Fact]
    public void WriteThenReadBackShortReadAtEndGapReadsZero()
    {
        using IVfsFile f = Vfs.Open(PathOf("a.db"), Db);
        f.Write(0, Bytes(100, 1));
        f.Write(300, Bytes(10, 9));
        Assert.Equal(310, f.FileSize());

        var buf = new byte[50];
        Assert.Equal(50, f.Read(10, buf));
        Assert.Equal(Bytes(100, 1).AsSpan(10, 50).ToArray(), buf);

        Assert.Equal(40, f.Read(100, new byte[40]));
        var gap = new byte[200];
        Assert.Equal(200, f.Read(100, gap));
        Assert.All(gap, x => Assert.Equal(0, x));

        var tail = new byte[50];
        Assert.Equal(10, f.Read(300, tail));
        Assert.Equal(0, f.Read(310, tail));
        Assert.Equal(0, f.Read(5000, tail));
    }

    [Fact]
    public void TruncateShrinksAndRegrowsWithZeros()
    {
        using IVfsFile f = Vfs.Open(PathOf("t.db"), Db);
        f.Write(0, Bytes(1000, 5));
        f.Truncate(400);
        Assert.Equal(400, f.FileSize());
        f.Truncate(600);
        var buf = new byte[600];
        Assert.Equal(600, f.Read(0, buf));
        Assert.Equal(Bytes(400, 5), buf.AsSpan(0, 400).ToArray());
        Assert.All(buf.AsSpan(400).ToArray(), x => Assert.Equal(0, x));
    }

    [Fact]
    public void WriteRunIsContiguous()
    {
        using IVfsFile f = Vfs.Open(PathOf("r.db"), Db);
        ReadOnlyMemory<byte>[] pages = [Bytes(10, 1), Bytes(20, 50), Bytes(5, 99)];
        f.WriteRun(7, pages);
        var all = new byte[35];
        Assert.Equal(35, f.Read(7, all));
        Assert.Equal([.. Bytes(10, 1), .. Bytes(20, 50), .. Bytes(5, 99)], all);
    }

    [Fact]
    public async Task AsyncFlavourMatchesSync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string p = PathOf("async.db");
        using (IVfsFile f = await Vfs.OpenAsync(p, Db, ct))
        {
            await f.WriteAsync(0, Bytes(64, 3), ct);
            await f.WriteRunAsync(64, new ReadOnlyMemory<byte>[] { Bytes(8, 7), Bytes(8, 8) }, ct);
            await f.SyncAsync(SyncMode.Normal, ct);
            await f.SyncDirectoryAsync(ct);
            Assert.Equal(80, await f.FileSizeAsync(ct));
            var buf = new byte[80];
            Assert.Equal(80, await f.ReadAsync(0, buf, ct));
            Assert.Equal([.. Bytes(64, 3), .. Bytes(8, 7), .. Bytes(8, 8)], buf);
            await f.TruncateAsync(10, ct);
            Assert.Equal(10, f.FileSize());
            Assert.True(await f.LockAsync(LockLevel.Shared, ct));
            await f.UnlockAsync(LockLevel.None, ct);
            Assert.False(await f.CheckReservedLockAsync(ct));
        }

        Assert.True(await Vfs.AccessAsync(p, AccessMode.Exists, ct));
        await Vfs.DeleteAsync(p, false, ct);
        Assert.False(await Vfs.AccessAsync(p, AccessMode.Exists, ct));
        await Vfs.SleepAsync(1, ct);
    }

    [Fact]
    public void AccessDeleteAndOpenRules()
    {
        string p = PathOf("rules.db");
        Assert.False(Vfs.Access(p, AccessMode.Exists));
        var ex = Assert.Throws<VfsException>(() => Vfs.Open(p, VfsOpenOptions.ReadOnly | VfsOpenOptions.MainDb));
        Assert.Equal(VfsCodes.CantOpen, ex.Code);

        Vfs.Open(p, Db).Dispose();
        Assert.True(Vfs.Access(p, AccessMode.Exists));
        Assert.True(Vfs.Access(p, AccessMode.ReadWrite));
        ex = Assert.Throws<VfsException>(() => Vfs.Open(p, Db | VfsOpenOptions.Exclusive));
        Assert.Equal(VfsCodes.CantOpen, ex.Code);

        Vfs.Delete(p, true);
        Assert.False(Vfs.Access(p, AccessMode.Exists));
        ex = Assert.Throws<VfsException>(() => Vfs.Delete(p, false));
        Assert.Equal(VfsCodes.IoErrDeleteNoEnt, ex.ExtendedCode);
        Assert.Equal(VfsCodes.IoErr, ex.Code);
    }

    [Fact]
    public void TempFileHasNoNameAndVanishes()
    {
        using IVfsFile f = Vfs.Open(string.Empty, VfsOpenOptions.TempDb);
        f.Write(0, Bytes(32, 1));
        Assert.Equal(32, f.FileSize());
    }

    [Fact]
    public void ReadOnlyOpenRefusesWrites()
    {
        string p = PathOf("ro.db");
        using (IVfsFile w = Vfs.Open(p, Db))
        {
            w.Write(0, Bytes(10, 1));
        }

        using IVfsFile r = Vfs.Open(p, VfsOpenOptions.ReadOnly | VfsOpenOptions.MainDb);
        Assert.NotEqual(0, (int)(r.Caps & VfsCaps.ReadOnly));
        Assert.Throws<VfsException>(() => r.Write(0, Bytes(1, 1)));
        Assert.False(r.Lock(LockLevel.Reserved));
    }

    // ---- locks (sqlite.org lockingv3) ----

    [Fact]
    public void LocksTwoConnectionsFollowTheStateMachine()
    {
        string p = PathOf("lock.db");
        using IVfsFile a = Vfs.Open(p, Db);
        using IVfsFile b = Vfs.Open(p, Db);

        Assert.True(a.Lock(LockLevel.Shared));
        Assert.True(b.Lock(LockLevel.Shared));          // many readers
        Assert.True(a.Lock(LockLevel.Reserved));
        Assert.False(b.Lock(LockLevel.Reserved));       // one writer-to-be
        Assert.True(b.CheckReservedLock());
        Assert.True(a.CheckReservedLock());
        Assert.True(b.Lock(LockLevel.Shared));          // already there: no-op
        Assert.False(a.Lock(LockLevel.Exclusive));      // b still reads: a waits at PENDING
        // lockingv3 §3.0: a PENDING lock admits no new readers
        using IVfsFile c = Vfs.Open(p, Db);
        Assert.False(c.Lock(LockLevel.Shared));
        b.Unlock(LockLevel.None);
        Assert.True(a.Lock(LockLevel.Exclusive));
        Assert.False(c.Lock(LockLevel.Shared));
        a.Unlock(LockLevel.Shared);                      // downgrade keeps reading, drops the write intent
        Assert.False(a.CheckReservedLock());
        Assert.True(c.Lock(LockLevel.Shared));
        Assert.True(c.Lock(LockLevel.Reserved));         // somebody else may now write
        Assert.False(a.Lock(LockLevel.Reserved));
        c.Unlock(LockLevel.None);
        a.Unlock(LockLevel.None);
        Assert.True(b.Lock(LockLevel.Shared));
        Assert.True(b.Lock(LockLevel.Exclusive));        // alone: straight to exclusive
        b.Unlock(LockLevel.None);
    }

    [Fact]
    public void LocksClosingOneConnectionKeepsTheOthersLocks()
    {
        string p = PathOf("close.db");
        using IVfsFile a = Vfs.Open(p, Db);
        IVfsFile b = Vfs.Open(p, Db);
        Assert.True(a.Lock(LockLevel.Shared));
        Assert.True(a.Lock(LockLevel.Reserved));
        Assert.True(b.Lock(LockLevel.Shared));
        b.Dispose();
        using IVfsFile c = Vfs.Open(p, Db);
        Assert.True(c.CheckReservedLock());
        Assert.False(c.Lock(LockLevel.Shared) && c.Lock(LockLevel.Reserved));
    }

    // ---- wal-index shared memory (walformat) ----

    [Fact]
    public void ShmRegionsAreSharedAndLocksAreArbitrated()
    {
        string p = PathOf("wal.db");
        using IVfsFile a = Vfs.Open(p, Db);
        using IVfsFile b = Vfs.Open(p, Db);
        IVfsShm sa = a.Shm!;
        IVfsShm sb = b.Shm!;
        Assert.True(ShmRegion.Empty.IsEmpty);
        Assert.True(sa.Map(0, false).IsEmpty);

        ShmRegion ra = sa.Map(0, true);
        Assert.Equal(IVfsShm.RegionSize, ra.Memory.Length);
        ShmAccessor.WriteUInt32(ra.Memory, 16, 0xABCD1234u);
        ShmRegion rb = sb.Map(0, false);
        Assert.Equal(0xABCD1234u, ShmAccessor.ReadUInt32(rb.Memory, 16));
        Assert.Equal(0xABCD1234u, ShmAccessor.CompareExchange(rb.Memory, 16, 7u, 0xABCD1234u));
        Assert.Equal(7u, ShmAccessor.ReadUInt32(ra.Memory, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShmAccessor.ReadUInt32(ra.Memory, 18));

        const ShmLockOptions Sh = ShmLockOptions.Lock | ShmLockOptions.Shared;
        const ShmLockOptions Ex = ShmLockOptions.Lock | ShmLockOptions.Exclusive;
        const ShmLockOptions ShU = ShmLockOptions.Unlock | ShmLockOptions.Shared;
        const ShmLockOptions ExU = ShmLockOptions.Unlock | ShmLockOptions.Exclusive;
        Assert.True(sa.Lock(3, 1, Sh));
        Assert.True(sb.Lock(3, 1, Sh));
        Assert.False(sb.Lock(3, 1, Ex));
        Assert.True(sa.Lock(0, 1, Ex));
        Assert.False(sb.Lock(0, 1, Sh));
        Assert.False(sb.Lock(0, 4, Ex));                // range: all or nothing
        Assert.True(sb.Lock(1, 2, Ex));                 // slots 0 and 3 untouched by the failed attempt
        Assert.True(sb.Lock(1, 2, ExU));
        Assert.True(sa.Lock(3, 1, ShU));
        Assert.False(sb.Lock(3, 1, Ex));                // b still holds one shared
        Assert.True(sb.Lock(3, 1, ShU));
        Assert.True(sb.Lock(3, 1, Ex));
        Assert.True(sb.Lock(3, 1, ExU));
        Assert.True(sa.Lock(0, 1, ExU));
        Assert.True(sb.Lock(0, 8, Ex));
        sb.Unmap(false);
        Assert.True(sa.Lock(0, 8, Ex));                 // unmapping released b's locks
        sa.Unmap(true);
        sa.Barrier();
    }

    [Fact]
    public void ShmUnmapDeleteRemovesTheFile()
    {
        string p = PathOf("walclean.db");
        using IVfsFile a = Vfs.Open(p, Db);
        a.Shm!.Map(0, true);
        Assert.True(Vfs.Access(p + "-shm", AccessMode.Exists));
        a.Shm!.Unmap(true);
        Assert.False(Vfs.Access(p + "-shm", AccessMode.Exists));
    }

    [Fact]
    public async Task ShmConcurrentWritersToTheSameWordLoseNothing()
    {
        string p = PathOf("race.db");
        using IVfsFile a = Vfs.Open(p, Db);
        using IVfsFile b = Vfs.Open(p, Db);
        Memory<byte> ma = a.Shm!.Map(0, true).Memory;
        Memory<byte> mb = b.Shm!.Map(0, true).Memory;
        const int N = 20000;
        CancellationToken ct = TestContext.Current.CancellationToken;
        await Task.WhenAll(Task.Run(() => Bump(ma), ct), Task.Run(() => Bump(mb), ct));
        Assert.Equal(2u * N, ShmAccessor.ReadUInt32(ma, 8));

        static void Bump(Memory<byte> m)
        {
            for (int i = 0; i < N; i++)
            {
                uint cur;
                do
                {
                    cur = ShmAccessor.ReadUInt32(m, 8);
                }
                while (ShmAccessor.CompareExchange(m, 8, cur + 1, cur) != cur);
            }
        }
    }
}

public sealed class MemoryVfsContractTests : VfsContractTests
{
    private readonly MemoryVfs _vfs = new();

    protected override IVfs Vfs => _vfs;

    protected override string PathOf(string name) => "/mem/" + name;
}

public sealed class FileVfsContractTests : VfsContractTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-vfs-" + Guid.NewGuid().ToString("N"));

    public FileVfsContractTests()
    {
        Directory.CreateDirectory(_dir);
    }

    protected override IVfs Vfs => FileVfs.Instance;

    protected override string PathOf(string name) => Path.Combine(_dir, name);

    public override void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }

        base.Dispose();
    }
}
