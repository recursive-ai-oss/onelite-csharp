using System;
using System.Buffers.Binary;
using System.IO;

namespace RecursiveAI.OneLite.Storage.Crypto;

/// <summary>
/// Whole-file page-by-page transforms between a plaintext database and its SQLCipher-compatible encrypted form. They exist for tests, tools
/// and offline conversion; the engine itself encrypts and decrypts one page at a time through <see cref="PageCodec"/>.
/// </summary>
/// <remarks>
/// A plaintext database can be encrypted page by page only if its pages already leave the reserve area free: its header's reserved-bytes field
/// (fileformat2 §1.3.8) must be at least <see cref="CipherSettings.ReserveSize"/>, so that no cell reaches the IV and MAC; the reference library writes 80 for every setting. Create such a file by setting the reserve before building it, or by
/// rebuilding an ordinary database with that reserve (<c>VACUUM</c> after the file-control that sets reserve bytes).
/// </remarks>
public static class CipherFile
{
    private const int HeaderReservedOffset = 20;

    /// <summary>Encrypts a plaintext database.</summary>
    /// <param name="plain">The plaintext database, positioned anywhere; it is read from the start.</param>
    /// <param name="output">Receives the encrypted file, written from its current position.</param>
    /// <param name="settings">The cipher parameters; <see cref="CipherSettings.PageSize"/> must be the database's page size.</param>
    /// <param name="key">The key.</param>
    /// <param name="salt">The 16-byte salt to use, or empty for a random one (ignored if the key carries one).</param>
    /// <returns>The salt used, which a plaintext-header file needs to keep elsewhere.</returns>
    /// <exception cref="OneLiteException">SQLITE_NOTADB when the input is not a database of the expected page size, or its reserved-bytes field is wrong.</exception>
    public static byte[] Encrypt(Stream plain, Stream output, CipherSettings settings, CipherKey key, ReadOnlySpan<byte> salt = default)
    {
        ArgumentNullException.ThrowIfNull(plain);
        ArgumentNullException.ThrowIfNull(output);
        plain.Position = 0;
        int p = settings.PageSize;
        long length = plain.Length;
        if (length == 0 || length % p != 0)
        {
            throw new OneLiteException(ResultCode.NotADb, "file is not a database", $"size {length} is not a whole number of {p}-byte pages");
        }

        byte[] page = new byte[p];
        byte[] enc = new byte[p];
        using PageCodec codec = PageCodec.CreateNew(settings, key, salt);
        long pages = length / p;
        for (long i = 1; i <= pages; i++)
        {
            plain.ReadExactly(page);
            if (i == 1)
            {
                CheckHeader(page, settings);
            }

            codec.Encode((uint)i, page, enc);
            output.Write(enc);
        }

        return codec.Salt.ToArray();
    }

    /// <summary>Decrypts an encrypted database to plaintext.</summary>
    /// <param name="cipher">The encrypted file, read from the start.</param>
    /// <param name="output">Receives the plaintext database, written from its current position.</param>
    /// <param name="settings">The cipher parameters the file was written with.</param>
    /// <param name="key">The key.</param>
    /// <param name="externalSalt">The salt kept outside the file (required with a plaintext header), or empty.</param>
    /// <exception cref="OneLiteException">SQLITE_NOTADB "file is not a database" for a wrong key or a damaged page 1 or a size that is not whole pages;
    /// SQLITE_CORRUPT naming the page for a later bad page.</exception>
    public static void Decrypt(Stream cipher, Stream output, CipherSettings settings, CipherKey key, ReadOnlySpan<byte> externalSalt = default)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        ArgumentNullException.ThrowIfNull(output);
        cipher.Position = 0;
        int p = settings.PageSize;
        long length = cipher.Length;
        if (length < p || length % p != 0)
        {
            throw new OneLiteException(ResultCode.NotADb, "file is not a database", $"size {length} is not a whole number of {p}-byte pages");
        }

        byte[] enc = new byte[p];
        byte[] page = new byte[p];
        cipher.ReadExactly(enc);
        using PageCodec codec = PageCodec.OpenExisting(settings, key, enc, externalSalt);
        long pages = length / p;
        for (long i = 1; i <= pages; i++)
        {
            if (i > 1)
            {
                cipher.ReadExactly(enc);
            }

            codec.Decode((uint)i, enc, page);
            output.Write(page);
        }
    }

    private static void CheckHeader(byte[] page, CipherSettings settings)
    {
        if (!page.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8))
        {
            throw new OneLiteException(ResultCode.NotADb, "file is not a database", "page 1 does not start with the SQLite header string");
        }

        int ps = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(16));
        if (ps == 1)
        {
            ps = 65536;
        }

        if (ps != settings.PageSize)
        {
            throw new OneLiteException(ResultCode.NotADb, "file is not a database", $"header page size {ps} differs from cipher_page_size {settings.PageSize}");
        }

        if (page[HeaderReservedOffset] < settings.ReserveSize)
        {
            throw new OneLiteException(ResultCode.NotADb, "file is not a database",
                $"header reserved bytes {page[HeaderReservedOffset]} are fewer than the {settings.ReserveSize} the cipher needs; rebuild the database with that reserve first");
        }
    }
}
