using System.Globalization;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.Differential;

/// <summary>Turns a seed into a database and the checks to run on it.</summary>
internal sealed class CaseGen
{
    private readonly Rng _r;
    private readonly CaseSetup _s;
    private readonly Fam.Filter _f;

    private CaseGen(Rng r, CaseSetup s, Fam.Filter f)
    {
        _r = r;
        _s = s;
        _f = f;
    }

    /// <summary>The checks of one case.</summary>
    internal sealed class Plan
    {
        internal CaseSetup Setup = new();
        internal List<Check> Checks = [];
    }

    /// <summary>
    /// Builds the database of case <paramref name="index"/> at <paramref name="path"/> through the pinned library and generates its checks.
    /// Deterministic: the statements depend only on the seed, the index, the families and the oracle list.
    /// </summary>
    internal static Plan Generate(long seed, long index, Fam.Filter filter, ISet<string> oracles, string path)
    {
        var r = Rng.ForCase(seed, index);
        var plan = new Plan();
        using (SqliteDb db = SqliteDb.Open(path))
        {
            plan.Setup = SchemaGen.Build(r, db, filter);
            if (filter.Names(Fam.Jsonb) && !filter.Names(Fam.Json) && !Fam.Shapes.Any(filter.Names))
            {
                // A JSONB byte-identity run: four documents per statement, each as stored bytes and read back through the engine's own blob.
                var jb = new JsonGen(r);
                int bn = r.Range(60, 120);
                for (int i = 0; i < bn; i++)
                {
                    plan.Checks.Add(new DiffCheck(jb.JsonbStatement(), null, false) { Families = [Fam.Jsonb] });
                }

                return Finish(plan);
            }

            if (filter.Names(Fam.Json) && !Fam.Shapes.Any(filter.Names))
            {
                // A run that names only the json group is a JSON campaign: documents, paths and every function of json1.
                var jg = new JsonGen(r);
                int jn = r.Range(60, 120);
                for (int i = 0; i < jn; i++)
                {
                    plan.Checks.Add(new DiffCheck(jg.Statement(), null, false) { Families = [Fam.Json] });
                }

                return Finish(plan);
            }

            if (plan.Setup.Tables.Count == 0)
            {
                return plan;
            }

            var g = new CaseGen(r, plan.Setup, filter);
            int n = r.Range(25, 60);
            if (oracles.Contains("diff"))
            {
                for (int i = 0; i < n; i++)
                {
                    plan.Checks.Add(g.Diff());
                }
            }

            for (int i = 0; i < 8; i++)
            {
                if (oracles.Contains("norec"))
                {
                    plan.Checks.Add(g.Metamorphic("norec", db));
                }

                if (oracles.Contains("tlp"))
                {
                    plan.Checks.Add(g.Metamorphic("tlp", db));
                }

                if (oracles.Contains("pqs"))
                {
                    Check? pqs = g.Metamorphic("pqs", db);
                    if (pqs is not null)
                    {
                        plan.Checks.Add(pqs);
                    }
                }
            }
        }

        return Finish(plan);
    }

    private static Plan Finish(Plan plan)
    {
        foreach (Check c in plan.Checks)
        {
            c.Utf16 = plan.Setup.Utf16;
        }

        return plan;
    }

    private static List<ScopeCol> Scope(TableDef t, string qualifier) => [.. t.Cols.Select(c => new ScopeCol(c.Name, c, qualifier))];

    private static bool HasId(TableDef t) => t.Find("id") is not null;

    private TableDef AnyTable() => _r.Pick(_s.Tables);

    private TableDef BaseTable()
    {
        List<TableDef> l = [.. _s.Tables.Where(t => !t.IsView)];
        return _r.Pick(l);
    }

    private Check Metamorphic(string oracle, SqliteDb db)
    {
        TableDef t = BaseTable();
        var g = new ExprGen(_r, _f, Scope(t, ""));
        Expr p = g.Condition();
        Check c;
        switch (oracle)
        {
            case "norec":
                c = new NoRecCheck(t.Name, p);
                break;
            case "tlp":
                c = new TlpCheck(t.Name, p);
                break;
            default:
                List<string[]> ids = db.QueryCanonical($"SELECT id FROM {t.Name}");
                if (ids.Count == 0)
                {
                    return new TlpCheck(t.Name, p) { Families = [Fam.Expr] };
                }

                long pivot = long.Parse(_r.Pick(ids)[0].AsSpan(2), CultureInfo.InvariantCulture);
                c = new PqsCheck(t.Name, p, pivot);
                break;
        }

        c.Families = [.. g.Tags];
        return c;
    }

    // ---- plain differential queries, by shape ----

    private static readonly string[] ShapeNames = [Fam.Select, Fam.OrderLimit, Fam.Distinct, Fam.Aggregate, Fam.Join, Fam.Subquery, Fam.Cte, Fam.Window, Fam.Compound, Fam.View, Fam.Series, Fam.Pragma, Fam.InSeek];

    private string PickShape()
    {
        List<string> named = [.. ShapeNames.Where(s => _f.Any && _f.Names(s))];
        if (_f.Any && named.Count == 0)
        {
            named = [Fam.Select, Fam.OrderLimit];
        }

        if (named.Count > 0)
        {
            return _r.Pick(named);
        }

        return _r.Weighted((6, Fam.Select), (4, Fam.OrderLimit), (2, Fam.Distinct), (4, Fam.Aggregate), (4, Fam.Join), (4, Fam.Subquery), (3, Fam.Cte), (3, Fam.Window), (3, Fam.Compound), (2, Fam.View), (1, Fam.Series), (1, Fam.Pragma));
    }

    private Check Diff()
    {
        string shape = PickShape();
        var tags = new HashSet<string> { shape, Fam.Expr };
        (Select? q, string? raw, bool ordered) = Build(shape, tags);
        string sql = raw ?? q!.ToString();
        return new DiffCheck(sql, q, ordered) { Families = tags };
    }

    private ExprGen Gen(HashSet<string> tags, List<ScopeCol> scope, int depth = 3)
    {
        var g = new ExprGen(_r, _f, scope) { MaxDepth = depth };
        tags.UnionWith(g.Tags);
        return g;
    }

    private static void Absorb(HashSet<string> tags, ExprGen g) => tags.UnionWith(g.Tags);

    private List<Expr> Columns(ExprGen g, TableDef t, string qual)
    {
        var cols = new List<Expr>();
        int n = _r.Range(1, 3);
        for (int i = 0; i < n; i++)
        {
            cols.Add(_r.Chance(25) ? Expr.Raw(Pre(qual) + _r.Pick(t.Cols).Name) : g.Any());
        }

        return cols;
    }

    private static string Pre(string qual) => qual.Length == 0 ? "" : qual + ".";

    private (Select? Q, string? Raw, bool Ordered) Build(string shape, HashSet<string> tags)
    {
        switch (shape)
        {
            case Fam.OrderLimit: return Simple(tags, orderLimit: true, distinct: false, view: false);
            case Fam.Distinct: return Simple(tags, orderLimit: false, distinct: true, view: false);
            case Fam.View: return Simple(tags, orderLimit: _r.Chance(30), distinct: false, view: _s.Tables.Any(t => t.IsView));
            case Fam.Aggregate: return Aggregate(tags);
            case Fam.Join: return Join(tags);
            case Fam.Subquery: return Subquery(tags);
            case Fam.Cte: return Cte(tags);
            case Fam.Window: return Window(tags);
            case Fam.Compound: return Compound(tags);
            case Fam.Series: return Series(tags);
            case Fam.Pragma: return Pragma();
            case Fam.InSeek: return InSeek(tags);
            default: return Simple(tags, orderLimit: false, distinct: false, view: false);
        }
    }

    private (Select?, string?, bool) Simple(HashSet<string> tags, bool orderLimit, bool distinct, bool view)
    {
        TableDef t = view ? _r.Pick(_s.Tables.Where(x => x.IsView).ToList()) : AnyTable();
        // A SELECT with no FROM never touches the file.
        bool noFrom = !orderLimit && _r.Chance(10);
        List<ScopeCol> scope = noFrom ? [] : Scope(t, "");
        ExprGen g = Gen(tags, scope);
        var q = new Select { Distinct = distinct };
        if (noFrom)
        {
            q.Cols = [g.Any(), g.Any()];
            Absorb(tags, g);
            return (q, null, false);
        }

        q.From = t.Name;
        q.Cols = _r.Chance(15) ? [Expr.Raw("*")] : Columns(g, t, "");
        if (_r.Chance(15) && !t.IsView && !t.WithoutRowid)
        {
            q.Cols.Add(Expr.Raw(_r.Pick(new[] { "rowid", "_rowid_", "oid" })));
        }

        if (_r.Chance(60))
        {
            q.Where = g.Predicate();
        }

        bool ordered = false;
        if (HasId(t) && !distinct && _r.Chance(orderLimit ? 100 : 30))
        {
            if (orderLimit)
            {
                tags.Add(Fam.OrderLimit);
                int nk = _r.Range(0, 2);
                for (int i = 0; i < nk; i++)
                {
                    Expr key = _r.Chance(50) ? Expr.Raw(_r.Pick(t.Cols).Name) : g.Any();
                    string dir = _r.Weighted((4, ""), (3, " DESC"), (1, " ASC"), (1, " DESC NULLS LAST"), (1, " NULLS FIRST"), (1, " COLLATE NOCASE"));
                    q.Order.Add(Expr.Node("{0}" + dir, key));
                }

                if (_r.Chance(25))
                {
                    q.Order.Insert(0, Expr.Raw(_r.Range(1, q.Cols.Count).ToString(CultureInfo.InvariantCulture)));
                }

                if (q.Cols.Count == 1 && q.Cols[0].ToString() == "*")
                {
                    q.Order.Clear();
                }
            }

            q.Order.Add(Expr.Raw("id"));
            ordered = true;
            if (orderLimit && _r.Chance(70))
            {
                q.Limit = _r.Chance(80) ? Expr.Raw(_r.Range(0, 12).ToString(CultureInfo.InvariantCulture)) : Expr.Raw(_r.Pick(new[] { "-1", "1 + 1", "NULL", "'3'", "2.0", "(SELECT 3)" }));
                if (_r.Chance(40))
                {
                    q.Offset = Expr.Raw(_r.Range(0, 8).ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        Absorb(tags, g);
        return (q, null, ordered);
    }

    private (Select?, string?, bool) Aggregate(HashSet<string> tags)
    {
        TableDef t = AnyTable();
        ExprGen g = Gen(tags, Scope(t, ""), 2);
        var q = new Select { From = t.Name };
        string x() => _r.Chance(70) ? _r.Pick(t.Cols).Name : g.Any().ToString();
        var groups = new List<Expr>();
        if (_r.Chance(65))
        {
            int ng = _r.Range(1, 2);
            for (int i = 0; i < ng; i++)
            {
                groups.Add(_r.Chance(70) ? Expr.Raw(_r.Pick(t.Cols).Name) : g.Any());
            }
        }

        q.Group = groups;
        q.Cols.AddRange(groups);
        int na = _r.Range(1, 3);
        for (int i = 0; i < na; i++)
        {
            string a = _r.Next(16) switch
            {
                12 => $"median({x()})",
                13 => $"{_r.Pick(new[] { "percentile", "percentile_cont", "percentile_disc" })}({x()}, {_r.Pick(new[] { "0", "0.25", "0.5", "0.9", "1" })}{(_r.Chance(30) ? " * 100" : "")})",
                14 => _r.Chance(50) ? $"json_group_array({x()})" : $"json_group_object({x()}, {x()})",
                15 => $"percentile({x()}, {_r.Pick(new[] { "5", "25", "50", "75", "100", "33.3" })}) FILTER (WHERE {g.Predicate()})",
                0 => "count(*)",
                1 => $"count({x()})",
                2 => $"count(DISTINCT {x()})",
                3 => $"sum({x()})",
                4 => $"total({x()})",
                5 => $"avg({x()})",
                6 => $"min({x()})",
                7 => $"max({x()})",
                8 => $"sum(DISTINCT {x()})",
                9 => $"count(*) FILTER (WHERE {g.Predicate()})",
                10 => $"min({x()}, {x()})",
                _ => $"max({x()}) + 1",
            };
            q.Cols.Add(Expr.Raw(a));
        }

        if (_r.Chance(35))
        {
            q.Where = g.Predicate();
        }

        if (groups.Count > 0 && _r.Chance(30))
        {
            q.Having = Expr.Raw(_r.Pick(new[] { "count(*) > 1", "count(*) = 1", "min(id) > 3", "sum(1) >= 2" }));
        }

        Absorb(tags, g);
        return (q, null, false);
    }

    private TableDef WithId() => _r.Pick(_s.Tables.Where(HasId).ToList());

    private (Select?, string?, bool) Join(HashSet<string> tags)
    {
        List<TableDef> with = [.. _s.Tables.Where(t => HasId(t) && !t.IsView)];
        if (with.Count == 0)
        {
            return Simple(tags, false, false, false);
        }

        TableDef a = _r.Pick(with), b = _r.Pick(with);
        var scope = Scope(a, "a");
        scope.AddRange(Scope(b, "b"));
        ExprGen g = Gen(tags, scope, 2);
        var q = new Select();
        string kind = _r.Weighted((5, "JOIN"), (4, "LEFT JOIN"), (2, "CROSS JOIN"), (2, "JOIN USING"), (1, "NATURAL JOIN"), (2, "COMMA"), (1, "RIGHT JOIN"), (1, "FULL JOIN"), (1, "LEFT OUTER JOIN"));
        string on = _r.Chance(50) ? $"a.{_r.Pick(a.Cols).Name} = b.{_r.Pick(b.Cols).Name}" : g.Predicate().ToString();
        q.From = kind switch
        {
            "COMMA" => $"{a.Name} AS a, {b.Name} AS b",
            "CROSS JOIN" => $"{a.Name} AS a CROSS JOIN {b.Name} AS b",
            "JOIN USING" => $"{a.Name} AS a JOIN {b.Name} AS b USING (id)",
            "NATURAL JOIN" => $"{a.Name} AS a NATURAL JOIN {b.Name} AS b",
            _ => $"{a.Name} AS a {kind} {b.Name} AS b ON {on}",
        };
        q.Cols = [Expr.Raw("a.id"), Expr.Raw("b.id")];
        if (_r.Chance(60))
        {
            q.Cols.Add(g.Any());
        }

        if (kind == "COMMA" || kind == "CROSS JOIN" || _r.Chance(30))
        {
            q.Where = g.Predicate();
        }

        q.Order = [Expr.Raw("a.id"), Expr.Raw("b.id")];
        Absorb(tags, g);
        return (q, null, true);
    }

    private (Select?, string?, bool) Subquery(HashSet<string> tags)
    {
        TableDef a = AnyTable(), b = AnyTable();
        var scope = Scope(a, "a");
        ExprGen g = Gen(tags, scope, 2);
        var inner = new ExprGen(_r, _f, Scope(b, "b")) { MaxDepth = 2 };
        string ac = _r.Pick(a.Cols).Name, bc = _r.Pick(b.Cols).Name;
        var q = new Select { From = a.Name + " AS a" };
        bool ordered = HasId(a);
        if (ordered)
        {
            q.Order = [Expr.Raw("a.id")];
        }

        string ac2 = _r.Pick(a.Cols).Name, bc2 = _r.Pick(b.Cols).Name;
        string rowOp = _r.Pick(new[] { "=", "<>", "<", "<=", ">", ">=", "IS", "IS NOT" });
        switch (_r.Next(11))
        {
            case 7:
                // a row value against a subquery of several columns (first row, so the subquery is ordered or an aggregate)
                q.Cols = [Expr.Raw(ordered ? "a.id" : "a." + ac), Expr.Raw($"(a.{ac}, a.{ac2}) {rowOp} (SELECT max(b.{bc}), min(b.{bc2}) FROM {b.Name} AS b)")];
                break;
            case 8:
                q.Cols = [Expr.Raw("a." + ac)];
                q.Where = Expr.Raw($"(a.{ac}, a.{ac2}) {(_r.Chance(30) ? "NOT IN" : "IN")} (SELECT b.{bc}, b.{bc2} FROM {b.Name} AS b)");
                break;
            case 9:
                q.Cols = [Expr.Raw("a." + ac)];
                q.Where = Expr.Raw($"(SELECT {_r.Pick(new[] { "max", "min" })}(b.{bc}), count(*) FROM {b.Name} AS b WHERE b.{bc2} {_r.Pick(new[] { "<", "=", ">" })} a.{ac2}) {rowOp} (a.{ac}, {_r.Range(0, 5)})");
                break;
            case 10:
                q.Cols = [Expr.Raw("a." + ac)];
                q.Where = Expr.Raw($"(a.{ac}, a.{ac2}) BETWEEN (SELECT min(b.{bc}), min(b.{bc2}) FROM {b.Name} AS b) AND (SELECT max(b.{bc}), max(b.{bc2}) FROM {b.Name} AS b)");
                break;
            case 0:
                q.Cols = [Expr.Raw(ordered ? "a.id" : "a." + ac), Expr.Raw($"(SELECT max(b.{bc}) FROM {b.Name} AS b WHERE b.{bc} {_r.Pick(new[] { "<", "=", ">" })} a.{ac})")];
                break;
            case 1:
                q.Cols = [Expr.Raw("a." + ac)];
                q.Where = Expr.Raw($"a.{ac} {(_r.Chance(30) ? "NOT IN" : "IN")} (SELECT b.{bc} FROM {b.Name} AS b WHERE {inner.Predicate()})");
                break;
            case 2:
                q.Cols = [Expr.Raw("a." + ac)];
                q.Where = Expr.Raw($"{(_r.Chance(30) ? "NOT " : "")}EXISTS (SELECT 1 FROM {b.Name} AS b WHERE b.{bc} = a.{ac})");
                break;
            case 3:
                q = new Select { From = $"(SELECT * FROM {a.Name}) AS s", Cols = [Expr.Raw("s.*")] };
                ordered = HasId(a);
                q.Order = ordered ? [Expr.Raw("s.id")] : [];
                var gs = new ExprGen(_r, _f, Scope(a, "s")) { MaxDepth = 2 };
                q.Where = gs.Predicate();
                Absorb(tags, gs);
                break;
            case 4:
                q.Cols = [Expr.Raw("a." + ac), Expr.Raw($"(SELECT count(*) FROM {b.Name} AS b)")];
                q.Where = g.Predicate();
                break;
            case 5:
                q.Cols = [Expr.Raw("a." + ac), Expr.Raw($"a.{ac} IN (SELECT {bc} FROM {b.Name})")];
                break;
            default:
                q.Cols = [Expr.Raw("a." + ac)];
                q.Where = Expr.Raw($"a.{ac} = (SELECT {(_r.Chance(50) ? "min" : "max")}(b.{bc}) FROM {b.Name} AS b)");
                break;
        }

        Absorb(tags, g);
        return (q, null, ordered);
    }

    // x IN (list) and x IN (SELECT ...) on the rowid, indexed and plain columns: duplicates, NULLs, mixed storage classes and a second predicate.
    // Without an ORDER BY the row order is compared too, because seeking walks the distinct values in ascending order.
    private (Select?, string?, bool) InSeek(HashSet<string> tags)
    {
        TableDef t = BaseTable();
        ColDef c = HasId(t) && _r.Chance(45) ? t.Find("id")! : _r.Pick(t.Cols);
        var items = new List<string>();
        int n = _r.Range(1, 6);
        for (int i = 0; i < n; i++)
        {
            if (items.Count > 0 && _r.Chance(15))
            {
                items.Add(_r.Pick(items));
            }
            else if (_r.Chance(8))
            {
                items.Add("NULL");
            }
            else
            {
                items.Add(SchemaGen.Literal(_r, _r.Chance(65) ? c.Affinity : _r.Pick(new[] { Affin.Integer, Affin.Text, Affin.Real, Affin.Numeric })));
            }
        }

        string rhs = _r.Next(3) switch
        {
            0 => "SELECT " + string.Join(" UNION ALL SELECT ", items),
            1 => $"SELECT {_r.Pick(t.Cols).Name} FROM {t.Name}",
            _ => string.Join(", ", items),
        };
        Expr where = Expr.Raw($"{c.Name} IN ({rhs})");
        if (_r.Chance(30))
        {
            var g = new ExprGen(_r, _f, Scope(t, "")) { MaxDepth = 2 };
            where = Expr.Raw($"({where}) AND ({g.Predicate()})");
            Absorb(tags, g);
        }

        var q = new Select { From = t.Name, Cols = [Expr.Raw(HasId(t) ? "id" : c.Name), Expr.Raw(c.Name)], Where = where };
        bool ordered = _r.Chance(60);
        if (!ordered && HasId(t))
        {
            q.Order = [Expr.Raw("id")];
            ordered = true;
        }

        return (q, null, ordered);
    }

    private (Select?, string?, bool) Cte(HashSet<string> tags)
    {
        TableDef t = AnyTable();
        if (_r.Chance(40))
        {
            int hi = _r.Range(1, 12);
            string start = _r.Pick(new[] { "1", "0", "-3", "5" });
            string step = _r.Pick(new[] { "1", "2", "3" });
            TableDef w = WithId();
            string union = _r.Chance(50) ? "UNION ALL" : "UNION";
            var rq = new Select
            {
                Prefix = $"WITH RECURSIVE r(n) AS (SELECT {start} {union} SELECT n + {step} FROM r WHERE n < {hi}) ",
                From = "r",
                Cols = [Expr.Raw("n")],
                Order = [Expr.Raw("n")],
            };
            if (_r.Chance(50))
            {
                rq.From = $"r JOIN {w.Name} AS t ON t.id = r.n";
                rq.Cols = [Expr.Raw("r.n"), Expr.Raw("t.id")];
            }

            return (rq, null, true);
        }

        ExprGen g = Gen(tags, Scope(t, ""), 2);
        string mat = _r.Weighted((6, ""), (2, "MATERIALIZED "), (2, "NOT MATERIALIZED "));
        string body = $"SELECT * FROM {t.Name}" + (_r.Chance(50) ? $" WHERE {g.Predicate()}" : "");
        var q = new Select { Prefix = $"WITH c AS {mat}({body}) ", From = "c" };
        var gc = new ExprGen(_r, _f, Scope(t, "")) { MaxDepth = 2 };
        q.Cols = Columns(gc, t, "");
        if (_r.Chance(50))
        {
            q.Where = gc.Predicate();
        }

        bool ordered = HasId(t);
        if (ordered)
        {
            q.Order = [Expr.Raw("id")];
        }

        Absorb(tags, g);
        Absorb(tags, gc);
        return (q, null, ordered);
    }

    // Window functions: partitions, orders with ties and NULLs, every frame unit with offsets and exclusions, FILTER, named and chained
    // windows, several windows in one statement, and windows over grouped rows. The rows are ordered by id unless the case leaves the
    // order to the windows, where the reference's order is compared as a multiset.
    private (Select?, string?, bool) Window(HashSet<string> tags)
    {
        // WITHOUT ROWID tables are left out: the reference scans their secondary indexes in index order, which the engine does not do yet,
        // and the order rows reach a window shows in its results.
        List<TableDef> with = [.. _s.Tables.Where(x => HasId(x) && !x.WithoutRowid)];
        if (with.Count == 0)
        {
            return Simple(tags, false, false, false);
        }

        TableDef t = _r.Pick(with);
        string x() => _r.Pick(t.Cols).Name;
        string num() => _r.Pick(t.Cols).Name;

        string Order(bool allowEmpty)
        {
            if (allowEmpty && _r.Chance(25))
            {
                return "";
            }

            int n = _r.Chance(75) ? 1 : _r.Range(2, 3);
            var terms = new List<string>();
            for (int i = 0; i < n; i++)
            {
                string term = _r.Chance(8) ? "id" : x();
                if (_r.Chance(15))
                {
                    term += " COLLATE " + _r.Pick(new[] { "NOCASE", "RTRIM", "BINARY" });
                }

                if (_r.Chance(40))
                {
                    term += " DESC";
                }

                if (_r.Chance(15))
                {
                    term += _r.Chance(50) ? " NULLS FIRST" : " NULLS LAST";
                }

                terms.Add(term);
            }

            return "ORDER BY " + string.Join(", ", terms);
        }

        string Partition() => _r.Chance(40) ? "PARTITION BY " + (_r.Chance(25) ? x() + ", " + x() : x()) + " " : "";
        string Offset() => _r.Weighted((6, _r.Range(0, 3).ToString(CultureInfo.InvariantCulture)), (1, "1.0"), (1, "'1'"), (1, "1+1"), (1, "0.5"), (1, "?"), (1, "NULL"), (1, "-1"), (1, "9223372036854775807"));
        string Bound(bool start, string unit)
        {
            string b = _r.Weighted((3, "UNBOUNDED"), (4, "N"), (3, "CURRENT"));
            string dir = _r.Chance(50) ? "PRECEDING" : "FOLLOWING";
            string off = unit == "RANGE" ? _r.Weighted((4, _r.Range(0, 3).ToString(CultureInfo.InvariantCulture)), (2, "0.5"), (1, "1e300"), (1, "'2'")) : Offset();
            if (off == "?")
            {
                off = "1";
            }

            return b switch
            {
                "UNBOUNDED" => start ? "UNBOUNDED PRECEDING" : "UNBOUNDED FOLLOWING",
                "CURRENT" => "CURRENT ROW",
                _ => off + " " + dir,
            };
        }

        string Frame(bool allowRangeOffset)
        {
            if (_r.Chance(30))
            {
                return "";
            }

            string unit = _r.Weighted((5, "ROWS"), (3, "RANGE"), (3, "GROUPS"));
            string f1 = Bound(true, unit), f2 = Bound(false, unit);
            string text = _r.Chance(25) ? $"{unit} {f1}" : $"{unit} BETWEEN {f1} AND {f2}";
            if (unit == "RANGE" && !allowRangeOffset && (text.Contains("PRECEDING", StringComparison.Ordinal) && !text.Contains("UNBOUNDED PRECEDING", StringComparison.Ordinal) || text.Contains(" FOLLOWING", StringComparison.Ordinal) && !text.Contains("UNBOUNDED FOLLOWING", StringComparison.Ordinal)))
            {
                text = unit + " BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW";
            }

            if (_r.Chance(30))
            {
                text += " EXCLUDE " + _r.Pick(new[] { "NO OTHERS", "CURRENT ROW", "GROUP", "TIES" });
            }

            return " " + text;
        }

        string Func(out bool framed)
        {
            framed = true;
            string filter = _r.Chance(15) ? $" FILTER (WHERE {x()} IS NOT NULL)" : "";
            switch (_r.Next(26))
            {
                case 22: return $"median({x()}){filter}";
                case 23: return $"{_r.Pick(new[] { "percentile", "percentile_cont", "percentile_disc" })}({num()}, {_r.Pick(new[] { "0.1", "0.5", "0.75" })}{(_r.Chance(50) ? " * 100" : "")}){filter}";
                case 24: return $"json_group_array({x()}){filter}";
                case 25: return $"json_group_object({x()}, {x()}){filter}";
                case 0: framed = false; return "row_number()";
                case 1: framed = false; return "rank()";
                case 2: framed = false; return "dense_rank()";
                case 3: framed = false; return "percent_rank()";
                case 4: framed = false; return "cume_dist()";
                case 5: framed = false; return $"ntile({_r.Range(1, 4)})";
                case 6: framed = false; return $"lag({x()}{(_r.Chance(40) ? ", " + _r.Range(0, 3) : "")}{(_r.Chance(30) ? ", " + x() : "")})";
                case 7: framed = false; return $"lead({x()}{(_r.Chance(40) ? ", " + _r.Pick(new[] { "1", "2", "0", "-1", "1.5", "'1'", "NULL" }) : "")}{(_r.Chance(30) ? ", 'd'" : "")})";
                case 8: return $"first_value({x()})";
                case 9: return $"last_value({x()})";
                case 10: return $"nth_value({x()}, {_r.Pick(new[] { "1", "2", "3", "'2'", "2.0", "0", "NULL" })})";
                case 11: return $"sum({num()}){filter}";
                case 12: return $"total({num()}){filter}";
                case 13: return $"avg({num()}){filter}";
                case 14: return $"count({x()}){filter}";
                case 15: return $"count(*){filter}";
                case 16: return $"min({x()}){filter}";
                case 17: return $"max({x()}){filter}";
                case 18: return $"group_concat({x()}){filter}";
                case 19: return $"group_concat({x()}, '|'){filter}";
                case 20: return $"sum({x()}) ";
                default: return $"count(DISTINCT {x()})";
            }
        }

        int shape = _r.Next(10);
        if (shape == 9)
        {
            // windows over groups
            string g = x();
            string[] aggs = ["count(*)", $"sum({num()})", $"max({x()})", $"min({x()})"];
            string agg = _r.Pick(aggs);
            string over = $"{Partition().Replace(x(), g, StringComparison.Ordinal)}ORDER BY {g}{(_r.Chance(30) ? " DESC" : "")}{Frame(false)}".Trim();
            string[] outer = [$"sum({agg}) OVER ({over})", $"rank() OVER (ORDER BY {agg})", $"lag({agg}) OVER (ORDER BY {g})", $"row_number() OVER (ORDER BY {g})", $"count(*) OVER ()"];
            return (null, $"SELECT {g}, {agg}, {_r.Pick(outer)} FROM {t.Name} GROUP BY {g}{(_r.Chance(25) ? " HAVING count(*) > 1" : "")} ORDER BY {g}", true);
        }

        int n = _r.Range(1, 3);
        var cols = new List<string> { "id" };
        var named = new List<string>();
        bool useNamed = _r.Chance(30);
        string windowClause = "";
        if (useNamed)
        {
            string wname = "w1";
            windowClause = $" WINDOW {wname} AS ({Partition()}{Order(true)})";
            named.Add(wname);
        }

        for (int i = 0; i < n; i++)
        {
            string f = Func(out bool framed);
            string order = Order(true);
            string part = Partition();
            string frame = Frame(order.Length > 0 && !order.Contains(',', StringComparison.Ordinal));
            if (!framed && frame.Length > 0 && _r.Chance(70))
            {
                frame = "";
            }

            string spec;
            if (named.Count > 0 && _r.Chance(60))
            {
                spec = _r.Chance(50) ? "w1" : $"(w1{(_r.Chance(50) ? " " + frame.Trim() : "")})";
                cols.Add($"{f} OVER {spec}");
            }
            else
            {
                cols.Add($"{f} OVER ({part}{order}{frame})".Replace("( ", "(", StringComparison.Ordinal));
            }
        }

        string extra = _r.Chance(15) ? $", {x()}" : "";
        bool ordered = _r.Chance(85);
        string distinct = _r.Chance(8) ? "DISTINCT " : "";
        string sql = $"SELECT {distinct}{string.Join(", ", cols)}{extra} FROM {t.Name}{windowClause}";
        if (ordered)
        {
            sql += _r.Chance(15) ? " ORDER BY id DESC LIMIT 3" : " ORDER BY id";
        }

        if (_r.Chance(8))
        {
            sql = $"SELECT * FROM ({sql.Replace(" ORDER BY id DESC LIMIT 3", string.Empty, StringComparison.Ordinal)}) WHERE id % 2 = 0";
            if (ordered)
            {
                sql += " ORDER BY id";
            }
        }

        return (null, sql, ordered && distinct.Length == 0);
    }

    private (Select?, string?, bool) Compound(HashSet<string> tags)
    {
        TableDef a = AnyTable(), b = AnyTable();
        ExprGen ga = Gen(tags, Scope(a, ""), 2), gb = Gen(tags, Scope(b, ""), 2);
        if (_r.Chance(30))
        {
            // The ternary-logic partition written as one compound statement.
            TableDef t = BaseTable();
            var g = new ExprGen(_r, _f, Scope(t, "")) { MaxDepth = 2 };
            string p = g.Condition().ToString();
            Absorb(tags, g);
            return (null, $"SELECT * FROM {t.Name} WHERE ({p}) UNION ALL SELECT * FROM {t.Name} WHERE NOT ({p}) UNION ALL SELECT * FROM {t.Name} WHERE ({p}) IS NULL", false);
        }

        int nc = _r.Range(1, 2);
        var q = new Select { From = a.Name };
        var rq = new Select { From = b.Name };
        for (int i = 0; i < nc; i++)
        {
            q.Cols.Add(_r.Chance(60) ? Expr.Raw(_r.Pick(a.Cols).Name) : ga.Any());
            rq.Cols.Add(_r.Chance(60) ? Expr.Raw(_r.Pick(b.Cols).Name) : gb.Any());
        }

        if (_r.Chance(40))
        {
            q.Where = ga.Predicate();
        }

        if (_r.Chance(40))
        {
            rq.Where = gb.Predicate();
        }

        q.Compound.Add((_r.Pick(new[] { "UNION", "UNION ALL", "INTERSECT", "EXCEPT" }), rq));
        Absorb(tags, ga);
        Absorb(tags, gb);
        return (q, null, false);
    }

    private (Select?, string?, bool) Series(HashSet<string> tags)
    {
        string a = _r.Pick(new[] { "1", "0", "-3", "5", "10", "2", "NULL", "'2'", "1.5" });
        string b = _r.Pick(new[] { "5", "10", "0", "-4", "20", "NULL", "3", "9223372036854775807" });
        string step = _r.Pick(new[] { "1", "2", "3", "-1", "-2", "0", "5" });
        string stop = b == "9223372036854775807" ? "7" : b;
        var q = new Select { From = $"generate_series({a}, {stop}" + (_r.Chance(70) ? $", {step})" : ")"), Cols = [Expr.Raw("value")] };
        if (_r.Chance(40))
        {
            q.Where = Expr.Raw("value % 2 = 1");
        }

        q.Order = [Expr.Raw("value")];
        return (q, null, true);
    }

    private (Select?, string?, bool) Pragma()
    {
        TableDef t = AnyTable();
        string sql = _r.Next(7) switch
        {
            0 => $"PRAGMA table_info({t.Name})",
            1 => $"PRAGMA table_xinfo({t.Name})",
            2 => $"PRAGMA index_list({t.Name})",
            3 => $"PRAGMA foreign_key_list({t.Name})",
            4 => "PRAGMA encoding",
            5 => "PRAGMA user_version",
            _ => "PRAGMA page_size",
        };
        return (null, sql, sql.Contains("table_", StringComparison.Ordinal));
    }
}
