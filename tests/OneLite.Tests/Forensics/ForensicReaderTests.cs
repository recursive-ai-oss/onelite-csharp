using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using RecursiveAI.OneLite.Fuzz;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Forensics;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Storage.Vfs;
using RecursiveAI.OneLite.Tests.Trees;
using RecursiveAI.OneLite.Tests.Vfs;

namespace RecursiveAI.OneLite.Tests.Forensics;

/// <summary>The forensic reader: log classification, snapshots, space and carving on images built with the B-tree layer, and the never-modify rule.</summary>
public sealed class ForensicReaderTests
{
    private const int PageSize = 1024;

    /// <summary>Builds a database whose table t(id INTEGER PRIMARY KEY, name TEXT, n INTEGER) holds the rows of each step: versions[k] is the file after step k.</summary>
    private static List<byte[]> Versions(params Action<ImagePageSource, BtContext, BTree, uint>[] steps)
    {
        ImagePageSource src = ImagePageSource.Create(PageSize);
        var ctx = new BtContext(src, TextEncoding.Utf8);
        uint root = 0;
        Drive.Run(src, () => BtAdmin.CreateTree(ctx, true, out root));
        BTree t = BTree.Table(ctx, root);
        using (BtCursor sc = BTree.Table(ctx, 1).OpenCursor())
        {
            byte[] rec = RecordWriter.ToArray([Mem.FromText("table", TextEncoding.Utf8), Mem.FromText("t", TextEncoding.Utf8), Mem.FromText("t", TextEncoding.Utf8),
                Mem.FromInt(root), Mem.FromText("CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT, n INTEGER)", TextEncoding.Utf8)]);
            Drive.Ok(src, sc, () => sc.Insert(1, rec));
        }

        var list = new List<byte[]>();
        foreach (Action<ImagePageSource, BtContext, BTree, uint> step in steps)
        {
            step(src, ctx, t, root);
            list.Add(src.ToBytes());
        }

        return list;
    }

    private static Action<ImagePageSource, BtContext, BTree, uint> Insert(int from, int to) => (src, _, t, _) =>
    {
        using BtCursor c = t.OpenCursor();
        for (int id = from; id <= to; id++)
        {
            byte[] rec = RecordWriter.ToArray([Mem.Null, Mem.FromText($"name-{id}-" + new string((char)('a' + (id % 26)), 8 + (id % 7)), TextEncoding.Utf8), Mem.FromInt(id * 1000L)]);
            long rowid = id;
            Drive.Ok(src, c, () => c.Insert(rowid, rec));
        }
    };

    private static Action<ImagePageSource, BtContext, BTree, uint> Delete(int from, int to) => (src, _, t, _) =>
    {
        using BtCursor c = t.OpenCursor();
        for (int id = from; id <= to; id++)
        {
            long rowid = id;
            Drive.Run(src, () => c.MoveToRowid(rowid, out _));
            Drive.Ok(src, c, c.Delete);
        }
    };

    /// <summary>The frames that turn <paramref name="from"/> into <paramref name="to"/>; the last one commits with the new size.</summary>
    private static List<(uint, byte[], uint, uint)> Diff(byte[] from, byte[] to)
    {
        var l = new List<(uint, byte[], uint, uint)>();
        int pages = to.Length / PageSize;
        for (int p = 1; p <= pages; p++)
        {
            ReadOnlySpan<byte> n = to.AsSpan((p - 1) * PageSize, PageSize);
            if (p * PageSize > from.Length || !from.AsSpan((p - 1) * PageSize, PageSize).SequenceEqual(n))
            {
                l.Add(((uint)p, n.ToArray(), 0u, 0u));
            }
        }

        var (pg, img, _, s) = l[^1];
        l[^1] = (pg, img, (uint)pages, s);
        return l;
    }

    private static ForensicDatabase OpenMem(byte[] db, byte[]? wal, byte[]? journal = null)
    {
        var vfs = new MemoryVfs();
        vfs.Import("m.db", db);
        if (wal != null)
        {
            vfs.Import("m.db-wal", wal);
        }

        if (journal != null)
        {
            vfs.Import("m.db-journal", journal);
        }

        return ForensicDatabase.Open("m.db", new ForensicOptions { Vfs = vfs });
    }

    private static int Count(ForensicSnapshot s) => s.ReadRows(s.Tables.Single(t => t.Name == "t")).Count;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SnapshotsShowTheDatabaseAndEachCommit(bool bigEndian)
    {
        List<byte[]> v = Versions(Insert(1, 10), Insert(11, 15), Insert(16, 20));
        var frames = Diff(v[0], v[1]);
        frames.AddRange(Diff(v[1], v[2]));
        byte[] wal = ForensicSeeds.BuildWal(PageSize, bigEndian, 0xA1, 0xB2, frames);
        using ForensicDatabase db = OpenMem(v[0], wal);
        Assert.Equal(2, db.Wal!.Commits.Count);
        Assert.Single(db.Wal.Generations);
        Assert.Equal(10, Count(db.OpenSnapshot(SnapshotSelector.DatabaseOnly)));
        Assert.Equal(20, Count(db.OpenSnapshot()));
        Assert.Equal(15, Count(db.OpenSnapshot(SnapshotSelector.AsOf(db.Wal.Commits[0].Id))));
        Assert.Equal(20, Count(db.OpenSnapshot(SnapshotSelector.AsOf(db.Wal.Commits[1].Id))));
        Assert.Throws<ArgumentException>(() => db.OpenSnapshot(SnapshotSelector.AsOf(99)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FramesAreClassifiedByTheValidityRules(bool bigEndian)
    {
        List<byte[]> v = Versions(Insert(1, 10), Insert(11, 15), Insert(16, 20));
        var frames = Diff(v[0], v[1]);
        int c1 = frames.Count;
        frames.AddRange(Diff(v[1], v[2]));
        // One frame after the last commit that is chained correctly but never committed, then an older generation (other salt) that ends in a commit.
        frames.Add((2, v[2].AsSpan(PageSize, PageSize).ToArray(), 0, 0));
        frames.Add((1, v[1].AsSpan(0, PageSize).ToArray(), 0, 0x77));
        frames.Add((2, v[1].AsSpan(PageSize, PageSize).ToArray(), 3, 0x77));
        byte[] wal = ForensicSeeds.BuildWal(PageSize, bigEndian, 0xA1, 0xB2, frames);
        byte[] torn = [.. wal, .. new byte[100]];
        using ForensicDatabase db = OpenMem(v[0], torn);
        WalInfo w = db.Wal!;
        Assert.True(w.Usable);
        Assert.Equal(frames.Count, w.Frames.Count);
        Assert.Equal(100, w.TornTailBytes);
        Assert.Equal(frames.Count - 2, w.ValidChainEnd);
        Assert.Equal(frames.Count - 3, w.LastCommitFrame);
        Assert.Equal(1, w.Frames.Count(f => f.State == WalFrameState.Trailing));
        Assert.Equal(2, w.Frames.Count(f => f.State == WalFrameState.Stale));
        Assert.Equal(2, w.Generations.Count);
        Assert.False(w.Generations[1].StartsAtFrameOne);
        Assert.Equal(3, w.Commits.Count);
        Assert.True(w.Commits[2].PrefixLost);
        Assert.False(w.Commits[1].PrefixLost);
        Assert.True(c1 > 0);
        // The trailing frame is in the snapshot only when asked for, and then the snapshot says so.
        Assert.False(db.OpenSnapshot().IncludesUncommitted);
        Assert.True(db.OpenSnapshot(SnapshotSelector.LatestWithTrailingFrames).IncludesUncommitted);
    }

    [Fact]
    public void ADamagedFrameEndsTheChain()
    {
        List<byte[]> v = Versions(Insert(1, 10), Insert(11, 15), Insert(16, 20));
        var frames = Diff(v[0], v[1]);
        int first = frames.Count;
        frames.AddRange(Diff(v[1], v[2]));
        byte[] wal = ForensicSeeds.BuildWal(PageSize, false, 0xA1, 0xB2, frames);
        // Flip one byte in the page of the first frame of the second commit.
        wal[32 + (first * (24 + PageSize)) + 24 + 100] ^= 0x40;
        using ForensicDatabase db = OpenMem(v[0], wal);
        Assert.Equal(first, db.Wal!.ValidChainEnd);
        Assert.Equal(WalFrameState.Damaged, db.Wal.Frames[first].State);
        Assert.Equal(15, Count(db.OpenSnapshot()));
        Assert.Single(db.Wal.Commits);
    }

    [Fact]
    public void ABadLogHeaderMakesTheLogUnusableButTheDatabaseReadable()
    {
        List<byte[]> v = Versions(Insert(1, 10), Insert(11, 15));
        byte[] wal = ForensicSeeds.BuildWal(PageSize, false, 1, 2, Diff(v[0], v[1]));
        wal[17] ^= 1;
        using ForensicDatabase db = OpenMem(v[0], wal);
        Assert.False(db.Wal!.Usable);
        Assert.Equal(10, Count(db.OpenSnapshot()));
    }

    [Fact]
    public void DeletedRowsAreCarvedFromThePageImageTheLogReplaced()
    {
        List<byte[]> v = Versions(Insert(1, 40), Delete(3, 9));
        byte[] wal = ForensicSeeds.BuildWal(PageSize, false, 5, 6, Diff(v[0], v[1]));
        using ForensicDatabase db = OpenMem(v[0], wal);
        ForensicSnapshot latest = db.OpenSnapshot();
        Assert.Equal(33, Count(latest));
        IReadOnlyList<CarvedRecord> found = latest.Carve();
        var ids = found.Where(c => c.Rowid is >= 3 and <= 9).Select(c => c.Rowid!.Value).ToHashSet();
        Assert.Equal(7, ids.Count);
        CarvedRecord one = found.First(c => c.Rowid == 5);
        Assert.Equal(CarveSource.SupersededDatabasePage, one.Source);
        Assert.Equal("t", one.Table);
        Assert.StartsWith("name-5-", (string)one.Values[1]!, StringComparison.Ordinal);
        Assert.Equal(5000L, one.Values[2]);
        Assert.True(one.Confidence >= 0.9, "an intact cell of a table-leaf page image scores high: " + one.Confidence);
        Assert.NotEmpty(one.Reasons);
        // Rows still live are not reported (an old copy of a living row is not a find).
        Assert.DoesNotContain(found, c => c.Rowid == 20);
    }

    [Fact]
    public void FilesAreNotChangedByAFullPass()
    {
        List<byte[]> v = Versions(Insert(1, 40), Delete(3, 9));
        string dir = Path.Combine(Path.GetTempPath(), "forensic-nonmod-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string db = Path.Combine(dir, "a.db");
            File.WriteAllBytes(db, v[0]);
            File.WriteAllBytes(db + "-wal", ForensicSeeds.BuildWal(PageSize, false, 5, 6, Diff(v[0], v[1])));
            File.WriteAllBytes(db + "-journal", v[0].AsSpan(0, 512 + (2 * PageSize)).ToArray());
            var all = new[] { db, db + "-wal", db + "-journal" };
            foreach (bool readOnly in new[] { false, true })
            {
                if (readOnly && !OperatingSystem.IsWindows())
                {
                    foreach (string f in all)
                    {
                        File.SetUnixFileMode(f, UnixFileMode.UserRead);
                    }

                    File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                }

                string[] before = all.Select(f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))) + File.GetLastWriteTimeUtc(f).Ticks).ToArray();
                string[] names = Directory.GetFileSystemEntries(dir).Order(StringComparer.Ordinal).ToArray();
                using (ForensicDatabase d = ForensicDatabase.Open(db))
                {
                    foreach (SnapshotSelector sel in new[] { SnapshotSelector.DatabaseOnly, SnapshotSelector.Latest, SnapshotSelector.LatestWithTrailingFrames, SnapshotSelector.AsOf(d.Wal!.Commits[0].Id) })
                    {
                        ForensicSnapshot s = d.OpenSnapshot(sel);
                        _ = Count(s);
                        _ = s.Space();
                        _ = s.Carve(new CarveOptions { MinConfidence = 0 });
                    }
                }

                Assert.Equal(before, all.Select(f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))) + File.GetLastWriteTimeUtc(f).Ticks).ToArray());
                Assert.Equal(names, Directory.GetFileSystemEntries(dir).Order(StringComparer.Ordinal).ToArray());
            }
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                foreach (string f in Directory.GetFiles(dir))
                {
                    File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }

            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void NotADatabaseAndCorruptHeadersAreTypedRefusals()
    {
        var junk = new byte[2000];
        new Random(3).NextBytes(junk);
        Assert.Throws<NotADatabaseException>(() => OpenMem(junk, null));
        Assert.Throws<NotADatabaseException>(() => OpenMem([], null));
        List<byte[]> v = Versions(Insert(1, 5));
        byte[] bad = (byte[])v[0].Clone();
        bad[16] = 0x03;
        bad[17] = 0x01;
        Assert.Throws<OneLiteException>(() => OpenMem(bad, null));
        Assert.Equal(26, new NotADatabaseException("x").Code);
        Assert.Throws<VfsException>(() => ForensicDatabase.Open("missing.db", new ForensicOptions { Vfs = new MemoryVfs() }));
    }

    /// <summary>Found by the mutation fuzzer: a frame naming page 4 billion in a commit of that size made the page table ask for gigabytes.</summary>
    [Fact]
    public void ALogNamingHugePageNumbersDoesNotExhaustMemory()
    {
        List<byte[]> v = Versions(Insert(1, 20));
        byte[] page = v[0].AsSpan(PageSize, PageSize).ToArray();
        byte[] wal = ForensicSeeds.BuildWal(PageSize, false, 1, 2, [(4_000_000_000u, page, 4_000_000_000u, 0u)]);
        using ForensicDatabase db = OpenMem(v[0], wal);
        ForensicSnapshot s = db.OpenSnapshot();
        SpaceReport r = s.Space();
        Assert.True(r.PagesExamined <= 1 << 24);
        _ = s.Carve(new CarveOptions { MinConfidence = 0 });
    }

    [Fact]
    public void BundledFuzzSeedsRunCleanly()
    {
        var rnd = new Random(11);
        for (int i = 0; i < 12; i++)
        {
            ForensicTarget.Run(ForensicSeeds.Make(rnd, i));
        }
    }

    [Fact]
    public void CarvesDeletedRowsOfADatabaseWrittenByTheShell()
    {
        string sqlite = SqliteOracle.Require();
        _ = sqlite;
        string dir = Path.Combine(Path.GetTempPath(), "forensic-carve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string db = Path.Combine(dir, "c.db");
            var sql = new System.Text.StringBuilder("pragma page_size=1024;\ncreate table t(id integer primary key, name text, n integer);\nbegin;\n");
            for (int i = 1; i <= 300; i++)
            {
                sql.Append($"insert into t values({i}, 'row-{i}-{new string((char)('a' + (i % 26)), 6 + (i % 11))}', {i * 37});\n");
            }

            sql.Append("commit;\ndelete from t where id % 3 = 0;\n");
            (int exit, string output) = SqliteOracle.Run(db, sql.ToString());
            Assert.Equal(0, exit);
            _ = output;
            using ForensicDatabase d = ForensicDatabase.Open(db);
            ForensicSnapshot s = d.OpenSnapshot();
            Assert.Equal(200, Count(s));
            IReadOnlyList<CarvedRecord> found = s.Carve();
            var rows = found.Select(c => (string)c.Values[1]!).ToHashSet();
            int hit = Enumerable.Range(1, 100).Count(k => rows.Any(r => r.StartsWith($"row-{k * 3}-", StringComparison.Ordinal)));
            Assert.True(hit >= 70, $"recovered {hit} of 100 deleted rows");
            Assert.All(found, c => Assert.DoesNotContain(c.Reasons, r => r.Length == 0));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
