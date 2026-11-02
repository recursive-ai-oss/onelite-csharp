using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite.Planning;

// PRAGMA foreign_key_list(table) and PRAGMA foreign_key_check[(table)] (pragma).
internal sealed partial class Compiler
{
    private void EmitText(string s, int dest) => Emit(Op.String8, 0, dest, 0, Encoding.UTF8.GetBytes(s));

    private static string ActionText(ForeignKeyAction a) => a switch
    {
        ForeignKeyAction.Cascade => "CASCADE",
        ForeignKeyAction.SetNull => "SET NULL",
        ForeignKeyAction.SetDefault => "SET DEFAULT",
        ForeignKeyAction.Restrict => "RESTRICT",
        _ => "NO ACTION",
    };

    /// <summary>
    /// One row per column of each foreign key: id, seq, table, from, to, on_update, on_delete, match. Ids count down from the last
    /// constraint declared; "to" is NULL when the key names no parent column; "match" is always NONE. An unknown table gives no rows.
    /// </summary>
    internal VmProgram CompileForeignKeyList(PragmaStatement ps)
    {
        string[] cols = ["id", "seq", "table", "from", "to", "on_update", "on_delete", "match"];
        var rows = new List<object?[]>();
        TableInfo? t = ps.Value is null ? null : _cat.FindIn(ps.Schema?.Text, ps.Value.Text);
        if (t is not null && t.Unsupported is null)
        {
            for (int k = 0; k < t.ForeignKeys.Count; k++)
            {
                FkInfo f = t.ForeignKeys[t.ForeignKeys.Count - 1 - k];
                for (int s = 0; s < f.Child.Length; s++)
                {
                    rows.Add([k, s, f.Parent, t.Columns[f.Child[s]].Name, f.ParentColumns is { } pc ? pc[s] : null, ActionText(f.OnUpdate), ActionText(f.OnDelete), "NONE"]);
                }
            }
        }

        return ConstRows(cols, rows);
    }

    private VmProgram PragmaRows(int b, string[] cols) => new()
    {
        Code = Finish(),
        RegisterCount = _nReg + 1,
        Cursors = [.. _cursors],
        ColumnNames = cols,
        ResultBase = b,
        ResultCount = cols.Length,
        ParameterNames = [],
        Encoding = _enc,
    };

    /// <summary>
    /// One row (table, rowid, parent, fkid) for each child row whose key has no parent row, in rowid order and, within a row, constraint id
    /// order. It works whether or not foreign keys are enforced. A named table that does not exist is an error; a key that cannot be
    /// resolved ("foreign key mismatch") is an error when the statement is prepared, though a missing parent table only makes every
    /// non-NULL key a violation. WITHOUT ROWID tables report a NULL rowid.
    /// </summary>
    internal VmProgram CompileForeignKeyCheck(PragmaStatement ps)
    {
        string[] cols = ["table", "rowid", "parent", "fkid"];
        List<TableInfo> tables;
        if (IsTempName(ps.Schema) && ps.Value is { } tv)
        {
            throw Err("no such table: temp." + tv.Text);
        }

        if (ps.Value is { } v)
        {
            TableInfo t = _cat.Find(v.Text) ?? throw Err("no such table: " + v.Text);
            tables = [t];
        }
        else
        {
            tables = [.. _cat.Tables.Values.Where(x => x.Unsupported is null && x.ForeignKeys.Count > 0)];
        }

        EmitPragmaTransaction(ReadFlags);
        int b = NewRegs(cols.Length);
        foreach (TableInfo t in tables)
        {
            if (t.ForeignKeys.Count == 0 || t.Unsupported is not null)
            {
                continue;
            }

            var keys = new FkKey?[t.ForeignKeys.Count];
            for (int i = 0; i < keys.Length; i++)
            {
                FkInfo f = t.ForeignKeys[i];
                if (_cat.Find(f.Parent) is not null)
                {
                    keys[i] = ResolveFk(t, f, out OneLiteException? err) ?? throw err!;
                }
            }

            int cur = AddCursor(new CursorSpec { Table = t, Index = t.WithoutRowid ? t.PrimaryKey : null, IsIndexTree = t.WithoutRowid, Root = t.Root });
            _table = t;
            _alias = t.Name;
            _tableCursor = cur;
            Emit(Op.OpenRead, cur);
            int end = NewLabel(), loop = NewLabel(), next = NewLabel();
            Emit(Op.Rewind, cur, end);
            Mark(loop);
            for (int i = keys.Length - 1; i >= 0; i--)
            {
                FkInfo f = t.ForeignKeys[i];
                int n = f.Child.Length;
                int skip = NewLabel();
                int pb = NewRegs(n);
                for (int k = 0; k < n; k++)
                {
                    CompileTableColumn(f.Child[k], pb + k);
                    Emit(Op.IfNull, pb + k, skip);
                }

                if (keys[i] is { } key)
                {
                    var sb = new StringBuilder("SELECT count(*) FROM ").Append(Q(key.Parent.Name)).Append(" WHERE ");
                    for (int k = 0; k < n; k++)
                    {
                        sb.Append(k > 0 ? " AND " : "").Append(Q(key.Parent.Columns[key.Cols[k]].Name)).Append(" = ?").Append(k + 1);
                    }

                    int cnt = NewReg();
                    Emit(Op.FkRun, cnt, 0, pb, MakeFkSpec(sb.ToString(), true), n);
                    Emit(Op.If, cnt, skip, 0);
                }

                EmitText(t.Name, b);
                if (t.WithoutRowid)
                {
                    Emit(Op.Null, 0, b + 1);
                }
                else
                {
                    Emit(Op.Rowid, cur, b + 1);
                }

                EmitText(f.Parent, b + 2);
                Emit(Op.Integer, 0, b + 3, 0, (long)(keys.Length - 1 - i));
                Emit(Op.ResultRow, b, cols.Length);
                Mark(skip);
            }

            Mark(next);
            Emit(Op.Next, cur, loop);
            Mark(end);
        }

        Emit(Op.Halt);
        return PragmaRows(b, cols);
    }
}
