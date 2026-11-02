using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite;

// The temp database (lang_naming "temp"): a second, private database that lives in memory for as long as the connection does, with its own
// pager, B-trees and sqlite_temp_schema. Its pager stays in a write transaction so that every statement can use it; the connection commits,
// rolls back and opens savepoints on it in step with the main database.
internal sealed class TempDb : IDisposable
{
    private readonly StorageEngine _engine;
    private BtContext? _ctx;

    internal TempDb(TextEncoding encoding)
    {
        var po = new PagerOptions
        {
            PageSize = 4096,
            Encoding = encoding,
            JournalMode = JournalMode.Memory,
            Synchronous = SynchronousMode.Off,
            CacheSize = -8000,
        };
        _engine = StorageEngine.Open(new MemoryVfs(), "temp", po);
        Reopen();
    }

    internal Pager Pager => _engine.Pager;

    internal PagerPageSource Source => _engine.Source;

    // The B-tree context over a page source that satisfies the memory pager's I/O on the spot, so a cursor on a temp table never suspends.
    internal BtContext Context => _ctx ??= new BtContext(new InlineSource(_engine.Source), _engine.Context.Encoding);

    internal StorageEngine Engine => _engine;

    // (Re)enters the always-open write transaction.
    internal void Reopen()
    {
        if (!_engine.Pager.InTransaction)
        {
            _engine.BeginRead();
        }

        _engine.EnsureContext();
        if (!_engine.Pager.InWriteTransaction)
        {
            _engine.BeginWrite();
        }
    }

    internal void Commit()
    {
        _engine.Commit();
        Reopen();
    }

    internal void Rollback()
    {
        _engine.Rollback();
        Reopen();
    }

    public void Dispose() => _engine.Dispose();
}

// Runs the pager's requests inline: the temp database lives in memory, so there is nothing to wait for.
internal sealed class InlineSource : IBtPageSource
{
    private readonly PagerPageSource _inner;

    internal InlineSource(PagerPageSource inner) => _inner = inner;

    private BtStatus Run(Func<BtStatus> op)
    {
        BtStatus s;
        while ((s = op()) == BtStatus.Pending)
        {
            _inner.Pager.ExecuteSync();
        }

        return s;
    }

    public int PageSize => _inner.PageSize;

    public int UsableSize => _inner.UsableSize;

    public uint PageCount => _inner.PageCount;

    public bool AutoVacuum => _inner.AutoVacuum;

    public Exception? LastError => _inner.LastError;

    public BtStatus GetPage(uint pgno, out PageHandle? page)
    {
        PageHandle? h = null;
        BtStatus s = Run(() => _inner.GetPage(pgno, out h));
        page = h;
        return s;
    }

    public void Release(PageHandle page) => _inner.Release(page);

    public BtStatus MakeWritable(PageHandle page) => Run(() => _inner.MakeWritable(page));

    public BtStatus Reserve(int allocations, int frees) => Run(() => _inner.Reserve(allocations, frees));

    public BtStatus ReservePtrmap(uint pgno) => Run(() => _inner.ReservePtrmap(pgno));

    public void EndEffect() => _inner.EndEffect();

    public PageHandle Allocate() => _inner.Allocate();

    public void Free(uint pgno) => _inner.Free(pgno);

    public void PtrmapPut(uint pgno, byte type, uint parent) => _inner.PtrmapPut(pgno, type, parent);

    public bool IsPtrmapPage(uint pgno) => _inner.IsPtrmapPage(pgno);

    public BtStatus PtrmapGet(uint pgno, out byte type, out uint parent)
    {
        byte t = 0;
        uint p = 0;
        BtStatus s = Run(() => _inner.PtrmapGet(pgno, out t, out p));
        type = t;
        parent = p;
        return s;
    }

    public uint LargestRootPage
    {
        get => _inner.LargestRootPage;
        set => _inner.LargestRootPage = value;
    }
}
