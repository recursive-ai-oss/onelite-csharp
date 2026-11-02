using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RecursiveAI.OneLite.Storage.Crypto;

/// <summary>
/// The SQLCipher-compatible page transform: <see cref="Encode"/> turns one plaintext database page into its on-disk form and
/// <see cref="Decode"/> turns it back, verifying the page MAC first. The pager calls it with the page number and a page-sized buffer on each side.
/// </summary>
/// <remarks>
/// <para>Layout of an encoded page of size P with reserve R (Zetetic's SQLCipher design page and API page; byte-level details were confirmed
/// against files the reference library writes): <c>[0, P-R)</c> AES-256-CBC ciphertext with no padding, then at <c>P-R</c> the 16-byte IV, then
/// the HMAC of the ciphertext and IV followed by the 4-byte little-endian page number, then zero padding up to the page end.</para>
/// <para>Page 1 holds the 16-byte salt where the plaintext would carry <c>"SQLite format 3\0"</c>; its ciphertext and MAC start at byte 16, so the
/// salt is not covered by the MAC. With a plaintext header of N bytes the first N bytes are stored as is, the ciphertext and MAC start at N and the
/// salt is held outside the file.</para>
/// <para>The plaintext page the caller supplies has P bytes; its last R bytes are not stored. <see cref="Decode"/> zeroes them. The database
/// header's reserved-bytes field (offset 20) must be at least R so that the B-tree stays clear of the IV and MAC (the reference library writes 80 whatever the digest).</para>
/// <para>Not thread-safe: one codec per connection and database. <see cref="Dispose"/> zeroes the derived keys.</para>
/// </remarks>
public sealed class PageCodec : IDisposable
{
    private const int HeaderMagicLength = 16;

    // fileformat2 §1.3.1: the header string every SQLite file starts with.
    private static ReadOnlySpan<byte> Magic => "SQLite format 3\0"u8;

    private readonly CipherSettings _settings;
    private readonly byte[] _salt;
    private byte[]? _encKey;
    private byte[]? _macKey;
    private Aes? _aes;
    private IncrementalHash? _mac;
    private readonly byte[] _macOut;
    private readonly byte[] _pgnoBytes = new byte[4];
    private readonly byte[] _iv = new byte[CipherSettings.BlockSize];

    private PageCodec(CipherSettings settings, CipherKey key, ReadOnlySpan<byte> salt)
    {
        _settings = settings;
        _salt = salt.ToArray();
        _macOut = new byte[settings.HmacSize];
        _encKey = GC.AllocateArray<byte>(CipherSettings.KeySize, pinned: true);
        _macKey = GC.AllocateArray<byte>(CipherSettings.KeySize, pinned: true);
        try
        {
            DeriveKeys(settings, key, _salt, _encKey, _macKey);
            _aes = Aes.Create();
            _aes.SetKey(_encKey);
            _mac = IncrementalHash.CreateHMAC(CipherSettings.Name(settings.HmacDigest), _macKey);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Whether this platform has the BCL crypto the codec needs. False in the browser build, where encryption is unsupported.</summary>
    public static bool IsSupported => !OperatingSystem.IsBrowser();

    /// <summary>The settings in force.</summary>
    public CipherSettings Settings => _settings;

    /// <summary>The 16-byte salt (read it to store it when a plaintext header keeps it out of the file).</summary>
    public ReadOnlySpan<byte> Salt => _salt;

    /// <summary>The reserved bytes per page this codec needs; the database header's offset-20 byte must be at least this.</summary>
    public int ReserveSize => _settings.ReserveSize;

    /// <summary>
    /// Starts a new database: the salt comes from the key if it carries one, else from <paramref name="salt"/>, else from the system random generator.
    /// </summary>
    /// <param name="settings">The cipher parameters.</param>
    /// <param name="key">The key.</param>
    /// <param name="salt">An explicit 16-byte salt, or empty. Ignored when the key carries one.</param>
    /// <returns>The codec.</returns>
    /// <exception cref="OneLiteException">Encryption is not supported on this platform (SQLITE_ERROR).</exception>
    public static PageCodec CreateNew(CipherSettings settings, CipherKey key, ReadOnlySpan<byte> salt = default)
    {
        Check(settings, key);
        byte[] salt16 = new byte[CipherSettings.SaltSize];
        if (key.HasSalt)
        {
            key.Salt.CopyTo(salt16);
        }
        else if (salt.Length == CipherSettings.SaltSize)
        {
            salt.CopyTo(salt16);
        }
        else if (salt.Length == 0)
        {
            RandomNumberGenerator.Fill(salt16);
        }
        else
        {
            throw new ArgumentException("a salt is 16 bytes", nameof(salt));
        }

        return new PageCodec(settings, key, salt16);
    }

    /// <summary>
    /// Opens an existing database: the salt comes from the key if it carries one, else from <paramref name="externalSalt"/> (required when the
    /// settings have a plaintext header), else from the first 16 bytes of page 1.
    /// </summary>
    /// <param name="settings">The cipher parameters the file was written with.</param>
    /// <param name="key">The key.</param>
    /// <param name="page1">The first page as stored (at least 16 bytes are read from it when it supplies the salt).</param>
    /// <param name="externalSalt">The salt kept outside the file, or empty.</param>
    /// <returns>The codec. It has not yet proven the key: <see cref="Decode"/> of page 1 does.</returns>
    /// <exception cref="OneLiteException">SQLITE_NOTADB when no salt can be found, or encryption is unsupported here (SQLITE_ERROR).</exception>
    public static PageCodec OpenExisting(CipherSettings settings, CipherKey key, ReadOnlySpan<byte> page1, ReadOnlySpan<byte> externalSalt = default)
    {
        Check(settings, key);
        byte[] salt16 = new byte[CipherSettings.SaltSize];
        if (key.HasSalt)
        {
            key.Salt.CopyTo(salt16);
        }
        else if (externalSalt.Length == CipherSettings.SaltSize)
        {
            externalSalt.CopyTo(salt16);
        }
        else if (settings.PlaintextHeaderSize == 0 && page1.Length >= CipherSettings.SaltSize)
        {
            page1[..CipherSettings.SaltSize].CopyTo(salt16);
        }
        else
        {
            throw NotADb("no salt: the file is too short, or a plaintext header needs the salt supplied");
        }

        return new PageCodec(settings, key, salt16);
    }

    /// <summary>
    /// Encodes one page. The IV is fresh from the system random generator on every call, so rewriting the same content gives different bytes.
    /// </summary>
    /// <param name="pageNumber">The 1-based page number; it is bound into the MAC so pages cannot be swapped.</param>
    /// <param name="plain">The page, <see cref="CipherSettings.PageSize"/> bytes. Its last <see cref="ReserveSize"/> bytes are ignored.</param>
    /// <param name="cipher">Receives the stored page, <see cref="CipherSettings.PageSize"/> bytes. Must not overlap <paramref name="plain"/>.</param>
    /// <exception cref="ArgumentException">A buffer is not exactly one page, or the page number is 0.</exception>
    public void Encode(uint pageNumber, ReadOnlySpan<byte> plain, Span<byte> cipher)
    {
        CheckArgs(pageNumber, plain.Length, cipher.Length);
        int p = _settings.PageSize;
        int dataEnd = p - _settings.ReserveSize;
        int offset = DataOffset(pageNumber);
        if (pageNumber == 1)
        {
            if (_settings.PlaintextHeaderSize != 0)
            {
                plain[..offset].CopyTo(cipher);
            }
            else
            {
                _salt.CopyTo(cipher);
            }
        }

        RandomNumberGenerator.Fill(_iv);
        _aes!.EncryptCbc(plain[offset..dataEnd], _iv, cipher[offset..dataEnd], PaddingMode.None);
        _iv.CopyTo(cipher[dataEnd..]);
        ComputeMac(pageNumber, cipher[offset..(dataEnd + CipherSettings.BlockSize)]);
        _macOut.CopyTo(cipher[(dataEnd + CipherSettings.BlockSize)..]);
        cipher[(dataEnd + CipherSettings.BlockSize + _macOut.Length)..].Clear();
    }

    /// <summary>
    /// Verifies and decodes one page. The MAC is checked (in constant time) before anything is decrypted.
    /// An all-zero stored page fails like any other page whose MAC does not match.
    /// </summary>
    /// <param name="pageNumber">The 1-based page number.</param>
    /// <param name="cipher">The stored page, <see cref="CipherSettings.PageSize"/> bytes.</param>
    /// <param name="plain">Receives the page; the reserve area is zeroed. On failure its contents are cleared. Must not overlap <paramref name="cipher"/>.</param>
    /// <returns>True when the page is authentic; false when the MAC does not match (a wrong key, a damaged page or a tampered one).</returns>
    /// <exception cref="ArgumentException">A buffer is not exactly one page, or the page number is 0.</exception>
    public bool TryDecode(uint pageNumber, ReadOnlySpan<byte> cipher, Span<byte> plain)
    {
        CheckArgs(pageNumber, plain.Length, cipher.Length);
        int p = _settings.PageSize;
        int dataEnd = p - _settings.ReserveSize;
        int offset = DataOffset(pageNumber);
        // No exemption for all-zero pages: the reference shell reports an all-zero page 2..N as a failed HMAC like any other damage
        // (docs/encryption.md, "All-zero pages"). A page past the end of the file never reaches the codec; the pager reads it as zeros.
        int macStart = dataEnd + CipherSettings.BlockSize;
        ComputeMac(pageNumber, cipher[offset..macStart]);
        if (!CryptographicOperations.FixedTimeEquals(_macOut, cipher.Slice(macStart, _macOut.Length)))
        {
            plain.Clear();
            return false;
        }

        if (pageNumber == 1)
        {
            if (_settings.PlaintextHeaderSize != 0)
            {
                cipher[..offset].CopyTo(plain);
            }
            else
            {
                Magic.CopyTo(plain);
            }
        }

        cipher.Slice(dataEnd, CipherSettings.BlockSize).CopyTo(_iv);
        _aes!.DecryptCbc(cipher[offset..dataEnd], _iv, plain[offset..dataEnd], PaddingMode.None);
        plain[dataEnd..].Clear();
        return true;
    }

    /// <summary>Like <see cref="TryDecode"/>, but throws on a bad page.</summary>
    /// <param name="pageNumber">The 1-based page number.</param>
    /// <param name="cipher">The stored page.</param>
    /// <param name="plain">Receives the page.</param>
    /// <exception cref="OneLiteException">SQLITE_NOTADB, "file is not a database": page 1 failed (the usual sign of a wrong key). Other pages
    /// raise SQLITE_CORRUPT with the page number in <see cref="OneLiteException.Detail"/>.</exception>
    public void Decode(uint pageNumber, ReadOnlySpan<byte> cipher, Span<byte> plain)
    {
        if (!TryDecode(pageNumber, cipher, plain))
        {
            // With a plaintext header the file still parses as a database, so a page 1 that fails its MAC is corruption rather than "not a
            // database" (SQLCipher 4.20 reports SQLITE_CORRUPT here; 4.5.6 reports a generic "SQL logic error"). Without one it is "file is not a database".
            const string wrong = "page 1: the page MAC does not match (wrong key or settings, or not an encrypted database)";
            throw pageNumber == 1
                ? (_settings.PlaintextHeaderSize != 0 ? new OneLiteException(ResultCode.Corrupt, "database disk image is malformed", wrong) : NotADb(wrong))
                : new OneLiteException(ResultCode.Corrupt, "database disk image is malformed", $"page {pageNumber}: the page MAC does not match (the page was changed or damaged)");
        }
    }

    /// <summary>Zeroes the derived keys and releases the cipher objects.</summary>
    public void Dispose()
    {
        _aes?.Dispose();
        _aes = null;
        _mac?.Dispose();
        _mac = null;
        if (_encKey is { } e)
        {
            CryptographicOperations.ZeroMemory(e);
            _encKey = null;
        }

        if (_macKey is { } m)
        {
            CryptographicOperations.ZeroMemory(m);
            _macKey = null;
        }
    }

    // Zetetic design page: the MAC key is derived from the encryption key with PBKDF2 and 2 iterations over the salt with every byte XORed
    // with 0x3a, so the two keys differ even though they come from one secret. The raw-key forms skip only the first step.
    private static void DeriveKeys(CipherSettings s, CipherKey key, byte[] salt, byte[] encKey, byte[] macKey)
    {
        if (key.IsRawKey)
        {
            key.Material.CopyTo(encKey);
        }
        else
        {
            Rfc2898DeriveBytes.Pbkdf2(key.Material, salt, encKey, s.KdfIterations, CipherSettings.Name(s.KdfDigest));
        }

        byte[] macSalt = new byte[salt.Length];
        for (int i = 0; i < salt.Length; i++)
        {
            macSalt[i] = (byte)(salt[i] ^ 0x3a);
        }

        Rfc2898DeriveBytes.Pbkdf2(encKey, macSalt, macKey, 2, CipherSettings.Name(s.KdfDigest));
    }

    private void ComputeMac(uint pageNumber, ReadOnlySpan<byte> ciphertextAndIv)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_pgnoBytes, pageNumber);
        _mac!.AppendData(ciphertextAndIv);
        _mac.AppendData(_pgnoBytes);
        _mac.GetHashAndReset(_macOut);
    }

    private int DataOffset(uint pageNumber)
    {
        if (pageNumber != 1)
        {
            return 0;
        }

        return _settings.PlaintextHeaderSize != 0 ? _settings.PlaintextHeaderSize : HeaderMagicLength;
    }

    private void CheckArgs(uint pageNumber, int plainLength, int cipherLength)
    {
        ObjectDisposedException.ThrowIf(_aes is null, this);
        if (pageNumber == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "page numbers start at 1");
        }

        if (plainLength != _settings.PageSize || cipherLength != _settings.PageSize)
        {
            throw new ArgumentException("buffers must be exactly one page");
        }
    }

    private static void Check(CipherSettings settings, CipherKey key)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(key);
        if (!IsSupported)
        {
            throw new OneLiteException(ResultCode.Error, "encryption is not supported in the browser build");
        }
    }

    private static OneLiteException NotADb(string detail) => new(ResultCode.NotADb, "file is not a database", detail);
}
