using System.IO;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;
using PagerClass = RecursiveAI.OneLite.Storage.Pager.Pager;

namespace RecursiveAI.OneLite.Tests.Pager;

public sealed class BusyRetryTests
{
    [Theory]
    [InlineData(JournalMode.Delete)]
    [InlineData(JournalMode.Persist)]
    public void AfterACommitFailsWithBusyTheNextTransactionGrowsTheFileCorrectly(JournalMode jm)
    {
        string dir = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "onelite-busy-retry");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"b-{jm}.db");
        File.Delete(file);
        File.Delete(file + "-journal");
        try
        {
            var vfs = new FileVfs();
            PagerOptions o = Kit.Opts(1024, jm, SynchronousMode.Off, 500);
            using PagerClass a = PagerClass.Open(vfs, file, o);
            using PagerClass b = PagerClass.Open(vfs, file, o);
            a.BeginReadSync();
            a.BeginWriteSync();
            PageFrame p1 = a.GetSync(1);
            a.MakeWritableSync(p1);
            a.Release(p1);
            for (int i = 0; i < 10; i++)
            {
                PageFrame f = a.AllocateSync();
                Kit.Fill(f, 1);
                a.Release(f);
            }

            a.CommitSync();
            a.EndReadSync();
            Assert.Equal(11u, a.PageCount);
            uint c1 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(File.ReadAllBytes(file).AsSpan(24));

            b.BeginReadSync();
            a.BeginReadSync();
            a.BeginWriteSync();
            for (int i = 0; i < 5; i++)
            {
                PageFrame f = a.AllocateSync();
                Kit.Fill(f, 2);
                a.Release(f);
            }

            Assert.Equal(16u, a.PageCount);
            OneLiteException e = Assert.Throws<OneLiteException>(a.CommitSync);
            Assert.Equal(ResultCode.Busy, e.Code);
            a.RollbackSync();
            Assert.Equal(11u, a.PageCount);
            a.EndReadSync();
            b.EndReadSync();

            a.BeginReadSync();
            a.BeginWriteSync();
            for (int i = 0; i < 5; i++)
            {
                PageFrame f = a.AllocateSync();
                Kit.Fill(f, 3);
                a.Release(f);
            }

            Assert.Equal(16u, a.PageCount);
            a.CommitSync();
            a.EndReadSync();
            Assert.Equal(16L * 1024, new FileInfo(file).Length);
            byte[] raw = File.ReadAllBytes(file);
            Assert.Equal(c1 + 1, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(24)));
            Assert.Equal(16u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(28)));
            using PagerClass c = PagerClass.Open(vfs, file, o);
            c.BeginReadSync();
            Assert.Equal(16u, c.PageCount);
            c.EndReadSync();
            b.BeginReadSync();
            Assert.Equal(16u, b.PageCount);
            b.EndReadSync();
        }
        finally
        {
            File.Delete(file);
            File.Delete(file + "-journal");
        }
    }
}
