using System.Collections.Immutable;
using System.Linq;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Vm;
using RecursiveAI.OneLite.VirtualTables;

namespace RecursiveAI.OneLite.Planning;

// INSERT, UPDATE and DELETE on a virtual table (vtab "xUpdate"): the statement reads the rows it changes first (as for a view), then hands
// every change to the module as (old rowid, new rowid, column values...), so a module never sees its own writes in the scan that finds them.
internal sealed partial class Compiler
{
    private bool IsVirtualTarget(QualifiedName qn, out TableInfo table)
    {
        table = null!;
        if (_cat.FindIn(qn.Schema?.Text, qn.Name.Text) is { Virtual: not null } v)
        {
            table = v;
            return true;
        }

        return false;
    }

    private static SelectStatement VirtualScan(TableInfo t, NamedTable target, Expr? where, bool allColumns)
    {
        string alias = target.Alias?.Text ?? t.Name;
        var cols = new List<ResultColumn> { new ExprColumn(new ColumnRef(null, new Name(alias), new Name("rowid")), null) };
        if (allColumns)
        {
            foreach (ColumnInfo c in t.Columns)
            {
                cols.Add(new ExprColumn(new ColumnRef(null, new Name(alias), new Name(c.Name)), null));
            }
        }

        var from = new FromClause([new FromItem(null, new NamedTable(target.Name, target.Alias, null), null, [])]);
        var core = new SimpleSelect(SelectQuantifier.None, [.. cols], from, where, [], null, [], [], null);
        return new SelectStatement(null, [core], []);
    }

    private VmProgram ScanProgram(SelectStatement sel)
    {
        var inner = new Compiler(this) { _cteEnv = null };
        VmProgram p = inner.CompileSelect(sel);
        p.SchemaCookie = _cat.Cookie;
        p.Encoding = _enc;
        return p;
    }

    private void CheckWritable(TableInfo t)
    {
        EnsureVirtual(t);
        VTab v = t.Virtual!.Instance!;
        if (!v.IsWritable)
        {
            throw Err("table " + t.Name + " may not be modified");
        }
    }

    private VmProgram CompileVirtualDelete(DeleteStatement del, TableInfo t)
    {
        PreassignParameters();
        if (del.Returning is not null)
        {
            throw Unsupported("RETURNING on a virtual table");
        }

        CheckWritable(t);
        VmProgram scan = ScanProgram(VirtualScan(t, del.Target, del.Where, false));
        BindTarget(del.Target);
        _forceTemp = t.Db == 1;
        Emit(Op.Transaction, 0, 0, 0, null, 1 | 8);
        var spec = new SubSelectSpec { Program = scan, Drain = true };
        Emit(Op.SubSelect, 0, 0, 0, spec);
        int again = NewLabel();
        int done = NewLabel();
        Mark(again);
        int row = NewRegs(1);
        int rid = NewReg();
        Emit(Op.SubNext, 0, done, row, spec);
        Emit(Op.VtUpdate, row, 1, rid, t, 1);
        Emit(Op.Goto, 0, again);
        Mark(done);
        Emit(Op.Halt);
        return DdlProgram();
    }

    private VmProgram CompileVirtualUpdate(UpdateStatement up, TableInfo t)
    {
        PreassignParameters();
        if (up.Returning is not null)
        {
            throw Unsupported("RETURNING on a virtual table");
        }

        if (up.From is not null)
        {
            throw Unsupported("UPDATE ... FROM on a virtual table");
        }

        CheckWritable(t);
        int n = t.Columns.Length;
        List<(int Col, Expr Value, int Part, int Of)> sets = ResolveSets(t, up.Sets);
        VmProgram scan = ScanProgram(VirtualScan(t, up.Target, up.Where, true));
        BindTarget(up.Target);
        _forceTemp = t.Db == 1;
        Emit(Op.Transaction, 0, 0, 0, null, 1 | 8);
        var spec = new SubSelectSpec { Program = scan, Drain = true };
        Emit(Op.SubSelect, 0, 0, 0, spec);
        int again = NewLabel();
        int done = NewLabel();
        Mark(again);
        int old = NewRegs(n + 1);
        Emit(Op.SubNext, 0, done, old, spec);
        int arg = NewRegs(n + 2);
        int outRid = NewReg();
        Emit(Op.Copy, old, arg);
        Emit(Op.Copy, old, arg + 1);
        for (int i = 0; i < n; i++)
        {
            Emit(Op.Copy, old + 1 + i, arg + 2 + i);
        }

        _colRegs = [.. Enumerable.Range(0, n).Select(i => old + 1 + i)];
        _rowidReg = old;
        foreach ((int col, Expr value, int part, int of) in sets)
        {
            CompileSetValue((col, value, part, of), col < 0 ? arg + 1 : arg + 2 + col);
        }

        _colRegs = null;
        Emit(Op.VtUpdate, arg, n + 2, outRid, t, 1);
        Emit(Op.Goto, 0, again);
        Mark(done);
        Emit(Op.Halt);
        return DdlProgram();
    }

    private VmProgram CompileVirtualInsert(InsertStatement ins, TableInfo t)
    {
        PreassignParameters();
        if (ins.Returning is not null)
        {
            throw Unsupported("RETURNING on a virtual table");
        }

        if (!ins.Upserts.IsDefaultOrEmpty)
        {
            throw Unsupported("upsert on a virtual table");
        }

        CheckWritable(t);
        int n = t.Columns.Length;
        var targets = new List<int>();
        if (ins.Columns.IsDefaultOrEmpty)
        {
            // Hidden columns are not part of the default column list.
            for (int i = 0; i < n; i++)
            {
                if (!t.Columns[i].Hidden)
                {
                    targets.Add(i);
                }
            }
        }
        else
        {
            foreach (Name c in ins.Columns)
            {
                int ci = t.FindColumn(c.Text);
                if (ci < 0 && IsRowidName(c.Text))
                {
                    ci = -1;
                }
                else if (ci < 0)
                {
                    throw Err("table " + t.Name + " has no column named " + c.Text);
                }

                targets.Add(ci);
            }
        }

        _forceTemp = t.Db == 1;
        SelectStatement? sel = ins.Source.Select;
        Emit(Op.Transaction, 0, 0, 0, null, 1 | 8);
        int srcBase = NewRegs(Math.Max(targets.Count, 1));
        int arg = NewRegs(n + 2);
        int outRid = NewReg();
        void Fire()
        {
            for (int i = 0; i < n + 2; i++)
            {
                Emit(Op.Null, 0, arg + i);
            }

            for (int k = 0; k < targets.Count; k++)
            {
                Emit(Op.Copy, srcBase + k, targets[k] < 0 ? arg + 1 : arg + 2 + targets[k]);
            }

            Emit(Op.VtUpdate, arg, n + 2, outRid, t, 1 | 2);
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

                Fire();
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
            Fire();
            Emit(Op.Goto, 0, again);
            Mark(done);
        }
        else
        {
            targets.Clear();
            Fire();
        }

        Emit(Op.Halt);
        return DdlProgram();
    }
}
