using System.Collections.Immutable;
using System.Linq;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite.Planning;

// Access by IN: one seek for each distinct value of "column IN (list)" or "column IN (SELECT ...)".
// lang_expr "The IN and NOT IN operators" defines the comparison (x IN (y, z) is x = +y OR x = +z, so the list carries no affinity of its own);
// the reference walks the distinct values in ascending order (observed), so the loop here reads them from a sorted set.
internal sealed partial class Compiler
{
    /// <summary>The loop over the values of an IN term: the cursor over the sorted set, the register holding the current value, and the labels.</summary>
    private struct InLoop
    {
        internal int Cursor;
        internal int Value;
        internal int Top;
        internal int Next;
    }

    /// <summary>The non-negated <c>column IN (...)</c> conjuncts of <paramref name="where"/> that a seek on <paramref name="s"/> can answer, with the column each tests.</summary>
    private List<(Expr Term, int Column)> InTerms(Source s, List<Expr> where, HashSet<Source> bound)
    {
        var found = new List<(Expr, int)>();
        foreach (Expr w in where)
        {
            foreach (Expr c in Conjuncts(w))
            {
                switch (c)
                {
                    case InListExpr { Negated: false, Items.IsDefaultOrEmpty: false } il when ColumnOf(il.Operand, s) is { } lc:
                        if (il.Items.All(i => IsUsableValue(i, s, bound, lc) && CmpAffOf(i) == Affinity.None))
                        {
                            found.Add((il, lc));
                        }

                        break;
                    case InSelectExpr { Negated: false } isel when ColumnOf(isel.Operand, s) is { } sc && _scope is not null:
                    {
                        // The set is built before the table is positioned, so the subquery may read only items that outer loops have positioned.
                        // (Asked of the subquery alone: the operand is a column of this very table.)
                        HashSet<Source>? used = SubquerySources(new SubqueryExpr(isel.Select), _scope);
                        if (used is not null && !used.Contains(s) && used.IsSubsetOf(bound))
                        {
                            found.Add((isel, sc));
                        }

                        break;
                    }
                }
            }
        }

        return found;
    }

    /// <summary>Whether the comparison's collation (the left operand's) is the one the index orders its first column by.</summary>
    private bool InCollationMatches(Expr term, Source s, string? indexColl)
    {
        string want = CollName(ParseColl(indexColl ?? "BINARY"));
        switch (term)
        {
            case InListExpr il:
                return il.Items.All(i => AsciiComparer.Instance.Equals(CollName(PairColl(il.Operand, i)), want));
            case InSelectExpr isel:
                return AsciiComparer.Instance.Equals(CollName(PairColl(isel.Operand, isel.Operand)), want);
            default:
                return false;
        }
    }

    /// <summary>
    /// Emits the code that gathers the values of the IN term into a sorted set without duplicates or NULLs (a NULL never equals anything), and the loop
    /// head that visits them. The caller emits the seek and body, marks <c>Next</c> and emits ENext. False when a subquery's result turns out to have an
    /// affinity that would make the comparison convert the stored values, which only a scan can answer.
    /// </summary>
    private bool EmitInLoop(Source s, AccessPlan plan, int lend, out InLoop loop)
    {
        loop = default;
        TableInfo t = s.Table;
        bool rowid = plan.Kind == PlanKind.RowidIn;
        Affinity colAff = rowid ? Affinity.Integer : t.Columns[plan.InColumn].Affinity;
        CollKind coll = rowid ? CollKind.Binary : ParseColl(plan.Index!.Collations[0] ?? "BINARY");
        int set = NewSet();
        var spec = new SetSpec { Collations = [coll] };
        if (plan.In is InListExpr il)
        {
            Emit(Op.SetOpen, set, 0, 0, spec);
            foreach (Expr item in il.Items)
            {
                int v = KeyReg(item, colAff);
                int skip = NewLabel();
                Emit(Op.IfNull, v, skip);
                Emit(Op.SetAdd, set, v, 1);
                Mark(skip);
            }
        }
        else
        {
            var isel = (InSelectExpr)plan.In!;
            _ = BeginSubquery(out Probe probe, out int guard, PlanNodeKind.Subquery, "IN (list of values)");
            SelectMeta meta;
            try
            {
                Emit(Op.SetOpen, set, 0, 0, spec);
                var d = new Dest { Kind = DestKind.Set, Set = set, UseOperandAff = true, OperandAff = colAff, OperandColl = coll };
                meta = CompileSelectStmt(isel.Select, d);
            }
            finally
            {
                EndSubquery(probe, guard);
            }

            if (meta.Names.Length != 1)
            {
                throw Err($"sub-select returns {meta.Names.Length} columns - expected 1");
            }

            // The stored values were converted as the comparison would convert them (CompareAffinity of the column and the subquery's column).
            // Same rule as IsUsableValue for an index: a conversion of the stored column values cannot be answered by looking keys up.
            Affinity cmp = Values.CompareAffinity(colAff, meta.Aff[0]);
            // The rowid column is never converted, so a subquery's values (converted to the comparison's affinity as they were stored) always work.
            bool ok = rowid || cmp switch
            {
                Affinity.Text => colAff == Affinity.Text,
                Affinity.Numeric or Affinity.Integer or Affinity.Real => colAff is Affinity.Numeric or Affinity.Integer or Affinity.Real,
                _ => true,
            };
            if (!ok)
            {
                return false;
            }
        }

        loop.Cursor = NewEphCursor();
        loop.Value = NewReg();
        loop.Top = NewLabel();
        loop.Next = NewLabel();
        Emit(Op.SetToEph, set, loop.Cursor);
        Emit(Op.ERewind, loop.Cursor, lend);
        Mark(loop.Top);
        Emit(Op.EColumn, loop.Cursor, 0, loop.Value);

        // The subquery's rows may include NULLs; the list's were dropped above.
        Emit(Op.IfNull, loop.Value, loop.Next);
        return true;
    }
}
