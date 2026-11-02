using System.Buffers.Binary;

namespace RecursiveAI.OneLite.Storage.Pager;

/// <summary>
/// The 100-byte database header at the start of page 1 (fileformat2 §1.3), parsed and checked. Every field is validated against its
/// documented range before anything is derived from it. All multi-byte fields are big endian.
/// </summary>
public readonly struct DatabaseHeader
{
    /// <summary>Size of the header in bytes.</summary>
    public const int Size = 100;

    /// <summary>Offset of the file change counter.</summary>
    public const int ChangeCounterOffset = 24;

    /// <summary>Offset of the database size in pages.</summary>
    public const int DatabaseSizeOffset = 28;

    /// <summary>Offset of the first freelist trunk page.</summary>
    public const int FreelistTrunkOffset = 32;

    /// <summary>Offset of the total number of freelist pages.</summary>
    public const int FreelistCountOffset = 36;

    /// <summary>Offset of the schema cookie.</summary>
    public const int SchemaCookieOffset = 40;

    /// <summary>Offset of the largest root B-tree page (non-zero means auto-vacuum).</summary>
    public const int LargestRootOffset = 52;

    /// <summary>Offset of the text encoding.</summary>
    public const int TextEncodingOffset = 56;

    /// <summary>Offset of the incremental-vacuum flag.</summary>
    public const int IncrementalVacuumOffset = 64;

    /// <summary>Offset of the "version-valid-for" number.</summary>
    public const int VersionValidForOffset = 92;

    /// <summary>Offset of the library version number that last wrote the file.</summary>
    public const int VersionNumberOffset = 96;

    /// <summary>The 16 bytes every database starts with: "SQLite format 3" and a NUL.</summary>
    public static ReadOnlySpan<byte> Magic => "SQLite format 3\0"u8;

    /// <summary>Page size in bytes (512 to 65536, a power of two).</summary>
    public int PageSize { get; init; }

    /// <summary>File format write version: 1 for rollback journal, 2 for WAL.</summary>
    public byte WriteVersion { get; init; }

    /// <summary>File format read version: 1 for rollback journal, 2 for WAL.</summary>
    public byte ReadVersion { get; init; }

    /// <summary>Bytes reserved at the end of every page.</summary>
    public byte ReservedBytes { get; init; }

    /// <summary>The file change counter.</summary>
    public uint ChangeCounter { get; init; }

    /// <summary>The in-header size in pages; meaningful only if <see cref="SizeIsValid"/>.</summary>
    public uint DatabaseSizeInHeader { get; init; }

    /// <summary>First freelist trunk page, or 0.</summary>
    public uint FreelistTrunk { get; init; }

    /// <summary>Total freelist pages (trunks and leaves).</summary>
    public uint FreelistCount { get; init; }

    /// <summary>The schema cookie.</summary>
    public uint SchemaCookie { get; init; }

    /// <summary>The schema format number (0 to 4).</summary>
    public uint SchemaFormat { get; init; }

    /// <summary>Largest root B-tree page when auto-vacuum is on, else 0.</summary>
    public uint LargestRootPage { get; init; }

    /// <summary>The text encoding; <see cref="TextEncoding.Utf8"/> for a database that has not set one yet.</summary>
    public TextEncoding Encoding { get; init; }

    /// <summary>True when the incremental-vacuum flag (offset 64) is set: free pages are kept until an incremental vacuum, not removed at commit.</summary>
    public bool IncrementalVacuum { get; init; }

    /// <summary>True when the pointer-map pages exist (auto-vacuum).</summary>
    public bool AutoVacuum => LargestRootPage != 0;

    /// <summary>The "version-valid-for" number.</summary>
    public uint VersionValidFor { get; init; }

    /// <summary>
    /// True when the in-header size can be trusted: it is non-zero and "version-valid-for" equals the change counter
    /// (fileformat2 §1.3.8: a library that does not maintain the size field leaves the two different).
    /// </summary>
    public bool SizeIsValid => DatabaseSizeInHeader != 0 && VersionValidFor == ChangeCounter;

    /// <summary>Usable bytes per page: the page size minus the reserved bytes.</summary>
    public int UsableSize => PageSize - ReservedBytes;

    /// <summary>Decodes a page size field: 1 means 65536; otherwise a power of two from 512 to 32768.</summary>
    /// <param name="raw">The 16-bit field.</param>
    /// <returns>The page size, or 0 if the field is out of range.</returns>
    public static int DecodePageSize(int raw)
    {
        if (raw == 1)
        {
            return 65536;
        }

        return raw >= 512 && raw <= 32768 && (raw & (raw - 1)) == 0 ? raw : 0;
    }

    /// <summary>Parses and validates a header.</summary>
    /// <param name="bytes">At least 100 bytes from the start of the file.</param>
    /// <returns>The header.</returns>
    /// <exception cref="OneLiteException"><c>SQLITE_NOTADB</c> if this is not a database header at all (magic, page size, payload fractions,
    /// format versions, reserved bytes), <c>SQLITE_CORRUPT</c> for out-of-range fields in an otherwise valid header.</exception>
    public static DatabaseHeader Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Size || !bytes[..16].SequenceEqual(Magic))
        {
            throw NotADb("the file does not start with the database header text");
        }

        int pageSize = DecodePageSize(BinaryPrimitives.ReadUInt16BigEndian(bytes[16..]));
        if (pageSize == 0)
        {
            throw NotADb("page size field " + BinaryPrimitives.ReadUInt16BigEndian(bytes[16..]) + " is not a power of two from 512 to 32768 or 1");
        }

        byte write = bytes[18];
        byte read = bytes[19];
        if (read > 2)
        {
            throw NotADb("read format version " + read + " is newer than this library understands");
        }

        if (bytes[21] != 64 || bytes[22] != 32 || bytes[23] != 32)
        {
            throw NotADb("payload fractions must be 64, 32, 32");
        }

        byte reserved = bytes[20];
        if (pageSize - reserved < 480)
        {
            throw NotADb("usable page size " + (pageSize - reserved) + " is below 480");
        }

        uint encoding = BinaryPrimitives.ReadUInt32BigEndian(bytes[TextEncodingOffset..]);
        if (encoding > 3)
        {
            throw Corrupt("text encoding " + encoding + " is not 1, 2 or 3");
        }

        uint format = BinaryPrimitives.ReadUInt32BigEndian(bytes[44..]);
        if (format > 4)
        {
            throw Corrupt("schema format " + format + " is not 0 to 4");
        }

        return new DatabaseHeader
        {
            PageSize = pageSize,
            WriteVersion = write,
            ReadVersion = read,
            ReservedBytes = reserved,
            ChangeCounter = BinaryPrimitives.ReadUInt32BigEndian(bytes[ChangeCounterOffset..]),
            DatabaseSizeInHeader = BinaryPrimitives.ReadUInt32BigEndian(bytes[DatabaseSizeOffset..]),
            FreelistTrunk = BinaryPrimitives.ReadUInt32BigEndian(bytes[FreelistTrunkOffset..]),
            FreelistCount = BinaryPrimitives.ReadUInt32BigEndian(bytes[FreelistCountOffset..]),
            SchemaCookie = BinaryPrimitives.ReadUInt32BigEndian(bytes[SchemaCookieOffset..]),
            SchemaFormat = format,
            LargestRootPage = BinaryPrimitives.ReadUInt32BigEndian(bytes[LargestRootOffset..]),
            Encoding = encoding == 0 ? TextEncoding.Utf8 : (TextEncoding)encoding,
            IncrementalVacuum = BinaryPrimitives.ReadUInt32BigEndian(bytes[IncrementalVacuumOffset..]) != 0,
            VersionValidFor = BinaryPrimitives.ReadUInt32BigEndian(bytes[VersionValidForOffset..]),
        };
    }

    /// <summary>
    /// Writes the header of a new, empty database into page 1, and the header of an empty table-leaf B-tree page after it
    /// (so the schema table starts out valid and empty). The rest of the page must already be zero.
    /// </summary>
    /// <param name="page">Page 1.</param>
    /// <param name="pageSize">Page size.</param>
    /// <param name="reservedBytes">Reserved bytes per page.</param>
    /// <param name="encoding">Text encoding.</param>
    /// <param name="autoVacuum">Whether to enable auto-vacuum; <paramref name="incremental"/> chooses the incremental flavour.</param>
    /// <param name="incremental">The incremental-vacuum flag.</param>
    public static void WriteNew(Span<byte> page, int pageSize, int reservedBytes, TextEncoding encoding, bool autoVacuum, bool incremental)
    {
        Magic.CopyTo(page);
        BinaryPrimitives.WriteUInt16BigEndian(page[16..], (ushort)(pageSize == 65536 ? 1 : pageSize));
        page[18] = 1;
        page[19] = 1;
        page[20] = (byte)reservedBytes;
        page[21] = 64;
        page[22] = 32;
        page[23] = 32;
        BinaryPrimitives.WriteUInt32BigEndian(page[DatabaseSizeOffset..], 1);
        BinaryPrimitives.WriteUInt32BigEndian(page[44..], 4);
        BinaryPrimitives.WriteUInt32BigEndian(page[LargestRootOffset..], autoVacuum ? 1u : 0u);
        BinaryPrimitives.WriteUInt32BigEndian(page[TextEncodingOffset..], (uint)encoding);
        BinaryPrimitives.WriteUInt32BigEndian(page[IncrementalVacuumOffset..], autoVacuum && incremental ? 1u : 0u);
        BinaryPrimitives.WriteUInt32BigEndian(page[VersionNumberOffset..], 3053004);

        // fileformat2 §1.6: an empty table-leaf page: type 13, no freeblock, zero cells, content area at the end of the page.
        page[100] = 0x0d;
        BinaryPrimitives.WriteUInt16BigEndian(page[105..], (ushort)((pageSize - reservedBytes) & 0xFFFF));
    }

    /// <summary>Reads a 32-bit header field from page 1.</summary>
    /// <param name="page1">Page 1.</param>
    /// <param name="offset">One of the offset constants.</param>
    /// <returns>The value.</returns>
    public static uint GetUInt32(ReadOnlySpan<byte> page1, int offset) => BinaryPrimitives.ReadUInt32BigEndian(page1[offset..]);

    /// <summary>Writes a 32-bit header field into page 1 (call after <c>MakeWritable</c>).</summary>
    /// <param name="page1">Page 1.</param>
    /// <param name="offset">One of the offset constants.</param>
    /// <param name="value">The value.</param>
    public static void SetUInt32(Span<byte> page1, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(page1[offset..], value);

    internal static OneLiteException NotADb(string detail) => new(ResultCode.NotADb, "file is not a database", detail);

    internal static OneLiteException Corrupt(string detail) => new(ResultCode.Corrupt, "database disk image is malformed", detail);
}
