using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneLite.Storage.Crypto;

namespace RecursiveAI.OneLite.Tests.Crypto;

/// <summary>
/// The page codec against files a SQLCipher shell wrote (testdata/cipher, see its README for how they were made), against the primitives' published
/// vectors, and for round trips and hostile pages. Zetetic's design and API pages give the parameters but no byte-level vectors, so the
/// known-answer files are oracle output; the layout checks below recompute the MAC independently with the BCL.
/// </summary>
public class PageCodecTests
{
    private const string RawSaltKey = "x'98483C6EB40B6C31A448C22A66DED3B5E5E8D5119CAC8327B655C8B5C483648101010101010101010101010101010101'";
    private static readonly byte[] ExternalSalt = Convert.FromHexString("01010101010101010101010101010101");

    public static TheoryData<string, int, string, string, string, int> Fixtures => new()
    {
        // file, page size, passphrase or key text, kdf digest, hmac digest, plaintext header size
        { "v4-default", 4096, "test123", "512", "512", 0 },
        { "v4-p1024", 1024, "test123", "512", "512", 0 },
        { "v3", 1024, "test123", "1", "1", 0 },
        { "v4-sha256-sha1", 1024, "test123", "256", "1", 0 },
        { "raw", 1024, "x'2DD29CA851E7B56E4697B0E1F08507293D761A05CE4D1B628663F411A8086D99'", "512", "512", 0 },
        { "raw-salt", 1024, RawSaltKey, "512", "512", 0 },
        { "plaintext-header", 1024, "test123", "512", "512", 32 },
    };

    private static CipherSettings SettingsFor(string file, int pageSize, string kdf, string hmac, int header)
    {
        int iter = file switch { "v4-default" => 256_000, "v3" => 64_000, "raw" or "raw-salt" => 256_000, _ => 4000 };
        return new CipherSettings(pageSize, iter, Digest(kdf), Digest(hmac), header);
    }

    private static CipherDigest Digest(string s) => s switch { "1" => CipherDigest.Sha1, "256" => CipherDigest.Sha256, _ => CipherDigest.Sha512 };

    private static byte[] ReadFixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "cipher", name + ".db"));

    [Fact]
    public void PrimitiveVectors()
    {
        // NIST SP 800-38A F.2.5, CBC-AES256.Encrypt, block 1.
        using Aes aes = Aes.Create();
        aes.SetKey(Convert.FromHexString("603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4"));
        byte[] ct = aes.EncryptCbc(Convert.FromHexString("6bc1bee22e409f96e93d7e117393172a"), Convert.FromHexString("000102030405060708090a0b0c0d0e0f"), PaddingMode.None);
        Assert.Equal("f58c4c04d6e5f1ba779eabfb5f7bfbd6", Convert.ToHexString(ct).ToLowerInvariant());

        // RFC 6070 test 1 (PBKDF2-HMAC-SHA1, c = 1) and test 2 (c = 2).
        Assert.Equal("0c60c80f961f0e71f3a9b524af6012062fe037a6",
            Convert.ToHexString(Rfc2898DeriveBytes.Pbkdf2("password"u8, "salt"u8, 1, HashAlgorithmName.SHA1, 20)).ToLowerInvariant());
        Assert.Equal("ea6c014dc72d6f8ccd1ed92ace1d41f0d8de8957",
            Convert.ToHexString(Rfc2898DeriveBytes.Pbkdf2("password"u8, "salt"u8, 2, HashAlgorithmName.SHA1, 20)).ToLowerInvariant());
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void DecodesFilesWrittenByTheReferenceShell(string file, int pageSize, string keyText, string kdf, string hmac, int header)
    {
        byte[] data = ReadFixture(file);
        CipherSettings s = SettingsFor(file, pageSize, kdf, hmac, header);
        Assert.Equal(0, data.Length % pageSize);
        using CipherKey key = CipherKey.Parse(keyText);
        using PageCodec codec = PageCodec.OpenExisting(s, key, data.AsSpan(0, pageSize), header != 0 ? ExternalSalt : default);
        byte[] page = new byte[pageSize];
        var all = new StringBuilder();
        for (int pg = 1; pg <= data.Length / pageSize; pg++)
        {
            codec.Decode((uint)pg, data.AsSpan((pg - 1) * pageSize, pageSize), page);
            all.Append(Encoding.Latin1.GetString(page));
            if (pg == 1)
            {
                Assert.True(page.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8));
                Assert.Equal(80, page[20]); // fileformat2 §1.3.8: the header says how many bytes each page reserves; the reference writes 80 whatever the digest
                Assert.Equal(pageSize == 65536 ? 1 : pageSize, (page[16] << 8) | page[17]);
            }

            Assert.All(page.AsSpan(pageSize - s.ReserveSize).ToArray(), b => Assert.Equal(0, b));
        }

        Assert.Contains("row-one", all.ToString());
        Assert.Contains("row-two", all.ToString());
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void StoredLayoutMatchesAnIndependentMacComputation(string file, int pageSize, string keyText, string kdf, string hmac, int header)
    {
        byte[] data = ReadFixture(file);
        CipherSettings s = SettingsFor(file, pageSize, kdf, hmac, header);
        using CipherKey key = CipherKey.Parse(keyText);
        byte[] salt = key.HasSalt ? key.Salt.ToArray() : header != 0 ? ExternalSalt : data[..16];
        byte[] enc = key.IsRawKey ? RawKeyBytes(keyText) : Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(keyText), salt, s.KdfIterations, Name(s.KdfDigest), 32);
        // Zetetic design page: MAC key = PBKDF2(enc key, salt XOR 0x3a, 2 iterations); the MAC covers ciphertext and IV, then the page number (LE).
        byte[] macSalt = salt.Select(b => (byte)(b ^ 0x3a)).ToArray();
        byte[] macKey = Rfc2898DeriveBytes.Pbkdf2(enc, macSalt, 2, Name(s.KdfDigest), 32);
        int dataEnd = pageSize - s.ReserveSize;
        for (int pg = 1; pg <= data.Length / pageSize; pg++)
        {
            ReadOnlySpan<byte> page = data.AsSpan((pg - 1) * pageSize, pageSize);
            int start = pg != 1 ? 0 : header != 0 ? header : 16;
            byte[] input = page[start..(dataEnd + 16)].ToArray().Concat(BitConverter.GetBytes(pg)).ToArray();
            using var h = IncrementalHash.CreateHMAC(Name(s.HmacDigest), macKey);
            h.AppendData(input);
            byte[] mac = h.GetHashAndReset();
            Assert.True(mac.AsSpan().SequenceEqual(page.Slice(dataEnd + 16, mac.Length)), $"{file} page {pg}");
        }
    }

    private static byte[] RawKeyBytes(string text) => Convert.FromHexString(text.AsSpan(2, 64));

    private static HashAlgorithmName Name(CipherDigest d) => d switch { CipherDigest.Sha1 => HashAlgorithmName.SHA1, CipherDigest.Sha256 => HashAlgorithmName.SHA256, _ => HashAlgorithmName.SHA512 };

    [Fact]
    public void WrongKeyOnPageOneIsNotADatabase()
    {
        byte[] data = ReadFixture("v4-p1024");
        CipherSettings s = new(1024, 4000, CipherDigest.Sha512, CipherDigest.Sha512);
        using CipherKey key = CipherKey.FromPassphrase("not the key");
        using PageCodec codec = PageCodec.OpenExisting(s, key, data.AsSpan(0, 1024));
        OneLiteException e = Assert.Throws<OneLiteException>(() => codec.Decode(1, data.AsSpan(0, 1024), new byte[1024]));
        Assert.Equal(ResultCode.NotADb, e.ExtendedCode);
        Assert.Equal("file is not a database", e.Message);
    }

    [Fact]
    public void WrongKeyWithAPlaintextHeaderIsCorruption()
    {
        // Observed black-box: the header still parses, so SQLCipher 4.20 reports corruption (4.5.6 says "SQL logic error").
        byte[] data = ReadFixture("plaintext-header");
        CipherSettings s = new(1024, 4000, CipherDigest.Sha512, CipherDigest.Sha512, 32);
        using CipherKey key = CipherKey.FromPassphrase("nope");
        using PageCodec codec = PageCodec.OpenExisting(s, key, data.AsSpan(0, 1024), ExternalSalt);
        OneLiteException e = Assert.Throws<OneLiteException>(() => codec.Decode(1, data.AsSpan(0, 1024), new byte[1024]));
        Assert.Equal(ResultCode.Corrupt, e.ExtendedCode);
        Assert.Equal("database disk image is malformed", e.Message);
    }

    [Fact]
    public void PlaintextHeaderNeedsASalt()
    {
        byte[] data = ReadFixture("plaintext-header");
        CipherSettings s = new(1024, 4000, CipherDigest.Sha512, CipherDigest.Sha512, 32);
        using CipherKey key = CipherKey.FromPassphrase("test123");
        Assert.Equal(ResultCode.NotADb, Assert.Throws<OneLiteException>(() => PageCodec.OpenExisting(s, key, data.AsSpan(0, 1024))).ExtendedCode);
    }

    [Fact]
    public void ATamperedLaterPageIsCorruptNamingThePage()
    {
        byte[] data = ReadFixture("v4-p1024");
        CipherSettings s = new(1024, 4000, CipherDigest.Sha512, CipherDigest.Sha512);
        using CipherKey key = CipherKey.FromPassphrase("test123");
        using PageCodec codec = PageCodec.OpenExisting(s, key, data.AsSpan(0, 1024));
        data[1024 + 100] ^= 1;
        byte[] page = new byte[1024];
        OneLiteException e = Assert.Throws<OneLiteException>(() => codec.Decode(2, data.AsSpan(1024, 1024), page));
        Assert.Equal(ResultCode.Corrupt, e.Code);
        Assert.Contains("page 2", e.Detail);
        Assert.All(page, b => Assert.Equal(0, b));
    }

    [Fact]
    public void ThePageNumberIsBoundIntoTheMac()
    {
        byte[] data = ReadFixture("v4-p1024");
        CipherSettings s = new(1024, 4000, CipherDigest.Sha512, CipherDigest.Sha512);
        using CipherKey key = CipherKey.FromPassphrase("test123");
        using PageCodec codec = PageCodec.OpenExisting(s, key, data.AsSpan(0, 1024));
        Assert.True(codec.TryDecode(2, data.AsSpan(1024, 1024), new byte[1024]));
        Assert.False(codec.TryDecode(3, data.AsSpan(1024, 1024), new byte[1024]));
    }

    [Theory]
    [InlineData(512, 4, 0)]
    [InlineData(1024, 1, 0)]
    [InlineData(4096, 3, 0)]
    [InlineData(65536, 3, 0)]
    [InlineData(1024, 4, 32)]
    [InlineData(512, 4, 64)]
    public void EncodeThenDecodeRoundTripsForEveryDigestCombination(int pageSize, int digestSet, int header)
    {
        (CipherDigest k, CipherDigest h)[] sets = [(CipherDigest.Sha512, CipherDigest.Sha512), (CipherDigest.Sha1, CipherDigest.Sha1), (CipherDigest.Sha256, CipherDigest.Sha256), (CipherDigest.Sha256, CipherDigest.Sha1)];
        var (kd, hd) = sets[digestSet % 4];
        var s = new CipherSettings(pageSize, 3, kd, hd, header);
        using CipherKey key = CipherKey.FromPassphrase("round trip");
        using PageCodec codec = PageCodec.CreateNew(s, key);
        var rng = new Random(pageSize + digestSet);
        foreach (uint pg in new uint[] { 1, 2, 77, uint.MaxValue })
        {
            byte[] plain = new byte[pageSize];
            rng.NextBytes(plain);
            if (pg == 1)
            {
                "SQLite format 3\0"u8.CopyTo(plain);
            }

            plain.AsSpan(pageSize - s.ReserveSize).Clear();
            byte[] c1 = new byte[pageSize], c2 = new byte[pageSize], back = new byte[pageSize];
            codec.Encode(pg, plain, c1);
            codec.Encode(pg, plain, c2);
            Assert.False(c1.AsSpan().SequenceEqual(c2), "a fresh IV on every write");
            Assert.False(c1.AsSpan(pg == 1 ? 32 : 0, 64).SequenceEqual(plain.AsSpan(pg == 1 ? 32 : 0, 64)));
            if (pg == 1)
            {
                Assert.True(header == 0 ? c1.AsSpan(0, 16).SequenceEqual(codec.Salt) : c1.AsSpan(0, header).SequenceEqual(plain.AsSpan(0, header)));
            }

            codec.Decode(pg, c1, back);
            Assert.True(plain.AsSpan().SequenceEqual(back));
            c1[^1] ^= 0x80; // the last reserve byte is padding or MAC; any change to the MAC area must fail, padding is not covered
            if (s.ReserveSize - 16 - s.HmacSize == 0)
            {
                Assert.False(codec.TryDecode(pg, c1, back));
            }
        }
    }

    [Fact]
    public void AllZeroPagesFailTheMacLikeAnyOtherDamage()
    {
        var s = new CipherSettings(1024, 2, CipherDigest.Sha512, CipherDigest.Sha512);
        using CipherKey key = CipherKey.FromPassphrase("z");
        using PageCodec codec = PageCodec.CreateNew(s, key);
        byte[] zero = new byte[1024], outp = new byte[1024];
        Array.Fill(outp, (byte)9);
        Assert.False(codec.TryDecode(5, zero, outp));
        Assert.All(outp, b => Assert.Equal(0, b));
        Assert.False(codec.TryDecode(1, zero, outp));
    }

    [Fact]
    public void RandomPagesNeverDecodeAndNeverThrow()
    {
        var s = new CipherSettings(512, 2, CipherDigest.Sha512, CipherDigest.Sha512);
        using CipherKey key = CipherKey.FromPassphrase("h");
        using PageCodec codec = PageCodec.CreateNew(s, key);
        var rng = new Random(7);
        byte[] page = new byte[512], outp = new byte[512];
        for (int i = 0; i < 2000; i++)
        {
            rng.NextBytes(page);
            Assert.False(codec.TryDecode((uint)(1 + i % 5), page, outp));
        }
    }

    [Fact]
    public void KeyFormsAreParsedLikePragmaKey()
    {
        using (CipherKey k = CipherKey.Parse("x'2DD29CA851E7B56E4697B0E1F08507293D761A05CE4D1B628663F411A8086D99'"))
        {
            Assert.True(k.IsRawKey);
            Assert.False(k.HasSalt);
        }

        using (CipherKey k = CipherKey.Parse(RawSaltKey))
        {
            Assert.True(k.IsRawKey);
            Assert.Equal("01010101010101010101010101010101", Convert.ToHexString(k.Salt).ToLowerInvariant());
        }

        foreach (string text in new[] { "x'abcd'", "x'zz" + new string('0', 62) + "'", "plain", "", "x'" + new string('0', 63) + "'" })
        {
            using CipherKey k = CipherKey.Parse(text);
            Assert.False(k.IsRawKey);
        }

        Assert.Throws<ArgumentException>(() => CipherKey.FromRawKey(new byte[31]));
    }

    [Fact]
    public void SettingsValidateAndReserveFollowsTheMac()
    {
        Assert.Equal(80, CipherSettings.V4.ReserveSize);
        Assert.Equal(48, CipherSettings.V3.ReserveSize);
        Assert.Equal(48, new CipherSettings(4096, 1, CipherDigest.Sha256, CipherDigest.Sha256).ReserveSize);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CipherSettings(3000, 1, CipherDigest.Sha1, CipherDigest.Sha1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CipherSettings(256, 1, CipherDigest.Sha1, CipherDigest.Sha1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CipherSettings(4096, 0, CipherDigest.Sha1, CipherDigest.Sha1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CipherSettings(4096, 1, CipherDigest.Sha1, CipherDigest.Sha1, 17));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CipherSettings(4096, 1, CipherDigest.Sha1, CipherDigest.Sha1, 4096));
        Assert.Throws<ArgumentOutOfRangeException>(() => CipherSettings.ForCompatibility(2));
    }

    [Fact]
    public void DisposedCodecRefusesWork()
    {
        var s = new CipherSettings(512, 2, CipherDigest.Sha512, CipherDigest.Sha512);
        using CipherKey key = CipherKey.FromPassphrase("d");
        PageCodec codec = PageCodec.CreateNew(s, key);
        codec.Dispose();
        codec.Dispose();
        Assert.Throws<ObjectDisposedException>(() => codec.Encode(2, new byte[512], new byte[512]));
    }

    [Fact]
    public void WholeFileRoundTripAndHeaderChecks()
    {
        var s = new CipherSettings(1024, 2, CipherDigest.Sha512, CipherDigest.Sha512);
        byte[] db = new byte[1024 * 3];
        new Random(3).NextBytes(db);
        "SQLite format 3\0"u8.CopyTo(db);
        db[16] = 4; db[17] = 0; db[20] = (byte)s.ReserveSize;
        for (int pg = 0; pg < 3; pg++)
        {
            db.AsSpan(pg * 1024 + 1024 - s.ReserveSize, s.ReserveSize).Clear();
        }

        using CipherKey key = CipherKey.FromPassphrase("file");
        var enc = new MemoryStream();
        CipherFile.Encrypt(new MemoryStream(db), enc, s, key);
        Assert.Equal(db.Length, enc.Length);
        var dec = new MemoryStream();
        CipherFile.Decrypt(new MemoryStream(enc.ToArray()), dec, s, key);
        Assert.True(db.AsSpan().SequenceEqual(dec.ToArray()));

        db[20] = 0;
        Assert.Equal(ResultCode.NotADb, Assert.Throws<OneLiteException>(() => CipherFile.Encrypt(new MemoryStream(db), new MemoryStream(), s, key)).ExtendedCode);
        Assert.Equal(ResultCode.NotADb, Assert.Throws<OneLiteException>(() => CipherFile.Decrypt(new MemoryStream(new byte[1500]), new MemoryStream(), s, key)).ExtendedCode);
    }
}
