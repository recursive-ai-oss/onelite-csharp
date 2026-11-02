using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Sql.Parsing;
using SqlStatement = RecursiveAI.OneLite.Sql.Statement;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite.Planning;

// Foreign key enforcement (foreignkeys), with PRAGMA foreign_keys on.
//
// Each violation is counted instead of raised at once, so that a statement may pass through states that break a constraint:
// a counter for immediate constraints (checked when the statement ends) and one for deferred constraints (checked at COMMIT). The counter
// holds the number of child rows whose parent key is missing, and every row change moves it by the change it makes to that number:
//   child row inserted, parent missing      +1        child row deleted, parent missing       -1
//   parent key arrives: orphans it adopts   -n        parent key leaves: children it orphans  +n   (then the ON DELETE / ON UPDATE action runs)
// The lookups are small SELECTs on the other table, and the actions small DELETE and UPDATE statements, compiled on first use from SQL text
// (so the planner picks an index) and run as nested programs of the statement. Every hook works on registers that hold the row's values,
// never on a cursor, so an action that rewrites other rows cannot disturb the statement.
internal sealed class FkSpec
{
    internal string Sql = "";
    internal Catalog Cat = null!;
    internal TextEncoding Enc;
    internal bool IsCount;
    internal VmProgram? Program;

    /// <summary>Compiles the program itself instead of from <see cref="Sql"/> (PRAGMA optimize's ANALYZE of the tables it picked).</summary>
    internal Func<Compiler, VmProgram>? Custom;

    /// <summary>The compiled program; compiled on first use, so tables that refer to each other do not compile each other forever.</summary>
    internal VmProgram Get()
    {
        if (Program is { } done)
        {
            return done;
        }

        var c = new Compiler(Cat, Sql, Enc) { ForeignKeysEnabled = true, FkLazy = true };
        SqlStatement? stmt = Custom is null ? SqlParser.ParseOne(Sql, 0, out _) ?? throw new OneLiteException(ResultCode.Error, "internal foreign key statement is empty") : null;
        VmProgram p = Custom is not null ? Custom(c) : stmt switch
        {
            SelectStatement s => c.CompileSelect(s),
            DeleteStatement d => c.CompileDelete(d),
            UpdateStatement u => c.CompileUpdate(u),
            _ => throw new OneLiteException(ResultCode.Error, "internal foreign key statement"),
        };
        p.SchemaCookie = Cat.Cookie;
        p.Encoding = Enc;
        Program = p;
        return p;
    }
}

internal sealed partial class Compiler
{
    /// <summary>Set for the nested programs of foreign key actions: they compile the programs they need when they first run.</summary>
    internal bool FkLazy { get; set; }

    private bool _fkUsed;

    private sealed class FkKey
    {
        internal TableInfo Parent = null!;
        internal int[] Cols = [];
    }

    private static string Q(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static bool SameSet(int[] a, int[] b) => a.Length == b.Length && a.All(x => b.Contains(x));

    private static int[]? PrimaryKeyColumns(TableInfo p)
    {
        if (p.WithoutRowid)
        {
            return p.PrimaryKey?.Columns;
        }

        if (p.RowidAlias >= 0)
        {
            return [p.RowidAlias];
        }

        return p.AutoIndexes.Concat(p.Indexes).FirstOrDefault(i => i.IsPrimaryKey)?.Columns;
    }

    /// <summary>
    /// The parent columns of a foreign key: the PRIMARY KEY when none are named, and either way a PRIMARY KEY or UNIQUE column set, or the
    /// reference is a "foreign key mismatch" (foreignkeys §3). Returns null with the error when the parent table does not exist.
    /// </summary>
    private FkKey? ResolveFk(TableInfo child, FkInfo f, out OneLiteException? error)
    {
        error = null;
        TableInfo? p = _cat.FindIn(_cat.SchemaName(child.Db), f.Parent);
        if (p is null)
        {
            error = Err("no such table: " + (_cat.SchemaName(child.Db) + ".") + f.Parent);
            return null;
        }

        int[]? cols;
        if (f.ParentColumns is null)
        {
            cols = PrimaryKeyColumns(p);
        }
        else
        {
            cols = new int[f.ParentColumns.Length];
            for (int i = 0; i < cols.Length; i++)
            {
                cols[i] = p.FindColumn(f.ParentColumns[i]);
            }
        }

        bool ok = cols is not null && cols.Length == f.Child.Length && !cols.Contains(-1);
        if (ok)
        {
            int[] pk = PrimaryKeyColumns(p) ?? [];
            ok = SameSet(cols!, pk) || p.AutoIndexes.Concat(p.Indexes).Any(ix => ix.Unique && ix.Where is null && !ix.Terms.Any(t => t is not null) && SameSet(cols!, ix.Columns));
        }

        if (!ok)
        {
            error = Err("foreign key mismatch - \"" + child.Name + "\" referencing \"" + f.Parent + "\"");
            return null;
        }

        return new FkKey { Parent = p, Cols = cols! };
    }

    private FkSpec MakeFkSpec(string sql, bool count)
    {
        var spec = new FkSpec { Sql = sql, Cat = _cat, Enc = _enc, IsCount = count };
        if (!FkLazy)
        {
            // Errors in an action's table (a missing parent of the child it rewrites) belong to the statement that can fire it.
            spec.Get();
        }

        return spec;
    }

    /// <summary>The columns of <paramref name="t"/> a foreign key hook reads: its own foreign key columns and the parent columns others refer to.</summary>
    private HashSet<int> FkColumns(TableInfo t)
    {
        var set = new HashSet<int>();
        foreach (FkInfo f in t.ForeignKeys)
        {
            set.UnionWith(f.Child);
        }

        foreach (TableInfo c in _cat.Tables.Values)
        {
            if (c.Db != t.Db)
            {
                continue;
            }

            foreach (FkInfo f in c.ForeignKeys)
            {
                if (AsciiComparer.Instance.Equals(f.Parent, t.Name) && ResolveFk(c, f, out _) is { } k)
                {
                    set.UnionWith(k.Cols);
                }
            }
        }

        return set;
    }

    private bool FkInvolved(TableInfo t)
        => ForeignKeysEnabled && (t.ForeignKeys.Count > 0 || _cat.Tables.Values.Any(o => o.Db == t.Db && o.ForeignKeys.Any(f => AsciiComparer.Instance.Equals(f.Parent, t.Name))));

    /// <summary>Loads the columns the hooks need from the row under the write cursor (old-row mode) into registers of their own.</summary>
    private int[] CaptureFkImage(WriteCtx w)
    {
        TableInfo t = w.T;
        var img = new int[t.Columns.Length];
        foreach (int i in FkColumns(t))
        {
            img[i] = NewReg();
            if (i == t.RowidAlias)
            {
                Emit(Op.Rowid, w.Cursor, img[i]);
            }
            else
            {
                CompileTableColumn(i, img[i]);
            }
        }

        return img;
    }

    /// <summary>The registers that identify the written row among its table's rows, to leave it out of counts of other rows.</summary>
    private static int[] FkIdentity(WriteCtx w, int[] img)
        => w.T.WithoutRowid ? [.. w.T.PrimaryKey!.Columns.Select(c => img[c])] : [w.RowidReg];

    // ---- the row as a child

    /// <summary>
    /// Counts the row's own reference: when its parent row is missing the counter moves by <paramref name="delta"/> (+1 for a row that
    /// appears, -1 for one that goes away). A NULL in the key means no reference.
    /// </summary>
    private void EmitFkChild(TableInfo t, int[] img, int delta, Func<FkInfo, bool>? only = null)
    {
        foreach (FkInfo f in t.ForeignKeys)
        {
            if (only is not null && !only(f))
            {
                continue;
            }

            FkKey key = ResolveFk(t, f, out OneLiteException? err) ?? throw err!;
            _fkUsed = true;
            int n = f.Child.Length;
            int skip = NewLabel();
            int pb = NewRegs(n);
            for (int i = 0; i < n; i++)
            {
                Emit(Op.IfNull, img[f.Child[i]], skip);
                Emit(Op.Copy, img[f.Child[i]], pb + i);
            }

            var sb = new StringBuilder("SELECT count(*) FROM ").Append(Q(key.Parent.Name)).Append(" WHERE ");
            for (int i = 0; i < n; i++)
            {
                sb.Append(i > 0 ? " AND " : "").Append(Q(key.Parent.Columns[key.Cols[i]].Name)).Append(" = ?").Append(i + 1);
            }

            int cnt = NewReg();
            Emit(Op.FkRun, cnt, 0, pb, MakeFkSpec(sb.ToString(), true), n);
            Emit(Op.FkAdd, f.Deferred ? 1 : 0, delta, cnt, null, 0);
            Mark(skip);
        }
    }

    // ---- the row as a parent

    private enum FkEvent
    {
        Arrive,
        DepartDelete,
        DepartUpdate,
    }

    /// <summary>
    /// The row as a parent key. <paramref name="oldImg"/> holds the old row and <paramref name="newImg"/> the new one (either may be null).
    /// An update that leaves the key alone does nothing. <paramref name="ident"/> leaves the written row itself out of the counts of a table that refers to itself.
    /// </summary>
    private void EmitFkParent(TableInfo t, int[]? oldImg, int[]? newImg, int[]? ident, HashSet<int>? setCols, bool isUpdate)
    {
        foreach (TableInfo c in _cat.Tables.Values.ToList())
        {
            if (c.Db != t.Db)
            {
                continue;
            }

            foreach (FkInfo f in c.ForeignKeys)
            {
                if (!AsciiComparer.Instance.Equals(f.Parent, t.Name))
                {
                    continue;
                }

                // A parent insert next to a child that refers to it wrongly is not an error; only changes of existing keys need the key.
                FkKey? key = ResolveFk(c, f, out OneLiteException? err);
                if (key is null)
                {
                    if (oldImg is null)
                    {
                        continue;
                    }

                    if (isUpdate && setCols is not null && f.ParentColumns is not null && !f.ParentColumns.Any(pc => setCols.Contains(t.FindColumn(pc))))
                    {
                        continue;
                    }

                    throw err!;
                }

                if (isUpdate && setCols is not null && !key.Cols.Any(setCols.Contains))
                {
                    continue;
                }

                _fkUsed = true;
                int n = key.Cols.Length;
                int end = NewLabel();
                if (isUpdate)
                {
                    // the key is unchanged when every column is equal (NULLs count as equal)
                    int chg = NewReg();
                    Emit(Op.Integer, 0, chg, 0, 0L);
                    for (int i = 0; i < n; i++)
                    {
                        int d = NewReg();
                        Emit(Op.IsNot, oldImg![key.Cols[i]], newImg![key.Cols[i]], d);
                        Emit(Op.Or, chg, d, chg);
                    }

                    Emit(Op.IfNot, chg, end, 1);
                }

                int deferred = f.Deferred ? 1 : 0;
                bool self = ReferenceEquals(c, t) && ident is not null;
                if (oldImg is not null)
                {
                    int skipOld = NewLabel();
                    int cnt = EmitFkChildCount(c, f, key, oldImg, skipOld, self ? ident : null);
                    ForeignKeyAction action = isUpdate ? f.OnUpdate : f.OnDelete;
                    if (action == ForeignKeyAction.Restrict)
                    {
                        int none = NewLabel();
                        Emit(Op.IfNot, cnt, none, 1);
                        Emit(Op.Raise, ConstraintCodes.ForeignKey, 0, 0, "FOREIGN KEY constraint failed", (int)ErrorAction.Abort);
                        Mark(none);
                    }
                    else
                    {
                        Emit(Op.FkAdd, deferred, 1, cnt, null, 1);
                    }

                    Mark(skipOld);
                }

                if (newImg is not null && (isUpdate || oldImg is null))
                {
                    int skipNew = NewLabel();
                    int cnt = EmitFkChildCount(c, f, key, newImg, skipNew, self ? ident : null);
                    Emit(Op.FkAdd, deferred, -1, cnt, null, 1);
                    Mark(skipNew);
                }

                if (oldImg is not null)
                {
                    EmitFkAction(c, f, key, oldImg, isUpdate ? newImg : null);
                }

                Mark(end);
            }
        }
    }

    /// <summary>Counts the rows of child table <paramref name="c"/> that refer to the parent key held in <paramref name="img"/>; jumps to <paramref name="skip"/> if the key has a NULL.</summary>
    private int EmitFkChildCount(TableInfo c, FkInfo f, FkKey key, int[] img, int skip, int[]? exclude)
    {
        int n = key.Cols.Length;
        int total = n + (exclude?.Length ?? 0);
        int pb = NewRegs(total);
        for (int i = 0; i < n; i++)
        {
            Emit(Op.IfNull, img[key.Cols[i]], skip);
            Emit(Op.Copy, img[key.Cols[i]], pb + i);
        }

        var sb = new StringBuilder("SELECT count(*) FROM ").Append(Q(c.Name)).Append(" WHERE ");
        for (int i = 0; i < n; i++)
        {
            sb.Append(i > 0 ? " AND " : "").Append(Q(c.Columns[f.Child[i]].Name)).Append(" = ?").Append(i + 1);
        }

        if (exclude is not null)
        {
            if (c.WithoutRowid)
            {
                sb.Append(" AND NOT (");
                for (int i = 0; i < exclude.Length; i++)
                {
                    sb.Append(i > 0 ? " AND " : "").Append(Q(c.Columns[c.PrimaryKey!.Columns[i]].Name)).Append(" IS ?").Append(n + i + 1);
                }

                sb.Append(')');
            }
            else
            {
                sb.Append(" AND rowid IS NOT ?").Append(n + 1);
            }

            for (int i = 0; i < exclude.Length; i++)
            {
                Emit(Op.Copy, exclude[i], pb + n + i);
            }
        }

        int cnt = NewReg();
        Emit(Op.FkRun, cnt, 0, pb, MakeFkSpec(sb.ToString(), true), total);
        return cnt;
    }

    /// <summary>ON DELETE / ON UPDATE CASCADE, SET NULL and SET DEFAULT: rewrite the children that refer to the old key.</summary>
    private void EmitFkAction(TableInfo c, FkInfo f, FkKey key, int[] oldImg, int[]? newImg)
    {
        ForeignKeyAction action = newImg is null ? f.OnDelete : f.OnUpdate;
        if (action is ForeignKeyAction.NoAction or ForeignKeyAction.Restrict)
        {
            return;
        }

        int n = key.Cols.Length;
        int skip = NewLabel();
        bool cascadeUpdate = action == ForeignKeyAction.Cascade && newImg is not null;
        int total = cascadeUpdate ? 2 * n : n;
        int pb = NewRegs(total);
        for (int i = 0; i < n; i++)
        {
            Emit(Op.IfNull, oldImg[key.Cols[i]], skip);
            Emit(Op.Copy, oldImg[key.Cols[i]], pb + i);
            if (cascadeUpdate)
            {
                Emit(Op.Copy, newImg![key.Cols[i]], pb + n + i);
            }
        }

        var where = new StringBuilder(" WHERE ");
        for (int i = 0; i < n; i++)
        {
            where.Append(i > 0 ? " AND " : "").Append(Q(c.Columns[f.Child[i]].Name)).Append(" = ?").Append(i + 1);
        }

        string sql;
        if (action == ForeignKeyAction.Cascade && newImg is null)
        {
            sql = "DELETE FROM " + Q(c.Name) + where;
        }
        else
        {
            var set = new StringBuilder("UPDATE ").Append(Q(c.Name)).Append(" SET ");
            for (int i = 0; i < n; i++)
            {
                ColumnInfo col = c.Columns[f.Child[i]];
                string value = cascadeUpdate ? "?" + (n + i + 1) : action == ForeignKeyAction.SetDefault ? col.DefaultSql ?? "NULL" : "NULL";
                set.Append(i > 0 ? ", " : "").Append(Q(col.Name)).Append(" = ").Append(value);
            }

            sql = set.Append(where).ToString();
        }

        int dummy = NewReg();
        Emit(Op.FkRun, dummy, 0, pb, MakeFkSpec(sql, false), total);
        Mark(skip);
    }
}
