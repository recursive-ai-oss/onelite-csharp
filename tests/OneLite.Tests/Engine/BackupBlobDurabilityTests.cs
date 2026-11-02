using System.Collections.Generic;
using System.Text;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// Crash sweeps for the backup API and incremental blob writes on the fault-injecting VFS. The operation is cut after storage calls under many loss
/// policies; a fresh engine recovers each image, which must pass integrity_check and hold exactly the old content or exactly the new content, and an
/// acknowledged commit (synchronous=FULL, unsynced data dropped) must still be there.
/// </summary>
public sealed class BackupBlobDurabilityTests(ITestOutputHelper output)
{
    private const string Db = "crash.db";

    public static IEnumerable<object[]> Cases()
    {
        foreach (JournalMode jm in new[] { JournalMode.Delete, JournalMode.Wal })
        {
            foreach (SynchronousMode sm in new[] { SynchronousMode.Full, SynchronousMode.Normal })
            {
                yield return [jm, sm];
            }
        }
    }

    private static IEnumerable<CrashPolicy> Policies()
    {
        yield return CrashPolicy.DropUnsynced;
        yield return CrashPolicy.KeepAll;
        for (ulong seed = 1; seed <= 20; seed++)
        {
            yield return new CrashPolicy { Seed = seed, Reorder = true };
            yield return new CrashPolicy { Seed = seed, Reorder = false };
        }

        yield return new CrashPolicy { Seed = 9, TearSectors = true, TornFill = TornFill.Random };
    }

    private static DatabaseOptions Opts(IVfs vfs, JournalMode jm, SynchronousMode sm) => new() { Vfs = vfs, PageSize = 1024, JournalMode = jm, Synchronous = sm, CacheSize = 400 };

    private static void Exec(Connection c, string sql)
    {
        string rest = sql;
        while (!string.IsNullOrWhiteSpace(rest))
        {
            using Statement s = c.Prepare(rest);
            while (s.Step())
            {
            }

            rest = s.Tail;
        }
    }

    // What the database holds, as text: integrity result, each table's row count and rowid total.
    private static string Sig(MemoryVfs m, JournalMode jm)
    {
        using Database d = Database.Open(Db, Opts(m, jm, SynchronousMode.Full));
        using Connection c = d.Connect();
        var sb = new StringBuilder();
        using (Statement s = c.Prepare("PRAGMA integrity_check"))
        {
            while (s.Step())
            {
                sb.Append(s.GetString(0)).Append(';');
            }
        }

        var names = new List<string>();
        using (Statement s = c.Prepare("SELECT name FROM sqlite_schema WHERE type = 'table' ORDER BY 1"))
        {
            while (s.Step())
            {
                names.Add(s.GetString(0)!);
            }
        }

        foreach (string n in names)
        {
            string col = n == "t" ? "length(b)" : "1";
            using Statement s = c.Prepare($"SELECT count(*), total(rowid), total({col}) FROM \"{n}\"");
            s.Step();
            sb.Append(n).Append(':').Append(s.GetInt64(0)).Append(',').Append(s.GetInt64(1)).Append(',').Append(s.GetInt64(2)).Append(';');
        }

        return sb.ToString();
    }

    private static MemoryVfs MakeSource()
    {
        var vfs = new MemoryVfs();
        using Database d = Database.Open("src.db", new DatabaseOptions { Vfs = vfs, PageSize = 1024 });
        using Connection c = d.Connect();
        Exec(c, "CREATE TABLE t(a INTEGER PRIMARY KEY, b BLOB); CREATE INDEX tb ON t(b);");
        Exec(c, "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<40) INSERT INTO t SELECT i, zeroblob(i * 80) FROM n;");
        return vfs;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void CrashDuringABackupLeavesTheOldOrTheCompleteDestination(JournalMode jm, SynchronousMode sm)
    {
        MemoryVfs src = MakeSource();
        var inner = new MemoryVfs();
        using (Database d = Database.Open(Db, Opts(inner, jm, SynchronousMode.Off)))
        using (Connection c = d.Connect())
        {
            Exec(c, "CREATE TABLE k(x); INSERT INTO k VALUES (1), (2), (3);");
        }

        string oldSig = Sig(inner, jm);
        var fault = new FaultVfs(inner, new FaultOptions { SectorSize = 512 });
        long start = fault.OpCount;
        using (Database sd = Database.OpenReadOnly("src.db", new DatabaseOptions { Vfs = src }))
        using (Connection sc = sd.Connect())
        using (Database dd = Database.Open(Db, Opts(fault, jm, sm)))
        using (Connection dc = dd.Connect())
        using (Backup b = Backup.Create(dc, "main", sc, "main"))
        {
            while (b.Step(9) == BackupStatus.More)
            {
            }
        }

        long ack = fault.OpCount;
        string newSig = Sig(inner, jm);
        Assert.NotEqual(oldSig, newSig);
        Assert.Contains("t:40,", newSig, StringComparison.Ordinal);
        Sweep(fault, start, ack, jm, sm, oldSig, newSig, "backup");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void CrashDuringBlobWritesLeavesTheOldOrTheNewValue(JournalMode jm, SynchronousMode sm)
    {
        var inner = new MemoryVfs();
        using (Database d = Database.Open(Db, Opts(inner, jm, SynchronousMode.Off)))
        using (Connection c = d.Connect())
        {
            Exec(c, "CREATE TABLE t(a INTEGER PRIMARY KEY, b BLOB); INSERT INTO t VALUES (1, zeroblob(20000)), (2, x'01');");
        }

        string oldSig = BlobSig(inner, jm);
        var fault = new FaultVfs(inner, new FaultOptions { SectorSize = 512 });
        long start = fault.OpCount;
        using (Database dd = Database.Open(Db, Opts(fault, jm, sm)))
        using (Connection dc = dd.Connect())
        using (Blob b = dc.OpenBlob("main", "t", "b", 1, writable: true))
        {
            b.Write(100, Fill(1000, 1));
            b.Write(9000, Fill(3000, 2));
            b.Write(19000, Fill(500, 3));
        }

        long ack = fault.OpCount;
        string newSig = BlobSig(inner, jm);
        Assert.NotEqual(oldSig, newSig);
        Sweep(fault, start, ack, jm, sm, oldSig, newSig, "blob", BlobSig);
    }

    private static byte[] Fill(int n, byte v)
    {
        var b = new byte[n];
        Array.Fill(b, v);
        return b;
    }

    private static string BlobSig(MemoryVfs m, JournalMode jm)
    {
        string baseSig = Sig(m, jm);
        using Database d = Database.Open(Db, Opts(m, jm, SynchronousMode.Full));
        using Connection c = d.Connect();
        using Blob b = c.OpenBlob("main", "t", "b", 1);
        var all = new byte[b.Length];
        b.Read(0, all);
        long h = 17;
        foreach (byte x in all)
        {
            h = (h * 31) + x;
        }

        return baseSig + h;
    }

    private void Sweep(FaultVfs fault, long start, long ack, JournalMode jm, SynchronousMode sm, string oldSig, string newSig, string what, Func<MemoryVfs, JournalMode, string>? sig = null)
    {
        sig ??= Sig;
        int oldN = 0, newN = 0, images = 0;
        long step = Math.Max(1, (ack - start) / 60);
        for (long k = start; k <= ack; k = k == ack ? ack + 1 : Math.Min(ack, k + step))
        {
            foreach (CrashPolicy policy in Policies())
            {
                MemoryVfs m = fault.CrashImage(k, policy).ToMemoryVfs();
                string got = sig(m, jm);
                bool isOld = got == oldSig, isNew = got == newSig;
                Assert.True(isOld || isNew, $"{what}: crash after {k} of {ack} ops, policy {policy.Mode}/seed {policy.Seed}/reorder {policy.Reorder}/tear {policy.TearSectors}: neither the old nor the new content: {got} (old {oldSig} new {newSig})");
                if (sm == SynchronousMode.Full && k >= ack && policy.Mode == CrashMode.DropUnsynced)
                {
                    Assert.True(isNew, $"{what}: an acknowledged commit was lost");
                }

                oldN += isOld ? 1 : 0;
                newN += isNew && !isOld ? 1 : 0;
                images++;
            }
        }

        Assert.True(images > 300 && oldN > 0 && newN > 0, $"{images} images, old {oldN}, new {newN}");
        output.WriteLine($"{what} {jm}/{sm}: {ack - start} storage calls, {images} crash images (old {oldN}, new {newN})");
    }
}
