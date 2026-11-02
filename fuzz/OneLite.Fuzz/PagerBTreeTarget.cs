using System;
using System.Collections.Generic;
using System.Linq;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Fuzz;

/// <summary>
/// B-tree operations on the pager over hostile files: the input is a database image and a rollback journal. The files are opened through
/// <see cref="StorageEngine"/> (which plays back a hot journal), every plausible root is scanned, sought in and checked, a write transaction
/// inserts, deletes and erases, and the result is committed or rolled back. Acceptable outcomes: success, or a typed
/// <see cref="OneLiteException"/> with a code a damaged file can cause. Anything else is a bug.
/// </summary>
public static class PagerBTreeTarget
{
    private const int MaxRoots = 24;

    /// <summary>Joins a database image and a journal into one fuzz input: a 4-byte big-endian length of the image, the image, the journal.</summary>
    public static byte[] Pack(byte[] db, byte[] journal)
    {
        var r = new byte[4 + db.Length + journal.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(r, db.Length);
        db.CopyTo(r, 4);
        journal.CopyTo(r, 4 + db.Length);
        return r;
    }

    private static void Allow(OneLiteException e)
    {
        if (e.Code is not (ResultCode.Corrupt or ResultCode.NotADb or ResultCode.CantOpen or ResultCode.IoErr or ResultCode.ReadOnly or ResultCode.Full or ResultCode.Busy))
        {
            throw new InvalidOperationException($"unexpected result code {e.ExtendedCode}: {e.Message} {e.Detail}", e);
        }
    }

    /// <summary>Runs one input.</summary>
    /// <param name="input">The packed files (see <see cref="Pack"/>); anything shorter than 4 bytes is ignored.</param>
    public static void Run(byte[] input)
    {
        if (input.Length < 4)
        {
            return;
        }

        int dbLen = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(input);
        if (dbLen < 0 || dbLen > input.Length - 4)
        {
            dbLen = input.Length - 4;
        }

        // Journal headers can claim a database far larger than the file; an in-memory file would allocate it (see PagerTargets.Journal).
        ReadOnlySpan<byte> journal = input.AsSpan(4 + dbLen);
        if (journal.Length >= 28 && (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(journal[16..]) * System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(journal[24..]) > 64L << 20)
        {
            return;
        }

        var vfs = new MemoryVfs();
        Put(vfs, "f.db", input.AsSpan(4, dbLen));
        if (journal.Length > 0)
        {
            Put(vfs, "f.db-journal", journal);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        StorageEngine? eng = null;
        try
        {
            eng = StorageEngine.Open(vfs, "f.db", new PagerOptions { PageSize = 512, CacheSize = 24, Synchronous = SynchronousMode.Off });
            Walk(eng);
        }
        catch (OneLiteException e)
        {
            Allow(e);
        }
        finally
        {
            if (eng != null)
            {
                try
                {
                    if (eng.Pager.InWriteTransaction)
                    {
                        eng.Rollback();
                    }

                    eng.EndRead();
                }
                catch (OneLiteException e)
                {
                    Allow(e);
                }
                catch (InvalidOperationException)
                {
                    // a transaction state the failed run left behind; closing still releases everything
                }

                eng.Dispose();
            }
        }

        if (sw.ElapsedMilliseconds > 8000)
        {
            throw new TimeoutException("input took " + sw.ElapsedMilliseconds + " ms");
        }
    }

    private static void Walk(StorageEngine eng)
    {
        eng.BeginRead();
        BtContext ctx = eng.Context;
        var pump = new Pump(ctx.Encoding, eng.Pager.ExecuteSync, () => eng.Source.LastError);
        uint pages = eng.Pager.PageCount;
        var trees = new List<BTree>();
        List<SchemaEntry>? schema = null;
        try
        {
            schema = SchemaLite.Read(ctx, pump.Complete);
        }
        catch (OneLiteException)
        {
            // a damaged schema is fine; the roots below are still tried
        }

        uint n = Math.Min(pages, MaxRoots);
        for (uint p = 1; p <= n; p++)
        {
            trees.Add(BTree.Table(ctx, p));
            trees.Add(BTree.Index(ctx, p, KeyInfo.Binary(ctx.Encoding, 2)));
        }

        if (schema != null)
        {
            foreach (SchemaEntry e in schema.Take(20))
            {
                if (e.RootPage >= 1 && e.RootPage <= pages)
                {
                    TableDef? def = e.Type == "table" ? SchemaLite.ParseTable(e.Sql ?? "") : null;
                    trees.Add(e.Type == "table" && !(def?.WithoutRowid ?? false) ? BTree.Table(ctx, e.RootPage) : BTree.Index(ctx, e.RootPage, KeyInfo.Binary(ctx.Encoding, 2)));
                }
            }
        }

        foreach (BTree t in trees)
        {
            BTreeTarget.Scan(pump, t);
        }

        eng.BeginWrite();
        for (int i = 0; i < Math.Min(trees.Count, 10); i++)
        {
            BTreeTarget.Mutate(pump, ctx, trees[i], i);
        }

        if (trees.Count % 3 == 0)
        {
            eng.Rollback();
        }
        else
        {
            eng.Commit();
        }
    }

    private static void Put(MemoryVfs vfs, string path, ReadOnlySpan<byte> bytes)
    {
        using IVfsFile f = vfs.Open(path, VfsOpenOptions.ReadWrite | VfsOpenOptions.Create);
        f.Write(0, bytes);
    }

    /// <summary>
    /// A valid input to mutate: a database written through the pager and a hot journal left by a transaction that lost power during its commit
    /// (or an empty journal for odd seeds, so the plain-database path is mutated too).
    /// </summary>
    /// <param name="rnd">The source of the workload.</param>
    /// <param name="i">The seed number; picks the page size, vacuum mode and the crash point.</param>
    /// <returns>The packed input.</returns>
    public static byte[] Seed(Random rnd, int i)
    {
        int ps = new[] { 512, 1024, 512, 2048 }[i % 4];
        int av = i % 3;
        var inner = new MemoryVfs();
        var fault = new FaultVfs(inner, new FaultOptions { SectorSize = 512 });
        var opts = new PagerOptions { PageSize = ps, AutoVacuum = av != 0, IncrementalVacuum = av == 2, CacheSize = 8, Synchronous = SynchronousMode.Full };
        using (StorageEngine eng = StorageEngine.Open(fault, "f.db", opts))
        {
            eng.BeginRead();
            eng.BeginWrite();
            uint root = 0, idx = 0;
            eng.Run(() => BtAdmin.CreateTree(eng.Context, true, out root));
            eng.Run(() => BtAdmin.CreateTree(eng.Context, false, out idx));
            Fill(eng, root, idx, rnd, 1, 60, ps);
            eng.Commit();
            eng.BeginWrite();
            Fill(eng, root, idx, rnd, 100, 40, ps);
            if (i % 2 == 0)
            {
                fault.HaltAfter(fault.OpCount + 3 + (i % 11));
                try
                {
                    eng.Commit();
                }
                catch (PowerLossException)
                {
                    // expected: the power-loss stand-in ends the commit
                }
            }
            else
            {
                eng.Commit();
            }
        }

        CrashImage img = fault.CrashImage(fault.OpCount, i % 3 == 0 ? CrashPolicy.KeepAll : CrashPolicy.DropUnsynced);
        byte[] db = img.Files.TryGetValue("f.db", out byte[]? d) ? d : [];
        byte[] j = img.Files.TryGetValue("f.db-journal", out byte[]? jj) ? jj : [];
        return Pack(db, j);
    }

    private static void Fill(StorageEngine eng, uint root, uint idx, Random rnd, int first, int count, int ps)
    {
        using BtCursor t = BTree.Table(eng.Context, root).OpenCursor();
        using BtCursor x = BTree.Index(eng.Context, idx, KeyInfo.Binary(eng.Context.Encoding, 2)).OpenCursor();
        for (int r = first; r < first + count; r++)
        {
            var blob = new byte[rnd.Next(10) == 0 ? rnd.Next(ps * 2) : rnd.Next(40)];
            rnd.NextBytes(blob);
            byte[] rec = RecordWriter.ToArray([Mem.FromInt(r), Mem.FromBlob(blob)]);
            long id = r;
            eng.Run(() => t.Insert(id, rec));
            Mem[] key = [Mem.FromText("key" + r, eng.Context.Encoding), Mem.FromInt(r)];
            byte[] krec = RecordWriter.ToArray(key);
            eng.Run(() => x.Insert(krec, key));
            if (r % 4 == 0)
            {
                eng.Run(() => t.MoveToRowid(id - 1, out _));
                eng.Run(t.Delete);
            }
        }
    }
}
