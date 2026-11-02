using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Storage.Forensics;

/// <summary>
/// The file is not a database at all (SQLITE_NOTADB, result code 26): the 16-byte magic string at the start is missing, or a file that
/// should hold a write-ahead log or journal has a wrong header in a way that makes the whole file unusable for a required role.
/// </summary>
public sealed class NotADatabaseException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="detail">What was wrong, in words.</param>
    public NotADatabaseException(string detail) : base("file is not a database")
    {
        Detail = detail;
    }

    /// <summary>The primary result code, SQLITE_NOTADB.</summary>
    public int Code { get; } = 26;

    /// <summary>The extended result code (equal to the primary code).</summary>
    public int ExtendedCode { get; } = 26;

    /// <summary>What was wrong.</summary>
    public string Detail { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{Message} ({Detail})";
}

/// <summary>A log frame changed between the scan that listed it and the read of its page: a live writer restarted or rewrote the log. Open the files again and repeat the pass.</summary>
public sealed class ForensicChangedException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="frame">The frame that changed.</param>
    public ForensicChangedException(int frame) : base($"log frame {frame} changed while it was being read")
    {
        Frame = frame;
    }

    /// <summary>The frame that changed (1-based).</summary>
    public int Frame { get; }
}

/// <summary>Options for <see cref="ForensicDatabase.Open(string, ForensicOptions?)"/>.</summary>
public sealed class ForensicOptions
{
    /// <summary>The VFS to read through. Default: <see cref="FileVfs.Instance"/>. Files are opened read-only, no lock is taken and nothing is created.</summary>
    public IVfs? Vfs { get; init; }

    /// <summary>Read the <c>-wal</c> file when it exists. Default true. When false the database file alone is read, whatever the log holds.</summary>
    public bool IncludeWal { get; init; } = true;

    /// <summary>Read the rollback journal when it exists, as a source of older page images for carving. Default true. A journal is never applied.</summary>
    public bool IncludeJournal { get; init; } = true;
}

/// <summary>Which state of the database a snapshot shows.</summary>
public enum SnapshotMode
{
    /// <summary>The last valid commit of the log (or the database file when there is no usable log). What a normal reader would see.</summary>
    Latest,

    /// <summary>The database file only; the log is ignored, as if it did not exist.</summary>
    DatabaseOnly,

    /// <summary>The database as of one commit listed in <see cref="WalInfo.Commits"/>.</summary>
    AsOfCommit,
}

/// <summary>Chooses a snapshot for <see cref="ForensicDatabase.OpenSnapshot"/>.</summary>
public readonly struct SnapshotSelector
{
    private SnapshotSelector(SnapshotMode mode, int commit, bool trailing)
    {
        Mode = mode;
        CommitId = commit;
        IncludeTrailingFrames = trailing;
    }

    /// <summary>The mode.</summary>
    public SnapshotMode Mode { get; }

    /// <summary>For <see cref="SnapshotMode.AsOfCommit"/>, the <see cref="WalCommit.Id"/>.</summary>
    public int CommitId { get; }

    /// <summary>
    /// Also overlay the frames of the current log generation that follow the last commit (written but never committed; their checksums are
    /// valid). The snapshot is then flagged <see cref="ForensicSnapshot.IncludesUncommitted"/>. Only meaningful for <see cref="SnapshotMode.Latest"/>.
    /// </summary>
    public bool IncludeTrailingFrames { get; }

    /// <summary>The last valid commit.</summary>
    public static SnapshotSelector Latest => new(SnapshotMode.Latest, 0, false);

    /// <summary>The database file alone.</summary>
    public static SnapshotSelector DatabaseOnly => new(SnapshotMode.DatabaseOnly, 0, false);

    /// <summary>The state as of commit <paramref name="commitId"/> (<see cref="WalCommit.Id"/>).</summary>
    public static SnapshotSelector AsOf(int commitId) => new(SnapshotMode.AsOfCommit, commitId, false);

    /// <summary>The latest commit plus the uncommitted frames that follow it.</summary>
    public static SnapshotSelector LatestWithTrailingFrames => new(SnapshotMode.Latest, 0, true);
}

/// <summary>What a log frame is, judged by the validity rules of the log format (see docs/forensics.md).</summary>
public enum WalFrameState : byte
{
    /// <summary>In the valid chain of the current generation, at or before the last commit frame: a normal reader uses it.</summary>
    Committed,

    /// <summary>In the valid chain of the current generation but after the last commit frame: written, never committed.</summary>
    Trailing,

    /// <summary>Same salts as the log header but the checksum fails: a torn or damaged frame. The chain of the current generation ends here.</summary>
    Damaged,

    /// <summary>Salts differ from the log header: a frame left from an earlier generation that a restart did not overwrite, or garbage.</summary>
    Stale,
}

/// <summary>One 24-byte frame header and where its page image sits.</summary>
public readonly struct WalFrameInfo
{
    internal WalFrameInfo(int index, uint pgno, uint dbSize, uint s1, uint s2, uint c1, uint c2, long offset, WalFrameState state, int generation, bool verified)
    {
        Index = index;
        PageNumber = pgno;
        DatabaseSizeAfterCommit = dbSize;
        Salt1 = s1;
        Salt2 = s2;
        Checksum1 = c1;
        Checksum2 = c2;
        Offset = offset;
        State = state;
        Generation = generation;
        ChecksumVerified = verified;
    }

    /// <summary>Position in the file, counted from 1.</summary>
    public int Index { get; }

    /// <summary>The database page this frame holds.</summary>
    public uint PageNumber { get; }

    /// <summary>Non-zero in a commit frame: the database size in pages after the commit.</summary>
    public uint DatabaseSizeAfterCommit { get; }

    /// <summary>First salt of the frame header.</summary>
    public uint Salt1 { get; }

    /// <summary>Second salt of the frame header.</summary>
    public uint Salt2 { get; }

    /// <summary>First checksum word of the frame header.</summary>
    public uint Checksum1 { get; }

    /// <summary>Second checksum word of the frame header.</summary>
    public uint Checksum2 { get; }

    /// <summary>File offset of the frame header; the page image follows 24 bytes later.</summary>
    public long Offset { get; }

    /// <summary>The frame's state.</summary>
    public WalFrameState State { get; }

    /// <summary>Generation number: 0 for the current generation, then 1, 2, ... for runs of stale frames in file order.</summary>
    public int Generation { get; }

    /// <summary>True if the cumulative checksum was verified against a known predecessor (the log header or the previous frame's stored checksum). False for the first frame of a stale run.</summary>
    public bool ChecksumVerified { get; }

    /// <summary>True for a commit frame.</summary>
    public bool IsCommit => DatabaseSizeAfterCommit != 0;
}

/// <summary>A run of frames written under one pair of salts.</summary>
public sealed class WalGeneration
{
    internal WalGeneration(int number, uint salt1, uint salt2, int first, int last, bool current, bool anchored)
    {
        Number = number;
        Salt1 = salt1;
        Salt2 = salt2;
        FirstFrame = first;
        LastFrame = last;
        IsCurrent = current;
        StartsAtFrameOne = first == 1;
        ChecksumAnchored = anchored;
    }

    /// <summary>0 for the generation the log header describes; others number stale runs in file order.</summary>
    public int Number { get; }

    /// <summary>First salt.</summary>
    public uint Salt1 { get; }

    /// <summary>Second salt.</summary>
    public uint Salt2 { get; }

    /// <summary>First frame (1-based file position).</summary>
    public int FirstFrame { get; internal set; }

    /// <summary>Last frame of the run.</summary>
    public int LastFrame { get; internal set; }

    /// <summary>Number of frames in the run.</summary>
    public int FrameCount => LastFrame - FirstFrame + 1;

    /// <summary>True for the generation the log header describes.</summary>
    public bool IsCurrent { get; }

    /// <summary>True if the run begins at frame 1, so no earlier frame of the generation has been overwritten. A stale run that does not start at frame 1 has lost its first frames to a newer generation: a snapshot taken inside it is partial.</summary>
    public bool StartsAtFrameOne { get; }

    /// <summary>True if the first frame's checksum was checked against the log header (only the current generation).</summary>
    public bool ChecksumAnchored { get; }
}

/// <summary>A transaction in the log: its last frame is a commit frame.</summary>
public sealed class WalCommit
{
    internal WalCommit(int id, int generation, int firstFrame, int lastFrame, uint dbSize, bool partial)
    {
        Id = id;
        Generation = generation;
        FirstFrame = firstFrame;
        LastFrame = lastFrame;
        DatabaseSizePages = dbSize;
        PrefixLost = partial;
    }

    /// <summary>Identifier for <see cref="SnapshotSelector.AsOf"/>: 1, 2, ... in file order of the commit frames.</summary>
    public int Id { get; }

    /// <summary>The <see cref="WalGeneration.Number"/> it belongs to.</summary>
    public int Generation { get; }

    /// <summary>First frame of the transaction (the frame after the previous commit frame of the run, or the run's first frame).</summary>
    public int FirstFrame { get; }

    /// <summary>The commit frame.</summary>
    public int LastFrame { get; }

    /// <summary>Frames in this transaction.</summary>
    public int FrameCount => LastFrame - FirstFrame + 1;

    /// <summary>Database size in pages after the commit.</summary>
    public uint DatabaseSizePages { get; }

    /// <summary>True if earlier frames of this commit's generation are gone (overwritten by a newer generation), so a snapshot as of this commit is missing pages.</summary>
    public bool PrefixLost { get; }
}

/// <summary>What the <c>-wal</c> file holds.</summary>
public sealed class WalInfo
{
    /// <summary>False if the header is unusable (reason in <see cref="UnusableReason"/>); then no frame is trusted.</summary>
    public bool Usable { get; internal set; }

    /// <summary>Why the log cannot be used, or null.</summary>
    public string? UnusableReason { get; internal set; }

    /// <summary>File length in bytes.</summary>
    public long FileLength { get; internal set; }

    /// <summary>Page size recorded in the log header.</summary>
    public int PageSize { get; internal set; }

    /// <summary>Header magic (0x377f0682 or 0x377f0683); the low bit selects big-endian checksum words.</summary>
    public uint Magic { get; internal set; }

    /// <summary>Checkpoint sequence number of the header.</summary>
    public uint CheckpointSequence { get; internal set; }

    /// <summary>Header salt 1.</summary>
    public uint Salt1 { get; internal set; }

    /// <summary>Header salt 2.</summary>
    public uint Salt2 { get; internal set; }

    /// <summary>Every complete frame in the file, in file order.</summary>
    public IReadOnlyList<WalFrameInfo> Frames { get; internal set; } = [];

    /// <summary>Generations: the current one first, then stale runs.</summary>
    public IReadOnlyList<WalGeneration> Generations { get; internal set; } = [];

    /// <summary>Commits of every generation, in file order. Commits of stale generations are marked <see cref="WalCommit.PrefixLost"/> when frames before them are gone.</summary>
    public IReadOnlyList<WalCommit> Commits { get; internal set; } = [];

    /// <summary>Bytes after the last complete frame (a torn final frame).</summary>
    public int TornTailBytes { get; internal set; }

    /// <summary>Last frame of the current generation's valid chain (0 if none).</summary>
    public int ValidChainEnd { get; internal set; }

    /// <summary>Last commit frame of the current generation (0 if none).</summary>
    public int LastCommitFrame { get; internal set; }
}

/// <summary>What the <c>-shm</c> file said, read as plain bytes (the wal-index is never mapped, locked or written). Advisory only.</summary>
public sealed class ShmInfo
{
    /// <summary>True if the first header copy has version 3007000 and both copies agree.</summary>
    public bool Valid { get; internal set; }

    /// <summary>mxFrame: the last frame the wal-index believes is valid.</summary>
    public uint MaxFrame { get; internal set; }

    /// <summary>nBackfill: frames already copied into the database file by a checkpoint.</summary>
    public uint Backfilled { get; internal set; }

    /// <summary>Salt 1 recorded in the wal-index.</summary>
    public uint Salt1 { get; internal set; }

    /// <summary>Salt 2 recorded in the wal-index.</summary>
    public uint Salt2 { get; internal set; }

    /// <summary>True if the salts equal the log header's: the index describes this log, not an earlier one.</summary>
    public bool MatchesWal { get; internal set; }
}

/// <summary>One page image in a rollback journal.</summary>
public readonly struct JournalRecord
{
    internal JournalRecord(uint pgno, long offset, bool checksumOk)
    {
        PageNumber = pgno;
        Offset = offset;
        ChecksumOk = checksumOk;
    }

    /// <summary>The database page the image belongs to.</summary>
    public uint PageNumber { get; }

    /// <summary>File offset of the page image (4 bytes after the record start).</summary>
    public long Offset { get; }

    /// <summary>True if the stored checksum matches the nonce-based sum.</summary>
    public bool ChecksumOk { get; }
}

/// <summary>What the rollback journal holds. It is only a source of older page images; it is never played back.</summary>
public sealed class JournalInfo
{
    /// <summary>True if the header magic matched.</summary>
    public bool HeaderValid { get; internal set; }

    /// <summary>File length.</summary>
    public long FileLength { get; internal set; }

    /// <summary>Page size from the header.</summary>
    public int PageSize { get; internal set; }

    /// <summary>The database size in pages before the transaction that wrote the journal.</summary>
    public uint OriginalDatabasePages { get; internal set; }

    /// <summary>Page images, in file order (those that fit in the file and whose page number is non-zero).</summary>
    public IReadOnlyList<JournalRecord> Records { get; internal set; } = [];
}

/// <summary>The fixed fields of a database header (fileformat2 §1.3).</summary>
public sealed class DatabaseHeaderInfo
{
    /// <summary>Page size in bytes.</summary>
    public int PageSize { get; internal set; }

    /// <summary>Reserved bytes at the end of each page.</summary>
    public int ReservedBytes { get; internal set; }

    /// <summary>File change counter.</summary>
    public uint ChangeCounter { get; internal set; }

    /// <summary>Database size in pages as recorded in the header (trusted only when <see cref="SizeFieldValid"/>).</summary>
    public uint SizeInHeader { get; internal set; }

    /// <summary>True if "version-valid-for" equals the change counter, so the size field is current.</summary>
    public bool SizeFieldValid { get; internal set; }

    /// <summary>Page number of the first freelist trunk, 0 for none.</summary>
    public uint FirstFreelistTrunk { get; internal set; }

    /// <summary>Total freelist pages according to the header.</summary>
    public uint FreelistCount { get; internal set; }

    /// <summary>Text encoding.</summary>
    public TextEncoding Encoding { get; internal set; }

    /// <summary>Header offset 52: largest root page for auto-vacuum, 0 if not auto-vacuum.</summary>
    public uint LargestRootPage { get; internal set; }

    /// <summary>File format read version (2 = WAL).</summary>
    public int ReadVersion { get; internal set; }

    /// <summary>Schema cookie.</summary>
    public uint SchemaCookie { get; internal set; }
}
