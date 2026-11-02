using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Storage.Wal;

/// <summary>The kinds of checkpoint (sqlite.org pragma wal_checkpoint).</summary>
public enum CheckpointMode : byte
{
    /// <summary>Copies as many frames as it can without waiting for any reader or writer.</summary>
    Passive,

    /// <summary>Blocks new writers, waits (per the busy policy) until every reader uses the newest snapshot, then copies everything.</summary>
    Full,

    /// <summary>Like <see cref="Full"/>, then also waits until no reader uses the log, so the next writer starts it over.</summary>
    Restart,

    /// <summary>Like <see cref="Restart"/>, then truncates the WAL file to zero bytes.</summary>
    Truncate,
}

/// <summary>What a checkpoint did: the three numbers <c>PRAGMA wal_checkpoint</c> returns.</summary>
/// <param name="Busy">True if a lock the checkpoint needed could not be had, so it stopped early (or did nothing).</param>
/// <param name="LogFrames">The number of frames in the WAL, or -1 if the database is not in WAL mode.</param>
/// <param name="CheckpointedFrames">The number of frames copied into the database so far, or -1 if the database is not in WAL mode.</param>
public readonly record struct CheckpointResult(bool Busy, int LogFrames, int CheckpointedFrames);

/// <summary>One connection's state for a database in WAL mode: the log file, the wal-index and the snapshot or write transaction in progress.</summary>
internal sealed class WalConnection
{
    public WalConnection(IVfsFile? file, WalIndex index, bool readOnly)
    {
        File = file;
        Index = index;
        ReadOnly = readOnly;
    }

    /// <summary>The <c>-wal</c> file; null for a read-only connection that found none.</summary>
    public IVfsFile? File { get; }

    /// <summary>The wal-index.</summary>
    public WalIndex Index { get; set; }

    /// <summary>True for a read-only connection.</summary>
    public bool ReadOnly { get; }

    /// <summary>The read lock held (0 to 4), or -1.</summary>
    public int ReadSlot { get; set; } = -1;

    /// <summary>The wal-index header this read transaction started from.</summary>
    public WalIndexHeader Snap;

    /// <summary>The number of frames this reader may use (0 when it holds read lock 0 and works from the database file).</summary>
    public uint ReadMx { get; set; }

    /// <summary>True between the start of a write transaction and its commit or rollback.</summary>
    public bool Writing { get; set; }

    /// <summary>The writer's private copy of the header, advanced as frames are appended and published only at commit.</summary>
    public WalIndexHeader Hdr;

    /// <summary>The checkpoint sequence number of the WAL header on disk.</summary>
    public uint Seq { get; set; }

    /// <summary>True once this write transaction has looked at whether the log can start over.</summary>
    public bool RestartChecked { get; set; }

    /// <summary>True if this write transaction started the log over.</summary>
    public bool Restarted { get; set; }

    /// <summary>True if the cache pushed pages of this write transaction into the WAL early.</summary>
    public bool Spilled { get; set; }

    /// <summary>True once the directory was synced after the first WAL sync of this connection.</summary>
    public bool DirSynced { get; set; }

    /// <summary>The newest frame holding <paramref name="pgno"/> that this connection may use, as a byte offset of the page content in the WAL.</summary>
    public bool Locate(uint pgno, int pageSize, out long offset)
    {
        offset = 0;
        uint limit = Writing ? Hdr.MxFrame : ReadMx;
        if (limit == 0 || File == null)
        {
            return false;
        }

        uint frame = Index.Find(pgno, limit);
        if (frame == 0)
        {
            return false;
        }

        offset = WalFormat.FrameOffset(frame, pageSize) + WalFormat.FrameHeaderSize;
        return true;
    }
}
