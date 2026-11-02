namespace RecursiveAI.OneLite.Storage.BTree;

/// <summary>Outcome of a restartable storage-layer call. After <see cref="Pending"/> the caller runs the outstanding I/O and calls the same operation again with the same arguments.</summary>
public enum BtStatus : byte
{
    /// <summary>The call finished.</summary>
    Ok,

    /// <summary>One I/O is outstanding; nothing observable has happened yet. Call again after completing it.</summary>
    Pending,

    /// <summary>The call failed; the exception is in the cursor's or source's <c>Error</c>.</summary>
    Error,
}

/// <summary>A pinned page. <see cref="Data"/> is the whole page (page size bytes); on page 1 the first 100 bytes are the database header.</summary>
public abstract class PageHandle
{
    /// <summary>The page number, from 1.</summary>
    public abstract uint Number { get; }

    /// <summary>The page's bytes.</summary>
    public abstract byte[] Data { get; }
}

/// <summary>
/// The narrow view of the pager the B-tree is written against (see docs/btree.md). Every call that can need I/O returns
/// <see cref="BtStatus"/> and obeys the re-entry contract: after <see cref="BtStatus.Pending"/> the same call is repeated and
/// nothing has changed. <see cref="Allocate"/>, <see cref="Free"/> and <see cref="PtrmapPut"/> never suspend: the B-tree calls
/// <see cref="Reserve"/> and <see cref="ReservePtrmap"/> first, in its reserve phase.
/// </summary>
public interface IBtPageSource
{
    /// <summary>Page size in bytes.</summary>
    int PageSize { get; }

    /// <summary>Usable bytes per page: the page size minus the reserved bytes at the end of each page (header offset 20).</summary>
    int UsableSize { get; }

    /// <summary>The database size in pages (the bound for every page number read from a file).</summary>
    uint PageCount { get; }

    /// <summary>True for auto-vacuum and incremental-vacuum databases (the file has pointer-map pages).</summary>
    bool AutoVacuum { get; }

    /// <summary>The exception behind the last <see cref="BtStatus.Error"/>.</summary>
    Exception? LastError { get; }

    /// <summary>Pins page <paramref name="pgno"/>. A page number of 0, beyond <see cref="PageCount"/> or the lock-byte page is SQLITE_CORRUPT.</summary>
    BtStatus GetPage(uint pgno, out PageHandle? page);

    /// <summary>Unpins a page.</summary>
    void Release(PageHandle page);

    /// <summary>Prepares a pinned page for modification (journals its original content). Cheap when already done.</summary>
    BtStatus MakeWritable(PageHandle page);

    /// <summary>Makes <see cref="Allocate"/> <paramref name="allocations"/> times and <see cref="Free"/> <paramref name="frees"/> times possible without I/O: loads and pins the freelist trunks and the pointer-map pages they will touch.</summary>
    BtStatus Reserve(int allocations, int frees);

    /// <summary>Makes <see cref="PtrmapPut"/> for page <paramref name="pgno"/> possible without I/O (auto-vacuum only; otherwise a no-op).</summary>
    BtStatus ReservePtrmap(uint pgno);

    /// <summary>Called when an insert or delete has finished its effect phase: what earlier <see cref="Reserve"/> calls set aside and did not use is not owed to the next operation.</summary>
    void EndEffect()
    {
    }

    /// <summary>Returns a zeroed, writable, pinned page: from the freelist, else by growing the file. Only after <see cref="Reserve"/>.</summary>
    PageHandle Allocate();

    /// <summary>Puts a page on the freelist. Only after <see cref="Reserve"/>. The page need not be pinned; if it is, the caller releases it first.</summary>
    void Free(uint pgno);

    /// <summary>Writes a pointer-map entry (fileformat2 §1.7): <paramref name="type"/> 1 root, 2 free, 3 first overflow page (parent = the page holding the cell), 4 later overflow page (parent = previous overflow page), 5 non-root B-tree page (parent = its parent page).</summary>
    void PtrmapPut(uint pgno, byte type, uint parent);

    /// <summary>True if <paramref name="pgno"/> is a pointer-map page (auto-vacuum databases only). Sources that never write answer false.</summary>
    bool IsPtrmapPage(uint pgno) => false;

    /// <summary>Reads a pointer-map entry (fileformat2 §1.7). Only after <see cref="ReservePtrmap"/> for the page; auto-vacuum writers only.</summary>
    BtStatus PtrmapGet(uint pgno, out byte type, out uint parent) => throw new NotSupportedException("this page source has no pointer map");

    /// <summary>The "largest root b-tree page" header field (offset 52), meaningful in auto-vacuum databases.</summary>
    uint LargestRootPage { get; set; }
}
