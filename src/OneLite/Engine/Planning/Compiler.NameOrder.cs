using RecursiveAI.OneLite.Sql;

namespace RecursiveAI.OneLite.Planning;

// Which missing column an expression reports. The docs only say "no such column"; the pinned shell names one by the order in which it
// resolves the parts of an expression, which is fixed here from observed behaviour (a few thousand random expressions over missing
// columns, compared with the shell):
//  - the walk goes left to right through an operator's operands, and stops at the first missing column;
//  - a function call (and LIKE/GLOB/MATCH, which are calls of like(pattern, text), and ISNULL/NOTNULL) resolves its arguments in a walk of
//    its own: a missing column there ends that walk only, so a later sibling that is also missing replaces the name;
//  - once a name is set, any operator node, literal or constant met afterwards ends the whole walk, but a column or a function call
//    is still resolved;
//  - IS and IS NOT whose right operand is a plain column resolve that column first, and are resolved even after a name is set;
//  - a constant ISNULL (`1 ISNULL`) is a literal.
internal sealed partial class Compiler
{
    private readonly List<Expr> _exprStack = new();

    private void CompileInto(Expr e, int dest)
    {
        _exprStack.Add(e);
        try
        {
            CompileIntoCore(e, dest);
        }
        finally
        {
            _exprStack.RemoveAt(_exprStack.Count - 1);
        }
    }

    /// <summary>The missing column the reference reports for the expression being compiled when <paramref name="failed"/> is the one found missing.</summary>
    private ColumnRef FirstMissingColumn(ColumnRef failed)
    {
        // the outermost expression of the current SELECT: entries above the last subquery belong to its scope
        Expr? root = null;
        for (int i = _exprStack.Count - 1; i >= 0; i--)
        {
            Expr x = _exprStack[i];
            if (x is SubqueryExpr or ExistsExpr or InSelectExpr)
            {
                break;
            }

            root = x;
        }

        if (root is null)
        {
            return failed;
        }

        ColumnRef? name = null;
        _ = WalkNames(root, ref name);
        return name ?? failed;
    }

    private bool ColumnIsMissing(ColumnRef c)
    {
        if (_excluded != null && c.Table is { } xt && AsciiComparer.Instance.Equals(xt.Text, "excluded"))
        {
            return false;
        }

        try
        {
            if (c is { Schema: null, Table: null } && (AsciiComparer.Instance.Equals(c.Column.Text, "true") || AsciiComparer.Instance.Equals(c.Column.Text, "false")))
            {
                return false;
            }

            return !Resolve(c, out _, out _, out _, false);
        }
        catch (OneLiteException)
        {
            return false;
        }
    }

    /// <summary>Walks <paramref name="n"/> in the reference's order; true when the whole walk ends.</summary>
    private bool WalkNames(Expr n, ref ColumnRef? name)
    {
        switch (n)
        {
            case ParenExpr p:
                return WalkNames(p.Inner, ref name);
            case ColumnRef c:
                if (ColumnIsMissing(c))
                {
                    name = c;
                    return true;
                }

                return false;
            case LiteralExpr or VariableExpr:
                return name is not null;
            case NullTestExpr nt:
                if (nt.Operand is LiteralExpr)
                {
                    return name is not null;
                }

                _ = WalkNames(nt.Operand, ref name);
                return false;
            case FunctionCall f:
                foreach (Expr a in f.Args)
                {
                    if (WalkNames(a, ref name))
                    {
                        break;
                    }
                }

                return false;
            case LikeExpr l:
                if (!WalkNames(l.Pattern, ref name) && !WalkNames(l.Left, ref name) && l.Escape is not null)
                {
                    _ = WalkNames(l.Escape, ref name);
                }

                return false;
            case BinaryExpr { Op: BinaryOp.Is or BinaryOp.IsNot } b when Unparen(b.Right) is ColumnRef:
                return WalkNames(b.Right, ref name) || WalkNames(b.Left, ref name);
            case SubqueryExpr or ExistsExpr:
                return name is not null;
        }

        if (name is not null)
        {
            return true;
        }

        switch (n)
        {
            case UnaryExpr u:
                return WalkNames(u.Operand, ref name);
            case BinaryExpr b:
                return WalkNames(b.Left, ref name) || WalkNames(b.Right, ref name);
            case CollateExpr c:
                return WalkNames(c.Operand, ref name);
            case CastExpr c:
                return WalkNames(c.Operand, ref name);
            case BetweenExpr bt:
                return WalkNames(bt.Operand, ref name) || WalkNames(bt.Low, ref name) || WalkNames(bt.High, ref name);
            case InListExpr il:
                if (WalkNames(il.Operand, ref name))
                {
                    return true;
                }

                foreach (Expr x in il.Items)
                {
                    if (WalkNames(x, ref name))
                    {
                        return true;
                    }
                }

                return false;
            case InSelectExpr isel:
                return WalkNames(isel.Operand, ref name);
            case InTableExpr it:
                if (WalkNames(it.Operand, ref name))
                {
                    return true;
                }

                foreach (Expr x in it.Args)
                {
                    if (WalkNames(x, ref name))
                    {
                        return true;
                    }
                }

                return false;
            case CaseExpr ce:
                if (ce.Operand is not null && WalkNames(ce.Operand, ref name))
                {
                    return true;
                }

                foreach (WhenClause w in ce.Whens)
                {
                    if (WalkNames(w.When, ref name) || WalkNames(w.Then, ref name))
                    {
                        return true;
                    }
                }

                return ce.Else is not null && WalkNames(ce.Else, ref name);
            case RowValueExpr rv:
                foreach (Expr x in rv.Items)
                {
                    if (WalkNames(x, ref name))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    private static Expr Unparen(Expr e)
    {
        while (e is ParenExpr p)
        {
            e = p.Inner;
        }

        return e;
    }
}
