using System.Data.Common;

namespace RecursiveAI.OneLite.Data;

/// <summary>
/// A database error surfaced through ADO.NET. It carries the SQLite result code and extended code of the failure, and its message is the library's
/// text in the form ADO.NET SQLite providers use: <c>SQLite Error 1: 'no such table: t'.</c>
/// </summary>
public sealed class OneLiteDataException : DbException
{
    /// <summary>Wraps an engine error.</summary>
    /// <param name="inner">The engine exception.</param>
    public OneLiteDataException(OneLiteException inner)
        : base(Format(inner), inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ResultCode = inner.Code;
        ExtendedResultCode = inner.ExtendedCode;
        Detail = inner.Detail;
        Position = inner.Position;
        Identifier = inner.Identifier;
        Suggestions = inner.Suggestions;
        Caret = inner.Caret;
        Constraint = inner.Constraint;
        Sql = inner.Sql;
        Explanation = inner.Explain();
    }

    /// <summary>The SQL text the error is about, or null.</summary>
    public string? Sql { get; }

    /// <summary>Where in <see cref="Sql"/> the error is, when it has a place in the text; otherwise null.</summary>
    public SqlPosition? Position { get; }

    /// <summary>The name the error is about (an unknown table, column or function, the token a syntax error stopped at), or null.</summary>
    public string? Identifier { get; }

    /// <summary>Names that look like what was meant, closest first; empty if none.</summary>
    public IReadOnlyList<string> Suggestions { get; }

    /// <summary>The line of SQL with a second line holding <c>^~~</c> under the error; null when there is no position.</summary>
    public string? Caret { get; }

    /// <summary>For a failed constraint, which one and on what; otherwise null.</summary>
    public ConstraintInfo? Constraint { get; }

    /// <summary>A readable account of the error (position, caret, constraint details and suggestions); empty when OneLite has nothing to add to <see cref="Exception.Message"/>.</summary>
    public string Explanation { get; }

    /// <summary>The primary SQLite result code, for example 1 for SQLITE_ERROR or 5 for SQLITE_BUSY.</summary>
    public int ResultCode { get; }

    /// <summary>The extended result code (equals <see cref="ResultCode"/> when there is none).</summary>
    public int ExtendedResultCode { get; }

    /// <summary>What OneLite adds to the identical message (for corruption: the page and the cause); empty if nothing.</summary>
    public string Detail { get; }

    /// <summary>The primary result code (same as <see cref="ResultCode"/>; the name ADO.NET SQLite providers use).</summary>
    public int SqliteErrorCode => ResultCode;

    /// <summary>The extended result code (same as <see cref="ExtendedResultCode"/>).</summary>
    public int SqliteExtendedErrorCode => ExtendedResultCode;

    /// <inheritdoc />
    public override int ErrorCode => ResultCode;

    private static string Format(OneLiteException e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return $"SQLite Error {e.Code}: '{e.Message}'.";
    }
}
