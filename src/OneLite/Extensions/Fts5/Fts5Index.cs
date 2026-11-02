using System.Linq;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Internal.Text;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Extensions.Fts5;

/// <summary>Orders and compares byte-array keys bytewise (the key order inside a segment).</summary>
internal sealed class ByteKeyComparer : IComparer<byte[]>, IEqualityComparer<byte[]>
{
    internal static readonly ByteKeyComparer Instance = new();

    public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);

    public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);

    public int GetHashCode(byte[] obj)
    {
        var h = new HashCode();
        h.AddBytes(obj);
        return h.ToHashCode();
    }
}

/// <summary>The occurrences of one term in one row: (column &lt;&lt; 32 | offset) values in order.</summary>
internal sealed class Posting
{
    internal long Rowid;
    internal long[] Pos = [];
}

/// <summary>The terms of one row as the index sees them.</summary>
internal sealed class DocTokens
{
    internal Dictionary<byte[], List<long>> Terms = new(ByteKeyComparer.Instance);
    internal long[] ColSizes = [];
}

/// <summary>Index entries waiting to become a segment: key, then rowid.</summary>
internal sealed class Pending
{
    internal SortedDictionary<byte[], SortedDictionary<long, (long[] Pos, bool Del)>> Map = new(ByteKeyComparer.Instance);

    internal bool IsEmpty => Map.Count == 0;

    internal void Add(long rowid, DocTokens t, bool delete)
    {
        foreach (KeyValuePair<byte[], List<long>> kv in t.Terms)
        {
            if (!Map.TryGetValue(kv.Key, out SortedDictionary<long, (long[] Pos, bool Del)>? d))
            {
                Map[kv.Key] = d = [];
            }

            d[rowid] = delete ? ([], true) : ([.. kv.Value], false);
        }
    }
}

/// <summary>Feeds tokens of one column into a <see cref="DocTokens"/>.</summary>
internal sealed class DocSink(DocTokens doc, Fts5Config cfg) : TokenSink
{
    internal int Col;
    internal long Count;

    internal override void Token(ReadOnlySpan<byte> token, int start, int end)
    {
        long pos = (long)Col << 32 | (uint)Count;
        Count++;
        Add(0, token, pos);
        if (cfg.Prefixes.Count > 0)
        {
            var starts = new List<int>();
            for (int i = 0; i < token.Length; i++)
            {
                if ((token[i] & 0xC0) != 0x80)
                {
                    starts.Add(i);
                }
            }

            for (int k = 0; k < cfg.Prefixes.Count; k++)
            {
                int n = cfg.Prefixes[k];
                if (n <= starts.Count)
                {
                    Add(k + 1, token[..(n == starts.Count ? token.Length : starts[n])], pos);
                }
            }
        }
    }

    private void Add(int idx, ReadOnlySpan<byte> term, long pos)
    {
        var key = new byte[term.Length + 1];
        key[0] = (byte)('0' + idx);
        term.CopyTo(key.AsSpan(1));
        if (!doc.Terms.TryGetValue(key, out List<long>? l))
        {
            doc.Terms[key] = l = [];
        }

        l.Add(pos);
    }
}

/// <summary>The index of one fts5 table: structure, segments, merging and lookups, over a <see cref="Fts5Store"/>.</summary>
internal sealed class Fts5Index
{
    private readonly Fts5Config _cfg;
    private readonly Fts5Store _store;

    internal Fts5Index(Fts5Config cfg, Fts5Store store)
    {
        _cfg = cfg;
        _store = store;
    }

    internal bool DetailNone => _cfg.Detail == DetailMode.None;

    internal Structure ReadStructure() => Structure.Parse(_store.Data(F5.StructureId), _cfg.ContentlessDelete);

    /// <summary>The averages record: row count, then the token total of each column.</summary>
    internal long[] ReadAverages()
    {
        var a = new long[_cfg.ColumnCount + 1];
        byte[]? b = _store.Data(F5.AveragesId);
        if (b is null)
        {
            return a;
        }

        int p = 0;
        for (int i = 0; i < a.Length && p < b.Length; i++)
        {
            a[i] = F5.GetVarint(b, ref p);
        }

        return a;
    }

    internal static byte[] AveragesBlob(long[] a)
    {
        var b = new ByteBuf();
        foreach (long v in a)
        {
            b.Varint(v);
        }

        return b.ToArray();
    }

    /// <summary>The text of a value as the tokenizer sees it (UTF-8).</summary>
    internal static ReadOnlySpan<byte> TextOf(in Mem m, TextEncoding enc, Span<byte> scratch)
    {
        switch (m.Kind)
        {
            case StorageClass.Null:
                return default;
            case StorageClass.Integral:
            case StorageClass.Real:
                return Vm.Values.AsText(m, scratch);
            default:
                return enc == TextEncoding.Utf8 ? m.Bytes : TextCodec.ToUtf8(m.Bytes, enc);
        }
    }

    /// <summary>Tokenizes the indexed columns of a row (values in database encoding).</summary>
    internal DocTokens Tokenize(ReadOnlySpan<Mem> values, TextEncoding enc)
    {
        var doc = new DocTokens { ColSizes = new long[_cfg.ColumnCount] };
        var sink = new DocSink(doc, _cfg);
        Span<byte> scratch = stackalloc byte[64];
        for (int c = 0; c < _cfg.ColumnCount; c++)
        {
            if (_cfg.Unindexed[c] || c >= values.Length)
            {
                continue;
            }

            ReadOnlySpan<byte> text = TextOf(values[c], enc, scratch);
            sink.Col = c;
            sink.Count = 0;
            if (text.Length > 0)
            {
                _cfg.Tokenizer.Tokenize(text, TokenizeReason.Document, sink);
            }

            doc.ColSizes[c] = sink.Count;
        }

        return doc;
    }

    // ---------------------------------------------------------------- writing segments

    private byte[] EncodePos(long[] pos)
    {
        switch (_cfg.Detail)
        {
            case DetailMode.Full:
                return PosList.Encode(pos);
            case DetailMode.Column:
            {
                var b = new ByteBuf();
                int prev = 0;
                bool first = true;
                int last = -1;
                foreach (long p in pos)
                {
                    int c = (int)(p >> 32);
                    if (c == last)
                    {
                        continue;
                    }

                    b.Varint(first ? c + 2 : c - prev + 2);
                    first = false;
                    prev = c;
                    last = c;
                }

                return b.ToArray();
            }

            default:
                return [];
        }
    }

    /// <summary>Turns pending entries into a new level-0 segment; adds its pages to <paramref name="ops"/> and the structure.</summary>
    internal void Flush(Structure st, Pending p, List<WriteOp> ops)
    {
        if (p.IsEmpty)
        {
            return;
        }

        var w = new SegWriter(_cfg.Pgsz, DetailNone);
        foreach (KeyValuePair<byte[], SortedDictionary<long, (long[] Pos, bool Del)>> kv in p.Map)
        {
            w.BeginKey(kv.Key);
            foreach (KeyValuePair<long, (long[] Pos, bool Del)> e in kv.Value)
            {
                w.Entry(e.Key, e.Value.Del ? [] : EncodePos(e.Value.Pos), e.Value.Del);
            }
        }

        w.Finish();
        if (st.Levels.Count == 0)
        {
            st.Levels.Add(new Level());
        }

        Seg g = EmitSegment(st, w, ops);
        st.Levels[0].Segs.Add(g);
        st.NWrite += g.Last;
        AutoMerge(st, ops);
    }

    private Seg EmitSegment(Structure st, SegWriter w, List<WriteOp> ops)
    {
        int id = st.NewSegmentId();
        for (int i = 0; i < w.Pages.Count; i++)
        {
            long did = F5.DataId(id, i + 1);
            ops.Add(new WriteOp { Kind = OpKind.PutData, Id = did, Blob = w.Pages[i] });
            _store.SetData(did, w.Pages[i]);
        }

        foreach ((byte[] term, int pgno) in w.Index)
        {
            ops.Add(new WriteOp { Kind = OpKind.PutIdx, Id = id, Blob = term, Id2 = pgno * 2L });
        }

        return new Seg { Id = id, First = 1, Last = w.Pages.Count };
    }

    private void DropSegment(Seg g, List<WriteOp> ops)
    {
        long lo = F5.DataId(g.Id, 0);
        long hi = F5.DataEnd(g.Id) - 1;
        ops.Add(new WriteOp { Kind = OpKind.DelDataRange, Id = lo, Id2 = hi });
        ops.Add(new WriteOp { Kind = OpKind.DelIdxSeg, Id = g.Id });
        _store.RemoveDataRange(lo, hi);
    }

    /// <summary>Merges the listed segments (oldest first) into one segment placed at the end of <paramref name="outLevel"/>.</summary>
    internal void MergeInto(Structure st, int outLevel, List<Seg> inputsOldestFirst, bool dropDeletes, List<WriteOp> ops)
    {
        foreach (Seg g in inputsOldestFirst)
        {
            _store.NeedDataRange(F5.DataId(g.Id, g.First), F5.DataId(g.Id, g.Last));
        }

        var scans = new List<SegScanner>();
        for (int i = inputsOldestFirst.Count - 1; i >= 0; i--)
        {
            var sc = new SegScanner(_store, inputsOldestFirst[i], DetailNone);
            sc.Rewind();
            scans.Add(sc); // newest first
        }

        var w = new SegWriter(_cfg.Pgsz, DetailNone);
        var all = new List<(long Rowid, int Prio, byte[] Pl, bool Del)>();
        for (; ; )
        {
            byte[]? min = null;
            foreach (SegScanner sc in scans)
            {
                if (sc.HasKey && (min is null || sc.Key.SequenceCompareTo(min) < 0))
                {
                    min = sc.Key.ToArray();
                }
            }

            if (min is null)
            {
                break;
            }

            all.Clear();
            for (int s = 0; s < scans.Count; s++)
            {
                SegScanner sc = scans[s];
                if (sc.HasKey && sc.Key.SequenceEqual(min))
                {
                    while (sc.NextEntry(out long rowid, out byte[] pl, out bool del))
                    {
                        all.Add((rowid, s, pl, del));
                    }

                    sc.NextKey();
                }
            }

            all.Sort((a, b) => a.Rowid != b.Rowid ? a.Rowid.CompareTo(b.Rowid) : a.Prio.CompareTo(b.Prio));
            bool began = false;
            long prev = long.MinValue;
            bool havePrev = false;
            foreach ((long rowid, _, byte[] pl, bool del) in all)
            {
                if (havePrev && rowid == prev)
                {
                    continue;
                }

                havePrev = true;
                prev = rowid;
                if (del && dropDeletes)
                {
                    continue;
                }

                if (!began)
                {
                    w.BeginKey(min);
                    began = true;
                }

                w.Entry(rowid, pl, del);
            }
        }

        w.Finish();
        foreach (Seg g in inputsOldestFirst)
        {
            DropSegment(g, ops);
            foreach (Level l in st.Levels)
            {
                l.Segs.Remove(g);
            }
        }

        if (!w.IsEmpty)
        {
            while (st.Levels.Count <= outLevel)
            {
                st.Levels.Add(new Level());
            }

            st.Levels[outLevel].Segs.Add(EmitSegment(st, w, ops));
        }

        while (st.Levels.Count > 0 && st.Levels[^1].Segs.Count == 0)
        {
            st.Levels.RemoveAt(st.Levels.Count - 1);
        }
    }

    /// <summary>Merges a level into the next when it holds too many segments (fts5 "automerge" and "crisismerge").</summary>
    internal void AutoMerge(Structure st, List<WriteOp> ops)
    {
        for (int l = 0; l < st.Levels.Count; l++)
        {
            int n = st.Levels[l].Segs.Count;
            bool due = (_cfg.Automerge > 1 && n >= _cfg.Automerge) || (_cfg.Crisismerge > 1 && n >= _cfg.Crisismerge);
            if (!due)
            {
                continue;
            }

            bool older = false;
            for (int k = l + 1; k < st.Levels.Count; k++)
            {
                older |= st.Levels[k].Segs.Count > 0;
            }

            MergeInto(st, l + 1, [.. st.Levels[l].Segs], !older, ops);
            l = -1; // start over: levels shifted
        }
    }

    /// <summary>Merges everything into one segment (the 'optimize' command).</summary>
    internal void Optimize(Structure st, List<WriteOp> ops)
    {
        List<Seg> all = [];
        for (int l = st.Levels.Count - 1; l >= 0; l--)
        {
            all.AddRange(st.Levels[l].Segs);
        }

        if (all.Count <= 1 && !(all.Count == 1))
        {
            return;
        }

        if (all.Count == 1)
        {
            // already one segment: rewrite only to drop delete markers
            MergeInto(st, st.Levels.Count - 1, all, true, ops);
            return;
        }

        MergeInto(st, st.Levels.Count - 1, all, true, ops);
    }

    /// <summary>Merges the largest level once if it holds at least <paramref name="minSegs"/> segments; false when nothing was due.</summary>
    internal bool MergeOnce(Structure st, int minSegs, List<WriteOp> ops)
    {
        int best = -1;
        for (int l = 0; l < st.Levels.Count; l++)
        {
            if (st.Levels[l].Segs.Count >= Math.Max(2, minSegs) && (best < 0 || st.Levels[l].Segs.Count > st.Levels[best].Segs.Count))
            {
                best = l;
            }
        }

        if (best < 0)
        {
            return false;
        }

        bool older = false;
        for (int k = best + 1; k < st.Levels.Count; k++)
        {
            older |= st.Levels[k].Segs.Count > 0;
        }

        MergeInto(st, best + 1, [.. st.Levels[best].Segs], !older, ops);
        return true;
    }

    // ---------------------------------------------------------------- reading

    /// <summary>
    /// The live rows that contain a term, newest data winning. <paramref name="key"/> is the full key (index digit, then the term);
    /// with <paramref name="scan"/> every key that starts with it counts (a prefix query on a table with no matching prefix index).
    /// </summary>
    internal List<Posting> Lookup(Structure st, byte[] key, bool scan)
    {
        var seen = new HashSet<long>();
        var live = new List<Posting>();
        foreach (Seg g in st.NewestFirst())
        {
            _store.NeedDataRange(F5.DataId(g.Id, g.First), F5.DataId(g.Id, g.Last));
            var sc = new SegScanner(_store, g, DetailNone);
            sc.Seek(key);
            var local = new Dictionary<long, (List<long> Pos, bool Live)>();
            while (sc.HasKey && (scan ? sc.Key.StartsWith(key) : sc.Key.SequenceEqual(key)))
            {
                while (sc.NextEntry(out long rowid, out byte[] pl, out bool del))
                {
                    if (seen.Contains(rowid))
                    {
                        continue;
                    }

                    if (!local.TryGetValue(rowid, out var cur))
                    {
                        cur = ([], false);
                    }

                    if (!del)
                    {
                        cur.Live = true;
                        cur.Pos.AddRange(DecodePos(pl));
                    }

                    local[rowid] = cur;
                }

                if (!scan)
                {
                    break;
                }

                sc.NextKey();
            }

            foreach (KeyValuePair<long, (List<long> Pos, bool Live)> kv in local)
            {
                seen.Add(kv.Key);
                if (kv.Value.Live)
                {
                    kv.Value.Pos.Sort();
                    live.Add(new Posting { Rowid = kv.Key, Pos = [.. kv.Value.Pos] });
                }
            }
        }

        live.Sort((a, b) => a.Rowid.CompareTo(b.Rowid));
        return live;
    }

    private long[] DecodePos(byte[] pl)
    {
        long[] r = DecodePosRaw(pl);
        foreach (long p in r)
        {
            if ((p >> 32) >= _cfg.ColumnCount)
            {
                throw F5.Corrupt("column number out of range in a position list");
            }
        }

        return r;
    }

    private long[] DecodePosRaw(byte[] pl)
    {
        switch (_cfg.Detail)
        {
            case DetailMode.Full:
                return PosList.Decode(pl);
            case DetailMode.Column:
            {
                var l = new List<long>();
                int p = 0;
                long col = 0;
                bool first = true;
                while (p < pl.Length)
                {
                    long v = F5.GetVarint(pl, ref p);
                    col = first ? v - 2 : col + v - 2;
                    first = false;
                    if (col < 0 || col > 2000)
                    {
                        throw F5.Corrupt("bad column list");
                    }

                    l.Add(col << 32);
                }

                return [.. l];
            }

            default:
                return [];
        }
    }

    /// <summary>Every live (key, row) of the index, in key order: for integrity checks and 'rebuild' comparisons.</summary>
    internal SortedDictionary<byte[], SortedDictionary<long, long[]>> ReadAll(Structure st)
    {
        var res = new SortedDictionary<byte[], SortedDictionary<long, long[]>>(ByteKeyComparer.Instance);
        var dead = new Dictionary<byte[], HashSet<long>>(ByteKeyComparer.Instance);
        foreach (Seg g in st.NewestFirst())
        {
            _store.NeedDataRange(F5.DataId(g.Id, g.First), F5.DataId(g.Id, g.Last));
            var sc = new SegScanner(_store, g, DetailNone);
            sc.Rewind();
            while (sc.HasKey)
            {
                byte[] k = sc.Key.ToArray();
                if (!res.TryGetValue(k, out SortedDictionary<long, long[]>? d))
                {
                    res[k] = d = [];
                }

                if (!dead.TryGetValue(k, out HashSet<long>? ds))
                {
                    dead[k] = ds = [];
                }

                while (sc.NextEntry(out long rowid, out byte[] pl, out bool del))
                {
                    if (d.ContainsKey(rowid) || ds.Contains(rowid))
                    {
                        continue;
                    }

                    if (del)
                    {
                        ds.Add(rowid);
                    }
                    else
                    {
                        d[rowid] = DecodePos(pl);
                    }
                }

                sc.NextKey();
            }
        }

        foreach (byte[] k in res.Keys.Where(k => res[k].Count == 0).ToArray())
        {
            res.Remove(k);
        }

        return res;
    }
}
