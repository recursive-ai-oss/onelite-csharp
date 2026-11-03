using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Storage.Pager;

/// <summary>The outcome of a pager call (step contract).</summary>
public enum PagerStatus : byte
{
    /// <summary>The call finished.</summary>
    Ok,

    /// <summary>The call needs one I/O: run <see cref="Pager.PendingIo"/> and call again with the same arguments.</summary>
    Pending,

    /// <summary>The call failed; see <see cref="Pager.Error"/>.</summary>
    Error,
}

/// <summary>What kind of I/O a request asks for.</summary>
public enum IoKind : byte
{
    /// <summary>No request.</summary>
    None,

    /// <summary>A pure suspend point with no I/O (test hook).</summary>
    Yield,

    /// <summary>Read <see cref="IoRequest.Length"/> bytes at <see cref="IoRequest.Offset"/> into <see cref="IoRequest.Data"/>; the completion value is the bytes read.</summary>
    Read,

    /// <summary>Write <see cref="IoRequest.Length"/> bytes of <see cref="IoRequest.Data"/> at <see cref="IoRequest.Offset"/>.</summary>
    Write,

    /// <summary>Write consecutive pages (<see cref="IoRequest.Run"/>) starting at <see cref="IoRequest.Offset"/>.</summary>
    WriteRun,

    /// <summary>Sync the file; <see cref="IoRequest.Arg"/> is a <see cref="SyncMode"/>.</summary>
    Sync,

    /// <summary>Sync the directory that holds the file.</summary>
    SyncDirectory,

    /// <summary>Truncate the file to <see cref="IoRequest.Offset"/> bytes.</summary>
    Truncate,

    /// <summary>Get the file size; the completion value is the size.</summary>
    FileSize,

    /// <summary>Open <see cref="IoRequest.Path"/> with <see cref="VfsOpenOptions"/> in <see cref="IoRequest.Arg"/>; the completion carries the file.</summary>
    Open,

    /// <summary>Close the file.</summary>
    Close,

    /// <summary>Delete <see cref="IoRequest.Path"/>; <see cref="IoRequest.Arg"/> non-zero also syncs the directory.</summary>
    Delete,

    /// <summary>Test whether <see cref="IoRequest.Path"/> exists; the completion value is 1 or 0.</summary>
    Access,

    /// <summary>Try to raise the file lock to <see cref="LockLevel"/> in <see cref="IoRequest.Arg"/>; the completion value is 1 if granted, 0 if busy.</summary>
    Lock,

    /// <summary>Lower the lock to <see cref="LockLevel"/> in <see cref="IoRequest.Arg"/>.</summary>
    Unlock,

    /// <summary>Is a RESERVED or stronger lock held by anyone? The completion value is 1 or 0.</summary>
    CheckReservedLock,

    /// <summary>Sleep <see cref="IoRequest.Length"/> milliseconds (busy handling).</summary>
    Sleep,

    /// <summary>Map a wal-index region (<see cref="IoRequest.Offset"/>); the region comes back in <see cref="IoCompletion.Region"/>.</summary>
    ShmMap,

    /// <summary>Take or release wal-index lock slots (<see cref="IoRequest.Offset"/> first slot, <see cref="IoRequest.Length"/> count, <see cref="IoRequest.Arg"/> a <see cref="ShmLockOptions"/>); the result is 1 for granted.</summary>
    ShmLock,

    /// <summary>Wait until the task in <see cref="IoRequest.Wait"/> completes (group commit: a turn, a covering sync, the end of a group).</summary>
    GroupWait,
}

/// <summary>The named state of the pager that issued a request (suspend-point notes; used for diagnostics and fault-injection tests).</summary>
public enum ResumeState : byte
{
    /// <summary>None.</summary>
    None,
    /// <summary>A test yield.</summary>
    Yield,
    /// <summary>Get: read the page from the database.</summary>
    GetReadDb,
    /// <summary>Get: read the page from the page source (WAL).</summary>
    GetReadSource,
    /// <summary>Lock wait: the lock request.</summary>
    LockRequest,
    /// <summary>Lock wait: the busy sleep.</summary>
    LockSleep,
    /// <summary>Begin read: probe for a journal.</summary>
    HotJournalAccess,
    /// <summary>Begin read: reserved-lock probe.</summary>
    HotJournalReserved,
    /// <summary>Begin read: open the journal.</summary>
    HotJournalOpen,
    /// <summary>Begin read: journal size.</summary>
    HotJournalSize,
    /// <summary>Begin read: journal header.</summary>
    HotJournalHeader,
    /// <summary>Begin read: trailer tail.</summary>
    HotJournalTail,
    /// <summary>Begin read: trailer name.</summary>
    HotJournalName,
    /// <summary>Begin read: super-journal probe.</summary>
    HotJournalSuper,
    /// <summary>Begin read: exclusive lock for recovery.</summary>
    HotJournalExclusive,
    /// <summary>Begin read: re-check after the exclusive lock.</summary>
    HotJournalRecheck,
    /// <summary>Begin read: release after a failed exclusive attempt.</summary>
    HotJournalRelease,
    /// <summary>Begin read: close a journal that is not hot.</summary>
    HotJournalClose,
    /// <summary>Begin read: file size.</summary>
    OpenFileSize,
    /// <summary>Begin read: page 1 header.</summary>
    OpenRead1,
    /// <summary>Playback: segment header.</summary>
    PlaybackHeader,
    /// <summary>Playback: record.</summary>
    PlaybackRecord,
    /// <summary>Playback: write a page.</summary>
    PlaybackWritePage,
    /// <summary>Playback: truncate the database.</summary>
    PlaybackTruncate,
    /// <summary>Playback: sync the database.</summary>
    PlaybackSyncDb,
    /// <summary>Playback: release the exclusive lock.</summary>
    PlaybackUnlock,
    /// <summary>Journal creation.</summary>
    JournalOpen,
    /// <summary>Journal buffer flush.</summary>
    JournalFlush,
    /// <summary>Journal sync before the header rewrite.</summary>
    JournalSync1,
    /// <summary>Directory sync after the journal's first sync.</summary>
    JournalDirSync,
    /// <summary>Journal header rewrite.</summary>
    JournalHeader,
    /// <summary>Journal sync after the header rewrite.</summary>
    JournalSync2,
    /// <summary>Spill: write a dirty page.</summary>
    SpillWriteDbPage,
    /// <summary>Spill or commit: exclusive lock.</summary>
    EscalateExclusive,
    /// <summary>Commit: write database pages.</summary>
    CommitWriteDb,
    /// <summary>Commit: truncate the database.</summary>
    CommitTruncateDb,
    /// <summary>Commit: sync the database.</summary>
    CommitSyncDb,
    /// <summary>Finalize the journal: close it.</summary>
    FinalizeClose,
    /// <summary>Finalize the journal: delete it.</summary>
    FinalizeDelete,
    /// <summary>Finalize the journal: truncate it.</summary>
    FinalizeTruncate,
    /// <summary>Finalize the journal: zero its header.</summary>
    FinalizeZeroHeader,
    /// <summary>Finalize the journal: sync it.</summary>
    FinalizeSync,
    /// <summary>Unlock after commit or rollback.</summary>
    CommitUnlock,
    /// <summary>End of read transaction.</summary>
    EndReadUnlock,
    /// <summary>Allocation or free: trunk page.</summary>
    AllocTrunk,
    /// <summary>Mapping a wal-index region.</summary>
    WalShmMap,
    /// <summary>Taking or releasing a wal-index lock.</summary>
    WalShmLock,
    /// <summary>Waiting between attempts to get a WAL lock.</summary>
    WalSleep,
    /// <summary>Opening the -wal file.</summary>
    WalOpen,
    /// <summary>Getting the size of the WAL.</summary>
    WalFileSize,
    /// <summary>Reading the WAL header during recovery.</summary>
    WalReadHeader,
    /// <summary>Reading a WAL frame during recovery.</summary>
    WalReadFrame,
    /// <summary>Writing the WAL header.</summary>
    WalWriteHeader,
    /// <summary>Making the WAL header durable.</summary>
    WalSyncHeader,
    /// <summary>Syncing the directory after the first WAL sync.</summary>
    WalDirSync,
    /// <summary>Appending frames to the WAL.</summary>
    WalAppend,
    /// <summary>Making the appended frames durable.</summary>
    WalSync,
    /// <summary>Cutting the WAL back to journal_size_limit.</summary>
    WalSizeLimit,
    /// <summary>Checkpoint: making the WAL durable before the database is overwritten.</summary>
    CkptSyncWal,
    /// <summary>Checkpoint: reading a frame.</summary>
    CkptReadFrame,
    /// <summary>Checkpoint: writing a page into the database.</summary>
    CkptWriteDb,
    /// <summary>Checkpoint: sizing the database to the last commit.</summary>
    CkptTruncateDb,
    /// <summary>Checkpoint: making the database durable.</summary>
    CkptSyncDb,
    /// <summary>Checkpoint: emptying the WAL file.</summary>
    CkptTruncateWal,
    /// <summary>Deleting the -wal file when leaving WAL mode.</summary>
    WalCloseDelete,
    /// <summary>Sorter: opening the temporary file for a run.</summary>
    SortSpillOpen,
    /// <summary>Sorter: writing a sorted run to the temporary file.</summary>
    SortSpillRun,
    /// <summary>Sorter: reading a run back during the merge.</summary>
    SortMergeRead,
    /// <summary>Encrypted database: page 1 in the database file does not decode, so look for a WAL that holds a newer page 1.</summary>
    WalProbe,
    /// <summary>Group commit: waiting for the turn to append, for a covering sync, or for the group to drain.</summary>
    WalGroupWait,
}

/// <summary>The one I/O a pager is waiting for. Filled in place; never allocated per request.</summary>
public struct IoRequest
{
    /// <summary>What to do.</summary>
    public IoKind Kind { get; set; }

    /// <summary>The state that issued it.</summary>
    public ResumeState Phase { get; set; }

    /// <summary>The file operated on (null for VFS-level calls).</summary>
    public IVfsFile? File { get; set; }

    /// <summary>The path for Open, Delete and Access.</summary>
    public string? Path { get; set; }

    /// <summary>Byte offset (or the new size for Truncate).</summary>
    public long Offset { get; set; }

    /// <summary>Byte count, or milliseconds for Sleep.</summary>
    public int Length { get; set; }

    /// <summary>The buffer for Read and Write.</summary>
    public Memory<byte> Data { get; set; }

    /// <summary>The pages of a WriteRun.</summary>
    public ReadOnlyMemory<byte>[]? Run { get; set; }

    /// <summary>The number of used entries in <see cref="Run"/>.</summary>
    public int RunCount { get; set; }

    /// <summary>Kind-specific argument (lock level, sync mode, open options, delete-with-directory-sync).</summary>
    public int Arg { get; set; }

    /// <summary>True if the request can be abandoned without consequence.</summary>
    public bool Cancellable { get; set; }

    /// <summary>The wal-index to map or lock, for <see cref="IoKind.ShmMap"/> and <see cref="IoKind.ShmLock"/>.</summary>
    public IVfsShm? Shm { get; set; }

    /// <summary>The task to wait for, for <see cref="IoKind.GroupWait"/>.</summary>
    public Task? Wait { get; set; }
}

/// <summary>The result of running an <see cref="IoRequest"/>.</summary>
public struct IoCompletion
{
    /// <summary>Bytes read, file size, 1/0 for lock granted or exists.</summary>
    public long Value { get; set; }

    /// <summary>0, or the extended result code of the failure.</summary>
    public int Status { get; set; }

    /// <summary>The opened file for Open.</summary>
    public IVfsFile? File { get; set; }

    /// <summary>The mapped region, for <see cref="IoKind.ShmMap"/>.</summary>
    public ShmRegion? Region { get; set; }
}

/// <summary>Flags for <see cref="Pager.Get"/>.</summary>
[Flags]
public enum GetOptions
{
    /// <summary>Read the page normally.</summary>
    None = 0,

    /// <summary>The caller will overwrite the whole page, so its old content need not be read.</summary>
    NoContent = 1,
}

/// <summary>The rollback journal mode (<c>PRAGMA journal_mode</c>, rollback modes only).</summary>
public enum JournalMode : byte
{
    /// <summary>Delete the journal at commit.</summary>
    Delete,

    /// <summary>Truncate the journal to zero length at commit.</summary>
    Truncate,

    /// <summary>Overwrite the journal header with zeros at commit.</summary>
    Persist,

    /// <summary>Keep the journal in memory; a crash can corrupt the database.</summary>
    Memory,

    /// <summary>No journal; rollback is impossible and a crash can corrupt the database.</summary>
    Off,

    /// <summary>Write-ahead log (sqlite.org wal): the database header says WAL (bytes 18 and 19 are 2) and changes go to the <c>-wal</c> file first. Switch with <see cref="Pager.ChangeJournalMode"/>.</summary>
    Wal,
}

/// <summary><c>PRAGMA synchronous</c>.</summary>
public enum SynchronousMode : byte
{
    /// <summary>Never sync.</summary>
    Off,

    /// <summary>Sync at the critical moments; a commit may be lost on power failure but the file is not corrupted.</summary>
    Normal,

    /// <summary>Sync so that no acknowledged commit is lost.</summary>
    Full,

    /// <summary>Full, plus a directory sync after the journal is deleted.</summary>
    Extra,
}

/// <summary>Where a page is read from when the WAL layer provides one (the seam for the WAL).</summary>
public interface IPageSource
{
    /// <summary>
    /// Looks up a page in the connection's snapshot. Memory only; never suspends.
    /// </summary>
    /// <param name="pageNumber">The page.</param>
    /// <param name="file">The file holding the page image.</param>
    /// <param name="offset">Byte offset of the page image in <paramref name="file"/>.</param>
    /// <returns>True if the page comes from the source; false to read the database file.</returns>
    bool TryLocate(uint pageNumber, out IVfsFile? file, out long offset);
}
