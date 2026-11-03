using System.Collections.Immutable;
using System.Linq;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite.Planning;

internal enum PlanKind : byte
{
    /// <summary>Visit every row in key order.</summary>
    Scan,

    /// <summary>One row by rowid: <c>rowid = expr</c>.</summary>
    RowidEq,

    /// <summary>A range of rowids.</summary>
    RowidRange,

    /// <summary>A range of one index (or of a WITHOUT ROWID table's primary key): equality on leading columns, then an optional range.</summary>
    Index,

    /// <summary>One rowid seek for each distinct value of <c>rowid IN (list or subquery)</c>, in ascending order.</summary>
    RowidIn,

    /// <summary>One index seek for each distinct value of <c>leading-column IN (list or subquery)</c>, in ascending order.</summary>
    IndexIn,
}

/// <summary>A WHERE term of the form <c>column op constant</c>, with the column on the left.</summary>
internal sealed record Term(int Column, BinaryOp Cmp, Expr Value, Expr Left, Expr Right);

/// <summary>The access path chosen for the FROM table. Plans only decide which rows are visited; the whole WHERE clause is still evaluated for each.</summary>
internal sealed class AccessPlan
{
    internal PlanKind Kind;
    internal IndexInfo? Index;
    internal List<Term> Equal = [];
    internal Term? Lower;
    internal Term? Upper;

    /// <summary>For <see cref="PlanKind.RowidIn"/> and <see cref="PlanKind.IndexIn"/>: the <c>IN</c> term whose values are looked up, and the table column it tests.</summary>
    internal Expr? In;
    internal int InColumn;

    /// <summary>Walk the whole index (or the table, for a rowid scan) from its last entry to its first.</summary>
    internal bool Reverse;

    // What a query plan reports about the choice. Read only: nothing in the compiler reads these.
    /// <summary>Why this path was taken when the reason is not evident from its shape (a covering index, a GROUP BY order).</summary>
    internal string? Reason;

    /// <summary>What else this path does for the query (ORDER BY or GROUP BY satisfied by its order).</summary>
    internal List<string> Notes = [];

    /// <summary>The indexes weighed for this table, set only when a plan is being recorded.</summary>
    internal List<PlanCandidate>? Considered;
}

/// <summary>Whether a compile records a query plan, and whether the program it makes counts the rows of each loop.</summary>
internal enum PlanMode : byte
{
    Off,
    Record,
    Count,
}

internal sealed partial class Compiler
{
    // ------------------------------------------------------------------ planning

    /// <summary>The indexes ChoosePlan weighed, collected only while a plan is being recorded.</summary>
    private List<PlanCandidate>? _trace;

    /// <summary>Records the query plan while the program is generated; null when none was asked for.</summary>
    private PlanRecorder? _plan;

    /// <summary>A remark for the next loop recorded (set just before emitting it).</summary>
    private string? _planNote;

    /// <summary>The steps after the loops: window functions, aggregation, DISTINCT and the ORDER BY sort.</summary>
    private void RecordPostSteps(PlanRecorder rec, Expr[] groupExprs, ImmutableArray<OrderingTerm> order, bool hasAgg, bool grouped, bool orderedGroups, bool useSorter, bool distinct)
    {
        if (_win is not null)
        {
            rec.Leaf(PlanNodeKind.Window, string.Empty, "(rows of each partition are buffered, then the window functions are computed)");
        }

        if (hasAgg)
        {
            PlanNode a = rec.Leaf(PlanNodeKind.Aggregate, grouped ? "GROUP BY " + string.Join(", ", groupExprs.Select(g => SqlWriter.Write(g))) : string.Empty);
            a.Detail = !grouped ? "(a single result row)" : orderedGroups ? "(groups arrive in key order)" : "(rows go through a temporary sorter by the group key)";
        }

        if (distinct)
        {
            rec.Leaf(PlanNodeKind.Distinct, string.Empty, "(rows already seen are kept in an in-memory set)");
        }

        if (useSorter)
        {
            rec.Sort("ORDER BY " + string.Join(", ", order.Select(o => SqlWriter.Write(o.Expr) + (o.Order == SortOrder.Desc ? " DESC" : string.Empty))));
        }
    }

    /// <summary>Starts recording a plan for the statement being compiled.</summary>
    internal void StartPlan(string title, bool counting) => _plan = new PlanRecorder(_sql, title, counting);

    /// <summary>The plan recorded so far, or null when none is being recorded.</summary>
    internal QueryPlan? FinishPlan() => _plan?.Build();

    /// <summary>The columns the single-table SELECT being planned reads, so index choice can prefer an index that holds them all; null when unknown.</summary>
    private HashSet<int>? _coverUsed;

    /// <summary>
    /// Chooses how to read <paramref name="s"/> given the terms that may restrict it. A term is usable when one side is a column of <paramref name="s"/>
    /// and the other reads only constants, outer queries and the items in <paramref name="bound"/> (already positioned by outer loops).
    /// </summary>
    private AccessPlan MakePlan(Source s, List<Expr> where, HashSet<Source> bound)
    {
        _trace = _plan is null ? null : [];
        AccessPlan plan = ChoosePlan(s, where, bound);
        plan.Considered = _trace;
        _trace = null;
        if (s.Forced is { Usable: true } forced && plan.Kind == PlanKind.Scan)
        {
            // INDEXED BY with nothing to look up: the rows are read through the index, in its order.
            plan = new AccessPlan { Kind = PlanKind.Index, Index = forced };
        }

        return plan;
    }

    private AccessPlan ChoosePlan(Source s, List<Expr> where, HashSet<Source> bound)
    {
        var scan = new AccessPlan { Kind = PlanKind.Scan };
        if (where.Count == 0 || s.Kind != SourceKind.Table)
        {
            return scan;
        }

        TableInfo table = s.Table;
        try
        {
            var terms = new List<Term>();
            foreach (Expr w in where)
            {
                foreach (Expr c in Conjuncts(w))
                {
                    ExtractTerms(c, terms, s, bound);
                }
            }

            List<(Expr Term, int Column)> ins = InTerms(s, where, bound);
            if (terms.Count == 0 && ins.Count == 0)
            {
                return scan;
            }

            // Rowid terms.
            if (!table.WithoutRowid)
            {
                Term? eq = terms.Find(t => IsRowidTerm(s, t) && t.Cmp == BinaryOp.Eq);
                if (eq is not null)
                {
                    return new AccessPlan { Kind = PlanKind.RowidEq, Equal = [eq] };
                }
            }

            // Indexes.
            AccessPlan? best = null;
            int bestScore = 0;
            var candidates = new List<IndexInfo>();
            if (s.NotIndexed)
            {
                // none
            }
            else if (s.Forced is not null)
            {
                candidates.Add(s.Forced);
            }
            else if (table.WithoutRowid && table.PrimaryKey is { Seekable: true } pk)
            {
                candidates.Add(pk);
                candidates.AddRange(table.Indexes.Where(i => i.Seekable));
            }
            else
            {
                candidates.AddRange(table.Indexes.Where(i => i.Seekable));
            }

            foreach (IndexInfo idx in candidates)
            {
                AccessPlan p = new() { Kind = PlanKind.Index, Index = idx };
                int pos = 0;
                for (; pos < idx.Columns.Length; pos++)
                {
                    Term? t = terms.Find(x => x.Column == idx.Columns[pos] && x.Cmp == BinaryOp.Eq && CollMatches(x, idx.Collations[pos]));
                    if (t is null)
                    {
                        break;
                    }

                    p.Equal.Add(t);
                }

                if (pos < idx.Columns.Length)
                {
                    p.Lower = terms.Find(x => x.Column == idx.Columns[pos] && x.Cmp is BinaryOp.Gt or BinaryOp.Ge && CollMatches(x, idx.Collations[pos]));
                    p.Upper = terms.Find(x => x.Column == idx.Columns[pos] && x.Cmp is BinaryOp.Lt or BinaryOp.Le && CollMatches(x, idx.Collations[pos]));
                }

                int score = (p.Equal.Count * 4) + (p.Lower is not null ? 1 : 0) + (p.Upper is not null ? 1 : 0);
                _trace?.Add(new PlanCandidate(idx, p, score));

                // Observed with the pinned shell: among indexes that restrict equally, one that holds every column the query reads wins, the
                // narrower such index first; otherwise the index created last. A forced or primary-key candidate keeps its place at the front.
                bool later = false;
                if (score == bestScore && score > 0 && best is not null && s.Forced is null)
                {
                    bool coversNew = _coverUsed is not null && IndexCovers(table, idx, _coverUsed);
                    bool coversOld = _coverUsed is not null && IndexCovers(table, best.Index!, _coverUsed);
                    later = coversNew != coversOld
                        ? coversNew
                        : !coversNew || IndexWidth(table, idx) <= IndexWidth(table, best.Index!);
                    if (best.Index!.IsPrimaryKey && table.WithoutRowid && !coversNew)
                    {
                        later = false;
                    }
                }

                if (score > bestScore || later)
                {
                    best = p;
                    bestScore = score;
                }
            }

            // pragma optimize: a table counts as queried once the WHERE clause constrains a column that one of its (non-primary-key) indexes holds,
            // because the planner then weighs that index with its statistics (observed with the pinned shell, skip-scan candidates included).
            if (table.Db == 0 && terms.Count > 0 && table.Indexes.Any(i => !i.IsPrimaryKey && terms.Any(x => Array.IndexOf(i.Columns, x.Column) >= 0)))
            {
                _cat.Owner?.StatsUsed.Add(table.Name);
                _cat.Owner?.CompileStatsTables.Add(table.Name);
            }

            if (best is not null && best.Equal.Count > 0)
            {
                return best;
            }

            // x IN (...) on the rowid or on the first column of an index: one seek per distinct value (optoverview "The IN-early-out optimization"
            // and queryplanner). An index in descending order is left out: the reference walks its values the other way round.
            if (ins.Count > 0)
            {
                if (!table.WithoutRowid && ins.Find(x => x.Column == -1 || x.Column == table.RowidAlias) is { Term: not null } rw)
                {
                    return new AccessPlan { Kind = PlanKind.RowidIn, In = rw.Term, InColumn = -1 };
                }

                foreach (IndexInfo idx in candidates)
                {
                    foreach ((Expr term, int column) in ins)
                    {
                        if (column == idx.Columns[0] && !(idx.Desc.Length > 0 && idx.Desc[0]) && InCollationMatches(term, s, idx.Collations[0]))
                        {
                            return new AccessPlan { Kind = PlanKind.IndexIn, Index = idx, In = term, InColumn = column };
                        }
                    }
                }
            }

            if (!table.WithoutRowid)
            {
                Term? lo = terms.Find(t => IsRowidTerm(s, t) && t.Cmp is BinaryOp.Gt or BinaryOp.Ge);
                Term? hi = terms.Find(t => IsRowidTerm(s, t) && t.Cmp is BinaryOp.Lt or BinaryOp.Le);
                if (lo is not null || hi is not null)
                {
                    return new AccessPlan { Kind = PlanKind.RowidRange, Lower = lo, Upper = hi };
                }
            }

            return best ?? scan;
        }
        catch (OneLiteException)
        {
            // An unknown collation or similar: the full scan is always correct, and compiling the clause reports the error properly.
            return scan;
        }
    }

    /// <summary>
    /// A full scan that touches only columns held by an index reads that index instead of the table, so rows come out in index order
    /// (observed with the pinned shell: the covering index whose columns have the smallest estimated width, the one created last on a tie). The index entry is
    /// still followed to the table row here, so values never depend on this choice, only the order of rows without an ORDER BY.
    /// </summary>
    private AccessPlan CoveringScan(Source src, AccessPlan scan, List<(Expr Expr, string Name)> cols, SimpleSelect core)
    {
        TableInfo table = src.Table;
        if (src.Kind != SourceKind.Table || src.NotIndexed || src.Forced is not null || table.Indexes.Count == 0)
        {
            return scan;
        }

        if (!TryUsedColumns(src, cols, core, out HashSet<int>? used))
        {
            return scan;
        }

        IndexInfo? best = null;
        int bestCost = int.MaxValue;
        foreach (IndexInfo idx in table.Indexes)
        {
            if (!idx.Scannable)
            {
                continue;
            }

            // Observed: the index behind a UNIQUE constraint of a WITHOUT ROWID table is never read instead of the table; an explicit CREATE INDEX is.
            if (table.WithoutRowid && idx.Name.StartsWith("sqlite_autoindex_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IndexCovers(table, idx, used))
            {
                continue;
            }

            int cost = IndexWidth(table, idx);

            if (cost <= bestCost)
            {
                best = idx;
                bestCost = cost;
            }
        }

        // The table itself is read when it is no wider than the index (a rowid alias column is not counted: it is not stored in the row;
        // a WITHOUT ROWID table counts every column, and an index that adds up to the same width is still preferred).
        int tableCost = 0;
        for (int i = 0; i < table.Columns.Length; i++)
        {
            if (table.WithoutRowid || i != table.RowidAlias)
            {
                tableCost += EstimatedWidth(table.Columns[i].DeclaredType);
            }
        }

        return best is null || bestCost >= tableCost + (table.WithoutRowid ? 1 : 0) ? scan : new AccessPlan { Kind = PlanKind.Index, Index = best };
    }

    /// <summary>The table columns a single-table SELECT reads (-1 for the rowid); false when something in it is not understood.</summary>
    private bool TryUsedColumns(Source src, List<(Expr Expr, string Name)> cols, SimpleSelect core, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HashSet<int>? used)
    {
        used = new HashSet<int>();
        bool ok = true;
        foreach ((Expr e, _) in cols)
        {
            ok &= CollectColumns(src, e, used);
        }

        foreach (OrderingTerm ot in core.OrderBy)
        {
            ok &= CollectColumns(src, ot.Expr, used);
        }

        if (core.Where is not null)
        {
            ok &= CollectColumns(src, core.Where, used);
        }

        if (!core.GroupBy.IsDefaultOrEmpty)
        {
            foreach (Expr g in core.GroupBy)
            {
                ok &= CollectColumns(src, g, used);
            }
        }

        if (core.Having is not null)
        {
            ok &= CollectColumns(src, core.Having, used);
        }

        if (core.Limit is { } l)
        {
            ok &= CollectColumns(src, l.Limit, used) && (l.Offset is null || CollectColumns(src, l.Offset, used));
        }

        if (!ok)
        {
            used = null;
        }

        return ok;
    }

    /// <summary>An index entry also holds the rowid, or for a WITHOUT ROWID table the primary key columns the index lacks, so those are covered too.</summary>
    private static bool IndexCovers(TableInfo table, IndexInfo idx, HashSet<int> used)
    {
        foreach (int c in used)
        {
            // Observed: a generated column is read from the table even when an index holds its value.
            if (c >= 0 && table.Columns[c].Generated != GeneratedKind.None)
            {
                return false;
            }

            if (!(c < 0 || c == table.RowidAlias || Array.IndexOf(idx.Columns, c) >= 0 || (table.WithoutRowid && Array.IndexOf(table.PrimaryKey!.Columns, c) >= 0)))
            {
                return false;
            }
        }

        return true;
    }

    private static int IndexWidth(TableInfo table, IndexInfo idx)
    {
        int cost = 0;
        foreach (int c in idx.Columns)
        {
            cost += c < 0 ? 1 : EstimatedWidth(table.Columns[c].DeclaredType);
        }

        if (table.WithoutRowid)
        {
            foreach (int pc in table.PrimaryKey!.Columns)
            {
                if (Array.IndexOf(idx.Columns, pc) < 0)
                {
                    cost += EstimatedWidth(table.Columns[pc].DeclaredType);
                }
            }
        }

        return cost;
    }

    /// <summary>
    /// The width a column is assumed to have when comparing indexes, from its declared type (observed: a length in parentheses gives
    /// length / 4 + 1; TEXT, CLOB and BLOB types count 5; every other type, and none, counts 1).
    /// </summary>
    private static int EstimatedWidth(string declared)
    {
        string u = declared.ToUpperInvariant();
        int open = declared.IndexOf('(', StringComparison.Ordinal);
        if (open >= 0 && !u.Contains("TEXT", StringComparison.Ordinal))
        {
            int n = 0;
            for (int i = open + 1; i < declared.Length && declared[i] is >= '0' and <= '9'; i++)
            {
                n = Math.Min((n * 10) + (declared[i] - '0'), 1_000_000);
            }

            return (n / 4) + 1;
        }

        // Observed against sized VARCHAR columns: TEXT, CLOB and BLOB types (a size after TEXT is ignored) rank like VARCHAR(16), the others
        // (CHAR and VARCHAR included) like no type.
        return u.Contains("CLOB", StringComparison.Ordinal) || u.Contains("TEXT", StringComparison.Ordinal) || u.Contains("BLOB", StringComparison.Ordinal) ? 5 : 1;
    }

    /// <summary>Adds the table columns an expression reads to <paramref name="used"/>; false when it holds something this walk does not understand.</summary>
    private bool CollectColumns(Source src, Expr e, HashSet<int> used)
    {
        switch (e)
        {
            case LiteralExpr or VariableExpr:
                return true;
            case ColumnRef cr:
                if (TryResolveColumn(cr, out Source rs, out int col) && rs == src)
                {
                    used.Add(col);
                }
                else if (_scope is not null && _scope.Aliases.ContainsKey(cr.Column.Text))
                {
                    return false;
                }

                return true;
            case UnaryExpr u:
                return CollectColumns(src, u.Operand, used);
            case BinaryExpr b:
                return CollectColumns(src, b.Left, used) && CollectColumns(src, b.Right, used);
            case CollateExpr c:
                return CollectColumns(src, c.Operand, used);
            case CastExpr c:
                return CollectColumns(src, c.Operand, used);
            case BetweenExpr bt:
                return CollectColumns(src, bt.Operand, used) && CollectColumns(src, bt.Low, used) && CollectColumns(src, bt.High, used);
            case InListExpr { Items.IsDefaultOrEmpty: true }:
                return true; // `x IN ()` is a constant; the reference never reads x
            case InListExpr il:
                return CollectColumns(src, il.Operand, used) && il.Items.All(i => CollectColumns(src, i, used));
            case CaseExpr ce:
                return (ce.Operand is null || CollectColumns(src, ce.Operand, used))
                    && ce.Whens.All(w => CollectColumns(src, w.When, used) && CollectColumns(src, w.Then, used))
                    && (ce.Else is null || CollectColumns(src, ce.Else, used));
            case FunctionCall f when f.Over is not null:
                // A window call reads its arguments and what its window sorts and partitions by; the reference's inner query reads them too.
                return f.OrderBy.IsDefaultOrEmpty && WindowCallInputs(f).All(a => CollectColumns(src, a, used));
            case FunctionCall f:
                return f.Over is null && (f.Filter is null || CollectColumns(src, f.Filter, used)) && f.OrderBy.All(o => CollectColumns(src, o.Expr, used)) && f.Args.All(a => CollectColumns(src, a, used));
            case NullTestExpr n:
                return CollectColumns(src, n.Operand, used);
            case LikeExpr lk:
                return CollectColumns(src, lk.Left, used) && CollectColumns(src, lk.Pattern, used) && (lk.Escape is null || CollectColumns(src, lk.Escape, used));
            case ParenExpr p:
                return CollectColumns(src, p.Inner, used);
            case RowValueExpr rv:
                return rv.Items.All(i => CollectColumns(src, i, used));
            default:
                return false;
        }
    }

    private static bool IsRowidTerm(Source s, Term t) => t.Column == -1 || t.Column == s.Table.RowidAlias;

    private bool CollMatches(Term t, string? indexColl)
    {
        if (indexColl is null)
        {
            indexColl = "BINARY";
        }

        return AsciiComparer.Instance.Equals(CollName(PairColl(t.Left, t.Right)), CollName(ParseColl(indexColl)));
    }

    private static string CollName(CollKind k) => k.ToString();

    /// <summary>Recognises <c>column op constant</c>, <c>constant op column</c> and <c>column BETWEEN constant AND constant</c>.</summary>
    private void ExtractTerms(Expr c, List<Term> terms, Source s, HashSet<Source> bound)
    {
        switch (c)
        {
            case BinaryExpr { Op: BinaryOp.Eq or BinaryOp.Lt or BinaryOp.Le or BinaryOp.Gt or BinaryOp.Ge } b:
            {
                if (ColumnOf(b.Left, s) is { } lc && IsUsableValue(b.Right, s, bound, lc))
                {
                    terms.Add(new Term(lc, b.Op, b.Right, b.Left, b.Right));
                }
                else if (ColumnOf(b.Right, s) is { } rc && IsUsableValue(b.Left, s, bound, rc))
                {
                    BinaryOp flipped = b.Op switch { BinaryOp.Lt => BinaryOp.Gt, BinaryOp.Le => BinaryOp.Ge, BinaryOp.Gt => BinaryOp.Lt, BinaryOp.Ge => BinaryOp.Le, _ => b.Op };
                    terms.Add(new Term(rc, flipped, b.Left, b.Left, b.Right));
                }

                return;
            }

            case BetweenExpr { Negated: false } bt:
                if (ColumnOf(bt.Operand, s) is { } bc && IsUsableValue(bt.Low, s, bound, bc) && IsUsableValue(bt.High, s, bound, bc))
                {
                    terms.Add(new Term(bc, BinaryOp.Ge, bt.Low, bt.Operand, bt.Low));
                    terms.Add(new Term(bc, BinaryOp.Le, bt.High, bt.Operand, bt.High));
                }

                return;
        }
    }

    /// <summary>The column an expression is, looking through parentheses and COLLATE: -1 for the rowid, null if it is not a column of the table.</summary>
    private int? ColumnOf(Expr e, Source s)
    {
        while (true)
        {
            switch (e)
            {
                case ParenExpr p:
                    e = p.Inner;
                    continue;
                case CollateExpr c:
                    e = c.Operand;
                    continue;
                case ColumnRef cr when TryResolveColumn(cr, out Source rs, out int col) && rs == s:
                    return col;
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// An expression an index or rowid seek may use as its key: no column references and no affinity of its own. (An operand with an
    /// affinity would change which conversions the comparison applies, so such terms are left to the filter.)
    /// </summary>
    private bool IsUsableValue(Expr e, Source s, HashSet<Source> bound, int column)
    {
        if (!IsKeyExpr(e))
        {
            return false;
        }

        var refs = new HashSet<Source>(ReferenceEqualityComparer.Instance);
        if (_scope is null || !CollectSources(e, _scope, refs) || refs.Contains(s) || !refs.IsSubsetOf(bound))
        {
            return false;
        }

        // A rowid seek converts its key as for a bare value, so a key with an affinity of its own is left to the filter. An index seek applies
        // the affinity the comparison would (datatype3 "Comparison Expressions"), taken from the key (KeyReg).
        if (column < 0 || column == s.Table.RowidAlias)
        {
            return Values.CompareAffinity(Affinity.Integer, CmpAffOf(e)) == Values.CompareAffinity(Affinity.Integer, Affinity.None);
        }

        // Observed: a comparison that converts the stored values (TEXT column against a numeric operand, or a column with no numeric affinity
        // against a numeric comparison) cannot be answered by the index, which orders them unconverted; such a term is left to the filter.
        Affinity colAff = s.Table.Columns[column].Affinity;
        Affinity cmp = Values.CompareAffinity(colAff, CmpAffOf(e));
        return cmp switch
        {
            Affinity.Text => colAff == Affinity.Text,
            Affinity.Numeric or Affinity.Integer or Affinity.Real => colAff is Affinity.Numeric or Affinity.Integer or Affinity.Real,
            _ => true,
        };
    }

    /// <summary>An expression an index seek may compute its key from: columns, constants and deterministic operators; no subqueries or aggregates.</summary>
    private static bool IsKeyExpr(Expr e)
    {
        switch (e)
        {
            case LiteralExpr:
            case VariableExpr:
            case ColumnRef:
                return true;
            case ParenExpr p:
                return IsKeyExpr(p.Inner);
            case CollateExpr c:
                return IsKeyExpr(c.Operand);
            case UnaryExpr u:
                return IsKeyExpr(u.Operand);
            case BinaryExpr b:
                return IsKeyExpr(b.Left) && IsKeyExpr(b.Right);
            case CastExpr ce:
                return IsKeyExpr(ce.Operand);
            case FunctionCall f:
                return !f.Star && f.Over is null && f.Filter is null && !ContainsAggregate(f) && f.Args.All(IsKeyExpr);
            default:
                return false;
        }
    }

    private static bool IsConstant(Expr e)
    {
        switch (e)
        {
            case LiteralExpr:
            case VariableExpr:
                return true;
            case ParenExpr p:
                return IsConstant(p.Inner);
            case CollateExpr c:
                return IsConstant(c.Operand);
            case UnaryExpr u:
                return IsConstant(u.Operand);
            case BinaryExpr b:
                return IsConstant(b.Left) && IsConstant(b.Right);
            case CastExpr ce:
                return IsConstant(ce.Operand);
            case FunctionCall f:
                return !f.Star && f.Over is null && f.Args.All(IsConstant);
            default:
                return false;
        }
    }

    // ------------------------------------------------------------------ the loop

    private int KeyReg(Expr value, Affinity columnAffinity, bool keyHasOwnAffinity = false)
    {
        int r = NewReg();
        CompileInto(value, r);
        Affinity a = Values.CompareAffinity(columnAffinity, keyHasOwnAffinity ? CmpAffOf(value) : Affinity.None);
        if (a != Affinity.None)
        {
            Emit(Op.CompareAffinity, r, 0, 0, null, (int)a);
        }

        return r;
    }

    /// <summary>
    /// A table-valued function's loop: the arguments (the call's, then the hidden columns the WHERE clause fixes) go into consecutive registers, then
    /// VFilter starts the scan and VNext advances it.
    /// </summary>
    private void EmitModuleLoop(Source s, int lend, Action<int> body)
    {
        ModuleDef def = s.Module!;
        int hiddenCount = def.Columns.Length - def.HiddenFrom;
        var args = new Expr?[hiddenCount];
        for (int i = 0; i < s.ModuleArgs.Length; i++)
        {
            args[i] = s.ModuleArgs[i];
        }

        if (s.ModuleEq is not null)
        {
            foreach ((int col, Expr value) in s.ModuleEq)
            {
                int k = col - def.HiddenFrom;
                if (args[k] is null)
                {
                    args[k] = value;
                }
            }
        }

        // The arguments given are a prefix of the hidden columns; a gap (the second but not the first) leaves the earlier ones NULL.
        int count = 0;
        for (int i = 0; i < hiddenCount; i++)
        {
            if (args[i] is not null)
            {
                count = i + 1;
            }
        }

        if (def.TooFewMessage(count) is { } msg)
        {
            throw Err(msg);
        }

        int firstReg = count == 0 ? 0 : NewRegs(count);
        for (int i = 0; i < count; i++)
        {
            if (args[i] is { } a)
            {
                CompileInto(a, firstReg + i);
            }
            else
            {
                Emit(Op.Null, 0, firstReg + i);
            }
        }

        int cur = s.Cursor;
        Emit(Op.VFilter, cur, lend, firstReg, def, count);
        int loop = NewLabel(), next = NewLabel();
        Mark(loop);
        body(next);
        Mark(next);
        Emit(Op.VNext, cur, loop);
    }

    /// <summary>
    /// Emits the loop that positions <paramref name="s"/> on each row <paramref name="plan"/> visits; <paramref name="body"/> gets the label that continues
    /// with the next row. Control reaches <paramref name="lend"/> when the rows are used up.
    /// </summary>
    private void EmitSourceLoop(Source s, AccessPlan plan, int lend, Action<int> body)
    {
        PlanNode? node = _plan?.Access(s, plan, _planNote);
        _planNote = null;
        if (node is not null && node.CounterId >= 0)
        {
            // Counting: one instruction when the loop starts and one for each row that enters its body.
            Emit(Op.PlanCount, node.CounterId, 0);
            Action<int> inner = body;
            int counter = node.CounterId;
            body = next =>
            {
                Emit(Op.PlanCount, counter, 1);
                inner(next);
            };
        }

        try
        {
            EmitSourceLoopCore(s, plan, lend, body);
        }
        finally
        {
            if (node is not null)
            {
                _plan!.Pop(node);
            }
        }
    }

    private void EmitSourceLoopCore(Source s, AccessPlan plan, int lend, Action<int> body)
    {
        TableInfo t = s.Table;
        int cur = s.Cursor;
        if (s.Kind == SourceKind.Eph)
        {
            Emit(Op.ERewind, cur, lend);
            int eloop = NewLabel(), enext = NewLabel();
            Mark(eloop);
            body(enext);
            Mark(enext);
            Emit(Op.ENext, cur, eloop);
            return;
        }

        if (s.Kind == SourceKind.Recursive)
        {
            EmitRecursiveLoop(s, lend, body);
            return;
        }

        if (s.Kind == SourceKind.Module)
        {
            EmitModuleLoop(s, lend, body);
            return;
        }

        if (s.Kind == SourceKind.VTab)
        {
            EmitVirtualLoop(s, lend, body);
            return;
        }

        switch (plan.Kind)
        {
            case PlanKind.Scan:
            {
                Emit(Op.OpenRead, cur);
                Emit(plan.Reverse ? Op.Last : Op.Rewind, cur, lend);
                int loop = NewLabel(), next = NewLabel();
                Mark(loop);
                body(next);
                Mark(next);
                Emit(plan.Reverse ? Op.Prev : Op.Next, cur, loop);
                return;
            }

            case PlanKind.RowidEq:
            {
                Emit(Op.OpenRead, cur);
                int key = KeyReg(plan.Equal[0].Value, Affinity.Integer);
                int next = NewLabel();
                Emit(Op.SeekRowid, cur, lend, key);
                body(next);
                Mark(next);
                return;
            }

            case PlanKind.RowidIn:
            {
                if (!EmitInLoop(s, plan, lend, out InLoop inLoop))
                {
                    EmitSourceLoopCore(s, new AccessPlan { Kind = PlanKind.Scan }, lend, body);
                    return;
                }

                Emit(Op.OpenRead, cur);
                Emit(Op.SeekRowid, cur, inLoop.Next, inLoop.Value);
                body(inLoop.Next);
                Mark(inLoop.Next);
                Emit(Op.ENext, inLoop.Cursor, inLoop.Top);
                return;
            }

            case PlanKind.RowidRange:
            {
                Emit(Op.OpenRead, cur);
                int lo = 0, hi = 0;
                if (plan.Lower is not null)
                {
                    lo = KeyReg(plan.Lower.Value, Affinity.Integer);
                    Emit(Op.IfNull, lo, lend);
                }

                if (plan.Upper is not null)
                {
                    hi = KeyReg(plan.Upper.Value, Affinity.Integer);
                    Emit(Op.IfNull, hi, lend);
                }

                if (plan.Lower is not null)
                {
                    Emit(plan.Lower.Cmp == BinaryOp.Gt ? Op.SeekGT : Op.SeekGE, cur, lend, lo);
                }
                else
                {
                    Emit(Op.Rewind, cur, lend);
                }

                int loop = NewLabel(), next = NewLabel();
                Mark(loop);
                if (plan.Upper is not null)
                {
                    int rid = NewReg(), stop = NewReg();
                    Emit(Op.Rowid, cur, rid);
                    Emit(plan.Upper.Cmp == BinaryOp.Lt ? Op.Ge : Op.Gt, rid, hi, stop, null, CmpFlags(Affinity.Numeric, CollKind.Binary));
                    Emit(Op.If, stop, lend, 0);
                }

                body(next);
                Mark(next);
                Emit(Op.Next, cur, loop);
                return;
            }

            default:
            {
                IndexInfo idx = plan.Index!;
                int icur;
                bool isIn = plan.Kind == PlanKind.IndexIn;
                InLoop inLoop = default;
                if (isIn && !EmitInLoop(s, plan, lend, out inLoop))
                {
                    EmitSourceLoopCore(s, new AccessPlan { Kind = PlanKind.Scan }, lend, body);
                    return;
                }

                // With IN, running out of entries for one value moves on to the next value; otherwise it ends the loop.
                int stop = isIn ? NewLabel() : lend;

                // A secondary index of a WITHOUT ROWID table: each entry ends with the primary key columns the index does not hold already
                // (fileformat2 §1.6), which locate the table row.
                bool wrSecondary = t.WithoutRowid && !ReferenceEquals(idx, t.PrimaryKey);
                if (t.WithoutRowid && !wrSecondary)
                {
                    Emit(Op.OpenRead, cur);
                    icur = cur;
                }
                else
                {
                    Emit(Op.OpenRead, cur);
                    _cursors.Add(new CursorSpec { Table = t, Index = idx, IsIndexTree = true, Root = idx.Root });
                    icur = _cursors.Count - 1;
                    Emit(Op.OpenRead, icur);
                }

                int k = isIn ? 1 : plan.Equal.Count;
                int nLow = k + (plan.Lower is not null ? 1 : 0);
                int nUp = k + (plan.Upper is not null ? 1 : 0);
                int lowBlock = NewRegs(Math.Max(nLow, 1));
                int upBlock = NewRegs(Math.Max(nUp, 1));
                for (int i = 0; i < k; i++)
                {
                    int r = isIn ? inLoop.Value : KeyReg(plan.Equal[i].Value, t.Columns[idx.Columns[i]].Affinity, true);
                    Emit(Op.IfNull, r, stop);
                    Emit(Op.Copy, r, lowBlock + i);
                    Emit(Op.Copy, r, upBlock + i);
                }

                if (plan.Lower is not null)
                {
                    int r = KeyReg(plan.Lower.Value, t.Columns[idx.Columns[k]].Affinity, true);
                    Emit(Op.IfNull, r, stop);
                    Emit(Op.Copy, r, lowBlock + k);
                }

                if (plan.Upper is not null)
                {
                    int r = KeyReg(plan.Upper.Value, t.Columns[idx.Columns[k]].Affinity, true);
                    Emit(Op.IfNull, r, stop);
                    Emit(Op.Copy, r, upBlock + k);
                }

                // A descending range column lists its large values first: the scan starts at the upper bound and ends past the lower one
                // (the key comparison already follows the index's own order).
                bool descRange = (plan.Lower is not null || plan.Upper is not null) && idx.Desc.Length > k && idx.Desc[k];
                int startBlock = descRange ? upBlock : lowBlock, startN = descRange ? nUp : nLow;
                int endBlock = descRange ? lowBlock : upBlock, endN = descRange ? nLow : nUp;
                Term? startBound = descRange ? plan.Upper : plan.Lower, endBound = descRange ? plan.Lower : plan.Upper;
                bool startStrict = startBound is { Cmp: BinaryOp.Gt or BinaryOp.Lt };
                bool endStrict = endBound is { Cmp: BinaryOp.Gt or BinaryOp.Lt };
                if (plan.Reverse)
                {
                    Emit(Op.Last, icur, stop);
                }
                else if (startN == 0)
                {
                    Emit(Op.Rewind, icur, stop);
                }
                else
                {
                    Emit(startStrict ? Op.SeekGT : Op.SeekGE, icur, stop, startBlock, null, startN);
                }

                int loop = NewLabel(), next = NewLabel();
                Mark(loop);
                if (endN > 0)
                {
                    // Leave the loop once the entry is past the end key: >= for a strict bound, > otherwise.
                    Emit(descRange ? (endStrict ? Op.IdxGE : Op.IdxGT) : (plan.Upper is { Cmp: BinaryOp.Lt } ? Op.IdxGE : Op.IdxGT), icur, stop, endBlock, null, endN);
                }

                if (wrSecondary)
                {
                    int[] pkCols = t.PrimaryKey!.Columns;
                    int pkBase = NewRegs(pkCols.Length);
                    var tail = new List<int>();
                    foreach (int pc in pkCols)
                    {
                        if (Array.IndexOf(idx.Columns, pc) < 0)
                        {
                            tail.Add(pc);
                        }
                    }

                    for (int j = 0; j < pkCols.Length; j++)
                    {
                        int at = Array.IndexOf(idx.Columns, pkCols[j]);
                        Emit(Op.Column, icur, at >= 0 ? at : idx.Columns.Length + tail.IndexOf(pkCols[j]), pkBase + j);
                    }

                    Emit(Op.IdxProbe, cur, next, pkBase, null, pkCols.Length);
                }
                else if (!t.WithoutRowid)
                {
                    int rid = NewReg();
                    Emit(Op.IdxRowid, icur, rid);
                    Emit(Op.SeekRowid, cur, next, rid);
                }

                body(next);
                Mark(next);
                Emit(plan.Reverse ? Op.Prev : Op.Next, icur, loop);
                if (isIn)
                {
                    Mark(stop);
                    Mark(inLoop.Next);
                    Emit(Op.ENext, inLoop.Cursor, inLoop.Top);
                }

                return;
            }
        }
    }
}
