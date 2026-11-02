using System.Collections.Immutable;
using System.Linq;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite.Planning;

internal sealed partial class Compiler
{
    /// <summary>
    /// The reference looks up every table name of a statement, subqueries in expressions included, before it resolves any column, so a missing
    /// table is reported even when a column error comes earlier in the text. Called after a column error: returns the message for the last
    /// missing table name, or null when every table exists. Names of CTEs declared anywhere in the statement are taken as present.
    /// </summary>
    private string? MissingTableMessage(SelectStatement s)
    {
        var ctes = new HashSet<string>(AsciiComparer.Instance);
        string? last = null;
        ScanSelect(s, ctes, ref last);
        return last;
    }

    private void ScanSelect(SelectStatement s, HashSet<string> ctes, ref string? last)
    {
        if (s.With is { } w)
        {
            foreach (CommonTableExpression c in w.Tables)
            {
                ctes.Add(c.Name.Text);
            }

            foreach (CommonTableExpression c in w.Tables)
            {
                ScanSelect(c.Select, ctes, ref last);
            }
        }

        foreach (SelectCore core in s.Cores)
        {
            switch (core)
            {
                case SimpleSelect ss:
                    foreach (ResultColumn rc in ss.Columns)
                    {
                        if (rc is ExprColumn ec)
                        {
                            ScanExpr(ec.Expr, ctes, ref last);
                        }
                    }

                    if (ss.From is { } f)
                    {
                        ScanFrom(f, ctes, ref last);
                    }

                    foreach (Expr? e in new[] { ss.Where, ss.Having })
                    {
                        if (e is not null)
                        {
                            ScanExpr(e, ctes, ref last);
                        }
                    }

                    foreach (Expr g in ss.GroupBy)
                    {
                        ScanExpr(g, ctes, ref last);
                    }

                    break;
                case ValuesCore vc:
                    foreach (ImmutableArray<Expr> row in vc.Rows)
                    {
                        foreach (Expr e in row)
                        {
                            ScanExpr(e, ctes, ref last);
                        }
                    }

                    break;
            }
        }
    }

    private void ScanFrom(FromClause f, HashSet<string> ctes, ref string? last)
    {
        foreach (FromItem it in f.Items)
        {
            switch (it.Source)
            {
                case NamedTable nt:
                    string? schema = nt.Name.Schema?.Text;
                    string name = nt.Name.Name.Text;
                    if ((schema is null && ctes.Contains(name)) || _cat.FindIn(schema, name) is not null || (schema is null && Modules.Find(name) is not null) || (schema is null && EponymousTable(name) is not null))
                    {
                        break;
                    }

                    last = "no such table: " + (schema is null ? name : schema + "." + name);
                    break;
                case SubquerySource sq:
                    ScanSelect(sq.Select, ctes, ref last);
                    break;
                case ParenFrom pf:
                    ScanFrom(pf.From, ctes, ref last);
                    break;
                case TableFunction tf:
                    foreach (Expr a in tf.Args)
                    {
                        ScanExpr(a, ctes, ref last);
                    }

                    break;
            }

            if (it.On is not null)
            {
                ScanExpr(it.On, ctes, ref last);
            }
        }
    }

    private void ScanExpr(Expr e, HashSet<string> ctes, ref string? last)
    {
        switch (e)
        {
            case SubqueryExpr sq:
                ScanSelect(sq.Select, ctes, ref last);
                return;
            case ExistsExpr ex:
                ScanSelect(ex.Select, ctes, ref last);
                return;
            case InSelectExpr isl:
                ScanExpr(isl.Operand, ctes, ref last);
                ScanSelect(isl.Select, ctes, ref last);
                return;
            case ParenExpr p:
                ScanExpr(p.Inner, ctes, ref last);
                return;
            case CollateExpr c:
                ScanExpr(c.Operand, ctes, ref last);
                return;
            case CastExpr ce:
                ScanExpr(ce.Operand, ctes, ref last);
                return;
            case RowValueExpr rv:
                foreach (Expr x in rv.Items)
                {
                    ScanExpr(x, ctes, ref last);
                }

                return;
        }

        foreach (Expr child in CollationChildren(e))
        {
            ScanExpr(child, ctes, ref last);
        }
    }
}

internal sealed partial class Compiler
{
    /// <summary>
    /// Names the enclosing query reads from the view or subquery it selects from (null: every column). The reference merges a simple view or
    /// subquery into the outer query, so the outer query's columns alone decide whether a covering index is read; this is the same decision for
    /// the body that OneLite materializes.
    /// </summary>
    private HashSet<string>? _neededFromOuter;

    /// <summary>Set while the body of such a view is compiled: its first SELECT reads it once to decide on a covering index.</summary>
    private HashSet<string>? _coverNames;

    private static bool QualifiesForMerge(SelectStatement sel)
        => sel.With is null && sel.Cores.Length == 1 && sel.Cores[0] is SimpleSelect { From: not null, GroupBy.IsDefaultOrEmpty: true, Having: null, Limit: null, OrderBy.IsDefaultOrEmpty: true, Windows.IsDefaultOrEmpty: true } ss
            && ss.Quantifier != SelectQuantifier.Distinct
            && !ss.Columns.Any(c => c is ExprColumn ec && (ContainsAggregate(ec.Expr) || HasWindow(ec.Expr)));

    private static bool HasWindow(Expr e) => e is FunctionCall { Over: not null } || CollationChildren(e).Any(HasWindow);

    /// <summary>The column names a SELECT mentions anywhere (subqueries included); false when it selects a star, which needs every column.</summary>
    private static bool CollectNames(SimpleSelect core, HashSet<string> names)
    {
        foreach (ResultColumn rc in core.Columns)
        {
            if (rc is StarColumn)
            {
                return false;
            }

            if (rc is ExprColumn ec)
            {
                NamesIn(ec.Expr, names);
            }
        }

        foreach (Expr? e in new[] { core.Where, core.Having })
        {
            if (e is not null)
            {
                NamesIn(e, names);
            }
        }

        foreach (Expr g in core.GroupBy)
        {
            NamesIn(g, names);
        }

        foreach (OrderingTerm o in core.OrderBy)
        {
            NamesIn(o.Expr, names);
        }

        if (core.Limit is { } l)
        {
            NamesIn(l.Limit, names);
            if (l.Offset is not null)
            {
                NamesIn(l.Offset, names);
            }
        }

        if (core.From is { } f)
        {
            foreach (FromItem it in f.Items)
            {
                if (it.On is not null)
                {
                    NamesIn(it.On, names);
                }

                foreach (Name u in it.UsingColumns)
                {
                    names.Add(u.Text);
                }
            }
        }

        return true;
    }

    private static void NamesIn(Expr e, HashSet<string> names)
    {
        switch (e)
        {
            case ColumnRef cr:
                names.Add(cr.Column.Text);
                return;
            case SubqueryExpr sq:
                NamesInSelect(sq.Select, names);
                return;
            case ExistsExpr ex:
                NamesInSelect(ex.Select, names);
                return;
            case InSelectExpr isl:
                NamesIn(isl.Operand, names);
                NamesInSelect(isl.Select, names);
                return;
            case ParenExpr p:
                NamesIn(p.Inner, names);
                return;
            case CollateExpr c:
                NamesIn(c.Operand, names);
                return;
            case CastExpr ce:
                NamesIn(ce.Operand, names);
                return;
            case RowValueExpr rv:
                foreach (Expr x in rv.Items)
                {
                    NamesIn(x, names);
                }

                return;
            case FunctionCall f:
                if (f.Filter is not null)
                {
                    NamesIn(f.Filter, names);
                }

                foreach (OrderingTerm o in f.OrderBy)
                {
                    NamesIn(o.Expr, names);
                }

                break;
        }

        foreach (Expr child in CollationChildren(e))
        {
            NamesIn(child, names);
        }
    }

    private static void NamesInSelect(SelectStatement s, HashSet<string> names)
    {
        foreach (SelectCore core in s.Cores)
        {
            if (core is SimpleSelect ss)
            {
                // A star inside a subquery says nothing about the outer columns; its own names are over-counted, which is harmless.
                CollectNames(ss, names);
                if (ss.From is { } f)
                {
                    foreach (FromItem it in f.Items)
                    {
                        if (it.Source is SubquerySource sq)
                        {
                            NamesInSelect(sq.Select, names);
                        }
                    }
                }
            }
            else if (core is ValuesCore vc)
            {
                foreach (ImmutableArray<Expr> row in vc.Rows)
                {
                    foreach (Expr e in row)
                    {
                        NamesIn(e, names);
                    }
                }
            }
        }
    }
}
