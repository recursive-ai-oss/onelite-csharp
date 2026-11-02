using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Storage.BTree;

/// <summary>
/// Builds the compacted image of a database for VACUUM (lang_vacuum): every B-tree is read in key order and written again with its pages
/// filled as full as the cells allow (what loading the rows in order into an empty tree gives), overflow chains are copied to new page
/// numbers, and the pointer-map pages of an auto-vacuum image are generated. Free pages, leaked pages and the old pointer map are simply
/// not copied. The caller chooses the root
/// page numbers first (<see cref="NextRoot"/>), updates sqlite_schema accordingly, and then calls <see cref="Build"/>.
/// The whole image is held in memory, so an encrypted database never puts plaintext in a temporary file.
/// </summary>
internal sealed class VacuumImage
{
    private readonly BtContext _ctx;
    private readonly IBtPageSource _src;
    private readonly Action _pump;
    private readonly bool _av;
    private readonly uint _perMap;
    private readonly uint _lockByte;
    private uint _last = 1;
    private uint _largestRoot = 1;
    private byte[]?[] _pages = [];
    private byte[] _ptType = [];
    private uint[] _ptParent = [];
    private byte[] _seen = [];

    /// <param name="ctx">The context over the database being vacuumed.</param>
    /// <param name="pump">Runs the source's outstanding I/O inline.</param>
    /// <param name="autoVacuum">Whether the image gets pointer-map pages.</param>
    public VacuumImage(BtContext ctx, Action pump, bool autoVacuum)
    {
        _ctx = ctx;
        _src = ctx.Source;
        _pump = pump;
        _av = autoVacuum;
        _perMap = (uint)(ctx.Usable / 5);
        _lockByte = (uint)(0x40000000L / ctx.PageSize) + 1;
    }

    // fileformat2 §1.7: page 2 and then every (usable/5 + 1)'th page is a pointer-map page.
    private bool IsPtrmap(uint p) => _av && p >= 2 && (p - 2) % (_perMap + 1) == 0;

    private uint Next()
    {
        do
        {
            _last++;
        }
        while (IsPtrmap(_last) || _last == _lockByte);
        return _last;
    }

    /// <summary>The page number of the next root: roots come first so that every root lies below every other page (fileformat2 §1.7).</summary>
    public uint NextRoot()
    {
        uint r = Next();
        _largestRoot = r;
        return r;
    }

    private PageHandle Read(uint pgno)
    {
        for (; ; )
        {
            BtStatus s = _src.GetPage(pgno, out PageHandle? h);
            if (s == BtStatus.Ok)
            {
                return h!;
            }

            if (s == BtStatus.Error)
            {
                throw _src.LastError!;
            }

            _pump();
        }
    }

    private void MarkSeen(uint old)
    {
        if (old == 0 || old > _src.PageCount)
        {
            throw OneLiteException.Corrupt(old, "page number is outside the database");
        }

        if (_seen[old] != 0)
        {
            throw OneLiteException.Corrupt(old, "page is used twice (a loop or a shared page)");
        }

        _seen[old] = 1;
    }

    private void Map(uint pg, byte type, uint parent)
    {
        _ptType[pg] = type;
        _ptParent[pg] = parent;
    }

    /// <summary>
    /// Copies the trees rooted at the given old pages.
    /// </summary>
    /// <param name="roots">Old root page to the new root page chosen with <see cref="NextRoot"/>. Page 1 maps to 1 and is always copied.</param>
    /// <param name="header">The old page 1 (header), whose fields the image keeps except the ones this method resets.</param>
    /// <param name="incremental">The incremental-vacuum flag of the image.</param>
    /// <param name="sink">When set, each page is handed over as soon as it is final (page 1 last) and none are kept, so memory holds one tree; the returned list is then empty.</param>
    /// <param name="emptySchemaTree">Leave sqlite_schema empty (the caller inserts its rows, with the new root pages, afterwards).</param>
    /// <param name="rollbackJournalHeader">Write the rollback-journal version bytes (18 and 19) whatever the old header said.</param>
    /// <returns>The new pages, page 1 first.</returns>
    public List<byte[]> Build(IReadOnlyDictionary<uint, uint> roots, byte[] header, bool incremental, Action<uint, byte[]>? sink = null, bool emptySchemaTree = false, bool rollbackJournalHeader = false)
    {
        _sink = sink;
        uint oldCount = _src.PageCount;
        long cap = (long)oldCount + (oldCount / Math.Max(1, _perMap)) + 8;
        if (cap > int.MaxValue / 2)
        {
            throw new OneLiteException(ResultCode.Full, "database is too large to vacuum in memory");
        }

        _pages = new byte[cap][];
        _ptType = new byte[cap];
        _ptParent = new uint[cap];
        _seen = new byte[oldCount + 1];

        Repack(1, 1, emptySchemaTree);
        foreach (KeyValuePair<uint, uint> kv in roots)
        {
            if (kv.Key != 1)
            {
                Repack(kv.Key, kv.Value);
            }
        }

        uint n = _last;
        PageCount = n;
        var result = new List<byte[]>(_sink is null ? (int)n : 1);
        for (uint p = 1; p <= n; p++)
        {
            if (IsPtrmap(p))
            {
                byte[] mapPage = BuildPtrmapPage(p, n);
                if (_sink is null)
                {
                    result.Add(mapPage);
                }
                else
                {
                    _sink(p, mapPage);
                }
            }
            else if (_sink is null || p == 1)
            {
                result.Add(_pages[p] ?? new byte[_ctx.PageSize]);
            }
            else if (_pages[p] is { } left)
            {
                _sink(p, left);
                _pages[p] = null;
            }
        }

        // The header: everything of the old one except the freelist (now empty), the auto-vacuum fields and the schema cookie (VACUUM changes the schema).
        byte[] p1 = result[0];
        Buffer.BlockCopy(header, 0, p1, 0, 100);
        Pg.PutU32(p1, DatabaseHeader.FreelistTrunkOffset, 0);
        Pg.PutU32(p1, DatabaseHeader.FreelistCountOffset, 0);
        Pg.PutU32(p1, DatabaseHeader.LargestRootOffset, _av ? _largestRoot : 0);
        Pg.PutU32(p1, DatabaseHeader.IncrementalVacuumOffset, _av && incremental ? 1u : 0u);
        Pg.PutU32(p1, DatabaseHeader.SchemaCookieOffset, unchecked(Pg.U32(header, DatabaseHeader.SchemaCookieOffset) + 1));
        Pg.PutU32(p1, DatabaseHeader.DatabaseSizeOffset, n);
        if (rollbackJournalHeader)
        {
            // fileformat2 §1.3: bytes 18 and 19 are the file-format versions for writing and reading; 1 means the rollback journal.
            p1[18] = 1;
            p1[19] = 1;
        }

        if (_sink is not null)
        {
            _sink(1, p1);
            result.Clear();
        }

        return result;
    }

    private Action<uint, byte[]>? _sink;

    /// <summary>The number of pages of the image, known once <see cref="Build"/> returned.</summary>
    public uint PageCount { get; private set; }

    private byte[] BuildPtrmapPage(uint m, uint n)
    {
        var page = new byte[_ctx.PageSize];
        for (uint i = 1; i <= _perMap && m + i <= n; i++)
        {
            uint p = m + i;
            if (p == _lockByte)
            {
                continue;
            }

            int o = (int)(i - 1) * 5;
            page[o] = _ptType[p];
            Pg.PutU32(page, o + 1, _ptParent[p]);
            if (_ptType[p] == 0)
            {
                throw new InvalidOperationException($"vacuum left page {p} without a pointer-map entry");
            }
        }

        return page;
    }

    // ------------------------------------------------------------------ repacking

    /// <summary>One entry of a tree in key order: a whole leaf cell (an index entry taken from an interior page loses its child pointer) and its overflow chain.</summary>
    private sealed class Entry
    {
        public byte[] Cell = [];
        public long Rowid;
        public byte[][]? Chain;
    }

    /// <summary>A page of the new tree. Leaves hold <see cref="Entries"/>; interior pages hold <see cref="Kids"/> and, in an index, <see cref="Divs"/> (one per kid but the last).</summary>
    private sealed class Node
    {
        public List<Entry>? Entries;
        public List<Node>? Kids;
        public List<Entry>? Divs;
        public long MaxRowid;
        public uint Page;
    }

    private bool _intKey;
    private bool _kindSet;

    private void Collect(uint old, int depth, Action<Entry> take)
    {
        if (depth >= BtCursor.MaxDepth)
        {
            throw OneLiteException.Corrupt(old, "b-tree is deeper than the format allows");
        }

        MarkSeen(old);
        byte[] img;
        PageHandle h = Read(old);
        try
        {
            img = (byte[])h.Data.Clone();
        }
        finally
        {
            _src.Release(h);
        }

        int hoff = Pg.HdrOff(old);
        byte type = img[hoff];
        if (!Pg.ValidType(type))
        {
            throw OneLiteException.Corrupt(old, $"page type {type} is not a b-tree page");
        }

        if (!_kindSet)
        {
            _intKey = Pg.IsIntKey(type);
            _kindSet = true;
        }
        else if (Pg.IsIntKey(type) != _intKey)
        {
            throw OneLiteException.Corrupt(old, "page type does not match the rest of its b-tree");
        }

        bool leaf = Pg.IsLeaf(type);
        int ncell = Pg.NCell(img, hoff);
        int ptrEnd = Pg.PtrArray(type, hoff) + 2 * ncell;
        if (ptrEnd > _ctx.Usable)
        {
            throw OneLiteException.Corrupt(old, $"cell pointer array ({ncell} cells) runs past the page");
        }

        for (int i = 0; i < ncell; i++)
        {
            int off = Pg.CellOffset(img, hoff, type, i);
            if (off < ptrEnd || off > _ctx.Usable - 4)
            {
                throw OneLiteException.Corrupt(old, $"cell {i} offset {off} outside [{ptrEnd}, {_ctx.Usable - 4}]");
            }

            CellInfo ci = _ctx.ParseCell(old, type, img, off, _ctx.Usable);
            if (!leaf)
            {
                Collect(ci.Child, depth + 1, take);
                if (_intKey)
                {
                    continue;
                }
            }

            if (off + ci.Size > _ctx.Usable)
            {
                throw OneLiteException.Corrupt(old, $"cell {i} runs past the page");
            }

            int skip = leaf ? 0 : 4;
            var e = new Entry { Cell = img.AsSpan(off + skip, ci.Size - skip).ToArray(), Rowid = ci.Rowid };
            if (ci.Overflow != 0)
            {
                e.Chain = ReadChain(old, ci);
            }

            take(e);
        }

        if (!leaf)
        {
            Collect(Pg.RightPtr(img, hoff), depth + 1, take);
        }
    }

    private byte[][] ReadChain(uint oldPage, CellInfo ci)
    {
        long remaining = ci.PayloadSize - ci.Local;
        long chain = (remaining + _ctx.Usable - 5) / (_ctx.Usable - 4);
        var pages = new byte[chain][];
        uint o = ci.Overflow;
        for (long k = 0; k < chain; k++)
        {
            if (o == 0)
            {
                throw OneLiteException.Corrupt(oldPage, "overflow chain of a cell ends early");
            }

            MarkSeen(o);
            PageHandle h = Read(o);
            try
            {
                pages[k] = (byte[])h.Data.Clone();
            }
            finally
            {
                _src.Release(h);
            }

            o = Pg.U32(pages[k], 0);
        }

        return pages;
    }

    private void Repack(uint old, uint dest, bool empty = false)
    {
        _kindSet = false;
        int hoff = Pg.HdrOff(dest);
        int leafAvail = _ctx.Usable - hoff - 8;
        int interiorAvail = _ctx.Usable - hoff - 12;
        var all = new List<Node>();
        var level = new List<Node>();
        var promoted = new List<Entry>();

        // Leaves are filled as the entries arrive and written as soon as they are closed, so a big tree is never held whole: only the open leaf
        // and the one before it (an index may need to move an entry out of it at the end) wait in memory. Cells shorter than 4 bytes still take 4
        // (fileformat2 §1.6).
        Node cur = new() { Entries = [] };
        Node? pend = null;
        int used = 0;
        byte leafType = 0, interiorType = 0;

        void Kind()
        {
            leafType = _intKey ? Pg.TableLeaf : Pg.IndexLeaf;
            interiorType = _intKey ? Pg.TableInterior : Pg.IndexInterior;
        }

        void Emit(Node n, uint page, bool root)
        {
            uint before = _last;
            n.MaxRowid = n.Entries!.Count > 0 ? n.Entries[^1].Rowid : 0;
            n.Page = page;
            WriteNode(n, n.Page == 1 ? 100 : 0, leafType, interiorType, root);
            n.Entries = null;
            HandOver(before, n.Page);
            level.Add(n);
        }

        void Take(Entry e)
        {
            int cost = Math.Max(4, e.Cell.Length) + 2;
            if (used + cost <= leafAvail)
            {
                cur.Entries!.Add(e);
                used += cost;
                return;
            }

            if (pend is not null)
            {
                Emit(pend, Next(), false);
            }

            pend = cur;
            if (_intKey)
            {
                cur = new Node { Entries = [e] };
                used = cost;
            }
            else
            {
                // an index entry that does not fit becomes the divider above, and the leaf after it starts empty
                promoted.Add(e);
                cur = new Node { Entries = [] };
                used = 0;
            }
        }

        if (empty)
        {
            // the caller fills this table-kind tree itself
            _kindSet = true;
            _intKey = true;
        }
        else
        {
            Collect(old, 0, e =>
            {
                if (leafType == 0)
                {
                    Kind();
                }

                Take(e);
            });
        }

        Kind();
        if (!_intKey && cur.Entries!.Count == 0 && pend is not null)
        {
            // never end on an empty leaf: the previous leaf's last entry becomes the divider and the entry just promoted starts the last leaf
            List<Entry> prev = pend.Entries!;
            Entry p = prev[^1];
            prev.RemoveAt(prev.Count - 1);
            cur.Entries!.Add(promoted[^1]);
            promoted[^1] = p;
        }

        if (pend is not null)
        {
            Emit(pend, Next(), false);
        }

        bool single = level.Count == 0;
        Emit(cur, single ? dest : Next(), single);
        uint interiorFrom = _last;

        while (level.Count > 1)
        {
            var next = new List<Node>();
            var nextProm = new List<Entry>();
            if (_intKey)
            {
                Node g = new() { Kids = [] };
                used = 0;
                foreach (Node kid in level)
                {
                    if (g.Kids!.Count > 0)
                    {
                        int cost = 4 + Varint.Length((ulong)g.Kids[^1].MaxRowid) + 2;
                        if (used + cost > interiorAvail)
                        {
                            next.Add(g);
                            g = new Node { Kids = [] };
                            used = 0;
                        }
                        else
                        {
                            used += cost;
                        }
                    }

                    g.Kids.Add(kid);
                }

                if (g.Kids!.Count == 1 && next.Count > 0)
                {
                    List<Node> pk = next[^1].Kids!;
                    g.Kids.Insert(0, pk[^1]);
                    pk.RemoveAt(pk.Count - 1);
                }

                next.Add(g);
                foreach (Node n in next)
                {
                    n.MaxRowid = n.Kids![^1].MaxRowid;
                }
            }
            else
            {
                Node g = new() { Kids = [], Divs = [] };
                used = 0;
                for (int i = 0; i < level.Count - 1; i++)
                {
                    int cost = 4 + Math.Max(4, promoted[i].Cell.Length) + 2;
                    if (used + cost <= interiorAvail)
                    {
                        g.Kids!.Add(level[i]);
                        g.Divs!.Add(promoted[i]);
                        used += cost;
                    }
                    else
                    {
                        g.Kids!.Add(level[i]);
                        next.Add(g);
                        nextProm.Add(promoted[i]);
                        g = new Node { Kids = [], Divs = [] };
                        used = 0;
                    }
                }

                if (g.Kids!.Count == 0 && next.Count > 0)
                {
                    // never end on a page without cells: the previous page gives up its last pair, whose divider goes up instead, and
                    // that page's old right child and the divider just promoted become the pair of the last page
                    Node pv = next[^1];
                    int j = pv.Divs!.Count - 1;
                    Entry dj = pv.Divs[j];
                    Node right = pv.Kids![j + 1];
                    pv.Kids.RemoveAt(j + 1);
                    pv.Divs.RemoveAt(j);
                    g.Kids.Add(right);
                    g.Divs!.Add(nextProm[^1]);
                    nextProm[^1] = dj;
                }

                g.Kids.Add(level[^1]);
                next.Add(g);
            }

            all.AddRange(next);
            promoted = nextProm;
            level = next;
        }

        // Page numbers: the root takes the number chosen for it, the others follow in creation order.
        Node root = level[0];
        foreach (Node n in all)
        {
            n.Page = ReferenceEquals(n, root) ? dest : Next();
        }

        foreach (Node n in all)
        {
            WriteNode(n, hoff: n.Page == 1 ? 100 : 0, leafType, interiorType, ReferenceEquals(n, root));
        }

        HandOver(interiorFrom, dest);
    }

    // With a sink, the pages made since `from` (and the page `also`) are final: pass them on and forget them. Page 1 waits for the header.
    private void HandOver(uint from, uint also)
    {
        if (_sink is null)
        {
            return;
        }

        for (uint p = from + 1; p <= _last; p++)
        {
            Hand(p);
        }

        Hand(also);
    }

    private void Hand(uint p)
    {
        if (p != 1 && _pages[p] is { } img)
        {
            _sink!(p, img);
            _pages[p] = null;
        }
    }

    private void WriteNode(Node n, int hoff, byte leafType, byte interiorType, bool isRoot)
    {
        var page = new byte[_ctx.PageSize];
        bool leaf = n.Entries is not null;
        page[hoff] = leaf ? leafType : interiorType;
        int hdr = leaf ? 8 : 12;
        int ptrs = hoff + hdr;
        int top = _ctx.Usable;
        int count = leaf ? n.Entries!.Count : n.Kids!.Count - 1;
        byte[] vb = new byte[9];
        for (int i = 0; i < count; i++)
        {
            int size;
            byte[] cell;
            Entry? e = null;
            if (leaf)
            {
                e = n.Entries![i];
                cell = e.Cell;
                size = cell.Length;
            }
            else if (_intKey)
            {
                int vl = Varint.Write(vb, (ulong)n.Kids![i].MaxRowid);
                cell = new byte[4 + vl];
                Buffer.BlockCopy(vb, 0, cell, 4, vl);
                Pg.PutU32(cell, 0, n.Kids[i].Page);
                size = cell.Length;
            }
            else
            {
                e = n.Divs![i];
                cell = new byte[4 + e.Cell.Length];
                e.Cell.CopyTo(cell, 4);
                Pg.PutU32(cell, 0, n.Kids![i].Page);
                size = cell.Length;
            }

            int pad = Math.Max(4, size);
            top -= pad;
            if (top < ptrs + 2 * (i + 1))
            {
                throw new InvalidOperationException("vacuum overfilled a page");
            }

            Buffer.BlockCopy(cell, 0, page, top, size);
            Pg.PutU16(page, ptrs + 2 * i, top);
            if (e?.Chain is { } chain)
            {
                WriteChain(page, top + size - 4, n.Page, chain);
            }
        }

        page[hoff + 3] = (byte)(count >> 8);
        page[hoff + 4] = (byte)count;
        Pg.SetContentStart(page, hoff, top);
        if (!leaf)
        {
            Pg.PutU32(page, hoff + 8, n.Kids![^1].Page);
            foreach (Node k in n.Kids)
            {
                Map(k.Page, 5, n.Page);
            }
        }

        if (isRoot && n.Page != 1)
        {
            Map(n.Page, 1, 0);
        }

        _pages[n.Page] = page;
    }

    private void WriteChain(byte[] cellPage, int ptrOff, uint cellPageNo, byte[][] chain)
    {
        uint prev = cellPageNo;
        byte[] prevImg = cellPage;
        int prevOff = ptrOff;
        for (int k = 0; k < chain.Length; k++)
        {
            uint nn = Next();
            Pg.PutU32(prevImg, prevOff, nn);
            Map(nn, k == 0 ? (byte)3 : (byte)4, prev);
            _pages[nn] = chain[k];
            prev = nn;
            prevImg = chain[k];
            prevOff = 0;
        }

        Pg.PutU32(prevImg, 0, 0);
    }
}
