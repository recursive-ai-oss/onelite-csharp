using System.Collections.Generic;
using System.IO;
using RecursiveAI.OneLite.Durability;
using RecursiveAI.OneLite.Oracle;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// PRAGMA incremental_vacuum on the fault-injecting VFS: the pragma is cut after storage calls under several loss policies; a fresh engine recovers
/// each image, which must equal the file before the pragma or the file after it, and the pinned shell must pass integrity_check on a sample of them.
/// </summary>
public sealed class IncrementalVacuumDurabilityTests(ITestOutputHelper output)
{
    private const string Db = "vac.db";

    public static IEnumerable<object[]> Cases() =>
    [
        [JournalMode.Delete, 512, 7], [JournalMode.Delete, 1024, 0], [JournalMode.Truncate, 512, 25], [JournalMode.Persist, 1024, 3],
        [JournalMode.Wal, 512, 9], [JournalMode.Wal, 1024, 0],
    ];

    private static DatabaseOptions Opts(IVfs vfs, JournalMode jm, int ps) => new() { Vfs = vfs, PageSize = ps, JournalMode = jm, Synchronous = SynchronousMode.Full, CacheSize = 400 };

    private static void Exec(Connection c, string sql)
    {
        foreach (string one in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using Statement s = c.Prepare(one);
            while (s.Step())
            {
            }
        }
    }

    private static MemoryVfs Prepare(JournalMode jm, int ps)
    {
        var vfs = new MemoryVfs();
        using Database db = Database.Open(Db, Opts(vfs, JournalMode.Delete, ps));
        using Connection c = db.Connect();
        Exec(c, "PRAGMA auto_vacuum = 2");
        Exec(c, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT); CREATE INDEX tb ON t(b); CREATE TABLE big(x)");
        Exec(c, "WITH RECURSIVE r(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM r WHERE i<400) INSERT INTO t SELECT i, hex(randomblob(20 + i % 50)) FROM r");
        Exec(c, "INSERT INTO big VALUES(zeroblob(20000)); INSERT INTO big VALUES(zeroblob(9000))");
        Exec(c, "DELETE FROM t WHERE a % 3 <> 0; DROP TABLE big");
        return vfs;
    }

    // A free leaf page carries no information; the rest of the file is compared byte for byte.
    private static byte[] Raw(MemoryVfs vfs, int ps)
    {
        using IVfsFile f = vfs.Open(Db, VfsOpenOptions.ReadOnly);
        var c = new byte[f.FileSize()];
        f.Read(0, c);
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

    // A fresh engine recovers the image (hot journal, WAL), which is then read back.
    private static byte[] Image(MemoryVfs m, JournalMode jm, int ps)
    {
        using (Database db = Database.Open(Db, Opts(m, jm, ps)))
        using (Connection c = db.Connect())
        {
            Exec(c, "SELECT count(*) FROM sqlite_schema");
        }

        return Raw(m, ps);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void CrashAtEveryPointLeavesTheOldOrTheNewFile(JournalMode jm, int ps, int pages)
    {
        MemoryVfs prepared = Prepare(jm, ps);
        string sql = pages == 0 ? "PRAGMA incremental_vacuum" : $"PRAGMA incremental_vacuum({pages})";
        var fault = new FaultVfs(prepared, new FaultOptions { SectorSize = 512 });
        byte[] oldImg = Image(prepared, jm, ps);
        long start = fault.OpCount;
        using (Database db = Database.Open(Db, Opts(fault, jm, ps)))
        using (Connection c = db.Connect())
        {
            Exec(c, sql);
        }

        long ack = fault.OpCount;
        byte[] newImg = Image(prepared, jm, ps);
        Assert.False(oldImg.AsSpan().SequenceEqual(newImg));
        string dir = Path.Combine(Path.GetTempPath(), "onelite-ivdur-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            int oldN = 0, newN = 0, images = 0, shell = 0;
            long step = Math.Max(1, (ack - start) / 200);
            for (long k = start; k <= ack; k = k == ack ? ack + 1 : Math.Min(ack, k + step))
            {
                for (ulong seed = 0; seed < 6; seed++)
                {
                    CrashPolicy policy = seed == 0 ? CrashPolicy.DropUnsynced : seed == 1 ? CrashPolicy.KeepAll : new CrashPolicy { Seed = seed, Reorder = seed % 2 == 0 };
                    MemoryVfs m = fault.CrashImage(k, policy).ToMemoryVfs();
                    byte[] got = Image(m, jm, ps);
                    bool isOld = got.AsSpan().SequenceEqual(oldImg), isNew = got.AsSpan().SequenceEqual(newImg);
                    Assert.True(isOld || isNew, $"crash after {k} of {ack} ops, policy {policy.Mode}/seed {policy.Seed}: neither the old nor the new file");
                    if (k >= ack && policy.Mode == CrashMode.DropUnsynced)
                    {
                        Assert.True(isNew, "an acknowledged vacuum was lost");
                    }

                    oldN += isOld ? 1 : 0;
                    newN += isNew && !isOld ? 1 : 0;
                    images++;
                    if (File.Exists(OraclePaths.Cli) && images % 17 == 0)
                    {
                        string f = Path.Combine(dir, "i.db");
                        File.WriteAllBytes(f, got);
                        Assert.True(PinnedSqlite.IntegrityOk(f, out string detail), detail);
                        shell++;
                    }
                }
            }

            Assert.True(images > 30 && oldN > 0 && newN > 0, $"{images} images, old {oldN}, new {newN}");
            output.WriteLine($"{jm} ps={ps} N={pages}: {ack - start} storage calls, {images} crash images (old {oldN}, new {newN}), {shell} checked by the shell");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
