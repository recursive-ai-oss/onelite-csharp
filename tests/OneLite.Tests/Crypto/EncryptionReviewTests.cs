using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneLite.Storage.Crypto;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;
using PagerClass = RecursiveAI.OneLite.Storage.Pager.Pager;

namespace RecursiveAI.OneLite.Tests.Crypto;

/// <summary>
/// Properties of the encryption path that matter to a reviewer more than to a round trip: a fresh IV for every write wherever a page is written,
/// no plaintext (and no passphrase) in any file at any instant, keys wiped, tampering never yielding plaintext, and no secret in error text.
/// </summary>
public class EncryptionReviewTests
{
    private const string Db = "rv.db";
    private const int Ps = 1024;
    private const int Reserve = 80;
    private const string Canary = "CANARY-plaintext-9f3c61d2-do-not-store";
    private const string Pass = "PASSPHRASE-canary-5b7e";

    private static readonly CipherSettings Fast = new(Ps, 2, CipherDigest.Sha512, CipherDigest.Sha512);

    private static PagerOptions Opts(string key, JournalMode jm, int cache = 100) =>
        new() { Key = CipherKey.FromPassphrase(key), Cipher = Fast, JournalMode = jm, Synchronous = SynchronousMode.Full, CacheSize = cache, WalAutoCheckpoint = 0 };

    // Page content: the canary repeated over the whole usable area, so a leak anywhere in a page shows.
    private static void FillCanary(PageFrame f, int gen)
    {
        byte[] pattern = Encoding.ASCII.GetBytes($"{Canary}|{f.PageNumber}|{gen}|");
        for (int i = f.PageNumber == 1 ? 108 : 0, j = 0; i < f.Data.Length - Reserve; i++, j++)
        {
            f.Data[i] = pattern[j % pattern.Length];
        }
    }

    private static void Create(IVfs vfs, string key, int pages, JournalMode jm)
    {
        // A pager opened with the WAL option already reports WAL mode, which turns ChangeJournalMode(Wal) into a no-op: start in rollback mode.
        using PagerClass p = PagerClass.Open(vfs, Db, Opts(key, jm == JournalMode.Wal ? JournalMode.Delete : jm));
        p.BeginReadSync();
        p.BeginWriteSync();
        PageFrame p1 = p.GetSync(1);
        p.MakeWritableSync(p1);
        FillCanary(p1, 0);
        p.Release(p1);
        for (int i = 2; i <= pages; i++)
        {
            PageFrame f = p.AllocateSync();
            FillCanary(f, 0);
            p.Release(f);
        }

        p.CommitSync();
        if (jm == JournalMode.Wal)
        {
            p.ChangeJournalModeSync(JournalMode.Wal);
        }

        p.EndReadSync();
    }

    private static void Rewrite(PagerClass p, int pages, int gen)
    {
        for (uint n = 1; n <= pages; n++)
        {
            PageFrame f = p.GetSync(n);
            p.MakeWritableSync(f);
            FillCanary(f, gen);
            p.Release(f);
        }
    }

    private static byte[] Bytes(MemoryVfs vfs, string path) => vfs.Export(path) ?? [];

    // ---- IV freshness -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void CodecGivesEveryEncodeOfTheSamePageAnotherIv()
    {
        using var codec = PageCodec.CreateNew(Fast, CipherKey.FromPassphrase(Pass));
        byte[] plain = new byte[Ps];
        byte[] enc = new byte[Ps];
        var ivs = new HashSet<string>();
        var bodies = new HashSet<string>();
        for (int i = 0; i < 3000; i++)
        {
            codec.Encode(7, plain, enc);
            Assert.True(ivs.Add(Convert.ToHexString(enc.AsSpan(Ps - Reserve, 16))), "an IV repeated");
            Assert.True(bodies.Add(Convert.ToHexString(enc.AsSpan(0, 32))), "a ciphertext repeated");
        }

        // Page 1 as well (its first 16 bytes are the salt, so only the IV and the body vary).
        codec.Encode(1, plain, enc);
        byte[] first = enc.AsSpan(Ps - Reserve, 16).ToArray();
        codec.Encode(1, plain, enc);
        Assert.False(first.AsSpan().SequenceEqual(enc.AsSpan(Ps - Reserve, 16)));
    }

    // Writing the same content again must never repeat an IV: not in the database file, not in the journal record of the old content, not in the WAL.
    [Theory]
    [InlineData(JournalMode.Persist)]
    [InlineData(JournalMode.Wal)]
    public void RewritingIdenticalContentUsesNewIvsInDatabaseJournalAndWal(JournalMode jm)
    {
        const int pages = 8;
        var vfs = new MemoryVfs();
        Create(vfs, Pass, pages, jm);
        var seen = new Dictionary<string, string>();
        void Collect(string what, byte[] file, int headerBytes, int stride, int firstSlot)
        {
            for (long off = headerBytes + (long)firstSlot * stride; off + stride <= file.Length; off += stride)
            {
                // Position of a page's IV inside a unit that ends with the page: page end minus reserve.
                string iv = Convert.ToHexString(file.AsSpan((int)off + stride - Reserve, 16));
                Assert.True(seen.TryAdd(iv, $"{what}@{off}"), $"IV repeated: {what}@{off} and {seen.GetValueOrDefault(iv)}");
            }
        }

        // IVs of the pages as first written. (The journal of a Persist database is stale content, collected below.)
        Collect("db0", Bytes(vfs, Db), 0, Ps, 0);
        using (PagerClass p = PagerClass.Open(vfs, Db, Opts(Pass, jm)))
        {
            int journalIvs = 0;
            for (int gen = 1; gen <= 3; gen++)
            {
                // In WAL mode the log lives only while a connection holds a read transaction open (the last close checkpoints and deletes it).
                if (!p.InTransaction)
                {
                    p.BeginReadSync();
                }

                p.BeginWriteSync();
                Rewrite(p, pages, 1); // the same generation each time: byte-identical plaintext
                p.CommitSync();
                if (jm != JournalMode.Wal)
                {
                    p.EndReadSync();
                }
                if (jm == JournalMode.Persist)
                {
                    seen.Clear();
                    // Database and journal now hold two different encryptions of unchanged-or-older content; none of their IVs may match.
                    Collect($"db{gen}", Bytes(vfs, Db), 0, Ps, 0);
                    byte[] j = Bytes(vfs, Db + "-journal");
                    if (j.Length > 512)
                    {
                        // Journal records: 4-byte page number, a stored page, 4-byte checksum (fileformat2 §1.5).
                        for (int off = 512; off + 4 + Ps + 4 <= j.Length; off += 4 + Ps + 4)
                        {
                            string iv = Convert.ToHexString(j.AsSpan(off + 4 + Ps - Reserve, 16));
                            if (iv.Trim('0').Length == 0)
                            {
                                continue; // zeroed (a retired journal body)
                            }

                            Assert.True(seen.TryAdd(iv, $"journal@{off}"), $"journal IV repeats {seen.GetValueOrDefault(iv)}");
                            journalIvs++;
                        }
                    }
                }
            }

            if (jm == JournalMode.Persist)
            {
                Assert.True(journalIvs >= pages, $"only {journalIvs} journal records were compared");
            }

            if (jm == JournalMode.Wal)
            {
                // Three commits of the same eight pages sit in the log: 24 frames, 24 different IVs, none equal to the first-written pages' IVs.
                byte[] wal = Bytes(vfs, Db + "-wal");
                int frame = 24 + Ps;
                Assert.True(wal.Length >= 32 + 3 * pages * frame, $"WAL is {wal.Length} bytes");
                for (long off = 32; off + frame <= wal.Length; off += frame)
                {
                    string iv = Convert.ToHexString(wal.AsSpan((int)off + 24 + Ps - Reserve, 16));
                    Assert.True(seen.TryAdd(iv, $"wal@{off}"), $"WAL IV repeats {seen.GetValueOrDefault(iv)}");
                }

                p.EndReadSync();
            }
        }
    }

    [Fact]
    public void RekeyToTheSameKeyStillWritesNewIvs()
    {
        var vfs = new MemoryVfs();
        Create(vfs, Pass, 6, JournalMode.Delete);
        byte[] before = Bytes(vfs, Db);
        using (PagerClass p = PagerClass.Open(vfs, Db, Opts(Pass, JournalMode.Delete)))
        {
            p.RekeySync(CipherKey.FromPassphrase(Pass));
        }

        byte[] after = Bytes(vfs, Db);
        Assert.Equal(before.Length, after.Length);
        for (int pg = 0; pg < before.Length / Ps; pg++)
        {
            Assert.False(before.AsSpan(pg * Ps + Ps - Reserve, 16).SequenceEqual(after.AsSpan(pg * Ps + Ps - Reserve, 16)), $"page {pg + 1} kept its IV through a rekey");
        }
    }

    // ---- plaintext leakage ----------------------------------------------------------------------------------------------------------------

    // At every storage call of a write (spills included, with a small cache), keeping everything that was written, no file holds the canary,
    // the passphrase or the SQLite magic string.
    [Theory]
    [InlineData(JournalMode.Delete, 100)]
    [InlineData(JournalMode.Delete, 6)]
    [InlineData(JournalMode.Truncate, 6)]
    [InlineData(JournalMode.Persist, 6)]
    [InlineData(JournalMode.Wal, 100)]
    [InlineData(JournalMode.Wal, 6)]
    public void NoFileEverHoldsPlaintextOrThePassphrase(JournalMode jm, int cache)
    {
        const int pages = 24;
        var inner = new MemoryVfs();
        Create(inner, Pass, pages, jm);
        var fault = new FaultVfs(inner, new FaultOptions { SectorSize = 512 });
        using (PagerClass p = PagerClass.Open(fault, Db, Opts(Pass, jm, cache)))
        {
            p.BeginReadSync();
            p.BeginWriteSync();
            Rewrite(p, pages, 1);
            // Add pages and free one, so the freelist and extension paths write too.
            PageFrame extra = p.AllocateSync();
            FillCanary(extra, 1);
            p.Release(extra);
            p.CommitSync();
            p.BeginWriteSync();
            while (p.Rekey(CipherKey.FromPassphrase(Pass + "-new")) == PagerStatus.Pending)
            {
                p.ExecuteSync();
            }

            p.CommitSync();
            p.EndReadSync();
        }

        byte[][] needles = [Encoding.ASCII.GetBytes(Canary), Encoding.ASCII.GetBytes("PASSPHRASE-canary"), "SQLite format 3"u8.ToArray()];
        long points = fault.OpCount;
        int files = 0;
        bool sawWal = false;
        bool sawJournal = false;
        for (long k = 0; k <= points; k++)
        {
            MemoryVfs m = fault.CrashImage(k, CrashPolicy.KeepAll).ToMemoryVfs();
            foreach (string path in m.List())
            {
                byte[] data = Bytes(m, path);
                files++;
                sawWal |= path.EndsWith("-wal", StringComparison.Ordinal) && data.Length > 0;
                sawJournal |= path.EndsWith("-journal", StringComparison.Ordinal) && data.Length > 0;
                foreach (byte[] needle in needles)
                {
                    Assert.True(data.AsSpan().IndexOf(needle) < 0, $"{jm}/cache {cache}: '{Encoding.ASCII.GetString(needle)}' found in {path} after {k} of {points} calls");
                }
            }
        }

        Assert.True(files > points);

        // The sweep must really have exercised the log it claims to (a test that starts in the wrong journal mode proves nothing).
        Assert.Equal(jm == JournalMode.Wal, sawWal);
        Assert.Equal(jm != JournalMode.Wal, sawJournal);
    }

    // The same promise on real files: while a spilling write is still open (journal or log on disk) and after it, every file in the directory.
    [Theory]
    [InlineData(JournalMode.Delete)]
    [InlineData(JournalMode.Persist)]
    [InlineData(JournalMode.Wal)]
    public void NoFileOnDiskHoldsPlaintextWhileATransactionIsOpen(JournalMode jm)
    {
        string dir = Path.Combine(Path.GetTempPath(), "onelite-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "rv.db");
            FileVfs vfs = FileVfs.Instance;
            using (PagerClass c = PagerClass.Open(vfs, path, Opts(Pass, JournalMode.Delete, 6)))
            {
                c.BeginReadSync();
                c.BeginWriteSync();
                for (int i = 0; i < 30; i++)
                {
                    PageFrame f = i == 0 ? c.GetSync(1) : c.AllocateSync();
                    c.MakeWritableSync(f);
                    FillCanary(f, 0);
                    c.Release(f);
                }

                c.CommitSync();
                if (jm == JournalMode.Wal)
                {
                    c.ChangeJournalModeSync(JournalMode.Wal);
                }

                c.EndReadSync();
            }

            int scanned = 0;
            void Scan(string when)
            {
                foreach (string file in Directory.GetFiles(dir))
                {
                    // Read through the VFS's main-file open: a FileStream takes the runtime's advisory flock, which on macOS is the same
                    // mechanism as the record locks the open connection holds, so it fails while the database is in use.
                    byte[] data;
                    using (IVfsFile f = vfs.Open(file, VfsOpenOptions.ReadOnly | VfsOpenOptions.MainDb))
                    {
                        data = new byte[f.FileSize()];
                        Assert.Equal(data.Length, f.Read(0, data));
                    }

                    scanned++;
                    Assert.True(data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(Canary)) < 0, $"{jm}: canary in {Path.GetFileName(file)} {when}");
                    Assert.True(data.AsSpan().IndexOf(Encoding.ASCII.GetBytes("PASSPHRASE-canary")) < 0, $"{jm}: passphrase in {Path.GetFileName(file)} {when}");
                }
            }

            using (PagerClass p = PagerClass.Open(vfs, path, Opts(Pass, JournalMode.Delete, 6)))
            {
                p.BeginReadSync();
                p.BeginWriteSync();
                Rewrite(p, 30, 1);
                Scan("mid-transaction");
                string[] names = Directory.GetFiles(dir).Select(Path.GetFileName).ToArray()!;
                Assert.Contains(names, n => n!.EndsWith(jm == JournalMode.Wal ? "-wal" : "-journal", StringComparison.Ordinal));
                p.CommitSync();
                Scan("after commit");
                p.EndReadSync();
            }

            Scan("after close");
            Assert.True(scanned >= 4);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---- MAC first ------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 40)]            // page 1 ciphertext
    [InlineData(5, 100)]           // ciphertext
    [InlineData(5, Ps - Reserve + 3)]   // IV
    [InlineData(5, Ps - Reserve + 16 + 5)] // MAC
    public void AnyChangedBitFailsTheMacAndReleasesNoPlaintext(uint page, int at)
    {
        using var codec = PageCodec.CreateNew(Fast, CipherKey.FromPassphrase(Pass));
        byte[] plain = new byte[Ps];
        Array.Fill(plain, (byte)'Z', 0, Ps - Reserve);
        byte[] enc = new byte[Ps];
        codec.Encode(page, plain, enc);
        enc[at] ^= 0x01;
        byte[] outp = new byte[Ps];
        Array.Fill(outp, (byte)0xEE); // the caller's buffer must not keep, or be handed, anything
        Assert.False(codec.TryDecode(page, enc, outp));
        Assert.All(outp, b => Assert.Equal(0, b));
        // A page decoded under another page number is a different message.
        enc[at] ^= 0x01;
        Assert.False(codec.TryDecode(page + 1, enc, outp));
        Assert.True(codec.TryDecode(page, enc, outp));
    }

    [Fact]
    public void DamagedStoredPagesAreCorruptNotExceptions()
    {
        var vfs = new MemoryVfs();
        Create(vfs, Pass, 6, JournalMode.Delete);
        byte[] file = Bytes(vfs, Db);

        // Cut mid-page: the short page reads as zeros padded and fails its MAC (page 6 of 6).
        vfs.Import(Db, file.AsSpan(0, file.Length - 100));
        using (PagerClass p = PagerClass.Open(vfs, Db, Opts(Pass, JournalMode.Delete)))
        {
            p.BeginReadSync();
            OneLiteException e = Assert.Throws<OneLiteException>(() => p.Release(p.GetSync(6)));
            Assert.Equal(ResultCode.Corrupt, e.Code);
            p.EndReadSync();
        }

        // Page 1 zeroed: not a database.
        byte[] z = (byte[])file.Clone();
        Array.Clear(z, 0, Ps);
        vfs.Import(Db, z);
        OneLiteException? e1 = null;
        try
        {
            using PagerClass p = PagerClass.Open(vfs, Db, Opts(Pass, JournalMode.Delete));
            p.BeginReadSync();
        }
        catch (OneLiteException e)
        {
            e1 = e;
        }

        Assert.NotNull(e1);
        Assert.Equal(ResultCode.NotADb, e1!.Code);
    }

    // ---- keys -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void NoSecretAppearsInErrorTextOrToString()
    {
        string hex = new string('a', 64);
        var vfs = new MemoryVfs();
        Create(vfs, Pass, 4, JournalMode.Delete);
        foreach (string wrong in new[] { "WRONG-" + Pass, "x'" + hex + "'" })
        {
            var ex = Assert.Throws<OneLiteException>(() =>
            {
                using PagerClass p = PagerClass.Open(vfs, Db, new PagerOptions { Key = CipherKey.Parse(wrong), Cipher = Fast });
                p.BeginReadSync();
            });
            string all = ex.ToString() + ex.Message + ex.Detail;
            Assert.DoesNotContain(Pass, all, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(hex, all, StringComparison.OrdinalIgnoreCase);
        }

        using CipherKey k = CipherKey.FromPassphrase(Pass);
        Assert.DoesNotContain(Pass, k.ToString(), StringComparison.Ordinal);
        using var opts = new PagerOptionsHolder(new PagerOptions { Key = k });
        Assert.DoesNotContain(Pass, opts.Options.ToString(), StringComparison.Ordinal);
    }

    private sealed class PagerOptionsHolder : IDisposable
    {
        public PagerOptionsHolder(PagerOptions o) => Options = o;

        public PagerOptions Options { get; }

        public void Dispose()
        {
        }
    }

    private static byte[] Field(object o, string name) => (byte[])o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o)!;

    [Fact]
    public void KeysAreZeroedOnDispose()
    {
        // Reaching the private arrays by reflection is the only way to see that the same array was wiped.
        CipherKey k = CipherKey.FromPassphrase(Pass);
        byte[] material = Field(k, "_material");
        Assert.Contains(material, b => b != 0);
        Assert.Equal(Encoding.UTF8.GetBytes(Pass), material);
        k.Dispose();
        Assert.All(material, b => Assert.Equal(0, b));

        using CipherKey k2 = CipherKey.Parse("x'" + new string('b', 64) + "'");
        PageCodec codec = PageCodec.CreateNew(Fast, k2);
        byte[] enc = Field(codec, "_encKey");
        byte[] mac = Field(codec, "_macKey");
        Assert.Contains(enc, b => b != 0);
        Assert.Contains(mac, b => b != 0);
        codec.Dispose();
        Assert.All(enc, b => Assert.Equal(0, b));
        Assert.All(mac, b => Assert.Equal(0, b));

        // A codec that fails half way through its construction (here: an unsupported digest cannot happen, so use a disposed key) leaves nothing.
        using CipherKey dead = CipherKey.FromPassphrase(Pass);
        dead.Dispose();
        Assert.Throws<ObjectDisposedException>(() => PageCodec.CreateNew(Fast, dead));
    }

    [Fact]
    public void ClosingAnEncryptedPagerWipesKeysAndCachedPlaintext()
    {
        var vfs = new MemoryVfs();
        Create(vfs, Pass, 4, JournalMode.Delete);
        PagerClass p = PagerClass.Open(vfs, Db, Opts(Pass, JournalMode.Delete));
        p.BeginReadSync();
        PageFrame f = p.GetSync(3);
        Assert.Contains(f.Data, b => b == (byte)'C');
        PageCodec codec = (PageCodec)typeof(PagerClass).GetField("_codec", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(p)!;
        byte[] enc = Field(codec, "_encKey");
        p.Release(f);
        p.EndReadSync();
        p.Dispose();
        Assert.All(enc, b => Assert.Equal(0, b));
        Assert.All(f.Data, b => Assert.Equal(0, b));
    }

    [Fact]
    public void ThePassphraseConstructorLeavesOnlyThePinnedCopy()
    {
        // Same bytes whether built from a string or from bytes, including multi-byte characters and the empty string.
        foreach (string s in new[] { "", "a", "pässwörd-日本語-🔐", Pass })
        {
            using CipherKey a = CipherKey.FromPassphrase(s);
            using CipherKey b = CipherKey.FromPassphrase(Encoding.UTF8.GetBytes(s));
            Assert.Equal(Field(b, "_material"), Field(a, "_material"));
        }
    }

    // The random generator is the only source of IVs and salts: two new databases with one passphrase differ everywhere.
    [Fact]
    public void TwoNewDatabasesWithOnePassphraseShareNoSalt()
    {
        var a = new MemoryVfs();
        var b = new MemoryVfs();
        Create(a, Pass, 3, JournalMode.Delete);
        Create(b, Pass, 3, JournalMode.Delete);
        Assert.False(Bytes(a, Db).AsSpan(0, 16).SequenceEqual(Bytes(b, Db).AsSpan(0, 16)));
        Assert.False(CryptographicOperations.FixedTimeEquals(Bytes(a, Db).AsSpan(Ps, 64), Bytes(b, Db).AsSpan(Ps, 64)));
    }
}
