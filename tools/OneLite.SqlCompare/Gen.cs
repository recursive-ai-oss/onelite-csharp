using System;
using System.Collections.Generic;
using System.Text;

namespace RecursiveAI.OneLite.SqlCompare;

/// <summary>
/// A grammar-driven statement generator written from the syntax diagrams of the lang_* pages. Statements are built as token lists so that
/// the mutation step (drop, duplicate, swap, replace, insert, truncate a token) can produce near-valid text that exercises the error paths.
/// The same seed always gives the same statements.
/// </summary>
internal sealed class Gen
{
    private readonly Random _r;
    private readonly List<string> _o = [];
    private bool _inTrigger;

    internal Gen(int seed) => _r = new Random(seed);

    private static readonly string[] Plain = ["a", "b", "c", "t", "u", "v", "x1", "id", "name", "_z", "Tbl", "col2"];
    // Words that the dialect allows as identifiers although they are keywords (lang_keywords: fallback words), and a few that it does not.
    private static readonly string[] Fallbacks = ["key", "temp", "first", "last", "action", "after", "begin", "cast", "cross", "database", "end", "filter", "full", "glob", "if", "indexed", "left", "match", "no", "nulls", "of", "over", "partition", "plan", "query", "range", "raise", "recursive", "release", "replace", "right", "row", "rows", "savepoint", "temporary", "ties", "trigger", "vacuum", "view", "virtual", "with", "without", "window", "current", "following", "preceding", "unbounded", "groups", "exclude", "others", "always", "generated", "materialized", "do", "nothing", "abort", "fail", "ignore", "rename", "reindex", "analyze", "attach", "detach", "explain", "pragma", "strict"];
    private static readonly string[] Reserved = ["select", "from", "where", "table", "index", "order", "group", "by", "insert", "into", "values", "set", "and", "or", "not", "null", "case", "when", "then", "else", "as", "on", "using", "join", "create", "drop", "alter", "update", "delete", "primary", "foreign", "references", "unique", "check", "default", "constraint", "collate", "between", "in", "is", "like", "limit", "having", "distinct", "union", "all", "except", "intersect", "exists", "escape", "returning"];
    private static readonly string[] Types = ["integer", "int", "text", "real", "blob", "numeric", "varchar", "double precision", "unsigned big int", "character", "any", "float", "boolean", "datetime", "native character", "generated", "a b c"];
    private static readonly string[] Funcs = ["abs", "count", "sum", "max", "min", "coalesce", "ifnull", "length", "lower", "upper", "substr", "random", "json_extract", "group_concat", "total", "avg", "iif", "typeof", "likely", "f", "row_number", "rank", "lag", "lead", "ntile", "nth_value", "first_value", "date", "strftime", "printf", "replace", "glob", "like", "if"];
    private static readonly string[] BinOps = ["+", "-", "*", "/", "%", "||", "&", "|", "<<", ">>", "<", "<=", ">", ">=", "=", "==", "!=", "<>", "AND", "OR", "->", "->>", "IS", "IS NOT", "IS DISTINCT FROM", "IS NOT DISTINCT FROM"];
    private static readonly string[] Noise = ["(", ")", ",", ";", ".", "*", "=", "select", "from", "where", "'x'", "1", "?", "null", "not", "as", "by", "on", "and", "or", "values", "into", "(", ")", "case", "end", "end", "begin", "select", "in", "is", "like", "between", "over", "filter", "'", "\"", "1.5e", "0x", "x'1", "$", ":", "@", "/*", "--", "!", "~", "||", "<", "+", "-", "[", "]", "`", "union", "all", "limit", "order", "group", "having", "distinct", "primary", "key", "default", "constraint", "unique", "check", "references", "collate", "returning", "do", "nothing", "update", "set", "table", "index", "view", "trigger", "if", "exists", "temp", "to", "add", "column", "rename", "drop", "create"];

    private bool P(int pct) => _r.Next(100) < pct;
    private T Pick<T>(T[] a) => a[_r.Next(a.Length)];
    private void W(string s) => _o.Add(s);
    private void W(params string[] s) => _o.AddRange(s);

    // ---- names ----
    private string Ident()
    {
        int k = _r.Next(100);
        string baseName = k < 55 ? Pick(Plain) : k < 75 ? Pick(Fallbacks) : k < 80 ? Pick(Reserved) : Pick(Plain) + _r.Next(10);
        if (k >= 55 && k < 75 || k >= 80)
        {
            return P(15) ? Quote(baseName) : baseName;
        }

        if (k >= 75 && k < 80)
        {
            return Quote(baseName);
        }

        return P(12) ? Quote(baseName) : baseName;
    }

    private string Quote(string s) => _r.Next(5) switch
    {
        0 => "\"" + s + "\"",
        1 => "[" + s + "]",
        2 => "`" + s + "`",
        3 => "\"" + s + "\"\"" + "q\"",
        _ => "'" + s + "'",
    };

    private string Safe() => P(8) ? Quote(Pick(Plain)) : Pick(Plain);   // never a keyword: used where a stray keyword would just make noise
    private void QName() { if (P(15)) { W(Pick(["main", "temp", "aux"]), "."); } W(Ident()); }

    // ---- expressions ----
    private void Literal()
    {
        switch (_r.Next(14))
        {
            case 0: W(_r.Next(1000).ToString()); break;
            case 1: W(_r.Next(100) + "." + _r.Next(100)); break;
            case 2: W("0x" + _r.Next(65536).ToString("x")); break;
            case 3: W(_r.Next(10) + "e" + Pick(["", "+", "-"]) + _r.Next(20)); break;
            case 4: W("'" + Pick(["", "abc", "it''s", "a b", "é"]) + "'"); break;
            case 5: W("x'" + Pick(["", "ab", "00ff", "ABCD"]) + "'"); break;
            case 6: W("NULL"); break;
            case 7: W(Pick(["TRUE", "FALSE"])); break;
            case 8: W(Pick(["CURRENT_TIME", "CURRENT_DATE", "CURRENT_TIMESTAMP"])); break;
            case 9: W(Pick(["1_000", "1_0.5", "9223372036854775807", "9223372036854775808", "18446744073709551616", ".5", "5."])); break;
            case 10: W(Pick(["?", "?1", "?12", ":a", "@b", "$c", "$c::d", "$c(e)", "?32766"])); break;
            case 11: W(_r.Next(1000).ToString()); break;
            case 12: W("E'x'"); break;
            default: W(_r.Next(50).ToString()); break;
        }
    }

    private void ColRef()
    {
        int k = _r.Next(10);
        if (k < 6) { W(Ident()); }
        else if (k < 9) { W(Ident(), ".", Ident()); }
        else { W(Ident(), ".", Ident(), ".", Ident()); }
    }

    private void Expr(int d)
    {
        if (d <= 0 || P(20))
        {
            if (P(45)) { ColRef(); } else { Literal(); }
            return;
        }

        switch (_r.Next(36))
        {
            case 0: case 1: case 2: case 3: case 4:
                Expr(d - 1); W(Pick(BinOps)); Expr(d - 1); break;
            case 5: W(Pick(["-", "+", "~", "NOT"])); Expr(d - 1); break;
            case 6: W("("); Expr(d - 1); W(")"); break;
            case 7: Expr(d - 1); W("COLLATE", Ident()); break;
            case 8: W("CAST", "("); Expr(d - 1); W("AS"); TypeName(); W(")"); break;
            case 9: Expr(d - 1); if (P(40)) { W("NOT"); } W("BETWEEN"); Expr(d - 1); W("AND"); Expr(d - 1); break;
            case 10:
                Expr(d - 1); if (P(40)) { W("NOT"); } W("IN", "(");
                int n = _r.Next(4);
                for (int i = 0; i < n; i++) { if (i > 0) { W(","); } Expr(d - 1); }
                W(")"); break;
            case 11: Expr(d - 1); if (P(40)) { W("NOT"); } W("IN", "("); Select(d - 2, false); W(")"); break;
            case 12: Expr(d - 1); if (P(40)) { W("NOT"); } W("IN"); QName(); if (P(30)) { W("("); Args(d - 1); W(")"); } break;
            case 13: if (P(30)) { W("NOT"); } W("EXISTS", "("); Select(d - 2, false); W(")"); break;
            case 14: W("("); Select(d - 2, false); W(")"); break;
            case 15: Case(d); break;
            case 16: case 17: case 18: Call(d); break;
            case 19: Expr(d - 1); W(Pick(["ISNULL", "NOTNULL", "NOT NULL"])); break;
            case 20: case 21:
                Expr(d - 1); if (P(35)) { W("NOT"); } W(Pick(["LIKE", "GLOB", "REGEXP", "MATCH"])); Expr(d - 1);
                if (P(25)) { W("ESCAPE"); Expr(d - 1); }
                break;
            case 22: W("("); Expr(d - 1); W(","); Expr(d - 1); if (P(30)) { W(","); Expr(d - 1); } W(")"); break;
            case 23: W("RAISE", "("); W(Pick(["IGNORE", "ROLLBACK, 'm'", "ABORT, 'm'", "FAIL, 'm'", "BAD"])); W(")"); break;
            case 24: Expr(d - 1); W("IS"); if (P(50)) { W("NOT"); } Expr(d - 1); break;
            case 25: W("NOT"); Expr(d - 1); W(Pick(["=", "AND", "OR", "IN (1)", "LIKE 'a'", "BETWEEN 1 AND 2"])); Expr(d - 1); break;
            case 26: Expr(d - 1); W("COLLATE", Ident(), Pick(["+", "=", "AND"])); Expr(d - 1); break;
            case 27: W("-", "-", "(");  Expr(d - 1); W(")"); break;
            case 28: Expr(d - 1); W("NOT", "NULL", Pick(["AND", "OR", "="])); Expr(d - 1); break;
            case 29: Expr(d - 1); if (P(50)) { W("NOT"); } W("IN", "()"); break;
            case 30: W("(", "SELECT"); Expr(d - 1); W(")"); break;
            case 31: W("CASE"); Expr(d - 1); W("END"); break;
            case 32: ColRef(); W(Pick(["->", "->>"])); Literal(); break;
            default: Literal(); break;
        }
    }

    private void Case(int d)
    {
        W("CASE");
        if (P(40)) { Expr(d - 1); }
        int n = 1 + _r.Next(3);
        for (int i = 0; i < n; i++) { W("WHEN"); Expr(d - 1); W("THEN"); Expr(d - 1); }
        if (P(50)) { W("ELSE"); Expr(d - 1); }
        W("END");
    }

    private void Args(int d)
    {
        int n = _r.Next(4);
        for (int i = 0; i < n; i++) { if (i > 0) { W(","); } Expr(d - 1); }
    }

    private void Call(int d)
    {
        W(P(85) ? Pick(Funcs) : Ident(), "(");
        switch (_r.Next(8))
        {
            case 0: W("*"); break;
            case 1: W("DISTINCT"); Expr(d - 1); break;
            case 2: W("ALL"); Expr(d - 1); break;
            case 3: Expr(d - 1); if (P(60)) { W("ORDER", "BY"); OrderTerms(d - 1); } break;
            default: Args(d); break;
        }

        W(")");
        if (P(25)) { W("FILTER", "(", "WHERE"); Expr(d - 1); W(")"); }
        if (P(30))
        {
            W("OVER");
            if (P(35)) { W(Ident()); } else { W("("); WindowBody(d - 1); W(")"); }
        }
    }

    private void TypeName()
    {
        W(Pick(Types));
        if (P(30)) { W("(", _r.Next(100).ToString()); if (P(40)) { W(",", Pick(["-5", "+3", "7"])); } W(")"); }
    }

    private void OrderTerms(int d)
    {
        int n = 1 + _r.Next(3);
        for (int i = 0; i < n; i++)
        {
            if (i > 0) { W(","); }
            Expr(d);
            if (P(10)) { W("COLLATE", Ident()); }
            if (P(30)) { W(Pick(["ASC", "DESC"])); }
            if (P(15)) { W("NULLS", Pick(["FIRST", "LAST"])); }
        }
    }

    private void WindowBody(int d)
    {
        if (P(20)) { W(Ident()); }
        if (P(40)) { W("PARTITION", "BY"); Args(d); if (_o[^1] == "BY") { W("1"); } }
        if (P(40)) { W("ORDER", "BY"); OrderTerms(d); }
        if (P(50))
        {
            W(Pick(["ROWS", "RANGE", "GROUPS"]));
            if (P(60)) { W("BETWEEN"); Bound(d); W("AND"); Bound(d); } else { Bound(d); }
            if (P(30)) { W("EXCLUDE", Pick(["NO OTHERS", "CURRENT ROW", "GROUP", "TIES"])); }
        }
    }

    private void Bound(int d)
    {
        switch (_r.Next(5))
        {
            case 0: W("UNBOUNDED", "PRECEDING"); break;
            case 1: W("UNBOUNDED", "FOLLOWING"); break;
            case 2: W("CURRENT", "ROW"); break;
            case 3: Expr(d - 1); W("PRECEDING"); break;
            default: Expr(d - 1); W("FOLLOWING"); break;
        }
    }

    // ---- select ----
    private void With(int d)
    {
        W("WITH"); if (P(25)) { W("RECURSIVE"); }
        int n = 1 + _r.Next(2);
        for (int i = 0; i < n; i++)
        {
            if (i > 0) { W(","); }
            W(Ident());
            if (P(40)) { W("("); Cols(); W(")"); }
            W("AS");
            if (P(30)) { W(Pick(["MATERIALIZED", "NOT MATERIALIZED"])); }
            W("("); Select(d - 1, false); W(")");
        }
    }

    private void Cols() { int n = 1 + _r.Next(3); for (int i = 0; i < n; i++) { if (i > 0) { W(","); } W(Ident()); } }

    private void Select(int d, bool allowWith = true)
    {
        if (allowWith && P(12)) { With(d); }
        int cores = P(75) ? 1 : 2 + _r.Next(3);
        for (int i = 0; i < cores; i++)
        {
            if (i > 0) { W(Pick(["UNION", "UNION ALL", "INTERSECT", "EXCEPT"])); }
            if (P(10)) { Values(d); } else { SimpleSelect(d); }
            if ((i == cores - 1 || P(10)) && P(40) && _o[^1] != ")") { }
        }

        if (P(25) && cores > 0) { W("ORDER", "BY"); OrderTerms(d - 1); }
        if (P(20)) { W("LIMIT"); Expr(d - 1); if (P(50)) { W(Pick(["OFFSET", ","])); Expr(d - 1); } }
    }

    private void Values(int d)
    {
        W("VALUES");
        int rows = 1 + _r.Next(3), cols = 1 + _r.Next(3);
        for (int i = 0; i < rows; i++)
        {
            if (i > 0) { W(","); }
            W("(");
            for (int j = 0; j < cols; j++) { if (j > 0) { W(","); } Expr(d - 1); }
            W(")");
        }
    }

    private void SimpleSelect(int d)
    {
        W("SELECT");
        if (P(15)) { W(Pick(["DISTINCT", "ALL"])); }
        int n = 1 + _r.Next(3);
        for (int i = 0; i < n; i++)
        {
            if (i > 0) { W(","); }
            if (P(15)) { W("*"); }
            else if (P(8)) { W(Ident(), ".", "*"); }
            else
            {
                Expr(d - 1);
                if (P(30)) { if (P(60)) { W("AS"); } W(P(15) ? Pick(Fallbacks) : Safe()); }
            }
        }

        if (P(70)) { From(d); }
        if (P(40)) { W("WHERE"); Expr(d - 1); }
        if (P(15)) { W("GROUP", "BY"); Args(d - 1); if (_o[^1] == "BY") { W("1"); } if (P(40)) { W("HAVING"); Expr(d - 1); } }
        if (P(8)) { W("WINDOW"); int k = 1 + _r.Next(2); for (int i = 0; i < k; i++) { if (i > 0) { W(","); } W(Ident(), "AS", "("); WindowBody(d - 1); W(")"); } }
    }

    private void From(int d)
    {
        W("FROM");
        FromItems(d, 0);
    }

    private void FromItems(int d, int depth)
    {
        TableSourceItem(d, depth);
        int n = _r.Next(3);
        for (int i = 0; i < n; i++)
        {
            if (P(25)) { W(","); }
            else
            {
                switch (_r.Next(6))
                {
                    case 0: W("JOIN"); break;
                    case 1: W("LEFT", "JOIN"); break;
                    case 2: W(Pick(["LEFT OUTER", "INNER", "CROSS", "NATURAL", "NATURAL LEFT", "NATURAL LEFT OUTER", "RIGHT", "FULL", "FULL OUTER", "RIGHT OUTER", "NATURAL INNER", "LEFT INNER", "LEFT CROSS"]), "JOIN"); break;
                    case 3: W(Pick(["NATURAL", "INNER", "LEFT", "OUTER", "CROSS"]), Pick(["NATURAL", "JOIN", "LEFT", "OUTER"]), "JOIN"); break;
                    default: W("JOIN"); break;
                }
            }

            TableSourceItem(d, depth);
            if (P(35)) { W("ON"); Expr(d - 1); }
            else if (P(15)) { W("USING", "("); Cols(); W(")"); }
        }
    }

    private void TableSourceItem(int d, int depth)
    {
        switch (_r.Next(10))
        {
            case 0: W("("); Select(d - 2); W(")"); Alias(); break;
            case 1: if (depth < 2) { W("("); FromItems(d, depth + 1); W(")"); Alias(); } else { W(Ident()); } break;
            case 2: QName(); W("("); Args(d - 1); W(")"); Alias(); break;
            default:
                QName(); Alias();
                if (P(10)) { W("INDEXED", "BY", Ident()); }
                else if (P(8)) { W("NOT", "INDEXED"); }
                break;
        }
    }

    private void Alias()
    {
        if (P(40)) { if (P(60)) { W("AS"); } W(P(10) ? Pick(Fallbacks) : Safe()); }
    }

    // ---- statements ----
    private void Returning(int d)
    {
        if (!P(25)) { return; }
        W("RETURNING");
        int n = 1 + _r.Next(2);
        for (int i = 0; i < n; i++)
        {
            if (i > 0) { W(","); }
            if (P(30)) { W("*"); } else { Expr(d - 1); if (P(30)) { W("AS", Safe()); } }
        }
    }

    private void Conflict() { if (P(25)) { W("OR", Pick(["ROLLBACK", "ABORT", "FAIL", "IGNORE", "REPLACE"])); } }

    private void Insert(int d)
    {
        if (P(10)) { With(d); }
        if (P(15)) { W("REPLACE"); } else { W("INSERT"); Conflict(); }
        W("INTO"); QName();
        if (P(15)) { W("AS", Safe()); }
        if (P(50)) { W("("); Cols(); W(")"); }
        if (P(10) && !_inTrigger) { W("DEFAULT", "VALUES"); }
        else if (P(35)) { Values(d); }
        else { Select(d, false); }
        int ups = P(25) ? 1 + _r.Next(2) : 0;
        for (int i = 0; i < ups; i++)
        {
            W("ON", "CONFLICT");
            if (P(60)) { W("("); OrderTerms(d - 2); W(")"); if (P(30)) { W("WHERE"); Expr(d - 1); } }
            W("DO");
            if (P(40)) { W("NOTHING"); }
            else { W("UPDATE", "SET"); SetList(d); if (P(30)) { W("WHERE"); Expr(d - 1); } }
        }

        if (!_inTrigger || P(30)) { Returning(d); }
    }

    private void SetList(int d)
    {
        int n = 1 + _r.Next(3);
        for (int i = 0; i < n; i++)
        {
            if (i > 0) { W(","); }
            if (P(15)) { W("("); Cols(); W(")"); } else { W(Ident()); }
            W("="); Expr(d - 1);
        }
    }

    private void Update(int d)
    {
        if (P(8) && !_inTrigger) { With(d); }
        W("UPDATE"); Conflict(); QName(); Alias();
        if (P(8)) { W("INDEXED", "BY", Ident()); }
        W("SET"); SetList(d);
        if (P(20)) { From(d); }
        if (P(50)) { W("WHERE"); Expr(d - 1); }
        if (!_inTrigger) { Returning(d); }
    }

    private void Delete(int d)
    {
        if (P(8) && !_inTrigger) { With(d); }
        W("DELETE", "FROM"); QName(); Alias();
        if (P(50)) { W("WHERE"); Expr(d - 1); }
        if (!_inTrigger) { Returning(d); }
    }

    private void ColConstraint(int d)
    {
        if (P(15)) { W("CONSTRAINT", Ident()); }
        switch (_r.Next(11))
        {
            case 0: W("PRIMARY", "KEY"); if (P(30)) { W(Pick(["ASC", "DESC"])); } OnConflict(); if (P(25)) { W("AUTOINCREMENT"); } break;
            case 1: W("NOT", "NULL"); OnConflict(); break;
            case 2: W("UNIQUE"); OnConflict(); break;
            case 3: W("CHECK", "("); Expr(d - 1); W(")"); break;
            case 4:
                W("DEFAULT");
                switch (_r.Next(6))
                {
                    case 0: W("("); Expr(d - 1); W(")"); break;
                    case 1: W(Pick(["-", "+"]), _r.Next(50).ToString()); break;
                    case 2: W("'s'"); break;
                    case 3: W(Pick(["NULL", "TRUE", "FALSE", "CURRENT_TIMESTAMP"])); break;
                    case 4: W(Pick(Fallbacks)); break;
                    default: Literal(); break;
                }

                break;
            case 5: W("COLLATE", Ident()); break;
            case 6: References(); break;
            case 7: if (P(60)) { W("GENERATED", "ALWAYS"); } W("AS", "("); Expr(d - 1); W(")"); if (P(50)) { W(Pick(["STORED", "VIRTUAL"])); } break;
            case 8: W("NULL"); break;
            default: W("REFERENCES", Ident()); break;
        }
    }

    private void OnConflict() { if (P(20)) { W("ON", "CONFLICT", Pick(["ROLLBACK", "ABORT", "FAIL", "IGNORE", "REPLACE"])); } }

    private void References()
    {
        W("REFERENCES", Ident());
        if (P(50)) { W("("); Cols(); W(")"); }
        int n = _r.Next(3);
        for (int i = 0; i < n; i++)
        {
            switch (_r.Next(4))
            {
                case 0: W("ON", Pick(["DELETE", "UPDATE"]), Pick(["SET NULL", "SET DEFAULT", "CASCADE", "RESTRICT", "NO ACTION"])); break;
                case 1: W("MATCH", Ident()); break;
                case 2: if (P(50)) { W("NOT"); } W("DEFERRABLE"); if (P(50)) { W("INITIALLY", Pick(["DEFERRED", "IMMEDIATE"])); } break;
                default: W("ON", Pick(["DELETE", "UPDATE"]), "SET", "NULL"); break;
            }
        }
    }

    private void ColumnDef(int d)
    {
        W(Ident());
        if (P(85)) { TypeName(); }
        int n = _r.Next(4);
        for (int i = 0; i < n; i++) { ColConstraint(d); }
    }

    private void TableConstraint(int d)
    {
        if (P(20)) { W("CONSTRAINT", Ident()); }
        switch (_r.Next(4))
        {
            case 0: W("PRIMARY", "KEY", "("); OrderTerms(0); W(")"); OnConflict(); break;
            case 1: W("UNIQUE", "("); OrderTerms(0); W(")"); OnConflict(); break;
            case 2: W("CHECK", "("); Expr(d - 1); W(")"); break;
            default: W("FOREIGN", "KEY", "("); Cols(); W(")"); References(); break;
        }
    }

    private void CreateTable(int d)
    {
        W("CREATE"); if (P(15)) { W(Pick(["TEMP", "TEMPORARY"])); }
        W("TABLE"); if (P(20)) { W("IF", "NOT", "EXISTS"); }
        QNameNew();
        if (P(15)) { W("AS"); Select(d, true); return; }
        W("(");
        int n = 1 + _r.Next(4);
        for (int i = 0; i < n; i++) { if (i > 0) { W(","); } ColumnDef(d); }
        int tc = P(35) ? 1 + _r.Next(2) : 0;
        for (int i = 0; i < tc; i++) { W(","); TableConstraint(d); }
        W(")");
        if (P(25)) { W(Pick(["WITHOUT ROWID", "STRICT", "WITHOUT ROWID, STRICT", "STRICT, WITHOUT ROWID", "WITHOUT", "STRICT,", "ROWID"])); }
    }

    // New object names use a unique suffix so that "already exists" never hides a later syntax error.
    private void QNameNew() { if (P(10)) { W(Pick(["main", "temp"]), "."); } W(Pick(Plain) + "_n" + _r.Next(1000000)); }

    private void CreateIndex(int d)
    {
        W("CREATE"); if (P(25)) { W("UNIQUE"); }
        W("INDEX"); if (P(20)) { W("IF", "NOT", "EXISTS"); }
        if (P(10)) { W(Pick(["main", "temp"]), "."); }
        W("ix_n" + _r.Next(1000000), "ON", Pick(["t", "u"]));
        W("("); OrderTerms(d - 2); W(")");
        if (P(25)) { W("WHERE"); Expr(d - 1); }
    }

    private void CreateView(int d)
    {
        W("CREATE"); if (P(20)) { W(Pick(["TEMP", "TEMPORARY"])); }
        W("VIEW"); if (P(20)) { W("IF", "NOT", "EXISTS"); }
        QNameNew();
        if (P(30)) { W("("); Cols(); W(")"); }
        W("AS"); Select(d, true);
    }

    private void CreateTrigger(int d)
    {
        W("CREATE"); if (P(20)) { W(Pick(["TEMP", "TEMPORARY"])); }
        W("TRIGGER"); if (P(20)) { W("IF", "NOT", "EXISTS"); }
        W("trg_n" + _r.Next(1000000));
        if (P(60)) { W(Pick(["BEFORE", "AFTER", "INSTEAD OF"])); }
        switch (_r.Next(3))
        {
            case 0: W("DELETE"); break;
            case 1: W("INSERT"); break;
            default: W("UPDATE"); if (P(40)) { W("OF"); Cols(); } break;
        }

        W("ON", Pick(["t", "u", "v"]));
        if (P(50)) { W("FOR", "EACH", "ROW"); }
        if (P(30)) { W("WHEN"); Expr(d - 1); }
        W("BEGIN");
        int n = _r.Next(4);
        _inTrigger = true;
        for (int i = 0; i < n; i++)
        {
            switch (_r.Next(4))
            {
                case 0: Insert(d - 1); break;
                case 1: Update(d - 1); break;
                case 2: Delete(d - 1); break;
                default: Select(d - 1, !P(70) && false); break;
            }

            W(";");
        }

        _inTrigger = false;
        W("END");
    }

    private void CreateVirtual()
    {
        W("CREATE", "VIRTUAL", "TABLE"); if (P(20)) { W("IF", "NOT", "EXISTS"); }
        QNameNew(); W("USING", Pick(["fts5", "rtree", "mod", "fts4", "x"]));
        if (P(70))
        {
            W("(");
            int n = _r.Next(4);
            for (int i = 0; i < n; i++)
            {
                if (i > 0) { W(","); }
                switch (_r.Next(5))
                {
                    case 0: W(Ident()); break;
                    case 1: W(Ident(), "=", "'x'"); break;
                    case 2: W(Ident(), "=", "1"); break;
                    case 3: W("'s'"); break;
                    default: W(Ident(), "(", "a", ",", "b", ")", Ident()); break;
                }
            }

            W(")");
        }
    }

    private void Drop()
    {
        W("DROP", Pick(["TABLE", "INDEX", "VIEW", "TRIGGER"]));
        if (P(40)) { W("IF", "EXISTS"); }
        QName();
    }

    private void Alter(int d)
    {
        W("ALTER", "TABLE"); QName();
        switch (_r.Next(11))
        {
            case 0: W("RENAME", "TO", Ident()); break;
            case 1: W("RENAME"); if (P(60)) { W("COLUMN"); } W(Ident(), "TO", Ident()); break;
            case 2: W("ADD"); if (P(60)) { W("COLUMN"); } ColumnDef(d); break;
            case 3: W("DROP"); if (P(60)) { W("COLUMN"); } W(Ident()); break;
            case 4: W("ADD"); if (P(50)) { W("CONSTRAINT", Ident()); } W("CHECK", "("); Expr(d - 1); W(")"); OnConflict(); break;
            case 5: W("DROP", "CONSTRAINT", Ident()); break;
            case 6: W("ALTER"); if (P(50)) { W("COLUMN"); } W(Ident(), "SET", "NOT", "NULL"); OnConflict(); break;
            case 7: W("ALTER"); if (P(50)) { W("COLUMN"); } W(Ident(), "DROP", "NOT", "NULL"); break;
            case 8: W("ADD", "PRIMARY", "KEY"); break;
            case 9: W("RENAME"); break;
            default: W(Pick(["ADD", "DROP", "RENAME", "SET"]), Ident()); break;
        }
    }

    private void Pragma()
    {
        W("PRAGMA"); if (P(25)) { W(Pick(["main", "temp"]), "."); }
        W(Pick(["journal_mode", "synchronous", "foreign_keys", "table_info", "user_version", "cache_size", "page_size", "wal_checkpoint", "integrity_check", "optimize", "x", "key"]));
        string[] vals = ["ON", "OFF", "NO", "FULL", "DELETE", "DEFAULT", "1", "-5", "+3", "'str'", "wal", "t", "\"t\"", "NORMAL", "1.5", "x'ab'", "null", "TRUE", "(", "a b"];
        switch (_r.Next(4))
        {
            case 0: W("=", Pick(vals)); break;
            case 1: W("(", Pick(vals), ")"); break;
            case 2: W("=", Pick(vals), Pick(vals)); break;
            default: break;
        }
    }

    private void Txn()
    {
        switch (_r.Next(8))
        {
            case 0: W("BEGIN"); if (P(50)) { W(Pick(["DEFERRED", "IMMEDIATE", "EXCLUSIVE"])); } if (P(40)) { W("TRANSACTION"); if (P(30)) { W(Ident()); } } break;
            case 1: W(Pick(["COMMIT", "END"])); if (P(40)) { W("TRANSACTION"); if (P(30)) { W(Ident()); } } break;
            case 2: W("ROLLBACK"); if (P(40)) { W("TRANSACTION"); } if (P(50)) { W("TO"); if (P(50)) { W("SAVEPOINT"); } W(Ident()); } break;
            case 3: W("SAVEPOINT", Ident()); break;
            case 4: W("RELEASE"); if (P(50)) { W("SAVEPOINT"); } W(Ident()); break;
            case 5: W("BEGIN", "CONCURRENT"); break;
            case 6: W("BEGIN", Pick(["DEFERRED", "IMMEDIATE", "EXCLUSIVE"]), "CONCURRENT"); break;
            default: W("BEGIN", "TRANSACTION", "TRANSACTION"); break;
        }
    }

    private void Misc(int d)
    {
        switch (_r.Next(8))
        {
            case 0: W("VACUUM"); if (P(40)) { W(Ident()); } if (P(40)) { W("INTO"); Expr(d - 2); } break;
            case 1: W("ANALYZE"); if (P(50)) { QName(); } break;
            case 2: W("REINDEX"); if (P(60)) { if (P(30)) { W(Pick(["main", "temp"]), "."); } W(Ident()); } break;
            case 3: W("ATTACH"); if (P(50)) { W("DATABASE"); } Expr(d - 2); W("AS"); Expr(d - 2); if (P(30)) { W("KEY"); Expr(d - 2); } break;
            case 4: W("DETACH"); if (P(50)) { W("DATABASE"); } Expr(d - 2); break;
            case 5: W("EXPLAIN"); if (P(50)) { W("QUERY", "PLAN"); } Statement(d, "explain-inner"); break;
            case 6: W("EXPLAIN"); if (P(50)) { W("QUERY", "PLAN"); } Select(d, true); break;
            default: W("VACUUM", "INTO", "'f'"); break;
        }
    }

    internal static readonly string[] Kinds = ["select", "insert", "update", "delete", "create-table", "create-index", "create-view", "create-trigger", "create-virtual", "drop", "alter", "pragma", "transaction", "misc"];

    private string _kind = "";

    /// <summary>The kind of the statement <see cref="Next"/> made last.</summary>
    internal string LastKind { get; private set; } = "";

    /// <summary>Whether that statement was mutated.</summary>
    internal bool LastMutated { get; private set; }

    private void Statement(int d, string? forced = null)
    {
        string k = forced ?? Kinds[_r.Next(Kinds.Length)];
        if (forced == null) { _kind = k; }
        switch (k)
        {
            case "select": Select(d); break;
            case "insert": Insert(d); break;
            case "update": Update(d); break;
            case "delete": Delete(d); break;
            case "create-table": CreateTable(d); break;
            case "create-index": CreateIndex(d); break;
            case "create-view": CreateView(d); break;
            case "create-trigger": CreateTrigger(d); break;
            case "create-virtual": CreateVirtual(); break;
            case "drop": Drop(); break;
            case "alter": Alter(d); break;
            case "pragma": Pragma(); break;
            case "transaction": Txn(); break;
            case "explain-inner":
                switch (_r.Next(5))
                {
                    case 0: Insert(d - 1); break;
                    case 1: Update(d - 1); break;
                    case 2: Delete(d - 1); break;
                    case 3: Pragma(); break;
                    default: Txn(); break;
                }

                break;
            default: Misc(d); break;
        }
    }

    // ---- expressions over literals only, for the precedence check: the value of `SELECT e` must not change when every operator is parenthesised ----
    private static readonly string[] PureLits = ["0", "1", "2", "3", "7", "-1", "1.5", "'a'", "'B'", "'10'", "NULL", "TRUE", "x'01'", "''", "9223372036854775807"];
    private static readonly string[] PureOps = ["+", "-", "*", "/", "%", "||", "&", "|", "<<", ">>", "<", "<=", ">", ">=", "=", "==", "!=", "<>", "AND", "OR", "IS", "IS NOT", "IS DISTINCT FROM", "IS NOT DISTINCT FROM", "AND", "OR", "=", "IS"];

    private void PureExpr(int d)
    {
        if (d <= 0 || P(22))
        {
            W(Pick(PureLits));
            return;
        }

        switch (_r.Next(16))
        {
            case 0: case 1: case 2: case 3: case 4: case 5:
                PureExpr(d - 1); W(Pick(PureOps)); PureExpr(d - 1); break;
            case 6: W(Pick(["-", "+", "~", "NOT"])); PureExpr(d - 1); break;
            case 7: W("("); PureExpr(d - 1); W(")"); break;
            case 8: PureExpr(d - 1); if (P(40)) { W("NOT"); } W("BETWEEN"); PureExpr(d - 1); W("AND"); PureExpr(d - 1); break;
            case 9: PureExpr(d - 1); if (P(40)) { W("NOT"); } W("IN", "("); PureExpr(d - 1); W(","); PureExpr(d - 1); W(")"); break;
            case 10: PureExpr(d - 1); if (P(40)) { W("NOT"); } W(Pick(["LIKE", "GLOB"])); PureExpr(d - 1); if (P(20)) { W("ESCAPE", "'x'"); } break;
            case 11: PureExpr(d - 1); W(Pick(["ISNULL", "NOTNULL", "NOT NULL"])); break;
            case 12: W("CASE"); if (P(40)) { PureExpr(d - 1); } W("WHEN"); PureExpr(d - 1); W("THEN"); PureExpr(d - 1); if (P(50)) { W("ELSE"); PureExpr(d - 1); } W("END"); break;
            case 13: W("CAST", "("); PureExpr(d - 1); W("AS", Pick(["INTEGER", "TEXT", "REAL", "BLOB", "NUMERIC"]), ")"); break;
            case 14: PureExpr(d - 1); W("COLLATE", Pick(["NOCASE", "BINARY", "RTRIM"])); break;
            default: W(Pick(["coalesce", "abs", "typeof", "max", "min"]), "("); PureExpr(d - 1); W(","); PureExpr(d - 1); W(")"); break;
        }
    }

    /// <summary>A <c>SELECT</c> of one literal-only expression.</summary>
    internal string NextPure()
    {
        _o.Clear();
        W("SELECT");
        PureExpr(2 + _r.Next(4));
        return string.Join(' ', _o);
    }

    // ---- mutation ----
    private void Mutate()
    {
        int times = 1 + (P(25) ? 1 : 0);
        for (int t = 0; t < times && _o.Count > 0; t++)
        {
            int i = _r.Next(_o.Count);
            switch (_r.Next(8))
            {
                case 0: _o.RemoveAt(i); break;
                case 1: _o.Insert(i, _o[i]); break;
                case 2: if (i + 1 < _o.Count) { (_o[i], _o[i + 1]) = (_o[i + 1], _o[i]); } break;
                case 3: _o[i] = Pick(Noise); break;
                case 4: _o.Insert(i, Pick(Noise)); break;
                case 5: _o.RemoveRange(i, _o.Count - i); break;
                case 6: _o[i] = P(50) ? Pick(Reserved) : Pick(Fallbacks); break;
                default:
                    string tok = _o[i];
                    if (tok.Length > 1) { int c = _r.Next(tok.Length); _o[i] = tok.Remove(c, 1); }
                    else { _o.RemoveAt(i); }
                    break;
            }
        }
    }

    /// <summary>Makes one statement text and reports its kind and whether it was mutated.</summary>
    internal string Next(out string kind, out bool mutated)
    {
        LastKind = "";
        _o.Clear();
        _inTrigger = false;
        Statement(1 + _r.Next(4));
        kind = _kind;
        LastKind = kind;
        mutated = P(40);
        LastMutated = mutated;
        if (mutated) { Mutate(); }
        var sb = new StringBuilder();
        for (int i = 0; i < _o.Count; i++)
        {
            string tok = _o[i];
            bool tight = i > 0 && (tok is "," or ")" or "." || _o[i - 1] is "(" or "." || tok == "(" && P(50)) && P(80);
            if (i > 0 && !tight) { sb.Append(P(3) ? "\n" : " "); }
            sb.Append(tok);
        }

        // A trailing second statement, comment or junk exercises the tail offset.
        switch (_r.Next(20))
        {
            case 0: sb.Append("; select 1"); break;
            case 1: sb.Append(" -- c"); break;
            case 2: sb.Append(" /* c */ ;"); break;
            case 3: sb.Append(';'); break;
            case 4: sb.Append("; x y"); break;
            case 5: sb.Insert(0, "/* lead */ "); break;
            default: break;
        }

        // Character-level damage reaches the tokenizer's own error paths: unbalanced quotes, stray characters, odd whitespace, non-ASCII text.
        if (P(8))
        {
            string[] junk = ["'", "\"", "`", "[", "]", "\\", "\u00e9", "\u2028", "\t", "\r", "\f", "\v", ";", "/", "*", "-", "x", "0x", "e", ".", "_", "$", "@", ":", "#", "?", "!", "\U0001F600", "\u00a0", "1e", "x'", "/*", "--"];
            int at = _r.Next(sb.Length + 1);
            switch (_r.Next(3))
            {
                case 0: sb.Insert(at, Pick(junk)); break;
                case 1: if (at < sb.Length) { sb.Remove(at, 1); } break;
                default: if (at < sb.Length) { sb.Remove(at, 1).Insert(at, Pick(junk)); } break;
            }
        }

        return sb.ToString();
    }
}
