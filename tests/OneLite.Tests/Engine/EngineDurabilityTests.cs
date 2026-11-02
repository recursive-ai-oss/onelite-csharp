using System.Collections.Generic;
using RecursiveAI.OneLite.Durability;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// Crash sweeps with SQL transactions on the fault-injecting VFS. The workload is the durability tool's (inserts with overflow rows, an index, deletes
/// of older rows, a rolled-back savepoint and a statement that fails half way, all in one transaction), run through the engine. The transaction is cut
/// after storage calls under several loss policies; a fresh engine recovers each image, which must equal the file before the transaction or the file
/// after it, byte for byte (free pages aside).
/// </summary>
public sealed class EngineDurabilityTests(ITestOutputHelper output)
{
    private const string Db = "crash.db";

    public static IEnumerable<object[]> Cases()
    {
        foreach (JournalMode jm in new[] { JournalMode.Delete, JournalMode.Truncate, JournalMode.Persist })
        {
            foreach (SynchronousMode sm in new[] { SynchronousMode.Full, SynchronousMode.Normal })
            {
                foreach ((int ps, bool av, int cache, int cut) in new[] { (1024, false, 400, 29), (1024, true, 400, 29), (512, true, 16, 29), (512, false, 400, 12), (4096, false, 400, 11) })
                {
                    yield return [jm, sm, ps, av, cache, cut];
                }
            }
        }
    }

    private static IEnumerable<CrashPolicy> Policies()
    {
        yield return CrashPolicy.DropUnsynced;
        yield return CrashPolicy.KeepAll;
        for (ulong seed = 1; seed <= 8; seed++)
        {
            yield return new CrashPolicy { Seed = seed, Reorder = true };
            yield return new CrashPolicy { Seed = seed, Reorder = false };
        }

        yield return new CrashPolicy { Seed = 9, TearSectors = true, TornFill = TornFill.Random };
    }

    private static DatabaseOptions Opts(IVfs vfs, int ps, bool av, JournalMode jm, SynchronousMode sm, int cache)
        => new() { Vfs = vfs, PageSize = ps, AutoVacuum = av, JournalMode = jm, Synchronous = sm, CacheSize = cache };

    private static byte[] Canonical(byte[] image, int ps)
    {
        // A free leaf page carries no information and a page taken from the freelist is not journaled, so a rolled-back transaction may leave other bytes there.
        byte[] c = (byte[])image.Clone();
        uint trunk = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(c.AsSpan(32));
        for (int guard = 0; trunk != 0 && guard < 100_000; guard++)
        {
            int off = (int)(trunk - 1) * ps;
            uint n = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(c.AsSpan(off + 4));
            for (uint i = 0; i < n; i++)
            {
                uint leaf = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(c.AsSpan(off + 8 + (4 * (int)i)));
                c.AsSpan((int)(leaf - 1) * ps, ps).Clear();
            }

            trunk = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(c.AsSpan(off));
        }

        return c;
    }

    private static byte[] Image(IVfs vfs)
    {
        using IVfsFile f = vfs.Open(Db, VfsOpenOptions.ReadOnly);
        var b = new byte[f.FileSize()];
        f.Read(0, b);
        return b;
    }

    private static MemoryVfs Prepare(int ps, bool av, int before)
    {
        var vfs = new MemoryVfs();
        using Database db = Database.Open(Db, Opts(vfs, ps, av, JournalMode.Delete, SynchronousMode.Off, 400));
        using Connection conn = db.Connect();
        EngineWriter.Setup(conn);
        using Statement ins = conn.Prepare(Workload.InsertSql);
        for (long t = 1; t <= before; t++)
        {
            EngineWriter.RunTransaction(conn, ins, t);
        }

        return vfs;
    }

    private static void RunLast(IVfs vfs, int ps, bool av, JournalMode jm, SynchronousMode sm, int cache, int cut)
    {
        using Database db = Database.Open(Db, Opts(vfs, ps, av, jm, sm, cache));
        using Connection conn = db.Connect();
        EngineWriter.Setup(conn);
        using Statement ins = conn.Prepare(Workload.InsertSql);
        EngineWriter.RunTransaction(conn, ins, cut);
    }

    private static void Recover(MemoryVfs m, int ps, bool av, JournalMode jm, SynchronousMode sm, int cache)
    {
        using Database db = Database.Open(Db, Opts(m, ps, av, jm, sm, cache));
        using Connection conn = db.Connect();
        using Statement s = conn.Prepare("SELECT count(*) FROM sqlite_schema");
        while (s.Step())
        {
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void CrashAtEveryPointLeavesTheOldOrTheNewFile(JournalMode jm, SynchronousMode sm, int ps, bool av, int cache, int cut)
    {
        var fault = new FaultVfs(Prepare(ps, av, cut - 1), new FaultOptions { SectorSize = 512 });
        byte[] oldImg = Canonical(Image(fault.Inner), ps);
        long start = fault.OpCount;
        RunLast(fault, ps, av, jm, sm, cache, cut);
        long ack = fault.OpCount;
        byte[] newImg = Canonical(Image(fault.Inner), ps);
        Assert.False(oldImg.AsSpan().SequenceEqual(newImg));
        int oldN = 0, newN = 0, images = 0;
        long step = Math.Max(1, (ack - start) / 300);
        for (long k = start; k <= ack; k = k == ack ? ack + 1 : Math.Min(ack, k + step))
        {
            foreach (CrashPolicy policy in Policies())
            {
                MemoryVfs m = fault.CrashImage(k, policy).ToMemoryVfs();
                Recover(m, ps, av, jm, sm, cache);
                byte[] got = Canonical(Image(m), ps);
                bool isOld = got.AsSpan().SequenceEqual(oldImg), isNew = got.AsSpan().SequenceEqual(newImg);
                Assert.True(isOld || isNew, $"crash after {k} of {ack} ops, policy {policy.Mode}/seed {policy.Seed}/reorder {policy.Reorder}/tear {policy.TearSectors}: neither the old nor the new file");
                if (sm == SynchronousMode.Full && k >= ack && policy.Mode == CrashMode.DropUnsynced)
                {
                    Assert.True(isNew, "an acknowledged commit was lost");
                }

                oldN += isOld ? 1 : 0;
                newN += isNew && !isOld ? 1 : 0;
                images++;
            }
        }

        Assert.True(images > 100 && oldN > 0 && newN > 0, $"{images} images, old {oldN}, new {newN}");
        output.WriteLine($"{jm}/{sm} ps={ps} av={av} cache={cache} cut={cut}: {ack - start} storage calls, {images} crash images (old {oldN}, new {newN})");
    }
}
