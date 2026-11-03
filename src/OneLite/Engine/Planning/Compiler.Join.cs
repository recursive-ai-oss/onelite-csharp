using RecursiveAI.OneLite.VirtualTables;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite.Planning;

/// <summary>One nested loop of a join: a source, the WHERE terms tested once it is positioned, and how it is read.</summary>
internal sealed class LoopLevel
{
    internal Source S = null!;
    internal List<Expr> Where = [];

    /// <summary>Terms evaluated into a register when the level's row is reached, and tested later.</summary>
    internal List<(Expr Expr, int Reg)> Compute = [];
    internal AccessPlan Plan = null!;
}

internal sealed partial class Compiler
{
    private readonly Dictionary<Source, AccessPlan> _planByCursor = [];

    // ------------------------------------------------------------------ FROM

    private static void FlattenFrom(FromClause from, List<FromItem> items, List<Expr> extraOn)
    {
        foreach (FromItem it in from.Items)
        {
            if (it.Source is ParenFrom pf)
            {
                if (pf.Alias is not null || it.UsingColumns.Length > 0 || (it.Join is { } j && (j.Flags & (JoinFlags.Left | JoinFlags.Right | JoinFlags.Full | JoinFlags.Natural)) != 0))
                {
                    throw Unsupported("parenthesized FROM clauses");
                }

                int first = items.Count;
                FlattenFrom(pf.From, items, extraOn);
                if (items.Count > first)
                {
                    FromItem head = items[first];
                    items[first] = head with { Join = it.Join ?? head.Join };
                }

                if (it.On is not null)
                {
                    extraOn.Add(it.On);
                }

                continue;
            }

            items.Add(it);
        }
    }

    private const string NestedMarker = "nested";

    /// <summary>Materializes a subquery of a FROM clause; an error raised inside is marked as nested for the CTE error-order rule.</summary>
    private Source MaterializeFromSubquery(SubquerySource sq)
    {
        try
        {
            return MaterializeSubquery(sq.Select, sq.Alias?.Text ?? "(subquery-" + (++_anonCount).ToString(CultureInfo.InvariantCulture) + ")", default);
        }
        catch (OneLiteException ex)
        {
            ex.Data[NestedMarker] = true;
            throw;
        }
    }

    /// <summary>Opens the FROM items (emitting the code that fills ephemeral ones) and returns the conditions of inner joins.</summary>
    private List<Expr> BuildFrom(FromClause? from, Scope scope)
    {
        var pool = new List<Expr>();
        if (from is null)
        {
            return pool;
        }

        var items = new List<FromItem>();
        FlattenFrom(from, items, pool);
        for (int i = 0; i < items.Count; i++)
        {
            FromItem item = items[i];
            Source s;
            try
            {
                s = item.Source switch
                {
                    NamedTable nt => OpenNamed(nt, scope),
                    SubquerySource sq => MaterializeFromSubquery(sq),
                    TableFunction tf => OpenModule(tf),
                    _ => throw Unsupported("parenthesized FROM clauses"),
                };
            }
            catch (OneLiteException ex) when (item.Source is NamedTable { Name.Schema: null } failed && FindCte(failed.Name.Name.Text) is not null
                && !ex.Message.StartsWith("no such table", StringComparison.Ordinal)
                && !ex.Data.Contains(NestedMarker))
            {
                // The reference goes on resolving the following items after an error found directly in a CTE's body after its FROM items resolved (not one inside a
                // FROM subquery of the body, and not a missing table), without that WITH clause in scope, and reports the last error it hit:
                // the last later item that names a CTE.
                for (int k = items.Count - 1; k > i; k--)
                {
                    if (items[k].Source is NamedTable { Name.Schema: null } later && FindCte(later.Name.Name.Text) is not null)
                    {
                        throw Err("no such table: " + later.Name.Name.Text);
                    }
                }

                throw;
            }

            s.Index = i;
            JoinFlags jf = item.Join?.Flags ?? JoinFlags.None;
            s.Join = jf & (JoinFlags.Left | JoinFlags.Right | JoinFlags.Full | JoinFlags.Natural);
            s.CrossJoined = (jf & JoinFlags.Cross) != 0;
            scope.Sources.Add(s);
            if (i == 0)
            {
                continue;
            }

            bool outer = (s.Join & (JoinFlags.Left | JoinFlags.Right | JoinFlags.Full)) != 0;
            List<Expr> target = outer ? s.On : pool;
            List<string> usingCols = [];
            if ((s.Join & JoinFlags.Natural) != 0)
            {
                foreach (ColumnInfo c in s.Table.Columns)
                {
                    if (scope.Sources.Take(i).Any(l => l.Table.FindColumn(c.Name) >= 0 && !l.Hidden.Contains(l.Table.FindColumn(c.Name))))
                    {
                        usingCols.Add(c.Name);
                    }
                }
            }
            else if (item.UsingColumns.Length > 0)
            {
                foreach (Name n in item.UsingColumns)
                {
                    if (s.Table.FindColumn(n.Text) < 0 || !scope.Sources.Take(i).Any(l => l.Table.FindColumn(n.Text) >= 0))
                    {
                        throw Err("cannot join using column " + n.Text + " - column not present in both tables");
                    }

                    usingCols.Add(n.Text);
                }
            }

            foreach (string col in usingCols)
            {
                // Observed: a column found in several earlier items is only ambiguous when a RIGHT or FULL join is involved (it would need a COALESCE across them).
                bool rightInvolved = s.IsRightSide || scope.Sources.Take(i).Any(l => l.IsRightSide);
                if (rightInvolved && scope.Sources.Take(i).Count(l => l.Table.FindColumn(col) >= 0 && !l.Hidden.Contains(l.Table.FindColumn(col))) > 1)
                {
                    throw Err("ambiguous reference to " + col + " in USING()");
                }

                Source left = scope.Sources.Take(i).First(l => l.Table.FindColumn(col) >= 0 && !l.Hidden.Contains(l.Table.FindColumn(col)));
                int rc = s.Table.FindColumn(col);
                int lc = left.Table.FindColumn(col);
                target.Add(new BinaryExpr(BinaryOp.Eq, new ColumnRef(null, new Name(left.Alias), new Name(left.Table.Columns[lc].Name)), new ColumnRef(null, new Name(s.Alias), new Name(col))));
                s.Hidden.Add(rc);
                if ((s.Join & (JoinFlags.Right | JoinFlags.Full)) != 0)
                {
                    left.Coalesce[lc] = (s, rc);
                }
            }

            if (item.On is not null)
            {
                target.AddRange(Conjuncts(item.On));
            }
        }

        return pool;
    }

    private Source OpenNamed(NamedTable nt, Scope scope)
    {
        _ = scope;
        string? schema = nt.Name.Schema?.Text;
        string name = nt.Name.Name.Text;
        if (schema is null && FindCte(name) is { } def)
        {
            return OpenCte(def, nt.Alias?.Text ?? name);
        }

        TableInfo? t = _cat.FindIn(schema, name);
        if (t is null && (schema is null || AsciiComparer.Instance.Equals(schema, "main")) && Modules.Find(name) is null)
        {
            t = EponymousTable(name);
        }

        if (t is { Virtual: not null })
        {
            return OpenVirtual(t, nt.Alias?.Text ?? t.Name, []);
        }

        if (t is null && schema is null && Modules.Find(name) is not null)
        {
            // A table-valued function named like a table takes its arguments from the WHERE clause (hidden columns).
            return OpenModule(new TableFunction(nt.Name, [], nt.Alias));
        }

        if (t is null)
        {
            throw Err("no such table: " + (schema is null ? (_viewDepth > 0 || InTrigger ? "main." + name : name) : schema + "." + name));
        }

        if (t.Unsupported is not null)
        {
            throw Unsupported(t.Unsupported);
        }

        if (t.View is not null)
        {
            return OpenView(t, nt.Alias?.Text ?? t.Name);
        }

        // lang_indexedby: NOT INDEXED keeps indexes from being used to find rows (the rowid still can be); INDEXED BY names the one to use.
        IndexInfo? forced = null;
        if (nt.Index is { IndexName: { } hint })
        {
            forced = t.Indexes.Find(x => AsciiComparer.Instance.Equals(x.Name, hint.Text));
            if (forced is null)
            {
                throw Err("no such index: " + hint.Text);
            }
        }

        _touchesFile = true;
        _cursors.Add(new CursorSpec { Table = t, Index = t.WithoutRowid ? t.PrimaryKey : null, IsIndexTree = t.WithoutRowid, Root = t.Root });
        return new Source { Alias = nt.Alias?.Text ?? t.Name, Table = t, Cursor = _cursors.Count - 1, Kind = SourceKind.Table, NotIndexed = nt.Index is { IndexName: null }, Forced = forced };
    }

    private readonly List<CteDef> _cteActive = [];

    private int _viewDepth;
    private readonly List<TableInfo> _viewsActive = [];

    // lang_createview: a view is a stored SELECT, run as a subquery of the referencing statement. Its body sees neither the caller's
    // WITH clause nor its FROM items; table names missing inside it are reported with their schema ("no such table: main.t").
    private Source OpenView(TableInfo v, string alias)
    {
        if (_viewsActive.Contains(v))
        {
            throw Err("view " + v.Name + " is circularly defined");
        }

        CteNode? savedEnv = _cteEnv;
        Scope? savedScope = _scope;
        int savedFloor = _depthFloor;
        string savedSql = _sql;
        _sql = v.ViewSql;
        _viewsActive.Add(v);
        _viewDepth++;
        try
        {
            _depthFloor = NextDepth();
            _scope = null;
            _cteEnv = null;
            return MaterializeSubquery(v.View!.Select, alias, v.View.Columns, v.Name, view: true);
        }
        finally
        {
            _sql = savedSql;
            _viewDepth--;
            _viewsActive.Remove(v);
            _cteEnv = savedEnv;
            _scope = savedScope;
            _depthFloor = savedFloor;
        }
    }

    private Source OpenCte(CteDef def, string alias)
    {
        if (def.CurrentCursor >= 0)
        {
            // The recursive part reads the row being expanded.
            return new Source { Alias = alias, Table = def.Shape!, Cursor = def.CurrentCursor, Kind = SourceKind.Eph };
        }

        CteNode? savedEnv = _cteEnv;
        Scope? savedScope = _scope;
        int savedFloor = _depthFloor;
        try
        {
            // The body can't see the enclosing FROM items, but its scopes keep counting depth from here, so a subquery probe
            // around this CTE isn't mistaken for a correlated one by the body's own column references.
            _depthFloor = NextDepth();
            _scope = null;
            if (!def.Recursive)
            {
                if (_cteActive.Contains(def))
                {
                    throw Err("circular reference: " + def.Name);
                }

                _cteActive.Add(def);
                try
                {
                    _cteEnv = def.EnvForBody;
                    return MaterializeSubquery(def.Ast.Select, alias, def.Ast.Columns, def.Name);
                }
                finally
                {
                    _cteActive.Remove(def);
                }
            }

            return OpenRecursive(def, alias);
        }
        finally
        {
            _cteEnv = savedEnv;
            _scope = savedScope;
            _depthFloor = savedFloor;
        }
    }

    private int _depthFloor;

    private int NextDepth() => _scope is null ? _depthFloor : _scope.Depth + 1;

    private Source OpenRecursive(CteDef def, string alias)
    {
        SelectStatement sel = def.Ast.Select;
        if (sel.Cores.Length < 2)
        {
            throw Err("circular reference: " + def.Name);
        }

        var rec = new RecursiveCte { Def = def, Select = sel };
        for (int i = 0; i < sel.Cores.Length; i++)
        {
            if (sel.Cores[i] is not SimpleSelect ss)
            {
                throw Unsupported("VALUES in a recursive common table expression");
            }

            if (ss.From is { } f && FromReferences(f, def.Name))
            {
                rec.Step.Add(ss);
            }
            else if (rec.Step.Count > 0)
            {
                throw Err("recursive reference in a subquery: " + def.Name);
            }
            else
            {
                rec.Initial.Add(ss);
            }
        }

        foreach (CompoundOperator op in sel.Operators)
        {
            if (op is CompoundOperator.Intersect or CompoundOperator.Except)
            {
                throw Err("recursive reference in a subquery: " + def.Name);
            }

            rec.Distinct |= op == CompoundOperator.Union;
        }

        if (rec.Initial.Count == 0)
        {
            throw Err("circular reference: " + def.Name);
        }

        if (sel.Cores[^1] is SimpleSelect { OrderBy.IsDefaultOrEmpty: false })
        {
            throw Unsupported("ORDER BY in a recursive common table expression");
        }

        // The column names come from the first initial SELECT.
        _cteEnv = def.Env;
        SelectMeta? peek = null;
        DryRun(() => peek = CompileOne(rec.Initial[0], new Dest { Kind = DestKind.Eph, Cursor = 0 }, false));
        string[] names = [.. peek!.Names];
        if (!def.Ast.Columns.IsDefaultOrEmpty)
        {
            if (def.Ast.Columns.Length != names.Length)
            {
                throw Err($"table {def.Name} has {names.Length} values for {def.Ast.Columns.Length} columns");
            }

            names = [.. def.Ast.Columns.Select(c => c.Text)];
        }

        rec.ColumnNames = names;
        def.Shape = SynthTable(alias, names, new Affinity[names.Length], new string?[names.Length]);
        rec.QueueCursor = NewEphCursor();
        rec.CurrentCursor = NewEphCursor();
        return new Source { Alias = alias, Table = def.Shape, Cursor = rec.QueueCursor, Kind = SourceKind.Recursive, Rec = rec };
    }

    private Source MaterializeSubquery(SelectStatement sel, string alias, ImmutableArray<Name> colNames, string? cteName = null, bool view = false)
    {
        HashSet<string>? need = _neededFromOuter;
        _neededFromOuter = null;
        int cur = NewEphCursor();
        // A subquery that names nothing of the enclosing query gives the same rows every time: fill it once per statement, not once
        // per row of an enclosing correlated subquery.
        _ = BeginSubquery(out Probe probe, out int guard, PlanNodeKind.Materialize, alias,
            view ? "(view " + cteName + ")" : cteName is not null ? "(common table expression " + cteName + ")" : "(subquery in FROM)");
        SelectMeta meta;
        bool savedNames = _namesForTable;
        _namesForTable = true;
        try
        {
            Emit(Op.EphOpen, cur);
            _coverNames = need is not null && colNames.IsDefaultOrEmpty && QualifiesForMerge(sel) ? need : null;
            meta = CompileSelectStmt(sel, new Dest { Kind = DestKind.Eph, Cursor = cur });
        }
        finally
        {
            _namesForTable = savedNames;
            EndSubquery(probe, guard);
        }

        string[] names = meta.Names;
        if (!colNames.IsDefaultOrEmpty)
        {
            if (colNames.Length != names.Length)
            {
                throw Err(view ? $"expected {colNames.Length} columns for '{cteName}' but got {names.Length}" : $"table {cteName ?? alias} has {names.Length} values for {colNames.Length} columns");
            }

            names = [.. colNames.Select(c => c.Text)];
        }

        TableInfo ti = SynthTable(alias, names, meta.Aff, meta.CollNames, colNames.IsDefaultOrEmpty || colNames.Length == names.Length ? meta.Decl : null);
        if (meta.Origins.Length == ti.Columns.Length)
        {
            for (int i = 0; i < ti.Columns.Length; i++)
            {
                ti.Columns[i].Origin = meta.Origins[i];
            }
        }

        return new Source { Alias = alias, Table = ti, Cursor = cur, Kind = SourceKind.Eph };
    }

    /// <summary>A table-valued function in FROM: its columns come from the module; the arguments are compiled where the loop is emitted.</summary>
    private Source OpenModule(TableFunction tf)
    {
        string name = tf.Name.Name.Text;
        ModuleDef? def = tf.Name.Schema is null || AsciiComparer.Instance.Equals(tf.Name.Schema.Text, "main") ? Modules.Find(name) : null;
        if (def is null && (tf.Name.Schema is null || AsciiComparer.Instance.Equals(tf.Name.Schema.Text, "main")) && (_cat.FindIn(null, name) ?? EponymousTable(name)) is { Virtual: not null } vt)
        {
            return OpenVirtual(vt, tf.Alias?.Text ?? name, tf.Args);
        }

        if (def is null)
        {
            throw Err("no such table: " + (tf.Name.Schema is { } sch ? sch.Text + "." : "") + name);
        }

        if (tf.Args.Length > def.MaxArgs)
        {
            throw Err($"too many arguments on {def.Name}() - max {def.MaxArgs}");
        }

        _cursors.Add(new CursorSpec { Module = def });
        TableInfo ti = SynthTable(tf.Alias?.Text ?? name, def.Columns, new Affinity[def.Columns.Length], new string?[def.Columns.Length]);
        return new Source { Alias = tf.Alias?.Text ?? name, Table = ti, Cursor = _cursors.Count - 1, Kind = SourceKind.Module, Module = def, ModuleArgs = tf.Args };
    }

    private static TableInfo SynthTable(string alias, string[] names, Affinity[] aff, string?[] coll, string[]? decl = null)
    {
        var cols = new ColumnInfo[names.Length];
        var seen = new Dictionary<string, int>(AsciiComparer.Instance);
        for (int i = 0; i < cols.Length; i++)
        {
            string nm = names[i];
            if (seen.TryGetValue(nm, out int count))
            {
                // Repeated names get ":N" suffixes (sqlite_schema-free behaviour of subquery columns).
                seen[nm] = count + 1;
                nm += ":" + (count).ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                seen[nm] = 1;
            }

            cols[i] = new ColumnInfo { Name = nm, Affinity = aff[i], CollationName = coll[i], RecordIndex = i, TypeText = decl is not null && i < decl.Length ? decl[i] : "" };
        }

        return new TableInfo { Name = alias, Columns = cols };
    }

    // ------------------------------------------------------------------ the nest of loops

    private HashSet<Source> SourcesOf(Expr e, Scope scope)
    {
        var found = new HashSet<Source>();
        CollectSources(e, scope, found);
        return found;
    }

    /// <summary>The FROM items of <paramref name="scope"/> an expression reads. A subquery counts as reading every item of every scope (it may be correlated to any).</summary>
    private bool CollectSources(Expr e, Scope scope, HashSet<Source> found)
    {
        switch (e)
        {
            case ColumnRef cr:
                if (Resolve(cr, out Source? s, out _, out _, false) && s is not null && scope.Sources.Contains(s))
                {
                    found.Add(s);
                }

                return true;
            case LiteralExpr or VariableExpr:
                return true;
            case ParenExpr p:
                return CollectSources(p.Inner, scope, found);
            case UnaryExpr u:
                return CollectSources(u.Operand, scope, found);
            case BinaryExpr b:
                return CollectSources(b.Left, scope, found) & CollectSources(b.Right, scope, found);
            case CollateExpr c:
                return CollectSources(c.Operand, scope, found);
            case CastExpr c:
                return CollectSources(c.Operand, scope, found);
            case BetweenExpr bt:
                return CollectSources(bt.Operand, scope, found) & CollectSources(bt.Low, scope, found) & CollectSources(bt.High, scope, found);
            case InListExpr il:
            {
                bool ok = CollectSources(il.Operand, scope, found);
                foreach (Expr i in il.Items)
                {
                    ok &= CollectSources(i, scope, found);
                }

                return ok;
            }

            case NullTestExpr n:
                return CollectSources(n.Operand, scope, found);
            case LikeExpr l:
                return CollectSources(l.Left, scope, found) & CollectSources(l.Pattern, scope, found) & (l.Escape is null || CollectSources(l.Escape, scope, found));
            case CaseExpr ce:
            {
                bool ok = ce.Operand is null || CollectSources(ce.Operand, scope, found);
                foreach (WhenClause w in ce.Whens)
                {
                    ok &= CollectSources(w.When, scope, found) & CollectSources(w.Then, scope, found);
                }

                return ok & (ce.Else is null || CollectSources(ce.Else, scope, found));
            }

            case FunctionCall f:
            {
                bool ok = f.Filter is null || CollectSources(f.Filter, scope, found);
                foreach (Expr a in f.Args)
                {
                    ok &= CollectSources(a, scope, found);
                }

                return ok;
            }

            case RowValueExpr rv:
            {
                bool ok = true;
                foreach (Expr a in rv.Items)
                {
                    ok &= CollectSources(a, scope, found);
                }

                return ok;
            }

            case ExistsExpr or SubqueryExpr or InSelectExpr:
            {
                // A subquery reads exactly the items its correlated columns name; finding them lets the term run in an early loop.
                if (SubquerySources(e, scope) is { } used)
                {
                    found.UnionWith(used);
                    return _subqueryKnown;
                }

                goto default;
            }

            default:
            {
                // Anything unknown: assume it reads every item of this scope.
                foreach (Source s2 in scope.Sources)
                {
                    found.Add(s2);
                }

                return false;
            }
        }
    }

    /// <summary>
    /// When set, <see cref="CollectSources"/> reports success for a subquery whose correlated items it could tell (it normally reports false, so callers
    /// that only want columns keep treating a subquery as opaque). A virtual table's constraint operand may be a subquery (vtab: xBestIndex).
    /// </summary>
    private bool _subqueryKnown;

    private readonly Dictionary<Expr, (Scope Scope, HashSet<Source>? Used)> _subquerySources = new(ReferenceEqualityComparer.Instance);

    /// <summary>Compiles a subquery expression without keeping the code, to learn which items of <paramref name="scope"/> it reads; null if that can't be told.</summary>
    private HashSet<Source>? SubquerySources(Expr e, Scope scope)
    {
        // The same expression can be compiled more than once (a trial compile builds its own FROM items), so the answer belongs to one scope.
        if (_subquerySources.TryGetValue(e, out (Scope Scope, HashSet<Source>? Used) known) && ReferenceEquals(known.Scope, scope))
        {
            return known.Used;
        }

        var probe = new Probe { InnerDepth = scope.Depth + 1, Outer = [] };
        Scope? saved = _scope;
        _probes.Add(probe);
        HashSet<Source>? result = null;
        try
        {
            _scope = scope;
            DryRun(() => CompileInto(e, NewReg()));
            result = new HashSet<Source>(probe.Outer.Where(scope.Sources.Contains));
        }
        catch (OneLiteException)
        {
            // The real compile reports the error in its own order.
        }
        finally
        {
            _scope = saved;
            _probes.Remove(probe);
        }

        _subquerySources[e] = (scope, result);
        return result;
    }

    private void EmitNest(Scope scope, Expr? where, List<Expr> joinPool, Action body)
    {
        List<Source> sources = scope.Sources;
        var conj = new List<Expr>();
        var whereConj = new List<Expr>();
        if (where is not null)
        {
            whereConj.AddRange(Conjuncts(where));
            conj.AddRange(whereConj);
        }

        var poolConj = new List<Expr>();
        foreach (Expr j in joinPool)
        {
            poolConj.AddRange(Conjuncts(j));
        }

        conj.AddRange(poolConj);

        // Terms that read columns are tested before terms that run a subquery, so a join condition rejects a row before its subquery is run.
        bool Cheap(Expr e) => CollectSources(e, scope, []);
        conj = [.. conj.OrderBy(e => Cheap(e) ? 0 : 1)];
        whereConj = [.. whereConj.OrderBy(e => Cheap(e) ? 0 : 1)];

        int nestEnd = NewLabel();
        if (sources.Count == 0)
        {
            foreach (Expr c in conj)
            {
                int r = CompileCondition(c);
                Emit(Op.IfNot, r, nestEnd, 1);
            }

            body();
            Mark(nestEnd);
            return;
        }

        List<Source> order = ChooseOrder(scope, conj);
        var levels = new List<LoopLevel>();
        foreach (Source s in order)
        {
            levels.Add(new LoopLevel { S = s });
        }

        var pre = new List<Expr>();
        foreach (Expr c in conj)
        {
            IEnumerable<Source> refs = SourcesOf(c, scope);
            int max = -1;
            foreach (Source r in refs)
            {
                max = Math.Max(max, order.IndexOf(r));
            }

            (max < 0 ? pre : levels[max].Where).Add(c);
        }

        foreach (Expr c in pre)
        {
            int r = CompileCondition(c);
            Emit(Op.IfNot, r, nestEnd, 1);
        }

        // With a RIGHT or FULL join, the WHERE clause applies to the joined rows only after every item has been matched: a term on an early item
        // must not remove the rows that later items would have matched, or those later rows would come out as unmatched.
        bool late = sources.Any(x => x.IsRightSide);
        Action mainBody = body;
        if (late)
        {
            foreach (LoopLevel lv in levels)
            {
                lv.Where.RemoveAll(w => whereConj.Contains(w));
            }

            // A term whose items all lie at or after the last RIGHT/FULL item can filter early: every match has been recorded by then.
            // A term on earlier items is computed where its items are bound (once per row of that level) and tested at the end.
            int lastRight = levels.FindLastIndex(l => l.S.IsRightSide);
            var atEnd = new List<Expr>();
            var computed = new List<int>();
            foreach (Expr c in whereConj)
            {
                int max = -1;
                foreach (Source r in SourcesOf(c, scope))
                {
                    max = Math.Max(max, order.IndexOf(r));
                }

                if (max < 0)
                {
                    atEnd.Add(c);
                }
                else if (max >= lastRight)
                {
                    levels[max].Where.Add(c);
                }
                else
                {
                    int reg = NewReg();
                    levels[max].Compute.Add((c, reg));
                    computed.Add(reg);
                }
            }

            mainBody = () =>
            {
                int skipRow = NewLabel();
                foreach (Expr c in atEnd)
                {
                    int r = CompileCondition(c);
                    Emit(Op.IfNot, r, skipRow, 1);
                }

                foreach (int reg in computed)
                {
                    Emit(Op.IfNot, reg, skipRow, 1);
                }

                body();
                Mark(skipRow);
            };
        }

        var bound = new HashSet<Source>();
        for (int i = 0; i < levels.Count; i++)
        {
            LoopLevel lv = levels[i];
            Source s = lv.S;
            if (_planByCursor.TryGetValue(s, out AccessPlan? pre2) && sources.Count == 1)
            {
                lv.Plan = pre2;
            }
            else if (s.Kind == SourceKind.Table)
            {
                var cand = new List<Expr>();
                if (!s.IsOuterNullFill)
                {
                    cand.AddRange(lv.Where);
                }

                cand.AddRange(s.On);
                lv.Plan = MakePlan(s, cand, bound);
            }
            else if (s.Kind == SourceKind.Module)
            {
                var cand = new List<Expr>();
                if (!s.IsOuterNullFill)
                {
                    cand.AddRange(lv.Where);
                }

                cand.AddRange(s.On);
                s.ModuleEq = ModuleEqTerms(s, cand, bound, out List<Expr> used);

                // The function answers the terms it took as arguments, so they are not tested again against the column.
                foreach (Expr u in used)
                {
                    lv.Where.Remove(u);
                    s.On.Remove(u);
                }

                lv.Plan = new AccessPlan { Kind = PlanKind.Scan };
            }
            else if (s.Kind == SourceKind.VTab)
            {
                PlanVirtual(s, lv.Where, bound);
                lv.Plan = new AccessPlan { Kind = PlanKind.Scan };
            }
            else
            {
                lv.Plan = new AccessPlan { Kind = PlanKind.Scan };
            }

            bound.Add(s);
        }

        foreach (Source s in sources)
        {
            if (s.IsRightSide && s.MatchSet < 0)
            {
                s.MatchSet = NewSet();
                Emit(Op.SetOpen, s.MatchSet, 0, 0, new SetSpec { Collations = [CollKind.Binary] });
            }
        }

        EmitLevel(levels, 0, mainBody);

        // Rows of a RIGHT or FULL join's right item that matched nothing come out once, with every earlier item NULL.
        for (int k = 0; k < levels.Count; k++)
        {
            Source s = levels[k].S;
            if (!s.IsRightSide)
            {
                continue;
            }

            var rest = new List<LoopLevel>();
            for (int j = k + 1; j < levels.Count; j++)
            {
                // The join conditions of the items after this one still apply; those among earlier items and the WHERE terms are tested at the end.
                var lv = new LoopLevel { S = levels[j].S, Plan = new AccessPlan { Kind = PlanKind.Scan } };
                lv.Where.AddRange(levels[j].Where.Where(w => poolConj.Contains(w)));
                rest.Add(lv);
            }

            int lend = NewLabel();
            int key = NewReg();
            _planNote = "second pass of a RIGHT or FULL join: rows of this table that matched nothing";
            EmitSourceLoop(s, new AccessPlan { Kind = PlanKind.Scan }, lend, next =>
            {
                Emit(Op.RowKey, s.Cursor, key);
                Emit(Op.SetFound, s.MatchSet, next, key, null, 1);
                for (int j = 0; j < k; j++)
                {
                    Emit(Op.NullRow, levels[j].S.Cursor);
                }

                // The WHERE terms are tested at the innermost level, so a row that fails one moves on to the next row of that level
                // and not past the whole unmatched row.
                if (rest.Count > 0)
                {
                    rest[^1].Where.AddRange(whereConj);
                    EmitLevel(rest, 0, body);
                    return;
                }

                foreach (Expr c in whereConj)
                {
                    int r = CompileCondition(c);
                    Emit(Op.IfNot, r, next, 1);
                }

                body();
            });
            Mark(lend);
        }

        Mark(nestEnd);
    }

    private void EmitLevel(List<LoopLevel> lv, int i, Action body)
    {
        if (i == lv.Count)
        {
            body();
            return;
        }

        LoopLevel level = lv[i];
        Source s = level.S;
        bool outer = s.IsOuterNullFill;
        int matchReg = 0, nullPass = 0;
        if (outer)
        {
            matchReg = NewReg();
            nullPass = NewReg();
            Emit(Op.Integer, 0, matchReg, 0, 0L);
            Emit(Op.Integer, 0, nullPass, 0, 0L);
        }

        int post = NewLabel(), afterLoop = NewLabel(), cont = NewLabel(), contEnd = NewLabel();
        EmitSourceLoop(s, level.Plan, post, next =>
        {
            foreach (Expr on in s.On)
            {
                int r = CompileCondition(on);
                Emit(Op.IfNot, r, next, 1);
            }

            if (outer)
            {
                Emit(Op.Integer, 0, matchReg, 0, 1L);
            }

            if (s.IsRightSide)
            {
                int key = NewReg();
                Emit(Op.RowKey, s.Cursor, key);
                Emit(Op.SetAdd, s.MatchSet, key, 1);
            }

            Mark(cont);
            foreach ((Expr ce, int creg) in level.Compute)
            {
                CompileInto(ce, creg);
            }

            foreach (Expr w in level.Where)
            {
                int r = CompileCondition(w);
                Emit(Op.IfNot, r, contEnd, 1);
            }

            EmitLevel(lv, i + 1, body);
            Mark(contEnd);
            if (outer)
            {
                Emit(Op.If, nullPass, afterLoop, 0);
            }
        });
        Mark(post);
        if (outer)
        {
            Emit(Op.If, matchReg, afterLoop, 0);
            Emit(Op.NullRow, s.Cursor);
            Emit(Op.Integer, 0, nullPass, 0, 1L);
            Emit(Op.Goto, 0, cont);
        }

        Mark(afterLoop);
    }

    /// <summary>
    /// Picks the order of the nested loops. Items joined with an outer join keep their written order; otherwise the next item is the one a
    /// WHERE or ON term can look up through a rowid or an index given the items already placed, then the one with a term at all.
    /// </summary>
    private List<Source> ChooseOrder(Scope scope, List<Expr> conj)
    {
        List<Source> src = scope.Sources;
        if (src.Count <= 1 || src.Any(s => (s.Join & (JoinFlags.Left | JoinFlags.Right | JoinFlags.Full)) != 0)
            || (src.Any(IsCallForm) && src.Any(s => s.CrossJoined)))
        {
            return [.. src];
        }

        var placed = new List<Source>();
        var remaining = new List<Source>(src);
        var bound = new HashSet<Source>();
        while (remaining.Count > 0)
        {
            Source best = remaining[0];
            int bestScore = -1;
            foreach (Source c in remaining)
            {
                // A table-valued function reads its arguments from the items before it.
                // and, observed, goes inside the other items when nothing ties it to them.
                if (IsFunctionLike(c) && remaining.Count > 1 && (!ModuleArgsBound(c, scope, bound) || HiddenTermsWait(c, scope, conj, bound)
                    || (IsCallForm(c) && remaining.Any(o => !IsCallForm(o)) && !EnablesLookup(c, remaining, scope, conj, bound))))
                {
                    continue;
                }

                int score = 0;
                var withC = new HashSet<Source>(bound) { c };
                foreach (Expr e in conj)
                {
                    var refs = new HashSet<Source>();
                    if (!CollectSources(e, scope, refs) || !refs.Contains(c) || !refs.IsSubsetOf(withC))
                    {
                        continue;
                    }

                    score = Math.Max(score, TermScore(c, e, bound));
                }

                if (c.Kind == SourceKind.VTab && !IsCallForm(c))
                {
                    // The module's own answer replaces the generic score: a term it cannot use does not make the table a good first loop.
                    int vs = VirtualScore(c, conj, bound);
                    if (vs < 0 && remaining.Count > 1)
                    {
                        continue;
                    }

                    score = Math.Max(vs, 0);
                }

                if (score > bestScore)
                {
                    best = c;
                    bestScore = score;
                }
            }

            placed.Add(best);
            remaining.Remove(best);
            bound.Add(best);
        }

        return placed;
    }

    /// <summary>True when placing the function <paramref name="m"/> next lets another item be found through its rowid or an index.</summary>
    private bool EnablesLookup(Source m, List<Source> remaining, Scope scope, List<Expr> conj, HashSet<Source> bound)
    {
        var withM = new HashSet<Source>(bound) { m };
        foreach (Source o in remaining)
        {
            if (IsCallForm(o))
            {
                continue;
            }

            var withO = new HashSet<Source>(withM) { o };
            foreach (Expr e in conj)
            {
                var refs = new HashSet<Source>();
                if (CollectSources(e, scope, refs) && refs.Contains(o) && refs.Contains(m) && refs.IsSubsetOf(withO) && TermScore(o, e, withM) >= 10)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// How good a place the virtual table <paramref name="c"/> is for the next loop, by asking the module (vtab: xBestIndex) with the terms that
    /// the items before it make usable: a plan that returns one row ranks with a rowid lookup, one that takes an argument with an indexed equality.
    /// </summary>
    private int VirtualScore(Source c, List<Expr> conj, HashSet<Source> bound)
    {
        try
        {
            VTab vtab = _cat.Owner!.ConnectVirtual(c.Table);
            List<VtTerm> terms = VtTerms(c, conj, bound, vtab.AcceptsExtendedConstraints);
            AddVtLimitAndArgs(c, terms, vtab, bound, conj);
            if (terms.Count == 0)
            {
                return 0;
            }

            var info = new BestIndexInfo([.. terms.Select(t => new IndexConstraint(t.Column, t.Op, t.Usable))], []);
            vtab.BestIndex(info);
            if (!info.Usage.Any(u => u.ArgvIndex > 0))
            {
                return 0;
            }

            return info.ScanIsUnique || info.EstimatedRows <= 1 ? 100 : info.EstimatedCost < 1e6 ? 50 : 0;
        }
        catch (OneLiteException)
        {
            // The module refuses this place in the nest (a required constraint is not usable yet); the planning pass reports its error if no place works.
            return -1;
        }
    }

    /// <summary>A table-valued call <c>f(args)</c>: a built-in function, or a virtual table named with an argument list.</summary>
    private static bool IsCallForm(Source s) => s.Kind == SourceKind.Module || (s.Kind == SourceKind.VTab && s.ModuleArgs.Length > 0);

    /// <summary>A table-valued function: a built-in function, or a virtual table with hidden columns (they take their arguments from the items before it).</summary>
    private static bool IsFunctionLike(Source s) => s.Kind == SourceKind.Module || (s.Kind == SourceKind.VTab && (s.ModuleArgs.Length > 0 || s.Table.Columns.Any(c => c.Hidden)));

    /// <summary>True when a term fixes a hidden column of the virtual table <paramref name="c"/> to a value that reads an item not placed yet, so <paramref name="c"/> has to wait for it.</summary>
    private bool HiddenTermsWait(Source c, Scope scope, List<Expr> conj, HashSet<Source> bound)
    {
        if (c.Kind != SourceKind.VTab)
        {
            return false;
        }

        foreach (Expr e0 in conj)
        {
            if (StripParens(e0) is not BinaryExpr { Op: BinaryOp.Eq } b)
            {
                continue;
            }

            foreach ((Expr col, Expr val) in new[] { (b.Left, b.Right), (b.Right, b.Left) })
            {
                if (ColumnOf(col, c) is { } ci && ci >= 0 && c.Table.Columns[ci].Hidden)
                {
                    var refs = new HashSet<Source>();
                    if (CollectSources(val, scope, refs) && !refs.Contains(c) && !refs.IsSubsetOf(bound))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private bool ModuleArgsBound(Source m, Scope scope, HashSet<Source> bound)
    {
        foreach (Expr a in m.ModuleArgs)
        {
            var refs = new HashSet<Source>();
            if (!CollectSources(a, scope, refs) || !refs.IsSubsetOf(bound))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryModuleTerm(Source s, Expr column, Expr value, HashSet<Source> bound, Dictionary<int, Expr> found)
    {
        if (ColumnOf(column, s) is not { } col || col < s.Module!.HiddenFrom || _scope is null)
        {
            return false;
        }

        var refs = new HashSet<Source>();
        return CollectSources(value, _scope, refs) && !refs.Contains(s) && refs.IsSubsetOf(bound) && found.TryAdd(col, value);
    }

    /// <summary>The hidden columns of a table-valued function that the terms fix to a value computable from the items before it (<c>WHERE json = x</c>).</summary>
    private Dictionary<int, Expr> ModuleEqTerms(Source s, List<Expr> terms, HashSet<Source> bound, out List<Expr> used)
    {
        var found = new Dictionary<int, Expr>();
        used = [];
        foreach (Expr c in terms)
        {
            // Any value that only reads the items before this one will do: the function takes it as an argument, whatever its affinity.
            if (c is BinaryExpr { Op: BinaryOp.Eq } b)
            {
                if (TryModuleTerm(s, b.Left, b.Right, bound, found) || TryModuleTerm(s, b.Right, b.Left, bound, found))
                {
                    used.Add(c);
                }
            }
        }

        return found;
    }

    private int TermScore(Source s, Expr e, HashSet<Source> bound)
    {
        var terms = new List<Term>();
        ExtractTerms(e, terms, s, bound);
        int score = 1;
        foreach (Term t in terms)
        {
            if (s.Kind != SourceKind.Table)
            {
                score = Math.Max(score, 3);
                continue;
            }

            bool rowid = IsRowidTerm(s, t);
            bool indexed = s.Table.Indexes.Any(ix => ix.Usable && ix.Columns.Length > 0 && ix.Columns[0] == t.Column) || (s.Table.WithoutRowid && s.Table.PrimaryKey is { Usable: true } pk && pk.Columns[0] == t.Column);
            if (t.Cmp == BinaryOp.Eq)
            {
                score = Math.Max(score, rowid ? 100 : indexed ? 50 : 3);
            }
            else
            {
                score = Math.Max(score, rowid || indexed ? 10 : 2);
            }
        }

        return score;
    }
}
