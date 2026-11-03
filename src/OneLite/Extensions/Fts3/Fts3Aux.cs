using System.Buffers.Binary;
using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Internal.Text;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Extensions.Fts3;

/// <summary>The auxiliary functions <c>offsets</c>, <c>snippet</c> and <c>matchinfo</c>, and <c>optimize()</c> (sqlite.org/fts3.html, "Auxiliary Functions").</summary>
internal static class Fts3Aux
{
    /// <summary>True when the first argument is the hidden column of an fts3/fts4 table.</summary>
    internal static bool Handles(ReadOnlySpan<Mem> args) => args.Length > 0 && Fts3Cursor.FromRef(args[0]) is not null;

    private static string Txt(in Mem m)
    {
        Span<byte> scratch = stackalloc byte[64];
        return Encoding.UTF8.GetString(Vm.Values.AsText(m, scratch));
    }

    private static void SetText(ref Mem dst, ref byte[]? own, string s) => Vm.Values.SetBytes(ref dst, ref own, StorageClass.Text, Encoding.UTF8.GetBytes(s));

    internal static void Call(Vm.FuncId id, ReadOnlySpan<Mem> args, ref Mem dst, ref byte[]? own)
    {
        string name = id switch { Vm.FuncId.Fts3Offsets => "offsets", Vm.FuncId.Fts3Matchinfo => "matchinfo", Vm.FuncId.Fts3Optimize => "optimize", _ => "snippet" };
        Fts3Cursor? c = args.Length > 0 ? Fts3Cursor.FromRef(args[0]) : null;
        if (c is null)
        {
            throw new OneLiteException(ResultCode.Error, "unable to use function " + name + " in the requested context");
        }

        if (id == Vm.FuncId.Fts5Snippet && args.Length > 6)
        {
            throw new OneLiteException(ResultCode.Error, "wrong number of arguments to function snippet()");
        }

        switch (id)
        {
            case Vm.FuncId.Fts3Optimize:
                SetText(ref dst, ref own, c.Optimize() ? "Index optimized" : "Index already optimal");
                return;
            case Vm.FuncId.Fts3Offsets:
                if (!c.HasMatch || !c.OnRow)
                {
                    SetText(ref dst, ref own, string.Empty);
                    return;
                }

                SetText(ref dst, ref own, Offsets(c));
                return;
            case Vm.FuncId.Fts3Matchinfo:
            {
                string fmt = args.Length > 1 ? Txt(args[1]) : "pcx";
                if (!c.HasMatch || !c.OnRow)
                {
                    Vm.Values.SetBytes(ref dst, ref own, StorageClass.Blob, []);
                    return;
                }

                Vm.Values.SetBytes(ref dst, ref own, StorageClass.Blob, MatchInfo(c, fmt));
                return;
            }

            default:
            {
                string start = args.Length > 1 ? Txt(args[1]) : "<b>";
                string end = args.Length > 2 ? Txt(args[2]) : "</b>";
                string ell = args.Length > 3 ? Txt(args[3]) : "<b>...</b>";
                int col = args.Length > 4 ? (int)Vm.Values.ToInt(args[4]) : -1;
                int tokens = args.Length > 5 ? (int)Vm.Values.ToInt(args[5]) : 15;
                if (!c.HasMatch || !c.OnRow)
                {
                    SetText(ref dst, ref own, string.Empty);
                    return;
                }

                SetText(ref dst, ref own, Snippet(c, start, end, ell, col, tokens));
                return;
            }
        }
    }

    // ---------------------------------------------------------------- shared pieces

    /// <summary>The matchable phrases: all but those under a NOT, numbered in query order.</summary>
    private static List<QPhrase> Matchable(Fts3Query q) => [.. q.Phrases.Where(p => !p.Negated)];

    private static List<(int Col, int Pos)> InstancesIn(Fts3Query q, QPhrase ph, long doc, Fts3Cursor c)
    {
        Dictionary<long, List<(int Col, int Pos)>> hits = Fts3Query.HitsOf(ph, c.Index, c.Lang);
        if (!hits.TryGetValue(doc, out List<(int Col, int Pos)>? l))
        {
            return [];
        }

        // inside a NEAR chain only the instances that belong to a satisfied chain count
        QNear? near = FindNear(q.Root, ph);
        if (near is null)
        {
            return l;
        }

        return [.. l.Where(i => q.Participates(near, ph, doc, i, c.Index, c.Lang))];
    }

    private static QNear? FindNear(QNode? n, QPhrase ph)
    {
        switch (n)
        {
            case QNear nr:
                return nr.Parts.Contains(ph) ? nr : null;
            case QBin b:
                return FindNear(b.Left, ph) ?? FindNear(b.Right, ph);
            default:
                return null;
        }
    }

    /// <summary>Tokens of a column value: for each token position its byte offset and size.</summary>
    private static List<(int Start, int Size)> TokenSpans(Fts3Cursor c, Mem value)
    {
        byte[]? text = Fts3Index.TextOf(value);
        var l = new List<(int, int)>();
        if (text is null)
        {
            return l;
        }

        var sink = new CollectSink();
        c.Config.Impl.Tokenize(text, TokenizeReason.Aux, sink);
        foreach ((_, int s, int e) in sink.Tokens)
        {
            l.Add((s, e - s));
        }

        return l;
    }

    // ---------------------------------------------------------------- offsets

    private static string Offsets(Fts3Cursor c)
    {
        Fts3Query q = c.Query!;
        (Mem[] Values, long Lang)? row = c.Row();
        if (row is null)
        {
            return string.Empty;
        }

        List<QPhrase> ms = Matchable(q);
        var items = new List<(int Col, int Pos, int Term)>();
        int term = 0;
        foreach (QPhrase ph in ms)
        {
            foreach ((int col, int pos) in InstancesIn(q, ph, c.Docid, c))
            {
                for (int k = 0; k < ph.Terms.Count; k++)
                {
                    items.Add((col, pos + k, term + k));
                }
            }

            term += ph.Terms.Count;
        }

        items.Sort();
        var sb = new StringBuilder();
        var spans = new Dictionary<int, List<(int Start, int Size)>>();
        foreach ((int col, int pos, int t) in items)
        {
            if (col >= row.Value.Values.Length)
            {
                continue;
            }

            if (!spans.TryGetValue(col, out List<(int Start, int Size)>? sp))
            {
                spans[col] = sp = TokenSpans(c, row.Value.Values[col]);
            }

            if (pos >= sp.Count)
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(col).Append(' ').Append(t).Append(' ').Append(sp[pos].Start).Append(' ').Append(sp[pos].Size);
        }

        return sb.ToString();
    }

    // ---------------------------------------------------------------- matchinfo

    private static bool NodeMatches(QNode? n, long doc, Fts3Cursor c)
    {
        switch (n)
        {
            case null:
                return false;
            case QPhrase p:
                return Fts3Query.HitsOf(p, c.Index, c.Lang).ContainsKey(doc);
            case QNear nr:
                return Fts3Query.Evaluate(nr, c.Index, c.Lang).Contains(doc);
            case QBin b:
                return b.Op switch
                {
                    QOp.And => NodeMatches(b.Left, doc, c) && NodeMatches(b.Right, doc, c),
                    QOp.Or => NodeMatches(b.Left, doc, c) || NodeMatches(b.Right, doc, c),
                    _ => NodeMatches(b.Left, doc, c) && !NodeMatches(b.Right, doc, c),
                };
            default:
                return false;
        }
    }

    // Phrases that lie under only matching subexpressions (what 'y' and 'b' count).
    private static void Usable(QNode? n, long doc, Fts3Cursor c, bool ok, HashSet<QPhrase> into)
    {
        switch (n)
        {
            case QPhrase p:
                if (ok)
                {
                    into.Add(p);
                }

                break;
            case QNear nr:
                foreach (QPhrase p in nr.Parts)
                {
                    if (ok && NodeMatches(nr, doc, c))
                    {
                        into.Add(p);
                    }
                }

                break;
            case QBin b:
            {
                bool m = ok && NodeMatches(b, doc, c);
                Usable(b.Left, doc, c, m, into);
                if (b.Op != QOp.Not)
                {
                    Usable(b.Right, doc, c, m, into);
                }

                break;
            }
        }
    }

    private static byte[] MatchInfo(Fts3Cursor c, string fmt)
    {
        Fts3Config cfg = c.Config;
        Fts3Query q = c.Query!;
        List<QPhrase> ms = Matchable(q);
        int ncol = cfg.ColumnCount;
        var o = new List<uint>();
        long doc = c.Docid;
        foreach (char f in fmt)
        {
            switch (f)
            {
                case 'p':
                    o.Add((uint)ms.Count);
                    break;
                case 'c':
                    o.Add((uint)ncol);
                    break;
                case 'x':
                    foreach (QPhrase ph in ms)
                    {
                        Dictionary<long, List<(int Col, int Pos)>> all = Fts3Query.HitsOf(ph, c.Index, c.Lang);
                        for (int col = 0; col < ncol; col++)
                        {
                            long here = InstancesIn(q, ph, doc, c).Count(i => i.Col == col);
                            long total = 0;
                            long docs = 0;
                            foreach (long d in all.Keys)
                            {
                                long k = InstancesIn(q, ph, d, c).Count(i => i.Col == col);
                                total += k;
                                if (k > 0)
                                {
                                    docs++;
                                }
                            }

                            o.Add((uint)here);
                            o.Add((uint)total);
                            o.Add((uint)docs);
                        }
                    }

                    break;
                case 'y':
                case 'b':
                {
                    var use = new HashSet<QPhrase>();
                    Usable(q.Root, doc, c, true, use);
                    if (f == 'y')
                    {
                        foreach (QPhrase ph in ms)
                        {
                            for (int col = 0; col < ncol; col++)
                            {
                                o.Add(use.Contains(ph) ? (uint)InstancesIn(q, ph, doc, c).Count(i => i.Col == col) : 0u);
                            }
                        }
                    }
                    else
                    {
                        int words = (ncol + 31) / 32;
                        foreach (QPhrase ph in ms)
                        {
                            var bits = new uint[words];
                            if (use.Contains(ph))
                            {
                                for (int col = 0; col < ncol; col++)
                                {
                                    if (InstancesIn(q, ph, doc, c).Any(i => i.Col == col))
                                    {
                                        bits[col / 32] |= 1u << (col % 32);
                                    }
                                }
                            }

                            o.AddRange(bits);
                        }
                    }

                    break;
                }

                case 'n':
                    if (!cfg.IsFts4)
                    {
                        throw new OneLiteException(ResultCode.Error, "unrecognized matchinfo request: n");
                    }

                    o.Add((uint)Stat(c)[0]);
                    break;
                case 'a':
                    if (!cfg.IsFts4)
                    {
                        throw new OneLiteException(ResultCode.Error, "unrecognized matchinfo request: a");
                    }

                    {
                        long[] st = Stat(c);
                        for (int i = 0; i < ncol; i++)
                        {
                            o.Add(st[0] == 0 ? 0u : (uint)((st[1 + i] + st[0] / 2) / st[0]));
                        }
                    }

                    break;
                case 'l':
                    if (!cfg.HasDocSize)
                    {
                        throw new OneLiteException(ResultCode.Error, "unrecognized matchinfo request: l");
                    }

                    {
                        long[] sz = DocSize(c, doc);
                        for (int i = 0; i < ncol; i++)
                        {
                            o.Add((uint)sz[i]);
                        }
                    }

                    break;
                case 's':
                    for (int col = 0; col < ncol; col++)
                    {
                        o.Add((uint)Lcs(q, ms, c, doc, col));
                    }

                    break;
                default:
                    throw new OneLiteException(ResultCode.Error, "unrecognized matchinfo request: " + f);
            }
        }

        byte[] r = new byte[o.Count * 4];
        for (int i = 0; i < o.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(i * 4), o[i]);
        }

        if (!BitConverter.IsLittleEndian)
        {
            for (int i = 0; i < o.Count; i++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(i * 4), o[i]);
            }
        }

        return r;
    }

    private static long[] Stat(Fts3Cursor c)
    {
        var r = new long[c.Config.ColumnCount + 2];
        Statement s = c.Db.S("SELECT value FROM " + c.Db.T("_stat") + " WHERE id = 0");
        if (s.Step() && s.ColumnType(0) is ColumnType.Blob or ColumnType.Text)
        {
            byte[] b = s.GetBlob(0).ToArray();
            int pos = 0;
            for (int i = 0; i < r.Length && pos < b.Length; i++)
            {
                r[i] = F3.GetVarint(b, ref pos);
            }
        }

        s.Reset();
        return r;
    }

    private static long[] DocSize(Fts3Cursor c, long doc)
    {
        var r = new long[c.Config.ColumnCount];
        Statement s = c.Db.S("SELECT size FROM " + c.Db.T("_docsize") + " WHERE docid = ?1");
        s.Bind(1, doc);
        if (s.Step() && s.ColumnType(0) is ColumnType.Blob or ColumnType.Text)
        {
            byte[] b = s.GetBlob(0).ToArray();
            int pos = 0;
            for (int i = 0; i < r.Length && pos < b.Length; i++)
            {
                r[i] = F3.GetVarint(b, ref pos);
            }
        }

        s.Reset();
        return r;
    }

    // The longest run of phrase matches that follow each other in the column and are consecutive phrases of the query (a missing phrase breaks the run).
    private static int Lcs(Fts3Query q, List<QPhrase> ms, Fts3Cursor c, long doc, int col)
    {
        var inst = new List<(int Pos, int Len, int Phrase)>();
        for (int i = 0; i < ms.Count; i++)
        {
            foreach ((int cc, int p) in InstancesIn(q, ms[i], doc, c))
            {
                if (cc == col)
                {
                    inst.Add((p, ms[i].Terms.Count, i));
                }
            }
        }

        inst.Sort();
        int best = 0;
        var len = new int[inst.Count];
        for (int a = 0; a < inst.Count; a++)
        {
            len[a] = 1;
            for (int b = 0; b < a; b++)
            {
                if (inst[b].Pos + inst[b].Len == inst[a].Pos && inst[b].Phrase + 1 == inst[a].Phrase)
                {
                    len[a] = Math.Max(len[a], len[b] + 1);
                }
            }

            best = Math.Max(best, len[a]);
        }

        return best;
    }

    // ---------------------------------------------------------------- snippet

    private sealed class Frag
    {
        internal int Col;
        internal int Start;
        internal int Size;
        internal HashSet<int> Phrases = [];
        internal int Hits;
    }

    // Fragments are found by trying one to four windows (fts3 "The Snippet Function"): a window starts at a hit and takes the hits after it
    // that fit; it is then moved so that the hits it holds sit in its middle. Observed against the reference: the start is
    // (first hit + last hit + 1 - size) / 2, clamped to the column; windows are ranked by new phrases covered, then by hits held.
    private static string Snippet(Fts3Cursor c, string start, string end, string ell, int colArg, int tokensArg)
    {
        Fts3Query q = c.Query!;
        (Mem[] Values, long Lang)? row = c.Row();
        if (row is null)
        {
            return string.Empty;
        }

        int ncol = c.Config.ColumnCount;
        List<QPhrase> ms = Matchable(q);
        int n = Math.Abs(tokensArg);
        if (n == 0)
        {
            n = 15;
        }

        n = Math.Min(n, 64);
        bool fixedSize = tokensArg < 0;

        var hits = new List<(int Col, int Pos, int Phrase, int Len)>();
        for (int i = 0; i < ms.Count; i++)
        {
            foreach ((int col, int pos) in InstancesIn(q, ms[i], c.Docid, c))
            {
                if (colArg < 0 || col == colArg)
                {
                    hits.Add((col, pos, i, ms[i].Terms.Count));
                }
            }
        }

        hits.Sort();
        var present = new HashSet<int>(hits.Select(h => h.Phrase));
        var spans = new Dictionary<int, List<(int Start, int Size)>>();
        List<(int Start, int Size)> Spans(int col)
        {
            if (!spans.TryGetValue(col, out List<(int Start, int Size)>? sp))
            {
                spans[col] = sp = TokenSpans(c, row.Value.Values[col]);
            }

            return sp;
        }

        var frags = new List<Frag>();
        for (int m = 1; m <= 4; m++)
        {
            frags.Clear();
            var covered = new HashSet<int>();
            int size = fixedSize ? n : Math.Max(1, (n + m - 1) / m);
            for (int f = 0; f < m; f++)
            {
                Frag? best = null;
                int bestNew = -1;
                for (int col = 0; col < ncol; col++)
                {
                    List<(int Col, int Pos, int Phrase, int Len)> ch = [.. hits.Where(h => h.Col == col)];
                    if (ch.Count == 0)
                    {
                        continue;
                    }

                    int total = Spans(col).Count;
                    for (int i = 0; i < ch.Count; i++)
                    {
                        int first = ch[i].Pos;
                        int last = first;
                        var ph = new HashSet<int>();
                        int count = 0;
                        last = first + ch[i].Len - 1;
                        for (int j = i; j < ch.Count && (j == i || ch[j].Pos + ch[j].Len <= first + size); j++)
                        {
                            ph.Add(ch[j].Phrase);
                            last = Math.Max(last, ch[j].Pos + ch[j].Len - 1);
                            count++;
                        }

                        int s = (int)Math.Floor((first + last + 1 - size) / 2.0);
                        s = Math.Max(0, Math.Min(s, total - size));
                        s = Math.Max(0, s);
                        if (frags.Any(x => x.Col == col && x.Start == s))
                        {
                            continue;
                        }

                        int nw = ph.Count(x => !covered.Contains(x));
                        if (best is null || nw > bestNew || (nw == bestNew && count > best.Hits))
                        {
                            best = new Frag { Col = col, Start = s, Size = size, Phrases = ph, Hits = count };
                            bestNew = nw;
                        }
                    }
                }

                if (best is null)
                {
                    break;
                }

                frags.Add(best);
                covered.UnionWith(best.Phrases);
            }

            if (present.IsSubsetOf(covered))
            {
                break;
            }
        }

        if (frags.Count == 0)
        {
            // no match in the asked-for column: its first tokens
            if (colArg < 0 || colArg >= ncol)
            {
                return string.Empty;
            }

            frags.Add(new Frag { Col = colArg, Start = 0, Size = fixedSize ? n : n });
        }

        // fragments print in the order they were chosen: the one holding the most new phrases (then hits) first
        var sb = new StringBuilder();
        for (int fi = 0; fi < frags.Count; fi++)
        {
            Frag fr = frags[fi];
            List<(int Start, int Size)> sp = Spans(fr.Col);
            byte[] text = Fts3Index.TextOf(row.Value.Values[fr.Col]) ?? [];
            int st = fr.Start;
            int stop = Math.Min(sp.Count, st + fr.Size); // exclusive token index
            bool atStart = st == 0;
            bool atEnd = stop >= sp.Count;
            int from = atStart || sp.Count == 0 ? 0 : sp[st].Start;
            int to = atEnd || sp.Count == 0 ? text.Length : sp[stop - 1].Start + sp[stop - 1].Size;
            if (fi == 0 ? !atStart : true)
            {
                sb.Append(ell);
            }

            var marked = new SortedSet<int>();
            foreach ((int hc, int hp, _, int hl) in hits)
            {
                if (hc != fr.Col)
                {
                    continue;
                }

                for (int k = 0; k < hl; k++)
                {
                    if (hp + k >= st && hp + k < stop)
                    {
                        marked.Add(hp + k);
                    }
                }
            }

            int cur = from;
            foreach (int tp in marked)
            {
                sb.Append(Encoding.UTF8.GetString(text, cur, sp[tp].Start - cur));
                sb.Append(start).Append(Encoding.UTF8.GetString(text, sp[tp].Start, sp[tp].Size)).Append(end);
                cur = sp[tp].Start + sp[tp].Size;
            }

            sb.Append(Encoding.UTF8.GetString(text, cur, Math.Max(0, to - cur)));
            if (fi == frags.Count - 1 && !atEnd)
            {
                sb.Append(ell);
            }
        }

        return sb.ToString();
    }
}
