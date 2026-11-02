using System.Buffers.Binary;

namespace RecursiveAI.OneLite.Storage.Record;

/// <summary>A value's storage class (datatype3 §1).</summary>
public enum StorageClass : byte
{
    /// <summary>NULL.</summary>
    Null = 0,

    /// <summary>A signed integer of 1 to 8 bytes.</summary>
    Integral = 1,

    /// <summary>An 8-byte IEEE float.</summary>
    Real = 2,

    /// <summary>Text in the database encoding.</summary>
    Text = 3,

    /// <summary>A blob.</summary>
    Blob = 4,
}

/// <summary>
/// One value to be written into a record or compared with a stored one. For text and blob values the bytes are
/// <c>Buffer[Offset .. Offset + Length]</c>; text is already in the database encoding.
/// </summary>
public struct Mem
{
    /// <summary>The storage class.</summary>
    public StorageClass Kind { get; set; }

    /// <summary>The value when <see cref="Kind"/> is <see cref="StorageClass.Integral"/>.</summary>
    public long IntValue { get; set; }

    /// <summary>The value when <see cref="Kind"/> is <see cref="StorageClass.Real"/>.</summary>
    public double RealValue { get; set; }

    /// <summary>The bytes of a text or blob value.</summary>
    public byte[]? Buffer { get; set; }

    /// <summary>Start of the value in <see cref="Buffer"/>.</summary>
    public int Offset { get; set; }

    /// <summary>Length of the value in <see cref="Buffer"/>.</summary>
    public int Length { get; set; }

    /// <summary>The application-defined subtype of a value passed between SQL functions (the JSON functions mark their results with it); zero otherwise.</summary>
    internal byte Subtype { get; set; }

    /// <summary>The bytes of a text or blob value.</summary>
    public readonly ReadOnlySpan<byte> Bytes => Buffer == null ? default : new ReadOnlySpan<byte>(Buffer, Offset, Length);

    /// <summary>A NULL.</summary>
    public static Mem Null => default;

    /// <summary>An integer.</summary>
    public static Mem FromInt(long v) => new() { Kind = StorageClass.Integral, IntValue = v };

    /// <summary>A float.</summary>
    public static Mem FromReal(double v) => new() { Kind = StorageClass.Real, RealValue = v };

    /// <summary>A blob over <paramref name="b"/>.</summary>
    public static Mem FromBlob(byte[] b) => new() { Kind = StorageClass.Blob, Buffer = b, Length = b.Length };

    /// <summary>Text whose bytes are already in the database encoding.</summary>
    public static Mem FromTextBytes(byte[] b) => new() { Kind = StorageClass.Text, Buffer = b, Length = b.Length };

    /// <summary>Text from a string, encoded as <paramref name="enc"/>.</summary>
    public static Mem FromText(string s, TextEncoding enc) => FromTextBytes(TextCodec.Encode(s, enc));
}

/// <summary>Serial types (fileformat2 §2.1): the per-column code in a record header.</summary>
public static class SerialType
{
    /// <summary>Largest content size this reader accepts for one column (SQLite's default string and blob limit).</summary>
    public const long MaxContent = 1_000_000_000;

    /// <summary>The storage class a serial type stands for; types 10 and 11 are reserved and have none.</summary>
    public static bool TryGetClass(ulong t, out StorageClass c)
    {
        switch (t)
        {
            case 0: c = StorageClass.Null; return true;
            case 7: c = StorageClass.Real; return true;
            case 10:
            case 11: c = StorageClass.Null; return false;
            case >= 12: c = (t & 1) == 0 ? StorageClass.Blob : StorageClass.Text; return true;
            default: c = StorageClass.Integral; return true; // 1-6, 8, 9
        }
    }

    /// <summary>The number of content bytes for serial type <paramref name="t"/>, or -1 if the type is reserved or the size exceeds <see cref="MaxContent"/>.</summary>
    public static long ContentSize(ulong t)
    {
        switch (t)
        {
            case 0: case 8: case 9: return 0;
            case 1: return 1;
            case 2: return 2;
            case 3: return 3;
            case 4: return 4;
            case 5: return 6;
            case 6: case 7: return 8;
            case 10: case 11: return -1;
            default:
                if (t > 2UL * (ulong)MaxContent + 13)
                {
                    return -1;
                }

                return (long)((t - 12) / 2);
        }
    }

    /// <summary>The smallest integer serial type for <paramref name="v"/> (8 and 9 for the constants 0 and 1).</summary>
    public static int ForInteger(long v)
    {
        if (v == 0)
        {
            return 8;
        }

        if (v == 1)
        {
            return 9;
        }

        if (v >= -128 && v <= 127)
        {
            return 1;
        }

        if (v >= -32768 && v <= 32767)
        {
            return 2;
        }

        if (v >= -8388608 && v <= 8388607)
        {
            return 3;
        }

        if (v >= int.MinValue && v <= int.MaxValue)
        {
            return 4;
        }

        if (v >= -140737488355328L && v <= 140737488355327L)
        {
            return 5;
        }

        return 6;
    }

    /// <summary>Reads a big-endian two's-complement integer of <paramref name="n"/> bytes (1 to 8).</summary>
    public static long ReadInt(ReadOnlySpan<byte> src, int n)
    {
        long v = (sbyte)src[0];
        for (int i = 1; i < n; i++)
        {
            v = (v << 8) | src[i];
        }

        return v;
    }
}

/// <summary>A decoded view of one record (fileformat2 §2.1) over caller-owned bytes. Reusable: <see cref="Parse"/> again for the next record; after warm-up it allocates nothing.</summary>
public sealed class RecordView
{
    private ulong[] _types = new ulong[16];
    private int[] _offsets = new int[16];
    private int _count;
    private byte[]? _array;
    private int _base;
    private int _length;

    /// <summary>Number of columns in the record header.</summary>
    public int ColumnCount => _count;

    /// <summary>Total size of the record in bytes (header plus bodies).</summary>
    public int Size => _length;

    /// <summary>The bytes of the record that was last parsed (empty before the first parse).</summary>
    public ReadOnlySpan<byte> Bytes => _array is null ? default : new ReadOnlySpan<byte>(_array, _base, _length);

    /// <summary>
    /// Parses the header of <paramref name="owner"/>, which must live in <paramref name="owner"/> at <paramref name="offset"/>.
    /// Validates the header size, every serial type and that all bodies lie inside the record.
    /// </summary>
    /// <returns>False if the record is malformed (the BTree layer reports SQLITE_CORRUPT).</returns>
    public bool Parse(byte[] owner, int offset, int length)
    {
        _array = owner;
        _base = offset;
        _length = length;
        _count = 0;
        ReadOnlySpan<byte> rec = new(owner, offset, length);
        int hn = Varint.Read(rec, out ulong hdrSize);
        if (hn == 0 || hdrSize < (ulong)hn || hdrSize > (ulong)length)
        {
            return false;
        }

        int hdrEnd = (int)hdrSize;
        long body = hdrEnd;
        int p = hn;
        while (p < hdrEnd)
        {
            int n = Varint.Read(rec.Slice(p, hdrEnd - p), out ulong t);
            if (n == 0)
            {
                return false;
            }

            p += n;
            long size = SerialType.ContentSize(t);
            if (size < 0)
            {
                return false;
            }

            if (_count == _types.Length)
            {
                Array.Resize(ref _types, _count * 2);
                Array.Resize(ref _offsets, _count * 2);
            }

            _types[_count] = t;
            _offsets[_count] = (int)Math.Min(body, int.MaxValue);
            _count++;
            body += size;
            if (body > length)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The serial type of column <paramref name="i"/>.</summary>
    public ulong SerialTypeOf(int i) => _types[i];

    /// <summary>The storage class of column <paramref name="i"/>.</summary>
    public StorageClass ClassOf(int i)
    {
        SerialType.TryGetClass(_types[i], out StorageClass c);
        return c;
    }

    /// <summary>The integer value of column <paramref name="i"/> (serial types 1-6, 8, 9); 0 for other types.</summary>
    public long GetInt64(int i)
    {
        ulong t = _types[i];
        switch (t)
        {
            case 8: return 0;
            case 9: return 1;
            case >= 1 and <= 6:
                int n = (int)SerialType.ContentSize(t);
                return SerialType.ReadInt(new ReadOnlySpan<byte>(_array!, _base + _offsets[i], n), n);
            case 7: return (long)GetDouble(i);
            default: return 0;
        }
    }

    /// <summary>The float value of column <paramref name="i"/>; integers convert.</summary>
    public double GetDouble(int i)
    {
        if (_types[i] == 7)
        {
            ulong bits = BinaryPrimitives.ReadUInt64BigEndian(new ReadOnlySpan<byte>(_array!, _base + _offsets[i], 8));
            return BitConverter.UInt64BitsToDouble(bits);
        }

        return GetInt64(i);
    }

    /// <summary>The raw content bytes of a text or blob column (text in the database encoding).</summary>
    public ReadOnlySpan<byte> GetBytes(int i)
    {
        ulong t = _types[i];
        if (t < 12)
        {
            return default;
        }

        return new ReadOnlySpan<byte>(_array!, _base + _offsets[i], (int)((t - 12) / 2));
    }

    /// <summary>Copies column <paramref name="i"/> into <paramref name="m"/>, referencing the record's bytes for text and blobs.</summary>
    public void Load(int i, ref Mem m)
    {
        StorageClass c = ClassOf(i);
        m.Kind = c;
        m.Subtype = 0;
        switch (c)
        {
            case StorageClass.Integral: m.IntValue = GetInt64(i); break;
            case StorageClass.Real: m.RealValue = GetDouble(i); break;
            case StorageClass.Text:
            case StorageClass.Blob:
                m.Buffer = _array;
                m.Offset = _base + _offsets[i];
                m.Length = (int)((_types[i] - 12) / 2);
                break;
        }
    }
}

/// <summary>Builds records (fileformat2 §2.1).</summary>
public static class RecordWriter
{
    /// <summary>The serial type for one value.</summary>
    public static ulong TypeOf(in Mem m)
    {
        switch (m.Kind)
        {
            case StorageClass.Null: return 0;
            case StorageClass.Integral: return (ulong)SerialType.ForInteger(m.IntValue);
            case StorageClass.Real: return 7;
            case StorageClass.Blob: return 12 + 2UL * (ulong)m.Length;
            default: return 13 + 2UL * (ulong)m.Length;
        }
    }

    /// <summary>The encoded size of a record holding <paramref name="values"/>.</summary>
    public static int Size(ReadOnlySpan<Mem> values)
    {
        long body = 0;
        int hdr = 0;
        foreach (ref readonly Mem m in values)
        {
            ulong t = TypeOf(in m);
            hdr += Varint.Length(t);
            body += SerialType.ContentSize(t);
        }

        // The header size varint counts itself; it is one byte unless the header is 126 bytes or more.
        int hs = hdr + 1;
        if (hs > 127)
        {
            hs = hdr + Varint.Length((ulong)hdr + 2);
            hs = hdr + Varint.Length((ulong)hs);
        }

        long total = hs + body;
        if (total > int.MaxValue)
        {
            throw new ArgumentException("record too large");
        }

        return (int)total;
    }

    /// <summary>Writes the record into <paramref name="dst"/> (at least <see cref="Size"/> bytes) and returns its size.</summary>
    public static int Write(Span<byte> dst, ReadOnlySpan<Mem> values)
    {
        int hdr = 0;
        foreach (ref readonly Mem m in values)
        {
            hdr += Varint.Length(TypeOf(in m));
        }

        int hs = hdr + 1;
        if (hs > 127)
        {
            hs = hdr + Varint.Length((ulong)hdr + 2);
            hs = hdr + Varint.Length((ulong)hs);
        }

        int p = Varint.Write(dst, (ulong)hs);
        foreach (ref readonly Mem m in values)
        {
            p += Varint.Write(dst[p..], TypeOf(in m));
        }

        int q = hs;
        foreach (ref readonly Mem m in values)
        {
            switch (m.Kind)
            {
                case StorageClass.Integral:
                    {
                        int st = SerialType.ForInteger(m.IntValue);
                        int n = (int)SerialType.ContentSize((ulong)st);
                        for (int i = 0; i < n; i++)
                        {
                            dst[q + i] = (byte)(m.IntValue >> (8 * (n - 1 - i)));
                        }

                        q += n;
                        break;
                    }
                case StorageClass.Real:
                    BinaryPrimitives.WriteUInt64BigEndian(dst[q..], BitConverter.DoubleToUInt64Bits(m.RealValue));
                    q += 8;
                    break;
                case StorageClass.Text:
                case StorageClass.Blob:
                    m.Bytes.CopyTo(dst[q..]);
                    q += m.Length;
                    break;
            }
        }

        return q;
    }

    /// <summary>Allocates and returns the record.</summary>
    public static byte[] ToArray(ReadOnlySpan<Mem> values)
    {
        var b = new byte[Size(values)];
        Write(b, values);
        return b;
    }
}
