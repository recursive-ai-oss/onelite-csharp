using System;
using System.Text;

namespace RecursiveAI.OneLite.Cli;

/// <summary>Small lexical helpers on SQL text: when a statement is complete, where the next one starts, identifier and literal quoting.</summary>
internal static class SqlText
{
    /// <summary>
    /// True if <paramref name="sql"/> ends in a complete statement, by the rule documented for sqlite3_complete: the last token is a semicolon
    /// that is not inside a CREATE TRIGGER body (a trigger's body statements end in semicolons, so only <c>END ;</c> completes it).
    /// Strings, quoted identifiers and comments are skipped.
    /// </summary>
    public static bool IsComplete(string sql)
    {
        // States: 0 = nothing yet, 1 = saw EXPLAIN, 2 = saw CREATE (and optionally TEMP), 3 = inside CREATE TRIGGER, 4 = other statement.
        int state = 0;
        bool lastSemi = false;
        bool lastEnd = false;
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int e = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (e < 0)
                {
                    return false;
                }

                i = e + 2;
                continue;
            }

            if (c == ';')
            {
                lastSemi = true;
                i++;
                if (state == 3 && !lastEnd)
                {
                    lastSemi = false;
                    continue;
                }

                state = 0;
                lastEnd = false;
                continue;
            }

            lastSemi = false;
            if (c is '\'' or '"' or '`' or '[')
            {
                char close = c == '[' ? ']' : c;
                int j = i + 1;
                while (j < sql.Length)
                {
                    if (sql[j] == close)
                    {
                        if (close != ']' && j + 1 < sql.Length && sql[j + 1] == close)
                        {
                            j += 2;
                            continue;
                        }

                        break;
                    }

                    j++;
                }

                if (j >= sql.Length)
                {
                    return false;
                }

                i = j + 1;
                lastEnd = false;
                if (state is 0 or 1)
                {
                    state = 4;
                }

                continue;
            }

            if (IsIdentChar(c))
            {
                int j = i;
                while (j < sql.Length && IsIdentChar(sql[j]))
                {
                    j++;
                }

                string w = sql[i..j];
                i = j;
                lastEnd = false;
                switch (state)
                {
                    case 0:
                    case 1:
                        if (w.Equals("explain", StringComparison.OrdinalIgnoreCase))
                        {
                            state = 1;
                        }
                        else if (w.Equals("create", StringComparison.OrdinalIgnoreCase))
                        {
                            state = 2;
                        }
                        else
                        {
                            state = 4;
                        }

                        break;
                    case 2:
                        if (w.Equals("trigger", StringComparison.OrdinalIgnoreCase))
                        {
                            state = 3;
                        }
                        else if (!(w.Equals("temp", StringComparison.OrdinalIgnoreCase) || w.Equals("temporary", StringComparison.OrdinalIgnoreCase)))
                        {
                            state = 4;
                        }

                        break;
                    case 3:
                        lastEnd = w.Equals("end", StringComparison.OrdinalIgnoreCase);
                        break;
                    default:
                        break;
                }

                continue;
            }

            // Operators and punctuation.
            i++;
            lastEnd = false;
            if (state is 0 or 1)
            {
                state = 4;
            }
        }

        return lastSemi;
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '.' || c > 127;

    /// <summary>Index of the first character of the next statement: skips white space, comments and stray semicolons. Returns the text length if only blanks remain.</summary>
    public static int SkipBlank(string sql, int start)
    {
        int i = start;
        while (i < sql.Length)
        {
            char c = sql[i];
            if (char.IsWhiteSpace(c) || c == ';')
            {
                i++;
            }
            else if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int e = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = e < 0 ? sql.Length : e + 2;
            }
            else
            {
                break;
            }
        }

        return i;
    }

    /// <summary>Quotes an identifier with double quotes when it is not a plain word (embedded quotes are doubled).</summary>
    public static string QuoteIdent(string name)
    {
        bool plain = name.Length > 0 && !char.IsDigit(name[0]);
        foreach (char c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                plain = false;
            }
        }

        if (plain && !IsKeyword(name))
        {
            return name;
        }

        return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>Always quotes an identifier with double quotes.</summary>
    public static string AlwaysQuoteIdent(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>Quotes text as an SQL string literal.</summary>
    public static string QuoteString(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static readonly string[] Keywords =
    [
        "abort", "action", "add", "after", "all", "alter", "always", "analyze", "and", "as", "asc", "attach", "autoincrement", "before", "begin",
        "between", "by", "cascade", "case", "cast", "check", "collate", "column", "commit", "conflict", "constraint", "create", "cross", "current",
        "current_date", "current_time", "current_timestamp", "database", "default", "deferrable", "deferred", "delete", "desc", "detach", "distinct",
        "do", "drop", "each", "else", "end", "escape", "except", "exclude", "exclusive", "exists", "explain", "fail", "filter", "first", "following",
        "for", "foreign", "from", "full", "generated", "glob", "group", "groups", "having", "if", "ignore", "immediate", "in", "index", "indexed",
        "initially", "inner", "insert", "instead", "intersect", "into", "is", "isnull", "join", "key", "last", "left", "like", "limit", "match",
        "materialized", "natural", "no", "not", "nothing", "notnull", "null", "nulls", "of", "offset", "on", "or", "order", "others", "outer",
        "over", "partition", "plan", "pragma", "preceding", "primary", "query", "raise", "range", "recursive", "references", "regexp", "reindex",
        "release", "rename", "replace", "restrict", "returning", "right", "rollback", "row", "rows", "savepoint", "select", "set", "table", "temp",
        "temporary", "then", "ties", "to", "transaction", "trigger", "unbounded", "union", "unique", "update", "using", "vacuum", "values", "view",
        "virtual", "when", "where", "window", "with", "without",
    ];

    private static bool IsKeyword(string s) => Array.BinarySearch(Keywords, s.ToLowerInvariant(), StringComparer.Ordinal) >= 0;

    /// <summary>The number of display columns of UTF-8 text (wide East Asian characters count 2, combining marks 0).</summary>
    public static int DisplayWidth(ReadOnlySpan<byte> utf8)
    {
        int w = 0;
        int i = 0;
        while (i < utf8.Length)
        {
            int cp = DecodeCodePoint(utf8, i, out int len);
            i += len;
            w += CharWidth(cp);
        }

        return w;
    }

    /// <summary>Decodes one code point (an invalid byte decodes as itself, length 1).</summary>
    public static int DecodeCodePoint(ReadOnlySpan<byte> s, int i, out int len)
    {
        byte b = s[i];
        len = 1;
        if (b < 0x80)
        {
            return b;
        }

        int need = b >= 0xF0 ? 3 : b >= 0xE0 ? 2 : b >= 0xC0 ? 1 : 0;
        if (need == 0 || i + need >= s.Length)
        {
            return b;
        }

        int cp = b & (0x3F >> need);
        for (int k = 1; k <= need; k++)
        {
            if ((s[i + k] & 0xC0) != 0x80)
            {
                return b;
            }

            cp = (cp << 6) | (s[i + k] & 0x3F);
        }

        len = need + 1;
        return cp;
    }

    private static int CharWidth(int cp)
    {
        if (cp is >= 0x300 and <= 0x36F or 0x200B or >= 0xFE00 and <= 0xFE0F)
        {
            return 0;
        }

        bool wide = cp is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0x303E or >= 0x3041 and <= 0x33FF or >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF or >= 0xA000 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF
            or >= 0xFE30 and <= 0xFE6F or >= 0xFF00 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 or >= 0x20000 and <= 0x3FFFD;
        return wide ? 2 : 1;
    }

    /// <summary>Appends the UTF-8 bytes of a string to a list of bytes.</summary>
    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}
