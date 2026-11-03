using System.Collections.Immutable;
using System.Linq;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite.Planning;

// INSERT, UPDATE and DELETE (lang_insert, lang_update, lang_delete, lang_upsert, lang_returning, lang_conflict).
//
// A write statement is compiled into three parts: the rows to write (VALUES, a nested SELECT, or a scan that first collects the keys of the
// rows to change, so that a statement never reads rows it has already written), the per-row subroutine, and the end of the statement. The
// per-row subroutine runs the constraints in the order lang_createtable documents (NOT NULL, CHECK, then the uniqueness constraints), resolves
// conflicts by the ON CONFLICT action in force, and only then touches the B-trees.
internal sealed partial class Compiler
{
    private int[]? _colRegs;
    private int _rowidReg;
    private int _oldPk;

    /// <summary>PRAGMA foreign_keys on the connection compiling the statement.</summary>
    internal bool ForeignKeysEnabled { get; set; }
    private int[]? _excluded;
    private int _excludedRowid;

    /// <summary>One index of the table being written, with the registers that hold its key for the new and the old row.</summary>
    private sealed class IdxW
    {
        internal IndexInfo Info = null!;
        internal int Cursor;
        internal int KeyCount;
        internal int[] Tail = [];
        internal int KeyBase;
        internal int OldBase;
        internal int Cond;
        internal int OldCond;
        internal int DelBase;
        internal int DelCond;
        internal bool Affected = true;
    }

    private sealed class WriteCtx
    {
        internal TableInfo T = null!;
        internal int Cursor;
        internal List<IdxW> Idx = [];
        internal int RowidReg;
        internal int RecBase;
        internal int[] ColReg = [];
        internal int OldRowid;
    }

    private int AddCursor(CursorSpec spec)
    {
        _cursors.Add(spec);
        return _cursors.Count - 1;
    }

    private void PreassignParameters()
    {
        _anonPos = [];
        int pos = 0;
        for (; ; )
        {
            Sql.Parsing.Token t = Sql.Parsing.Lexer.Next(_sql, pos);
            if (t.Kind == Sql.Parsing.TokenKind.Eof)
            {
                return;
            }

            if (t.Kind == Sql.Parsing.TokenKind.Variable)
            {
                string text = _sql[t.Start..t.End];
                if (text == "?")
                {
                    _params.Add(null);
                    _anonPos[t.Start] = _params.Count;
                }
                else if (text.Length > 1 && text[0] == '?')
                {
                    int n = int.Parse(text.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
                    while (_params.Count < n)
                    {
                        _params.Add(null);
                    }
                }
                else if (_params.IndexOf(text) < 0)
                {
                    _params.Add(text);
                }
            }

            pos = t.End;
        }
    }

    private TableInfo TableForWrite(QualifiedName qn)
    {
        string? schema = qn.Schema?.Text;
        string name = qn.Name.Text;
        TableInfo? t = _cat.FindIn(schema, name);
        if (t is null)
        {
            throw Err("no such table: " + (schema is null ? (InTrigger ? "main." + name : name) : schema + "." + name));
        }

        if (ReferenceEquals(t, _cat.MainTables.GetValueOrDefault("sqlite_master")) || ReferenceEquals(t, _cat.TempMaster))
        {
            throw Err("table " + name + " may not be modified");
        }

        if (t.View is not null)
        {
            throw Err("cannot modify " + name + " because it is a view");
        }


        if (t.Unsupported is not null)
        {
            if (t.Unsupported == "views")
            {
                throw Err("cannot modify " + name + " because it is a view");
            }

            throw Unsupported(t.Unsupported);
        }

        return t;
    }

    private static ErrorAction ToErrorAction(ConflictAction a) => a switch
    {
        ConflictAction.Fail => ErrorAction.Fail,
        ConflictAction.Rollback => ErrorAction.Rollback,
        _ => ErrorAction.Abort,
    };

    /// <summary>Emits "fail with this constraint error" for the action in force; IGNORE skips the row instead (lang_conflict).</summary>
    private void EmitConstraintFailure(int ext, string message, ConflictAction action, int skip, int valuesReg = 0, int valuesCount = 0)
    {
        if (action == ConflictAction.Ignore)
        {
            Emit(Op.Goto, 0, skip);
        }
        else
        {
            Emit(Op.Raise, ext, valuesReg, valuesCount, message, (int)ToErrorAction(action));
        }
    }

    private WriteCtx OpenWriteCtx(TableInfo t)
    {
        var w = new WriteCtx { T = t };
        w.Cursor = AddCursor(new CursorSpec { Table = t, Index = t.WithoutRowid ? t.PrimaryKey : null, IsIndexTree = t.WithoutRowid, Root = t.Root });
        Emit(Op.OpenWrite, w.Cursor);
        foreach (IndexInfo ix in t.Indexes)
        {
            if (ix.Root == 0 || ix.Columns.Length == 0)
            {
                continue;
            }

            int cur = AddCursor(new CursorSpec { Table = t, Index = ix, IsIndexTree = true, Root = ix.Root });
            Emit(Op.OpenWrite, cur);
            w.Idx.Add(new IdxW { Info = ix, Cursor = cur });
        }

        AllocRegisters(w);
        return w;
    }

    private void AllocRegisters(WriteCtx w)
    {
        TableInfo t = w.T;
        w.RowidReg = NewReg();
        w.OldRowid = NewReg();
        w.RecBase = NewRegs(Math.Max(t.RecordWidth, 1));
        w.ColReg = new int[t.Columns.Length];
        for (int i = 0; i < t.Columns.Length; i++)
        {
            ColumnInfo c = t.Columns[i];
            w.ColReg[i] = i == t.RowidAlias ? w.RowidReg : c.RecordIndex >= 0 ? w.RecBase + c.RecordIndex : NewReg();
        }

        foreach (IdxW x in w.Idx)
        {
            if (t.WithoutRowid)
            {
                var tail = new List<int>();
                foreach (int pc in t.PrimaryKey!.Columns)
                {
                    if (Array.IndexOf(x.Info.Columns, pc) < 0)
                    {
                        tail.Add(pc);
                    }
                }

                x.Tail = [.. tail];
                x.KeyCount = x.Info.Columns.Length + tail.Count;
            }
            else
            {
                x.KeyCount = x.Info.Columns.Length + 1;
            }

            x.KeyBase = NewRegs(x.KeyCount);
            x.OldBase = NewRegs(x.KeyCount);
            x.Cond = NewReg();
            x.OldCond = NewReg();
            x.DelBase = NewRegs(x.KeyCount);
            x.DelCond = NewReg();
        }
    }

    /// <summary>The same table and cursors with registers of their own (an upsert's UPDATE keeps <c>excluded</c> intact while it builds the new row).</summary>
    private WriteCtx CloneRegisters(WriteCtx w)
    {
        var c = new WriteCtx { T = w.T, Cursor = w.Cursor };
        foreach (IdxW x in w.Idx)
        {
            c.Idx.Add(new IdxW { Info = x.Info, Cursor = x.Cursor });
        }

        AllocRegisters(c);
        return c;
    }

    // ------------------------------------------------------------------ the new row

    /// <summary>
    /// Index keys of the row in registers (new row) or under the cursor (old row). Key = the index columns, then the rowid, or for a
    /// WITHOUT ROWID table the primary key columns the index does not already hold (fileformat2 §1.6, "WITHOUT ROWID tables").
    /// </summary>
    private void EmitIndexKey(WriteCtx w, IdxW x, int keyBase, int cond, int skipIfNot)
    {
        IndexInfo ix = x.Info;
        if (ix.Where is not null)
        {
            CompileInto(ix.Where, cond);
        }

        for (int i = 0; i < ix.Columns.Length; i++)
        {
            if (ix.Terms.Length > i && ix.Terms[i] is { } term)
            {
                CompileInto(term, keyBase + i);
            }
            else
            {
                CompileTableColumn(ix.Columns[i], keyBase + i);
            }
        }

        int n = ix.Columns.Length;
        if (w.T.WithoutRowid)
        {
            foreach (int pc in x.Tail)
            {
                CompileTableColumn(pc, keyBase + n++);
            }
        }
        else if (_colRegs != null)
        {
            Emit(Op.Copy, _rowidReg, keyBase + n);
        }
        else
        {
            Emit(Op.Rowid, _tableCursor, keyBase + n);
        }

        _ = skipIfNot;
    }

    private void SetNewRowMode(WriteCtx w)
    {
        _colRegs = w.ColReg;
        _rowidReg = w.RowidReg;
    }

    private void SetOldRowMode(WriteCtx w)
    {
        _colRegs = null;
        _tableCursor = w.Cursor;
    }

    private static int StrictTypeCode(string declared) => declared.ToUpperInvariant() switch
    {
        "INT" or "INTEGER" => 1,
        "REAL" => 2,
        "TEXT" => 3,
        "BLOB" => 4,
        _ => 5,
    };

    /// <summary>
    /// Everything that does not need the B-trees: affinity (datatype3 §3), STRICT type checks, generated columns, NOT NULL and CHECK. The
    /// columns' values are in <c>w.ColReg</c> and the rowid in <c>w.RowidReg</c>.
    /// </summary>
    private void EmitRowChecks(WriteCtx w, ConflictAction? stmtMode, int skip)
    {
        TableInfo t = w.T;
        SetNewRowMode(w);
        for (int i = 0; i < t.Columns.Length; i++)
        {
            ColumnInfo c = t.Columns[i];
            if (i == t.RowidAlias || c.Generated != GeneratedKind.None)
            {
                continue;
            }

            EmitStoreAffinity(t, c, w.ColReg[i]);
        }

        for (int i = 0; i < t.Columns.Length; i++)
        {
            ColumnInfo c = t.Columns[i];
            if (c.Generated == GeneratedKind.None)
            {
                continue;
            }

            CompileInto(c.GeneratedExpr!, w.ColReg[i]);
            EmitStoreAffinity(t, c, w.ColReg[i]);
        }

        for (int i = 0; i < t.Columns.Length; i++)
        {
            ColumnInfo c = t.Columns[i];
            if (i == t.RowidAlias)
            {
                continue;
            }

            bool notNull = c.NotNull || (t.WithoutRowid && c.PrimaryKey);
            if (!notNull)
            {
                continue;
            }

            ConflictAction act = stmtMode ?? c.NotNullConflict ?? ConflictAction.Abort;
            int ok = NewLabel();
            Emit(Op.NotNull, w.ColReg[i], ok);
            if (act == ConflictAction.Replace && c.Default is not null)
            {
                // REPLACE swaps a NULL for the column's default (lang_conflict, "REPLACE").
                CompileInto(c.Default, w.ColReg[i]);
                EmitStoreAffinity(t, c, w.ColReg[i]);
                Emit(Op.NotNull, w.ColReg[i], ok);
            }

            EmitConstraintFailure(ConstraintCodes.NotNull, "NOT NULL constraint failed: " + t.Name + "." + c.Name, act == ConflictAction.Replace ? ConflictAction.Abort : act, skip);
            Mark(ok);
        }

        foreach (CheckInfo ck in t.Checks)
        {
            int r = Compile(ck.Expr);
            int ok = NewLabel();
            Emit(Op.If, r, ok, 1);
            ConflictAction act = stmtMode ?? ck.OnConflict ?? ConflictAction.Abort;
            EmitConstraintFailure(ConstraintCodes.Check, "CHECK constraint failed: " + ck.Message, act == ConflictAction.Replace ? ConflictAction.Abort : act, skip);
            Mark(ok);
        }
    }

    private void EmitStoreAffinity(TableInfo t, ColumnInfo c, int reg)
    {
        if (c.Affinity != Affinity.None)
        {
            Emit(Op.Affinity, reg, 0, 0, null, (int)c.Affinity);
        }

        if (t.Strict)
        {
            Emit(Op.StrictCheck, reg, StrictTypeCode(c.DeclaredType), 0, c.DeclaredType.ToUpperInvariant() + " column " + t.Name + "." + c.Name);
        }
    }

    // ------------------------------------------------------------------ uniqueness constraints

    private static string UniqueMessage(TableInfo t, IndexInfo ix)
    {
        var parts = new List<string>();
        for (int i = 0; i < ix.Columns.Length; i++)
        {
            if (ix.Columns[i] < 0 || (ix.Terms.Length > i && ix.Terms[i] is not null))
            {
                return "index '" + ix.Name + "'";
            }

            parts.Add(t.Name + "." + t.Columns[ix.Columns[i]].Name);
        }

        return "UNIQUE constraint failed: " + string.Join(", ", parts);
    }

    private static string RowidUniqueMessage(TableInfo t)
        => "UNIQUE constraint failed: " + t.Name + "." + (t.RowidAlias >= 0 ? t.Columns[t.RowidAlias].Name : "rowid");

    /// <summary>Which upsert clause (if any) handles a conflict on the constraint over these columns; -1 = rowid. Null cols means "any".</summary>
    private static int FindUpsert(ImmutableArray<UpsertClause> ups, TableInfo t, int[]? cols, bool isRowid)
    {
        for (int k = 0; k < ups.Length; k++)
        {
            UpsertClause u = ups[k];
            if (u.Target.IsDefaultOrEmpty)
            {
                return k;
            }

            var tc = new List<int>();
            bool rowidTarget = false;
            foreach (OrderingTerm term in u.Target)
            {
                Expr e = term.Expr;
                if (e is CollateExpr ce)
                {
                    e = ce.Operand;
                }

                if (e is ColumnRef cr)
                {
                    int ci = t.FindColumn(cr.Column.Text);
                    if (ci < 0 && !t.WithoutRowid && IsRowidName(cr.Column.Text))
                    {
                        rowidTarget = true;
                    }
                    else if (ci >= 0 && ci == t.RowidAlias)
                    {
                        rowidTarget = true;
                    }
                    else if (ci >= 0)
                    {
                        tc.Add(ci);
                    }
                }
            }

            if (isRowid ? rowidTarget && tc.Count == 0 : cols is not null && tc.Count == cols.Length && !rowidTarget && tc.OrderBy(x => x).SequenceEqual(cols.OrderBy(x => x)))
            {
                return k;
            }
        }

        return -1;
    }

    // ------------------------------------------------------------------ deleting a row

    /// <summary>Deletes the row the write cursor is on, with its index entries (the keys are read from the row).</summary>
    // REPLACE deletes the rows that conflict. Their DELETE triggers fire only while recursive_triggers is on (lang_conflict, lang_createtrigger).
    private void EmitReplaceDelete(WriteCtx w, int skip)
    {
        TableInfo t = w.T;
        bool before = HasTrigger(t, TriggerTime.Before, TriggerEvent.Delete);
        bool after = HasTrigger(t, TriggerTime.After, TriggerEvent.Delete);
        int oldImg = 0;
        if (before || after)
        {
            int off = NewLabel();
            Emit(Op.RecTrig, 0, off);
            oldImg = CaptureOldRow(w);
            if (before)
            {
                EmitTriggers(t, TriggerTime.Before, TriggerEvent.Delete, oldImg, 0, null, skip);
            }

            Mark(off);
        }

        EmitDeleteRow(w, false);
        if (after)
        {
            int off2 = NewLabel();
            Emit(Op.RecTrig, 0, off2);
            EmitTriggers(t, TriggerTime.After, TriggerEvent.Delete, oldImg, 0, null, skip);
            Mark(off2);
        }
    }

    private bool _truncateDelete;

    private void EmitDeleteRow(WriteCtx w, bool count)
    {
        SetOldRowMode(w);
        int[]? fkImg = FkInvolved(w.T) ? CaptureFkImage(w) : null;
        if (fkImg is not null)
        {
            EmitFkChild(w.T, fkImg, -1);
        }

        foreach (IdxW x in w.Idx)
        {
            int skip = NewLabel();
            EmitIndexKey(w, x, x.DelBase, x.DelCond, 0);
            if (x.Info.Where is not null)
            {
                Emit(Op.IfNot, x.DelCond, skip, 1);
            }

            Emit(Op.IdxDelete, x.Cursor, x.DelBase, x.KeyCount);
            Mark(skip);
        }

        Emit(Op.Delete, w.Cursor, 0, 0, count && !w.T.WithoutRowid ? w.T : null, count ? (_truncateDelete ? 1 : 1 | 4) : 0);
        if (fkImg is not null)
        {
            EmitFkParent(w.T, fkImg, null, null, null, false);
        }
    }

    // ------------------------------------------------------------------ INSERT

    internal VmProgram CompileInsert(InsertStatement ins)
    {
        if (ins.With is { } with)
        {
            RegisterWith(with);
        }

        if (IsViewTarget(ins.Table, out TableInfo insView))
        {
            return CompileViewInsert(ins, insView);
        }

        if (IsVirtualTarget(ins.Table, out TableInfo insVirtual))
        {
            return CompileVirtualInsert(ins, insVirtual);
        }

        PreassignParameters();
        TableInfo t = TableForWrite(ins.Table);
        _table = t;
        _alias = ins.Alias?.Text ?? t.Name;
        SetColumns(true);
        ConflictAction? mode = ins.Verb == InsertVerb.Replace ? ConflictAction.Replace : ins.OrAction;

        // Which table column each source value goes to.
        var targets = new List<int>();
        if (ins.Columns.IsDefaultOrEmpty)
        {
            // DEFAULT VALUES supplies nothing: every column takes its default
            for (int i = 0; ins.Source.Select is not null && i < t.Columns.Length; i++)
            {
                if (t.Columns[i].Generated == GeneratedKind.None)
                {
                    targets.Add(i);
                }
            }
        }
        else
        {
            foreach (Name n in ins.Columns)
            {
                int ci = t.FindColumn(n.Text);
                if (ci < 0 && !t.WithoutRowid && IsRowidName(n.Text))
                {
                    ci = t.RowidAlias >= 0 ? t.RowidAlias : -1;
                    if (t.RowidAlias < 0)
                    {
                        targets.Add(-1);
                        continue;
                    }
                }

                if (ci < 0)
                {
                    throw Err("table " + t.Name + " has no column named " + n.Text);
                }

                if (t.Columns[ci].Generated != GeneratedKind.None)
                {
                    throw Err("cannot INSERT into generated column \"" + t.Columns[ci].Name + "\"");
                }

                targets.Add(ci);
            }
        }

        SelectStatement? sel = ins.Source.Select;
        ImmutableArray<ImmutableArray<Expr>> valueRows = default;
        if (sel is { Cores: [ValuesCore vc], With: null } && sel.Cores.Length == 1)
        {
            valueRows = vc.Rows;
        }

        int nSrc = targets.Count;
        VmProgram? subProg = null;
        if (sel is not null && valueRows.IsDefault)
        {
            var inner = new Compiler(this) { _cteEnv = _cteEnv };
            subProg = inner.CompileSelect(sel);
            subProg.SchemaCookie = _cat.Cookie;
            subProg.Encoding = _enc;
            if (subProg.ResultCount != nSrc)
            {
                throw Err((ins.Columns.IsDefaultOrEmpty ? "table " + t.Name + " has " + nSrc + " columns but " + subProg.ResultCount + " values were supplied" : subProg.ResultCount + " values for " + nSrc + " columns"));
            }
        }
        else if (!valueRows.IsDefault)
        {
            // lang_insert: a multi-row VALUES list is checked for equal row widths before the columns are matched
            foreach (ImmutableArray<Expr> row in valueRows)
            {
                if (row.Length != valueRows[0].Length)
                {
                    throw Err("all VALUES must have the same number of terms");
                }
            }

            foreach (ImmutableArray<Expr> row in valueRows)
            {
                if (row.Length != nSrc)
                {
                    throw Err(ins.Columns.IsDefaultOrEmpty ? "table " + t.Name + " has " + nSrc + " columns but " + row.Length + " values were supplied" : row.Length + " values for " + nSrc + " columns");
                }
            }
        }
        else if (!ins.Columns.IsDefaultOrEmpty)
        {
            // DEFAULT VALUES ignores a column list only when there is none.
            throw Err("near \"DEFAULT\": syntax error");
        }

        Emit(Op.Transaction, 0, 0, 0, null, 1 | 8);
        int main = NewLabel();
        Emit(Op.Goto, 0, main);

        // ---- the per-row subroutine
        int body = NewLabel();
        int skip = NewLabel();
        Mark(body);
        int ret = NewReg();
        int srcBase = NewRegs(Math.Max(nSrc, 1));
        var returning = ins.Returning;
        // The write context is created before the subroutine's code but its OpenWrite instructions must run in the main part: emit them there.
        // (Cursor numbers and registers are all that the subroutine needs.)
        int mark = _code.Count;
        WriteCtx w = OpenWriteCtx(t);
        List<Instr> opens = _code.GetRange(mark, _code.Count - mark);
        _code.RemoveRange(mark, _code.Count - mark);

        SetNewRowMode(w);
        int seq = 0;
        int seqCursor = 0;
        if (t.Autoincrement)
        {
            TableInfo st = _cat.FindIn(_cat.SchemaName(t.Db), "sqlite_sequence") ?? throw Err("no such table: sqlite_sequence");
            seqCursor = AddCursor(new CursorSpec { Table = st, Root = st.Root });
            opens.Add(new Instr { Op = Op.OpenWrite, P1 = seqCursor });
            seq = NewRegs(3);
        }

        // map the source values onto the columns
        EmitRowMapping(w, targets, srcBase, ret);
        bool rowidExplicit = targets.Contains(t.RowidAlias) && t.RowidAlias >= 0;
        for (int k = 0; k < targets.Count; k++)
        {
            if (targets[k] == -1)
            {
                // an explicit rowid on a table without an alias column
                Emit(Op.Copy, srcBase + k, w.RowidReg);
                rowidExplicit = true;
            }
        }

        EmitRowidAssignment(w, rowidExplicit || (t.RowidAlias >= 0 && !t.WithoutRowid), seq, seqCursor, t.RowidAlias >= 0 && !targets.Contains(t.RowidAlias) && t.Columns[t.RowidAlias].Default is null && !rowidExplicit);
        if (HasTrigger(t, TriggerTime.Before, TriggerEvent.Insert))
        {
            EmitTriggers(t, TriggerTime.Before, TriggerEvent.Insert, 0, CaptureNewRow(w), null, skip);
        }

        EmitRowChecks(w, mode, skip);
        foreach (IdxW x in w.Idx)
        {
            EmitIndexKey(w, x, x.KeyBase, x.Cond, 0);
        }

        // upsert code blocks are emitted after the insert itself
        var upsertLabels = new int[ins.Upserts.IsDefault ? 0 : ins.Upserts.Length];
        for (int k = 0; k < upsertLabels.Length; k++)
        {
            upsertLabels[k] = NewLabel();
        }

        EmitInsertConflicts(w, mode, skip, ins.Upserts, upsertLabels);
        if (t.WithoutRowid)
        {
            Emit(Op.IdxInsert, w.Cursor, w.RecBase, t.RecordWidth, null, 1);
        }
        else
        {
            Emit(Op.Insert, w.Cursor, w.RecBase, w.RowidReg, t, 1 | 2 | 4);
        }

        foreach (IdxW x in w.Idx)
        {
            int nx = NewLabel();
            if (x.Info.Where is not null)
            {
                Emit(Op.IfNot, x.Cond, nx, 1);
            }

            Emit(Op.IdxInsert, x.Cursor, x.KeyBase, x.KeyCount);
            Mark(nx);
        }

        if (FkInvolved(t))
        {
            // the new row, now in the table: first the orphans it adopts (its own reference is left out), then its own reference
            EmitFkParent(t, null, w.ColReg, FkIdentity(w, w.ColReg), null, false);
            EmitFkChild(t, w.ColReg, 1);
        }

        if (HasTrigger(t, TriggerTime.After, TriggerEvent.Insert))
        {
            EmitTriggers(t, TriggerTime.After, TriggerEvent.Insert, 0, CaptureNewRow(w), null, skip);
        }

        int retCols = 0;
        int retBase = 0;
        string[] retNames = [];
        if (returning is not null)
        {
            EmitReturningRow(w, returning, out retBase, out retCols, out retNames);
        }

        Emit(Op.Goto, 0, skip);

        for (int k = 0; k < upsertLabels.Length; k++)
        {
            Mark(upsertLabels[k]);
            EmitUpsertUpdate(w, ins.Upserts[k], skip, returning);
        }

        Mark(skip);
        Emit(Op.Return, ret);

        // ---- the main part: open cursors, feed rows to the subroutine
        Mark(main);
        foreach (Instr o in opens)
        {
            _code.Add(o);
        }

        if (t.Autoincrement)
        {
            Emit(Op.SeqLoad, seq, seqCursor, 0, t.Name);
        }

        if (!valueRows.IsDefault)
        {
            foreach (ImmutableArray<Expr> row in valueRows)
            {
                _tableCursor = -1;
                _colRegs = null;
                SetColumns(false);
                for (int k = 0; k < row.Length; k++)
                {
                    CompileInto(row[k], srcBase + k);
                }

                SetColumns(true);
                Emit(Op.Gosub, ret, body);
            }
        }
        else if (subProg is not null)
        {
            bool drain = MentionsTable(sel!, t.Name);
            var spec = new SubSelectSpec { Program = subProg, Drain = drain };
            Emit(Op.SubSelect, 0, 0, 0, spec);
            int again = NewLabel();
            int done = NewLabel();
            Mark(again);
            Emit(Op.SubNext, 0, done, srcBase, spec);
            Emit(Op.Gosub, ret, body);
            Emit(Op.Goto, 0, again);
            Mark(done);
        }
        else
        {
            Emit(Op.Gosub, ret, body);
        }

        if (t.Autoincrement)
        {
            Emit(Op.SeqStore, seq, seqCursor, 0, t.Name);
        }

        VmProgram p = EmitReturningLoop(retBase, retCols, retNames);
        return p;
    }

    private bool MentionsTable(SelectStatement s, string table)
    {
        // A SELECT that reads the table being written must not see the rows it adds (the "Halloween problem"): its rows are read in full first.
        // The test is on the statement's text, so a quoted name counts; when the text cannot be found the rows are read first to be safe.
        if (s.Span.End > _sql.Length || s.Span.End <= s.Span.Start)
        {
            return true;
        }

        return _sql.AsSpan(s.Span.Start, s.Span.End - s.Span.Start).Contains(table.AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Source values to column registers; unnamed columns take their DEFAULT or NULL (lang_insert).</summary>
    private void EmitRowMapping(WriteCtx w, List<int> targets, int srcBase, int ret)
    {
        _ = ret;
        TableInfo t = w.T;
        _colRegs = null;
        SetColumns(false);
        for (int i = 0; i < t.Columns.Length; i++)
        {
            ColumnInfo c = t.Columns[i];
            int reg = w.ColReg[i];
            if (c.Generated != GeneratedKind.None)
            {
                continue;
            }

            int k = targets.IndexOf(i);
            if (k >= 0)
            {
                Emit(Op.Copy, srcBase + k, reg);
            }
            else if (c.Default is not null)
            {
                CompileInto(c.Default, reg);
            }
            else
            {
                Emit(Op.Null, 0, reg);
            }

            if (i == t.RowidAlias)
            {
                continue;
            }
        }

        if (t.RowidAlias < 0)
        {
            Emit(Op.Null, 0, w.RowidReg);
        }

        if (t.RowidAlias >= 0 && t.Columns[t.RowidAlias].RecordIndex >= 0)
        {
            // The alias column's own slot in the record stays NULL: the value is the rowid (fileformat2 §2.1, "INTEGER PRIMARY KEY").
            Emit(Op.Null, 0, w.RecBase + t.Columns[t.RowidAlias].RecordIndex);
        }

        SetColumns(true);
    }

    /// <summary>Chooses the rowid: the value given for the INTEGER PRIMARY KEY or rowid column, else one more than the largest (lang_createtable "ROWID").</summary>
    private void EmitRowidAssignment(WriteCtx w, bool maybeGiven, int seq, int seqCursor, bool neverGiven)
    {
        _ = seqCursor;
        TableInfo t = w.T;
        if (t.WithoutRowid)
        {
            return;
        }

        if (neverGiven || !maybeGiven)
        {
            Emit(Op.NewRowid, w.Cursor, w.RowidReg, seq);
            return;
        }

        int have = NewLabel();
        int done = NewLabel();
        Emit(Op.NotNull, w.RowidReg, have);
        Emit(Op.NewRowid, w.Cursor, w.RowidReg, seq);
        Emit(Op.Goto, 0, done);
        Mark(have);
        Emit(Op.RowidFix, w.RowidReg);
        if (seq != 0)
        {
            Emit(Op.MaxReg, seq, w.RowidReg);
        }

        Mark(done);
    }

    /// <summary>Probes the rowid and every UNIQUE index for an existing entry and resolves each hit by the action in force.</summary>
    private void EmitInsertConflicts(WriteCtx w, ConflictAction? stmtMode, int skip, ImmutableArray<UpsertClause> ups, int[] upsertLabels)
    {
        TableInfo t = w.T;
        bool hasUps = !ups.IsDefault && ups.Length > 0;

        // the table's own key: the rowid, or the PRIMARY KEY of a WITHOUT ROWID table
        {
            int noConflict = NewLabel();
            ConflictAction act = stmtMode ?? t.PkConflict ?? ConflictAction.Abort;
            string msg;
            int[]? pkCols = null;
            if (t.WithoutRowid)
            {
                if (t.PrimaryKey is null)
                {
                    return;
                }

                pkCols = t.PrimaryKey.Columns;
                Emit(Op.IdxProbe, w.Cursor, noConflict, w.RecBase, null, pkCols.Length | 0x200);
                msg = UniqueMessage(t, t.PrimaryKey);
            }
            else
            {
                Emit(Op.SeekRowid, w.Cursor, noConflict, w.RowidReg, null, 0x200);
                msg = RowidUniqueMessage(t);
            }

            int u = hasUps ? FindUpsert(ups, t, pkCols, !t.WithoutRowid) : -1;
            if (u >= 0)
            {
                EmitUpsertDispatch(ups[u], upsertLabels[u], skip);
            }
            else if (act == ConflictAction.Replace)
            {
                EmitReplaceDelete(w, skip);
                SetNewRowMode(w);
            }
            else
            {
                EmitConstraintFailure(ConstraintCodes.PrimaryKey, msg, act, skip, t.WithoutRowid ? w.RecBase : w.RowidReg, t.WithoutRowid ? t.PrimaryKey!.Columns.Length : 1);
            }

            Mark(noConflict);
        }

        // The reference tries the newest index first, which decides the message when a row breaks several constraints.
        foreach (IdxW x in Enumerable.Reverse(w.Idx))
        {
            if (!x.Info.Unique)
            {
                continue;
            }

            int noConflict = NewLabel();
            if (x.Info.Where is not null)
            {
                Emit(Op.IfNot, x.Cond, noConflict, 1);
            }

            Emit(Op.IdxProbe, x.Cursor, noConflict, x.KeyBase, null, x.Info.Columns.Length | 0x100 | 0x200);
            ConflictAction act = stmtMode ?? x.Info.OnConflict ?? ConflictAction.Abort;
            int u = hasUps ? FindUpsert(ups, t, x.Info.Columns, false) : -1;
            if (u >= 0 || act == ConflictAction.Replace)
            {
                EmitPositionOnConflictRow(w, x, noConflict);
                if (u >= 0)
                {
                    EmitUpsertDispatch(ups[u], upsertLabels[u], skip);
                }
                else
                {
                    EmitReplaceDelete(w, skip);
                    SetNewRowMode(w);
                }
            }
            else
            {
                EmitConstraintFailure(x.Info.IsPrimaryKey ? ConstraintCodes.PrimaryKey : ConstraintCodes.Unique, UniqueMessage(t, x.Info), act, skip, x.KeyBase, x.Info.Columns.Length);
            }

            Mark(noConflict);
        }
    }

    private void EmitUpsertDispatch(UpsertClause clause, int label, int skip)
    {
        Emit(Op.Goto, 0, clause.DoNothing ? skip : label);
    }

    /// <summary>The index cursor is on a conflicting entry; put the table cursor on the row that entry belongs to.</summary>
    private void EmitPositionOnConflictRow(WriteCtx w, IdxW x, int missing)
    {
        TableInfo t = w.T;
        if (!t.WithoutRowid)
        {
            int r = NewReg();
            Emit(Op.IdxRowid, x.Cursor, r);
            Emit(Op.SeekRowid, w.Cursor, missing, r);
        }
        else
        {
            int n = t.PrimaryKey!.Columns.Length;
            int b = NewRegs(n);
            // The entry holds the index columns and then the primary key columns it lacks; assemble the whole primary key.
            for (int j = 0; j < n; j++)
            {
                int pc = t.PrimaryKey.Columns[j];
                int at = Array.IndexOf(x.Info.Columns, pc);
                if (at >= 0)
                {
                    Emit(Op.Column, x.Cursor, at, b + j);
                }
                else
                {
                    Emit(Op.Column, x.Cursor, x.Info.Columns.Length + Array.IndexOf(x.Tail, pc), b + j);
                }
            }

            Emit(Op.IdxProbe, w.Cursor, missing, b, null, n);
        }
    }

    // ------------------------------------------------------------------ upsert DO UPDATE and RETURNING

    private void EmitUpsertUpdate(WriteCtx w, UpsertClause u, int skip, ReturningClause? returning)
    {
        if (u.DoNothing)
        {
            Emit(Op.Goto, 0, skip);
            return;
        }

        // the new row's values stay readable as excluded.*; the update builds its row in registers of its own
        _excluded = (int[])w.ColReg.Clone();
        _excludedRowid = w.RowidReg;
        WriteCtx uw = CloneRegisters(w);
        SetOldRowMode(uw);
        if (u.UpdateWhere is not null)
        {
            int r = Compile(u.UpdateWhere);
            Emit(Op.IfNot, r, skip, 1);
        }

        EmitUpdateBody(uw, ResolveSets(w.T, u.Sets), null, skip, returning, true, null);
        _excluded = null;
        Emit(Op.Goto, 0, skip);
    }

    // ------------------------------------------------------------------ RETURNING

    private void EmitReturningRow(WriteCtx w, ReturningClause rc, out int baseReg, out int count, out string[] names)
    {
        TableInfo t = w.T;
        var exprs = new List<(Expr? E, int Col, string Name)>();
        foreach (ResultColumn rcol in rc.Columns)
        {
            switch (rcol)
            {
                case StarColumn:
                    for (int i = 0; i < t.Columns.Length; i++)
                    {
                        exprs.Add((null, i, t.Columns[i].Name));
                    }

                    break;
                case ExprColumn ec:
                    exprs.Add((ec.Expr, -1, ResultName(ec)));
                    break;
            }
        }

        count = exprs.Count;
        baseReg = NewRegs(Math.Max(count, 1));
        names = [.. exprs.Select(e => e.Name)];
        for (int i = 0; i < count; i++)
        {
            if (exprs[i].E is { } e)
            {
                CompileInto(e, baseReg + i);
            }
            else
            {
                CompileTableColumn(exprs[i].Col, baseReg + i);
            }
        }

        Emit(Op.RetAdd, baseReg, count);
    }

    private VmProgram EmitReturningLoop(int retBase, int retCols, string[] retNames)
    {
        if (_fkUsed)
        {
            Emit(Op.FkCheck);
        }

        if (retCols > 0)
        {
            int again = NewLabel();
            int done = NewLabel();
            Mark(again);
            Emit(Op.RetRow, retBase, done, retCols);
            Emit(Op.ResultRow, retBase, retCols);
            Emit(Op.Goto, 0, again);
            Mark(done);
        }

        Emit(Op.Halt);
        return new VmProgram
        {
            Code = Finish(),
            RegisterCount = _nReg + 1,
            Cursors = [.. _cursors],
            ColumnNames = retNames,
            ResultBase = retBase,
            ResultCount = retCols,
            ParameterNames = [.. _params],
            Encoding = _enc,
            OnceCount = _onceCount,
            SorterCount = _sorterCount,
            SetCount = _setCount,
            Aggregates = [.. _aggSpecs],
        };
    }

    // ------------------------------------------------------------------ UPDATE

    internal VmProgram CompileUpdate(UpdateStatement up)
    {
        if (IsViewTarget(up.Target.Name, out TableInfo upView))
        {
            return CompileViewUpdate(up, upView);
        }

        if (up.With is { } with)
        {
            RegisterWith(with);
        }

        if (IsVirtualTarget(up.Target.Name, out TableInfo upVirtual))
        {
            return CompileVirtualUpdate(up, upVirtual);
        }

        PreassignParameters();
        TableInfo t = TableForWrite(up.Target.Name);
        if (up.From is not null)
        {
            return CompileUpdateFrom(up, t);
        }

        ConflictAction? mode = up.OrAction;

        List<(int Col, Expr Value, int Part, int Of)> sets = ResolveSets(t, up.Sets);

        // ---- phase 1: collect the keys of the rows to change
        BindTarget(up.Target);
        Emit(Op.Transaction, 0, 0, 0, null, 1 | 8);
        int keyBase = EmitKeyCollection(t, up.Where);

        // ---- phase 2: apply
        WriteCtx w = OpenWriteCtx(t);
        int loop = NewLabel();
        int done = NewLabel();
        Mark(loop);
        int keyCount = t.WithoutRowid ? t.PrimaryKey!.Columns.Length : 0;
        Emit(Op.RowSetRead, keyBase, done, keyCount);
        int next = loop;
        if (t.WithoutRowid)
        {
            Emit(Op.IdxProbe, w.Cursor, next, keyBase, null, keyCount);
        }
        else
        {
            Emit(Op.SeekRowid, w.Cursor, next, keyBase);
        }

        SetOldRowMode(w);
        int retBase = 0;
        int retCols = 0;
        string[] retNames = [];
        EmitUpdateBody(w, sets, mode, next, up.Returning, true, (b, c, n) => { retBase = b; retCols = c; retNames = n; });
        Emit(Op.Goto, 0, loop);
        Mark(done);
        return EmitReturningLoop(retBase, retCols, retNames);
    }

    private static List<(int Col, Expr Value, int Part, int Of)> ResolveSets(TableInfo t, IEnumerable<SetClause> setClauses)
    {
        var sets = new List<(int Col, Expr Value, int Part, int Of)>();
        foreach (SetClause sc in setClauses)
        {
            for (int k = 0; k < sc.Columns.Length; k++)
            {
                string cn = sc.Columns[k].Text;
                int ci = t.FindColumn(cn);
                if (ci < 0 && !t.WithoutRowid && IsRowidName(cn))
                {
                    ci = -1;
                }
                else if (ci < 0)
                {
                    throw Err("no such column: " + cn);
                }
                else if (t.Columns[ci].Generated != GeneratedKind.None)
                {
                    throw Err("cannot UPDATE generated column \"" + t.Columns[ci].Name + "\"");
                }

                sets.Add((ci, sc.Value, k, sc.Columns.Length));
            }
        }

        return sets;
    }

    /// <summary>Collects, for every row the WHERE clause selects, its rowid (or primary key) into the row set; returns the register the keys are read into.</summary>
    private int EmitKeyCollection(TableInfo t, Expr? where)
    {
        int n = t.WithoutRowid ? t.PrimaryKey!.Columns.Length : 1;
        int regs = NewRegs(n);
        EmitNest(_dmlScope!, where, [], () =>
        {
            if (t.WithoutRowid)
            {
                for (int j = 0; j < n; j++)
                {
                    CompileTableColumn(t.PrimaryKey!.Columns[j], regs + j);
                }

                Emit(Op.RowSetAdd, regs, n);
            }
            else
            {
                Emit(Op.Rowid, _tableCursor, regs);
                Emit(Op.RowSetAdd, regs, 0);
            }
        });
        return regs;
    }

    /// <summary>
    /// Rewrites the row the write cursor is on. New values are computed from the old row first (every SET expression sees the old values), then
    /// constraints are checked, conflicts resolved and the old index entries swapped for the new ones.
    /// </summary>
    private void EmitUpdateBody(WriteCtx w, List<(int Col, Expr Value, int Part, int Of)> sets, ConflictAction? mode, int skip, ReturningClause? returning, bool count, Action<int, int, string[]>? retOut)
    {
        TableInfo t = w.T;
        SetOldRowMode(w);
        var setCols = new HashSet<int>(sets.Select(s => s.Col));
        bool rowidChanged = setCols.Contains(-1) || (t.RowidAlias >= 0 && setCols.Contains(t.RowidAlias));
        bool pkChanged = t.WithoutRowid && t.PrimaryKey!.Columns.Any(setCols.Contains);

        // the old rowid, or the old primary key of a WITHOUT ROWID table
        if (!t.WithoutRowid)
        {
            Emit(Op.Rowid, w.Cursor, w.OldRowid);
        }
        else
        {
            _oldPk = NewRegs(t.PrimaryKey!.Columns.Length);
            for (int j = 0; j < t.PrimaryKey.Columns.Length; j++)
            {
                CompileTableColumn(t.PrimaryKey.Columns[j], _oldPk + j);
            }
        }

        // the foreign key columns of the old row, while the cursor still stands on it
        int[]? fkOld = FkInvolved(t) ? CaptureFkImage(w) : null;
        bool tBefore = HasTrigger(t, TriggerTime.Before, TriggerEvent.Update, setCols);
        bool tAfter = HasTrigger(t, TriggerTime.After, TriggerEvent.Update, setCols);
        int trOld = 0;
        if (tBefore || tAfter)
        {
            trOld = CaptureOldRow(w);
            SetOldRowMode(w);
        }

        // old index keys, while the cursor still stands on the old row
        foreach (IdxW x in w.Idx)
        {
            x.Affected = rowidChanged || pkChanged || IndexTouched(t, x.Info, setCols);
            if (x.Affected)
            {
                EmitIndexKey(w, x, x.OldBase, x.OldCond, 0);
            }
        }

        // new values
        for (int i = 0; i < t.Columns.Length; i++)
        {
            ColumnInfo c = t.Columns[i];
            if (c.Generated != GeneratedKind.None)
            {
                continue;
            }

            var hit = sets.Where(s => s.Col == i).ToList();
            int reg = w.ColReg[i];
            if (hit.Count > 0)
            {
                CompileSetValue(hit[^1], reg);
            }
            else if (i == t.RowidAlias)
            {
                Emit(Op.Rowid, w.Cursor, reg);
            }
            else
            {
                CompileTableColumn(i, reg);
            }
        }

        if (t.RowidAlias < 0 && !t.WithoutRowid)
        {
            var hit = sets.Where(s => s.Col == -1).ToList();
            if (hit.Count > 0)
            {
                CompileSetValue(hit[^1], w.RowidReg);
            }
            else
            {
                Emit(Op.Copy, w.OldRowid, w.RowidReg);
            }
        }

        if (t.RowidAlias >= 0 && t.Columns[t.RowidAlias].RecordIndex >= 0)
        {
            Emit(Op.Null, 0, w.RecBase + t.Columns[t.RowidAlias].RecordIndex);
        }

        if (!t.WithoutRowid)
        {
            // a NULL rowid assigned to the alias column stays NULL for the check below: the reference fails with a datatype mismatch
            Emit(Op.RowidFix, w.RowidReg);
        }

        if (tBefore)
        {
            EmitTriggers(t, TriggerTime.Before, TriggerEvent.Update, trOld, CaptureNewRow(w), setCols, skip);
        }

        EmitRowChecks(w, mode, skip);
        foreach (IdxW x in w.Idx)
        {
            if (x.Affected)
            {
                EmitIndexKey(w, x, x.KeyBase, x.Cond, 0);
            }
        }

        SetNewRowMode(w);
        EmitUpdateConflicts(w, mode, skip, rowidChanged, pkChanged);
        Func<FkInfo, bool>? fkChildChanged = null;
        if (fkOld is not null)
        {
            // a reference needs a new look when its columns change, or, in a table that refers to itself, when the key it may point at does
            fkChildChanged = f => f.Child.Any(setCols.Contains) || (AsciiComparer.Instance.Equals(f.Parent, t.Name) && (f.ParentColumns is null || f.ParentColumns.Any(pc => setCols.Contains(t.FindColumn(pc))) || rowidChanged));
            EmitFkChild(t, fkOld, -1, fkChildChanged);
        }

        // swap index entries and rewrite the row
        foreach (IdxW x in w.Idx)
        {
            if (!x.Affected)
            {
                continue;
            }

            int nx = NewLabel();
            if (x.Info.Where is not null)
            {
                Emit(Op.IfNot, x.OldCond, nx, 1);
            }

            Emit(Op.IdxDelete, x.Cursor, x.OldBase, x.KeyCount);
            Mark(nx);
        }

        if (t.WithoutRowid)
        {
            EmitReseek(w, skip);
            Emit(Op.Delete, w.Cursor, 0, 0, null, 0);
            Emit(Op.IdxInsert, w.Cursor, w.RecBase, t.RecordWidth, null, count ? 1 : 0);
        }
        else
        {
            if (rowidChanged)
            {
                Emit(Op.SeekRowid, w.Cursor, skip, w.OldRowid);
                Emit(Op.Delete, w.Cursor, 0, 0, null, 0);
            }

            Emit(Op.Insert, w.Cursor, w.RecBase, w.RowidReg, t, count ? 1 | 8 : 8);
        }

        foreach (IdxW x in w.Idx)
        {
            if (!x.Affected)
            {
                continue;
            }

            int nx = NewLabel();
            if (x.Info.Where is not null)
            {
                Emit(Op.IfNot, x.Cond, nx, 1);
            }

            Emit(Op.IdxInsert, x.Cursor, x.KeyBase, x.KeyCount);
            Mark(nx);
        }

        if (fkOld is not null)
        {
            EmitFkChild(t, w.ColReg, 1, fkChildChanged);
            EmitFkParent(t, fkOld, w.ColReg, FkIdentity(w, w.ColReg), setCols, true);
        }

        if (tAfter)
        {
            EmitTriggers(t, TriggerTime.After, TriggerEvent.Update, trOld, CaptureNewRow(w), setCols, skip);
        }

        if (returning is not null)
        {
            EmitReturningRow(w, returning, out int rb, out int rc, out string[] rn);
            retOut?.Invoke(rb, rc, rn);
        }
    }

    private void CompileSetValue((int Col, Expr Value, int Part, int Of) s, int reg)
    {
        if (s.Of > 1)
        {
            if (s.Value is RowValueExpr rv && rv.Items.Length == s.Of)
            {
                CompileInto(rv.Items[s.Part], reg);
                return;
            }

            throw Unsupported("multi-column SET from a subquery");
        }

        CompileInto(s.Value, reg);
    }

    private static bool IndexTouched(TableInfo t, IndexInfo ix, HashSet<int> setCols)
    {
        if (ix.Where is not null || ix.Terms.Any(e => e is not null))
        {
            return true;
        }

        foreach (int c in ix.Columns)
        {
            if (c >= 0 && (setCols.Contains(c) || t.Columns[c].Generated != GeneratedKind.None))
            {
                return true;
            }
        }

        return false;
    }

    private void EmitUpdateConflicts(WriteCtx w, ConflictAction? stmtMode, int skip, bool rowidChanged, bool pkChanged)
    {
        TableInfo t = w.T;
        if (rowidChanged || pkChanged)
        {
            int noConflict = NewLabel();
            ConflictAction act = stmtMode ?? t.PkConflict ?? ConflictAction.Abort;
            string msg;
            if (t.WithoutRowid)
            {
                Emit(Op.IdxProbe, w.Cursor, noConflict, w.RecBase, null, t.PrimaryKey!.Columns.Length | 0x200);
                msg = UniqueMessage(t, t.PrimaryKey);
            }
            else
            {
                // Setting the rowid to the value it already has is no conflict with itself.
                int same = NewReg();
                Emit(Op.Eq, w.RowidReg, w.OldRowid, same, null, CmpFlags(Affinity.None, CollKind.Binary));
                Emit(Op.If, same, noConflict, 0);
                Emit(Op.SeekRowid, w.Cursor, noConflict, w.RowidReg, null, 0x200);
                msg = RowidUniqueMessage(t);
            }

            if (act == ConflictAction.Replace)
            {
                EmitReplaceDelete(w, skip);
                SetNewRowMode(w);
                EmitReseek(w, skip);
            }
            else
            {
                EmitConstraintFailure(ConstraintCodes.PrimaryKey, msg, act, skip, t.WithoutRowid ? w.RecBase : w.RowidReg, t.WithoutRowid ? t.PrimaryKey!.Columns.Length : 1);
            }

            Mark(noConflict);
        }

        // The reference tries the newest index first, which decides the message when a row breaks several constraints.
        foreach (IdxW x in Enumerable.Reverse(w.Idx))
        {
            if (!x.Info.Unique || !x.Affected)
            {
                continue;
            }

            int noConflict = NewLabel();
            if (x.Info.Where is not null)
            {
                Emit(Op.IfNot, x.Cond, noConflict, 1);
            }

            // an entry that belongs to this very row is not a conflict
            var spec = new ProbeSpec();
            if (t.WithoutRowid)
            {
                spec.TailBase = w.OldRowid; // replaced below by the old primary key
            }

            ProbeSpec? exclusion = null;
            if (!t.WithoutRowid)
            {
                spec.TailBase = w.OldRowid;
                spec.TailCount = 1;
                exclusion = spec;
            }
            else if (x.Tail.Length > 0)
            {
                // old primary key columns not in the index, in order, read from the old row's registers captured in OldBase's tail
                spec.TailBase = x.OldBase + x.Info.Columns.Length;
                spec.TailCount = x.Tail.Length;
                exclusion = spec;
            }

            Emit(Op.IdxProbe, x.Cursor, noConflict, x.KeyBase, exclusion, x.Info.Columns.Length | 0x100 | 0x200);
            ConflictAction act = stmtMode ?? x.Info.OnConflict ?? ConflictAction.Abort;
            if (act == ConflictAction.Replace)
            {
                EmitPositionOnConflictRow(w, x, noConflict);
                EmitReplaceDelete(w, skip);
                SetNewRowMode(w);
                EmitReseek(w, skip);
            }
            else
            {
                EmitConstraintFailure(x.Info.IsPrimaryKey ? ConstraintCodes.PrimaryKey : ConstraintCodes.Unique, UniqueMessage(t, x.Info), act, skip, x.KeyBase, x.Info.Columns.Length);
            }

            Mark(noConflict);
        }
    }

    /// <summary>After REPLACE removed another row, stand on the row being updated again.</summary>
    private void EmitReseek(WriteCtx w, int skip)
    {
        if (w.T.WithoutRowid)
        {
            Emit(Op.IdxProbe, w.Cursor, skip, _oldPk, null, w.T.PrimaryKey!.Columns.Length);
            return;
        }

        Emit(Op.SeekRowid, w.Cursor, skip, w.OldRowid);
    }

    // ------------------------------------------------------------------ DELETE

    internal VmProgram CompileDelete(DeleteStatement del)
    {
        if (del.With is { } with)
        {
            RegisterWith(with);
        }

        if (IsViewTarget(del.Target.Name, out TableInfo delView))
        {
            return CompileViewDelete(del, delView);
        }

        if (IsVirtualTarget(del.Target.Name, out TableInfo delVirtual))
        {
            return CompileVirtualDelete(del, delVirtual);
        }

        PreassignParameters();
        TableInfo t = TableForWrite(del.Target.Name);
        BindTarget(del.Target);
        Emit(Op.Transaction, 0, 0, 0, null, 1 | 8);
        int keyBase = EmitKeyCollection(t, del.Where);
        WriteCtx w = OpenWriteCtx(t);
        int loop = NewLabel();
        int done = NewLabel();
        Mark(loop);
        int keyCount = t.WithoutRowid ? t.PrimaryKey!.Columns.Length : 0;
        Emit(Op.RowSetRead, keyBase, done, keyCount);
        if (t.WithoutRowid)
        {
            Emit(Op.IdxProbe, w.Cursor, loop, keyBase, null, keyCount);
        }
        else
        {
            Emit(Op.SeekRowid, w.Cursor, loop, keyBase);
        }

        int retBase = 0;
        int retCols = 0;
        string[] retNames = [];
        if (del.Returning is not null)
        {
            SetOldRowMode(w);
            EmitReturningRow(w, del.Returning, out retBase, out retCols, out retNames);
        }

        bool tBefore = HasTrigger(t, TriggerTime.Before, TriggerEvent.Delete);
        bool tAfter = HasTrigger(t, TriggerTime.After, TriggerEvent.Delete);
        int oldImg = 0;
        if (tBefore || tAfter)
        {
            oldImg = CaptureOldRow(w);
        }

        if (tBefore)
        {
            EmitTriggers(t, TriggerTime.Before, TriggerEvent.Delete, oldImg, 0, null, loop);
            // the trigger may have removed the row
            if (t.WithoutRowid)
            {
                Emit(Op.IdxProbe, w.Cursor, loop, keyBase, null, keyCount);
            }
            else
            {
                Emit(Op.SeekRowid, w.Cursor, loop, keyBase);
            }
        }

        // lang_delete, "The Truncate Optimization": a DELETE with no WHERE on a table with no triggers, foreign keys or RETURNING empties it without
        // visiting rows, so the update hook is not called for the rows.
        _truncateDelete = del.Where is null && del.Returning is null && !tBefore && !tAfter && !FkInvolved(t);
        EmitDeleteRow(w, true);
        _truncateDelete = false;
        if (tAfter)
        {
            EmitTriggers(t, TriggerTime.After, TriggerEvent.Delete, oldImg, 0, null, loop);
        }

        Emit(Op.Goto, 0, loop);
        Mark(done);
        return EmitReturningLoop(retBase, retCols, retNames);
    }
}
