using System.Text;

namespace RecursiveAI.OneLite.Differential;

/// <summary>
/// An expression kept as a tree so that the minimizer can replace any node by one of its children or by a constant.
/// A leaf is raw SQL text; an inner node is a format whose <c>{0}</c>, <c>{1}</c>... mark the children.
/// </summary>
internal sealed class Expr
{
    private const char Mark = '\u0001';
    private readonly string _text;

    internal Expr[] Kids { get; }

    private Expr(string text, Expr[] kids)
    {
        _text = text;
        Kids = kids;
    }

    internal static Expr Raw(string sql) => new(sql, []);

    internal static Expr Node(string format, params Expr[] kids)
    {
        string f = format;
        for (int i = 0; i < kids.Length; i++)
        {
            f = f.Replace("{" + i + "}", Mark + ((char)('0' + i)).ToString(), StringComparison.Ordinal);
        }

        return new Expr(f, kids);
    }

    internal static readonly Expr Null = Raw("NULL");
    internal static readonly Expr Zero = Raw("0");
    internal static readonly Expr One = Raw("1");
    internal static readonly Expr EmptyText = Raw("''");

    public override string ToString()
    {
        if (Kids.Length == 0)
        {
            return _text;
        }

        var sb = new StringBuilder(_text.Length + 16);
        for (int i = 0; i < _text.Length; i++)
        {
            if (_text[i] == Mark)
            {
                sb.Append(Kids[_text[++i] - '0']);
            }
            else
            {
                sb.Append(_text[i]);
            }
        }

        return sb.ToString();
    }

    private Expr WithKid(int i, Expr k)
    {
        Expr[] n = (Expr[])Kids.Clone();
        n[i] = k;
        return new Expr(_text, n);
    }

    /// <summary>Smaller expressions to try in its place: a child, a constant, or the same node with a simplified child.</summary>
    internal IEnumerable<Expr> Simplify()
    {
        if (Kids.Length > 0)
        {
            foreach (Expr k in Kids)
            {
                yield return k;
            }
        }

        if (_text != "NULL" && _text != "0" && _text != "1" && _text != "''")
        {
            yield return Null;
            yield return Zero;
            yield return One;
            yield return EmptyText;
        }

        for (int i = 0; i < Kids.Length; i++)
        {
            foreach (Expr s in Kids[i].Simplify())
            {
                yield return WithKid(i, s);
            }
        }
    }
}

/// <summary>A SELECT kept in parts. Clauses that hold expressions are trees; <see cref="From"/> and <see cref="Prefix"/> are raw text.</summary>
internal sealed class Select
{
    internal string Prefix = "";
    internal bool Distinct;
    internal List<Expr> Cols = [];
    internal string From = "";
    internal Expr? Where;
    internal List<Expr> Group = [];
    internal Expr? Having;
    internal List<Expr> Order = [];
    internal Expr? Limit;
    internal Expr? Offset;
    internal List<(string Op, Select Query)> Compound = [];

    internal Select Clone()
    {
        var c = (Select)MemberwiseClone();
        c.Cols = [.. Cols];
        c.Group = [.. Group];
        c.Order = [.. Order];
        c.Compound = [.. Compound];
        return c;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(Prefix).Append("SELECT ");
        if (Distinct)
        {
            sb.Append("DISTINCT ");
        }

        sb.Append(string.Join(", ", Cols));
        if (From.Length > 0)
        {
            sb.Append(" FROM ").Append(From);
        }

        if (Where is not null)
        {
            sb.Append(" WHERE ").Append(Where);
        }

        if (Group.Count > 0)
        {
            sb.Append(" GROUP BY ").Append(string.Join(", ", Group));
        }

        if (Having is not null)
        {
            sb.Append(" HAVING ").Append(Having);
        }

        foreach ((string op, Select q) in Compound)
        {
            sb.Append(' ').Append(op).Append(' ').Append(q);
        }

        if (Order.Count > 0)
        {
            sb.Append(" ORDER BY ").Append(string.Join(", ", Order));
        }

        if (Limit is not null)
        {
            sb.Append(" LIMIT ").Append(Limit);
            if (Offset is not null)
            {
                sb.Append(" OFFSET ").Append(Offset);
            }
        }

        return sb.ToString();
    }

    /// <summary>Every expression slot, in a fixed order.</summary>
    private List<Expr> Slots()
    {
        var l = new List<Expr>(Cols);
        if (Where is not null)
        {
            l.Add(Where);
        }

        l.AddRange(Group);
        if (Having is not null)
        {
            l.Add(Having);
        }

        l.AddRange(Order);
        if (Limit is not null)
        {
            l.Add(Limit);
        }

        if (Offset is not null)
        {
            l.Add(Offset);
        }

        return l;
    }

    private Select WithSlot(int index, Expr e)
    {
        Select c = Clone();
        int i = index;
        if (i < c.Cols.Count)
        {
            c.Cols[i] = e;
            return c;
        }

        i -= c.Cols.Count;
        if (c.Where is not null)
        {
            if (i == 0)
            {
                c.Where = e;
                return c;
            }

            i--;
        }

        if (i < c.Group.Count)
        {
            c.Group[i] = e;
            return c;
        }

        i -= c.Group.Count;
        if (c.Having is not null)
        {
            if (i == 0)
            {
                c.Having = e;
                return c;
            }

            i--;
        }

        if (i < c.Order.Count)
        {
            c.Order[i] = e;
            return c;
        }

        i -= c.Order.Count;
        if (c.Limit is not null)
        {
            if (i == 0)
            {
                c.Limit = e;
                return c;
            }

            i--;
        }

        c.Offset = e;
        return c;
    }

    /// <summary>Smaller queries to try: whole clauses removed first, then expressions simplified.</summary>
    internal IEnumerable<Select> Shrinks()
    {
        if (Compound.Count > 0)
        {
            for (int i = 0; i < Compound.Count; i++)
            {
                Select c = Clone();
                c.Compound.RemoveAt(i);
                yield return c;
            }
        }

        if (Offset is not null)
        {
            Select c = Clone();
            c.Offset = null;
            yield return c;
        }

        if (Limit is not null)
        {
            Select c = Clone();
            c.Limit = null;
            c.Offset = null;
            yield return c;
        }

        if (Order.Count > 0)
        {
            Select c = Clone();
            c.Order.Clear();
            yield return c;
        }

        if (Having is not null)
        {
            Select c = Clone();
            c.Having = null;
            yield return c;
        }

        if (Group.Count > 0)
        {
            Select c = Clone();
            c.Group.Clear();
            c.Having = null;
            yield return c;
        }

        if (Where is not null)
        {
            Select c = Clone();
            c.Where = null;
            yield return c;
        }

        if (Distinct)
        {
            Select c = Clone();
            c.Distinct = false;
            yield return c;
        }

        if (Prefix.Length > 0)
        {
            Select c = Clone();
            c.Prefix = "";
            yield return c;
        }

        if (Cols.Count > 1)
        {
            for (int i = 0; i < Cols.Count; i++)
            {
                Select c = Clone();
                c.Cols.RemoveAt(i);
                yield return c;
            }
        }

        List<Expr> slots = Slots();
        for (int i = 0; i < slots.Count; i++)
        {
            foreach (Expr s in slots[i].Simplify())
            {
                yield return WithSlot(i, s);
            }
        }

        foreach ((string op, Select q) in Compound)
        {
            _ = op;
            foreach (Select s in q.Shrinks())
            {
                Select c = Clone();
                int at = c.Compound.FindIndex(x => ReferenceEquals(x.Query, q));
                c.Compound[at] = (c.Compound[at].Op, s);
                yield return c;
            }
        }
    }
}
