using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneLite.Sql.Parsing;

internal sealed partial class SqlParser
{
    // The parser stack of the reference grammar overflows at about this many entries ("Recursion limit"); nesting is weighed by how many
    // entries a construct keeps on that stack.
    private const int StackLimit = 2495;

    // Measured against the reference: every subquery adds this much to the height of the expressions around it (limits: SQLITE_MAX_EXPR_DEPTH).
    private const int SubqueryHeight = 23;

    // the token kinds of the words of the type name parsed last: only bare GENERATED and ALWAYS words can belong to a generated column
    private readonly List<TokenKind> _typeWordKinds = [];

    private int _stack;
    private int _height;
    private int _selectHeight;

    private void Enter(int weight)
    {
        _stack += weight;
        if (_stack > StackLimit || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw OneLiteException.SqlSyntax("Recursion limit", _sql, -1);
        }
    }

    private void Leave(int weight) => _stack -= weight;

    // the real call stack, which must not overflow however deep the input nests
    private void GuardNativeStack()
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw OneLiteException.SqlSyntax("Recursion limit", _sql, -1);
        }
    }

    // lang_expr / limits.html SQLITE_MAX_EXPR_DEPTH: the height of an expression tree counts every operator and operand node.
    private void NoteHeight(int h)
    {
        _height = h;
        if (h > _selectHeight)
        {
            _selectHeight = h;
        }

        if (h > MaxExprDepth)
        {
            Pending($"Expression tree is too large (maximum depth {MaxExprDepth})", -1);
        }
    }

    // ---- precedence (lang_expr, "Operators, and Parse-Affecting Attributes") ------------------------------------------
    // 1 OR; 2 AND; 3 NOT (prefix); 4 = == <> != IS BETWEEN IN MATCH LIKE REGEXP GLOB ISNULL NOTNULL NOT NULL; 5 < > <= >=;
    // 6 ESCAPE; 7 & | << >>; 8 + -; 9 * / %; 10 || -> ->>; 11 COLLATE; 12 ~ + - (prefix)
    private const int PrecNot = 3;
    private const int PrecEquality = 4;
    private const int PrecComparison = 5;
    private const int PrecUnary = 12;

    private static int InfixPrecedence(TokenKind k) => k switch
    {
        TokenKind.Or => 1,
        TokenKind.And => 2,
        TokenKind.Eq or TokenKind.Ne or TokenKind.Is or TokenKind.Like or TokenKind.Glob or TokenKind.Regexp or TokenKind.Match
            or TokenKind.Between or TokenKind.In or TokenKind.Isnull or TokenKind.Notnull or TokenKind.Not => PrecEquality,
        TokenKind.Lt or TokenKind.Le or TokenKind.Gt or TokenKind.Ge => PrecComparison,
        TokenKind.Amp or TokenKind.Bar or TokenKind.Lshift or TokenKind.Rshift => 7,
        TokenKind.Plus or TokenKind.Minus => 8,
        TokenKind.Star or TokenKind.Slash or TokenKind.Percent => 9,
        TokenKind.Concat or TokenKind.Arrow or TokenKind.Arrow2 => 10,
        TokenKind.Collate => 11,
        _ => -1,
    };

    private static BinaryOp BinaryOpOf(TokenKind k) => k switch
    {
        TokenKind.Or => BinaryOp.Or,
        TokenKind.And => BinaryOp.And,
        TokenKind.Eq => BinaryOp.Eq,
        TokenKind.Ne => BinaryOp.Ne,
        TokenKind.Lt => BinaryOp.Lt,
        TokenKind.Le => BinaryOp.Le,
        TokenKind.Gt => BinaryOp.Gt,
        TokenKind.Ge => BinaryOp.Ge,
        TokenKind.Amp => BinaryOp.BitAnd,
        TokenKind.Bar => BinaryOp.BitOr,
        TokenKind.Lshift => BinaryOp.Lshift,
        TokenKind.Rshift => BinaryOp.Rshift,
        TokenKind.Plus => BinaryOp.Add,
        TokenKind.Minus => BinaryOp.Sub,
        TokenKind.Star => BinaryOp.Mul,
        TokenKind.Slash => BinaryOp.Div,
        TokenKind.Percent => BinaryOp.Rem,
        TokenKind.Concat => BinaryOp.Concat,
        TokenKind.Arrow => BinaryOp.Arrow,
        _ => BinaryOp.Arrow2,
    };

    // stopAtAnd: this is the lower bound of BETWEEN, where an AND on the outermost level belongs to the BETWEEN. Operands of operators
    // inside it still take AND as an operator, so `x BETWEEN a OR b AND c` reads `a OR (b AND c)` and then lacks its AND (lang_expr).
    private Expr ParseExpr(int minPrec = 0, bool stopAtAnd = false)
    {
        GuardNativeStack();
        int start = Peek(0).Start;
        Expr left = ParsePrefix();
        for (; ; )
        {
            TokenKind k = Kind();
            int prec = InfixPrecedence(k);
            if (prec < 0 || prec < minPrec || stopAtAnd && k == TokenKind.And)
            {
                break;
            }

            int hl = _height;
            switch (k)
            {
                case TokenKind.Collate:
                {
                    Advance();
                    Name coll = ParseIds();
                    left = Done(new CollateExpr(left, coll), start);
                    break;
                }

                case TokenKind.Isnull:
                    Advance();
                    left = Done(new NullTestExpr(left, NullTest.IsNull), start);
                    break;
                case TokenKind.Notnull:
                    Advance();
                    left = Done(new NullTestExpr(left, NullTest.NotNull), start);
                    break;
                case TokenKind.Not:
                    Advance();
                    left = ParseNegatedPostfix(left, start, hl);
                    break;
                case TokenKind.Between:
                case TokenKind.In:
                case TokenKind.Like:
                case TokenKind.Glob:
                case TokenKind.Regexp:
                case TokenKind.Match:
                    left = ParseKeywordOperator(left, start, hl, negated: false);
                    break;
                case TokenKind.Is:
                    left = ParseIs(left, start, hl);
                    break;
                default:
                {
                    Advance();
                    Enter(2); // the left operand and the operator wait on the parser stack
                    Expr right = ParseExpr(prec + 1);
                    Leave(2);
                    left = Done(new BinaryExpr(BinaryOpOf(k), left, right), start);
                    NoteHeight(Math.Max(hl, _height) + 1);
                    break;
                }
            }
        }

        return left;
    }

    // NOT already consumed: NULL (postfix NOT NULL), or the negated BETWEEN, IN, LIKE, GLOB, REGEXP, MATCH
    private Expr ParseNegatedPostfix(Expr left, int start, int hl)
    {
        if (Kind() == TokenKind.Null)
        {
            Advance();
            return Done(new NullTestExpr(left, NullTest.NotNull), start);
        }

        if (Kind() is TokenKind.Between or TokenKind.In or TokenKind.Like or TokenKind.Glob or TokenKind.Regexp or TokenKind.Match)
        {
            return ParseKeywordOperator(left, start, hl, negated: true);
        }

        throw Fail();
    }

    private Expr ParseKeywordOperator(Expr left, int start, int hl, bool negated)
    {
        TokenKind k = Advance().Kind;
        switch (k)
        {
            case TokenKind.Between:
            {
                Enter(2);
                Expr low = ParseExpr(0, stopAtAnd: true);
                Leave(2);
                int h1 = _height;
                Expect(TokenKind.And);
                Enter(4);
                Expr high = ParseExpr(PrecComparison);
                Leave(4);
                Expr e = Done(new BetweenExpr(left, low, high, negated), start);
                NoteHeight(Math.Max(hl, Math.Max(h1, _height)) + 1);
                return e;
            }

            case TokenKind.In:
                return ParseIn(left, start, hl, negated);
            default:
            {
                LikeOp op = k switch { TokenKind.Like => LikeOp.Like, TokenKind.Glob => LikeOp.Glob, TokenKind.Regexp => LikeOp.Regexp, _ => LikeOp.Match };
                Enter(2);
                Expr pattern = ParseExpr(PrecComparison);
                Leave(2);
                int h = Math.Max(hl, _height);
                Expr? escape = null;
                if (Accept(TokenKind.Escape))
                {
                    escape = ParseExpr(PrecComparison); // measured: the operand takes comparison operators too, although lang_expr lists ESCAPE above them
                    h = Math.Max(h, _height);
                }

                Expr e = Done(new LikeExpr(left, op, pattern, escape, negated), start);
                NoteHeight(h + 1);
                return e;
            }
        }
    }

    private Expr ParseIs(Expr left, int start, int hl)
    {
        Advance(); // IS
        bool not = Accept(TokenKind.Not);
        bool distinct = false;
        if (Kind() == TokenKind.Distinct)
        {
            Advance();
            Expect(TokenKind.From);
            distinct = true;
        }

        // Observed: when an operator that binds tighter than IS follows (+ - * / % || -> << & | < >), TRUE is the start of the right operand,
        // so `x IS TRUE + 0` is `x IS (TRUE + 0)`; after =, IS, AND, BETWEEN, IN, LIKE, COLLATE or a NULL test it is a truth test.
        if (!distinct && (IsWord(Peek(0), "TRUE") || IsWord(Peek(0), "FALSE")) && Kind(1) is not (TokenKind.Dot or TokenKind.LParen)
            && !(InfixPrecedence(Kind(1)) is > PrecEquality and not 11))
        {
            bool wantTrue = IsWord(Peek(0), "TRUE");
            Advance();
            NoteHeight(hl + 2);
            return TruthTest(left, not, wantTrue, start);
        }

        Enter(2);
        Expr right = ParseExpr(PrecComparison);
        Leave(2);

        // Observed: the reference gives an empty IN list the value of TRUE or FALSE for this purpose, so x IS (y NOT IN ()) is x IS TRUE.
        Expr unwrapped = right;
        while (unwrapped is ParenExpr pe)
        {
            unwrapped = pe.Inner;
        }

        // The empty list stands in for a constant only when its operand makes no call (a function, LIKE): abs(1) IN () stays an expression.
        if (unwrapped is InListExpr { Items.IsDefaultOrEmpty: true } empty && !RecursiveAI.OneLite.Planning.Compiler.HasCall(empty.Operand))
        {
            NoteHeight(Math.Max(hl, _height) + 2);
            return TruthTest(left, not ^ distinct, empty.Negated, start);
        }

        // IS DISTINCT FROM is IS NOT, and IS NOT DISTINCT FROM is IS (lang_expr)
        BinaryOp op = not ^ distinct ? BinaryOp.IsNot : BinaryOp.Is;
        Expr e = Done(new BinaryExpr(op, left, right), start);
        NoteHeight(Math.Max(hl, _height) + 1);
        return e;
    }

    // lang_expr: x IS [NOT] TRUE|FALSE tests the truth value of x and is never NULL. It is written as (x AND 1) IS [NOT] 1|0, because
    // AND maps every non-NULL x to 1 or 0 by its numeric value and leaves NULL as NULL, which IS then separates.
    private BinaryExpr TruthTest(Expr left, bool not, bool wantTrue, int start)
    {
        Expr anded = Done(new BinaryExpr(BinaryOp.And, left, Done(new LiteralExpr(LiteralKind.Integer, "1"), _prevEnd - 1)), start);
        return Done(new BinaryExpr(not ? BinaryOp.IsNot : BinaryOp.Is, anded, Done(new LiteralExpr(LiteralKind.Integer, wantTrue ? "1" : "0"), _prevEnd - 1)), start);
    }

    private Expr ParseIn(Expr left, int start, int hl, bool negated)
    {
        if (Kind() == TokenKind.LParen)
        {
            if (Kind(1) is TokenKind.Select or TokenKind.Values or TokenKind.With)
            {
                Advance();
                SelectStatement sel = ParseSelectBody();
                Expect(TokenKind.RParen);
                int hs = _height;
                Expr e = Done(new InSelectExpr(left, sel, negated), start);
                NoteHeight(Math.Max(hl, hs + SubqueryHeight - 1) + 1);
                return e;
            }

            Advance();
            ImmutableArray<Expr> items = ParseExprListUntilParen(out int hi);
            Expr list = Done(new InListExpr(left, items, negated), start);
            NoteHeight(Math.Max(hl, hi + 1) + 1); // the list is a node of its own
            return list;
        }

        QualifiedName table = ParseQualifiedName();
        if (Accept(TokenKind.LParen))
        {
            ImmutableArray<Expr> args = ParseExprListUntilParen(out int ha);
            Expr fn = Done(new InTableExpr(left, table, args, true, negated), start);
            NoteHeight(Math.Max(hl, ha) + 1);
            return fn;
        }

        Expr plain = Done(new InTableExpr(left, table, default, false, negated), start);
        NoteHeight(hl + 1);
        return plain;
    }

    // after "(": zero or more comma-separated expressions and the closing parenthesis; maxHeight is the tallest item
    private ImmutableArray<Expr> ParseExprListUntilParen(out int maxHeight)
    {
        maxHeight = 0;
        if (Accept(TokenKind.RParen))
        {
            return ImmutableArray<Expr>.Empty;
        }

        ImmutableArray<Expr>.Builder b = Builders<Expr>.Rent();
        Enter(3); // the expression, IN and the parenthesis
        do
        {
            b.Add(ParseListItem(b.Count));
            maxHeight = Math.Max(maxHeight, _height);
        }
        while (Accept(TokenKind.Comma));
        Leave(3);
        Expect(TokenKind.RParen);
        return Builders<Expr>.Finish(b);
    }

    // an item after the first also has the list so far and its comma on the parser stack
    private Expr ParseListItem(int index)
    {
        if (index == 0)
        {
            return ParseExpr();
        }

        Enter(2);
        Expr e = ParseExpr();
        Leave(2);
        return e;
    }

    private ImmutableArray<Expr> ParseExprList(out int maxHeight)
    {
        maxHeight = 0;
        ImmutableArray<Expr>.Builder b = Builders<Expr>.Rent();
        do
        {
            b.Add(ParseListItem(b.Count));
            maxHeight = Math.Max(maxHeight, _height);
        }
        while (Accept(TokenKind.Comma));
        return Builders<Expr>.Finish(b);
    }

    private Expr ParsePrefix()
    {
        int start = Peek(0).Start;
        switch (Kind())
        {
            case TokenKind.Minus:
            case TokenKind.Plus:
            case TokenKind.Tilde:
            {
                TokenKind k = Advance().Kind;
                Enter(1);
                Expr operand = ParseExpr(PrecUnary);
                Leave(1);
                UnaryOp op = k == TokenKind.Minus ? UnaryOp.Minus : k == TokenKind.Plus ? UnaryOp.Plus : UnaryOp.BitNot;
                Expr e = Done(new UnaryExpr(op, operand), start);
                NoteHeight(_height + 1);
                return e;
            }

            case TokenKind.Not:
            {
                Advance();
                Enter(1);
                Expr operand = ParseExpr(PrecEquality);
                Leave(1);
                Expr e = Done(new UnaryExpr(UnaryOp.Not, operand), start);
                NoteHeight(_height + 1);
                return e;
            }

            default:
                return ParsePrimary();
        }
    }

    private Expr Literal(LiteralKind kind, string text, Token t)
    {
        Expr e = Done(new LiteralExpr(kind, text), t.Start);
        NoteHeight(1);
        return e;
    }

    private Expr ParsePrimary()
    {
        Token t = Peek(0);
        int start = t.Start;
        switch (t.Kind)
        {
            case TokenKind.Integer:
            case TokenKind.Float:
                Advance();
                if ((t.Flags & TokenFlags.BadNumber) != 0)
                {
                    Pending($"unrecognized token: \"{_sql.AsSpan(t.Start, t.End - t.Start)}\"", -1);
                }

                return Literal(t.Kind == TokenKind.Integer ? LiteralKind.Integer : LiteralKind.Float, _sql.Substring(t.Start, t.End - t.Start), t);
            case TokenKind.String:
                if (Kind(1) == TokenKind.Dot)
                {
                    return ParseNameExpression();
                }

                Advance();
                return Literal(LiteralKind.String, Unquote(t), t);
            case TokenKind.Blob:
                Advance();
                return Literal(LiteralKind.Blob, _sql.Substring(t.Start + 2, t.End - t.Start - 3), t);
            case TokenKind.Null:
                Advance();
                return Literal(LiteralKind.Null, "NULL", t);
            case TokenKind.CurrentDate:
                Advance();
                return Literal(LiteralKind.CurrentDate, "CURRENT_DATE", t);
            case TokenKind.CurrentTime:
                Advance();
                return Literal(LiteralKind.CurrentTime, "CURRENT_TIME", t);
            case TokenKind.CurrentTimestamp:
                Advance();
                return Literal(LiteralKind.CurrentTimestamp, "CURRENT_TIMESTAMP", t);
            case TokenKind.Variable:
                return ParseVariable(t);
            case TokenKind.LParen:
                return ParseParenthesized(start);
            case TokenKind.Case:
                return ParseCase(start);
            case TokenKind.Cast:
                return ParseCast(start);
            case TokenKind.Raise:
                return ParseRaise(start);
            case TokenKind.Exists:
            {
                Advance();
                Expect(TokenKind.LParen);
                SelectStatement sel = ParseSelectBody();
                Expect(TokenKind.RParen);
                Expr e = Done(new ExistsExpr(sel), start);
                NoteHeight(_height + SubqueryHeight);
                return e;
            }

            default:
                if (IsNmStart(t.Kind))
                {
                    return ParseNameExpression();
                }

                throw Fail();
        }
    }

    // name, name.name, name.name.name, or a function call
    private Expr ParseNameExpression()
    {
        int start = Peek(0).Start;
        TokenKind k = Kind();
        if (Kind(1) == TokenKind.LParen && (IsIdStart(k) || Lexer.IsJoinKeyword(k)))
        {
            return ParseFunctionCall(start);
        }

        Name first = ParseNm();
        if (!Accept(TokenKind.Dot))
        {
            Expr e = Done(new ColumnRef(null, null, first), start);
            NoteHeight(1);
            return e;
        }

        Name second = ParseNm();
        if (!Accept(TokenKind.Dot))
        {
            Expr e = Done(new ColumnRef(null, first, second), start);
            NoteHeight(1);
            return e;
        }

        Name third = ParseNm();
        Expr e3 = Done(new ColumnRef(first, second, third), start);
        NoteHeight(1);
        return e3;
    }

    private Expr ParseVariable(Token t)
    {
        Advance();
        string text = _sql.Substring(t.Start, t.End - t.Start);
        if (text[0] == '#' && text.Length > 1 && text[1] is >= '0' and <= '9')
        {
            // #NNN is a register reference the library uses internally; user SQL may not contain one
            Pending($"near \"{text}\": syntax error", t.Start);
        }
        else if (text[0] == '?')
        {
            if (text.Length > 1)
            {
                // ?NNN: between 1 and the variable limit (limits.html SQLITE_MAX_VARIABLE_NUMBER)
                bool ok = text.Length <= 6 && int.TryParse(text.AsSpan(1), out int n) && n >= 1 && n <= MaxVariableNumber;
                if (!ok)
                {
                    Pending($"variable number must be between ?1 and ?{MaxVariableNumber}", t.Start);
                }
                else if (int.Parse(text.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture) > _maxVariable)
                {
                    _maxVariable = int.Parse(text.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            else
            {
                _autoVariables = Math.Max(_autoVariables, _maxVariable) + 1;
                _maxVariable = _autoVariables;
                if (_maxVariable > MaxVariableNumber)
                {
                    Pending("too many SQL variables", t.Start);
                }
            }
        }
        else
        {
            // a name used twice is one variable
            _namedVariables ??= [];
            if (!_namedVariables.Contains(text))
            {
                _namedVariables.Add(text);
                _maxVariable++;
                if (_maxVariable > MaxVariableNumber)
                {
                    Pending("too many SQL variables", t.Start);
                }
            }
        }

        Expr e = Done(new VariableExpr(text), t.Start);
        NoteHeight(1);
        return e;
    }

    private Expr ParseParenthesized(int start)
    {
        Advance(); // (
        Enter(1);
        try
        {
            if (Kind() is TokenKind.Select or TokenKind.Values or TokenKind.With)
            {
                SelectStatement sel = ParseSelectBody();
                Expect(TokenKind.RParen);
                Expr sub = Done(new SubqueryExpr(sel), start);
                NoteHeight(_height + SubqueryHeight);
                return sub;
            }

            Expr first = ParseExpr();
            int h = _height;
            if (Kind() == TokenKind.Comma)
            {
                ImmutableArray<Expr>.Builder items = Builders<Expr>.Rent();
                items.Add(first);
                while (Accept(TokenKind.Comma))
                {
                    items.Add(ParseListItem(items.Count));
                    h = Math.Max(h, _height);
                }

                Expect(TokenKind.RParen);
                Expr row = Done(new RowValueExpr(Builders<Expr>.Finish(items)), start);
                NoteHeight(h + 1);
                return row;
            }

            Expect(TokenKind.RParen);
            Expr paren = Done(new ParenExpr(first), start);
            _height = h; // parentheses add no node
            return paren;
        }
        finally
        {
            Leave(1);
        }
    }

    private Expr ParseCase(int start)
    {
        Advance();
        Expr? operand = null;
        int h = 0;
        if (Kind() != TokenKind.When)
        {
            Enter(1);
            operand = ParseExpr();
            Leave(1);
            h = _height;
        }

        ImmutableArray<WhenClause>.Builder whens = Builders<WhenClause>.Rent();
        do
        {
            int ws = Peek(0).Start;
            Expect(TokenKind.When);
            Enter(3); // CASE, its operand and WHEN
            Expr w = ParseExpr();
            Leave(3);
            h = Math.Max(h, _height);
            Expect(TokenKind.Then);
            Enter(5);
            Expr t = ParseExpr();
            Leave(5);
            h = Math.Max(h, _height);
            whens.Add(Done(new WhenClause(w, t), ws));
        }
        while (Kind() == TokenKind.When);
        Expr? els = null;
        if (Accept(TokenKind.Else))
        {
            Enter(4);
            els = ParseExpr();
            Leave(4);
            h = Math.Max(h, _height);
        }

        Expect(TokenKind.End);
        Expr e = Done(new CaseExpr(operand, Builders<WhenClause>.Finish(whens), els), start);
        NoteHeight(h + 1);
        return e;
    }

    private Expr ParseCast(int start)
    {
        Advance();
        Expect(TokenKind.LParen);
        Expr operand = ParseExpr();
        int h = _height;
        Expect(TokenKind.As);
        TypeName type = ParseTypeName();
        Expect(TokenKind.RParen);
        Expr e = Done(new CastExpr(operand, type), start);
        NoteHeight(h + 1);
        return e;
    }

    private Expr ParseRaise(int start)
    {
        Advance();
        Expect(TokenKind.LParen);
        RaiseKind kind;
        Expr? message = null;
        int h = 0;
        if (Accept(TokenKind.Ignore))
        {
            kind = RaiseKind.Ignore;
        }
        else
        {
            kind = Kind() switch
            {
                TokenKind.Rollback => RaiseKind.Rollback,
                TokenKind.Abort => RaiseKind.Abort,
                TokenKind.Fail => RaiseKind.Fail,
                _ => throw Fail(),
            };
            Advance();
            Expect(TokenKind.Comma);
            message = ParseExpr();
            h = _height;
        }

        Expect(TokenKind.RParen);
        Expr e = Done(new RaiseExpr(kind, message), start);
        NoteHeight(h + 1);
        return e;
    }

    // type words (names or strings) with an optional (n) or (n,m) size; empty is allowed where the grammar allows it
    private TypeName ParseTypeName()
    {
        int start = Peek(0).Start;
        ImmutableArray<string>.Builder words = Builders<string>.Rent();
        _typeWordKinds.Clear();
        while (IsIdsStart(Kind()))
        {
            Token w = Advance();
            _typeWordKinds.Add(w.Kind);
            words.Add(Unquote(w));
        }

        ImmutableArray<string> sizes = ImmutableArray<string>.Empty;
        if (words.Count > 0 && Kind() == TokenKind.LParen)
        {
            Advance();
            ImmutableArray<string>.Builder b = Builders<string>.Rent();
            b.Add(ParseSignedNumber());
            if (Accept(TokenKind.Comma))
            {
                b.Add(ParseSignedNumber());
            }

            Expect(TokenKind.RParen);
            sizes = Builders<string>.Finish(b);
        }

        return Done(new TypeName(Builders<string>.Finish(words), sizes), start);
    }

    private string ParseSignedNumber()
    {
        int start = Peek(0).Start;
        if (Kind() is TokenKind.Plus or TokenKind.Minus)
        {
            Advance();
        }

        if (Kind() is not (TokenKind.Integer or TokenKind.Float))
        {
            throw Fail();
        }

        Token n = Advance();
        return _sql.Substring(start, n.End - start);
    }

    // name(args) with the aggregate and window parts
    private Expr ParseFunctionCall(int start)
    {
        Name name = NameOf(Advance());
        Advance(); // (
        Enter(3);
        try
        {
            CallQuantifier quant = CallQuantifier.None;
            bool star = false;
            ImmutableArray<Expr> args = ImmutableArray<Expr>.Empty;
            ImmutableArray<OrderingTerm> orderBy = ImmutableArray<OrderingTerm>.Empty;
            int h = 0;
            if (Accept(TokenKind.Star))
            {
                star = true;
                Expect(TokenKind.RParen);
            }
            else if (!Accept(TokenKind.RParen))
            {
                if (Accept(TokenKind.Distinct))
                {
                    quant = CallQuantifier.Distinct;
                }
                else if (Accept(TokenKind.All))
                {
                    quant = CallQuantifier.All;
                }

                // lang_expr: the argument list may be empty after DISTINCT or ALL and before ORDER BY
                if (Kind() is not (TokenKind.RParen or TokenKind.Order))
                {
                    args = ParseExprList(out h);
                }

                if (Kind() == TokenKind.Order)
                {
                    Advance();
                    Expect(TokenKind.By);
                    orderBy = ParseOrderingTerms();
                }

                Expect(TokenKind.RParen);
            }

            Expr? filter = null;
            if (Kind() == TokenKind.Filter)
            {
                Advance();
                Expect(TokenKind.LParen);
                Expect(TokenKind.Where);
                Enter(5);
                filter = ParseExpr();
                Leave(5);
                Expect(TokenKind.RParen);
            }

            WindowReference? over = null;
            if (Kind() == TokenKind.Over)
            {
                int os = Peek(0).Start;
                Advance();
                if (Kind() == TokenKind.LParen)
                {
                    over = Done(new WindowReference(null, ParseWindowSpec()), os);
                }
                else
                {
                    over = Done(new WindowReference(ParseNm(), null), os);
                }
            }

            Expr e = Done(new FunctionCall(name, quant, star, args, orderBy, filter, over), start);
            NoteHeight(h + 1);
            return e;
        }
        finally
        {
            Leave(3);
        }
    }

    private ImmutableArray<OrderingTerm> ParseOrderingTerms()
    {
        ImmutableArray<OrderingTerm>.Builder b = Builders<OrderingTerm>.Rent();
        do
        {
            int start = Peek(0).Start;
            Expr e = ParseExpr();
            SortOrder order = SortOrder.None;
            if (Accept(TokenKind.Asc))
            {
                order = SortOrder.Asc;
            }
            else if (Accept(TokenKind.Desc))
            {
                order = SortOrder.Desc;
            }

            NullsOrder nulls = NullsOrder.None;
            if (Accept(TokenKind.Nulls))
            {
                if (Accept(TokenKind.First))
                {
                    nulls = NullsOrder.First;
                }
                else
                {
                    Expect(TokenKind.Last);
                    nulls = NullsOrder.Last;
                }
            }

            b.Add(Done(new OrderingTerm(e, order, nulls), start));
        }
        while (Accept(TokenKind.Comma));
        return Builders<OrderingTerm>.Finish(b);
    }

    // ( [base] [PARTITION BY ..] [ORDER BY ..] [frame] )   (lang_select window-defn)
    private WindowSpec ParseWindowSpec()
    {
        int start = Peek(0).Start;
        Expect(TokenKind.LParen);
        Enter(6); // the call's argument list, its parenthesis, OVER and this parenthesis wait on the parser stack
        Name? baseName = null;
        if (IsNmStart(Kind()) && Kind() is not (TokenKind.Partition or TokenKind.Range or TokenKind.Rows or TokenKind.Groups))
        {
            baseName = ParseNm();
        }

        ImmutableArray<Expr> partition = ImmutableArray<Expr>.Empty;
        if (Accept(TokenKind.Partition))
        {
            Expect(TokenKind.By);
            partition = ParseExprList(out _);
        }

        ImmutableArray<OrderingTerm> order = ImmutableArray<OrderingTerm>.Empty;
        if (Accept(TokenKind.Order))
        {
            Expect(TokenKind.By);
            order = ParseOrderingTerms();
        }

        FrameSpec? frame = null;
        if (Kind() is TokenKind.Range or TokenKind.Rows or TokenKind.Groups)
        {
            frame = ParseFrame();
        }

        Leave(6);
        Expect(TokenKind.RParen);
        return Done(new WindowSpec(baseName, partition, order, frame), start);
    }

    private FrameSpec ParseFrame()
    {
        int start = Peek(0).Start;
        TokenKind u = Advance().Kind;
        FrameUnit unit = u == TokenKind.Range ? FrameUnit.Range : u == TokenKind.Rows ? FrameUnit.Rows : FrameUnit.Groups;
        FrameBound first;
        FrameBound? second = null;
        if (Accept(TokenKind.Between))
        {
            first = ParseFrameBound(isStart: true);
            Expect(TokenKind.And);
            second = ParseFrameBound(isStart: false);
        }
        else
        {
            first = ParseFrameBound(isStart: true);
        }

        FrameExclude exclude = FrameExclude.None;
        if (Accept(TokenKind.Exclude))
        {
            switch (Kind())
            {
                case TokenKind.No:
                    Advance();
                    Expect(TokenKind.Others);
                    exclude = FrameExclude.NoOthers;
                    break;
                case TokenKind.Current:
                    Advance();
                    Expect(TokenKind.Row);
                    exclude = FrameExclude.CurrentRow;
                    break;
                case TokenKind.Group:
                    Advance();
                    exclude = FrameExclude.Group;
                    break;
                case TokenKind.Ties:
                    Advance();
                    exclude = FrameExclude.Ties;
                    break;
                default:
                    throw Fail();
            }
        }

        return Done(new FrameSpec(unit, first, second, exclude), start);
    }

    // frame_bound_s: UNBOUNDED PRECEDING | CURRENT ROW | expr PRECEDING | expr FOLLOWING
    // frame_bound_e: UNBOUNDED FOLLOWING | CURRENT ROW | expr PRECEDING | expr FOLLOWING
    private FrameBound ParseFrameBound(bool isStart)
    {
        int start = Peek(0).Start;
        if (Accept(TokenKind.Unbounded))
        {
            if (isStart)
            {
                Expect(TokenKind.Preceding);
                return Done(new FrameBound(FrameBoundKind.UnboundedPreceding, null), start);
            }

            Expect(TokenKind.Following);
            return Done(new FrameBound(FrameBoundKind.UnboundedFollowing, null), start);
        }

        if (Accept(TokenKind.Current))
        {
            Expect(TokenKind.Row);
            return Done(new FrameBound(FrameBoundKind.CurrentRow, null), start);
        }

        Expr e = ParseExpr();
        if (Accept(TokenKind.Preceding))
        {
            return Done(new FrameBound(FrameBoundKind.Preceding, e), start);
        }

        Expect(TokenKind.Following);
        return Done(new FrameBound(FrameBoundKind.Following, e), start);
    }
}
