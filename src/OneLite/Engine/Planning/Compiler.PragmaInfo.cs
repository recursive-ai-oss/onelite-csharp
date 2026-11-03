using System.Linq;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite.Planning;

// The introspection pragmas (pragma): table_info, table_xinfo, index_list, index_info, index_xinfo, table_list, database_list, collation_list,
// module_list, pragma_list and compile_options. Each reads the schema the connection has loaded and returns constant rows.
internal sealed partial class Compiler
{
    /// <summary>The file of the main database as PRAGMA database_list shows it (the full path, "" for a database with no file).</summary>
    internal string DatabaseFile { get; set; } = string.Empty;

    /// <summary>The connection has opened its temporary database (a statement named it), so database_list shows it.</summary>
    internal bool TempOpen { get; set; }

    /// <summary>The database is encrypted: SQLCipher names the page_size result column cipher_page_size.</summary>
    internal bool Encrypted { get; set; }

    private static readonly string[] s_pragmaNames =
    [
        "analysis_limit", "application_id", "auto_vacuum", "automatic_index", "busy_timeout", "cache_size", "cache_spill", "case_sensitive_like",
        "cell_size_check", "checkpoint_fullfsync", "collation_list", "compile_options", "count_changes", "data_version", "database_list",
        "default_cache_size", "defer_foreign_keys", "empty_result_callbacks", "encoding", "foreign_key_check", "foreign_key_list", "foreign_keys",
        "freelist_count", "full_column_names", "fullfsync", "function_list", "hard_heap_limit", "ignore_check_constraints", "incremental_vacuum",
        "index_info", "index_list", "index_xinfo", "integrity_check", "journal_mode", "journal_size_limit", "legacy_alter_table", "locking_mode",
        "max_page_count", "mmap_size", "module_list", "optimize", "page_count", "page_size", "pragma_list", "query_only", "quick_check",
        "read_uncommitted", "recursive_triggers", "reverse_unordered_selects", "schema_version", "secure_delete", "short_column_names",
        "shrink_memory", "soft_heap_limit", "synchronous", "table_info", "table_list", "table_xinfo", "temp_store", "temp_store_directory",
        "threads", "trusted_schema", "user_version", "wal_autocheckpoint", "wal_checkpoint", "writable_schema",
    ];

    private static readonly string[] s_moduleNames =
    [
        "generate_series", "bytecode", "dbstat", "tables_used", "sqlite_stmt", "sqlite_dbpage", "geopoly", "rtree_i32", "fsdir", "fts5vocab",
        "fts4aux", "fts3", "zipfile", "fts5", "fts4", "completion", "rtree", "fts3tokenize",
    ];

    private static bool IsTempName(Name? schema) => schema is not null && AsciiComparer.Instance.Equals(schema.Text, "temp");

    // Set by InfoRows: the rows an introspection pragma would return, taken instead of a program (the pragma_* table-valued functions).
    private static readonly int[] s_mainAndTemp = [0, 1];

    private bool _captureRows;
    private (string[] Cols, List<object?[]> Rows)? _captured;

    /// <summary>The rows of the introspection pragma <paramref name="name"/> for the pragma_* table-valued functions; null if there is no such pragma.</summary>
    internal (string[] Cols, List<object?[]> Rows)? InfoRows(string name, string? schema, string? arg)
    {
        if (schema is not null && _cat.SlotOf(schema) < 0)
        {
            throw Err("unknown database '" + schema + "'");
        }

        _captureRows = true;
        _captured = null;
        var ps = new PragmaStatement(schema is null ? null : new Name(schema), new Name(name), arg is null ? null : new PragmaValue(PragmaValueKind.String, arg), true);
        if (CompilePragmaInfo(ps, name) is null && _captured is null)
        {
            return null;
        }

        return _captured;
    }

    private VmProgram ConstRows(string[] cols, List<object?[]> rows)
    {
        if (_captureRows)
        {
            _captured = (cols, rows);
            return null!;
        }

        EmitPragmaTransaction(ReadFlags);
        int b = NewRegs(cols.Length);
        foreach (object?[] row in rows)
        {
            for (int i = 0; i < cols.Length; i++)
            {
                switch (row[i])
                {
                    case null:
                        Emit(Op.Null, 0, b + i);
                        break;
                    case string s:
                        EmitText(s, b + i);
                        break;
                    default:
                        Emit(Op.Integer, 0, b + i, 0, Convert.ToInt64(row[i], System.Globalization.CultureInfo.InvariantCulture));
                        break;
                }
            }

            Emit(Op.ResultRow, b, cols.Length);
        }

        Emit(Op.Halt);
        return PragmaRows(b, cols);
    }

    /// <summary>The program for an introspection pragma, or null when <paramref name="name"/> is not one.</summary>
    private VmProgram? CompilePragmaInfo(PragmaStatement ps, string name)
    {
        bool temp = IsTempName(ps.Schema);
        string? arg = ps.Value?.Text;
        switch (name)
        {
            case "table_info" or "table_xinfo":
            {
                bool x = name == "table_xinfo";
                string[] cols = x ? ["cid", "name", "type", "notnull", "dflt_value", "pk", "hidden"] : ["cid", "name", "type", "notnull", "dflt_value", "pk"];
                var rows = new List<object?[]>();
                TableInfo? t = arg is null ? null : _cat.FindIn(ps.Schema?.Text, arg);
                if (t is not null && t.View is not null)
                {
                    // The columns of a view are those of its SELECT, which is compiled (and its errors raised) the way a query on the view would.
                    // Nothing is kept of that code. A view's columns are never NOT NULL, defaulted or part of a key.
                    TableInfo shape = ViewShape(t, throwErrors: true)!;
                    foreach (ColumnInfo c in shape.Columns)
                    {
                        object?[] row = [rows.Count, c.Name, c.TypeText, 0, null, 0];
                        rows.Add(x ? [.. row, 0] : row);
                    }
                }
                else if (t is not null)
                {
                    // A virtual table's columns are what its module declares, so the module is connected first. Hidden columns are table_xinfo's.
                    EnsureVirtual(t);
                    int[] pkOrder = PragmaPkColumns(t);
                    for (int i = 0; i < t.Columns.Length; i++)
                    {
                        ColumnInfo c = t.Columns[i];
                        int hidden = c.Hidden ? 1 : c.Generated == GeneratedKind.Virtual ? 2 : c.Generated == GeneratedKind.Stored ? 3 : 0;
                        if (!x && hidden != 0)
                        {
                            continue;
                        }

                        int pk = Array.IndexOf(pkOrder, i) + 1;
                        bool notNull = c.NotNull || (pk > 0 && (t.WithoutRowid || (t.Strict && i != t.RowidAlias)));
                        string? typeText = c.Hidden && c.TypeText is { } tt && tt.EndsWith("HIDDEN", StringComparison.OrdinalIgnoreCase) ? tt[..^6].TrimEnd() : c.TypeText;
                        object?[] row = [rows.Count, c.Name, typeText, notNull ? 1 : 0, c.DefaultText, pk];
                        rows.Add(x ? [.. row, hidden] : row);
                    }
                }

                return ConstRows(cols, rows);
            }

            case "index_list":
            {
                var rows = new List<object?[]>();
                TableInfo? t = arg is null ? null : _cat.FindIn(ps.Schema?.Text, arg);
                if (t is not null)
                {
                    foreach (IndexInfo ix in ListedIndexes(t))
                    {
                        string origin = ix.IsPrimaryKey ? "pk" : ix.Name.StartsWith("sqlite_autoindex_", StringComparison.Ordinal) ? "u" : "c";
                        rows.Add([rows.Count, ix.Name, ix.Unique ? 1 : 0, origin, ix.Where is null ? 0 : 1]);
                    }
                }

                return ConstRows(["seq", "name", "unique", "origin", "partial"], rows);
            }

            case "index_info" or "index_xinfo":
            {
                bool x = name == "index_xinfo";
                var rows = new List<object?[]>();
                if (arg is not null && FindIndex(arg, ps.Schema?.Text) is { } found)
                {
                    (TableInfo t, IndexInfo ix) = found;
                    int n = 0;
                    for (int k = 0; k < ix.Columns.Length; k++)
                    {
                        // A key on a virtual generated column is kept as an expression, but the pragmas still name the column.
                        int cid = ix.Columns[k];
                        if (cid < 0 && k < ix.Terms.Length && ix.Terms[k] is ColumnRef { Table: null } cref)
                        {
                            cid = t.FindColumn(cref.Column.Text);
                        }

                        bool expr = cid < 0;
                        if (expr)
                        {
                            cid = -2;
                        }

                        string? cn = expr ? null : t.Columns[cid].Name;
                        string coll = (k < ix.Collations.Length ? ix.Collations[k] : null) ?? (expr ? null : t.Columns[cid].CollationName) ?? "BINARY";
                        rows.Add(x ? [n++, cid, cn, k < ix.Desc.Length && ix.Desc[k] ? 1 : 0, coll, 1] : [n++, cid, cn]);
                    }

                    if (x)
                    {
                        if (t.WithoutRowid)
                        {
                            // The rest of the row: the key columns not already in the index (for the primary key itself, every other column).
                            IEnumerable<int> rest = ix.IsPrimaryKey && ix.Name != "PRIMARY KEY" || ix == t.PrimaryKey
                                ? Enumerable.Range(0, t.Columns.Length).Where(c => Array.IndexOf(ix.Columns, c) < 0 && t.Columns[c].Generated != GeneratedKind.Virtual)
                                : PragmaPkColumns(t).Where(c => Array.IndexOf(ix.Columns, c) < 0);
                            foreach (int c in rest)
                            {
                                rows.Add([n++, c, t.Columns[c].Name, 0, t.Columns[c].CollationName ?? "BINARY", 0]);
                            }
                        }
                        else
                        {
                            rows.Add([n++, -1, null, 0, "BINARY", 0]);
                        }
                    }
                }

                return ConstRows(x ? ["seqno", "cid", "name", "desc", "coll", "key"] : ["seqno", "cid", "name"], rows);
            }

            case "database_list":
            {
                var rows = new List<object?[]> { new object?[] { 0, "main", DatabaseFile } };
                if (TempOpen || temp)
                {
                    rows.Add([1, "temp", string.Empty]);
                }

                foreach (AttachedSchema a in _cat.Attached)
                {
                    rows.Add([a.Slot, a.Name, a.Path]);
                }

                return ConstRows(["seq", "name", "file"], rows);
            }

            case "collation_list":
                return ConstRows(["seq", "name"], [[0, "BINARY"], [1, "NOCASE"], [2, "RTRIM"]]);
            case "module_list":
                return ConstRows(["name"], [.. s_moduleNames.Select(m => new object?[] { m })]);
            case "pragma_list":
                return ConstRows(["name"], [.. s_pragmaNames.Select(m => new object?[] { m })]);
            case "compile_options":
                return ConstRows(["compile_options"], [.. Scalar.CompileOptions.Select(m => new object?[] { m })]);
            case "table_list":
            {
                var rows = new List<object?[]>();
                int only = ps.Schema is null ? -1 : _cat.SlotOf(ps.Schema.Text);
                foreach (int db in s_mainAndTemp.Concat(_cat.Attached.Select(x => x.Slot)))
                {
                    if (only >= 0 && only != db)
                    {
                        continue;
                    }

                    var listed = new List<(string Shown, TableInfo T)>();
                    foreach (TableInfo t in _cat.DictOf(db).Values.Distinct())
                    {
                        bool master = t.Name is "sqlite_master" or "sqlite_temp_master";
                        string shown = master ? (db == 1 ? "sqlite_temp_schema" : "sqlite_schema") : t.Name;
                        if (arg is null || AsciiComparer.Instance.Equals(t.Name, arg) || AsciiComparer.Instance.Equals(shown, arg))
                        {
                            listed.Add((shown, t));
                        }
                    }

                    foreach ((string shown, TableInfo t) in listed.OrderBy(x => x.T.Name == "sqlite_temp_master" ? "\uffff" : x.Shown, StringComparer.Ordinal))
                    {
                        bool master = t.Name is "sqlite_master" or "sqlite_temp_master";
                        string type = t.View is not null || t.Unsupported == "views" ? "view" : t.Unsupported == "virtual tables" ? "virtual" : "table";
                        rows.Add([_cat.SchemaName(db), shown, type, master ? 5 : t.View is not null ? ViewShape(t)?.Columns.Length ?? 0 : t.Columns.Length, t.WithoutRowid ? 1 : 0, t.Strict ? 1 : 0]);
                    }
                }

                return ConstRows(["schema", "name", "type", "ncol", "wr", "strict"], rows);
            }

            case "foreign_key_list":
                return CompileForeignKeyList(ps);
            default:
                return null;
        }
    }

    /// <summary>The columns a view's SELECT produces, found by compiling it and throwing the code away; null (or the error, when asked) if it doesn't compile.</summary>
    private TableInfo? ViewShape(TableInfo view, bool throwErrors = false)
    {
        int codeMark = _code.Count, cursorMark = _cursors.Count;
        bool touched = _touchesFile;
        try
        {
            return OpenView(view, view.Name).Table;
        }
        catch (OneLiteException) when (!throwErrors)
        {
            return null;
        }
        finally
        {
            _code.RemoveRange(codeMark, _code.Count - codeMark);
            _cursors.RemoveRange(cursorMark, _cursors.Count - cursorMark);
            _touchesFile = touched;
        }
    }

    // The primary key's columns in key order: the rowid alias, the WITHOUT ROWID key, or the index behind a PRIMARY KEY constraint.
    private static int[] PragmaPkColumns(TableInfo t)
    {
        if (t.PrimaryKey is { } wk)
        {
            return wk.Columns;
        }

        if (t.RowidAlias >= 0)
        {
            return [t.RowidAlias];
        }

        IndexInfo? pk = t.AutoIndexes.FirstOrDefault(a => a.IsPrimaryKey);
        return pk?.Columns ?? [];
    }

    // PRAGMA index_list order: the schema's order reversed (the newest index first); a WITHOUT ROWID table's key comes last.
    private static List<IndexInfo> ListedIndexes(TableInfo t)
    {
        var l = new List<IndexInfo>(t.Indexes);
        l.Reverse();
        if (t.PrimaryKey is { } pk && t.WithoutRowid)
        {
            int n = Math.Max(t.AutoIndexes.FindIndex(a => a.IsPrimaryKey) + 1, 1);
            if (!t.Indexes.Any(i => i.IsPrimaryKey))
            {
                l.Add(new IndexInfo { Name = "sqlite_autoindex_" + t.Name + "_" + n, Columns = pk.Columns, Collations = pk.Collations, Desc = pk.Desc, Terms = pk.Terms, Unique = true, IsPrimaryKey = true });
            }
        }

        return l;
    }

    private (TableInfo, IndexInfo)? FindIndex(string name, string? schema = null)
    {
        IEnumerable<TableInfo> candidates = schema is null ? _cat.Tables.Values.Distinct()
            : _cat.SlotOf(schema) is var sl && sl >= 0 ? _cat.DictOf(sl).Values.Distinct() : [];
        foreach (TableInfo t in candidates)
        {
            foreach (IndexInfo ix in ListedIndexes(t))
            {
                if (AsciiComparer.Instance.Equals(ix.Name, name))
                {
                    return (t, ix);
                }
            }
        }

        // A WITHOUT ROWID table's key is an index that goes by the table's name.
        return _cat.FindIn(schema, name) is { WithoutRowid: true, PrimaryKey: { } pk } wt ? (wt, pk) : null;
    }
}
