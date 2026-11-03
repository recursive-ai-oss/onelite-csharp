using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RecursiveAI.OneLite.Cli;

internal sealed partial class Shell
{
    private IEnumerable<Cell[]> QueryCells(string sql)
    {
        Connection? c = Conn();
        if (c is null)
        {
            yield break;
        }

        using Statement st = c.Prepare(sql);
        while (st.Step())
        {
            var cells = new Cell[st.ColumnCount];
            for (int i = 0; i < cells.Length; i++)
            {
                ColumnType t = st.ColumnType(i);
                cells[i] = t == ColumnType.Null ? new Cell(null, t) : new Cell(st.GetTextUtf8(i).ToArray(), t);
            }

            yield return cells;
        }
    }

    private static string Str(Cell c) => c.Data is null ? string.Empty : Encoding.UTF8.GetString(c.Data);

    private bool DotDump(string[] a)
    {
        bool nosys = false, dataOnly = false, rowids = false;
        bool schemaOnly = false;
        var pats = new List<string>();
        foreach (string s in a)
        {
            switch (s)
            {
                case "--nosys":
                    nosys = true;
                    break;
                case "--data-only":
                    dataOnly = true;
                    break;
                case "--preserve-rowids":
                    rowids = true;
                    break;
                case "--newlines":
                    break;
                default:
                    if (s.StartsWith('-'))
                    {
                        return OptionError(Array.IndexOf(a, s) + 1, "unknown option");
                    }

                    pats.Add(s);
                    break;
            }
        }

        if (Conn() is null)
        {
            return false;
        }

        try
        {
            DumpOne(pats, nosys, dataOnly, schemaOnly, rowids);
        }
        catch (OneLiteException e)
        {
            return DotFail(e.Message);
        }

        return true;
    }

    private static string Like(string pat) => " LIKE " + SqlText.QuoteString(pat) + " ESCAPE '\\'";

    private void DumpOne(List<string> pats, bool nosys, bool dataOnly, bool schemaOnly, bool rowids)
    {
        string where = pats.Count == 0 ? "1" : string.Join(" OR ", pats.Select(p => "name LIKE " + SqlText.QuoteString(p)));
        bool anyVirtual = QueryCells($"SELECT 1 FROM sqlite_schema AS o WHERE ({where}) AND type='table' AND sql LIKE 'CREATE VIRTUAL TABLE%' LIMIT 1").Any();
        if (anyVirtual)
        {
            _out.Write("/* WARNING: Script requires that SQLITE_DBCONFIG_DEFENSIVE be disabled */\n");
        }

        if (!dataOnly)
        {
            _out.Write("PRAGMA foreign_keys=OFF;\nBEGIN TRANSACTION;\n");
        }

        bool writable = false;

        var tables = QueryCells($"SELECT name, type, sql FROM sqlite_schema AS o WHERE ({where}) AND type=='table' AND sql NOT NULL ORDER BY tbl_name='sqlite_sequence', rowid").ToList();
        foreach (Cell[] t in tables)
        {
            string name = Str(t[0]);
            string sql = Str(t[2]);
            bool isSys = name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase);
            if (name == "sqlite_sequence")
            {
                if (nosys)
                {
                    continue;
                }

                if (!writable)
                {
                    _out.Write("PRAGMA writable_schema=ON;\n");
                    writable = true;
                }

                _out.Write("CREATE TABLE IF NOT EXISTS sqlite_sequence(name,seq);\nDELETE FROM sqlite_sequence;\n");
            }
            else if (name.StartsWith("sqlite_stat", StringComparison.OrdinalIgnoreCase))
            {
                if (nosys)
                {
                    continue;
                }

                if (!dataOnly && name == "sqlite_stat1")
                {
                    _out.Write("ANALYZE sqlite_schema;\n");
                }
            }
            else if (isSys)
            {
                continue;
            }
            else if (sql.StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase))
            {
                if (!dataOnly)
                {
                    if (!writable)
                    {
                        _out.Write("PRAGMA writable_schema=ON;\n");
                        writable = true;
                    }

                    _out.Write("INSERT INTO sqlite_schema(type,name,tbl_name,rootpage,sql)VALUES('table'," + SqlText.QuoteString(name) + "," + SqlText.QuoteString(name) + ",0," + Encoding.UTF8.GetString(Renderer.QuoteText(Encoding.UTF8.GetBytes(sql))) + ");\n");
                }

                continue;
            }
            else if (!dataOnly)
            {
                _out.Write(CreateWithIfNotExists(sql, name) + ";\n");
            }

            if (schemaOnly)
            {
                continue;
            }

            DumpRows(name, rowids);
        }

        {
            if (!dataOnly)
            {
                var others = QueryCells($"SELECT sql FROM sqlite_schema AS o WHERE ({where}) AND sql NOT NULL AND type IN ('index','trigger','view') ORDER BY CASE type WHEN 'view' THEN 0 WHEN 'trigger' THEN 1 ELSE 2 END, rowid").ToList();
                foreach (Cell[] o in others)
                {
                    _out.Write(Str(o[0]) + ";\n");
                }
            }
        }

        if (writable)
        {
            _out.Write("PRAGMA writable_schema=OFF;\n");
        }

        if (!dataOnly)
        {
            _out.Write("COMMIT;\n");
        }
    }

    /// <summary>A table whose stored name is quoted, or one the engine creates by itself, is dumped with IF NOT EXISTS so loading it twice is harmless.</summary>
    private static string CreateWithIfNotExists(string sql, string name)
    {
        const string prefix = "CREATE TABLE ";
        if (sql.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            string after = sql[prefix.Length..];
            if (after.StartsWith('"') || after.StartsWith('\''))
            {
                return prefix + "IF NOT EXISTS " + after;
            }
        }

        _ = name;
        return sql;
    }

    private void DumpRows(string table, bool preserveRowids)
    {
        List<string?[]> cols = Query($"PRAGMA table_info({SqlText.AlwaysQuoteIdent(table)})");
        if (cols.Count == 0)
        {
            return;
        }

        var names = cols.Select(c => c[1]!).ToList();
        string select = string.Join(",", names.Select(SqlText.AlwaysQuoteIdent));
        string head = "INSERT INTO " + SqlText.QuoteIdent(table);
        if (preserveRowids)
        {
            bool aliasesRowid = cols.Count(c => c[5] != "0") == 1 && cols.First(c => c[5] != "0")[2]?.Equals("INTEGER", StringComparison.OrdinalIgnoreCase) == true;
            if (!aliasesRowid && QueryCells($"SELECT 1 FROM sqlite_schema WHERE name={SqlText.QuoteString(table)} AND sql NOT LIKE '%WITHOUT ROWID%'").Any())
            {
                select = "rowid," + select;
                names.Insert(0, "rowid");
            }

            head += "(" + string.Join(",", names.Select(SqlText.QuoteIdent)) + ")";
        }

        head += " VALUES(";
        foreach (Cell[] row in QueryCells($"SELECT {select} FROM {SqlText.AlwaysQuoteIdent(table)}"))
        {
            _out.Write(head);
            for (int i = 0; i < row.Length; i++)
            {
                _out.Write(i > 0 ? "," : string.Empty);
                _out.Write(Renderer.QuoteCell(row[i]));
            }

            _out.Write(");\n");
        }
    }
}
