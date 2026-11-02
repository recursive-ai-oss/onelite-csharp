using System.Linq;
using System.Globalization;
using System.Text;
using RecursiveAI.OneLite.Internal.Text;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Extensions.Fts5;

/// <summary>The auxiliary functions <c>bm25</c>, <c>highlight</c> and <c>snippet</c> (fts5 "Built-in Auxiliary Functions").</summary>
internal static class Fts5Aux
{
    internal static void Call(Vm.FuncId id, ReadOnlySpan<Mem> args, ref Mem dst, ref byte[]? own)
    {
        if (id == Vm.FuncId.Fts5SourceId)
        {
            Vm.Values.SetBytes(ref dst, ref own, StorageClass.Text, Encoding.ASCII.GetBytes("fts5: 2026-07-24 19:02:57 bf7c7f30031888f4e796e429ab3978879485813aaca6f641c7b33e4e09459bcc"));
            return;
        }

        string name = id switch { Vm.FuncId.Fts5Bm25 => "bm25", Vm.FuncId.Fts5Highlight => "highlight", _ => "snippet" };
        Fts5Cursor? c = Fts5Cursor.FromRef(args[0]);
        if (c is null || c.Current is null)
        {
            throw new OneLiteException(ResultCode.Error, "unable to use function " + name + " in the requested context");
        }

        if (c.Config.Detail != DetailMode.Full)
        {
            Populate(c);
        }

        switch (id)
        {
            case Vm.FuncId.Fts5Bm25:
            {
                var w = new double[Math.Max(0, args.Length - 1)];
                for (int i = 0; i < w.Length; i++)
                {
                    w[i] = Vm.Values.ToReal(args[i + 1]);
                }

                Vm.Values.SetReal(ref dst, Bm25(c, w));
                return;
            }

            case Vm.FuncId.Fts5Highlight:
            {
                string? s = Highlight(c, (int)Vm.Values.ToInt(args[1]), Txt(args[2]), Txt(args[3]));
                if (s is null)
                {
                    Vm.Values.SetNull(ref dst);
                }
                else
                {
                    Vm.Values.SetBytes(ref dst, ref own, StorageClass.Text, Encoding.UTF8.GetBytes(s));
                }

                return;
            }

            default:
            {
                int n = (int)Math.Clamp(Vm.Values.ToInt(args[5]), 1, 64); // observed: out-of-range counts are clamped

                string? s = Snippet(c, (int)Vm.Values.ToInt(args[1]), Txt(args[2]), Txt(args[3]), Txt(args[4]), n);
                if (s is null)
                {
                    Vm.Values.SetNull(ref dst);
                }
                else
                {
                    Vm.Values.SetBytes(ref dst, ref own, StorageClass.Text, Encoding.UTF8.GetBytes(s));
                }

                return;
            }
        }
    }

    /// <summary>True for <c>name(number, ...)</c>: what the <c>rank</c> special command accepts (the function itself is looked up when used).</summary>
    internal static bool RankSpecIsValid(string spec)
    {
        spec = spec.Trim();
        int open = spec.IndexOf('(', StringComparison.Ordinal);
        if (open < 1 || spec[^1] != ')')
        {
            return false;
        }

        string name = spec[..open].Trim();
        if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_') || !name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_'))
        {
            return false;
        }

        string args = spec[(open + 1)..^1];
        return args.Trim().Length == 0 || args.Split(',').All(x => double.TryParse(x.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _));
    }

    private static string Txt(in Mem m)
    {
        Span<byte> scratch = stackalloc byte[64];
        return Encoding.UTF8.GetString(Vm.Values.AsText(m, scratch));
    }

    /// <summary>The value of the <c>rank</c> column: the configured ranking function, which must be <c>bm25</c> with optional weights.</summary>
    internal static double RankValue(Fts5Cursor c, string spec)
    {
        spec = spec.Trim();
        int open = spec.IndexOf('(', StringComparison.Ordinal);
        string fn = open < 0 ? spec : spec[..open].Trim();
        var w = new List<double>();
        if (open >= 0)
        {
            int close = spec.LastIndexOf(')');
            if (close < open)
            {
                throw new OneLiteException(ResultCode.Error, "parse error in \"" + spec + "\"");
            }

            foreach (string a in spec[(open + 1)..close].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                {
                    throw new OneLiteException(ResultCode.Error, "parse error in \"" + spec + "\"");
                }

                w.Add(d);
            }
        }

        if (!fn.Equals("bm25", StringComparison.OrdinalIgnoreCase))
        {
            throw new OneLiteException(ResultCode.Error, "no such function: " + fn);
        }

        return Bm25(c, [.. w]);
    }

    /// <summary>Okapi BM25 with k1 = 1.2 and b = 0.75, negated so that better matches sort first (Robertson and Zaragoza).</summary>
    internal static double Bm25(Fts5Cursor c, double[] weights)
    {
        const double k1 = 1.2;
        const double b = 0.75;
        DocMatch d = c.Current!;
        long[] avg = c.Averages;
        double nRow = avg.Length > 0 ? avg[0] : 0;
        double total = 0;
        for (int i = 1; i < avg.Length; i++)
        {
            total += avg[i];
        }

        double avgdl = nRow > 0 ? total / nRow : 1;
        if (avgdl <= 0)
        {
            avgdl = 1;
        }

        double dl = 0;
        foreach (long s in c.Sizes)
        {
            dl += s;
        }

        double score = 0;
        for (int p = 0; p < c.Phrases.Count; p++)
        {
            double n = c.PhraseDocs[p];
            double idf = Math.Log((nRow - n + 0.5) / (n + 0.5));
            if (idf <= 0)
            {
                idf = 1e-6;
            }

            double tf = 0;
            if (p < d.Inst.Length && d.Inst[p] is { } inst)
            {
                foreach (long pos in inst)
                {
                    int col = (int)(pos >> 32);
                    tf += col < weights.Length ? weights[col] : 1.0;
                }
            }

            score += idf * ((tf * (k1 + 1.0)) / (tf + (k1 * (1 - b + (b * dl / avgdl)))));
        }

        return -score;
    }

    /// <summary>
    /// With detail other than full the index holds no usable positions, so the instances of the matched phrases are found the way the
    /// reference finds them: by tokenizing the row's column text and comparing the tokens with the (single-token) phrases.
    /// </summary>
    private static void Populate(Fts5Cursor c)
    {
        DocMatch d = c.Current!;
        if (d.Resolved)
        {
            return;
        }

        d.Resolved = true;
        long[]?[] took = d.Inst;
        var lists = new List<long>[c.Phrases.Count];
        for (int col = 0; col < c.Config.ColumnCount; col++)
        {
            byte[]? text = c.ColumnText(col); // every column, unindexed ones too (observed)
            if (text is null)
            {
                continue;
            }

            var toks = new TokenBytes();
            c.Config.Tokenizer.Tokenize(text, TokenizeReason.Aux, toks);
            for (int p = 0; p < c.Phrases.Count; p++)
            {
                QPhrase ph = c.Phrases[p];
                if (ph.Terms.Count != 1 || (p < took.Length && took[p] is null) || (ph.Cols is not null && (col >= ph.Cols.Length || !ph.Cols[col])))
                {
                    continue;
                }

                PTerm t = ph.Terms[0];
                for (int i = 0; i < toks.L.Count; i++)
                {
                    ReadOnlySpan<byte> tk = toks.L[i];
                    if (t.Prefix ? tk.StartsWith(t.Token) : tk.SequenceEqual(t.Token))
                    {
                        (lists[p] ??= []).Add(((long)col << 32) | (uint)i);
                    }
                }
            }
        }

        d.Inst = new long[]?[c.Phrases.Count];
        for (int p = 0; p < lists.Length; p++)
        {
            d.Inst[p] = lists[p]?.ToArray();
        }
    }

    private sealed class TokenBytes : TokenSink
    {
        internal readonly List<byte[]> L = [];

        internal override void Token(ReadOnlySpan<byte> token, int start, int end) => L.Add(token.ToArray());
    }

    /// <summary>The text between two tokens ends a sentence when its last non-blank character is '.' or ':' and blank space follows it.</summary>
    private static bool SentenceStart(byte[] text, int from, int to)
    {
        if (to <= from || text[to - 1] is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)'\f'))
        {
            return false;
        }

        int i = to - 1;
        while (i >= from && text[i] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)'\f')
        {
            i--;
        }

        return i >= from && text[i] is (byte)'.' or (byte)':';
    }

    private sealed class Toks : TokenSink
    {
        internal readonly List<(int Start, int End)> L = [];

        internal override void Token(ReadOnlySpan<byte> token, int start, int end) => L.Add((start, end));
    }

    /// <summary>Instances in one column as token ranges (first, last, phrase), sorted.</summary>
    private static List<(int A, int B, int P)> Inst(Fts5Cursor c, int col)
    {
        var r = new List<(int, int, int)>();
        DocMatch d = c.Current!;
        // positions read from a damaged index can lie beyond the column's tokens; they describe nothing in this text
        int limit = c.Config.Unindexed[col] ? int.MaxValue : TokenCount(c, col);
        for (int p = 0; p < c.Phrases.Count && p < d.Inst.Length; p++)
        {
            long[]? inst = d.Inst[p] ?? (d.Leak is { } lk && p < lk.Length ? lk[p] : null);
            if (inst is null)
            {
                continue;
            }

            int width = Math.Max(1, c.Phrases[p].Terms.Count);
            foreach (long pos in inst)
            {
                if ((int)(pos >> 32) == col)
                {
                    int off = (int)(pos & 0xFFFFFFFFL);
                    if (off >= limit)
                    {
                        continue;
                    }

                    r.Add((off, Math.Min(off + width - 1, limit - 1), p));
                }
            }
        }

        r.Sort();
        return r;
    }

    private static int TokenCount(Fts5Cursor c, int col)
    {
        byte[]? text = c.ColumnText(col);
        if (text is null || c.Config.Unindexed[col])
        {
            return 0;
        }

        var t = new Toks();
        c.Config.Tokenizer.Tokenize(text, TokenizeReason.Aux, t);
        return t.L.Count;
    }

    private static List<(int A, int B)> Ranges(Fts5Cursor c, int col) => Inst(c, col).Select(i => (i.A, i.B)).ToList();

    /// <summary>The best anchor instance of a column: the one whose n-token window holds the most different phrases (first wins ties).</summary>
    private static (int Score, int Anchor) BestAnchor(List<(int A, int B, int P)> inst, int n, int m)
    {
        int best = -1;
        int anchor = 0;
        foreach ((int a, _, _) in inst)
        {
            int score = 0;
            foreach ((int x, int y, int p) in inst)
            {
                if (x >= a && y <= a + n - 1)
                {
                    score++;
                }
            }

            // observed (choosing among columns): an instance without more than n tokens from it to the end of its text scores nothing,
            // so a column whose matches all sit at its end loses to one with a match that has room
            if (m >= 0 && a + n >= m)
            {
                score = 0;
            }

            if (score > best)
            {
                best = score;
                anchor = a;
            }
        }

        return (best, anchor); // -1: no instance at all
    }

    private static List<(int A, int B)> Union(List<(int A, int B)> r)
    {
        var u = new List<(int A, int B)>();
        foreach ((int a, int b) in r)
        {
            if (u.Count > 0 && a <= u[^1].B)
            {
                u[^1] = (u[^1].A, Math.Max(u[^1].B, b));
            }
            else
            {
                u.Add((a, b));
            }
        }

        return u;
    }

    private static string? Highlight(Fts5Cursor c, int col, string open, string close)
    {
        if (col < 0 || col >= c.Config.ColumnCount)
        {
            return null; // observed: no error, an empty result
        }

        byte[]? text = c.ColumnText(col);
        if (text is null)
        {
            return null;
        }

        var toks = new Toks();
        c.Config.Tokenizer.Tokenize(text, TokenizeReason.Aux, toks);
        List<(int A, int B)> ranges = Union(Ranges(c, col));
        var sb = new StringBuilder();
        int last = 0;
        foreach ((int a, int b) in ranges)
        {
            if (a >= toks.L.Count)
            {
                continue;
            }

            int bb = Math.Min(b, toks.L.Count - 1);
            if (a < 0 || bb < a || toks.L[a].Start < last)
            {
                continue; // positions from a damaged index that do not describe this text
            }

            sb.Append(Encoding.UTF8.GetString(text, last, toks.L[a].Start - last)).Append(open);
            sb.Append(Encoding.UTF8.GetString(text, toks.L[a].Start, toks.L[bb].End - toks.L[a].Start)).Append(close);
            last = toks.L[bb].End;
        }

        sb.Append(Encoding.UTF8.GetString(text, last, text.Length - last));
        return sb.ToString();
    }

    private static string? Snippet(Fts5Cursor c, int col, string open, string close, string ellipsis, int n)
    {
        int ncol = c.Config.ColumnCount;
        if (col >= ncol)
        {
            throw new OneLiteException(25, "column index out of range");
        }

        int bestCol = col;
        if (col < 0)
        {
            int best = -1;
            bestCol = 0;
            for (int i = 0; i < ncol; i++)
            {
                if (c.Config.Unindexed[i])
                {
                    continue;
                }

                (int sc, _) = BestAnchor(Inst(c, i), n, TokenCount(c, i));
                if (sc > best)
                {
                    best = sc; // equal scores keep the first column
                    bestCol = i;
                }
            }
        }

        byte[]? text = c.ColumnText(bestCol);
        if (text is null)
        {
            return null;
        }

        if (c.Config.Unindexed[bestCol])
        {
            return Encoding.UTF8.GetString(text); // nothing is tokenized there: the whole text
        }

        var toks = new Toks();
        c.Config.Tokenizer.Tokenize(text, TokenizeReason.Aux, toks);
        int m = toks.L.Count;
        if (m == 0)
        {
            return Encoding.UTF8.GetString(text); // no tokens: the text as it is
        }

        List<(int A, int B, int P)> inst = Inst(c, bestCol);
        int ws = 0;
        if (m > n && inst.Count > 0)
        {
            int anchor = BestAnchor(inst, n, m).Anchor;
            int last = anchor;
            foreach ((int x, int y, _) in inst)
            {
                if (x >= anchor && (y <= anchor + n - 1 || x == anchor))
                {
                    last = Math.Max(last, y);
                }
            }

            int span = last - anchor + 1;
            ws = Math.Clamp(anchor - ((n - span) / 2), 0, m - n);
            // observed: the window starts at the latest sentence start before the first match that still reaches it; the text start is
            // a sentence start, so a first match within the first n tokens gives a window at the text start
            int pick = -1;
            bool anchorStartsSentence = anchor > 0 && SentenceStart(text, toks.L[anchor - 1].End, toks.L[anchor].Start);
            // a sentence start at the first match itself counts when more than one token is covered (observed)
            for (int t = Math.Min(span > 1 ? anchor : anchor - 1, m - 1); t >= 0 && pick < 0; t--)
            {
                if (t + n > anchor && ((t == 0 && !anchorStartsSentence) || (t > 0 && SentenceStart(text, toks.L[t - 1].End, toks.L[t].Start))))
                {
                    pick = t;
                }
            }

            if (pick < 0 && anchor == 0 && !anchorStartsSentence)
            {
                pick = 0;
            }

            if (pick >= 0)
            {
                ws = pick;
            }
        }

        int we = Math.Min(m, ws + n) - 1;
        var sb = new StringBuilder();
        if (ws > 0)
        {
            sb.Append(ellipsis);
        }

        int pos = ws == 0 ? 0 : toks.L[ws].Start; // from the very start of the text when the window starts there
        foreach ((int a, int b) in Union(inst.Select(i => (i.A, i.B)).ToList()))
        {
            if (a < ws || a > we) // a phrase that began before the window is not marked
            {
                continue;
            }

            int aa = Math.Max(a, ws);
            int bb = Math.Min(b, we);
            if (bb < aa || toks.L[aa].Start < pos)
            {
                continue; // positions from a damaged index that do not describe this text
            }

            sb.Append(Encoding.UTF8.GetString(text, pos, toks.L[aa].Start - pos)).Append(open);
            sb.Append(Encoding.UTF8.GetString(text, toks.L[aa].Start, toks.L[bb].End - toks.L[aa].Start)).Append(close);
            pos = toks.L[bb].End;
        }

        // a snippet that reaches the end of the text keeps the text that follows its last token
        sb.Append(Encoding.UTF8.GetString(text, pos, Math.Max(0, (we == m - 1 ? text.Length : toks.L[we].End) - pos)));
        if (we < m - 1)
        {
            sb.Append(ellipsis);
        }

        return sb.ToString();
    }
}
