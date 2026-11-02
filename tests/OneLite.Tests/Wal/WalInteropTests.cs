using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;
using RecursiveAI.OneLite.Storage.Wal;
using RecursiveAI.OneLite.Tests.Vfs;
using PagerClass = RecursiveAI.OneLite.Storage.Pager.Pager;

namespace RecursiveAI.OneLite.Tests.Wal;

/// <summary>
/// Files made by the pinned <c>sqlite3</c> read by our pager, and files made by our pager read by <c>sqlite3</c>, with the reference shell holding
/// the database open as a live peer. Skipped when the shell is not installed (ONELITE_SQLITE3).
/// </summary>
public class WalInteropTests
{
    private readonly ITestOutputHelper _out;

    public WalInteropTests(ITestOutputHelper output) => _out = output;

    private static string NewDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "walinterop-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static PagerOptions Opts(int pageSize, SynchronousMode sm = SynchronousMode.Full, int cache = 200, int autoCheckpoint = 0) =>
        new() { PageSize = pageSize, Synchronous = sm, CacheSize = cache, WalAutoCheckpoint = autoCheckpoint, Busy = n => n < 400 ? 5 : -1 };

    private static List<byte[]> Snapshot(FileVfs vfs, string db, int pageSize, out uint pages)
    {
        using PagerClass p = PagerClass.Open(vfs, db, Opts(pageSize));
        p.BeginReadSync();
        pages = p.PageCount;
        var list = new List<byte[]>();
        for (uint n = 1; n <= pages; n++)
        {
            list.Add(WalKit.Page(p, n));
        }

        p.EndReadSync();
        return list;
    }

    private static void Fill(SqliteOracle.Session s, int rows, int tag)
    {
        var sb = new StringBuilder("begin;");
        for (int i = 0; i < rows; i++)
        {
            sb.Append($"insert into t(a, b) values({tag * 1000 + i}, hex(randomblob({20 + (i * 37 + tag) % 300})));");
        }

        sb.Append("commit;");
        s.Run(sb.ToString());
    }

    private static string ImageDump(string sqlite, string dbCopy)
    {
        // The image the reference shell makes of a copy: checkpoint everything and read the database file.
        (int exit, string output) = SqliteOracle.Run(dbCopy, "pragma wal_checkpoint(truncate);");
        Assert.Equal(0, exit);
        return output;
    }

    private static void CopyFiles(string from, string to, bool withShm)
    {
        Directory.CreateDirectory(to);
        foreach (string suffix in withShm ? new[] { "", "-wal", "-shm" } : new[] { "", "-wal" })
        {
            string src = from + suffix;
            if (File.Exists(src))
            {
                using var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var o = new FileStream(Path.Combine(to, Path.GetFileName(src)), FileMode.Create);
                i.CopyTo(o);
            }
        }
    }

    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(4096)]
    [InlineData(32768)]
    [InlineData(65536)]
    public void WeReadASnapshotOfALogTheShellWrote(int pageSize)
    {
        string sqlite = SqliteOracle.Require();
        string dir = NewDir();
        string db = Path.Combine(dir, "a.db");
        var vfs = new FileVfs();
        using (var s = new SqliteOracle.Session(db))
        {
            s.Run($"pragma page_size={pageSize}; pragma journal_mode=wal; create table t(id integer primary key, a, b); create index tb on t(b);");
            Fill(s, 60, 1);
            s.Run("pragma wal_checkpoint(passive);");
            Fill(s, 60, 2);
            s.Run("delete from t where id % 5 = 0; update t set b = hex(randomblob(500)) where id % 7 = 0;");
            Assert.True(File.Exists(db + "-wal") && new FileInfo(db + "-wal").Length > 0);
            // While the shell holds the file, read through the shared wal-index.
            List<byte[]> live = Snapshot(vfs, db, pageSize, out uint pages);
            // A copy of the three files, and a copy without the wal-index (a crash).
            CopyFiles(db, Path.Combine(dir, "withshm"), true);
            CopyFiles(db, Path.Combine(dir, "noshm"), false);
            s.Run("pragma wal_checkpoint(truncate);");
            byte[] after = File.ReadAllBytes(db);
            Assert.Equal((long)pages * pageSize, after.Length);
            for (int i = 0; i < live.Count; i++)
            {
                Assert.True(live[i].AsSpan().SequenceEqual(after.AsSpan(i * pageSize, pageSize)), $"live page {i + 1} differs from the checkpointed database (page size {pageSize})");
            }

            // The copy without -shm: we recover the index from the log; the shell recovers its own copy; same image.
            List<byte[]> rec = Snapshot(vfs, Path.Combine(dir, "noshm", "a.db"), pageSize, out uint rpages);
            Assert.Equal(pages, rpages);
            for (int i = 0; i < rec.Count; i++)
            {
                Assert.True(rec[i].AsSpan().SequenceEqual(after.AsSpan(i * pageSize, pageSize)), $"recovered page {i + 1} differs (page size {pageSize})");
            }
        }

        Directory.Delete(dir, true);
        _ = sqlite;
    }

    [Fact]
    public void ManyGenerationsAndATornFinalFrameReadLikeTheShell()
    {
        SqliteOracle.Require();
        string dir = NewDir();
        string db = Path.Combine(dir, "g.db");
        var vfs = new FileVfs();
        using var s = new SqliteOracle.Session(db);
        s.Run("pragma page_size=1024; pragma journal_mode=wal; create table t(id integer primary key, a, b);");
        for (int g = 1; g <= 4; g++)
        {
            Fill(s, 30, g);
            // Restart the log with a full checkpoint every other generation: new salts and a new sequence number.
            s.Run(g % 2 == 0 ? "pragma wal_checkpoint(restart);" : "select 1;");
        }

        Fill(s, 10, 9);
        string live = Path.Combine(dir, "live");
        CopyFiles(db, live, false);
        string walCopy = Path.Combine(live, "g.db-wal");
        long len = new FileInfo(walCopy).Length;
        // Cut inside the last frame: the final commit is torn and must be ignored, by both implementations.
        foreach (long cut in new[] { len, len - 100, len - 1024 - 24 - 5, len / 2 + 7 })
        {
            string d2 = Path.Combine(dir, "cut" + cut);
            CopyFiles(db, d2, false);
            using (var fs = new FileStream(Path.Combine(d2, "g.db-wal"), FileMode.Open))
            {
                fs.SetLength(cut);
            }

            List<byte[]> ours = Snapshot(vfs, Path.Combine(d2, "g.db"), 1024, out uint pages);
            ImageDump("", Path.Combine(d2, "g.db"));
            byte[] theirs = File.ReadAllBytes(Path.Combine(d2, "g.db"));
            Assert.Equal((long)pages * 1024, theirs.Length);
            for (int i = 0; i < ours.Count; i++)
            {
                Assert.True(ours[i].AsSpan().SequenceEqual(theirs.AsSpan(i * 1024, 1024)), $"cut at {cut}: page {i + 1} differs");
            }
        }

        Directory.Delete(dir, true);
    }

    [Fact]
    public void ACheckpointByUsOfALogTheShellWroteGivesTheShellsDatabase()
    {
        SqliteOracle.Require();
        string dir = NewDir();
        string db = Path.Combine(dir, "c.db");
        var vfs = new FileVfs();
        using var s = new SqliteOracle.Session(db);
        s.Run("pragma page_size=4096; pragma journal_mode=wal; create table t(id integer primary key, a, b);");
        Fill(s, 50, 1);
        Fill(s, 50, 2);
        string copy = Path.Combine(dir, "copy");
        CopyFiles(db, copy, false);
        // Ours: open the copy (no shm: we recover), checkpoint everything.
        using (PagerClass p = PagerClass.Open(vfs, Path.Combine(copy, "c.db"), Opts(4096)))
        {
            p.BeginReadSync();
            p.EndReadSync();
            CheckpointResult r = p.CheckpointSync(CheckpointMode.Truncate);
            Assert.False(r.Busy);
        }

        string copy2 = Path.Combine(dir, "copy2");
        CopyFiles(db, copy2, false);
        ImageDump("", Path.Combine(copy2, "c.db"));
        Assert.True(File.ReadAllBytes(Path.Combine(copy, "c.db")).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(copy2, "c.db"))), "database after our checkpoint differs from the shell's");
        Directory.Delete(dir, true);
    }

    [Theory]
    [InlineData(1024, SynchronousMode.Full)]
    [InlineData(4096, SynchronousMode.Normal)]
    public void TheShellReadsWhatWeCommitAndOursCheckpointedFileIsSound(int pageSize, SynchronousMode sm)
    {
        SqliteOracle.Require();
        string dir = NewDir();
        string db = Path.Combine(dir, "w.db");
        var vfs = new FileVfs();
        // The shell makes the database (rollback journal mode) and we turn it into a WAL database ourselves.
        (int exit, string o) = SqliteOracle.Run(db, $"pragma page_size={pageSize}; create table t(id integer primary key, a text); insert into t(a) select 'AAAAAAAAAA' from generate_series(1,40);");
        Assert.Equal(0, exit);
        using (PagerClass p = PagerClass.Open(vfs, db, Opts(pageSize, sm)))
        {
            p.BeginReadSync();
            p.ChangeJournalModeSync(JournalMode.Wal);
            p.EndReadSync();
            Assert.Equal("wal", SqliteOracle.Run(db, "pragma journal_mode;").Output.Trim());

            using var s = new SqliteOracle.Session(db);
            Assert.Equal("40|40|0", s.Run("select count(*), sum(a='AAAAAAAAAA'), sum(a='BBBBBBBBBB') from t;").Trim());
            for (int round = 1; round <= 5; round++)
            {
                // Rewrite ten bytes of text in place on every page that holds some, as a row update that does not change any size would.
                p.BeginReadSync();
                p.BeginWriteSync();
                byte[] from = Encoding.ASCII.GetBytes(new string((char)('A' + round - 1), 10));
                byte[] to = Encoding.ASCII.GetBytes(new string((char)('A' + round), 10));
                for (uint n = 2; n <= p.PageCount; n++)
                {
                    PageFrame f = p.GetSync(n);
                    int at = f.Data.AsSpan().IndexOf(from);
                    if (at >= 0)
                    {
                        p.MakeWritableSync(f);
                        while (at >= 0)
                        {
                            to.CopyTo(f.Data, at);
                            int next = f.Data.AsSpan(at + 10).IndexOf(from);
                            at = next < 0 ? -1 : at + 10 + next;
                        }
                    }

                    p.Release(f);
                }

                // Grow the file and give the pages back: the free list is the one structure a page-level edit can build validly.
                PageFrame x = p.AllocateSync();
                PageFrame y = p.AllocateSync();
                uint xn = x.PageNumber;
                uint yn = y.PageNumber;
                p.Release(x);
                p.Release(y);
                p.FreeSync(xn);
                p.FreeSync(yn);
                p.CommitSync();
                p.EndReadSync();
                string want = new string((char)('A' + round), 10);
                Assert.Equal("40|40", s.Run($"select count(*), sum(a='{want}') from t;").Trim());
                Assert.Equal("ok", s.Run("pragma integrity_check;").Trim());
                if (round == 3)
                {
                    CheckpointResult r = p.CheckpointSync(CheckpointMode.Passive);
                    Assert.False(r.Busy);
                    Assert.Equal("ok", s.Run("pragma integrity_check;").Trim());
                }
            }

        }

        // Back out of WAL: ours removes the log, the shell finds a plain database.
        using (PagerClass p = PagerClass.Open(vfs, db, Opts(pageSize, sm)))
        {
            p.BeginReadSync();
            p.ChangeJournalModeSync(JournalMode.Delete);
            p.EndReadSync();
        }

        (_, string mode) = SqliteOracle.Run(db, "pragma journal_mode; pragma integrity_check; select count(*), sum(a='FFFFFFFFFF') from t;");
        Assert.Equal("delete\nok\n40|40", mode.Replace("\r", "").Trim());
        Directory.Delete(dir, true);
    }

    [Fact]
    public void BothDirectionsShareOneWalAndOneShm()
    {
        SqliteOracle.Require();
        string dir = NewDir();
        string db = Path.Combine(dir, "b.db");
        var vfs = new FileVfs();
        (int exit, _) = SqliteOracle.Run(db, "pragma page_size=1024; create table t(id integer primary key, a text); insert into t(a) select 'AAAAAAAAAA' from generate_series(1,30); pragma journal_mode=wal;");
        Assert.Equal(0, exit);
        using PagerClass p = PagerClass.Open(vfs, db, Opts(1024));
        using var s = new SqliteOracle.Session(db);
        // Alternate writers: the shell inserts rows (growing the log), we edit text in place; each sees the other's commits.
        for (int i = 1; i <= 8; i++)
        {
            s.Run($"insert into t(a) values('AAAAAAAAAA'); update t set a = 'AAAAAAAAAA' where id = 1;");
            p.BeginReadSync();
            Assert.True(p.PageCount >= 2);
            int found = 0;
            for (uint n = 2; n <= p.PageCount; n++)
            {
                found += CountOf(p, n, "AAAAAAAAAA");
            }

            Assert.Equal(30 + i, found);
            p.BeginWriteSync();
            for (uint n = 2; n <= p.PageCount; n++)
            {
                PageFrame f = p.GetSync(n);
                int at = f.Data.AsSpan().IndexOf("AAAAAAAAAA"u8);
                if (at >= 0)
                {
                    p.MakeWritableSync(f);
                    "AAAAAAAAAA"u8.CopyTo(f.Data.AsSpan(at));
                    f.Data[at] = (byte)'A';
                }

                p.Release(f);
            }

            p.CommitSync();
            p.EndReadSync();
        }

        Assert.Equal("ok", s.Run("pragma integrity_check;").Trim());
        Directory.Delete(dir, true);
    }

    [Fact]
    public void OurCheckpointStopsAtAShellReadersSnapshotAndCompletesOnceItEnds()
    {
        SqliteOracle.Require();
        string dir = NewDir();
        string db = Path.Combine(dir, "r.db");
        var s = new SqliteOracle.Session(db);
        s.Run("pragma page_size=1024; pragma journal_mode=wal; pragma wal_autocheckpoint=0; create table t(id integer primary key, a, b);");
        Fill(s, 30, 1);
        var r = new SqliteOracle.Session(db);
        // The pragma echoes its value on the first line; the last line is the query's row.
        string held = r.Run("pragma wal_autocheckpoint=0; begin; select count(*), sum(a) from t;").Trim().Split('\n')[^1];
        Fill(s, 30, 2);
        Fill(s, 30, 3);
        string dumpBefore = s.Run(".dump");
        using (PagerClass p = PagerClass.Open(new FileVfs(), db, Opts(1024)))
        {
            // A pager learns the database is in WAL mode from its first read transaction.
            p.BeginReadSync();
            p.EndReadSync();
            // PASSIVE: copies only what the reader's snapshot allows, never fails and never waits.
            CheckpointResult passive = p.CheckpointSync(CheckpointMode.Passive);
            Assert.False(passive.Busy);
            Assert.True(passive.CheckpointedFrames < passive.LogFrames, $"{passive}");
            Assert.Equal(held, r.Run("select count(*), sum(a) from t;").Trim());
            Assert.Equal("ok", s.Run("pragma integrity_check;").Trim());
            r.Run("commit;");
            CheckpointResult full = p.CheckpointSync(CheckpointMode.Full);
            Assert.False(full.Busy);
            Assert.Equal(full.LogFrames, full.CheckpointedFrames);
        }

        s.Dispose();
        r.Dispose();
        (int exit, string output) = SqliteOracle.Run(db, "pragma integrity_check;");
        Assert.Equal(0, exit);
        Assert.Equal("ok", output.Trim());
        Assert.Equal(dumpBefore.Trim(), SqliteOracle.Run(db, ".dump").Output.Trim());
        Directory.Delete(dir, true);
    }

    /// <summary>
    /// The generated corpus of logs the shell left behind (a log with and without its -shm, after checkpoints with a reader, restarts, big
    /// transactions, garbage after the last frame, a torn tail). Each case is read by us twice (with the shell's -shm, and rebuilt from the log alone)
    /// and compared page by page with what the shell's own recovery and full checkpoint leave in the database file.
    /// Set ONELITE_CORPUS_DB to the corpus's db directory (default ~/onelite/corpus/generated/db).
    /// </summary>
    [Fact]
    public void CorpusLogsReadLikeTheShell()
    {
        SqliteOracle.Require();
        string dir = Environment.GetEnvironmentVariable("ONELITE_CORPUS_DB") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onelite", "corpus", "generated", "db");
        Assert.SkipWhen(!Directory.Exists(dir), "the generated corpus is not installed");
        int cases = 0, pagesCompared = 0;
        var vfs = new FileVfs();
        foreach (string wal in Directory.EnumerateFiles(dir, "wal*.db-wal").Concat(Directory.EnumerateFiles(dir, "walmode*.db-wal")).Order(StringComparer.Ordinal))
        {
            string db = wal[..^4];
            foreach (bool withShm in new[] { true, false })
            {
                string work = NewDir();
                string theirsDir = Path.Combine(work, "theirs");
                string oursDir = Path.Combine(work, "ours");
                CopyFiles(db, theirsDir, withShm);
                CopyFiles(db, oursDir, withShm);
                string name = Path.GetFileName(db);
                byte[] head = new byte[18];
                using (var fs = new FileStream(db, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    _ = fs.Read(head, 0, head.Length);
                }

                int ps = head[16] << 8 | head[17];
                ps = ps == 1 ? 65536 : ps;
                List<byte[]> ours = Snapshot(vfs, Path.Combine(oursDir, name), ps, out uint pages);
                _ = ImageDump("", Path.Combine(theirsDir, name));
                byte[] theirs = File.ReadAllBytes(Path.Combine(theirsDir, name));
                Assert.True((long)pages * ps == theirs.Length, $"{name} (shm {withShm}): we see {pages} pages, the shell's database has {theirs.Length / ps}");
                for (int n = 0; n < ours.Count; n++)
                {
                    Assert.True(ours[n].AsSpan().SequenceEqual(theirs.AsSpan(n * ps, ps)), $"{name} (shm {withShm}): page {n + 1} differs");
                }

                pagesCompared += ours.Count;
                cases++;
                Directory.Delete(work, true);
            }
        }

        Assert.True(cases > 0, "no wal cases in " + dir);
        _out.WriteLine($"{cases} corpus cases, {pagesCompared} pages equal");
        Console.Error.WriteLine($"corpus wal cases: {cases}, pages compared: {pagesCompared}");
    }

    private static int CountOf(PagerClass p, uint n, string text)
    {
        PageFrame f = p.GetSync(n);
        int count = 0;
        ReadOnlySpan<byte> t = Encoding.ASCII.GetBytes(text);
        ReadOnlySpan<byte> d = f.Data;
        for (int at = d.IndexOf(t); at >= 0; at = d[(at + t.Length)..].IndexOf(t) is var k and >= 0 ? at + t.Length + k : -1)
        {
            count++;
        }

        p.Release(f);
        return count;
    }
}
