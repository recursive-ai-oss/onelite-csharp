using System.Collections.Immutable;
using System.Linq;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite.Planning;

// INSERT, UPDATE and DELETE on a view (lang_createtrigger, "INSTEAD OF triggers"). The statement changes nothing itself: for every row it
// would change, the INSTEAD OF triggers of the event run with that row as OLD and NEW. Without such a trigger the view is read-only.
internal sealed partial class Compiler
{
    private bool IsViewTarget(QualifiedName qn, out TableInfo view)
    {
        view = null!;
        if (_cat.FindIn(qn.Schema?.Text, qn.Name.Text) is { View: not null } v)
        {
            view = v;
            return true;
        }

        return false;
    }

    private static SelectStatement ViewScan(QualifiedName qn, Name? alias, Expr? where)
    {
        var from = new FromClause([new FromItem(null, new NamedTable(qn, alias, null), null, [])]);
        var core = new SimpleSelect(SelectQuantifier.None, [new StarColumn(null)], from, where, [], null, [], [], null);
        return new SelectStatement(null, [core], []);
    }

    /// <summary>Gives the view its columns (the names its SELECT produces), compiling a scan of it once.</summary>
    private VmProgram ViewShape(TableInfo v, QualifiedName qn, Name? alias, Expr? where)
    {
        var inner = new Compiler(this) { _cteEnv = null };
        VmProgram p = inner.CompileSelect(ViewScan(qn, alias, where));
        p.SchemaCookie = _cat.Cookie;
        p.Encoding = _enc;
        if (v.Columns.Length != p.ResultCount)
        {
            v.Columns = [.. p.ColumnNames.Take(p.ResultCount).Select((n, i) => new ColumnInfo { Name = n, RecordIndex = i })];
        }

        return p;
    }

    private static HashSet<int> SetColumnsOf(TableInfo v, IEnumerable<SetClause> sets)
    {
        var h = new HashSet<int>();
        foreach (SetClause sc in sets)
        {
            foreach (Name n in sc.Columns)
            {
                int ci = v.FindColumn(n.Text);
                if (ci < 0)
                {
                    throw Err("no such column: " + n.Text);
                }

                h.Add(ci);
            }
        }

        return h;
    }

    private VmProgram CompileViewDelete(DeleteStatement del, TableInfo v)
    {
        PreassignParameters();
        if (!HasTrigger(v, TriggerTime.InsteadOf, TriggerEvent.Delete))
        {
            throw Err("cannot modify " + v.Name + " because it is a view");
        }

        VmProgram scan = ViewShape(v, del.Target.Name, del.Target.Alias, del.Where);
        int n = v.Columns.Length;
        Emit(Op.Transaction, 0, 0, 0, null, 1 | 8);
        var spec = new SubSelectSpec { Program = scan, Drain = true };
        Emit(Op.SubSelect, 0, 0, 0, spec);
        int again = NewLabel();
        int done = NewLabel();
        Mark(again);
        int row = NewRegs(n + 1);
        Emit(Op.SubNext, 0, done, row, spec);
        Emit(Op.Null, 0, row + n);
        EmitTriggers(v, TriggerTime.InsteadOf, TriggerEvent.Delete, row, 0, null, again);
        Emit(Op.Goto, 0, again);
        Mark(done);
        Emit(Op.Halt);
        return DdlProgram();
    }

    private VmProgram CompileViewUpdate(UpdateStatement up, TableInfo v)
    {
        PreassignParameters();
        if (up.From is not null)
        {
            throw Unsupported("UPDATE ... FROM on a view");
        }

        if (!HasTrigger(v, TriggerTime.InsteadOf, TriggerEvent.Update))
        {
            throw Err("cannot modify " + v.Name + " because it is a view");
        }

        VmProgram scan = ViewShape(v, up.Target.Name, up.Target.Alias, up.Where);
        int n = v.Columns.Length;
        HashSet<int> setCols = SetColumnsOf(v, up.Sets);
        BindTarget(up.Target);
        Emit(Op.Transaction, 0, 0, 0, null, 1 | 8);
        var spec = new SubSelectSpec { Program = scan, Drain = true };
        Emit(Op.SubSelect, 0, 0, 0, spec);
        int again = NewLabel();
        int done = NewLabel();
        Mark(again);
        int oldB = NewRegs(n + 1);
        Emit(Op.SubNext, 0, done, oldB, spec);
        Emit(Op.Null, 0, oldB + n);
        int newB = NewRegs(n + 1);
        for (int i = 0; i <= n; i++)
        {
            Emit(Op.Copy, oldB + i, newB + i);
        }

        _colRegs = [.. Enumerable.Range(0, n).Select(i => oldB + i)];
        _rowidReg = oldB + n;
        _setRows = null;
        foreach (SetClause sc in up.Sets)
        {
            for (int k = 0; k < sc.Columns.Length; k++)
            {
                int ci = v.FindColumn(sc.Columns[k].Text);
                CompileSetValue((ci, sc.Value, k, sc.Columns.Length), newB + ci);
            }
        }

        _colRegs = null;
        EmitViewAffinity(v, newB, all: true);
        EmitTriggers(v, TriggerTime.InsteadOf, TriggerEvent.Update, oldB, newB, setCols, again);
        Emit(Op.Goto, 0, again);
        Mark(done);
        Emit(Op.Halt);
        return DdlProgram();
    }

    private VmProgram CompileViewInsert(InsertStatement ins, TableInfo v)
    {
        PreassignParameters();
        if (!HasTrigger(v, TriggerTime.InsteadOf, TriggerEvent.Insert))
        {
            throw Err("cannot modify " + v.Name + " because it is a view");
        }

        VmProgram scan = ViewShape(v, ins.Table, null, null);
        int n = v.Columns.Length;
        var targets = new List<int>();
        if (ins.Columns.IsDefaultOrEmpty)
        {
            targets.AddRange(Enumerable.Range(0, n));
        }
        else
        {
            foreach (Name c in ins.Columns)
            {
                int ci = v.FindColumn(c.Text);
                if (ci < 0)
                {
                    throw Err("table " + v.Name + " has no column named " + c.Text);
                }

                targets.Add(ci);
            }
        }

        SelectStatement? sel = ins.Source.Select;
        Emit(Op.Transaction, 0, 0, 0, null, 1 | 8);
        int srcBase = NewRegs(Math.Max(targets.Count, 1));
        int newB = NewRegs(n + 1);
        void Fire(int skip)
        {
            for (int i = 0; i <= n; i++)
            {
                Emit(Op.Null, 0, newB + i);
            }

            for (int k = 0; k < targets.Count; k++)
            {
                Emit(Op.Copy, srcBase + k, newB + targets[k]);
            }

            EmitViewAffinity(v, newB);
            EmitTriggers(v, TriggerTime.InsteadOf, TriggerEvent.Insert, 0, newB, null, skip);
        }

        if (sel is { Cores: [ValuesCore vc], With: null })
        {
            foreach (ImmutableArray<Expr> row in vc.Rows)
            {
                if (row.Length != targets.Count)
                {
                    throw Err(row.Length + " values for " + targets.Count + " columns");
                }

                _colRegs = null;
                SetColumns(false);
                for (int k = 0; k < row.Length; k++)
                {
                    CompileInto(row[k], srcBase + k);
                }

                int next = NewLabel();
                Fire(next);
                Mark(next);
            }
        }
        else if (sel is not null)
        {
            var inner = new Compiler(this) { _cteEnv = _cteEnv };
            VmProgram sub = inner.CompileSelect(sel);
            sub.SchemaCookie = _cat.Cookie;
            sub.Encoding = _enc;
            if (sub.ResultCount != targets.Count)
            {
                throw Err(sub.ResultCount + " values for " + targets.Count + " columns");
            }

            var spec = new SubSelectSpec { Program = sub, Drain = true };
            Emit(Op.SubSelect, 0, 0, 0, spec);
            int again = NewLabel();
            int done = NewLabel();
            Mark(again);
            Emit(Op.SubNext, 0, done, srcBase, spec);
            Fire(again);
            Emit(Op.Goto, 0, again);
            Mark(done);
        }
        else
        {
            targets.Clear();
            int next = NewLabel();
            Fire(next);
            Mark(next);
        }

        _ = scan;
        Emit(Op.Halt);
        return DdlProgram();
    }
}

/// <summary>An expression whose value is already in a register (the value an UPDATE ... FROM computed for a SET clause).</summary>
internal sealed record RegExpr(int Reg) : Expr;

internal sealed partial class Compiler
{
    // lang_update "Update from": the target joined with the FROM items gives, for each matching row, the new values of the SET clauses. A
    // target row that matches several times is updated once, by the first match.
    /// <summary>The first column reference in <paramref name="e"/> qualified by the UPDATE target's name, unless a FROM item has the same name.</summary>
    private static string? FindTargetRef(Expr e, string targetName, ImmutableArray<FromItem> from)
    {
        switch (e)
        {
            case ColumnRef { Table: { } q } cr when AsciiComparer.Instance.Equals(q.Text, targetName):
                foreach (FromItem f in from)
                {
                    string? name = f.Source is NamedTable nt ? nt.Alias?.Text ?? nt.Name.Name.Text : null;
                    if (name is not null && AsciiComparer.Instance.Equals(name, targetName))
                    {
                        return null;
                    }
                }

                return q.Text + "." + cr.Column.Text;
            case ParenExpr p:
                return FindTargetRef(p.Inner, targetName, from);
            case CastExpr c:
                return FindTargetRef(c.Operand, targetName, from);
            case CollateExpr co:
                return FindTargetRef(co.Operand, targetName, from);
            default:
                foreach (Expr child in CollationChildren(e))
                {
                    if (FindTargetRef(child, targetName, from) is { } r)
                    {
                        return r;
                    }
                }

                return null;
        }
    }

    private VmProgram CompileUpdateFrom(UpdateStatement up, TableInfo t)
    {
        if (t.WithoutRowid)
        {
            throw Unsupported("UPDATE ... FROM on a WITHOUT ROWID table");
        }

        List<(int Col, Expr Value, int Part, int Of)> sets = ResolveSets(t, up.Sets);
        string alias = up.Target.Alias?.Text ?? t.Name;
        var cols = new List<ResultColumn> { new ExprColumn(new ColumnRef(null, new Name(alias), new Name("rowid")), null) };
        foreach ((int _, Expr value, int part, int of) in sets)
        {
            cols.Add(new ExprColumn(of > 1 ? (value is RowValueExpr rv && rv.Items.Length == of ? rv.Items[part] : throw Unsupported("multi-column SET from a subquery")) : value, null));
        }

        // lang_update: the target is not visible to the ON clauses of the FROM clause's own joins
        foreach (FromItem fi in up.From!.Items)
        {
            if (fi.On is not null && FindTargetRef(fi.On, alias, up.From.Items) is { } bad)
            {
                throw Err("no such column: " + bad);
            }
        }

        var items = new List<FromItem> { new(null, new NamedTable(up.Target.Name, up.Target.Alias, null), null, []) };
        for (int i = 0; i < up.From!.Items.Length; i++)
        {
            FromItem f = up.From.Items[i];
            items.Add(i == 0 ? f with { Join = new JoinOperator(JoinFlags.Comma, []) } : f);
        }

        var core = new SimpleSelect(SelectQuantifier.None, [.. cols], new FromClause([.. items]), up.Where, [], null, [], [], null);
        var inner = new Compiler(this) { _cteEnv = _cteEnv };
        VmProgram scan = inner.CompileSelect(new SelectStatement(null, [core], []));
        scan.SchemaCookie = _cat.Cookie;
        scan.Encoding = _enc;

        BindTarget(up.Target);
        Emit(Op.Transaction, 0, 0, 0, null, 1 | 8);
        WriteCtx w = OpenWriteCtx(t);
        var spec = new SubSelectSpec { Program = scan, Drain = true };
        Emit(Op.SubSelect, 0, 0, 0, spec);
        int loop = NewLabel();
        int done = NewLabel();
        int set = NewSet();
        Emit(Op.SetOpen, set, 0, 0, new SetSpec { Collations = [CollKind.Binary] });
        int row = NewRegs(cols.Count);
        Mark(loop);
        Emit(Op.SubNext, 0, done, row, spec);
        Emit(Op.SetFound, set, loop, row, null, 1);
        Emit(Op.SetAdd, set, row, 1);
        Emit(Op.SeekRowid, w.Cursor, loop, row);
        SetOldRowMode(w);
        var fixedSets = new List<(int Col, Expr Value, int Part, int Of)>();
        for (int i = 0; i < sets.Count; i++)
        {
            fixedSets.Add((sets[i].Col, new RegExpr(row + 1 + i), 0, 1));
        }

        int retBase = 0;
        int retCols = 0;
        string[] retNames = [];
        EmitUpdateBody(w, fixedSets, up.OrAction, loop, up.Returning, true, (b, c, n) => { retBase = b; retCols = c; retNames = n; });
        Emit(Op.Goto, 0, loop);
        Mark(done);
        return EmitReturningLoop(retBase, retCols, retNames);
    }

    /// <summary>Observed: NEW.x in an INSTEAD OF trigger is converted only when the view's column has REAL affinity (it selects a REAL table column) for INSERT, and by every affinity for UPDATE.</summary>
    private void EmitViewAffinity(TableInfo v, int newB, bool all = false)
    {
        var c = new Compiler(_cat, "", _enc);
        SelectMeta meta = c.CompileSelectStmt(v.View!.Select, new Dest { Kind = DestKind.Eph, Cursor = c.NewEphCursor() });
        for (int i = 0; i < v.Columns.Length && i < meta.Aff.Length; i++)
        {
            if (all ? meta.Aff[i] != Affinity.None : meta.Aff[i] == Affinity.Real)
            {
                Emit(Op.Affinity, newB + i, 0, 0, null, (int)meta.Aff[i]);
            }
        }
    }
}
