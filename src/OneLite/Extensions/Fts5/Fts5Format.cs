using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Extensions.Fts5;

/// <summary>A growable byte buffer with varint helpers.</summary>
internal sealed class ByteBuf
{
    private byte[] _b;
    private int _n;

    internal ByteBuf(int capacity = 64) => _b = new byte[Math.Max(16, capacity)];

    internal int Length => _n;

    internal void Clear() => _n = 0;

    internal ReadOnlySpan<byte> Span => new(_b, 0, _n);

    internal byte[] ToArray() => Span.ToArray();

    private void Grow(int extra)
    {
        if (_n + extra > _b.Length)
        {
            Array.Resize(ref _b, Math.Max(_b.Length * 2, _n + extra));
        }
    }

    internal void Add(byte x)
    {
        Grow(1);
        _b[_n++] = x;
    }

    internal void Add(ReadOnlySpan<byte> s)
    {
        Grow(s.Length);
        s.CopyTo(_b.AsSpan(_n));
        _n += s.Length;
    }

    /// <summary>Appends a varint (fileformat2 §1.8 / fts5 "Varint Format").</summary>
    internal void Varint(ulong v)
    {
        Grow(9);
        _n += Storage.Record.Varint.Write(_b.AsSpan(_n), v);
    }

    internal void Varint(long v) => Varint((ulong)v);

    internal void Truncate(int n) => _n = n;
}

/// <summary>Helpers shared by the FTS5 format code.</summary>
internal static class F5
{
    internal static OneLiteException Corrupt(string what) => new(ResultCode.CorruptVtab, "database disk image is malformed", "fts5: " + what);

    /// <summary>Reads a varint at <paramref name="pos"/>, advancing it; a truncated varint is corruption.</summary>
    internal static long GetVarint(ReadOnlySpan<byte> s, ref int pos)
    {
        if (pos < 0 || pos >= s.Length)
        {
            throw Corrupt("truncated varint");
        }

        int n = Storage.Record.Varint.Read(s[pos..], out ulong v);
        if (n == 0)
        {
            throw Corrupt("truncated varint");
        }

        pos += n;
        return (long)v;
    }

    /// <summary>Compares two keys bytewise (the order of keys inside a segment).</summary>
    internal static int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.SequenceCompareTo(b);

    /// <summary>The %_data rowid of page <paramref name="pgno"/> of segment <paramref name="segid"/>: segment id in bits 37 and up, page number below.</summary>
    internal static long DataId(int segid, int pgno) => ((long)segid << 37) | (uint)pgno;

    /// <summary>The first %_data rowid past every record of the segment (leaves, doclist index pages).</summary>
    internal static long DataEnd(int segid) => (long)(segid + 1) << 37;

    internal const long AveragesId = 1;
    internal const long StructureId = 10;
}

/// <summary>One segment b-tree: its id and the numbers of its first and last leaf page.</summary>
internal sealed class Seg
{
    internal int Id;
    internal int First;
    internal int Last;

    // contentless_delete extras (fts5 "Structure Record Format")
    internal long OriginLo;
    internal long OriginHi;
    internal long NTombstonePages;
    internal long NTombstones;
    internal long NEntries;
}

/// <summary>A level of the index: its segments from oldest to newest, and the size of an unfinished incremental merge (always 0 here).</summary>
internal sealed class Level
{
    internal long NMerge;
    internal List<Seg> Segs = [];
}

/// <summary>The structure record (%_data rowid 10): the list of segments by level.</summary>
internal sealed class Structure
{
    internal uint Cookie;
    internal long NWrite;
    internal List<Level> Levels = [];

    internal int SegmentCount => Levels.Sum(l => l.Segs.Count);

    internal Structure Clone()
    {
        var s = new Structure { Cookie = Cookie, NWrite = NWrite };
        foreach (Level l in Levels)
        {
            var nl = new Level { NMerge = l.NMerge };
            foreach (Seg g in l.Segs)
            {
                nl.Segs.Add(new Seg { Id = g.Id, First = g.First, Last = g.Last, OriginLo = g.OriginLo, OriginHi = g.OriginHi, NTombstonePages = g.NTombstonePages, NTombstones = g.NTombstones, NEntries = g.NEntries });
            }

            s.Levels.Add(nl);
        }

        return s;
    }

    /// <summary>Parses the record; <paramref name="extras"/> says the table is contentless_delete (five more fields per segment).</summary>
    internal static Structure Parse(byte[]? blob, bool extras)
    {
        var s = new Structure();
        if (blob is null || blob.Length == 0)
        {
            return s;
        }

        if (blob.Length < 4)
        {
            throw F5.Corrupt("structure record too short");
        }

        s.Cookie = (uint)((blob[0] << 24) | (blob[1] << 16) | (blob[2] << 8) | blob[3]);
        int p = 4;
        long nLevel = F5.GetVarint(blob, ref p);
        long nSeg = F5.GetVarint(blob, ref p);
        s.NWrite = F5.GetVarint(blob, ref p);
        if (nLevel < 0 || nLevel > 2000 || nSeg < 0 || nSeg > 65535)
        {
            throw F5.Corrupt("bad level or segment count");
        }

        long total = 0;
        for (long i = 0; i < nLevel; i++)
        {
            var lv = new Level { NMerge = F5.GetVarint(blob, ref p) };
            long n = F5.GetVarint(blob, ref p);
            if (n < 0 || n > 65535 || total + n > nSeg)
            {
                throw F5.Corrupt("bad segment count in level");
            }

            total += n;
            for (long k = 0; k < n; k++)
            {
                var g = new Seg
                {
                    Id = SmallVarint(blob, ref p),
                    First = SmallVarint(blob, ref p),
                    Last = SmallVarint(blob, ref p),
                };
                if (g.Id < 0 || g.Id > 65535 || g.First < 0 || g.Last < g.First)
                {
                    throw F5.Corrupt("bad segment");
                }

                if (extras)
                {
                    g.OriginLo = F5.GetVarint(blob, ref p);
                    g.OriginHi = F5.GetVarint(blob, ref p);
                    g.NTombstonePages = F5.GetVarint(blob, ref p);
                    g.NTombstones = F5.GetVarint(blob, ref p);
                    g.NEntries = F5.GetVarint(blob, ref p);
                }

                lv.Segs.Add(g);
            }

            s.Levels.Add(lv);
        }

        return s;
    }

    // A page number or segment id outside the int range is a damaged record, not an arithmetic fault.
    private static int SmallVarint(byte[] blob, ref int p)
    {
        long v = F5.GetVarint(blob, ref p);
        return v is < 0 or > int.MaxValue ? throw F5.Corrupt("bad segment") : (int)v;
    }

    internal byte[] Serialize(bool extras)
    {
        var b = new ByteBuf();
        b.Add((byte)(Cookie >> 24));
        b.Add((byte)(Cookie >> 16));
        b.Add((byte)(Cookie >> 8));
        b.Add((byte)Cookie);
        b.Varint(Levels.Count);
        b.Varint(SegmentCount);
        b.Varint(NWrite);
        foreach (Level l in Levels)
        {
            b.Varint(l.NMerge);
            b.Varint(l.Segs.Count);
            foreach (Seg g in l.Segs)
            {
                b.Varint(g.Id);
                b.Varint(g.First);
                b.Varint(g.Last);
                if (extras)
                {
                    b.Varint(g.OriginLo);
                    b.Varint(g.OriginHi);
                    b.Varint(g.NTombstonePages);
                    b.Varint(g.NTombstones);
                    b.Varint(g.NEntries);
                }
            }
        }

        return b.ToArray();
    }

    /// <summary>An unused segment id.</summary>
    internal int NewSegmentId()
    {
        var used = new HashSet<int>(Levels.SelectMany(l => l.Segs).Select(g => g.Id));
        for (int i = 1; i < 65536; i++)
        {
            if (!used.Contains(i))
            {
                return i;
            }
        }

        throw new OneLiteException(ResultCode.Full, "fts5: too many segments");
    }

    /// <summary>All segments from newest to oldest (level 0 first; inside a level the last listed is the newest).</summary>
    internal IEnumerable<Seg> NewestFirst()
    {
        foreach (Level l in Levels)
        {
            for (int i = l.Segs.Count - 1; i >= 0; i--)
            {
                yield return l.Segs[i];
            }
        }
    }
}

/// <summary>Where segment pages come from. Implementations may throw to ask for a page that is not loaded yet.</summary>
internal interface IBlobSource
{
    /// <summary>The %_data record with this rowid, or null when there is none.</summary>
    byte[]? Data(long id);

    /// <summary>Like <see cref="Data"/>, but returns false instead of throwing when the record has not been loaded yet.</summary>
    bool TryData(long id, out byte[]? value);

    /// <summary>The parsed leaf page <paramref name="pgno"/> of segment <paramref name="segid"/> (null when it does not exist), parsed once while it stays loaded; false when not loaded yet.</summary>
    bool TryPage(int segid, int pgno, out SegPage? page);
}

/// <summary>A segment leaf page (fts5 "Pagination"): 4-byte header, key/doclist data, footer of key offsets.</summary>
internal sealed class SegPage
{
    internal byte[] Buf = [];

    /// <summary>Offset of the first rowid on the page when it comes before the first key, else 0.</summary>
    internal int FirstRowid;

    /// <summary>Offset of the footer, which is also the end of the data.</summary>
    internal int FooterOff;

    /// <summary>Offsets of the keys that start on this page.</summary>
    internal int[] KeyOffs = [];

    internal static SegPage Parse(byte[] blob, int segid, int pgno)
    {
        string where = $"segment {segid} page {pgno}: ";
        if (blob.Length < 4)
        {
            throw F5.Corrupt(where + "page too short");
        }

        int first = (blob[0] << 8) | blob[1];
        int footer = (blob[2] << 8) | blob[3];
        if (footer < 4 || footer > blob.Length || (first != 0 && (first < 4 || first >= footer)))
        {
            throw F5.Corrupt(where + "bad page header");
        }

        var keys = new List<int>();
        int p = footer;
        long off = 0;
        while (p < blob.Length)
        {
            long d = F5.GetVarint(blob, ref p);
            off += d;
            if (off < 4 || off >= footer || (keys.Count > 0 && off <= keys[^1]) || d < 0)
            {
                throw F5.Corrupt(where + "bad key offset");
            }

            keys.Add((int)off);
        }

        if (keys.Count > 0 && first != 0 && first >= keys[0])
        {
            throw F5.Corrupt(where + "rowid offset after first key");
        }

        return new SegPage { Buf = blob, FirstRowid = first, FooterOff = footer, KeyOffs = [.. keys] };
    }
}

/// <summary>Reads the keys and doclists of one segment in order.</summary>
internal sealed class SegScanner
{
    private readonly IBlobSource _src;
    private readonly Seg _seg;
    private int _pgno;
    private SegPage? _page;
    private int _pos;
    private int _keyIdx;
    private long _rowid;
    private bool _firstEntry;
    private byte[] _key = [];

    private readonly bool _none;

    internal SegScanner(IBlobSource src, Seg seg, bool detailNone = false)
    {
        _src = src;
        _seg = seg;
        _none = detailNone;
    }

    /// <summary>True when a key is current.</summary>
    internal bool HasKey { get; private set; }

    internal ReadOnlySpan<byte> Key => _key;

    private SegPage Load(int pgno)
    {
        if (!_src.TryPage(_seg.Id, pgno, out SegPage? pg))
        {
            _src.Data(F5.DataId(_seg.Id, pgno)); // not loaded: the source signals the fetch
            throw F5.Corrupt($"segment {_seg.Id}: page {pgno} cannot be read");
        }

        return pg ?? throw F5.Corrupt($"segment {_seg.Id} page {pgno} is missing");
    }

    private static ReadOnlySpan<byte> FirstKeyOf(SegPage pg)
    {
        int p = pg.KeyOffs[0];
        long n = F5.GetVarint(pg.Buf, ref p);
        if (n < 0 || p + n > pg.FooterOff)
        {
            throw F5.Corrupt("key runs off its page");
        }

        return pg.Buf.AsSpan(p, (int)n);
    }

    /// <summary>
    /// The page <see cref="Seek"/> starts its key scan on: the last page whose first key is not greater than <paramref name="term"/>.
    /// Returns -1 with <paramref name="missing"/> set when a probed page has not been loaded yet (the store then fetches it and the
    /// search is repeated; the probes are the same each time).
    /// </summary>
    internal static int FindStartPage(IBlobSource src, Seg seg, ReadOnlySpan<byte> term, out long missing)
    {
        missing = 0;
        int lo = seg.First;
        int hi = seg.Last;
        int best = lo;
        // The last page whose first key is <= term; pages without a key are skipped over.
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) / 2);
            int probe = mid;
            SegPage? pg = null;
            while (probe <= hi)
            {
                if (!src.TryPage(seg.Id, probe, out pg))
                {
                    missing = F5.DataId(seg.Id, probe);
                    return -1;
                }

                if (pg is null)
                {
                    throw F5.Corrupt($"segment {seg.Id} page {probe} is missing");
                }

                if (pg.KeyOffs.Length > 0)
                {
                    break;
                }

                probe++;
            }

            if (probe > hi || pg is null || pg.KeyOffs.Length == 0)
            {
                hi = mid - 1;
                continue;
            }

            if (F5.Compare(FirstKeyOf(pg), term) <= 0)
            {
                best = probe;
                lo = probe + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return best;
    }

    /// <summary>Positions on the first key that is not smaller than <paramref name="term"/>.</summary>
    internal void Seek(ReadOnlySpan<byte> term)
    {
        if (IsEmpty())
        {
            HasKey = false;
            return;
        }

        int best = FindStartPage(_src, _seg, term, out long missing);
        if (best < 0)
        {
            _src.Data(missing); // not loaded: the store signals the fetch
            throw F5.Corrupt($"segment {_seg.Id}: page {missing} cannot be read");
        }

        _pgno = best;
        _page = Load(best);
        _keyIdx = 0;
        _pos = 0;
        HasKey = false;
        while (NextKey())
        {
            if (F5.Compare(_key, term) >= 0)
            {
                return;
            }
        }
    }

    // Observed in files the reference shell wrote after a merge that dropped every entry: a segment recorded with first and last page 0 and no
    // page 0 in the data table holds nothing (a stray page 1 may remain).
    private bool IsEmpty() => _seg.First == 0 && _seg.Last == 0 && _src.Data(F5.DataId(_seg.Id, 0)) is null;

    /// <summary>Positions on the segment's first key.</summary>
    internal void Rewind()
    {
        if (IsEmpty())
        {
            HasKey = false;
            return;
        }

        _pgno = _seg.First;
        _page = Load(_pgno);
        _keyIdx = 0;
        _pos = 0;
        HasKey = false;
        NextKey();
    }

    /// <summary>Moves to the next key, skipping what is left of the current doclist. False at the end of the segment.</summary>
    internal bool NextKey()
    {
        SegPage pg = _page!;
        for (; ; )
        {
            if (_keyIdx < pg.KeyOffs.Length)
            {
                break;
            }

            if (_pgno >= _seg.Last)
            {
                HasKey = false;
                return false;
            }

            _pgno++;
            pg = _page = Load(_pgno);
            _keyIdx = 0;
        }

        int p = pg.KeyOffs[_keyIdx];
        long first = F5.GetVarint(pg.Buf, ref p);
        long prefix = 0;
        long suffix = first;
        if (_keyIdx > 0)
        {
            prefix = first;
            suffix = F5.GetVarint(pg.Buf, ref p);
            if (prefix < 0 || prefix > _key.Length)
            {
                throw F5.Corrupt("bad key prefix size");
            }
        }

        if (suffix < 0 || p + suffix > pg.FooterOff)
        {
            throw F5.Corrupt("key runs off its page");
        }

        var nk = new byte[prefix + suffix];
        Array.Copy(_key, nk, prefix);
        Array.Copy(pg.Buf, p, nk, prefix, suffix);
        _key = nk;
        _pos = p + (int)suffix;
        _keyIdx++;
        _firstEntry = true;
        HasKey = true;
        return true;
    }

    /// <summary>Reads the next doclist entry of the current key; false when the doclist is done.</summary>
    internal bool NextEntry(out long rowid, out byte[] poslist, out bool del)
    {
        bool ok = NextEntry(out rowid, out ReadOnlySpan<byte> pl, out del);
        poslist = ok ? pl.ToArray() : [];
        return ok;
    }

    /// <summary>
    /// Like the overload above, but the position list is a view: into the page when the list lies inside it, else into a buffer reused by
    /// the next call. Copy it to keep it.
    /// </summary>
    internal bool NextEntry(out long rowid, out ReadOnlySpan<byte> poslist, out bool del)
    {
        rowid = 0;
        poslist = default;
        del = false;
        SegPage pg = _page!;
        for (; ; )
        {
            if (_pos >= pg.FooterOff)
            {
                if (_pgno >= _seg.Last)
                {
                    return false;
                }

                _pgno++;
                pg = _page = Load(_pgno);
                _keyIdx = 0;
                _pos = 4;
                continue;
            }

            break;
        }

        if (_keyIdx < pg.KeyOffs.Length && _pos == pg.KeyOffs[_keyIdx])
        {
            return false;
        }

        int at = _pos;
        long v = F5.GetVarint(pg.Buf, ref _pos);
        if (_firstEntry || (pg.FirstRowid != 0 && at == pg.FirstRowid))
        {
            rowid = v;
        }
        else
        {
            rowid = _rowid + v;
        }

        _firstEntry = false;
        _rowid = rowid;
        if (_none)
        {
            // detail=none keeps no position lists: an entry is its rowid, optionally followed by zero bytes. One zero marks a delete; two
            // mean the row was deleted and written again (live). A zero is never a rowid delta of a following entry.
            int zeros = 0;
            while (zeros < 2 && _pos < pg.FooterOff && pg.Buf[_pos] == 0 && !(_keyIdx < pg.KeyOffs.Length && _pos == pg.KeyOffs[_keyIdx]))
            {
                zeros++;
                _pos++;
            }

            del = zeros == 1;
            return true;
        }

        long psz = F5.GetVarint(pg.Buf, ref _pos);
        bool flag = (psz & 1) != 0;
        long size = psz >> 1;
        // The flag means "this entry replaces what older segments hold for the row". With no positions behind it, the row is gone;
        // with positions it is the row's new entry (an update that keeps the term).
        del = flag && size == 0;
        if (size < 0 || size > int.MaxValue / 2)
        {
            throw F5.Corrupt("bad position list size");
        }

        if (size <= pg.FooterOff - _pos && !(_keyIdx < pg.KeyOffs.Length && _pos < pg.KeyOffs[_keyIdx] && _pos + size > pg.KeyOffs[_keyIdx]))
        {
            poslist = pg.Buf.AsSpan(_pos, (int)size);
            _pos += (int)size;
            return true;
        }

        // the list runs on into the next page(s): gather it
        if (_spill.Length < size)
        {
            _spill = new byte[Math.Max(size, 256)];
        }

        byte[] res = _spill;
        int got = 0;
        while (got < size)
        {
            int avail = pg.FooterOff - _pos;
            if (avail <= 0)
            {
                if (_pgno >= _seg.Last)
                {
                    throw F5.Corrupt("position list runs off the segment");
                }

                _pgno++;
                pg = _page = Load(_pgno);
                _keyIdx = 0;
                _pos = 4;
                if (pg.KeyOffs.Length > 0 && pg.KeyOffs[0] == 4)
                {
                    throw F5.Corrupt("position list interrupted by a key");
                }

                continue;
            }

            int take = (int)Math.Min(avail, size - got);
            if (_keyIdx < pg.KeyOffs.Length && _pos < pg.KeyOffs[_keyIdx] && _pos + take > pg.KeyOffs[_keyIdx])
            {
                throw F5.Corrupt("position list interrupted by a key");
            }

            Array.Copy(pg.Buf, _pos, res, got, take);
            got += take;
            _pos += take;
        }

        poslist = res.AsSpan(0, (int)size);
        return true;
    }

    private byte[] _spill = [];
}

/// <summary>Builds the pages of one segment from keys and doclists given in order.</summary>
internal sealed class SegWriter
{
    private readonly int _pgsz;
    private readonly ByteBuf _body = new(4096);
    private readonly List<int> _keyOffs = [];
    private int _firstRowid;
    private bool _pageHasKey;
    private byte[] _prevKey = [];
    private byte[]? _lastKeyBeforePage;
    private long _prevRowid;
    private bool _firstInDoclist;
    private bool _any;
    private bool _haveKey;

    /// <summary>Finished pages: page number (from 1) and bytes.</summary>
    internal List<byte[]> Pages { get; } = [];

    /// <summary>For each page that has a key: the %_idx term, page number.</summary>
    internal List<(byte[] Term, int Pgno)> Index { get; } = [];

    private byte[]? _pageFirstKey;

    private readonly bool _none;

    internal SegWriter(int pgsz, bool detailNone = false)
    {
        _pgsz = Math.Max(32, pgsz);
        _none = detailNone;
        _body.Add(0);
        _body.Add(0);
        _body.Add(0);
        _body.Add(0);
    }

    internal bool IsEmpty => !_any;

    /// <summary>Starts the doclist of <paramref name="key"/>; keys must come in increasing bytewise order.</summary>
    internal void BeginKey(ReadOnlySpan<byte> key)
    {
        if (_any && _body.Length >= _pgsz)
        {
            FlushPage();
        }

        int off = _body.Length;
        if (!_pageHasKey)
        {
            _body.Varint(key.Length);
            _body.Add(key);
            _pageFirstKey = key.ToArray();
        }
        else
        {
            int common = 0;
            int max = Math.Min(key.Length, _prevKey.Length);
            while (common < max && key[common] == _prevKey[common])
            {
                common++;
            }

            _body.Varint(common);
            _body.Varint(key.Length - common);
            _body.Add(key[common..]);
        }

        _keyOffs.Add(off);
        _pageHasKey = true;
        _prevKey = key.ToArray();
        _firstInDoclist = true;
        _haveKey = true;
        _any = true;
    }

    /// <summary>Adds one document to the current key's doclist.</summary>
    internal void Entry(long rowid, ReadOnlySpan<byte> poslist, bool del)
    {
        if (!_haveKey)
        {
            throw new InvalidOperationException("entry before key");
        }

        bool absolute = _firstInDoclist;
        if (!_firstInDoclist && _body.Length >= _pgsz)
        {
            FlushPage();
            absolute = true;
        }

        if (absolute && !_pageHasKey && _firstRowid == 0)
        {
            _firstRowid = _body.Length;
        }

        _body.Varint(absolute ? rowid : rowid - _prevRowid);
        if (_none)
        {
            if (del)
            {
                _body.Add(0);
            }
        }
        else
        {
            _body.Varint(((ulong)poslist.Length << 1) | (del ? 1UL : 0UL));
            _body.Add(poslist);
        }

        _prevRowid = rowid;
        _firstInDoclist = false;
    }

    private void FlushPage()
    {
        int pgno = Pages.Count + 1;
        int footerOff = _body.Length;
        var b = new ByteBuf(footerOff + 16);
        b.Add(_body.Span);
        int prev = 0;
        foreach (int o in _keyOffs)
        {
            b.Varint(o - prev);
            prev = o;
        }

        byte[] page = b.ToArray();
        page[0] = (byte)(_firstRowid >> 8);
        page[1] = (byte)_firstRowid;
        page[2] = (byte)(footerOff >> 8);
        page[3] = (byte)footerOff;
        Pages.Add(page);
        if (_pageHasKey)
        {
            byte[] fk = _pageFirstKey!;
            byte[] term;
            if (_lastKeyBeforePage is null)
            {
                term = [];
            }
            else
            {
                int len = 1;
                while (len < fk.Length && F5.Compare(fk.AsSpan(0, len), _lastKeyBeforePage) <= 0)
                {
                    len++;
                }

                term = fk.AsSpan(0, len).ToArray();
            }

            Index.Add((term, pgno));
        }

        if (_haveKey)
        {
            _lastKeyBeforePage = _prevKey;
        }

        _body.Clear();
        _body.Add(0);
        _body.Add(0);
        _body.Add(0);
        _body.Add(0);
        _keyOffs.Clear();
        _firstRowid = 0;
        _pageHasKey = false;
        _pageFirstKey = null;
    }

    /// <summary>Finishes the last page.</summary>
    internal void Finish()
    {
        if (_any)
        {
            FlushPage();
        }
    }
}

/// <summary>Poslist encoding and decoding (fts5 "Position list format"), detail=full.</summary>
internal static class PosList
{
    /// <summary>Encodes sorted (column, offset) pairs.</summary>
    internal static byte[] Encode(ReadOnlySpan<long> positions)
    {
        var b = new ByteBuf();
        int col = 0;
        long prev = 0;
        bool startOfCol = true;
        foreach (long p in positions)
        {
            int c = (int)(p >> 32);
            long off = p & 0xFFFFFFFFL;
            if (c != col)
            {
                b.Add(1);
                b.Varint(c);
                col = c;
                startOfCol = true;
            }

            b.Varint(startOfCol ? off + 2 : off - prev + 2);
            startOfCol = false;
            prev = off;
        }

        return b.ToArray();
    }

    /// <summary>Decodes like <see cref="Decode"/> into a reusable buffer; returns the number of values.</summary>
    internal static int DecodeInto(ReadOnlySpan<byte> pl, ref long[] buf)
    {
        int n = 0;
        int p = 0;
        long col = 0;
        long prev = 0;
        bool start = true;
        while (p < pl.Length)
        {
            long v = F5.GetVarint(pl, ref p);
            if (v == 1)
            {
                col = F5.GetVarint(pl, ref p);
                if (col < 0 || col > 2000)
                {
                    throw F5.Corrupt("bad column in position list");
                }

                start = true;
                continue;
            }

            long off = start ? v - 2 : prev + v - 2;
            if (off < 0 || off > 0x7FFFFFFF)
            {
                throw F5.Corrupt("bad offset in position list");
            }

            if (n == buf.Length)
            {
                Array.Resize(ref buf, Math.Max(16, n * 2));
            }

            buf[n++] = (col << 32) | off;
            prev = off;
            start = false;
        }

        return n;
    }

    /// <summary>Decodes into (column &lt;&lt; 32 | offset) values.</summary>
    internal static long[] Decode(ReadOnlySpan<byte> pl)
    {
        var l = new List<long>();
        int p = 0;
        long col = 0;
        long prev = 0;
        bool start = true;
        while (p < pl.Length)
        {
            long v = F5.GetVarint(pl, ref p);
            if (v == 1)
            {
                col = F5.GetVarint(pl, ref p);
                if (col < 0 || col > 2000)
                {
                    throw F5.Corrupt("bad column in position list");
                }

                start = true;
                continue;
            }

            long off = start ? v - 2 : prev + v - 2;
            if (off < 0 || off > 0x7FFFFFFF)
            {
                throw F5.Corrupt("bad offset in position list");
            }

            l.Add((col << 32) | off);
            prev = off;
            start = false;
        }

        return [.. l];
    }

    /// <summary>The columns named in a position list, for detail=column.</summary>
    internal static int[] Columns(ReadOnlySpan<byte> pl)
    {
        var cols = new SortedSet<int>();
        int p = 0;
        int col = 0;
        bool first = true;
        while (p < pl.Length)
        {
            long v = F5.GetVarint(pl, ref p);
            if (v == 1)
            {
                col = (int)F5.GetVarint(pl, ref p);
                cols.Add(col);
                first = false;
            }
            else if (first)
            {
                cols.Add(0);
                first = false;
            }
        }

        return [.. cols];
    }
}
