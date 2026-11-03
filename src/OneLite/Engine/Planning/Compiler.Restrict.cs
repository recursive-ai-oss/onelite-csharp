using System.Linq;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Storage;

namespace RecursiveAI.OneLite.Planning;

// Checks a CREATE TABLE or CREATE INDEX makes while it still has the statement in hand (lang_createtable, lang_createindex): the expressions of
// CHECK constraints, generated columns and indexes are resolved against the new object at once, so that a mistake fails the CREATE and not the first
// write, and a few constructs are refused there outright.
internal sealed partial class Compiler
{
    /// <summary>What an expression being checked at CREATE time may not contain; <see cref="What"/> is the phrase the messages end with.</summary>
    private sealed class ExprRestriction
    {
        internal string What = "";
        internal bool NoDot;
        internal bool NoRowid;
        internal bool NoNonDeterministic;

        /// <summary>Only name resolution is checked: errors of later code generation are left to the first use.</summary>
        internal bool ResolveOnly;

        /// <summary>An unknown collating sequence is not an error (the reference looks it up when the comparison runs).</summary>
        internal bool LenientCollation;

        /// <summary>Only a bind parameter is reported (the reference refuses those in a generated column before it resolves any CHECK).</summary>
        internal bool ParametersOnly;
    }

    private ExprRestriction? _restrict;

    private OneLiteException Prohibited(string what, SqlNode at, bool position = true)
    {
        string message = what + " prohibited in " + _restrict!.What;
        return position ? ErrAt(message, at) : Err(message);
    }

    /// <summary>Applied where code generation meets a node a restricted expression may not hold; does nothing outside such an expression.</summary>
    private void CheckRestricted(Expr e)
    {
        if (_restrict is null)
        {
            return;
        }

        switch (e)
        {
            case VariableExpr v:
                throw Prohibited("parameters", v);
            case InSelectExpr or InTableExpr:
                throw Prohibited("subqueries", e);
            case ExistsExpr or SubqueryExpr:
                // The reference gives no position for these two (black-box).
                throw Prohibited("subqueries", e, false);
            case ColumnRef { Table: { } q } cr when _restrict.NoDot && AsciiComparer.Instance.Equals(q.Text, _alias):
                throw Prohibited("the \".\" operator", cr);
            case FunctionCall f when _restrict.NoNonDeterministic && _cat.Owner?.IsNonDeterministic(f.Name.Text) == true:
                throw ErrAt("non-deterministic functions prohibited in " + _restrict.What, f);
            case LiteralExpr { Kind: LiteralKind.CurrentTime or LiteralKind.CurrentDate or LiteralKind.CurrentTimestamp } when _restrict.NoNonDeterministic:
                throw ErrAt("non-deterministic functions prohibited in " + _restrict.What, e);
        }
    }

    private void ResolveRestricted(Expr e, ExprRestriction restriction)
    {
        _restrict = restriction;
        bool savedLenient = t_lenientCollation;
        t_lenientCollation = restriction.LenientCollation;
        try
        {
            DryRun(() => CompileInto(e, NewReg()));
        }
        catch (OneLiteException ex) when (restriction.ParametersOnly ? !ex.Message.StartsWith("parameters prohibited", StringComparison.Ordinal) : restriction.ResolveOnly && !IsResolveTimeError(ex))
        {
            // The reference only resolves names here; whatever its code generation refuses is refused when a row is written, which is what happens here too.
        }
        finally
        {
            t_lenientCollation = savedLenient;
            _restrict = null;
        }
    }

    [ThreadStatic]
    private static bool t_lenientCollation;

    // Marks an error the reference raises while resolving names (so at CREATE for a CHECK), though this compiler finds it during code generation.
    private const string ResolveTimeKey = "resolve-time";

    private static bool IsResolveTimeError(OneLiteException ex)
        => ex.Data.Contains(ResolveTimeKey) || IsResolveTimeMessage(ex.Message);

    private static bool IsResolveTimeMessage(string message)
        => message.StartsWith("no such column", StringComparison.Ordinal) || message.StartsWith("no such function", StringComparison.Ordinal)
            || message.StartsWith("wrong number of arguments", StringComparison.Ordinal) || message.StartsWith("misuse of", StringComparison.Ordinal)
            || message.StartsWith("ambiguous column name", StringComparison.Ordinal) || message.StartsWith("FILTER may not", StringComparison.Ordinal)
            || message.StartsWith("ORDER BY may not", StringComparison.Ordinal) || message.Contains("prohibited in", StringComparison.Ordinal);

    /// <summary>The columns of a PRIMARY KEY or UNIQUE constraint: plain column names, with a COLLATE that exists.</summary>
    private void CheckKeyColumns(TableInfo built, IEnumerable<OrderingTerm> terms, bool primaryKey)
    {
        foreach (OrderingTerm term in terms)
        {
            Expr e = term.Expr;
            while (e is CollateExpr ce)
            {
                ParseColl(ce.Collation.Text);
                e = ce.Operand;
            }

            if (e is not ColumnRef { Schema: null, Table: null } cr)
            {
                throw Err("expressions prohibited in PRIMARY KEY and UNIQUE constraints");
            }

            int col = built.FindColumn(cr.Column.Text);
            if (col < 0)
            {
                throw ErrAt("no such column: " + cr.Column.Text, cr);
            }

            if (primaryKey && built.Columns[col].Generated != GeneratedKind.None)
            {
                throw Err("generated columns cannot be part of the PRIMARY KEY");
            }
        }
    }

    // Foreign keys are checked as far as the reference does when the table is created: the child column names and the number of columns on each
    // side. The parent table and its columns are looked up when a row is written.
    private static void CheckForeignKey(TableInfo built, IReadOnlyList<IndexedColumn> childColumns, ForeignKeyClause clause)
    {
        if (childColumns.Count > 0 && !clause.Columns.IsDefaultOrEmpty && childColumns.Count != clause.Columns.Length)
        {
            throw Err("number of columns in foreign key does not match the number of columns in the referenced table");
        }

        foreach (IndexedColumn c in childColumns)
        {
            if (built.FindColumn(c.Name.Text) < 0)
            {
                throw Err("unknown column \"" + c.Name.Text + "\" in foreign key definition");
            }
        }
    }

    private void CheckTableDefinition(CreateTableStatement ct, TableInfo built)
    {
        foreach (ColumnDefinition cd in ct.Columns)
        {
            bool generated = cd.Constraints.Any(k => k is GeneratedConstraint);
            foreach (ColumnConstraint k in cd.Constraints)
            {
                if (generated && k is DefaultConstraint)
                {
                    throw Err("cannot use DEFAULT on a generated column");
                }

                if (generated && k is PrimaryKeyColumnConstraint)
                {
                    throw Err("generated columns cannot be part of the PRIMARY KEY");
                }

                if (k is ReferencesConstraint { Clause: { Columns.Length: > 1 } rc })
                {
                    throw ErrAt("foreign key on " + cd.Name.Text + " should reference only one column of table " + rc.Table.Text, rc.Table);
                }
            }
        }

        foreach (TableConstraint tc in ct.Constraints)
        {
            switch (tc)
            {
                case PrimaryKeyTableConstraint pk:
                    CheckKeyColumns(built, pk.Columns, true);
                    break;
                case UniqueTableConstraint uq:
                    CheckKeyColumns(built, uq.Columns, false);
                    break;
                case ForeignKeyTableConstraint fk:
                    CheckForeignKey(built, fk.Columns, fk.Clause);
                    break;
            }
        }

        // CHECK constraints first, then generated columns (black-box: when both are wrong the CHECK is the one reported).
        _table = built;
        _alias = built.Name;
        _tableCursor = 0;
        SetColumns(true);
        try
        {
            foreach (ColumnInfo c in built.Columns)
            {
                if (c.Generated != GeneratedKind.None)
                {
                    ResolveRestricted(c.GeneratedExpr!, new ExprRestriction { What = "generated columns", ResolveOnly = true, ParametersOnly = true, LenientCollation = true });
                }
            }

            foreach (CheckInfo ck in built.Checks)
            {
                ResolveRestricted(ck.Expr, new ExprRestriction { What = "CHECK constraints", ResolveOnly = true, LenientCollation = true });
            }

            foreach (ColumnInfo c in built.Columns)
            {
                if (c.Generated != GeneratedKind.None)
                {
                    ResolveRestricted(c.GeneratedExpr!, new ExprRestriction { What = "generated columns", NoDot = true, NoRowid = true, NoNonDeterministic = true, ResolveOnly = true, LenientCollation = true });
                }
            }
        }
        finally
        {
            SetColumns(false);
            _table = null;
        }
    }
}
