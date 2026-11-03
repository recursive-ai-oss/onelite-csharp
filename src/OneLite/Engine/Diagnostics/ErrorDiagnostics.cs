using System.Linq;
using System.Globalization;
using System.Text;
using RecursiveAI.OneLite.Planning;
using RecursiveAI.OneLite.Sql.Parsing;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite.Diagnostics;

/// <summary>
/// Adds the OneLite-only members to an error (position, caret, identifier, suggestions, constraint details). It reads the error's reference-library
/// message and the SQL text, so the message, the codes and the code paths that raise errors are untouched, and nothing runs unless an error happens.
/// </summary>
internal static class ErrorDiagnostics
{
    private static readonly string[] s_keywords =
    [
        "SELECT", "FROM", "WHERE", "GROUP", "ORDER", "BY", "HAVING", "LIMIT", "OFFSET", "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE", "CREATE",
        "TABLE", "INDEX", "VIEW", "TRIGGER", "DROP", "ALTER", "JOIN", "LEFT", "INNER", "CROSS", "OUTER", "ON", "USING", "UNION", "EXCEPT", "INTERSECT",
        "DISTINCT", "BETWEEN", "LIKE", "GLOB", "AND", "OR", "NOT", "NULL", "CASE", "WHEN", "THEN", "ELSE", "END", "BEGIN", "COMMIT", "ROLLBACK", "PRAGMA",
        "WITH", "RECURSIVE", "RETURNING", "CONFLICT", "REPLACE", "DEFAULT", "PRIMARY", "UNIQUE", "CHECK", "REFERENCES", "FOREIGN", "CONSTRAINT",
    ];

    private static readonly string[] s_aggregates = ["count", "sum", "avg", "min", "max", "total", "group_concat", "string_agg", "row_number", "rank", "dense_rank",
        "percent_rank", "cume_dist", "ntile", "lag", "lead", "first_value", "last_value", "nth_value"];

    private static readonly string[] s_collations = ["BINARY", "NOCASE", "RTRIM"];

    /// <summary>Fills in the diagnostics of <paramref name="e"/> for the statement <paramref name="sql"/>. Returns <paramref name="e"/>.</summary>
    internal static OneLiteException Enrich(OneLiteException e, string sql, Catalog? cat)
    {
        if (e.Sql is not null)
        {
            return e;
        }

        e.Sql = sql;
        try
        {
            if (e.Code == ResultCode.Constraint)
            {
                Constraint(e, sql);
            }
            else
            {
                Names(e, sql, cat);
                if (e.NodeStart >= 0 && e.NodeStart <= sql.Length && e.ErrorOffset < 0)
                {
                    // The compiler named the token the reference library points at (sqlite3_error_offset).
                    e.ErrorOffset = Encoding.UTF8.GetByteCount(sql.AsSpan(0, e.NodeStart));
                    Token t = Lexer.Next(sql, e.NodeStart);
                    SetPosition(e, sql, e.NodeStart, Math.Max(1, Math.Min(t.End, sql.Length) - e.NodeStart));
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Diagnostics are best effort and must never replace the real error.
        }

        return e;
    }

    // ------------------------------------------------------------------ compile errors

    private static void Names(OneLiteException e, string sql, Catalog? cat)
    {
        string m = e.Message;
        if (e.ErrorOffset >= 0)
        {
            int at = CharIndexOfByte(sql, e.ErrorOffset);
            Token t = Lexer.Next(sql, at);
            int len = Math.Max(1, Math.Min(t.End, sql.Length) - Math.Max(t.Start, 0));
            if (m.StartsWith("near \"", StringComparison.Ordinal) && m.EndsWith("\": syntax error", StringComparison.Ordinal))
            {
                string word = m[6..^15];
                e.Identifier = word;
                e.Suggestions = Closest(word, s_keywords);
            }
            else if (m.StartsWith("unrecognized token: \"", StringComparison.Ordinal))
            {
                e.Identifier = m[21..^1];
            }

            SetPosition(e, sql, t.Start >= at ? t.Start : at, len);
            return;
        }

        if (Take(m, "no such table: ", out string? name) || Take(m, "no such view: ", out name))
        {
            string bare = LastPart(name);
            e.Identifier = bare;
            Locate(e, sql, bare, TokenWanted.Plain);
            e.Suggestions = cat is null ? [] : Closest(bare, TableNames(cat).Concat(CteNames(sql)));
        }
        else if (Take(m, "no such index: ", out name) || Take(m, "no such trigger: ", out name))
        {
            string bare = LastPart(name);
            e.Identifier = bare;
            Locate(e, sql, bare, TokenWanted.Plain);
            if (cat is not null)
            {
                bool trig = m.StartsWith("no such trigger", StringComparison.Ordinal);
                e.Suggestions = Closest(bare, trig ? cat.Triggers.Keys : cat.Tables.Values.SelectMany(t => t.Indexes.Select(i => i.Name)));
            }
        }
        else if (Take(m, "no such column: ", out name) || m.StartsWith("no such column: \"", StringComparison.Ordinal))
        {
            ColumnError(e, sql, cat, m, "no such column: ");
        }
        else if (Take(m, "ambiguous column name: ", out name))
        {
            ColumnError(e, sql, cat, m, "ambiguous column name: ");
        }
        else if (Take(m, "no such function: ", out name))
        {
            e.Identifier = name;
            Locate(e, sql, name, TokenWanted.Call);
            e.Suggestions = Closest(name, FunctionNames());
        }
        else if (m.StartsWith("wrong number of arguments to function ", StringComparison.Ordinal) && m.EndsWith("()", StringComparison.Ordinal))
        {
            string fn = m[38..^2];
            e.Identifier = fn;
            Locate(e, sql, fn, TokenWanted.Call);
            FuncDef? def = FunctionTable.Find(fn);
            if (def is not null)
            {
                e.Suggestions = [Arity(def)];
            }
        }
        else if (Take(m, "no such collation sequence: ", out name))
        {
            e.Identifier = name;
            Locate(e, sql, name, TokenWanted.Plain);
            e.Suggestions = Closest(name, s_collations);
        }
        else if ((m.StartsWith("misuse of aggregate function ", StringComparison.Ordinal) || m.StartsWith("misuse of window function ", StringComparison.Ordinal)
            || m.StartsWith("misuse of aggregate: ", StringComparison.Ordinal)) && m.EndsWith("()", StringComparison.Ordinal))
        {
            string fn = m[(m.LastIndexOf(' ') + 1)..^2];
            e.Identifier = fn;
            Locate(e, sql, fn, TokenWanted.Call);
        }
        else if (m.EndsWith("() may not be used as a window function", StringComparison.Ordinal))
        {
            e.Identifier = m[..^39];
            Locate(e, sql, e.Identifier, TokenWanted.Call);
        }
        else if (m.StartsWith("cannot modify ", StringComparison.Ordinal) && m.EndsWith(" because it is a view", StringComparison.Ordinal))
        {
            e.Identifier = m[14..^21];
            Locate(e, sql, e.Identifier, TokenWanted.Plain);
        }
        else if (m.StartsWith("there is already another table or index with this name: ", StringComparison.Ordinal))
        {
            e.Identifier = m["there is already another table or index with this name: ".Length..];
            Locate(e, sql, e.Identifier, TokenWanted.Plain, last: true);
        }
        else if ((m.StartsWith("index ", StringComparison.Ordinal) || m.StartsWith("view ", StringComparison.Ordinal) || m.StartsWith("trigger ", StringComparison.Ordinal))
            && m.EndsWith(" already exists", StringComparison.Ordinal))
        {
            e.Identifier = LastPart(m[(m.IndexOf(' ') + 1)..^15]);
            Locate(e, sql, e.Identifier, TokenWanted.Plain);
        }
        else if (Take(m, "table ", out name) && name.EndsWith(" already exists", StringComparison.Ordinal))
        {
            e.Identifier = name[..^15];
            Locate(e, sql, e.Identifier, TokenWanted.Plain);
        }
        else if (m.StartsWith("duplicate column name: ", StringComparison.Ordinal))
        {
            e.Identifier = m[23..];
            Locate(e, sql, e.Identifier, TokenWanted.Plain, last: true);
        }
        else if (m.StartsWith("missing datatype for ", StringComparison.Ordinal) || m.StartsWith("unknown datatype for ", StringComparison.Ordinal))
        {
            string rest = m[21..];
            int colon = rest.IndexOf(':', StringComparison.Ordinal);
            string qual = colon >= 0 ? rest[..colon] : rest;
            string col = LastPart(qual);
            string target = col;
            if (colon >= 0 && rest.Length > colon + 3)
            {
                string written = rest[(colon + 3)..^1]; // the type as written, between the quotes
                int sp = written.IndexOfAny([' ', '(']);
                target = sp > 0 ? written[..sp] : written;
            }

            e.Identifier = target;
            Locate(e, sql, target, TokenWanted.Plain);
        }
        else if (m.StartsWith("table ", StringComparison.Ordinal) && m.Contains(" has no column named ", StringComparison.Ordinal))
        {
            int k = m.IndexOf(" has no column named ", StringComparison.Ordinal);
            string table = m[6..k];
            string col = m[(k + 21)..];
            e.Identifier = col;
            Locate(e, sql, col, TokenWanted.Plain);
            if (cat?.Find(table) is { } ti)
            {
                e.Suggestions = Closest(col, ti.Columns.Select(c => c.Name));
            }
        }
    }

    private static void ColumnError(OneLiteException e, string sql, Catalog? cat, string m, string prefix)
    {
        string text = m[prefix.Length..];
        int q = text.IndexOf("\" - should", StringComparison.Ordinal);
        bool literalHint = q > 0 && text[0] == '"';
        if (literalHint)
        {
            text = text[1..q];
        }
        else if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            text = text[1..^1];
        }

        string table = string.Empty, col = text;
        int dot = text.LastIndexOf('.');
        if (dot >= 0)
        {
            col = text[(dot + 1)..];
            table = text[..dot];
            int d2 = table.LastIndexOf('.');
            if (d2 >= 0)
            {
                table = table[(d2 + 1)..];
            }
        }

        e.Identifier = col;
        Locate(e, sql, col, TokenWanted.Plain, qualifier: table.Length > 0 ? table : null, literal: literalHint);
        if (cat is null)
        {
            return;
        }

        // The tables the statement mentions are the ones in scope; a column missing from the named table may live in another.
        List<TableInfo> inScope = TablesMentioned(sql, cat);
        if (table.Length > 0 && !inScope.Any(t => string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase)))
        {
            table = string.Empty; // the qualifier is an alias; any table in scope may be the one meant
        }
        var found = new List<string>();
        if (prefix.StartsWith("ambiguous", StringComparison.Ordinal))
        {
            foreach (TableInfo t in inScope)
            {
                if (t.Columns.Any(c => string.Equals(c.Name, col, StringComparison.OrdinalIgnoreCase)))
                {
                    found.Add(t.Name + "." + col);
                }
            }

            e.Suggestions = found.Count > 1 ? found : [];
            return;
        }

        foreach (TableInfo t in inScope)
        {
            if (table.Length > 0 && string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (t.Columns.Any(c => string.Equals(c.Name, col, StringComparison.OrdinalIgnoreCase)))
            {
                found.Add(t.Name + "." + col);
            }
        }

        if (found.Count == 0)
        {
            // Not in any table the statement mentions: say which other tables have such a column.
            foreach (TableInfo t in cat.Tables.Values.Where(t => !inScope.Contains(t)).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (t.Columns.Any(c => string.Equals(c.Name, col, StringComparison.OrdinalIgnoreCase)))
                {
                    found.Add(t.Name + "." + col);
                }
            }
        }

        if (found.Count > 0)
        {
            e.Suggestions = found.Take(3).ToArray();
            return;
        }

        var pool = new List<string>();
        foreach (TableInfo t in inScope)
        {
            if (table.Length == 0 || string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase))
            {
                pool.AddRange(t.Columns.Select(c => c.Name));
            }
        }

        e.Suggestions = Closest(col, pool.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string Arity(FuncDef d)
        => d.MinArgs == d.MaxArgs ? $"{d.Name}() takes {d.MinArgs} argument{(d.MinArgs == 1 ? "" : "s")}"
            : d.MaxArgs >= 1000 ? $"{d.Name}() takes at least {d.MinArgs} argument{(d.MinArgs == 1 ? "" : "s")}"
            : $"{d.Name}() takes {d.MinArgs} to {d.MaxArgs} arguments";

    private static IEnumerable<string> TableNames(Catalog cat) => cat.Tables.Values.Select(t => t.Name).Where(n => !n.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)).Distinct();

    /// <summary>Names a WITH clause defines: a name followed by AS and an opening parenthesis (or MATERIALIZED).</summary>
    private static List<string> CteNames(string sql)
    {
        var res = new List<string>();
        string? last = null;
        bool wasName = false;
        int pos = 0;
        for (; ; )
        {
            Token t = Lexer.Next(sql, pos);
            if (t.Kind == TokenKind.Eof || t.End <= t.Start)
            {
                break;
            }

            pos = t.End;
            if (t.Kind == TokenKind.As && wasName && last is not null)
            {
                res.Add(last);
            }

            wasName = TryName(sql, t, out string text);
            last = wasName ? text : null;
        }

        return res;
    }

    private static IEnumerable<string> FunctionNames() => FunctionTable.Names().Concat(s_aggregates).Distinct(StringComparer.OrdinalIgnoreCase);

    private static List<TableInfo> TablesMentioned(string sql, Catalog cat)
    {
        var res = new List<TableInfo>();
        foreach ((string text, _, _, _) in Words(sql))
        {
            if (cat.Find(text) is { } t && !res.Contains(t))
            {
                res.Add(t);
            }
        }

        return res;
    }

    // ------------------------------------------------------------------ constraint errors

    private static void Constraint(OneLiteException e, string sql)
    {
        string m = e.Message;
        int ext = e.ExtendedCode;
        ConstraintKind kind;
        string? table = null, name = null;
        var cols = new List<string>();
        if (m.StartsWith("UNIQUE constraint failed: ", StringComparison.Ordinal) || m.StartsWith("PRIMARY KEY constraint failed: ", StringComparison.Ordinal))
        {
            kind = ext == ConstraintCodes.PrimaryKey ? ConstraintKind.PrimaryKey : ConstraintKind.Unique;
            ParseColumns(m[(m.IndexOf(':', StringComparison.Ordinal) + 2)..], ref table, cols);
        }
        else if (m.StartsWith("NOT NULL constraint failed: ", StringComparison.Ordinal))
        {
            kind = ConstraintKind.NotNull;
            ParseColumns(m[28..], ref table, cols);
        }
        else if (m.StartsWith("CHECK constraint failed: ", StringComparison.Ordinal))
        {
            kind = ConstraintKind.Check;
            name = m[25..];
        }
        else if (m.StartsWith("FOREIGN KEY constraint failed", StringComparison.Ordinal))
        {
            kind = ConstraintKind.ForeignKey;
        }
        else if (m.StartsWith("cannot store ", StringComparison.Ordinal) && m.Contains(" column ", StringComparison.Ordinal))
        {
            kind = ConstraintKind.DataType;
            ParseColumns(m[(m.LastIndexOf(" column ", StringComparison.Ordinal) + 8)..], ref table, cols);
        }
        else
        {
            kind = ConstraintKind.Other;
        }

        IReadOnlyList<string> values = e.Constraint?.KeyValues ?? [];
        e.Constraint = new ConstraintInfo(kind, table, cols, name, values);

        // Point at the column named in the statement if it appears there, else at the table.
        string? target = cols.Count > 0 ? cols[0] : table;
        if (target is not null)
        {
            e.Identifier = target;
            Locate(e, sql, target, TokenWanted.Plain);
            if (e.Position is null && table is not null && target != table)
            {
                Locate(e, sql, table, TokenWanted.Plain);
            }
        }
    }

    private static void ParseColumns(string list, ref string? table, List<string> cols)
    {
        foreach (string part in list.Split(", "))
        {
            int dot = part.LastIndexOf('.');
            if (dot > 0)
            {
                table ??= part[..dot];
                cols.Add(part[(dot + 1)..]);
            }
            else
            {
                cols.Add(part);
            }
        }
    }

    /// <summary>Renders values of the conflicting key as SQL literals, for the constraint details.</summary>
    internal static string Literal(in Storage.Record.Mem v)
    {
        switch (v.Kind)
        {
            case Storage.Record.StorageClass.Null:
                return "NULL";
            case Storage.Record.StorageClass.Integral:
                return v.IntValue.ToString(CultureInfo.InvariantCulture);
            case Storage.Record.StorageClass.Real:
                return v.RealValue.ToString("R", CultureInfo.InvariantCulture);
            case Storage.Record.StorageClass.Blob:
                return "x'" + Convert.ToHexString(v.Bytes) + "'";
            default:
            {
                string s = Encoding.UTF8.GetString(v.Bytes);
                if (s.Length > 40)
                {
                    s = s[..40] + "...";
                }

                return "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";
            }
        }
    }

    // ------------------------------------------------------------------ locating text

    private enum TokenWanted : byte { Plain, Call }

    private static void Locate(OneLiteException e, string sql, string name, TokenWanted wanted, string? qualifier = null, bool literal = false, bool last = false)
    {
        Token? prev = null, prev2 = null;
        int found = -1, foundEnd = -1;
        int pos = 0;
        string? prevText = null, prevText2 = null;
        for (; ; )
        {
            Token t = Lexer.Next(sql, pos);
            if (t.Kind == TokenKind.Eof || t.End <= t.Start)
            {
                break;
            }

            pos = t.End;
            Token? next = null;
            if (TryName(sql, t, out string text) && string.Equals(text, name, StringComparison.OrdinalIgnoreCase))
            {
                bool afterDot = prev is { Kind: TokenKind.Dot };
                bool quoted = (t.Flags & (TokenFlags.DoubleQuoted | TokenFlags.Bracketed | TokenFlags.Backquoted)) != 0;
                bool ok;
                if (wanted == TokenWanted.Call)
                {
                    Token n = Lexer.Next(sql, t.End);
                    next = n;
                    ok = n.Kind == TokenKind.LParen;
                }
                else if (qualifier is not null)
                {
                    ok = afterDot && prevText2 is not null && string.Equals(prevText2, qualifier, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    ok = true;
                }

                if (literal && !quoted)
                {
                    ok = false;
                }

                if (ok)
                {
                    found = t.Start;
                    foundEnd = t.End;
                    if (!last)
                    {
                        break;
                    }
                }
            }

            prev2 = prev;
            prev = t;
            prevText2 = prevText;
            prevText = TryName(sql, t, out string pt) ? pt : null;
        }

        _ = prev2;
        if (found >= 0)
        {
            SetPosition(e, sql, found, foundEnd - found);
        }
    }

    private static void SetPosition(OneLiteException e, string sql, int offset, int length)
    {
        offset = Math.Clamp(offset, 0, sql.Length);
        length = Math.Max(1, length);
        int line = 1, lineStart = 0;
        for (int i = 0; i < offset; i++)
        {
            if (sql[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        int lineEnd = sql.IndexOf('\n', lineStart);
        if (lineEnd < 0)
        {
            lineEnd = sql.Length;
        }

        int col = offset - lineStart + 1;
        int bytes = Encoding.UTF8.GetByteCount(sql.AsSpan(0, offset));
        e.Position = new SqlPosition(offset, length, line, col, bytes);

        string text = sql[lineStart..lineEnd].TrimEnd('\r');
        int caretAt = offset - lineStart;
        const int Window = 120;
        string prefix = string.Empty;
        if (text.Length > Window)
        {
            int from = Math.Max(0, caretAt - (Window / 2));
            int to = Math.Min(text.Length, from + Window);
            prefix = from > 0 ? "..." : string.Empty;
            text = prefix + text[from..to] + (to < text.Length ? "..." : string.Empty);
            caretAt = caretAt - from + prefix.Length;
        }

        var sb = new StringBuilder();
        sb.Append(text).Append('\n');
        for (int i = 0; i < caretAt && i < text.Length; i++)
        {
            sb.Append(text[i] == '\t' ? '\t' : ' ');
        }

        int span = Math.Max(1, Math.Min(length, text.Length - caretAt));
        sb.Append('^').Append('~', span - 1);
        e.Caret = sb.ToString();
    }

    private static IEnumerable<(string Text, int Start, int End, Token Tok)> Words(string sql)
    {
        int pos = 0;
        for (; ; )
        {
            Token t = Lexer.Next(sql, pos);
            if (t.Kind == TokenKind.Eof || t.End <= t.Start)
            {
                yield break;
            }

            pos = t.End;
            if (TryName(sql, t, out string text))
            {
                yield return (text, t.Start, t.End, t);
            }
        }
    }

    private static bool TryName(string sql, Token t, out string text)
    {
        text = string.Empty;
        if (t.End > sql.Length || t.End <= t.Start)
        {
            return false;
        }

        string raw = sql[t.Start..t.End];
        char c = raw[0];
        if ((t.Flags & TokenFlags.DoubleQuoted) != 0 || (t.Flags & TokenFlags.Backquoted) != 0)
        {
            text = raw.Length >= 2 ? raw[1..^1].Replace(new string(c, 2), c.ToString(), StringComparison.Ordinal) : raw;
            return true;
        }

        if ((t.Flags & TokenFlags.Bracketed) != 0)
        {
            text = raw.Length >= 2 ? raw[1..^1] : raw;
            return true;
        }

        if (char.IsLetter(c) || c == '_')
        {
            text = raw;
            return true;
        }

        return false;
    }

    private static int CharIndexOfByte(string sql, int byteOffset)
    {
        int bytes = 0, i = 0;
        while (i < sql.Length && bytes < byteOffset)
        {
            char c = sql[i];
            if (char.IsHighSurrogate(c) && i + 1 < sql.Length)
            {
                bytes += 4;
                i += 2;
            }
            else
            {
                bytes += c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
                i++;
            }
        }

        return i;
    }

    private static bool Take(string m, string prefix, out string rest)
    {
        if (m.StartsWith(prefix, StringComparison.Ordinal))
        {
            rest = m[prefix.Length..];
            return true;
        }

        rest = string.Empty;
        return false;
    }

    private static string LastPart(string qualified)
    {
        int dot = qualified.LastIndexOf('.');
        return dot >= 0 ? qualified[(dot + 1)..] : qualified;
    }

    // ------------------------------------------------------------------ suggestions

    /// <summary>The (at most three) candidates closest to <paramref name="word"/> by edit distance, within about a third of its length, closest first.</summary>
    internal static IReadOnlyList<string> Closest(string word, IEnumerable<string> candidates)
    {
        if (word.Length == 0)
        {
            return [];
        }

        int max = Math.Clamp(word.Length / 3, 1, 3);
        var scored = new List<(int D, string Name)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string c in candidates)
        {
            if (!seen.Add(c) || string.Equals(c, word, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int d = Distance(word, c);
            if (word.Length >= 2 && d > max && IsSubsequence(word, c))
            {
                d = max;
            }

            if (d <= max || (word.Length >= 3 && c.Length > word.Length && c.StartsWith(word, StringComparison.OrdinalIgnoreCase)))
            {
                scored.Add((Math.Min(d, max), c));
            }
        }

        return [.. scored.OrderBy(s => s.D).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Take(3).Select(s => s.Name)];
    }

    private static bool IsSubsequence(string word, string candidate)
    {
        int i = 0;
        foreach (char ch in candidate)
        {
            if (i < word.Length && char.ToLowerInvariant(ch) == char.ToLowerInvariant(word[i]))
            {
                i++;
            }
        }

        return i == word.Length;
    }

    /// <summary>Optimal-string-alignment distance (insert, delete, substitute, swap two neighbours), ignoring case.</summary>
    internal static int Distance(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        int n = a.Length, m = b.Length;
        if (Math.Abs(n - m) > 4)
        {
            return Math.Abs(n - m);
        }

        var d = new int[n + 1, m + 1];
        for (int i = 0; i <= n; i++)
        {
            d[i, 0] = i;
        }

        for (int j = 0; j <= m; j++)
        {
            d[0, j] = j;
        }

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int v = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    v = Math.Min(v, d[i - 2, j - 2] + 1);
                }

                d[i, j] = v;
            }
        }

        return d[n, m];
    }

    // ------------------------------------------------------------------ rendering

    internal static string Render(OneLiteException e)
    {
        if (e.Sql is null || (e.Position is null && e.Suggestions.Count == 0 && e.Constraint is null))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        if (e.Position is { } p)
        {
            sb.Append("at ").Append(p.ToString()).Append(": ").Append(e.Message);
            if (e.Caret is { } caret)
            {
                foreach (string l in caret.Split('\n'))
                {
                    sb.Append("\n  ").Append(l);
                }
            }
        }

        if (e.Constraint is { } c)
        {
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(c.Kind switch
            {
                ConstraintKind.Unique or ConstraintKind.PrimaryKey => "the key (" + string.Join(", ", c.Columns) + ")" + (c.KeyValues.Count > 0 ? " = (" + string.Join(", ", c.KeyValues) + ")" : string.Empty) + " already exists in table " + c.Table,
                ConstraintKind.NotNull => "column " + c.Table + "." + string.Join(", ", c.Columns) + " may not be NULL",
                ConstraintKind.Check => "the row violates CHECK " + c.Name,
                ConstraintKind.DataType => "column " + c.Table + "." + string.Join(", ", c.Columns) + " is in a STRICT table and does not accept that type",
                ConstraintKind.ForeignKey => "a row refers to, or is referred to by, a row that does not exist",
                _ => "constraint failed",
            });
        }

        if (e.Suggestions.Count > 0)
        {
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            bool isArity = e.Message.StartsWith("wrong number", StringComparison.Ordinal);
            bool isMatch = e.Message.StartsWith("ambiguous", StringComparison.Ordinal);
            if (isArity)
            {
                sb.Append(e.Suggestions[0]);
            }
            else if (isMatch)
            {
                sb.Append("write one of: ").Append(string.Join(", ", e.Suggestions));
            }
            else
            {
                sb.Append("did you mean ").Append(string.Join(" or ", e.Suggestions.Select(s => "\"" + s + "\""))).Append('?');
            }
        }

        return sb.ToString();
    }
}
