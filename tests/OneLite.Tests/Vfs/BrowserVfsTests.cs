using System.Linq;
using System.Threading.Tasks;
using RecursiveAI.OneLite.Storage.Vfs;
using RecursiveAI.OneLite.Storage.Vfs.Browser;
using RecursiveAI.OneLite.Tests.Pager;
using RecursiveAI.OneLite.Tests.Wal;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Vfs;

/// <summary>
/// The parts of the browser file systems that do not need a browser: the lock levels built from named try-locks (checked against an in-process
/// table with the Web Locks rules) and whole-database import and export (checked on the memory backend). The OPFS backends themselves are
/// tested in a browser by tests/OneLite.BrowserTests.
/// </summary>
public sealed class BrowserVfsTests
{
    private static async Task<bool> Lock(LevelLocks l, LockLevel level) => await l.LockAsync(level);

    [Fact]
    public async Task LockLevelsFollowLockingV3()
    {
        var table = new LocalLockTable();
        var a = new LevelLocks(table, "db");
        var c = new LevelLocks(table, "db");
        var d = new LevelLocks(table, "db");

        Assert.True(await Lock(a, LockLevel.Shared));
        Assert.True(await Lock(c, LockLevel.Shared));
        Assert.True(await Lock(a, LockLevel.Reserved));
        Assert.False(await Lock(c, LockLevel.Reserved));
        Assert.True(await c.CheckReservedAsync());

        // PENDING: A cannot reach EXCLUSIVE while C reads, and stays PENDING, which keeps new readers out.
        Assert.False(await Lock(a, LockLevel.Exclusive));
        Assert.Equal(LockLevel.Pending, a.Level);
        Assert.False(await Lock(d, LockLevel.Shared));
        Assert.Equal(LockLevel.None, d.Level);
        Assert.Equal(LockLevel.Shared, c.Level);

        await c.UnlockAsync(LockLevel.None);
        Assert.True(await Lock(a, LockLevel.Exclusive));
        Assert.False(await Lock(d, LockLevel.Shared));

        await a.UnlockAsync(LockLevel.Shared);
        Assert.Equal(LockLevel.Shared, a.Level);
        Assert.True(await Lock(d, LockLevel.Shared));
        Assert.False(await d.CheckReservedAsync());
        await a.UnlockAsync(LockLevel.None);
        await d.UnlockAsync(LockLevel.None);
        Assert.True(await Lock(c, LockLevel.Shared));
        Assert.True(await Lock(c, LockLevel.Exclusive));
    }

    [Fact]
    public async Task AFailedExclusiveKeepsTheReadLock()
    {
        var table = new LocalLockTable();
        var a = new LevelLocks(table, "db");
        var b = new LevelLocks(table, "db");
        Assert.True(await Lock(a, LockLevel.Shared));
        Assert.True(await Lock(b, LockLevel.Shared));
        Assert.False(await Lock(a, LockLevel.Exclusive));

        // A is back to reading on SHARED: when B leaves and A drops PENDING, a third reader can come in.
        await b.UnlockAsync(LockLevel.None);
        await a.UnlockAsync(LockLevel.Shared);
        Assert.True(await Lock(new LevelLocks(table, "db"), LockLevel.Shared));
    }

    [Fact]
    public async Task ReleasingWithoutWaitingFreesTheLocks()
    {
        var table = new LocalLockTable();
        var a = new LevelLocks(table, "db");
        Assert.True(await Lock(a, LockLevel.Shared));
        Assert.True(await Lock(a, LockLevel.Reserved));
        a.ReleaseAllNow();
        Assert.Equal(LockLevel.None, a.Level);
        var b = new LevelLocks(table, "db");
        Assert.True(await Lock(b, LockLevel.Shared) && await Lock(b, LockLevel.Exclusive));
    }

    [Fact]
    public async Task DifferentFilesDoNotShareLocks()
    {
        var table = new LocalLockTable();
        var a = new LevelLocks(table, "one");
        var b = new LevelLocks(table, "two");
        Assert.True(await Lock(a, LockLevel.Shared) && await Lock(a, LockLevel.Exclusive));
        Assert.True(await Lock(b, LockLevel.Shared) && await Lock(b, LockLevel.Exclusive));
    }

    [Fact]
    public async Task ExportEqualsTheFileAndImportRestoresIt()
    {
        MemoryVfs vfs = Kit.Prepare(40);
        byte[] original = vfs.Export(Kit.Db)!;
        byte[] exported = await DatabaseTransfer.ExportAsync(vfs, Kit.Db, TestContext.Current.CancellationToken);
        Assert.True(original.AsSpan().SequenceEqual(exported), Kit.Describe(original, exported));

        var other = new MemoryVfs();
        await DatabaseTransfer.ImportAsync(other, "copy.db", exported, TestContext.Current.CancellationToken);
        Assert.True(exported.AsSpan().SequenceEqual(other.Export("copy.db")), "imported bytes");

        using var ms = new System.IO.MemoryStream(exported);
        var third = new MemoryVfs();
        await DatabaseTransfer.ImportAsync(third, "copy.db", ms, TestContext.Current.CancellationToken);
        Assert.True(exported.AsSpan().SequenceEqual(third.Export("copy.db")), "imported from a stream");
    }

    [Fact]
    public async Task ExportOfAWalDatabaseIncludesTheLog()
    {
        MemoryVfs vfs = WalKit.PrepareWal(30);
        {
            // Commit through the log and leave without checkpointing, as a crash or a close that cannot checkpoint would.
            using var p = RecursiveAI.OneLite.Storage.Pager.Pager.Open(vfs, Kit.Db, WalKit.Opts(autoCheckpoint: 0));
            p.BeginReadSync();
            WalKit.Write(p, new uint[] { 3, 4, 5 }, 1);
            p.EndReadSync();
            Assert.True(vfs.Export(Kit.Db + "-wal")!.Length > 0, "the log has frames");
        }

        byte[] exported = await DatabaseTransfer.ExportAsync(vfs, Kit.Db, TestContext.Current.CancellationToken);
        Assert.Equal(30 * 512, exported.Length);
        var copy = new MemoryVfs();
        await DatabaseTransfer.ImportAsync(copy, Kit.Db, exported, TestContext.Current.CancellationToken);
        using var q = RecursiveAI.OneLite.Storage.Pager.Pager.Open(copy, Kit.Db, WalKit.Opts());
        q.BeginReadSync();
        foreach (uint n in new uint[] { 3, 4, 5 })
        {
            Assert.True(WalKit.Expect(n, 1, 512).AsSpan().SequenceEqual(WalKit.Page(q, n)), $"page {n} carries the committed change");
        }
    }

    [Fact]
    public async Task ImportRefusesWhileAReaderIsConnectedAndRejectsNonDatabases()
    {
        MemoryVfs vfs = Kit.Prepare(10);
        byte[] bytes = vfs.Export(Kit.Db)!;
        using (IVfsFile reader = vfs.Open(Kit.Db, VfsOpenOptions.ReadWrite | VfsOpenOptions.MainDb))
        {
            Assert.True(reader.Lock(LockLevel.Shared));
            OneLiteException busy = await Assert.ThrowsAsync<OneLiteException>(() => DatabaseTransfer.ImportAsync(vfs, Kit.Db, bytes, TestContext.Current.CancellationToken).AsTask());
            Assert.Equal(ResultCode.Busy, busy.ExtendedCode & 0xFF);
        }

        foreach (byte[] junk in new[] { System.Array.Empty<byte>(), new byte[99], new byte[4096], bytes.Take(bytes.Length - 3).ToArray() })
        {
            OneLiteException e = await Assert.ThrowsAsync<OneLiteException>(() => DatabaseTransfer.ImportAsync(vfs, Kit.Db, junk, TestContext.Current.CancellationToken).AsTask());
            Assert.Equal(ResultCode.NotADb, e.ExtendedCode & 0xFF);
        }

        Assert.True(bytes.AsSpan().SequenceEqual(vfs.Export(Kit.Db)), "rejected imports leave the file alone");
    }

    [Fact]
    public async Task ImportRemovesAStaleJournal()
    {
        MemoryVfs vfs = Kit.Prepare(10);
        byte[] bytes = vfs.Export(Kit.Db)!;
        vfs.Import(Kit.Db + "-journal", new byte[1024]);
        await DatabaseTransfer.ImportAsync(vfs, Kit.Db, bytes, TestContext.Current.CancellationToken);
        Assert.False(Kit.Exists(vfs, Kit.Db + "-journal"));
    }

    [Fact]
    public async Task ExportOfAMissingDatabaseIsCantOpen()
    {
        OneLiteException e = await Assert.ThrowsAsync<OneLiteException>(() => DatabaseTransfer.ExportAsync(new MemoryVfs(), "nope.db", TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(ResultCode.CantOpen, e.ExtendedCode & 0xFF);
    }
}
