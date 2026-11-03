using System.Linq;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Sql.Parsing;
using SqlStatement = RecursiveAI.OneLite.Sql.Statement;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite.Planning;

// REINDEX (lang_reindex): the named indexes are emptied (their root pages stay) and filled again from their tables.
internal sealed partial class Compiler
{
    internal VmProgram CompileReindex(ReindexStatement ri)
    {
        PreassignParameters();
        var todo = new List<(TableInfo T, IndexInfo Ix)>();
        void AddTable(TableInfo t)
        {
            if (t.View is null && t.Unsupported is null)
            {
                foreach (IndexInfo ix in t.Indexes)
                {
                    if (ix.Root != 0 && ix.Columns.Length > 0)
                    {
                        todo.Add((t, ix));
                    }
                }
            }
        }

        if (ri.Target is null)
        {
            foreach (TableInfo t in _cat.Tables.Values.Distinct())
            {
                AddTable(t);
            }
        }
        else
        {
            string? schema = ri.Target.Schema?.Text;
            int rdb = schema is null ? -1 : _cat.SlotOf(schema);
            if (schema is not null && rdb < 0)
            {
                throw ErrAt("unknown database " + schema, ri.Target.Schema!);
            }

            string name = ri.Target.Name.Text;
            if (_cat.FindIn(schema, name) is { } tt)
            {
                AddTable(tt);
            }
            else if (FindIndex(name, out TableInfo? owner, rdb) is { } one)
            {
                todo.Add((owner!, one));
            }
            else if (schema is null && (AsciiComparer.Instance.Equals(name, "BINARY") || AsciiComparer.Instance.Equals(name, "NOCASE") || AsciiComparer.Instance.Equals(name, "RTRIM")))
            {
                foreach (TableInfo t in _cat.Tables.Values.Distinct())
                {
                    if (t.View is null && t.Unsupported is null)
                    {
                        foreach (IndexInfo ix in t.Indexes)
                        {
                            if (ix.Root != 0 && ix.Columns.Length > 0 && ix.Collations.Any(c => AsciiComparer.Instance.Equals(c ?? "BINARY", name)))
                            {
                                todo.Add((t, ix));
                            }
                        }
                    }
                }
            }
            else
            {
                throw Err("unable to identify the object to be reindexed");
            }
        }

        if (todo.Count == 0)
        {
            return EmptyProgram();
        }

        Emit(Op.Transaction, 0, 0, 0, null, 1);
        foreach ((TableInfo t, IndexInfo ix) in todo)
        {
            int root = NewReg();
            Emit(Op.Integer, 0, root, 0, (long)ix.Root);
            Emit(Op.ClearTree, root, 0, 1, t.Db);
            _table = t;
            _alias = t.Name;
            SetColumns(true);
            EmitIndexBuild(t, ix, root);
        }

        Emit(Op.Halt);
        return DdlProgram();
    }

    // ANALYZE (lang_analyze): sqlite_stat1 gets one row per index, "rows avg1 avg2 ..." where avgK is the average number of rows per distinct
    // value of the first K columns, rounded up, and a row with only the row count for a table that has no index covering all of its rows.
    // Tables and indexes with no rows get no row. The numbers come from small SELECTs run as nested programs.
    internal VmProgram CompileAnalyze(AnalyzeStatement an)
    {
        PreassignParameters();
        var tables = new List<TableInfo>();
        bool whole = an.Target is null || (an.Target.Schema is null && AsciiComparer.Instance.Equals(an.Target.Name.Text, "main"));
        IndexInfo? onlyIndex = null;
        if (whole)
        {
            // lang_analyze: a schema name analyses everything in that schema
            tables.AddRange(SchemaHashOrder.Order(TablesOf(0).Where(t => !IsSchemaTable(t)).ToList(), t => t.Name).Where(Analyzable));
        }
        else
        {
            string? schema = an.Target!.Schema?.Text;
            if (schema is not null && !AsciiComparer.Instance.Equals(schema, "main"))
            {
                if (_cat.SlotOf(schema) < 1)
                {
                    throw ErrAt("unknown database " + schema, an.Target.Schema!);
                }

                string other = an.Target.Name.Text;
                if (_cat.FindIn(schema, other) is null && FindIndex(other, out _, _cat.SlotOf(schema)) is null)
                {
                    throw Err("no such table: " + schema + "." + other);
                }

                throw Unsupported("ANALYZE of the " + (schema.Equals("temp", StringComparison.OrdinalIgnoreCase) ? "temp schema" : "attached database " + schema));
            }

            string name = an.Target!.Name.Text;
            if (_cat.FindIn(schema ?? "main", name) is { } one && Analyzable(one))
            {
                tables.Add(one);
            }
            else if (FindIndex(name, out TableInfo? owner) is { } oneIndex && owner is not null && Analyzable(owner))
            {
                // an index named alone: only its own row of sqlite_stat1 is made
                tables.Add(owner);
                onlyIndex = oneIndex;
            }
            else if (_cat.Find(name) is null && FindIndex(name, out _) is null)
            {
                throw Err("no such table: " + (schema is null ? name : schema + "." + name));
            }
        }

        return CompileAnalyzeTables(tables, whole, null, onlyIndex);
    }

    // The tables of PRAGMA optimize that its decisions picked: one program for all of them, so sqlite_stat1 is made once. Table i runs only when the
    // optimize statement's register for it (variable i + 1) is true.
    internal VmProgram CompileAnalyzeGuarded(List<TableInfo> tables) => CompileAnalyzeTables(tables, false, Enumerable.Range(1, tables.Count).ToArray(), null);

    private VmProgram CompileAnalyzeTables(List<TableInfo> tables, bool whole, int[]? guard, IndexInfo? onlyIndex)
    {
        // sqlite_stat1, created by the first ANALYZE
        const string statSql = "CREATE TABLE sqlite_stat1(tbl,idx,stat)";
        TableInfo? existing = _cat.FindIn("main", "sqlite_stat1");
        TableInfo stat = existing ?? SchemaBuilder.BuildTable((CreateTableStatement)SqlParser.ParseOne(statSql, 0, out _)!, 0, statSql);
        Emit(Op.Transaction, 0, 0, 0, null, 1);
        int sc;
        if (existing is null)
        {
            TableInfo master = _cat.MasterOf(0);
            int mc = AddCursor(new CursorSpec { Table = master, Root = 1 });
            Emit(Op.OpenWrite, mc);
            int root = NewReg();
            Emit(Op.CreateBtree, root, 1, 0);
            EmitSchemaRow(mc, master, "table", "sqlite_stat1", "sqlite_stat1", root, statSql);
            sc = AddCursor(new CursorSpec { Table = stat, Root = 0 });
            Emit(Op.OpenWrite, sc, root);
        }
        else
        {
            sc = AddCursor(new CursorSpec { Table = stat, Root = stat.Root });
            Emit(Op.OpenWrite, sc);
            for (int ti = 0; ti < tables.Count; ti++)
            {
                TableInfo t = tables[ti];
                int skipDelete = NewLabel();
                EmitGuard(guard, ti, skipDelete);
                Emit(Op.FkRun, NewReg(), 0, 0, MakeFkSpec("DELETE FROM sqlite_stat1 WHERE tbl = " + QuoteLiteral(t.Name)
                    + (onlyIndex is null ? "" : " AND idx = " + QuoteLiteral(onlyIndex.Name)), false), 0);
                Mark(skipDelete);
            }

            if (whole)
            {
                Emit(Op.FkRun, NewReg(), 0, 0, MakeFkSpec("DELETE FROM sqlite_stat1", false), 0);
            }
        }

        for (int ti = 0; ti < tables.Count; ti++)
        {
            TableInfo t = tables[ti];
            int tableEnd = NewLabel();
            EmitGuard(guard, ti, tableEnd);
            string tq = Q(t.Name);
            var rows = new List<(string? Idx, string Sql)>();
            bool fullIndex = false;
            var idxs = new List<IndexInfo>(t.Indexes);
            if (t.WithoutRowid && t.PrimaryKey is { } pk)
            {
                idxs.Add(pk);
            }

            // newest index first
            idxs.Reverse();
            foreach (IndexInfo ix in idxs)
            {
                if (ix.Columns.Length == 0 || (onlyIndex is not null && !ReferenceEquals(ix, onlyIndex)))
                {
                    continue;
                }

                if (ix.Where is null)
                {
                    fullIndex = true;
                }

                string where = ix.Where is null ? "" : " WHERE " + SqlWriter.Write(ix.Where);
                string name = ReferenceEquals(ix, t.PrimaryKey) ? t.Name : ix.Name;
                rows.Add((name, StatSql(t, ix, tq, where)));
            }

            if (!fullIndex && onlyIndex is null)
            {
                rows.Add((null, "SELECT CASE WHEN n > 0 THEN CAST(n AS TEXT) END FROM (SELECT count(*) AS n FROM " + tq + ")"));
            }

            foreach ((string? idx, string sql) in rows)
            {
                int b = NewRegs(3);
                int skip = NewLabel();
                Emit(Op.String8, 0, b, 0, Utf8(t.Name));
                if (idx is null)
                {
                    Emit(Op.Null, 0, b + 1);
                }
                else
                {
                    Emit(Op.String8, 0, b + 1, 0, Utf8(idx));
                }

                Emit(Op.Null, 0, b + 2);
                Emit(Op.FkRun, b + 2, 0, 0, MakeFkSpec(sql, true), 0);
                Emit(Op.IfNull, b + 2, skip);
                int rid = NewReg();
                int rec = NewReg();
                Emit(Op.NewRowid, sc, rid, 0);
                Emit(Op.Insert, sc, b, rid, stat, 0);
                Mark(skip);
                _ = rec;
            }

            Mark(tableEnd);
        }

        if (existing is null)
        {
            // after the nested SELECTs: they were compiled for the old cookie
            Emit(Op.BumpCookie);
        }

        Emit(Op.Halt);
        return DdlProgram();
    }

    private void EmitGuard(int[]? guard, int index, int skipLabel)
    {
        if (guard is null)
        {
            return;
        }

        int g = NewReg();
        Emit(Op.Variable, guard[index], g);
        Emit(Op.IfNot, g, skipLabel);
    }

    // VACUUM (lang_vacuum), and VACUUM INTO, which reads the database in a read transaction and writes the copy to another file.
    internal VmProgram CompileVacuum(VacuumStatement vs)
    {
        PreassignParameters();
        string? schema = vs.Schema?.Text;
        int vdb = 0;
        if (schema is not null && !AsciiComparer.Instance.Equals(schema, "main"))
        {
            if (AsciiComparer.Instance.Equals(schema, "temp"))
            {
                if (vs.Into is null)
                {
                    return EmptyProgram();
                }

                vdb = 1;
            }
            else
            {
                vdb = _cat.SlotOf(schema);
                if (vdb < 0)
                {
                    throw ErrAt("unknown database " + schema, vs.Schema!);
                }
            }
        }

        if (vs.Into is not null)
        {
            int file = CompileNameExpr(vs.Into);
            Emit(Op.Transaction, 0, 0, 0, null, 0);
            Emit(Op.Vacuum, vdb, file);
            Emit(Op.Halt);
            VmProgram ip = DdlProgram();
            ip.ExtraReadMask = vdb >= 2 ? 1 << vdb : 0;
            return ip;
        }

        Emit(Op.Transaction, 0, 0, 0, null, 1);
        Emit(Op.Vacuum, vdb);
        Emit(Op.Halt);
        VmProgram vp = DdlProgram();
        vp.ExtraWriteMask = vdb >= 2 ? 1 << vdb : 0;
        return vp;
    }

    // PRAGMA optimize(MASK) (pragma): runs ANALYZE on the tables that are likely to gain from it. Per the documented rule, a table qualifies when MASK has
    // 0x02 and it is an ordinary table whose name does not start with sqlite_ and (an index of it lacks a sqlite_stat1 row, or (it was queried through an
    // index on this connection or MASK has 0x10000) and its row count differs from the analysed one by about tenfold). Observed with the pinned shell: a
    // table that has indexes but no rows never qualifies; a table with no index qualifies only through the size rule, measured against a default estimate
    // of about a million rows when it has no statistics row. Bit 0x01 lists the ANALYZE commands instead of running them. The decisions are all taken
    // before the first ANALYZE runs, so one table's analysis never changes another's.
    private VmProgram CompileOptimize(PragmaStatement ps)
    {
        long mask = 0xfffe;
        if (ps.Value is { } mv)
        {
            mask = ParseMask(mv.Text);
        }

        int slot = SlotOfSchema(ps.Schema);
        var tables = new List<TableInfo>();
        if (slot == 0 && (mask & 2) != 0)
        {
            tables.AddRange(TablesOf(0).Where(Analyzable).Reverse());
        }

        TableInfo? stat = _cat.FindIn("main", "sqlite_stat1");
        bool debug = (mask & 1) != 0;
        var picked = new List<(TableInfo T, int Reg)>();
        var asked = new List<(TableInfo T, string Sql)>();
        foreach (TableInfo t in tables)
        {
            string? sql = OptimizeDecisionSql(t, stat is not null, (mask & 0x10000) != 0 || (_cat.Owner?.StatsUsed.Contains(t.Name) ?? false));
            if (sql is not null)
            {
                asked.Add((t, sql));
            }
        }

        Emit(Op.Transaction, 0, 0, 0, null, 0);
        int first = asked.Count > 0 ? NewRegs(asked.Count) : 0;
        for (int i = 0; i < asked.Count; i++)
        {
            Emit(Op.FkRun, first + i, 0, 0, MakeFkSpec(asked[i].Sql, true), 0);
            picked.Add((asked[i].T, first + i));
        }

        int end = NewLabel();
        int b = NewReg();
        if (debug)
        {
            foreach ((TableInfo t, int d) in picked)
            {
                int skip = NewLabel();
                Emit(Op.IfNot, d, skip);
                Emit(Op.String8, 0, b, 0, Utf8("ANALYZE \"main\"." + Q(t.Name)));
                Emit(Op.ResultRow, b, 1);
                Mark(skip);
            }

            Emit(Op.Halt);
            return new VmProgram { Code = Finish(), RegisterCount = _nReg + 1, ColumnNames = ["optimize"], ResultBase = b, ResultCount = 1, ParameterNames = [], Encoding = _enc };
        }

        if (picked.Count > 0)
        {
            // Any table to analyse: only now take the write lock, as the reference's ANALYZE commands do.
            int any = NewReg();
            Emit(Op.Integer, 0, any, 0, 0L);
            foreach ((TableInfo _, int d) in picked)
            {
                int no = NewLabel();
                Emit(Op.IfNot, d, no);
                Emit(Op.Integer, 0, any, 0, 1L);
                Mark(no);
            }

            Emit(Op.IfNot, any, end);
            Emit(Op.Transaction, 0, 0, 0, null, 1 | 4);
            List<TableInfo> list = picked.Select(x => x.T).ToList();
            var spec = new FkSpec { Sql = "ANALYZE", Cat = _cat, Enc = _enc, IsCount = false, Custom = c => c.CompileAnalyzeGuarded(list) };
            Emit(Op.FkRun, NewReg(), 0, picked[0].Reg, spec, picked.Count);
        }

        Mark(end);
        Emit(Op.Halt);
        return new VmProgram { Code = Finish(), RegisterCount = _nReg + 1, ColumnNames = [], ResultBase = b, ResultCount = 0, ParameterNames = [], Encoding = _enc };
    }

    // The argument of PRAGMA optimize as the reference reads an integer: decimal or 0x hex, a sign allowed; anything else is 0.
    private static long ParseMask(string text)
    {
        string t = text.Trim();
        bool neg = t.StartsWith('-');
        if (neg || t.StartsWith('+'))
        {
            t = t[1..];
        }

        long v = 0;
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 2; i < t.Length && Uri.IsHexDigit(t[i]) && v < (1L << 55); i++)
            {
                v = (v << 4) | (long)Convert.ToInt32(t[i].ToString(), 16);
            }
        }
        else
        {
            for (int i = 0; i < t.Length && t[i] is >= '0' and <= '9' && v < (1L << 55); i++)
            {
                v = (v * 10) + (t[i] - '0');
            }
        }

        return neg ? -v : v;
    }

    // The SELECT that answers 1 when PRAGMA optimize would analyse table t, or null when it never would. statExists: sqlite_stat1 exists now.
    private static string? OptimizeDecisionSql(TableInfo t, bool statExists, bool sizeCheck)
    {
        var all = new List<string>();
        var full = new List<string>();
        var idxs = new List<IndexInfo>(t.Indexes);
        if (t.WithoutRowid && t.PrimaryKey is { } pk)
        {
            idxs.Add(pk);
        }

        foreach (IndexInfo ix in idxs)
        {
            if (ix.Columns.Length == 0)
            {
                continue;
            }

            string name = ReferenceEquals(ix, t.PrimaryKey) ? t.Name : ix.Name;
            all.Add(QuoteLiteral(name));
            if (ix.Where is null)
            {
                full.Add(QuoteLiteral(name));
            }
        }

        string tq = QuoteLiteral(t.Name);
        string est = !statExists ? "NULL"
            : "(SELECT CAST(substr(stat, 1, instr(stat || ' ', ' ') - 1) AS INTEGER) FROM sqlite_stat1 WHERE tbl = " + tq + " AND (idx IS NULL"
              + (full.Count > 0 ? " OR idx IN (" + string.Join(", ", full) + ")" : "") + ") ORDER BY rowid LIMIT 1)";
        string tenfold = "abs(log2(max(n, 1) * 1.0) - log2(coalesce(max(e, 1), 1048576))) * 10 >= 33";
        string cond;
        if (all.Count == 0)
        {
            if (!sizeCheck)
            {
                return null;
            }

            cond = tenfold;
        }
        else
        {
            string lacking = statExists
                ? "(SELECT count(*) FROM sqlite_stat1 WHERE tbl = " + tq + " AND idx IN (" + string.Join(", ", all) + ")) < " + all.Count
                : "1";
            // an emptied table whose statistics remain still qualifies by size; one that never had statistics does not
            cond = "(n > 0 AND " + lacking + ")" + (sizeCheck && statExists ? " OR (e IS NOT NULL AND " + tenfold + ")" : "");
        }

        return "SELECT CASE WHEN " + cond + " THEN 1 ELSE 0 END FROM (SELECT count(*) AS n, " + est + " AS e FROM main." + Q(t.Name) + ")";
    }

    // PRAGMA incremental_vacuum(N) (pragma): a write transaction, then up to N pages leave the freelist, one result row without columns for each page freed.
    // N below 1, missing or not a number means the whole freelist. The temp database has no auto-vacuum, so there is nothing to do there.
    private VmProgram CompileIncrementalVacuum(PragmaStatement ps)
    {
        int slot = SlotOfSchema(ps.Schema);
        long n = 0;
        if (ps.Value is { } v)
        {
            string t = v.Text.Trim();
            int i = 0;
            bool neg = false;
            if (i < t.Length && (t[i] == '-' || t[i] == '+'))
            {
                neg = t[i] == '-';
                i++;
            }

            for (; i < t.Length && t[i] is >= '0' and <= '9' && n < int.MaxValue; i++)
            {
                n = (n * 10) + (t[i] - '0');
            }

            if (neg)
            {
                n = 0;
            }
        }

        if (slot == 1)
        {
            Emit(Op.Halt);
            return new VmProgram { Code = Finish(), RegisterCount = _nReg + 1, ColumnNames = [], ParameterNames = [], Encoding = _enc };
        }

        Emit(Op.Transaction, 0, 0, 0, null, 1 | 4);
        int cnt = NewReg();
        Emit(Op.IncrVacuum, slot, cnt, 0, n);
        int again = NewLabel(), body = NewLabel(), end = NewLabel();
        Mark(again);
        Emit(Op.IfPos, cnt, body, 1);
        Emit(Op.Goto, 0, end);
        Mark(body);
        Emit(Op.ResultRow, cnt, 0);
        Emit(Op.Goto, 0, again);
        Mark(end);
        Emit(Op.Halt);
        return new VmProgram { Code = Finish(), RegisterCount = _nReg + 1, ColumnNames = [], ResultBase = cnt, ResultCount = 0, ParameterNames = [], Encoding = _enc };
    }

    // The schema table itself (under either of its names) is listed by the catalog but is the hash order's own first entry.
    private static bool IsSchemaTable(TableInfo t)
        => t.Name.Equals("sqlite_master", StringComparison.OrdinalIgnoreCase) || t.Name.Equals("sqlite_schema", StringComparison.OrdinalIgnoreCase);

    private static bool Analyzable(TableInfo t)
        => t.View is null && t.Unsupported is null && !t.Name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase);

    private static string QuoteLiteral(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";

    // One row of sqlite_stat1 for an index: "rows avg1 avg2 ...", avgK being the average number of rows per distinct value of the first K key columns.
    // Observed against the pinned shell: the average rounds up, except that 2 becomes 1 when the rows are at most 1.1 times the distinct values; a
    // partial index with no rows still gets a row of zeros even when its table is empty; a full index on a table with no rows gets none. Key expressions and the index's
    // collations decide what is distinct.
    private static string StatSql(TableInfo t, IndexInfo ix, string tq, string where)
    {
        var sb = new System.Text.StringBuilder(where.Length > 0 ? "SELECT CASE WHEN 1 THEN n" : "SELECT CASE WHEN tn > 0 THEN n");
        var keys = new List<string>();
        for (int k = 0; k < ix.Columns.Length; k++)
        {
            string key = ix.Columns[k] >= 0 ? Q(t.Columns[ix.Columns[k]].Name) : "(" + SqlWriter.Write(ix.Terms[k]!) + ")";
            if (ix.Collations[k] is { } coll && !AsciiComparer.Instance.Equals(coll, "BINARY"))
            {
                key += " COLLATE " + coll;
            }

            keys.Add(key);
            string avg = "((n + d" + (k + 1) + " - 1) / d" + (k + 1) + ")";
            sb.Append(" || ' ' || CASE WHEN n = 0 THEN 0 WHEN ").Append(avg).Append(" = 2 AND n * 10 <= d").Append(k + 1).Append(" * 11 THEN 1 ELSE ").Append(avg).Append(" END");
        }

        sb.Append(" END FROM (SELECT (SELECT count(*) FROM ").Append(tq).Append(") AS tn, (SELECT count(*) FROM ").Append(tq).Append(where).Append(") AS n");
        for (int k = 0; k < keys.Count; k++)
        {
            // Observed: a UNIQUE index of one descending column counts every row as distinct, NULLs included, as long as its first entry (the
            // largest value) is not NULL; with nothing but NULLs it counts them as equal (an ascending one, or one with more columns, always
            // treats NULLs as equal like any other index).
            if (ix.Unique && ix.Columns.Length == 1 && ix.Desc.Length == 1 && ix.Desc[0])
            {
                string nonNull = where.Length > 0 ? where + " AND " + keys[0] + " IS NOT NULL" : " WHERE " + keys[0] + " IS NOT NULL";
                sb.Append(", CASE WHEN EXISTS (SELECT 1 FROM ").Append(tq).Append(nonNull).Append(") THEN (SELECT count(*) FROM ").Append(tq).Append(where)
                    .Append(") ELSE (SELECT count(*) FROM (SELECT DISTINCT ").Append(keys[0]).Append(" FROM ").Append(tq).Append(where).Append(")) END AS d1");
            }
            else
            {
                sb.Append(", (SELECT count(*) FROM (SELECT DISTINCT ").Append(string.Join(", ", keys.Take(k + 1))).Append(" FROM ").Append(tq).Append(where).Append(")) AS d").Append(k + 1);
            }
        }

        return sb.Append(')').ToString();
    }
}
