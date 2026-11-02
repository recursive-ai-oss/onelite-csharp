namespace RecursiveAI.OneLite.Sql;

internal sealed record ExplainStatement(bool QueryPlan, Statement Inner) : Statement;

internal sealed record AttachStatement(Expr Database, Expr SchemaName, Expr? Key) : Statement;

internal sealed record DetachStatement(Expr SchemaName) : Statement;

/// <summary><c>Concurrent</c> exists only when the parser option for the BEGIN CONCURRENT extension is on.</summary>
internal enum BeginMode { Default, Deferred, Immediate, Exclusive, Concurrent }

internal sealed record BeginStatement(BeginMode Mode, Name? TransactionName) : Statement;

/// <summary>COMMIT or END.</summary>
internal sealed record CommitStatement(Name? TransactionName) : Statement;

/// <summary>ROLLBACK [TRANSACTION [name]] [TO [SAVEPOINT] savepoint].</summary>
internal sealed record RollbackStatement(Name? TransactionName, Name? ToSavepoint) : Statement;

internal sealed record SavepointStatement(Name Savepoint) : Statement;

internal sealed record ReleaseStatement(Name Savepoint) : Statement;

internal enum PragmaValueKind { Number, Word, String, Keyword }

/// <summary>A PRAGMA value: a signed number as written, a name, the keywords ON, DELETE and DEFAULT (as written), or a string.</summary>
internal sealed record PragmaValue(PragmaValueKind Kind, string Text) : SqlNode;

/// <summary>PRAGMA [schema.]name, name = value, or name(value). <c>Parenthesized</c> tells the last two apart.</summary>
internal sealed record PragmaStatement(Name? Schema, Name Name, PragmaValue? Value, bool Parenthesized) : Statement;

internal sealed record VacuumStatement(Name? Schema, Expr? Into) : Statement;

internal sealed record ReindexStatement(QualifiedName? Target) : Statement;

internal sealed record AnalyzeStatement(QualifiedName? Target) : Statement;
