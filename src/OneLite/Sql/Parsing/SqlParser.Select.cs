using System.Collections.Immutable;

namespace RecursiveAI.OneLite.Sql.Parsing;

internal sealed partial class SqlParser
{
    private SelectStatement ParseSelectStatement(WithClause? with)
    {
        int start = with?.Span.Start ?? Peek(0).Start;
        SelectStatement s = ParseSelectBody(with);
        s.Span = new SourceSpan(start, _prevEnd);
        return s;
    }

    /// <summary>select-stmt, including a leading WITH. On return <c>_height</c> is the tallest expression inside it.</summary>
    private SelectStatement ParseSelectBody(WithClause? with = null)
    {
        int start = with?.Span.Start ?? Peek(0).Start;
        int outerHeight = _selectHeight;
        _selectHeight = 0;
        if (with == null && Kind() == TokenKind.With)
        {
            with = ParseWith();
        }

        Enter(2); // SELECT and its result columns are on the parser stack while the clauses after them are parsed

        ImmutableArray<SelectCore>.Builder cores = Builders<SelectCore>.Rent();
        ImmutableArray<CompoundOperator>.Builder ops = Builders<CompoundOperator>.Rent();
        cores.Add(ParseSelectCore());
        for (; ; )
        {
            CompoundOperator op;
            switch (Kind())
            {
                case TokenKind.Union:
                    Advance();
                    op = Accept(TokenKind.All) ? CompoundOperator.UnionAll : CompoundOperator.Union;
                    break;
                case TokenKind.Intersect:
                    Advance();
                    op = CompoundOperator.Intersect;
                    break;
                case TokenKind.Except:
                    Advance();
                    op = CompoundOperator.Except;
                    break;
                default:
                    goto done;
            }

            ops.Add(op);
            cores.Add(ParseSelectCore());
            if (cores.Count > MaxCompoundSelect)
            {
                Pending("too many terms in compound SELECT", -1);
            }
        }

    done:
        Leave(2);
        int inner = _selectHeight;
        _selectHeight = Math.Max(outerHeight, inner);
        _height = inner;
        return Done(new SelectStatement(with, Builders<SelectCore>.Finish(cores), Builders<CompoundOperator>.Finish(ops)), start);
    }

    private WithClause ParseWith()
    {
        int start = Peek(0).Start;
        Expect(TokenKind.With);
        bool recursive = Accept(TokenKind.Recursive);
        ImmutableArray<CommonTableExpression>.Builder tables = Builders<CommonTableExpression>.Rent();
        do
        {
            int cs = Peek(0).Start;
            Name name = ParseNm();
            ImmutableArray<Name> cols = ImmutableArray<Name>.Empty;
            if (Kind() == TokenKind.LParen)
            {
                Advance();
                cols = ParseEidlistUntilParen();
            }

            Expect(TokenKind.As);
            CteMaterialization mat = CteMaterialization.Default;
            if (Accept(TokenKind.Materialized))
            {
                mat = CteMaterialization.Materialized;
            }
            else if (Accept(TokenKind.Not))
            {
                Expect(TokenKind.Materialized);
                mat = CteMaterialization.NotMaterialized;
            }

            Expect(TokenKind.LParen);
            Enter(5); // WITH, the name, its column list, AS and the parenthesis
            SelectStatement sel = ParseSelectStatement(null);
            Leave(5);
            Expect(TokenKind.RParen);
            tables.Add(Done(new CommonTableExpression(name, cols, mat, sel), cs));
        }
        while (Accept(TokenKind.Comma));
        return Done(new WithClause(recursive, Builders<CommonTableExpression>.Finish(tables)), start);
    }

    // after "(": one or more names and ")" (insert column lists and USING)
    private ImmutableArray<Name> ParseColumnNameListUntilParen()
    {
        ImmutableArray<Name>.Builder b = Builders<Name>.Rent();
        do
        {
            b.Add(ParseNm());
        }
        while (Accept(TokenKind.Comma));
        Expect(TokenKind.RParen);
        return Builders<Name>.Finish(b);
    }

    private SelectCore ParseSelectCore()
    {
        int start = Peek(0).Start;
        if (Kind() == TokenKind.Values)
        {
            Advance();
            ImmutableArray<ImmutableArray<Expr>>.Builder rows = Builders<ImmutableArray<Expr>>.Rent();
            do
            {
                Expect(TokenKind.LParen);
                rows.Add(ParseExprList(out _));
                Expect(TokenKind.RParen);
            }
            while (Accept(TokenKind.Comma));
            return Done(new ValuesCore(Builders<ImmutableArray<Expr>>.Finish(rows)), start);
        }

        Expect(TokenKind.Select);
        SelectQuantifier quant = SelectQuantifier.None;
        if (Accept(TokenKind.Distinct))
        {
            quant = SelectQuantifier.Distinct;
        }
        else if (Accept(TokenKind.All))
        {
            quant = SelectQuantifier.All;
        }

        ImmutableArray<ResultColumn> columns = ParseResultColumns();
        FromClause? from = null;
        if (Kind() == TokenKind.From)
        {
            from = ParseFromClause();
        }

        Expr? where = Accept(TokenKind.Where) ? ParseExpr() : null;
        ImmutableArray<Expr> groupBy = ImmutableArray<Expr>.Empty;
        if (Accept(TokenKind.Group))
        {
            Expect(TokenKind.By);
            groupBy = ParseExprList(out _);
        }

        Expr? having = Accept(TokenKind.Having) ? ParseExpr() : null;
        ImmutableArray<WindowDefinition> windows = ImmutableArray<WindowDefinition>.Empty;
        if (Accept(TokenKind.Window))
        {
            ImmutableArray<WindowDefinition>.Builder b = Builders<WindowDefinition>.Rent();
            do
            {
                int ws = Peek(0).Start;
                Name wn = ParseNm();
                Expect(TokenKind.As);
                b.Add(Done(new WindowDefinition(wn, ParseWindowSpec()), ws));
            }
            while (Accept(TokenKind.Comma));
            windows = Builders<WindowDefinition>.Finish(b);
        }

        ImmutableArray<OrderingTerm> orderBy = ImmutableArray<OrderingTerm>.Empty;
        if (Accept(TokenKind.Order))
        {
            Expect(TokenKind.By);
            orderBy = ParseOrderingTerms();
        }

        LimitClause? limit = ParseLimit();
        return Done(new SimpleSelect(quant, columns, from, where, groupBy, having, windows, orderBy, limit), start);
    }

    private LimitClause? ParseLimit()
    {
        if (Kind() != TokenKind.Limit)
        {
            return null;
        }

        int start = Peek(0).Start;
        Advance();
        Expr first = ParseExpr();
        if (Accept(TokenKind.Offset))
        {
            Expr off = ParseExpr();
            return Done(new LimitClause(first, off), start);
        }

        if (Accept(TokenKind.Comma))
        {
            // LIMIT offset, count
            Expr count = ParseExpr();
            return Done(new LimitClause(count, first), start);
        }

        return Done(new LimitClause(first, null), start);
    }

    private ImmutableArray<ResultColumn> ParseResultColumns()
    {
        ImmutableArray<ResultColumn>.Builder b = Builders<ResultColumn>.Rent();
        do
        {
            int start = Peek(0).Start;
            if (Kind() == TokenKind.Star)
            {
                Advance();
                b.Add(Done(new StarColumn(null), start));
            }
            // CAST, RAISE and the CURRENT_ words start an expression of their own, so they are not names before ".*" (lang_select)
            else if (IsNmStart(Kind()) && Kind() is not (TokenKind.Cast or TokenKind.Raise or TokenKind.CurrentTime or TokenKind.CurrentDate or TokenKind.CurrentTimestamp)
                && Kind(1) == TokenKind.Dot && Kind(2) == TokenKind.Star)
            {
                Name t = ParseNm();
                Advance();
                Advance();
                b.Add(Done(new StarColumn(t), start));
            }
            else
            {
                Expr e = ParseExpr();
                Name? alias = ParseAlias();
                b.Add(Done(new ExprColumn(e, alias), start));
            }
        }
        while (Accept(TokenKind.Comma));
        return Builders<ResultColumn>.Finish(b);
    }

    // as: AS name | a bare identifier or string
    private Name? ParseAlias()
    {
        if (Accept(TokenKind.As))
        {
            return ParseNm();
        }

        return IsIdsStart(Kind()) ? ParseIds() : null;
    }

    private FromClause ParseFromClause()
    {
        int start = Peek(0).Start;
        Expect(TokenKind.From);
        Enter(2); // FROM and the list of tables so far
        FromClause list = ParseTableList(start);
        Leave(2);
        return list;
    }

    // seltablist: table-or-subquery (join-operator table-or-subquery)* with each table's ON or USING
    private FromClause ParseTableList(int start)
    {
        ImmutableArray<FromItem>.Builder items = Builders<FromItem>.Rent();
        JoinOperator? join = null;
        for (; ; )
        {
            int itemStart = join?.Span.Start ?? Peek(0).Start;
            TableSource source = ParseTableSource();
            Expr? on = null;
            ImmutableArray<Name> usingCols = ImmutableArray<Name>.Empty;
            if (Accept(TokenKind.On))
            {
                on = ParseExpr();
            }
            else if (Accept(TokenKind.Using))
            {
                Expect(TokenKind.LParen);
                usingCols = ParseColumnNameListUntilParen();
            }

            items.Add(Done(new FromItem(join, source, on, usingCols), itemStart));
            join = ParseJoinOperator();
            if (join == null)
            {
                break;
            }
        }

        return Done(new FromClause(Builders<FromItem>.Finish(items)), start);
    }

    // , | [NATURAL] [LEFT [OUTER] | INNER | CROSS | ..] JOIN: up to three words before JOIN
    private JoinOperator? ParseJoinOperator()
    {
        int start = Peek(0).Start;
        if (Accept(TokenKind.Comma))
        {
            return Done(new JoinOperator(JoinFlags.Comma, ImmutableArray<string>.Empty), start);
        }

        ImmutableArray<string>.Builder words = Builders<string>.Rent();
        if (Kind() != TokenKind.Join)
        {
            if (!Lexer.IsJoinKeyword(Kind()))
            {
                return null;
            }

            words.Add(WordText(Advance()));
            while (words.Count < 3 && IsNmStart(Kind()))
            {
                words.Add(WordText(Advance()));
            }
        }

        Expect(TokenKind.Join);
        JoinFlags flags = JoinFlags.None;
        foreach (string w in words)
        {
            flags |= w switch
            {
                "NATURAL" => JoinFlags.Natural,
                "LEFT" => JoinFlags.Left,
                "RIGHT" => JoinFlags.Right,
                "FULL" => JoinFlags.Full,
                "OUTER" => JoinFlags.Outer,
                "INNER" => JoinFlags.Inner,
                "CROSS" => JoinFlags.Cross,
                _ => JoinFlags.Unknown,
            };
        }

        return Done(new JoinOperator(flags, Builders<string>.Finish(words)), start);
    }

    private string WordText(Token t) => Unquote(t).ToUpperInvariant();

    private TableSource ParseTableSource()
    {
        int start = Peek(0).Start;
        if (Kind() == TokenKind.LParen)
        {
            Advance();
            Enter(2); // the parenthesis and the empty list of tables before it
            TableSource result;
            if (Kind() is TokenKind.Select or TokenKind.Values or TokenKind.With)
            {
                SelectStatement sel = ParseSelectStatement(null);
                Expect(TokenKind.RParen);
                result = Done(new SubquerySource(sel, ParseAlias()), start);
            }
            else
            {
                FromClause inner = ParseTableList(Peek(0).Start);
                Expect(TokenKind.RParen);
                result = Done(new ParenFrom(inner, ParseAlias()), start);
            }

            Leave(2);
            return result;
        }

        QualifiedName name = ParseQualifiedName();
        if (Kind() == TokenKind.LParen)
        {
            Advance();
            ImmutableArray<Expr> args = ParseExprListUntilParen(out _);
            return Done(new TableFunction(name, args, ParseAlias()), start);
        }

        Name? alias = ParseAlias();
        IndexHint? hint = null;
        int hs = Peek(0).Start;
        if (Kind() == TokenKind.Indexed)
        {
            Advance();
            Expect(TokenKind.By);
            hint = Done(new IndexHint(ParseNm()), hs);
        }
        else if (Kind() == TokenKind.Not)
        {
            Advance();
            Expect(TokenKind.Indexed);
            hint = Done(new IndexHint(null), hs);
        }

        return Done(new NamedTable(name, alias, hint), start);
    }
}
