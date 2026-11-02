using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Storage.BTree;

/// <summary>
/// A whole database file held in memory, as an <see cref="IBtPageSource"/>: pages, the freelist (trunk and leaf pages) and, for
/// auto-vacuum databases, the pointer map. It stands in for the pager wherever the B-tree needs a page source without a file
/// (tests, the interop reader and writer, fuzz targets). <see cref="YieldEvery"/> turns every n-th page request into
/// <see cref="BtStatus.Pending"/> so the restart paths run.
/// </summary>
public sealed class ImagePageSource : IBtPageSource
{
    private sealed class Page : PageHandle
    {
        public Page(uint n, byte[] d)
        {
            Number = n;
            Data = d;
        }

        public override uint Number { get; }

        public override byte[] Data { get; }

        public int Pins;
    }

    private readonly List<Page?> _pages = [null];
    private readonly HashSet<uint> _ready = [];
    private readonly HashSet<uint> _readyMap = [];
    private readonly int _pageSize;
    private readonly int _reserved;
    private long _requests;
    private string? _exemptKind;
    private uint _exemptPage;
    private bool _awaiting;

    private ImagePageSource(int pageSize, int reserved)
    {
        _pageSize = pageSize;
        _reserved = reserved;
        LastError = null;
    }

    /// <summary>When positive, every n-th request that could miss the cache returns Pending once (the retry then succeeds).</summary>
    public int YieldEvery { get; set; }

    /// <summary>How many Pending results were issued.</summary>
    public long PendingCount { get; private set; }

    /// <inheritdoc/>
    public int PageSize => _pageSize;

    /// <inheritdoc/>
    public int UsableSize => _pageSize - _reserved;

    /// <inheritdoc/>
    public uint PageCount { get; private set; }

    /// <inheritdoc/>
    public bool AutoVacuum => Header32(52) != 0;

    /// <inheritdoc/>
    public Exception? LastError { get; private set; }

    /// <summary>The database text encoding from the header.</summary>
    public TextEncoding Encoding => (TextEncoding)Math.Clamp((int)Header32(56), 1, 3);

    /// <summary>The number of pinned pages (a leak check for tests).</summary>
    public int PinnedPages
    {
        get
        {
            int n = 0;
            foreach (Page? p in _pages)
            {
                if (p != null)
                {
                    n += p.Pins;
                }
            }

            return n;
        }
    }

    /// <inheritdoc/>
    public uint LargestRootPage
    {
        get => Header32(52);
        set => SetHeader32(52, value);
    }

    /// <summary>The first freelist trunk page, or 0.</summary>
    public uint FreelistTrunk => Header32(32);

    /// <summary>The number of freelist pages (trunks and leaves).</summary>
    public uint FreelistCount => Header32(36);

    /// <summary>Whether this is an incremental-vacuum database (header offset 64).</summary>
    public bool IncrementalVacuum => Header32(64) != 0;

    /// <summary>Header offset 60, the user version.</summary>
    public uint UserVersion
    {
        get => Header32(60);
        set => SetHeader32(60, value);
    }

    /// <summary>Header offset 40, the schema cookie.</summary>
    public uint SchemaCookie
    {
        get => Header32(40);
        set => SetHeader32(40, value);
    }

    private uint Header32(int off) => Pg.U32(_pages[1]!.Data, off);

    private void SetHeader32(int off, uint v) => Pg.PutU32(_pages[1]!.Data, off, v);

    /// <summary>Creates an empty database: page 1 with the header and an empty <c>sqlite_schema</c> leaf.</summary>
    /// <param name="pageSize">512 to 65536, a power of two.</param>
    /// <param name="reserved">Reserved bytes at the end of every page (0 to 255).</param>
    /// <param name="encoding">Text encoding.</param>
    /// <param name="autoVacuum">0 none, 1 full, 2 incremental.</param>
    public static ImagePageSource Create(int pageSize = 4096, int reserved = 0, TextEncoding encoding = TextEncoding.Utf8, int autoVacuum = 0)
    {
        if (pageSize < 512 || pageSize > 65536 || (pageSize & (pageSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        var s = new ImagePageSource(pageSize, reserved);
        var d = new byte[pageSize];
        "SQLite format 3\0"u8.CopyTo(d);
        Pg.PutU16(d, 16, pageSize == 65536 ? 1 : pageSize);
        d[18] = 1;
        d[19] = 1;
        d[20] = (byte)reserved;
        d[21] = 64;
        d[22] = 32;
        d[23] = 32;
        Pg.PutU32(d, 24, 1);
        Pg.PutU32(d, 28, 1);
        Pg.PutU32(d, 44, 4);
        Pg.PutU32(d, 52, autoVacuum != 0 ? 1u : 0u);
        Pg.PutU32(d, 56, (uint)encoding);
        Pg.PutU32(d, 64, autoVacuum == 2 ? 1u : 0u);
        Pg.PutU32(d, 92, 1);
        Pg.PutU32(d, 96, 3053004);
        d[100] = Pg.TableLeaf;
        Pg.SetContentStart(d, 100, pageSize - reserved);
        s._pages.Add(new Page(1, d));
        s.PageCount = 1;
        return s;
    }

    /// <summary>Loads an image of a database file. The header is validated only as far as page geometry; the rest is the B-tree's job.</summary>
    /// <exception cref="OneLiteException">The geometry fields are out of range, or the image is not a whole number of pages.</exception>
    public static ImagePageSource FromBytes(byte[] image)
    {
        if (image.Length < 512 || !"SQLite format 3\0"u8.SequenceEqual(image.AsSpan(0, 16)))
        {
            throw OneLiteException.Corrupt(1, "not a database file");
        }

        int ps = Pg.U16(image, 16);
        if (ps == 1)
        {
            ps = 65536;
        }

        if (ps < 512 || ps > 65536 || (ps & (ps - 1)) != 0)
        {
            throw OneLiteException.Corrupt(1, $"page size {ps} is not valid");
        }

        int reserved = image[20];
        if (ps - reserved < 480)
        {
            throw OneLiteException.Corrupt(1, "reserved bytes leave fewer than 480 usable");
        }

        uint nPages = Pg.U32(image, 28);
        if (nPages == 0 || Pg.U32(image, 92) != Pg.U32(image, 24) || (long)nPages * ps > image.Length)
        {
            nPages = (uint)(image.Length / ps);
        }

        if (nPages == 0)
        {
            throw OneLiteException.Corrupt(1, "the image is shorter than one page");
        }

        var s = new ImagePageSource(ps, reserved);
        for (uint i = 1; i <= nPages; i++)
        {
            var d = new byte[ps];
            Buffer.BlockCopy(image, (int)((i - 1) * ps), d, 0, ps);
            s._pages.Add(new Page(i, d));
        }

        s.PageCount = nPages;
        int enc = (int)Pg.U32(s._pages[1]!.Data, 56);
        if (enc is < 1 or > 3)
        {
            throw OneLiteException.Corrupt(1, $"text encoding {enc} is not valid");
        }

        return s;
    }

    /// <summary>Serializes the image as a database file: stamps the size in pages and bumps the change counter.</summary>
    public byte[] ToBytes()
    {
        byte[] h = _pages[1]!.Data;
        Pg.PutU32(h, 28, PageCount);
        uint cc = Pg.U32(h, 24) + 1;
        Pg.PutU32(h, 24, cc);
        Pg.PutU32(h, 92, cc);
        var o = new byte[(long)PageCount * _pageSize];
        for (uint i = 1; i <= PageCount; i++)
        {
            Buffer.BlockCopy(_pages[(int)i]!.Data, 0, o, (int)((i - 1) * _pageSize), _pageSize);
        }

        return o;
    }

    /// <summary>Acknowledges the outstanding Pending request; the next identical call succeeds.</summary>
    public void Complete()
    {
        _awaiting = false;
    }

    private bool Inject(string kind, uint page)
    {
        if (YieldEvery <= 0)
        {
            return false;
        }

        if (_exemptKind == kind && _exemptPage == page && !_awaiting)
        {
            _exemptKind = null;
            return false;
        }

        if (_awaiting)
        {
            return true;
        }

        if (++_requests % YieldEvery == 0)
        {
            _exemptKind = kind;
            _exemptPage = page;
            _awaiting = true;
            PendingCount++;
            return true;
        }

        return false;
    }

    private BtStatus Fail(Exception e)
    {
        LastError = e;
        return BtStatus.Error;
    }

    /// <summary>The page holding the lock bytes at file offset 0x40000000; it is never used (fileformat2 §1.3).</summary>
    public uint LockBytePage => (uint)(0x40000000L / _pageSize) + 1;

    /// <inheritdoc/>
    public BtStatus GetPage(uint pgno, out PageHandle? page)
    {
        page = null;
        if (pgno == 0 || pgno > PageCount)
        {
            return Fail(OneLiteException.Corrupt(pgno, $"page number {pgno} is outside the database of {PageCount} pages"));
        }

        if (pgno == LockBytePage)
        {
            return Fail(OneLiteException.Corrupt(pgno, "page is the lock-byte page"));
        }

        if (Inject("get", pgno))
        {
            return BtStatus.Pending;
        }

        Page p = _pages[(int)pgno]!;
        p.Pins++;
        page = p;
        return BtStatus.Ok;
    }

    /// <inheritdoc/>
    public void Release(PageHandle page)
    {
        var p = (Page)page;
        if (p.Pins <= 0)
        {
            throw new InvalidOperationException("page released more often than it was pinned");
        }

        p.Pins--;
    }

    /// <inheritdoc/>
    public BtStatus MakeWritable(PageHandle page)
    {
        // Like a pager, only the first call per page can need I/O (the journal original); later calls are free.
        if (_ready.Contains(page.Number))
        {
            return BtStatus.Ok;
        }

        if (Inject("write", page.Number))
        {
            return BtStatus.Pending;
        }

        _ready.Add(page.Number);
        return BtStatus.Ok;
    }

    private int _allocBudget;
    private int _freeBudget;

    /// <inheritdoc/>
    public BtStatus Reserve(int allocations, int frees)
    {
        if (Inject("reserve", (uint)(allocations * 1000 + frees)))
        {
            return BtStatus.Pending;
        }

        // Like the pager, allocations and frees are only possible up to what was reserved; the tests rely on this to catch a writer that
        // reserves too little (a real pager would have to suspend in the effect phase).
        _allocBudget += allocations;
        _freeBudget += frees;
        return BtStatus.Ok;
    }

    /// <inheritdoc/>
    public BtStatus ReservePtrmap(uint pgno)
    {
        if (!AutoVacuum || _readyMap.Contains(PtrmapPageOf(pgno)))
        {
            return BtStatus.Ok;
        }

        if (Inject("ptrmap", PtrmapPageOf(pgno)))
        {
            return BtStatus.Pending;
        }

        _readyMap.Add(PtrmapPageOf(pgno));
        return BtStatus.Ok;
    }

    private uint PtrmapGroup => (uint)(UsableSize / 5 + 1);

    /// <summary>The pointer-map page that describes <paramref name="pgno"/> (fileformat2 §1.7).</summary>
    public uint PtrmapPageOf(uint pgno) => pgno < 2 ? 0 : ((pgno - 2) / PtrmapGroup) * PtrmapGroup + 2;

    /// <summary>True if <paramref name="pgno"/> is itself a pointer-map page.</summary>
    public bool IsPtrmapPage(uint pgno) => AutoVacuum && pgno >= 2 && PtrmapPageOf(pgno) == pgno;

    /// <inheritdoc/>
    public void PtrmapPut(uint pgno, byte type, uint parent)
    {
        if (!AutoVacuum || pgno < 3 || IsPtrmapPage(pgno))
        {
            return;
        }

        uint mp = PtrmapPageOf(pgno);
        if (mp > PageCount)
        {
            throw new InvalidOperationException("pointer-map page does not exist yet");
        }

        // Like the pager: the entry's page must have been reserved (or belong to a page this transaction allocated).
        if (!_readyMap.Contains(mp))
        {
            throw new InvalidOperationException($"PtrmapPut({pgno}) without ReservePtrmap");
        }

        byte[] d = _pages[(int)mp]!.Data;
        int off = 5 * (int)(pgno - mp - 1);
        d[off] = type;
        Pg.PutU32(d, off + 1, parent);
    }

    /// <summary>Reads a pointer-map entry.</summary>
    public (byte Type, uint Parent) PtrmapGet(uint pgno)
    {
        uint mp = PtrmapPageOf(pgno);
        byte[] d = _pages[(int)mp]!.Data;
        int off = 5 * (int)(pgno - mp - 1);
        return (d[off], Pg.U32(d, off + 1));
    }

    /// <inheritdoc/>
    BtStatus IBtPageSource.PtrmapGet(uint pgno, out byte type, out uint parent)
    {
        (type, parent) = PtrmapGet(pgno);
        return BtStatus.Ok;
    }

    private Page Extend()
    {
        uint n = PageCount + 1;
        while (n == LockBytePage || IsPtrmapPage(n))
        {
            _pages.Add(new Page(n, new byte[_pageSize]));
            PageCount = n;
            n++;
        }

        var p = new Page(n, new byte[_pageSize]);
        _pages.Add(p);
        PageCount = n;
        if (AutoVacuum)
        {
            _readyMap.Add(PtrmapPageOf(n));
        }

        return p;
    }

    private int MaxTrunkLeaves => UsableSize / 4 - 8;

    private Page TrunkPage(uint trunk)
    {
        if (trunk < 2 || trunk > PageCount || trunk == LockBytePage)
        {
            throw OneLiteException.Corrupt(trunk, "freelist trunk is not a page of the file");
        }

        return _pages[(int)trunk]!;
    }

    /// <inheritdoc/>
    public PageHandle Allocate()
    {
        if (_allocBudget <= 0)
        {
            throw new InvalidOperationException("Allocate beyond what Reserve covered");
        }

        _allocBudget--;
        uint trunk = FreelistTrunk;
        Page p;
        if (trunk != 0)
        {
            Page t = TrunkPage(trunk);
            uint leaves = Pg.U32(t.Data, 4);
            if (leaves > MaxTrunkLeaves)
            {
                throw OneLiteException.Corrupt(trunk, $"freelist trunk claims {leaves} leaves");
            }

            if (FreelistCount == 0)
            {
                throw OneLiteException.Corrupt(trunk, "freelist count is zero but a trunk exists");
            }

            if (leaves > 0)
            {
                uint leaf = Pg.U32(t.Data, 8 + 4 * (int)(leaves - 1));
                if (leaf < 2 || leaf > PageCount || leaf == LockBytePage)
                {
                    throw OneLiteException.Corrupt(trunk, $"freelist leaf {leaf} is not a page of the file");
                }

                Pg.PutU32(t.Data, 4, leaves - 1);
                p = _pages[(int)leaf]!;
            }
            else
            {
                uint nextTrunk = Pg.U32(t.Data, 0);
                if (nextTrunk > PageCount)
                {
                    throw OneLiteException.Corrupt(trunk, $"next freelist trunk {nextTrunk} is not a page of the file");
                }

                SetHeader32(32, nextTrunk);
                p = t;
            }

            SetHeader32(36, FreelistCount - 1);
            Array.Clear(p.Data);
        }
        else
        {
            p = Extend();
        }

        if (AutoVacuum && p.Number >= 3 && !IsPtrmapPage(p.Number))
        {
            _readyMap.Add(PtrmapPageOf(p.Number));
        }

        p.Pins++;
        return p;
    }

    /// <inheritdoc/>
    public void Free(uint pgno)
    {
        if (_freeBudget <= 0)
        {
            throw new InvalidOperationException("Free beyond what Reserve covered");
        }

        _freeBudget--;
        if (pgno < 2 || pgno > PageCount)
        {
            throw OneLiteException.Corrupt(pgno, "a page outside the file is freed");
        }

        Page p = _pages[(int)pgno]!;
        uint trunk = FreelistTrunk;
        int maxLeaves = MaxTrunkLeaves;
        if (trunk != 0)
        {
            Page t = TrunkPage(trunk);
            uint n = Pg.U32(t.Data, 4);
            if (n < maxLeaves)
            {
                Pg.PutU32(t.Data, 8 + 4 * (int)n, pgno);
                Pg.PutU32(t.Data, 4, n + 1);
                SetHeader32(36, FreelistCount + 1);
                PtrmapPut(pgno, 2, 0);
                return;
            }
        }

        Array.Clear(p.Data);
        Pg.PutU32(p.Data, 0, trunk);
        SetHeader32(32, pgno);
        SetHeader32(36, FreelistCount + 1);
        PtrmapPut(pgno, 2, 0);
    }
}
