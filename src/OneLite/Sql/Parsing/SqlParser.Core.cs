using System.Collections.Immutable;

namespace RecursiveAI.OneLite.Sql.Parsing;

/// <summary>
/// A hand-written recursive-descent parser for SQLite's dialect, built from the syntax diagrams of the lang_* pages and checked
/// against the pinned sqlite3. It reports the same errors at the same token: a grammar error is raised at the first token no rule accepts.
/// </summary>
internal sealed partial class SqlParser
{
    // limits.html, from the pinned build's compile options
    private const int MaxExprDepth = 1000;
    private const int MaxCompoundSelect = 500;
    private const int MaxVariableNumber = 32766;

    private readonly string _sql;
    private readonly ParserOptions _options;
    private TokenWindow _buf; // the next few tokens, inline so a parse allocates no array for them
    private int _count;
    private int _lexPos;
    private TokenKind _lastLexed = TokenKind.Semi;
    private int _prevEnd;
    private string? _pendingMessage;
    private int _pendingPosition;
    private int _autoVariables;
    private int _maxVariable;
    private List<string>? _namedVariables;
    private bool _inTrigger;

    [System.Runtime.CompilerServices.InlineArray(4)]
    private struct TokenWindow
    {
        private Token _first;
    }

    private SqlParser(string sql, int start, ParserOptions options)
    {
        _sql = sql;
        _options = options;
        _lexPos = start;
        _prevEnd = start;
    }

    /// <summary>
    /// Parses the first statement at or after <paramref name="start"/>, as <c>sqlite3_prepare</c> does. Returns null for an empty statement
    /// (blank text, comments or a lone semicolon). <paramref name="end"/> is the index after the statement and its semicolon.
    /// </summary>
    /// <exception cref="OneLiteException">The text is not valid SQL.</exception>
    internal static Statement? ParseOne(string sql, int start, out int end, ParserOptions? options = null)
    {
        // Like sqlite3_prepare, step over empty statements (lone semicolons) to the first real one (c3ref/prepare).
        int pos = start;
        for (; ; )
        {
            var p = new SqlParser(sql, pos, options ?? ParserOptions.Default);
            Statement? s = p.ParseCommandOrEmpty();
            end = p._prevEnd;
            if (s != null || end >= sql.Length || end <= pos)
            {
                return s;
            }

            pos = end;
        }
    }

    /// <summary>Parses every statement of a script.</summary>
    /// <exception cref="OneLiteException">The text is not valid SQL.</exception>
    internal static ImmutableArray<Statement> ParseAll(string sql, ParserOptions? options = null)
    {
        ImmutableArray<Statement>.Builder list = Builders<Statement>.Rent();
        int pos = 0;
        while (true)
        {
            var p = new SqlParser(sql, pos, options ?? ParserOptions.Default);
            bool atEnd = p.Peek(0).Kind == TokenKind.Eof;
            if (atEnd)
            {
                return Builders<Statement>.Finish(list);
            }

            Statement? s = p.ParseCommandOrEmpty();
            if (s != null)
            {
                list.Add(s);
            }

            pos = p._prevEnd;
        }
    }

    // ---- token stream ---------------------------------------------------------------------------------------------

    private Token Peek(int i)
    {
        while (_count <= i)
        {
            Token t = Lexer.Next(_sql, _lexPos);
            if (t.Kind is TokenKind.Filter or TokenKind.Over or TokenKind.Window)
            {
                t = Contextual(t);
            }

            _lexPos = t.End;
            _lastLexed = t.Kind;
            _buf[_count++] = t;
        }

        return _buf[i];
    }

    private TokenKind Kind(int i = 0) => Peek(i).Kind;

    // FILTER, OVER and WINDOW are keywords only where they can start the clause they name; elsewhere they are plain names. Which is
    // decided by the neighbouring tokens (found by black-box probing of the pinned sqlite3):
    //   FILTER: after a closing parenthesis and followed by an opening one.
    //   OVER:   after a closing parenthesis and followed by an opening one or by a name.
    //   WINDOW: followed by a name and AS.
    private Token Contextual(Token t)
    {
        Token n1 = Lexer.Next(_sql, t.End);
        bool keyword;
        switch (t.Kind)
        {
            case TokenKind.Filter:
                keyword = _lastLexed == TokenKind.RParen && n1.Kind == TokenKind.LParen;
                break;
            case TokenKind.Over:
                keyword = _lastLexed == TokenKind.RParen && (n1.Kind == TokenKind.LParen || IsNameAfterKeyword(n1.Kind));
                break;
            default:
                keyword = IsNameAfterKeyword(n1.Kind) && Lexer.Next(_sql, n1.End).Kind == TokenKind.As;
                break;
        }

        return keyword ? t : new Token(TokenKind.Id, t.Start, t.End);
    }

    // the name that may follow OVER or WINDOW: INDEXED, which can name a table, cannot, and neither can FILTER (found by probing every keyword)
    private static bool IsNameAfterKeyword(TokenKind k) => k is not (TokenKind.Indexed or TokenKind.Filter) && IsNameToken(k);

    private static bool IsNameToken(TokenKind k) => k is TokenKind.Id or TokenKind.String or TokenKind.Indexed || Lexer.IsJoinKeyword(k) || Lexer.IsFallback(k);

    private Token Advance()
    {
        Token t = Peek(0);
        if (_pendingMessage != null)
        {
            ThrowPending();
        }

        for (int i = 1; i < _count; i++)
        {
            _buf[i - 1] = _buf[i];
        }

        _count--;
        _prevEnd = t.End;
        return t;
    }

    private bool Accept(TokenKind k)
    {
        if (Peek(0).Kind != k)
        {
            return false;
        }

        Advance();
        return true;
    }

    private Token Expect(TokenKind k)
    {
        if (Peek(0).Kind != k)
        {
            throw Fail();
        }

        return Advance();
    }

    // ---- errors ---------------------------------------------------------------------------------------------------

    /// <summary>The error SQLite raises when the next token cannot be accepted (lang_keywords / the "near" message).</summary>
    private OneLiteException Fail()
    {
        Token t = Peek(0);
        if (t.Kind == TokenKind.Eof)
        {
            return OneLiteException.SqlSyntax("incomplete input", _sql, -1);
        }

        string text = _sql.Substring(t.Start, t.End - t.Start);
        return t.Kind == TokenKind.Illegal
            ? OneLiteException.SqlSyntax($"unrecognized token: \"{text}\"", _sql, t.Start)
            : OneLiteException.SqlSyntax($"near \"{text}\": syntax error", _sql, t.Start);
    }

    // Errors the grammar's actions raise run when a rule is reduced, which happens once the following token is known to be acceptable.
    // So they are held back until the next token is consumed, and dropped if that token is itself a syntax error.
    private void Pending(string message, int position)
    {
        if (_pendingMessage == null)
        {
            _pendingMessage = message;
            _pendingPosition = position;
        }
    }

    private void ThrowPending()
    {
        string m = _pendingMessage!;
        _pendingMessage = null;
        throw OneLiteException.SqlSyntax(m, _sql, _pendingPosition);
    }

    // ---- statements -----------------------------------------------------------------------------------------------

    private Statement? ParseCommandOrEmpty()
    {
        if (Peek(0).Kind == TokenKind.Semi)
        {
            Advance();
            return null;
        }

        if (Peek(0).Kind == TokenKind.Eof)
        {
            _prevEnd = Peek(0).Start; // a NUL byte ends the text early, as the library reads it
            return null;
        }

        Statement s = ParseCommand(allowExplain: true);
        Terminate();
        return s;
    }

    private void Terminate()
    {
        if (Peek(0).Kind == TokenKind.Semi)
        {
            Advance();
        }
        else if (Peek(0).Kind == TokenKind.Eof)
        {
            if (_pendingMessage != null)
            {
                ThrowPending();
            }

            _prevEnd = Peek(0).Start; // a NUL byte ends the text early, as the library reads it
        }
        else
        {
            throw Fail();
        }
    }

    private T Done<T>(T node, int start)
        where T : SqlNode
    {
        node.Span = new SourceSpan(start, _prevEnd);
        return node;
    }

    private Statement ParseCommand(bool allowExplain)
    {
        int start = Peek(0).Start;
        switch (Kind())
        {
            case TokenKind.Explain when allowExplain:
            {
                Advance();
                bool plan = false;
                if (Accept(TokenKind.Query))
                {
                    Expect(TokenKind.Plan);
                    plan = true;
                }

                Statement inner = ParseCommand(allowExplain: false);
                return Done(new ExplainStatement(plan, inner), start);
            }

            case TokenKind.Select:
            case TokenKind.Values:
                return ParseSelectStatement(null);
            case TokenKind.With:
            {
                WithClause with = ParseWith();
                switch (Kind())
                {
                    case TokenKind.Select:
                    case TokenKind.Values:
                        return ParseSelectStatement(with);
                    case TokenKind.Insert:
                    case TokenKind.Replace:
                        return ParseInsert(with, start);
                    case TokenKind.Update:
                        return ParseUpdate(with, start);
                    case TokenKind.Delete:
                        return ParseDelete(with, start);
                    default:
                        throw Fail();
                }
            }

            case TokenKind.Insert:
            case TokenKind.Replace:
                return ParseInsert(null, start);
            case TokenKind.Update:
                return ParseUpdate(null, start);
            case TokenKind.Delete:
                return ParseDelete(null, start);
            case TokenKind.Create:
                return ParseCreate(start);
            case TokenKind.Drop:
                return ParseDrop(start);
            case TokenKind.Alter:
                return ParseAlter(start);
            case TokenKind.Attach:
                return ParseAttach(start);
            case TokenKind.Detach:
                Advance();
                Accept(TokenKind.Database);
                return Done(new DetachStatement(ParseExpr()), start);
            case TokenKind.Begin:
                return ParseBegin(start);
            case TokenKind.Commit:
            case TokenKind.End:
            {
                Advance();
                return Done(new CommitStatement(ParseTransactionName()), start);
            }

            case TokenKind.Rollback:
            {
                Advance();
                Name? txn = ParseTransactionName();
                Name? to = null;
                if (Accept(TokenKind.To))
                {
                    Accept(TokenKind.Savepoint);
                    to = ParseNm();
                }

                return Done(new RollbackStatement(txn, to), start);
            }

            case TokenKind.Savepoint:
                Advance();
                return Done(new SavepointStatement(ParseNm()), start);
            case TokenKind.Release:
                Advance();
                Accept(TokenKind.Savepoint);
                return Done(new ReleaseStatement(ParseNm()), start);
            case TokenKind.Pragma:
                return ParsePragma(start);
            case TokenKind.Vacuum:
            {
                Advance();
                Name? schema = IsNmStart(Kind()) ? ParseNm() : null;
                Expr? into = Accept(TokenKind.Into) ? ParseExpr() : null;
                return Done(new VacuumStatement(schema, into), start);
            }

            case TokenKind.Reindex:
                Advance();
                return Done(new ReindexStatement(IsNmStart(Kind()) ? ParseQualifiedName() : null), start);
            case TokenKind.Analyze:
                Advance();
                return Done(new AnalyzeStatement(IsNmStart(Kind()) ? ParseQualifiedName() : null), start);
            default:
                throw Fail();
        }
    }

    private Name? ParseTransactionName()
    {
        // trans_opt: nothing, TRANSACTION, or TRANSACTION name
        if (!Accept(TokenKind.Transaction))
        {
            return null;
        }

        return IsNmStart(Kind()) ? ParseNm() : null;
    }

    private BeginStatement ParseBegin(int start)
    {
        Advance();
        BeginMode mode = BeginMode.Default;
        switch (Kind())
        {
            case TokenKind.Deferred:
                Advance();
                mode = BeginMode.Deferred;
                break;
            case TokenKind.Immediate:
                Advance();
                mode = BeginMode.Immediate;
                break;
            case TokenKind.Exclusive:
                Advance();
                mode = BeginMode.Exclusive;
                break;
            case TokenKind.Id when _options.BeginConcurrent && IsWord(Peek(0), "CONCURRENT"):
                Advance();
                mode = BeginMode.Concurrent;
                break;
        }

        return Done(new BeginStatement(mode, ParseTransactionName()), start);
    }

    private bool IsWord(Token t, string upper)
        => t.End - t.Start == upper.Length && _sql.AsSpan(t.Start, t.End - t.Start).Equals(upper, StringComparison.OrdinalIgnoreCase);

    private AttachStatement ParseAttach(int start)
    {
        Advance();
        Accept(TokenKind.Database);
        Expr db = ParseExpr();
        Expect(TokenKind.As);
        Expr name = ParseExpr();
        Expr? key = Accept(TokenKind.Key) ? ParseExpr() : null;
        return Done(new AttachStatement(db, name, key), start);
    }

    private PragmaStatement ParsePragma(int start)
    {
        Advance();
        Name first = ParseNm();
        Name? schema = null;
        Name name = first;
        if (Accept(TokenKind.Dot))
        {
            schema = first;
            name = ParseNm();
        }

        PragmaValue? value = null;
        bool paren = false;
        if (Accept(TokenKind.Eq))
        {
            value = ParsePragmaValue();
        }
        else if (Accept(TokenKind.LParen))
        {
            paren = true;
            value = ParsePragmaValue();
            Expect(TokenKind.RParen);
        }

        return Done(new PragmaStatement(schema, name, value, paren), start);
    }

    // A number written with digit separators (1_000) is a different token class in the grammar, which pragma values do not accept.
    private bool HasSeparator(Token t) => _sql.AsSpan(t.Start, t.End - t.Start).Contains('_');

    // pragma values: a signed number, a name, a string, or the words ON, DELETE and DEFAULT
    private PragmaValue ParsePragmaValue()
    {
        int start = Peek(0).Start;
        switch (Kind())
        {
            case TokenKind.Plus:
            case TokenKind.Minus:
            {
                Token sign = Advance();
                Token num = Peek(0);
                if (num.Kind is not (TokenKind.Integer or TokenKind.Float) || HasSeparator(num))
                {
                    throw Fail();
                }

                Advance();
                return Done(new PragmaValue(PragmaValueKind.Number, _sql.Substring(sign.Start, num.End - sign.Start)), start);
            }

            case TokenKind.Integer:
            case TokenKind.Float:
            {
                if (HasSeparator(Peek(0)))
                {
                    throw Fail();
                }

                Token num = Advance();
                return Done(new PragmaValue(PragmaValueKind.Number, _sql.Substring(num.Start, num.End - num.Start)), start);
            }

            case TokenKind.On:
            case TokenKind.Delete:
            case TokenKind.Default:
            {
                Token w = Advance();
                return Done(new PragmaValue(PragmaValueKind.Keyword, _sql.Substring(w.Start, w.End - w.Start)), start);
            }

            case TokenKind.String:
            {
                Name n = ParseNm();
                return Done(new PragmaValue(PragmaValueKind.String, n.Text), start);
            }

            default:
            {
                Name n = ParseNm();
                return Done(new PragmaValue(PragmaValueKind.Word, n.Text), start);
            }
        }
    }

    // ---- names ----------------------------------------------------------------------------------------------------

    // nm: an identifier, a string, a join word (CROSS, LEFT ..), INDEXED, or a keyword that may serve as a name
    private static bool IsNmStart(TokenKind k) => IsNameToken(k) && !IsContextual(k);

    // FILTER, OVER and WINDOW reach the grammar only when they are keywords (see Contextual); they never stand for a name there
    private static bool IsContextual(TokenKind k) => k is TokenKind.Filter or TokenKind.Over or TokenKind.Window;

    // ids (aliases, type words, collation names): like nm, but without the join words and INDEXED
    private static bool IsIdsStart(TokenKind k) => (k is TokenKind.Id or TokenKind.String || Lexer.IsFallback(k)) && !IsContextual(k);

    // id (function and column names): an identifier, INDEXED, or a fallback keyword
    private static bool IsIdStart(TokenKind k) => (k is TokenKind.Id or TokenKind.Indexed || Lexer.IsFallback(k)) && !IsContextual(k);

    private Name ParseNm()
    {
        if (!IsNmStart(Kind()))
        {
            throw Fail();
        }

        return NameOf(Advance());
    }

    private Name ParseIds()
    {
        if (!IsIdsStart(Kind()))
        {
            throw Fail();
        }

        return NameOf(Advance());
    }

    private Name NameOf(Token t) => new Name(Unquote(t)) { Span = new SourceSpan(t.Start, t.End) };

    private string Unquote(Token t)
    {
        ReadOnlySpan<char> raw = _sql.AsSpan(t.Start, t.End - t.Start);
        char q;
        if (t.Kind == TokenKind.String)
        {
            q = '\'';
        }
        else if ((t.Flags & TokenFlags.DoubleQuoted) != 0)
        {
            q = '"';
        }
        else if ((t.Flags & TokenFlags.Backquoted) != 0)
        {
            q = '`';
        }
        else if ((t.Flags & TokenFlags.Bracketed) != 0)
        {
            return raw[1..^1].ToString();
        }
        else
        {
            return raw.ToString();
        }

        ReadOnlySpan<char> inner = raw[1..^1];
        int doubled = inner.IndexOf(q);
        return doubled < 0 ? inner.ToString() : inner.ToString().Replace(new string(q, 2), new string(q, 1), StringComparison.Ordinal);
    }

    private QualifiedName ParseQualifiedName()
    {
        int start = Peek(0).Start;
        Name first = ParseNm();
        if (Accept(TokenKind.Dot))
        {
            Name second = ParseNm();
            return Done(new QualifiedName(first, second), start);
        }

        return Done(new QualifiedName(null, first), start);
    }
}
