using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Internal.Text;

namespace RecursiveAI.OneLite.Extensions.Fts3;

/// <summary>One term of a query phrase, with its prefix flag.</summary>
internal readonly record struct QTerm(byte[] Term, bool Prefix);

internal abstract class QNode
{
}

/// <summary>A phrase: terms that must follow each other, in one column (or any column).</summary>
internal sealed class QPhrase : QNode
{
    internal List<QTerm> Terms = [];
    internal int Column = -1;
    internal bool Anchored;

    /// <summary>Position in the query among all phrases (the index the auxiliary functions use).</summary>
    internal int Index;

    /// <summary>Index of the phrase's first term among all terms of the query (offsets() reports this).</summary>
    internal int FirstTermIndex;

    /// <summary>True when the phrase is under a NOT: its instances are not reported.</summary>
    internal bool Negated;

    internal Dictionary<long, List<(int Col, int Pos)>>? Hits;
}

/// <summary><c>a NEAR/n b NEAR/m c</c>: phrases that must lie close together in one column.</summary>
internal sealed class QNear : QNode
{
    internal List<QPhrase> Parts = [];
    internal List<int> Distances = [];
}

internal enum QOp
{
    And,
    Or,
    Not,
}

internal sealed class QBin : QNode
{
    internal QOp Op;
    internal QNode Left = null!;
    internal QNode Right = null!;
}

/// <summary>Parses and evaluates the standard query syntax of fts3 and fts4 (sqlite.org/fts3.html, "Full-text Index Queries").</summary>
internal sealed class Fts3Query
{
    private readonly Fts3Config _cfg;
    private readonly string _text;
    private readonly byte[] _bytes;
    private int _i;
    private int _defaultCol;

    internal List<QPhrase> Phrases = [];

    internal QNode? Root;

    private Fts3Query(Fts3Config cfg, string text, int defaultCol)
    {
        _cfg = cfg;
        _text = text;
        _bytes = Encoding.UTF8.GetBytes(text);
        _defaultCol = defaultCol;
    }

    private const int MaxPhrases = 4095;
    private const int MaxNearOperands = 13;

    private static OneLiteException TooLarge() => new(ResultCode.Error, "FTS expression tree is too large (maximum depth 12)");

    internal static OneLiteException Malformed(string q) => new(ResultCode.Error, "malformed MATCH expression: [" + q + "]");

    /// <summary>Parses <paramref name="text"/>; <paramref name="column"/> is the column the MATCH was written against (or -1 for all).</summary>
    internal static Fts3Query Parse(Fts3Config cfg, string text, int column)
    {
        var q = new Fts3Query(cfg, text, column);
        q.Root = q.ParseAnd();
        // The reference balances chains of AND and OR into a tree and allows 12 levels: 4095 phrases (observed), and 12 NEAR operators in a row.
        if (q.Phrases.Count > MaxPhrases)
        {
            throw TooLarge();
        }

        int term = 0;
        int ph = 0;
        foreach (QPhrase p in q.Phrases)
        {
            p.Index = ph++;
            p.FirstTermIndex = term;
            term += p.Terms.Count;
        }

        return q;
    }

    // ---------------------------------------------------------------- lexing

    private enum Kind
    {
        End,
        Or,
        Near,
        Minus,
        Phrase,
    }

    private sealed class Lex
    {
        internal Kind K;
        internal int NearDistance = 10;
        internal QPhrase? Phrase;
    }

    private static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0b or 0x0c;

    // what may follow OR and NEAR for them to be operators (observed): the end, white space, a parenthesis or a double quote
    private bool SpaceOrEnd(int p) => p >= _bytes.Length || IsSpace(_bytes[p]) || _bytes[p] is (byte)'(' or (byte)')' or (byte)'"';

    private List<Lex> LexAll()
    {
        var l = new List<Lex>();
        int col = _defaultCol;
        bool anchor = false;
        _i = 0;
        int pendingCol = -2; // column filter waiting for its term
        // OR and NEAR are operators only where the reference looks for them: right after white space that follows the previous word or phrase,
        // and followed by white space, a parenthesis or a quote. After a separator or punctuation byte that is not white space (observed: "alpha, OR beta", "(a) OR b",
        // or a tokenizer separator right after a word) they are ordinary words.
        bool dirty = false;
        bool skipped = false; // the previous step only skipped one byte
        while (_i < _bytes.Length)
        {
            if (!skipped)
            {
                dirty = false;
            }

            skipped = false;
            byte b = _bytes[_i];
            if (b == (byte)'"')
            {
                int end = Array.IndexOf(_bytes, (byte)'"', _i + 1);
                bool closed = end >= 0;
                if (!closed)
                {
                    throw Malformed(_text);
                }

                QPhrase ph = PhraseOf(_bytes.AsSpan(_i + 1, end - _i - 1), true);
                // a '^' before the quote does nothing (observed); one right before the first word inside it anchors the phrase to the start of its column
                anchor = _cfg.IsFts4 && QuoteIsAnchored(_bytes.AsSpan(_i + 1, end - _i - 1));
                _i = closed ? end + 1 : end;
                Finish(l, ph, ref pendingCol, ref anchor, true);
                continue;
            }

            // A '-' directly before a token is the NOT operator, unless the tokenizer counts '-' as a token character.
            if (b == (byte)'-' && !StartsToken(_i) && StartsToken(_i + 1))
            {
                l.Add(new Lex { K = Kind.Minus });
                _i++;
                continue;
            }

            if (b == (byte)'-' && _cfg.IsFts4 && _i + 1 < _bytes.Length && _bytes[_i + 1] == (byte)'^' && !StartsToken(_i) && StartsToken(_i + 2))
            {
                throw Malformed(_text); // a negated word cannot be anchored
            }

            // '^' anchors the word that follows it directly
            if (b == (byte)'^' && _cfg.IsFts4 && StartsToken(_i + 1))
            {
                anchor = true;
                _i++;
                continue;
            }

            if (b == (byte)'O' && _i + 1 < _bytes.Length && _bytes[_i + 1] == (byte)'R' && SpaceOrEnd(_i + 2) && !dirty && (_i == 0 || !IsWordByte(_bytes[_i - 1])))
            {
                l.Add(new Lex { K = Kind.Or });
                _i += 2;
                continue;
            }

            if (b == (byte)'N' && _i + 3 < _bytes.Length && _bytes[_i + 1] == (byte)'E' && _bytes[_i + 2] == (byte)'A' && _bytes[_i + 3] == (byte)'R' && !dirty && (_i == 0 || !IsWordByte(_bytes[_i - 1])))
            {
                int p = _i + 4;
                int dist = 10;
                bool ok = true;
                if (p < _bytes.Length && _bytes[p] == (byte)'/')
                {
                    int q = p + 1;
                    int digits = 0;
                    long v = 0;
                    while (q < _bytes.Length && _bytes[q] >= (byte)'0' && _bytes[q] <= (byte)'9')
                    {
                        v = Math.Min(int.MaxValue, v * 10 + (_bytes[q] - '0'));
                        q++;
                        digits++;
                    }

                    if (digits > 0)
                    {
                        dist = (int)v;
                        p = q;
                    }
                    else
                    {
                        ok = false;
                    }
                }

                if (ok && SpaceOrEnd(p))
                {
                    l.Add(new Lex { K = Kind.Near, NearDistance = dist });
                    _i = p;
                    continue;
                }
            }

            if (StartsToken(_i))
            {
                int st = _i;
                if (FirstToken(st) is not { } first)
                {
                    _i = _bytes.Length;
                    continue;
                }

                (byte[] tok, int s0, int e0) = first;
                int tokStart = st + s0;
                int tokEnd = st + e0;
                // a column filter: the declared name immediately followed by ':'
                if (tokEnd < _bytes.Length && _bytes[tokEnd] == (byte)':' && tokStart == _i)
                {
                    string name = Encoding.UTF8.GetString(_bytes, tokStart, tokEnd - tokStart);
                    int ci = _cfg.Columns.FindIndex(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (ci >= 0)
                    {
                        pendingCol = ci;
                        _i = tokEnd + 1;
                        continue;
                    }
                }

                var ph = new QPhrase { Terms = [new QTerm(tok, false)] };
                _i = tokEnd;
                if (_i < _bytes.Length && _bytes[_i] == (byte)'*')
                {
                    ph.Terms[0] = new QTerm(tok, true);
                    _i++;
                }

                Finish(l, ph, ref pendingCol, ref anchor, false);
                continue;
            }

            dirty |= !IsSpace(b);
            skipped = true;
            _i++;
        }

        _ = col;
        return l;
    }

    // True when the tokenizer, run on the text from position p, starts its first token right there.
    private bool StartsToken(int p)
    {
        if (p >= _bytes.Length)
        {
            return false;
        }

        if (p < _noTokenBefore)
        {
            return false;
        }

        return FirstToken(p) is { Start: 0 };
    }

    // Positions below this are known not to start a token (the nearest token starts later), so a run of separators is scanned once.
    private int _noTokenBefore;

    // The first token of the text from position p (offsets relative to p), or null if there is none. The tokenizer cannot be stopped after one
    // token, so it runs on a window that doubles until the token ends inside it; running it on the whole rest of the query for every word
    // would make a long query quadratic.
    private (byte[] Token, int Start, int End)? FirstToken(int p)
    {
        int rest = _bytes.Length - p;
        for (int window = 64; ; window *= 4)
        {
            int len = Math.Min(window, rest);
            var sink = new CollectSink();
            _cfg.Impl.Tokenize(_bytes.AsSpan(p, len), TokenizeReason.Query, new FirstOnly(sink));
            if (sink.Tokens.Count == 0 && len < rest)
            {
                continue;
            }

            if (sink.Tokens.Count == 0)
            {
                _noTokenBefore = _bytes.Length;
                return null;
            }

            if (sink.Tokens[0].End < len || len == rest)
            {
                _noTokenBefore = Math.Max(_noTokenBefore, p + sink.Tokens[0].Start);
                return sink.Tokens[0];
            }
        }
    }

    private static bool IsWordByte(byte b) => char.IsAsciiLetterOrDigit((char)b) || b >= 0x80 || b == (byte)'_';

    // Passes only the first token on, then ignores the rest.
    private sealed class FirstOnly(CollectSink inner) : TokenSink
    {
        internal override void Token(ReadOnlySpan<byte> token, int start, int end)
        {
            if (inner.Tokens.Count == 0)
            {
                inner.Token(token, start, end);
            }
        }
    }

    // A column filter in front of a quoted phrase has no effect in the reference (observed with a space after the colon; written without one the
    // reference finds nothing, which is not emulated), so only the column of the MATCH applies there.
    private void Finish(List<Lex> l, QPhrase ph, ref int pendingCol, ref bool anchor, bool quoted)
    {
        if (ph.Terms.Count == 0)
        {
            pendingCol = -2;
            anchor = false;
            return;
        }

        ph.Column = !quoted && pendingCol >= -1 ? pendingCol : _defaultCol;
        ph.Anchored = anchor;
        pendingCol = -2;
        anchor = false;
        l.Add(new Lex { K = Kind.Phrase, Phrase = ph });
    }

    private bool QuoteIsAnchored(ReadOnlySpan<byte> text)
    {
        var sink = new CollectSink();
        _cfg.Impl.Tokenize(text, TokenizeReason.Query, sink);
        return sink.Tokens.Count > 0 && sink.Tokens[0].Start > 0 && text[sink.Tokens[0].Start - 1] == (byte)'^';
    }

    private QPhrase PhraseOf(ReadOnlySpan<byte> text, bool quoted)
    {
        var sink = new CollectSink();
        _cfg.Impl.Tokenize(text, TokenizeReason.Query, sink);
        var ph = new QPhrase();
        foreach ((byte[] tok, _, int end) in sink.Tokens)
        {
            ph.Terms.Add(new QTerm(tok, end < text.Length && text[end] == (byte)'*'));
        }

        _ = quoted;
        return ph;
    }

    // ---------------------------------------------------------------- grammar

    private List<Lex> _l = [];
    private int _p;

    private QNode? ParseAnd()
    {
        _l = LexAll();
        _p = 0;
        if (_l.Count == 0)
        {
            return null;
        }

        var positives = new List<QNode>();
        var negatives = new List<QPhrase>();
        while (_p < _l.Count)
        {
            if (_l[_p].K == Kind.Minus)
            {
                _p++;
                if (_p >= _l.Count || _l[_p].K != Kind.Phrase)
                {
                    throw Malformed(_text);
                }

                var n = (QPhrase)ParseOrGroup(true);
                n.Negated = true;
                negatives.Add(n);
                continue;
            }

            positives.Add(ParseOrGroup(false));
        }

        if (positives.Count == 0)
        {
            throw Malformed(_text);
        }

        QNode left = positives[0];
        for (int i = 1; i < positives.Count; i++)
        {
            left = new QBin { Op = QOp.And, Left = left, Right = positives[i] };
        }

        foreach (QPhrase n in negatives)
        {
            left = new QBin { Op = QOp.Not, Left = left, Right = n };
        }

        return left;
    }

    private QNode ParseOrGroup(bool negated)
    {
        QNode left = ParseNear(negated);
        while (_p < _l.Count && _l[_p].K == Kind.Or)
        {
            if (negated)
            {
                throw Malformed(_text);
            }

            _p++;
            if (_p < _l.Count && _l[_p].K == Kind.Minus)
            {
                throw Malformed(_text);
            }

            QNode right = ParseNear(false);
            left = new QBin { Op = QOp.Or, Left = left, Right = right };
        }

        return left;
    }

    private QNode ParseNear(bool negated)
    {
        if (_p >= _l.Count)
        {
            throw Malformed(_text);
        }

        Lex t = _l[_p];
        if (t.K != Kind.Phrase)
        {
            throw Malformed(_text);
        }

        _p++;
        QPhrase first = t.Phrase!;
        Phrases.Add(first);
        if (_p < _l.Count && _l[_p].K == Kind.Near)
        {
            if (negated)
            {
                throw Malformed(_text);
            }

            var n = new QNear();
            n.Parts.Add(first);
            while (_p < _l.Count && _l[_p].K == Kind.Near)
            {
                int d = _l[_p].NearDistance;
                _p++;
                if (_p >= _l.Count || _l[_p].K != Kind.Phrase)
                {
                    throw Malformed(_text);
                }

                QPhrase nx = _l[_p].Phrase!;
                _p++;
                Phrases.Add(nx);
                n.Parts.Add(nx);
                n.Distances.Add(d);
                if (n.Parts.Count > MaxNearOperands)
                {
                    throw TooLarge();
                }
            }

            return n;
        }

        return first;
    }

    // ---------------------------------------------------------------- evaluation

    /// <summary>The instances (column, start position) of a phrase in each document, from the index.</summary>
    internal static Dictionary<long, List<(int Col, int Pos)>> HitsOf(QPhrase ph, Fts3Index idx, long langid)
    {
        if (ph.Hits is { } cached)
        {
            return cached;
        }

        var perTerm = new List<SortedDictionary<long, List<(int Col, int Pos)>>>();
        foreach (QTerm t in ph.Terms)
        {
            perTerm.Add(idx.Postings(langid, t.Term, t.Prefix));
        }

        var res = new Dictionary<long, List<(int Col, int Pos)>>();
        foreach (KeyValuePair<long, List<(int Col, int Pos)>> first in perTerm[0])
        {
            var inst = new List<(int, int)>();
            bool ok = true;
            var sets = new List<HashSet<(int, int)>>();
            for (int k = 1; k < perTerm.Count && ok; k++)
            {
                if (!perTerm[k].TryGetValue(first.Key, out List<(int, int)>? l))
                {
                    ok = false;
                    break;
                }

                sets.Add([.. l]);
            }

            if (!ok)
            {
                continue;
            }

            foreach ((int c, int p) in first.Value)
            {
                if (ph.Column >= 0 && c != ph.Column)
                {
                    continue;
                }

                if (ph.Anchored && p != 0)
                {
                    continue;
                }

                bool all = true;
                for (int k = 1; k < perTerm.Count; k++)
                {
                    if (!sets[k - 1].Contains((c, p + k)))
                    {
                        all = false;
                        break;
                    }
                }

                if (all)
                {
                    inst.Add((c, p));
                }
            }

            if (inst.Count > 0)
            {
                res[first.Key] = inst;
            }
        }

        ph.Hits = res;
        return res;
    }

    /// <summary>The ids of the documents the query matches.</summary>
    internal static HashSet<long> Evaluate(QNode? n, Fts3Index idx, long langid)
    {
        switch (n)
        {
            case null:
                return [];
            case QPhrase p:
                return [.. HitsOf(p, idx, langid).Keys];
            case QNear nr:
                return EvalNear(nr, idx, langid);
            case QBin b:
            {
                if (b.Op == QOp.Not)
                {
                    HashSet<long> l = Evaluate(b.Left, idx, langid);
                    HashSet<long> r = Evaluate(b.Right, idx, langid);
                    l.ExceptWith(r);
                    return l;
                }

                HashSet<long> a = Evaluate(b.Left, idx, langid);
                HashSet<long> c = Evaluate(b.Right, idx, langid);
                if (b.Op == QOp.And)
                {
                    a.IntersectWith(c);
                }
                else
                {
                    a.UnionWith(c);
                }

                return a;
            }

            default:
                return [];
        }
    }

    private static HashSet<long> EvalNear(QNear n, Fts3Index idx, long langid)
    {
        var docs = new HashSet<long>(HitsOf(n.Parts[0], idx, langid).Keys);
        for (int i = 1; i < n.Parts.Count; i++)
        {
            docs.IntersectWith(HitsOf(n.Parts[i], idx, langid).Keys);
        }

        var res = new HashSet<long>();
        foreach (long d in docs)
        {
            if (NearMatch(n, d, idx, langid))
            {
                res.Add(d);
            }
        }

        return res;
    }

    private static bool NearMatch(QNear n, long doc, Fts3Index idx, long langid)
    {
        for (int k = 0; k < n.Parts.Count; k++)
        {
            _ = k;
        }

        List<(int Col, int Pos)> starts = HitsOf(n.Parts[0], idx, langid)[doc];
        foreach ((int c, int p) in starts)
        {
            if (Chain(n, 1, doc, c, p, idx, langid))
            {
                return true;
            }
        }

        return false;
    }

    private readonly Dictionary<(QNear, long), HashSet<(int Part, int Col, int Pos)>> _participants = [];

    /// <summary>True when the instance of <paramref name="ph"/> is part of a chain of instances that satisfies its NEAR operators in the document.</summary>
    internal bool Participates(QNear n, QPhrase ph, long doc, (int Col, int Pos) inst, Fts3Index idx, long langid)
    {
        if (!_participants.TryGetValue((n, doc), out HashSet<(int Part, int Col, int Pos)>? set))
        {
            set = [];
            var chain = new List<(int Col, int Pos)>();
            foreach ((int c, int p) in HitsOf(n.Parts[0], idx, langid).GetValueOrDefault(doc) ?? [])
            {
                chain.Clear();
                chain.Add((c, p));
                Collect(n, 1, doc, chain, set, idx, langid);
            }

            _participants[(n, doc)] = set;
        }

        return set.Contains((n.Parts.IndexOf(ph), inst.Col, inst.Pos));
    }

    private static void Collect(QNear n, int k, long doc, List<(int Col, int Pos)> chain, HashSet<(int Part, int Col, int Pos)> into, Fts3Index idx, long langid)
    {
        if (k == n.Parts.Count)
        {
            for (int i = 0; i < chain.Count; i++)
            {
                into.Add((i, chain[i].Col, chain[i].Pos));
            }

            return;
        }

        QPhrase prev = n.Parts[k - 1];
        QPhrase cur = n.Parts[k];
        (int pc, int pp) = chain[k - 1];
        foreach ((int c2, int p2) in HitsOf(cur, idx, langid).GetValueOrDefault(doc) ?? [])
        {
            if (c2 != pc)
            {
                continue;
            }

            long gap = p2 >= pp ? p2 - (pp + prev.Terms.Count) : pp - (p2 + cur.Terms.Count);
            if (gap < 0 || gap > n.Distances[k - 1])
            {
                continue;
            }

            chain.Add((c2, p2));
            Collect(n, k + 1, doc, chain, into, idx, langid);
            chain.RemoveAt(chain.Count - 1);
        }
    }

    // Is there an instance of part k, in column c, within the distance of the instance of part k-1 starting at position prevPos?
    private static bool Chain(QNear n, int k, long doc, int c, int prevPos, Fts3Index idx, long langid)
    {
        if (k == n.Parts.Count)
        {
            return true;
        }

        QPhrase prev = n.Parts[k - 1];
        QPhrase cur = n.Parts[k];
        int dist = n.Distances[k - 1];
        foreach ((int c2, int p2) in HitsOf(cur, idx, langid)[doc])
        {
            if (c2 != c)
            {
                continue;
            }

            // tokens between the two instances, whichever comes first
            long gap = p2 >= prevPos ? p2 - (prevPos + prev.Terms.Count) : prevPos - (p2 + cur.Terms.Count);
            if (gap < 0 || gap > dist)
            {
                continue;
            }

            if (Chain(n, k + 1, doc, c, p2, idx, langid))
            {
                return true;
            }
        }

        return false;
    }
}
