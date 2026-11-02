using System;
using System.Security.Cryptography;
using System.Text;

namespace RecursiveAI.OneLite.Storage.Crypto;

/// <summary>
/// The key material a caller supplies: a passphrase (run through PBKDF2), a raw 32-byte key (used as is), or a raw key with an explicit
/// 16-byte salt. These are the three forms of <c>PRAGMA key</c> on Zetetic's API page: <c>'passphrase'</c>, <c>"x'&lt;64 hex&gt;'"</c> and
/// <c>"x'&lt;96 hex&gt;'"</c>. The bytes live in pinned arrays that <see cref="Dispose"/> zeroes. Managed memory can still be copied by the
/// runtime before it is pinned (for example a <see cref="string"/> the caller built), so the guarantee covers what this type holds, not the caller's copies.
/// </summary>
public sealed class CipherKey : IDisposable
{
    private byte[]? _material;
    private byte[]? _salt;

    private CipherKey(byte[] material, bool isRaw, byte[]? salt)
    {
        _material = material;
        IsRawKey = isRaw;
        _salt = salt;
    }

    /// <summary>True for a raw 32-byte key (no PBKDF2 step); false for a passphrase.</summary>
    public bool IsRawKey { get; }

    /// <summary>True when the key form carried its own salt (<c>x'&lt;96 hex&gt;'</c>).</summary>
    public bool HasSalt => _salt is not null;

    /// <summary>The salt that came with the key, or empty.</summary>
    public ReadOnlySpan<byte> Salt => _salt;

    internal ReadOnlySpan<byte> Material => _material ?? throw new ObjectDisposedException(nameof(CipherKey));

    /// <summary>A key from a passphrase, taken as its UTF-8 bytes.</summary>
    /// <param name="passphrase">The passphrase.</param>
    /// <returns>The key. Its bytes are copied; the caller's string is untouched.</returns>
    public static CipherKey FromPassphrase(string passphrase)
    {
        ArgumentNullException.ThrowIfNull(passphrase);

        // Encode straight into the pinned array: a temporary byte[] from Encoding.GetBytes(string) would leave an unzeroed copy on the heap.
        byte[] pinned = GC.AllocateUninitializedArray<byte>(Encoding.UTF8.GetByteCount(passphrase), pinned: true);
        Encoding.UTF8.GetBytes(passphrase, pinned);
        return new CipherKey(pinned, false, null);
    }

    /// <summary>A key from passphrase bytes (the bytes are copied).</summary>
    /// <param name="passphrase">The passphrase bytes, as the C API's <c>sqlite3_key</c> takes them.</param>
    /// <returns>The key.</returns>
    public static CipherKey FromPassphrase(ReadOnlySpan<byte> passphrase) => new(Pin(passphrase), false, null);

    /// <summary>A raw key, used directly as the AES-256 key; the page HMAC key is still derived from it and the salt.</summary>
    /// <param name="key">Exactly 32 bytes.</param>
    /// <param name="salt">The 16-byte salt, or empty to take it from the file (or generate one when creating).</param>
    /// <returns>The key.</returns>
    /// <exception cref="ArgumentException">The key is not 32 bytes, or the salt is neither empty nor 16 bytes.</exception>
    public static CipherKey FromRawKey(ReadOnlySpan<byte> key, ReadOnlySpan<byte> salt = default)
    {
        if (key.Length != CipherSettings.KeySize)
        {
            throw new ArgumentException("a raw key is 32 bytes", nameof(key));
        }

        if (salt.Length != 0 && salt.Length != CipherSettings.SaltSize)
        {
            throw new ArgumentException("a salt is 16 bytes", nameof(salt));
        }

        return new CipherKey(Pin(key), true, salt.Length == 0 ? null : salt.ToArray());
    }

    /// <summary>
    /// Interprets the text of a <c>PRAGMA key</c> value: <c>x'</c> followed by 64 hex digits and <c>'</c> is a raw key, 96 hex digits a raw key plus
    /// salt; anything else is a passphrase.
    /// </summary>
    /// <param name="text">The key text.</param>
    /// <returns>The key.</returns>
    public static CipherKey Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length >= 3 && (text[0] == 'x' || text[0] == 'X') && text[1] == '\'' && text[^1] == '\'')
        {
            int digits = text.Length - 3;
            if (digits == 64 || digits == 96)
            {
                byte[] bytes = GC.AllocateArray<byte>(48, pinned: true);
                if (Convert.FromHexString(text.AsSpan(2, digits), bytes, out _, out int written) == System.Buffers.OperationStatus.Done && written == digits / 2)
                {
                    try
                    {
                        return FromRawKey(bytes.AsSpan(0, 32), digits == 96 ? bytes.AsSpan(32, 16) : default);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(bytes);
                    }
                }

                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        return FromPassphrase(text);
    }

    /// <summary>A second key object with its own pinned copy of the bytes, so the original can be disposed while the copy lives on.</summary>
    /// <returns>The copy.</returns>
    /// <exception cref="ObjectDisposedException">This key was disposed.</exception>
    public CipherKey Clone() => new(Pin(Material), IsRawKey, _salt is null ? null : (byte[])_salt.Clone());

    /// <summary>Zeroes the key bytes.</summary>
    public void Dispose()
    {
        if (_material is { } m)
        {
            CryptographicOperations.ZeroMemory(m);
            _material = null;
        }

        if (_salt is { } s)
        {
            CryptographicOperations.ZeroMemory(s);
            _salt = null;
        }
    }

    internal static byte[] Pin(ReadOnlySpan<byte> source)
    {
        // Pinned object heap: the runtime never moves the array, so zeroing it later wipes the only copy this type made.
        byte[] a = GC.AllocateUninitializedArray<byte>(source.Length, pinned: true);
        source.CopyTo(a);
        return a;
    }
}
