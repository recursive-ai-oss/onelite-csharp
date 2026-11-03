using System;
using System.Collections.Generic;
using System.Linq;
using RecursiveAI.OneLite.Storage.Crypto;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;
using RecursiveAI.OneLite.Storage.Wal;
using PagerClass = RecursiveAI.OneLite.Storage.Pager.Pager;

namespace RecursiveAI.OneLite.Tests.Crypto;

/// <summary>
/// An encrypted database through the pager: the file holds only encoded pages, the cache plaintext; the journal and WAL carry encoded pages too;
/// wrong keys fail like the reference; and a crash at any storage call leaves the file readable under exactly one key with whole commits.
/// </summary>
public class EncryptedPagerTests
{
    private const string Db = "enc.db";
    private const int Ps = 1024;

    // Few iterations: the tests are about the pager, not about PBKDF2's cost.
    private static readonly CipherSettings Fast = new(Ps, 2, CipherDigest.Sha512, CipherDigest.Sha512);

    private readonly ITestOutputHelper _out;

    public EncryptedPagerTests(ITestOutputHelper output) => _out = output;

    private static PagerOptions Opts(string key, JournalMode jm = JournalMode.Delete, SynchronousMode sm = SynchronousMode.Full, int cache = 100, CipherSettings? s = null) =>
        new() { Key = CipherKey.FromPassphrase(key), Cipher = s ?? Fast, JournalMode = jm, Synchronous = sm, CacheSize = cache, WalAutoCheckpoint = 0 };

    // The page content tests write: a pattern of (page, generation) over the usable part of the page. Page 1 keeps its header.
    private static byte Expect(uint page, int gen, int i) => (byte)((page * 31 + gen * 7 + i) & 0xFF);

    private static void Fill(PageFrame f, int gen)
    {
        for (int i = f.PageNumber == 1 ? 108 : 0; i < f.Data.Length - 80; i++)
        {
            f.Data[i] = Expect(f.PageNumber, gen, i);
        }
    }

    private static bool Matches(PageFrame f, int gen)
    {
        for (int i = f.PageNumber == 1 ? 108 : 0; i < f.Data.Length - 80; i++)
        {
            if (f.Data[i] != Expect(f.PageNumber, gen, i))
            {
                return false;
            }
        }

        return true;
    }

    private static void Create(IVfs vfs, string key, int pages, JournalMode jm = JournalMode.Delete, CipherSettings? s = null)
    {
        // A pager opened with the WAL option already reports WAL mode, which turns ChangeJournalMode(Wal) into a no-op: start in rollback mode.
        using PagerClass p = PagerClass.Open(vfs, Db, Opts(key, jm == JournalMode.Wal ? JournalMode.Delete : jm, s: s));
        p.BeginReadSync();
        p.BeginWriteSync();
        PageFrame p1 = p.GetSync(1);
        p.MakeWritableSync(p1);
        Fill(p1, 0);
        p.Release(p1);
        for (int i = 2; i <= pages; i++)
        {
            PageFrame f = p.AllocateSync();
            Fill(f, 0);
            p.Release(f);
        }

        p.CommitSync();
        if (jm == JournalMode.Wal)
        {
            p.ChangeJournalModeSync(JournalMode.Wal);
        }

        p.EndReadSync();
    }

    private static void Rewrite(PagerClass p, int from, int to, int gen)
    {
        for (uint n = (uint)from; n <= to; n++)
        {
            PageFrame f = p.GetSync(n);
            p.MakeWritableSync(f);
            Fill(f, gen);
            p.Release(f);
        }
    }

    // The generation every page 1..pages shows, or -1 if they disagree or a page fails to read.
    private static int ReadGeneration(IVfs vfs, string key, int pages, CipherSettings? s = null)
    {
        using PagerClass p = PagerClass.Open(vfs, Db, Opts(key, JournalMode.Delete, s: s));
        p.BeginReadSync();
        try
        {
            Assert.Equal((uint)pages, p.PageCount);
            for (int gen = 0; gen <= 4; gen++)
            {
                bool all = true;
                for (uint n = 2; n <= pages && all; n++)
                {
                    PageFrame f = p.GetSync(n);
                    all = Matches(f, gen);
                    p.Release(f);
                }

                if (all)
                {
                    return gen;
                }
            }

            return -1;
        }
        finally
        {
            p.EndReadSync();
        }
    }

    // A one-page database: the header page, written through the pager.
    private static void Touch(PagerClass p)
    {
        p.BeginReadSync();
        p.BeginWriteSync();
        PageFrame p1 = p.GetSync(1);
        p.MakeWritableSync(p1);
        Fill(p1, 0);
        p.Release(p1);
        p.CommitSync();
        p.EndReadSync();
    }

    private static OneLiteException? OpenError(IVfs vfs, PagerOptions o)
    {
        try
        {
            using PagerClass p = PagerClass.Open(vfs, Db, o);
            p.BeginReadSync();
            p.EndReadSync();
            return null;
        }
        catch (OneLiteException e)
        {
            return e;
        }
    }

    [Theory]
    [InlineData(JournalMode.Delete)]
    [InlineData(JournalMode.Truncate)]
    [InlineData(JournalMode.Persist)]
    [InlineData(JournalMode.Wal)]
    public void RoundTripStoresOnlyEncodedPages(JournalMode jm)
    {
        var vfs = new MemoryVfs();
        Create(vfs, "secret", 12, jm);
        using (PagerClass p = PagerClass.Open(vfs, Db, Opts("secret", jm)))
        {
            p.BeginReadSync();
            p.BeginWriteSync();
            Rewrite(p, 2, 12, 1);
            p.CommitSync();
            p.EndReadSync();
        }

        Assert.Equal(1, ReadGeneration(vfs, "secret", 12));
        byte[] bytes = Kit(vfs, Db);
        if (jm == JournalMode.Wal)
        {
            bytes = [.. bytes, .. Kit(vfs, Db + "-wal")];
        }

        // None of the plaintext pattern (or the "SQLite format 3" magic) appears in the stored file or the log.
        Assert.False(bytes.AsSpan().IndexOf("SQLite format 3"u8) >= 0);
        byte[] pattern = Enumerable.Range(0, 64).Select(i => Expect(5, 1, i)).ToArray();
        Assert.False(bytes.AsSpan().IndexOf(pattern) >= 0);
    }

    private static byte[] Kit(MemoryVfs vfs, string path)
    {
        if (!vfs.Access(path, AccessMode.Exists))
        {
            return [];
        }

        using IVfsFile f = vfs.Open(path, VfsOpenOptions.ReadOnly);
        var b = new byte[f.FileSize()];
        f.Read(0, b);
        return b;
    }

    [Fact]
    public void WrongKeyAndUnencryptedFilesAreNotADatabase()
    {
        var vfs = new MemoryVfs();
        Create(vfs, "right", 5);
        OneLiteException? e = OpenError(vfs, Opts("wrong"));
        Assert.NotNull(e);
        Assert.Equal(ResultCode.NotADb, e!.Code);
        Assert.Equal("file is not a database", e.Message);

        // No key at all on an encrypted file: the header is not SQLite's.
        Assert.Equal(ResultCode.NotADb, OpenError(vfs, new PagerOptions { PageSize = Ps })!.Code);

        // A plain database opened with a key.
        var plain = new MemoryVfs();
        using (PagerClass p = PagerClass.Open(plain, Db, new PagerOptions { PageSize = Ps }))
        {
            Touch(p);
        }

        Assert.Equal(ResultCode.NotADb, OpenError(plain, Opts("right"))!.Code);
    }

    [Fact]
    public void ADamagedLaterPageIsCorruptAndNamesThePage()
    {
        var vfs = new MemoryVfs();
        Create(vfs, "k", 6);
        using (IVfsFile f = vfs.Open(Db, VfsOpenOptions.ReadWrite))
        {
            // Flip the byte rather than store a fixed one: the page is random ciphertext, so a fixed value equals it once in 256 runs.
            byte[] b = new byte[1];
            f.Read(4L * Ps + 100, b);
            b[0] ^= 0xFF;
            f.Write(4L * Ps + 100, b);
        }

        using PagerClass p = PagerClass.Open(vfs, Db, Opts("k"));
        p.BeginReadSync();
        OneLiteException e = Assert.Throws<OneLiteException>(() => p.GetSync(5));
        Assert.Equal(ResultCode.Corrupt, e.Code);
        Assert.Contains("page 5", e.Detail);
        p.Release(p.GetSync(4));
    }

    [Fact]
    public void RawKeyAndSaltAndV3SettingsOpenTheirFiles()
    {
        byte[] raw = new byte[32];
        for (int i = 0; i < 32; i++)
        {
            raw[i] = (byte)(i * 7 + 1);
        }

        var vfs = new MemoryVfs();
        using (PagerClass p = PagerClass.Open(vfs, Db, new PagerOptions { Key = CipherKey.FromRawKey(raw), Cipher = CipherSettings.V3 }))
        {
            Touch(p);
            Assert.Equal(CipherSettings.V3.PageSize, p.PageSize);
        }

        using PagerClass q = PagerClass.Open(vfs, Db, new PagerOptions { Key = CipherKey.Parse("x'" + Convert.ToHexString(raw) + "'"), Cipher = CipherSettings.V3 });
        q.BeginReadSync();
        Assert.Equal(1u, q.PageCount);
        q.EndReadSync();
    }

    [Fact]
    public void KeyProviderIsCalledOnceAndPlaintextHeaderNeedsTheSalt()
    {
        CipherSettings s = Fast.WithPlaintextHeaderSize(32);
        var vfs = new MemoryVfs();
        byte[] salt;
        int calls = 0;
        using (PagerClass p = PagerClass.Open(vfs, Db, new PagerOptions { KeyProvider = () => { calls++; return CipherKey.FromPassphrase("pw"); }, Cipher = s }))
        {
            Touch(p);
            p.BeginReadSync();
            p.EndReadSync();
            salt = p.CipherSalt.ToArray();
        }

        Assert.Equal(1, calls);
        Assert.Equal("SQLite format 3\0"u8.ToArray(), Kit(vfs, Db).AsSpan(0, 16).ToArray());
        PagerOptions good = new() { Key = CipherKey.FromPassphrase("pw"), Cipher = s, CipherSalt = salt };
        Assert.Null(OpenError(vfs, good));
        PagerOptions noSalt = new() { Key = CipherKey.FromPassphrase("pw"), Cipher = s };
        Assert.Equal(ResultCode.NotADb, OpenError(vfs, noSalt)!.Code);
    }

    [Fact]
    public void CachedReadsOfAnEncryptedDatabaseAllocateNothing()
    {
        var vfs = new MemoryVfs();
        Create(vfs, "k", 8);
        using PagerClass p = PagerClass.Open(vfs, Db, Opts("k"));
        p.BeginReadSync();
        p.Release(p.GetSync(3));
        long best = long.MaxValue;
        for (int rep = 0; rep < 3; rep++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++)
            {
                if (p.Get(3, GetOptions.None, out PageFrame? f) != PagerStatus.Ok)
                {
                    Assert.Fail("expected a cache hit");
                }

                p.Release(f!);
            }

            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Equal(0, best);
    }

    // ---- crash sweeps ----------------------------------------------------------------------------------------------------------------

    private static IEnumerable<CrashPolicy> Policies()
    {
        yield return CrashPolicy.DropUnsynced;
        yield return CrashPolicy.KeepAll;
        for (ulong seed = 1; seed <= 2; seed++)
        {
            yield return new CrashPolicy { Seed = seed, Reorder = true };
        }

        yield return new CrashPolicy { Seed = 9, TearSectors = true, TornFill = TornFill.Random };
    }

    [Theory]
    [InlineData(JournalMode.Delete, 100)]
    [InlineData(JournalMode.Delete, 6)]
    [InlineData(JournalMode.Truncate, 100)]
    [InlineData(JournalMode.Persist, 100)]
    [InlineData(JournalMode.Wal, 100)]
    [InlineData(JournalMode.Wal, 6)]
    public void CrashAtEveryPointLeavesOneWholeGeneration(JournalMode jm, int cache)
    {
        const int pages = 30;
        var inner = new MemoryVfs();
        Create(inner, "k", pages, jm);
        var fault = new FaultVfs(inner, new FaultOptions { SectorSize = 512 });
        long ack;
        using (PagerClass p = PagerClass.Open(fault, Db, Opts("k", jm, cache: cache)))
        {
            p.BeginReadSync();
            p.BeginWriteSync();
            Rewrite(p, 2, pages, 1);
            p.CommitSync();
            ack = fault.OpCount;
            p.EndReadSync();
        }

        long points = fault.OpCount;
        int old = 0, fresh = 0;
        for (long k = 0; k <= points; k++)
        {
            foreach (CrashPolicy policy in Policies())
            {
                MemoryVfs m = fault.CrashImage(k, policy).ToMemoryVfs();
                int gen = ReadGeneration(m, "k", pages);
                Assert.True(gen is 0 or 1, $"{jm}: crash after {k} of {points} ops, {policy.Mode}/seed {policy.Seed}: generation {gen}");
                if (k >= ack && policy.Mode == CrashMode.DropUnsynced)
                {
                    Assert.Equal(1, gen);
                }

                old += gen == 0 ? 1 : 0;
                fresh += gen == 1 ? 1 : 0;
            }
        }

        _out.WriteLine($"{jm} cache={cache}: {points + 1} crash points, old {old}, new {fresh}");
        Assert.True(old > 0 && fresh > 0);
    }

    /// <summary>
    /// A rekey is one transaction. After a crash anywhere in it, the file opens under exactly one of the two keys and holds the full content.
    /// </summary>
    [Theory]
    [InlineData(JournalMode.Delete, 100)]
    [InlineData(JournalMode.Delete, 6)]
    [InlineData(JournalMode.Truncate, 8)]
    [InlineData(JournalMode.Persist, 100)]
    [InlineData(JournalMode.Wal, 100)]
    [InlineData(JournalMode.Wal, 6)]
    public void CrashDuringRekeyOpensUnderExactlyOneKey(JournalMode jm, int cache)
    {
        const int pages = 40;
        var inner = new MemoryVfs();
        Create(inner, "old", pages, jm);
        var fault = new FaultVfs(inner, new FaultOptions { SectorSize = 512 });
        long ack;
        using (PagerClass p = PagerClass.Open(fault, Db, Opts("old", jm, cache: cache)))
        {
            p.RekeySync(CipherKey.FromPassphrase("new"));
            ack = fault.OpCount;
        }

        long points = fault.OpCount;
        int oldKey = 0, newKey = 0;
        for (long k = 0; k <= points; k++)
        {
            foreach (CrashPolicy policy in Policies())
            {
                MemoryVfs m = fault.CrashImage(k, policy).ToMemoryVfs();
                OneLiteException? eo = null, en = null;
                int go = -2, gn = -2;
                try
                {
                    go = ReadGeneration(m, "old", pages);
                }
                catch (OneLiteException e)
                {
                    eo = e;
                }

                try
                {
                    gn = ReadGeneration(m, "new", pages);
                }
                catch (OneLiteException e)
                {
                    en = e;
                }

                string where = $"{jm}/{cache}: crash after {k} of {points} ops, {policy.Mode}/seed {policy.Seed}/tear {policy.TearSectors}";
                Assert.True((eo == null) ^ (en == null), where + $": old key {(eo == null ? "opens" : eo.Message)}, new key {(en == null ? "opens" : en.Message)}");
                Assert.True(eo == null ? go == 0 : gn == 0, where + ": content lost");
                if (k >= ack && policy.Mode == CrashMode.DropUnsynced)
                {
                    Assert.Null(en);
                }

                oldKey += eo == null ? 1 : 0;
                newKey += en == null ? 1 : 0;
            }
        }

        _out.WriteLine($"{jm} cache={cache}: {points + 1} crash points, old key {oldKey}, new key {newKey}");
        Assert.True(oldKey > 0 && newKey > 0);
    }

    [Fact]
    public void RollbackOfARekeyKeepsTheOldKey()
    {
        var vfs = new MemoryVfs();
        Create(vfs, "old", 20);
        using (PagerClass p = PagerClass.Open(vfs, Db, Opts("old", cache: 5)))
        {
            p.BeginReadSync();
            p.BeginWriteSync();
            while (p.Rekey(CipherKey.FromPassphrase("new")) == PagerStatus.Pending)
            {
                p.ExecuteSync();
            }

            p.RollbackSync();
            p.EndReadSync();
            p.BeginReadSync();
            p.Release(p.GetSync(7));
            p.EndReadSync();
        }

        Assert.Equal(0, ReadGeneration(vfs, "old", 20));
        Assert.NotNull(OpenError(vfs, Opts("new")));
    }

    // After a rekey in WAL mode and before the next checkpoint, page 1 in the database file is still stored under the old key: a new opener has to read
    // it through the log, as SQLite opens a WAL that exists before it reads page 1.
    [Fact]
    public void WalDatabaseOpensUnderTheNewKeyBeforeTheNextCheckpoint()
    {
        var vfs = new MemoryVfs();
        Create(vfs, "old", 20, JournalMode.Wal);
        using PagerClass p = PagerClass.Open(vfs, Db, Opts("old", JournalMode.Wal));
        p.RekeySync(CipherKey.FromPassphrase("new"));
        Assert.Equal(0, ReadGeneration(vfs, "new", 20));
        Assert.Equal(ResultCode.NotADb, Assert.Throws<OneLiteException>(() => ReadGeneration(vfs, "old", 20)).Code);
        p.CheckpointSync(CheckpointMode.Truncate);
        Assert.Equal(0, ReadGeneration(vfs, "new", 20));
    }

    [Fact]
    public void RekeyChangesSettingsAndLaterWritesUseTheNewKey()
    {
        var vfs = new MemoryVfs();
        Create(vfs, "old", 10);
        CipherSettings s2 = new(Ps, 3, CipherDigest.Sha256, CipherDigest.Sha256);
        using (PagerClass p = PagerClass.Open(vfs, Db, Opts("old")))
        {
            p.RekeySync(CipherKey.FromPassphrase("new"), s2);
            p.BeginReadSync();
            p.BeginWriteSync();
            Rewrite(p, 2, 5, 1);
            p.CommitSync();
            p.EndReadSync();
        }

        Assert.Null(OpenError(vfs, Opts("new", s: s2)));
        Assert.Equal(ResultCode.NotADb, OpenError(vfs, Opts("old"))!.Code);
    }

    /// <summary>ATTACH with a different key: two pagers, two keys, one multi-file commit.</summary>
    [Fact]
    public void TwoDatabasesWithDifferentKeysCommitTogether()
    {
        var vfs = new MemoryVfs();
        using PagerClass a = PagerClass.Open(vfs, "a.db", new PagerOptions { Key = CipherKey.FromPassphrase("ka"), Cipher = Fast });
        using PagerClass b = PagerClass.Open(vfs, "b.db", new PagerOptions { Key = CipherKey.FromPassphrase("kb"), Cipher = Fast });
        foreach (PagerClass p in new[] { a, b })
        {
            p.BeginReadSync();
            p.BeginWriteSync();
            PageFrame p1 = p.GetSync(1);
            p.MakeWritableSync(p1);
            Fill(p1, 0);
            p.Release(p1);
        }

        MultiCommit.CommitSync([a, b]);
        a.EndReadSync();
        b.EndReadSync();
        foreach ((string path, string key) in new[] { ("a.db", "ka"), ("b.db", "kb") })
        {
            using PagerClass q = PagerClass.Open(vfs, path, new PagerOptions { Key = CipherKey.FromPassphrase(key), Cipher = Fast });
            q.BeginReadSync();
            Assert.Equal(1u, q.PageCount);
            q.EndReadSync();
        }

        Assert.Equal(ResultCode.NotADb, Assert.Throws<OneLiteException>(() =>
        {
            using PagerClass q = PagerClass.Open(vfs, "a.db", new PagerOptions { Key = CipherKey.FromPassphrase("kb"), Cipher = Fast });
            q.BeginReadSync();
        }).Code);
    }
}
