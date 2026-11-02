using RecursiveAI.OneLite.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Fuzz;

/// <summary>What the targets need to finish a Pending call: the text encoding and the action that performs the outstanding I/O.</summary>
internal sealed class Pump(TextEncoding encoding, Action complete, Func<Exception?> lastError)
{
    /// <summary>The page source's last failure: a call that returned Error reports its exception on the cursor or here.</summary>
    public Exception? LastError => lastError();

    public TextEncoding Encoding { get; } = encoding;

    public void Complete() => complete();
}

/// <summary>
/// The page and record decoding target: a database image (hostile) is opened, every page is tried as the root of a rowid table and of an
/// index, scanned, sought in, integrity-checked and written to. The only acceptable outcomes are success and a typed SQLITE_CORRUPT
/// (<see cref="OneLiteException"/>, or a cursor in the Fault state carrying one); any other exception, hang or oversized
/// allocation is a bug and escapes to the caller.
/// </summary>
public static class BTreeTarget
{
    private const int MaxRoots = 48;
    private const int StepBudget = 4000;

    /// <summary>Runs one input.</summary>
    public static void Run(byte[] data)
    {
        ImagePageSource src;
        try
        {
            src = ImagePageSource.FromBytes(data);
        }
        catch (OneLiteException)
        {
            return;
        }

        var ctx = new BtContext(src, src.Encoding);
        var pump = new Pump(src.Encoding, src.Complete, () => src.LastError);
        long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        List<SchemaEntry>? schema = null;
        try
        {
            schema = SchemaLite.Read(ctx, src.Complete);
        }
        catch (OneLiteException)
        {
            // fine
        }

        uint n = Math.Min(src.PageCount, MaxRoots);
        var trees = new List<BTree>();
        for (uint p = 1; p <= n; p++)
        {
            trees.Add(BTree.Table(ctx, p));
            trees.Add(BTree.Index(ctx, p, KeyInfo.Binary(src.Encoding, 2)));
        }

        if (schema != null)
        {
            foreach (SchemaEntry e in schema.Take(50))
            {
                if (e.RootPage >= 1 && e.RootPage <= src.PageCount)
                {
                    TableDef? def = e.Type == "table" ? SchemaLite.ParseTable(e.Sql ?? "") : null;
                    trees.Add(e.Type == "table" && !(def?.WithoutRowid ?? false) ? BTree.Table(ctx, e.RootPage) : BTree.Index(ctx, e.RootPage, KeyInfo.Binary(src.Encoding, 2)));
                }
            }
        }

        foreach (BTree t in trees)
        {
            Scan(pump, t);
        }

        _ = new BtIntegrity(ctx, src.Complete).Check(trees.Take(8));
        // Writes into hostile trees: they must fail as corruption or succeed, never throw anything else.
        for (int i = 0; i < Math.Min(trees.Count, 12); i++)
        {
            Mutate(pump, ctx, trees[i], i);
        }

        if (sw.ElapsedMilliseconds > 8000)
        {
            throw new TimeoutException("input took " + sw.ElapsedMilliseconds + " ms");
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - alloc0;
        if (allocated > 64L * data.Length + 32_000_000)
        {
            throw new InvalidOperationException($"input of {data.Length} bytes caused {allocated} bytes of allocation");
        }
    }

    internal static BtStatus Drive(Pump src, Func<BtStatus> op)
    {
        for (int guard = 0; guard < 100_000; guard++)
        {
            BtStatus s = op();
            if (s != BtStatus.Pending)
            {
                return s;
            }

            src.Complete();
        }

        throw new InvalidOperationException("a call stayed Pending");
    }

    private static void Expect(Pump src, BtCursor c, BtStatus s)
    {
        if (s == BtStatus.Error && c.Error is not OneLiteException && src.LastError is not OneLiteException)
        {
            throw new InvalidOperationException("cursor failed with " + c.Error?.GetType().Name + ": " + c.Error?.Message, c.Error);
        }
    }

    internal static void Scan(Pump src, BTree t)
    {
        using BtCursor c = t.OpenCursor();
        var rv = new RecordView();
        BtStatus s = Drive(src, c.First);
        Expect(src, c, s);
        int steps = 0;
        while (s == BtStatus.Ok && c.State == CursorState.Valid && steps++ < StepBudget)
        {
            s = Drive(src, c.LoadPayload);
            Expect(src, c, s);
            if (s != BtStatus.Ok)
            {
                break;
            }

            if (c.ParseRecord(rv))
            {
                for (int i = 0; i < Math.Min(rv.ColumnCount, 8); i++)
                {
                    _ = rv.ClassOf(i);
                    _ = rv.GetInt64(i);
                    _ = rv.GetDouble(i);
                    _ = rv.GetBytes(i).Length;
                }
            }

            if (t.IntKey)
            {
                _ = c.Rowid;
            }

            s = Drive(src, c.Next);
            Expect(src, c, s);
        }

        s = Drive(src, c.Last);
        Expect(src, c, s);
        for (int i = 0; s == BtStatus.Ok && c.State == CursorState.Valid && i < 200; i++)
        {
            s = Drive(src, c.Previous);
            Expect(src, c, s);
        }

        if (t.IntKey)
        {
            foreach (long k in new long[] { 0, 1, 77, long.MaxValue, long.MinValue })
            {
                s = Drive(src, () => c.MoveToRowid(k, out _));
                Expect(src, c, s);
            }
        }
        else
        {
            Mem[] key = [Mem.FromInt(1), Mem.FromText("a", src.Encoding)];
            s = Drive(src, () => c.MoveToKey(key, 0, out _));
            Expect(src, c, s);
        }
    }

    internal static void Mutate(Pump src, BtContext ctx, BTree t, int salt)
    {
        using BtCursor c = t.OpenCursor();
        BtStatus s;
        if (t.IntKey)
        {
            byte[] rec = RecordWriter.ToArray([Mem.FromInt(salt), Mem.FromBlob(new byte[(salt * 97) % 700])]);
            s = Drive(src, () => c.Insert(salt * 13L, rec));
            Expect(src, c, s);
            s = Drive(src, () => c.MoveToRowid(salt * 13L, out _));
            Expect(src, c, s);
            if (s == BtStatus.Ok && c.State == CursorState.Valid)
            {
                s = Drive(src, c.Delete);
                Expect(src, c, s);
            }
        }
        else
        {
            Mem[] key = [Mem.FromInt(salt), Mem.FromText("k" + salt, src.Encoding)];
            byte[] rec = RecordWriter.ToArray(key);
            s = Drive(src, () => c.Insert(rec, key));
            Expect(src, c, s);
        }

        if (salt % 4 == 3)
        {
            var eraser = new TreeEraser(t, destroy: salt % 8 == 7);
            for (int g = 0; g < 100_000; g++)
            {
                BtStatus es = eraser.Step();
                if (es == BtStatus.Pending)
                {
                    src.Complete();
                    continue;
                }

                if (es == BtStatus.Error && eraser.Error is not OneLiteException && src.LastError is not OneLiteException)
                {
                    throw new InvalidOperationException("eraser failed with " + eraser.Error?.GetType().Name, eraser.Error);
                }

                break;
            }
        }

        _ = ctx;
    }

    /// <summary>Record decoding on arbitrary bytes: Parse may refuse, but nothing may throw.</summary>
    public static void RunRecord(byte[] data)
    {
        var rv = new RecordView();
        if (!rv.Parse(data, 0, data.Length))
        {
            return;
        }

        var key = new Mem[3];
        for (int i = 0; i < rv.ColumnCount; i++)
        {
            _ = rv.ClassOf(i);
            _ = rv.GetInt64(i);
            _ = rv.GetDouble(i);
            _ = rv.GetBytes(i);
            if (i < 3)
            {
                rv.Load(i, ref key[i]);
            }
        }

        var ki = new KeyInfo(TextEncoding.Utf16Le, [Collations.NoCase(TextEncoding.Utf16Le), Collations.RTrim(TextEncoding.Utf8), null], [false, true, false]);
        _ = RecordCompare.Compare(ki, rv, key, 0);
        _ = RecordCompare.Compare(ki, rv, rv);
    }
}
