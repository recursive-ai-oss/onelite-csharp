using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite.Planning;

internal sealed class AggPlan
{
    internal List<FunctionCall> Calls = [];
    internal Dictionary<FunctionCall, int> Index = new(ReferenceEqualityComparer.Instance);
    internal int[] ResultRegs = [];
}

internal sealed partial class Compiler
{
    private AggPlan? _aggPlan;
    private bool _aggDiscover;

    // ------------------------------------------------------------------ aggregates

    /// <summary>
    /// Walks the expressions evaluated once per group (result columns, HAVING, ORDER BY) without generating usable code, and records the
    /// aggregate calls they hold and the columns they read.
    /// </summary>
    private void PostDiscover(IEnumerable<Expr> colExprs, Expr? having, ImmutableArray<OrderingTerm> order, List<(Expr Expr, string Name)> cols, SimpleSelect core, bool dry)
    {
        _ = core;
        // A subquery inside a window query's expressions runs this too: put back what the enclosing query was recording.
        Dictionary<(Source, int), int>? outerRec = _recordCols;
        AggPlan? outerPlan = _aggPlan;
        bool outerDiscover = _aggDiscover;
        _aggPlan = new AggPlan();
        _aggDiscover = true;
        _recordCols = [];
        try
        {
            foreach (Expr e in colExprs)
            {
                Compile(e);
            }

            if (_scope is not null)
            {
                _scope.AliasesVisible = true;
            }

            if (having is not null)
            {
                Compile(having);
            }

            for (int k = 0; k < order.Length; k++)
            {
                if (ResolveOrderTerm(order[k].Expr, cols, k) < 0)
                {
                    Compile(order[k].Expr);
                }
            }
        }
        finally
        {
            _aggDiscover = outerDiscover;
            if (dry)
            {
                _recordCols = outerRec;
                _aggPlan = outerPlan;
            }
        }
    }

    /// <summary>An aggregate call inside an expression evaluated per group.</summary>
    private void CompileAggregateRef(FunctionCall f, AggKind kind, int dest)
    {
        string name = f.Name.Text;
        if (_aggPlan is null)
        {
            throw Err("misuse of aggregate function " + name + "()");
        }

        int nargs = f.Args.IsDefaultOrEmpty ? 0 : f.Args.Length;
        if (_aggDiscover)
        {
            if (!_aggPlan.Index.ContainsKey(f))
            {
                bool distinct = f.Quantifier == CallQuantifier.Distinct;
                if (distinct && (nargs != 1 || kind == AggKind.CountStar))
                {
                    throw Err("DISTINCT aggregates must have exactly one argument");
                }

                bool arityOk = kind switch
                {
                    AggKind.CountStar => f.Star || nargs == 0,
                    AggKind.Count => nargs == 1,
                    AggKind.GroupConcat => AsciiComparer.Instance.Equals(name, "string_agg") ? nargs == 2 : nargs is 1 or 2,
                    AggKind.JsonGroupObject or AggKind.JsonbGroupObject => nargs == 2,
                    AggKind.User => true,
                    _ => nargs == 1,
                };
                if (!arityOk)
                {
                    throw Err("wrong number of arguments to function " + name + "()");
                }

                foreach (Expr a in f.Args)
                {
                    if (FindAggregate(a) is { } inner)
                    {
                        throw Err("misuse of aggregate function " + inner.Name.Text + "()");
                    }

                    RejectWindowCalls(a);

                    Compile(a);
                }

                if (f.Filter is not null)
                {
                    Compile(f.Filter);
                }

                foreach (OrderingTerm ot in f.OrderBy)
                {
                    Compile(ot.Expr);
                }

                _aggPlan.Index[f] = _aggPlan.Calls.Count;
                _aggPlan.Calls.Add(f);
            }

            Emit(Op.Null, 0, dest);
            return;
        }

        Emit(Op.Copy, _aggPlan.ResultRegs[_aggPlan.Index[f]], dest);
    }

    private void EmitAggregate(SimpleSelect core, Scope scope, List<Expr> joinPool, Expr[] groupExprs, ImmutableArray<OrderingTerm> order, Pipe pipe, bool orderedGroups)
    {
        List<(Expr Expr, string Name)> cols = pipe.Cols;
        AggPlan? plan = null;
        Dictionary<(Source, int), int>? keys = null;
        Dictionary<(Source, int), int>? outerRec = _recordCols;
        AggPlan? outerPlan = _aggPlan;
        DryRun(() =>
        {
            PostDiscover(cols.Select(c => c.Expr), core.Having, order, cols, core, dry: false);
            plan = _aggPlan;
            keys = _recordCols;
            _aggPlan = outerPlan;
            _recordCols = outerRec;
        });
        plan!.ResultRegs = [];
        List<(Source Src, int Col)> slots = [.. keys!.OrderBy(kv => kv.Value).Select(kv => (kv.Key.Item1, kv.Key.Item2))];
        int m = slots.Count, g = groupExprs.Length;
        int kb = NewRegs(g + m);
        int cur = kb + g;
        int bare = NewRegs(Math.Max(m, 1));
        int a = plan.Calls.Count;
        plan.ResultRegs = new int[a];
        for (int i = 0; i < a; i++)
        {
            plan.ResultRegs[i] = NewReg();
        }

        var curMap = new Dictionary<(Source, int), int>();
        var bareMap = new Dictionary<(Source, int), int>();
        for (int i = 0; i < m; i++)
        {
            curMap[slots[i]] = cur + i;
            bareMap[slots[i]] = bare + i;
        }

        // With min() or max() in the query, bare columns come from the row that last set the last one of them in the query (lang_select "Bare columns in an aggregate query").
        int minMax = plan.Calls.Count(c => AggregateKind(c) is AggKind.Min or AggKind.Max);
        bool bareOnChange = minMax >= 1;

        // Observed: with several of them only the last one in the query decides the row (select a, min(c), max(b) takes the row of max(b)).
        int lastMinMax = -1;
        for (int i = 0; i < plan.Calls.Count; i++)
        {
            if (AggregateKind(plan.Calls[i]) is AggKind.Min or AggKind.Max)
            {
                lastMinMax = i;
            }
        }

        int changed = bareOnChange ? NewReg() : 0;
        int hasBare = NewReg();
        var specIdx = new int[a];
        var orderCount = new int[a];
        for (int i = 0; i < a; i++)
        {
            FunctionCall f = plan.Calls[i];
            AggKind kind = AggregateKind(f)!.Value;
            var spec = new AggSpec { Kind = kind, Distinct = f.Quantifier == CallQuantifier.Distinct, NArgs = f.Star ? 0 : f.Args.Length };
            if (kind == AggKind.User)
            {
                spec.UserFactory = t_owner!.FindUserAggregate(f.Name.Text, spec.NArgs);
            }

            if (!f.Args.IsDefaultOrEmpty && f.Args.Length > 0)
            {
                spec.Coll = CollOf(f.Args[0], out _);
            }

            if (!f.OrderBy.IsDefaultOrEmpty)
            {
                spec.Order = [.. f.OrderBy.Select(ot =>
                {
                    bool desc = ot.Order == SortOrder.Desc;
                    return new SortTerm(desc, ot.Nulls == NullsOrder.None ? !desc : ot.Nulls == NullsOrder.First, CollOf(ot.Expr, out _));
                })];
                orderCount[i] = f.OrderBy.Length;
            }

            specIdx[i] = _aggSpecs.Count;
            _aggSpecs.Add(spec);
        }

        int ret = NewReg();
        int mainStart = NewLabel(), post = NewLabel();
        Emit(Op.Goto, 0, mainStart);

        // The per-group output, as a subroutine (called when a group ends).
        Mark(post);
        for (int i = 0; i < a; i++)
        {
            Emit(Op.AggFinal, specIdx[i], plan.ResultRegs[i]);
        }

        Dictionary<(Source, int), int>? outerMap = _colMap;
        AggPlan? outerAgg = _aggPlan;
        _aggPlan = plan;
        _colMap = bareMap;
        scope.AliasesVisible = true;

        // Observed: a result expression that is a GROUP BY expression shows the value of the group's first row, even when the bare columns
        // come from the row of a min() or max().
        int firstKey = NewRegs(Math.Max(g, 1));
        int hasFirst = NewReg();
        if (g > 0)
        {
            _groupKeyRegs = [];
            for (int j = 0; j < g; j++)
            {
                // A plain column is read like any bare column (from the min()/max() row); only a computed key is copied from the group's key.
                if (StripParens(groupExprs[j]) is not ColumnRef || !TryResolveColumn((ColumnRef)StripParens(groupExprs[j]), out _, out _))
                {
                    _groupKeyRegs.Add((groupExprs[j], firstKey + j));
                }

                if (StripParens(groupExprs[j]) is ColumnRef { Schema: null, Table: null } gc && !TryResolveColumn(gc, out _, out _))
                {
                    // GROUP BY a result alias: the aliased result expression is the key
                    for (int ci = 0; ci < cols.Count; ci++)
                    {
                        if (_aliases.TryGetValue(ci, out string? alias) && AsciiComparer.Instance.Equals(alias, gc.Column.Text))
                        {
                            _groupKeyRegs.Add((cols[ci].Expr, firstKey + j));
                        }
                    }
                }
            }
        }

        int skipGroup = NewLabel();
        if (core.Having is not null)
        {
            int hr = Compile(core.Having);
            Emit(Op.IfNot, hr, skipGroup, 1);
        }

        if (_win is not null)
        {
            _win.Slots = slots;
            _win.NAgg = a;
            _win.Agg = plan;
            WindowLayout(_win);
            WindowFeed(_win, [.. Enumerable.Range(bare, m)], plan.ResultRegs);
        }
        else
        {
            EmitProduce(pipe, order);
        }

        Mark(skipGroup);
        Emit(Op.Return, ret);
        _colMap = outerMap;
        _aggPlan = outerAgg;
        _groupKeyRegs = null;

        void Steps()
        {
            _colMap = curMap;
            _aggPlan = plan;
            try
            {
                if (changed != 0)
                {
                    Emit(Op.Integer, 0, changed, 0, 0L);
                }

                for (int i = 0; i < a; i++)
                {
                    FunctionCall f = plan.Calls[i];
                    int skip = NewLabel();
                    if (f.Filter is not null)
                    {
                        int fr = Compile(f.Filter);
                        Emit(Op.IfNot, fr, skip, 1);
                    }

                    int nargs = f.Star || f.Args.IsDefaultOrEmpty ? 0 : f.Args.Length;
                    int total = orderCount[i] + nargs;
                    int bas = NewRegs(Math.Max(total, 1));
                    for (int k = 0; k < orderCount[i]; k++)
                    {
                        CompileInto(f.OrderBy[k].Expr, bas + k);
                    }

                    for (int k = 0; k < nargs; k++)
                    {
                        CompileInto(f.Args[k], bas + orderCount[i] + k);
                    }

                    bool isMinMax = AggregateKind(f) is AggKind.Min or AggKind.Max;
                    if (isMinMax && bareOnChange && i == lastMinMax)
                    {
                        int one = NewReg();
                        Emit(Op.AggStep, specIdx[i], bas, total, null, one);
                        Emit(Op.Or, changed, one, changed);
                    }
                    else
                    {
                        Emit(Op.AggStep, specIdx[i], bas, total, null, 0);
                    }

                    Mark(skip);
                }

                int noBare = NewLabel(), doCopy = NewLabel();
                if (bareOnChange)
                {
                    // The first row of a group sets the bare columns; after that only a row that changes a min() or max() does (observed).
                    Emit(Op.IfNot, hasBare, doCopy, 1);
                    Emit(Op.IfNot, changed, noBare, 1);
                    Mark(doCopy);
                    Emit(Op.Integer, 0, hasBare, 0, 1L);
                }
                else
                {
                    // Without a min() or max() the bare columns come from the first row of the group (observed).
                    Emit(Op.If, hasBare, noBare, 0);
                    Emit(Op.Integer, 0, hasBare, 0, 1L);
                }

                for (int i = 0; i < m; i++)
                {
                    Emit(Op.Copy, cur + i, bare + i);
                }

                Mark(noBare);
            }
            finally
            {
                _colMap = outerMap;
                _aggPlan = outerAgg;
            }
        }

        void LoadSlots(int target)
        {
            for (int i = 0; i < m; i++)
            {
                EmitSourceColumn(slots[i].Src, slots[i].Col, target + i, false);
            }
        }

        void ResetAll()
        {
            for (int i = 0; i < a; i++)
            {
                Emit(Op.AggReset, specIdx[i]);
            }

            for (int i = 0; i < m; i++)
            {
                Emit(Op.Null, 0, bare + i);
            }

            Emit(Op.Integer, 0, hasBare, 0, 0L);
            Emit(Op.Integer, 0, hasFirst, 0, 0L);
        }

        Mark(mainStart);
        ResetAll();
        if (g == 0)
        {
            EmitNest(scope, core.Where, joinPool, () =>
            {
                LoadSlots(cur);
                Steps();
            });
            Emit(Op.Gosub, ret, post);
            return;
        }

        var groupColl = new CollKind[g];
        for (int j = 0; j < g; j++)
        {
            groupColl[j] = CollOf(groupExprs[j], out _);
        }

        int have = NewReg();
        int prev = NewRegs(g);
        Emit(Op.Integer, 0, have, 0, 0L);

        // One row of the grouped stream (keys in kb, columns in cur): ends the running group when the keys changed, then feeds the aggregates.
        void GroupRow()
        {
            int same = NewLabel(), newGroup = NewLabel();
            Emit(Op.IfNot, have, same, 1);
            int diff = NewReg();
            for (int j = 0; j < g; j++)
            {
                Emit(Op.IsNot, prev + j, kb + j, diff, null, CmpFlags(Affinity.None, groupColl[j]));
                Emit(Op.If, diff, newGroup, 0);
            }

            Emit(Op.Goto, 0, same);
            Mark(newGroup);
            Emit(Op.Gosub, ret, post);
            ResetAll();
            Mark(same);
            int notFirst = NewLabel();
            Emit(Op.If, hasFirst, notFirst, 0);
            for (int j = 0; j < g; j++)
            {
                Emit(Op.Copy, kb + j, firstKey + j);
            }

            Emit(Op.Integer, 0, hasFirst, 0, 1L);
            Mark(notFirst);
            for (int j = 0; j < g; j++)
            {
                Emit(Op.Copy, kb + j, prev + j);
            }

            Emit(Op.Integer, 0, have, 0, 1L);
            Steps();
        }

        if (orderedGroups)
        {
            // The rows already arrive in group order (an index or the rowid): no sorter.
            EmitNest(scope, core.Where, joinPool, () =>
            {
                for (int j = 0; j < g; j++)
                {
                    CompileInto(groupExprs[j], kb + j);
                }

                LoadSlots(kb + g);
                GroupRow();
            });
        }
        else
        {
            int sorter = NewSorter();
            var terms = new SortTerm[g];
            for (int j = 0; j < g; j++)
            {
                terms[j] = new SortTerm(false, true, groupColl[j]);
            }

            Emit(Op.SorterOpen, sorter, 0, 0, terms);
            EmitNest(scope, core.Where, joinPool, () =>
            {
                for (int j = 0; j < g; j++)
                {
                    CompileInto(groupExprs[j], kb + j);
                }

                LoadSlots(kb + g);
                Emit(Op.SorterInsert, sorter, kb, g + m);
            });
            int afterSort = NewLabel(), loop = NewLabel();
            Emit(Op.SorterSort, sorter, afterSort);
            Mark(loop);
            Emit(Op.SorterData, sorter, kb, g + m);
            GroupRow();
            Emit(Op.SorterNext, sorter, loop);
            Mark(afterSort);
        }

        int doneGroups = NewLabel();
        Emit(Op.IfNot, have, doneGroups, 1);
        Emit(Op.Gosub, ret, post);
        Mark(doneGroups);
    }

    // ------------------------------------------------------------------ subqueries in expressions

    /// <summary>The state of the grouped query around a subquery in one of its expressions, kept while the subquery is compiled.</summary>
    private sealed record AggSave(Dictionary<(Source, int), int>? RecordCols, Dictionary<(Source, int), int>? ColMap, AggPlan? Plan, bool Discover,
        List<(Expr Expr, int Reg)>? GroupKeys, HashSet<Source> Sources);

    private readonly List<AggSave> _aggSaves = [];

    private int BeginSubquery(out Probe probe, out int guard, PlanNodeKind kind = PlanNodeKind.Subquery, string title = "", string detail = "")
    {
        // The subquery has its own FROM items and its own aggregates; the columns of the grouped query it refers to are still read from the group.
        _aggSaves.Add(new AggSave(_recordCols, _colMap, _aggPlan, _aggDiscover, _groupKeyRegs, _scope is null ? [] : [.. _scope.Sources]));
        _recordCols = null;
        _colMap = null;
        _aggPlan = null;
        _aggDiscover = false;
        _groupKeyRegs = null;
        probe = new Probe { InnerDepth = NextDepth() };
        if (_plan is not null)
        {
            probe.PlanNode = _plan.Push(kind, title);
            probe.PlanNode.Detail = detail;
        }

        _probes.Add(probe);
        guard = Emit(Op.Once, _onceCount++, 0);
        return NewReg();
    }

    private void EndSubquery(Probe probe, int guard)
    {
        AggSave save = _aggSaves[^1];
        _aggSaves.RemoveAt(_aggSaves.Count - 1);
        _recordCols = save.RecordCols;
        _colMap = save.ColMap;
        _aggPlan = save.Plan;
        _aggDiscover = save.Discover;
        _groupKeyRegs = save.GroupKeys;
        _probes.Remove(probe);
        if (probe.PlanNode is { } pn)
        {
            pn.Runs = probe.Correlated ? "per outer row" : "once";
            _plan?.Pop(pn);
        }

        int skip = NewLabel();
        Mark(skip);
        Instr g = _code[guard];
        if (probe.Correlated)
        {
            _code[guard] = new Instr { Op = Op.Noop };
        }
        else
        {
            g.P2 = skip;
            _code[guard] = g;
        }
    }

    private void CompileScalarSubquery(SelectStatement sel, int dest)
    {
        int cache = BeginSubquery(out Probe probe, out int guard, PlanNodeKind.Subquery, "scalar");
        try
        {
            Emit(Op.Null, 0, cache);
            SelectMeta m = CompileSelectStmt(sel, new Dest { Kind = DestKind.Scalar, Reg = cache });
            if (m.Names.Length != 1)
            {
                throw Err($"sub-select returns {m.Names.Length} columns - expected 1");
            }
        }
        finally
        {
            EndSubquery(probe, guard);
        }

        Emit(Op.Copy, cache, dest);
    }

    private void CompileExists(SelectStatement sel, int dest)
    {
        int cache = BeginSubquery(out Probe probe, out int guard, PlanNodeKind.Subquery, "EXISTS");
        try
        {
            Emit(Op.Integer, 0, cache, 0, 0L);
            CompileSelectStmt(sel, new Dest { Kind = DestKind.Exists, Reg = cache });
        }
        finally
        {
            EndSubquery(probe, guard);
        }

        Emit(Op.Copy, cache, dest);
    }

    private static SelectStatement SelectAllFrom(QualifiedName table)
        => new(null, [new SimpleSelect(SelectQuantifier.None, [new StarColumn(null)], new FromClause([new FromItem(null, new NamedTable(table, null, null), null, ImmutableArray<Name>.Empty)]),
            null, ImmutableArray<Expr>.Empty, null, ImmutableArray<WindowDefinition>.Empty, ImmutableArray<OrderingTerm>.Empty, null)], ImmutableArray<CompoundOperator>.Empty);

    private void CompileInSelect(Expr operand, SelectStatement sel, bool negated, int dest)
    {
        int width = operand is RowValueExpr rv ? rv.Items.Length : 1;
        // The operand belongs to the enclosing query: resolve it before the subquery's probe opens, or its columns would make an
        // uncorrelated subquery look correlated and it would be rebuilt for every outer row.
        Affinity operandAff = width == 1 ? CmpAffOf(operand) : Affinity.None;
        bool hasColl = false;
        CollKind operandColl = width == 1 ? CollOf(operand, out hasColl) : CollKind.Binary;
        int cache = BeginSubquery(out Probe probe, out int guard, PlanNodeKind.Subquery, "IN (list of values)");
        int set = NewSet();
        var spec = new SetSpec();
        SelectMeta meta;
        try
        {
            Emit(Op.SetOpen, set, 0, 0, spec);
            var d = new Dest { Kind = DestKind.Set, Set = set, UseOperandAff = width == 1, OperandAff = operandAff, OperandColl = hasColl ? operandColl : null };
            meta = CompileSelectStmt(sel, d);
        }
        finally
        {
            EndSubquery(probe, guard);
        }

        _ = cache;
        if (meta.Names.Length != width)
        {
            throw Err($"sub-select returns {meta.Names.Length} columns - expected {width}");
        }

        if (width == 1)
        {
            CollKind oc = CollOf(operand, out bool explicitColl, out bool any);
            spec.Collations = [any ? oc : meta.Coll[0]];
            _ = explicitColl;
            int x = NewReg();
            CompileInto(operand, x);
            Affinity aff = Values.CompareAffinity(CmpAffOf(operand), meta.Aff[0]);
            if (aff != Affinity.None)
            {
                Emit(Op.CompareAffinity, x, 0, 0, null, (int)aff);
            }

            Emit(Op.InSet, set, x, dest, null, negated ? 1 : 0);
            return;
        }

        var items = ((RowValueExpr)operand).Items;
        var colls = new CollKind[width];
        for (int i = 0; i < width; i++)
        {
            CollOf(items[i], out bool a2, out bool any2);
            _ = a2;
            colls[i] = any2 ? CollOf(items[i], out _) : meta.Coll[i];
        }

        spec.Collations = colls;
        int xs = NewRegs(width);
        for (int i = 0; i < width; i++)
        {
            CompileInto(items[i], xs + i);
        }

        Emit(Op.InSet, set, xs, dest, null, (negated ? 1 : 0) | (width << 1));
    }

    // ------------------------------------------------------------------ row values

    private static bool IsRow(Expr e) => e is RowValueExpr;

    /// <summary>A comparison of row values: (a,b) = (c,d), and the ordering forms, which stop at the first pair that differs (lang_expr "Row Values").</summary>
    private void CompileRowCompare(BinaryOp op, Expr left, Expr right, int dest)
    {
        if (left is not RowValueExpr l || right is not RowValueExpr r || l.Items.Length != r.Items.Length)
        {
            if (left is RowValueExpr lx && right is RowValueExpr rx)
            {
                throw Err($"row value misused");
            }

            throw Err("row value misused");
        }

        int n = l.Items.Length;
        var a = new int[n];
        var b = new int[n];
        for (int i = 0; i < n; i++)
        {
            a[i] = Compile(l.Items[i]);
            b[i] = Compile(r.Items[i]);
        }

        int Flags(int i) => CmpFlags(Values.CompareAffinity(CmpAffOf(l.Items[i]), CmpAffOf(r.Items[i])), PairColl(l.Items[i], r.Items[i]));

        if (op is BinaryOp.Eq or BinaryOp.Ne or BinaryOp.Is or BinaryOp.IsNot)
        {
            Op cmp = op is BinaryOp.Eq or BinaryOp.Ne ? Op.Eq : Op.Is;
            int acc = NewReg();
            Emit(Op.Integer, 0, acc, 0, 1L);
            for (int i = 0; i < n; i++)
            {
                int t = NewReg();
                Emit(cmp, a[i], b[i], t, null, Flags(i));
                Emit(Op.And, acc, t, acc);
            }

            if (op is BinaryOp.Ne or BinaryOp.IsNot)
            {
                Emit(Op.Not, acc, acc);
            }

            Emit(Op.Copy, acc, dest);
            return;
        }

        Op strict = op is BinaryOp.Lt or BinaryOp.Le ? Op.Lt : Op.Gt;
        Op last = op switch { BinaryOp.Lt => Op.Lt, BinaryOp.Le => Op.Le, BinaryOp.Gt => Op.Gt, _ => Op.Ge };
        int done = NewLabel();
        int res = NewReg();
        for (int i = 0; i < n; i++)
        {
            if (i == n - 1)
            {
                Emit(last, a[i], b[i], res, null, Flags(i));
                break;
            }

            int nullLbl = NewLabel(), moreLbl = NewLabel();
            Emit(Op.IfNull, a[i], nullLbl);
            Emit(Op.IfNull, b[i], nullLbl);
            int eq = NewReg();
            Emit(Op.Eq, a[i], b[i], eq, null, Flags(i));
            Emit(Op.If, eq, moreLbl, 0);
            Emit(strict, a[i], b[i], res, null, Flags(i));
            Emit(Op.Goto, 0, done);
            Mark(nullLbl);
            Emit(Op.Null, 0, res);
            Emit(Op.Goto, 0, done);
            Mark(moreLbl);
        }

        Mark(done);
        Emit(Op.Copy, res, dest);
    }

    private void CompileInRows(InListExpr il, int dest)
    {
        var left = il.Operand;
        int acc = NewReg();
        Emit(Op.Integer, 0, acc, 0, 0L);
        foreach (Expr item in il.Items)
        {
            int t = NewReg();
            CompileRowCompare(BinaryOp.Eq, left, item, t);
            Emit(Op.Or, acc, t, acc);
        }

        if (il.Negated)
        {
            Emit(Op.Not, acc, acc);
        }

        Emit(Op.Copy, acc, dest);
    }
}
