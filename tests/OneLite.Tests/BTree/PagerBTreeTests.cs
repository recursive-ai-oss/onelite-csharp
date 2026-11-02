using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;
using RecursiveAI.OneLite.Tests.Vfs;

namespace RecursiveAI.OneLite.Tests.Trees;

/// <summary>
/// The B-tree on the real pager: the same randomized model workload as the in-memory tests, on files through the rollback journal,
/// with the pager's Yield injection forcing every restart path, commits in the middle, and the pinned <c>sqlite3</c> as the judge of
/// what lands on disk (integrity_check, its <c>.dump</c> against the same operations done by the shell itself).
/// </summary>
public sealed class PagerBTreeTests
{
    private static readonly object LogLock = new();

    public static IEnumerable<object[]> WriteConfigs()
    {
        foreach (int ps in new[] { 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536 })
        {
            foreach (TextEncoding enc in new[] { TextEncoding.Utf8, TextEncoding.Utf16Le, TextEncoding.Utf16Be })
            {
                foreach (int av in new[] { 0, 1, 2 })
                {
                    // journal modes rotate across the grid so every page size, encoding and vacuum mode meets every mode
                    JournalMode jm = (ps / 512 + (int)enc + av) % 3 == 0 ? JournalMode.Delete : (ps / 512 + (int)enc + av) % 3 == 1 ? JournalMode.Truncate : JournalMode.Persist;
                    yield return new object[] { ps, enc, av, jm, 0, 0 };
                    yield return new object[] { ps, enc, av, jm, 3, 0 };
                    yield return new object[] { ps, enc, av, JournalMode.Delete, 1, 0 };
                }
            }
        }

        yield return new object[] { 1024, TextEncoding.Utf8, 0, JournalMode.Delete, 2, 16 };
        yield return new object[] { 4096, TextEncoding.Utf16Le, 1, JournalMode.Truncate, 7, 32 };
        yield return new object[] { 512, TextEncoding.Utf8, 2, JournalMode.Persist, 0, 8 };
    }

    private static string Scratch(string name)
    {
        string dir = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "onelite-pager-btree");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    internal static PagerOptions Options(int pageSize, TextEncoding enc, int av, JournalMode jm, int yield, int reserved, int cache = 200) => new()
    {
        PageSize = pageSize,
        Encoding = enc,
        AutoVacuum = av != 0,
        IncrementalVacuum = av == 2,
        JournalMode = jm,
        Synchronous = SynchronousMode.Off,
        YieldEvery = yield,
        ReservedBytes = reserved,
        CacheSize = cache,
    };

    private static void Cleanup(string file)
    {
        foreach (string f in new[] { file, file + "-journal" })
        {
            File.Delete(f);
        }
    }

    /// <summary>Runs the model workload on a fresh file, committing every <paramref name="commitEvery"/> operations.</summary>
    internal static TestDb RunWorkload(StorageEngine eng, TextEncoding enc, int ops, int commitEvery, int seed)
    {
        eng.BeginRead();
        eng.BeginWrite();
        var db = new TestDb(eng, enc);
        var rnd = new Random(seed);
        for (int i = 0; i < ops; i++)
        {
            db.Step(rnd);
            if (i % commitEvery == commitEvery - 1)
            {
                db.Verify();
                db.Checkpoint();
            }
        }

        db.Verify();
        db.Finish();
        return db;
    }

    [Theory]
    [MemberData(nameof(WriteConfigs))]
    public void FilesWrittenThroughThePagerPassSqlite(int pageSize, TextEncoding enc, int autoVacuum, JournalMode journal, int yieldEvery, int reserved) =>
        WriteAndCheck(pageSize, enc, autoVacuum, journal, yieldEvery, reserved, 0);

    /// <summary>The same grid with another workload seed and the rotation of journal modes shifted, so each cell meets a different mode and different data.</summary>
    public static IEnumerable<object[]> SecondSeedConfigs()
    {
        foreach (int ps in new[] { 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536 })
        {
            foreach (TextEncoding enc in new[] { TextEncoding.Utf8, TextEncoding.Utf16Le, TextEncoding.Utf16Be })
            {
                foreach (int av in new[] { 0, 1, 2 })
                {
                    int r = (ps / 512 + (int)enc + av + 1) % 3;
                    yield return new object[] { ps, enc, av, r == 0 ? JournalMode.Delete : r == 1 ? JournalMode.Truncate : JournalMode.Persist };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(SecondSeedConfigs))]
    public void FilesWrittenWithASecondSeedPassSqlite(int pageSize, TextEncoding enc, int autoVacuum, JournalMode journal) =>
        WriteAndCheck(pageSize, enc, autoVacuum, journal, 0, 0, 99_991);

    private static void WriteAndCheck(int pageSize, TextEncoding enc, int autoVacuum, JournalMode journal, int yieldEvery, int reserved, int seedOffset)
    {
        _ = SqliteOracle.Require();
        string file = Scratch($"w{seedOffset}-{pageSize}-{(int)enc}-{autoVacuum}-{journal}-{yieldEvery}-{reserved}.db");
        Cleanup(file);
        try
        {
            TestDb db;
            using (StorageEngine eng = StorageEngine.Open(new FileVfs(), file, Options(pageSize, enc, autoVacuum, journal, yieldEvery, reserved)))
            {
                int n = pageSize >= 32768 ? 400 : 1200;
                db = RunWorkload(eng, enc, n, 131, pageSize * 7 + (int)enc * 101 + autoVacuum * 13 + reserved + seedOffset);
                eng.EndRead();
            }

            SqliteWriteInteropTests.SqliteChecks(db, file, pageSize, enc, autoVacuum);
            SqliteWriteInteropTests.DumpsEqual(db, file, pageSize, enc, autoVacuum);
            Log($"ok seed={seedOffset} page={pageSize} enc={(int)enc} av={autoVacuum} journal={journal} yield={yieldEvery} reserved={reserved} rows={db.Rows.Count} wr={db.Wr.Count} size={new FileInfo(file).Length}");
        }
        catch
        {
            string? keep = Environment.GetEnvironmentVariable("ONELITE_KEEP");
            if (keep != null && File.Exists(file))
            {
                Directory.CreateDirectory(keep);
                File.Copy(file, Path.Combine(keep, Path.GetFileName(file)), overwrite: true);
            }

            throw;
        }
        finally
        {
            Cleanup(file);
        }
    }

    public static IEnumerable<object[]> YieldFileGrid()
    {
        foreach (int ps in new[] { 512, 4096, 16384, 65536 })
        {
            foreach (int av in new[] { 0, 1, 2 })
            {
                foreach (int y in new[] { 1, 2, 3, 7 })
                {
                    foreach (TextEncoding enc in new[] { TextEncoding.Utf8, TextEncoding.Utf16Le })
                    {
                        yield return new object[] { ps, av, y, JournalMode.Delete, enc };
                    }

                    yield return new object[] { ps, av, y, JournalMode.Persist, TextEncoding.Utf16Be };
                }
            }
        }
    }

    /// <summary>The workload on a real file with every restart path forced, checked against the model only (no oracle needed).</summary>
    [Theory]
    [MemberData(nameof(YieldFileGrid))]
    public void YieldInjectionOnFilesKeepsTheModel(int pageSize, int autoVacuum, int yieldEvery, JournalMode jm, TextEncoding enc)
    {
        string file = Scratch($"y-{pageSize}-{autoVacuum}-{yieldEvery}-{jm}-{(int)enc}.db");
        Cleanup(file);
        try
        {
            using StorageEngine eng = StorageEngine.Open(new FileVfs(), file, Options(pageSize, enc, autoVacuum, jm, yieldEvery, 0));
            RunWorkload(eng, enc, pageSize >= 32768 ? 400 : 1200, 131, pageSize * 7 + (int)enc * 101 + autoVacuum * 13);
            eng.EndRead();
        }
        finally
        {
            Cleanup(file);
        }
    }

    /// <summary>Same workload on the memory VFS with a tiny cache (spills), results independent of Yield injection.</summary>
    public static IEnumerable<object[]> YieldGrid()
    {
        foreach (int ps in new[] { 512, 1024, 4096 })
        {
            foreach (int av in new[] { 0, 1, 2 })
            {
                foreach (int y in new[] { 1, 2, 3, 7 })
                {
                    yield return new object[] { ps, av, y };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(YieldGrid))]
    public void InjectedYieldsDoNotChangeTheFile(int pageSize, int autoVacuum, int yieldEvery)
    {
        byte[] Run(int yield)
        {
            var vfs = new MemoryVfs();
            using (StorageEngine eng = StorageEngine.Open(vfs, "m.db", Options(pageSize, TextEncoding.Utf8, autoVacuum, JournalMode.Delete, yield, 0, cache: 12)))
            {
                RunWorkload(eng, TextEncoding.Utf8, 600, 97, 4242);
                eng.EndRead();
            }

            using IVfsFile f = vfs.Open("m.db", VfsOpenOptions.ReadOnly);
            var b = new byte[f.FileSize()];
            f.Read(0, b);
            return b;
        }

        byte[] plain = Run(0);
        byte[] injected = Run(yieldEvery);
        Assert.Equal(plain.Length, injected.Length);
        Assert.True(plain.AsSpan().SequenceEqual(injected), "the injected run produced a different file");
    }

    /// <summary>
    /// fileformat2 §1.3: offset 52 holds the largest root page (non-zero only in auto-vacuum files) and offset 64 the incremental-vacuum flag.
    /// A new database with one table must carry the same values as the pinned shell writes for <c>PRAGMA auto_vacuum=1|2; CREATE TABLE</c>.
    /// </summary>
    [Theory]
    [InlineData(0, 512)]
    [InlineData(1, 512)]
    [InlineData(2, 512)]
    [InlineData(1, 4096)]
    [InlineData(2, 4096)]
    [InlineData(2, 65536)]
    public void AutoVacuumHeaderFieldsMatchSqlite(int autoVacuum, int pageSize)
    {
        _ = SqliteOracle.Require();
        string ours = Scratch($"hdr-{autoVacuum}-{pageSize}.db");
        string theirs = ours + ".ref";
        Cleanup(ours);
        File.Delete(theirs);
        try
        {
            using (StorageEngine eng = StorageEngine.Open(new FileVfs(), ours, Options(pageSize, TextEncoding.Utf8, autoVacuum, JournalMode.Delete, 0, 0)))
            {
                eng.BeginRead();
                eng.BeginWrite();
                uint root = 0;
                Assert.Equal(BtStatus.Ok, eng.Run(() => BtAdmin.CreateTree(eng.Context, true, out root)));
                byte[] rec = RecursiveAI.OneLite.Storage.Record.RecordWriter.ToArray([RecursiveAI.OneLite.Storage.Record.Mem.FromText("table", TextEncoding.Utf8), RecursiveAI.OneLite.Storage.Record.Mem.FromText("t", TextEncoding.Utf8), RecursiveAI.OneLite.Storage.Record.Mem.FromText("t", TextEncoding.Utf8), RecursiveAI.OneLite.Storage.Record.Mem.FromInt(root), RecursiveAI.OneLite.Storage.Record.Mem.FromText("CREATE TABLE t(x)", TextEncoding.Utf8)]);
                using (BtCursor sc = BTree.Table(eng.Context, 1).OpenCursor())
                {
                    Assert.Equal(BtStatus.Ok, eng.Run(() => sc.Insert(1, rec)));
                }

                eng.Commit();
                eng.EndRead();
            }

            SqliteWriteInteropTests.RunScript(theirs, $"PRAGMA page_size={pageSize}; PRAGMA auto_vacuum={autoVacuum}; CREATE TABLE t(x);\n", Path.GetDirectoryName(ours)!);
            byte[] a = File.ReadAllBytes(ours), b = File.ReadAllBytes(theirs);
            Assert.Equal(b.AsSpan(52, 4).ToArray(), a.AsSpan(52, 4).ToArray());
            Assert.Equal(b.AsSpan(64, 4).ToArray(), a.AsSpan(64, 4).ToArray());
            Assert.Equal(b.Length, a.Length);
            (int exit, string output) = SqliteOracle.Run(ours, "PRAGMA integrity_check;", 60_000);
            Assert.Equal(0, exit);
            Assert.Equal("ok", output.Trim());
        }
        finally
        {
            Cleanup(ours);
            File.Delete(theirs);
        }
    }

    /// <summary>A commit that fails with BUSY (another connection holds SHARED) is rolled back; the same change made again afterwards must commit.</summary>
    [Theory]
    [InlineData(JournalMode.Delete, 0)]
    [InlineData(JournalMode.Delete, 1)]
    [InlineData(JournalMode.Truncate, 0)]
    [InlineData(JournalMode.Persist, 1)]
    public void ACommitThatFailedWithBusyCanBeRetried(JournalMode jm, int autoVacuum)
    {
        string file = Scratch($"busy-{jm}-{autoVacuum}.db");
        Cleanup(file);
        try
        {
            PagerOptions opts = Options(1024, TextEncoding.Utf8, autoVacuum, jm, 0, 0, cache: 500);
            using StorageEngine a = StorageEngine.Open(new FileVfs(), file, opts);
            using StorageEngine other = StorageEngine.Open(new FileVfs(), file, opts);
            // seed: one table with some rows
            a.BeginRead();
            a.BeginWrite();
            uint root = 0;
            BtStatus cs = a.Run(() => BtAdmin.CreateTree(a.Context, true, out root));
            Assert.True(cs == BtStatus.Ok, a.Source.LastError + " " + (a.Source.LastError as OneLiteException)?.Detail);
            InsertRows(a, root, 1, 50);
            a.Commit();
            a.EndRead();

            for (int attempt = 0; attempt < 3; attempt++)
            {
                other.BeginRead();
                a.BeginRead();
                a.BeginWrite();
                InsertRows(a, root, 100 + (attempt * 100), 60);
                OneLiteException e = Assert.Throws<OneLiteException>(a.Commit);
                Assert.Equal(ResultCode.Busy, e.Code);
                a.Rollback();
                a.EndRead();
                other.EndRead();
            }

            a.BeginRead();
            a.BeginWrite();
            InsertRows(a, root, 1000, 60);
            a.Commit();
            a.EndRead();

            using StorageEngine c = StorageEngine.Open(new FileVfs(), file, opts);
            c.BeginRead();
            using BtCursor cur = BTree.Table(c.Context, root).OpenCursor();
            int n = 0;
            Assert.Equal(BtStatus.Ok, c.Run(cur.First));
            while (cur.State == CursorState.Valid)
            {
                n++;
                BtStatus st = c.Run(cur.Next);
                Assert.True(st == BtStatus.Ok, $"after {n} rows: {cur.Error} {(cur.Error as OneLiteException)?.Detail}");
            }

            Assert.Equal(110, n);
            c.EndRead();
        }
        finally
        {
            Cleanup(file);
        }
    }

    private static void InsertRows(StorageEngine eng, uint root, long first, int count)
    {
        using BtCursor c = BTree.Table(eng.Context, root).OpenCursor();
        for (int i = 0; i < count; i++)
        {
            long id = first + i;
            byte[] rec = RecursiveAI.OneLite.Storage.Record.RecordWriter.ToArray([RecursiveAI.OneLite.Storage.Record.Mem.FromInt(id), RecursiveAI.OneLite.Storage.Record.Mem.FromBlob(new byte[200 + (i * 13 % 300)])]);
            Assert.Equal(BtStatus.Ok, eng.Run(() => c.Insert(id, rec)));
        }
    }

    private static void Log(string line)
    {
        string? path = Environment.GetEnvironmentVariable("ONELITE_INTEROP_LOG");
        if (path == null)
        {
            return;
        }

        lock (LogLock)
        {
            File.AppendAllText(path, line + "\n");
        }
    }
}
