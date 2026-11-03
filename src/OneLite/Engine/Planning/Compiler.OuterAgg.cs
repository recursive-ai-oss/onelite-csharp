using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite.Planning;

// lang_aggfunc: an aggregate function whose arguments read only columns of an enclosing query is an aggregate of that query, not of the
// subquery it is written in. When its arguments read columns of several queries, the innermost of them owns it. With no columns at all it
// belongs to the query it is written in.
internal sealed partial class Compiler
{
    /// <summary>The scope of the query that owns an aggregate call, or null when it is the current one (or no column of its arguments resolves).</summary>
    private Scope? OwnerScope(FunctionCall f)
    {
        if (_scope is null)
        {
            return null;
        }

        var refs = new List<ColumnRef>();
        foreach (Expr a in f.Args)
        {
            CollectColumnRefs(a, refs);
        }

        if (f.Filter is not null)
        {
            CollectColumnRefs(f.Filter, refs);
        }

        foreach (OrderingTerm ot in f.OrderBy)
        {
            CollectColumnRefs(ot.Expr, refs);
        }

        Scope? best = null;
        foreach (ColumnRef cr in refs)
        {
            Source? src;
            try
            {
                if (!Resolve(cr, out src, out _, out Expr? alias, false) || alias is not null)
                {
                    continue;
                }
            }
            catch (OneLiteException)
            {
                continue;   // reported by the normal walk, in its own order
            }

            for (Scope? sc = _scope; sc is not null; sc = sc.Parent)
            {
                if (sc.Sources.Contains(src!))
                {
                    if (best is null || sc.Depth > best.Depth)
                    {
                        best = sc;
                    }

                    break;
                }
            }
        }

        return best is null || ReferenceEquals(best, _scope) ? null : best;
    }

    private static void CollectColumnRefs(Expr e, List<ColumnRef> into)
    {
        switch (e)
        {
            case ColumnRef c:
                into.Add(c);
                break;
            case FunctionCall f:
                foreach (Expr a in f.Args)
                {
                    CollectColumnRefs(a, into);
                }

                break;
            case ParenExpr p:
                CollectColumnRefs(p.Inner, into);
                break;
            case UnaryExpr u:
                CollectColumnRefs(u.Operand, into);
                break;
            case BinaryExpr b:
                CollectColumnRefs(b.Left, into);
                CollectColumnRefs(b.Right, into);
                break;
            case CollateExpr c:
                CollectColumnRefs(c.Operand, into);
                break;
            case CastExpr c:
                CollectColumnRefs(c.Operand, into);
                break;
            case BetweenExpr bt:
                CollectColumnRefs(bt.Operand, into);
                CollectColumnRefs(bt.Low, into);
                CollectColumnRefs(bt.High, into);
                break;
            case InListExpr il:
                CollectColumnRefs(il.Operand, into);
                foreach (Expr x in il.Items)
                {
                    CollectColumnRefs(x, into);
                }

                break;
            case InSelectExpr isl:
                CollectColumnRefs(isl.Operand, into);
                break;
            case NullTestExpr nt:
                CollectColumnRefs(nt.Operand, into);
                break;
            case LikeExpr l:
                CollectColumnRefs(l.Left, into);
                CollectColumnRefs(l.Pattern, into);
                if (l.Escape is not null)
                {
                    CollectColumnRefs(l.Escape, into);
                }

                break;
            case RowValueExpr rv:
                foreach (Expr x in rv.Items)
                {
                    CollectColumnRefs(x, into);
                }

                break;
            case CaseExpr ce:
                if (ce.Operand is not null)
                {
                    CollectColumnRefs(ce.Operand, into);
                }

                foreach (WhenClause w in ce.Whens)
                {
                    CollectColumnRefs(w.When, into);
                    CollectColumnRefs(w.Then, into);
                }

                if (ce.Else is not null)
                {
                    CollectColumnRefs(ce.Else, into);
                }

                break;
        }
    }

    /// <summary>
    /// An aggregate call in a subquery that belongs to the enclosing query <paramref name="owner"/>: it is planned with that query's aggregates and
    /// read from the finished group here.
    /// </summary>
    private void CompileOuterAggregate(FunctionCall f, AggKind kind, Scope owner, int dest)
    {
        string name = f.Name.Text;

        AggSave? save = null;
        for (int k = _aggSaves.Count - 1; k >= 0; k--)
        {
            if (ReferenceEquals(_aggSaves[k].Owner, owner))
            {
                save = _aggSaves[k];
                break;
            }
        }

        bool planned = save?.Plan is not null;

        // Observed: WHERE and ON of a query that is not an aggregate query give the plain misuse text; in an aggregate query they take the owner's aggregate.
        if (_scope!.NoAgg == AggClause.Filter && !_scope.IsAgg)
        {
            throw Err("misuse of aggregate function " + name + "()");
        }

        // Observed: an EXISTS in a query that cannot take the aggregate (its WHERE, say) does not look at its own result columns and ORDER BY for them.
        if (_scope.IgnoreOuterAgg && _scope.NoAgg is AggClause.None or AggClause.Order && !planned && !owner.CanBecomeAgg)
        {
            Emit(Op.Null, 0, dest);
            return;
        }

        if (owner.NoAgg != AggClause.None)
        {
            throw Err("misuse of aggregate: " + name + "()");
        }

        if (!planned)
        {
            if (owner.CanBecomeAgg)
            {
                throw new OuterAggregateException(owner, name);
            }

            throw Err("misuse of aggregate: " + name + "()");
        }

        AggPlan plan = save!.Plan!;
        if (plan.Registering)
        {
            // an aggregate inside the arguments of another aggregate of the same query
            throw Err("misuse of aggregate: " + name + "()");
        }

        if (save.Discover)
        {
            // Plan it as if written in the owner: its arguments are resolved and recorded in the owner's state, not the subquery's.
            int at = _aggSaves.IndexOf(save);
            List<AggSave> inner = _aggSaves.GetRange(at, _aggSaves.Count - at);
            _aggSaves.RemoveRange(at, inner.Count);
            var current = new AggSave(_recordCols, _colMap, _aggPlan, _aggDiscover, _groupKeyRegs, [], _scope);
            Scope? scopeNow = _scope;
            (_recordCols, _colMap, _aggPlan, _aggDiscover, _groupKeyRegs, _scope) = (save.RecordCols, save.ColMap, save.Plan, save.Discover, save.GroupKeys, owner);
            try
            {
                RegisterAggregate(plan, f, kind);
            }
            finally
            {
                (_recordCols, _colMap, _aggPlan, _aggDiscover, _groupKeyRegs, _scope) = (current.RecordCols, current.ColMap, current.Plan, current.Discover, current.GroupKeys, scopeNow);
                _aggSaves.AddRange(inner);
            }

            Emit(Op.Null, 0, dest);
            return;
        }

        if (!plan.Index.TryGetValue(f, out int idx) || idx >= plan.ResultRegs.Length)
        {
            throw Err("misuse of aggregate function " + name + "()");
        }

        // the value changes with the owner's group, so every subquery between here and the owner runs again per group
        foreach (Probe p in _probes)
        {
            if (p.InnerDepth > owner.Depth)
            {
                p.Correlated = true;
            }
        }

        Emit(Op.Copy, plan.ResultRegs[idx], dest);
    }
}
