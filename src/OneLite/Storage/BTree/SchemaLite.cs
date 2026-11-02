using System.Globalization;
using System.Text;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Storage.BTree;

/// <summary>One row of <c>sqlite_schema</c>.</summary>
public sealed class SchemaEntry
{
    /// <summary>"table", "index", "view" or "trigger".</summary>
    public string Type { get; init; } = "";

    /// <summary>Object name.</summary>
    public string Name { get; init; } = "";

    /// <summary>The table the object belongs to.</summary>
    public string TableName { get; init; } = "";

    /// <summary>Root page of the object's B-tree (0 for views and triggers).</summary>
    public uint RootPage { get; init; }

    /// <summary>The stored CREATE statement, or null.</summary>
    public string? Sql { get; init; }
}

/// <summary>A column of a table as far as the stored CREATE text says.</summary>
public sealed class ColumnDef
{
    /// <summary>Name as declared, unquoted.</summary>
    public string Name { get; init; } = "";

    /// <summary>Declared type text (may be empty).</summary>
    public string DeclaredType { get; init; } = "";

    /// <summary>Affinity letter: 'B' blob/none, 'T' text, 'N' numeric, 'I' integer, 'R' real (datatype3 §3.1).</summary>
    public char Affinity { get; init; } = 'B';

    /// <summary>0 an ordinary column, 1 generated VIRTUAL (not stored), 2 generated STORED.</summary>
    public int Generated { get; init; }

    /// <summary>The generated column's expression text.</summary>
    public string? GeneratedExpr { get; init; }

    /// <summary>True if the column carries a column-level PRIMARY KEY.</summary>
    public bool PrimaryKey { get; init; }
}

/// <summary>The parts of a CREATE TABLE statement needed to read rows out of the B-tree.</summary>
public sealed class TableDef
{
    /// <summary>Columns in declared order.</summary>
    public IReadOnlyList<ColumnDef> Columns { get; init; } = [];

    /// <summary>Indexes (into <see cref="Columns"/>) of the PRIMARY KEY columns in key order.</summary>
    public IReadOnlyList<int> PrimaryKey { get; init; } = [];

    /// <summary>True for WITHOUT ROWID.</summary>
    public bool WithoutRowid { get; init; }

    /// <summary>True for STRICT.</summary>
    public bool Strict { get; init; }

    /// <summary>For a rowid table, the column that is an alias of the rowid (INTEGER PRIMARY KEY), or -1.</summary>
    public int RowidAlias { get; init; } = -1;

    /// <summary>For each column, its position in the stored record, or -1 for a VIRTUAL generated column.</summary>
    public IReadOnlyList<int> RecordIndex { get; init; } = [];
}

/// <summary>
/// An interim reader for <c>sqlite_schema</c>: it finds each object's root page and, from the CREATE TABLE text, the column list and
/// whether a table is WITHOUT ROWID or STRICT. It is not a SQL parser; the real parser replaces it.
/// </summary>
public static class SchemaLite
{
    /// <summary>Reads every <c>sqlite_schema</c> row (a rowid table rooted at page 1). Page reads that return Pending are completed by <paramref name="pump"/>.</summary>
    public static List<SchemaEntry> Read(BtContext ctx, Action pump)
    {
        var list = new List<SchemaEntry>();
        using BtCursor c = BTree.Table(ctx, 1).OpenCursor();
        var rv = new RecordView();
        Run(c, pump, c.First);
        while (c.State == CursorState.Valid)
        {
            Run(c, pump, c.LoadPayload);
            if (!c.ParseRecord(rv) || rv.ColumnCount < 5)
            {
                throw OneLiteException.Corrupt(1, "a sqlite_schema row is not a five-column record");
            }

            long root = rv.GetInt64(3);
            if (root < 0 || root > uint.MaxValue)
            {
                throw OneLiteException.Corrupt(1, "root page out of range");
            }

            list.Add(new SchemaEntry
            {
                Type = Text(rv, 0, ctx.Encoding),
                Name = Text(rv, 1, ctx.Encoding),
                TableName = Text(rv, 2, ctx.Encoding),
                RootPage = (uint)root,
                Sql = rv.ClassOf(4) == StorageClass.Null ? null : Text(rv, 4, ctx.Encoding),
            });
            Run(c, pump, c.Next);
        }

        return list;
    }

    private static string Text(RecordView rv, int i, TextEncoding enc) => TextCodec.Decode(rv.GetBytes(i), enc);

    private static void Run(BtCursor c, Action pump, Func<BtStatus> op)
    {
        for (; ; )
        {
            BtStatus s = op();
            if (s == BtStatus.Ok)
            {
                return;
            }

            if (s == BtStatus.Error)
            {
                throw c.Error ?? new InvalidOperationException("cursor failed");
            }

            pump();
        }
    }

    private static List<string> Tokens(string sql)
    {
        var t = new List<string>();
        int i = 0;
        while (i < sql.Length)
        {
            char ch = sql[i];
            if (char.IsWhiteSpace(ch))
            {
                i++;
            }
            else if (ch == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++;
                }
            }
            else if (ch == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int e = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = e < 0 ? sql.Length : e + 2;
            }
            else if (ch is '"' or '`' or '[' or '\'')
            {
                char close = ch == '[' ? ']' : ch;
                var sb = new StringBuilder();
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == close)
                    {
                        if (close != ']' && i + 1 < sql.Length && sql[i + 1] == close)
                        {
                            sb.Append(close);
                            i += 2;
                            continue;
                        }

                        break;
                    }

                    sb.Append(sql[i++]);
                }

                i++;
                // Quoted tokens keep a marker so keywords inside quotes are not mistaken for keywords.
                t.Add((ch == '\'' ? "'" : "\"") + sb);
            }
            else if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '$')
            {
                int s = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '$' || sql[i] == '.'))
                {
                    i++;
                }

                t.Add(sql[s..i]);
            }
            else
            {
                t.Add(ch.ToString());
                i++;
            }
        }

        return t;
    }

    private static bool Kw(string tok, string kw) => string.Equals(tok, kw, StringComparison.OrdinalIgnoreCase);

    private static string Unq(string tok) => tok.Length > 0 && (tok[0] == '"' || tok[0] == '\'') ? tok[1..] : tok;

    /// <summary>Parses a CREATE TABLE statement. Returns null for virtual tables and anything that is not a plain CREATE TABLE.</summary>
    public static TableDef? ParseTable(string sql)
    {
        List<string> t = Tokens(sql);
        int p = 0;
        if (t.Count < 4 || !Kw(t[p++], "CREATE"))
        {
            return null;
        }

        if (Kw(t[p], "TEMP") || Kw(t[p], "TEMPORARY"))
        {
            p++;
        }

        if (!Kw(t[p++], "TABLE"))
        {
            return null;
        }

        if (Kw(t[p], "IF"))
        {
            p += 3;
        }

        p++; // name
        if (p < t.Count && t[p] == ".")
        {
            p += 2;
        }

        if (p >= t.Count || t[p] != "(")
        {
            return null;
        }

        p++;
        var items = new List<List<string>>();
        var cur = new List<string>();
        int depth = 0;
        for (; p < t.Count; p++)
        {
            string tok = t[p];
            if (tok == "(")
            {
                depth++;
            }
            else if (tok == ")")
            {
                if (depth == 0)
                {
                    break;
                }

                depth--;
            }

            if (tok == "," && depth == 0)
            {
                items.Add(cur);
                cur = [];
            }
            else
            {
                cur.Add(tok);
            }
        }

        items.Add(cur);
        p++;
        bool wor = false, strict = false;
        for (; p < t.Count; p++)
        {
            if (Kw(t[p], "WITHOUT") && p + 1 < t.Count && Kw(t[p + 1], "ROWID"))
            {
                wor = true;
            }
            else if (Kw(t[p], "STRICT"))
            {
                strict = true;
            }
        }

        var cols = new List<ColumnDef>();
        var pkNames = new List<string>();
        foreach (List<string> it in items)
        {
            if (it.Count == 0)
            {
                continue;
            }

            string first = it[0];
            if (Kw(first, "PRIMARY") || Kw(first, "UNIQUE") || Kw(first, "CHECK") || Kw(first, "FOREIGN") || Kw(first, "CONSTRAINT"))
            {
                int k = 0;
                if (Kw(first, "CONSTRAINT"))
                {
                    k = 2;
                }

                if (k < it.Count && Kw(it[k], "PRIMARY"))
                {
                    int q = k;
                    while (q < it.Count && it[q] != "(")
                    {
                        q++;
                    }

                    q++;
                    int d = 0;
                    string? name = null;
                    for (; q < it.Count; q++)
                    {
                        if (it[q] == "(")
                        {
                            d++;
                        }
                        else if (it[q] == ")")
                        {
                            if (d == 0)
                            {
                                break;
                            }

                            d--;
                        }
                        else if (d == 0)
                        {
                            if (it[q] == ",")
                            {
                                continue;
                            }

                            if (name == null || (it[q - 1] == ","))
                            {
                                name = Unq(it[q]);
                                pkNames.Add(name);
                            }
                        }
                    }
                }

                continue;
            }

            string cname = Unq(first);
            int i = 1;
            var type = new StringBuilder();
            while (i < it.Count && !IsConstraintStart(it[i]))
            {
                if (it[i] == "(")
                {
                    int d = 0;
                    do
                    {
                        if (it[i] == "(")
                        {
                            d++;
                        }
                        else if (it[i] == ")")
                        {
                            d--;
                        }

                        type.Append(it[i]);
                        i++;
                    }
                    while (i < it.Count && d > 0);
                    continue;
                }

                if (type.Length > 0)
                {
                    type.Append(' ');
                }

                type.Append(Unq(it[i]));
                i++;
            }

            bool pk = false;
            int gen = 0;
            string? expr = null;
            for (int j = i; j < it.Count; j++)
            {
                if (Kw(it[j], "PRIMARY") && j + 1 < it.Count && Kw(it[j + 1], "KEY"))
                {
                    pk = true;
                }
                else if ((Kw(it[j], "AS") || Kw(it[j], "GENERATED")) && gen == 0)
                {
                    int s = j;
                    while (s < it.Count && it[s] != "(")
                    {
                        s++;
                    }

                    if (s < it.Count)
                    {
                        int d = 0;
                        int e = s;
                        for (; e < it.Count; e++)
                        {
                            if (it[e] == "(")
                            {
                                d++;
                            }
                            else if (it[e] == ")" && --d == 0)
                            {
                                break;
                            }
                        }

                        expr = string.Join(' ', it.GetRange(s + 1, e - s - 1));
                        gen = e + 1 < it.Count && Kw(it[e + 1], "STORED") ? 2 : 1;
                        j = e;
                    }
                }
            }

            string decl = type.ToString();
            cols.Add(new ColumnDef { Name = cname, DeclaredType = decl, Affinity = AffinityOf(decl, strict), Generated = gen, GeneratedExpr = expr, PrimaryKey = pk });
        }

        var pkIdx = new List<int>();
        foreach (string n in pkNames)
        {
            int ix = cols.FindIndex(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase));
            if (ix >= 0)
            {
                pkIdx.Add(ix);
            }
        }

        if (pkIdx.Count == 0)
        {
            for (int i = 0; i < cols.Count; i++)
            {
                if (cols[i].PrimaryKey)
                {
                    pkIdx.Add(i);
                }
            }
        }

        int alias = -1;
        if (!wor && pkIdx.Count == 1 && string.Equals(cols[pkIdx[0]].DeclaredType, "INTEGER", StringComparison.OrdinalIgnoreCase))
        {
            alias = pkIdx[0];
        }

        var recIdx = new int[cols.Count];
        Array.Fill(recIdx, -1);
        int r = 0;
        if (wor)
        {
            foreach (int ix in pkIdx)
            {
                recIdx[ix] = r++;
            }

            for (int i = 0; i < cols.Count; i++)
            {
                if (recIdx[i] < 0 && cols[i].Generated != 1)
                {
                    recIdx[i] = r++;
                }
            }
        }
        else
        {
            for (int i = 0; i < cols.Count; i++)
            {
                if (cols[i].Generated != 1)
                {
                    recIdx[i] = r++;
                }
            }
        }

        return new TableDef { Columns = cols, PrimaryKey = pkIdx, WithoutRowid = wor, Strict = strict, RowidAlias = alias, RecordIndex = recIdx };
    }

    private static bool IsConstraintStart(string tok) =>
        Kw(tok, "CONSTRAINT") || Kw(tok, "PRIMARY") || Kw(tok, "NOT") || Kw(tok, "NULL") || Kw(tok, "UNIQUE") || Kw(tok, "CHECK")
        || Kw(tok, "DEFAULT") || Kw(tok, "COLLATE") || Kw(tok, "REFERENCES") || Kw(tok, "GENERATED") || Kw(tok, "AS");

    /// <summary>The column affinity for a declared type (datatype3 §3.1; STRICT tables use their fixed type names).</summary>
    public static char AffinityOf(string declared, bool strict)
    {
        string u = declared.ToUpperInvariant();
        if (strict)
        {
            return u switch { "INT" or "INTEGER" => 'I', "REAL" => 'R', "TEXT" => 'T', _ => 'B' };
        }

        if (u.Contains("INT", StringComparison.Ordinal))
        {
            return 'I';
        }

        if (u.Contains("CHAR", StringComparison.Ordinal) || u.Contains("CLOB", StringComparison.Ordinal) || u.Contains("TEXT", StringComparison.Ordinal))
        {
            return 'T';
        }

        if (u.Length == 0 || u.Contains("BLOB", StringComparison.Ordinal))
        {
            return 'B';
        }

        if (u.Contains("REAL", StringComparison.Ordinal) || u.Contains("FLOA", StringComparison.Ordinal) || u.Contains("DOUB", StringComparison.Ordinal))
        {
            return 'R';
        }

        return 'N';
    }

    /// <summary>
    /// Evaluates a generated-column expression made of column names, numeric literals, parentheses and + - * / %. Returns false for anything else.
    /// </summary>
    public static bool TryEvalSimple(string expr, Func<string, Mem?> column, out Mem result)
    {
        result = Mem.Null;
        List<string> t = Tokens(expr);
        int p = 0;
        try
        {
            Mem v = Sum(t, ref p, column);
            if (p != t.Count)
            {
                return false;
            }

            result = v;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static Mem Sum(List<string> t, ref int p, Func<string, Mem?> col)
    {
        Mem a = Prod(t, ref p, col);
        while (p < t.Count && (t[p] == "+" || t[p] == "-"))
        {
            string op = t[p++];
            Mem b = Prod(t, ref p, col);
            a = Arith(op[0], a, b);
        }

        return a;
    }

    private static Mem Prod(List<string> t, ref int p, Func<string, Mem?> col)
    {
        Mem a = Unary(t, ref p, col);
        while (p < t.Count && (t[p] == "*" || t[p] == "/" || t[p] == "%"))
        {
            string op = t[p++];
            Mem b = Unary(t, ref p, col);
            a = Arith(op[0], a, b);
        }

        return a;
    }

    private static Mem Unary(List<string> t, ref int p, Func<string, Mem?> col)
    {
        if (p >= t.Count)
        {
            throw new FormatException();
        }

        string tok = t[p];
        if (tok == "-")
        {
            p++;
            return Arith('-', Mem.FromInt(0), Unary(t, ref p, col));
        }

        if (tok == "(")
        {
            p++;
            Mem v = Sum(t, ref p, col);
            if (p >= t.Count || t[p] != ")")
            {
                throw new FormatException();
            }

            p++;
            return v;
        }

        p++;
        if (long.TryParse(tok, NumberStyles.None, CultureInfo.InvariantCulture, out long l))
        {
            return Mem.FromInt(l);
        }

        if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv) && char.IsDigit(tok[0]))
        {
            return Mem.FromReal(dv);
        }

        return col(Unq(tok)) ?? throw new FormatException();
    }

    private static Mem Arith(char op, Mem a, Mem b)
    {
        if (a.Kind == StorageClass.Null || b.Kind == StorageClass.Null)
        {
            return Mem.Null;
        }

        if (a.Kind == StorageClass.Integral && b.Kind == StorageClass.Integral)
        {
            long x = a.IntValue, y = b.IntValue;
            try
            {
                checked
                {
                    switch (op)
                    {
                        case '+': return Mem.FromInt(x + y);
                        case '-': return Mem.FromInt(x - y);
                        case '*': return Mem.FromInt(x * y);
                        case '/': return y == 0 ? Mem.Null : Mem.FromInt(x / y);
                        default: return y == 0 ? Mem.Null : Mem.FromInt(x % y);
                    }
                }
            }
            catch (OverflowException)
            {
                // integer overflow continues in floating point, as in SQLite
            }
        }

        if (a.Kind is not (StorageClass.Integral or StorageClass.Real) || b.Kind is not (StorageClass.Integral or StorageClass.Real))
        {
            throw new FormatException();
        }

        double dx = a.Kind == StorageClass.Integral ? a.IntValue : a.RealValue;
        double dy = b.Kind == StorageClass.Integral ? b.IntValue : b.RealValue;
        return op switch
        {
            '+' => Mem.FromReal(dx + dy),
            '-' => Mem.FromReal(dx - dy),
            '*' => Mem.FromReal(dx * dy),
            '/' => dy == 0 ? Mem.Null : Mem.FromReal(dx / dy),
            _ => dy == 0 ? Mem.Null : Mem.FromReal(Math.IEEERemainder(dx, dy)),
        };
    }
}
