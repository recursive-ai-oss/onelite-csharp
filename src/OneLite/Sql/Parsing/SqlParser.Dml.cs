using System.Collections.Immutable;

namespace RecursiveAI.OneLite.Sql.Parsing;

internal sealed partial class SqlParser
{
    private ConflictAction ParseConflictAction()
    {
        ConflictAction a = Kind() switch
        {
            TokenKind.Rollback => ConflictAction.Rollback,
            TokenKind.Abort => ConflictAction.Abort,
            TokenKind.Fail => ConflictAction.Fail,
            TokenKind.Ignore => ConflictAction.Ignore,
            TokenKind.Replace => ConflictAction.Replace,
            _ => throw Fail(),
        };
        Advance();
        return a;
    }

    // ON CONFLICT action, when present
    private ConflictAction? ParseOnConflict()
    {
        if (Kind() != TokenKind.On)
        {
            return null;
        }

        Advance();
        Expect(TokenKind.Conflict);
        return ParseConflictAction();
    }

    private ReturningClause ParseReturning()
    {
        int start = Peek(0).Start;
        Expect(TokenKind.Returning);
        return Done(new ReturningClause(ParseResultColumns()), start);
    }

    private InsertStatement ParseInsert(WithClause? with, int start)
    {
        if (_inTrigger && with != null)
        {
            throw Fail();
        }

        InsertVerb verb = Kind() == TokenKind.Replace ? InsertVerb.Replace : InsertVerb.Insert;
        Advance();
        ConflictAction? or = null;
        if (verb == InsertVerb.Insert && Accept(TokenKind.Or))
        {
            or = ParseConflictAction();
        }

        Expect(TokenKind.Into);
        QualifiedName table = ParseQualifiedName();
        Name? alias = Accept(TokenKind.As) ? ParseNm() : null;
        ImmutableArray<Name> columns = ImmutableArray<Name>.Empty;
        if (Accept(TokenKind.LParen))
        {
            columns = ParseColumnNameListUntilParen();
        }

        int ss = Peek(0).Start;
        InsertSource source;
        ImmutableArray<UpsertClause> upserts = ImmutableArray<UpsertClause>.Empty;
        ReturningClause? returning = null;
        if (Kind() == TokenKind.Default && !_inTrigger)
        {
            Advance();
            Expect(TokenKind.Values);
            source = Done(new InsertSource(null), ss);
            if (Kind() == TokenKind.Returning)
            {
                returning = ParseReturning();
            }
        }
        else
        {
            if (Kind() is not (TokenKind.Select or TokenKind.Values or TokenKind.With))
            {
                throw Fail();
            }

            source = Done(new InsertSource(ParseSelectStatement(null)), ss);
            (upserts, returning) = ParseUpserts();
        }

        return Done(new InsertStatement(with, verb, or, table, alias, columns, source, upserts, returning), start);
    }

    // upsert: (ON CONFLICT (target) [WHERE ..] DO NOTHING | DO UPDATE SET ..)* with a target-less clause last, then RETURNING
    private (ImmutableArray<UpsertClause>, ReturningClause?) ParseUpserts()
    {
        ImmutableArray<UpsertClause>.Builder list = Builders<UpsertClause>.Rent();
        while (Kind() == TokenKind.On)
        {
            int start = Peek(0).Start;
            Advance();
            Expect(TokenKind.Conflict);
            ImmutableArray<OrderingTerm> target = ImmutableArray<OrderingTerm>.Empty;
            Expr? targetWhere = null;
            bool hasTarget = false;
            if (Accept(TokenKind.LParen))
            {
                hasTarget = true;
                target = ParseOrderingTerms();
                Expect(TokenKind.RParen);
                targetWhere = Accept(TokenKind.Where) ? ParseExpr() : null;
            }

            Expect(TokenKind.Do);
            if (Accept(TokenKind.Nothing))
            {
                list.Add(Done(new UpsertClause(target, targetWhere, true, ImmutableArray<SetClause>.Empty, null), start));
            }
            else
            {
                Expect(TokenKind.Update);
                Expect(TokenKind.Set);
                ImmutableArray<SetClause> sets = ParseSetList();
                Expr? where = Accept(TokenKind.Where) ? ParseExpr() : null;
                list.Add(Done(new UpsertClause(target, targetWhere, false, sets, where), start));
            }

            if (!hasTarget)
            {
                return (Builders<UpsertClause>.Finish(list), Kind() == TokenKind.Returning ? ParseReturning() : null);
            }
        }

        return (Builders<UpsertClause>.Finish(list), Kind() == TokenKind.Returning ? ParseReturning() : null);
    }

    private ImmutableArray<SetClause> ParseSetList()
    {
        ImmutableArray<SetClause>.Builder b = Builders<SetClause>.Rent();
        do
        {
            int start = Peek(0).Start;
            ImmutableArray<Name> cols;
            if (Accept(TokenKind.LParen))
            {
                cols = ParseColumnNameListUntilParen();
            }
            else
            {
                cols = [ParseNm()];
            }

            Expect(TokenKind.Eq);
            Expr value = ParseExpr();
            b.Add(Done(new SetClause(cols, value), start));
        }
        while (Accept(TokenKind.Comma));
        return Builders<SetClause>.Finish(b);
    }

    // [schema.]name [AS alias] [INDEXED BY name | NOT INDEXED]
    private NamedTable ParseQualifiedTable()
    {
        int start = Peek(0).Start;
        QualifiedName name = ParseQualifiedName();
        Name? alias = Accept(TokenKind.As) ? ParseNm() : null;
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

    private UpdateStatement ParseUpdate(WithClause? with, int start)
    {
        if (_inTrigger && with != null)
        {
            throw Fail();
        }

        Expect(TokenKind.Update);
        ConflictAction? or = Accept(TokenKind.Or) ? ParseConflictAction() : null;
        NamedTable target = ParseQualifiedTable();
        Expect(TokenKind.Set);
        ImmutableArray<SetClause> sets = ParseSetList();
        FromClause? from = Kind() == TokenKind.From ? ParseFromClause() : null;
        Expr? where = Accept(TokenKind.Where) ? ParseExpr() : null;
        ReturningClause? returning = !_inTrigger && Kind() == TokenKind.Returning ? ParseReturning() : null;
        return Done(new UpdateStatement(with, or, target, sets, from, where, returning), start);
    }

    private DeleteStatement ParseDelete(WithClause? with, int start)
    {
        if (_inTrigger && with != null)
        {
            throw Fail();
        }

        Expect(TokenKind.Delete);
        Expect(TokenKind.From);
        NamedTable target = ParseQualifiedTable();
        Expr? where = Accept(TokenKind.Where) ? ParseExpr() : null;
        ReturningClause? returning = !_inTrigger && Kind() == TokenKind.Returning ? ParseReturning() : null;
        return Done(new DeleteStatement(with, target, where, returning), start);
    }
}
