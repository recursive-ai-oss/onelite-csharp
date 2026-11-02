using System.Text;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Sql.Parsing;

namespace RecursiveAI.OneLite.Planning;

// ALTER TABLE ... RENAME (lang_altertable): the stored CREATE text of every object that mentions the table or column is rewritten token by
// token, so the text the user wrote (spacing, comments, quoting of the rest) stays as it was and only the renamed identifiers change, to the
// new name in double quotes. A name is taken as a reference to the table when it stands where a table can: after FROM, JOIN, INTO, UPDATE,
// REFERENCES, TABLE or the ON of an index or trigger header, after a comma of a FROM list, or in front of ".".
internal static class SqlRewrite
{
    internal static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    internal static string Unquote(string raw)
    {
        if (raw.Length >= 2)
        {
            char q = raw[0];
            if (q is '"' or '`')
            {
                return raw[1..^1].Replace(new string(q, 2), new string(q, 1), StringComparison.Ordinal);
            }

            if (q == '[')
            {
                return raw[1..^1];
            }
        }

        return raw;
    }

    internal static List<Token> Lex(string sql)
    {
        var toks = new List<Token>();
        int pos = 0;
        for (; ; )
        {
            Token t = Lexer.Next(sql, pos);
            if (t.Kind == TokenKind.Eof)
            {
                return toks;
            }

            toks.Add(t);
            pos = t.End;
        }
    }

    private static bool IsName(string sql, Token t, string name)
        => t.Kind == TokenKind.Id && AsciiComparer.Instance.Equals(Unquote(sql[t.Start..t.End]), name);

    private static bool IsFromEnd(TokenKind k)
        => k is TokenKind.Where or TokenKind.Group or TokenKind.Order or TokenKind.Having or TokenKind.Limit or TokenKind.Union or TokenKind.Except
            or TokenKind.Intersect or TokenKind.Set or TokenKind.Values or TokenKind.Returning or TokenKind.Semi or TokenKind.Window;

    private static string Apply(string sql, List<(Token Tok, string Text)> edits)
    {
        if (edits.Count == 0)
        {
            return sql;
        }

        var sb = new StringBuilder();
        int at = 0;
        foreach ((Token t, string text) in edits)
        {
            sb.Append(sql, at, t.Start - at).Append(text);
            at = t.End;
        }

        return sb.Append(sql, at, sql.Length - at).ToString();
    }

    /// <summary>Every name the text uses where a table can stand, as indexes into the token list.</summary>
    private static List<int> TableRefs(string sql, List<Token> toks, string table)
    {
        var hits = new List<int>();
        int fromDepth = -1;
        int depth = 0;
        var fromStack = new Stack<int>();
        for (int i = 0; i < toks.Count; i++)
        {
            Token t = toks[i];
            switch (t.Kind)
            {
                case TokenKind.LParen:
                    depth++;
                    continue;
                case TokenKind.RParen:
                    if (fromDepth == depth)
                    {
                        fromDepth = fromStack.Count > 0 ? fromStack.Pop() : -1;
                    }

                    depth--;
                    continue;
                case TokenKind.From:
                    fromStack.Push(fromDepth);
                    fromDepth = depth;
                    continue;
                default:
                    if (fromDepth == depth && IsFromEnd(t.Kind))
                    {
                        fromDepth = fromStack.Count > 0 ? fromStack.Pop() : -1;
                    }

                    break;
            }

            if (!IsName(sql, t, table))
            {
                continue;
            }

            TokenKind prev = i > 0 ? toks[i - 1].Kind : TokenKind.Eof;
            bool nextDot = i + 1 < toks.Count && toks[i + 1].Kind == TokenKind.Dot;
            if (prev == TokenKind.Dot)
            {
                // main.t1
                if (i >= 2 && IsName(sql, toks[i - 2], "main") && !nextDot)
                {
                    hits.Add(i);
                }

                continue;
            }

            if (nextDot
                || prev is TokenKind.From or TokenKind.Join or TokenKind.Into or TokenKind.Update or TokenKind.References or TokenKind.Table
                || (prev == TokenKind.On && IsHeaderOn(toks, i))
                || (prev == TokenKind.Comma && fromDepth == depth))
            {
                hits.Add(i);
            }
        }

        return hits;
    }

    // The ON of CREATE INDEX name ON table / CREATE TRIGGER ... ON table, as opposed to a join's ON.
    private static bool IsHeaderOn(List<Token> toks, int i)
    {
        for (int k = 0; k < i && k < 12; k++)
        {
            if (toks[k].Kind == TokenKind.Select)
            {
                return false;
            }
        }

        return toks.Count > 1 && toks[0].Kind == TokenKind.Create;
    }

    /// <summary>The text with each reference to table <paramref name="oldName"/> renamed.</summary>
    internal static string RenameTable(string sql, string oldName, string newName)
    {
        List<Token> toks = Lex(sql);
        var edits = new List<(Token, string)>();
        foreach (int i in TableRefs(sql, toks, oldName))
        {
            edits.Add((toks[i], Quote(newName)));
        }

        return Apply(sql, edits);
    }

    /// <summary>
    /// Renames column <paramref name="oldCol"/> of <paramref name="table"/> in the text of one object. <paramref name="own"/> is true for the
    /// table's own CREATE TABLE and its indexes (every use of the name is the column); <paramref name="newOld"/> is true for a trigger on the
    /// table (NEW.x and OLD.x); <paramref name="bare"/> says an unqualified use may be taken as this table's column.
    /// </summary>
    internal static string RenameColumn(string sql, string table, string oldCol, string newCol, bool own, bool newOld, bool bare, string? newText = null, List<(int Start, int End)>? hits = null)
    {
        newText ??= Quote(newCol);
        List<Token> toks = Lex(sql);
        var aliases = new List<string> { table };
        if (!own)
        {
            // t1 AS q / t1 q
            foreach (int i in TableRefs(sql, toks, table))
            {
                if (i + 1 < toks.Count && toks[i + 1].Kind == TokenKind.As && i + 2 < toks.Count && toks[i + 2].Kind == TokenKind.Id)
                {
                    aliases.Add(Unquote(sql[toks[i + 2].Start..toks[i + 2].End]));
                }
                else if (i + 1 < toks.Count && toks[i + 1].Kind == TokenKind.Id)
                {
                    aliases.Add(Unquote(sql[toks[i + 1].Start..toks[i + 1].End]));
                }
            }
        }

        var edits = new List<(Token, string)>();
        bool afterReferences = false;
        string? refTable = null;
        for (int i = 0; i < toks.Count; i++)
        {
            Token t = toks[i];
            TokenKind prev = i > 0 ? toks[i - 1].Kind : TokenKind.Eof;
            if (t.Kind == TokenKind.References)
            {
                afterReferences = true;
                refTable = null;
                continue;
            }

            if (afterReferences)
            {
                if (refTable is null && t.Kind == TokenKind.Id)
                {
                    refTable = Unquote(sql[t.Start..t.End]);
                    continue;
                }

                if (refTable is not null)
                {
                    if (t.Kind == TokenKind.LParen)
                    {
                        // the parent's columns: ours when it is the table being changed
                        int k = i + 1;
                        for (; k < toks.Count && toks[k].Kind != TokenKind.RParen; k++)
                        {
                            if (AsciiComparer.Instance.Equals(refTable, table) && IsName(sql, toks[k], oldCol))
                            {
                                edits.Add((toks[k], newText));
                            }
                        }

                        i = k;
                    }

                    afterReferences = false;
                    refTable = null;
                    continue;
                }
            }

            if (!IsName(sql, t, oldCol))
            {
                continue;
            }

            bool nextDot = i + 1 < toks.Count && toks[i + 1].Kind == TokenKind.Dot;
            bool nextParen = i + 1 < toks.Count && toks[i + 1].Kind == TokenKind.LParen;
            if (nextDot || nextParen || prev is TokenKind.As or TokenKind.Collate or TokenKind.Table or TokenKind.Index or TokenKind.Trigger or TokenKind.View or TokenKind.Constraint)
            {
                continue;
            }

            if (own && prev == TokenKind.On && i > 0 && toks[0].Kind == TokenKind.Create)
            {
                continue;
            }

            if (prev == TokenKind.Dot)
            {
                string q = i >= 2 ? Unquote(sql[toks[i - 2].Start..toks[i - 2].End]) : "";
                bool qualifies = aliases.Exists(a => AsciiComparer.Instance.Equals(a, q))
                    || (newOld && (q.Equals("new", StringComparison.OrdinalIgnoreCase) || q.Equals("old", StringComparison.OrdinalIgnoreCase)));
                if (qualifies && (i < 3 || toks[i - 3].Kind != TokenKind.Dot || true))
                {
                    edits.Add((t, newText));
                }

                continue;
            }

            if (own || bare)
            {
                edits.Add((t, newText));
            }
        }

        if (hits is not null)
        {
            foreach ((Token ht, string _) in edits)
            {
                // the use as written: a qualifier before the name is part of it
                int start = ht.Start;
                int k = toks.IndexOf(ht);
                if (k >= 2 && toks[k - 1].Kind == TokenKind.Dot)
                {
                    start = toks[k - 2].Start;
                }

                hits.Add((start, ht.End));
            }
        }

        return Apply(sql, edits);
    }
}
