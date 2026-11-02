using System.Globalization;

namespace RecursiveAI.OneLite.Differential;

/// <summary>A column an expression may name, with the qualifier it is written with (empty for none).</summary>
internal readonly record struct ScopeCol(string Name, ColDef Col, string Qualifier)
{
    internal string Ref => Qualifier.Length == 0 ? Name : Qualifier + "." + Name;
}

/// <summary>
/// Random expressions over the columns in scope, from the operator and function lists of lang_expr, lang_corefunc, lang_mathfunc,
/// lang_datefunc and json1. Mismatched types (text compared with numbers, blobs in arithmetic, integers at the edge of the 64-bit
/// range) are deliberate: they are where affinity and coercion rules differ between implementations.
/// </summary>
internal sealed class ExprGen
{
    private readonly Rng _r;
    private readonly Fam.Filter _filter;
    private readonly IReadOnlyList<ScopeCol> _cols;

    internal ExprGen(Rng r, Fam.Filter filter, IReadOnlyList<ScopeCol> cols)
    {
        _r = r;
        _filter = filter;
        _cols = cols;
    }

    /// <summary>The families the generated expressions used.</summary>
    internal HashSet<string> Tags { get; } = [Fam.Expr];

    internal int MaxDepth { get; set; } = 3;

    private static readonly string[] DateTexts =
        ["'2024-02-29'", "'2024-02-29 12:30:45'", "'2023-12-31T23:59:59'", "'12:30:45'", "'2460000.5'", "1700000000", "'2024-13-01'", "'0000-01-01'", "'9999-12-31'", "'2024-03-10 25:00'", "2460000.5", "'1970-01-01'"];

    private static readonly string[] DateMods =
        ["'+1 day'", "'-3 months'", "'start of month'", "'start of year'", "'start of day'", "'weekday 1'", "'+12 hours'", "'unixepoch'", "'julianday'", "'auto'", "'subsec'", "'ceiling'", "'floor'", "'+1 year'", "'-0001-00-00'", "'+01:30'", "'bogus'"];

    private static readonly string[] JsonTexts =
        ["'{\"a\":1,\"b\":[1,2,3],\"c\":{\"d\":null}}'", "'[1,2,3]'", "'{\"a\":\"x\"}'", "'[]'", "'{'", "'null'", "'\"s\"'", "'{\"a\":1.5e2}'"];

    private static readonly string[] JsonPaths = ["'$.a'", "'$.b[1]'", "'$.c.d'", "'$[0]'", "'$'", "'$.zz'", "'$.b[#-1]'", "'bad'"];

    private static readonly string[] LikePatterns = ["'a%'", "'%b'", "'_b_'", "'%'", "'A%'", "''", "'%a%'", "'a_c'", "'a\\%b'", "'[a-c]*'", "'*b*'", "'?bc'"];

    internal Expr Literal(Affin? a = null)
        => Expr.Raw(SchemaGen.Literal(_r, a ?? _r.Pick(new[] { Affin.Integer, Affin.Text, Affin.Real, Affin.Blob, Affin.Numeric })));

    /// <summary>A literal in the style of the column (used where the test wants a value that suits it).</summary>
    internal Expr Value(ColDef c) => Literal(c.Affinity);

    private Expr Column()
    {
        if (_cols.Count == 0)
        {
            return Literal();
        }

        return Expr.Raw(_r.Pick(_cols).Ref);
    }

    private ScopeCol? ColumnOf(Affin? want = null)
    {
        List<ScopeCol> cs = want is null ? [.. _cols] : [.. _cols.Where(c => c.Col.Affinity == want)];
        return cs.Count == 0 ? null : _r.Pick(cs);
    }

    private Expr Leaf() => _cols.Count > 0 && _r.Chance(60) ? Column() : Literal();

    /// <summary>Any value-producing expression.</summary>
    internal Expr Any() => Gen(0);

    private Expr Gen(int depth)
    {
        if (depth >= MaxDepth || _r.Chance(18))
        {
            return Leaf();
        }

        int k = _r.Weighted((10, 0), (7, 1), (4, 2), (12, 3), (6, 4), (6, 5), (3, 6), (4, 7), (4, 8), (3, 9), (4, 10), (10, 11), (5, 12));
        Expr a() => Gen(depth + 1);
        switch (k)
        {
            case 0:
                return Expr.Node("({0} " + _r.Pick(new[] { "+", "-", "*", "/", "%" }) + " {1})", a(), a());
            case 1:
                return Expr.Node("({0} " + _r.Pick(new[] { "&", "|", "<<", ">>", "||" }) + " {1})", a(), a());
            case 2:
                return Expr.Node("(" + _r.Pick(new[] { "- ", "+ ", "~", "NOT " }) + "{0})", a());
            case 3:
                return Comparison(depth);
            case 4:
                return Expr.Node("({0} " + _r.Pick(new[] { "AND", "OR" }) + " {1})", Boolish(depth + 1), Boolish(depth + 1));
            case 5:
                return Expr.Node("({0} " + _r.Pick(new[] { "IS NULL", "IS NOT NULL", "ISNULL", "NOTNULL", "IS TRUE", "IS NOT TRUE", "IS FALSE" }) + ")", a());
            case 6:
                return In(depth);
            case 7:
                return Expr.Node("({0} " + _r.Pick(new[] { "BETWEEN", "NOT BETWEEN" }) + " {1} AND {2})", a(), a(), a());
            case 8:
                return Expr.Node("(CASE " + (_r.Chance(50) ? "WHEN {0} THEN {1} ELSE {2} END)" : "{0} WHEN {1} THEN {2} END)"), a(), a(), a());
            case 9:
                return Cast(depth);
            case 10:
                return Like(depth);
            case 11:
                return Function(depth);
            default:
                return Collated(depth);
        }
    }

    private Expr Boolish(int depth) => depth >= MaxDepth ? Comparison(depth) : _r.Chance(70) ? Comparison(depth) : Gen(depth);

    private static readonly string[] CmpOps = ["=", "==", "<", "<=", ">", ">=", "<>", "!=", "IS", "IS NOT", "IS DISTINCT FROM", "IS NOT DISTINCT FROM"];

    private Expr Comparison(int depth)
    {
        string op = _r.Pick(CmpOps);
        // Column against literal of a possibly different kind: the affinity cases of datatype3 section 4.
        if (_cols.Count > 0 && _r.Chance(50))
        {
            ScopeCol c = _r.Pick(_cols);
            bool mismatch = _filter.AllowsExprGroup(Fam.Affinity) && _r.Chance(50);
            Expr lit = mismatch ? Literal() : Literal(c.Col.Affinity);
            if (mismatch)
            {
                Tags.Add(Fam.Affinity);
            }

            return _r.Chance(50)
                ? Expr.Node("(" + c.Ref + " " + op + " {0})", lit)
                : Expr.Node("({0} " + op + " " + c.Ref + ")", lit);
        }

        if (_cols.Count > 1 && _r.Chance(50) && _filter.AllowsExprGroup(Fam.Affinity))
        {
            Tags.Add(Fam.Affinity);
            return Expr.Raw("(" + _r.Pick(_cols).Ref + " " + op + " " + _r.Pick(_cols).Ref + ")");
        }

        return Expr.Node("({0} " + op + " {1})", Gen(depth + 1), Gen(depth + 1));
    }

    private Expr In(int depth)
    {
        Expr x = _cols.Count > 0 && _r.Chance(60) ? Column() : Gen(depth + 1);
        int n = _r.Range(0, 4);
        var items = new List<Expr> { x };
        for (int i = 0; i < n; i++)
        {
            items.Add(_r.Chance(70) ? Literal() : Gen(depth + 1));
        }

        string list = string.Join(", ", Enumerable.Range(1, n).Select(i => "{" + i + "}"));
        return Expr.Node("({0} " + (_r.Chance(30) ? "NOT IN" : "IN") + " (" + list + "))", [.. items]);
    }

    private Expr Cast(int depth)
    {
        if (!_filter.AllowsExprGroup(Fam.Cast))
        {
            return Leaf();
        }

        Tags.Add(Fam.Cast);
        string t = _r.Pick(new[] { "INTEGER", "TEXT", "REAL", "NUMERIC", "BLOB", "INT", "VARCHAR(3)", "FLOAT", "BOOLEAN", "" });
        return Expr.Node("CAST({0} AS " + t + ")", Gen(depth + 1));
    }

    private Expr Collated(int depth)
    {
        if (!_filter.AllowsExprGroup(Fam.Collation))
        {
            return Leaf();
        }

        Tags.Add(Fam.Collation);
        string coll = _r.Pick(new[] { "NOCASE", "RTRIM", "BINARY", "nocase" });
        Expr x = _cols.Count > 0 && _r.Chance(60) ? Column() : Gen(depth + 1);
        string op = _r.Pick(new[] { "=", "<", ">=", "<>", "IN (", "LIKE", "BETWEEN" });
        return op switch
        {
            "IN (" => Expr.Node("({0} COLLATE " + coll + " IN ({1}, {2}))", x, Literal(Affin.Text), Literal(Affin.Text)),
            "BETWEEN" => Expr.Node("({0} COLLATE " + coll + " BETWEEN {1} AND {2})", x, Literal(Affin.Text), Literal(Affin.Text)),
            _ => Expr.Node("({0} COLLATE " + coll + " " + op + " {1})", x, _r.Chance(50) ? Literal(Affin.Text) : Gen(depth + 1)),
        };
    }

    private Expr Like(int depth)
    {
        if (!_filter.AllowsExprGroup(Fam.Like))
        {
            return Leaf();
        }

        Tags.Add(Fam.Like);
        Expr x = _cols.Count > 0 && _r.Chance(70) ? Column() : Gen(depth + 1);
        string pat = _r.Pick(LikePatterns);
        string neg = _r.Chance(20) ? "NOT " : "";
        return _r.Weighted((6, 0), (3, 1), (1, 2)) switch
        {
            0 => Expr.Node("({0} " + neg + "LIKE " + pat + ")", x),
            1 => Expr.Node("({0} " + neg + "GLOB " + pat + ")", x),
            _ => Expr.Node("({0} " + neg + "LIKE " + pat + " ESCAPE '\\')", x),
        };
    }

    private Expr Function(int depth)
    {
        var groups = new List<string> { Fam.Core };
        groups.AddRange(new[] { Fam.Str, Fam.Str, Fam.Math, Fam.Math, Fam.DateTime, Fam.Json });
        groups = [.. groups.Where(_filter.AllowsExprGroup)];
        if (groups.Count == 0)
        {
            return Leaf();
        }

        string g = _r.Pick(groups);
        Tags.Add(g);
        Expr a() => Gen(depth + 1);
        switch (g)
        {
            case Fam.Core:
                return _r.Next(13) switch
                {
                    0 => Expr.Node("typeof({0})", a()),
                    1 => Expr.Node("coalesce({0}, {1})", a(), a()),
                    2 => Expr.Node("ifnull({0}, {1})", a(), a()),
                    3 => Expr.Node("nullif({0}, {1})", a(), a()),
                    4 => Expr.Node("iif({0}, {1}, {2})", a(), a(), a()),
                    5 => Expr.Node("abs({0})", a()),
                    6 => Expr.Node("min({0}, {1})", a(), a()),
                    7 => Expr.Node("max({0}, {1}, {2})", a(), a(), a()),
                    8 => Expr.Node("length({0})", a()),
                    9 => Expr.Node("octet_length({0})", a()),
                    10 => Expr.Node("quote({0})", a()),
                    11 => Expr.Node("hex({0})", a()),
                    _ => Expr.Node("coalesce({0}, {1}, {2})", a(), a(), a()),
                };
            case Fam.Str:
                return _r.Next(14) switch
                {
                    0 => Expr.Node("lower({0})", a()),
                    1 => Expr.Node("upper({0})", a()),
                    2 => Expr.Node("substr({0}, {1})", a(), a()),
                    3 => Expr.Node("substr({0}, {1}, {2})", a(), a(), a()),
                    4 => Expr.Node("trim({0})", a()),
                    5 => Expr.Node("ltrim({0}, {1})", a(), Literal(Affin.Text)),
                    6 => Expr.Node("rtrim({0}, {1})", a(), Literal(Affin.Text)),
                    7 => Expr.Node("replace({0}, {1}, {2})", a(), Literal(Affin.Text), Literal(Affin.Text)),
                    8 => Expr.Node("instr({0}, {1})", a(), a()),
                    9 => Expr.Node("printf(" + _r.Pick(new[] { "'%d'", "'%s'", "'%5.2f'", "'%x'", "'%q'", "'%.3s'", "'%05d'", "'%e'", "'%g'", "'%c'", "'%-6s|'", "'%+d'", "'%i'", "'%Q'", "'%w'", "'%z'" }) + ", {0})", a()),
                    10 => Expr.Node("unicode({0})", a()),
                    11 => Expr.Node("char({0}, {1})", Expr.Raw(_r.Range(0, 0x2fff).ToString(CultureInfo.InvariantCulture)), Expr.Raw(_r.Range(32, 126).ToString(CultureInfo.InvariantCulture))),
                    12 => Expr.Node("concat({0}, {1}, {2})", a(), a(), a()),
                    _ => Expr.Node("concat_ws({0}, {1}, {2})", Literal(Affin.Text), a(), a()),
                };
            case Fam.Math:
                return _r.Next(15) switch
                {
                    0 => Expr.Node("round({0})", a()),
                    1 => Expr.Node("round({0}, {1})", a(), Expr.Raw(_r.Range(-1, 4).ToString(CultureInfo.InvariantCulture))),
                    2 => Expr.Node("ceil({0})", a()),
                    3 => Expr.Node("floor({0})", a()),
                    4 => Expr.Node("trunc({0})", a()),
                    5 => Expr.Node("sqrt({0})", a()),
                    6 => Expr.Node("ln({0})", a()),
                    7 => Expr.Node("log({0}, {1})", a(), a()),
                    8 => Expr.Node("exp({0})", a()),
                    9 => Expr.Node("pow({0}, {1})", a(), a()),
                    10 => Expr.Node("mod({0}, {1})", a(), a()),
                    11 => Expr.Node(_r.Pick(new[] { "sin", "cos", "tan", "atan", "asin", "acos", "sinh", "tanh", "degrees", "radians", "log2", "log10" }) + "({0})", a()),
                    12 => Expr.Node("atan2({0}, {1})", a(), a()),
                    13 => Expr.Node("sign({0})", a()),
                    _ => Expr.Raw("pi()"),
                };
            case Fam.DateTime:
                return DateFn(depth);
            default:
                return JsonFn(depth);
        }
    }

    private Expr DateArg(int depth)
        => _r.Chance(60) ? Expr.Raw(_r.Pick(DateTexts)) : _cols.Count > 0 && _r.Chance(60) ? Column() : Gen(depth + 1);

    private Expr DateFn(int depth)
    {
        int nm = _r.Weighted((4, 0), (3, 1), (2, 2));
        string mods = string.Concat(Enumerable.Range(0, nm).Select(_ => ", " + _r.Pick(DateMods)));
        Expr x = DateArg(depth);
        return _r.Next(8) switch
        {
            0 => Expr.Node("date({0}" + mods + ")", x),
            1 => Expr.Node("time({0}" + mods + ")", x),
            2 => Expr.Node("datetime({0}" + mods + ")", x),
            3 => Expr.Node("julianday({0}" + mods + ")", x),
            4 => Expr.Node("unixepoch({0}" + mods + ")", x),
            5 => Expr.Node("strftime(" + _r.Pick(new[] { "'%Y-%m-%d'", "'%H:%M:%f'", "'%j %w %W'", "'%s'", "'%J'", "'%Y%m%d%H%M%S'", "'%%'", "'%a %b'", "'%G-%V'", "'%q'" }) + ", {0}" + mods + ")", x),
            6 => Expr.Node("timediff({0}, {1})", x, DateArg(depth)),
            _ => Expr.Node("date({0}" + mods + ")", x),
        };
    }

    private Expr JsonFn(int depth)
    {
        Expr j = _cols.Count > 0 && _r.Chance(35) ? Column() : Expr.Raw(_r.Pick(JsonTexts));
        string p = _r.Pick(JsonPaths);
        Expr a() => Gen(depth + 1);
        return _r.Next(10) switch
        {
            0 => Expr.Node("json_valid({0})", j),
            1 => Expr.Node("json_type({0}, " + p + ")", j),
            2 => Expr.Node("json_extract({0}, " + p + ")", j),
            3 => Expr.Node("json_array({0}, {1})", a(), a()),
            4 => Expr.Node("json_object('k', {0})", a()),
            5 => Expr.Node("({0} -> " + p + ")", j),
            6 => Expr.Node("({0} ->> " + p + ")", j),
            7 => Expr.Node("json_array_length({0})", j),
            8 => Expr.Node("json_quote({0})", a()),
            _ => Expr.Node("json({0})", j),
        };
    }

    /// <summary>A WHERE-style condition: a plain comparison an index could serve, or an arbitrary expression.</summary>
    internal Expr Predicate()
    {
        if (_cols.Count > 0 && _r.Chance(45))
        {
            return Sargable();
        }

        return _r.Chance(60) ? Comparison(0) : Gen(0);
    }

    private Expr Sargable()
    {
        ScopeCol c = _r.Pick(_cols);
        Affin a = c.Col.Affinity;
        Expr lit() => _filter.AllowsExprGroup(Fam.Affinity) && _r.Chance(25) ? Literal() : Literal(a);
        switch (_r.Next(8))
        {
            case 0:
                return Expr.Node("(" + c.Ref + " = {0})", lit());
            case 1:
                return Expr.Node("(" + c.Ref + " " + _r.Pick(new[] { "<", "<=", ">", ">=" }) + " {0})", lit());
            case 2:
                return Expr.Node("(" + c.Ref + " BETWEEN {0} AND {1})", lit(), lit());
            case 3:
                return Expr.Node("(" + c.Ref + " IN ({0}, {1}, {2}))", lit(), lit(), lit());
            case 4:
                return Expr.Raw("(" + c.Ref + " IS NULL)");
            case 5:
                return Expr.Raw("(" + c.Ref + " LIKE " + _r.Pick(LikePatterns) + ")");
            case 6:
                return Expr.Node("({0} = " + c.Ref + ")", lit());
            default:
                return Expr.Node("(" + c.Ref + " " + _r.Pick(new[] { "IS", "IS NOT", "<>" }) + " {0})", lit());
        }
    }

    /// <summary>A single-table condition for the metamorphic oracles.</summary>
    internal Expr Condition()
    {
        if (_r.Chance(55))
        {
            Expr first = Sargable();
            return _r.Chance(25) ? Expr.Node("({0} " + _r.Pick(new[] { "AND", "OR" }) + " {1})", first, Predicate()) : first;
        }

        return Gen(0);
    }

    internal ScopeCol? PickColumn(Affin? want = null) => ColumnOf(want);
}
