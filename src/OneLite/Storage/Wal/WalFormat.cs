using System.Buffers.Binary;

namespace RecursiveAI.OneLite.Storage.Wal;

/// <summary>
/// The on-disk format of the write-ahead log (sqlite.org fileformat2 §4 and walformat): a 32-byte header, then frames of a 24-byte header plus one
/// page. All header integers are big-endian; frame and header checksums run over 32-bit words in the byte order the magic number announces.
/// </summary>
internal static class WalFormat
{
    /// <summary>The WAL header is 32 bytes.</summary>
    public const int HeaderSize = 32;

    /// <summary>Each frame starts with a 24-byte header.</summary>
    public const int FrameHeaderSize = 24;

    /// <summary>Magic number when checksums are computed over little-endian words.</summary>
    public const uint MagicLittleEndian = 0x377f0682;

    /// <summary>Magic number when checksums are computed over big-endian words.</summary>
    public const uint MagicBigEndian = 0x377f0683;

    /// <summary>The file format version, 3007000, in both the WAL header and the wal-index header.</summary>
    public const uint FormatVersion = 3007000;

    /// <summary>The byte offset of frame <paramref name="frame"/> (numbered from 1) in the WAL file.</summary>
    public static long FrameOffset(uint frame, int pageSize) => HeaderSize + (long)(frame - 1) * (pageSize + FrameHeaderSize);

    /// <summary>
    /// fileformat2 §4.2: s0 += x(i) + s1; s1 += x(i+1) + s0 over pairs of 32-bit words. The input length must be a multiple of 8.
    /// </summary>
    public static void Checksum(bool bigEndian, ReadOnlySpan<byte> data, ref uint s0, ref uint s1)
    {
        if ((data.Length & 7) != 0)
        {
            throw new ArgumentException("A WAL checksum covers a multiple of 8 bytes.", nameof(data));
        }

        uint a = s0;
        uint b = s1;
        if (bigEndian)
        {
            for (int i = 0; i < data.Length; i += 8)
            {
                a += BinaryPrimitives.ReadUInt32BigEndian(data[i..]) + b;
                b += BinaryPrimitives.ReadUInt32BigEndian(data[(i + 4)..]) + a;
            }
        }
        else
        {
            for (int i = 0; i < data.Length; i += 8)
            {
                a += BinaryPrimitives.ReadUInt32LittleEndian(data[i..]) + b;
                b += BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 4)..]) + a;
            }
        }

        s0 = a;
        s1 = b;
    }

    /// <summary>Reads a 32-bit word in the byte order of this machine (the wal-index and the salts it copies use it).</summary>
    public static uint ReadNative(ReadOnlySpan<byte> s) => BitConverter.IsLittleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);

    /// <summary>Writes a 32-bit word in the byte order of this machine.</summary>
    public static void WriteNative(Span<byte> s, uint v)
    {
        if (BitConverter.IsLittleEndian)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(s, v);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(s, v);
        }
    }

    /// <summary>True if <paramref name="pageSize"/> is a power of two from 512 to 65536.</summary>
    public static bool IsValidPageSize(int pageSize) => pageSize is >= 512 and <= 65536 && (pageSize & (pageSize - 1)) == 0;
}

/// <summary>The 32-byte WAL file header (fileformat2 §4.1).</summary>
internal struct WalHeader
{
    /// <summary>0x377f0682 or 0x377f0683.</summary>
    public uint Magic;

    /// <summary>Always 3007000.</summary>
    public uint Version;

    /// <summary>Database page size.</summary>
    public int PageSize;

    /// <summary>Checkpoint sequence number: one more after every WAL restart.</summary>
    public uint CheckpointSequence;

    /// <summary>Salt-1: incremented at every restart.</summary>
    public uint Salt1;

    /// <summary>Salt-2: random at every restart.</summary>
    public uint Salt2;

    /// <summary>Checksum over the first 24 bytes.</summary>
    public uint Checksum1;

    /// <summary>Second half of that checksum.</summary>
    public uint Checksum2;

    /// <summary>True if checksums run over big-endian words.</summary>
    public readonly bool BigEndianChecksums => (Magic & 1) != 0;

    /// <summary>Parses and validates a header: magic, version, page size and checksum. Returns false for anything else.</summary>
    public static bool TryParse(ReadOnlySpan<byte> s, out WalHeader h)
    {
        h = default;
        if (s.Length < WalFormat.HeaderSize)
        {
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32BigEndian(s);
        if (magic != WalFormat.MagicLittleEndian && magic != WalFormat.MagicBigEndian)
        {
            return false;
        }

        h.Magic = magic;
        h.Version = BinaryPrimitives.ReadUInt32BigEndian(s[4..]);
        uint ps = BinaryPrimitives.ReadUInt32BigEndian(s[8..]);
        h.CheckpointSequence = BinaryPrimitives.ReadUInt32BigEndian(s[12..]);
        h.Salt1 = BinaryPrimitives.ReadUInt32BigEndian(s[16..]);
        h.Salt2 = BinaryPrimitives.ReadUInt32BigEndian(s[20..]);
        h.Checksum1 = BinaryPrimitives.ReadUInt32BigEndian(s[24..]);
        h.Checksum2 = BinaryPrimitives.ReadUInt32BigEndian(s[28..]);
        if (h.Version != WalFormat.FormatVersion || ps > 65536 || !WalFormat.IsValidPageSize((int)ps))
        {
            return false;
        }

        h.PageSize = (int)ps;
        uint s0 = 0;
        uint s1 = 0;
        WalFormat.Checksum(h.BigEndianChecksums, s[..24], ref s0, ref s1);
        return s0 == h.Checksum1 && s1 == h.Checksum2;
    }

    /// <summary>Writes the header (computing its checksum, which is also stored in the struct).</summary>
    public void Write(Span<byte> s)
    {
        BinaryPrimitives.WriteUInt32BigEndian(s, Magic);
        BinaryPrimitives.WriteUInt32BigEndian(s[4..], WalFormat.FormatVersion);
        BinaryPrimitives.WriteUInt32BigEndian(s[8..], (uint)PageSize);
        BinaryPrimitives.WriteUInt32BigEndian(s[12..], CheckpointSequence);
        BinaryPrimitives.WriteUInt32BigEndian(s[16..], Salt1);
        BinaryPrimitives.WriteUInt32BigEndian(s[20..], Salt2);
        uint s0 = 0;
        uint s1 = 0;
        WalFormat.Checksum(BigEndianChecksums, s[..24], ref s0, ref s1);
        Checksum1 = s0;
        Checksum2 = s1;
        BinaryPrimitives.WriteUInt32BigEndian(s[24..], s0);
        BinaryPrimitives.WriteUInt32BigEndian(s[28..], s1);
    }
}
