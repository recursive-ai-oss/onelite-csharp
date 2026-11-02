using System.Collections.Generic;

namespace RecursiveAI.OneLite.Sql.Parsing;

/// <summary>
/// The SQL tokenizer. It is a pure function of (text, position): it allocates nothing, never throws, and reports a character or
/// run it cannot tokenize as an <see cref="TokenKind.Illegal"/> token so the parser can decide when that is an error
/// (a syntax error earlier in the statement wins over an illegal token later in it).
/// </summary>
internal static class Lexer
{
    private static readonly Dictionary<string, TokenKind> s_keywords = BuildKeywords();
    private static readonly Dictionary<string, TokenKind>.AlternateLookup<ReadOnlySpan<char>> s_keywordLookup = s_keywords.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly bool[] s_fallback = BuildFallback();

    private const int LongestKeyword = 17; // CURRENT_TIMESTAMP

    private static Dictionary<string, TokenKind> BuildKeywords()
    {
        var d = new Dictionary<string, TokenKind>(KeywordTable.All.Length, StringComparer.Ordinal);
        foreach ((string text, TokenKind kind) in KeywordTable.All)
        {
            d.Add(text, kind);
        }

        return d;
    }

    private static bool[] BuildFallback()
    {
        var f = new bool[256];
        foreach (TokenKind k in KeywordTable.Fallback)
        {
            f[(int)k] = true;
        }

        return f;
    }

    /// <summary>True for keywords that may stand in for an identifier where the grammar has no use for the keyword (lang_keywords).</summary>
    internal static bool IsFallback(TokenKind k) => s_fallback[(int)k];

    /// <summary>The words that begin a join operator and, unlike other keywords, may name a table or column but not an alias.</summary>
    internal static bool IsJoinKeyword(TokenKind k) => k is TokenKind.Cross or TokenKind.Full or TokenKind.Inner or TokenKind.Left or TokenKind.Natural or TokenKind.Outer or TokenKind.Right;

    private static bool IsIdChar(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '$' || c >= 0x80;

    private static bool IsIdStart(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_' || c >= 0x80;

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    private static bool IsHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    /// <summary>Looks a word up in the keyword table, ignoring ASCII case.</summary>
    internal static bool TryKeyword(ReadOnlySpan<char> word, out TokenKind kind)
    {
        kind = default;
        if (word.Length < 2 || word.Length > LongestKeyword)
        {
            return false;
        }

        Span<char> upper = stackalloc char[LongestKeyword];
        for (int i = 0; i < word.Length; i++)
        {
            char c = word[i];
            if (c >= 0x80)
            {
                return false;
            }

            upper[i] = c is >= 'a' and <= 'z' ? (char)(c - 32) : c;
        }

        return s_keywordLookup.TryGetValue(upper[..word.Length], out kind);
    }

    /// <summary>The token that starts at or after <paramref name="pos"/>, after skipping blanks and comments.</summary>
    internal static Token Next(string s, int pos)
    {
        int n = s.Length;
        int i = pos;
        while (i < n)
        {
            char c = s[i];
            if (c is ' ' or '\t' or '\n' or '\f' or '\r')
            {
                // a vertical tab counts as a blank only inside a run that began with another blank (found by probing)
                i++;
                while (i < n && s[i] is ' ' or '\t' or '\n' or '\f' or '\r' or '\v')
                {
                    i++;
                }
            }
            else if (c == '-' && i + 1 < n && s[i + 1] == '-')
            {
                i += 2;
                while (i < n && s[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && i + 2 < n && s[i + 1] == '*') // a lone "/*" at the very end is a slash and a star
            {
                i += 2;
                while (i < n && !(s[i] == '*' && i + 1 < n && s[i + 1] == '/'))
                {
                    i++;
                }

                i = i < n ? i + 2 : n; // an unterminated comment runs to the end of the input
            }
            else
            {
                break;
            }
        }

        if (i >= n || s[i] == '\0')
        {
            return new Token(TokenKind.Eof, i, i);
        }

        int start = i;
        char ch = s[i];
        char next = i + 1 < n ? s[i + 1] : '\0';
        switch (ch)
        {
            case '-':
                if (next == '>')
                {
                    return i + 2 < n && s[i + 2] == '>' ? new Token(TokenKind.Arrow2, start, i + 3) : new Token(TokenKind.Arrow, start, i + 2);
                }

                return new Token(TokenKind.Minus, start, i + 1);
            case '(': return new Token(TokenKind.LParen, start, i + 1);
            case ')': return new Token(TokenKind.RParen, start, i + 1);
            case ';': return new Token(TokenKind.Semi, start, i + 1);
            case '+': return new Token(TokenKind.Plus, start, i + 1);
            case '*': return new Token(TokenKind.Star, start, i + 1);
            case '%': return new Token(TokenKind.Percent, start, i + 1);
            case '&': return new Token(TokenKind.Amp, start, i + 1);
            case '~': return new Token(TokenKind.Tilde, start, i + 1);
            case ',': return new Token(TokenKind.Comma, start, i + 1);
            case '/': return new Token(TokenKind.Slash, start, i + 1);
            case '=': return new Token(TokenKind.Eq, start, next == '=' ? i + 2 : i + 1);
            case '<':
                if (next == '=')
                {
                    return new Token(TokenKind.Le, start, i + 2);
                }

                if (next == '>')
                {
                    return new Token(TokenKind.Ne, start, i + 2);
                }

                return next == '<' ? new Token(TokenKind.Lshift, start, i + 2) : new Token(TokenKind.Lt, start, i + 1);
            case '>':
                if (next == '=')
                {
                    return new Token(TokenKind.Ge, start, i + 2);
                }

                return next == '>' ? new Token(TokenKind.Rshift, start, i + 2) : new Token(TokenKind.Gt, start, i + 1);
            case '!':
                return next == '=' ? new Token(TokenKind.Ne, start, i + 2) : new Token(TokenKind.Illegal, start, i + 1);
            case '|':
                return next == '|' ? new Token(TokenKind.Concat, start, i + 2) : new Token(TokenKind.Bar, start, i + 1);
            case '.':
                return IsDigit(next) ? Number(s, start) : new Token(TokenKind.Dot, start, i + 1);
            case '\'':
                return Quoted(s, start, '\'', TokenKind.String, TokenFlags.None);
            case '"':
                return Quoted(s, start, '"', TokenKind.Id, TokenFlags.DoubleQuoted);
            case '`':
                return Quoted(s, start, '`', TokenKind.Id, TokenFlags.Backquoted);
            case '[':
            {
                int j = i + 1;
                while (j < n && s[j] != ']')
                {
                    j++;
                }

                return j < n ? new Token(TokenKind.Id, start, j + 1, TokenFlags.Bracketed) : new Token(TokenKind.Illegal, start, n);
            }

            case '?':
            {
                int j = i + 1;
                while (j < n && IsDigit(s[j]))
                {
                    j++;
                }

                return new Token(TokenKind.Variable, start, j);
            }

            case ':':
            case '@':
            case '$':
                return Variable(s, start);
            case '#':
                return Variable(s, start); // #name is a variable like :name
        }

        if (IsDigit(ch))
        {
            return Number(s, start);
        }

        if ((ch is 'x' or 'X') && next == '\'')
        {
            // lang_expr blob literals: an even number of hex digits between the quotes. A bad one is one illegal token
            // that runs to the closing quote.
            int j = i + 2;
            while (j < n && IsHex(s[j]))
            {
                j++;
            }

            if (j < n && s[j] == '\'' && ((j - (i + 2)) & 1) == 0)
            {
                return new Token(TokenKind.Blob, start, j + 1);
            }

            while (j < n && s[j] != '\'')
            {
                j++;
            }

            return new Token(TokenKind.Illegal, start, j < n ? j + 1 : n);
        }

        if (IsIdStart(ch))
        {
            int j = i + 1;
            while (j < n && IsIdChar(s[j]))
            {
                j++;
            }

            return TryKeyword(s.AsSpan(start, j - start), out TokenKind kw) ? new Token(kw, start, j) : new Token(TokenKind.Id, start, j);
        }

        return new Token(TokenKind.Illegal, start, i + 1);
    }

    private static Token Quoted(string s, int start, char quote, TokenKind kind, TokenFlags flags)
    {
        int n = s.Length;
        int j = start + 1;
        while (j < n)
        {
            if (s[j] == quote)
            {
                if (j + 1 < n && s[j + 1] == quote)
                {
                    j += 2;
                    continue;
                }

                return new Token(kind, start, j + 1, flags);
            }

            j++;
        }

        return new Token(TokenKind.Illegal, start, n); // unterminated: the rest of the input is the token
    }

    // lang_expr "Variables": ?NNN is handled by the caller; :AAAA @AAAA $AAAA take identifier characters, may contain "::", and may end in
    // a parenthesised suffix that runs to the first ")" and may not contain a blank.
    private static Token Variable(string s, int start)
    {
        int n = s.Length;
        int i = start + 1;
        int count = 0;
        while (i < n)
        {
            char c = s[i];
            if (IsIdChar(c))
            {
                count++;
                i++;
            }
            else if (c == '(' && count > 0)
            {
                i++;
                while (i < n && s[i] is not (' ' or '\t' or '\n' or '\f' or '\r') && s[i] != ')')
                {
                    i++;
                }

                if (i < n && s[i] == ')')
                {
                    i++;
                }
                else
                {
                    count = 0;
                }

                break;
            }
            else if (c == ':' && i + 1 < n && s[i + 1] == ':')
            {
                i += 2;
            }
            else
            {
                break;
            }
        }

        return count == 0 ? new Token(TokenKind.Illegal, start, i) : new Token(TokenKind.Variable, start, i);
    }

    // datatype3 / lang_expr numeric literals: decimal with optional point and exponent, or 0x followed by hex digits. Digits may be
    // separated by single underscores. A number that runs straight into identifier characters (1e, 12abc, 0x) is one illegal token.
    private static Token Number(string s, int start)
    {
        int n = s.Length;
        int i = start;
        bool isFloat = false;
        bool hasUnderscore = false;
        bool hex = false;
        if (s[i] == '0' && i + 1 < n && (s[i + 1] is 'x' or 'X') && i + 2 < n && IsHex(s[i + 2]))
        {
            hex = true;
            i += 2;
            while (i < n && (IsHex(s[i]) || s[i] == '_'))
            {
                hasUnderscore |= s[i] == '_';
                i++;
            }
        }
        else
        {
            while (i < n && (IsDigit(s[i]) || s[i] == '_'))
            {
                hasUnderscore |= s[i] == '_';
                i++;
            }

            if (i < n && s[i] == '.')
            {
                isFloat = true;
                i++;
                while (i < n && (IsDigit(s[i]) || s[i] == '_'))
                {
                    hasUnderscore |= s[i] == '_';
                    i++;
                }
            }

            if (i < n && (s[i] is 'e' or 'E'))
            {
                int j = i + 1;
                if (j < n && (s[j] is '+' or '-'))
                {
                    j++;
                }

                if (j < n && IsDigit(s[j]))
                {
                    isFloat = true;
                    i = j;
                    while (i < n && (IsDigit(s[i]) || s[i] == '_'))
                    {
                        hasUnderscore |= s[i] == '_';
                        i++;
                    }
                }
            }
        }

        if (i < n && IsIdChar(s[i]))
        {
            while (i < n && IsIdChar(s[i]))
            {
                i++;
            }

            return new Token(TokenKind.Illegal, start, i);
        }

        TokenFlags flags = hasUnderscore && !SeparatorsValid(s, start, i, hex) ? TokenFlags.BadNumber : TokenFlags.None;
        TokenKind kind = isFloat || (!hex && IntegerOverflows(s, start, i)) ? TokenKind.Float : TokenKind.Integer;
        return new Token(kind, start, i, flags);
    }

    private static bool SeparatorsValid(string s, int start, int end, bool hex)
    {
        for (int k = start; k < end; k++)
        {
            if (s[k] != '_')
            {
                continue;
            }

            bool before = k > start && (hex ? IsHex(s[k - 1]) : IsDigit(s[k - 1])) && s[k - 1] != '_';
            bool after = k + 1 < end && (hex ? IsHex(s[k + 1]) : IsDigit(s[k + 1]));
            if (!before || !after)
            {
                return false;
            }
        }

        return true;
    }

    // lang_expr: an integer literal outside the 64-bit range is a floating-point literal.
    private static bool IntegerOverflows(string s, int start, int end)
    {
        const string Max = "9223372036854775807";
        int digits = 0;
        int cmp = 0;
        bool significant = false;
        for (int k = start; k < end; k++)
        {
            char c = s[k];
            if (c == '_')
            {
                continue;
            }

            if (!significant && c == '0')
            {
                continue;
            }

            significant = true;
            if (digits < Max.Length && cmp == 0)
            {
                cmp = c.CompareTo(Max[digits]);
            }

            digits++;
        }

        return digits > Max.Length || (digits == Max.Length && cmp > 0);
    }
}
