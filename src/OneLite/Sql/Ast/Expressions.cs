using System.Collections.Immutable;

namespace RecursiveAI.OneLite.Sql;

internal abstract record Expr : SqlNode;

internal enum LiteralKind
{
    Null,
    Integer,
    Float,

    /// <summary>Text is the unquoted value.</summary>
    String,

    /// <summary>Text is the hex digits between the quotes.</summary>
    Blob,
    CurrentTime,
    CurrentDate,
    CurrentTimestamp,

    /// <summary>A bare word after DEFAULT (lang_createtable: DEFAULT id), which SQLite takes as text.</summary>
    Identifier,
}

/// <summary>A literal. For numbers <c>Text</c> is the source text (digit separators included); keywords carry their canonical name.</summary>
internal sealed record LiteralExpr(LiteralKind Kind, string Text) : Expr;

/// <summary>A bind parameter exactly as written: <c>?</c>, <c>?12</c>, <c>:name</c>, <c>@name</c>, <c>$name</c>.</summary>
internal sealed record VariableExpr(string Text) : Expr;

/// <summary>[[schema.]table.]column.</summary>
internal sealed record ColumnRef(Name? Schema, Name? Table, Name Column) : Expr;

internal enum UnaryOp { Plus, Minus, BitNot, Not }

internal sealed record UnaryExpr(UnaryOp Op, Expr Operand) : Expr;

/// <summary>
/// Binary operators. <c>==</c> is <see cref="Eq"/>, <c>&lt;&gt;</c> is <see cref="Ne"/>; <c>IS DISTINCT FROM</c> is <see cref="IsNot"/> and
/// <c>IS NOT DISTINCT FROM</c> is <see cref="Is"/> (lang_expr says they are spellings of each other).
/// </summary>
internal enum BinaryOp
{
    Or, And, Eq, Ne, Is, IsNot, Lt, Le, Gt, Ge, BitAnd, BitOr, Lshift, Rshift, Add, Sub, Mul, Div, Rem, Concat, Arrow, Arrow2,
}

internal sealed record BinaryExpr(BinaryOp Op, Expr Left, Expr Right) : Expr;

internal sealed record CollateExpr(Expr Operand, Name Collation) : Expr;

internal sealed record CastExpr(Expr Operand, TypeName Type) : Expr;

internal sealed record BetweenExpr(Expr Operand, Expr Low, Expr High, bool Negated) : Expr;

internal sealed record InListExpr(Expr Operand, ImmutableArray<Expr> Items, bool Negated) : Expr;

internal sealed record InSelectExpr(Expr Operand, SelectStatement Select, bool Negated) : Expr;

/// <summary>expr IN [schema.]table or expr IN [schema.]table-function(args). <c>Args</c> is default (not empty) for the plain table form.</summary>
internal sealed record InTableExpr(Expr Operand, QualifiedName Table, ImmutableArray<Expr> Args, bool IsFunction, bool Negated) : Expr;

internal sealed record ExistsExpr(SelectStatement Select) : Expr;

internal sealed record SubqueryExpr(SelectStatement Select) : Expr;

internal sealed record WhenClause(Expr When, Expr Then) : SqlNode;

internal sealed record CaseExpr(Expr? Operand, ImmutableArray<WhenClause> Whens, Expr? Else) : Expr;

internal enum CallQuantifier { None, Distinct, All }

/// <summary>A function call, with the aggregate and window parts: <c>name(DISTINCT args ORDER BY ..) FILTER (WHERE ..) OVER ..</c>.</summary>
internal sealed record FunctionCall(Name Name, CallQuantifier Quantifier, bool Star, ImmutableArray<Expr> Args, ImmutableArray<OrderingTerm> OrderBy, Expr? Filter, WindowReference? Over) : Expr;

internal enum NullTest { IsNull, NotNull }

/// <summary>expr ISNULL, expr NOTNULL or expr NOT NULL. (<c>IS NULL</c> and <c>IS NOT NULL</c> are <see cref="BinaryExpr"/> with <see cref="BinaryOp.Is"/>.)</summary>
internal sealed record NullTestExpr(Expr Operand, NullTest Test) : Expr;

internal enum LikeOp { Like, Glob, Regexp, Match }

internal sealed record LikeExpr(Expr Left, LikeOp Op, Expr Pattern, Expr? Escape, bool Negated) : Expr;

internal enum RaiseKind { Ignore, Rollback, Abort, Fail }

internal sealed record RaiseExpr(RaiseKind Kind, Expr? Message) : Expr;

/// <summary>(a, b, ...) with at least two items.</summary>
internal sealed record RowValueExpr(ImmutableArray<Expr> Items) : Expr;

/// <summary>(expr). Kept so result columns and error carets keep the source text SQLite names things by.</summary>
internal sealed record ParenExpr(Expr Inner) : Expr;

/// <summary>A window name or an inline window definition after OVER.</summary>
internal sealed record WindowReference(Name? Named, WindowSpec? Spec) : SqlNode;

/// <summary>([base] [PARTITION BY ..] [ORDER BY ..] [frame]).</summary>
internal sealed record WindowSpec(Name? BaseName, ImmutableArray<Expr> PartitionBy, ImmutableArray<OrderingTerm> OrderBy, FrameSpec? Frame) : SqlNode;

internal sealed record WindowDefinition(Name Name, WindowSpec Spec) : SqlNode;

internal enum FrameUnit { Range, Rows, Groups }

internal enum FrameBoundKind { UnboundedPreceding, Preceding, CurrentRow, Following, UnboundedFollowing }

internal enum FrameExclude { None, NoOthers, CurrentRow, Group, Ties }

internal sealed record FrameBound(FrameBoundKind Kind, Expr? Offset) : SqlNode;

/// <summary><c>End</c> is null for the one-bound form <c>ROWS 1 PRECEDING</c>.</summary>
internal sealed record FrameSpec(FrameUnit Unit, FrameBound Start, FrameBound? End, FrameExclude Exclude) : SqlNode;
