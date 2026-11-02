using System;
using System.Buffers.Binary;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;
using PagerClass = RecursiveAI.OneLite.Storage.Pager.Pager;

namespace RecursiveAI.OneLite.Tests.Pager;

/// <summary>Hostile database and journal files end in a typed error or success, never another exception or a huge allocation (found by the fuzz loop).</summary>
public class HostileFileTests
{
    private static byte[] Snapshot(MemoryVfs vfs, string path)
    {
        using IVfsFile f = vfs.Open(path, VfsOpenOptions.ReadOnly);
        byte[] b = new byte[f.FileSize()];
        f.Read(0, b);
        return b;
    }

    private static void Put(MemoryVfs vfs, string path, byte[] bytes)
    {
        using IVfsFile f = vfs.Open(path, VfsOpenOptions.ReadWrite | VfsOpenOptions.Create);
        f.Write(0, bytes);
    }

    /// <summary>
    /// Minimized fuzz repro: the header's page count (with a matching version-valid-for, so it is trusted) is far larger than the file. A write
    /// transaction then journals pages with numbers in the billions; the journaled-page set must not allocate a bit per page number.
    /// </summary>
    [Fact]
    public void HeaderClaimingBillionsOfPagesDoesNotExhaustMemory()
    {
        MemoryVfs good = Kit.Prepare(12);
        byte[] db = Snapshot(good, Kit.Db);
        BinaryPrimitives.WriteUInt32BigEndian(db.AsSpan(28), 0xFFFF00FF);
        BinaryPrimitives.WriteUInt32BigEndian(db.AsSpan(32), 0xFF000005); // freelist trunk
        BinaryPrimitives.WriteUInt32BigEndian(db.AsSpan(36), 2);
        // version-valid-for equals the change counter, so the size field is trusted (fileformat2 §1.3.7)
        db.AsSpan(24, 4).CopyTo(db.AsSpan(92));

        var vfs = new MemoryVfs();
        Put(vfs, Kit.Db, db);
        long before = GC.GetTotalAllocatedBytes(true);
        try
        {
            using PagerClass p = PagerClass.Open(vfs, Kit.Db, Kit.Opts());
            p.BeginReadSync();
            try
            {
                p.BeginWriteSync();
                for (int i = 0; i < 6; i++)
                {
                    p.Release(p.AllocateSync());
                }
            }
            catch (OneLiteException e)
            {
                Assert.True(e.Code is ResultCode.Corrupt or ResultCode.NotADb or ResultCode.IoErr or ResultCode.Full, e.Message);
            }
            finally
            {
                if (p.InWriteTransaction)
                {
                    p.RollbackSync();
                }
            }

            p.EndReadSync();
        }
        catch (OneLiteException e)
        {
            Assert.True(e.Code is ResultCode.Corrupt or ResultCode.NotADb, e.Message);
        }

        Assert.True(GC.GetTotalAllocatedBytes(true) - before < 64 << 20);
    }

    [Theory]
    [InlineData(0xFFFFFFFFu, 512u)]
    [InlineData(0x7FFFFFFFu, 65536u)]
    [InlineData(0x00FFFFFFu, 512u)] // 8 GiB claimed
    public void JournalClaimingAHugeDatabaseIsRefusedBeforeAnythingIsWritten(uint initialPages, uint pageSize)
    {
        MemoryVfs vfs = Kit.Prepare(12);
        byte[] before = Snapshot(vfs, Kit.Db);
        var j = new byte[512 + 4 + 512 + 4];
        JournalFormat.WriteHeader(j.AsSpan(0, 512), withMagic: true, 1, 0x1234, initialPages, 512, (int)pageSize);
        BinaryPrimitives.WriteUInt32BigEndian(j.AsSpan(512), 3);
        Put(vfs, Kit.Db + "-journal", j);

        using PagerClass p = PagerClass.Open(vfs, Kit.Db, Kit.Opts());
        OneLiteException e = Assert.Throws<OneLiteException>(p.BeginReadSync);
        Assert.Equal(ResultCode.Corrupt, e.Code);
        Assert.Equal(before, Snapshot(vfs, Kit.Db));
    }

    [Fact]
    public void TruncatedAndGarbageJournalsAreIgnoredOrTyped()
    {
        var rnd = new Random(7);
        for (int round = 0; round < 300; round++)
        {
            MemoryVfs vfs = Kit.Prepare(12);
            byte[] j = new byte[rnd.Next(1, 3000)];
            rnd.NextBytes(j);
            if (round % 2 == 0 && j.Length >= 28)
            {
                JournalFormat.WriteHeader(j.AsSpan(0, 28), withMagic: true, (uint)rnd.Next(0, 6), 5, 12, 512, 512);
            }

            Put(vfs, Kit.Db + "-journal", j);
            try
            {
                using PagerClass p = PagerClass.Open(vfs, Kit.Db, Kit.Opts());
                p.BeginReadSync();
                p.EndReadSync();
            }
            catch (OneLiteException e)
            {
                Assert.True(e.Code is ResultCode.Corrupt or ResultCode.NotADb or ResultCode.IoErr, e.Message);
            }
        }
    }
}
