namespace RecursiveAI.OneLite.Storage.Record;

/// <summary>
/// SQLite's variable-length integer (fileformat2 §1.8): 1 to 9 bytes, big-endian, seven bits per byte with the high bit meaning
/// "more follows"; the ninth byte, if reached, contributes all eight of its bits.
/// </summary>
public static class Varint
{
    /// <summary>The longest encoding.</summary>
    public const int MaxLength = 9;

    /// <summary>Decodes a varint from the start of <paramref name="src"/>.</summary>
    /// <param name="src">Bytes to decode from.</param>
    /// <param name="value">The value (all 64 bits; callers cast to <see cref="long"/> for rowids).</param>
    /// <returns>The number of bytes used, or 0 if <paramref name="src"/> ended inside the varint.</returns>
    public static int Read(ReadOnlySpan<byte> src, out ulong value)
    {
        ulong v = 0;
        int n = Math.Min(8, src.Length);
        for (int i = 0; i < n; i++)
        {
            byte b = src[i];
            v = (v << 7) | (uint)(b & 0x7f);
            if ((b & 0x80) == 0)
            {
                value = v;
                return i + 1;
            }
        }

        if (src.Length < 9)
        {
            value = 0;
            return 0;
        }

        value = (v << 8) | src[8];
        return 9;
    }

    /// <summary>The encoded length of <paramref name="value"/>.</summary>
    public static int Length(ulong value)
    {
        if ((value & 0xff00000000000000UL) != 0)
        {
            return 9;
        }

        int n = 1;
        while ((value >>= 7) != 0)
        {
            n++;
        }

        return n;
    }

    /// <summary>Encodes <paramref name="value"/> at the start of <paramref name="dst"/> (which must hold <see cref="Length"/> bytes).</summary>
    /// <returns>The number of bytes written.</returns>
    public static int Write(Span<byte> dst, ulong value)
    {
        if ((value & 0xff00000000000000UL) != 0)
        {
            dst[8] = (byte)value;
            value >>= 8;
            for (int i = 7; i >= 0; i--)
            {
                dst[i] = (byte)((value & 0x7f) | 0x80);
                value >>= 7;
            }

            return 9;
        }

        int n = Length(value);
        for (int i = n - 1; i >= 0; i--)
        {
            dst[i] = (byte)((value & 0x7f) | (i == n - 1 ? 0u : 0x80u));
            value >>= 7;
        }

        return n;
    }
}
