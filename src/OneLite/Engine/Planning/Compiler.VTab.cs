using System.Collections.Immutable;
using System.Linq;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Sql.Parsing;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Vm;
using RecursiveAI.OneLite.VirtualTables;

namespace RecursiveAI.OneLite.Planning;

/// <summary>How a scan of a virtual table was planned: the module's plan numbers and the expressions that become the arguments of the scan.</summary>
internal sealed class VtPlan
{
    internal TableInfo Table = null!;
    internal int IndexNumber;
    internal string? IndexString;

    /// <summary>The right-hand values, in argv order.</summary>
    internal List<Expr> Args = [];
}

/// <summary>What the VtFilter instruction needs besides its registers.</summary>
internal sealed class VtFilterSpec
{
    internal int IndexNumber;
    internal string? IndexString;
}

/// <summary>What the VtCreate instruction needs: the user module and the table it creates.</summary>
internal sealed class VtCreateSpec
{
    internal required string Module { get; init; }
    internal required string Schema { get; init; }
    internal required string Name { get; init; }
    internal required ImmutableArray<string> Args { get; init; }
}

// Virtual tables (vtab): CREATE VIRTUAL TABLE, DROP, scans with best-index negotiation. DML is in Compiler.VTabDml.cs.
internal sealed partial class Compiler
{
    private void EnsureVirtual(TableInfo t)
    {
        if (t.Virtual is null)
        {
            return;
        }

        Connection owner = _cat.Owner ?? throw Err("no such table: " + t.Name);
        t.Virtual.Schema = _cat.SchemaName(t.Db);
        owner.ConnectVirtual(t);
    }

    /// <summary>A virtual table named in FROM (or an eponymous module): its columns come from the module's declaration.</summary>
    private Source OpenVirtual(TableInfo t, string alias, ImmutableArray<Expr> callArgs)
    {
        EnsureVirtual(t);
        int hidden = t.Columns.Count(c => c.Hidden);
        if (callArgs.Length > hidden)
        {
            throw Err($"too many arguments on {t.Name}() - max {hidden}");
        }

        _touchesFile = true;
        _cursors.Add(new CursorSpec { Table = t, Root = 0 });
        return new Source { Alias = alias, Table = t, Cursor = _cursors.Count - 1, Kind = SourceKind.VTab, ModuleArgs = callArgs };
    }

    /// <summary>An eponymous module used as a table (<c>FROM dbstat</c>, <c>FROM dbstat('main')</c>), or null if <paramref name="name"/> is no such module.</summary>
    private TableInfo? EponymousTable(string name)
    {
        bool eponymous = VirtualModules.FindBuiltin(name) is { Eponymous: true } || _cat.Owner?.FindUserModule(name) is { IsEponymous: true };
        if (!eponymous)
        {
            return null;
        }

        return new TableInfo { Name = name, Virtual = new VirtualInfo { Module = name, Args = [], Schema = "main" } };
    }

    // ------------------------------------------------------------------ CREATE VIRTUAL TABLE

    private VmProgram CompileCreateVirtual(CreateVirtualTableStatement cv)
    {
        PreassignParameters();
        int db = CreateDb(cv.Name.Schema?.Text, false);
        string name = cv.Name.Name.Text;
        if (name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase) && !WritableSchema)
        {
            throw Err("object name reserved for internal use: " + name);
        }

        if (NameInUse(name, db))
        {
            if (cv.IfNotExists)
            {
                return EmptyProgram();
            }

            throw Err(AlreadyExists(name, db));
        }

        string module = cv.Module.Text;
        BuiltinModule? bm = VirtualModules.FindBuiltin(module);
        if (bm is FunctionModule || (bm is null && _cat.Owner?.FindUserModule(module) is null))
        {
            throw Err("no such module: " + module);
        }

        string schemaName = _cat.SchemaName(db);
        int pageSize = _cat.Owner?.PagerOf(db).PageSize ?? 4096;
        ShadowTable[] shadows = bm?.ShadowTables(schemaName, name, cv.Arguments, pageSize) ?? [];
        foreach (ShadowTable sh in shadows)
        {
            if (NameInUse(sh.Name, db))
            {
                throw Err("table \"" + sh.Name + "\" already exists");
            }
        }

        _forceTemp = db == 1;
        TableInfo master = _cat.MasterOf(db);
        Emit(Op.Transaction, 0, 0, 0, null, 1);
        int mc = AddCursor(new CursorSpec { Table = master, Root = 1 });
        Emit(Op.OpenWrite, mc);
        EmitSchemaRow(mc, master, "table", name, name, 0, SchemaText("CREATE VIRTUAL TABLE ", cv));
        foreach (ShadowTable sh in shadows)
        {
            int root = NewReg();
            Emit(Op.CreateBtree, root, sh.Sql.EndsWith("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase) ? 0 : 1, db);
            EmitSchemaRow(mc, master, "table", sh.Name, sh.Name, root, sh.Sql);

            // PRIMARY KEY and UNIQUE constraints of a shadow table get their automatic indexes, as in CREATE TABLE.
            if (SqlParser.ParseOne(sh.Sql, 0, out _) is CreateTableStatement shCt)
            {
                TableInfo shBuilt = SchemaBuilder.BuildTable(shCt, 0, null);
                for (int i = 0; i < shBuilt.AutoIndexes.Count; i++)
                {
                    if (shBuilt.AutoIndexes[i].Columns.Length == 0)
                    {
                        continue;
                    }

                    int ir = NewReg();
                    Emit(Op.CreateBtree, ir, 0, db);
                    EmitSchemaRow(mc, master, "index", "sqlite_autoindex_" + sh.Name + "_" + (i + 1), sh.Name, ir, null);
                }
            }
            if (sh.Rows.Length > 0)
            {
                var ct = (CreateTableStatement)SqlParser.ParseOne(sh.Sql, 0, out _)!;
                TableInfo shape = SchemaBuilder.BuildTable(ct, 0, sh.Sql);
                int cur = AddCursor(new CursorSpec { Table = shape, Index = shape.WithoutRowid ? shape.PrimaryKey : null, IsIndexTree = shape.WithoutRowid, Root = 0 });
                Emit(Op.OpenWrite, cur, root);
                foreach ((long rowid, object?[] values) in sh.Rows)
                {
                    int b = NewRegs(Math.Max(values.Length, 1));
                    for (int i = 0; i < values.Length; i++)
                    {
                        switch (values[i])
                        {
                            case long l:
                                Emit(Op.Integer, 0, b + i, 0, l);
                                break;
                            case double d:
                                Emit(Op.Real, 0, b + i, 0, d);
                                break;
                            case string s:
                                Emit(Op.String8, 0, b + i, 0, Utf8(s));
                                break;
                            case byte[] blob:
                                Emit(Op.Blob, 0, b + i, 0, blob);
                                break;
                            default:
                                Emit(Op.Null, 0, b + i);
                                break;
                        }
                    }

                    if (shape.WithoutRowid)
                    {
                        Emit(Op.IdxInsert, cur, b, shape.RecordWidth, null, 0);
                        continue;
                    }

                    int rid = NewReg();
                    Emit(Op.Integer, 0, rid, 0, rowid);
                    Emit(Op.Insert, cur, b, rid, shape, 0);
                }
            }
        }

        if (bm is null)
        {
            Emit(Op.VtCreate, 0, 0, 0, new VtCreateSpec { Module = module, Schema = schemaName, Name = name, Args = cv.Arguments });
        }

        Emit(Op.BumpCookie, db);
        Emit(Op.Halt);
        return DdlProgram();
    }

    /// <summary>The part of DROP TABLE that is specific to a virtual table: the module's own cleanup, then its shadow tables.</summary>
    private void EmitDropVirtual(TableInfo t, int mc)
    {
        VirtualInfo v = t.Virtual!;
        EnsureVirtual(t);
        Emit(Op.VtDestroy, 0, 0, 0, t);
        Emit(Op.SchemaDelete, mc, 0, t.Db == 1 ? 5 : 0, t.Name);
        if (VirtualModules.FindBuiltin(v.Module) is { } bm)
        {
            var roots = new List<(uint Root, bool IsIndex)>();
            foreach (string shName in bm.ShadowNames(t.Name, v.Args))
            {
                if (_cat.FindIn(_cat.SchemaName(t.Db), shName) is { } sh)
                {
                    Emit(Op.SchemaDelete, mc, 0, t.Db == 1 ? 5 : 0, shName);
                    roots.Add((sh.Root, false));
                    foreach (IndexInfo ix in sh.Indexes)
                    {
                        if (ix.Root != 0)
                        {
                            roots.Add((ix.Root, true));
                        }
                    }
                }
            }

            if (roots.Count > 0)
            {
                EmitDestroyAll(mc, roots);
            }
        }
    }

    // ------------------------------------------------------------------ scans

    /// <summary>Adds what is not a WHERE term to the constraints offered: the LIMIT, and the arguments of a table-valued call (equalities on the hidden columns, in order).</summary>
    private void AddVtLimitAndArgs(Source s, List<VtTerm> terms, VTab vtab, HashSet<Source> bound, List<Expr> where)
    {
        // A LIMIT with nothing else in the way is offered to a table that asks for it: one source, no offset, every WHERE term one the table can see.
        if (vtab.AcceptsExtendedConstraints && _vtLimit is { } lim && _scope is { Sources.Count: 1 } && !s.IsOuterNullFill && s.On.Count == 0
            && where.All(w => terms.Any(t => ReferenceEquals(t.Conjunct, w))) && VtUsable(lim, s, bound))
        {
            terms.Add(new VtTerm { Conjunct = lim, Column = -1, Op = ConstraintOp.Limit, Value = lim, Usable = true });
        }

        // The arguments of a table-valued call fill the hidden columns in order (they behave as equality terms).
        var hidden = new List<int>();
        for (int i = 0; i < s.Table.Columns.Length; i++)
        {
            if (s.Table.Columns[i].Hidden)
            {
                hidden.Add(i);
            }
        }

        for (int i = 0; i < s.ModuleArgs.Length; i++)
        {
            terms.Add(new VtTerm { Conjunct = new BinaryExpr(BinaryOp.Eq, new ColumnRef(null, new Name(s.Alias), new Name(s.Table.Columns[hidden[i]].Name)), s.ModuleArgs[i]), Column = hidden[i], Op = ConstraintOp.Eq, Value = s.ModuleArgs[i], Usable = true, FromArg = true });
        }
    }

    /// <summary>The ORDER BY offered to the virtual table of a one-table SELECT, and the tables whose module said it delivers rows in that order.</summary>
    private readonly Dictionary<Source, IndexOrderBy[]> _vtOrderOffer = new(ReferenceEqualityComparer.Instance);

    private readonly HashSet<Source> _vtOrderConsumed = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Offers the ORDER BY of a one-table SELECT to the virtual table and reports whether it will deliver the rows in that order (vtab: orderByConsumed),
    /// so no sorter is needed. Only terms that are plain columns of the table are offered; otherwise nothing is.
    /// </summary>
    private bool VtabConsumesOrder(Source s, Expr? where, List<Expr> joinPool, ImmutableArray<OrderingTerm> order, List<(Expr Expr, string Name)> cols)
    {
        var offer = new IndexOrderBy[order.Length];
        for (int k = 0; k < order.Length; k++)
        {
            OrderingTerm ot = order[k];
            if (ot.Nulls != NullsOrder.None)
            {
                return false;
            }

            int pos = ResolveOrderTerm(ot.Expr, cols, k);
            Expr e = pos >= 0 ? cols[pos].Expr : ot.Expr;
            if (pos >= 0 && StripParens(ot.Expr) is CollateExpr)
            {
                return false;
            }

            e = StripParens(e);
            if (e is not ColumnRef || ColumnOf(e, s) is not { } col)
            {
                return false;
            }

            offer[k] = new IndexOrderBy(col, ot.Order == SortOrder.Desc);
        }

        var cand = new List<Expr>();
        if (where is not null)
        {
            cand.AddRange(Conjuncts(where));
        }

        foreach (Expr j in joinPool)
        {
            cand.AddRange(Conjuncts(j));
        }

        var whereOnly = new List<Expr>(cand);
        cand.AddRange(s.On);
        VTab vtab = _cat.Owner!.ConnectVirtual(s.Table);
        var none = new HashSet<Source>(ReferenceEqualityComparer.Instance);
        List<VtTerm> terms = VtTerms(s, cand, none, vtab.AcceptsExtendedConstraints);
        AddVtLimitAndArgs(s, terms, vtab, none, whereOnly);
        var info = new BestIndexInfo([.. terms.Select(t => new IndexConstraint(t.Column, t.Op, t.Usable))], offer);
        try
        {
            vtab.BestIndex(info);
        }
        catch (OneLiteException)
        {
            // The real planning pass reports it.
            return false;
        }

        if (!info.OrderByConsumed)
        {
            return false;
        }

        _vtOrderOffer[s] = offer;
        _vtOrderConsumed.Add(s);
        return true;
    }

    /// <summary>The LIMIT of the SELECT being planned when it can be handed to a virtual table; otherwise null.</summary>
    private Expr? _vtLimit;

    private sealed class VtTerm
    {
        internal Expr Conjunct = null!;
        internal int Column;
        internal ConstraintOp Op;
        internal Expr Value = null!;
        internal bool Usable;

        /// <summary>Not a WHERE term: a table-function argument for a hidden column.</summary>
        internal bool FromArg;
    }

    private static ConstraintOp? VtOp(BinaryOp op) => op switch
    {
        BinaryOp.Eq => ConstraintOp.Eq,
        BinaryOp.Ne => ConstraintOp.Ne,
        BinaryOp.Lt => ConstraintOp.Lt,
        BinaryOp.Le => ConstraintOp.Le,
        BinaryOp.Gt => ConstraintOp.Gt,
        BinaryOp.Ge => ConstraintOp.Ge,
        BinaryOp.Is => ConstraintOp.Is,
        BinaryOp.IsNot => ConstraintOp.IsNot,
        _ => null,
    };

    private static ConstraintOp FlipVt(ConstraintOp op) => op switch
    {
        ConstraintOp.Lt => ConstraintOp.Gt,
        ConstraintOp.Le => ConstraintOp.Ge,
        ConstraintOp.Gt => ConstraintOp.Lt,
        ConstraintOp.Ge => ConstraintOp.Le,
        _ => op,
    };

    private bool VtUsable(Expr value, Source s, HashSet<Source> bound)
    {
        var refs = new HashSet<Source>(ReferenceEqualityComparer.Instance);
        bool saved = _subqueryKnown;
        _subqueryKnown = true;
        try
        {
            return _scope is not null && CollectSources(value, _scope, refs) && !refs.Contains(s) && refs.IsSubsetOf(bound);
        }
        finally
        {
            _subqueryKnown = saved;
        }
    }

    private void AddVtTerm(List<VtTerm> terms, Source s, Expr conjunct, Expr colSide, Expr valSide, ConstraintOp op, HashSet<Source> bound)
    {
        if (ColumnOf(colSide, s) is { } col)
        {
            terms.Add(new VtTerm { Conjunct = conjunct, Column = col, Op = op, Value = valSide, Usable = VtUsable(valSide, s, bound) });
        }
    }

    private List<VtTerm> VtTerms(Source s, List<Expr> cand, HashSet<Source> bound, bool extended)
    {
        var terms = new List<VtTerm>();
        foreach (Expr c0 in cand)
        {
            Expr c = c0;
            while (c is ParenExpr p)
            {
                c = p.Inner;
            }

            switch (c)
            {
                case BinaryExpr { Op: BinaryOp.Is or BinaryOp.IsNot } nb when nb.Right is LiteralExpr { Kind: LiteralKind.Null } && ColumnOf(nb.Left, s) is { } nc:
                    terms.Add(new VtTerm { Conjunct = c0, Column = nc, Op = nb.Op == BinaryOp.Is ? ConstraintOp.IsNull : ConstraintOp.IsNotNull, Value = nb.Right, Usable = true });
                    break;
                case BinaryExpr b when VtOp(b.Op) is { } op:
                    if (ColumnOf(b.Left, s) is not null && ColumnOf(b.Right, s) is null)
                    {
                        AddVtTerm(terms, s, c0, b.Left, b.Right, op, bound);
                    }
                    else if (ColumnOf(b.Right, s) is not null && ColumnOf(b.Left, s) is null)
                    {
                        AddVtTerm(terms, s, c0, b.Right, b.Left, FlipVt(op), bound);
                    }

                    break;
                case BetweenExpr { Negated: false } bt when ColumnOf(bt.Operand, s) is not null && ColumnOf(bt.Low, s) is null && ColumnOf(bt.High, s) is null:
                    // x BETWEEN a AND b is offered as x >= a and x <= b; the term is only dropped when the table takes both.
                    AddVtTerm(terms, s, c0, bt.Operand, bt.Low, ConstraintOp.Ge, bound);
                    AddVtTerm(terms, s, c0, bt.Operand, bt.High, ConstraintOp.Le, bound);
                    break;
                case InListExpr { Negated: false } il when extended && il.Items.Length is > 0 and <= 100 && ColumnOf(il.Operand, s) is { } inCol
                    && il.Items.All(i => ColumnOf(i, s) is null && VtUsable(i, s, bound)):
                    // x IN (a, b, ...) is offered as one In term per element, for tables that ask for it.
                    foreach (Expr item in il.Items)
                    {
                        terms.Add(new VtTerm { Conjunct = c0, Column = inCol, Op = ConstraintOp.In, Value = item, Usable = true });
                    }

                    break;
                case NullTestExpr nt when ColumnOf(nt.Operand, s) is { } c1:
                    terms.Add(new VtTerm { Conjunct = c0, Column = c1, Op = nt.Test == NullTest.IsNull ? ConstraintOp.IsNull : ConstraintOp.IsNotNull, Value = new LiteralExpr(LiteralKind.Null, "NULL"), Usable = true });
                    break;
                case LikeExpr { Negated: false, Escape: null } l when ColumnOf(l.Left, s) is not null && ColumnOf(l.Pattern, s) is null:
                {
                    ConstraintOp lop = l.Op switch { LikeOp.Like => ConstraintOp.Like, LikeOp.Glob => ConstraintOp.Glob, LikeOp.Regexp => ConstraintOp.Regexp, _ => ConstraintOp.Match };
                    AddVtTerm(terms, s, c0, l.Left, l.Pattern, lop, bound);
                    break;
                }
            }
        }

        return terms;
    }

    /// <summary>
    /// Asks the module for a plan for the source at its place in the loop nest. Terms the module takes as arguments and promises to enforce are
    /// removed from <paramref name="where"/> and the ON terms; every other term stays and is tested on the rows the scan returns.
    /// </summary>
    private void PlanVirtual(Source s, List<Expr> where, HashSet<Source> bound)
    {
        _vtOrderOffer.TryGetValue(s, out IndexOrderBy[]? offer);
        var cand = new List<Expr>();
        if (!s.IsOuterNullFill)
        {
            cand.AddRange(where);
        }

        cand.AddRange(s.On);
        VTab vtab = _cat.Owner!.ConnectVirtual(s.Table);
        List<VtTerm> terms = VtTerms(s, cand, bound, vtab.AcceptsExtendedConstraints);

        AddVtLimitAndArgs(s, terms, vtab, bound, where);

        var info = new BestIndexInfo([.. terms.Select(t => new IndexConstraint(t.Column, t.Op, t.Usable))], offer ?? []);
        try
        {
            vtab.BestIndex(info);
        }
        catch (OneLiteException e) when (e.Code == ResultCode.Constraint)
        {
            throw Err("no query solution");
        }

        if (offer is not null && _vtOrderConsumed.Contains(s) && !info.OrderByConsumed)
        {
            // The sort was dropped on the strength of the first answer, so a module must give the same one again.
            throw Err("xBestIndex malfunction");
        }

        var plan = new VtPlan { Table = s.Table, IndexNumber = info.IndexNumber, IndexString = info.IndexString };
        var ordered = new SortedDictionary<int, VtTerm>();
        for (int i = 0; i < terms.Count; i++)
        {
            IndexConstraintUsage u = info.Usage[i];
            if (u.ArgvIndex < 0 || u.ArgvIndex > terms.Count || (u.ArgvIndex > 0 && !terms[i].Usable))
            {
                throw Err("xBestIndex malfunction");
            }

            if (u.ArgvIndex > 0)
            {
                if (!ordered.TryAdd(u.ArgvIndex, terms[i]))
                {
                    throw Err("xBestIndex malfunction");
                }
            }

            bool consumed = u.ArgvIndex > 0 && u.Omit;
            if (consumed)
            {
                // A term that was offered as several constraints (BETWEEN, IN) is dropped only when the table took every one of them.
                bool all = true;
                for (int j = 0; j < terms.Count; j++)
                {
                    if (ReferenceEquals(terms[j].Conjunct, terms[i].Conjunct) && !(info.Usage[j].ArgvIndex > 0 && info.Usage[j].Omit))
                    {
                        all = false;
                    }
                }

                if (all)
                {
                    where.Remove(terms[i].Conjunct);
                    s.On.Remove(terms[i].Conjunct);
                }
            }
            else if (terms[i].FromArg)
            {
                // A call argument the module did not enforce is tested like any other equality.
                where.Add(terms[i].Conjunct);
            }
        }

        int expect = 1;
        foreach ((int k, VtTerm t) in ordered)
        {
            if (k != expect++)
            {
                throw Err("xBestIndex malfunction");
            }

            plan.Args.Add(t.Value);
        }

        s.Vt = plan;
    }

    /// <summary>A virtual table's loop: the argument values go into consecutive registers, VtFilter starts the scan, VNext advances it.</summary>
    private void EmitVirtualLoop(Source s, int lend, Action<int> body)
    {
        VtPlan plan = s.Vt!;
        int count = plan.Args.Count;
        int first = count == 0 ? 0 : NewRegs(count);
        for (int i = 0; i < count; i++)
        {
            CompileInto(plan.Args[i], first + i);
        }

        int cur = s.Cursor;
        Emit(Op.VtFilter, cur, lend, first, new VtFilterSpec { IndexNumber = plan.IndexNumber, IndexString = plan.IndexString }, count);
        int loop = NewLabel(), next = NewLabel();
        Mark(loop);
        body(next);
        Mark(next);
        Emit(Op.VNext, cur, loop);
    }
}
