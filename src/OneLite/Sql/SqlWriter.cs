using System.Collections.Immutable;
using System.Text;
using RecursiveAI.OneLite.Sql.Parsing;

namespace RecursiveAI.OneLite.Sql;

/// <summary>
/// Writes a syntax tree back as SQL text in one canonical form: upper-case keywords, every token separated by a blank, names quoted
/// only when they must be. Parsing the output gives a tree equal to the input (spans aside), so the text is also what
/// <c>ALTER TABLE</c> rewriting and error carets can be built on. <c>parenthesize</c> wraps every operator expression in
/// parentheses, which makes the result independent of operator precedence.
/// </summary>
internal sealed class SqlWriter
{
    private readonly StringBuilder _sb = new();
    private readonly bool _paren;

    private SqlWriter(bool parenthesize) => _paren = parenthesize;

    /// <summary>Canonical SQL text for a statement (no trailing semicolon).</summary>
    internal static string Write(Statement statement, bool parenthesize = false)
    {
        var w = new SqlWriter(parenthesize);
        w.Stmt(statement);
        return w._sb.ToString();
    }

    /// <summary>Canonical SQL text for an expression.</summary>
    internal static string Write(Expr expr, bool parenthesize = false)
    {
        var w = new SqlWriter(parenthesize);
        w.Ex(expr);
        return w._sb.ToString();
    }

    // ---- tokens ---------------------------------------------------------------------------------------------------

    private SqlWriter Put(string s)
    {
        _sb.Append(s);
        return this;
    }

    /// <summary>A name: bare when it is a plain identifier that is not a keyword, else double-quoted (lang_keywords).</summary>
    internal static string QuoteName(string name)
    {
        bool plain = name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_' || name[0] >= 0x80);
        for (int i = 1; plain && i < name.Length; i++)
        {
            char c = name[i];
            plain = char.IsAsciiLetterOrDigit(c) || c == '_' || c == '$' || c >= 0x80;
        }

        if (plain && !Lexer.TryKeyword(name, out _))
        {
            return name;
        }

        return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private SqlWriter Nm(Name? n) => Put(QuoteName(n!.Text));

    private void List<T>(ImmutableArray<T> items, Action<T> each, string sep = ", ")
    {
        for (int i = 0; i < items.Length; i++)
        {
            if (i > 0)
            {
                _sb.Append(sep);
            }

            each(items[i]);
        }
    }

    private void QName(QualifiedName q)
    {
        if (q.Schema != null)
        {
            Nm(q.Schema).Put(".");
        }

        Nm(q.Name);
    }

    private static string Conflict(ConflictAction a) => a switch
    {
        ConflictAction.Rollback => "ROLLBACK",
        ConflictAction.Abort => "ABORT",
        ConflictAction.Fail => "FAIL",
        ConflictAction.Ignore => "IGNORE",
        _ => "REPLACE",
    };

    private void OnConflict(ConflictAction? a)
    {
        if (a != null)
        {
            Put(" ON CONFLICT ").Put(Conflict(a.Value));
        }
    }

    // ---- expressions ----------------------------------------------------------------------------------------------

    private static string OpText(BinaryOp op) => op switch
    {
        BinaryOp.Or => "OR", BinaryOp.And => "AND", BinaryOp.Eq => "=", BinaryOp.Ne => "!=", BinaryOp.Is => "IS", BinaryOp.IsNot => "IS NOT",
        BinaryOp.Lt => "<", BinaryOp.Le => "<=", BinaryOp.Gt => ">", BinaryOp.Ge => ">=", BinaryOp.BitAnd => "&", BinaryOp.BitOr => "|",
        BinaryOp.Lshift => "<<", BinaryOp.Rshift => ">>", BinaryOp.Add => "+", BinaryOp.Sub => "-", BinaryOp.Mul => "*", BinaryOp.Div => "/",
        BinaryOp.Rem => "%", BinaryOp.Concat => "||", BinaryOp.Arrow => "->", _ => "->>",
    };

    private static bool IsBareTruthName(Expr e)
    {
        string? name = e switch { ColumnRef { Schema: null, Table: null } c => c.Column.Text, LiteralExpr { Kind: LiteralKind.Identifier } l => l.Text, _ => null };
        return name is not null && QuoteName(name) is { } q && (q.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || q.Equals("FALSE", StringComparison.OrdinalIgnoreCase));
    }

    private void Ex(Expr e)
    {
        // Trees the parser accepts nest up to a few thousand levels; a caller with a small stack gets an exception, not a crash.
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        bool wrap = _paren && e is UnaryExpr or BinaryExpr or CollateExpr or BetweenExpr or InListExpr or InSelectExpr or InTableExpr or LikeExpr or NullTestExpr;
        if (wrap)
        {
            Put("(");
        }

        switch (e)
        {
            case LiteralExpr l:
                Put(l.Kind switch
                {
                    LiteralKind.String => "'" + l.Text.Replace("'", "''", StringComparison.Ordinal) + "'",
                    LiteralKind.Blob => "x'" + l.Text + "'",
                    LiteralKind.Identifier => QuoteName(l.Text),
                    _ => l.Text,
                });
                break;
            case VariableExpr v:
                Put(v.Text);
                break;
            case ColumnRef c:
                if (c.Schema != null)
                {
                    Nm(c.Schema).Put(".");
                }

                if (c.Table != null)
                {
                    Nm(c.Table).Put(".");
                }

                Nm(c.Column);
                break;
            case UnaryExpr u:
                Put(u.Op switch { UnaryOp.Plus => "+ ", UnaryOp.Minus => "- ", UnaryOp.BitNot => "~ ", _ => "NOT " });
                Ex(u.Operand);
                break;
            case BinaryExpr { Op: BinaryOp.Is or BinaryOp.IsNot, Left: BinaryExpr { Op: BinaryOp.And, Right: LiteralExpr { Kind: LiteralKind.Integer, Text: "1" } } truth, Right: LiteralExpr { Kind: LiteralKind.Integer, Text: "1" or "0" } want } t:
                // The parser reads x IS [NOT] TRUE|FALSE as (x AND 1) IS [NOT] 1|0. Written as that, the unparenthesized text would read
                // back as x AND (1 IS [NOT] 1), so the truth test is written in the form it came from.
                Ex(truth.Left);
                Put(t.Op == BinaryOp.Is ? " IS " : " IS NOT ").Put(want.Text == "1" ? "TRUE" : "FALSE");
                break;
            case BinaryExpr b:
                Ex(b.Left);
                // `a IS NOT x` means IS NOT, so an IS whose right side is a NOT expression is written with DISTINCT FROM, which cannot be misread.
                // A bare name TRUE or FALSE on the right would read back as a truth test, so it takes the same form.
                if (b.Op is BinaryOp.Is or BinaryOp.IsNot && (b.Right is UnaryExpr { Op: UnaryOp.Not } || IsBareTruthName(b.Right)))
                {
                    Put(b.Op == BinaryOp.Is ? " IS NOT DISTINCT FROM " : " IS DISTINCT FROM ");
                }
                else
                {
                    Put(" ").Put(OpText(b.Op)).Put(" ");
                }

                Ex(b.Right);
                break;
            case CollateExpr c:
                Ex(c.Operand);
                Put(" COLLATE ").Nm(c.Collation);
                break;
            case CastExpr c:
                Put("CAST(");
                Ex(c.Operand);
                Put(" AS ");
                Type(c.Type);
                Put(")");
                break;
            case BetweenExpr b:
                Ex(b.Operand);
                Put(b.Negated ? " NOT BETWEEN " : " BETWEEN ");
                Ex(b.Low);
                Put(" AND ");
                Ex(b.High);
                break;
            case InListExpr i:
                Ex(i.Operand);
                Put(i.Negated ? " NOT IN (" : " IN (");
                List(i.Items, Ex);
                Put(")");
                break;
            case InSelectExpr i:
                Ex(i.Operand);
                Put(i.Negated ? " NOT IN (" : " IN (");
                Select(i.Select);
                Put(")");
                break;
            case InTableExpr i:
                Ex(i.Operand);
                Put(i.Negated ? " NOT IN " : " IN ");
                QName(i.Table);
                if (i.IsFunction)
                {
                    Put("(");
                    List(i.Args, Ex);
                    Put(")");
                }

                break;
            case ExistsExpr x:
                Put("EXISTS (");
                Select(x.Select);
                Put(")");
                break;
            case SubqueryExpr s:
                Put("(");
                Select(s.Select);
                Put(")");
                break;
            case CaseExpr c:
                Put("CASE");
                if (c.Operand != null)
                {
                    Put(" ");
                    Ex(c.Operand);
                }

                foreach (WhenClause w in c.Whens)
                {
                    Put(" WHEN ");
                    Ex(w.When);
                    Put(" THEN ");
                    Ex(w.Then);
                }

                if (c.Else != null)
                {
                    Put(" ELSE ");
                    Ex(c.Else);
                }

                Put(" END");
                break;
            case FunctionCall f:
                Call(f);
                break;
            case NullTestExpr n:
                Ex(n.Operand);
                Put(n.Test == NullTest.IsNull ? " ISNULL" : " NOT NULL");
                break;
            case LikeExpr l:
                Ex(l.Left);
                Put(l.Negated ? " NOT " : " ").Put(l.Op switch { LikeOp.Like => "LIKE", LikeOp.Glob => "GLOB", LikeOp.Regexp => "REGEXP", _ => "MATCH" }).Put(" ");
                Ex(l.Pattern);
                if (l.Escape != null)
                {
                    Put(" ESCAPE ");
                    Ex(l.Escape);
                }

                break;
            case RaiseExpr r:
                Put("RAISE(");
                if (r.Kind == RaiseKind.Ignore)
                {
                    Put("IGNORE");
                }
                else
                {
                    Put(r.Kind switch { RaiseKind.Rollback => "ROLLBACK", RaiseKind.Abort => "ABORT", _ => "FAIL" }).Put(", ");
                    Ex(r.Message!);
                }

                Put(")");
                break;
            case RowValueExpr r:
                Put("(");
                List(r.Items, Ex);
                Put(")");
                break;
            case ParenExpr p:
                Put("(");
                Ex(p.Inner);
                Put(")");
                break;
            default:
                throw new NotSupportedException(e.GetType().Name);
        }

        if (wrap)
        {
            Put(")");
        }
    }

    private void Type(TypeName t)
    {
        List(t.Words, w => Put(QuoteName(w)), " ");
        if (!t.Sizes.IsDefaultOrEmpty)
        {
            Put("(");
            List(t.Sizes, s => Put(s));
            Put(")");
        }
    }

    private void Call(FunctionCall f)
    {
        Nm(f.Name).Put("(");
        if (f.Star)
        {
            Put("*");
        }
        else
        {
            if (f.Quantifier != CallQuantifier.None)
            {
                Put(f.Quantifier == CallQuantifier.Distinct ? "DISTINCT " : "ALL ");
            }

            List(f.Args, Ex);
            if (!f.OrderBy.IsDefaultOrEmpty)
            {
                Put(" ORDER BY ");
                List(f.OrderBy, Term);
            }
        }

        Put(")");
        if (f.Filter != null)
        {
            Put(" FILTER (WHERE ");
            Ex(f.Filter);
            Put(")");
        }

        if (f.Over != null)
        {
            Put(" OVER ");
            if (f.Over.Named != null)
            {
                Nm(f.Over.Named);
            }
            else
            {
                Window(f.Over.Spec!);
            }
        }
    }

    private void Term(OrderingTerm t)
    {
        Ex(t.Expr);
        if (t.Order != SortOrder.None)
        {
            Put(t.Order == SortOrder.Asc ? " ASC" : " DESC");
        }

        if (t.Nulls != NullsOrder.None)
        {
            Put(t.Nulls == NullsOrder.First ? " NULLS FIRST" : " NULLS LAST");
        }
    }

    private void Window(WindowSpec w)
    {
        Put("(");
        bool any = false;
        if (w.BaseName != null)
        {
            Nm(w.BaseName);
            any = true;
        }

        if (!w.PartitionBy.IsDefaultOrEmpty)
        {
            Put(any ? " " : "").Put("PARTITION BY ");
            List(w.PartitionBy, Ex);
            any = true;
        }

        if (!w.OrderBy.IsDefaultOrEmpty)
        {
            Put(any ? " " : "").Put("ORDER BY ");
            List(w.OrderBy, Term);
            any = true;
        }

        if (w.Frame != null)
        {
            Put(any ? " " : "");
            FrameSpec f = w.Frame;
            Put(f.Unit switch { FrameUnit.Range => "RANGE ", FrameUnit.Rows => "ROWS ", _ => "GROUPS " });
            if (f.End != null)
            {
                Put("BETWEEN ");
                Bound(f.Start);
                Put(" AND ");
                Bound(f.End);
            }
            else
            {
                Bound(f.Start);
            }

            if (f.Exclude != FrameExclude.None)
            {
                Put(f.Exclude switch { FrameExclude.NoOthers => " EXCLUDE NO OTHERS", FrameExclude.CurrentRow => " EXCLUDE CURRENT ROW", FrameExclude.Group => " EXCLUDE GROUP", _ => " EXCLUDE TIES" });
            }
        }

        Put(")");
    }

    private void Bound(FrameBound b)
    {
        switch (b.Kind)
        {
            case FrameBoundKind.UnboundedPreceding: Put("UNBOUNDED PRECEDING"); break;
            case FrameBoundKind.UnboundedFollowing: Put("UNBOUNDED FOLLOWING"); break;
            case FrameBoundKind.CurrentRow: Put("CURRENT ROW"); break;
            case FrameBoundKind.Preceding: Ex(b.Offset!); Put(" PRECEDING"); break;
            default: Ex(b.Offset!); Put(" FOLLOWING"); break;
        }
    }

    // ---- SELECT ---------------------------------------------------------------------------------------------------

    private void WithClause(WithClause w)
    {
        Put(w.Recursive ? "WITH RECURSIVE " : "WITH ");
        List(w.Tables, t =>
        {
            Nm(t.Name);
            if (!t.Columns.IsDefaultOrEmpty)
            {
                Put("(");
                List(t.Columns, c => Nm(c));
                Put(")");
            }

            Put(" AS ");
            Put(t.Materialization switch { CteMaterialization.Materialized => "MATERIALIZED ", CteMaterialization.NotMaterialized => "NOT MATERIALIZED ", _ => "" });
            Put("(");
            Select(t.Select);
            Put(")");
        });
        Put(" ");
    }

    private void Select(SelectStatement s)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        if (s.With != null)
        {
            WithClause(s.With);
        }

        for (int i = 0; i < s.Cores.Length; i++)
        {
            if (i > 0)
            {
                Put(s.Operators[i - 1] switch { CompoundOperator.Union => " UNION ", CompoundOperator.UnionAll => " UNION ALL ", CompoundOperator.Intersect => " INTERSECT ", _ => " EXCEPT " });
            }

            Core(s.Cores[i]);
        }
    }

    private void Core(SelectCore core)
    {
        if (core is ValuesCore v)
        {
            Put("VALUES ");
            List(v.Rows, r =>
            {
                Put("(");
                List(r, Ex);
                Put(")");
            });
            return;
        }

        var s = (SimpleSelect)core;
        Put("SELECT ");
        if (s.Quantifier != SelectQuantifier.None)
        {
            Put(s.Quantifier == SelectQuantifier.Distinct ? "DISTINCT " : "ALL ");
        }

        ResultColumns(s.Columns);
        if (s.From != null)
        {
            Put(" FROM ");
            From(s.From);
        }

        if (s.Where != null)
        {
            Put(" WHERE ");
            Ex(s.Where);
        }

        if (!s.GroupBy.IsDefaultOrEmpty)
        {
            Put(" GROUP BY ");
            List(s.GroupBy, Ex);
        }

        if (s.Having != null)
        {
            Put(" HAVING ");
            Ex(s.Having);
        }

        if (!s.Windows.IsDefaultOrEmpty)
        {
            Put(" WINDOW ");
            List(s.Windows, w =>
            {
                Nm(w.Name).Put(" AS ");
                Window(w.Spec);
            });
        }

        if (!s.OrderBy.IsDefaultOrEmpty)
        {
            Put(" ORDER BY ");
            List(s.OrderBy, Term);
        }

        Limit(s.Limit);
    }

    private void Limit(LimitClause? l)
    {
        if (l == null)
        {
            return;
        }

        Put(" LIMIT ");
        Ex(l.Limit);
        if (l.Offset != null)
        {
            Put(" OFFSET ");
            Ex(l.Offset);
        }
    }

    private void ResultColumns(ImmutableArray<ResultColumn> cols)
        => List(cols, c =>
        {
            switch (c)
            {
                case StarColumn s:
                    if (s.Table != null)
                    {
                        Nm(s.Table).Put(".");
                    }

                    Put("*");
                    break;
                case ExprColumn e:
                    Ex(e.Expr);
                    if (e.Alias != null)
                    {
                        Put(" AS ").Nm(e.Alias);
                    }

                    break;
            }
        });

    private void From(FromClause f)
    {
        for (int i = 0; i < f.Items.Length; i++)
        {
            FromItem it = f.Items[i];
            if (it.Join != null)
            {
                if ((it.Join.Flags & JoinFlags.Comma) != 0)
                {
                    Put(", ");
                }
                else
                {
                    Put(" ");
                    for (int w = 0; w < it.Join.Words.Length; w++)
                    {
                        string word = it.Join.Words[w];
                        Put(w == 0 || Lexer.TryKeyword(word, out TokenKind k) && Lexer.IsJoinKeyword(k) ? word : QuoteName(word)).Put(" ");
                    }

                    Put("JOIN ");
                }
            }

            Source(it.Source);
            if (it.On != null)
            {
                Put(" ON ");
                Ex(it.On);
            }
            else if (!it.UsingColumns.IsDefaultOrEmpty)
            {
                Put(" USING (");
                List(it.UsingColumns, c => Nm(c));
                Put(")");
            }
        }
    }

    private void Alias(Name? a)
    {
        if (a != null)
        {
            Put(" AS ").Nm(a);
        }
    }

    private void Source(TableSource src)
    {
        switch (src)
        {
            case NamedTable t:
                QName(t.Name);
                Alias(t.Alias);
                Hint(t.Index);
                break;
            case TableFunction t:
                QName(t.Name);
                Put("(");
                List(t.Args, Ex);
                Put(")");
                Alias(t.Alias);
                break;
            case SubquerySource q:
                Put("(");
                Select(q.Select);
                Put(")");
                Alias(q.Alias);
                break;
            case ParenFrom p:
                Put("(");
                From(p.From);
                Put(")");
                Alias(p.Alias);
                break;
        }
    }

    private void Hint(IndexHint? h)
    {
        if (h == null)
        {
            return;
        }

        if (h.IndexName == null)
        {
            Put(" NOT INDEXED");
        }
        else
        {
            Put(" INDEXED BY ").Nm(h.IndexName);
        }
    }

    // ---- statements -----------------------------------------------------------------------------------------------

    private void Stmt(Statement st)
    {
        switch (st)
        {
            case ExplainStatement e:
                Put(e.QueryPlan ? "EXPLAIN QUERY PLAN " : "EXPLAIN ");
                Stmt(e.Inner);
                break;
            case SelectStatement s:
                Select(s);
                break;
            case InsertStatement i:
                Insert(i);
                break;
            case UpdateStatement u:
                Update(u);
                break;
            case DeleteStatement d:
                if (d.With != null)
                {
                    WithClause(d.With);
                }

                Put("DELETE FROM ");
                Target(d.Target);
                Where(d.Where);
                Returning(d.Returning);
                break;
            case CreateTableStatement c:
                CreateTable(c);
                break;
            case CreateIndexStatement c:
                Put(c.Unique ? "CREATE UNIQUE INDEX " : "CREATE INDEX ").Put(c.IfNotExists ? "IF NOT EXISTS " : "");
                QName(c.Name);
                Put(" ON ").Nm(c.Table).Put("(");
                List(c.Columns, Term);
                Put(")");
                Where(c.Where);
                break;
            case CreateViewStatement c:
                Put(c.Temporary ? "CREATE TEMP VIEW " : "CREATE VIEW ").Put(c.IfNotExists ? "IF NOT EXISTS " : "");
                QName(c.Name);
                if (!c.Columns.IsDefaultOrEmpty)
                {
                    Put("(");
                    List(c.Columns, n => Nm(n));
                    Put(")");
                }

                Put(" AS ");
                Select(c.Select);
                break;
            case CreateTriggerStatement c:
                Trigger(c);
                break;
            case CreateVirtualTableStatement c:
                Put("CREATE VIRTUAL TABLE ").Put(c.IfNotExists ? "IF NOT EXISTS " : "");
                QName(c.Name);
                Put(" USING ").Nm(c.Module);
                if (!c.Arguments.IsDefaultOrEmpty)
                {
                    Put("(");
                    List(c.Arguments, a => Put(a));
                    Put(")");
                }

                break;
            case DropStatement d:
                Put(d.Kind switch { DropKind.Table => "DROP TABLE ", DropKind.View => "DROP VIEW ", DropKind.Index => "DROP INDEX ", _ => "DROP TRIGGER " }).Put(d.IfExists ? "IF EXISTS " : "");
                QName(d.Name);
                break;
            case AlterTableStatement a:
                Alter(a);
                break;
            case AttachStatement a:
                Put("ATTACH ");
                Ex(a.Database);
                Put(" AS ");
                Ex(a.SchemaName);
                if (a.Key != null)
                {
                    Put(" KEY ");
                    Ex(a.Key);
                }

                break;
            case DetachStatement d:
                Put("DETACH ");
                Ex(d.SchemaName);
                break;
            case BeginStatement b:
                Put(b.Mode switch { BeginMode.Deferred => "BEGIN DEFERRED", BeginMode.Immediate => "BEGIN IMMEDIATE", BeginMode.Exclusive => "BEGIN EXCLUSIVE", BeginMode.Concurrent => "BEGIN CONCURRENT", _ => "BEGIN" });
                Txn(b.TransactionName);
                break;
            case CommitStatement c:
                Put("COMMIT");
                Txn(c.TransactionName);
                break;
            case RollbackStatement r:
                Put("ROLLBACK");
                Txn(r.TransactionName);
                if (r.ToSavepoint != null)
                {
                    Put(" TO ").Nm(r.ToSavepoint);
                }

                break;
            case SavepointStatement s:
                Put("SAVEPOINT ").Nm(s.Savepoint);
                break;
            case ReleaseStatement r:
                Put("RELEASE ").Nm(r.Savepoint);
                break;
            case PragmaStatement p:
                Pragma(p);
                break;
            case VacuumStatement v:
                Put("VACUUM");
                if (v.Schema != null)
                {
                    Put(" ").Nm(v.Schema);
                }

                if (v.Into != null)
                {
                    Put(" INTO ");
                    Ex(v.Into);
                }

                break;
            case ReindexStatement r:
                Put("REINDEX");
                if (r.Target != null)
                {
                    Put(" ");
                    QName(r.Target);
                }

                break;
            case AnalyzeStatement a:
                Put("ANALYZE");
                if (a.Target != null)
                {
                    Put(" ");
                    QName(a.Target);
                }

                break;
            default:
                throw new NotSupportedException(st.GetType().Name);
        }
    }

    private void Txn(Name? n)
    {
        if (n != null)
        {
            Put(" TRANSACTION ").Nm(n);
        }
    }

    private void Pragma(PragmaStatement p)
    {
        Put("PRAGMA ");
        if (p.Schema != null)
        {
            Nm(p.Schema).Put(".");
        }

        Nm(p.Name);
        if (p.Value == null)
        {
            return;
        }

        string v = p.Value.Kind switch
        {
            PragmaValueKind.Number => p.Value.Text,
            PragmaValueKind.String => "'" + p.Value.Text.Replace("'", "''", StringComparison.Ordinal) + "'",
            PragmaValueKind.Keyword => p.Value.Text,
            _ => QuoteName(p.Value.Text),
        };
        Put(p.Parenthesized ? "(" + v + ")" : " = " + v);
    }

    private void Target(NamedTable t)
    {
        QName(t.Name);
        Alias(t.Alias);
        Hint(t.Index);
    }

    private void Where(Expr? e)
    {
        if (e != null)
        {
            Put(" WHERE ");
            Ex(e);
        }
    }

    private void Returning(ReturningClause? r)
    {
        if (r != null)
        {
            Put(" RETURNING ");
            ResultColumns(r.Columns);
        }
    }

    private void SetList(ImmutableArray<SetClause> sets)
        => List(sets, s =>
        {
            if (s.Columns.Length == 1)
            {
                Nm(s.Columns[0]);
            }
            else
            {
                Put("(");
                List(s.Columns, c => Nm(c));
                Put(")");
            }

            Put(" = ");
            Ex(s.Value);
        });

    private void Insert(InsertStatement i)
    {
        if (i.With != null)
        {
            WithClause(i.With);
        }

        Put(i.Verb == InsertVerb.Replace ? "REPLACE INTO " : i.OrAction == null ? "INSERT INTO " : "INSERT OR " + Conflict(i.OrAction.Value) + " INTO ");
        QName(i.Table);
        Alias(i.Alias);
        if (!i.Columns.IsDefaultOrEmpty)
        {
            Put("(");
            List(i.Columns, c => Nm(c));
            Put(")");
        }

        Put(" ");
        if (i.Source.Select == null)
        {
            Put("DEFAULT VALUES");
        }
        else
        {
            Select(i.Source.Select);
        }

        foreach (UpsertClause u in i.Upserts)
        {
            Put(" ON CONFLICT");
            if (!u.Target.IsDefaultOrEmpty)
            {
                Put(" (");
                List(u.Target, Term);
                Put(")");
                Where(u.TargetWhere);
            }

            if (u.DoNothing)
            {
                Put(" DO NOTHING");
            }
            else
            {
                Put(" DO UPDATE SET ");
                SetList(u.Sets);
                Where(u.UpdateWhere);
            }
        }

        Returning(i.Returning);
    }

    private void Update(UpdateStatement u)
    {
        if (u.With != null)
        {
            WithClause(u.With);
        }

        Put(u.OrAction == null ? "UPDATE " : "UPDATE OR " + Conflict(u.OrAction.Value) + " ");
        Target(u.Target);
        Put(" SET ");
        SetList(u.Sets);
        if (u.From != null)
        {
            Put(" FROM ");
            From(u.From);
        }

        Where(u.Where);
        Returning(u.Returning);
    }

    private void Trigger(CreateTriggerStatement c)
    {
        Put(c.Temporary ? "CREATE TEMP TRIGGER " : "CREATE TRIGGER ").Put(c.IfNotExists ? "IF NOT EXISTS " : "");
        QName(c.Name);
        Put(c.Time switch { TriggerTime.Before => " BEFORE", TriggerTime.After => " AFTER", TriggerTime.InsteadOf => " INSTEAD OF", _ => "" });
        Put(c.Event switch { TriggerEvent.Delete => " DELETE", TriggerEvent.Insert => " INSERT", _ => " UPDATE" });
        if (!c.UpdateColumns.IsDefaultOrEmpty)
        {
            Put(" OF ");
            List(c.UpdateColumns, n => Nm(n));
        }

        Put(" ON ");
        QName(c.Table);
        if (c.ForEachRow)
        {
            Put(" FOR EACH ROW");
        }

        if (c.When != null)
        {
            Put(" WHEN ");
            Ex(c.When);
        }

        Put(" BEGIN ");
        foreach (Statement s in c.Body)
        {
            Stmt(s);
            Put("; ");
        }

        Put("END");
    }

    private void Alter(AlterTableStatement a)
    {
        Put("ALTER TABLE ");
        QName(a.Table);
        switch (a)
        {
            case RenameTableStatement r:
                Put(" RENAME TO ").Nm(r.NewName);
                break;
            case RenameColumnStatement r:
                Put(" RENAME COLUMN ").Nm(r.OldName).Put(" TO ").Nm(r.NewName);
                break;
            case AddColumnStatement c:
                Put(" ADD COLUMN ");
                ColumnDef(c.Column);
                break;
            case DropColumnStatement d:
                Put(" DROP COLUMN ").Nm(d.Column);
                break;
            case AddCheckConstraintStatement c:
                Put(" ADD ");
                if (c.ConstraintName != null)
                {
                    Put("CONSTRAINT ").Nm(c.ConstraintName).Put(" ");
                }

                Put("CHECK (");
                Ex(c.Check);
                Put(")");
                OnConflict(c.OnConflict);
                break;
            case DropConstraintStatement d:
                Put(" DROP CONSTRAINT ").Nm(d.ConstraintName);
                break;
            case AlterColumnNotNullStatement n:
                Put(" ALTER COLUMN ").Nm(n.Column).Put(n.SetNotNull ? " SET NOT NULL" : " DROP NOT NULL");
                OnConflict(n.OnConflict);
                break;
        }
    }

    // ---- CREATE TABLE ---------------------------------------------------------------------------------------------

    private void CreateTable(CreateTableStatement c)
    {
        Put(c.Temporary ? "CREATE TEMP TABLE " : "CREATE TABLE ").Put(c.IfNotExists ? "IF NOT EXISTS " : "");
        QName(c.Name);
        if (c.AsSelect != null)
        {
            Put(" AS ");
            Select(c.AsSelect);
            return;
        }

        Put("(");
        List(c.Columns, ColumnDef);
        foreach (TableConstraint t in c.Constraints)
        {
            Put(", ");
            TableCons(t);
        }

        Put(")");
        if (!c.Options.IsDefaultOrEmpty)
        {
            Put(" ");
            List(c.Options, o => Put(o.Without ? "WITHOUT " + QuoteName(o.Name) : QuoteName(o.Name)));
        }
    }

    private void ColumnDef(ColumnDefinition d)
    {
        Nm(d.Name);
        if (d.Type != null)
        {
            Put(" ");
            Type(d.Type);
        }

        foreach (ColumnConstraint c in d.Constraints)
        {
            Put(" ");
            if (c.ConstraintName != null)
            {
                Put("CONSTRAINT ").Nm(c.ConstraintName).Put(" ");
            }

            switch (c)
            {
                case PrimaryKeyColumnConstraint p:
                    Put("PRIMARY KEY");
                    if (p.Order != SortOrder.None)
                    {
                        Put(p.Order == SortOrder.Asc ? " ASC" : " DESC");
                    }

                    OnConflict(p.OnConflict);
                    if (p.Autoincrement)
                    {
                        Put(" AUTOINCREMENT");
                    }

                    break;
                case NotNullConstraint n:
                    Put("NOT NULL");
                    OnConflict(n.OnConflict);
                    break;
                case NullConstraint n:
                    Put("NULL");
                    OnConflict(n.OnConflict);
                    break;
                case UniqueColumnConstraint u:
                    Put("UNIQUE");
                    OnConflict(u.OnConflict);
                    break;
                case CheckColumnConstraint k:
                    Put("CHECK (");
                    Ex(k.Check);
                    Put(")");
                    break;
                case DefaultConstraint df:
                    Put("DEFAULT ");
                    Ex(df.Value);
                    break;
                case CollateConstraint co:
                    Put("COLLATE ").Nm(co.Collation);
                    break;
                case ReferencesConstraint r:
                    Put("REFERENCES ");
                    ForeignKey(r.Clause);
                    break;
                case DeferralConstraint df:
                    Deferral(df.Deferral);
                    break;
                case GeneratedConstraint g:
                    Put(g.Always ? "GENERATED ALWAYS AS (" : "AS (");
                    Ex(g.Value);
                    Put(")");
                    if (g.StorageWord != null)
                    {
                        Put(" ").Put(QuoteName(g.StorageWord));
                    }

                    break;
                case DanglingColumnConstraintName:
                    // the CONSTRAINT keyword and name were written above
                    _sb.Length--; // drop the blank that precedes the next token
                    break;
            }
        }
    }

    private void Deferral(Deferral d)
    {
        Put(d.Not ? "NOT DEFERRABLE" : "DEFERRABLE");
        if (d.Initially != null)
        {
            Put(d.Initially == DeferMode.Deferred ? " INITIALLY DEFERRED" : " INITIALLY IMMEDIATE");
        }
    }

    private void ForeignKey(ForeignKeyClause f)
    {
        Nm(f.Table);
        if (!f.Columns.IsDefaultOrEmpty)
        {
            Put("(");
            IndexedColumns(f.Columns);
            Put(")");
        }

        foreach (ForeignKeyRule r in f.Rules)
        {
            if (r.Kind == ForeignKeyRuleKind.Match)
            {
                Put(" MATCH ").Nm(r.MatchName);
                continue;
            }

            Put(r.Kind == ForeignKeyRuleKind.OnDelete ? " ON DELETE " : " ON UPDATE ");
            Put(r.Action switch { ForeignKeyAction.SetNull => "SET NULL", ForeignKeyAction.SetDefault => "SET DEFAULT", ForeignKeyAction.Cascade => "CASCADE", ForeignKeyAction.Restrict => "RESTRICT", _ => "NO ACTION" });
        }
    }

    private void IndexedColumns(ImmutableArray<IndexedColumn> cols)
        => List(cols, c =>
        {
            Nm(c.Name);
            if (c.Collation != null)
            {
                Put(" COLLATE ").Nm(c.Collation);
            }

            if (c.Order != SortOrder.None)
            {
                Put(c.Order == SortOrder.Asc ? " ASC" : " DESC");
            }
        });

    private void TableCons(TableConstraint t)
    {
        if (t.ConstraintName != null)
        {
            Put("CONSTRAINT ").Nm(t.ConstraintName).Put(" ");
        }

        switch (t)
        {
            case PrimaryKeyTableConstraint p:
                Put("PRIMARY KEY (");
                List(p.Columns, Term);
                Put(p.Autoincrement ? " AUTOINCREMENT)" : ")");
                OnConflict(p.OnConflict);
                break;
            case UniqueTableConstraint u:
                Put("UNIQUE (");
                List(u.Columns, Term);
                Put(")");
                OnConflict(u.OnConflict);
                break;
            case CheckTableConstraint k:
                Put("CHECK (");
                Ex(k.Check);
                Put(")");
                OnConflict(k.OnConflict);
                break;
            case ForeignKeyTableConstraint f:
                Put("FOREIGN KEY (");
                IndexedColumns(f.Columns);
                Put(") REFERENCES ");
                ForeignKey(f.Clause);
                if (f.Deferral != null)
                {
                    Put(" ");
                    Deferral(f.Deferral);
                }

                break;
            case DanglingTableConstraintName:
                _sb.Length--; // the name was written above; drop the trailing blank
                break;
        }
    }
}
