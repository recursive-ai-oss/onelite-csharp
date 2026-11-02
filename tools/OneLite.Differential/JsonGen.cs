using System.Globalization;
using System.Linq;
using System.Text;

namespace RecursiveAI.OneLite.Differential;

/// <summary>
/// Generates SQL that exercises the JSON functions: documents (canonical JSON, JSON5 forms, deep nesting, big numbers, damaged text),
/// paths (valid, missing and malformed) and every function of json1, alone and nested.
/// </summary>
internal sealed class JsonGen
{
    /// <summary>Starts every statement of the JSON campaign, so the classifier knows not to excuse one-sided runtime errors.</summary>
    internal const string Marker = "/*json*/ ";

    private readonly Rng _r;
    private readonly List<string> _keys = ["a", "b", "c", "x", "k1", "name", "a b", "é", ""];

    internal JsonGen(Rng r) => _r = r;

    // ---------------------------------------------------------------- SQL literals

    /// <summary>An SQL expression for an arbitrary string: a plain literal, or a cast from a blob when it holds NUL or other control bytes.</summary>
    internal static string Lit(string s)
    {
        bool plain = true;
        foreach (char ch in s)
        {
            if (ch == 0 || (ch < 0x20 && ch != '\n' && ch != '\t'))
            {
                plain = false;
            }
        }

        if (plain)
        {
            return "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";
        }

        return "cast(x'" + Convert.ToHexString(Encoding.UTF8.GetBytes(s)) + "' as text)";
    }

    internal static string BytesLit(byte[] b) => "cast(x'" + Convert.ToHexString(b) + "' as text)";

    // ---------------------------------------------------------------- documents

    private static readonly string[] Ints = ["0", "1", "-1", "7", "42", "255", "-0", "9223372036854775807", "9223372036854775808", "-9223372036854775808", "-9223372036854775809", "123456789012345678901234567890", "100", "65536"];
    private static readonly string[] Reals = ["1.5", "-0.5", "1e5", "1E-3", "1.5e+300", "0.1", "123456789.123456789", "1e400", "-1e400", "5e-324", "1.0", "0.0", "-0.0", "2.5E2", "3.14159", "1e22", "1e-7"];
    private static readonly string[] Json5Nums = ["0x1F", "-0xff", "+5", ".5", "5.", "1.e3", "Infinity", "-Infinity", "+Inf", "NaN", "0X10", "0xFFFFFFFFFFFFFFFF", "0x10000000000000000", "-.5", "+.5e1", "0x0"];
    private static readonly string[] Words = ["abc", "hello world", "x", "", "a\\nb", "tab\\there", "q\\\"uote", "back\\\\slash", "sl\\/ash", "\\u00e9t\\u00e9", "\\ud83d\\ude00", "\\u0000", "\\u001f", "é", "日本語", "😀", "a very long string that goes past eleven bytes", "\\b\\f\\r"];
    private static readonly string[] Json5Strs = ["'single'", "'it\\'s'", "'say \"hi\"'", "\"a\\x41b\"", "\"v\\vt\"", "\"z\\0z\"", "\"cont\\\nin\"", "\"q\\'q\"", "'x'", "\"\\u2028\"", "\"\\x7f\""];

    internal string Doc(int depth = 3, bool json5 = false)
    {
        var sb = new StringBuilder();
        bool j5 = json5 || _r.Chance(10);
        Value(sb, depth, j5);
        string s = sb.ToString();
        if (j5 && _r.Chance(30))
        {
            s = _r.Pick(new[] { "/* c */", "// c\n", " \t\n", "\u00a0", "\u2028" }) + s + _r.Pick(new[] { " // t", "/* t */", "\n", "" });
        }
        else if (_r.Chance(25))
        {
            s = _r.Pick(new[] { " ", "\n", "\t", "\r\n" }) + s + _r.Pick(new[] { " ", "\n", "" });
        }

        return s;
    }

    private void Value(StringBuilder sb, int depth, bool j5)
    {
        int kind = _r.Weighted((depth > 0 ? 3 : 0, 0), (depth > 0 ? 3 : 0, 1), (4, 2), (3, 3), (4, 4), (1, 5), (1, 6), (1, 7));
        switch (kind)
        {
            case 0:
                Obj(sb, depth, j5);
                break;
            case 1:
                Arr(sb, depth, j5);
                break;
            case 2:
                sb.Append(_r.Pick(Ints));
                break;
            case 3:
                sb.Append(_r.Chance(15) && j5 ? _r.Pick(Json5Nums) : _r.Pick(Reals));
                break;
            case 4:
                Str(sb, j5);
                break;
            case 5:
                sb.Append("true");
                break;
            case 6:
                sb.Append("false");
                break;
            default:
                sb.Append("null");
                break;
        }
    }

    private void Str(StringBuilder sb, bool j5)
    {
        if (j5 && _r.Chance(35))
        {
            sb.Append(_r.Pick(Json5Strs));
            return;
        }

        sb.Append('"').Append(_r.Pick(Words)).Append('"');
    }

    private void Arr(StringBuilder sb, int depth, bool j5)
    {
        int n = _r.Weighted((3, 0), (6, _r.Range(1, 4)), (2, _r.Range(5, 14)), (1, _r.Range(60, 140)));
        sb.Append('[');
        for (int i = 0; i < n; i++)
        {
            if (i > 0)
            {
                sb.Append(_r.Chance(8) ? " , " : ",");
            }

            Value(sb, n > 20 ? 0 : depth - 1, j5);
        }

        if (j5 && n > 0 && _r.Chance(20))
        {
            sb.Append(',');
        }

        sb.Append(']');
    }

    private void Obj(StringBuilder sb, int depth, bool j5)
    {
        int n = _r.Weighted((3, 0), (8, _r.Range(1, 4)), (2, _r.Range(5, 10)));
        sb.Append('{');
        for (int i = 0; i < n; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            string k = _r.Pick(_keys);
            if (j5 && k.Length > 0 && k.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && !char.IsAsciiDigit(k[0]) && _r.Chance(40))
            {
                sb.Append(k);
            }
            else if (j5 && _r.Chance(15))
            {
                sb.Append('\'').Append(k).Append('\'');
            }
            else
            {
                sb.Append('"').Append(k).Append('"');
            }

            sb.Append(_r.Chance(10) ? " : " : ":");
            Value(sb, depth - 1, j5);
        }

        if (j5 && n > 0 && _r.Chance(20))
        {
            sb.Append(',');
        }

        sb.Append('}');
    }

    /// <summary>Text that is mostly valid and sometimes damaged.</summary>
    internal string Maybe()
    {
        string d = Doc(_r.Range(1, 4));
        if (_r.Chance(18))
        {
            string bad = Damage(d);
            d = HasLoneSurrogate(bad) ? d : bad;
        }

        return d;
    }

    private static bool HasLoneSurrogate(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(s[i]))
            {
                return true;
            }
        }

        return false;
    }

    private string Damage(string d)
    {
        if (d.Length == 0)
        {
            return "{";
        }

        int at = _r.Next(d.Length);
        return _r.Next(7) switch
        {
            0 => d[..at],
            1 => d.Remove(at, 1),
            2 => d.Insert(at, _r.Pick(new[] { ",", ":", "]", "}", "[", "{", "\"", "'", "\\", "/", "*", "x", " ", "0", "-", "." })),
            3 => d + _r.Pick(new[] { " x", "]", "}", ",", " /* ", "//" }),
            4 => d.Replace(',', ';'),
            5 => d.Replace("\"", "'", StringComparison.Ordinal),
            _ => d.Insert(at, _r.Pick(new[] { "\\q", "\\u12", "\\x4", "\t", "\u00a0", "01", "1e", "0x", "tru", "nul" })),
        };
    }

    /// <summary>A deeply nested document, around the 1000-level limit.</summary>
    internal string Deep()
    {
        int n = _r.Pick(new[] { 10, 100, 500, 999, 1000, 1001, 1002 });
        bool obj = _r.Chance(30);
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            sb.Append(obj ? "{\"a\":" : "[");
        }

        sb.Append(_r.Pick(new[] { "1", "null", "\"x\"", "" }));
        for (int i = 0; i < n; i++)
        {
            sb.Append(obj ? "}" : "]");
        }

        return sb.ToString();
    }

    // ---------------------------------------------------------------- paths

    internal string Path()
    {
        if (_r.Chance(12))
        {
            return _r.Pick(new[]
            {
                "$.", "$[", "a", "", "$..a", "$[a]", "$.a[", "$[1.5]", "$[ 1]", "$[-1]", "$[#-]", "$[]", "$.\"a", "$ ", "$[1]x", "$.a.", "$a", "$[#+1]", "$['a']", "$.*", "$[*]", "$.\"a\"x",
                "$[01]", "$[#-0]", "$[99999999999999999999]", "$.\"\"", "$.\"a b\"", "$.a b", "$.é",
            });
        }

        var sb = new StringBuilder("$");
        int n = _r.Weighted((1, 0), (6, 1), (4, 2), (2, 3), (1, _r.Range(4, 8)));
        for (int i = 0; i < n; i++)
        {
            switch (_r.Next(8))
            {
                case 0:
                case 1:
                case 2:
                    sb.Append('.').Append(_r.Pick(_keys.Where(k => k.Length > 0 && !k.Contains(' ', StringComparison.Ordinal)).ToList()));
                    break;
                case 3:
                    sb.Append(".\"").Append(_r.Pick(_keys)).Append('"');
                    break;
                case 4:
                case 5:
                    sb.Append('[').Append(_r.Range(0, 5).ToString(CultureInfo.InvariantCulture)).Append(']');
                    break;
                case 6:
                    sb.Append("[#-").Append(_r.Range(1, 4).ToString(CultureInfo.InvariantCulture)).Append(']');
                    break;
                default:
                    sb.Append(_r.Chance(50) ? "[#]" : "[" + _r.Range(0, 3).ToString(CultureInfo.InvariantCulture) + "]");
                    break;
            }
        }

        return sb.ToString();
    }

    // ---------------------------------------------------------------- SQL values and expressions

    private string DocArg()
    {
        return _r.Weighted(
            (14, Lit(Maybe())),
            (2, "jsonb(" + Lit(Doc()) + ")"),
            (2, "json(" + Lit(Doc()) + ")"),
            (1, "NULL"),
            (1, Lit(Deep())),
            (1, _r.Pick(new[] { "5", "1.5", "x'0b'", "x'7b7d'", "x'c30131'", "x'1331'", "x''", "x'0c'" })));
    }

    private string PathArg() => _r.Chance(3) ? "NULL" : Lit(Path());

    private string ValueArg()
    {
        return _r.Weighted(
            (3, _r.Pick(Ints)),
            (2, _r.Pick(Reals)),
            (4, Lit(Plain())),
            (1, "NULL"),
            (3, "json(" + Lit(Doc(2)) + ")"),
            (1, "jsonb(" + Lit(Doc(2)) + ")"),
            (1, "json_array(1,2)"),
            (1, Lit(Doc(1))),
            (1, "1=1"),
            (1, Lit(Doc(1)) + "->'$'"),
            (1, Lit(Doc(1)) + "->>'$'"),
            (1, "x'0b'"),
            (1, _r.Chance(30) ? "x'ff'" : "NULL"));
    }

    private string Plain()
    {
        return _r.Pick(new[] { "abc", "a\"b", "back\\slash", "line\nbreak", "tab\there", "é", "", "[1,2]", "{\"a\":1}", "x/y", "a'b", "\u0001ctl", "😀" });
    }

    /// <summary>One statement.</summary>
    /// <summary>One statement of a JSONB byte-identity run: a document as bytes, as text read back from its own blob, as a re-encoded blob and its blob validity.</summary>
    internal string JsonbStatement()
    {
        string d = _r.Chance(20) ? Lit(Deep()) : DocArg();
        return Marker + $"SELECT hex(jsonb({d})), json(jsonb({d})), hex(jsonb(json(jsonb({d})))), json_valid(jsonb({d}), 8)";
    }

    private static readonly string[] s_tvfColumns = ["key", "value", "type", "atom", "id", "parent", "fullkey", "path", "rowid", "typeof(value)", "json", "root"];

    /// <summary>A statement over a table-valued function: json_each, json_tree, their jsonb forms, generate_series, joined or aggregated.</summary>
    private string TableStatement()
    {
        string fn = _r.Pick(new[] { "json_each", "json_tree", "jsonb_each", "jsonb_tree" });
        string args = _r.Chance(3) ? DocArg() + ", " + PathArg() : DocArg();
        // The reference sometimes returns an empty "root" for a few rows of json_tree over a document nested hundreds of levels deep (which rows depends on
        // the other columns selected), so "root" is left out for such documents.
        string[] pool = args.Length > 2000 ? [.. s_tvfColumns.Where(c => c != "root")] : s_tvfColumns;
        string cols = string.Join(", ", Enumerable.Range(0, _r.Range(1, 4)).Select(_ => _r.Pick(pool)));
        return _r.Weighted<Func<string>>(
            (6, () => $"SELECT {cols} FROM {fn}({args})"),
            (2, () => $"SELECT {cols} FROM {fn}({args}) WHERE type = {Lit(_r.Pick(new[] { "integer", "text", "object", "array", "null", "real", "true", "false" }))}"),
            (2, () => $"SELECT count(*), json_group_array(value), json_group_object(coalesce(key, 'k'), type) FROM {fn}({args})"),
            (2, () => $"SELECT hex(jsonb_group_array(atom)), jsonb_group_object(fullkey, id) FROM {fn}({args})"),
            (2, () => $"SELECT a.key, b.key, b.value FROM json_each({args}) a, json_each(a.value) b"),
            (1, () => $"SELECT {cols} FROM {fn} WHERE json = {DocArg()}"),
            (1, () => $"SELECT {cols} FROM {fn}({args}) ORDER BY value, key LIMIT 5"),
            (2, () => $"SELECT value, rowid FROM generate_series({_r.Range(-5, 5)}, {_r.Range(-5, 9)}, {_r.Range(-3, 3)})"),
            (1, () => $"SELECT sum(value), count(*) FROM generate_series({_r.Pick(new[] { "0", "1", "-3", "2.5", "'4'", "NULL", "'x'" })}, {_r.Pick(new[] { "5", "9223372036854775807", "0", "7.9", "NULL" })}, 9223372036854775807)"),
            (1, () => $"SELECT x.value, y.value FROM generate_series(1, {_r.Range(1, 3)}) x, json_each({args}) y"))();
    }

    internal string Statement()
    {
        if (_r.Chance(20))
        {
            return Marker + TableStatement();
        }

        int n = _r.Weighted((6, 1), (3, 2), (1, 3));
        var cols = new List<string>();
        for (int i = 0; i < n; i++)
        {
            cols.Add(Expr(2));
        }

        return Marker + "SELECT " + string.Join(", ", cols);
    }

    private string Expr(int depth)
    {
        string d = DocArg();
        if (depth > 0 && _r.Chance(12))
        {
            d = Expr(depth - 1);
        }

        string p = PathArg();
        return _r.Next(44) switch
        {
            0 => $"json({d})",
            1 or 2 or 3 => $"hex(jsonb({d}))",
            4 => $"json_valid({d})",
            5 => $"json_valid({d}, {_r.Range(0, 16)})",
            6 or 7 => $"json_error_position({d})",
            8 => $"json_type({d})",
            9 => $"json_type({d}, {p})",
            10 => $"json_array_length({d})",
            11 => $"json_array_length({d}, {p})",
            12 or 13 => $"json_extract({d}, {p})",
            14 => $"json_extract({d}, {p}, {PathArg()})",
            15 => $"hex(jsonb_extract({d}, {p}))",
            16 => $"{d} -> {p}",
            17 => $"{d} ->> {p}",
            18 => $"{d} -> {_r.Pick(new[] { "'a'", "'b'", "0", "1", "-1", "'a b'", "''", "1.5", "NULL", "'x'", "2" })}",
            19 => $"{d} ->> {_r.Pick(new[] { "'a'", "'b'", "0", "1", "-1", "'a b'", "''", "1.5", "NULL", "'x'", "2" })}",
            20 or 21 => $"json_set({d}, {p}, {ValueArg()}{Pair()})",
            22 => $"hex(jsonb_set({d}, {p}, {ValueArg()}))",
            23 => $"json_insert({d}, {p}, {ValueArg()}{Pair()})",
            24 => $"hex(jsonb_insert({d}, {p}, {ValueArg()}))",
            25 => $"json_replace({d}, {p}, {ValueArg()}{Pair()})",
            26 => $"hex(jsonb_replace({d}, {p}, {ValueArg()}))",
            27 => $"json_array_insert({d}, {p}, {ValueArg()})",
            28 => $"hex(jsonb_array_insert({d}, {p}, {ValueArg()}))",
            29 => $"json_remove({d}{(_r.Chance(85) ? ", " + p : "")}{(_r.Chance(30) ? ", " + PathArg() : "")})",
            30 => $"hex(jsonb_remove({d}, {p}))",
            31 => $"json_patch({d}, {DocArg()})",
            32 => $"hex(jsonb_patch({d}, {DocArg()}))",
            33 => $"json_pretty({d})",
            34 => $"json_pretty({d}, {_r.Pick(new[] { "'  '", "'\\t'", "''", "NULL", "'--'", "3" })})",
            35 => $"json_quote({ValueArg()})",
            36 => $"json_array({ValueArg()}, {ValueArg()}, {ValueArg()})",
            37 => $"hex(jsonb_array({ValueArg()}, {ValueArg()}))",
            38 => $"json_object({Lit(_r.Pick(_keys))}, {ValueArg()}, {Lit(_r.Pick(_keys))}, {ValueArg()})",
            39 => $"hex(jsonb_object({Lit(_r.Pick(_keys))}, {ValueArg()}))",
            40 => $"json_object({_r.Pick(new[] { "1", "NULL", "x'61'", "1.5" })}, {ValueArg()})",
            41 => $"json_set({d}, {p}, {ValueArg()}, {PathArg()}, {ValueArg()})",
            42 => $"json_array()",
            _ => $"hex(jsonb({Lit(Deep())}))",
        };
    }

    private string Pair() => _r.Chance(20) ? $", {PathArg()}, {ValueArg()}" : "";
}
