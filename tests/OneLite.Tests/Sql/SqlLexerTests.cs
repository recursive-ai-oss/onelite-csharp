using System.Collections.Generic;
using RecursiveAI.OneLite.Sql.Parsing;

namespace RecursiveAI.OneLite.Tests.Sql;

/// <summary>The tokenizer: token kinds and spans for the lexical rules of lang_keywords, lang_expr and datatype3, and no allocation per token.</summary>
public sealed class SqlLexerTests
{
    private static List<(TokenKind Kind, string Text)> Lex(string sql)
    {
        var list = new List<(TokenKind, string)>();
        int pos = 0;
        for (; ; )
        {
            Token t = Lexer.Next(sql, pos);
            if (t.Kind == TokenKind.Eof)
            {
                return list;
            }

            list.Add((t.Kind, sql.Substring(t.Start, t.End - t.Start)));
            pos = t.End;
        }
    }

    [Theory]
    [InlineData("SELECT select SeLeCt", "Select")]
    [InlineData("current_timestamp", "CurrentTimestamp")]
    public void KeywordsIgnoreCase(string sql, string kindName)
    {
        TokenKind kind = Enum.Parse<TokenKind>(kindName);
        foreach ((TokenKind k, _) in Lex(sql))
        {
            Assert.Equal(kind, k);
        }
    }

    [Fact]
    public void QuotedNamesAndStringsAreOneToken()
    {
        var t = Lex("\"a\"\"b\" [c d] `e``f` 'it''s' x'0A' select");
        Assert.Equal(
            [TokenKind.Id, TokenKind.Id, TokenKind.Id, TokenKind.String, TokenKind.Blob, TokenKind.Select],
            t.ConvertAll(x => x.Kind));
        Assert.Equal("\"a\"\"b\"", t[0].Text);
        Assert.Equal("'it''s'", t[3].Text);
    }

    [Theory]
    [InlineData("1", "Integer")]
    [InlineData("0x1F", "Integer")]
    [InlineData("1_000", "Integer")]
    [InlineData("1.5e-3", "Float")]
    [InlineData(".5", "Float")]
    [InlineData("5.", "Float")]
    [InlineData("1e", "Illegal")]      // a number that runs into identifier characters is one illegal token
    [InlineData("12abc", "Illegal")]
    [InlineData("0x", "Illegal")]
    [InlineData("'unterminated", "Illegal")]
    [InlineData("x'0g'", "Illegal")]
    public void NumbersAndBadTokens(string sql, string kindName)
    {
        TokenKind kind = Enum.Parse<TokenKind>(kindName);
        var t = Lex(sql);
        Assert.Single(t);
        Assert.Equal(kind, t[0].Kind);
    }

    [Theory]
    [InlineData("?")]
    [InlineData("?12")]
    [InlineData(":name")]
    [InlineData("@name")]
    [InlineData("$name::sub(x)")]
    [InlineData("#name")]
    public void VariablesAreOneToken(string sql)
    {
        var t = Lex(sql);
        Assert.Single(t);
        Assert.Equal(TokenKind.Variable, t[0].Kind);
        Assert.Equal(sql, t[0].Text);
    }

    [Fact]
    public void CommentsAndBlanksAreSkipped()
    {
        var t = Lex("select -- line\n 1 /* block\n */ + 2 /* open");
        Assert.Equal([TokenKind.Select, TokenKind.Integer, TokenKind.Plus, TokenKind.Integer], t.ConvertAll(x => x.Kind));
    }

    [Fact]
    public void OperatorsTakeTheLongestForm()
    {
        var t = Lex("<= <> != == << >> || -> ->> < > = ! ~");
        Assert.Equal(
            [TokenKind.Le, TokenKind.Ne, TokenKind.Ne, TokenKind.Eq, TokenKind.Lshift, TokenKind.Rshift, TokenKind.Concat, TokenKind.Arrow, TokenKind.Arrow2, TokenKind.Lt, TokenKind.Gt, TokenKind.Eq, TokenKind.Illegal, TokenKind.Tilde],
            t.ConvertAll(x => x.Kind));
    }

    [Fact]
    public void NonAsciiLettersAreIdentifierCharacters()
    {
        var t = Lex("naïve_名前 x");
        Assert.Equal(2, t.Count);
        Assert.Equal("naïve_名前", t[0].Text);
    }

    [Fact]
    public void TokenizingAllocatesNothing()
    {
        const string Sql = "select a, \"b c\", 'it''s', x'ff', 1.5e3, ?1 || :n from t where a >= 0x10 -- tail\n and b in (1, 2) /* c */ order by 1 desc";
        for (int i = 0; i < 50; i++)
        {
            Walk(Sql);
        }

        // The runtime may allocate once mid-run (tiered compilation, thread statics); a per-token allocation shows in every
        // repetition, so the smallest of three is the lexer's own cost.
        long best = long.MaxValue;
        for (int rep = 0; rep < 3; rep++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                Walk(Sql);
            }

            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Equal(0, best);
    }

    private static void Walk(string sql)
    {
        int pos = 0;
        for (; ; )
        {
            Token t = Lexer.Next(sql, pos);
            if (t.Kind == TokenKind.Eof)
            {
                return;
            }

            pos = t.End;
        }
    }
}
