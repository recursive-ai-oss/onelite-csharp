using System;
using System.Threading.Tasks;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Pager;

namespace RecursiveAI.OneLite.BrowserTests;

/// <summary>Runs the pager's and the B-tree's restartable calls to their end on the chosen driver: inline for the worker backend, awaited for the main-thread one.</summary>
internal sealed class Drive(Pager pager, bool sync)
{
    public Pager Pager { get; } = pager;

    public async Task Run(Func<PagerStatus> op)
    {
        for (; ; )
        {
            PagerStatus s = op();
            if (s == PagerStatus.Ok)
            {
                return;
            }

            if (s == PagerStatus.Error)
            {
                throw (Exception?)Pager.Error ?? new InvalidOperationException("pager error without an exception");
            }

            if (sync)
            {
                Pager.ExecuteSync();
            }
            else
            {
                await Pager.ExecuteAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task RunBt(Func<BtStatus> op, Func<Exception?> error)
    {
        for (; ; )
        {
            BtStatus s = op();
            if (s == BtStatus.Ok)
            {
                return;
            }

            if (s == BtStatus.Error)
            {
                throw error() ?? new InvalidOperationException("B-tree error without an exception");
            }

            if (sync)
            {
                Pager.ExecuteSync();
            }
            else
            {
                await Pager.ExecuteAsync().ConfigureAwait(false);
            }
        }
    }

    public Task BeginRead() => Run(Pager.BeginRead);

    public Task EndRead() => Run(Pager.EndRead);

    public Task BeginWrite() => Run(Pager.BeginWrite);

    public Task Commit() => Run(Pager.Commit);

    public Task Rollback() => Run(Pager.Rollback);

    public async Task<PageFrame> Get(uint pgno)
    {
        PageFrame? f = null;
        await Run(() => Pager.Get(pgno, GetOptions.None, out f)).ConfigureAwait(false);
        return f!;
    }

    public async Task<PageFrame> Allocate()
    {
        PageFrame? f = null;
        await Run(() => Pager.Allocate(out f)).ConfigureAwait(false);
        return f!;
    }

    public Task MakeWritable(PageFrame f) => Run(() => Pager.MakeWritable(f));

    public async Task<byte[]> Page(uint pgno)
    {
        PageFrame f = await Get(pgno).ConfigureAwait(false);
        byte[] copy = (byte[])f.Data.Clone();
        Pager.Release(f);
        return copy;
    }
}

/// <summary>The B-tree's page source over a pager that is in a write (or read) transaction.</summary>
internal sealed class PagerBtSource(Pager pager) : IBtPageSource
{
    private readonly System.Collections.Generic.Stack<Handle> _free = new();

    private sealed class Handle : PageHandle
    {
        internal PageFrame? Frame;

        public override uint Number => Frame!.PageNumber;

        public override byte[] Data => Frame!.Data;
    }

    public int PageSize => pager.PageSize;

    public int UsableSize => pager.UsableSize;

    public uint PageCount => pager.PageCount;

    public bool AutoVacuum => pager.AutoVacuum;

    public Exception? LastError { get; private set; }

    private BtStatus Map(PagerStatus s)
    {
        if (s == PagerStatus.Error)
        {
            LastError = pager.Error;
            return BtStatus.Error;
        }

        return s == PagerStatus.Pending ? BtStatus.Pending : BtStatus.Ok;
    }

    private Handle Wrap(PageFrame f)
    {
        Handle h = _free.Count > 0 ? _free.Pop() : new Handle();
        h.Frame = f;
        return h;
    }

    public BtStatus GetPage(uint pgno, out PageHandle? page)
    {
        page = null;
        BtStatus s = Map(pager.Get(pgno, GetOptions.None, out PageFrame? f));
        if (s == BtStatus.Ok)
        {
            page = Wrap(f!);
        }

        return s;
    }

    public void Release(PageHandle page)
    {
        var h = (Handle)page;
        pager.Release(h.Frame!);
        h.Frame = null;
        _free.Push(h);
    }

    public BtStatus MakeWritable(PageHandle page) => Map(pager.MakeWritable(((Handle)page).Frame!));

    public BtStatus Reserve(int allocations, int frees) => Map(pager.ReservePages(allocations, frees));

    public BtStatus ReservePtrmap(uint pgno) => Map(pager.ReservePtrmap(pgno));

    public PageHandle Allocate()
    {
        if (pager.Allocate(out PageFrame? f) != PagerStatus.Ok)
        {
            throw (Exception?)pager.Error ?? new InvalidOperationException("allocation needed I/O after Reserve");
        }

        return Wrap(f!);
    }

    public void Free(uint pgno)
    {
        if (pager.Free(pgno) != PagerStatus.Ok)
        {
            throw (Exception?)pager.Error ?? new InvalidOperationException("free needed I/O after Reserve");
        }
    }

    public void PtrmapPut(uint pgno, byte type, uint parent)
    {
        if (pager.PtrmapPut(pgno, (PtrmapType)type, parent) != PagerStatus.Ok)
        {
            throw (Exception?)pager.Error ?? new InvalidOperationException("pointer-map write needed I/O after Reserve");
        }
    }

    public uint LargestRootPage
    {
        get => pager.LargestRootPage;
        set
        {
            if (pager.SetLargestRootPage(value) != PagerStatus.Ok)
            {
                throw (Exception?)pager.Error ?? new InvalidOperationException("header write needed I/O");
            }
        }
    }
}
