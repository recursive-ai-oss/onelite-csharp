using System.Collections.Immutable;

namespace RecursiveAI.OneLite.Sql;

internal abstract record Statement : SqlNode;

internal enum CompoundOperator { Union, UnionAll, Intersect, Except }

/// <summary>
/// [WITH ..] core (compound-op core)*. Each core is a plain SELECT or a VALUES list. In the SQLite grammar ORDER BY and LIMIT belong to
/// each plain SELECT (a VALUES core has none), so they hang on <see cref="SimpleSelect"/>; the dialect rejects them later
/// unless they sit on the last core.
/// </summary>
internal sealed record SelectStatement(WithClause? With, ImmutableArray<SelectCore> Cores, ImmutableArray<CompoundOperator> Operators) : Statement;

internal abstract record SelectCore : SqlNode;

internal enum SelectQuantifier { None, Distinct, All }

internal sealed record SimpleSelect(
    SelectQuantifier Quantifier,
    ImmutableArray<ResultColumn> Columns,
    FromClause? From,
    Expr? Where,
    ImmutableArray<Expr> GroupBy,
    Expr? Having,
    ImmutableArray<WindowDefinition> Windows,
    ImmutableArray<OrderingTerm> OrderBy,
    LimitClause? Limit) : SelectCore;

internal sealed record ValuesCore(ImmutableArray<ImmutableArray<Expr>> Rows) : SelectCore;

internal abstract record ResultColumn : SqlNode;

/// <summary><c>*</c> or <c>table.*</c>.</summary>
internal sealed record StarColumn(Name? Table) : ResultColumn;

internal sealed record ExprColumn(Expr Expr, Name? Alias) : ResultColumn;

[Flags]
internal enum JoinFlags { None = 0, Comma = 1, Natural = 2, Left = 4, Right = 8, Full = 16, Outer = 32, Inner = 64, Cross = 128, Unknown = 256 }

/// <summary>
/// The join operator before a table: a comma, or JOIN with up to three type words. <c>Words</c> are the words as written (upper-cased);
/// <c>Flags</c> is their decoding, with <see cref="JoinFlags.Unknown"/> for a combination SQLite rejects after parsing.
/// </summary>
internal sealed record JoinOperator(JoinFlags Flags, ImmutableArray<string> Words) : SqlNode;

/// <summary>One table in a FROM clause with the join that attaches it and its ON or USING constraint.</summary>
internal sealed record FromItem(JoinOperator? Join, TableSource Source, Expr? On, ImmutableArray<Name> UsingColumns) : SqlNode;

internal sealed record FromClause(ImmutableArray<FromItem> Items) : SqlNode;

internal abstract record TableSource : SqlNode;

/// <summary>INDEXED BY name, or NOT INDEXED when <c>IndexName</c> is null.</summary>
internal sealed record IndexHint(Name? IndexName) : SqlNode;

internal sealed record NamedTable(QualifiedName Name, Name? Alias, IndexHint? Index) : TableSource;

internal sealed record TableFunction(QualifiedName Name, ImmutableArray<Expr> Args, Name? Alias) : TableSource;

internal sealed record SubquerySource(SelectStatement Select, Name? Alias) : TableSource;

/// <summary>( join-clause ), a parenthesised group of tables.</summary>
internal sealed record ParenFrom(FromClause From, Name? Alias) : TableSource;
