using System.Collections.Immutable;

namespace RecursiveAI.OneLite.Sql;

/// <summary>A half-open range of UTF-16 indexes into the SQL text.</summary>
internal readonly record struct SourceSpan(int Start, int End)
{
    internal int Length => End - Start;
}

/// <summary>Base of every syntax tree node. <see cref="Span"/> covers the node's source text; the parser sets it once while building the node.</summary>
internal abstract record SqlNode
{
    internal SourceSpan Span { get; set; }
}

/// <summary>An identifier with its quoting removed ("a""b" is <c>a"b</c>).</summary>
internal sealed record Name(string Text) : SqlNode;

/// <summary>[schema.]name.</summary>
internal sealed record QualifiedName(Name? Schema, Name Name) : SqlNode;

internal enum SortOrder { None, Asc, Desc }

internal enum NullsOrder { None, First, Last }

internal enum ConflictAction { Rollback, Abort, Fail, Ignore, Replace }

/// <summary>expr [COLLATE name] [ASC|DESC] [NULLS FIRST|LAST]: an ORDER BY term, an index column, a PRIMARY KEY or UNIQUE column.</summary>
internal sealed record OrderingTerm(Expr Expr, SortOrder Order, NullsOrder Nulls) : SqlNode;

/// <summary>name [COLLATE name] [ASC|DESC] (foreign-key column lists).</summary>
internal sealed record IndexedColumn(Name Name, Name? Collation, SortOrder Order) : SqlNode;

/// <summary>WITH [RECURSIVE] cte, ...</summary>
internal sealed record WithClause(bool Recursive, ImmutableArray<CommonTableExpression> Tables) : SqlNode;

internal enum CteMaterialization { Default, Materialized, NotMaterialized }

internal sealed record CommonTableExpression(Name Name, ImmutableArray<Name> Columns, CteMaterialization Materialization, SelectStatement Select) : SqlNode;

/// <summary>LIMIT expr [OFFSET expr]. The comma form <c>LIMIT a, b</c> is stored as <c>LIMIT b OFFSET a</c>, which is what it means.</summary>
internal sealed record LimitClause(Expr Limit, Expr? Offset) : SqlNode;

/// <summary>RETURNING result-column, ...</summary>
internal sealed record ReturningClause(ImmutableArray<ResultColumn> Columns) : SqlNode;

/// <summary>A type name as written after a column name or in CAST: its words and optional size arguments such as <c>(10)</c> or <c>(10,2)</c>.</summary>
internal sealed record TypeName(ImmutableArray<string> Words, ImmutableArray<string> Sizes) : SqlNode;
