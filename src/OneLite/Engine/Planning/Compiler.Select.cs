using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite.Planning;

/// <summary>How a FROM item is read.</summary>
internal enum SourceKind : byte
{
    /// <summary>A B-tree table.</summary>
    Table,

    /// <summary>An ephemeral table filled before the loops start (a subquery, a CTE, the current row of a recursive CTE).</summary>
    Eph,

    /// <summary>A recursive CTE: the loop is the queue, and each row it yields feeds the recursive part.</summary>
    Recursive,

    /// <summary>A table-valued function (json_each, json_tree, generate_series): rows come from a module cursor.</summary>
    Module,

    /// <summary>A virtual table (CREATE VIRTUAL TABLE, or an eponymous module): rows come from a module cursor planned with its best-index.</summary>
    VTab,
}

/// <summary>One item of a FROM clause: a table or an ephemeral table, under its alias.</summary>
internal sealed class Source
{
    internal string Alias = "";
    internal TableInfo Table = null!;
    internal int Cursor;

    /// <summary>While the body of a covered index loop is emitted: the index cursor the columns are read from, and the index (see AccessPlan.Covered).</summary>
    internal int CoverCursor = -1;
    internal IndexInfo? CoverIndex;
    internal SourceKind Kind;

    /// <summary>Position in the FROM clause as written.</summary>
    internal int Index;

    /// <summary>The join between this item and everything before it.</summary>
    internal JoinFlags Join;

    /// <summary>ON conditions of an outer join (they decide matches rather than filter rows).</summary>
    internal List<Expr> On = [];

    /// <summary>Columns left out of <c>*</c> because a USING or NATURAL join merged them into an earlier item.</summary>
    internal HashSet<int> Hidden = [];

    /// <summary>For the left item of a RIGHT or FULL join: merged columns read as coalesce(this, other).</summary>
    internal Dictionary<int, (Source Other, int Col)> Coalesce = [];

    /// <summary>Set index of the "matched" rows of a RIGHT or FULL join's right item.</summary>
    internal int MatchSet = -1;

    /// <summary>NOT INDEXED: no index is used to find rows.</summary>
    internal bool NotIndexed;

    /// <summary>INDEXED BY: the index to use.</summary>
    internal IndexInfo? Forced;

    internal RecursiveCte? Rec;

    /// <summary>For <see cref="SourceKind.Module"/>: the function, its call arguments (they feed the first hidden columns) and the hidden columns left out of <c>*</c>.</summary>
    internal ModuleDef? Module;
    internal ImmutableArray<Expr> ModuleArgs;

    /// <summary>Joined with CROSS JOIN, which keeps the written order of the loops.</summary>
    internal bool CrossJoined;

    /// <summary>For a module: hidden columns fixed by an <c>= expr</c> term of the WHERE clause, which the function takes as arguments.</summary>
    internal Dictionary<int, Expr>? ModuleEq;

    /// <summary>For <see cref="SourceKind.VTab"/>: the plan the module chose.</summary>
    internal VtPlan? Vt;
    internal bool IsOuterNullFill => (Join & (JoinFlags.Left | JoinFlags.Full)) != 0;
    internal bool IsRightSide => (Join & (JoinFlags.Right | JoinFlags.Full)) != 0;
}

/// <summary>A scope for name resolution: the FROM items of one SELECT, with the enclosing SELECT as parent.</summary>
internal sealed class Scope
{
    internal Scope? Parent;
    internal int Depth;
    internal List<Source> Sources = [];

    /// <summary>Result column aliases, visible in WHERE, GROUP BY, HAVING and ORDER BY (not in the result columns themselves).</summary>
    internal Dictionary<string, Expr> Aliases = new(AsciiComparer.Instance);
    internal bool AliasesVisible;

    /// <summary>True while a clause that cannot hold aggregates is being resolved: WHERE and ON, or GROUP BY and the ORDER BY of a plain query.</summary>
    internal AggClause NoAgg;

    /// <summary>True while the result columns of a query not yet known to be an aggregate query are resolved: an aggregate of this query found inside a subquery makes it one.</summary>
    internal bool CanBecomeAgg;

    /// <summary>True for the query directly under EXISTS: the reference ignores the aggregates of an enclosing query in its result columns and ORDER BY.</summary>
    internal bool IgnoreOuterAgg;

    /// <summary>True when the query is an aggregate query (aggregates in its result columns or ORDER BY, or a GROUP BY).</summary>
    internal bool IsAgg;
}

internal enum AggClause : byte { None, Filter, Other, Order }

/// <summary>Raised when a subquery holds an aggregate that belongs to an enclosing query that was not yet planned as an aggregate query (lang_aggfunc).</summary>
internal sealed class OuterAggregateException(Scope owner, string name) : Exception
{
    internal string Name { get; } = name;
    internal Scope Owner { get; } = owner;
}

/// <summary>A name introduced by WITH.</summary>
internal sealed class CteDef
{
    internal string Name = "";
    internal CommonTableExpression Ast = null!;
    internal CteNode? Env;
    internal bool Recursive;
    internal CteNode? EnvForBody;

    /// <summary>While the recursive part is being compiled: the cursor holding the row being expanded.</summary>
    internal int CurrentCursor = -1;
    internal TableInfo? Shape;
}

internal sealed class CteNode
{
    internal CteDef Def = null!;
    internal CteNode? Next;
}

/// <summary>What a recursive CTE loop needs besides its source.</summary>
internal sealed class RecursiveCte
{
    internal CteDef Def = null!;
    internal int QueueCursor;
    internal int CurrentCursor;
    internal List<SimpleSelect> Initial = [];
    internal List<SimpleSelect> Step = [];
    internal bool Distinct;
    internal SelectStatement Select = null!;
    internal string[] ColumnNames = [];
}

internal enum DestKind : byte
{
    /// <summary>The statement's result rows.</summary>
    Output,

    /// <summary>Append to an ephemeral table.</summary>
    Eph,

    /// <summary>Append to an ephemeral table unless an identical row was queued before (UNION in a recursive CTE).</summary>
    EphDistinct,

    /// <summary>Add to a set.</summary>
    Set,

    /// <summary>The first row's first column, then stop (a scalar subquery).</summary>
    Scalar,

    /// <summary>Set a flag, then stop (EXISTS).</summary>
    Exists,
}

/// <summary>Where the rows of a SELECT go.</summary>
internal sealed class Dest
{
    internal DestKind Kind;
    internal int Cursor;
    internal int Reg;
    internal int Set;

    /// <summary>For an IN (SELECT ...) set: the affinity applied to stored values.</summary>
    internal Affinity Aff;

    /// <summary>For the set behind <c>x IN (SELECT ...)</c>: the affinity of x, from which the stored side's conversion follows once the SELECT's columns are known.</summary>
    internal bool UseOperandAff;
    internal Affinity OperandAff;

    /// <summary>For the same set: the collation of x when it has one (a column's or an explicit COLLATE), otherwise null.</summary>
    internal CollKind? OperandColl;

    /// <summary>For a set: an equal row replaces the one already there (UNION keeps the last of equal rows).</summary>
    internal bool Replace;

    /// <summary>LIMIT and OFFSET registers and the exit label of a compound select whose rows stream through this destination.</summary>
    internal int LimReg, OffReg, End;

    internal Dest WithLimit(int lim, int off, int end) => new() { Kind = Kind, Cursor = Cursor, Reg = Reg, Set = Set, Aff = Aff, Replace = Replace, UseOperandAff = UseOperandAff, OperandAff = OperandAff, LimReg = lim, OffReg = off, End = end };
}

/// <summary>What a SELECT's result columns look like to whoever reads them.</summary>
internal sealed class SelectMeta
{
    internal string[] Names = [];
    internal Affinity[] Aff = [];
    internal CollKind[] Coll = [];
    internal string?[] CollNames = [];

    /// <summary>Per result column, the declared type of the table column it is a plain reference to (see DeclTypeOf).</summary>
    internal string[] Decl = [];

    /// <summary>Per result column, the table column it is a plain reference to (sqlite3_column_database_name and friends); null for an expression.</summary>
    internal ColumnOrigin?[] Origins = [];
}

internal sealed partial class Compiler
{
    private Scope? _scope;
    private CteNode? _cteEnv;
    private int _anonCount;
    private int _onceCount;
    private int _sorterCount;
    private int _setCount;
    private readonly List<AggSpec> _aggSpecs = [];
    private bool _touchesFile;
    private readonly List<Probe> _probes = [];

    private sealed class Probe
    {
        internal int InnerDepth;
        internal bool Correlated;

        /// <summary>The plan node recorded for the subquery, when a plan is being recorded.</summary>
        internal PlanNode? PlanNode;

        /// <summary>When set, the FROM items of enclosing queries the subquery reads are collected here.</summary>
        internal HashSet<Source>? Outer;
    }

    // ------------------------------------------------------------------ entry

    /// <summary>Compiles a SELECT statement.</summary>
    internal VmProgram CompileSelect(SelectStatement s)
    {
        var prog = new VmProgram { Encoding = _enc };
        int tx = Emit(Op.Transaction, 0, 0, 0);
        var dest = new Dest { Kind = DestKind.Output };
        SelectMeta meta;
        try
        {
            meta = CompileSelectStmt(s, dest);
        }
        catch (OneLiteException ex) when (ex.Message.StartsWith("no such column", StringComparison.Ordinal) && MissingTableMessage(s) is { } missing)
        {
            throw Err(missing);
        }

        Emit(Op.Halt);
        if (!_touchesFile)
        {
            _code[tx] = new Instr { Op = Op.Noop };
        }

        prog.Code = Finish();
        prog.RegisterCount = _nReg + 1;
        prog.Cursors = [.. _cursors];
        prog.ColumnNames = meta.Names;
        prog.ColumnOrigin = meta.Origins.Length == meta.Names.Length ? meta.Origins : new ColumnOrigin?[meta.Names.Length];
        prog.ResultBase = _outBase;
        prog.ResultCount = meta.Names.Length;
        prog.ParameterNames = [.. _params];
        prog.OnceCount = _onceCount;
        prog.SorterCount = _sorterCount;
        prog.SetCount = _setCount;
        prog.Aggregates = [.. _aggSpecs];
        prog.WindowStages = [.. _winStages];
        return prog;
    }

    private int NewSorter() => _sorterCount++;

    private int NewSet() => _setCount++;

    private int NewEphCursor()
    {
        _cursors.Add(new CursorSpec { Ephemeral = true });
        return _cursors.Count - 1;
    }

    // ------------------------------------------------------------------ statements, WITH, compound

    private SelectMeta CompileSelectStmt(SelectStatement s, Dest dest)
    {
        CteNode? saved = _cteEnv;
        try
        {
            if (s.With is { } w)
            {
                RegisterWith(w);
            }

            if (s.Cores.Length == 1)
            {
                return CompileOne(s.Cores[0], dest, true);
            }

            PlanNode? compound = _plan?.Push(PlanNodeKind.Compound, string.Join(" ", s.Operators.Select(CompoundName)));
            try
            {
                return CompileCompound(s, dest);
            }
            finally
            {
                if (compound is not null)
                {
                    _plan!.Pop(compound);
                }
            }
        }
        finally
        {
            _cteEnv = saved;
        }
    }

    private void RegisterWith(WithClause w)
    {
        var defs = new List<CteDef>();
        var seen = new HashSet<string>(AsciiComparer.Instance);
        foreach (CommonTableExpression cte in w.Tables)
        {
            if (!seen.Add(cte.Name.Text))
            {
                throw Err("duplicate WITH table name: " + cte.Name.Text);
            }

            var def = new CteDef { Name = cte.Name.Text, Ast = cte, Env = _cteEnv };
            def.Recursive = SelectReferences(cte.Select, def.Name);
            var node = new CteNode { Def = def, Next = _cteEnv };
            def.EnvForBody = def.Recursive ? node : _cteEnv;
            _cteEnv = node;
            defs.Add(def);
        }

        // A CTE may read one written after it in the same WITH clause (lang_with); only a cycle is an error, which OpenCte reports.
        foreach (CteDef def in defs)
        {
            if (!def.Recursive)
            {
                def.EnvForBody = _cteEnv;
            }
        }
    }

    private CteDef? FindCte(string name)
    {
        for (CteNode? n = _cteEnv; n is not null; n = n.Next)
        {
            if (AsciiComparer.Instance.Equals(n.Def.Name, name))
            {
                return n.Def;
            }
        }

        return null;
    }

    /// <summary>Whether a SELECT reads a table of this name in a FROM clause (at any depth).</summary>
    private static bool SelectReferences(SelectStatement s, string name)
    {
        foreach (SelectCore c in s.Cores)
        {
            if (c is SimpleSelect { From: { } f } && FromReferences(f, name))
            {
                return true;
            }
        }

        return false;
    }

    private static bool FromReferences(FromClause f, string name)
    {
        foreach (FromItem it in f.Items)
        {
            switch (it.Source)
            {
                case NamedTable nt when nt.Name.Schema is null && AsciiComparer.Instance.Equals(nt.Name.Name.Text, name):
                    return true;
                case SubquerySource sq when SelectReferences(sq.Select, name):
                    return true;
                case ParenFrom pf when FromReferences(pf.From, name):
                    return true;
            }
        }

        return false;
    }

    private SelectMeta CompileOne(SelectCore core, Dest dest, bool applyOrderLimit)
    {
        return core switch
        {
            SimpleSelect ss => CompileSimple(ss, dest, applyOrderLimit),
            ValuesCore vc => CompileValues(vc, dest),
            _ => throw Unsupported(core.GetType().Name),
        };
    }

    private SelectMeta CompileValues(ValuesCore vc, Dest dest)
    {
        int n = vc.Rows[0].Length;
        foreach (ImmutableArray<Expr> row in vc.Rows)
        {
            if (row.Length != n)
            {
                throw Err("all VALUES must have the same number of terms");
            }
        }

        var scope = new Scope { Parent = _scope, Depth = NextDepth() };
        Scope? savedScope = _scope;
        _scope = scope;
        try
        {
            int end = NewLabel();
            int bas = dest.Kind == DestKind.Output ? OutBase(n) : NewRegs(n);
            var meta = new SelectMeta { Names = new string[n], Aff = new Affinity[n], Coll = new CollKind[n], CollNames = new string?[n] };
            for (int i = 0; i < n; i++)
            {
                meta.Names[i] = "column" + (i + 1).ToString(CultureInfo.InvariantCulture);
            }

            bool first = true;
            foreach (ImmutableArray<Expr> row in vc.Rows)
            {
                for (int i = 0; i < n; i++)
                {
                    CompileInto(row[i], bas + i);
                }

                if (first)
                {
                    first = false;
                }

                EmitDest(dest, bas, n, 0, 0, end, 0);
            }

            Mark(end);
            return meta;
        }
        finally
        {
            _scope = savedScope;
        }
    }

    private int _outBase;

    /// <summary>The registers the statement's result row is read from (the same for every ResultRow).</summary>
    private int OutBase(int n)
    {
        if (_outBase == 0)
        {
            _outBase = NewRegs(n);
        }

        return _outBase;
    }

    private static string CompoundName(CompoundOperator op) => op switch
    {
        CompoundOperator.Union => "UNION",
        CompoundOperator.UnionAll => "UNION ALL",
        CompoundOperator.Intersect => "INTERSECT",
        _ => "EXCEPT",
    };

    private SelectMeta CompileCompound(SelectStatement s, Dest dest)
    {
        try
        {
            return CompileCompoundCore(s, dest);
        }
        catch (OneLiteException) when (s.Cores.Length > 1)
        {
            // Observed: the reference prepares the sides of a compound from the last to the first, so when several sides are wrong the
            // error it reports is the right-most one. The code built so far is abandoned with the error.
            OneLiteException? rightmost = null;
            for (int j = s.Cores.Length - 1; j >= 1 && rightmost is null; j--)
            {
                try
                {
                    CompileOne(s.Cores[j], new Dest { Kind = DestKind.Eph, Cursor = NewEphCursor() }, false);
                }
                catch (OneLiteException later)
                {
                    rightmost = later;
                }
            }

            if (rightmost is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(rightmost).Throw();
            }

            throw;
        }
    }

    private SelectMeta CompileCompoundCore(SelectStatement s, Dest dest)
    {
        int last = s.Cores.Length - 1;
        for (int i = 0; i < last; i++)
        {
            if (s.Cores[i] is SimpleSelect { OrderBy.IsDefaultOrEmpty: false })
            {
                throw Err("ORDER BY clause should come after " + CompoundName(s.Operators[i]) + " not before");
            }
        }

        for (int i = 0; i < last; i++)
        {
            if (s.Cores[i] is SimpleSelect { Limit: not null })
            {
                throw Err("LIMIT clause should come after " + CompoundName(s.Operators[i]) + " not before");
            }
        }

        ImmutableArray<OrderingTerm> order = s.Cores[last] is SimpleSelect ls && !ls.OrderBy.IsDefault ? ls.OrderBy : ImmutableArray<OrderingTerm>.Empty;
        LimitClause? limit = (s.Cores[last] as SimpleSelect)?.Limit;
        bool plain = order.IsDefaultOrEmpty && limit is null;
        bool allAll = s.Operators.All(o => o == CompoundOperator.UnionAll) && !dest.UseOperandAff;

        // The compound's own LIMIT is evaluated once, before any row (lang_select "LIMIT clause").
        int limReg = 0, offReg = 0;
        EmitLimitRegs(limit, ref limReg, ref offReg);
        int end = NewLabel();
        if (limReg != 0)
        {
            Emit(Op.IfNot, limReg, end, 0);
        }

        if (allAll && order.IsDefaultOrEmpty)
        {
            // UNION ALL only: rows stream straight to the destination.
            SelectMeta? first = null;
            var inner = dest;
            for (int i = 0; i < s.Cores.Length; i++)
            {
                SelectMeta m = CompileOneInCompound(s, i, inner.WithLimit(limReg, offReg, end), s.Operators.Length > 0 ? s.Operators[Math.Min(i, s.Operators.Length - 1)] : CompoundOperator.UnionAll);
                first ??= m;
                CheckColumns(s, i, first, m);
            }

            Mark(end);
            return first!;
        }

        // General case: fold left to right through an ephemeral table (lang_select "compound select").
        int cur = NewEphCursor();
        Emit(Op.EphOpen, cur);
        SelectMeta? meta0 = null;
        var d0 = new Dest { Kind = DestKind.Eph, Cursor = cur };
        SelectMeta? prevMeta = null;
        for (int i = 0; i < s.Cores.Length; i++)
        {
            if (i == 0)
            {
                meta0 = CompileOneInCompound(s, 0, d0, CompoundOperator.UnionAll);
                prevMeta = meta0;
                continue;
            }

            CompoundOperator op = s.Operators[i - 1];
            int n = meta0!.Names.Length;
            var spec = new SetSpec { Collations = meta0.Coll };
            switch (op)
            {
                case CompoundOperator.UnionAll:
                {
                    SelectMeta m = CompileOneInCompound(s, i, d0, op);
                    CheckColumns(s, i, meta0, m);
                    prevMeta = Accumulate(prevMeta, m);
                    break;
                }

                case CompoundOperator.Union:
                {
                    int set = NewSet();
                    Emit(Op.SetOpen, set, 0, 0, spec);
                    EmitEphToSet(cur, set, n);
                    SelectMeta m = CompileOneInCompound(s, i, new Dest { Kind = DestKind.Set, Set = set, Replace = true }, op);
                    CheckColumns(s, i, meta0, m);
                    spec.Collations = StageCollations(prevMeta, m);
                    int next = NewEphCursor();
                    Emit(Op.SetToEph, set, next);
                    cur = next;
                    d0 = new Dest { Kind = DestKind.Eph, Cursor = cur };
                    prevMeta = Accumulate(prevMeta, m);
                    break;
                }

                default:
                {
                    int right = NewSet();
                    Emit(Op.SetOpen, right, 0, 0, spec);
                    SelectMeta m = CompileOneInCompound(s, i, new Dest { Kind = DestKind.Set, Set = right }, op);
                    CheckColumns(s, i, meta0, m);
                    spec.Collations = StageCollations(prevMeta, m);
                    int result = NewSet();
                    Emit(Op.SetOpen, result, 0, 0, spec);
                    int regs = NewRegs(n);
                    int endLoop = NewLabel();
                    Emit(Op.ERewind, cur, endLoop);
                    int loop = NewLabel(), skip = NewLabel();
                    Mark(loop);
                    for (int c = 0; c < n; c++)
                    {
                        Emit(Op.EColumn, cur, c, regs + c);
                    }

                    if (op == CompoundOperator.Intersect)
                    {
                        // keep rows found in the right set
                        int keep = NewLabel();
                        Emit(Op.SetFound, right, keep, regs, null, n);
                        Emit(Op.Goto, 0, skip);
                        Mark(keep);
                    }
                    else
                    {
                        Emit(Op.SetFound, right, skip, regs, null, n);
                    }

                    Emit(Op.SetAdd, result, regs, n);
                    Mark(skip);
                    Emit(Op.ENext, cur, loop);
                    Mark(endLoop);
                    int next = NewEphCursor();
                    Emit(Op.SetToEph, result, next);
                    cur = next;
                    d0 = new Dest { Kind = DestKind.Eph, Cursor = cur };
                    prevMeta = Accumulate(prevMeta, m);
                    break;
                }
            }
        }

        // Read the folded rows back out, ordered if the last SELECT asked for it.
        int cols = meta0!.Names.Length;
        if (dest.UseOperandAff)
        {
            dest.Aff = Values.InSetAffinity(dest.OperandAff, meta0.Aff[0]);
        }

        EmitCompoundOutput(cur, meta0, order, dest, limReg, offReg, end);
        Mark(end);
        _ = cols;
        return meta0;
    }

    private SelectMeta CompileOneInCompound(SelectStatement s, int i, Dest dest, CompoundOperator op)
    {
        PlanNode? arm = _plan?.Push(PlanNodeKind.Arm, (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (arm is not null)
        {
            arm.Detail = i == 0 ? "(first)" : "(" + CompoundName(op) + ")";
        }

        _ = op;
        // Observed: UNION, INTERSECT and EXCEPT order each side by all its result columns and merge, so the rows of a side are read the way
        // an ORDER BY on all its columns would read them (through an index when one serves, which decides which of equal rows is kept).
        CompoundOperator adjacent = s.Operators.Length == 0 ? CompoundOperator.UnionAll : s.Operators[i == 0 ? 0 : i - 1];
        _armDistinct = adjacent != CompoundOperator.UnionAll;
        try
        {
            return CompileOne(s.Cores[i], dest, false);
        }
        finally
        {
            if (arm is not null)
            {
                _plan!.Pop(arm);
            }
            _armDistinct = false;
        }
    }

    /// <summary>The collations a compound step compares rows under: a column's collation in the left SELECT if it has one, else the right one's.</summary>
    private static CollKind[] StageCollations(SelectMeta? left, SelectMeta right)
    {
        var res = new CollKind[right.Names.Length];
        for (int j = 0; j < res.Length; j++)
        {
            res[j] = left is not null && j < left.CollNames.Length && left.CollNames[j] is not null ? left.Coll[j] : right.Coll[j];
        }

        return res;
    }

    /// <summary>What the next compound step sees to its left: each column's collation is the leftmost explicit one among the SELECTs so far (lang_select "compound").</summary>
    private static SelectMeta Accumulate(SelectMeta? left, SelectMeta right)
    {
        var res = new SelectMeta { Names = right.Names, Aff = right.Aff, Coll = StageCollations(left, right), CollNames = new string?[right.Names.Length] };
        for (int j = 0; j < res.CollNames.Length; j++)
        {
            res.CollNames[j] = left is not null && j < left.CollNames.Length && left.CollNames[j] is not null ? left.CollNames[j] : right.CollNames[j];
        }

        return res;
    }

    private static void CheckColumns(SelectStatement s, int i, SelectMeta first, SelectMeta m)
    {
        if (m.Names.Length != first.Names.Length)
        {
            throw Err("SELECTs to the left and right of " + CompoundName(s.Operators[i - 1]) + " do not have the same number of result columns");
        }
    }

    private void EmitEphToSet(int cur, int set, int n)
    {
        int regs = NewRegs(n);
        int endLoop = NewLabel(), loop = NewLabel();
        Emit(Op.ERewind, cur, endLoop);
        Mark(loop);
        for (int c = 0; c < n; c++)
        {
            Emit(Op.EColumn, cur, c, regs + c);
        }

        Emit(Op.SetAdd, set, regs, n, null, 0);
        Emit(Op.ENext, cur, loop);
        Mark(endLoop);
    }
}
