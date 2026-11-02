namespace RecursiveAI.OneLite;

/// <summary>A place in SQL text.</summary>
/// <param name="Offset">UTF-16 index of the first character.</param>
/// <param name="Length">Length in UTF-16 characters of the text the error is about (at least 1).</param>
/// <param name="Line">1-based line number; a line ends at <c>\n</c>.</param>
/// <param name="Column">1-based column in UTF-16 characters.</param>
/// <param name="ByteOffset">UTF-8 byte offset of the first character, the unit <c>sqlite3_error_offset</c> uses.</param>
public readonly record struct SqlPosition(int Offset, int Length, int Line, int Column, int ByteOffset)
{
    /// <summary>"line L, column C".</summary>
    /// <returns>The text.</returns>
    public override string ToString() => $"line {Line}, column {Column}";
}

/// <summary>The kinds of constraint failure <see cref="ConstraintInfo"/> distinguishes.</summary>
public enum ConstraintKind
{
    /// <summary>A UNIQUE constraint or unique index.</summary>
    Unique,

    /// <summary>A PRIMARY KEY.</summary>
    PrimaryKey,

    /// <summary>A NOT NULL constraint.</summary>
    NotNull,

    /// <summary>A CHECK constraint.</summary>
    Check,

    /// <summary>A FOREIGN KEY.</summary>
    ForeignKey,

    /// <summary>A STRICT table's column type (SQLITE_CONSTRAINT_DATATYPE).</summary>
    DataType,

    /// <summary>Another constraint failure (a trigger's RAISE, for example).</summary>
    Other,
}

/// <summary>Which constraint failed and on what, taken from the failing statement; the message text itself is unchanged.</summary>
public sealed class ConstraintInfo
{
    internal ConstraintInfo(ConstraintKind kind, string? table, IReadOnlyList<string> columns, string? name, IReadOnlyList<string> keyValues)
    {
        Kind = kind;
        Table = table;
        Columns = columns;
        Name = name;
        KeyValues = keyValues;
    }

    /// <summary>The kind of constraint.</summary>
    public ConstraintKind Kind { get; }

    /// <summary>The table the constraint belongs to; null for a foreign key or a trigger's RAISE.</summary>
    public string? Table { get; }

    /// <summary>The constrained columns (for UNIQUE and PRIMARY KEY, the whole key); empty if not known.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>The CHECK constraint's name or expression text as the message gives it; null otherwise.</summary>
    public string? Name { get; }

    /// <summary>For UNIQUE and PRIMARY KEY, the values of the conflicting key as SQL literals, in <see cref="Columns"/> order (empty if not cheap to know).</summary>
    public IReadOnlyList<string> KeyValues { get; }
}
