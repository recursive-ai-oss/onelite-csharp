using RecursiveAI.OneLite.Storage;
using System.Linq;
using System.Collections.Immutable;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Sql.Parsing;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Vm;
using SqlStatement = RecursiveAI.OneLite.Sql.Statement;

namespace RecursiveAI.OneLite.Planning;

/// <summary>ASCII-only case folding for SQL identifiers: SQLite folds A-Z and nothing else (lang_naming).</summary>
internal sealed class AsciiComparer : IEqualityComparer<string>
{
    internal static readonly AsciiComparer Instance = new();

    public bool Equals(string? x, string? y)
    {
        if (x is null || y is null || x.Length != y.Length)
        {
            return x is null && y is null;
        }

        for (int i = 0; i < x.Length; i++)
        {
            if (Fold(x[i]) != Fold(y[i]))
            {
                return false;
            }
        }

        return true;
    }

    public int GetHashCode(string obj)
    {
        var h = new HashCode();
        foreach (char c in obj)
        {
            h.Add(Fold(c));
        }

        return h.ToHashCode();
    }

    internal static char Fold(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
}

internal enum GeneratedKind : byte
{
    None,
    Virtual,
    Stored,
}

/// <summary>One column of a table as the schema declares it.</summary>
internal sealed class ColumnInfo
{
    internal string Name = "";
    internal string DeclaredType = "";

    /// <summary>For a column of a view, CTE or subquery shape: the table column it passes through (sqlite3_column_origin_name and friends), or null.</summary>
    internal ColumnOrigin? Origin;

    /// <summary>The type as it prints in PRAGMA table_info: written text, with the six standard names in upper case.</summary>
    internal string TypeText = "";

    /// <summary>The DEFAULT expression's text as written (no outer parentheses), or null.</summary>
    internal string? DefaultText;
    internal Affinity Affinity;

    /// <summary>The collation named by the column definition, or null for BINARY.</summary>
    internal string? CollationName;
    internal GeneratedKind Generated;
    internal Expr? GeneratedExpr;
    internal Expr? Default;

    /// <summary>The DEFAULT expression as written (parenthesized), for ON DELETE SET DEFAULT.</summary>
    internal string? DefaultSql;
    internal bool PrimaryKey;

    /// <summary>NOT NULL, and the conflict clause that goes with it (lang_createtable "NOT NULL constraints").</summary>
    internal bool NotNull;
    internal ConflictAction? NotNullConflict;

    /// <summary>The position of the column's value in a stored record, or -1 for a virtual generated column.</summary>
    internal int RecordIndex;

    /// <summary>A hidden column of a virtual table: left out of <c>*</c>, and set by a table-function argument or an equality.</summary>
    internal bool Hidden;
}

/// <summary>A secondary index (or a WITHOUT ROWID table's key) over plain columns.</summary>
internal sealed class IndexInfo
{
    internal string Name = "";
    internal uint Root;

    /// <summary>The table column of each key column, in key order.</summary>
    internal int[] Columns = [];

    /// <summary>The collation of each key column.</summary>
    internal string?[] Collations = [];

    /// <summary>For each key column, whether the index orders it descending.</summary>
    internal bool[] Desc = [];

    /// <summary>True when every key column is a plain ascending column of a non-partial index, so a seek on its leading columns is sound.</summary>
    internal bool Usable;

    /// <summary>True when only a DESC key column keeps the index from being <see cref="Usable"/>: it still serves seeks and ranges, read in key order.</summary>
    internal bool DescOnly;

    /// <summary>The index can locate rows by its leading columns (an ascending index, or one whose only obstacle is a descending column).</summary>
    internal bool Seekable => Usable || DescOnly;

    /// <summary>True when a full scan of the index visits every row: not partial (descending and expression columns are fine: the tree is in key order and every entry points at its row).</summary>
    internal bool Scannable => Where is null;

    /// <summary>
    /// True when the index has no WHERE and every key column is a plain column (descending ones included): it holds a row for every table
    /// row, so a full scan of it can stand in for the table when it covers the query (the row order is the index's own).
    /// </summary>
    internal bool Coverable;
    /// <summary>UNIQUE index or constraint, the ON CONFLICT clause it carries, and whether it is the table's PRIMARY KEY.</summary>
    internal bool Unique;
    internal ConflictAction? OnConflict;
    internal bool IsPrimaryKey;

    /// <summary>Per key column: the expression of an index on an expression, null for a plain column.</summary>
    internal Expr?[] Terms = [];

    /// <summary>The WHERE of a partial index.</summary>
    internal Expr? Where;
}

/// <summary>A FOREIGN KEY of a table (foreignkeys).</summary>
internal sealed class FkInfo
{
    internal int[] Child = [];
    internal string Parent = "";
    internal string[]? ParentColumns;
    internal ForeignKeyAction OnDelete = ForeignKeyAction.NoAction;
    internal ForeignKeyAction OnUpdate = ForeignKeyAction.NoAction;
    internal bool Deferred;
}

/// <summary>A CHECK constraint with the text used in its failure message.</summary>
internal sealed class CheckInfo
{
    internal Expr Expr = null!;
    internal string Message = "";
    internal ConflictAction? OnConflict;
}

/// <summary>A table (or the schema table, or a view or virtual table the engine does not run yet).</summary>
internal sealed class TableInfo
{
    internal string Name = "";
    internal uint Root;
    internal ColumnInfo[] Columns = [];
    internal bool WithoutRowid;
    internal bool Strict;

    /// <summary>Index of the INTEGER PRIMARY KEY column that aliases the rowid, or -1.</summary>
    internal int RowidAlias = -1;

    /// <summary>For a WITHOUT ROWID table: the key columns of the primary key, in key order.</summary>
    internal IndexInfo? PrimaryKey;

    internal List<IndexInfo> Indexes = [];

    /// <summary>The indexes behind PRIMARY KEY and UNIQUE constraints, numbered from 1 as sqlite_autoindex_TABLE_N.</summary>
    internal List<IndexInfo> AutoIndexes = [];

    /// <summary>Why queries on this object are refused ("views", "virtual tables"), or null if the table is usable.</summary>
    internal string? Unsupported;

    /// <summary>For a virtual table: its module, arguments and (once connected) its instance.</summary>
    internal VirtualTables.VirtualInfo? Virtual;

    /// <summary>For a view: its definition (lang_createview). A view has no columns of its own until it is opened.</summary>
    internal CreateViewStatement? View;

    /// <summary>For a view: the CREATE VIEW text the definition was parsed from (result column names are cut from it).</summary>
    internal string ViewSql = "";

    /// <summary>0 for the main database, 1 for the temp database (whose B-trees live in the connection's own in-memory pager).</summary>
    internal int Db;

    /// <summary>True for an object of the temp database.</summary>
    internal bool IsTemp => Db == 1;

    /// <summary>A copy that shares everything but the trigger list (used to add triggers of the temp schema to a main table).</summary>
    internal TableInfo CopyForTriggers()
    {
        var c = (TableInfo)MemberwiseClone();
        c.Triggers = [.. Triggers];
        return c;
    }

    /// <summary>For a WITHOUT ROWID table: record position to column mapping is in <see cref="ColumnInfo.RecordIndex"/>; this is the record width.</summary>
    internal int RecordWidth;

    internal bool Autoincrement;

    /// <summary>The table has a trigger. Writes to it are refused until triggers run.</summary>
    internal bool HasTriggers;

    /// <summary>The triggers on this table or view (lang_createtrigger), in the order they were created.</summary>
    internal List<TriggerInfo> Triggers = [];
    internal ConflictAction? PkConflict;
    internal List<CheckInfo> Checks = [];
    internal List<FkInfo> ForeignKeys = [];

    internal int FindColumn(string name)
    {
        for (int i = 0; i < Columns.Length; i++)
        {
            if (AsciiComparer.Instance.Equals(Columns[i].Name, name))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>A trigger read from sqlite_schema. Its body is compiled when it first fires (<see cref="TrigSpec"/>).</summary>
internal sealed class TriggerInfo
{
    internal string Name = "";
    internal string Table = "";
    internal string Sql = "";
    internal CreateTriggerStatement Ast = null!;

    /// <summary>0 main, 1 temp: the schema whose table holds this trigger's row.</summary>
    internal int Db;

    /// <summary>For a trigger of the temp schema: it is on a table of the main schema (its ON clause says main., or no temp table has that name).</summary>
    internal bool OnMain;

    /// <summary>BEFORE, AFTER or INSTEAD OF (a trigger with no time given is BEFORE).</summary>
    internal TriggerTime Time;
    internal TrigSpec? Spec;
}

internal sealed record SchemaRow(long Rowid, string Type, string Name, string Table, uint Root, string? Sql, int Db = 0);

/// <summary>The parsed schema of the main database (fileformat2 §2.6): tables by name, loaded from sqlite_schema.</summary>
internal sealed class Catalog
{
    internal readonly Dictionary<string, TableInfo> Tables = new(AsciiComparer.Instance);

    /// <summary>The schema cookie (header offset 40) the catalog was read under.</summary>
    internal uint Cookie;

    /// <summary>The connection this catalog belongs to (virtual tables connect through it).</summary>
    internal Connection? Owner;

    internal readonly Dictionary<string, TriggerInfo> Triggers = new(AsciiComparer.Instance);

    /// <summary>The rows of sqlite_schema as read, with their rowids.</summary>
    internal readonly List<SchemaRow> Rows = [];

    /// <summary>The main schema's own objects (before temp objects shadow them). Equal to <see cref="Tables"/> while there are none.</summary>
    internal Dictionary<string, TableInfo> MainTables = new(AsciiComparer.Instance);

    /// <summary>The temp schema's objects, including sqlite_temp_master.</summary>
    internal Dictionary<string, TableInfo> TempTables = new(AsciiComparer.Instance);

    /// <summary>The rows of sqlite_temp_schema.</summary>
    internal readonly List<SchemaRow> TempRows = [];

    /// <summary>The temp schema table, sqlite_temp_master (root page 1 of the temp database).</summary>
    internal TableInfo TempMaster = SchemaBuilder.SchemaTable(1);

    /// <summary>The schemas of the attached databases, in the order of their slots (2 and up).</summary>
    internal readonly List<AttachedSchema> Attached = [];

    /// <summary>Finds an object the way an unqualified name is resolved (lang_naming): temp first, then main, then the attached databases in order.</summary>
    internal TableInfo? Find(string name) => Tables.GetValueOrDefault(name);

    /// <summary>The slot of a schema name: 0 main, 1 temp, 2 and up attached; -1 if there is no such database.</summary>
    internal int SlotOf(string schema)
    {
        if (AsciiComparer.Instance.Equals(schema, "main"))
        {
            return 0;
        }

        if (AsciiComparer.Instance.Equals(schema, "temp"))
        {
            return 1;
        }

        foreach (AttachedSchema a in Attached)
        {
            if (AsciiComparer.Instance.Equals(a.Name, schema))
            {
                return a.Slot;
            }
        }

        return -1;
    }

    /// <summary>The name of the schema in slot <paramref name="db"/>.</summary>
    internal string SchemaName(int db) => db == 0 ? "main" : db == 1 ? "temp" : Attached.Find(a => a.Slot == db)?.Name ?? "main";

    /// <summary>The objects of the schema in slot <paramref name="db"/>.</summary>
    internal Dictionary<string, TableInfo> DictOf(int db)
        => db == 0 ? MainTables : db == 1 ? TempTables : Attached.Find(a => a.Slot == db)?.Tables ?? MainTables;

    /// <summary>Finds an object in a named schema ("main", "temp" or an attached name), or by search order when <paramref name="schema"/> is null.</summary>
    internal TableInfo? FindIn(string? schema, string name)
    {
        if (schema is null)
        {
            return Find(name);
        }

        int slot = SlotOf(schema);
        return slot < 0 ? null : DictOf(slot).GetValueOrDefault(name);
    }

    /// <summary>The schema table (sqlite_master, sqlite_temp_master or an attached database's sqlite_master) of database <paramref name="db"/>.</summary>
    internal TableInfo MasterOf(int db) => db == 1 ? TempMaster : db == 0 ? MainTables["sqlite_master"] : Attached.Find(a => a.Slot == db)!.Master;

    /// <summary>The rows of the schema table of database <paramref name="db"/>.</summary>
    internal List<SchemaRow> RowsOf(int db) => db == 1 ? TempRows : db == 0 ? Rows : Attached.Find(a => a.Slot == db)!.Rows;
}

/// <summary>The schema of one attached database.</summary>
internal sealed class AttachedSchema
{
    internal string Name = "";
    internal int Slot;
    internal string Path = "";
    internal Dictionary<string, TableInfo> Tables = new(AsciiComparer.Instance);
    internal TableInfo Master = null!;
    internal List<SchemaRow> Rows = [];
}

/// <summary>Builds <see cref="TableInfo"/> and <see cref="IndexInfo"/> from the CREATE statements stored in sqlite_schema.</summary>
internal static class SchemaBuilder
{
    internal static TableInfo SchemaTable(int db = 0)
    {
        string[] names = ["type", "name", "tbl_name", "rootpage", "sql"];
        Affinity[] aff = [Affinity.Text, Affinity.Text, Affinity.Text, Affinity.Integer, Affinity.Text];
        var cols = new ColumnInfo[5];
        for (int i = 0; i < 5; i++)
        {
            cols[i] = new ColumnInfo { Name = names[i], DeclaredType = i == 3 ? "int" : "text", TypeText = i == 3 ? "INT" : "TEXT", Affinity = aff[i], RecordIndex = i };
        }

        return new TableInfo { Name = db == 1 ? "sqlite_temp_master" : "sqlite_master", Root = 1, Columns = cols, RecordWidth = 5, Db = db };
    }

    /// <summary>
    /// The indexes SQLite creates for PRIMARY KEY (other than an INTEGER PRIMARY KEY) and UNIQUE constraints, in the order it names them
    /// (sqlite_autoindex_TABLE_1, _2, ...): column constraints in column order, then table constraints; a repeat of an earlier column list
    /// makes no new index (lang_createtable "UNIQUE constraints"; the numbering was checked against the shell).
    /// </summary>
    private static List<IndexInfo> AutoIndexColumns(CreateTableStatement s, TableInfo t, int[] pk, bool[] pkDesc)
    {
        var result = new List<IndexInfo>();

        void Add(int[] cols, bool[] desc, string?[]? colls, ConflictAction? onConflict, bool isPk)
        {
            if (cols.Length == 0)
            {
                return;
            }

            foreach (IndexInfo e in result)
            {
                if (e.Columns.AsSpan().SequenceEqual(cols))
                {
                    return;
                }
            }

            result.Add(new IndexInfo
            {
                Columns = cols,
                Collations = colls ?? [.. cols.Select(c => t.Columns[c].CollationName)],
                Usable = Array.IndexOf(desc, true) < 0,
                DescOnly = Array.IndexOf(desc, true) >= 0,
                Coverable = true,
                Unique = true,
                OnConflict = onConflict,
                IsPrimaryKey = isPk,
                Desc = desc,
                Terms = new Expr?[cols.Length],
            });
        }

        bool hasTablePk = s.Constraints.Any(c => c is PrimaryKeyTableConstraint);
        for (int i = 0; i < s.Columns.Length; i++)
        {
            foreach (ColumnConstraint k in s.Columns[i].Constraints)
            {
                if (k is PrimaryKeyColumnConstraint pc && !hasTablePk && t.RowidAlias != i && !t.WithoutRowid)
                {
                    Add([i], [pc.Order == SortOrder.Desc], null, pc.OnConflict, true);
                }
                else if (k is UniqueColumnConstraint uc)
                {
                    Add([i], [false], null, uc.OnConflict, false);
                }
            }
        }

        foreach (TableConstraint tc in s.Constraints)
        {
            switch (tc)
            {
                case PrimaryKeyTableConstraint ptc when pk.Length > 0 && !(pk.Length == 1 && t.RowidAlias == pk[0]) && !t.WithoutRowid:
                    Add(pk, pkDesc, null, ptc.OnConflict, true);
                    break;
                case UniqueTableConstraint u:
                {
                    var cols = new List<int>();
                    var desc = new List<bool>();
                    var colls = new List<string?>();
                    bool plain = true;
                    foreach (OrderingTerm term in u.Columns)
                    {
                        Expr e = term.Expr;
                        string? cn = null;
                        if (e is CollateExpr ce)
                        {
                            cn = ce.Collation.Text;
                            e = ce.Operand;
                        }

                        int ci = e is ColumnRef cr && cr.Table is null ? t.FindColumn(cr.Column.Text) : -1;
                        if (ci < 0)
                        {
                            plain = false;
                            break;
                        }

                        cols.Add(ci);
                        desc.Add(term.Order == SortOrder.Desc);
                        colls.Add(cn ?? t.Columns[ci].CollationName);
                    }

                    if (plain)
                    {
                        Add([.. cols], [.. desc], [.. colls], u.OnConflict, false);
                    }
                    else
                    {
                        // An index over an expression still takes its number; keep the numbering with a placeholder.
                        result.Add(new IndexInfo { Columns = [], Collations = [], Usable = false });
                    }

                    break;
                }
            }
        }

        return result;
    }

    private static string TypeText(TypeName? t) => t is null ? string.Empty : string.Join(' ', t.Words);

    internal static TableInfo BuildTable(CreateTableStatement s, uint root, string? sql = null)
    {
        var checks = new List<CheckInfo>();
        var fks = new List<(int Column, ForeignKeyClause Clause, Deferral? Deferral)>();
        string Text(Expr e) => sql is not null && e.Span.End <= sql.Length && e.Span.End > e.Span.Start ? sql[e.Span.Start..e.Span.End] : "";
        ConflictAction? pkConflict = null;
        bool autoinc = false;
        bool strict = s.Strict;
        bool wor = s.WithoutRowid;
        var cols = new List<ColumnInfo>();
        for (int cx = 0; cx < s.Columns.Length; cx++)
        {
            ColumnDefinition cd = s.Columns[cx];
            var c = new ColumnInfo { Name = cd.Name.Text, DeclaredType = TypeText(cd.Type) };
            if (cd.Type is { } ctn && sql is not null && ctn.Span.End <= sql.Length && ctn.Span.End > ctn.Span.Start)
            {
                string written = sql[ctn.Span.Start..ctn.Span.End].Trim();
                c.TypeText = written.ToUpperInvariant() is "INT" or "INTEGER" or "REAL" or "TEXT" or "BLOB" or "ANY" ? written.ToUpperInvariant() : written;
            }
            else
            {
                c.TypeText = c.DeclaredType;
            }

            c.Affinity = Values.AffinityOf(c.DeclaredType, strict);
            foreach (ColumnConstraint k in cd.Constraints)
            {
                switch (k)
                {
                    case PrimaryKeyColumnConstraint pkc:
                        c.PrimaryKey = true;
                        pkConflict = pkc.OnConflict;
                        autoinc |= pkc.Autoincrement;
                        break;
                    case NotNullConstraint nn:
                        c.NotNull = true;
                        c.NotNullConflict = nn.OnConflict;
                        break;
                    case CheckColumnConstraint ck:
                        checks.Add(new CheckInfo { Expr = ck.Check, Message = ck.ConstraintName?.Text ?? Text(ck.Check) });
                        break;
                    case ReferencesConstraint rc:
                        fks.Add((cx, rc.Clause, null));
                        break;
                    case DeferralConstraint dfc when fks.Count > 0 && fks[^1].Column == cx && fks[^1].Deferral is null:
                        fks[^1] = (cx, fks[^1].Clause, dfc.Deferral);
                        break;
                    case CollateConstraint cc:
                        c.CollationName ??= cc.Collation.Text;
                        break;
                    case DefaultConstraint dc:
                        if (c.Default is null)
                        {
                            c.Default = dc.Value;
                            c.DefaultSql = "(" + Text(dc.Value) + ")";
                            c.DefaultText = Text(dc.Value);
                        }

                        break;
                    case GeneratedConstraint gc:
                        c.GeneratedExpr = gc.Value;
                        c.Generated = string.Equals(gc.StorageWord, "STORED", StringComparison.OrdinalIgnoreCase) ? GeneratedKind.Stored : GeneratedKind.Virtual;
                        break;
                }
            }

            cols.Add(c);
        }

        var t = new TableInfo { Name = s.Name.Name.Text, Root = root, Columns = [.. cols], WithoutRowid = wor, Strict = strict };

        // The primary key: a column-level PRIMARY KEY or the table constraint (lang_createtable §"The PRIMARY KEY").
        int[] pk = [];
        bool[] pkDesc = [];
        string?[] pkColl = [];
        PrimaryKeyTableConstraint? tpk = null;
        foreach (TableConstraint tc in s.Constraints)
        {
            if (tc is PrimaryKeyTableConstraint p)
            {
                tpk = p;
                pkConflict = p.OnConflict;
                autoinc |= p.Autoincrement;
            }
            else if (tc is CheckTableConstraint ctc)
            {
                checks.Add(new CheckInfo { Expr = ctc.Check, Message = ctc.ConstraintName?.Text ?? Text(ctc.Check), OnConflict = ctc.OnConflict });
            }
        }

        if (tpk != null)
        {
            var idx = new List<int>();
            var desc = new List<bool>();
            var coll = new List<string?>();
            foreach (OrderingTerm term in tpk.Columns)
            {
                Expr e = term.Expr;
                string? cn = null;
                if (e is CollateExpr ce)
                {
                    cn = ce.Collation.Text;
                    e = ce.Operand;
                }

                int ci = e is ColumnRef cr && cr.Table is null ? t.FindColumn(cr.Column.Text) : -1;
                if (ci < 0)
                {
                    idx.Clear();
                    break;
                }

                idx.Add(ci);
                desc.Add(term.Order == SortOrder.Desc);
                coll.Add(cn ?? t.Columns[ci].CollationName);
            }

            pk = [.. idx];
            pkDesc = [.. desc];
            pkColl = [.. coll];
        }
        else
        {
            for (int i = 0; i < s.Columns.Length; i++)
            {
                foreach (ColumnConstraint k in s.Columns[i].Constraints)
                {
                    if (k is PrimaryKeyColumnConstraint pc)
                    {
                        pk = [i];
                        pkDesc = [pc.Order == SortOrder.Desc];
                        pkColl = [t.Columns[i].CollationName];
                    }
                }
            }
        }

        foreach (int c in pk)
        {
            t.Columns[c].PrimaryKey = true;
        }

        // INTEGER PRIMARY KEY (exactly that type, not DESC) makes the column an alias of the rowid (datatype3 §"Rowid"; lang_createtable).
        if (!wor && pk.Length == 1 && !pkDesc[0])
        {
            TypeName? ty = pk[0] < s.Columns.Length ? s.Columns[pk[0]].Type : null;
            if (ty is { Words.Length: 1, Sizes.Length: 0 } && string.Equals(ty.Words[0], "INTEGER", StringComparison.OrdinalIgnoreCase))
            {
                t.RowidAlias = pk[0];
            }
        }

        t.AutoIndexes = AutoIndexColumns(s, t, pk, pkDesc);

        // Record layout (fileformat2 §2.1; lang_createtable "WITHOUT ROWID"): key columns first for WITHOUT ROWID, virtual generated columns not stored.
        int r = 0;
        if (wor && pk.Length > 0)
        {
            foreach (int c in pk)
            {
                t.Columns[c].RecordIndex = r++;
            }

            for (int i = 0; i < t.Columns.Length; i++)
            {
                if (Array.IndexOf(pk, i) < 0)
                {
                    t.Columns[i].RecordIndex = t.Columns[i].Generated == GeneratedKind.Virtual ? -1 : r++;
                }
            }

            t.PrimaryKey = new IndexInfo
            {
                Name = "PRIMARY KEY",
                Root = root,
                Columns = pk,
                Collations = pkColl,
                Usable = Array.IndexOf(pkDesc, true) < 0,
                DescOnly = Array.IndexOf(pkDesc, true) >= 0,
                Unique = true,
                IsPrimaryKey = true,
                OnConflict = pkConflict,
                Desc = pkDesc,
                Terms = new Expr?[pk.Length],
            };
        }
        else
        {
            for (int i = 0; i < t.Columns.Length; i++)
            {
                t.Columns[i].RecordIndex = t.Columns[i].Generated == GeneratedKind.Virtual ? -1 : r++;
            }
        }

        t.RecordWidth = r;
        t.Checks = checks;
        t.PkConflict = pkConflict;
        t.Autoincrement = autoinc && t.RowidAlias >= 0;
        foreach ((int fc, ForeignKeyClause clause, Deferral? dfr) in fks)
        {
            t.ForeignKeys.Add(MakeFk(t, [fc], clause, dfr));
        }

        foreach (TableConstraint tc in s.Constraints)
        {
            if (tc is ForeignKeyTableConstraint fkc)
            {
                var cc = new List<int>();
                foreach (IndexedColumn ic in fkc.Columns)
                {
                    cc.Add(t.FindColumn(ic.Name.Text));
                }

                if (!cc.Contains(-1))
                {
                    t.ForeignKeys.Add(MakeFk(t, [.. cc], fkc.Clause, fkc.Deferral));
                }
            }
        }

        return t;
    }

    private static FkInfo MakeFk(TableInfo t, int[] child, ForeignKeyClause clause, Deferral? deferral)
    {
        // foreignkeys: a constraint is deferred only by DEFERRABLE INITIALLY DEFERRED; NOT DEFERRABLE is always immediate.
        var fk = new FkInfo { Child = child, Parent = clause.Table.Text, Deferred = deferral is { Not: false, Initially: DeferMode.Deferred } };
        if (!clause.Columns.IsDefaultOrEmpty)
        {
            fk.ParentColumns = [.. clause.Columns.Select(c => c.Name.Text)];
        }

        foreach (ForeignKeyRule r in clause.Rules)
        {
            if (r.Kind == ForeignKeyRuleKind.OnDelete)
            {
                fk.OnDelete = r.Action;
            }
            else if (r.Kind == ForeignKeyRuleKind.OnUpdate)
            {
                fk.OnUpdate = r.Action;
            }
        }

        return fk;
    }

    /// <summary>Builds an index; returns null if the statement does not describe a plain index of an existing column set.</summary>
    internal static IndexInfo BuildIndex(CreateIndexStatement s, uint root, TableInfo table)
    {
        var idx = new IndexInfo { Name = s.Name.Name.Text, Root = root, Unique = s.Unique, Where = s.Where };
        var cols = new List<int>();
        var descs = new List<bool>();
        var terms = new List<Expr?>();
        var colls = new List<string?>();
        bool usable = s.Where is null;
        bool seekable = s.Where is null;
        bool coverable = s.Where is null;
        foreach (OrderingTerm term in s.Columns)
        {
            Expr e = term.Expr;
            string? cn = null;
            if (e is CollateExpr ce)
            {
                cn = ce.Collation.Text;
                e = ce.Operand;
            }

            int ci = e is ColumnRef cr && cr.Table is null ? table.FindColumn(cr.Column.Text) : -1;
            descs.Add(term.Order == SortOrder.Desc);
            if (ci < 0 || table.Columns[ci].Generated == GeneratedKind.Virtual)
            {
                usable = false;
                seekable = false;

                // An expression key still makes a row for every table row; a virtual column's value would have to be recomputed.
                coverable &= ci < 0;
                cols.Add(-1);
                colls.Add(cn);
                terms.Add(e);
                continue;
            }

            terms.Add(null);

            if (term.Order == SortOrder.Desc)
            {
                usable = false;
            }

            // Observed: the reference does not read an index on a generated column in place of the table or to order a window's rows.
            coverable &= table.Columns[ci].Generated == GeneratedKind.None;
            cols.Add(ci);
            colls.Add(cn ?? table.Columns[ci].CollationName);
        }

        idx.Columns = [.. cols];
        idx.Collations = [.. colls];
        idx.Desc = [.. descs];
        idx.Terms = [.. terms];
        idx.Usable = usable;
        idx.DescOnly = seekable && !usable;
        idx.Coverable = coverable;
        return idx;
    }
}

/// <summary>
/// Reads sqlite_schema into a <see cref="Catalog"/> (fileformat2 §2.6). A restartable state machine over one B-tree cursor: after
/// <see cref="BtStatus.Pending"/> call <see cref="Step"/> again; the catalog is published only when it is complete (spec: Schema.Reload).
/// </summary>
internal sealed class SchemaLoader
{
    private readonly BtContext _ctx;
    private readonly TextEncoding _enc;
    private readonly BtCursor _cur;
    private readonly RecordView _view = new();
    private int _state;
    private readonly List<(string Type, string Name, string Table, uint Root, string? Sql)> _rows = [];
    private readonly List<long> _rowids = [];

    private readonly int _db;

    internal SchemaLoader(BtContext ctx, int db = 0)
    {
        _db = db;
        _ctx = ctx;
        _enc = ctx.Encoding;
        _cur = BTree.Table(ctx, 1).OpenCursor();
    }

    internal Exception? Error => _cur.Error;

    private string Text(int i)
    {
        return _view.ClassOf(i) switch
        {
            StorageClass.Null => string.Empty,
            StorageClass.Integral => _view.GetInt64(i).ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => TextCodec.Decode(_view.GetBytes(i), _enc),
        };
    }

    /// <summary>Advances the load. On Ok, <paramref name="catalog"/> is complete.</summary>
    internal BtStatus Step(out Catalog? catalog)
    {
        catalog = null;
        for (; ; )
        {
            switch (_state)
            {
                case 0:
                {
                    BtStatus s = _cur.First();
                    if (s != BtStatus.Ok)
                    {
                        return s;
                    }

                    _state = 1;
                    break;
                }

                case 1:
                    if (_cur.State != CursorState.Valid)
                    {
                        catalog = Build();
                        _cur.Dispose();
                        _state = 3;
                        return BtStatus.Ok;
                    }

                    _state = 2;
                    break;
                case 2:
                {
                    BtStatus s = _cur.LoadPayload();
                    if (s != BtStatus.Ok)
                    {
                        return s;
                    }

                    if (!_cur.ParseRecord(_view) || _view.ColumnCount < 5)
                    {
                        throw new OneLiteException(ResultCode.Corrupt, "database disk image is malformed", "sqlite_schema row " + _cur.Rowid + " is not a schema record");
                    }

                    uint root = _view.ClassOf(3) == StorageClass.Integral ? (uint)_view.GetInt64(3) : 0;
                    string? sql = _view.ClassOf(4) == StorageClass.Null ? null : Text(4);
                    _rows.Add((Text(0), Text(1), Text(2), root, sql));
                    _rowids.Add(_cur.Rowid);
                    _state = 4;
                    break;
                }

                case 4:
                {
                    BtStatus s = _cur.Next();
                    if (s != BtStatus.Ok)
                    {
                        return s;
                    }

                    _state = 1;
                    break;
                }

                default:
                    throw new InvalidOperationException("schema load is finished");
            }
        }
    }

    private Catalog Build()
    {
        var cat = new Catalog();
        for (int ri = 0; ri < _rows.Count; ri++)
        {
            (_db >= 2 ? cat.Rows : cat.RowsOf(_db)).Add(new SchemaRow(_rowids[ri], _rows[ri].Type, _rows[ri].Name, _rows[ri].Table, _rows[ri].Root, _rows[ri].Sql, _db));
        }

        TableInfo master = SchemaBuilder.SchemaTable(_db);
        cat.Tables[_db == 1 ? "sqlite_temp_master" : "sqlite_master"] = master;
        cat.Tables[_db == 1 ? "sqlite_temp_schema" : "sqlite_schema"] = master;
        foreach ((string type, string name, string table, uint root, string? sql) in _rows)
        {
            if (type == "table" && sql != null)
            {
                SqlStatement? st = ParseSchemaSql(name, sql);
                switch (st)
                {
                    case CreateTableStatement ct:
                        cat.Tables[name] = SchemaBuilder.BuildTable(ct, root, sql);
                        break;
                    case CreateVirtualTableStatement cvt:
                        cat.Tables[name] = new TableInfo { Name = name, Root = root, Virtual = new VirtualTables.VirtualInfo { Module = cvt.Module.Text, Args = cvt.Arguments } };
                        break;
                    default:
                        throw Malformed(name, "not a CREATE TABLE statement");
                }
            }
            else if (type == "view")
            {
                cat.Tables[name] = new TableInfo { Name = name, ViewSql = sql ?? "", View = ParseSchemaSql(name, sql ?? "") as CreateViewStatement ?? throw Malformed(name, "not a CREATE VIEW statement") };
            }
        }

        foreach ((string type, string tname, string table, uint _, string? tsql) in _rows)
        {
            if (type == "trigger" && tsql is not null)
            {
                if (ParseSchemaSql(tname, tsql) is not CreateTriggerStatement cts)
                {
                    throw Malformed(tname, "not a CREATE TRIGGER statement");
                }

                var ti = new TriggerInfo { Name = tname, Table = table, Sql = tsql, Ast = cts, Time = cts.Time == TriggerTime.Default ? TriggerTime.Before : cts.Time };
                cat.Triggers[tname] = ti;
                string? ts = cts.Table.Schema?.Text;
                ti.OnMain = _db == 1 && (ts is not null ? AsciiComparer.Instance.Equals(ts, "main") : !cat.Tables.ContainsKey(table));
                if (!ti.OnMain && cat.Tables.TryGetValue(table, out TableInfo? tt))
                {
                    tt.HasTriggers = true;
                    tt.Triggers.Add(ti);
                }
            }
        }

        foreach ((string type, string name, string table, uint root, string? sql) in _rows)
        {
            if (type == "index" && sql == null && cat.Tables.TryGetValue(table, out TableInfo? at) && at.Unsupported is null)
            {
                // The index behind a PRIMARY KEY or UNIQUE constraint has no SQL; its number says which constraint it is.
                int us = name.LastIndexOf('_');
                if (name.StartsWith("sqlite_autoindex_", StringComparison.Ordinal) && us > 0
                    && int.TryParse(name.AsSpan(us + 1), out int n) && n >= 1 && n <= at.AutoIndexes.Count && at.AutoIndexes[n - 1].Columns.Length > 0)
                {
                    IndexInfo a = at.AutoIndexes[n - 1];
                    at.Indexes.Add(new IndexInfo { Name = name, Root = root, Columns = a.Columns, Collations = a.Collations, Usable = a.Usable, DescOnly = a.DescOnly, Coverable = a.Coverable, Unique = true, OnConflict = a.OnConflict, IsPrimaryKey = a.IsPrimaryKey, Desc = a.Desc, Terms = a.Terms });
                }
            }
            else if (type == "index" && sql != null && cat.Tables.TryGetValue(table, out TableInfo? t) && t.Unsupported is null)
            {
                if (ParseSchemaSql(name, sql) is CreateIndexStatement ci)
                {
                    t.Indexes.Add(SchemaBuilder.BuildIndex(ci, root, t));
                }
                else
                {
                    throw Malformed(name, "not a CREATE INDEX statement");
                }
            }
        }

        foreach (TableInfo t in cat.Tables.Values)
        {
            t.Db = _db;
        }

        foreach (TriggerInfo t in cat.Triggers.Values)
        {
            t.Db = _db;
        }

        if (_db == 1)
        {
            cat.TempMaster = master;
        }

        return cat;
    }

    private static SqlStatement? ParseSchemaSql(string name, string sql)
    {
        try
        {
            return SqlParser.ParseOne(sql, 0, out _);
        }
        catch (OneLiteException e)
        {
            throw Malformed(name, e.Message);
        }
    }

    private static OneLiteException Malformed(string name, string detail)
        => new(ResultCode.Corrupt, "malformed database schema (" + name + ")", detail);
}
