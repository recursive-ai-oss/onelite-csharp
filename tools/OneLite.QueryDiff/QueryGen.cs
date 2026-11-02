using System.Globalization;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.QueryDiff;

internal sealed class ColMeta
{
    internal string Name = "";
    internal string Type = "";
    internal bool Pk;
    internal bool Indexed;
    internal List<string> Samples = [];
}

internal sealed class TableMeta
{
    internal string Name = "";
    internal List<ColMeta> Cols = [];
    internal bool WithoutRowid;
    internal bool HasRowid => !WithoutRowid;
    internal long Rows;
    internal string? RowidName;
}

/// <summary>What the generator knows about a database, read through the pinned shell.</summary>
internal static class Introspect
{
    internal static string Q(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static JsonElement[] Json(string db, string sql)
    {
        Proc.Result r = Proc.Run(OraclePaths.Cli, ["-batch", "-init", "/dev/null", "-readonly", "-json", db, sql], timeout: TimeSpan.FromMinutes(5));
        if (string.IsNullOrWhiteSpace(r.Stdout))
        {
            return [];
        }

        using JsonDocument d = JsonDocument.Parse(r.Stdout);
        return [.. d.RootElement.EnumerateArray().Select(e => e.Clone())];
    }

    internal static List<TableMeta> Tables(string db)
    {
        var tables = new List<TableMeta>();
        JsonElement[] master = Json(db, "select name, sql from sqlite_master where type='table' and sql not like 'CREATE VIRTUAL%' order by name");
        foreach (JsonElement m in master)
        {
            string name = m.GetProperty("name").GetString()!;
            string sql = m.GetProperty("sql").GetString() ?? "";
            var t = new TableMeta { Name = name, WithoutRowid = sql.Contains("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase) };
            foreach (JsonElement c in Json(db, $"select name, type, pk, hidden from pragma_table_xinfo({SqlStr(name)})"))
            {
                if (c.GetProperty("hidden").GetInt32() != 0)
                {
                    continue;
                }

                t.Cols.Add(new ColMeta { Name = c.GetProperty("name").GetString()!, Type = c.GetProperty("type").GetString() ?? "", Pk = c.GetProperty("pk").GetInt32() > 0 });
            }

            if (t.Cols.Count == 0)
            {
                continue;
            }

            t.Rows = Json(db, $"select count(*) as n from {Q(name)}")[0].GetProperty("n").GetInt64();
            foreach (JsonElement ix in Json(db, $"select ii.name from pragma_index_list({SqlStr(name)}) il, pragma_index_info(il.name) ii where ii.seqno = 0"))
            {
                string? cn = ix.GetProperty("name").GetString();
                ColMeta? cm = t.Cols.Find(x => x.Name == cn);
                if (cm != null)
                {
                    cm.Indexed = true;
                }
            }

            if (t.HasRowid)
            {
                t.RowidName = t.Cols.Any(c => c.Name.Equals("rowid", StringComparison.OrdinalIgnoreCase)) ? (t.Cols.Any(c => c.Name.Equals("oid", StringComparison.OrdinalIgnoreCase)) ? null : "oid") : "rowid";
            }

            foreach (ColMeta c in t.Cols)
            {
                string order = t.HasRowid ? "min(rowid)" : Q(c.Name);
                foreach (JsonElement s in Json(db, $"select quote({Q(c.Name)}) as q from {Q(name)} where {Q(c.Name)} is not null and length(quote({Q(c.Name)})) < 60 group by {Q(c.Name)} order by {order} limit 7"))
                {
                    c.Samples.Add(s.GetProperty("q").GetString()!);
                }
            }

            tables.Add(t);
        }

        return tables;
    }

    internal static string SqlStr(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";
}

/// <summary>A seeded generator of queries in the supported subset.</summary>
internal sealed class QueryGen
{
    private readonly Random _r;
    private readonly List<TableMeta> _tables;
    private TableMeta? _t;
    private string _q = "";

    internal QueryGen(int seed, List<TableMeta> tables)
    {
        _r = new Random(seed);
        _tables = tables;
    }

    private bool P(double p) => _r.NextDouble() < p;

    private T Pick<T>(IReadOnlyList<T> l) => l[_r.Next(l.Count)];

    private string PickOf(params ReadOnlySpan<string> l) => l[_r.Next(l.Length)];

    private static readonly string[] s_ints = ["0", "1", "-1", "2", "3", "7", "10", "100", "255", "1000", "65536", "2147483647", "2147483648", "9223372036854775807", "-9223372036854775807", "9223372036854775806", "(-9223372036854775807-1)", "-9223372036854775808", "9223372036854775808", "0x10", "0xFF", "1e3", "12345678901234567890"];
    private static readonly string[] s_reals = ["0.0", "0.5", "-0.5", "1.5", "2.5", "3.14159", "1e10", "1e-5", "-1e100", "123456789.123456789", "0.1", "0.30000000000000004", "1.0", "100.0", "1e15", "1e16", "1e17", "5e-324", "1.7976931348623157e308", "9007199254740993", "9223372036854775807.0"];
    private static readonly string[] s_strs = ["''", "'a'", "'abc'", "'ABC'", "'Abc'", "'abc '", "' abc'", "'12'", "' 12 '", "'12abc'", "'1e3'", "'0x10'", "'1.5'", "'-7'", "'+7'", "'.5'", "'5.'", "'é'", "'日本語'", "'a%'", "'%b%'", "'_b_'", "'a_c'", "'A*'", "'[a-c]*'", "'x''y'", "'9223372036854775808'", "'0'", "'1'", "'true'", "'NULL'"];
    private static readonly string[] s_blobs = ["x''", "x'00'", "x'ff'", "x'616263'", "x'4142'"];
    private static readonly string[] s_types = ["INTEGER", "int", "REAL", "TEXT", "BLOB", "NUMERIC", "VARCHAR(10)", "FLOAT", "unsigned big int", "none", "DOUBLE"];
    private static readonly string[] s_colls = ["NOCASE", "BINARY", "RTRIM", "nocase"];

    private string Literal(ColMeta? like = null)
    {
        if (like != null && like.Samples.Count > 0 && P(0.6))
        {
            return Pick(like.Samples);
        }

        return _r.Next(10) switch
        {
            0 => "NULL",
            1 or 2 or 3 => Pick(s_ints),
            4 or 5 => Pick(s_reals),
            6 or 7 or 8 => Pick(s_strs),
            _ => Pick(s_blobs),
        };
    }

    private string Col()
    {
        ColMeta c = Pick(_t!.Cols);
        string n = Introspect.Q(c.Name);
        return P(0.2) ? _q + "." + n : n;
    }

    private string ColOrRowid()
    {
        if (_t!.RowidName != null && P(0.15))
        {
            return _t.RowidName;
        }

        return Col();
    }

    private string Atom()
    {
        if (_t != null && P(0.55))
        {
            return ColOrRowid();
        }

        return Literal();
    }

    private string Expr(int depth)
    {
        if (depth <= 0 || P(0.18))
        {
            return Atom();
        }

        switch (_r.Next(22))
        {
            case 0: return "(" + Expr(depth - 1) + ")";
            case 1: return PickOf("-", "+", "~", "NOT ") + " " + Expr(depth - 1);
            case 2:
            case 3:
            case 4: return "(" + Expr(depth - 1) + " " + PickOf("+", "-", "*", "/", "%") + " " + Expr(depth - 1) + ")";
            case 5: return "(" + Expr(depth - 1) + " || " + Expr(depth - 1) + ")";
            case 6: return "(" + Expr(depth - 1) + " " + PickOf("&", "|", "<<", ">>") + " " + Expr(depth - 1) + ")";
            case 7:
            case 8:
            case 9: return "(" + Expr(depth - 1) + " " + PickOf("=", "==", "<>", "!=", "<", "<=", ">", ">=", "IS", "IS NOT") + " " + Expr(depth - 1) + ")";
            case 10: return "(" + Expr(depth - 1) + " " + PickOf("AND", "OR") + " " + Expr(depth - 1) + ")";
            case 11: return "(" + Expr(depth - 1) + " IS " + (P(0.5) ? "NOT " : "") + "NULL)";
            case 12: return "(" + Expr(depth - 1) + (P(0.3) ? " NOT" : "") + " BETWEEN " + Expr(depth - 1) + " AND " + Expr(depth - 1) + ")";
            case 13:
            {
                int n = _r.Next(0, 5);
                return "(" + Expr(depth - 1) + (P(0.3) ? " NOT" : "") + " IN (" + string.Join(", ", Enumerable.Range(0, n).Select(_ => Atom())) + "))";
            }

            case 14: return "(" + Expr(depth - 1) + (P(0.2) ? " NOT" : "") + " LIKE " + PatternAtom() + (P(0.2) ? " ESCAPE '!'" : "") + ")";
            case 15: return "(" + Expr(depth - 1) + (P(0.2) ? " NOT" : "") + " GLOB " + PatternAtom() + ")";
            case 16: return "CAST(" + Expr(depth - 1) + " AS " + Pick(s_types) + ")";
            case 17: return "(" + Expr(depth - 1) + " COLLATE " + Pick(s_colls) + ")";
            case 18:
            {
                var sb = new StringBuilder("CASE");
                bool operand = P(0.4);
                if (operand)
                {
                    sb.Append(' ').Append(Expr(depth - 1));
                }

                for (int i = _r.Next(1, 3); i > 0; i--)
                {
                    sb.Append(" WHEN ").Append(Expr(depth - 1)).Append(" THEN ").Append(Expr(depth - 1));
                }

                if (P(0.6))
                {
                    sb.Append(" ELSE ").Append(Expr(depth - 1));
                }

                return sb.Append(" END").ToString();
            }

            default: return Call(depth);
        }
    }

    private string PatternAtom() => P(0.7) ? Pick(s_strs) : Atom();

    private string Args(int depth, int n) => string.Join(", ", Enumerable.Range(0, n).Select(_ => Expr(depth - 1)));

    private static readonly string[] s_fmts =
    [
        "'%d'", "'%5d|'", "'%-5d|'", "'%05d'", "'%+d'", "'% d'", "'%,d'", "'%i'", "'%u'", "'%x'", "'%X'", "'%#x'", "'%o'", "'%c'", "'%5c|'", "'%s'", "'%10s|'", "'%-10s|'",
        "'%.3s'", "'%!.3s'", "'%q'", "'%Q'", "'%w'", "'%#Q'", "'%f'", "'%.2f'", "'%8.3f|'", "'%08.2f'", "'%e'", "'%.3e'", "'%E'", "'%g'", "'%.3g'", "'%G'", "'%.15g'",
        "'%!.15g'", "'%.17g'", "'%.0f'", "'%#.0f'", "'%!.2f'", "'%5.1f%%'", "'%s=%d'", "'%d %d %d'", "'%*d|'", "'%.*f'", "'abc'", "'%%'", "'%z'", "'%y'", "'%'", "'%lld'", "'%ld'",
    ];

    private static readonly string[] s_timeValues =
    [
        "'2024-02-29'", "'2023-12-31 23:59:59'", "'2000-01-01 00:00:00.123'", "'12:34:56'", "'12:34'", "2451545", "2451545.5", "0", "1700000000", "'1999-12-31T23:59:59Z'",
        "'2024-03-10 08:15:00+02:00'", "'0001-01-01'", "'9999-12-31'", "'2024-13-01'", "'2024-02-30'", "'abc'", "NULL", "'2021-01-01'", "'2020-12-31'", "'2024-12-30'", "'2024-05-06 07:08:09.999'",
        "-1", "5373484.5", "'  2024-01-01'", "'2024-01-01 24:00'", "2460000.123456", "'-0001-01-01'", "'2024-06-15 12:00:00 -05:30'", "1.5", "x'323032342d3031'",
    ];

    private static readonly string[] s_modifiers =
    [
        "'+1 day'", "'-3 months'", "'+1 year'", "'+10 hours'", "'-45 minutes'", "'+30.5 seconds'", "'start of month'", "'start of year'", "'start of day'", "'weekday 3'", "'weekday 0'",
        "'unixepoch'", "'auto'", "'julianday'", "'subsec'", "'subsecond'", "'ceiling'", "'floor'", "'+01:30'", "'-02:00:30.250'", "'+0001-02-03'", "'bogus'", "'+999999 days'",
        "'1 month'", "'+1.5 months'", "'-1 year'", "'+2 weeks'", "'weekday 7'", "'-0000-01-01 00:00:01'", "'SUBSEC'", "'+1 days'", "'end of month'", "NULL", "5", "'+400 years'",
    ];

    private string TimeValue() => P(0.8) ? Pick(s_timeValues) : Expr(1);

    private string Modifiers()
    {
        int n = P(0.4) ? 0 : _r.Next(1, 4);
        return string.Concat(Enumerable.Range(0, n).Select(_ => ", " + (P(0.9) ? Pick(s_modifiers) : Expr(1))));
    }

    private string DateCall()
    {
        switch (_r.Next(8))
        {
            case 0: return "date(" + TimeValue() + Modifiers() + ")";
            case 1: return "time(" + TimeValue() + Modifiers() + ")";
            case 2: return "datetime(" + TimeValue() + Modifiers() + ")";
            case 3: return "julianday(" + TimeValue() + Modifiers() + ")";
            case 4: return "unixepoch(" + TimeValue() + Modifiers() + ")";
            case 5:
            {
                string f = PickOf("'%Y-%m-%d'", "'%H:%M:%S'", "'%d %e %f %F'", "'%G %g %H %I'", "'%j %J %k %l'", "'%m %M %p %P'", "'%R %s %S %T'", "'%U %u %V %w %W %Y %%'", "'%a'", "'%Q'", "'%'", "''", "'x%Yy'");
                return "strftime(" + f + ", " + TimeValue() + Modifiers() + ")";
            }

            case 6: return "timediff(" + TimeValue() + ", " + TimeValue() + ")";
            default: return "date(" + TimeValue() + Modifiers() + ")";
        }
    }

    private string Call(int depth)
    {
        string[] one = ["length", "lower", "upper", "typeof", "abs", "hex", "quote", "unicode", "octet_length", "sign", "likely", "unlikely", "trim", "ltrim", "rtrim", "round", "unistr", "zeroblob_s"];
        string[] math1 = ["acos", "acosh", "asin", "asinh", "atan", "atanh", "ceil", "ceiling", "cos", "cosh", "degrees", "exp", "floor", "ln", "log", "log10", "log2", "radians", "sin", "sinh", "sqrt", "tan", "tanh", "trunc"];
        switch (_r.Next(40))
        {
            case 0: return "coalesce(" + string.Join(", ", Enumerable.Range(0, _r.Next(2, 4)).Select(_ => Expr(depth - 1))) + ")";
            case 1: return "ifnull(" + Expr(depth - 1) + ", " + Expr(depth - 1) + ")";
            case 2:
            case 3: return PickOf("substr", "substring") + "(" + Expr(depth - 1) + ", " + SmallInt() + (P(0.6) ? ", " + SmallInt() : "") + ")";
            case 4 when P(0.08): return Pick(one.Where(x => !x.EndsWith("_s", StringComparison.Ordinal)).ToList()) + "(" + Expr(depth - 1) + ", " + Expr(depth - 1) + ")";
            case 5: return "char(" + Args(depth, _r.Next(0, 4)) + ")";
            case 6: return PickOf("concat", "concat_ws") + "(" + Args(depth, _r.Next(2, 5)) + ")";
            case 7: return "instr(" + Args(depth, 2) + ")";
            case 8:
            case 9: return PickOf("trim", "ltrim", "rtrim") + "(" + Expr(depth - 1) + ", " + (P(0.7) ? PickOf("' '", "'a'", "'ab'", "'1'", "''", "'xy'", "'0'") : Expr(depth - 1)) + ")";
            case 10: return "replace(" + Expr(depth - 1) + ", " + (P(0.7) ? PickOf("'a'", "'b'", "''", "'ab'", "'1'", "' '") : Expr(depth - 1)) + ", " + (P(0.7) ? PickOf("'x'", "''", "'XYZ'", "'1'") : Expr(depth - 1)) + ")";
            case 11: return "unhex(" + Expr(depth - 1) + (P(0.4) ? ", " + PickOf("' '", "'-'", "''", "NULL") : "") + ")";
            case 12: return "round(" + Expr(depth - 1) + (P(0.6) ? ", " + PickOf("0", "1", "2", "3", "-1", "30", "31", "15", "NULL", "'2'") : "") + ")";
            case 13:
            {
                string fn = PickOf("nullif", "iif", "if");
                int n = fn == "nullif" ? 2 : P(0.2) ? 2 : P(0.2) ? 4 : 3;
                return fn + "(" + Args(depth, n) + ")";
            }
            case 14: return PickOf("max", "min") + "(" + Args(depth, _r.Next(2, 5)) + ")";
            case 15: return "likelihood(" + Expr(depth - 1) + ", " + PickOf("0.5", "0", "1", "0.25") + ")";
            case 16: return PickOf("like", "glob") + "(" + Expr(depth - 1) + ", " + Expr(depth - 1) + (P(0.2) ? ", " + PickOf("'!'", "'\\'", "'%'", "NULL") : "") + ")";
            case 17: return "zeroblob(" + _r.Next(-1, 6) + ")";
            case 18: return PickOf("sqlite_version()", "sqlite_source_id()", "sqlite_compileoption_used('THREADSAFE')", "sqlite_compileoption_used('SQLITE_ENABLE_FTS5')", "sqlite_compileoption_used('NOPE')", "sqlite_compileoption_get(" + _r.Next(-1, 60) + ")", "pi()", "changes()", "total_changes()", "last_insert_rowid()");
            case 19:
            case 20:
            case 21:
            {
                string fmt = P(0.9) ? Pick(s_fmts) : Expr(1);
                var sb = new StringBuilder(PickOf("printf", "format") + "(" + fmt);
                if (fmt.Contains('*', StringComparison.Ordinal))
                {
                    sb.Append(", ").Append(PickOf("0", "1", "3", "5", "-4", "12", "30", "100"));
                }

                for (int i = _r.Next(0, 4); i > 0; i--)
                {
                    sb.Append(", ").Append(Expr(depth - 1));
                }

                return sb.Append(')').ToString();
            }

            case 22:
            case 23:
            case 24:
            case 25: return Pick(math1) + "(" + Expr(depth - 1) + ")";
            case 26: return PickOf("atan2", "mod", "pow", "power", "log") + "(" + Args(depth, 2) + ")";
            case 27:
            case 28:
            case 29:
            case 30:
            case 31: return DateCall();
            default:
            {
                string f = Pick(one);
                if (f == "zeroblob_s")
                {
                    return "zeroblob(" + _r.Next(0, 4) + ")";
                }

                return f + "(" + Expr(depth - 1) + ")";
            }
        }
    }

    private string SmallInt() => P(0.7) ? _r.Next(-6, 8).ToString(CultureInfo.InvariantCulture) : Expr(0);

    private string Cond()
    {
        // Terms an index or rowid seek can use come up often.
        int n = _r.Next(1, 4);
        var parts = new List<string>();
        for (int i = 0; i < n; i++)
        {
            parts.Add(P(0.6) ? SeekTerm() : Expr(3));
        }

        return string.Join(P(0.85) ? " AND " : " OR ", parts);
    }

    private string SeekTerm()
    {
        ColMeta c = P(0.6) && _t!.Cols.Any(x => x.Indexed || x.Pk) ? Pick(_t.Cols.Where(x => x.Indexed || x.Pk).ToList()) : Pick(_t!.Cols);
        string name = Introspect.Q(c.Name);
        if (_t.RowidName != null && P(0.2))
        {
            name = _t.RowidName;
            c = new ColMeta { Name = name, Samples = [.. Enumerable.Range(0, 5).Select(_ => _r.Next(-3, 120).ToString(CultureInfo.InvariantCulture))] };
        }

        string v() => Literal(c);
        switch (_r.Next(8))
        {
            case 0:
            case 1: return $"{name} = {v()}";
            case 2: return $"{name} {PickOf("<", "<=", ">", ">=")} {v()}";
            case 3: return $"{v()} {PickOf("<", "<=", ">", ">=", "=")} {name}";
            case 4: return $"{name} BETWEEN {v()} AND {v()}";
            case 5: return $"{name} {PickOf(">", ">=")} {v()} AND {name} {PickOf("<", "<=")} {v()}";
            case 6: return $"{name} IN ({v()}, {v()}, {v()})";
            default: return $"{name} = {v()} AND {Col()} {PickOf("=", "<", ">")} {v()}";
        }
    }

    private string Order(out bool fullyOrdered)
    {
        var terms = new List<string>();
        int n = _r.Next(1, 3);
        for (int i = 0; i < n; i++)
        {
            string e = P(0.1) ? _r.Next(1, 4).ToString(CultureInfo.InvariantCulture) : P(0.7) ? ColOrRowid() : Expr(2);
            if (!char.IsDigit(e[0]) && !e.Contains('"', StringComparison.Ordinal) && !e.Contains("rowid", StringComparison.OrdinalIgnoreCase))
            {
                // A term with no column in it is a constant: the reference folds some constants into column positions while
                // parsing, which only a degenerate query can observe.
                e = ColOrRowid();
            }

            string dir = P(0.4) ? " DESC" : P(0.2) ? " ASC" : "";
            string nulls = P(0.2) ? (P(0.5) ? " NULLS FIRST" : " NULLS LAST") : "";
            string coll = P(0.15) ? " COLLATE " + Pick(s_colls) : "";
            terms.Add(e + coll + dir + nulls);
        }

        fullyOrdered = P(0.6);
        if (fullyOrdered)
        {
            if (_t!.HasRowid)
            {
                terms.Add(_t.RowidName ?? "_rowid_");
            }
            else
            {
                terms.AddRange(_t.Cols.Where(c => c.Pk).Select(c => Introspect.Q(c.Name)));
            }
        }

        return " ORDER BY " + string.Join(", ", terms);
    }

    /// <summary>The next query: SQL text and whether the row order is fixed by the query.</summary>
    internal (string Sql, bool Ordered, bool NeedsTable) Next()
    {
        if (_tables.Count == 0 || P(0.12))
        {
            _t = null;
            _q = "";
            string sql = "SELECT " + string.Join(", ", Enumerable.Range(0, _r.Next(1, 5)).Select(_ => Expr(4) + (P(0.2) ? " AS x" + _r.Next(9) : "")));
            if (P(0.1))
            {
                sql += " WHERE " + Expr(3);
            }

            if (P(0.1))
            {
                sql += " LIMIT " + _r.Next(-1, 3) + (P(0.5) ? " OFFSET " + _r.Next(-1, 3) : "");
            }

            return (sql, true, false);
        }

        _t = Pick(_tables);
        _q = P(0.3) ? "q" : Introspect.Q(_t.Name);
        string from = " FROM " + Introspect.Q(_t.Name) + (_q == "q" ? " AS q" : "");
        string sel;
        bool agg = P(0.12);
        if (agg)
        {
            sel = "count(*)" + (P(0.3) ? " + " + Literal() : "") + (P(0.2) ? ", count( * )" : "");
        }
        else if (P(0.2))
        {
            sel = "*";
        }
        else if (P(0.1))
        {
            sel = _q + ".*" + (P(0.5) ? ", " + Expr(3) : "");
        }
        else
        {
            sel = string.Join(", ", Enumerable.Range(0, _r.Next(1, 5)).Select(_ => Expr(P(0.5) ? 1 : 3) + (P(0.25) ? " AS " + PickOf("x", "y", "\"my col\"", "a1") : "")));
        }

        string where = P(0.75) ? " WHERE " + Cond() : "";
        string order = "";
        string limit = "";
        bool ordered = false;
        if (!agg && P(0.45))
        {
            order = Order(out bool full);
            ordered = full;
            if (full && P(0.5))
            {
                limit = " LIMIT " + PickOf("0", "1", "2", "5", "10", "-1", "3 + 4", "1e1", "'3'") + (P(0.5) ? " OFFSET " + PickOf("0", "1", "3", "-2", "100000") : "");
            }
        }
        else if (agg && P(0.2))
        {
            limit = " LIMIT " + PickOf("0", "1", "2") + (P(0.5) ? " OFFSET " + PickOf("0", "1") : "");
        }

        return ("SELECT " + sel + from + where + order + limit, ordered, true);
    }
}
