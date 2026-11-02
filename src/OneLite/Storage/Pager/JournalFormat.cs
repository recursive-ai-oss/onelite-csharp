using System.Buffers.Binary;

namespace RecursiveAI.OneLite.Storage.Pager;

/// <summary>
/// The rollback journal's on-disk format (fileformat2 "The Rollback Journal", plus the super-journal trailer of atomiccommit §5 whose
/// layout is documented in <c>docs/durability.md</c>).
/// </summary>
internal static class JournalFormat
{
    /// <summary>The journal header magic.</summary>
    public static ReadOnlySpan<byte> Magic => [0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7];

    /// <summary>Bytes of a header that carry data (the rest of the sector is zero).</summary>
    public const int HeaderFields = 28;

    /// <summary>Record count meaning "all records to the end of the file".</summary>
    public const uint CountToEnd = 0xFFFFFFFF;

    /// <summary>The longest super-journal path accepted when reading a trailer.</summary>
    public const int MaxSuperJournalName = 4096;

    /// <summary>A parsed journal header.</summary>
    internal readonly struct Header
    {
        public uint RecordCount { get; init; }

        public uint Nonce { get; init; }

        public uint InitialPages { get; init; }

        public int SectorSize { get; init; }

        public int PageSize { get; init; }
    }

    /// <summary>Parses a header; returns false if the magic is wrong or any field is out of range (which ends playback, not an error).</summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> bytes, out Header header)
    {
        header = default;
        if (bytes.Length < HeaderFields || !bytes[..8].SequenceEqual(Magic))
        {
            return false;
        }

        uint sector = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]);
        uint pageSize = BinaryPrimitives.ReadUInt32BigEndian(bytes[24..]);
        if (!IsPow2InRange(sector) || !IsPow2InRange(pageSize))
        {
            return false;
        }

        header = new Header
        {
            RecordCount = BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]),
            Nonce = BinaryPrimitives.ReadUInt32BigEndian(bytes[12..]),
            InitialPages = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]),
            SectorSize = (int)sector,
            PageSize = (int)pageSize,
        };
        return true;
    }

    private static bool IsPow2InRange(uint v) => v >= 512 && v <= 65536 && (v & (v - 1)) == 0;

    /// <summary>Writes a header into the first bytes of <paramref name="dst"/>, which must be one sector long and zeroed.</summary>
    public static void WriteHeader(Span<byte> dst, bool withMagic, uint recordCount, uint nonce, uint initialPages, int sectorSize, int pageSize)
    {
        if (withMagic)
        {
            Magic.CopyTo(dst);
        }

        BinaryPrimitives.WriteUInt32BigEndian(dst[8..], recordCount);
        BinaryPrimitives.WriteUInt32BigEndian(dst[12..], nonce);
        BinaryPrimitives.WriteUInt32BigEndian(dst[16..], initialPages);
        BinaryPrimitives.WriteUInt32BigEndian(dst[20..], (uint)sectorSize);
        BinaryPrimitives.WriteUInt32BigEndian(dst[24..], (uint)pageSize);
    }

    /// <summary>fileformat2: the checksum is the nonce plus every 200th byte of the page, counting down from <c>pageSize - 200</c>.</summary>
    public static uint Checksum(uint nonce, ReadOnlySpan<byte> page)
    {
        uint sum = nonce;
        for (int x = page.Length - 200; x >= 0; x -= 200)
        {
            sum += page[x];
        }

        return sum;
    }

    /// <summary>Size of the super-journal trailer for a name of <paramref name="nameLength"/> bytes.</summary>
    public static int TrailerSize(int nameLength) => 4 + nameLength + 4 + 4 + 8;

    /// <summary>Writes the trailer: lock-byte page number, name, name length, sum of name bytes, magic.</summary>
    public static void WriteTrailer(Span<byte> dst, uint lockBytePage, ReadOnlySpan<byte> name)
    {
        BinaryPrimitives.WriteUInt32BigEndian(dst, lockBytePage);
        name.CopyTo(dst[4..]);
        uint sum = 0;
        foreach (byte b in name)
        {
            sum += b;
        }

        BinaryPrimitives.WriteUInt32BigEndian(dst[(4 + name.Length)..], (uint)name.Length);
        BinaryPrimitives.WriteUInt32BigEndian(dst[(8 + name.Length)..], sum);
        Magic.CopyTo(dst[(12 + name.Length)..]);
    }

    /// <summary>
    /// Reads the last 16 bytes of a journal. If they are a trailer's length, checksum and magic, returns the name length (the whole trailer
    /// is then <see cref="TrailerSize"/> bytes) and the expected name checksum.
    /// </summary>
    public static bool TryParseTrailerTail(ReadOnlySpan<byte> last16, long fileSize, out int nameLength, out uint nameSum)
    {
        nameLength = 0;
        nameSum = 0;
        if (last16.Length < 16 || !last16[8..].SequenceEqual(Magic))
        {
            return false;
        }

        uint len = BinaryPrimitives.ReadUInt32BigEndian(last16);
        if (len == 0 || len > MaxSuperJournalName || TrailerSize((int)len) > fileSize)
        {
            return false;
        }

        nameLength = (int)len;
        nameSum = BinaryPrimitives.ReadUInt32BigEndian(last16[4..]);
        return true;
    }
}
