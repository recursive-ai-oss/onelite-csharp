using RecursiveAI.OneLite.Storage.Pager;
using PagerClass = RecursiveAI.OneLite.Storage.Pager.Pager;

namespace RecursiveAI.OneLite.Storage.BTree;

/// <summary>
/// The B-tree's page source on top of a <see cref="PagerClass"/>: every page read, edit, allocation, free and pointer-map write goes
/// through the pager, so the rollback journal, the cache and the locks see all of it. The statuses map one to one
/// (<see cref="PagerStatus"/> to <see cref="BtStatus"/>): after <see cref="BtStatus.Pending"/> the caller runs the pager's outstanding
/// request (<see cref="PagerClass.ExecuteSync"/> or <c>ExecuteAsync</c> on <see cref="Pager"/>) and repeats the same call.
/// The owner opens the pager, begins read and write transactions and commits; this class only forwards page traffic. It keeps a pool of
/// handles, so steady-state page access allocates nothing.
/// </summary>
public sealed class PagerPageSource : IBtPageSource
{
    private readonly PagerClass _pager;
    private readonly Stack<FrameHandle> _pool = new();

    /// <summary>Creates the source over an open pager.</summary>
    /// <param name="pager">The pager. It must stay open as long as the source is used.</param>
    public PagerPageSource(PagerClass pager)
    {
        ArgumentNullException.ThrowIfNull(pager);
        _pager = pager;
    }

    /// <summary>The pager, for running pending requests and driving transactions.</summary>
    public PagerClass Pager => _pager;

    /// <inheritdoc/>
    public int PageSize => _pager.PageSize;

    /// <inheritdoc/>
    public int UsableSize => _pager.UsableSize;

    /// <inheritdoc/>
    public uint PageCount => _pager.PageCount;

    /// <inheritdoc/>
    public bool AutoVacuum => _pager.AutoVacuum;

    /// <inheritdoc/>
    public Exception? LastError => _pager.Error;

    /// <inheritdoc/>
    public uint LargestRootPage
    {
        get => _pager.LargestRootPage;
        set
        {
            // Reserve() arranges page 1 in the cache, writable, so this does not suspend (pager.md, "Reserve, then effect").
            if (_pager.SetLargestRootPage(value) != PagerStatus.Ok)
            {
                throw _pager.Error ?? OneLiteException.Corrupt(1, "the largest root page could not be written");
            }
        }
    }

    private sealed class FrameHandle : PageHandle
    {
        public PageFrame? Frame;

        public override uint Number => Frame!.PageNumber;

        public override byte[] Data => Frame!.Data;
    }

    private static BtStatus Map(PagerStatus s) => s switch
    {
        PagerStatus.Ok => BtStatus.Ok,
        PagerStatus.Pending => BtStatus.Pending,
        _ => BtStatus.Error,
    };

    private FrameHandle Wrap(PageFrame f)
    {
        FrameHandle h = _pool.Count > 0 ? _pool.Pop() : new FrameHandle();
        h.Frame = f;
        return h;
    }

    /// <inheritdoc/>
    public BtStatus GetPage(uint pgno, out PageHandle? page)
    {
        page = null;
        PagerStatus s = _pager.Get(pgno, GetOptions.None, out PageFrame? f);
        if (s != PagerStatus.Ok)
        {
            return Map(s);
        }

        page = Wrap(f!);
        return BtStatus.Ok;
    }

    /// <inheritdoc/>
    public void Release(PageHandle page)
    {
        var h = (FrameHandle)page;
        _pager.Release(h.Frame!);
        h.Frame = null;
        _pool.Push(h);
    }

    /// <inheritdoc/>
    public BtStatus MakeWritable(PageHandle page) => Map(_pager.MakeWritable(((FrameHandle)page).Frame!));

    /// <inheritdoc/>
    public BtStatus Reserve(int allocations, int frees) => Map(_pager.ReservePages(allocations, frees));

    /// <inheritdoc/>
    public void EndEffect() => _pager.EndReservePhase();

    /// <inheritdoc/>
    public BtStatus ReservePtrmap(uint pgno) => Map(_pager.ReservePtrmap(pgno));

    /// <inheritdoc/>
    public bool IsPtrmapPage(uint pgno) => _pager.IsPtrmapPage(pgno);

    /// <inheritdoc/>
    public BtStatus PtrmapGet(uint pgno, out byte type, out uint parent)
    {
        PagerStatus s = _pager.PtrmapGet(pgno, out PtrmapType t, out parent);
        type = (byte)t;
        return Map(s);
    }

    /// <inheritdoc/>
    public PageHandle Allocate()
    {
        PagerStatus s = _pager.Allocate(out PageFrame? f);
        if (s != PagerStatus.Ok)
        {
            throw Unexpected(s, "Allocate");
        }

        return Wrap(f!);
    }

    /// <inheritdoc/>
    public void Free(uint pgno)
    {
        PagerStatus s = _pager.Free(pgno);
        if (s != PagerStatus.Ok)
        {
            throw Unexpected(s, "Free");
        }

        if (_pager.AutoVacuum && pgno >= 3 && !_pager.IsPtrmapPage(pgno))
        {
            // fileformat2 §1.7: a free page has pointer-map type 2 and parent 0.
            PtrmapPut(pgno, 2, 0);
        }
    }

    /// <inheritdoc/>
    public void PtrmapPut(uint pgno, byte type, uint parent)
    {
        if (!_pager.AutoVacuum)
        {
            return;
        }

        PagerStatus s = _pager.PtrmapPut(pgno, (PtrmapType)type, parent);
        if (s != PagerStatus.Ok)
        {
            throw Unexpected(s, $"PtrmapPut({pgno}, type {type}, parent {parent}; its pointer-map page is {_pager.PtrmapPageOf(pgno)}, database {_pager.PageCount} pages)");
        }
    }

    // After Reserve, these calls never suspend; a Pending here is a bug in the reserve phase, and an Error carries the pager's exception.
    private Exception Unexpected(PagerStatus s, string call) =>
        s == PagerStatus.Error && _pager.Error != null
            ? _pager.Error
            : new InvalidOperationException($"{call} suspended in the effect phase ({_pager.PendingIo.Phase}); the reserve phase did not cover it");
}
