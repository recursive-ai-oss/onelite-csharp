using System;
using System.Security.Cryptography;

namespace RecursiveAI.OneLite.Storage.Crypto;

/// <summary>The hash behind a PBKDF2 or HMAC step (<c>cipher_kdf_algorithm</c>, <c>cipher_hmac_algorithm</c>).</summary>
public enum CipherDigest : byte
{
    /// <summary>SHA-1 (20-byte output). The SQLCipher 3 setting.</summary>
    Sha1 = 1,

    /// <summary>SHA-256 (32-byte output).</summary>
    Sha256 = 2,

    /// <summary>SHA-512 (64-byte output). The SQLCipher 4 setting.</summary>
    Sha512 = 3,
}

/// <summary>
/// The parameters of one SQLCipher-compatible encrypted database. They are not stored in the file, so whoever opens the file must supply
/// the same ones that created it, exactly as with <c>PRAGMA cipher_page_size</c>, <c>kdf_iter</c> and the other settings on Zetetic's API page.
/// Instances are immutable.
/// </summary>
public sealed class CipherSettings
{
    // fileformat2 §1.3.2: the page size is a power of two from 512 to 65536.
    private const int MinPageSize = 512;

    private const int MaxPageSize = 65536;

    /// <summary>The size of the AES block and of the per-page IV, in bytes (FIPS 197, SP 800-38A).</summary>
    public const int BlockSize = 16;

    /// <summary>The size of the salt at the start of the file, in bytes.</summary>
    public const int SaltSize = 16;

    /// <summary>The size of the AES-256 key, in bytes.</summary>
    public const int KeySize = 32;

    /// <summary>Creates settings; every argument is validated.</summary>
    /// <param name="pageSize">The database page size, a power of two from 512 to 65536 (<c>cipher_page_size</c>).</param>
    /// <param name="kdfIterations">PBKDF2 iterations for a passphrase, at least 1 (<c>kdf_iter</c>). Ignored for raw keys.</param>
    /// <param name="kdfDigest">The PBKDF2 hash for the encryption key (<c>cipher_kdf_algorithm</c>).</param>
    /// <param name="hmacDigest">The HMAC hash for page MACs and for deriving the MAC key (<c>cipher_hmac_algorithm</c>).</param>
    /// <param name="plaintextHeaderSize">Bytes at the start of page 1 left unencrypted (<c>cipher_plaintext_header_size</c>); 0 for none,
    /// otherwise a multiple of 16 that is at least 16 and below the usable size of page 1. With a plaintext header the salt is not in the file.</param>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its documented range.</exception>
    public CipherSettings(int pageSize, int kdfIterations, CipherDigest kdfDigest, CipherDigest hmacDigest, int plaintextHeaderSize = 0)
    {
        if (pageSize < MinPageSize || pageSize > MaxPageSize || (pageSize & (pageSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "cipher_page_size must be a power of two between 512 and 65536");
        }

        if (kdfIterations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(kdfIterations), "kdf_iter must be at least 1");
        }

        if (!Enum.IsDefined(kdfDigest))
        {
            throw new ArgumentOutOfRangeException(nameof(kdfDigest));
        }

        if (!Enum.IsDefined(hmacDigest))
        {
            throw new ArgumentOutOfRangeException(nameof(hmacDigest));
        }

        PageSize = pageSize;
        KdfIterations = kdfIterations;
        KdfDigest = kdfDigest;
        HmacDigest = hmacDigest;
        ReserveSize = ReserveFor(hmacDigest);
        if (plaintextHeaderSize != 0 && (plaintextHeaderSize < BlockSize || plaintextHeaderSize % BlockSize != 0 || plaintextHeaderSize >= pageSize - ReserveSize))
        {
            throw new ArgumentOutOfRangeException(nameof(plaintextHeaderSize),
                "cipher_plaintext_header_size must be 0, or a multiple of 16 that is at least 16 and below the usable size of page 1");
        }

        PlaintextHeaderSize = plaintextHeaderSize;
    }

    /// <summary>SQLCipher 4 defaults (<c>cipher_compatibility = 4</c>): 4096-byte pages, 256,000 iterations, SHA-512 for both, reserve 80.</summary>
    public static CipherSettings V4 { get; } = new(4096, 256_000, CipherDigest.Sha512, CipherDigest.Sha512);

    /// <summary>SQLCipher 3 defaults (<c>cipher_compatibility = 3</c>): 1024-byte pages, 64,000 iterations, SHA-1 for both, reserve 48.</summary>
    public static CipherSettings V3 { get; } = new(1024, 64_000, CipherDigest.Sha1, CipherDigest.Sha1);

    /// <summary>The database page size in bytes.</summary>
    public int PageSize { get; }

    /// <summary>PBKDF2 iterations used to turn a passphrase into the encryption key.</summary>
    public int KdfIterations { get; }

    /// <summary>The hash of the passphrase PBKDF2.</summary>
    public CipherDigest KdfDigest { get; }

    /// <summary>The hash of the page HMACs and of the MAC-key PBKDF2.</summary>
    public CipherDigest HmacDigest { get; }

    /// <summary>The unencrypted prefix of page 1 in bytes, or 0 when the salt leads the file instead.</summary>
    public int PlaintextHeaderSize { get; }

    /// <summary>
    /// Bytes reserved at the end of every page (the database header's "reserved bytes per page", fileformat2 §1.3.8): the 16-byte IV, the HMAC,
    /// rounded up to the 16-byte cipher block: 80 for SHA-512, 48 for SHA-256 and for SHA-1.
    /// </summary>
    public int ReserveSize { get; }

    /// <summary>The HMAC output size in bytes.</summary>
    public int HmacSize => DigestSize(HmacDigest);

    /// <summary>A copy of these settings with another page size.</summary>
    /// <param name="pageSize">The new page size.</param>
    /// <returns>The new settings.</returns>
    public CipherSettings WithPageSize(int pageSize) => new(pageSize, KdfIterations, KdfDigest, HmacDigest, PlaintextHeaderSize);

    /// <summary>A copy of these settings with another iteration count.</summary>
    /// <param name="iterations">The new count.</param>
    /// <returns>The new settings.</returns>
    public CipherSettings WithKdfIterations(int iterations) => new(PageSize, iterations, KdfDigest, HmacDigest, PlaintextHeaderSize);

    /// <summary>A copy of these settings with another plaintext header size.</summary>
    /// <param name="size">The new size; 0 for none.</param>
    /// <returns>The new settings.</returns>
    public CipherSettings WithPlaintextHeaderSize(int size) => new(PageSize, KdfIterations, KdfDigest, HmacDigest, size);

    /// <summary>A copy of these settings with other digests.</summary>
    /// <param name="kdf">The PBKDF2 hash.</param>
    /// <param name="hmac">The HMAC hash.</param>
    /// <returns>The new settings.</returns>
    public CipherSettings WithDigests(CipherDigest kdf, CipherDigest hmac) => new(PageSize, KdfIterations, kdf, hmac, PlaintextHeaderSize);

    /// <summary>The settings <c>PRAGMA cipher_compatibility</c> selects for a major version.</summary>
    /// <param name="majorVersion">3 or 4.</param>
    /// <returns>The defaults of that SQLCipher major version.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Versions 1 and 2 (HMAC-less or 4,000-iteration formats) are not supported.</exception>
    public static CipherSettings ForCompatibility(int majorVersion) => majorVersion switch
    {
        4 => V4,
        3 => V3,
        _ => throw new ArgumentOutOfRangeException(nameof(majorVersion), "only SQLCipher 3 and 4 compatibility settings are supported"),
    };

    internal static int DigestSize(CipherDigest d) => d switch
    {
        CipherDigest.Sha1 => 20,
        CipherDigest.Sha256 => 32,
        _ => 64,
    };

    internal static HashAlgorithmName Name(CipherDigest d) => d switch
    {
        CipherDigest.Sha1 => HashAlgorithmName.SHA1,
        CipherDigest.Sha256 => HashAlgorithmName.SHA256,
        _ => HashAlgorithmName.SHA512,
    };

    private static int ReserveFor(CipherDigest hmac)
    {
        // IV then MAC, padded up to a whole cipher block so the encrypted area stays block-aligned.
        int n = BlockSize + DigestSize(hmac);
        return (n + BlockSize - 1) / BlockSize * BlockSize;
    }
}
