using System.Collections.Immutable;
using System.Linq;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite.Planning;

/// <summary>
/// Window functions (windowfunctions). A SELECT core that calls them runs in stages, the way the reference does it: the rows the core
/// would produce (one per group in an aggregate query) are carried, each with the values the windows need, through one stage per
/// distinct PARTITION BY / ORDER BY. A stage sorts its input (not needed when it has neither), runs <see cref="WindowRun"/> over the
/// sorted rows and hands each finished row, now carrying the stage's results, to the next stage; the last stage's rows go through
/// the core's ordinary output path (DISTINCT, ORDER BY, LIMIT), where a window call is just a read of its result.
/// </summary>
internal sealed partial class Compiler
{
    private readonly List<WinStageSpec> _winStages = [];
    private WinCompile? _win;

    /// <summary>The FROM items whose columns a window query carries to its finished rows; null outside window queries.</summary>
    private Scope? _carryScope;

    private enum WinPhase { Discover, DiscoverPost, Feed, Emit }

    /// <summary>A window with its base windows folded in.</summary>
    private sealed class ResolvedWindow
    {
        internal ImmutableArray<Expr> Partition = ImmutableArray<Expr>.Empty;
        internal ImmutableArray<OrderingTerm> Order = ImmutableArray<OrderingTerm>.Empty;
        internal FrameSpec? Frame;
        internal string Key = "";
    }

    private sealed class WinCall
    {
        internal FunctionCall Call = null!;
        internal WinFn Fn;
        internal AggKind? Agg;
        internal ResolvedWindow Window = null!;
        internal string StageKey = "";

        /// <summary>A frame error raised once the call's own names are resolved (the reference reports a missing column first).</summary>
        internal string? LateError;
        internal FrameUnit Unit;
        internal FrameBoundKind Start, End;
        internal Expr? StartOff, EndOff;
        internal FrameExclude Exclude;
        internal int[] ArgSlots = [];
        internal int FilterSlot = -1, StartOffSlot = -1, EndOffSlot = -1, ResultSlot;
    }

    private sealed class WinStage
    {
        internal List<WinCall> Calls = [];
        internal ResolvedWindow Window = null!;
        internal string Key = "";
        internal int Id;
        internal int Sorter = -1;

        /// <summary>The rows already arrive in this stage's order (an index scan), so it neither sorts nor needs the sorter.</summary>
        internal bool Streamed;
        internal int[] KeySlots = [];
        internal SortTerm[] KeyTerms = [];
        internal int S;
        internal int Ret;
        internal int ConsumerLabel;
        internal int[] PartSlots = [];
        internal int[] OrderSlots = [];
    }

    private sealed class WinCompile
    {
        internal WinPhase Phase;
        internal Dictionary<string, WindowDefinition> Defs = new(AsciiComparer.Instance);
        internal List<WinCall> Calls = [];
        internal Dictionary<FunctionCall, WinCall> Index = new(ReferenceEqualityComparer.Instance);
        internal List<WinStage> Stages = [];

        /// <summary>The key of the window whose order the chosen index scan provides, or null.</summary>
        internal string? IndexKey;
        internal int C, W;

        /// <summary>The columns carried from the loops (or from the grouped row), in slot order.</summary>
        internal List<(Source Src, int Col)> Slots = [];

        internal int NAgg;
        internal AggPlan? Agg;
        internal Dictionary<(Source, int), int>? ColMap;
        internal bool Grouped;
    }

    // ------------------------------------------------------------------ names and shapes

    private static bool IsWindowOnly(string name, out WinFn fn, out int min, out int max)
    {
        (fn, min, max) = (WinFn.RowNumber, 0, 0);
        if (AsciiComparer.Instance.Equals(name, "row_number")) { fn = WinFn.RowNumber; return true; }
        if (AsciiComparer.Instance.Equals(name, "rank")) { fn = WinFn.Rank; return true; }
        if (AsciiComparer.Instance.Equals(name, "dense_rank")) { fn = WinFn.DenseRank; return true; }
        if (AsciiComparer.Instance.Equals(name, "percent_rank")) { fn = WinFn.PercentRank; return true; }
        if (AsciiComparer.Instance.Equals(name, "cume_dist")) { fn = WinFn.CumeDist; return true; }
        if (AsciiComparer.Instance.Equals(name, "ntile")) { fn = WinFn.Ntile; (min, max) = (1, 1); return true; }
        if (AsciiComparer.Instance.Equals(name, "lag")) { fn = WinFn.Lag; (min, max) = (1, 3); return true; }
        if (AsciiComparer.Instance.Equals(name, "lead")) { fn = WinFn.Lead; (min, max) = (1, 3); return true; }
        if (AsciiComparer.Instance.Equals(name, "first_value")) { fn = WinFn.FirstValue; (min, max) = (1, 1); return true; }
        if (AsciiComparer.Instance.Equals(name, "last_value")) { fn = WinFn.LastValue; (min, max) = (1, 1); return true; }
        if (AsciiComparer.Instance.Equals(name, "nth_value")) { fn = WinFn.NthValue; (min, max) = (2, 2); return true; }
        return false;
    }

    /// <summary>The first window call in an expression (not looking into subqueries), or null.</summary>
    private static FunctionCall? FindWindowCall(Expr e)
    {
        switch (e)
        {
            case FunctionCall f:
                if (f.Over is not null)
                {
                    return f;
                }

                foreach (Expr a in f.Args)
                {
                    if (FindWindowCall(a) is { } fa)
                    {
                        return fa;
                    }
                }

                return f.Filter is null ? null : FindWindowCall(f.Filter);
            case ParenExpr p:
                return FindWindowCall(p.Inner);
            case UnaryExpr u:
                return FindWindowCall(u.Operand);
            case BinaryExpr b:
                return FindWindowCall(b.Left) ?? FindWindowCall(b.Right);
            case CollateExpr c:
                return FindWindowCall(c.Operand);
            case CastExpr ce:
                return FindWindowCall(ce.Operand);
            case BetweenExpr bt:
                return FindWindowCall(bt.Operand) ?? FindWindowCall(bt.Low) ?? FindWindowCall(bt.High);
            case InListExpr il:
                return FindWindowCall(il.Operand) ?? il.Items.Select(FindWindowCall).FirstOrDefault(x => x is not null);
            case InSelectExpr isl:
                return FindWindowCall(isl.Operand);
            case NullTestExpr nt:
                return FindWindowCall(nt.Operand);
            case LikeExpr l:
                return FindWindowCall(l.Left) ?? FindWindowCall(l.Pattern) ?? (l.Escape is null ? null : FindWindowCall(l.Escape));
            case RowValueExpr rv:
                return rv.Items.Select(FindWindowCall).FirstOrDefault(x => x is not null);
            case CaseExpr ce:
                return (ce.Operand is null ? null : FindWindowCall(ce.Operand))
                    ?? ce.Whens.Select(w => FindWindowCall(w.When) ?? FindWindowCall(w.Then)).FirstOrDefault(x => x is not null)
                    ?? (ce.Else is null ? null : FindWindowCall(ce.Else));
            default:
                return null;
        }
    }

    /// <summary>
    /// Whether a frame offset names a column, calls a function or holds a subquery. Observed: such an offset is not an error to write
    /// but evaluates to NULL (nothing in it is even resolved), so the frame is refused when it is used.
    /// </summary>
    private static bool IsNullOffset(Expr e)
    {
        switch (e)
        {
            case ColumnRef { Schema: null, Table: null } cr when AsciiComparer.Instance.Equals(cr.Column.Text, "true") || AsciiComparer.Instance.Equals(cr.Column.Text, "false"):
                return false;   // the constants TRUE and FALSE, 1 and 0
            case ColumnRef or SubqueryExpr or ExistsExpr or InSelectExpr or FunctionCall or InTableExpr:
                return true;
            case ParenExpr p:
                return IsNullOffset(p.Inner);
            case UnaryExpr u:
                return IsNullOffset(u.Operand);
            case BinaryExpr b:
                return IsNullOffset(b.Left) || IsNullOffset(b.Right);
            case CollateExpr c:
                return IsNullOffset(c.Operand);
            case CastExpr ce:
                return IsNullOffset(ce.Operand);
            case BetweenExpr bt:
                return IsNullOffset(bt.Operand) || IsNullOffset(bt.Low) || IsNullOffset(bt.High);
            case InListExpr il:
                return IsNullOffset(il.Operand) || il.Items.Any(IsNullOffset);
            case NullTestExpr nt:
                return IsNullOffset(nt.Operand);
            case LikeExpr l:
                return IsNullOffset(l.Left) || IsNullOffset(l.Pattern) || (l.Escape is not null && IsNullOffset(l.Escape));
            case RowValueExpr rv:
                return rv.Items.Any(IsNullOffset);
            case CaseExpr ce:
                return (ce.Operand is not null && IsNullOffset(ce.Operand)) || ce.Whens.Any(w => IsNullOffset(w.When) || IsNullOffset(w.Then))
                    || (ce.Else is not null && IsNullOffset(ce.Else));
            default:
                return false;
        }
    }

    private static WinCompile NewWinCompile(SimpleSelect core)
    {
        var w = new WinCompile { Phase = WinPhase.Discover };
        // Observed: a name defined twice means the later definition.
        foreach (WindowDefinition d in core.Windows)
        {
            w.Defs[d.Name.Text] = d;
        }

        return w;
    }

    /// <summary>Errors for a window call anywhere a window function may not stand (WHERE, GROUP BY, HAVING, ON).</summary>
    private static void RejectWindowCalls(Expr? e)
    {
        if (e is not null && FindWindowCall(e) is { } w)
        {
            throw Err("misuse of window function " + w.Name.Text + "()");
        }
    }

    // ------------------------------------------------------------------ resolving a call

    private static ResolvedWindow ResolveWindow(WinCompile win, WindowReference over)
    {
        WindowSpec spec;
        if (over.Named is { } nm)
        {
            if (!win.Defs.TryGetValue(nm.Text, out WindowDefinition? d))
            {
                throw Err("no such window: " + nm.Text);
            }

            spec = d.Spec;
        }
        else
        {
            spec = over.Spec!;
        }

        return ResolveSpec(win, spec);
    }

    private static ResolvedWindow ResolveSpec(WinCompile win, WindowSpec s)
    {
        var r = new ResolvedWindow { Partition = s.PartitionBy.IsDefault ? ImmutableArray<Expr>.Empty : s.PartitionBy, Order = s.OrderBy.IsDefault ? ImmutableArray<OrderingTerm>.Empty : s.OrderBy, Frame = s.Frame };
        if (s.BaseName is { } bn)
        {
            if (!win.Defs.TryGetValue(bn.Text, out WindowDefinition? bd))
            {
                throw Err("no such window: " + bn.Text);
            }

            // Observed: a base window contributes what its own definition says, not what that definition inherits in turn.
            var b = new ResolvedWindow
            {
                Partition = bd.Spec.PartitionBy.IsDefault ? ImmutableArray<Expr>.Empty : bd.Spec.PartitionBy,
                Order = bd.Spec.OrderBy.IsDefault ? ImmutableArray<OrderingTerm>.Empty : bd.Spec.OrderBy,
                Frame = bd.Spec.Frame,
            };
            if (!r.Partition.IsEmpty && !b.Partition.IsEmpty)
            {
                throw Err("cannot override PARTITION clause of window: " + bn.Text);
            }

            if (!r.Order.IsEmpty && !b.Order.IsEmpty)
            {
                throw Err("cannot override ORDER BY clause of window: " + bn.Text);
            }

            if (b.Frame is not null)
            {
                throw Err("cannot override frame specification of window: " + bn.Text);
            }

            if (r.Partition.IsEmpty)
            {
                r.Partition = b.Partition;
            }

            if (r.Order.IsEmpty)
            {
                r.Order = b.Order;
            }
        }

        r.Key = string.Join(",", r.Partition.Select(e => SqlWriter.Write(e))) + "|" + string.Join(",", r.Order.Select(o => SqlWriter.Write(o.Expr) + "/" + o.Order + "/" + o.Nulls));
        return r;
    }


    /// <summary>The expressions a window call reads for each row (arguments, FILTER, PARTITION BY, ORDER BY), for working out which columns a query needs.</summary>
    private IEnumerable<Expr> WindowCallInputs(FunctionCall f)
    {
        foreach (Expr a in f.Args)
        {
            yield return a;
        }

        if (f.Filter is not null)
        {
            yield return f.Filter;
        }

        if (_win is not null && f.Over is not null)
        {
            ResolvedWindow w = ResolveWindow(_win, f.Over);
            foreach (Expr p in w.Partition)
            {
                yield return p;
            }

            foreach (OrderingTerm o in w.Order)
            {
                yield return o.Expr;
            }
        }
    }

    /// <summary>
    /// Errors the reference raises while parsing, before it looks at any table: DISTINCT or an ORDER BY inside a window function call,
    /// and a frame whose start comes after its end. They come in the order the text has them: result columns, ORDER BY, then WINDOW.
    /// </summary>
    private static void PreValidateWindows(SimpleSelect core)
    {
        static void Walk(Expr e)
        {
            switch (e)
            {
                case FunctionCall f:
                    foreach (Expr a in f.Args)
                    {
                        Walk(a);
                    }

                    if (f.Over is not null)
                    {
                        // Observed: the frame is parsed with the OVER clause, so its error comes before the call's DISTINCT error.
                        CheckFrameOrder(f.Over.Spec?.Frame);
                        if (f.Quantifier == CallQuantifier.Distinct)
                        {
                            throw Err("DISTINCT is not supported for window functions");
                        }

                        if (!f.OrderBy.IsDefaultOrEmpty)
                        {
                            throw Err("ORDER BY may not be used with non-aggregate " + f.Name.Text + "()");
                        }

                        CheckFrameOrder(f.Over.Spec?.Frame);
                    }

                    break;
                case ParenExpr p:
                    Walk(p.Inner);
                    break;
                case UnaryExpr u:
                    Walk(u.Operand);
                    break;
                case BinaryExpr b:
                    Walk(b.Left);
                    Walk(b.Right);
                    break;
                case CollateExpr c:
                    Walk(c.Operand);
                    break;
                case CastExpr ce:
                    Walk(ce.Operand);
                    break;
                case BetweenExpr bt:
                    Walk(bt.Operand);
                    Walk(bt.Low);
                    Walk(bt.High);
                    break;
                case CaseExpr ce:
                    if (ce.Operand is not null)
                    {
                        Walk(ce.Operand);
                    }

                    foreach (WhenClause w in ce.Whens)
                    {
                        Walk(w.When);
                        Walk(w.Then);
                    }

                    if (ce.Else is not null)
                    {
                        Walk(ce.Else);
                    }

                    break;
            }
        }

        foreach (ResultColumn rc in core.Columns)
        {
            if (rc is ExprColumn ec)
            {
                Walk(ec.Expr);
            }
        }

        if (!core.OrderBy.IsDefaultOrEmpty)
        {
            foreach (OrderingTerm o in core.OrderBy)
            {
                Walk(o.Expr);
            }
        }

        if (!core.Windows.IsDefaultOrEmpty)
        {
            var seen = new Dictionary<string, WindowDefinition>(AsciiComparer.Instance);
            foreach (WindowDefinition d in core.Windows)
            {
                CheckFrameOrder(d.Spec.Frame);
                if (d.Spec.BaseName is { } bn && seen.TryGetValue(bn.Text, out WindowDefinition? bd))
                {
                    if (d.Spec.PartitionBy.Length > 0 && bd.Spec.PartitionBy.Length > 0)
                    {
                        throw Err("cannot override PARTITION clause of window: " + bn.Text);
                    }

                    if (d.Spec.OrderBy.Length > 0 && bd.Spec.OrderBy.Length > 0)
                    {
                        throw Err("cannot override ORDER BY clause of window: " + bn.Text);
                    }

                    if (bd.Spec.Frame is not null)
                    {
                        throw Err("cannot override frame specification of window: " + bn.Text);
                    }
                }

                seen[d.Name.Text] = d;
            }
        }
    }

    private static void CheckFrameOrder(FrameSpec? fr)
    {
        if (fr is not null && BoundRank(fr.Start.Kind) > BoundRank(fr.End?.Kind ?? FrameBoundKind.CurrentRow))
        {
            throw Err("unsupported frame specification");
        }
    }

    private static int BoundRank(FrameBoundKind k) => k switch
    {
        FrameBoundKind.UnboundedPreceding => 0,
        FrameBoundKind.Preceding => 1,
        FrameBoundKind.CurrentRow => 2,
        FrameBoundKind.Following => 3,
        _ => 4,
    };

    private WinCall RegisterWindowCall(FunctionCall f)
    {
        WinCompile win = _win!;
        if (win.Index.TryGetValue(f, out WinCall? have))
        {
            return have;
        }

        string name = f.Name.Text;
        int nargs = f.Args.IsDefaultOrEmpty ? 0 : f.Args.Length;
        var call = new WinCall { Call = f };
        if (IsWindowOnly(name, out WinFn fn, out int min, out int max))
        {
            call.Fn = fn;
            if (f.Quantifier == CallQuantifier.Distinct)
            {
                throw Err("DISTINCT is not supported for window functions");
            }

            if (nargs < min || nargs > max || f.Star)
            {
                throw Err("wrong number of arguments to function " + name + "()");
            }

            if (f.Filter is not null)
            {
                throw Err("FILTER clause may only be used with aggregate window functions");
            }

            if (!f.OrderBy.IsDefaultOrEmpty)
            {
                throw Err("ORDER BY may not be used with non-aggregate " + name + "()");
            }
        }
        else
        {
            AggKind? kind = null;
            bool known = true;
            bool arity = true;
            if (AsciiComparer.Instance.Equals(name, "count"))
            {
                kind = f.Star || nargs == 0 ? AggKind.CountStar : AggKind.Count;
                arity = f.Star || nargs <= 1;
            }
            else if (AsciiComparer.Instance.Equals(name, "sum") || AsciiComparer.Instance.Equals(name, "total") || AsciiComparer.Instance.Equals(name, "avg"))
            {
                kind = AsciiComparer.Instance.Equals(name, "sum") ? AggKind.Sum : AsciiComparer.Instance.Equals(name, "total") ? AggKind.Total : AggKind.Avg;
                arity = !f.Star && nargs == 1;
            }
            else if (AsciiComparer.Instance.Equals(name, "min") || AsciiComparer.Instance.Equals(name, "max"))
            {
                if (nargs != 1 || f.Star)
                {
                    throw Err(name + "() may not be used as a window function");
                }

                kind = AsciiComparer.Instance.Equals(name, "min") ? AggKind.Min : AggKind.Max;
            }
            else if (AsciiComparer.Instance.Equals(name, "group_concat") || AsciiComparer.Instance.Equals(name, "string_agg"))
            {
                kind = AggKind.GroupConcat;
                arity = !f.Star && (AsciiComparer.Instance.Equals(name, "string_agg") ? nargs == 2 : nargs is 1 or 2);
            }
            else
            {
                known = FunctionTable.Find(name) is not null || FunctionTable.IsKnownUnsupported(name);
            }

            if (kind is null)
            {
                throw Err(known ? name + "() may not be used as a window function" : "no such function: " + name);
            }

            if (f.Quantifier == CallQuantifier.Distinct)
            {
                throw Err("DISTINCT is not supported for window functions");
            }

            if (!arity)
            {
                throw Err("wrong number of arguments to function " + name + "()");
            }

            if (!f.OrderBy.IsDefaultOrEmpty)
            {
                throw Err("ORDER BY may not be used with non-aggregate " + name + "()");
            }

            call.Fn = WinFn.Aggregate;
            call.Agg = kind;
        }

        call.Window = ResolveWindow(win, f.Over!);
        call.StageKey = StageKeyOf(call.Window, f.Name.Text);

        // The frame: the default is RANGE UNBOUNDED PRECEDING to CURRENT ROW, and a one-bound frame ends at the current row.
        FrameSpec? fr = call.Window.Frame;
        if (fr is null)
        {
            call.Unit = FrameUnit.Range;
            call.Start = FrameBoundKind.UnboundedPreceding;
            call.End = FrameBoundKind.CurrentRow;
        }
        else
        {
            call.Unit = fr.Unit;
            call.Start = fr.Start.Kind;
            call.End = fr.End?.Kind ?? FrameBoundKind.CurrentRow;
            bool framed = call.Fn is WinFn.Aggregate or WinFn.FirstValue or WinFn.LastValue or WinFn.NthValue;

            // Observed: the ranking, ntile, lag and lead functions ignore the frame, so its offsets are never evaluated or checked.
            call.StartOff = framed ? fr.Start.Offset : null;
            call.EndOff = framed ? fr.End?.Offset : null;
            call.Exclude = fr.Exclude;
            if (BoundRank(call.Start) > BoundRank(call.End))
            {
                throw Err("unsupported frame specification");
            }

            bool offsets = call.Start is FrameBoundKind.Preceding or FrameBoundKind.Following || call.End is FrameBoundKind.Preceding or FrameBoundKind.Following;
            if (call.Unit == FrameUnit.Range && offsets && call.Window.Order.Length != 1)
            {
                call.LateError = "RANGE with offset PRECEDING/FOLLOWING requires one ORDER BY expression";
            }
        }

        win.Index[f] = call;
        win.Calls.Add(call);
        return call;
    }

    /// <summary>Compiles the expressions a window call reads for each row; used to find errors and the columns they need.</summary>
    private void CompileWindowInternals(WinCall call)
    {
        WinCompile win = _win!;
        WinPhase saved = win.Phase;
        win.Phase = WinPhase.Feed;
        try
        {
            FunctionCall f = call.Call;
            foreach (Expr a in f.Args)
            {
                if (FindAggregate(a) is not null && !_aggDiscover && _aggPlan is null)
                {
                    throw Err("misuse of aggregate function " + FindAggregate(a)!.Name.Text + "()");
                }

                Compile(a);
            }

            if (f.Filter is not null)
            {
                Compile(f.Filter);
            }

            foreach (Expr p in call.Window.Partition)
            {
                Compile(p);
            }

            foreach (OrderingTerm o in call.Window.Order)
            {
                Compile(o.Expr);
            }

            if (call.LateError is not null)
            {
                throw Err(call.LateError);
            }

            if (call.StartOff is not null)
            {
                CompileOffsetInto(call.StartOff, 0);
            }

            if (call.EndOff is not null)
            {
                CompileOffsetInto(call.EndOff, 0);
            }
        }
        finally
        {
            win.Phase = saved;
        }
    }

    /// <summary>A frame offset: compiled into <paramref name="dest"/>, or only checked when <paramref name="dest"/> is 0.</summary>
    private void CompileOffsetInto(Expr e, int dest)
    {
        bool isNull = IsNullOffset(e);
        if (dest == 0)
        {
            if (!isNull)
            {
                Compile(e);
            }

            return;
        }

        if (isNull)
        {
            Emit(Op.Null, 0, dest);
        }
        else
        {
            CompileInto(e, dest);
        }
    }

    private void CompileWindowCall(FunctionCall f, int dest)
    {
        string name = f.Name.Text;
        WinCompile? win = _win;
        if (win is null || win.Phase == WinPhase.Feed)
        {
            throw Err("misuse of window function " + name + "()");
        }

        switch (win.Phase)
        {
            case WinPhase.Discover:
            {
                WinCall call = RegisterWindowCall(f);
                CompileWindowInternals(call);
                Emit(Op.Null, 0, dest);
                return;
            }

            case WinPhase.DiscoverPost:
                RegisterWindowCall(f);
                Emit(Op.Null, 0, dest);
                return;
            default:
                Emit(Op.Copy, win.C + win.Index[f].ResultSlot, dest);
                return;
        }
    }

    // ------------------------------------------------------------------ planning the stages

    /// <summary>
    /// Groups the calls into stages and opens them. The reference runs the windows last-written first, so the rows leave in the order
    /// of the first one written; calls with the same PARTITION BY and ORDER BY share a stage.
    /// </summary>
    private void WindowSetup(WinCompile win)
    {
        // Observed: windows that are written the same (partition, order and frame) are one stage, and it stands where its first call was
        // written; the stages then run last-written first.
        var order = new List<string>();
        foreach (WinCall c in win.Calls)
        {
            string key = c.StageKey;
            if (!order.Contains(key))
            {
                order.Add(key);
            }
        }

        for (int i = order.Count - 1; i >= 0; i--)
        {
            WinCall first = win.Calls.First(c => c.StageKey == order[i]);
            win.Stages.Add(new WinStage { Window = first.Window, Key = first.StageKey, Id = _winStages.Count, Ret = 0 });
            _winStages.Add(new WinStageSpec());
        }

        for (int i = win.Calls.Count - 1; i >= 0; i--)
        {
            WinCall c = win.Calls[i];
            win.Stages.Find(s => s.Key == c.StageKey)!.Calls.Add(c);
        }

        foreach (WinStage st in win.Stages)
        {
            int nk = st.Window.Partition.Length + st.Window.Order.Length;
            var terms = new SortTerm[nk];
            int k = 0;
            foreach (Expr p in st.Window.Partition)
            {
                terms[k++] = new SortTerm(false, true, CollOf(p, out _));
            }

            foreach (OrderingTerm o in st.Window.Order)
            {
                bool desc = o.Order == SortOrder.Desc;
                terms[k++] = new SortTerm(desc, o.Nulls == NullsOrder.None ? !desc : o.Nulls == NullsOrder.First, CollOf(o.Expr, out _));
            }

            st.KeyTerms = terms;
            Emit(Op.WinOpen, st.Id);
            st.Streamed = win.IndexKey is not null && st.Key == win.IndexKey && st == win.Stages[0];
            if (nk > 0 && !st.Streamed)
            {
                st.Sorter = NewSorter();
                Emit(Op.SorterOpen, st.Sorter, 0, 0, terms);
            }
        }
    }

    /// <summary>
    /// What makes two calls one stage. Observed: calls written with the same partition, order and frame share a stage, and the ranking
    /// functions, ntile, lag and lead share with any call whose frame is the one they use internally, whatever frame they were written with.
    /// </summary>
    private static string StageKeyOf(ResolvedWindow w, string fnName)
    {
        string frame;
        bool only = IsWindowOnly(fnName, out WinFn fn, out _, out _);
        if (only && fn is WinFn.RowNumber or WinFn.Lag)
        {
            frame = Canon("ROWS", FrameBoundKind.UnboundedPreceding, FrameBoundKind.CurrentRow);
        }
        else if (only && fn is WinFn.Lead)
        {
            frame = Canon("ROWS", FrameBoundKind.UnboundedPreceding, FrameBoundKind.UnboundedFollowing);
        }
        else if (only && fn is WinFn.Rank or WinFn.DenseRank)
        {
            frame = Canon("RANGE", FrameBoundKind.UnboundedPreceding, FrameBoundKind.CurrentRow);
        }
        else if (only && fn is WinFn.Ntile)
        {
            frame = Canon("ROWS", FrameBoundKind.CurrentRow, FrameBoundKind.UnboundedFollowing);
        }
        else if (only && fn is WinFn.PercentRank)
        {
            frame = Canon("GROUPS", FrameBoundKind.CurrentRow, FrameBoundKind.UnboundedFollowing);
        }
        else if (only && fn is WinFn.CumeDist)
        {
            frame = "CUME_DIST";
        }
        else
        {
            string Bound(FrameBound? b) => b is null ? "-" : b.Kind + ":" + (b.Offset is null ? "" : SqlWriter.Write(b.Offset));
            frame = w.Frame is null ? Canon("RANGE", FrameBoundKind.UnboundedPreceding, FrameBoundKind.CurrentRow) : w.Frame.Unit.ToString().ToUpperInvariant() + ":" + Bound(w.Frame.Start) + ":" + (w.Frame.End is null ? "CurrentRow:" : Bound(w.Frame.End)) + ":" + w.Frame.Exclude;
        }

        return w.Key + "|" + frame;
    }

    private static string Canon(string unit, FrameBoundKind start, FrameBoundKind end) => unit + ":" + start + ":" + ":" + end + ":" + ":" + FrameExclude.None;

    /// <summary>Lays out the carried row: the columns (and, after grouping, the group's aggregates), then each call's inputs, then the results.</summary>
    private void WindowLayout(WinCompile win)
    {
        int nP = win.Slots.Count + win.NAgg;
        int slot = nP;
        foreach (WinStage st in win.Stages)
        {
            st.PartSlots = new int[st.Window.Partition.Length];
            for (int i = 0; i < st.PartSlots.Length; i++)
            {
                st.PartSlots[i] = slot++;
            }

            st.OrderSlots = new int[st.Window.Order.Length];
            for (int i = 0; i < st.OrderSlots.Length; i++)
            {
                st.OrderSlots[i] = slot++;
            }

            foreach (WinCall c in st.Calls)
            {
                c.ArgSlots = new int[c.Call.Star ? 0 : c.Call.Args.Length];
                for (int i = 0; i < c.ArgSlots.Length; i++)
                {
                    c.ArgSlots[i] = slot++;
                }

                c.FilterSlot = c.Call.Filter is null ? -1 : slot++;
                c.StartOffSlot = c.StartOff is null ? -1 : slot++;
                c.EndOffSlot = c.EndOff is null ? -1 : slot++;
            }
        }

        foreach (WinCall c in win.Calls)
        {
            c.ResultSlot = slot++;
        }

        win.W = slot;
        win.C = NewRegs(slot);
        foreach (WinStage st in win.Stages)
        {
            int nk = st.KeyTerms.Length;
            st.S = nk > 0 ? NewRegs(nk + win.W) : 0;
            st.Ret = NewReg();
            st.ConsumerLabel = NewLabel();
            st.KeySlots = [.. st.PartSlots, .. st.OrderSlots];
        }

        win.ColMap = [];
        for (int i = 0; i < win.Slots.Count; i++)
        {
            win.ColMap[win.Slots[i]] = win.C + i;
        }

        if (win.Agg is { } ap)
        {
            win.Agg = new AggPlan { Calls = ap.Calls, Index = ap.Index, ResultRegs = [.. Enumerable.Range(0, win.NAgg).Select(j => win.C + win.Slots.Count + j)] };
        }

        // The runtime description of each stage.
        foreach (WinStage st in win.Stages)
        {
            var spec = new WinStageSpec
            {
                Width = win.W,
                PartFields = st.PartSlots,
                PartColl = [.. st.Window.Partition.Select(p => CollOf(p, out _))],
                OrderFields = st.OrderSlots,
                OrderTerms = st.KeyTerms[st.PartSlots.Length..],
                Funcs = [.. st.Calls.Select(MakeFuncSpec)],
            };
            _winStages[st.Id] = spec;
        }
    }

    private WinFuncSpec MakeFuncSpec(WinCall c)
    {
        var fs = new WinFuncSpec
        {
            Fn = c.Fn,
            ArgFields = c.ArgSlots,
            FilterField = c.FilterSlot,
            Unit = c.Unit switch { FrameUnit.Rows => WinUnit.Rows, FrameUnit.Groups => WinUnit.Groups, _ => WinUnit.Range },
            Start = ToBound(c.Start),
            End = ToBound(c.End),
            StartOffField = c.StartOffSlot,
            EndOffField = c.EndOffSlot,
            Exclude = c.Exclude switch { FrameExclude.CurrentRow => WinExclude.CurrentRow, FrameExclude.Group => WinExclude.Group, FrameExclude.Ties => WinExclude.Ties, _ => WinExclude.NoOthers },
            ExplicitNoOthers = c.Exclude == FrameExclude.NoOthers,
            ResultField = c.ResultSlot,
            Back = -1,
        };
        if (c.Agg is { } k)
        {
            fs.Agg = new AggSpec { Kind = k, NArgs = c.Call.Star ? 0 : c.Call.Args.Length };
            if (!c.Call.Args.IsDefaultOrEmpty && c.Call.Args.Length > 0)
            {
                fs.Agg.Coll = CollOf(c.Call.Args[0], out _);
            }
        }

        if (c.Fn is WinFn.Lag or WinFn.Lead)
        {
            int sign = c.Fn == WinFn.Lag ? 1 : -1;
            if (c.Call.Args.Length < 2)
            {
                fs.Back = c.Fn == WinFn.Lag ? 1 : 0;
            }
            else if (LiteralInt(c.Call.Args[1]) is { } v)
            {
                long back = sign * v;
                fs.Back = back <= 0 ? 0 : back > 1_000_000 ? -1 : (int)back;
            }
        }

        return fs;
    }

    private static long? LiteralInt(Expr e)
    {
        while (e is ParenExpr p)
        {
            e = p.Inner;
        }

        if (e is LiteralExpr { Kind: LiteralKind.Integer } l && long.TryParse(l.Text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long v))
        {
            return v;
        }

        return null;
    }

    private static WinBound ToBound(FrameBoundKind k) => k switch
    {
        FrameBoundKind.UnboundedPreceding => WinBound.UnboundedPreceding,
        FrameBoundKind.Preceding => WinBound.Preceding,
        FrameBoundKind.CurrentRow => WinBound.CurrentRow,
        FrameBoundKind.Following => WinBound.Following,
        _ => WinBound.UnboundedFollowing,
    };

    // ------------------------------------------------------------------ the rows going in

    /// <summary>One row of the core: carries what the windows and the output need, then enters the first stage.</summary>
    private void WindowFeed(WinCompile win, int[]? bare, int[]? aggRegs)
    {
        for (int i = 0; i < win.Slots.Count; i++)
        {
            if (bare is null)
            {
                EmitSourceColumn(win.Slots[i].Src, win.Slots[i].Col, win.C + i, false);
            }
            else
            {
                Emit(Op.Copy, bare[i], win.C + i);
            }
        }

        for (int j = 0; j < win.NAgg; j++)
        {
            Emit(Op.Copy, aggRegs![j], win.C + win.Slots.Count + j);
        }

        WinPhase saved = win.Phase;
        win.Phase = WinPhase.Feed;
        try
        {
            foreach (WinStage st in win.Stages)
            {
                for (int i = 0; i < st.PartSlots.Length; i++)
                {
                    CompileInto(st.Window.Partition[i], win.C + st.PartSlots[i]);
                }

                for (int i = 0; i < st.OrderSlots.Length; i++)
                {
                    CompileInto(st.Window.Order[i].Expr, win.C + st.OrderSlots[i]);
                }

                foreach (WinCall c in st.Calls)
                {
                    for (int i = 0; i < c.ArgSlots.Length; i++)
                    {
                        CompileInto(c.Call.Args[i], win.C + c.ArgSlots[i]);
                    }

                    if (c.FilterSlot >= 0)
                    {
                        CompileInto(c.Call.Filter!, win.C + c.FilterSlot);
                    }

                    if (c.StartOffSlot >= 0)
                    {
                        CompileOffsetInto(c.StartOff!, win.C + c.StartOffSlot);
                    }

                    if (c.EndOffSlot >= 0)
                    {
                        CompileOffsetInto(c.EndOff!, win.C + c.EndOffSlot);
                    }
                }
            }
        }
        finally
        {
            win.Phase = saved;
        }

        foreach (WinCall c in win.Calls)
        {
            Emit(Op.Null, 0, win.C + c.ResultSlot);
        }

        WindowInsert(win, 0);
    }

    /// <summary>Hands the carried row (in registers) to stage <paramref name="i"/>.</summary>
    private void WindowInsert(WinCompile win, int i)
    {
        WinStage st = win.Stages[i];
        int nk = st.KeyTerms.Length;
        if (nk > 0 && !st.Streamed)
        {
            for (int k = 0; k < nk; k++)
            {
                Emit(Op.Copy, win.C + st.KeySlots[k], st.S + k);
            }

            for (int w = 0; w < win.W; w++)
            {
                Emit(Op.Copy, win.C + w, st.S + nk + w);
            }

            Emit(Op.SorterInsert, st.Sorter, st.S, nk + win.W);
        }
        else
        {
            Emit(Op.WinPush, st.Id, win.C);
            WindowDrain(win, st);
        }
    }

    private void WindowDrain(WinCompile win, WinStage st)
    {
        int again = NewLabel(), none = NewLabel();
        Mark(again);
        Emit(Op.WinOut, st.Id, none, win.C);
        Emit(Op.Gosub, st.Ret, st.ConsumerLabel);
        Emit(Op.Goto, 0, again);
        Mark(none);
    }

    // ------------------------------------------------------------------ the rows coming out

    /// <summary>After the core's last row: sorts and runs each stage in turn, then the output path for the finished rows.</summary>
    private void WindowFinish(WinCompile win, Pipe pipe, ImmutableArray<OrderingTerm> order)
    {
        for (int i = 0; i < win.Stages.Count; i++)
        {
            WinStage st = win.Stages[i];
            int nk = st.KeyTerms.Length;
            if (nk > 0 && !st.Streamed)
            {
                int end = NewLabel(), again = NewLabel();
                Emit(Op.SorterSort, st.Sorter, end);
                Mark(again);
                Emit(Op.SorterData, st.Sorter, st.S, nk + win.W);
                Emit(Op.WinPush, st.Id, st.S + nk);
                WindowDrain(win, st);
                Emit(Op.SorterNext, st.Sorter, again);
                Mark(end);
            }

            Emit(Op.WinFinish, st.Id);
            WindowDrain(win, st);
        }

        int over = NewLabel();
        Emit(Op.Goto, 0, over);
        for (int i = 0; i < win.Stages.Count; i++)
        {
            WinStage st = win.Stages[i];
            Mark(st.ConsumerLabel);
            if (i + 1 < win.Stages.Count)
            {
                WindowInsert(win, i + 1);
            }
            else
            {
                // The finished row: columns and aggregates read from the carried registers, window calls read their results.
                var savedMap = _colMap;
                var savedAgg = _aggPlan;
                bool savedAlias = _scope is not null && _scope.AliasesVisible;
                WinPhase savedPhase = win.Phase;
                _colMap = win.ColMap;
                _aggPlan = win.Agg;
                win.Phase = WinPhase.Emit;
                if (win.Grouped && _scope is not null)
                {
                    _scope.AliasesVisible = true;
                }

                try
                {
                    EmitProduce(pipe, order);
                }
                finally
                {
                    _colMap = savedMap;
                    _aggPlan = savedAgg;
                    win.Phase = savedPhase;
                    if (_scope is not null)
                    {
                        _scope.AliasesVisible = savedAlias;
                    }
                }
            }

            Emit(Op.Return, st.Ret);
        }

        Mark(over);
    }

    // ------------------------------------------------------------------ rows in index order

    /// <summary>
    /// Observed: when the window that runs first (the last one written) has PARTITION BY and ORDER BY terms that are the leading columns of an
    /// index, or just the rowid, the reference reads the index (backwards when every ORDER BY term is DESC) instead of sorting. Rows that tie
    /// then come out in index order, which for a backwards scan is rowid descending, and the later stages sort stably over that order.
    /// </summary>
    private void WindowIndexPlan(Source only, ref AccessPlan plan, List<(Expr Expr, string Name)> cols)
    {
        TableInfo t = _win is null ? null! : only.Table;
        if (_win is null || plan.Kind is not (PlanKind.Scan or PlanKind.RowidRange) || only.Kind != SourceKind.Table || t.WithoutRowid || only.NotIndexed || only.Forced is not null)
        {
            return;
        }

        FunctionCall? last = null;
        var seen = new HashSet<string>();
        foreach ((Expr e, _) in cols)
        {
            if (FindWindowCall(e) is { Over: not null } f && seen.Add(StageKeyOf(ResolveWindow(_win, f.Over), f.Name.Text)))
            {
                last = f;
            }
        }

        if (last?.Over is null)
        {
            return;
        }

        ResolvedWindow w = ResolveWindow(_win, last.Over);
        int nk = w.Partition.Length + w.Order.Length;
        if (nk == 0)
        {
            return;
        }

        // Observed: partition terms ask for ascending order; a term with an explicit NULLS clause is left to the sorter.
        var wantDesc = new List<bool>();
        foreach (Expr unused in w.Partition)
        {
            wantDesc.Add(false);
        }

        bool nullsOdd = false;
        foreach (OrderingTerm o in w.Order)
        {
            // An explicit NULLS clause is not something an index scan gives, but the index still decides the order of rows that tie.
            nullsOdd |= o.Nulls != NullsOrder.None;
            wantDesc.Add(o.Order == SortOrder.Desc);
        }

        var wanted = new List<(int Col, CollKind Coll)>();
        foreach (Expr e in w.Partition.Concat(w.Order.Select(o => o.Expr)))
        {
            Expr x = e;
            while (x is ParenExpr or CollateExpr)
            {
                x = x is ParenExpr p ? p.Inner : ((CollateExpr)x).Operand;
            }

            if (x is not ColumnRef cr || !TryResolveColumn(cr, out Source rs, out int col) || rs != only)
            {
                return;
            }

            // A term repeating an earlier column (and collation) adds nothing to the order.
            (int, CollKind) key = (col, CollOf(e, out _));
            if (wanted.Contains(key))
            {
                wantDesc.RemoveAt(wanted.Count);
                continue;
            }

            wanted.Add(key);
        }

        if (wanted.Count == 1 && (wanted[0].Col == -1 || wanted[0].Col == t.RowidAlias))
        {
            if (wantDesc[0] && w.Partition.IsEmpty)
            {
                plan = new AccessPlan { Kind = PlanKind.Scan, Reverse = true };
            }
            else if (!wantDesc[0])
            {
                // A rowid range already walks in rowid order; keep it.
                plan = plan.Kind == PlanKind.RowidRange ? plan : new AccessPlan { Kind = PlanKind.Scan };
            }
            else
            {
                return;
            }

            _win.IndexKey = StageKeyOf(w, last.Name.Text);
            return;
        }

        IndexInfo? best = null;
        bool bestRev = false;
        foreach (IndexInfo idx in t.Indexes)
        {
            if (nullsOdd || !idx.Coverable || idx.Columns.Length < wanted.Count || idx.Desc.Length != idx.Columns.Length)
            {
                continue;
            }

            bool ok = true;
            bool? rev = null;
            for (int i = 0; i < wanted.Count && ok; i++)
            {
                ok = idx.Columns[i] >= 0 && idx.Columns[i] == wanted[i].Col && ParseColl(idx.Collations[i] ?? "BINARY") == wanted[i].Coll;
                bool r = idx.Desc[i] != wantDesc[i];
                rev ??= r;
                ok &= rev == r;
            }

            if (ok && (best is null || idx.Columns.Length <= best.Columns.Length))
            {
                best = idx;
                bestRev = rev == true;
            }
        }

        if (best is not null)
        {
            plan = new AccessPlan { Kind = PlanKind.Index, Index = best, Reverse = bestRev };
            _win.IndexKey = StageKeyOf(w, last.Name.Text);
            return;
        }

        // Observed: an index that matches only the first terms is still the one read, and the stage then sorts the rows it gets; the
        // rows that tie come out in index order (rowid descending on a backwards scan).
        int bestLen = 0;
        foreach (IndexInfo idx in t.Indexes)
        {
            if (!idx.Coverable || idx.Desc.Length != idx.Columns.Length)
            {
                continue;
            }

            int len = 0;
            bool? rev = null;
            while (len < wanted.Count && len < idx.Columns.Length && idx.Columns[len] >= 0 && idx.Columns[len] == wanted[len].Col
                && ParseColl(idx.Collations[len] ?? "BINARY") == wanted[len].Coll)
            {
                bool r = idx.Desc[len] != wantDesc[len];
                rev ??= r;
                if (rev != r)
                {
                    break;
                }

                len++;
            }

            if (len > 0 && (len > bestLen || (len == bestLen && best is not null && idx.Columns.Length <= best.Columns.Length)))
            {
                best = idx;
                bestLen = len;
                bestRev = rev == true;
            }
        }

        if (best is not null)
        {
            plan = new AccessPlan { Kind = PlanKind.Index, Index = best, Reverse = bestRev };
        }
    }
}
