using RecursiveAI.OneLite.Storage;
using System;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Tests.Trees;

/// <summary>
/// The cached read path must not allocate: scanning with <c>Next</c>, seeking by rowid or key, and decoding integers, reals and blob
/// spans out of the page. Measured with the thread's allocation counter after a warm-up pass that sizes every reusable buffer.
/// </summary>
public sealed class HotPathAllocationTests
{
    private const int Rows = 6000;

    // The assertion library allocates even when it passes, so the measured loops use a plain check.
    private static void Check(bool ok)
    {
        if (!ok)
        {
            throw new InvalidOperationException("unexpected result in the measured loop");
        }
    }

    private static (ImagePageSource Src, BTree Table, BTree Index) Build()
    {
        var src = ImagePageSource.Create(4096, 0, TextEncoding.Utf8, 0);
        var ctx = new BtContext(src, TextEncoding.Utf8);
        uint tr = 0, ix = 0;
        Drive.Run(src, () => BtAdmin.CreateTree(ctx, true, out tr));
        Drive.Run(src, () => BtAdmin.CreateTree(ctx, false, out ix));
        var table = BTree.Table(ctx, tr);
        var index = BTree.Index(ctx, ix, new KeyInfo(TextEncoding.Utf8, [null, null]));
        using BtCursor t = table.OpenCursor();
        using BtCursor i = index.OpenCursor();
        var blob = new byte[24];
        for (int n = 1; n <= Rows; n++)
        {
            blob[0] = (byte)n;
            byte[] rec = RecordWriter.ToArray([Mem.FromInt(n * 1000L), Mem.FromReal(n / 3.0), Mem.FromBlob(blob)]);
            long rowid = n;
            Drive.Ok(src, t, () => t.Insert(rowid, rec));
            Mem[] key = [Mem.FromInt(n * 7L), Mem.FromInt(n)];
            byte[] krec = RecordWriter.ToArray(key);
            Drive.Ok(src, i, () => i.Insert(krec, key));
        }

        return (src, table, index);
    }

    [Fact]
    public void NextLoopAndRowidSeekAllocateNothing()
    {
        var (src, table, _) = Build();
        using BtCursor c = table.OpenCursor();
        var view = new RecordView();

        long Scan()
        {
            long sum = 0;
            BtStatus s = c.First();
            Check(s == BtStatus.Ok);
            while (c.State == CursorState.Valid)
            {
                Check(c.ParseRecord(view));
                sum += view.GetInt64(0) + (long)view.GetDouble(1) + view.GetBytes(2)[0];
                Check(c.Next() == BtStatus.Ok);
            }

            return sum;
        }

        long Seeks()
        {
            long sum = 0;
            for (int n = 1; n <= Rows; n += 7)
            {
                Check(c.MoveToRowid(n, out int cmp) == BtStatus.Ok);
                Check(cmp == 0);
                Check(c.ParseRecord(view));
                sum += view.GetInt64(0) + c.Rowid;
            }

            return sum;
        }

        long expect = Scan();
        long expectSeek = Seeks(); // warm-up: sizes the path, scratch and view arrays
        // Tiered compilation can allocate once while it promotes a method; a real per-call allocation shows in every repetition.
        long minScan = long.MaxValue, minSeek = long.MaxValue;
        for (int rep = 0; rep < 5; rep++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long a = Scan();
            long afterScan = GC.GetAllocatedBytesForCurrentThread();
            long b = Seeks();
            long afterSeek = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(expect, a);
            Assert.Equal(expectSeek, b);
            minScan = Math.Min(minScan, afterScan - before);
            minSeek = Math.Min(minSeek, afterSeek - afterScan);
        }

        Assert.Equal(0, minScan);
        Assert.Equal(0, minSeek);
        GC.KeepAlive(src);
    }

    [Fact]
    public void IndexKeySeekAllocatesNothing()
    {
        var (src, _, index) = Build();
        using BtCursor c = index.OpenCursor();
        var key = new Mem[2];

        long Seeks()
        {
            long found = 0;
            for (int n = 1; n <= Rows; n += 5)
            {
                key[0] = Mem.FromInt(n * 7L);
                key[1] = Mem.FromInt(n);
                Check(c.MoveToKey(key, 0, out int cmp) == BtStatus.Ok);
                if (cmp == 0)
                {
                    found++;
                }
            }

            return found;
        }

        long expect = Seeks();
        Assert.Equal((Rows + 4) / 5, expect);
        long min = long.MaxValue;
        for (int rep = 0; rep < 5; rep++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long again = Seeks();
            long used = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(expect, again);
            min = Math.Min(min, used);
        }

        Assert.Equal(0, min);
        GC.KeepAlive(src);
    }

    private static void Settle(StorageEngine eng, Func<BtStatus> call)
    {
        BtStatus s;
        while ((s = call()) == BtStatus.Pending)
        {
            eng.Pager.ExecuteSync();
        }

        Check(s == BtStatus.Ok);
    }

    /// <summary>The same promise through the real pager: a cached scan, a point lookup loop and inserts that do not split allocate nothing.</summary>
    [Fact]
    public void ScanLookupAndInsertWithoutSplitThroughThePagerAllocateNothing()
    {
        using StorageEngine eng = StorageEngine.Open(new RecursiveAI.OneLite.Storage.Vfs.MemoryVfs(), "hot.db", PagerBTreeTests.Options(4096, TextEncoding.Utf8, 0, RecursiveAI.OneLite.Storage.Pager.JournalMode.Delete, 0, 0, cache: 2000));
        eng.BeginRead();
        eng.BeginWrite();
        uint root = 0;
        Check(eng.Run(() => BtAdmin.CreateTree(eng.Context, true, out root)) == BtStatus.Ok);
        using BtCursor c = BTree.Table(eng.Context, root).OpenCursor();
        var blob = new byte[24];
        for (int n = 1; n <= Rows; n++)
        {
            blob[0] = (byte)n;
            byte[] rec = RecordWriter.ToArray([Mem.FromInt(n * 1000L), Mem.FromReal(n / 3.0), Mem.FromBlob(blob)]);
            long rowid = n;
            Check(eng.Run(() => c.Insert(rowid, rec)) == BtStatus.Ok);
        }

        c.Reset();
        eng.Commit();
        var view = new RecordView();
        Func<BtStatus> first = c.First, nextRow = c.Next; // delegates made once: a method group converted per call would allocate

        long Scan()
        {
            long sum = 0;
            Settle(eng, first);
            while (c.State == CursorState.Valid)
            {
                Check(c.ParseRecord(view));
                sum += view.GetInt64(0) + (long)view.GetDouble(1) + view.GetBytes(2)[0];
                Settle(eng, nextRow);
            }

            return sum;
        }

        long Seeks()
        {
            long sum = 0;
            for (int n = 1; n <= Rows; n += 7)
            {
                int cmp = 1;
                BtStatus s;
                while ((s = c.MoveToRowid(n, out cmp)) == BtStatus.Pending)
                {
                    eng.Pager.ExecuteSync();
                }

                Check(s == BtStatus.Ok && cmp == 0);
                Check(c.ParseRecord(view));
                sum += view.GetInt64(0) + c.Rowid;
            }

            return sum;
        }

        long expect = Scan();
        long expectSeek = Seeks();
        long minScan = long.MaxValue, minSeek = long.MaxValue;
        for (int rep = 0; rep < 5; rep++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long a = Scan();
            long afterScan = GC.GetAllocatedBytesForCurrentThread();
            long b = Seeks();
            long afterSeek = GC.GetAllocatedBytesForCurrentThread();
            Check(a == expect && b == expectSeek);
            minScan = Math.Min(minScan, afterScan - before);
            minSeek = Math.Min(minSeek, afterSeek - afterScan);
        }

        Assert.Equal(0, minScan);
        Assert.Equal(0, minSeek);

        // inserts into the right-most leaf, which has room for all of them: no balance, no new page
        c.Reset();
        eng.BeginWrite();
        var small = RecordWriter.ToArray([Mem.FromInt(1), Mem.FromInt(2)]);
        long next = Rows + 1;
        Check(eng.Run(() => c.Insert(next++, small)) == BtStatus.Ok); // warm-up: journals the leaf, sizes the buffers
        long minInsert = long.MaxValue;
        for (int rep = 0; rep < 5; rep++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int k = 0; k < 8; k++)
            {
                BtStatus s;
                while ((s = c.Insert(next, small)) == BtStatus.Pending)
                {
                    eng.Pager.ExecuteSync();
                }

                Check(s == BtStatus.Ok);
                next++;
            }

            minInsert = Math.Min(minInsert, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Equal(0, minInsert);
        c.Reset();
        eng.Commit();
        eng.EndRead();
    }
}
