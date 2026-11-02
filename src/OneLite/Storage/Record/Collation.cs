using System.Text;

namespace RecursiveAI.OneLite.Storage.Record;

/// <summary>Conversions between .NET strings and the three database text encodings.</summary>
public static class TextCodec
{
    /// <summary>Encodes <paramref name="s"/> in <paramref name="enc"/>.</summary>
    public static byte[] Encode(string s, TextEncoding enc) => enc switch
    {
        TextEncoding.Utf8 => Encoding.UTF8.GetBytes(s),
        TextEncoding.Utf16Le => Encoding.Unicode.GetBytes(s),
        _ => Encoding.BigEndianUnicode.GetBytes(s),
    };

    /// <summary>Decodes text bytes of the given encoding to a string (invalid sequences become U+FFFD).</summary>
    public static string Decode(ReadOnlySpan<byte> b, TextEncoding enc) => enc switch
    {
        TextEncoding.Utf8 => Encoding.UTF8.GetString(b),
        TextEncoding.Utf16Le => Encoding.Unicode.GetString(b[..(b.Length & ~1)]),
        _ => Encoding.BigEndianUnicode.GetString(b[..(b.Length & ~1)]),
    };

    /// <summary>Converts stored text to UTF-8, the form a result is rendered in. UTF-8 input is returned as is, byte for byte.</summary>
    public static byte[] ToUtf8(ReadOnlySpan<byte> b, TextEncoding enc)
        => enc == TextEncoding.Utf8 ? b.ToArray() : Encoding.UTF8.GetBytes(Decode(b, enc));

    /// <summary>
    /// Reads the next code point from UTF-16 bytes. A lone surrogate is returned as itself, so ordering stays total.
    /// </summary>
    internal static int NextUtf16(ReadOnlySpan<byte> b, ref int pos, bool bigEndian)
    {
        int u = Unit(b, pos, bigEndian);
        pos += 2;
        if (u is >= 0xD800 and < 0xDC00 && pos + 1 < b.Length)
        {
            int u2 = Unit(b, pos, bigEndian);
            if (u2 is >= 0xDC00 and < 0xE000)
            {
                pos += 2;
                return 0x10000 + ((u - 0xD800) << 10) + (u2 - 0xDC00);
            }
        }

        return u;
    }

    private static int Unit(ReadOnlySpan<byte> b, int pos, bool be)
        => be ? (b[pos] << 8) | b[pos + 1] : (b[pos + 1] << 8) | b[pos];
}

/// <summary>A collating sequence: compares two text values given as stored bytes in the database encoding (datatype3 §7).</summary>
/// <param name="a">Left text.</param>
/// <param name="b">Right text.</param>
/// <returns>Negative, zero or positive.</returns>
public delegate int CollationFunction(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b);

/// <summary>The built-in collations BINARY, NOCASE and RTRIM (datatype3 §7.1).</summary>
public static class Collations
{
    /// <summary>BINARY: memcmp of the stored bytes, shorter first when one is a prefix of the other.</summary>
    public static int Binary(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int c = a.SequenceCompareTo(b);
        return c;
    }

    /// <summary>Returns the NOCASE collation (ASCII letters fold to lower case) for a database encoding.</summary>
    public static CollationFunction NoCase(TextEncoding enc) => enc switch
    {
        TextEncoding.Utf8 => NoCaseUtf8,
        TextEncoding.Utf16Le => (a, b) => Utf16(a, b, false, true, false),
        _ => (a, b) => Utf16(a, b, true, true, false),
    };

    /// <summary>Returns the RTRIM collation (trailing spaces ignored) for a database encoding.</summary>
    public static CollationFunction RTrim(TextEncoding enc) => enc switch
    {
        TextEncoding.Utf8 => RTrimUtf8,
        TextEncoding.Utf16Le => (a, b) => Utf16(a, b, false, false, true),
        _ => (a, b) => Utf16(a, b, true, false, true),
    };

    private static int Fold(int c) => c is >= 'A' and <= 'Z' ? c + 32 : c;

    private static int NoCaseUtf8(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int d = Fold(a[i]) - Fold(b[i]);
            if (d != 0)
            {
                return d;
            }
        }

        return a.Length - b.Length;
    }

    private static int RTrimUtf8(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        a = a.TrimEnd((byte)' ');
        b = b.TrimEnd((byte)' ');
        return a.SequenceCompareTo(b);
    }

    private static int Utf16(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, bool be, bool fold, bool rtrim)
    {
        // Both collations are defined on UTF-8; the other encodings are transcoded first, which orders by code point.
        int la = a.Length & ~1, lb = b.Length & ~1;
        if (rtrim)
        {
            while (la >= 2 && Unit16(a, la - 2, be) == ' ')
            {
                la -= 2;
            }

            while (lb >= 2 && Unit16(b, lb - 2, be) == ' ')
            {
                lb -= 2;
            }
        }

        a = a[..la];
        b = b[..lb];
        int pa = 0, pb = 0;
        while (pa < a.Length && pb < b.Length)
        {
            int ca = TextCodec.NextUtf16(a, ref pa, be);
            int cb = TextCodec.NextUtf16(b, ref pb, be);
            if (fold)
            {
                ca = Fold(ca);
                cb = Fold(cb);
            }

            if (ca != cb)
            {
                return ca < cb ? -1 : 1;
            }
        }

        return (pa < a.Length ? 1 : 0) - (pb < b.Length ? 1 : 0);
    }

    private static int Unit16(ReadOnlySpan<byte> b, int pos, bool be) => be ? (b[pos] << 8) | b[pos + 1] : (b[pos + 1] << 8) | b[pos];
}

/// <summary>How an index or WITHOUT ROWID tree orders its keys: how many leading columns are compared, each column's collation and direction.</summary>
public sealed class KeyInfo
{
    /// <summary>Creates key info.</summary>
    /// <param name="encoding">The database text encoding.</param>
    /// <param name="collations">Per-column collation; null entries mean BINARY. Its length is the number of columns compared.</param>
    /// <param name="descending">Per-column descending flag; null means all ascending.</param>
    public KeyInfo(TextEncoding encoding, CollationFunction?[] collations, bool[]? descending = null)
    {
        Encoding = encoding;
        Collations = collations;
        Descending = descending;
    }

    /// <summary>Simple key info comparing <paramref name="columns"/> columns with BINARY and ascending order.</summary>
    public static KeyInfo Binary(TextEncoding enc, int columns) => new(enc, new CollationFunction?[columns]);

    /// <summary>The database text encoding.</summary>
    public TextEncoding Encoding { get; }

    /// <summary>Per-column collation (null = BINARY).</summary>
    public CollationFunction?[] Collations { get; }

    /// <summary>Per-column descending flags, or null.</summary>
    public bool[]? Descending { get; }

    /// <summary>The number of columns compared.</summary>
    public int ColumnCount => Collations.Length;
}

/// <summary>Comparison of records with each other and with unpacked keys, in SQLite's sort order (datatype3 §4.1, §7).</summary>
public static class RecordCompare
{
    /// <summary>
    /// Compares a stored record with an unpacked key.
    /// </summary>
    /// <param name="ki">Column collations and directions.</param>
    /// <param name="rec">A parsed record.</param>
    /// <param name="key">The key columns. At most <c>ki.ColumnCount</c> are compared; comparison also stops at <paramref name="key"/>'s length.</param>
    /// <param name="prefixResult">The result when every key column equals the record's. 0 for an exact match; -1 or 1 positions a seek just before or after all records that begin with the key.</param>
    /// <returns>Negative if the record sorts before the key, positive if after.</returns>
    public static int Compare(KeyInfo ki, RecordView rec, ReadOnlySpan<Mem> key, int prefixResult)
    {
        int n = Math.Min(key.Length, ki.ColumnCount);
        for (int i = 0; i < n; i++)
        {
            int c;
            if (i >= rec.ColumnCount)
            {
                c = key[i].Kind == StorageClass.Null ? 0 : -1;
            }
            else
            {
                c = CompareColumn(ki, i, rec, key[i]);
            }

            if (c != 0)
            {
                return ki.Descending != null && ki.Descending[i] ? -c : c;
            }
        }

        return prefixResult;
    }

    /// <summary>Compares the first <c>ki.ColumnCount</c> columns of two records.</summary>
    public static int Compare(KeyInfo ki, RecordView a, RecordView b)
    {
        int n = ki.ColumnCount;
        for (int i = 0; i < n; i++)
        {
            StorageClass ca = i < a.ColumnCount ? a.ClassOf(i) : StorageClass.Null;
            StorageClass cb = i < b.ColumnCount ? b.ClassOf(i) : StorageClass.Null;
            int c;
            if (ca == StorageClass.Null || cb == StorageClass.Null)
            {
                c = ((ca == StorageClass.Null) ? 0 : 1) - ((cb == StorageClass.Null) ? 0 : 1);
            }
            else
            {
                c = Order(ca, cb, a, i, b, i, ki.Collations[i]);
            }

            if (c != 0)
            {
                return ki.Descending != null && ki.Descending[i] ? -c : c;
            }
        }

        return 0;
    }

    private static int CompareColumn(KeyInfo ki, int i, RecordView rec, in Mem m)
    {
        StorageClass ca = rec.ClassOf(i);
        StorageClass cb = m.Kind;
        if (ca == StorageClass.Null || cb == StorageClass.Null)
        {
            return ((ca == StorageClass.Null) ? 0 : 1) - ((cb == StorageClass.Null) ? 0 : 1);
        }

        int ra = Rank(ca), rb = Rank(cb);
        if (ra != rb)
        {
            return ra < rb ? -1 : 1;
        }

        switch (ra)
        {
            case 1:
                if (ca == StorageClass.Integral && cb == StorageClass.Integral)
                {
                    return rec.GetInt64(i).CompareTo(m.IntValue);
                }

                if (ca == StorageClass.Real && cb == StorageClass.Real)
                {
                    return rec.GetDouble(i).CompareTo(m.RealValue);
                }

                return ca == StorageClass.Integral ? IntReal(rec.GetInt64(i), m.RealValue) : -IntReal(m.IntValue, rec.GetDouble(i));
            case 2:
                return (ki.Collations[i] ?? Collations.Binary)(rec.GetBytes(i), m.Bytes);
            default:
                return rec.GetBytes(i).SequenceCompareTo(m.Bytes);
        }
    }

    private static int Rank(StorageClass c) => c switch
    {
        StorageClass.Integral or StorageClass.Real => 1,
        StorageClass.Text => 2,
        _ => 3,
    };

    private static int Order(StorageClass ca, StorageClass cb, RecordView a, int i, RecordView b, int j, CollationFunction? coll)
    {
        int ra = Rank(ca), rb = Rank(cb);
        if (ra != rb)
        {
            return ra < rb ? -1 : 1;
        }

        switch (ra)
        {
            case 1:
                if (ca == StorageClass.Integral && cb == StorageClass.Integral)
                {
                    return a.GetInt64(i).CompareTo(b.GetInt64(j));
                }

                if (ca == StorageClass.Real && cb == StorageClass.Real)
                {
                    return a.GetDouble(i).CompareTo(b.GetDouble(j));
                }

                return ca == StorageClass.Integral ? IntReal(a.GetInt64(i), b.GetDouble(j)) : -IntReal(b.GetInt64(j), a.GetDouble(i));
            case 2:
                return (coll ?? Collations.Binary)(a.GetBytes(i), b.GetBytes(j));
            default:
                return a.GetBytes(i).SequenceCompareTo(b.GetBytes(j));
        }
    }

    /// <summary>Exact comparison of an integer with a float, without losing the low bits of large integers.</summary>
    public static int IntReal(long i, double r)
    {
        if (double.IsNaN(r))
        {
            return 1;
        }

        if (r >= 9223372036854775808.0)
        {
            return -1;
        }

        if (r < -9223372036854775808.0)
        {
            return 1;
        }

        long ri = (long)r;
        if (i < ri)
        {
            return -1;
        }

        if (i > ri)
        {
            return 1;
        }

        double frac = r - ri;
        return frac > 0 ? -1 : frac < 0 ? 1 : 0;
    }
}
