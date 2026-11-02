using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;
using RecursiveAI.OneLite.Storage.Wal;
using RecursiveAI.OneLite.Tests.Pager;
using PagerClass = RecursiveAI.OneLite.Storage.Pager.Pager;

namespace RecursiveAI.OneLite.Tests.Wal;

/// <summary>Helpers for WAL tests on page-pattern databases (see <see cref="Kit"/>).</summary>
internal static class WalKit
{
    public static PagerOptions Opts(int pageSize = 512, SynchronousMode sm = SynchronousMode.Full, int cache = 100, int yield = 0, int autoCheckpoint = 1000, long sizeLimit = -1) =>
        new() { PageSize = pageSize, Synchronous = sm, CacheSize = cache, YieldEvery = yield, WalAutoCheckpoint = autoCheckpoint, JournalSizeLimit = sizeLimit };

    /// <summary>A database of <paramref name="pages"/> pattern pages at generation 0, switched to WAL mode and closed cleanly (so the -wal and -shm are gone).</summary>
    public static MemoryVfs PrepareWal(int pages, int pageSize = 512)
    {
        MemoryVfs vfs = Kit.Prepare(pages, pageSize);
        using PagerClass p = PagerClass.Open(vfs, Kit.Db, Opts(pageSize));
        p.BeginReadSync();
        p.ChangeJournalModeSync(JournalMode.Wal);
        p.EndReadSync();
        return vfs;
    }

    /// <summary>Drives one pager operation to its end (running requests inline) and returns Ok or Error.</summary>
    public static PagerStatus Run(PagerClass p, Func<PagerStatus> op)
    {
        PagerStatus s;
        while ((s = op()) == PagerStatus.Pending)
        {
            p.ExecuteSync();
        }

        return s;
    }

    public static byte[] Page(PagerClass p, uint n)
    {
        PageFrame f = p.GetSync(n);
        byte[] copy = (byte[])f.Data.Clone();
        p.Release(f);
        return copy;
    }

    public static void Write(PagerClass p, IEnumerable<uint> pages, int gen)
    {
        p.BeginWriteSync();
        Kit.Rewrite(p, pages, gen);
        p.CommitSync();
    }

    /// <summary>The expected page content for (page, generation), built like <see cref="Kit.Fill"/> (page 1 keeps its first 108 bytes from <paramref name="basis"/>).</summary>
    public static byte[] Expect(uint n, int gen, int pageSize, byte[]? basis = null)
    {
        var b = new byte[pageSize];
        int start = 0;
        if (n == 1 && basis != null)
        {
            Array.Copy(basis, b, 108);
            start = 108;
        }

        for (int i = start; i < pageSize; i++)
        {
            b[i] = (byte)((n * 31 + gen * 7 + i) & 0xFF);
        }

        return b;
    }

    public static void CheckPattern(PagerClass p, uint n, int gen, int pageSize)
    {
        byte[] got = Page(p, n);
        byte[] want = Expect(n, gen, pageSize, got);
        // Page 1's header bytes beyond the first 108 are pattern bytes too, but its first 100 are the database header the pager maintains.
        Assert.True(got.AsSpan(n == 1 ? 108 : 0).SequenceEqual(want.AsSpan(n == 1 ? 108 : 0)), $"page {n} is not generation {gen}: {Kit.Describe(got, want)}");
    }
}

public class WalFormatTests
{
    [Fact]
    public void IndexHeaderChecksumMatchesTheReferenceShmDump()
    {
        // The words of a header dumped from a -shm file written by the reference shell (native byte order), with its stored checksum.
        var h = new WalIndexHeader { W0 = 3007000, W1 = 0, W2 = 7, W4 = 14, W5 = 6, W6 = 2395229110, W7 = 4127673782, W8 = 3612368897, W9 = 597786, W10 = 1895320846, W11 = 1234211327 };
        h.SetIdentity(false, 4096);
        Assert.Equal(0x10000001u, h.W3);
        Assert.True(h.ChecksumOk);
        Assert.Equal(4096, h.PageSize);
        Assert.False(h.BigEndianChecksums);
        h.W4++;
        Assert.False(h.ChecksumOk);
    }

    [Fact]
    public void WalHeaderRoundTripsAndRejectsDamage()
    {
        var h = new WalHeader { Magic = WalFormat.MagicLittleEndian, PageSize = 4096, CheckpointSequence = 3, Salt1 = 0xAABBCCDD, Salt2 = 0x01020304 };
        var b = new byte[32];
        h.Write(b);
        Assert.True(WalHeader.TryParse(b, out WalHeader r));
        Assert.Equal(0xAABBCCDDu, r.Salt1);
        Assert.Equal(h.Checksum1, r.Checksum1);
        b[13] ^= 1;
        Assert.False(WalHeader.TryParse(b, out _));
        b[13] ^= 1;
        b[0] = 0;
        Assert.False(WalHeader.TryParse(b, out _));
    }

    [Fact]
    public void ChecksumOfAKnownSequenceInBothByteOrders()
    {
        byte[] d = [1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0, 4, 0, 0, 0];
        uint a = 0;
        uint b = 0;
        WalFormat.Checksum(false, d, ref a, ref b);
        // s0 = 1; s1 = 2 + 1 = 3; s0 = 1 + 3 + 3 = 7; s1 = 3 + 4 + 7 = 14
        Assert.Equal((7u, 14u), (a, b));
        uint a2 = 0;
        uint b2 = 0;
        WalFormat.Checksum(true, d, ref a2, ref b2);
        Assert.NotEqual((a, b), (a2, b2));
    }
}

public class WalPagerTests
{
    private const int Ps = 512;

    [Fact]
    public void SwitchingToWalWritesTheVersionBytesAndKeepsTheContent()
    {
        MemoryVfs vfs = Kit.Prepare(8, Ps);
        using (PagerClass p = PagerClass.Open(vfs, Kit.Db, WalKit.Opts()))
        {
            p.BeginReadSync();
            Assert.Equal(JournalMode.Delete, p.JournalMode);
            p.ChangeJournalModeSync(JournalMode.Wal);
            Assert.Equal(JournalMode.Wal, p.JournalMode);
            Assert.True(p.IsWal);
            p.EndReadSync();
            Assert.True(Kit.Exists(vfs, Kit.Db + "-wal"));
        }

        // Closing the last connection checkpointed (nothing to do) and removed both files.
        Assert.False(Kit.Exists(vfs, Kit.Db + "-wal"));
        Assert.False(Kit.Exists(vfs, Kit.Db + "-shm"));
        byte[] img = Kit.ReadFile(vfs, Kit.Db);
        Assert.Equal(2, img[18]);
        Assert.Equal(2, img[19]);
        using PagerClass q = PagerClass.Open(vfs, Kit.Db, WalKit.Opts());
        q.BeginReadSync();
        Assert.True(q.IsWal);
        Assert.Equal(8u, q.PageCount);
        WalKit.CheckPattern(q, 5, 0, Ps);
        q.EndReadSync();
    }

    [Fact]
    public void CommitsAreVisibleToAnotherConnectionAndToACleanReopen()
    {
        MemoryVfs vfs = WalKit.PrepareWal(8, Ps);
        using var a = PagerClass.Open(vfs, Kit.Db, WalKit.Opts());
        using var b = PagerClass.Open(vfs, Kit.Db, WalKit.Opts());
        a.BeginReadSync();
        WalKit.Write(a, [2u, 3u, 7u], 1);
        b.BeginReadSync();
        WalKit.CheckPattern(b, 2, 1, Ps);
        WalKit.CheckPattern(b, 4, 0, Ps);
        WalKit.CheckPattern(b, 7, 1, Ps);
        Assert.Equal(3u, a.WalFrameCount);
    }

    [Fact]
    public void ASnapshotNeverSeesLaterCommitsAndABusySnapshotIsReported()
    {
        MemoryVfs vfs = WalKit.PrepareWal(8, Ps);
        using var w = PagerClass.Open(vfs, Kit.Db, WalKit.Opts());
        using var r = PagerClass.Open(vfs, Kit.Db, WalKit.Opts());
        r.BeginReadSync();
        WalKit.CheckPattern(r, 3, 0, Ps);
        w.BeginReadSync();
        WalKit.Write(w, [3u], 1);
        w.EndReadSync();
        // r still reads its snapshot.
        r.Release(r.GetSync(4));
        WalKit.CheckPattern(r, 3, 0, Ps);
        // r cannot start writing from the old snapshot.
        Assert.Equal(PagerStatus.Error, WalKit.Run(r, r.BeginWrite));
        Assert.Equal(ResultCode.BusySnapshot, r.Error?.ExtendedCode);
        r.EndReadSync();
        r.BeginReadSync();
        WalKit.CheckPattern(r, 3, 1, Ps);
    }

    [Fact]
    public void CheckpointCopiesFramesAndEveryModeLeavesTheSameDatabase()
    {
        foreach (CheckpointMode mode in Enum.GetValues<CheckpointMode>())
        {
            MemoryVfs vfs = WalKit.PrepareWal(8, Ps);
            using var a = PagerClass.Open(vfs, Kit.Db, WalKit.Opts(autoCheckpoint: 0));
            a.BeginReadSync();
            WalKit.Write(a, [2u, 3u], 1);
            WalKit.Write(a, [3u, 9u == 9u ? 4u : 0u], 2);
            a.EndReadSync();
            Assert.True(Kit.ReadFile(vfs, Kit.Db + "-wal").Length > 0);
            CheckpointResult res = a.CheckpointSync(mode);
            Assert.False(res.Busy, mode.ToString());
            byte[] db = Kit.ReadFile(vfs, Kit.Db);
            Assert.Equal(8 * Ps, db.Length);
            Assert.True(db.AsSpan(1 * Ps, Ps).SequenceEqual(WalKit.Expect(2, 1, Ps)), mode + " page 2");
            Assert.True(db.AsSpan(2 * Ps, Ps).SequenceEqual(WalKit.Expect(3, 2, Ps)), mode + " page 3");
            Assert.True(db.AsSpan(3 * Ps, Ps).SequenceEqual(WalKit.Expect(4, 2, Ps)), mode + " page 4");
            if (mode == CheckpointMode.Truncate)
            {
                Assert.Empty(Kit.ReadFile(vfs, Kit.Db + "-wal"));
            }

            // The log still works after the checkpoint (restart or not).
            a.BeginReadSync();
            WalKit.Write(a, [5u], 3);
            WalKit.CheckPattern(a, 5, 3, Ps);
            WalKit.CheckPattern(a, 3, 2, Ps);
            a.EndReadSync();
        }
    }

    [Fact]
    public void ACheckpointStopsAtTheOldestReader()
    {
        MemoryVfs vfs = WalKit.PrepareWal(8, Ps);
        using var w = PagerClass.Open(vfs, Kit.Db, WalKit.Opts(autoCheckpoint: 0));
        using var r = PagerClass.Open(vfs, Kit.Db, WalKit.Opts());
        w.BeginReadSync();
        WalKit.Write(w, [2u], 1);
        r.BeginReadSync();
        WalKit.Write(w, [2u], 2);
        w.EndReadSync();
        CheckpointResult res = w.CheckpointSync(CheckpointMode.Passive);
        Assert.False(res.Busy);
        Assert.True(res.CheckpointedFrames < res.LogFrames, $"{res}");
        // The reader's snapshot is intact: generation 1 of page 2.
        WalKit.CheckPattern(r, 2, 1, Ps);
        r.EndReadSync();
        res = w.CheckpointSync(CheckpointMode.Passive);
        Assert.Equal(res.LogFrames, res.CheckpointedFrames);
        Assert.True(Kit.ReadFile(vfs, Kit.Db).AsSpan(Ps, Ps).SequenceEqual(WalKit.Expect(2, 2, Ps)));
    }

    [Fact]
    public void RollbackDropsTheChangesAndALaterCommitSucceeds()
    {
        MemoryVfs vfs = WalKit.PrepareWal(8, Ps);
        using var a = PagerClass.Open(vfs, Kit.Db, WalKit.Opts());
        a.BeginReadSync();
        a.BeginWriteSync();
        Kit.Rewrite(a, [2u, 3u], 5);
        a.RollbackSync();
        WalKit.CheckPattern(a, 2, 0, Ps);
        WalKit.Write(a, [4u], 1);
        a.EndReadSync();
        using var b = PagerClass.Open(vfs, Kit.Db, WalKit.Opts());
        b.BeginReadSync();
        WalKit.CheckPattern(b, 2, 0, Ps);
        WalKit.CheckPattern(b, 4, 1, Ps);
    }

    [Fact]
    public void ALargeTransactionSpillsIntoTheWalAndStaysInvisibleUntilCommit()
    {
        MemoryVfs vfs = WalKit.PrepareWal(100, Ps);
        using var w = PagerClass.Open(vfs, Kit.Db, WalKit.Opts(cache: 10, autoCheckpoint: 0));
        using var r = PagerClass.Open(vfs, Kit.Db, WalKit.Opts());
        w.BeginReadSync();
        w.BeginWriteSync();
        Kit.Rewrite(w, Kit.Range(2, 60), 1);
        r.BeginReadSync();
        WalKit.CheckPattern(r, 30, 0, Ps);
        r.EndReadSync();
        // The writer reads its own spilled pages back.
        WalKit.CheckPattern(w, 5, 1, Ps);
        w.CommitSync();
        w.EndReadSync();
        r.BeginReadSync();
        for (uint n = 2; n <= 61; n++)
        {
            WalKit.CheckPattern(r, n, 1, Ps);
        }

        WalKit.CheckPattern(r, 62, 0, Ps);
    }

    [Fact]
    public void ARolledBackSpillLeavesNothingBehind()
    {
        MemoryVfs vfs = WalKit.PrepareWal(100, Ps);
        using var w = PagerClass.Open(vfs, Kit.Db, WalKit.Opts(cache: 10, autoCheckpoint: 0));
        w.BeginReadSync();
        w.BeginWriteSync();
        Kit.Rewrite(w, Kit.Range(2, 60), 1);
        w.RollbackSync();
        for (uint n = 2; n <= 61; n++)
        {
            WalKit.CheckPattern(w, n, 0, Ps);
        }

        WalKit.Write(w, [7u], 2);
        w.EndReadSync();
        using var r = PagerClass.Open(vfs, Kit.Db, WalKit.Opts());
        r.BeginReadSync();
        WalKit.CheckPattern(r, 7, 2, Ps);
        WalKit.CheckPattern(r, 8, 0, Ps);
    }

    [Fact]
    public void RecoveryRebuildsTheIndexFromTheWalWhenTheShmIsGone()
    {
        MemoryVfs vfs = WalKit.PrepareWal(8, Ps);
        var a = PagerClass.Open(vfs, Kit.Db, WalKit.Opts(autoCheckpoint: 0));
        a.BeginReadSync();
        WalKit.Write(a, [2u, 3u], 1);
        WalKit.Write(a, [3u], 2);
        a.EndReadSync();
        // A crashed process leaves the database and the log; the wal-index is gone.
        var fresh = new MemoryVfs();
        fresh.Import(Kit.Db, vfs.Export(Kit.Db)!);
        fresh.Import(Kit.Db + "-wal", vfs.Export(Kit.Db + "-wal")!);
        using PagerClass b = PagerClass.Open(fresh, Kit.Db, WalKit.Opts());
        b.BeginReadSync();
        WalKit.CheckPattern(b, 2, 1, Ps);
        WalKit.CheckPattern(b, 3, 2, Ps);
        WalKit.CheckPattern(b, 4, 0, Ps);
        a.Dispose();
    }

    [Fact]
    public void ATornFinalFrameIsIgnoredByRecovery()
    {
        MemoryVfs vfs = WalKit.PrepareWal(8, Ps);
        var a = PagerClass.Open(vfs, Kit.Db, WalKit.Opts(autoCheckpoint: 0));
        a.BeginReadSync();
        WalKit.Write(a, [2u], 1);
        WalKit.Write(a, [3u, 4u], 2);
        a.EndReadSync();
        byte[] wal = vfs.Export(Kit.Db + "-wal")!;
        // Cut the log in the middle of the last commit's last frame.
        int cut = wal.Length - Ps / 2;
        var fresh = new MemoryVfs();
        fresh.Import(Kit.Db, vfs.Export(Kit.Db)!);
        fresh.Import(Kit.Db + "-wal", wal.AsSpan(0, cut));
        using PagerClass b = PagerClass.Open(fresh, Kit.Db, WalKit.Opts());
        b.BeginReadSync();
        WalKit.CheckPattern(b, 2, 1, Ps);
        WalKit.CheckPattern(b, 3, 0, Ps);
        WalKit.CheckPattern(b, 4, 0, Ps);
        a.Dispose();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void YieldInjectionDoesNotChangeTheResult(int every)
    {
        MemoryVfs vfs = WalKit.PrepareWal(40, Ps);
        using var a = PagerClass.Open(vfs, Kit.Db, WalKit.Opts(yield: every, cache: 12, autoCheckpoint: 5));
        a.BeginReadSync();
        for (int g = 1; g <= 6; g++)
        {
            WalKit.Write(a, Kit.Range(2, 5 * g), g);
        }

        a.EndReadSync();
        using var b = PagerClass.Open(vfs, Kit.Db, WalKit.Opts(yield: every));
        b.BeginReadSync();
        for (uint n = 2; n <= 40; n++)
        {
            int gen = 0;
            for (int g = 1; g <= 6; g++)
            {
                if (n < 2 + 5 * g)
                {
                    gen = g;
                }
            }

            WalKit.CheckPattern(b, n, gen, Ps);
        }
    }

    [Fact]
    public void LeavingWalModeRestoresTheRollbackHeaderAndRemovesTheFiles()
    {
        MemoryVfs vfs = WalKit.PrepareWal(8, Ps);
        using var a = PagerClass.Open(vfs, Kit.Db, WalKit.Opts());
        a.BeginReadSync();
        WalKit.Write(a, [2u], 1);
        a.ChangeJournalModeSync(JournalMode.Delete);
        Assert.Equal(JournalMode.Delete, a.JournalMode);
        Assert.False(Kit.Exists(vfs, Kit.Db + "-wal"));
        byte[] img = Kit.ReadFile(vfs, Kit.Db);
        Assert.Equal(1, img[18]);
        Assert.Equal(1, img[19]);
        Assert.True(img.AsSpan(Ps, Ps).SequenceEqual(WalKit.Expect(2, 1, Ps)));
        WalKit.Write(a, [3u], 4);
        a.EndReadSync();
        Assert.False(Kit.Exists(vfs, Kit.Db + "-journal"));
    }

    [Fact]
    public void LeavingWalModeIsRefusedWhileAnotherConnectionIsOpen()
    {
        MemoryVfs vfs = WalKit.PrepareWal(8, Ps);
        using var a = PagerClass.Open(vfs, Kit.Db, WalKit.Opts());
        using var b = PagerClass.Open(vfs, Kit.Db, WalKit.Opts());
        b.BeginReadSync();
        a.BeginReadSync();
        PagerStatus s = WalKit.Run(a, () => a.ChangeJournalMode(JournalMode.Delete));
        Assert.Equal(PagerStatus.Error, s);
        Assert.Equal(ResultCode.Busy, a.Error!.ExtendedCode & 0xFF);
        Assert.Equal(JournalMode.Wal, a.JournalMode);
        WalKit.CheckPattern(a, 2, 0, Ps);
    }
}
