using System.Collections.Generic;
using System.Text;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// Crash sweep for a BEGIN CONCURRENT commit that has to validate and replay on a newer snapshot (the commit takes the write lock, rebuilds its
/// changes on the latest pages, appends WAL frames, syncs and publishes). The commit is cut after storage calls under many loss policies; a fresh
/// engine recovers each image, which must pass integrity_check and hold exactly the content before the commit or exactly the content after it, and an
/// acknowledged commit (synchronous=FULL, unsynced data dropped) must still be there. Nothing half replayed may ever be visible.
/// </summary>
public sealed class ConcurrentDurabilityTests(ITestOutputHelper output)
{
    private const string Db = "conc.db";

    public static IEnumerable<object[]> Cases()
    {
        yield return [SynchronousMode.Full];
        yield return [SynchronousMode.Normal];
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

    private static DatabaseOptions Opts(IVfs vfs, SynchronousMode sm) => new() { Vfs = vfs, PageSize = 1024, JournalMode = JournalMode.Wal, Synchronous = sm, ConcurrentWrites = true, CacheSize = 400 };

    private static void Exec(Connection c, string sql)
    {
        using Statement s = c.Prepare(sql);
        while (s.Step())
        {
        }
    }

    private static string Sig(MemoryVfs m)
    {
        using Database d = Database.Open(Db, Opts(m, SynchronousMode.Full));
        using Connection c = d.Connect();
        var sb = new StringBuilder();
        using (Statement s = c.Prepare("PRAGMA integrity_check"))
        {
            while (s.Step())
            {
                sb.Append(s.GetString(0)).Append(';');
            }
        }

        using Statement q = c.Prepare("SELECT count(*), total(v), total(id) FROM t");
        q.Step();
        sb.Append(q.GetInt64(0)).Append(',').Append(q.GetInt64(1)).Append(',').Append(q.GetInt64(2));
        return sb.ToString();
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void CrashDuringAStaleConcurrentCommitLeavesTheOldOrTheNewContent(SynchronousMode sm)
    {
        var inner = new MemoryVfs();
        using (Database d = Database.Open(Db, Opts(inner, SynchronousMode.Off)))
        using (Connection c = d.Connect())
        {
            Exec(c, "CREATE TABLE t(id INTEGER PRIMARY KEY, v INT, pad TEXT)");
            Exec(c, "CREATE INDEX tv ON t(v)");
            Exec(c, "WITH RECURSIVE s(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM s WHERE n<300) INSERT INTO t SELECT n, n, printf('%0100d', n) FROM s");
        }

        var fault = new FaultVfs(inner, new FaultOptions { SectorSize = 512 });
        // With synchronous=NORMAL the WAL is not synced at commit, so a crash may also lose the commit that came before.
        string baseSig = Sig(inner), oldSig, newSig;
        long start, ack;
        using (Database d = Database.Open(Db, Opts(fault, sm)))
        using (Connection a = d.Connect())
        using (Connection b = d.Connect())
        {
            Exec(a, "BEGIN CONCURRENT");
            Exec(b, "BEGIN CONCURRENT");
            Exec(a, "UPDATE t SET v = v + 1000 WHERE id BETWEEN 10 AND 60");
            Exec(a, "INSERT INTO t(v, pad) VALUES (5000, 'a')");
            Exec(b, "UPDATE t SET v = v + 2000 WHERE id BETWEEN 200 AND 250");
            Exec(b, "DELETE FROM t WHERE id > 290");
            Exec(b, "COMMIT");
            oldSig = Sig(fault.CrashImage(fault.OpCount, CrashPolicy.KeepAll).ToMemoryVfs());
            start = fault.OpCount;

            // a's snapshot is stale: its commit validates and replays on b's pages under the write lock.
            Exec(a, "COMMIT");
            ack = fault.OpCount;
            newSig = Sig(fault.CrashImage(ack, CrashPolicy.KeepAll).ToMemoryVfs());
        }

        Assert.NotEqual(oldSig, newSig);
        Assert.StartsWith("ok;", newSig, StringComparison.Ordinal);
        int oldN = 0, newN = 0, images = 0;
        long step = Math.Max(1, (ack - start) / 60);
        for (long k = start; k <= ack; k = k == ack ? ack + 1 : Math.Min(ack, k + step))
        {
            foreach (CrashPolicy policy in Policies())
            {
                string got = Sig(fault.CrashImage(k, policy).ToMemoryVfs());
                bool isOld = got == oldSig, isNew = got == newSig;
                Assert.True(isOld || (sm == SynchronousMode.Normal && got == baseSig) || isNew, $"crash after {k} of {ack} ops, policy {policy.Mode}/seed {policy.Seed}/reorder {policy.Reorder}/tear {policy.TearSectors}: neither the old nor the new content: {got} (old {oldSig} new {newSig})");
                if (sm == SynchronousMode.Full && k >= ack && policy.Mode == CrashMode.DropUnsynced)
                {
                    Assert.True(isNew, "an acknowledged commit was lost");
                }

                oldN += isOld || got == baseSig ? 1 : 0;
                newN += isNew && !isOld ? 1 : 0;
                images++;
            }
        }

        Assert.True(images > 40 && oldN > 0 && newN > 0, $"{images} images, old {oldN}, new {newN}");
        output.WriteLine($"concurrent commit {sm}: {ack - start} storage calls, {images} crash images (old {oldN}, new {newN})");
    }
}
