using System.Globalization;
using System.Text;

namespace RecursiveAI.OneLite.QueryDiff;

/// <summary>
/// A seeded generator of relational queries over the tables of a database: joins of every kind, subqueries in FROM, WHERE and the select
/// list (correlated or not), common table expressions (recursive too), compound selects, DISTINCT, grouping and aggregates. Every query is
/// bounded (equality joins, small tables for anything else) and either fully ordered (ORDER BY every output column, so tied rows are
/// identical) or compared as a multiset.
/// </summary>
internal sealed class RelGen
{
    private readonly Random _r;
    private readonly List<TableMeta> _all;
    private int _alias;

    internal RelGen(int seed, List<TableMeta> tables)
    {
        _r = new Random(seed);
        _all = [.. tables.Where(t => t.Cols.Count > 0)];
    }

    private sealed class Item
    {
        internal string Alias = "";
        internal TableMeta Table = null!;
        internal bool Synthetic;
    }

    private sealed class Sel
    {
        internal string Sql = "";
        internal int Arity;
        internal List<ColMeta> Cols = [];
    }

    private bool P(double p) => _r.NextDouble() < p;

    private T Pick<T>(IReadOnlyList<T> l) => l[_r.Next(l.Count)];

    private string PickOf(params string[] l) => l[_r.Next(l.Length)];

    private string NewAlias() => "r" + (++_alias).ToString(CultureInfo.InvariantCulture);

    private static string Q(string n) => Introspect.Q(n);

    private List<TableMeta> Pool(long maxRows) => _all.Where(t => t.Rows <= maxRows).ToList();

    private static bool Numeric(ColMeta c)
    {
        string t = c.Type.ToUpperInvariant();
        return t.Contains("INT", StringComparison.Ordinal) || t.Contains("REAL", StringComparison.Ordinal) || t.Contains("FLOA", StringComparison.Ordinal)
            || t.Contains("DOUB", StringComparison.Ordinal) || t.Contains("NUM", StringComparison.Ordinal) || t.Contains("DEC", StringComparison.Ordinal);
    }

    private string Lit(ColMeta c)
    {
        if (c.Samples.Count > 0 && P(0.7))
        {
            return Pick(c.Samples);
        }

        return Numeric(c) ? _r.Next(-2, 50).ToString(CultureInfo.InvariantCulture) : PickOf("'a'", "'abc'", "''", "'1'", "NULL", "'Z'");
    }

    private static string ColRef(Item it, ColMeta c) => it.Alias + "." + Q(c.Name);

    private (Item It, ColMeta C) AnyCol(List<Item> items)
    {
        Item it = Pick(items);
        return (it, Pick(it.Table.Cols));
    }

    // ------------------------------------------------------------------ FROM

    private Item TableItem(long maxRows)
    {
        List<TableMeta> pool = Pool(maxRows);
        if (pool.Count == 0)
        {
            pool = _all;
        }

        return new Item { Alias = NewAlias(), Table = Pick(pool) };
    }

    private static TableMeta Synth(string name, List<ColMeta> cols) => new() { Name = name, Cols = cols, WithoutRowid = true, Rows = 100 };

    /// <summary>A FROM clause: the text, the items it brings into scope and whether the result may be large.</summary>
    private string From(List<Item> items, int n, int depth, List<string> ctes, out bool grew)
    {
        grew = false;
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            Item it;
            string src;
            if (P(0.12) && depth < 2)
            {
                // a subquery in FROM, or a CTE read by name
                Sel s = Simple(depth + 1, null, true);
                string alias = NewAlias();
                var cols = new List<ColMeta>();
                for (int k = 0; k < s.Cols.Count; k++)
                {
                    cols.Add(new ColMeta { Name = "k" + k.ToString(CultureInfo.InvariantCulture), Type = s.Cols[k].Type, Samples = s.Cols[k].Samples });
                }

                it = new Item { Alias = alias, Table = Synth(alias, cols), Synthetic = true };
                string names = string.Join(", ", cols.Select(c => c.Name));
                if (P(0.5))
                {
                    string cte = "c" + alias;
                    ctes.Add(cte + "(" + names + ") AS (" + s.Sql + ")");
                    src = cte + " AS " + alias;
                }
                else
                {
                    src = "(" + s.Sql + ") AS " + alias;
                }
            }
            else
            {
                it = TableItem(i == 0 ? 3000 : 1500);
                src = Q(it.Table.Name) + " AS " + it.Alias;
            }

            if (i == 0)
            {
                sb.Append(src);
                items.Add(it);
                continue;
            }

            // equality on a pair of columns of similar type keeps joins linear and gives the planner an index to find
            (Item li, ColMeta lc) = AnyCol(items);
            ColMeta rc = it.Table.Cols.Where(c => Numeric(c) == Numeric(lc)).DefaultIfEmpty(it.Table.Cols[0]).First();
            if (P(0.5))
            {
                rc = Pick(it.Table.Cols.Where(c => c.Indexed || c.Pk).DefaultIfEmpty(rc).ToList());
            }

            string on = ColRef(li, lc) + " = " + ColRef(it, rc);
            if (P(0.2))
            {
                on += " AND " + ColRef(it, Pick(it.Table.Cols)) + " " + PickOf("IS NOT NULL", "IS NULL", "<> " + Lit(Pick(it.Table.Cols)));
            }

            string kind = PickOf("JOIN", "JOIN", "JOIN", "LEFT JOIN", "LEFT JOIN", "INNER JOIN", "CROSS JOIN", "RIGHT JOIN", "FULL JOIN", "LEFT OUTER JOIN");
            if (kind == "CROSS JOIN" && it.Table.Rows > 40)
            {
                kind = "JOIN";
            }

            if (P(0.12))
            {
                var common = items.SelectMany(x => x.Table.Cols.Select(c => (X: x, C: c))).Where(p => it.Table.Cols.Any(c => string.Equals(c.Name, p.C.Name, StringComparison.OrdinalIgnoreCase))).ToList();
                if (common.Count > 0)
                {
                    sb.Append(' ').Append(kind).Append(' ').Append(src).Append(" USING (").Append(Q(common[0].C.Name)).Append(')');
                    items.Add(it);
                    continue;
                }
            }

            sb.Append(' ').Append(kind).Append(' ').Append(src).Append(" ON ").Append(on);
            items.Add(it);
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------ expressions

    private string Cond(List<Item> items, List<Item>? outer, int depth)
    {
        int n = _r.Next(1, 3);
        var parts = new List<string>();
        for (int i = 0; i < n; i++)
        {
            parts.Add(Term(items, outer, depth));
        }

        return string.Join(P(0.8) ? " AND " : " OR ", parts);
    }

    private string Term(List<Item> items, List<Item>? outer, int depth)
    {
        (Item it, ColMeta c) = AnyCol(items);
        string col = ColRef(it, c);
        switch (_r.Next(14))
        {
            case 0:
            case 1: return col + " " + PickOf("=", "<", ">", "<=", ">=", "<>") + " " + Lit(c);
            case 2: return col + " IS " + PickOf("NULL", "NOT NULL");
            case 3: return col + " IN (" + Lit(c) + ", " + Lit(c) + ", " + Lit(c) + ")";
            case 4: return col + " BETWEEN " + Lit(c) + " AND " + Lit(c);
            case 5:
            case 6:
                if (depth < 2)
                {
                    return col + " " + PickOf("IN", "NOT IN") + " (" + Subselect(items, depth + 1, 1).Sql + ")";
                }

                return col + " IS NOT NULL";
            case 7:
            case 8:
                if (depth < 2)
                {
                    return PickOf("EXISTS", "NOT EXISTS") + " (" + Correlated(items, depth + 1) + ")";
                }

                return col + " IS NULL";
            case 9:
                if (depth < 2)
                {
                    return col + " " + PickOf("=", "<", ">=") + " (" + Scalar(items, depth + 1) + ")";
                }

                return col + " = " + Lit(c);
            case 10:
            {
                (Item i2, ColMeta c2) = AnyCol(items);
                return col + " " + PickOf("=", "<", "<>", "IS") + " " + ColRef(i2, c2);
            }

            case 11: return "(" + col + ", " + ColRef(it, Pick(it.Table.Cols)) + ") " + PickOf("=", "<", ">=", "IS NOT") + " (" + Lit(c) + ", " + Lit(c) + ")";
            case 12: return "NOT (" + col + " = " + Lit(c) + ")";
            default: return col + " " + PickOf("LIKE 'a%'", "GLOB '*1*'", "= 1", "> 0");
        }
    }

    private Sel Subselect(List<Item> outer, int depth, int arity)
    {
        var items = new List<Item>();
        var ctes = new List<string>();
        string from = From(items, 1, depth, ctes, out _);
        var cols = new List<string>();
        var meta = new List<ColMeta>();
        for (int i = 0; i < arity; i++)
        {
            (Item it, ColMeta c) = AnyCol(items);
            cols.Add(ColRef(it, c));
            meta.Add(c);
        }

        string where = P(0.5) ? " WHERE " + Cond(items, outer, depth) : "";
        string prefix = ctes.Count > 0 ? "WITH " + string.Join(", ", ctes) + " " : "";
        return new Sel { Sql = prefix + "SELECT " + (P(0.2) ? "DISTINCT " : "") + string.Join(", ", cols) + " FROM " + from + where, Arity = arity, Cols = meta };
    }

    private string Correlated(List<Item> outer, int depth)
    {
        var items = new List<Item>();
        var ctes = new List<string>();
        string from = From(items, 1, depth, ctes, out _);
        (Item oi, ColMeta oc) = AnyCol(outer);
        (Item ii, ColMeta ic) = AnyCol(items);
        string corr = ColRef(ii, ic) + " " + PickOf("=", "=", "<", ">") + " " + ColRef(oi, oc);
        string prefix = ctes.Count > 0 ? "WITH " + string.Join(", ", ctes) + " " : "";
        return prefix + "SELECT 1 FROM " + from + " WHERE " + corr + (P(0.3) ? " AND " + Term(items, null, 9) : "");
    }

    private string Scalar(List<Item> outer, int depth)
    {
        var items = new List<Item>();
        var ctes = new List<string>();
        string from = From(items, 1, depth, ctes, out _);
        (Item ii, ColMeta ic) = AnyCol(items);
        string agg = PickOf("max", "min", "count", "sum", "total") + "(" + ColRef(ii, ic) + ")";
        string where = "";
        if (P(0.6))
        {
            (Item oi, ColMeta oc) = AnyCol(outer);
            (Item i2, ColMeta c2) = AnyCol(items);
            where = " WHERE " + ColRef(i2, c2) + " = " + ColRef(oi, oc);
        }

        string prefix = ctes.Count > 0 ? "WITH " + string.Join(", ", ctes) + " " : "";
        return prefix + "SELECT " + agg + " FROM " + from + where;
    }

    // ------------------------------------------------------------------ whole selects

    private string Agg(List<Item> items)
    {
        (Item it, ColMeta c) = AnyCol(items);
        string col = ColRef(it, c);
        string distinct = P(0.2) ? "DISTINCT " : "";
        return _r.Next(9) switch
        {
            0 => "count(*)",
            1 => "count(" + distinct + col + ")",
            2 => "min(" + col + ")",
            3 => "max(" + col + ")",
            4 => Numeric(c) ? "sum(" + distinct + col + ")" : "count(" + col + ")",
            5 => "group_concat(" + col + ", '|' ORDER BY " + col + ")",
            6 => "count(*) FILTER (WHERE " + col + " IS NOT NULL)",
            7 => Numeric(c) ? "total(" + col + ")" : "min(" + col + ")",
            _ => "max(" + col + ") - min(" + col + ")",
        };
    }

    /// <summary>A SELECT over one to three FROM items. <paramref name="forSub"/>: no aggregates or ORDER BY, columns only (a source for FROM).</summary>
    private Sel Simple(int depth, List<Item>? outer, bool forSub)
    {
        var items = new List<Item>();
        var ctes = new List<string>();
        int n = P(0.45) ? 1 : P(0.7) ? 2 : 3;
        string from = From(items, n, depth, ctes, out _);
        string where = P(0.65) ? " WHERE " + Cond(items, outer, depth) : "";
        var cols = new List<string>();
        var meta = new List<ColMeta>();
        int arity = _r.Next(1, 4);
        string group = "", having = "";
        string prefix = ctes.Count > 0 ? "WITH " + string.Join(", ", ctes) + " " : "";
        bool aggregate = !forSub && P(0.4);
        if (aggregate)
        {
            var keys = new List<string>();
            if (P(0.7))
            {
                for (int k = 0; k < _r.Next(1, 3); k++)
                {
                    (Item it, ColMeta c) = AnyCol(items);
                    keys.Add(ColRef(it, c));
                    meta.Add(c);
                }

                group = " GROUP BY " + string.Join(", ", keys);
            }

            cols.AddRange(keys);
            for (int k = 0; k < arity; k++)
            {
                cols.Add(Agg(items));
                meta.Add(new ColMeta { Name = "agg", Type = "NUMERIC" });
            }

            if (keys.Count > 0 && P(0.3))
            {
                having = " HAVING count(*) " + PickOf(">", ">=", "<") + " " + _r.Next(0, 4);
            }
        }
        else
        {
            for (int k = 0; k < arity; k++)
            {
                (Item it, ColMeta c) = AnyCol(items);
                if (!forSub && P(0.2))
                {
                    cols.Add(ColRef(it, c) + " " + PickOf("+ 1", "|| 'x'", "* 2", "IS NULL"));
                }
                else if (!forSub && depth < 2 && P(0.1))
                {
                    cols.Add("(" + Scalar(items, depth + 1) + ")");
                }
                else
                {
                    cols.Add(ColRef(it, c));
                }

                if (forSub)
                {
                    cols[^1] += " AS k" + k.ToString(CultureInfo.InvariantCulture);
                }

                meta.Add(c);
            }
        }

        string sql = prefix + "SELECT " + (!aggregate && P(0.15) ? "DISTINCT " : "") + string.Join(", ", cols) + " FROM " + from + where + group + having;
        return new Sel { Sql = sql, Arity = cols.Count, Cols = meta };
    }

    private static string OrderAll(int n) => " ORDER BY " + string.Join(", ", Enumerable.Range(1, n));

    internal (string Sql, bool Ordered, bool NeedsTable) Next()
    {
        if (_all.Count == 0)
        {
            return ("SELECT 1", true, false);
        }

        _alias = 0;
        switch (_r.Next(10))
        {
            case 0:
            {
                // a recursive CTE joined with a table
                TableMeta t = Pick(Pool(500).DefaultIfEmpty(_all[0]).ToList());
                ColMeta c = Pick(t.Cols);
                string sql = $"WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < {_r.Next(1, 60)}) SELECT n.i, count({Q(t.Name)}.{Q(c.Name)}) FROM n LEFT JOIN {Q(t.Name)} ON {Q(t.Name)}.{Q(c.Name)} = n.i GROUP BY n.i" + (P(0.5) ? " HAVING count(*) > 0" : "") + " ORDER BY 1";
                return (sql, true, true);
            }

            case 1:
            case 2:
            {
                // compound select of two to three simple selects with the same number of columns
                int arity = _r.Next(1, 3);
                var parts = new List<string>();
                int terms = _r.Next(2, 4);
                var ops = new List<string>();
                for (int i = 0; i < terms; i++)
                {
                    var items = new List<Item>();
                    var ctes = new List<string>();
                    string from = From(items, P(0.7) ? 1 : 2, 2, ctes, out _);
                    var cols = Enumerable.Range(0, arity).Select(_ => { (Item it, ColMeta c) = AnyCol(items); return ColRef(it, c); }).ToList();
                    string where = P(0.5) ? " WHERE " + Cond(items, null, 2) : "";
                    if (ctes.Count > 0)
                    {
                        // a compound member cannot carry its own WITH; read the CTE through a subquery instead
                        from = From(items = [], 1, 3, ctes = [], out _);
                        cols = Enumerable.Range(0, arity).Select(_ => { (Item it, ColMeta c) = AnyCol(items); return ColRef(it, c); }).ToList();
                        where = "";
                    }

                    parts.Add("SELECT " + string.Join(", ", cols) + " FROM " + from + where);
                    if (i > 0)
                    {
                        ops.Add(PickOf("UNION", "UNION ALL", "INTERSECT", "EXCEPT"));
                    }
                }

                var sb = new StringBuilder(parts[0]);
                for (int i = 1; i < parts.Count; i++)
                {
                    sb.Append(' ').Append(ops[i - 1]).Append(' ').Append(parts[i]);
                }

                bool ordered = P(0.6);
                if (ordered)
                {
                    sb.Append(OrderAll(arity));
                    if (P(0.4))
                    {
                        sb.Append(" LIMIT ").Append(_r.Next(0, 20)).Append(P(0.4) ? " OFFSET " + _r.Next(0, 5) : "");
                    }
                }

                return (sb.ToString(), ordered, true);
            }

            default:
            {
                Sel s = Simple(0, null, false);
                bool ordered = P(0.55);
                string sql = s.Sql;
                if (ordered)
                {
                    sql += OrderAll(s.Arity);
                    if (P(0.4))
                    {
                        sql += " LIMIT " + _r.Next(0, 30) + (P(0.3) ? " OFFSET " + _r.Next(0, 5) : "");
                    }
                }

                return (sql, ordered, true);
            }
        }
    }
}
