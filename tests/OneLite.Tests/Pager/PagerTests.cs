using System;
using System.Buffers.Binary;
using System.Linq;
using System.Threading.Tasks;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;
using PagerClass = RecursiveAI.OneLite.Storage.Pager.Pager;

namespace RecursiveAI.OneLite.Tests.Pager;

public class PagerTests
{
    [Theory]
    [InlineData(512, 0)]
    [InlineData(4096, 0)]
    [InlineData(4096, 16)]
    [InlineData(65536, 0)]
    public void CreateCommitReopenReadsTheSamePages(int pageSize, int reserved)
    {
        MemoryVfs vfs = Kit.Prepare(12, pageSize, reserved);
        using PagerClass p = PagerClass.Open(vfs, Kit.Db, Kit.Opts(pageSize: 4096));
        p.BeginReadSync();
        Assert.Equal(pageSize, p.PageSize);
        Assert.Equal(reserved, p.ReservedBytes);
        Assert.Equal(12u, p.PageCount);
        for (uint n = 2; n <= 12; n++)
        {
            PageFrame f = p.GetSync(n);
            var expect = new PageFrame_(n, pageSize);
            Assert.True(expect.Matches(f.Data, 0));
            p.Release(f);
        }

        PageFrame p1 = p.GetSync(1);
        Assert.Equal(1u, DatabaseHeader.GetUInt32(p1.Data, DatabaseHeader.ChangeCounterOffset) - 0u);
        Assert.Equal(12u, DatabaseHeader.GetUInt32(p1.Data, DatabaseHeader.DatabaseSizeOffset));
        p.Release(p1);
        p.EndReadSync();
        Assert.False(Kit.Exists(vfs, Kit.Db + "-journal"));
    }

    private readonly struct PageFrame_(uint n, int size)
    {
        public bool Matches(byte[] data, int gen)
        {
            for (int i = 0; i < size; i++)
            {
                if (data[i] != (byte)((n * 31 + gen * 7 + i) & 0xFF))
                {
                    return false;
                }
            }

            return true;
        }
    }

    [Fact]
    public void CachedGetAndReleaseAllocateNothing()
    {
        MemoryVfs vfs = Kit.Prepare(8);
        using PagerClass p = PagerClass.Open(vfs, Kit.Db, Kit.Opts());
        p.BeginReadSync();
        p.Release(p.GetSync(3));

        // Under a loaded parallel test run the runtime can allocate on this thread for its own reasons (tiering, timers), so the
        // smallest of three repetitions is the loop's own cost.
        long best = long.MaxValue;
        for (int rep = 0; rep < 3; rep++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++)
            {
                PagerStatus s = p.Get(3, GetOptions.None, out PageFrame? f);
                if (s != PagerStatus.Ok)
                {
                    Assert.Fail("expected a cache hit");
                }

                p.Release(f!);
            }

            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Equal(0, best);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(100)]
    public void RollbackRestoresOldPagesWithAndWithoutSpill(int cache)
    {
        MemoryVfs vfs = Kit.Prepare(40);
        byte[] before = Kit.ReadFile(vfs, Kit.Db);
        using PagerClass p = PagerClass.Open(vfs, Kit.Db, Kit.Opts(cache: cache));
        p.BeginReadSync();
        p.BeginWriteSync();
        Kit.Rewrite(p, Kit.Range(2, 38), 5);
        for (int i = 0; i < 6; i++)
        {
            p.Release(p.AllocateSync());
        }

        p.RollbackSync();
        Assert.Equal(40u, p.PageCount);
        p.EndReadSync();
        byte[] after = Kit.ReadFile(vfs, Kit.Db);
        Assert.True(before.AsSpan().SequenceEqual(after), Kit.Describe(before, after));
        Assert.False(Kit.Exists(vfs, Kit.Db + "-journal"));
    }

    [Fact]
    public void SpillWithAReaderInAnotherPagerIsBusy()
    {
        MemoryVfs vfs = Kit.Prepare(40);
        using PagerClass reader = PagerClass.Open(vfs, Kit.Db, Kit.Opts());
        reader.BeginReadSync();
        using PagerClass w = PagerClass.Open(vfs, Kit.Db, Kit.Opts(cache: 3));
        w.BeginReadSync();
        w.BeginWriteSync();
        OneLiteException e = Assert.Throws<OneLiteException>(() => Kit.Rewrite(w, Kit.Range(2, 30), 1));
        Assert.Equal(ResultCode.Busy, e.Code);
        w.RollbackSync();
        w.EndReadSync();
        reader.EndReadSync();
    }

    [Fact]
    public void SecondWriterIsBusyAndReaderMayContinue()
    {
        MemoryVfs vfs = Kit.Prepare(6);
        using PagerClass a = PagerClass.Open(vfs, Kit.Db, Kit.Opts());
        using PagerClass b = PagerClass.Open(vfs, Kit.Db, Kit.Opts());
        a.BeginReadSync();
        a.BeginWriteSync();
        b.BeginReadSync();
        Assert.Equal(ResultCode.Busy, Assert.Throws<OneLiteException>(b.BeginWriteSync).Code);
        PageFrame f = b.GetSync(2);
        b.Release(f);
        a.RollbackSync();
        a.EndReadSync();
        b.EndReadSync();
    }

    [Fact]
    public void SavepointsNestAndRollBackToTheirOwnStart()
    {
        MemoryVfs vfs = Kit.Prepare(10);
        using PagerClass p = PagerClass.Open(vfs, Kit.Db, Kit.Opts());
        p.BeginReadSync();
        p.BeginWriteSync();
        Kit.Rewrite(p, [2u, 3u], 1);
        int s1 = p.OpenSavepoint();
        Kit.Rewrite(p, [3u, 4u], 2);
        int s2 = p.OpenSavepoint();
        Kit.Rewrite(p, [4u, 5u], 3);
        p.Release(p.AllocateSync());
        Assert.Equal(11u, p.PageCount);
        p.RollbackSavepointSync(s2);
        Assert.Equal(10u, p.PageCount);
        Check(p, 4, 2);
        Check(p, 5, 0);
        Check(p, 3, 2);
        p.RollbackSavepointSync(s1);
        Check(p, 3, 1);
        Check(p, 4, 0);
        Check(p, 2, 1);
        p.ReleaseSavepoint(s1);
        p.CommitSync();
        p.EndReadSync();

        static void Check(PagerClass p, uint n, int gen)
        {
            PageFrame f = p.GetSync(n);
            Assert.True(new PageFrame_(n, p.PageSize).Matches(f.Data, gen), $"page {n} should be generation {gen}");
            p.Release(f);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreelistAllocatesFreesAndReuses(bool autoVacuumOff)
    {
        _ = autoVacuumOff;
        MemoryVfs vfs = Kit.Prepare(30);
        using PagerClass p = PagerClass.Open(vfs, Kit.Db, Kit.Opts());
        p.BeginReadSync();
        p.BeginWriteSync();
        for (uint n = 5; n <= 28; n++)
        {
            p.FreeSync(n);
        }

        PageFrame p1 = p.GetSync(1);
        Assert.Equal(24u, DatabaseHeader.GetUInt32(p1.Data, DatabaseHeader.FreelistCountOffset));
        p.Release(p1);
        var got = new System.Collections.Generic.List<uint>();
        for (int i = 0; i < 26; i++)
        {
            PageFrame f = p.AllocateSync();
            Assert.All(f.Data, static b => Assert.Equal(0, b));
            got.Add(f.PageNumber);
            p.Release(f);
        }

        Assert.Equal(Enumerable.Range(5, 24).Select(static i => (uint)i).Order(), got.Take(24).Order());
        Assert.Equal(new uint[] { 31, 32 }, got.Skip(24));
        p.CommitSync();
        p.EndReadSync();
        p.BeginReadSync();
        Assert.Equal(32u, p.PageCount);
        PageFrame h = p.GetSync(1);
        Assert.Equal(0u, DatabaseHeader.GetUInt32(h.Data, DatabaseHeader.FreelistCountOffset));
        p.Release(h);
        p.EndReadSync();
    }

    [Fact]
    public void FreedPagesThatWereInUseAreRestoredByRollback()
    {
        MemoryVfs vfs = Kit.Prepare(20);
        byte[] before = Kit.ReadFile(vfs, Kit.Db);
        using PagerClass p = PagerClass.Open(vfs, Kit.Db, Kit.Opts(cache: 5));
        p.BeginReadSync();
        p.BeginWriteSync();
        for (uint n = 3; n <= 18; n++)
        {
            p.FreeSync(n);
        }

        for (int i = 0; i < 10; i++)
        {
            PageFrame f = p.AllocateSync();
            Kit.Fill(f, 9);
            p.Release(f);
        }

        p.RollbackSync();
        p.EndReadSync();
        Assert.True(before.AsSpan().SequenceEqual(Kit.ReadFile(vfs, Kit.Db)));
    }

    [Fact]
    public void FreelistWithManyTrunks()
    {
        MemoryVfs vfs = Kit.Prepare(300);
        using PagerClass p = PagerClass.Open(vfs, Kit.Db, Kit.Opts());
        p.BeginReadSync();
        p.BeginWriteSync();
        for (uint n = 2; n <= 300; n++)
        {
            p.FreeSync(n);
        }

        p.CommitSync();
        PageFrame h = p.GetSync(1);
        Assert.Equal(299u, DatabaseHeader.GetUInt32(h.Data, DatabaseHeader.FreelistCountOffset));
        p.Release(h);
        p.BeginWriteSync();
        for (int i = 0; i < 299; i++)
        {
            p.Release(p.AllocateSync());
        }

        Assert.Equal(300u, p.PageCount);
        p.CommitSync();
        p.EndReadSync();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void YieldAtEveryNthGetChangesNothing(int n)
    {
        byte[] plain = Workload(0);
        byte[] yielded = Workload(n);
        Assert.True(plain.AsSpan().SequenceEqual(yielded), Kit.Describe(plain, yielded));

        static byte[] Workload(int yield)
        {
            MemoryVfs vfs = Kit.Prepare(30);
            using PagerClass p = PagerClass.Open(vfs, Kit.Db, Kit.Opts(cache: 6, yield: yield));
            p.BeginReadSync();
            p.BeginWriteSync();
            Kit.Rewrite(p, Kit.Range(2, 25), 3);
            int sp = p.OpenSavepoint();
            Kit.Rewrite(p, Kit.Range(10, 5), 4);
            for (uint k = 20; k < 24; k++)
            {
                p.FreeSync(k);
            }

            p.RollbackSavepointSync(sp);
            p.Release(p.AllocateSync());
            p.CommitSync();
            p.EndReadSync();
            return Kit.ReadFile(vfs, Kit.Db);
        }
    }

    [Fact]
    public async Task AsyncDriverGivesTheSameFile()
    {
        MemoryVfs a = Kit.Prepare(20);
        MemoryVfs b = Kit.Prepare(20);
        using (PagerClass p = PagerClass.Open(a, Kit.Db, Kit.Opts(cache: 5)))
        {
            p.BeginReadSync();
            p.BeginWriteSync();
            Kit.Rewrite(p, Kit.Range(2, 18), 2);
            p.CommitSync();
            p.EndReadSync();
        }

        await using (var _ = new AsyncDisposeNoop())
        {
            using PagerClass p = await PagerClass.OpenAsync(b, Kit.Db, Kit.Opts(cache: 5), TestContext.Current.CancellationToken);
            await p.BeginReadAsync(TestContext.Current.CancellationToken);
            await p.BeginWriteAsync(TestContext.Current.CancellationToken);
            foreach (uint n in Kit.Range(2, 18))
            {
                PageFrame f = await p.GetAsync(n, ct: TestContext.Current.CancellationToken);
                await p.MakeWritableAsync(f, TestContext.Current.CancellationToken);
                Kit.Fill(f, 2);
                p.Release(f);
            }

            await p.CommitAsync(TestContext.Current.CancellationToken);
            await p.EndReadAsync(TestContext.Current.CancellationToken);
        }

        byte[] x = Kit.ReadFile(a, Kit.Db);
        byte[] y = Kit.ReadFile(b, Kit.Db);
        Assert.True(x.AsSpan().SequenceEqual(y), Kit.Describe(x, y));
    }

    private sealed class AsyncDisposeNoop : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(JournalMode.Delete)]
    [InlineData(JournalMode.Truncate)]
    [InlineData(JournalMode.Persist)]
    [InlineData(JournalMode.Memory)]
    [InlineData(JournalMode.Off)]
    public void EveryJournalModeCommitsTheSameDatabase(JournalMode jm)
    {
        MemoryVfs vfs = Kit.Prepare(20);
        MemoryVfs reference = Kit.Prepare(20);
        foreach ((MemoryVfs v, JournalMode m) in new[] { (vfs, jm), (reference, JournalMode.Delete) })
        {
            using PagerClass p = PagerClass.Open(v, Kit.Db, Kit.Opts(jm: m, cache: 4));
            p.BeginReadSync();
            p.BeginWriteSync();
            Kit.Rewrite(p, Kit.Range(2, 15), 6);
            p.CommitSync();
            p.BeginWriteSync();
            Kit.Rewrite(p, Kit.Range(5, 5), 7);
            if (m != JournalMode.Off)
            {
                p.RollbackSync();
            }
            else
            {
                p.CommitSync();
            }

            p.EndReadSync();
        }

        if (jm != JournalMode.Off)
        {
            byte[] x = Kit.ReadFile(vfs, Kit.Db);
            byte[] y = Kit.ReadFile(reference, Kit.Db);
            Assert.True(x.AsSpan().SequenceEqual(y), Kit.Describe(x, y));
        }

        Assert.Equal(jm is JournalMode.Truncate or JournalMode.Persist, Kit.Exists(vfs, Kit.Db + "-journal"));
    }

    [Fact]
    public void HostileHeadersGiveTypedErrors()
    {
        var rnd = new Random(5);
        MemoryVfs good = Kit.Prepare(4);
        byte[] image = Kit.ReadFile(good, Kit.Db);
        for (int i = 0; i < 400; i++)
        {
            byte[] m = (byte[])image.Clone();
            for (int k = 0; k < 1 + rnd.Next(4); k++)
            {
                m[rnd.Next(100)] = (byte)rnd.Next(256);
            }

            var v = new MemoryVfs();
            using (IVfsFile f = v.Open(Kit.Db, VfsOpenOptions.ReadWrite | VfsOpenOptions.Create))
            {
                f.Write(0, m);
            }

            using PagerClass p = PagerClass.Open(v, Kit.Db, Kit.Opts());
            try
            {
                p.BeginReadSync();
                for (uint n = 1; n <= Math.Min(p.PageCount, 8u); n++)
                {
                    try
                    {
                        p.Release(p.GetSync(n));
                    }
                    catch (OneLiteException e)
                    {
                        Assert.Contains(e.Code, new[] { ResultCode.Corrupt, ResultCode.NotADb, ResultCode.IoErr });
                    }
                }

                p.EndReadSync();
            }
            catch (OneLiteException e)
            {
                Assert.Contains(e.Code, new[] { ResultCode.Corrupt, ResultCode.NotADb, ResultCode.CantOpen });
            }
        }

        Assert.Equal(ResultCode.NotADb, Assert.Throws<OneLiteException>(() => DatabaseHeader.Parse(new byte[100])).Code);
        _ = BinaryPrimitives.ReadInt32BigEndian(image);
    }
}
