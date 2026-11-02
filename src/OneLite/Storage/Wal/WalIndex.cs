using System.Buffers.Binary;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Storage.Wal;

/// <summary>
/// The 48-byte wal-index header (sqlite.org walformat §2.1), held as twelve 32-bit words in the machine's byte order. Bytes 12 to 15 hold
/// isInit, bigEndCksum and szPage; they are kept as one raw word and split by the accessors.
/// </summary>
internal struct WalIndexHeader : IEquatable<WalIndexHeader>
{
    public const int Words = 12;
    public const int Bytes = 48;

    public uint W0, W1, W2, W3, W4, W5, W6, W7, W8, W9, W10, W11;

    /// <summary>iVersion, always 3007000.</summary>
    public readonly uint Version => W0;

    /// <summary>iChange: incremented with each transaction.</summary>
    public readonly uint Change => W2;

    /// <summary>mxFrame: the number of valid, committed frames in the WAL.</summary>
    public readonly uint MxFrame => W4;

    /// <summary>nPage: the size of the database in pages after the last commit in the WAL (meaningful when mxFrame is not 0).</summary>
    public readonly uint NPage => W5;

    /// <summary>The first salt, exactly as the WAL header holds it (in the WAL's byte order, copied word for word).</summary>
    public readonly uint Salt0 => W8;

    /// <summary>The second salt.</summary>
    public readonly uint Salt1 => W9;

    /// <summary>isInit: 1 once the wal-index has been initialised.</summary>
    public readonly bool IsInit => BitConverter.IsLittleEndian ? (W3 & 0xFF) == 1 : (W3 >> 24) == 1;

    /// <summary>bigEndCksum: true if the WAL's checksums run over big-endian words.</summary>
    public readonly bool BigEndianChecksums => (BitConverter.IsLittleEndian ? (W3 >> 8) & 0xFF : (W3 >> 16) & 0xFF) != 0;

    /// <summary>szPage as the database page size (1 stands for 65536).</summary>
    public readonly int PageSize
    {
        get
        {
            uint raw = BitConverter.IsLittleEndian ? W3 >> 16 : W3 & 0xFFFF;
            return raw == 1 ? 65536 : (int)raw;
        }
    }

    /// <summary>Fills the identification word (isInit = 1).</summary>
    public void SetIdentity(bool bigEndianChecksums, int pageSize)
    {
        uint sz = pageSize == 65536 ? 1u : (uint)pageSize;
        uint big = bigEndianChecksums ? 1u : 0u;
        W3 = BitConverter.IsLittleEndian ? 1u | (big << 8) | (sz << 16) : (1u << 24) | (big << 16) | sz;
    }

    /// <summary>Sets the checksum of the last frame (aFrameCksum).</summary>
    public void SetFrameChecksum(uint c0, uint c1)
    {
        W6 = c0;
        W7 = c1;
    }

    /// <summary>The checksum of the last frame, first word.</summary>
    public readonly uint FrameChecksum0 => W6;

    /// <summary>The checksum of the last frame, second word.</summary>
    public readonly uint FrameChecksum1 => W7;

    /// <summary>Sets the two salts as they appear in the WAL header.</summary>
    public void SetSalts(uint s0, uint s1)
    {
        W8 = s0;
        W9 = s1;
    }

    private readonly void Sum(out uint s0, out uint s1)
    {
        // walformat: aCksum covers bytes 0..39 as native-order words, with the algorithm of fileformat2 §4.2.
        s0 = 0;
        s1 = 0;
        s0 += W0 + s1; s1 += W1 + s0;
        s0 += W2 + s1; s1 += W3 + s0;
        s0 += W4 + s1; s1 += W5 + s0;
        s0 += W6 + s1; s1 += W7 + s0;
        s0 += W8 + s1; s1 += W9 + s0;
    }

    /// <summary>Computes and stores aCksum.</summary>
    public void Seal()
    {
        Sum(out uint a, out uint b);
        W10 = a;
        W11 = b;
    }

    /// <summary>True if the stored checksum matches the first ten words.</summary>
    public readonly bool ChecksumOk
    {
        get
        {
            Sum(out uint a, out uint b);
            return a == W10 && b == W11;
        }
    }

    /// <inheritdoc />
    public readonly bool Equals(WalIndexHeader o) =>
        W0 == o.W0 && W1 == o.W1 && W2 == o.W2 && W3 == o.W3 && W4 == o.W4 && W5 == o.W5 && W6 == o.W6 && W7 == o.W7 && W8 == o.W8 && W9 == o.W9 && W10 == o.W10 && W11 == o.W11;

    /// <inheritdoc />
    public override readonly bool Equals(object? obj) => obj is WalIndexHeader h && Equals(h);

    /// <inheritdoc />
    public override readonly int GetHashCode() => HashCode.Combine(W0, W2, W4, W8, W9);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(WalIndexHeader a, WalIndexHeader b) => a.Equals(b);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(WalIndexHeader a, WalIndexHeader b) => !a.Equals(b);
}

/// <summary>
/// The wal-index (sqlite.org walformat §2): header copies, checkpoint information and the hash tables that answer "the newest frame of page P that is
/// not newer than frame M". It lives in the regions of the <c>-shm</c> file and is shared with other processes, so every multi-word value is validated
/// by checksum and every index read from shared memory is range-checked: another process, or a damaged file, can put anything there.
/// </summary>
internal sealed class WalIndex
{
    /// <summary>Marks an unused read mark (walformat §2.3.1).</summary>
    public const uint ReadMarkNotUsed = 0xFFFFFFFF;

    /// <summary>Slot number of WAL_WRITE_LOCK.</summary>
    public const int WriteLock = 0;

    /// <summary>Slot number of WAL_CKPT_LOCK.</summary>
    public const int CheckpointLock = 1;

    /// <summary>Slot number of WAL_RECOVER_LOCK.</summary>
    public const int RecoverLock = 2;

    /// <summary>Slot number of WAL_READ_LOCK(0); read lock n is <c>ReadLock0 + n</c>.</summary>
    public const int ReadLock0 = 3;

    /// <summary>The number of read locks and read marks.</summary>
    public const int ReadSlots = 5;

    private const int HeaderRegionBytes = 136;
    private const int CkptInfoOffset = 96;
    private const int ReadMarkOffset = 100;
    private const int BackfillAttemptedOffset = 128;
    private const int FirstTableEntries = 4062;
    private const int TableEntries = 4096;
    private const int HashSlots = 8192;
    private const int HashOffset = 16384;

    private ShmRegion?[] _regions = new ShmRegion?[2];

    /// <summary>Creates an index over the given shared-memory object.</summary>
    public WalIndex(IVfsShm shm, bool isHeap)
    {
        Shm = shm;
        IsHeap = isHeap;
    }

    /// <summary>The shared memory the regions come from.</summary>
    public IVfsShm Shm { get; }

    /// <summary>True when the index is private to this connection (a backend without shared memory, or a read-only connection).</summary>
    public bool IsHeap { get; }

    /// <summary>True if region <paramref name="i"/> has been mapped.</summary>
    public bool HasRegion(int i) => i < _regions.Length && _regions[i] != null;

    /// <summary>Registers a mapped region.</summary>
    public void SetRegion(int i, ShmRegion r)
    {
        if (i >= _regions.Length)
        {
            Array.Resize(ref _regions, Math.Max(i + 1, _regions.Length * 2));
        }

        _regions[i] = r;
    }

    /// <summary>Forgets every region (the mapping stays with the VFS).</summary>
    public void ClearRegions() => Array.Clear(_regions);

    /// <summary>
    /// The most frames a wal-index header may claim. A header's checksum protects against accidents, not against a crafted file, so a header that
    /// asks for millions of 32 KiB regions is treated as unusable and the index is rebuilt from the log instead (which is bounded by the real file).
    /// </summary>
    public const uint MaxFrames = 1u << 27;

    /// <summary>The number of hash tables (regions) needed to index <paramref name="frames"/> frames; at least one.</summary>
    public static int RegionsFor(uint frames) => frames <= FirstTableEntries ? 1 : 1 + (int)((frames - FirstTableEntries + TableEntries - 1) / TableEntries);

    /// <summary>The table holding frame <paramref name="frame"/> (numbered from 1).</summary>
    public static int TableOf(uint frame) => frame <= FirstTableEntries ? 0 : 1 + (int)((frame - FirstTableEntries - 1) / TableEntries);

    private static uint FirstFrameOf(int table) => table == 0 ? 1u : FirstTableEntries + 1u + (uint)(table - 1) * TableEntries;

    private static int CapacityOf(int table) => table == 0 ? FirstTableEntries : TableEntries;

    private static int EntryOffset(int table) => table == 0 ? HeaderRegionBytes : 0;

    // ---- header ---------------------------------------------------------------------------------------------------------------------

    private Memory<byte> Region0 => _regions[0]!.Memory;

    /// <summary>
    /// Reads the header: both copies must be equal, checksummed, initialised and of the supported version. Returns false if they are not (the caller
    /// retries, then recovers).
    /// </summary>
    public bool TryReadHeader(out WalIndexHeader h)
    {
        h = default;
        if (!HasRegion(0))
        {
            return false;
        }

        Memory<byte> m = Region0;
        ReadCopy(m, 0, out WalIndexHeader a);
        ShmAccessor.Barrier();
        ReadCopy(m, WalIndexHeader.Bytes, out WalIndexHeader b);
        if (a != b || !a.IsInit || a.Version != WalFormat.FormatVersion || !a.ChecksumOk || !WalFormat.IsValidPageSize(a.PageSize) || a.MxFrame > MaxFrames)
        {
            return false;
        }

        h = a;
        return true;
    }

    private static void ReadCopy(Memory<byte> m, int at, out WalIndexHeader h)
    {
        h = default;
        h.W0 = ShmAccessor.ReadUInt32(m, at);
        h.W1 = ShmAccessor.ReadUInt32(m, at + 4);
        h.W2 = ShmAccessor.ReadUInt32(m, at + 8);
        h.W3 = ShmAccessor.ReadUInt32(m, at + 12);
        h.W4 = ShmAccessor.ReadUInt32(m, at + 16);
        h.W5 = ShmAccessor.ReadUInt32(m, at + 20);
        h.W6 = ShmAccessor.ReadUInt32(m, at + 24);
        h.W7 = ShmAccessor.ReadUInt32(m, at + 28);
        h.W8 = ShmAccessor.ReadUInt32(m, at + 32);
        h.W9 = ShmAccessor.ReadUInt32(m, at + 36);
        h.W10 = ShmAccessor.ReadUInt32(m, at + 40);
        h.W11 = ShmAccessor.ReadUInt32(m, at + 44);
    }

    private static void WriteCopy(Memory<byte> m, int at, in WalIndexHeader h)
    {
        ShmAccessor.WriteUInt32(m, at, h.W0);
        ShmAccessor.WriteUInt32(m, at + 4, h.W1);
        ShmAccessor.WriteUInt32(m, at + 8, h.W2);
        ShmAccessor.WriteUInt32(m, at + 12, h.W3);
        ShmAccessor.WriteUInt32(m, at + 16, h.W4);
        ShmAccessor.WriteUInt32(m, at + 20, h.W5);
        ShmAccessor.WriteUInt32(m, at + 24, h.W6);
        ShmAccessor.WriteUInt32(m, at + 28, h.W7);
        ShmAccessor.WriteUInt32(m, at + 32, h.W8);
        ShmAccessor.WriteUInt32(m, at + 36, h.W9);
        ShmAccessor.WriteUInt32(m, at + 40, h.W10);
        ShmAccessor.WriteUInt32(m, at + 44, h.W11);
    }

    /// <summary>
    /// Publishes a header: the second copy first, then the first, with barriers around them. A reader that sees two equal, checksummed copies has a
    /// complete header; any other interleaving makes it retry. The header is sealed here.
    /// </summary>
    public void WriteHeader(ref WalIndexHeader h)
    {
        h.Seal();
        Memory<byte> m = Region0;
        ShmAccessor.Barrier();
        WriteCopy(m, WalIndexHeader.Bytes, h);
        ShmAccessor.Barrier();
        WriteCopy(m, 0, h);
        ShmAccessor.Barrier();
    }

    /// <summary>Zeroes both header copies, so that readers find an uninitialised wal-index and recover.</summary>
    public void InvalidateHeader()
    {
        Memory<byte> m = Region0;
        for (int i = 0; i < 2 * WalIndexHeader.Bytes; i += 4)
        {
            ShmAccessor.WriteUInt32(m, i, 0);
        }

        ShmAccessor.Barrier();
    }

    // ---- checkpoint information and read marks ------------------------------------------------------------------------------

    /// <summary>nBackfill: the number of frames already copied into the database. Only grows, except at a WAL restart.</summary>
    public uint NBackfill
    {
        get => ShmAccessor.ReadUInt32(Region0, CkptInfoOffset);
        set => ShmAccessor.WriteUInt32(Region0, CkptInfoOffset, value);
    }

    /// <summary>nBackfillAttempted: the number of frames a checkpoint has started to copy.</summary>
    public uint NBackfillAttempted
    {
        get => ShmAccessor.ReadUInt32(Region0, BackfillAttemptedOffset);
        set => ShmAccessor.WriteUInt32(Region0, BackfillAttemptedOffset, value);
    }

    /// <summary>Reads read-mark <paramref name="i"/> (0 to 4).</summary>
    public uint ReadMark(int i) => ShmAccessor.ReadUInt32(Region0, ReadMarkOffset + 4 * i);

    /// <summary>Writes read-mark <paramref name="i"/>; the caller holds the matching read lock exclusively.</summary>
    public void SetReadMark(int i, uint value) => ShmAccessor.WriteUInt32(Region0, ReadMarkOffset + 4 * i, value);

    /// <summary>
    /// Resets the checkpoint information the way a recovery or a WAL restart does (walformat §3): nothing backfilled, mark 0 is zero, mark 1 is
    /// <paramref name="mark1"/>, the others unused.
    /// </summary>
    public void ResetCheckpointInfo(uint mark1)
    {
        NBackfill = 0;
        SetReadMark(0, 0);
        SetReadMark(1, mark1);
        for (int i = 2; i < ReadSlots; i++)
        {
            SetReadMark(i, ReadMarkNotUsed);
        }

        NBackfillAttempted = 0;
    }

    // ---- hash tables --------------------------------------------------------------------------------------------------------------

    private static int HashOf(uint pgno) => (int)((pgno * 383u) & (HashSlots - 1));

    private static ushort ReadSlot(ReadOnlySpan<byte> s, int slot) =>
        BitConverter.IsLittleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(s[(HashOffset + 2 * slot)..]) : BinaryPrimitives.ReadUInt16BigEndian(s[(HashOffset + 2 * slot)..]);

    private static void WriteSlot(Span<byte> s, int slot, ushort v)
    {
        if (BitConverter.IsLittleEndian)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(s[(HashOffset + 2 * slot)..], v);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(s[(HashOffset + 2 * slot)..], v);
        }
    }

    /// <summary>
    /// FindFrame(P, M) of walformat §2.2: the largest frame number not above <paramref name="maxFrame"/> that holds page <paramref name="pgno"/>, or 0.
    /// Allocation-free. Entries of the shared tables are range-checked: a damaged or hostile table yields "not found", never an exception or a loop.
    /// </summary>
    public uint Find(uint pgno, uint maxFrame)
    {
        if (maxFrame == 0)
        {
            return 0;
        }

        for (int t = TableOf(maxFrame); t >= 0; t--)
        {
            if (!HasRegion(t))
            {
                continue;
            }

            Memory<byte> m = _regions[t]!.Memory;
            ReadOnlySpan<byte> s = m.Span;
            uint first = FirstFrameOf(t);
            int cap = CapacityOf(t);
            int eo = EntryOffset(t);
            uint best = 0;
            int h = HashOf(pgno);
            for (int probes = 0; probes < HashSlots; probes++)
            {
                int local = ReadSlot(s, h);
                if (local == 0)
                {
                    break;
                }

                if (local <= cap)
                {
                    uint frame = first + (uint)local - 1;
                    if (frame <= maxFrame && frame > best && EntryAt(s, eo, local - 1) == pgno)
                    {
                        best = frame;
                    }
                }

                h = (h + 1) & (HashSlots - 1);
            }

            if (best != 0)
            {
                return best;
            }
        }

        return 0;
    }

    /// <summary>The page number recorded for <paramref name="frame"/>, or 0 if that part of the index is not mapped or holds nothing.</summary>
    public uint PageOf(uint frame)
    {
        int t = TableOf(frame);
        if (frame == 0 || !HasRegion(t))
        {
            return 0;
        }

        return EntryAt(_regions[t]!.Memory.Span, EntryOffset(t), (int)(frame - FirstFrameOf(t)));
    }

    private static uint EntryAt(ReadOnlySpan<byte> s, int entryOffset, int index) => WalFormat.ReadNative(s[(entryOffset + 4 * index)..]);

    private static void SetEntry(Span<byte> s, int entryOffset, int index, uint v) => WalFormat.WriteNative(s[(entryOffset + 4 * index)..], v);

    /// <summary>
    /// Records that frame <paramref name="frame"/> holds page <paramref name="pgno"/>. Frames are appended in order. Starting a new table clears
    /// it, and entries left over from frames that were rolled back or overwritten are removed first. Returns false if the table is not mapped or its
    /// hash chain is full (damaged shared memory).
    /// </summary>
    public bool Append(uint frame, uint pgno)
    {
        int t = TableOf(frame);
        if (!HasRegion(t) || frame == 0)
        {
            return false;
        }

        Span<byte> s = _regions[t]!.Memory.Span;
        uint first = FirstFrameOf(t);
        int local = (int)(frame - first) + 1;
        int eo = EntryOffset(t);
        if (local == 1)
        {
            s[eo..HashOffset].Clear();
            s[HashOffset..(HashOffset + 2 * HashSlots)].Clear();
        }
        else if (EntryAt(s, eo, local - 1) != 0)
        {
            CleanTable(s, eo, CapacityOf(t), local - 1);
        }

        SetEntry(s, eo, local - 1, pgno);
        int h = HashOf(pgno);
        for (int probes = 0; ReadSlot(s, h) != 0; h = (h + 1) & (HashSlots - 1))
        {
            if (++probes >= HashSlots)
            {
                return false;
            }
        }

        WriteSlot(s, h, (ushort)local);
        return true;
    }

    /// <summary>Removes every entry for a frame after <paramref name="mxFrame"/> (frames of a rolled-back or abandoned write).</summary>
    public void Cleanup(uint mxFrame)
    {
        int t = TableOf(mxFrame == 0 ? 1 : mxFrame);
        if (!HasRegion(t))
        {
            return;
        }

        Span<byte> s = _regions[t]!.Memory.Span;
        uint first = FirstFrameOf(t);
        int keep = mxFrame < first ? 0 : (int)(mxFrame - first) + 1;
        CleanTable(s, EntryOffset(t), CapacityOf(t), keep);
    }

    // Entries with a local index above 'keep' go; the survivors' probe chains stay valid because every removed entry was inserted after them.
    private static void CleanTable(Span<byte> s, int eo, int cap, int keep)
    {
        for (int i = 0; i < HashSlots; i++)
        {
            if (ReadSlot(s, i) > keep)
            {
                WriteSlot(s, i, 0);
            }
        }

        s[(eo + 4 * keep)..(eo + 4 * cap)].Clear();
    }
}
