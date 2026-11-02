using System.Collections.Immutable;

namespace RecursiveAI.OneLite.Sql;

internal enum InsertVerb { Insert, Replace }

/// <summary>The row source of an INSERT: a SELECT or VALUES, or <c>DEFAULT VALUES</c> (<c>Select</c> null).</summary>
internal sealed record InsertSource(SelectStatement? Select) : SqlNode;

/// <summary>col = expr, or (col, col, ..) = expr.</summary>
internal sealed record SetClause(ImmutableArray<Name> Columns, Expr Value) : SqlNode;

/// <summary>
/// ON CONFLICT [(target) [WHERE ..]] DO NOTHING | DO UPDATE SET .. [WHERE ..]. <c>Target</c> is empty when the clause has no conflict target.
/// </summary>
internal sealed record UpsertClause(ImmutableArray<OrderingTerm> Target, Expr? TargetWhere, bool DoNothing, ImmutableArray<SetClause> Sets, Expr? UpdateWhere) : SqlNode;

internal sealed record InsertStatement(
    WithClause? With,
    InsertVerb Verb,
    ConflictAction? OrAction,
    QualifiedName Table,
    Name? Alias,
    ImmutableArray<Name> Columns,
    InsertSource Source,
    ImmutableArray<UpsertClause> Upserts,
    ReturningClause? Returning) : Statement;

internal sealed record UpdateStatement(
    WithClause? With,
    ConflictAction? OrAction,
    NamedTable Target,
    ImmutableArray<SetClause> Sets,
    FromClause? From,
    Expr? Where,
    ReturningClause? Returning) : Statement;

internal sealed record DeleteStatement(WithClause? With, NamedTable Target, Expr? Where, ReturningClause? Returning) : Statement;
