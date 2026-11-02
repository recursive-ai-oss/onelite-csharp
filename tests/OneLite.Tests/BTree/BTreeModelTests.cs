using RecursiveAI.OneLite.Storage;
using System.Collections.Generic;
using System.Linq;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Tests.Trees;

/// <summary>Drives restartable operations to completion over an <see cref="ImagePageSource"/>.</summary>
internal static class Drive
{
    public static void Complete(IBtPageSource src)
    {
        if (src is ImagePageSource img)
        {
            img.Complete();
        }
        else
        {
            ((PagerPageSource)src).Pager.ExecuteSync();
        }
    }

    public static BtStatus Run(IBtPageSource src, Func<BtStatus> op)
    {
        for (int guard = 0; guard < 1_000_000; guard++)
        {
            BtStatus s = op();
            if (s != BtStatus.Pending)
            {
                return s;
            }

            Complete(src);
        }

        throw new InvalidOperationException("operation never finished (livelock under injection)");
    }

    public static void Ok(IBtPageSource src, BtCursor c, Func<BtStatus> op)
    {
        BtStatus s = Run(src, op);
        if (s != BtStatus.Ok)
        {
            throw new InvalidOperationException("operation failed: " + c.Error + (c.Error is OneLiteException oe ? " [" + oe.Detail + "]" : ""));
        }
    }
}

/// <summary>A randomized mix of inserts, overwrites and deletes against a dictionary model, with structural checks.</summary>
public sealed class BTreeModelTests
{
    public static IEnumerable<object[]> Configs()
    {
        foreach (int ps in new[] { 512, 1024, 4096, 65536 })
        {
            foreach (int av in new[] { 0, 1, 2 })
            {
                foreach (int y in new[] { 0, 1, 3 })
                {
                    yield return new object[] { ps, av, y };
                }
            }
        }
    }

    internal static byte[] Rec(long id, int len, Random r, int tag = 0)
    {
        var blob = new byte[len];
        r.NextBytes(blob);
        return RecordWriter.ToArray([Mem.FromInt(id), Mem.FromBlob(blob), Mem.FromInt(tag)]);
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public void RowidTable(int pageSize, int autoVacuum, int yieldEvery)
    {
        var src = ImagePageSource.Create(pageSize, 0, TextEncoding.Utf8, autoVacuum);
        src.YieldEvery = yieldEvery;
        var ctx = new BtContext(src, src.Encoding);
        uint root = 0;
        Drive.Run(src, () => BtAdmin.CreateTree(ctx, true, out root));
        var tree = BTree.Table(ctx, root);
        using BtCursor cur = tree.OpenCursor();
        using BtCursor scan = tree.OpenCursor();
        var model = new SortedDictionary<long, byte[]>();
        var rnd = new Random(pageSize * 31 + autoVacuum * 7 + 1);
        int ops = pageSize >= 4096 ? 1500 : 2500;
        for (int i = 0; i < ops; i++)
        {
            int kind = rnd.Next(10);
            if (kind < 6 || model.Count < 5)
            {
                long id = rnd.Next(3) == 0 ? rnd.NextInt64(-100, 100000) : (model.Count == 0 ? 1 : model.Keys.Max() + 1 + rnd.Next(3));
                int len = rnd.Next(20) == 0 ? rnd.Next(pageSize * 3) : rnd.Next(60);
                byte[] rec = Rec(id, len, rnd);
                Drive.Ok(src, cur, () => cur.Insert(id, rec));
                model[id] = rec;
            }
            else
            {
                long id = model.Keys.ElementAt(rnd.Next(model.Count));
                Drive.Ok(src, cur, () => cur.MoveToRowid(id, out _));
                Assert.Equal(CursorState.Valid, cur.State);
                Drive.Ok(src, cur, () => cur.Delete());
                model.Remove(id);
            }

            if (i % 200 == 199)
            {
                Verify(src, ctx, tree, scan, model);
            }
        }

        Verify(src, ctx, tree, scan, model);
        // drain
        foreach (long id in model.Keys.ToArray())
        {
            Drive.Ok(src, cur, () => cur.MoveToRowid(id, out _));
            Drive.Ok(src, cur, () => cur.Delete());
            model.Remove(id);
        }

        Verify(src, ctx, tree, scan, model);
        Assert.Equal(0, src.PinnedPages);
    }

    internal static void Verify(ImagePageSource src, BtContext ctx, BTree tree, BtCursor scan, SortedDictionary<long, byte[]> model)
    {
        scan.Reset();
        Drive.Ok(src, scan, () => scan.First());
        var rv = new RecordView();
        int count = 0;
        var e = model.GetEnumerator();
        while (scan.State == CursorState.Valid)
        {
            Assert.True(e.MoveNext(), "tree has more rows than the model");
            Assert.Equal(e.Current.Key, scan.Rowid);
            Drive.Ok(src, scan, scan.LoadPayload);
            Assert.True(scan.Payload.SequenceEqual(e.Current.Value), $"payload of {e.Current.Key} differs");
            Assert.True(scan.ParseRecord(rv));
            count++;
            Drive.Ok(src, scan, scan.Next);
        }

        Assert.Equal(model.Count, count);
        scan.Reset();
        var chk = new BtIntegrity(ctx, src.Complete);
        Assert.True(chk.Check([BTree.Table(ctx, 1), tree]), string.Join("\n", chk.Problems));
        Assert.Equal(0, src.PinnedPages);
    }
}
