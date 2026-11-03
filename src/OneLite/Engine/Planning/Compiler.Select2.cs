using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite.Planning;

internal sealed partial class Compiler
{
    /// <summary>One result row's path out of a SELECT core: result registers, ORDER BY sorter, DISTINCT set, LIMIT state, destination.</summary>
    private sealed class Pipe
    {
        internal List<(Expr Expr, string Name)> Cols = [];
        internal int N;
        internal int ResultBase;
        internal int Sorter = -1;
        internal int KeyBase;
        internal int[] KeyTarget = [];
        internal SimpleSelect Core = null!;
        internal int DistinctSet = -1;
        internal int LimReg, OffReg, End;
        internal Dest Dest = null!;
        internal SortTerm[] Terms = [];
    }

    /// <summary>Set while the side of a UNION, INTERSECT or EXCEPT is compiled; read once by that side's SELECT.</summary>
    private bool _armDistinct;

    private SelectMeta CompileSimple(SimpleSelect core, Dest dest, bool applyOrderLimit)
    {
        bool armDistinct = _armDistinct;
        _armDistinct = false;
        HashSet<string>? coverNames = _coverNames;
        _coverNames = null;
        var scope = new Scope { Parent = _scope, Depth = NextDepth() };
        Scope? saved = _scope;
        Dictionary<int, string> savedAliasesOuter = _aliases;
        WinCompile? savedWin = _win;
        Scope? savedCarry = _carryScope;
        _win = null;
        _scope = scope;
        try
        {
            PreValidateWindows(core);

            HashSet<string>? neededNames = null;
            if (core.From is { Items.Length: 1 } && core.Windows.IsDefaultOrEmpty)
            {
                var names = new HashSet<string>(AsciiComparer.Instance);
                if (CollectNames(core, names))
                {
                    neededNames = names;
                }
            }

            _neededFromOuter = neededNames;
            List<Expr> joinPool;
            try
            {
                joinPool = BuildFrom(core.From, scope);
            }
            finally
            {
                _neededFromOuter = null;
            }


            // Result columns.
            var cols = new List<(Expr Expr, string Name)>();
            var aliasMap = new Dictionary<int, string>();
            Dictionary<int, string> savedAliases = _aliases;
            _aliases = aliasMap;
            foreach (ResultColumn rc in core.Columns)
            {
                switch (rc)
                {
                    case StarColumn star:
                        ExpandStar(star, scope, cols);
                        break;
                    case ExprColumn ec:
                        if (ec.Alias is { } al)
                        {
                            scope.Aliases.TryAdd(al.Text, ec.Expr);
                            aliasMap[cols.Count] = al.Text;
                        }

                        cols.Add((ec.Expr, ResultName(ec)));
                        break;
                }
            }

            ImmutableArray<OrderingTerm> order = applyOrderLimit && !core.OrderBy.IsDefault ? core.OrderBy : ImmutableArray<OrderingTerm>.Empty;
            LimitClause? limit = applyOrderLimit ? core.Limit : null;
            // The reference resolves the names in LIMIT and OFFSET before those of any other clause.
            if (limit is not null)
            {
                DryRun(() =>
                {
                    Scope? s2 = _scope;
                    _scope = NoColumnsScope();
                    try
                    {
                        Compile(limit.Limit);
                        if (limit.Offset is not null)
                        {
                            Compile(limit.Offset);
                        }
                    }
                    finally
                    {
                        _scope = s2;
                    }
                });
            }

            OneLiteException? orderLowError = null;
            if (applyOrderLimit)
            {
                try
                {
                    CheckLowPositionsFirst([.. order.Select(o => o.Expr)], cols.Count, "ORDER BY");
                }
                catch (OneLiteException e)
                {
                    orderLowError = e;   // reported when the walk reaches ORDER BY, after unknown names in the result columns and WHERE
                }
            }

            // An aggregate inside a subquery belongs to this query when its arguments read this query's columns, and to an enclosing query when
            // they read only that query's (lang_aggfunc).
            Func<FunctionCall, bool> owns = call => OwnerScope(call) is null;
            // HAVING alone does not make a query an aggregate query (observed: "HAVING clause on a non-aggregate query").
            bool hasAgg = cols.Any(c => FindAggregate(c.Expr, owns) is not null) || (!order.IsDefaultOrEmpty && order.Any(o => FindAggregate(o.Expr, owns) is not null));
            bool grouped = !core.GroupBy.IsDefaultOrEmpty;
            if (core.Where is not null && FindAggregate(core.Where, owns) is { } wa)
            {
                throw Err(AggregateMisuse(wa.Name.Text, hasAgg || grouped));
            }

            foreach (Expr on in joinPool.Concat(scope.Sources.SelectMany(s => s.On)))
            {
                if (FindAggregate(on, owns) is { } oa)
                {
                    throw Err(AggregateMisuse(oa.Name.Text, hasAgg || grouped));
                }
            }

            bool hasWin = cols.Any(c => FindWindowCall(c.Expr) is not null) || (!order.IsDefaultOrEmpty && order.Any(o => FindWindowCall(o.Expr) is not null));
            if (hasWin)
            {
                RejectWindowCalls(core.Where);
                foreach (Expr on in joinPool.Concat(scope.Sources.SelectMany(s => s.On)))
                {
                    RejectWindowCalls(on);
                }
            }

            if (core.Having is not null && !hasAgg && !grouped)
            {
                throw Err("HAVING clause on a non-aggregate query");
            }

            if (hasWin)
            {
                foreach (Expr g in core.GroupBy)
                {
                    RejectWindowCalls(g);
                }

                RejectWindowCalls(core.Having);
                _win = NewWinCompile(core);
                _carryScope = scope;
            }

            hasAgg |= grouped;

            // Resolve every name first, in the order the reference library does (result columns, WHERE, GROUP BY, HAVING, ORDER BY, LIMIT), so the
            // first error reported is the same one; the code generated for this pass is thrown away.
            // A bad GROUP BY term is reported when the walk reaches GROUP BY, after the result columns and WHERE have resolved.
            Expr[] groupExprs = [];
            OneLiteException? groupError = null;
            if (grouped)
            {
                try
                {
                    groupExprs = ResolveGroupTerms(core, cols);
                }
                catch (OneLiteException e)
                {
                    groupError = e;
                }
            }

            void ResolveNames()
            {
                _columnsAllowed = true;
                scope.NoAgg = AggClause.None;
                scope.CanBecomeAgg = false;
                scope.IgnoreOuterAgg = dest.Kind == DestKind.Exists;
                scope.IsAgg = hasAgg;
                if (hasAgg)
                {
                    PostDiscover(cols.Select(c => c.Expr), core.Having, order, cols, core, dry: true);
                }
                else
                {
                    scope.CanBecomeAgg = true;
                    try
                    {
                        foreach ((Expr e, _) in cols)
                        {
                            Compile(e);
                        }
                    }
                    finally
                    {
                        scope.CanBecomeAgg = false;
                    }
                }

                scope.AliasesVisible = true;
                scope.NoAgg = AggClause.Filter;
                if (core.Where is not null)
                {
                    Compile(core.Where);
                }

                foreach (Expr on in joinPool)
                {
                    Compile(on);
                }

                foreach (Source s in scope.Sources)
                {
                    foreach (Expr on in s.On)
                    {
                        Compile(on);
                    }
                }

                if (groupError is not null)
                {
                    throw groupError;
                }

                scope.NoAgg = AggClause.Other;
                foreach (Expr g in groupExprs)
                {
                    Compile(g);
                }

                if (orderLowError is not null)
                {
                    throw orderLowError;
                }

                if (!hasAgg)
                {
                    scope.NoAgg = AggClause.Order;
                    for (int k = 0; k < order.Length; k++)
                    {
                        if (ResolveOrderTerm(order[k].Expr, cols, k) < 0)
                        {
                            Compile(order[k].Expr);
                        }
                    }
                }

                scope.AliasesVisible = false;
            }

            // The first pass learns whether a subquery holds an aggregate of this query; if so the query is an aggregate query and the pass runs again.
            try
            {
                DryRun(ResolveNames);
            }
            catch (OuterAggregateException ex) when (ReferenceEquals(ex.Owner, scope) && !hasAgg)
            {
                scope.NoAgg = AggClause.None;
                hasAgg = true;
                try
                {
                    DryRun(ResolveNames);
                }
                catch (OuterAggregateException again)
                {
                    throw Err("misuse of aggregate: " + again.Name + "()");
                }
            }
            finally
            {
                scope.NoAgg = AggClause.None;
            }

            if (_win is not null && !hasAgg)
            {
                // Which columns the finished rows still need once the loops are gone: the ones the result columns and ORDER BY read
                // outside the window calls' own inputs.
                _win.Phase = WinPhase.DiscoverPost;
                Dictionary<(Source, int), int> rec = [];
                _recordCols = rec;
                try
                {
                    DryRun(() =>
                    {
                        _columnsAllowed = true;
                        foreach ((Expr e, _) in cols)
                        {
                            Compile(e);
                        }

                        scope.AliasesVisible = true;
                        for (int k = 0; k < order.Length; k++)
                        {
                            if (ResolveOrderTerm(order[k].Expr, cols, k) < 0)
                            {
                                Compile(order[k].Expr);
                            }
                        }

                        scope.AliasesVisible = false;
                    });
                    _win.Slots = [.. rec.OrderBy(kv => kv.Value).Select(kv => (kv.Key.Item1, kv.Key.Item2))];
                }
                finally
                {
                    _recordCols = null;
                }

                _win.Phase = WinPhase.Discover;
            }

            var meta = new SelectMeta
            {
                Names = [.. cols.Select(c => c.Name)],
                Aff = [.. cols.Select(c => CmpAffOf(c.Expr))],
                Coll = [.. cols.Select(c => ResultCollOf(c.Expr, dest, core.Quantifier, out _))],
                CollNames = [.. cols.Select(c => ResultCollName(c.Expr, dest, core.Quantifier))],
                Decl = [.. cols.Select(c => DeclTypeOf(c.Expr))],
                Origins = [.. cols.Select(c => OriginOf(c.Expr))],
            };
            if (dest.UseOperandAff && meta.Aff.Length > 0)
            {
                dest.Aff = Values.InSetAffinity(dest.OperandAff, meta.Aff[0]);
            }

            var pipe = new Pipe { Cols = cols, N = cols.Count, Core = core, Dest = dest };
            int limReg = 0, offReg = 0;
            int end = NewLabel();
            {
                Scope? s3 = _scope;
                _scope = scope.Parent;
                try
                {
                    EmitLimitRegs(limit, ref limReg, ref offReg);
                }
                finally
                {
                    _scope = s3;
                }
            }

            if (limReg != 0)
            {
                Emit(Op.IfNot, limReg, end, 0); // LIMIT 0 returns nothing
            }

            pipe.LimReg = limReg;
            pipe.OffReg = offReg;
            pipe.End = end;

            if (core.Quantifier == SelectQuantifier.Distinct)
            {
                pipe.DistinctSet = NewSet();
                Emit(Op.SetOpen, pipe.DistinctSet, 0, 0, new SetSpec { Collations = [.. meta.Coll] });
            }

            // With a single table the access path is chosen now: ORDER BY rowid and GROUP BY on an index's columns can use its order.
            bool orderedGroups = false;
            bool orderByIndex = false;
            if (scope.Sources.Count == 1 && scope.Sources[0].Kind == SourceKind.Table)
            {
                Source only = scope.Sources[0];
                var cand = new List<Expr>();
                if (core.Where is not null)
                {
                    cand.Add(core.Where);
                }

                cand.AddRange(joinPool);
                _coverUsed = TryUsedColumns(only, cols, core, out HashSet<int>? usedCols) ? usedCols : null;
                AccessPlan plan = MakePlan(only, cand, new HashSet<Source>(ReferenceEqualityComparer.Instance));
                _coverUsed = null;
                if (!grouped && !hasAgg && core.Quantifier != SelectQuantifier.Distinct && plan.Kind == PlanKind.Scan)
                {
                    ImmutableArray<OrderingTerm> byTerms = order;
                    if (armDistinct && order.IsDefaultOrEmpty)
                    {
                        byTerms = AllColumnsOrder(cols);
                    }

                    if (TryOrderIndexPlan(only, byTerms, cols, out AccessPlan? byIndex, out bool partialOrder))
                    {
                        plan = byIndex;
                        orderByIndex = !order.IsDefaultOrEmpty && !partialOrder;
                    }
                }


                if (_win is not null && !hasAgg)
                {
                    WindowIndexPlan(only, ref plan, cols);
                }

                // ORDER BY the key order keeps the table scan: the reference does not switch to a covering index there.
                if (plan.Kind == PlanKind.Scan && _win?.IndexKey is null && (_win is not null || !OrderIsKeyOrder(core.OrderBy, scope)))
                {
                    AccessPlan before = plan;
                    plan = CoveringScan(only, plan, coverNames is null ? cols : [.. cols.Where(c => coverNames.Contains(c.Name))], core);
                    if (!ReferenceEquals(plan, before))
                    {
                        plan.Reason = $"every column the query uses is in {plan.Index!.Name}, which is narrower than the table, so the rows are read from that index (in its key order; each row is still fetched from the table)";
                    }
                }

                if (grouped)
                {
                    orderedGroups = GroupOrderPlan(only, groupExprs, ref plan);
                    if (orderedGroups)
                    {
                        plan.Notes.Add("GROUP BY is satisfied by this order, so no sorter is needed");
                    }
                }

                // Rows are read from the index alone when it holds every column the query uses. The rows and their order are those of the plan as it is.
                if (!NoCover && _win is null && plan.Kind is PlanKind.Index or PlanKind.IndexIn && plan.Index is { Where: null } cover && !only.Table.WithoutRowid
                    && TryUsedColumns(only, cols, core, out HashSet<int>? coverUsed) && IndexCovers(only.Table, cover, coverUsed))
                {
                    plan.Covered = true;
                    plan.Notes.Add($"the rows are read from {cover.Name} alone: it holds every column the query uses");
                }

                _planByCursor[only] = plan;
            }

            // A virtual table that delivers the rows in the ORDER BY's order needs no sorter (vtab: orderByConsumed).
            bool orderByVtab = !order.IsDefaultOrEmpty && !grouped && !hasAgg && _win is null && core.Quantifier != SelectQuantifier.Distinct
                && scope.Sources.Count == 1 && scope.Sources[0].Kind == SourceKind.VTab && scope.Sources[0].Join == JoinFlags.None
                && VtabConsumesOrder(scope.Sources[0], core.Where, joinPool, order, cols);

            // ORDER BY: a rowid-ordered scan needs no sorter.
            bool useSorter = !order.IsDefaultOrEmpty && !orderByIndex && !orderByVtab && !(!hasAgg && _win is null && core.Quantifier != SelectQuantifier.Distinct && OrderIsRowid(order, scope));
            if (_plan is not null && !order.IsDefaultOrEmpty && !useSorter && scope.Sources.Count == 1 && _planByCursor.TryGetValue(scope.Sources[0], out AccessPlan? ordered))
            {
                ordered.Notes.Add("ORDER BY is satisfied by the order the rows are read in, so no sorter is needed");
            }
            if (useSorter)
            {
                int nk = order.Length;
                pipe.KeyBase = NewRegs(nk + pipe.N);
                pipe.ResultBase = pipe.KeyBase + nk;
                pipe.Sorter = NewSorter();
                pipe.KeyTarget = new int[nk];
                var terms = new SortTerm[nk];
                for (int k = 0; k < nk; k++)
                {
                    OrderingTerm ot = order[k];
                    pipe.KeyTarget[k] = ResolveOrderTerm(ot.Expr, cols, k);
                    CollKind coll = pipe.KeyTarget[k] >= 0 ? CollOf(cols[pipe.KeyTarget[k]].Expr, out _) : OrderCollation(ot.Expr, hasAgg);
                    if (pipe.KeyTarget[k] >= 0 && StripParens(ot.Expr) is CollateExpr)
                    {
                        coll = CollOf(StripParens(ot.Expr), out _);
                    }

                    bool desc = ot.Order == SortOrder.Desc;
                    bool nullsFirst = ot.Nulls == NullsOrder.None ? !desc : ot.Nulls == NullsOrder.First;
                    terms[k] = new SortTerm(desc, nullsFirst, coll);
                }

                pipe.Terms = terms;
                Emit(Op.SorterOpen, pipe.Sorter, 0, 0, terms);
            }
            else
            {
                pipe.ResultBase = dest.Kind == DestKind.Output ? OutBase(pipe.N) : NewRegs(pipe.N);
            }

            _columnsAllowed = true;
            if (_win is not null)
            {
                _win.Grouped = hasAgg;
                WindowSetup(_win);
            }

            if (hasAgg)
            {
                EmitAggregate(core, scope, joinPool, groupExprs, order, pipe, orderedGroups);
            }
            else if (_win is not null)
            {
                WindowLayout(_win);
                EmitNest(scope, core.Where, joinPool, () => WindowFeed(_win, null, null));
            }
            else
            {
                _vtLimit = core.Limit is { Offset: null } && core.Quantifier != SelectQuantifier.Distinct ? core.Limit.Limit : null;
                try
                {
                    EmitNest(scope, core.Where, joinPool, () => EmitProduce(pipe, order));
                }
                finally
                {
                    _vtLimit = null;
                }
            }

            if (_win is not null)
            {
                WindowFinish(_win, pipe, order);
            }

            _columnsAllowed = false;
            if (useSorter)
            {
                EmitSortedOutput(pipe);
            }

            if (_plan is not null)
            {
                RecordPostSteps(_plan, groupExprs, order, hasAgg, grouped, orderedGroups, useSorter, core.Quantifier == SelectQuantifier.Distinct);
            }

            // Observed: `x IN (SELECT col FROM tbl)` looks col up in the index created last whose first column is col (same collation, not
            // partial), and when that index is descending a NULL entry sits at its end, where the reference never looks: a miss answers false
            // rather than NULL.
            if (dest.Kind == DestKind.Set && scope.Sources.Count == 1 && scope.Sources[0].Kind == SourceKind.Table && !hasAgg && !grouped && core.Where is null && core.Limit is null
                && core.Quantifier != SelectQuantifier.Distinct && cols.Count == 1 && StripParens(cols[0].Expr) is ColumnRef inCol
                && TryResolveColumn(inCol, out Source inSrc, out int inColumn) && inSrc == scope.Sources[0] && inColumn >= 0 && !inSrc.NotIndexed && inSrc.Forced is null)
            {
                TableInfo inTable = inSrc.Table;
                IndexInfo? lookup = null;
                string ownColl = inTable.Columns[inColumn].CollationName ?? "BINARY";
                var inCandidates = new List<IndexInfo>();
                if (inTable.WithoutRowid && inTable.PrimaryKey is { } inPk)
                {
                    inCandidates.Add(inPk);
                }

                inCandidates.AddRange(inTable.Indexes);
                foreach (IndexInfo ix in inCandidates)
                {
                    if (ix.Where is null && ix.Columns.Length > 0 && ix.Columns[0] == inColumn && AsciiComparer.Instance.Equals(ix.Collations[0] ?? "BINARY", ownColl))
                    {
                        lookup = ix;
                    }
                }

                // Observed: a numeric x against a column that is not numeric converts the stored side, so the set is a table of its own and every
                // NULL in it counts.
                static bool Numeric(Affinity a) => a is Affinity.Numeric or Affinity.Integer or Affinity.Real;
                if (lookup is not null && lookup.Desc.Length > 0 && lookup.Desc[0] && !(dest.UseOperandAff && Numeric(dest.OperandAff) && !Numeric(CmpAffOf(cols[0].Expr)))
                    && !(dest.OperandColl is { } oc && oc != ParseColl(lookup.Collations[0] ?? "BINARY")))
                {
                    Emit(Op.SetIgnoreNull, dest.Set);
                }
            }

            Mark(end);
            return meta;
        }
        finally
        {
            _scope = saved;
            _aliases = savedAliasesOuter;
            _win = savedWin;
            _carryScope = savedCarry;
        }
    }

    /// <summary>The reference library words the error differently when the query is itself an aggregate query ("misuse of aggregate: sum()", checked against the pinned shell).</summary>
    private static string AggregateMisuse(string name, bool aggregateQuery)
        => aggregateQuery ? "misuse of aggregate: " + name + "()" : "misuse of aggregate function " + name + "()";

    private static Expr StripParens(Expr e)
    {
        while (e is ParenExpr p)
        {
            e = p.Inner;
        }

        return e;
    }

    /// <summary>
    /// Whether GROUP BY on these terms can be fed in order: they are the leading columns of an index (same collations) or the rowid, and
    /// the plan is a plain scan. Chooses that index as the access path when it is.
    /// </summary>
    private bool GroupOrderPlan(Source s, Expr[] groupExprs, ref AccessPlan plan)
    {
        if (plan.Kind != PlanKind.Scan && !(plan.Kind == PlanKind.Index && plan.Equal.Count == 0 && plan.Lower is null && plan.Upper is null))
        {
            return false;
        }

        var colsWanted = new List<(int Col, CollKind Coll)>();
        foreach (Expr e in groupExprs)
        {
            Expr x = e;
            while (x is ParenExpr p)
            {
                x = p.Inner;
            }

            if (x is not ColumnRef cr || !TryResolveColumn(cr, out Source rs, out int col) || rs != s)
            {
                return false;
            }

            colsWanted.Add((col, CollOf(x, out _)));
        }

        TableInfo t = s.Table;
        if (plan.Kind == PlanKind.Scan && !t.WithoutRowid && colsWanted.Count == 1 && (colsWanted[0].Col == -1 || colsWanted[0].Col == t.RowidAlias))
        {
            return true;
        }

        IEnumerable<IndexInfo> candidates = t.WithoutRowid ? (t.PrimaryKey is { } pk ? [pk] : []) : t.Indexes;
        foreach (IndexInfo idx in candidates)
        {
            if (!idx.Usable || idx.Columns.Length < colsWanted.Count)
            {
                continue;
            }

            bool ok = true;
            for (int i = 0; i < colsWanted.Count && ok; i++)
            {
                ok = idx.Columns[i] == colsWanted[i].Col && ParseColl(idx.Collations[i] ?? "BINARY") == colsWanted[i].Coll;
            }

            if (ok)
            {
                plan = new AccessPlan { Kind = PlanKind.Index, Index = idx };
                return true;
            }
        }

        return false;
    }

    // A result column that carries an unknown COLLATE is not an error until something compares or sorts by it, as in the reference (`SELECT 'a' COLLATE nosuch` runs).
    // Only the statement's own result rows get this: a subquery, view or compound arm looks the collation up at once, as the reference does.
    private CollKind ResultCollOf(Expr e, Dest dest, SelectQuantifier quantifier, out bool any)
    {
        if (quantifier != SelectQuantifier.Distinct && dest.Kind is DestKind.Output or DestKind.Scalar or DestKind.Exists && MissingCollName(e) != null)
        {
            any = false;
            return CollKind.Binary;
        }

        return CollOf(e, out any);
    }

    private string? ResultCollName(Expr e, Dest dest, SelectQuantifier quantifier) => (quantifier != SelectQuantifier.Distinct && dest.Kind is DestKind.Output or DestKind.Scalar or DestKind.Exists ? MissingCollName(e) : null) ?? (CollOf(e, out bool any) is var ck && any ? CollName(ck) : null);

    // The unknown collation name that CollOf would trip over for this expression, or null.
    private static string? MissingCollName(Expr e)
    {
        switch (e)
        {
            case ParenExpr p:
                return MissingCollName(p.Inner);
            case CollateExpr c:
                return ParseCollOrNull(c.Collation.Text) == null ? c.Collation.Text : null;
            case UnaryExpr { Op: UnaryOp.Plus } u:
                return MissingCollName(u.Operand);
            case CastExpr ce:
                return MissingCollName(ce.Operand);
            case ColumnRef:
                return null;
            default:
                foreach (Expr child in CollationChildren(e))
                {
                    if (MissingCollName(child) is { } n)
                    {
                        return n;
                    }
                }

                return null;
        }
    }

    private static CollKind? ParseCollOrNull(string name)
    {
        try
        {
            return ParseColl(name);
        }
        catch (OneLiteException ex) when (ex.ExtendedCode == ResultCode.ErrorMissingCollSeq)
        {
            return null;
        }
    }

    private CollKind OrderCollation(Expr e, bool post) => CollOf(e, out _);

    /// <summary>
    /// Observed: in a simple SELECT a position term below 1 (0, a negative one, a folded 0) or above 65535 is reported before any term above
    /// the number of columns, whatever their order; among the rest, the first in the text.
    /// </summary>
    private static void CheckLowPositionsFirst(IReadOnlyList<Expr> terms, int columns, string clause)
    {
        for (int k = 0; k < terms.Count; k++)
        {
            bool c = false;
            if (ConstPosition(terms[k], ref c) is long n && (n < 1 || n > 65535))
            {
                throw Err($"{Ordinal(k + 1)} {clause} term out of range - should be between 1 and {columns}");
            }
        }
    }

    private Expr[] ResolveGroupTerms(SimpleSelect core, List<(Expr Expr, string Name)> cols)
    {
        CheckLowPositionsFirst(core.GroupBy, cols.Count, "GROUP BY");
        var res = new Expr[core.GroupBy.Length];
        for (int k = 0; k < res.Length; k++)
        {
            Expr e = core.GroupBy[k];
            bool collated = false;
            long? pos = ConstPosition(e, ref collated);
            if (pos is long n)
            {
                if (n < 1 || n > cols.Count)
                {
                    throw Err($"{Ordinal(k + 1)} GROUP BY term out of range - should be between 1 and {cols.Count}");
                }

                if (FindAggregate(cols[(int)n - 1].Expr, call => OwnerScope(call) is null) is not null)
                {
                    throw Err("aggregate functions are not allowed in the GROUP BY clause");
                }

                res[k] = cols[(int)n - 1].Expr;
                continue;
            }

            if (FindAggregate(e, call => OwnerScope(call) is null) is not null)
            {
                throw Err("aggregate functions are not allowed in the GROUP BY clause");
            }

            res[k] = e;
        }

        return res;
    }

    private void EmitLimitRegs(LimitClause? lim, ref int limReg, ref int offReg)
    {
        if (lim is null)
        {
            return;
        }

        // lang_select: LIMIT and OFFSET are evaluated once, before any row, so no column (the query's own or an outer one) is visible to them.
        Scope? saved = _scope;
        _scope = NoColumnsScope();
        try
        {
            limReg = NewReg();
            CompileInto(lim.Limit, limReg);
            Emit(Op.MustBeInt, limReg);
            if (lim.Offset is { } off)
            {
                offReg = NewReg();
                CompileInto(off, offReg);
                Emit(Op.MustBeInt, offReg);
                Emit(Op.OffsetFix, offReg);
            }
        }
        finally
        {
            _scope = saved;
        }
    }

    private Scope NoColumnsScope() => new() { Parent = null, Depth = (_scope?.Depth ?? -1) + 1 };

    /// <summary>ORDER BY the rowid (or the INTEGER PRIMARY KEY that is the rowid), or the single-column key of a WITHOUT ROWID table in its own
    /// collation: the scan already runs in that order, so rows reach the caller as they are found (and a runtime error mid-scan comes after the
    /// earlier rows, as in the reference).</summary>
    private bool OrderIsKeyOrder(ImmutableArray<OrderingTerm> order, Scope scope)
    {
        if (order.Length != 1 || scope.Sources.Count != 1 || scope.Sources[0].Kind != SourceKind.Table || order[0].Order == SortOrder.Desc || order[0].Nulls != NullsOrder.None)
        {
            return false;
        }

        Expr e = order[0].Expr;
        while (e is ParenExpr p)
        {
            e = p.Inner;
        }

        Source s = scope.Sources[0];
        if (e is not ColumnRef cr || !TryResolveColumn(cr, out Source rs, out int col) || rs != s)
        {
            return false;
        }

        if (s.Table.WithoutRowid)
        {
            return s.Table.PrimaryKey is { Usable: true, Columns.Length: 1 } pk && col == pk.Columns[0] && pk.Collations[0] == s.Table.Columns[col].CollationName;
        }

        return col == -1 || (col >= 0 && col == s.Table.RowidAlias);
    }

    /// <summary>
    /// ORDER BY terms that are the leading columns of an index (same collations, all in the index's direction or all opposite, then
    /// optionally the rowid) are served by walking that index forwards or backwards, so no sort runs and rows reach the caller as they are
    /// found (observed with the pinned shell: a runtime error mid-scan comes after the earlier rows). Of several indexes the narrowest wins.
    /// </summary>
    private bool TryOrderIndexPlan(Source s, ImmutableArray<OrderingTerm> order, List<(Expr Expr, string Name)> cols, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AccessPlan? plan, out bool partial)
    {
        plan = null;
        partial = false;
        bool skippedConstant = false;
        TableInfo t = s.Table;
        if (order.IsDefaultOrEmpty || s.Kind != SourceKind.Table || s.NotIndexed || s.Forced is not null)
        {
            return false;
        }

        var wantCol = new List<int>();
        var wantColl = new List<CollKind>();
        var wantDesc = new List<bool>();
        for (int k = 0; k < order.Length; k++)
        {
            OrderingTerm ot = order[k];
            if (ot.Nulls != NullsOrder.None)
            {
                return false;
            }

            Expr e = ot.Expr;
            int pos = ResolveOrderTerm(e, cols, k);
            CollKind? explicitColl = null;
            if (pos >= 0)
            {
                // A result column (position or alias): it must itself be a plain column, and an explicit COLLATE on the term still counts.
                Expr x = e;
                while (x is ParenExpr or CollateExpr)
                {
                    if (x is CollateExpr ce)
                    {
                        explicitColl ??= ParseColl(ce.Collation.Text);
                    }

                    x = x is ParenExpr pe ? pe.Inner : ((CollateExpr)x).Operand;
                }

                e = cols[pos].Expr;
            }

            while (e is ParenExpr or CollateExpr)
            {
                if (e is CollateExpr ce2)
                {
                    explicitColl ??= ParseColl(ce2.Collation.Text);
                }

                e = e is ParenExpr pe2 ? pe2.Inner : ((CollateExpr)e).Operand;
            }

            if (e is LiteralExpr)
            {
                // A constant orders nothing, but the reference still sorts on it, so this term is not served by the index.
                skippedConstant = true;
                continue;
            }

            if (e is not ColumnRef cr || !TryResolveColumn(cr, out Source rs, out int col) || rs != s)
            {
                return false;
            }

            if (col == t.RowidAlias && col >= 0)
            {
                col = -1;
            }

            wantCol.Add(col);
            wantColl.Add(explicitColl ?? (col < 0 ? CollKind.Binary : ParseColl(t.Columns[col].CollationName ?? "BINARY")));
            wantDesc.Add(ot.Order == SortOrder.Desc);
        }

        if (wantCol.Count == 0 || wantCol[0] < 0)
        {
            return false;
        }

        IndexInfo? best = null;
        bool bestReverse = false;
        int bestWidth = int.MaxValue;
        int bestMatched = 0;
        var candidates = new List<IndexInfo>(t.Indexes);
        if (t.WithoutRowid && t.PrimaryKey is { } pkIdx)
        {
            candidates.Add(pkIdx);
        }

        foreach (IndexInfo idx in candidates)
        {
            if (idx.Where is not null || Array.IndexOf(idx.Columns, -1) >= 0)
            {
                continue;
            }

            int matched = 0;
            int dir = 0; // 1 = same direction as the index, -1 = opposite
            for (int k = 0; k < wantCol.Count; k++)
            {
                bool desc = wantDesc[k];
                int d;
                if (k < idx.Columns.Length)
                {
                    if (idx.Columns[k] != wantCol[k] || ParseColl(idx.Collations[k] ?? "BINARY") != wantColl[k])
                    {
                        break;
                    }

                    bool idxDesc = idx.Desc.Length > k && idx.Desc[k];
                    d = desc == idxDesc ? 1 : -1;
                }
                else if (k == idx.Columns.Length && !t.WithoutRowid && wantCol[k] == -1)
                {
                    // the rowid that ends every entry, ascending
                    d = desc ? -1 : 1;
                }
                else
                {
                    break;
                }

                if (dir != 0 && d != dir)
                {
                    break;
                }

                dir = d;
                matched++;
            }

            if (matched == 0)
            {
                continue;
            }

            int width = IndexWidth(t, idx);
            if (matched > bestMatched || (matched == bestMatched && width <= bestWidth))
            {
                best = idx;
                bestWidth = width;
                bestMatched = matched;
                bestReverse = dir < 0;
            }
        }

        if (best is null)
        {
            return false;
        }

        plan = new AccessPlan { Kind = PlanKind.Index, Index = best, Reverse = bestReverse };
        partial = skippedConstant || bestMatched < wantCol.Count;
        return true;
    }

    /// <summary>The result columns as ascending ORDER BY terms, constants left out, up to the first column that is neither a plain column nor a constant.</summary>
    private static ImmutableArray<OrderingTerm> AllColumnsOrder(List<(Expr Expr, string Name)> cols)
    {
        var terms = ImmutableArray.CreateBuilder<OrderingTerm>();
        foreach ((Expr e, _) in cols)
        {
            Expr x = StripParens(e);
            if (x is LiteralExpr)
            {
                continue;
            }

            if (x is not ColumnRef)
            {
                // Observed: the columns before an expression still decide the scan order (an index on them serves as a partial order);
                // the expression and what follows it are sorted within equal prefixes.
                break;
            }

            terms.Add(new OrderingTerm(x, SortOrder.Asc, NullsOrder.None));
        }

        return terms.ToImmutable();
    }

    private bool OrderIsRowid(ImmutableArray<OrderingTerm> order, Scope scope)
        => OrderIsKeyOrder(order, scope) && _planByCursor.TryGetValue(scope.Sources[0], out AccessPlan? p2) && p2 is { Kind: PlanKind.Scan or PlanKind.RowidEq or PlanKind.RowidRange };

    // ------------------------------------------------------------------ rows leaving a core

    /// <summary>Computes one result row (and its ORDER BY keys) from the current state of the loops and hands it on.</summary>
    private void EmitProduce(Pipe p, ImmutableArray<OrderingTerm> order)
    {
        // Observed: without a sorter or DISTINCT the rows an OFFSET skips are not evaluated, so an error in them is never raised.
        int offRow = p.OffReg != 0 ? p.OffReg : p.Dest.OffReg;
        bool earlyOffset = p.Sorter < 0 && p.DistinctSet < 0 && offRow != 0;
        int skipRow = NewLabel();
        if (earlyOffset)
        {
            Emit(Op.IfPos, offRow, skipRow, 1);
        }

        for (int i = 0; i < p.N; i++)
        {
            CompileInto(p.Cols[i].Expr, p.ResultBase + i);
        }

        if (p.Sorter >= 0)
        {
            for (int k = 0; k < p.KeyTarget.Length; k++)
            {
                if (p.KeyTarget[k] >= 0)
                {
                    Emit(Op.Copy, p.ResultBase + p.KeyTarget[k], p.KeyBase + k);
                }
                else
                {
                    CompileInto(order[k].Expr, p.KeyBase + k);
                }
            }
        }

        int skip = NewLabel();
        if (p.DistinctSet >= 0)
        {
            Emit(Op.SetFound, p.DistinctSet, skip, p.ResultBase, null, p.N);
            Emit(Op.SetAdd, p.DistinctSet, p.ResultBase, p.N);
        }

        if (p.Sorter >= 0)
        {
            Emit(Op.SorterInsert, p.Sorter, p.KeyBase, p.KeyTarget.Length + p.N);
        }
        else
        {
            EmitDest(p.Dest, p.ResultBase, p.N, p.LimReg, p.OffReg, p.End, 0, earlyOffset);
        }

        Mark(skip);
        Mark(skipRow);
    }

    private void EmitSortedOutput(Pipe p)
    {
        int sortEnd = NewLabel(), again = NewLabel(), next = NewLabel();
        Emit(Op.SorterSort, p.Sorter, sortEnd);
        Mark(again);
        Emit(Op.SorterData, p.Sorter, p.KeyBase, p.KeyTarget.Length + p.N);
        EmitDest(p.Dest, p.ResultBase, p.N, p.LimReg, p.OffReg, p.End, next);
        Mark(next);
        Emit(Op.SorterNext, p.Sorter, again);
        Mark(sortEnd);
    }

    /// <summary>Delivers a row to its destination, applying OFFSET and LIMIT.</summary>
    private void EmitDest(Dest d, int bas, int n, int limReg, int offReg, int end, int skipLabel, bool offsetDone = false)
    {
        int lim = limReg != 0 ? limReg : d.LimReg;
        int off = offReg != 0 ? offReg : d.OffReg;
        int endL = limReg != 0 || end != 0 && d.LimReg == 0 ? end : d.End;
        int skip = skipLabel != 0 ? skipLabel : NewLabel();
        if (off != 0 && !offsetDone)
        {
            Emit(Op.IfPos, off, skip, 1);
        }

        switch (d.Kind)
        {
            case DestKind.Output:
            {
                int ob = OutBase(n);
                if (ob != bas)
                {
                    for (int i = 0; i < n; i++)
                    {
                        Emit(Op.Copy, bas + i, ob + i);
                    }
                }

                Emit(Op.ResultRow, ob, n);
                break;
            }

            case DestKind.Eph:
                Emit(Op.EphInsert, d.Cursor, bas, n);
                break;
            case DestKind.EphDistinct:
                Emit(Op.SetFound, d.Set, skip, bas, null, n);
                Emit(Op.SetAdd, d.Set, bas, n);
                Emit(Op.EphInsert, d.Cursor, bas, n);
                break;
            case DestKind.Set:
                if (d.Aff != Affinity.None)
                {
                    for (int i = 0; i < n; i++)
                    {
                        Emit(Op.CompareAffinity, bas + i, 0, 0, null, (int)d.Aff);
                    }
                }

                Emit(Op.SetAdd, d.Set, bas, n, null, d.Replace ? 1 : 0);
                break;
            case DestKind.Scalar:
                Emit(Op.Copy, bas, d.Reg);
                Emit(Op.Goto, 0, end != 0 ? end : d.End);
                break;
            case DestKind.Exists:
                Emit(Op.Integer, 0, d.Reg, 0, 1L);
                Emit(Op.Goto, 0, end != 0 ? end : d.End);
                break;
        }

        if (lim != 0 && d.Kind is not (DestKind.Scalar or DestKind.Exists))
        {
            Emit(Op.DecrJumpZero, lim, endL);
        }

        if (skipLabel == 0)
        {
            Mark(skip);
        }
    }

    private void EmitCompoundOutput(int cur, SelectMeta meta, ImmutableArray<OrderingTerm> order, Dest dest, int limReg, int offReg, int end)
    {
        int n = meta.Names.Length;
        int endLoop = NewLabel(), loop = NewLabel(), next = NewLabel();
        if (order.IsDefaultOrEmpty)
        {
            int regs = dest.Kind == DestKind.Output ? OutBase(n) : NewRegs(n);
            Emit(Op.ERewind, cur, endLoop);
            Mark(loop);
            for (int c = 0; c < n; c++)
            {
                Emit(Op.EColumn, cur, c, regs + c);
            }

            EmitDest(dest, regs, n, limReg, offReg, end, next);
            Mark(next);
            Emit(Op.ENext, cur, loop);
            Mark(endLoop);
            return;
        }

        int nk = order.Length;
        var terms = new SortTerm[nk];
        var targets = new int[nk];
        for (int k = 0; k < nk; k++)
        {
            OrderingTerm ot = order[k];
            Expr e = ot.Expr;
            CollKind? explicitColl = null;
            bool collated = false;
            long? pos = ConstPosition(e, ref collated);
            int target = -1;
            if (pos is long pn)
            {
                if (pn < 1 || pn > n)
                {
                    throw Err($"{Ordinal(k + 1)} ORDER BY term out of range - should be between 1 and {n}");
                }

                target = (int)pn - 1;
                if (collated)
                {
                    explicitColl = CollOf(e, out _, out _);
                }
            }
            else
            {
                Expr inner = e;
                for (; ; )
                {
                    if (inner is ParenExpr pe)
                    {
                        inner = pe.Inner;
                    }
                    else if (inner is CollateExpr ce)
                    {
                        explicitColl ??= ParseColl(ce.Collation.Text);
                        inner = ce.Operand;
                    }
                    else
                    {
                        break;
                    }
                }

                if (inner is ColumnRef { Schema: null, Table: null } cr)
                {
                    for (int i = 0; i < n; i++)
                    {
                        if (AsciiComparer.Instance.Equals(meta.Names[i], cr.Column.Text))
                        {
                            target = i;
                            break;
                        }
                    }
                }
            }

            if (target < 0)
            {
                throw Err($"{Ordinal(k + 1)} ORDER BY term does not match any column in the result set");
            }

            targets[k] = target;
            bool desc = ot.Order == SortOrder.Desc;
            bool nullsFirst = ot.Nulls == NullsOrder.None ? !desc : ot.Nulls == NullsOrder.First;
            terms[k] = new SortTerm(desc, nullsFirst, explicitColl ?? meta.Coll[target]);
        }

        int sorter = NewSorter();
        Emit(Op.SorterOpen, sorter, 0, 0, terms);
        int blk = NewRegs(nk + n);
        Emit(Op.ERewind, cur, endLoop);
        Mark(loop);
        for (int c = 0; c < n; c++)
        {
            Emit(Op.EColumn, cur, c, blk + nk + c);
        }

        for (int k = 0; k < nk; k++)
        {
            Emit(Op.Copy, blk + nk + targets[k], blk + k);
        }

        Emit(Op.SorterInsert, sorter, blk, nk + n);
        Emit(Op.ENext, cur, loop);
        Mark(endLoop);
        int sortEnd = NewLabel(), again = NewLabel();
        Emit(Op.SorterSort, sorter, sortEnd);
        Mark(again);
        Emit(Op.SorterData, sorter, blk, nk + n);
        EmitDest(dest, blk + nk, n, limReg, offReg, end, next);
        Mark(next);
        Emit(Op.SorterNext, sorter, again);
        Mark(sortEnd);
    }

    // ------------------------------------------------------------------ result columns

    // Expanding * looks every column up by schema, alias and name; two FROM items with the same alias that both have the column make that lookup
    // ambiguous, and the message spells the full path (a subquery has no schema and shows *). Tables of one schema only exist here: main.
    private static void CheckStarColumn(Scope scope, Source s, int col)
    {
        string name = s.Table.Columns[col].Name;
        foreach (Source o in scope.Sources)
        {
            if (ReferenceEquals(o, s) || (o.Kind == SourceKind.Table) != (s.Kind == SourceKind.Table) || !AsciiComparer.Instance.Equals(o.Alias, s.Alias))
            {
                continue;
            }

            for (int j = 0; j < o.Table.Columns.Length; j++)
            {
                if (!o.Hidden.Contains(j) && AsciiComparer.Instance.Equals(o.Table.Columns[j].Name, name))
                {
                    throw Err("ambiguous column name: " + (s.Kind == SourceKind.Table ? "main" : "*") + "." + s.Alias + "." + name);
                }
            }
        }
    }

    private static void ExpandStar(StarColumn star, Scope scope, List<(Expr Expr, string Name)> cols)
    {
        if (scope.Sources.Count == 0)
        {
            throw Err("no tables specified");
        }

        bool any = false;
        foreach (Source s in scope.Sources)
        {
            if (star.Table is not null && !AsciiComparer.Instance.Equals(star.Table.Text, s.Alias))
            {
                continue;
            }

            any = true;
            for (int i = 0; i < s.Table.Columns.Length; i++)
            {
                if ((star.Table is null && s.Hidden.Contains(i)) || (s.Module is not null && i >= s.Module.HiddenFrom) || s.Table.Columns[i].Hidden)
                {
                    continue;
                }

                CheckStarColumn(scope, s, i);

                // A column merged by USING in a RIGHT or FULL join reads as the coalesced value, which only an unqualified reference gets.
                bool merged = star.Table is null && s.Coalesce.ContainsKey(i);

                // A table and a subquery sharing an alias are told apart the way the reference does, by schema (a subquery has none).
                Name? schema = !merged && scope.Sources.Any(o => (o.Kind == SourceKind.Table) != (s.Kind == SourceKind.Table) && AsciiComparer.Instance.Equals(o.Alias, s.Alias))
                    ? (s.Kind == SourceKind.Table ? new Name("main") : SubquerySchema) : null;
                cols.Add((new ColumnRef(schema, merged ? null : new Name(s.Alias), new Name(s.Table.Columns[i].Name)), s.Table.Columns[i].Name));
            }
        }

        if (!any)
        {
            throw Err("no such table: " + star.Table!.Text);
        }
    }
}
