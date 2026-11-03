using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Tests.Vfs;

/// <summary>Growing an in-memory file costs memory only for the bytes written (a hostile journal can claim a huge database size).</summary>
public sealed class MemoryVfsSizeTests
{
    [Fact]
    public void TruncatingUpwardsAllocatesNothingAndReadsZeros()
    {
        var vfs = new MemoryVfs();
        using IVfsFile f = vfs.Open("big.db", VfsOpenOptions.Create | VfsOpenOptions.ReadWrite | VfsOpenOptions.MainDb);
        f.Write(0, [1, 2, 3]);
        long before = GC.GetTotalMemory(true);
        f.Truncate(1L << 30);
        long after = GC.GetTotalMemory(true);
        Assert.True(after - before < (16 << 20), $"allocated {after - before} bytes");
        Assert.Equal(1L << 30, f.FileSize());
        var buf = new byte[8];
        Assert.Equal(8, f.Read(1, buf));
        Assert.Equal(new byte[] { 2, 3, 0, 0, 0, 0, 0, 0 }, buf);
        Assert.Equal(8, f.Read((1L << 30) - 8, buf));
        Assert.All(buf, b => Assert.Equal(0, b));

        f.Write(5000, [9]);
        f.Truncate(2);
        f.Truncate(6000);
        Assert.Equal(8, f.Read(0, buf));
        Assert.Equal(new byte[] { 1, 2, 0, 0, 0, 0, 0, 0 }, buf);
        Assert.Equal(1, f.Read(5000, buf.AsSpan(0, 1)));
        Assert.Equal(0, buf[0]);
    }
}
