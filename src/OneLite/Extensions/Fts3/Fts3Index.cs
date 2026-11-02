using System.Linq;
using RecursiveAI.OneLite.Internal.Text;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Extensions.Fts3;

/// <summary>The tokens of one document, ready to be added to or removed from the index.</summary>
internal sealed class DocTokens
{
    /// <summary>For each term, its (column, position) pairs in order.</summary>
    internal SortedDictionary<byte[], List<(int Col, int Pos)>> Terms = new(ByteKeyComparer.Instance);

    /// <summary>Tokens per column (the <c>%_docsize</c> row).</summary>
    internal long[] ColTokens = [];

    /// <summary>Bytes of all indexed text, as the last number of the <c>%_stat</c> row.</summary>
    internal long Bytes;
}

/// <summary>A sink that records tokens with their positions.</summary>
internal sealed class CollectSink : TokenSink
{
    internal readonly List<(byte[] Token, int Start, int End)> Tokens = [];

    internal override void Token(ReadOnlySpan<byte> token, int start, int end) => Tokens.Add((token.ToArray(), start, end));
}

/// <summary>The inverted index of one fts3/fts4 table: reading terms, tokenizing documents, collecting and flushing changes.</summary>
internal sealed class Fts3Index
{
    private readonly Fts3Config _cfg;
    private readonly Fts3Db _db;

    internal Fts3Index(Fts3Config cfg, Fts3Db db)
    {
        _cfg = cfg;
        _db = db;
    }

    internal long FamilyBase(long langid, int index) => (langid * _cfg.IndexCount + index) * F3.LevelsPerIndex;

    // ---------------------------------------------------------------- reading

    /// <summary>
    /// The documents that contain <paramref name="term"/> (or, with <paramref name="prefix"/>, a term that starts with it) in language
    /// <paramref name="langid"/>, with each document's (column, position) pairs sorted. Where one document appears in several segments for the
    /// same term only the newest segment counts, and a bare docid entry there (a deletion) hides the older ones (fts3 "Data Structures").
    /// </summary>
    internal SortedDictionary<long, List<(int Col, int Pos)>> Postings(long langid, byte[] term, bool prefix)
    {
        long b = FamilyBase(langid, 0);
        List<SegDirRow> segs = _db.ReadDir(b, b + F3.LevelsPerIndex - 1);
        var seen = new Dictionary<byte[], HashSet<long>>(ByteKeyComparer.Instance);
        var result = new SortedDictionary<long, List<(int Col, int Pos)>>();
        foreach (SegDirRow seg in segs)
        {
            foreach ((byte[] t, byte[] doclist) in _db.Scan(seg, term))
            {
                bool match = prefix ? t.Length >= term.Length && t.AsSpan(0, term.Length).SequenceEqual(term) : t.AsSpan().SequenceEqual(term);
                if (!match)
                {
                    break;
                }

                if (!seen.TryGetValue(t, out HashSet<long>? ids))
                {
                    seen[t] = ids = [];
                }

                foreach (F3.Entry e in F3.ParseDoclist(doclist, _cfg.Descending))
                {
                    if (!ids.Add(e.Docid) || e.IsDelete)
                    {
                        continue;
                    }

                    if (!result.TryGetValue(e.Docid, out List<(int, int)>? l))
                    {
                        result[e.Docid] = l = [];
                    }

                    var tmp = new List<(int Col, int Pos)>();
                    F3.DecodePoslist(e.Poslist, 0, tmp);
                    l.AddRange(tmp);
                }
            }
        }

        foreach (List<(int Col, int Pos)> l in result.Values)
        {
            l.Sort();
        }

        return result;
    }

    /// <summary>Every term of the index of language <paramref name="langid"/> and index <paramref name="index"/> with its merged postings (used by fts4aux, integrity-check and rebuild checks).</summary>
    internal SortedDictionary<byte[], SortedDictionary<long, List<(int Col, int Pos)>>> ReadAll(long langid, int index) => ReadAllAt(FamilyBase(langid, index));

    /// <summary>Like <see cref="ReadAll"/> for the segments whose levels start at <paramref name="b"/>.</summary>
    internal SortedDictionary<byte[], SortedDictionary<long, List<(int Col, int Pos)>>> ReadAllAt(long b)
    {
        List<SegDirRow> segs = _db.ReadDir(b, b + F3.LevelsPerIndex - 1);
        var seen = new Dictionary<byte[], HashSet<long>>(ByteKeyComparer.Instance);
        var result = new SortedDictionary<byte[], SortedDictionary<long, List<(int Col, int Pos)>>>(ByteKeyComparer.Instance);
        foreach (SegDirRow seg in segs)
        {
            foreach ((byte[] t, byte[] doclist) in _db.Scan(seg, null))
            {
                if (!seen.TryGetValue(t, out HashSet<long>? ids))
                {
                    seen[t] = ids = [];
                }

                foreach (F3.Entry e in F3.ParseDoclist(doclist, _cfg.Descending))
                {
                    if (!ids.Add(e.Docid) || e.IsDelete)
                    {
                        continue;
                    }

                    if (!result.TryGetValue(t, out SortedDictionary<long, List<(int, int)>>? d))
                    {
                        result[t] = d = [];
                    }

                    var l = new List<(int Col, int Pos)>();
                    F3.DecodePoslist(e.Poslist, 0, l);
                    d[e.Docid] = l;
                }
            }
        }

        return result;
    }

    /// <summary>The distinct languages that have segments.</summary>
    internal List<long> Languages()
    {
        var set = new SortedSet<long>();
        foreach (SegDirRow r in _db.ReadDir(long.MinValue, long.MaxValue))
        {
            set.Add(r.Level / F3.LevelsPerIndex / _cfg.IndexCount);
        }

        return [.. set];
    }

    // ---------------------------------------------------------------- tokenizing

    /// <summary>The text of a value as the tokenizer sees it (UTF-8), or null for NULL.</summary>
    internal static byte[]? TextOf(in Mem m)
    {
        if (m.Kind == StorageClass.Null)
        {
            return null;
        }

        Span<byte> scratch = stackalloc byte[64];
        return Vm.Values.AsText(m, scratch).ToArray();
    }

    internal DocTokens Tokenize(IReadOnlyList<Mem> values)
    {
        int n = _cfg.ColumnCount;
        var d = new DocTokens { ColTokens = new long[n] };
        for (int c = 0; c < n; c++)
        {
            if (_cfg.NotIndexed[c] || c >= values.Count)
            {
                continue;
            }

            byte[]? text = TextOf(values[c]);
            if (text is null)
            {
                continue;
            }

            d.Bytes += text.Length;
            var sink = new CollectSink();
            _cfg.Impl.Tokenize(text, TokenizeReason.Document, sink);
            int pos = 0;
            foreach ((byte[] tok, _, _) in sink.Tokens)
            {
                if (!d.Terms.TryGetValue(tok, out List<(int, int)>? l))
                {
                    d.Terms[tok] = l = [];
                }

                l.Add((c, pos++));
            }

            d.ColTokens[c] = pos;
        }

        return d;
    }

    // ---------------------------------------------------------------- collecting changes

    /// <summary>Changes of one statement: for each (language, index) the terms with the entries to write, collected in memory and written as one new segment each.</summary>
    internal sealed class Pending
    {
        internal readonly Dictionary<(long Lang, int Index), SortedDictionary<byte[], SortedDictionary<long, byte[]>>> Families = [];

        internal bool IsEmpty => Families.Count == 0;
    }

    private static void Put(Pending p, (long, int) fam, byte[] term, long docid, byte[] poslist)
    {
        if (!p.Families.TryGetValue(fam, out SortedDictionary<byte[], SortedDictionary<long, byte[]>>? terms))
        {
            p.Families[fam] = terms = new(ByteKeyComparer.Instance);
        }

        if (!terms.TryGetValue(term, out SortedDictionary<long, byte[]>? docs))
        {
            terms[term] = docs = [];
        }

        docs[docid] = poslist;
    }

    /// <summary>Records the terms of a document: with <paramref name="remove"/> as deletion markers, otherwise with their positions.</summary>
    internal void Add(Pending p, long langid, long docid, DocTokens tokens, bool remove)
    {
        byte[] empty = [0];
        foreach (KeyValuePair<byte[], List<(int Col, int Pos)>> kv in tokens.Terms)
        {
            byte[] pl = empty;
            if (!remove)
            {
                var b = new List<byte>();
                F3.EncodePoslist(b, kv.Value);
                pl = [.. b];
            }

            Put(p, (langid, 0), kv.Key, docid, pl);
        }

        for (int i = 0; i < _cfg.Prefixes.Count; i++)
        {
            int len = _cfg.Prefixes[i];
            var groups = new SortedDictionary<byte[], List<(int Col, int Pos)>>(ByteKeyComparer.Instance);
            foreach (KeyValuePair<byte[], List<(int Col, int Pos)>> kv in tokens.Terms)
            {
                if (kv.Key.Length < len)
                {
                    continue;
                }

                byte[] pt = kv.Key[..len];
                if (!groups.TryGetValue(pt, out List<(int, int)>? l))
                {
                    groups[pt] = l = [];
                }

                l.AddRange(kv.Value);
            }

            foreach (KeyValuePair<byte[], List<(int Col, int Pos)>> kv in groups)
            {
                byte[] pl = empty;
                if (!remove)
                {
                    kv.Value.Sort();
                    var b = new List<byte>();
                    F3.EncodePoslist(b, kv.Value);
                    pl = [.. b];
                }

                Put(p, (langid, i + 1), kv.Key, docid, pl);
            }
        }
    }

    /// <summary>Writes the collected changes as one new level-0 segment per index and merges full levels.</summary>
    internal void Flush(Pending p, int automerge)
    {
        foreach (KeyValuePair<(long Lang, int Index), SortedDictionary<byte[], SortedDictionary<long, byte[]>>> fam in p.Families.OrderBy(f => f.Key))
        {
            long b = FamilyBase(fam.Key.Lang, fam.Key.Index);
            var list = new List<(byte[], byte[])>();
            foreach (KeyValuePair<byte[], SortedDictionary<long, byte[]>> t in fam.Value)
            {
                IEnumerable<KeyValuePair<long, byte[]>> docs = _cfg.Descending ? t.Value.Reverse() : t.Value;
                list.Add((t.Key, F3.BuildDoclist([.. docs.Select(d => new F3.Entry(d.Key, d.Value))], _cfg.Descending)));
            }

            // A full level is merged by the insert after the one that filled it (fts3 "The merge=X,Y command").
            _db.Cascade(b, automerge >= 2 ? automerge : F3.MergeCount);
            long idx = _db.MaxIdx(b) + 1;
            _db.WriteSegment(b, idx, list);
        }

        p.Families.Clear();
    }
}
