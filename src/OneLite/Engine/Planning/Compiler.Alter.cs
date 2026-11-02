using System.Collections.Immutable;
using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Sql.Parsing;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite.Planning;

// ALTER TABLE (lang_altertable): RENAME TO, RENAME COLUMN, ADD COLUMN and DROP COLUMN. Each is a rewrite of rows of sqlite_schema (found by
// rowid, taken from the catalog this program was compiled against; a changed schema cookie makes the statement compile again before it runs)
// plus, for DROP COLUMN, a rewrite of every row of the table.
internal sealed partial class Compiler
{
    private sealed record SchemaEdit(SchemaRow Row, string Name, string Table, string? Sql);

    internal VmProgram CompileAlter(AlterTableStatement a)
    {
        PreassignParameters();
        string? schema = a.Table.Schema?.Text;
        string tn = a.Table.Name.Text;
        _ = NamedDb(schema, "table", tn);
        TableInfo? t = _cat.FindIn(schema, tn);
        if (t is null)
        {
            throw Err("no such table: " + (schema is null ? tn : schema + "." + tn));
        }

        _forceTemp = t.Db == 1;

        if (t.View is not null)
        {
            throw Err(a is AddColumnStatement ? "Cannot add a column to a view" : "view " + t.Name + " may not be altered");
        }

        if (tn.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
        {
            throw Err("table " + tn + " may not be altered");
        }

        if (t.Unsupported is not null)
        {
            throw Unsupported(t.Unsupported);
        }

        if (a is not AddColumnStatement)
        {
            ValidateViewsAndTriggers();
        }

        return a switch
        {
            RenameTableStatement rt => CompileRenameTable(t, rt),
            RenameColumnStatement rc => CompileRenameColumn(t, rc),
            AddColumnStatement ac => CompileAddColumn(t, ac),
            DropColumnStatement dc => CompileDropColumn(t, dc),
            _ => throw Unsupported("ALTER TABLE " + a.GetType().Name.Replace("Statement", "", StringComparison.Ordinal)),
        };
    }

    /// <summary>
    /// lang_altertable: RENAME and DROP COLUMN re-parse every view and trigger of the schema, so one that is already broken (a missing table
    /// or column) fails the statement with "error in view v: ..." even when it has nothing to do with the table being changed.
    /// </summary>
    private void ValidateViewsAndTriggers()
    {
        foreach (TableInfo v in _cat.Tables.Values.Where(x => x.View is not null && x.Db == 0).ToList())
        {
            try
            {
                var c = new Compiler(_cat, "SELECT * FROM main." + Q(v.Name), _enc);
                _ = c.CompileSelect((SelectStatement)SqlParser.ParseOne("SELECT * FROM main." + Q(v.Name), 0, out _)!);
            }
            catch (OneLiteException e) when (e.Code == ResultCode.Error)
            {
                throw Err("error in view " + v.Name + ": " + QualifyMissing(e.Message));
            }
        }

        foreach (TriggerInfo ti in _cat.Triggers.Values.Where(x => x.Db == 0 && x.Time != TriggerTime.InsteadOf).ToList())
        {
            TableInfo? tt = _cat.Find(ti.Table);
            if (tt is null)
            {
                continue;
            }

            var spec = new TrigSpec { Info = ti, Table = tt, Cat = _cat, Enc = _enc, ForeignKeys = ForeignKeysEnabled };
            try
            {
                for (int i = ti.Ast.When is null ? 0 : -1; i < spec.BodyCount; i++)
                {
                    _ = spec.Get(i);
                }
            }
            catch (OneLiteException e) when (e.Code == ResultCode.Error)
            {
                throw Err("error in trigger " + ti.Name + ": " + QualifyMissing(e.Message));
            }
        }
    }

    private static string QualifyMissing(string message)
        => message.StartsWith("no such table: ", StringComparison.Ordinal) && !message.Contains('.', StringComparison.Ordinal)
            ? "no such table: main." + message["no such table: ".Length..]
            : message;

    private SchemaRow TableRow(TableInfo t) => _cat.RowsOf(t.Db).First(r => r.Type == "table" && AsciiComparer.Instance.Equals(r.Name, t.Name));

    private VmProgram EmitSchemaEdits(int db, List<SchemaEdit> edits, Action? extra = null) => EmitSchemaEdits(db, edits, [], extra);

    private VmProgram EmitSchemaEdits(int db, List<SchemaEdit> edits, List<SchemaEdit> tempEdits, Action? extra)
    {
        Emit(Op.Transaction, 0, 0, 0, null, 1);
        EmitEdits(db, edits);
        if (tempEdits.Count > 0)
        {
            _forceTemp = true;
            EmitEdits(1, tempEdits);
            Emit(Op.BumpCookie, 1);
        }

        extra?.Invoke();
        Emit(Op.BumpCookie, db);
        Emit(Op.Halt);
        return DdlProgram();
    }

    private void EmitEdits(int db, List<SchemaEdit> edits)
    {
        TableInfo master = _cat.MasterOf(db);
        int mc = AddCursor(new CursorSpec { Table = master, Root = 1 });
        Emit(Op.OpenWrite, mc);
        foreach (SchemaEdit e in edits)
        {
            int rid = NewReg();
            int b = NewRegs(5);
            int skip = NewLabel();
            Emit(Op.Integer, 0, rid, 0, e.Row.Rowid);
            Emit(Op.SeekRowid, mc, skip, rid);
            Emit(Op.String8, 0, b, 0, Utf8(e.Row.Type));
            Emit(Op.String8, 0, b + 1, 0, Utf8(e.Name));
            Emit(Op.String8, 0, b + 2, 0, Utf8(e.Table));
            Emit(Op.Integer, 0, b + 3, 0, (long)e.Row.Root);
            if (e.Sql is null)
            {
                Emit(Op.Null, 0, b + 4);
            }
            else
            {
                Emit(Op.String8, 0, b + 4, 0, Utf8(e.Sql));
            }

            Emit(Op.Delete, mc, 0, 0, null, 0);
            Emit(Op.Insert, mc, b, rid, master, 0);
            Mark(skip);
        }
    }

    private static void CheckParses(string name, string sql)
    {
        try
        {
            _ = SqlParser.ParseOne(sql, 0, out _);
        }
        catch (OneLiteException e)
        {
            throw Err("error in " + name + " after rename: " + e.Message);
        }
    }

    // ------------------------------------------------------------------ RENAME TO

    private VmProgram CompileRenameTable(TableInfo t, RenameTableStatement rt)
    {
        string old = t.Name;
        string nw = rt.NewName.Text;
        if (nw.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
        {
            throw Err("object name reserved for internal use: " + nw);
        }

        if (NameInUse(nw, t.Db) && !AsciiComparer.Instance.Equals(nw, old))
        {
            throw Err("there is already another table or index with this name: " + nw);
        }

        var edits = new List<SchemaEdit>();
        var tempEdits = new List<SchemaEdit>();

        // A main table's temp triggers are rewritten too (they live in the temp schema); a temp table of the same name hides the main one from them.
        IEnumerable<SchemaRow> rows = _cat.RowsOf(t.Db);
        if (t.Db == 0 && !_cat.TempTables.ContainsKey(old))
        {
            rows = rows.Concat(_cat.TempRows.Where(x => x.Type == "trigger"));
        }

        foreach (SchemaRow r in rows)
        {
            List<SchemaEdit> target = r.Db == 1 && t.Db == 0 ? tempEdits : edits;
            bool own = AsciiComparer.Instance.Equals(r.Table, old);
            string? sql = r.Sql;
            string name = r.Name;
            string tbl = r.Table;
            if (sql is not null)
            {
                sql = SqlRewrite.RenameTable(sql, old, nw);
            }

            if (own)
            {
                tbl = nw;
                if (r.Type == "table")
                {
                    name = nw;
                }
                else if (r.Type == "index" && name.StartsWith("sqlite_autoindex_" + old + "_", StringComparison.OrdinalIgnoreCase))
                {
                    name = "sqlite_autoindex_" + nw + name[("sqlite_autoindex_".Length + old.Length)..];
                }
            }

            if (sql != r.Sql || name != r.Name || tbl != r.Table)
            {
                if (sql is not null)
                {
                    CheckParses(r.Name, sql);
                }

                target.Add(new SchemaEdit(r, name, tbl, sql));
            }
        }

        return EmitSchemaEdits(t.Db, edits, tempEdits, () =>
        {
            if (t.Autoincrement && _cat.FindIn(_cat.SchemaName(t.Db), "sqlite_sequence") is { } st)
            {
                int sc = AddCursor(new CursorSpec { Table = st, Root = st.Root });
                Emit(Op.OpenWrite, sc);
                int sb = NewRegs(3);
                Emit(Op.SeqLoad, sb, sc, 0, old);
                int skip = NewLabel();
                Emit(Op.IfNull, sb + 1, skip);
                Emit(Op.SeekRowid, sc, skip, sb + 1);
                Emit(Op.Delete, sc);
                int rb = NewRegs(2);
                Emit(Op.String8, 0, rb, 0, Utf8(nw));
                Emit(Op.Copy, sb + 2, rb + 1);
                Emit(Op.Insert, sc, rb, sb + 1, st, 0);
                Mark(skip);
            }
        });
    }

    // ------------------------------------------------------------------ RENAME COLUMN

    private VmProgram CompileRenameColumn(TableInfo t, RenameColumnStatement rc)
    {
        int ci = t.FindColumn(rc.OldName.Text);
        if (ci < 0)
        {
            throw Err("no such column: \"" + rc.OldName.Text + "\"");
        }

        string oldCol = t.Columns[ci].Name;
        string nw = rc.NewName.Text;
        if (t.FindColumn(nw) >= 0 && !AsciiComparer.Instance.Equals(nw, oldCol))
        {
            throw Err("error in table " + t.Name + " after rename: duplicate column name: " + nw);
        }

        string rawNew = _sql[rc.NewName.Span.Start..rc.NewName.Span.End];
        string newText = rawNew.Length > 0 && rawNew[0] is '"' or '\'' or '`' or '[' ? SqlRewrite.Quote(nw) : rawNew;
        var edits = new List<SchemaEdit>();
        foreach (SchemaRow r in _cat.RowsOf(t.Db))
        {
            if (r.Sql is null)
            {
                continue;
            }

            bool own = AsciiComparer.Instance.Equals(r.Table, t.Name) && r.Type is "table" or "index";
            bool onTable = AsciiComparer.Instance.Equals(r.Table, t.Name);
            bool bare = own;
            if (!own && r.Type is "view" or "trigger")
            {
                // an unqualified name is this table's column when no other table the text mentions has a column of that name
                List<Token> toks = SqlRewrite.Lex(r.Sql);
                bare = onTable && r.Type == "trigger";
                if (!bare && SqlRewrite.RenameTable(r.Sql, t.Name, "x") != r.Sql)
                {
                    bare = !_cat.Tables.Values.Any(o => !ReferenceEquals(o, t) && o.View is null && o.FindColumn(oldCol) >= 0
                        && toks.Any(k => k.Kind == TokenKind.Id && AsciiComparer.Instance.Equals(SqlRewrite.Unquote(r.Sql[k.Start..k.End]), o.Name)));
                }
            }

            string sql = SqlRewrite.RenameColumn(r.Sql, t.Name, oldCol, nw, own, onTable && r.Type == "trigger", bare, newText);
            if (sql != r.Sql)
            {
                CheckParses(r.Name, sql);
                edits.Add(new SchemaEdit(r, r.Name, r.Table, sql));
            }
        }

        return EmitSchemaEdits(t.Db, edits);
    }

    // ------------------------------------------------------------------ ADD COLUMN

    private VmProgram CompileAddColumn(TableInfo t, AddColumnStatement ac)
    {
        ColumnDefinition cd = ac.Column;
        if (t.FindColumn(cd.Name.Text) >= 0)
        {
            throw Err("duplicate column name: " + cd.Name.Text);
        }

        // These are refused only when the table has rows (observed: the pinned shell adds such a column to an empty table).
        var guards = new List<string>();
        bool notNull = false;
        bool hasDefault = false;
        bool nullDefault = false;
        Expr? dflt = null;
        foreach (ColumnConstraint k in cd.Constraints)
        {
            switch (k)
            {
                case PrimaryKeyColumnConstraint:
                    throw Err("Cannot add a PRIMARY KEY column");
                case UniqueColumnConstraint:
                    throw Err("Cannot add a UNIQUE column");
                case NotNullConstraint:
                    notNull = true;
                    break;
                case DefaultConstraint dc:
                    hasDefault = true;
                    dflt = dc.Value;
                    nullDefault = dc.Value is LiteralExpr { Kind: LiteralKind.Null };
                    break;
                case GeneratedConstraint g when string.Equals(g.StorageWord, "STORED", StringComparison.OrdinalIgnoreCase):
                    guards.Add("cannot add a STORED column");
                    break;
                default:
                    break;
            }
        }

        if (dflt is not null && !IsConstantDefault(dflt))
        {
            // lang_altertable: only a table that has rows refuses it
            guards.Add("Cannot add a column with non-constant default");
        }

        if (ForeignKeysEnabled && cd.Constraints.OfType<ReferencesConstraint>().Any() && hasDefault && !nullDefault)
        {
            guards.Add("Cannot add a REFERENCES column with non-NULL default value");
        }

        if (notNull && (!hasDefault || nullDefault))
        {
            guards.Add("Cannot add a NOT NULL column with default value NULL");
        }

        SchemaRow row = TableRow(t);
        string sql = row.Sql!;
        List<Token> toks = SqlRewrite.Lex(sql);
        int open = toks.FindIndex(x => x.Kind == TokenKind.LParen);
        int depth = 0;
        int close = -1;
        for (int i = open; i >= 0 && i < toks.Count; i++)
        {
            if (toks[i].Kind == TokenKind.LParen)
            {
                depth++;
            }
            else if (toks[i].Kind == TokenKind.RParen && --depth == 0)
            {
                close = i;
                break;
            }
        }

        if (close < 0)
        {
            throw Err("malformed database schema (" + t.Name + ")");
        }

        string def = _sql[cd.Span.Start..cd.Span.End];
        string nsql = sql[..toks[close].Start] + ", " + def + sql[toks[close].Start..];
        CheckParses(t.Name, nsql);
        if (SqlParser.ParseOne(nsql, 0, out _) is CreateTableStatement cts)
        {
            // the new definition has to build (STRICT types, collations, ...)
            _ = SchemaBuilder.BuildTable(cts, row.Root, nsql);
        }

        var edits = new List<SchemaEdit> { new(row, row.Name, row.Table, nsql) };
        return EmitSchemaEdits(t.Db, edits, () =>
        {
            foreach (string g in guards)
            {
                int tc = AddCursor(new CursorSpec { Table = t, Index = t.WithoutRowid ? t.PrimaryKey : null, IsIndexTree = t.WithoutRowid, Root = t.Root });
                Emit(Op.OpenRead, tc);
                int ok = NewLabel();
                Emit(Op.Rewind, tc, ok);
                Emit(Op.Raise, 1, 0, 0, g, (int)ErrorAction.Abort);
                Mark(ok);
            }
        });
    }

    private static bool IsConstantDefault(Expr e) => e switch
    {
        LiteralExpr => true,
        UnaryExpr { Op: UnaryOp.Plus or UnaryOp.Minus } u => IsConstantDefault(u.Operand),
        ParenExpr p => IsConstantDefault(p.Inner),
        CastExpr c => IsConstantDefault(c.Operand),
        ColumnRef { Table: null, Schema: null } c => c.Column.Text.Equals("true", StringComparison.OrdinalIgnoreCase) || c.Column.Text.Equals("false", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    // ------------------------------------------------------------------ DROP COLUMN

    private VmProgram CompileDropColumn(TableInfo t, DropColumnStatement dc)
    {
        int ci = t.FindColumn(dc.Column.Text);
        if (ci < 0)
        {
            throw Err("no such column: \"" + dc.Column.Text + "\"");
        }

        ColumnInfo col = t.Columns[ci];
        if (col.PrimaryKey || (t.WithoutRowid && t.PrimaryKey!.Columns.Contains(ci)))
        {
            throw Err("cannot drop PRIMARY KEY column: \"" + col.Name + "\"");
        }

        if (t.AutoIndexes.Any(ix => ix.Columns.Contains(ci)))
        {
            throw Err("cannot drop UNIQUE column: \"" + col.Name + "\"");
        }

        if (t.Columns.Length == 1)
        {
            throw Err("cannot drop column \"" + col.Name + "\": no other columns exist");
        }

        foreach (IndexInfo ix in t.Indexes)
        {
            if (ix.Columns.Contains(ci) || ix.Terms.Any(x => x is not null && ExprMentions(x, col.Name)) || (ix.Where is not null && ExprMentions(ix.Where, col.Name)))
            {
                throw Err("error in index " + ix.Name + " after drop column: no such column: " + col.Name);
            }
        }

        // the definition text without the column
        SchemaRow row = TableRow(t);
        string sql = row.Sql!;
        if (SqlParser.ParseOne(sql, 0, out _) is not CreateTableStatement cts)
        {
            throw Err("malformed database schema (" + t.Name + ")");
        }

        ColumnDefinition def = cts.Columns[ci];
        List<Token> toks = SqlRewrite.Lex(sql);
        int start = def.Span.Start;
        int end = def.Span.End;
        // take the comma that separated it from its neighbour
        Token? after = toks.Where(x => x.Start >= end).Cast<Token?>().FirstOrDefault();
        Token? before = toks.Where(x => x.End <= start).Cast<Token?>().LastOrDefault();
        if (after is { Kind: TokenKind.Comma } comma)
        {
            end = comma.End;
            while (end < sql.Length && char.IsWhiteSpace(sql[end]))
            {
                end++;
            }
        }
        else if (before is { Kind: TokenKind.Comma } bc)
        {
            start = bc.Start;
        }

        string nsql = sql[..start] + sql[end..];
        CheckParses(t.Name, nsql);
        if (SqlParser.ParseOne(nsql, 0, out _) is not CreateTableStatement ncts)
        {
            throw Err("malformed database schema (" + t.Name + ")");
        }

        TableInfo nt = SchemaBuilder.BuildTable(ncts, t.Root, nsql);
        // every other object has to still work without the column: names that no longer resolve are an error
        foreach (SchemaRow r in _cat.RowsOf(t.Db))
        {
            if (r.Sql is null || r.Type == "table" && r == row || r.Type == "index")
            {
                continue;
            }

            if (r.Type is "view" or "trigger" && SqlRewrite.RenameTable(r.Sql, t.Name, "x") != r.Sql)
            {
                var hits = new List<(int Start, int End)>();
                _ = SqlRewrite.RenameColumn(r.Sql, t.Name, col.Name, "x", false, r.Type == "trigger" && AsciiComparer.Instance.Equals(r.Table, t.Name), true, null, hits);
                if (r.Type == "trigger" && System.Text.RegularExpressions.Regex.Match(r.Sql, @"\bUPDATE\s+OF\b.*?\bON\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline) is { Success: true } uo)
                {
                    // the column list of UPDATE OF is not checked
                    hits.RemoveAll(h => h.Start >= uo.Index && h.End <= uo.Index + uo.Length);
                }

                if (hits.Count > 0)
                {
                    string written = r.Sql[hits[0].Start..hits[0].End].Replace("\"", "", StringComparison.Ordinal);
                    // lang_altertable: the message shows the use as written
                    throw Err("error in " + r.Type + " " + r.Name + " after drop column: no such column: " + written);
                }
            }
        }

        var edits = new List<SchemaEdit> { new(row, row.Name, row.Table, nsql) };
        return EmitSchemaEdits(t.Db, edits, () =>
        {
            // every row loses the column's value
            if (t.WithoutRowid)
            {
                // The rows are the entries of the primary key's B-tree: each is rewritten without the column's field (the key columns come first and stay).
                int wc = AddCursor(new CursorSpec { Table = t, Index = t.PrimaryKey, IsIndexTree = true, Root = t.Root });
                Emit(Op.OpenWrite, wc);
                int wend = NewLabel();
                int wloop = NewLabel();
                int wb = NewRegs(Math.Max(nt.RecordWidth, 1));
                Emit(Op.Rewind, wc, wend);
                Mark(wloop);
                for (int i = 0; i < t.Columns.Length; i++)
                {
                    if (i == ci || t.Columns[i].RecordIndex < 0)
                    {
                        continue;
                    }

                    int ni = nt.FindColumn(t.Columns[i].Name);
                    Emit(Op.Column, wc, t.Columns[i].RecordIndex, wb + nt.Columns[ni].RecordIndex, t.Columns[i].Default is not null ? DefaultValue(t.Columns[i]) : null, t.Columns[i].Affinity == Affinity.Real ? 1 : 0);
                }

                Emit(Op.Delete, wc);
                Emit(Op.IdxInsert, wc, wb, nt.RecordWidth, null, 0);
                Emit(Op.Next, wc, wloop);
                Mark(wend);
                return;
            }

            int tc = AddCursor(new CursorSpec { Table = t, Root = t.Root });
            Emit(Op.OpenWrite, tc);
            int end2 = NewLabel();
            int loop = NewLabel();
            int width = Math.Max(nt.RecordWidth, 1);
            int rb = NewRegs(width);
            int rid = NewReg();
            Emit(Op.Rewind, tc, end2);
            Mark(loop);
            Emit(Op.Rowid, tc, rid);
            for (int i = 0; i < t.Columns.Length; i++)
            {
                if (i == ci || t.Columns[i].RecordIndex < 0)
                {
                    continue;
                }

                int ni = nt.FindColumn(t.Columns[i].Name);
                if (nt.Columns[ni].RecordIndex < 0)
                {
                    continue;
                }

                if (i == t.RowidAlias)
                {
                    Emit(Op.Null, 0, rb + nt.Columns[ni].RecordIndex);
                }
                else
                {
                    // a row written before ADD COLUMN has no value for the column: it gets the column's default now
                    Emit(Op.Column, tc, t.Columns[i].RecordIndex, rb + nt.Columns[ni].RecordIndex, t.Columns[i].Default is not null ? DefaultValue(t.Columns[i]) : null, t.Columns[i].Affinity == Affinity.Real ? 1 : 0);
                }
            }

            Emit(Op.Insert, tc, rb, rid, nt, 0);
            Emit(Op.Next, tc, loop);
            Mark(end2);
        });
    }

    private static bool ExprMentions(Expr e, string column)
        => SqlWriter.Write(e).Contains(SqlRewrite.Quote(column), StringComparison.OrdinalIgnoreCase)
            || SqlWriter.Write(e).Contains(column, StringComparison.OrdinalIgnoreCase);
}
