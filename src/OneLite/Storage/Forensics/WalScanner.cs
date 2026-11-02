using System.Buffers.Binary;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Storage.Forensics;

/// <summary>
/// Reads a write-ahead log file the way walformat describes it, but never applies it. Every frame is classified instead of the scan stopping
/// at the first bad one, so older generations and damaged tails stay available for recovery work.
/// </summary>
internal static class WalScanner
{
    public const int HeaderSize = 32;
    public const int FrameHeaderSize = 24;
    private const uint MagicLittle = 0x377f0682;
    private const uint MagicBig = 0x377f0683;
    private const uint FormatVersion = 3007000;

    /// <summary>
    /// walformat "checksum algorithm": the data is read as pairs of 32-bit words; for each pair s0 += x0 + s1; s1 += x1 + s0. The byte order of the
    /// words follows the low bit of the header magic. The input length must be a multiple of 8, which a header (24 bytes) and a page are.
    /// </summary>
    public static void Checksum(ReadOnlySpan<byte> data, bool bigEndian, ref uint s0, ref uint s1)
    {
        for (int i = 0; i + 8 <= data.Length; i += 8)
        {
            uint x0 = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data[i..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
            uint x1 = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data[(i + 4)..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 4)..]);
            s0 += x0 + s1;
            s1 += x1 + s0;
        }
    }

    private struct Raw
    {
        public uint Pgno, DbSize, Salt1, Salt2, C1, C2;
        public bool InChain, Verified;
        public int Gen;
        public bool Damaged;
    }

    public static WalInfo Scan(IVfsFile f, long length)
    {
        var info = new WalInfo { FileLength = length };
        if (length < HeaderSize)
        {
            info.UnusableReason = "shorter than the 32-byte log header";
            return info;
        }

        Span<byte> hdr = stackalloc byte[HeaderSize];
        if (f.Read(0, hdr) < HeaderSize)
        {
            info.UnusableReason = "short read of the log header";
            return info;
        }

        uint magic = BinaryPrimitives.ReadUInt32BigEndian(hdr);
        if (magic != MagicLittle && magic != MagicBig)
        {
            info.UnusableReason = $"log magic 0x{magic:x8} is not 0x377f0682 or 0x377f0683";
            return info;
        }

        uint version = BinaryPrimitives.ReadUInt32BigEndian(hdr[4..]);
        uint ps = BinaryPrimitives.ReadUInt32BigEndian(hdr[8..]);
        if (version != FormatVersion)
        {
            info.UnusableReason = $"log format version {version} is not {FormatVersion}";
            return info;
        }

        if (ps < 512 || ps > 65536 || (ps & (ps - 1)) != 0)
        {
            info.UnusableReason = $"log page size {ps} is not a power of two in 512..65536";
            return info;
        }

        bool big = (magic & 1) != 0;
        uint h0 = 0, h1 = 0;
        Checksum(hdr[..24], big, ref h0, ref h1);
        uint c1 = BinaryPrimitives.ReadUInt32BigEndian(hdr[24..]);
        uint c2 = BinaryPrimitives.ReadUInt32BigEndian(hdr[28..]);
        if (h0 != c1 || h1 != c2)
        {
            info.UnusableReason = "the log header checksum does not match";
            return info;
        }

        info.Magic = magic;
        info.PageSize = (int)ps;
        info.CheckpointSequence = BinaryPrimitives.ReadUInt32BigEndian(hdr[12..]);
        info.Salt1 = BinaryPrimitives.ReadUInt32BigEndian(hdr[16..]);
        info.Salt2 = BinaryPrimitives.ReadUInt32BigEndian(hdr[20..]);
        info.Usable = true;

        long frameSize = FrameHeaderSize + ps;
        long nFrames = (length - HeaderSize) / frameSize;
        info.TornTailBytes = (int)((length - HeaderSize) % frameSize);
        // The count comes from the real file length, so the arrays below are bounded by what is on disk.
        if (nFrames > int.MaxValue - 1)
        {
            nFrames = int.MaxValue - 1;
        }

        var raws = new Raw[nFrames];
        byte[] buf = new byte[frameSize];
        uint s0 = c1, s1 = c2;
        bool inChain = true;
        int chainEnd = 0, lastCommit = 0, gen = 0;
        for (int i = 1; i <= nFrames; i++)
        {
            long off = HeaderSize + (long)(i - 1) * frameSize;
            if (f.Read(off, buf) < frameSize)
            {
                nFrames = i - 1;
                break;
            }

            ref Raw r = ref raws[i - 1];
            r.Pgno = BinaryPrimitives.ReadUInt32BigEndian(buf);
            r.DbSize = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(4));
            r.Salt1 = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(8));
            r.Salt2 = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(12));
            r.C1 = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(16));
            r.C2 = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(20));
            bool saltsMatch = r.Salt1 == info.Salt1 && r.Salt2 == info.Salt2;
            if (inChain)
            {
                uint a = s0, b = s1;
                Checksum(buf.AsSpan(0, 8), big, ref a, ref b);
                Checksum(buf.AsSpan(FrameHeaderSize), big, ref a, ref b);
                // walformat: a frame is valid if its salts equal the header's and its checksum continues the chain; page number 0 never names a page.
                if (saltsMatch && r.Pgno != 0 && a == r.C1 && b == r.C2)
                {
                    r.InChain = true;
                    r.Verified = true;
                    s0 = r.C1;
                    s1 = r.C2;
                    chainEnd = i;
                    if (r.DbSize != 0)
                    {
                        lastCommit = i;
                    }

                    continue;
                }

                inChain = false;
            }

            // Out of the valid chain. Verify against the previous frame's stored checksum when it has the same salts: a run of frames written together
            // keeps its own cumulative chain even though its start is no longer anchored to a log header.
            r.Damaged = saltsMatch;
            if (saltsMatch)
            {
                r.Gen = 0;
                continue;
            }

            bool cont = false;
            if (i > 1)
            {
                Raw p = raws[i - 2];
                if (!p.InChain && !p.Damaged && p.Salt1 == r.Salt1 && p.Salt2 == r.Salt2 && r.Pgno != 0)
                {
                    uint a = p.C1, b = p.C2;
                    Checksum(buf.AsSpan(0, 8), big, ref a, ref b);
                    Checksum(buf.AsSpan(FrameHeaderSize), big, ref a, ref b);
                    cont = a == r.C1 && b == r.C2;
                    if (cont)
                    {
                        r.Gen = p.Gen;
                        r.Verified = true;
                    }
                }
            }

            if (!cont)
            {
                r.Gen = ++gen;
                r.Verified = false;
            }
        }

        var frames = new WalFrameInfo[nFrames];
        var gens = new List<WalGeneration> { new(0, info.Salt1, info.Salt2, 1, chainEnd, true, true) };
        var commits = new List<WalCommit>();
        int commitStart = 1;
        var stale = new Dictionary<int, WalGeneration>();
        var staleCommitStart = new Dictionary<int, int>();
        for (int i = 1; i <= nFrames; i++)
        {
            Raw r = raws[i - 1];
            long off = HeaderSize + (long)(i - 1) * frameSize;
            WalFrameState st = r.InChain ? (i <= lastCommit ? WalFrameState.Committed : WalFrameState.Trailing) : r.Damaged ? WalFrameState.Damaged : WalFrameState.Stale;
            frames[i - 1] = new WalFrameInfo(i, r.Pgno, r.DbSize, r.Salt1, r.Salt2, r.C1, r.C2, off, st, r.Gen, r.Verified);
            if (r.InChain)
            {
                if (r.DbSize != 0)
                {
                    commits.Add(new WalCommit(commits.Count + 1, 0, commitStart, i, r.DbSize, false));
                    commitStart = i + 1;
                }
            }
            else if (st == WalFrameState.Stale)
            {
                if (!stale.TryGetValue(r.Gen, out WalGeneration? g))
                {
                    g = new WalGeneration(r.Gen, r.Salt1, r.Salt2, i, i, false, false);
                    stale[r.Gen] = g;
                    gens.Add(g);
                    staleCommitStart[r.Gen] = i;
                }

                g.LastFrame = i;
                if (r.DbSize != 0)
                {
                    commits.Add(new WalCommit(commits.Count + 1, r.Gen, staleCommitStart[r.Gen], i, r.DbSize, g.FirstFrame != 1));
                    staleCommitStart[r.Gen] = i + 1;
                }
            }
        }

        info.Frames = frames;
        info.Generations = gens;
        info.Commits = commits;
        info.ValidChainEnd = chainEnd;
        info.LastCommitFrame = lastCommit;
        return info;
    }

    private static uint U(ReadOnlySpan<byte> b, int o, bool be) => be ? BinaryPrimitives.ReadUInt32BigEndian(b[o..]) : BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);

    /// <summary>Reads the wal-index header from a <c>-shm</c> file's bytes (walformat "wal-index header"): two copies of 48 bytes, then the checkpoint info.</summary>
    public static ShmInfo ParseShm(ReadOnlySpan<byte> b, WalInfo? wal)
    {
        var s = new ShmInfo();
        if (b.Length < 136)
        {
            return s;
        }

        // The wal-index is in the byte order of the machine that wrote it; try little-endian, then big-endian.
        for (int pass = 0; pass < 2; pass++)
        {
            bool be = pass == 1;
            if (U(b, 0, be) != FormatVersion || !b[..48].SequenceEqual(b.Slice(48, 48)))
            {
                continue;
            }

            s.Valid = true;
            s.MaxFrame = U(b, 16, be);
            s.Backfilled = U(b, 96, be);
            // The salts are a raw copy of the log header's eight bytes, so they read in the log header's (big-endian) order whatever the machine's.
            s.Salt1 = BinaryPrimitives.ReadUInt32BigEndian(b[32..]);
            s.Salt2 = BinaryPrimitives.ReadUInt32BigEndian(b[36..]);
            s.MatchesWal = wal is { Usable: true } && wal.Salt1 == s.Salt1 && wal.Salt2 == s.Salt2;
            break;
        }

        return s;
    }
}
