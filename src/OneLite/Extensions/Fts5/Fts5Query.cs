using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Internal.Text;

namespace RecursiveAI.OneLite.Extensions.Fts5;

/// <summary>One token of a phrase and whether it matches as a prefix.</summary>
internal readonly record struct PTerm(byte[] Token, bool Prefix);

internal abstract class QNode
{
}

/// <summary>A phrase: consecutive tokens, optionally anchored at the start of a column and restricted to some columns.</summary>
internal sealed class QPhrase : QNode
{
    internal List<PTerm> Terms = [];
    internal bool Initial;
    internal bool[]? Cols;
    internal int Id;
}

internal sealed class QNear : QNode
{
    internal List<QPhrase> Phrases = [];
    internal int N = 10;
}

internal sealed class QBin : QNode
{
    internal char Op; // '&', '|', '-'
    internal QNode L = null!;
    internal QNode R = null!;
    internal bool Implicit; // an AND written as juxtaposition
}

/// <summary>A node known to match nothing.</summary>
internal sealed class QNone : QNode
{
    internal static readonly QNone Instance = new();
}

/// <summary>The rows a query matches, each with the instances of every phrase that took part.</summary>
internal sealed class DocMatch
{
    internal long Rowid;

    /// <summary>For each phrase id: start positions (column &lt;&lt; 32 | offset), or null.</summary>
    internal long[]?[] Inst = [];

    /// <summary>Instances kept from a NOT whose left side matched nothing (see <c>EvalRoot</c>): highlight and snippet show them, bm25 does not.</summary>
    internal long[]?[]? Leak;

    /// <summary>True once <see cref="Inst"/> was rebuilt from the row text (detail other than full).</summary>
    internal bool Resolved;
}

/// <summary>Parses an FTS5 query string (fts5 "Full-text Query Syntax").</summary>
internal sealed class QueryParser
{
    private enum T { End, Str, Bare, LParen, RParen, LBrace, RBrace, Colon, Comma, Plus, Star, Caret, Minus, And, Or, Not, Near, Bad }

    private readonly Fts5Config _cfg;
    private readonly string _src;
    private int _pos;
    private T _tok;
    private string _text = "";
    private int _tokStart;
    private int _prevStart, _prevEnd;

    internal List<QPhrase> Phrases { get; } = [];

    private const int MaxStackUse = 97;
    private int _stackUse;
    private bool _afterOperator;

    private QueryParser(Fts5Config cfg, string src)
    {
        _cfg = cfg;
        _src = src;
    }

    /// <summary>Parses <paramref name="query"/>; <paramref name="column"/> restricts everything to one column (the <c>col MATCH</c> form), or -1.</summary>
    internal static (QNode? Root, List<QPhrase> Phrases, List<QNode> Leaks) Parse(Fts5Config cfg, string query, int column)
    {
        var p = new QueryParser(cfg, query);
        p.Next();
        bool[]? cols = null;
        if (column >= 0)
        {
            cols = new bool[cfg.ColumnCount];
            cols[column] = true;
        }

        if (p._tok == T.Star)
        {
            throw Fts5Config.Err("unknown special query: " + query[(p._tokStart + 1)..].Trim());
        }

        if (p._tok == T.End)
        {
            throw p.Syntax();
        }

        QNode n = p.ParseOr(cols);
        if (p._tok != T.End)
        {
            throw p.Syntax();
        }

        if (column >= 0 && cfg.Detail == DetailMode.None)
        {
            throw Fts5Config.Err("fts5: column queries are not supported (detail=none)");
        }

        var leaks = new List<QNode>();
        QNode? root = Simplify(n, ' ', leaks);
        return (root is QNone ? null : root, p.Phrases, leaks);
    }

    /// <summary>Drops phrases that tokenized to nothing (a one-letter word under trigram): they neither match nor restrict.</summary>
    private static QBin Join(QBin b, QNode l, QNode r)
    {
        b.L = l;
        b.R = r;
        return b;
    }

    /// <summary>
    /// Simplifies the tree. The right side of a NOT whose left side was an empty phrase is collected in <paramref name="leaks"/> unless the NOT is a
    /// child of an AND: the reference still positions it (see <c>EvalRoot</c>).
    /// </summary>
    private static QNode? Simplify(QNode n, char parent, List<QNode> leaks)
    {
        switch (n)
        {
            case QPhrase ph:
                return ph.Terms.Count == 0 ? null : ph;
            case QNear nr:
                nr.Phrases.RemoveAll(x => x.Terms.Count == 0);
                return nr.Phrases.Count == 0 ? null : nr.Phrases.Count == 1 ? nr.Phrases[0] : nr;
            case QBin b:
                QNode? l = Simplify(b.L, b.Op, leaks);
                QNode? r = Simplify(b.R, b.Op, leaks);
                switch (b.Op)
                {
                    case '|':
                        return l is null or QNone ? r : r is null or QNone ? l : Join(b, l, r);
                    case '-':
                        if (l is null && r is not null and not QNone && parent != '&')
                        {
                            leaks.Add(r);
                        }

                        return l is null or QNone ? l : r is null or QNone ? l : Join(b, l, r);
                    default: // an empty phrase is dropped from an implicit AND but makes an explicit one match nothing
                        if (l is QNone || r is QNone || (!b.Implicit && (l is null || r is null)))
                        {
                            return QNone.Instance;
                        }

                        return l is null ? r : r is null ? l : Join(b, l, r);
                }

            default:
                return n;
        }
    }

    private OneLiteException Syntax()
    {
        // an opening parenthesis straight after a primary is reported at the primary before it
        string near = _tok == T.End ? string.Empty : _tok == T.LParen && _prevEnd > _prevStart && _src[_prevEnd - 1] != '*' ? _src[_prevStart.._prevEnd] : _src[_tokStart.._pos];
        return Fts5Config.Err("fts5: syntax error near \"" + near + "\"");
    }

    private bool NextCharIsParen()
    {
        int p = _pos;
        while (p < _src.Length && char.IsWhiteSpace(_src[p]))
        {
            p++;
        }

        return p < _src.Length && _src[p] == '(';
    }

    private static bool IsBare(char c) => c > 127 || char.IsAsciiLetterOrDigit(c) || c == '_' || c == 26;

    private void Next()
    {
        _prevStart = _tokStart;
        _prevEnd = _pos;
        while (_pos < _src.Length && (_src[_pos] is ' ' or '\t' or '\n' or '\r' or '\f'))
        {
            _pos++;
        }

        _tokStart = _pos;
        if (_pos >= _src.Length)
        {
            _tok = T.End;
            return;
        }

        char c = _src[_pos];
        switch (c)
        {
            case '(': _tok = T.LParen; _pos++; return;
            case ')': _tok = T.RParen; _pos++; return;
            case '{': _tok = T.LBrace; _pos++; return;
            case '}': _tok = T.RBrace; _pos++; return;
            case ':': _tok = T.Colon; _pos++; return;
            case ',': _tok = T.Comma; _pos++; return;
            case '+': _tok = T.Plus; _pos++; return;
            case '*': _tok = T.Star; _pos++; return;
            case '^': _tok = T.Caret; _pos++; return;
            case '-': _tok = T.Minus; _pos++; return;
            case '"':
            {
                var sb = new StringBuilder();
                _pos++;
                for (; ; )
                {
                    if (_pos >= _src.Length)
                    {
                        throw Fts5Config.Err("unterminated string");
                    }

                    if (_src[_pos] == '"')
                    {
                        if (_pos + 1 < _src.Length && _src[_pos + 1] == '"')
                        {
                            sb.Append('"');
                            _pos += 2;
                            continue;
                        }

                        _pos++;
                        break;
                    }

                    sb.Append(_src[_pos++]);
                }

                _tok = T.Str;
                _text = sb.ToString();
                return;
            }

            default:
                if (IsBare(c))
                {
                    int st = _pos;
                    while (_pos < _src.Length && IsBare(_src[_pos]))
                    {
                        _pos++;
                    }

                    _text = _src[st.._pos];
                    _tok = _text switch { "AND" => T.And, "OR" => T.Or, "NOT" => T.Not, "NEAR" => T.Near, _ => T.Bare };
                    if (_tok == T.Near && !NextCharIsParen())
                    {
                        _tok = T.Bare;
                    }

                    return;
                }

                _pos++;
                _tok = T.Bad;
                throw Fts5Config.Err("fts5: syntax error near \"" + _src[_tokStart.._pos] + "\"");
        }
    }

    private QNode ParseOr(bool[]? cols)
    {
        QNode l = ParseAnd(cols);
        while (_tok == T.Or)
        {
            Next();
            _afterOperator = true;
            l = new QBin { Op = '|', L = l, R = ParseAnd(cols) };
        }

        return l;
    }

    private QNode ParseAnd(bool[]? cols)
    {
        QNode l = ParseNot(cols);
        while (_tok == T.And)
        {
            Next();
            _afterOperator = true;
            l = new QBin { Op = '&', L = l, R = ParseNot(cols) };
        }

        return l;
    }

    private QNode ParseNot(bool[]? cols)
    {
        QNode l = ParseImplicit(cols);
        while (_tok == T.Not)
        {
            Next();
            _afterOperator = true;
            l = new QBin { Op = '-', L = l, R = ParseImplicit(cols) };
        }

        return l;
    }

    private bool StartsPrimary() => _tok is T.Str or T.Bare or T.Near or T.Caret or T.LBrace or T.Minus;

    private QNode ParseImplicit(bool[]? cols)
    {
        bool paren;
        QNode l = ParsePrimary(cols, out paren);
        while (!paren && StartsPrimary())
        {
            QNode r = ParsePrimary(cols, out paren);
            if (paren)
            {
                throw Syntax();
            }

            l = new QBin { Op = '&', L = l, R = r, Implicit = true };
        }

        return l;
    }

    private bool[]? ColFilter(bool[]? outer)
    {
        // [-] colspec ':' ; the caller has seen a Minus, LBrace, or a name followed by a colon
        bool neg = false;
        if (_tok == T.Minus)
        {
            neg = true;
            Next();
        }

        var names = new List<string>();
        if (_tok == T.LBrace)
        {
            Next();
            while (_tok is T.Str or T.Bare or T.And or T.Or or T.Not or T.Near)
            {
                names.Add(_tok is T.Str or T.Bare ? _text : _src[_tokStart.._pos]);
                Next();
            }

            foreach (string n in names)
            {
                if (!_cfg.Columns.Exists(c => string.Equals(c, n, StringComparison.OrdinalIgnoreCase)))
                {
                    throw Fts5Config.Err("no such column: " + n); // reported before a missing brace
                }
            }

            if (_tok != T.RBrace || names.Count == 0)
            {
                throw Syntax();
            }

            Next();
        }
        else if (_tok is T.Str or T.Bare)
        {
            names.Add(_text);
            Next();
        }
        else
        {
            throw Syntax();
        }

        if (_tok != T.Colon)
        {
            throw Syntax();
        }

        Next();
        var sel = new bool[_cfg.ColumnCount];
        foreach (string n in names)
        {
            int ix = _cfg.Columns.FindIndex(c => string.Equals(c, n, StringComparison.OrdinalIgnoreCase));
            if (ix < 0)
            {
                throw Fts5Config.Err("no such column: " + n);
            }

            sel[ix] = true;
        }

        if (neg)
        {
            for (int i = 0; i < sel.Length; i++)
            {
                sel[i] = !sel[i];
            }
        }

        if (outer is not null)
        {
            for (int i = 0; i < sel.Length; i++)
            {
                sel[i] &= outer[i];
            }
        }

        return sel;
    }

    private bool LooksLikeColFilter()
    {
        if (_tok is T.LBrace or T.Minus)
        {
            return true;
        }

        if (_tok is T.Str or T.Bare)
        {
            int p = _pos;
            while (p < _src.Length && char.IsWhiteSpace(_src[p]))
            {
                p++;
            }

            return p < _src.Length && _src[p] == ':';
        }

        return false;
    }

    private QNode ParsePrimary(bool[]? cols, out bool paren)
    {
        bool filtered = LooksLikeColFilter();
        QNode n = ParsePrimaryCore(cols, out paren);
        if (filtered && _cfg.Detail == DetailMode.None)
        {
            // raised once the filtered primary parsed, so an error inside that primary comes first
            throw Fts5Config.Err("fts5: column queries are not supported (detail=none)");
        }

        return n;
    }

    private QNode ParsePrimaryCore(bool[]? cols, out bool paren)
    {
        paren = false;
        bool afterOperator = _afterOperator;
        _afterOperator = false;
        bool filtered = false;
        if (LooksLikeColFilter())
        {
            filtered = true;
            cols = ColFilter(cols);
        }

        if (_tok == T.LParen)
        {
            // The reference parser has a stack of 100 entries: a parenthesis costs one, and two more when a left operand and its operator
            // (or a column filter) sit below it. Deeper queries fail there with "parser stack overflow"; ours must not recurse without bound.
            int cost = filtered || afterOperator ? 3 : 1;
            _stackUse += cost;
            try
            {
                if (_stackUse > MaxStackUse)
                {
                    throw Fts5Config.Err("fts5: parser stack overflow");
                }

                Next();
                QNode e = ParseOr(cols);
                if (_tok != T.RParen)
                {
                    throw Syntax();
                }

                Next();
                paren = true;
                return e;
            }
            finally
            {
                _stackUse -= cost;
            }
        }

        if (_tok == T.Near)
        {
            return ParseNear(cols);
        }

        bool initial = false;
        if (_tok == T.Caret)
        {
            initial = true;
            Next();
        }

        if (_tok is not (T.Str or T.Bare))
        {
            throw Syntax();
        }

        QPhrase p = ParsePhrase(cols);
        if (initial && _cfg.Detail != DetailMode.Full && p.Terms.Count > 0)
        {
            throw Fts5Config.Err("fts5: phrase queries are not supported (detail!=full)");
        }

        p.Initial = initial;
        return p;
    }

    private QPhrase ParsePhrase(bool[]? cols, bool inNear = false)
    {
        var p = new QPhrase { Cols = cols, Id = Phrases.Count };
        Phrases.Add(p);
        for (; ; )
        {
            string s = _text;
            Next();
            bool star = false;
            if (_tok == T.Star)
            {
                star = true;
                Next();
            }

            AddTokens(p, s, star);
            if (_tok == T.Plus)
            {
                Next();
                if (_tok is not (T.Str or T.Bare))
                {
                    throw Syntax();
                }

                continue;
            }

            break;
        }

        if (!inNear && _cfg.Detail != DetailMode.Full && p.Terms.Count > 1)
        {
            throw Fts5Config.Err("fts5: phrase queries are not supported (detail!=full)");
        }

        return p;
    }

    private sealed class Collect(List<PTerm> into, bool star) : TokenSink
    {
        private PTerm? _last;

        internal override void Token(ReadOnlySpan<byte> token, int start, int end)
        {
            if (_last is { } l)
            {
                into.Add(l);
            }

            _last = new PTerm(token.ToArray(), false);
        }

        internal void Finish()
        {
            if (_last is { } l)
            {
                into.Add(star ? l with { Prefix = true } : l);
            }
        }
    }

    private void AddTokens(QPhrase p, string s, bool star)
    {
        var c = new Collect(p.Terms, star);
        byte[] utf8 = Encoding.UTF8.GetBytes(s);
        _cfg.Tokenizer.Tokenize(utf8, star ? TokenizeReason.Query | TokenizeReason.Prefix : TokenizeReason.Query, c);
        c.Finish();
    }

    private QNear ParseNear(bool[]? cols)
    {
        Next(); // NEAR
        if (_tok != T.LParen)
        {
            throw Syntax();
        }

        Next();
        var n = new QNear();
        while (_tok is T.Str or T.Bare or T.Near)
        {
            if (_tok == T.Near)
            {
                throw Syntax();
            }

            n.Phrases.Add(ParsePhrase(cols, true));
        }

        if (n.Phrases.Count == 0)
        {
            throw Syntax();
        }

        if (_tok == T.Comma)
        {
            Next();
            if (_tok != T.Bare || !int.TryParse(_text, out int v))
            {
                throw Fts5Config.Err("expected integer, got \"" + (_tok == T.End ? string.Empty : _src[_tokStart.._pos]) + "\"");
            }

            n.N = v;
            Next();
        }

        if (_tok != T.RParen)
        {
            throw Syntax();
        }

        Next();
        if (_cfg.Detail != DetailMode.Full)
        {
            if (n.Phrases.Count(x => x.Terms.Count > 0) > 1)
            {
                throw Fts5Config.Err("fts5: NEAR queries are not supported (detail!=full)");
            }

            if (n.Phrases.Exists(x => x.Terms.Count > 1))
            {
                throw Fts5Config.Err("fts5: phrase queries are not supported (detail!=full)");
            }
        }

        return n;
    }
}

/// <summary>Evaluates a parsed query against the index.</summary>
internal sealed class QueryEval
{
    private readonly Fts5Config _cfg;
    private readonly Fts5Index _idx;
    private readonly Structure _st;
    private readonly int _nPhrases;
    private readonly Dictionary<(byte[], bool), List<Posting>> _cache = new(new KeyCmp());

    private sealed class KeyCmp : IEqualityComparer<(byte[], bool)>
    {
        public bool Equals((byte[], bool) x, (byte[], bool) y) => x.Item2 == y.Item2 && x.Item1.AsSpan().SequenceEqual(y.Item1);

        public int GetHashCode((byte[], bool) o)
        {
            var h = new HashCode();
            h.AddBytes(o.Item1);
            h.Add(o.Item2);
            return h.ToHashCode();
        }
    }

    internal QueryEval(Fts5Config cfg, Fts5Index idx, Structure st, int nPhrases)
    {
        _cfg = cfg;
        _idx = idx;
        _st = st;
        _nPhrases = nPhrases;
    }

    private List<Posting> Postings(PTerm t)
    {
        byte[] key;
        bool scan = false;
        if (t.Prefix)
        {
            int chars = 0;
            foreach (byte b in t.Token)
            {
                if ((b & 0xC0) != 0x80)
                {
                    chars++;
                }
            }

            int k = _cfg.Prefixes.IndexOf(chars);
            if (k >= 0)
            {
                key = [(byte)('1' + k), .. t.Token];
            }
            else
            {
                key = [(byte)'0', .. t.Token];
                scan = true;
            }
        }
        else
        {
            key = [(byte)'0', .. t.Token];
        }

        if (!_cache.TryGetValue((key, scan), out List<Posting>? l))
        {
            _cache[(key, scan)] = l = _idx.Lookup(_st, key, scan);
        }

        return l;
    }

    // Observed: when the left side of a NOT matches no row at all, the reference leaves the right side positioned on its first row, and the
    // instances of that row's phrases still show in the auxiliary functions (highlight, snippet, bm25) if the row matches some other way.
    private readonly List<DocMatch> _leaks = [];

    /// <summary>Evaluates the whole query tree, with the NOT quirk above applied to the result.</summary>
    internal List<DocMatch> EvalRoot(QNode n, List<QNode>? emptyLeft = null)
    {
        _leaks.Clear();
        List<DocMatch> res = Eval(n);
        foreach (QNode right in emptyLeft ?? [])
        {
            List<DocMatch> rr = Eval(right);
            if (rr.Count > 0)
            {
                _leaks.Add(rr[0]);
            }
        }

        foreach (DocMatch leak in _leaks)
        {
            foreach (DocMatch d in res)
            {
                if (d.Rowid != leak.Rowid)
                {
                    continue;
                }

                for (int i = 0; i < d.Inst.Length && i < leak.Inst.Length; i++)
                {
                    if (d.Inst[i] is null && leak.Inst[i] is { } li)
                    {
                        (d.Leak ??= new long[]?[d.Inst.Length])[i] = li;
                    }
                }
            }
        }

        return res;
    }

    /// <summary>The rows in which the terms of a node meet, whatever their positions (a phrase that never occurs can still have some).</summary>
    private HashSet<long> Candidates(QNode n)
    {
        switch (n)
        {
            case QPhrase p:
            {
                HashSet<long>? acc = null;
                foreach (PTerm t in p.Terms)
                {
                    var rows = new HashSet<long>();
                    foreach (Posting po in Postings(t))
                    {
                        rows.Add(po.Rowid);
                    }

                    if (acc is null)
                    {
                        acc = rows;
                    }
                    else
                    {
                        acc.IntersectWith(rows);
                    }
                }

                return acc ?? [];
            }

            case QNear near:
            {
                HashSet<long>? acc = null;
                foreach (QPhrase p in near.Phrases)
                {
                    HashSet<long> rows = Candidates(p);
                    if (acc is null)
                    {
                        acc = rows;
                    }
                    else
                    {
                        acc.IntersectWith(rows);
                    }
                }

                return acc ?? [];
            }

            case QBin b:
            {
                HashSet<long> l = Candidates(b.L);
                if (b.Op == '-')
                {
                    return l;
                }

                HashSet<long> r = Candidates(b.R);
                if (b.Op == '&')
                {
                    l.IntersectWith(r);
                }
                else
                {
                    l.UnionWith(r);
                }

                return l;
            }

            default:
                return [];
        }
    }

    internal List<DocMatch> Eval(QNode n, bool parentIsAnd = false)
    {
        switch (n)
        {
            case QPhrase p:
                return EvalPhrase(p);
            case QNear near:
                return EvalNear(near);
            case QBin b:
            {
                List<DocMatch> l = Eval(b.L, b.Op == '&');
                List<DocMatch> r = Eval(b.R, b.Op == '&');
                // observed: as a child of an AND the reference never reaches the right side of a NOT whose left side is empty (below an
                // OR that is itself below an AND it does), and when the left side's terms do share rows (a phrase that never occurs) the
                // right side does not stay on its first row either
                if (b.Op == '-' && l.Count == 0 && r.Count > 0 && !parentIsAnd && Candidates(b.L).Count == 0)
                {
                    _leaks.Add(r[0]);
                }

                return b.Op switch { '&' => And(l, r), '|' => Or(l, r), _ => Not(l, r) };
            }

            default:
                return [];
        }
    }

    private DocMatch Merge(DocMatch a, DocMatch b)
    {
        var m = new DocMatch { Rowid = a.Rowid, Inst = new long[]?[_nPhrases] };
        for (int i = 0; i < _nPhrases; i++)
        {
            long[]? x = i < a.Inst.Length ? a.Inst[i] : null;
            long[]? y = i < b.Inst.Length ? b.Inst[i] : null;
            m.Inst[i] = x ?? y;
        }

        return m;
    }

    private List<DocMatch> And(List<DocMatch> l, List<DocMatch> r)
    {
        var o = new List<DocMatch>();
        int i = 0;
        int j = 0;
        while (i < l.Count && j < r.Count)
        {
            if (l[i].Rowid < r[j].Rowid)
            {
                i++;
            }
            else if (l[i].Rowid > r[j].Rowid)
            {
                j++;
            }
            else
            {
                o.Add(Merge(l[i], r[j]));
                i++;
                j++;
            }
        }

        return o;
    }

    private List<DocMatch> Or(List<DocMatch> l, List<DocMatch> r)
    {
        var o = new List<DocMatch>();
        int i = 0;
        int j = 0;
        while (i < l.Count || j < r.Count)
        {
            if (j >= r.Count || (i < l.Count && l[i].Rowid < r[j].Rowid))
            {
                o.Add(l[i++]);
            }
            else if (i >= l.Count || r[j].Rowid < l[i].Rowid)
            {
                o.Add(r[j++]);
            }
            else
            {
                o.Add(Merge(l[i++], r[j++]));
            }
        }

        return o;
    }

    private static List<DocMatch> Not(List<DocMatch> l, List<DocMatch> r)
    {
        var o = new List<DocMatch>();
        int j = 0;
        foreach (DocMatch d in l)
        {
            while (j < r.Count && r[j].Rowid < d.Rowid)
            {
                j++;
            }

            if (j < r.Count && r[j].Rowid == d.Rowid)
            {
                continue;
            }

            o.Add(d);
        }

        return o;
    }

    private static bool Has(long[] sorted, long v) => Array.BinarySearch(sorted, v) >= 0;

    private List<DocMatch> EvalPhrase(QPhrase p)
    {
        var res = new List<DocMatch>();
        if (p.Terms.Count == 0)
        {
            return res;
        }

        var lists = new List<Posting>[p.Terms.Count];
        for (int i = 0; i < lists.Length; i++)
        {
            lists[i] = Postings(p.Terms[i]);
            if (lists[i].Count == 0)
            {
                return res;
            }
        }

        var ptr = new int[lists.Length];
        for (; ; )
        {
            // advance every list to the largest current rowid
            long target = long.MinValue;
            bool done = false;
            for (int i = 0; i < lists.Length; i++)
            {
                if (ptr[i] >= lists[i].Count)
                {
                    done = true;
                    break;
                }

                target = Math.Max(target, lists[i][ptr[i]].Rowid);
            }

            if (done)
            {
                break;
            }

            bool all = true;
            for (int i = 0; i < lists.Length; i++)
            {
                while (ptr[i] < lists[i].Count && lists[i][ptr[i]].Rowid < target)
                {
                    ptr[i]++;
                }

                if (ptr[i] >= lists[i].Count)
                {
                    return res;
                }

                if (lists[i][ptr[i]].Rowid != target)
                {
                    all = false;
                }
            }

            if (!all)
            {
                continue;
            }

            var starts = new List<long>();
            if (_cfg.Detail == DetailMode.None)
            {
                // no positions are kept: a lone term matches wherever it is listed
                var dm = new DocMatch { Rowid = target, Inst = new long[]?[_nPhrases] };
                dm.Inst[p.Id] = []; // marks the phrase as taking part; Fts5Aux finds its instances in the row text
                res.Add(dm);
                for (int i = 0; i < lists.Length; i++)
                {
                    ptr[i]++;
                }

                continue;
            }

            foreach (long p0 in lists[0][ptr[0]].Pos)
            {
                int col = (int)(p0 >> 32);
                if (p.Cols is not null && (col >= p.Cols.Length || !p.Cols[col]))
                {
                    continue;
                }

                if (p.Initial && (p0 & 0xFFFFFFFFL) != 0)
                {
                    continue;
                }

                bool ok = true;
                for (int i = 1; i < lists.Length && ok; i++)
                {
                    ok = Has(lists[i][ptr[i]].Pos, p0 + i);
                }

                if (ok)
                {
                    starts.Add(p0);
                }
            }

            if (starts.Count > 0)
            {
                var d = new DocMatch { Rowid = target, Inst = new long[]?[_nPhrases] };
                d.Inst[p.Id] = [.. starts];
                res.Add(d);
            }

            for (int i = 0; i < lists.Length; i++)
            {
                ptr[i]++;
            }
        }

        return res;
    }

    // Can one instance of every phrase other than <paramref name="fixedQ"/> be added so that all lie within the NEAR distance?
    private static bool NearFeasible(QNear n, List<long[]> per, int fixedQ, int k, long maxStart, long minEnd)
    {
        if (k == per.Count)
        {
            return true;
        }

        if (k == fixedQ)
        {
            return NearFeasible(n, per, fixedQ, k + 1, maxStart, minEnd);
        }

        foreach (long y in per[k])
        {
            long st = y & 0xFFFFFFFFL;
            long ms = Math.Max(maxStart, st);
            long me = Math.Min(minEnd, st + n.Phrases[k].Terms.Count - 1);
            if (ms - me - 1 <= n.N && NearFeasible(n, per, fixedQ, k + 1, ms, me))
            {
                return true;
            }
        }

        return false;
    }

    private List<DocMatch> EvalNear(QNear n)
    {
        if (n.Phrases.Count == 1)
        {
            return EvalPhrase(n.Phrases[0]);
        }

        List<DocMatch>? acc = null;
        foreach (QPhrase p in n.Phrases)
        {
            List<DocMatch> r = EvalPhrase(p);
            acc = acc is null ? r : And(acc, r);
        }

        var res = new List<DocMatch>();
        foreach (DocMatch d in acc!)
        {
            var keep = new List<long>[_nPhrases];
            bool any = false;
            // group the instances of each phrase by column
            var cols = new SortedSet<int>();
            foreach (QPhrase p in n.Phrases)
            {
                foreach (long s in d.Inst[p.Id] ?? [])
                {
                    cols.Add((int)(s >> 32));
                }
            }

            foreach (int col in cols)
            {
                var per = new List<long[]>();
                bool missing = false;
                foreach (QPhrase p in n.Phrases)
                {
                    long[] s = (d.Inst[p.Id] ?? []).Where(x => (int)(x >> 32) == col).ToArray();
                    if (s.Length == 0)
                    {
                        missing = true;
                        break;
                    }

                    per.Add(s);
                }

                if (missing)
                {
                    continue;
                }

                // An instance counts when the other phrases have instances that, together with it, lie within the distance: the tokens between
                // the latest start and the earliest end number at most N (observed: "jump" at 1 and 3 with "happi" at 5 keeps all three for
                // NEAR(.., 6), and "ba" at 2 keeps every "baba" up to 7 tokens away).
                for (int q = 0; q < per.Count; q++)
                {
                    foreach (long x in per[q])
                    {
                        long st = x & 0xFFFFFFFFL;
                        if (NearFeasible(n, per, q, 0, st, st + n.Phrases[q].Terms.Count - 1))
                        {
                            any = true;
                            (keep[n.Phrases[q].Id] ??= []).Add(x);
                        }
                    }
                }
            }

            if (any)
            {
                var m = new DocMatch { Rowid = d.Rowid, Inst = new long[]?[_nPhrases] };
                foreach (QPhrase p in n.Phrases)
                {
                    if (keep[p.Id] is { } k)
                    {
                        m.Inst[p.Id] = [.. k.Distinct().OrderBy(x => x)];
                    }
                }

                res.Add(m);
            }
        }

        return res;
    }
}
