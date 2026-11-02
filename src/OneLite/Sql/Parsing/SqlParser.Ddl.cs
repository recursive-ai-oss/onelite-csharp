using System.Collections.Immutable;

namespace RecursiveAI.OneLite.Sql.Parsing;

internal sealed partial class SqlParser
{
    private DropStatement ParseDrop(int start)
    {
        Advance();
        DropKind kind = Kind() switch
        {
            TokenKind.Table => DropKind.Table,
            TokenKind.View => DropKind.View,
            TokenKind.Index => DropKind.Index,
            TokenKind.Trigger => DropKind.Trigger,
            _ => throw Fail(),
        };
        Advance();
        bool ifExists = false;
        if (Accept(TokenKind.If))
        {
            Expect(TokenKind.Exists);
            ifExists = true;
        }

        return Done(new DropStatement(kind, ifExists, ParseQualifiedName()), start);
    }

    private bool ParseIfNotExists()
    {
        if (!Accept(TokenKind.If))
        {
            return false;
        }

        Expect(TokenKind.Not);
        Expect(TokenKind.Exists);
        return true;
    }

    private Statement ParseCreate(int start)
    {
        Advance();
        bool temp = Kind() is TokenKind.Temp or TokenKind.Temporary;
        if (temp)
        {
            Advance();
        }

        switch (Kind())
        {
            case TokenKind.Table:
                return ParseCreateTable(start, temp);
            case TokenKind.View:
            {
                Advance();
                bool ine = ParseIfNotExists();
                QualifiedName name = ParseQualifiedName();
                ImmutableArray<Name> cols = ImmutableArray<Name>.Empty;
                if (Accept(TokenKind.LParen))
                {
                    cols = ParseEidlistUntilParen();
                }

                Expect(TokenKind.As);
                SelectStatement sel = ParseSelectStatement(null);
                return Done(new CreateViewStatement(temp, ine, name, cols, sel), start);
            }

            case TokenKind.Trigger:
                return ParseCreateTrigger(start, temp);
            case TokenKind.Unique when !temp:
            case TokenKind.Index when !temp:
            {
                bool unique = Accept(TokenKind.Unique);
                Expect(TokenKind.Index);
                bool ine = ParseIfNotExists();
                QualifiedName name = ParseQualifiedName();
                Expect(TokenKind.On);
                Name table = ParseNm();
                Expect(TokenKind.LParen);
                ImmutableArray<OrderingTerm> cols = ParseOrderingTerms();
                Expect(TokenKind.RParen);
                Expr? where = Accept(TokenKind.Where) ? ParseExpr() : null;
                return Done(new CreateIndexStatement(unique, ine, name, table, cols, where), start);
            }

            case TokenKind.Virtual when !temp:
            {
                Advance();
                Expect(TokenKind.Table);
                bool ine = ParseIfNotExists();
                QualifiedName name = ParseQualifiedName();
                Expect(TokenKind.Using);
                Name module = ParseNm();
                ImmutableArray<string> args = ImmutableArray<string>.Empty;
                if (Kind() == TokenKind.LParen)
                {
                    args = ParseModuleArguments();
                }

                return Done(new CreateVirtualTableStatement(ine, name, module, args), start);
            }

            default:
                throw Fail();
        }
    }

    // ( name [COLLATE c] [ASC|DESC], .. ) for views and foreign keys; only the names are kept for views
    // The column list of a view or a common table expression. The grammar shares its rule with index columns, so a collation or sort
    // order parses and then fails in the rule's action (observed: `syntax error after column name "a"`, no offset).
    private ImmutableArray<Name> ParseEidlistUntilParen()
    {
        ImmutableArray<Name>.Builder b = Builders<Name>.Rent();
        do
        {
            Name n = ParseNm();
            bool extras = false;
            if (Accept(TokenKind.Collate))
            {
                ParseIds();
                extras = true;
            }

            if (Accept(TokenKind.Asc) || Accept(TokenKind.Desc))
            {
                extras = true;
            }

            if (extras && Kind() is TokenKind.Comma or TokenKind.RParen)
            {
                Pending($"syntax error after column name \"{n.Text}\"", -1);
            }

            b.Add(n);
        }
        while (Accept(TokenKind.Comma));
        Expect(TokenKind.RParen);
        return Builders<Name>.Finish(b);
    }

    private ImmutableArray<IndexedColumn> ParseIndexedColumnsUntilParen()
    {
        ImmutableArray<IndexedColumn>.Builder b = Builders<IndexedColumn>.Rent();
        do
        {
            int start = Peek(0).Start;
            Name n = ParseNm();
            Name? coll = Accept(TokenKind.Collate) ? ParseIds() : null;
            SortOrder order = SortOrder.None;
            if (Accept(TokenKind.Asc))
            {
                order = SortOrder.Asc;
            }
            else if (Accept(TokenKind.Desc))
            {
                order = SortOrder.Desc;
            }

            b.Add(Done(new IndexedColumn(n, coll, order), start));
        }
        while (Accept(TokenKind.Comma));
        Expect(TokenKind.RParen);
        return Builders<IndexedColumn>.Finish(b);
    }

    // USING module ( args ): every token up to the matching parenthesis is part of an argument; arguments split at top-level commas
    private ImmutableArray<string> ParseModuleArguments()
    {
        Expect(TokenKind.LParen);
        ImmutableArray<string>.Builder args = Builders<string>.Rent();
        int depth = 1;
        int argStart = Peek(0).Start;
        int lastEnd = argStart;
        for (; ; )
        {
            Token t = Peek(0);
            if (t.Kind is TokenKind.Eof or TokenKind.Illegal)
            {
                throw Fail();
            }

            if (t.Kind == TokenKind.LParen)
            {
                depth++;
            }
            else if (t.Kind == TokenKind.RParen)
            {
                depth--;
                if (depth == 0)
                {
                    args.Add(_sql.Substring(argStart, Math.Max(0, lastEnd - argStart)).Trim());
                    Advance();
                    return Builders<string>.Finish(args);
                }
            }
            else if (t.Kind == TokenKind.Comma && depth == 1)
            {
                args.Add(_sql.Substring(argStart, Math.Max(0, lastEnd - argStart)).Trim());
                Advance();
                argStart = Peek(0).Start;
                lastEnd = argStart;
                continue;
            }

            lastEnd = t.End;
            Advance();
        }
    }

    private CreateTableStatement ParseCreateTable(int start, bool temp)
    {
        Advance(); // TABLE
        bool ine = ParseIfNotExists();
        QualifiedName name = ParseQualifiedName();
        if (Accept(TokenKind.As))
        {
            SelectStatement sel = ParseSelectStatement(null);
            return Done(new CreateTableStatement(temp, ine, name, ImmutableArray<ColumnDefinition>.Empty, ImmutableArray<TableConstraint>.Empty, ImmutableArray<TableOption>.Empty, sel), start);
        }

        Expect(TokenKind.LParen);
        ImmutableArray<ColumnDefinition>.Builder cols = Builders<ColumnDefinition>.Rent();
        ImmutableArray<TableConstraint>.Builder cons = Builders<TableConstraint>.Rent();
        cols.Add(ParseColumnDefinition());
        bool constraintsStarted = false;
        while (Kind() == TokenKind.Comma)
        {
            Advance();
            if (IsTableConstraintStart(Kind()))
            {
                constraintsStarted = true;
                break;
            }

            cols.Add(ParseColumnDefinition());
        }

        if (constraintsStarted)
        {
            // conslist: constraints, separated by commas or by nothing
            for (; ; )
            {
                ParseTableConstraint(cons);
                if (Kind() == TokenKind.Comma)
                {
                    Advance();
                }
                else if (!IsTableConstraintStart(Kind()))
                {
                    break;
                }
            }
        }

        Expect(TokenKind.RParen);
        ImmutableArray<TableOption>.Builder opts = Builders<TableOption>.Rent();
        // lang_createtable: table options are separated by commas, and the grammar lets a comma come before the first one
        bool leadingComma = Accept(TokenKind.Comma);
        if (leadingComma || Kind() == TokenKind.Without || IsNmStart(Kind()))
        {
            do
            {
                int os = Peek(0).Start;
                if (Accept(TokenKind.Without))
                {
                    opts.Add(Done(new TableOption(true, ParseNm().Text), os));
                }
                else
                {
                    opts.Add(Done(new TableOption(false, ParseNm().Text), os));
                }
            }
            while (Accept(TokenKind.Comma));
        }

        return Done(new CreateTableStatement(temp, ine, name, Builders<ColumnDefinition>.Finish(cols), Builders<TableConstraint>.Finish(cons), Builders<TableOption>.Finish(opts), null), start);
    }

    private static bool IsTableConstraintStart(TokenKind k) => k is TokenKind.Constraint or TokenKind.Primary or TokenKind.Unique or TokenKind.Check or TokenKind.Foreign;

    private void ParseTableConstraint(ImmutableArray<TableConstraint>.Builder into)
    {
        int start = Peek(0).Start;
        Name? cname = null;
        if (Accept(TokenKind.Constraint))
        {
            cname = ParseNm();
            if (!IsTableConstraintStart(Kind()) || Kind() == TokenKind.Constraint)
            {
                into.Add(Done(new DanglingTableConstraintName(cname), start));
                return;
            }
        }

        switch (Kind())
        {
            case TokenKind.Primary:
            {
                Advance();
                Expect(TokenKind.Key);
                Expect(TokenKind.LParen);
                ImmutableArray<OrderingTerm> cols = ParseOrderingTerms();
                bool auto = Accept(TokenKind.Autoincrement);
                Expect(TokenKind.RParen);
                into.Add(Done(new PrimaryKeyTableConstraint(cname, cols, auto, ParseOnConflict()), start));
                break;
            }

            case TokenKind.Unique:
            {
                Advance();
                Expect(TokenKind.LParen);
                ImmutableArray<OrderingTerm> cols = ParseOrderingTerms();
                Expect(TokenKind.RParen);
                into.Add(Done(new UniqueTableConstraint(cname, cols, ParseOnConflict()), start));
                break;
            }

            case TokenKind.Check:
            {
                Advance();
                Expect(TokenKind.LParen);
                Expr e = ParseExpr();
                Expect(TokenKind.RParen);
                into.Add(Done(new CheckTableConstraint(cname, e, ParseOnConflict()), start));
                break;
            }

            case TokenKind.Foreign:
            {
                Advance();
                Expect(TokenKind.Key);
                Expect(TokenKind.LParen);
                ImmutableArray<IndexedColumn> cols = ParseIndexedColumnsUntilParen();
                Expect(TokenKind.References);
                ForeignKeyClause fk = ParseForeignKeyClause();
                Deferral? d = Kind() is TokenKind.Not or TokenKind.Deferrable ? ParseDeferral() : null;
                into.Add(Done(new ForeignKeyTableConstraint(cname, cols, fk, d), start));
                break;
            }

            default:
                throw Fail();
        }
    }

    // after REFERENCES: table [(columns)] and any ON DELETE/UPDATE/MATCH rules
    private ForeignKeyClause ParseForeignKeyClause()
    {
        int start = Peek(0).Start;
        Name table = ParseNm();
        ImmutableArray<IndexedColumn> cols = ImmutableArray<IndexedColumn>.Empty;
        if (Accept(TokenKind.LParen))
        {
            cols = ParseIndexedColumnsUntilParen();
        }

        ImmutableArray<ForeignKeyRule>.Builder rules = Builders<ForeignKeyRule>.Rent();
        for (; ; )
        {
            int rs = Peek(0).Start;
            if (Accept(TokenKind.On))
            {
                ForeignKeyRuleKind kind;
                if (Accept(TokenKind.Delete))
                {
                    kind = ForeignKeyRuleKind.OnDelete;
                }
                else
                {
                    Expect(TokenKind.Update);
                    kind = ForeignKeyRuleKind.OnUpdate;
                }

                ForeignKeyAction action;
                if (Accept(TokenKind.Set))
                {
                    if (Accept(TokenKind.Null))
                    {
                        action = ForeignKeyAction.SetNull;
                    }
                    else
                    {
                        Expect(TokenKind.Default);
                        action = ForeignKeyAction.SetDefault;
                    }
                }
                else if (Accept(TokenKind.Cascade))
                {
                    action = ForeignKeyAction.Cascade;
                }
                else if (Accept(TokenKind.Restrict))
                {
                    action = ForeignKeyAction.Restrict;
                }
                else
                {
                    Expect(TokenKind.No);
                    Expect(TokenKind.Action);
                    action = ForeignKeyAction.NoAction;
                }

                rules.Add(Done(new ForeignKeyRule(kind, action, null), rs));
            }
            else if (Accept(TokenKind.Match))
            {
                rules.Add(Done(new ForeignKeyRule(ForeignKeyRuleKind.Match, ForeignKeyAction.NoAction, ParseNm()), rs));
            }
            else
            {
                break;
            }
        }

        return Done(new ForeignKeyClause(table, cols, Builders<ForeignKeyRule>.Finish(rules)), start);
    }

    // [NOT] DEFERRABLE [INITIALLY DEFERRED|IMMEDIATE]
    private Deferral ParseDeferral()
    {
        int start = Peek(0).Start;
        bool not = Accept(TokenKind.Not);
        Expect(TokenKind.Deferrable);
        DeferMode? initially = null;
        if (Accept(TokenKind.Initially))
        {
            if (Accept(TokenKind.Deferred))
            {
                initially = DeferMode.Deferred;
            }
            else
            {
                Expect(TokenKind.Immediate);
                initially = DeferMode.Immediate;
            }
        }

        return Done(new Deferral(not, initially), start);
    }

    private ColumnDefinition ParseColumnDefinition()
    {
        int start = Peek(0).Start;
        Name name = ParseNm();
        TypeName? type = null;
        TokenKind lastWord = TokenKind.Eof, prevWord = TokenKind.Eof; // kinds of the type's last two words: only bare GENERATED ALWAYS can be a constraint
        if (IsIdsStart(Kind()))
        {
            type = ParseTypeName();
            int count = _typeWordKinds.Count;
            lastWord = count > 0 ? _typeWordKinds[count - 1] : TokenKind.Eof;
            prevWord = count > 1 ? _typeWordKinds[count - 2] : TokenKind.Eof;
        }

        ImmutableArray<ColumnConstraint>.Builder cons = Builders<ColumnConstraint>.Rent();
        Name? cname = null;
        for (; ; )
        {
            int cs = Peek(0).Start;
            ColumnConstraint? c;
            switch (Kind())
            {
                case TokenKind.Constraint:
                    if (cname != null)
                    {
                        cons.Add(Done(new DanglingColumnConstraintName(cname), cname.Span.Start));
                    }

                    Advance();
                    cname = ParseNm();
                    continue;
                case TokenKind.Primary:
                {
                    Advance();
                    Expect(TokenKind.Key);
                    SortOrder order = SortOrder.None;
                    if (Accept(TokenKind.Asc))
                    {
                        order = SortOrder.Asc;
                    }
                    else if (Accept(TokenKind.Desc))
                    {
                        order = SortOrder.Desc;
                    }

                    ConflictAction? oc = ParseOnConflict();
                    bool auto = Accept(TokenKind.Autoincrement);
                    c = new PrimaryKeyColumnConstraint(cname, order, oc, auto);
                    break;
                }

                case TokenKind.Not when Kind(1) == TokenKind.Null:
                    Advance();
                    Advance();
                    c = new NotNullConstraint(cname, ParseOnConflict());
                    break;
                case TokenKind.Not:
                    c = new DeferralConstraint(cname, ParseDeferral());
                    break;
                case TokenKind.Null:
                    Advance();
                    c = new NullConstraint(cname, ParseOnConflict());
                    break;
                case TokenKind.Unique:
                    Advance();
                    c = new UniqueColumnConstraint(cname, ParseOnConflict());
                    break;
                case TokenKind.Check:
                {
                    Advance();
                    Expect(TokenKind.LParen);
                    Expr e = ParseExpr();
                    Expect(TokenKind.RParen);
                    c = new CheckColumnConstraint(cname, e);
                    break;
                }

                case TokenKind.Default:
                    c = new DefaultConstraint(cname, ParseDefaultValue());
                    break;
                case TokenKind.Collate:
                    Advance();
                    c = new CollateConstraint(cname, ParseIds());
                    break;
                case TokenKind.References:
                    Advance();
                    c = new ReferencesConstraint(cname, ParseForeignKeyClause());
                    break;
                case TokenKind.Deferrable:
                    c = new DeferralConstraint(cname, ParseDeferral());
                    break;
                case TokenKind.Generated:
                case TokenKind.As:
                {
                    // lang_createtable: after another constraint, GENERATED ALWAYS AS starts a constraint of its own
                    bool spelled = Kind() == TokenKind.Generated;
                    Advance();
                    if (spelled)
                    {
                        Expect(TokenKind.Always);
                        Expect(TokenKind.As);
                    }

                    Expect(TokenKind.LParen);
                    Expr e = ParseExpr();
                    Expect(TokenKind.RParen);
                    string? word = null;
                    if (IsIdStart(Kind()))
                    {
                        word = Unquote(Advance());
                    }

                    // GENERATED ALWAYS are ordinary words to the grammar; they belong to the constraint when AS follows them
                    bool always = spelled;
                    // with a size list the words belong to the type: `t generated (19) AS (1)` has the type generated(19)
                    if (!spelled && type != null && type.Words.Length > 0 && type.Sizes.IsEmpty)
                    {
                        ImmutableArray<string> w = type.Words;
                        int keep = w.Length;
                        if (keep >= 2 && lastWord == TokenKind.Always && prevWord == TokenKind.Generated)
                        {
                            keep -= 2;
                            always = true;
                        }
                        else if (lastWord == TokenKind.Generated)
                        {
                            keep -= 1;
                        }

                        if (keep != w.Length)
                        {
                            lastWord = prevWord = TokenKind.Eof; // the words are taken; a later AS constraint must not strip more
                            type = keep == 0 && type.Sizes.IsEmpty ? null : type with { Words = w.RemoveRange(keep, w.Length - keep) };
                        }
                    }

                    c = new GeneratedConstraint(cname, e, word, always);
                    break;
                }

                default:
                    if (cname != null)
                    {
                        cons.Add(Done(new DanglingColumnConstraintName(cname), cname.Span.Start));
                    }

                    return Done(new ColumnDefinition(name, type, Builders<ColumnConstraint>.Finish(cons)), start);
            }

            cons.Add(Done(c, cname?.Span.Start ?? cs));
            cname = null;
        }
    }

    // DEFAULT ( expr ) | DEFAULT [+|-] term | DEFAULT word
    private Expr ParseDefaultValue()
    {
        Advance(); // DEFAULT
        Token t = Peek(0);
        int start = t.Start;
        switch (t.Kind)
        {
            case TokenKind.LParen:
            {
                Advance();
                Expr inner = ParseExpr();
                Expect(TokenKind.RParen);
                return Done(new ParenExpr(inner), start);
            }

            case TokenKind.Plus:
            case TokenKind.Minus:
            {
                Advance();
                Expr term = ParseDefaultTerm();
                return Done(new UnaryExpr(t.Kind == TokenKind.Plus ? UnaryOp.Plus : UnaryOp.Minus, term), start);
            }

            default:
                if (IsIdStart(t.Kind) && !IsTermKeyword(t.Kind))
                {
                    Advance();
                    return Done(new LiteralExpr(LiteralKind.Identifier, Unquote(t)), start);
                }

                return ParseDefaultTerm();
        }
    }

    private static bool IsTermKeyword(TokenKind k) => k is TokenKind.CurrentDate or TokenKind.CurrentTime or TokenKind.CurrentTimestamp;

    // term: NULL | FLOAT | BLOB | STRING | INTEGER | CTIME_KW
    private LiteralExpr ParseDefaultTerm()
    {
        Token t = Peek(0);
        LiteralKind kind;
        string text;
        switch (t.Kind)
        {
            case TokenKind.Null: kind = LiteralKind.Null; text = "NULL"; break;
            case TokenKind.Integer: kind = LiteralKind.Integer; text = _sql.Substring(t.Start, t.End - t.Start); break;
            case TokenKind.Float: kind = LiteralKind.Float; text = _sql.Substring(t.Start, t.End - t.Start); break;
            case TokenKind.String: kind = LiteralKind.String; text = Unquote(t); break;
            case TokenKind.Blob: kind = LiteralKind.Blob; text = _sql.Substring(t.Start + 2, t.End - t.Start - 3); break;
            case TokenKind.CurrentDate: kind = LiteralKind.CurrentDate; text = "CURRENT_DATE"; break;
            case TokenKind.CurrentTime: kind = LiteralKind.CurrentTime; text = "CURRENT_TIME"; break;
            case TokenKind.CurrentTimestamp: kind = LiteralKind.CurrentTimestamp; text = "CURRENT_TIMESTAMP"; break;
            default: throw Fail();
        }

        Advance();
        if ((t.Flags & TokenFlags.BadNumber) != 0)
        {
            Pending($"unrecognized token: \"{text}\"", -1);
        }

        return Done(new LiteralExpr(kind, text), t.Start);
    }

    // ---- ALTER TABLE ----------------------------------------------------------------------------------------------

    private Statement ParseAlter(int start)
    {
        Advance();
        Expect(TokenKind.Table);
        QualifiedName table = ParseQualifiedName();
        switch (Kind())
        {
            case TokenKind.Rename:
            {
                Advance();
                if (Accept(TokenKind.To))
                {
                    return Done(new RenameTableStatement(table, ParseNm()), start);
                }

                Accept(TokenKind.Column);
                Name old = ParseNm();
                Expect(TokenKind.To);
                return Done(new RenameColumnStatement(table, old, ParseNm()), start);
            }

            case TokenKind.Add:
            {
                Advance();
                if (Kind() is TokenKind.Constraint or TokenKind.Check)
                {
                    Name? cname = null;
                    if (Accept(TokenKind.Constraint))
                    {
                        cname = ParseNm();
                    }

                    Expect(TokenKind.Check);
                    Expect(TokenKind.LParen);
                    Expr check = ParseExpr();
                    Expect(TokenKind.RParen);
                    return Done(new AddCheckConstraintStatement(table, cname, check, ParseOnConflict()), start);
                }

                Accept(TokenKind.Column);
                return Done(new AddColumnStatement(table, ParseColumnDefinition()), start);
            }

            case TokenKind.Drop:
                Advance();
                if (Accept(TokenKind.Constraint))
                {
                    return Done(new DropConstraintStatement(table, ParseNm()), start);
                }

                Accept(TokenKind.Column);
                return Done(new DropColumnStatement(table, ParseNm()), start);
            case TokenKind.Alter:
            {
                Advance();
                Accept(TokenKind.Column);
                Name col = ParseNm();
                if (Accept(TokenKind.Drop))
                {
                    Expect(TokenKind.Not);
                    Expect(TokenKind.Null);
                    return Done(new AlterColumnNotNullStatement(table, col, false, null), start);
                }

                Expect(TokenKind.Set);
                Expect(TokenKind.Not);
                Expect(TokenKind.Null);
                return Done(new AlterColumnNotNullStatement(table, col, true, ParseOnConflict()), start);
            }

            default:
                throw Fail();
        }
    }

    // ---- CREATE TRIGGER -------------------------------------------------------------------------------------------

    private CreateTriggerStatement ParseCreateTrigger(int start, bool temp)
    {
        Advance(); // TRIGGER
        bool ine = ParseIfNotExists();
        QualifiedName name = ParseQualifiedName();
        TriggerTime time = TriggerTime.Default;
        if (Accept(TokenKind.Before))
        {
            time = TriggerTime.Before;
        }
        else if (Accept(TokenKind.After))
        {
            time = TriggerTime.After;
        }
        else if (Accept(TokenKind.Instead))
        {
            Expect(TokenKind.Of);
            time = TriggerTime.InsteadOf;
        }

        TriggerEvent ev;
        ImmutableArray<Name> updateCols = ImmutableArray<Name>.Empty;
        switch (Kind())
        {
            case TokenKind.Delete:
                Advance();
                ev = TriggerEvent.Delete;
                break;
            case TokenKind.Insert:
                Advance();
                ev = TriggerEvent.Insert;
                break;
            case TokenKind.Update:
                Advance();
                ev = TriggerEvent.Update;
                if (Accept(TokenKind.Of))
                {
                    ImmutableArray<Name>.Builder b = Builders<Name>.Rent();
                    do
                    {
                        b.Add(ParseNm());
                    }
                    while (Accept(TokenKind.Comma));
                    updateCols = Builders<Name>.Finish(b);
                }

                break;
            default:
                throw Fail();
        }

        Expect(TokenKind.On);
        QualifiedName table = ParseQualifiedName();
        bool forEachRow = false;
        if (Accept(TokenKind.For))
        {
            Expect(TokenKind.Each);
            Expect(TokenKind.Row);
            forEachRow = true;
        }

        Expr? when = Accept(TokenKind.When) ? ParseExpr() : null;
        Expect(TokenKind.Begin);
        ImmutableArray<Statement>.Builder body = Builders<Statement>.Rent();
        _inTrigger = true;
        do
        {
            body.Add(ParseTriggerCommand());
            Expect(TokenKind.Semi);
        }
        while (Kind() != TokenKind.End);
        _inTrigger = false;
        Advance(); // END
        return Done(new CreateTriggerStatement(temp, ine, name, time, ev, updateCols, table, forEachRow, when, Builders<Statement>.Finish(body)), start);
    }

    private Statement ParseTriggerCommand()
    {
        int start = Peek(0).Start;
        switch (Kind())
        {
            case TokenKind.Update:
                return ParseUpdate(null, start);
            case TokenKind.Insert:
            case TokenKind.Replace:
                return ParseInsert(null, start);
            case TokenKind.Delete:
                return ParseDelete(null, start);
            case TokenKind.Select:
            case TokenKind.Values:
                return ParseSelectStatement(null);
            case TokenKind.With:
            {
                WithClause with = ParseWith();
                if (Kind() is not (TokenKind.Select or TokenKind.Values))
                {
                    throw Fail();
                }

                return ParseSelectStatement(with);
            }

            default:
                throw Fail();
        }
    }
}
