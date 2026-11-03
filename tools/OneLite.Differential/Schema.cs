using System.Globalization;
using System.Text;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.Differential;

/// <summary>The SQL families a campaign can be restricted to, and the tag every generated statement carries.</summary>
internal static class Fam
{
    // Query shapes.
    internal const string Select = "select";
    internal const string OrderLimit = "orderlimit";
    internal const string Distinct = "distinct";
    internal const string Aggregate = "aggregate";
    internal const string Join = "join";
    internal const string Subquery = "subquery";
    internal const string Cte = "cte";
    internal const string Window = "window";
    internal const string Compound = "compound";
    internal const string View = "view";
    internal const string Series = "series";
    internal const string Pragma = "pragma";
    internal const string InSeek = "inseek";

    // What expressions use.
    internal const string Expr = "expr";
    internal const string Affinity = "affinity";
    internal const string Collation = "collation";
    internal const string Cast = "cast";
    internal const string Str = "string";
    internal const string Math = "math";
    internal const string DateTime = "datetime";
    internal const string Core = "function";
    internal const string Json = "json";
    internal const string Jsonb = "jsonb";
    internal const string Like = "like";

    // Setup statements.
    internal const string Ddl = "ddl";
    internal const string Dml = "dml";
    internal const string Upsert = "upsert";
    internal const string Returning = "returning";
    internal const string Trigger = "trigger";
    internal const string Generated = "generated";
    internal const string Alter = "alter";
    internal const string Temp = "temp";
    internal const string Vacuum = "vacuum";

    internal static readonly string[] Shapes = [Select, OrderLimit, Distinct, Aggregate, Join, Subquery, Cte, Window, Compound, View, Series, Pragma, InSeek];
    internal static readonly string[] ExprGroups = [Affinity, Collation, Cast, Str, Math, DateTime, Core, Json, Jsonb, Like];
    internal static readonly string[] SetupGroups = [Ddl, Dml, Upsert, Returning, Trigger, Generated, Alter, Temp, Vacuum];

    internal static readonly string[] All = [.. Shapes, Expr, .. ExprGroups, .. SetupGroups];

    /// <summary>The families a run may use. Empty means all.</summary>
    internal sealed class Filter
    {
        private readonly HashSet<string> _set;

        internal Filter(IEnumerable<string> names)
        {
            _set = [.. names];
            foreach (string n in _set)
            {
                if (!All.Contains(n))
                {
                    throw new ArgumentException($"unknown family '{n}'; known: {string.Join(",", All)}");
                }
            }
        }

        internal bool Any => _set.Count > 0;

        internal bool Allows(string family) => _set.Count == 0 || _set.Contains(family);

        /// <summary>True when the list names <paramref name="family"/> (false for an empty list).</summary>
        internal bool Names(string family) => _set.Contains(family);

        /// <summary>Expression groups are all on unless the list names at least one of them.</summary>
        internal bool AllowsExprGroup(string family) => !ExprGroups.Any(_set.Contains) || _set.Contains(family);

        /// <summary>Setup families follow the same rule: a list that names none keeps all of them.</summary>
        internal bool AllowsSetup(string family) => !SetupGroups.Any(_set.Contains) || _set.Contains(family);
    }
}

/// <summary>Type affinity of a declared type, by the five rules of datatype3 section 3.1.</summary>
internal enum Affin { Integer, Text, Blob, Real, Numeric }

internal sealed class ColDef
{
    internal string Name = "";
    internal string Decl = "";
    internal string? Collation;
    internal bool Generated;
    internal bool Unique;

    internal Affin Affinity => AffinityOf(Decl);

    internal static Affin AffinityOf(string decl)
    {
        string d = decl.ToUpperInvariant();
        if (d.Contains("INT", StringComparison.Ordinal))
        {
            return Affin.Integer;
        }

        if (d.Contains("CHAR", StringComparison.Ordinal) || d.Contains("CLOB", StringComparison.Ordinal) || d.Contains("TEXT", StringComparison.Ordinal))
        {
            return Affin.Text;
        }

        if (d.Length == 0 || d.Contains("BLOB", StringComparison.Ordinal))
        {
            return Affin.Blob;
        }

        if (d.Contains("REAL", StringComparison.Ordinal) || d.Contains("FLOA", StringComparison.Ordinal) || d.Contains("DOUB", StringComparison.Ordinal))
        {
            return Affin.Real;
        }

        return Affin.Numeric;
    }
}

internal sealed class TableDef
{
    internal string Name = "";
    internal List<ColDef> Cols = [];
    internal bool WithoutRowid;
    internal bool Strict;
    internal bool IsView;
    internal int Rows;

    internal ColDef? Find(string name) => Cols.Find(c => c.Name == name);
}

/// <summary>Everything a case is made of: the statements that build the database and what is known about its shape.</summary>
internal sealed class CaseSetup
{
    internal List<string> Statements = [];
    internal List<TableDef> Tables = [];
    internal HashSet<string> Families = [];
    internal bool Utf16;
}

/// <summary>Builds a random schema with data. Statements are tried on the pinned library as they are made; only the ones that work are kept.</summary>
internal static class SchemaGen
{
    private static readonly string[] Decls =
        ["INTEGER", "INT", "TEXT", "REAL", "NUMERIC", "BLOB", "", "VARCHAR(10)", "DECIMAL(5,2)", "BOOLEAN", "DATETIME", "FLOAT", "CHAR(3)", "UNSIGNED BIG INT", "DOUBLE"];

    private static readonly string[] StrictDecls = ["INT", "INTEGER", "REAL", "TEXT", "BLOB", "ANY"];

    private static readonly string[] Texts =
    [
        "", "a", "A", "abc", "ABC", "abd", " a", "a ", "10", "9", "1e1", "0x10", "-5", "3.0", "+7", " 12 ", "\u00e9", "\u00c9", "\u65e5\u672c", "a%b", "_x", "it's",
        "{\"a\":1,\"b\":[1,2,3],\"c\":{\"d\":null}}", "[1,2,3]", "\"s\"", "2024-02-29", "12:30:45", "2024-02-29 12:30:45", "2023-12-31T23:59:59", "1700000000", "2460000.5",
        "hello world", "Hello World", "xyz", "NULL", "true", "0", "00", "1.5e3", "ab\u0000cd", "tab\there",
    ];

    private static readonly string[] Ints = ["0", "1", "-1", "2", "7", "10", "100", "-100", "255", "65536", "2147483647", "-2147483648", "9007199254740993", "9223372036854775807", "-9223372036854775808"];

    private static readonly string[] Reals = ["0.0", "-0.0", "0.5", "-0.5", "1.5", "2.5", "3.14159", "1e300", "-1e300", "1e-300", "1e15", "123456789012345.6", "0.1", "9223372036854775808", "-9223372036854775809", "1e18", "4.35", "100.0"];

    private static readonly string[] Blobs = ["x''", "x'00'", "x'ff00ab'", "x'41'", "x'616263'", "x'00000000'", "x'e9'"];

    internal static string Quote(string s)
    {
        string t = s.Replace("'", "''", StringComparison.Ordinal);
        if (t.Contains('\0', StringComparison.Ordinal) || t.Contains('\t', StringComparison.Ordinal))
        {
            // A NUL or tab inside a literal would not survive a script file or the shell; build it with char() instead.
            return "(" + string.Join(" || ", t.Select(ch => ch == '\0' ? "char(0)" : ch == '\t' ? "char(9)" : "'" + ch + "'")) + ")";
        }

        return "'" + t + "'";
    }

    internal static string Literal(Rng r, Affin a)
    {
        // Mostly values that suit the column, sometimes anything (SQLite stores whatever it is given).
        int k = r.Next(100);
        if (k < 7)
        {
            return "NULL";
        }

        Affin want = k < 75 ? a : r.Pick(new[] { Affin.Integer, Affin.Text, Affin.Real, Affin.Blob, Affin.Numeric });
        switch (want)
        {
            case Affin.Integer:
                return r.Chance(80) ? r.Pick(Ints) : r.Range(-50, 50).ToString(CultureInfo.InvariantCulture);
            case Affin.Real:
                return r.Chance(70) ? r.Pick(Reals) : (r.Range(-1000, 1000) / 8.0).ToString("R", CultureInfo.InvariantCulture);
            case Affin.Text:
                return Quote(r.Pick(Texts));
            case Affin.Blob:
                return r.Chance(30) ? r.Pick(Blobs) : r.Chance(50) ? Quote(r.Pick(Texts)) : r.Pick(Ints);
            default:
                return r.Chance(40) ? r.Pick(Ints) : r.Chance(50) ? r.Pick(Reals) : Quote(r.Pick(Texts));
        }
    }

    internal static CaseSetup Build(Rng r, SqliteDb db, Fam.Filter filter)
    {
        var s = new CaseSetup();

        bool Run(string sql, params string[] fams)
        {
            try
            {
                db.Exec(sql);
            }
            catch (SqliteException)
            {
                return false;
            }

            s.Statements.Add(sql);
            foreach (string f in fams)
            {
                s.Families.Add(f);
            }

            return true;
        }

        if (r.Chance(5))
        {
            string enc = r.Pick(new[] { "UTF-16le", "UTF-16be" });
            if (Run($"PRAGMA encoding='{enc}'", Fam.Pragma))
            {
                s.Utf16 = true;
            }
        }

        int nTables = r.Range(1, 3);
        for (int ti = 0; ti < nTables; ti++)
        {
            TableDef t = MakeTable(r, ti, filter, s);
            string ddl = RenderCreate(t);
            if (Run(ddl, t.Cols.Any(c => c.Generated) ? [Fam.Ddl, Fam.Generated] : [Fam.Ddl]))
            {
                s.Tables.Add(t);
            }
        }

        if (s.Tables.Count == 0)
        {
            return s;
        }

        bool triggers = filter.Allows(Fam.Trigger) && filter.AllowsSetup(Fam.Trigger);
        if (triggers && r.Chance(25) && Run("CREATE TABLE lg(id INTEGER PRIMARY KEY, v)", Fam.Ddl))
        {
            TableDef t0 = s.Tables[0];
            string cols = string.Join(", ", t0.Cols.Where(c => !c.Generated).Take(1).Select(c => "new." + c.Name));
            if (Run($"CREATE TRIGGER trg0 AFTER INSERT ON {t0.Name} BEGIN INSERT INTO lg(v) VALUES({cols}); END", Fam.Trigger, Fam.Ddl))
            {
                s.Tables.Add(new TableDef { Name = "lg", Cols = [new ColDef { Name = "id", Decl = "INTEGER" }, new ColDef { Name = "v", Decl = "" }] });
            }
        }

        // Indexes, so that plans differ from full scans.
        int nIdx = r.Range(0, 3);
        for (int i = 0; i < nIdx; i++)
        {
            TableDef t = r.Pick(s.Tables);
            if (t.Name == "lg")
            {
                continue;
            }

            ColDef c = r.Pick(t.Cols.Where(x => x.Name != "id").ToList() is { Count: > 0 } l ? l : t.Cols);
            string uniq = r.Chance(20) ? "UNIQUE " : "";
            string term = c.Name + r.Weighted((6, ""), (2, " DESC"), (2, " COLLATE NOCASE"), (1, " COLLATE RTRIM"));
            string where = r.Chance(15) ? $" WHERE {c.Name} IS NOT NULL" : "";
            if (r.Chance(10))
            {
                term = c.Affinity == Affin.Text ? $"lower({c.Name})" : $"({c.Name} + 0)";
            }

            Run($"CREATE {uniq}INDEX i{i} ON {t.Name}({term}){where}", Fam.Ddl);
        }

        // Data.
        foreach (TableDef t in s.Tables.Where(x => x.Name != "lg"))
        {
            int n = r.Weighted((1, 0), (2, 1), (4, r.Range(2, 8)), (6, r.Range(8, 30)));
            var real = t.Cols.Where(c => !c.Generated).ToList();
            string names = string.Join(", ", real.Select(c => c.Name));
            int nextId = r.Range(1, 5);
            for (int i = 0; i < n; i++)
            {
                var vals = new List<string>();
                foreach (ColDef c in real)
                {
                    vals.Add(c.Name == "id" ? (nextId += r.Weighted((8, 1), (2, r.Range(2, 50)))).ToString(CultureInfo.InvariantCulture) : Literal(r, c.Affinity));
                }

                string verb = r.Weighted((16, "INSERT"), (1, "INSERT OR REPLACE"), (1, "INSERT OR IGNORE"));
                if (Run($"{verb} INTO {t.Name}({names}) VALUES({string.Join(", ", vals)})", Fam.Dml))
                {
                    t.Rows++;
                }
            }
        }

        // Views over the tables.
        if (filter.Allows(Fam.View) && r.Chance(35))
        {
            TableDef b = r.Pick(s.Tables.Where(x => x.Name != "lg").ToList() is { Count: > 0 } l ? l : s.Tables);
            ColDef[] cs = [.. b.Cols.Take(3)];
            string body = r.Weighted((3, $"SELECT {string.Join(", ", cs.Select(c => c.Name))} FROM {b.Name}"), (2, $"SELECT id, {cs.Last().Name} AS x FROM {b.Name} WHERE id % 2 = 0"));
            if (Run($"CREATE VIEW v0 AS {body}", Fam.View, Fam.Ddl))
            {
                var v = new TableDef { Name = "v0", IsView = true };
                v.Cols = body.Contains(" AS x", StringComparison.Ordinal)
                    ? [new ColDef { Name = "id", Decl = "INTEGER" }, new ColDef { Name = "x", Decl = cs.Last().Decl }]
                    : [.. cs.Select(c => new ColDef { Name = c.Name, Decl = c.Decl })];
                s.Tables.Add(v);
            }
        }

        if (filter.AllowsSetup(Fam.Dml))
        {
            Mutations(r, s, Run, filter);
        }

        if (filter.Allows(Fam.Ddl))
        {
            DdlExtras(r, s, Run, filter);
        }

        return s;
    }

    // Schema changes: triggers of every event and time, INSTEAD OF triggers, ALTER TABLE, temp objects, VACUUM (with an auto_vacuum change),
    // REINDEX and ANALYZE, and DROPs of views and triggers. Each is tried on the pinned library first; only what works is kept.
    private static void DdlExtras(Rng r, CaseSetup s, Func<string, string[], bool> run, Fam.Filter filter)
    {
        List<TableDef> real = [.. s.Tables.Where(x => !x.IsView && x.Name != "lg")];
        if (real.Count == 0)
        {
            return;
        }

        int trig = 0;
        int temps = 0;
        int n = r.Range(0, 4);
        for (int i = 0; i < n; i++)
        {
            TableDef t = r.Pick(real);
            List<ColDef> plain = [.. t.Cols.Where(c => !c.Generated && c.Name != "id")];
            if (plain.Count == 0)
            {
                continue;
            }

            ColDef c = r.Pick(plain);
            string kind = r.Weighted((5, "trigger"), (2, "instead"), (3, "add"), (2, "drop-col"), (2, "rename-table"), (2, "vacuum"), (1, "reindex"), (1, "analyze"), (3, "temp-table"), (2, "temp-view"), (2, "temp-trigger"), (1, "drop"), (1, "autovacuum"));
            switch (kind)
            {
                case "trigger" when filter.AllowsSetup(Fam.Trigger):
                case "temp-trigger" when filter.AllowsSetup(Fam.Temp) && filter.AllowsSetup(Fam.Trigger):
                {
                    if (s.Tables.All(x => x.Name != "lg") && run("CREATE TABLE lg(id INTEGER PRIMARY KEY, v)", [Fam.Ddl]))
                    {
                        s.Tables.Add(new TableDef { Name = "lg", Cols = [new ColDef { Name = "id", Decl = "INTEGER" }, new ColDef { Name = "v", Decl = "" }] });
                    }

                    string ev = r.Pick(new[] { "INSERT", "UPDATE", "DELETE", "UPDATE OF " + c.Name });
                    string time = ev.StartsWith("INSERT", StringComparison.Ordinal) ? r.Pick(new[] { "BEFORE", "AFTER" }) : r.Pick(new[] { "BEFORE", "AFTER" });
                    string rowref = ev.StartsWith("DELETE", StringComparison.Ordinal) ? "old." : ev.StartsWith("INSERT", StringComparison.Ordinal) ? "new." : r.Pick(new[] { "old.", "new." });
                    string when = r.Chance(35) ? $" WHEN {rowref}{c.Name} IS NOT NULL" : "";
                    string body = r.Weighted(
                        (5, $"INSERT INTO lg(v) VALUES({rowref}{c.Name});"),
                        (2, $"UPDATE lg SET v = ifnull(v, '') || 'x';"),
                        (2, $"INSERT INTO lg(v) SELECT count(*) FROM {t.Name};"),
                        (1, time == "BEFORE" ? $"SELECT RAISE(IGNORE);" : $"INSERT INTO lg(v) VALUES(typeof({rowref}{c.Name}));"));
                    string temp = kind == "temp-trigger" ? "TEMP " : "";
                    if (run($"CREATE {temp}TRIGGER trx{trig++} {time} {ev} ON {t.Name} FOR EACH ROW{when} BEGIN {body} END", [Fam.Trigger, Fam.Ddl, .. kind == "temp-trigger" ? new[] { Fam.Temp } : []]))
                    {
                        string dml = ev.StartsWith("INSERT", StringComparison.Ordinal)
                            ? $"INSERT INTO {t.Name}(id, {c.Name}) VALUES({r.Range(100, 140)}, {Literal(r, c.Affinity)})"
                            : ev.StartsWith("DELETE", StringComparison.Ordinal) ? $"DELETE FROM {t.Name} WHERE id % 3 = 0" : $"UPDATE {t.Name} SET {c.Name} = {Literal(r, c.Affinity)} WHERE id % 2 = 0";
                        run(dml, [Fam.Dml]);
                    }

                    break;
                }

                case "instead" when s.Tables.Any(x => x.Name == "v0"):
                {
                    TableDef v = s.Tables.First(x => x.Name == "v0");
                    if (v.Cols.Count > 0 && s.Tables.All(x => x.Name != "lg") && run("CREATE TABLE lg(id INTEGER PRIMARY KEY, v)", [Fam.Ddl]))
                    {
                        s.Tables.Add(new TableDef { Name = "lg", Cols = [new ColDef { Name = "id", Decl = "INTEGER" }, new ColDef { Name = "v", Decl = "" }] });
                    }

                    string ev = r.Pick(new[] { "INSERT", "UPDATE", "DELETE" });
                    string vc = v.Cols[^1].Name;
                    string rowref = ev == "INSERT" ? "new." : "old.";
                    if (run($"CREATE TRIGGER vi{trig++} INSTEAD OF {ev} ON v0 BEGIN INSERT INTO lg(v) VALUES({rowref}{vc}); END", [Fam.Trigger, Fam.View, Fam.Ddl]))
                    {
                        run(ev == "INSERT" ? $"INSERT INTO v0({vc}) VALUES(1)" : ev == "UPDATE" ? $"UPDATE v0 SET {vc} = 1" : "DELETE FROM v0", [Fam.Dml]);
                    }

                    break;
                }

                case "add" when filter.AllowsSetup(Fam.Alter):
                {
                    string name = "y" + t.Cols.Count;
                    string decl = r.Pick(Decls);
                    string def = r.Chance(70) ? " DEFAULT " + Literal(r, ColDef.AffinityOf(decl)) : "";
                    if (run($"ALTER TABLE {t.Name} ADD COLUMN {name} {decl}{def}", [Fam.Ddl, Fam.Alter]))
                    {
                        t.Cols.Add(new ColDef { Name = name, Decl = decl });
                    }

                    break;
                }

                case "drop-col" when filter.AllowsSetup(Fam.Alter):
                    if (run($"ALTER TABLE {t.Name} DROP COLUMN {c.Name}", [Fam.Ddl, Fam.Alter]))
                    {
                        t.Cols.Remove(c);
                    }

                    break;
                case "rename-table" when filter.AllowsSetup(Fam.Alter):
                {
                    string nn = t.Name + "n";
                    if (run($"ALTER TABLE {t.Name} RENAME TO {nn}", [Fam.Ddl, Fam.Alter]))
                    {
                        t.Name = nn;
                    }

                    break;
                }

                case "vacuum" when filter.AllowsSetup(Fam.Vacuum):
                    run("VACUUM", [Fam.Ddl, Fam.Vacuum]);
                    break;
                case "autovacuum" when filter.AllowsSetup(Fam.Vacuum):
                    if (run($"PRAGMA auto_vacuum = {r.Range(0, 2)}", [Fam.Pragma, Fam.Vacuum]))
                    {
                        run("VACUUM", [Fam.Ddl, Fam.Vacuum]);
                    }

                    break;
                case "reindex":
                    run("REINDEX", [Fam.Ddl]);
                    break;
                case "analyze":
                    run("ANALYZE", [Fam.Ddl]);
                    break;
                case "temp-table" when filter.AllowsSetup(Fam.Temp):
                {
                    string name = "tt" + temps++;
                    if (run($"CREATE TEMP TABLE {name}(id INTEGER PRIMARY KEY, a, b TEXT)", [Fam.Ddl, Fam.Temp]))
                    {
                        var tt = new TableDef { Name = name, Cols = [new ColDef { Name = "id", Decl = "INTEGER" }, new ColDef { Name = "a", Decl = "" }, new ColDef { Name = "b", Decl = "TEXT" }] };
                        for (int k = 0; k < r.Range(0, 8); k++)
                        {
                            if (run($"INSERT INTO {name}(a, b) VALUES({Literal(r, Affin.Blob)}, {Literal(r, Affin.Text)})", [Fam.Dml, Fam.Temp]))
                            {
                                tt.Rows++;
                            }
                        }

                        if (r.Chance(40))
                        {
                            run($"CREATE INDEX {name}_a ON {name}(a)", [Fam.Ddl, Fam.Temp]);
                        }

                        s.Tables.Add(tt);
                    }

                    break;
                }

                case "temp-view" when filter.AllowsSetup(Fam.Temp):
                {
                    string name = "tv" + temps++;
                    if (run($"CREATE TEMP VIEW {name} AS SELECT id, {c.Name} AS x FROM {t.Name}", [Fam.Ddl, Fam.Temp, Fam.View]))
                    {
                        s.Tables.Add(new TableDef { Name = name, IsView = true, Cols = [new ColDef { Name = "id", Decl = "INTEGER" }, new ColDef { Name = "x", Decl = c.Decl }] });
                    }

                    break;
                }

                case "drop":
                    run(r.Chance(50) ? "DROP VIEW IF EXISTS v0" : $"DROP TRIGGER IF EXISTS trx{r.Range(0, 2)}", [Fam.Ddl]);
                    s.Tables.RemoveAll(x => x.Name == "v0" && x.IsView && s.Statements.Contains("DROP VIEW IF EXISTS v0"));
                    break;
                default:
                    break;
            }
        }
    }

    private static void Mutations(Rng r, CaseSetup s, Func<string, string[], bool> run, Fam.Filter filter)
    {
        List<TableDef> real = [.. s.Tables.Where(x => !x.IsView && x.Name != "lg")];
        if (real.Count == 0)
        {
            return;
        }

        int n = r.Range(0, 5);
        for (int i = 0; i < n; i++)
        {
            TableDef t = r.Pick(real);
            List<ColDef> cols = [.. t.Cols.Where(c => !c.Generated && c.Name != "id")];
            if (cols.Count == 0)
            {
                continue;
            }

            ColDef c = r.Pick(cols);
            string kind = r.Weighted((5, "update"), (3, "delete"), (3, "upsert"), (2, "returning"), (2, "replace"), (2, "alter"), (1, "drop-index"));
            var g = new ExprGen(r, new Fam.Filter([]), [new ScopeCol(c.Name, c, "")]) { MaxDepth = 2 };
            string pred = g.Predicate().ToString();
            switch (kind)
            {
                case "update":
                    run($"UPDATE {t.Name} SET {c.Name} = {g.Value(c).ToString()} WHERE {pred}", [Fam.Dml]);
                    break;
                case "delete":
                    run($"DELETE FROM {t.Name} WHERE {pred}", [Fam.Dml]);
                    break;
                case "upsert" when filter.AllowsSetup(Fam.Upsert):
                    run($"INSERT INTO {t.Name}(id, {c.Name}) VALUES({r.Range(1, 40)}, {Literal(r, c.Affinity)}) ON CONFLICT(id) DO UPDATE SET {c.Name} = excluded.{c.Name}", [Fam.Dml, Fam.Upsert]);
                    break;
                case "returning" when filter.AllowsSetup(Fam.Returning):
                    run($"UPDATE {t.Name} SET {c.Name} = {Literal(r, c.Affinity)} WHERE id % 3 = {r.Range(0, 2)} RETURNING id, {c.Name}", [Fam.Dml, Fam.Returning]);
                    break;
                case "replace":
                    run($"REPLACE INTO {t.Name}(id, {c.Name}) VALUES({r.Range(1, 40)}, {Literal(r, c.Affinity)})", [Fam.Dml]);
                    break;
                case "alter" when filter.AllowsSetup(Fam.Ddl):
                    if (r.Chance(60))
                    {
                        string name = "x" + t.Cols.Count;
                        string decl = r.Pick(Decls);
                        string def = r.Chance(60) ? " DEFAULT " + Literal(r, ColDef.AffinityOf(decl)) : "";
                        if (run($"ALTER TABLE {t.Name} ADD COLUMN {name} {decl}{def}", [Fam.Ddl]))
                        {
                            t.Cols.Add(new ColDef { Name = name, Decl = decl });
                        }
                    }
                    else
                    {
                        string nn = c.Name + "r";
                        if (run($"ALTER TABLE {t.Name} RENAME COLUMN {c.Name} TO {nn}", [Fam.Ddl]))
                        {
                            c.Name = nn;
                        }
                    }

                    break;
                case "drop-index":
                    run("DROP INDEX i0", [Fam.Ddl]);
                    break;
                default:
                    break;
            }
        }
    }

    private static TableDef MakeTable(Rng r, int index, Fam.Filter filter, CaseSetup s)
    {
        var t = new TableDef { Name = "t" + index };
        t.WithoutRowid = r.Chance(12);
        t.Strict = !t.WithoutRowid && r.Chance(10);
        t.Cols.Add(new ColDef { Name = "id", Decl = "INTEGER" });
        int n = r.Range(2, 5);
        for (int i = 0; i < n; i++)
        {
            string decl = t.Strict ? r.Pick(StrictDecls) : r.Pick(Decls);
            string? coll = ColDef.AffinityOf(decl) == Affin.Text && filter.AllowsExprGroup(Fam.Collation) && r.Chance(35) ? r.Pick(new[] { "NOCASE", "RTRIM", "BINARY" }) : null;
            t.Cols.Add(new ColDef { Name = "c" + i, Decl = decl, Collation = coll, Unique = r.Chance(8) });
        }

        if (!t.Strict && filter.AllowsSetup(Fam.Generated) && r.Chance(12))
        {
            ColDef? basis = t.Cols.Find(c => c.Affinity is Affin.Integer or Affin.Real or Affin.Numeric && c.Name != "id");
            if (basis is not null)
            {
                t.Cols.Add(new ColDef { Name = "g0", Decl = "", Generated = true });
            }
        }

        return t;
    }

    private static string RenderCreate(TableDef t)
    {
        var sb = new StringBuilder();
        sb.Append("CREATE TABLE ").Append(t.Name).Append('(');
        for (int i = 0; i < t.Cols.Count; i++)
        {
            ColDef c = t.Cols[i];
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(c.Name);
            if (c.Generated)
            {
                ColDef basis = t.Cols.First(x => x.Affinity is Affin.Integer or Affin.Real or Affin.Numeric && x.Name != "id" && !x.Generated);
                sb.Append(" GENERATED ALWAYS AS (").Append(basis.Name).Append(" * 2) ").Append(i % 2 == 0 ? "VIRTUAL" : "STORED");
                continue;
            }

            if (c.Decl.Length > 0)
            {
                sb.Append(' ').Append(c.Decl);
            }

            if (c.Name == "id")
            {
                sb.Append(" PRIMARY KEY");
            }

            if (c.Collation is not null)
            {
                sb.Append(" COLLATE ").Append(c.Collation);
            }

            if (c.Unique)
            {
                sb.Append(" UNIQUE");
            }
        }

        sb.Append(')');
        if (t.WithoutRowid && t.Strict)
        {
            sb.Append(" WITHOUT ROWID, STRICT");
        }
        else if (t.WithoutRowid)
        {
            sb.Append(" WITHOUT ROWID");
        }
        else if (t.Strict)
        {
            sb.Append(" STRICT");
        }

        return sb.ToString();
    }
}
