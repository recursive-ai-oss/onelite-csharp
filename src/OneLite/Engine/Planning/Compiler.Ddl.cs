using System.Collections.Immutable;
using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite.Planning;

// CREATE TABLE, CREATE INDEX, DROP TABLE and DROP INDEX (lang_createtable, lang_createindex, lang_droptable, lang_dropindex): rows of
// sqlite_schema, new or freed trees, and a new schema cookie so that every prepared statement is compiled again (fileformat2 §2.6).
internal sealed partial class Compiler
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    // Set when the statement writes the temp database.
    private bool _forceTemp;

    private IEnumerable<TableInfo> TablesOf(int db) => _cat.DictOf(db).Values.Distinct();

    // The database a CREATE puts its object in: TEMP, or the schema named in front of the object (lang_naming).
    private int CreateDb(string? schema, bool temp, SqlNode? at = null)
    {
        if (temp)
        {
            if (schema is not null && !AsciiComparer.Instance.Equals(schema, "temp"))
            {
                throw Err("temporary table name must be unqualified");
            }

            return 1;
        }

        if (schema is null || AsciiComparer.Instance.Equals(schema, "main"))
        {
            return 0;
        }

        int slot = _cat.SlotOf(schema);
        return slot >= 0 ? slot : throw (at is null ? Err("unknown database " + schema) : ErrAt("unknown database " + schema, at));
    }

    // The database named by a qualified object name in DROP/ALTER, or -1 if unqualified; an unknown name reads as "no such <kind>: schema.name".
    private int NamedDb(string? schema, string kind, string name)
    {
        if (schema is null)
        {
            return -1;
        }

        if (AsciiComparer.Instance.Equals(schema, "main"))
        {
            return 0;
        }

        int slot = _cat.SlotOf(schema);
        return slot >= 0 ? slot : throw Err("no such " + kind + ": " + schema + "." + name);
    }

    private bool NameInUse(string name, int db)
    {
        if (_cat.DictOf(db).ContainsKey(name))
        {
            return true;
        }

        foreach (TableInfo t in TablesOf(db))
        {
            foreach (IndexInfo ix in t.Indexes)
            {
                if (AsciiComparer.Instance.Equals(ix.Name, name))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // db -1: either database, temp first.
    private IndexInfo? FindIndex(string name, out TableInfo? owner, int db = -1)
    {
        var order = new List<int> { 1, 0 };
        order.AddRange(_cat.Attached.Select(a => a.Slot));
        foreach (int d in order)
        {
            if (db >= 0 && d != db)
            {
                continue;
            }

            foreach (TableInfo t in TablesOf(d))
            {
                foreach (IndexInfo ix in t.Indexes)
                {
                    if (AsciiComparer.Instance.Equals(ix.Name, name))
                    {
                        owner = t;
                        return ix;
                    }
                }
            }
        }

        owner = null;
        return null;
    }

    private string SchemaText(string prefix, CreateStatement cs)
    {
        int start = cs.SchemaSqlStart;
        int end = Math.Min(cs.Span.End, _sql.Length);
        string body = _sql[start..end].TrimEnd();
        if (body.EndsWith(';'))
        {
            body = body[..^1].TrimEnd();
        }

        // The shell keeps what follows the last token up to the terminating ';' (or the end of the text) in the stored text of an index, whitespace
        // and comments included, and in a view's, whitespace at the very end excepted. Other objects end at their last token.
        if (cs is CreateIndexStatement or CreateViewStatement)
        {
            int i = end;
            if (i > 0 && _sql[i - 1] == ';')
            {
                i--;
            }

            int j = i;
            while (j < _sql.Length)
            {
                char c = _sql[j];
                if (c is ' ' or '\t' or '\n' or '\r' or '\f')
                {
                    j++;
                }
                else if (c == '-' && j + 1 < _sql.Length && _sql[j + 1] == '-')
                {
                    while (j < _sql.Length && _sql[j] != '\n')
                    {
                        j++;
                    }
                }
                else if (c == '/' && j + 1 < _sql.Length && _sql[j + 1] == '*')
                {
                    int close = _sql.IndexOf("*/", j + 2, StringComparison.Ordinal);
                    j = close < 0 ? _sql.Length : close + 2;
                }
                else
                {
                    break;
                }
            }

            if (j >= _sql.Length || _sql[j] == ';')
            {
                string raw = _sql[start..j];
                body = cs is CreateViewStatement ? raw.TrimEnd() : raw;
            }
        }

        return prefix + body;
    }

    /// <summary>Emits the insertion of one sqlite_schema row; <paramref name="rootReg"/> is 0 for a row without a tree.</summary>
    private void EmitSchemaRow(int masterCursor, TableInfo master, string type, string name, string table, int rootReg, string? sql)
    {
        int b = NewRegs(5);
        int rid = NewReg();
        Emit(Op.String8, 0, b, 0, Utf8(type));
        Emit(Op.String8, 0, b + 1, 0, Utf8(name));
        Emit(Op.String8, 0, b + 2, 0, Utf8(table));
        if (rootReg != 0)
        {
            Emit(Op.Copy, rootReg, b + 3);
        }
        else
        {
            Emit(Op.Integer, 0, b + 3, 0, 0L);
        }

        if (sql is null)
        {
            Emit(Op.Null, 0, b + 4);
        }
        else
        {
            Emit(Op.String8, 0, b + 4, 0, Utf8(sql));
        }

        Emit(Op.NewRowid, masterCursor, rid, 0);
        Emit(Op.Insert, masterCursor, b, rid, master, 0);
    }

    private VmProgram DdlProgram() => new()
    {
        ForceTemp = _forceTemp,
        Code = Finish(),
        RegisterCount = _nReg + 1,
        Cursors = [.. _cursors],
        ParameterNames = [.. _params],
        Encoding = _enc,
        OnceCount = _onceCount,
        SorterCount = _sorterCount,
        SetCount = _setCount,
        Aggregates = [.. _aggSpecs],
    };

    internal VmProgram CompileCreate(CreateStatement cs)
    {
        switch (cs)
        {
            case CreateTableStatement ct:
                return CompileCreateTable(ct);
            case CreateIndexStatement ci:
                return CompileCreateIndex(ci);
            case CreateViewStatement cv:
                return CompileCreateView(cv);
            case CreateTriggerStatement ctr:
                return CompileCreateTrigger(ctr);
            case CreateVirtualTableStatement cv:
                return CompileCreateVirtual(cv);
            default:
                throw Unsupported("CREATE statement");
        }
    }

    private string AlreadyExists(string name, int db)
        => _cat.DictOf(db).GetValueOrDefault(name) is { } t ? (t.View is not null ? "view " : "table ") + name + " already exists" : "there is already an index named " + name;

    private VmProgram EmptyProgram()
    {
        Emit(Op.Halt);
        return new VmProgram { Code = Finish(), RegisterCount = 1, Encoding = _enc };
    }

    // lang_createview. The body is not checked here: a view over a missing table is created and fails when it is used.
    private VmProgram CompileCreateView(CreateViewStatement cv)
    {
        PreassignParameters();
        int db = CreateDb(cv.Name.Schema?.Text, cv.Temporary, cv.Name.Schema);
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

            throw AlreadyExists(name, db) is { } msg2 && (msg2.StartsWith("table ", StringComparison.Ordinal) || msg2.StartsWith("view ", StringComparison.Ordinal)) ? ErrAt(msg2, cv.Name.Name) : Err(AlreadyExists(name, db));
        }

        _forceTemp = db == 1;
        TableInfo master = _cat.MasterOf(db);
        Emit(Op.Transaction, 0, 0, 0, null, 1);
        int mc = AddCursor(new CursorSpec { Table = master, Root = 1 });
        Emit(Op.OpenWrite, mc);
        EmitSchemaRow(mc, master, "view", name, name, 0, SchemaText("CREATE VIEW ", cv));
        Emit(Op.BumpCookie, db);
        Emit(Op.Halt);
        return DdlProgram();
    }

    // lang_createtable: a parenthesized DEFAULT may use functions and operators but no column, parameter, subquery or window function.
    // The names TRUE and FALSE are the constants 1 and 0.
    private static bool IsConstantDefaultExpr(Expr e)
    {
        switch (e)
        {
            case ColumnRef { Schema: null, Table: null } c:
                return c.Column.Text.Equals("true", StringComparison.OrdinalIgnoreCase) || c.Column.Text.Equals("false", StringComparison.OrdinalIgnoreCase);
            case ColumnRef or VariableExpr or InSelectExpr or InTableExpr or ExistsExpr or SubqueryExpr or RaiseExpr or RowValueExpr:
                return false;
            case FunctionCall f:
                return f.Over is null && f.Args.All(IsConstantDefaultExpr);
            case ParenExpr p:
                return IsConstantDefaultExpr(p.Inner);
            case CastExpr ce:
                return IsConstantDefaultExpr(ce.Operand);
            case CollateExpr co:
                return IsConstantDefaultExpr(co.Operand);
            case InListExpr il:
                return IsConstantDefaultExpr(il.Operand) && il.Items.All(IsConstantDefaultExpr);
            case CaseExpr cs:
                return (cs.Operand is null || IsConstantDefaultExpr(cs.Operand)) && cs.Whens.All(w => IsConstantDefaultExpr(w.When) && IsConstantDefaultExpr(w.Then))
                    && (cs.Else is null || IsConstantDefaultExpr(cs.Else));
            default:
                return CollationChildren(e).All(IsConstantDefaultExpr);
        }
    }

    private VmProgram CompileCreateTable(CreateTableStatement ct)
    {
        PreassignParameters();
        if (ct.AsSelect is not null)
        {
            throw Unsupported("CREATE TABLE ... AS SELECT");
        }

        int db = CreateDb(ct.Name.Schema?.Text, ct.Temporary, ct.Name.Schema);
        string name = ct.Name.Name.Text;
        if (name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase) && !WritableSchema)
        {
            throw Err("object name reserved for internal use: " + name);
        }

        if (NameInUse(name, db))
        {
            if (ct.IfNotExists)
            {
                Emit(Op.Halt);
                return new VmProgram { Code = Finish(), RegisterCount = 1, Encoding = _enc };
            }

            throw AlreadyExists(name, db) is { } msg1 && (msg1.StartsWith("table ", StringComparison.Ordinal) || msg1.StartsWith("view ", StringComparison.Ordinal)) ? ErrAt(msg1, ct.Name.Name) : Err(AlreadyExists(name, db));
        }

        // Checks the reference makes while parsing the definition.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ColumnDefinition cd in ct.Columns)
        {
            if (!seen.Add(cd.Name.Text))
            {
                throw Err("duplicate column name: " + cd.Name.Text);
            }

            foreach (ColumnConstraint k in cd.Constraints)
            {
                if (k is CollateConstraint cc)
                {
                    ParseColl(cc.Collation.Text);
                }
                else if (k is DefaultConstraint { Value: ParenExpr dp } && !IsConstantDefaultExpr(dp))
                {
                    throw Err("default value of column [" + cd.Name.Text + "] is not constant");
                }
            }
        }

        if (ct.Strict)
        {
            // STRICT tables take exactly one of these type names, spelled as written in the message (lang_createtable "Strict Tables").
            foreach (ColumnDefinition cd in ct.Columns)
            {
                if (cd.Type is null || cd.Type.Words.IsDefaultOrEmpty)
                {
                    throw Err("missing datatype for " + name + "." + cd.Name.Text);
                }

                string written = string.Join(" ", cd.Type.Words) + (cd.Type.Sizes.IsDefaultOrEmpty ? string.Empty : "(" + string.Join(",", cd.Type.Sizes) + ")");
                if (cd.Type.Words.Length != 1 || !cd.Type.Sizes.IsDefaultOrEmpty
                    || !(written.Equals("INT", StringComparison.OrdinalIgnoreCase) || written.Equals("INTEGER", StringComparison.OrdinalIgnoreCase)
                        || written.Equals("REAL", StringComparison.OrdinalIgnoreCase) || written.Equals("TEXT", StringComparison.OrdinalIgnoreCase)
                        || written.Equals("BLOB", StringComparison.OrdinalIgnoreCase) || written.Equals("ANY", StringComparison.OrdinalIgnoreCase)))
                {
                    throw Err("unknown datatype for " + name + "." + cd.Name.Text + ": \"" + written + "\"");
                }
            }
        }

        int pkCount = ct.Constraints.Count(c => c is PrimaryKeyTableConstraint) + ct.Columns.Count(c => c.Constraints.Any(k => k is PrimaryKeyColumnConstraint));
        if (pkCount > 1)
        {
            throw Err("table \"" + name + "\" has more than one primary key");
        }

        if (ct.WithoutRowid && pkCount == 0)
        {
            throw Err("PRIMARY KEY missing on table " + name);
        }

        TableInfo built = SchemaBuilder.BuildTable(ct, 0, null);
        foreach (TableConstraint tc in ct.Constraints)
        {
            if (tc is PrimaryKeyTableConstraint { Columns: var cols })
            {
                foreach (OrderingTerm term in cols)
                {
                    Expr e = term.Expr is CollateExpr ce ? ce.Operand : term.Expr;
                    if (e is ColumnRef cr && built.FindColumn(cr.Column.Text) < 0)
                    {
                        throw Err("no such column: " + cr.Column.Text);
                    }
                }
            }
        }

        if ((ct.Columns.Any(c => c.Constraints.OfType<PrimaryKeyColumnConstraint>().Any(p => p.Autoincrement))
            || ct.Constraints.OfType<PrimaryKeyTableConstraint>().Any(p => p.Autoincrement)) && built.RowidAlias < 0)
        {
            throw Err("AUTOINCREMENT is only allowed on an INTEGER PRIMARY KEY");
        }

        _forceTemp = db == 1;
        TableInfo master = _cat.MasterOf(db);
        Emit(Op.Transaction, 0, 0, 0, null, 1);
        int mc = AddCursor(new CursorSpec { Table = master, Root = 1 });
        Emit(Op.OpenWrite, mc);

        int root = NewReg();
        Emit(Op.CreateBtree, root, built.WithoutRowid ? 0 : 1, db);
        string text = SchemaText("CREATE TABLE ", ct);
        EmitSchemaRow(mc, master, "table", name, name, root, text);
        for (int i = 0; i < built.AutoIndexes.Count; i++)
        {
            if (built.AutoIndexes[i].Columns.Length == 0)
            {
                continue;
            }

            int ir = NewReg();
            Emit(Op.CreateBtree, ir, 0, db);
            EmitSchemaRow(mc, master, "index", "sqlite_autoindex_" + name + "_" + (i + 1), name, ir, null);
        }

        if (built.Autoincrement && _cat.FindIn(_cat.SchemaName(db), "sqlite_sequence") is null)
        {
            // The table that remembers AUTOINCREMENT counters is created together with the first table that needs it.
            int sr = NewReg();
            Emit(Op.CreateBtree, sr, 1, db);
            EmitSchemaRow(mc, master, "table", "sqlite_sequence", "sqlite_sequence", sr, "CREATE TABLE sqlite_sequence(name,seq)");
        }

        Emit(Op.BumpCookie, db);
        Emit(Op.Halt);
        return DdlProgram();
    }

    /// <summary>Whether the SQL text of an expression calls a function whose result can change between calls (lang_createindex, partial indexes).</summary>
    private bool FindNonDeterministicCall(string expressionSql)
    {
        string text = System.Text.RegularExpressions.Regex.Replace(expressionSql, "'(?:[^']|'')*'", "''");
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"([A-Za-z_][A-Za-z0-9_]*)\s*\("))
        {
            if (_cat.Owner?.IsNonDeterministic(m.Groups[1].Value) == true)
            {
                return true;
            }
        }

        return false;
    }

    private VmProgram CompileCreateIndex(CreateIndexStatement ci)
    {
        PreassignParameters();
        string? schema = ci.Name.Schema?.Text;
        int named = schema is null ? -1 : CreateDb(schema, false);
        string name = ci.Name.Name.Text;
        TableInfo? t = named < 0 ? _cat.Find(ci.Table.Text) : _cat.FindIn(_cat.SchemaName(named), ci.Table.Text);
        if (t is null || t.Unsupported is not null || t.View is not null)
        {
            throw Err(t is null ? "no such table: " + (schema ?? "main") + "." + ci.Table.Text : "views may not be indexed");
        }

        int db = t.Db;

        if (ReferenceEquals(t, _cat.MasterOf(db)))
        {
            throw Err("table " + t.Name + " may not be indexed");
        }

        if (name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase) && !WritableSchema)
        {
            throw Err("object name reserved for internal use: " + name);
        }

        if (NameInUse(name, db))
        {
            if (ci.IfNotExists)
            {
                Emit(Op.Halt);
                return new VmProgram { Code = Finish(), RegisterCount = 1, Encoding = _enc };
            }

            throw Err(_cat.DictOf(db).GetValueOrDefault(name) is { } ex ? "there is already a " + (ex.View is not null ? "view" : "table") + " named " + name : "index " + name + " already exists");
        }

        IndexInfo idx = SchemaBuilder.BuildIndex(ci, 0, t);
        if (idx.Where is not null && FindNonDeterministicCall(SqlWriter.Write(idx.Where)))
        {
            throw Err("non-deterministic functions prohibited in partial index WHERE clauses");
        }

        _table = t;
        _alias = t.Name;
        SetColumns(true);
        // Resolve the key expressions now so that an unknown column fails here, with the reference's message.
        DryRun(() =>
        {
            _tableCursor = 0;
            for (int i = 0; i < idx.Columns.Length; i++)
            {
                int tmp = NewReg();
                if (idx.Terms[i] is { } term)
                {
                    CompileInto(term, tmp);
                }
            }

            if (idx.Where is not null)
            {
                CompileInto(idx.Where, NewReg());
            }
        });

        _forceTemp = db == 1;
        TableInfo master = _cat.MasterOf(db);
        Emit(Op.Transaction, 0, 0, 0, null, 1);
        int mc = AddCursor(new CursorSpec { Table = master, Root = 1 });
        Emit(Op.OpenWrite, mc);
        int root = NewReg();
        Emit(Op.CreateBtree, root, 0, db);

        EmitIndexBuild(t, idx, root);
        EmitSchemaRow(mc, master, "index", name, t.Name, root, SchemaText(ci.Unique ? "CREATE UNIQUE INDEX " : "CREATE INDEX ", ci));
        Emit(Op.BumpCookie, db);
        Emit(Op.Halt);
        return DdlProgram();
    }

    // Fills the (empty) index tree whose root is in register rootReg from the rows of its table.
    private void EmitIndexBuild(TableInfo t, IndexInfo idx, int root)
    {
        // The cursor on the new tree, and the one that reads the table.
        var ixSpec = new CursorSpec { Table = t, Index = idx, IsIndexTree = true, Root = 0 };
        int ic = AddCursor(ixSpec);
        Emit(Op.OpenWrite, ic, root);
        int tc = AddCursor(new CursorSpec { Table = t, Index = t.WithoutRowid ? t.PrimaryKey : null, IsIndexTree = t.WithoutRowid, Root = t.Root });
        Emit(Op.OpenRead, tc);
        var x = new IdxW { Info = idx, Cursor = ic };
        if (t.WithoutRowid)
        {
            var tail = new List<int>();
            foreach (int pc in t.PrimaryKey!.Columns)
            {
                if (Array.IndexOf(idx.Columns, pc) < 0)
                {
                    tail.Add(pc);
                }
            }

            x.Tail = [.. tail];
            x.KeyCount = idx.Columns.Length + tail.Count;
        }
        else
        {
            x.KeyCount = idx.Columns.Length + 1;
        }

        x.KeyBase = NewRegs(x.KeyCount);
        x.Cond = NewReg();
        var w = new WriteCtx { T = t, Cursor = tc };
        _tableCursor = tc;
        _colRegs = null;
        int end = NewLabel();
        Emit(Op.Rewind, tc, end);
        int loop = NewLabel();
        int next = NewLabel();
        Mark(loop);
        EmitIndexKey(w, x, x.KeyBase, x.Cond, 0);
        if (idx.Where is not null)
        {
            Emit(Op.IfNot, x.Cond, next, 1);
        }

        if (idx.Unique)
        {
            int ok = NewLabel();
            Emit(Op.IdxProbe, ic, ok, x.KeyBase, null, idx.Columns.Length | 0x100);
            Emit(Op.Raise, ConstraintCodes.Unique, 0, 0, UniqueMessage(t, idx), (int)ErrorAction.Abort);
            Mark(ok);
        }

        Emit(Op.IdxInsert, ic, x.KeyBase, x.KeyCount);
        Mark(next);
        Emit(Op.Next, tc, loop);
        Mark(end);
    }

    internal VmProgram CompileDrop(DropStatement ds)
    {
        PreassignParameters();
        string? schema = ds.Name.Schema?.Text;
        string name = ds.Name.Name.Text;
        string qualified = schema is null ? name : schema + "." + name;
        string kind = ds.Kind.ToString().ToLowerInvariant();
        int sdb = NamedDb(schema, kind, name);
        switch (ds.Kind)
        {
            case DropKind.Table:
            {
                TableInfo? t = sdb < 0 ? _cat.Find(name) : _cat.FindIn(_cat.SchemaName(sdb), name);
                if (t is null || ReferenceEquals(t, _cat.MainTables.GetValueOrDefault("sqlite_master")) || ReferenceEquals(t, _cat.TempMaster))
                {
                    if (ds.IfExists)
                    {
                        Emit(Op.Halt);
                        return new VmProgram { Code = Finish(), RegisterCount = 1, Encoding = _enc };
                    }

                    throw Err("no such table: " + qualified);
                }

                if (t.View is not null)
                {
                    throw Err("use DROP VIEW to delete view " + name);
                }

                if (t.Unsupported is not null)
                {
                    throw Unsupported(t.Unsupported);
                }

                if (name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase) && !WritableSchema)
                {
                    throw Err("table " + name + " may not be dropped");
                }

                _forceTemp = t.Db == 1;
                TableInfo master = _cat.MasterOf(t.Db);
                Emit(Op.Transaction, 0, 0, 0, null, 1);
                if (t.Virtual is not null)
                {
                    int vmc = AddCursor(new CursorSpec { Table = master, Root = 1 });
                    Emit(Op.OpenWrite, vmc);
                    EmitDropVirtual(t, vmc);
                    Emit(Op.BumpCookie, t.Db);
                    Emit(Op.Halt);
                    return DdlProgram();
                }

                if (ForeignKeysEnabled && _cat.Tables.Values.Any(o => !ReferenceEquals(o, t) && o.ForeignKeys.Any(f => AsciiComparer.Instance.Equals(f.Parent, t.Name))))
                {
                    // foreignkeys: DROP TABLE first does an implicit DELETE FROM the table, so ON DELETE actions run and an immediate violation
                    // fails the statement with the table still there (a deferred one fails at COMMIT).
                    Emit(Op.FkRun, NewReg(), 0, 0, MakeFkSpec("DELETE FROM " + (t.Db == 1 ? "temp." : "main.") + Q(t.Name), false), 0);
                    Emit(Op.FkCheck);
                }

                int mc = AddCursor(new CursorSpec { Table = master, Root = 1 });
                Emit(Op.OpenWrite, mc);
                Emit(Op.SchemaDelete, mc, 0, t.Db == 1 ? 5 : 0, name);
                if (t.Autoincrement && _cat.FindIn(_cat.SchemaName(t.Db), "sqlite_sequence") is { } st)
                {
                    int sc = AddCursor(new CursorSpec { Table = st, Root = st.Root });
                    Emit(Op.OpenWrite, sc);
                    int sb = NewRegs(3);
                    Emit(Op.SeqLoad, sb, sc, 0, name);
                    int skip = NewLabel();
                    Emit(Op.IfNull, sb + 1, skip);
                    Emit(Op.SeekRowid, sc, skip, sb + 1);
                    Emit(Op.Delete, sc);
                    Mark(skip);
                }

                var roots = new List<(uint Root, bool IsIndex)> { (t.Root, t.WithoutRowid) };
                foreach (IndexInfo ix in t.Indexes)
                {
                    if (ix.Root != 0)
                    {
                        roots.Add((ix.Root, true));
                    }
                }

                EmitDestroyAll(mc, roots);
                if (t.Db == 0 && _cat.TempRows.Any(r => r.Type == "trigger" && AsciiComparer.Instance.Equals(r.Table, name)))
                {
                    // Temp triggers on this table go with it.
                    _forceTemp = true;
                    int tc = AddCursor(new CursorSpec { Table = _cat.TempMaster, Root = 1 });
                    Emit(Op.OpenWrite, tc);
                    Emit(Op.SchemaDelete, tc, 0, 4, name);
                    Emit(Op.BumpCookie, 1);
                }

                Emit(Op.BumpCookie, t.Db);
                Emit(Op.Halt);
                return DdlProgram();
            }

            case DropKind.Index:
            {
                IndexInfo? ix = FindIndex(name, out TableInfo? owner, sdb);
                if (ix is null)
                {
                    if (ds.IfExists)
                    {
                        Emit(Op.Halt);
                        return new VmProgram { Code = Finish(), RegisterCount = 1, Encoding = _enc };
                    }

                    throw Err("no such index: " + qualified);
                }

                if (name.StartsWith("sqlite_autoindex_", StringComparison.OrdinalIgnoreCase))
                {
                    throw Err("index associated with UNIQUE or PRIMARY KEY constraint cannot be dropped");
                }

                _forceTemp = owner!.Db == 1;
                Emit(Op.Transaction, 0, 0, 0, null, 1);
                int mc = AddCursor(new CursorSpec { Table = _cat.MasterOf(owner.Db), Root = 1 });
                Emit(Op.OpenWrite, mc);
                Emit(Op.SchemaDelete, mc, 0, 1, name);
                EmitDestroyAll(mc, [(ix.Root, true)]);
                Emit(Op.BumpCookie, owner.Db);
                Emit(Op.Halt);
                return DdlProgram();
            }

            case DropKind.View:
            {
                TableInfo? v = sdb < 0 ? _cat.Find(name) : _cat.FindIn(_cat.SchemaName(sdb), name);
                if (v is null || v.View is null)
                {
                    if (v is not null)
                    {
                        throw Err("use DROP TABLE to delete table " + name);
                    }

                    if (ds.IfExists)
                    {
                        return EmptyProgram();
                    }

                    throw Err("no such view: " + qualified);
                }

                _forceTemp = v.Db == 1;
                Emit(Op.Transaction, 0, 0, 0, null, 1);
                int mc = AddCursor(new CursorSpec { Table = _cat.MasterOf(v.Db), Root = 1 });
                Emit(Op.OpenWrite, mc);
                // lang_dropview: the view's triggers (INSTEAD OF) go with it; their rows carry the view as tbl_name.
                Emit(Op.SchemaDelete, mc, 0, 0, name);
                if (v.Db == 0 && _cat.TempRows.Any(r => r.Type == "trigger" && AsciiComparer.Instance.Equals(r.Table, name)))
                {
                    _forceTemp = true;
                    int tc = AddCursor(new CursorSpec { Table = _cat.TempMaster, Root = 1 });
                    Emit(Op.OpenWrite, tc);
                    Emit(Op.SchemaDelete, tc, 0, 4, name);
                    Emit(Op.BumpCookie, 1);
                }

                Emit(Op.BumpCookie, v.Db);
                Emit(Op.Halt);
                return DdlProgram();
            }
            default:
                return CompileDropTrigger(ds);
        }
    }

    private void EmitDestroyAll(int masterCursor, List<(uint Root, bool IsIndex)> roots)
    {
        int b = NewRegs(roots.Count);
        for (int i = 0; i < roots.Count; i++)
        {
            Emit(Op.Integer, 0, b + i, 0, (long)roots[i].Root);
        }

        for (int i = 0; i < roots.Count; i++)
        {
            Emit(Op.Destroy, b + i, masterCursor, roots[i].IsIndex ? 1 : 0, null, roots.Count - 1 - i);
        }
    }
}
