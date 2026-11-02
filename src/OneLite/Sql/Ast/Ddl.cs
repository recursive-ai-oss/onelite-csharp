using System.Collections.Immutable;
using System.Linq;

namespace RecursiveAI.OneLite.Sql;

/// <summary>
/// Base of CREATE statements. <see cref="CreateStatement.SchemaSqlStart"/> is where the text SQLite stores in <c>sqlite_schema.sql</c> begins
/// (the unqualified object name); the stored text is the statement's canonical CREATE prefix followed by the source from there to the end.
/// </summary>
internal abstract record CreateStatement : Statement
{
    /// <summary>[schema.]name of the object created.</summary>
    public abstract QualifiedName Name { get; init; }

    internal int SchemaSqlStart => Name.Name.Span.Start;
}

internal enum DeferMode { Deferred, Immediate }

/// <summary>[NOT] DEFERRABLE [INITIALLY DEFERRED|IMMEDIATE].</summary>
internal sealed record Deferral(bool Not, DeferMode? Initially) : SqlNode;

internal enum ForeignKeyRuleKind { OnDelete, OnUpdate, Match }

internal enum ForeignKeyAction { SetNull, SetDefault, Cascade, Restrict, NoAction }

/// <summary>ON DELETE|UPDATE action, or MATCH name (<c>MatchName</c>).</summary>
internal sealed record ForeignKeyRule(ForeignKeyRuleKind Kind, ForeignKeyAction Action, Name? MatchName) : SqlNode;

/// <summary>REFERENCES table [(columns)] rules. The column list is empty when absent.</summary>
internal sealed record ForeignKeyClause(Name Table, ImmutableArray<IndexedColumn> Columns, ImmutableArray<ForeignKeyRule> Rules) : SqlNode;

/// <summary>A column constraint, optionally named with CONSTRAINT name.</summary>
internal abstract record ColumnConstraint : SqlNode
{
    /// <summary>The CONSTRAINT name, or null.</summary>
    public abstract Name? ConstraintName { get; init; }
}

internal sealed record PrimaryKeyColumnConstraint(Name? ConstraintName, SortOrder Order, ConflictAction? OnConflict, bool Autoincrement) : ColumnConstraint;

internal sealed record NotNullConstraint(Name? ConstraintName, ConflictAction? OnConflict) : ColumnConstraint;

internal sealed record NullConstraint(Name? ConstraintName, ConflictAction? OnConflict) : ColumnConstraint;

internal sealed record UniqueColumnConstraint(Name? ConstraintName, ConflictAction? OnConflict) : ColumnConstraint;

internal sealed record CheckColumnConstraint(Name? ConstraintName, Expr Check) : ColumnConstraint;

/// <summary>DEFAULT literal | signed literal | (expr) | word.</summary>
internal sealed record DefaultConstraint(Name? ConstraintName, Expr Value) : ColumnConstraint;

internal sealed record CollateConstraint(Name? ConstraintName, Name Collation) : ColumnConstraint;

internal sealed record ReferencesConstraint(Name? ConstraintName, ForeignKeyClause Clause) : ColumnConstraint;

internal sealed record DeferralConstraint(Name? ConstraintName, Deferral Deferral) : ColumnConstraint;

/// <summary>
/// [GENERATED ALWAYS] AS (expr) [STORED|VIRTUAL]. <c>StorageWord</c> is the word after the parenthesis as written (any identifier parses; SQLite
/// rejects all but STORED and VIRTUAL later). <c>Always</c> records whether GENERATED ALWAYS was written.
/// </summary>
internal sealed record GeneratedConstraint(Name? ConstraintName, Expr Value, string? StorageWord, bool Always) : ColumnConstraint;

/// <summary>A CONSTRAINT name that no constraint followed or that another CONSTRAINT name replaced; the grammar accepts it on its own.</summary>
internal sealed record DanglingColumnConstraintName(Name? ConstraintName) : ColumnConstraint;

internal sealed record ColumnDefinition(Name Name, TypeName? Type, ImmutableArray<ColumnConstraint> Constraints) : SqlNode;

internal abstract record TableConstraint : SqlNode
{
    /// <summary>The CONSTRAINT name, or null.</summary>
    public abstract Name? ConstraintName { get; init; }
}

internal sealed record PrimaryKeyTableConstraint(Name? ConstraintName, ImmutableArray<OrderingTerm> Columns, bool Autoincrement, ConflictAction? OnConflict) : TableConstraint;

internal sealed record UniqueTableConstraint(Name? ConstraintName, ImmutableArray<OrderingTerm> Columns, ConflictAction? OnConflict) : TableConstraint;

internal sealed record CheckTableConstraint(Name? ConstraintName, Expr Check, ConflictAction? OnConflict) : TableConstraint;

/// <summary>A CONSTRAINT name on its own between table constraints.</summary>
internal sealed record DanglingTableConstraintName(Name? ConstraintName) : TableConstraint;

internal sealed record ForeignKeyTableConstraint(Name? ConstraintName, ImmutableArray<IndexedColumn> Columns, ForeignKeyClause Clause, Deferral? Deferral) : TableConstraint;

/// <summary>A table option after the closing parenthesis: <c>WITHOUT ROWID</c> (<c>Without</c> true, <c>Name</c> ROWID), <c>STRICT</c>, or any other word SQLite rejects later.</summary>
internal sealed record TableOption(bool Without, string Name) : SqlNode;

internal sealed record CreateTableStatement(
    bool Temporary,
    bool IfNotExists,
    QualifiedName Name,
    ImmutableArray<ColumnDefinition> Columns,
    ImmutableArray<TableConstraint> Constraints,
    ImmutableArray<TableOption> Options,
    SelectStatement? AsSelect) : CreateStatement
{
    internal bool WithoutRowid => Options.Any(o => o.Without && string.Equals(o.Name, "ROWID", StringComparison.OrdinalIgnoreCase));

    internal bool Strict => Options.Any(o => !o.Without && string.Equals(o.Name, "STRICT", StringComparison.OrdinalIgnoreCase));
}

internal sealed record CreateIndexStatement(bool Unique, bool IfNotExists, QualifiedName Name, Name Table, ImmutableArray<OrderingTerm> Columns, Expr? Where) : CreateStatement;

internal sealed record CreateViewStatement(bool Temporary, bool IfNotExists, QualifiedName Name, ImmutableArray<Name> Columns, SelectStatement Select) : CreateStatement;

internal enum TriggerTime { Default, Before, After, InsteadOf }

internal enum TriggerEvent { Delete, Insert, Update }

internal sealed record CreateTriggerStatement(
    bool Temporary,
    bool IfNotExists,
    QualifiedName Name,
    TriggerTime Time,
    TriggerEvent Event,
    ImmutableArray<Name> UpdateColumns,
    QualifiedName Table,
    bool ForEachRow,
    Expr? When,
    ImmutableArray<Statement> Body) : CreateStatement;

/// <summary><c>Arguments</c> are the module arguments as raw text, trimmed, exactly as lang_createvtab says they are handed to the module.</summary>
internal sealed record CreateVirtualTableStatement(bool IfNotExists, QualifiedName Name, Name Module, ImmutableArray<string> Arguments) : CreateStatement;

internal enum DropKind { Table, Index, View, Trigger }

internal sealed record DropStatement(DropKind Kind, bool IfExists, QualifiedName Name) : Statement;

internal abstract record AlterTableStatement : Statement
{
    /// <summary>The table altered.</summary>
    public abstract QualifiedName Table { get; init; }
}

internal sealed record RenameTableStatement(QualifiedName Table, Name NewName) : AlterTableStatement;

internal sealed record RenameColumnStatement(QualifiedName Table, Name OldName, Name NewName) : AlterTableStatement;

internal sealed record AddColumnStatement(QualifiedName Table, ColumnDefinition Column) : AlterTableStatement;

internal sealed record DropColumnStatement(QualifiedName Table, Name Column) : AlterTableStatement;

/// <summary>ALTER TABLE t ADD [CONSTRAINT name] CHECK (expr) [ON CONFLICT action].</summary>
internal sealed record AddCheckConstraintStatement(QualifiedName Table, Name? ConstraintName, Expr Check, ConflictAction? OnConflict) : AlterTableStatement;

/// <summary>ALTER TABLE t DROP CONSTRAINT name.</summary>
internal sealed record DropConstraintStatement(QualifiedName Table, Name ConstraintName) : AlterTableStatement;

/// <summary>ALTER TABLE t ALTER [COLUMN] c SET NOT NULL [ON CONFLICT action], or DROP NOT NULL (<c>SetNotNull</c> false, no conflict action).</summary>
internal sealed record AlterColumnNotNullStatement(QualifiedName Table, Name Column, bool SetNotNull, ConflictAction? OnConflict) : AlterTableStatement;
