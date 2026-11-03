using System;
using System.Collections.Generic;
using System.Linq;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Record;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Trees;

/// <summary>
/// An insert right after a probe for the same rowid reuses the probe's position. That is only right while the cursor has stood there
/// without a break: the sequence probe, delete, step, insert once put the insert on the wrong entry.
/// </summary>
public sealed class ProbeReuseTests
{
    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    public void InsertAfterProbeDeleteAndStepKeepsEveryOtherRow(int pageSize)
    {
        var src = ImagePageSource.Create(pageSize, 0, TextEncoding.Utf8, 0);
        var ctx = new BtContext(src, src.Encoding);
        uint root = 0;
        Drive.Run(src, () => BtAdmin.CreateTree(ctx, true, out root));
        var tree = BTree.Table(ctx, root);
        using BtCursor cur = tree.OpenCursor();
        using BtCursor scan = tree.OpenCursor();
        var model = new SortedDictionary<long, byte[]>();
        var rnd = new Random(7);
        for (long id = 1; id <= 400; id++)
        {
            byte[] rec = BTreeModelTests.Rec(id, 10, rnd);
            Drive.Ok(src, cur, () => cur.Insert(id, rec));
            model[id] = rec;
        }

        for (int round = 0; round < 300; round++)
        {
            long id = model.Keys.ElementAt(rnd.Next(model.Count));
            // probe (the position is remembered), delete the row, step to the row after it, then write the same rowid again
            Drive.Ok(src, cur, () => cur.MoveToRowid(id, out _));
            Assert.Equal(CursorState.Valid, cur.State);
            Drive.Ok(src, cur, () => cur.Delete());
            model.Remove(id);
            Drive.Ok(src, cur, () => cur.Next());
            byte[] again = BTreeModelTests.Rec(id, 10, rnd, tag: round + 1);
            Drive.Ok(src, cur, () => cur.Insert(id, again));
            model[id] = again;
        }

        BTreeModelTests.Verify(src, ctx, tree, scan, model);
        Assert.Equal(400, model.Count);
    }
}
