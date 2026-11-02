namespace RecursiveAI.OneLite.Sql.Parsing;

/// <summary>Switches for the parts of the dialect that are not plain SQLite.</summary>
internal sealed class ParserOptions
{
    /// <summary>The default: exactly the pinned SQLite dialect.</summary>
    internal static readonly ParserOptions Default = new();

    /// <summary>The dialect with <c>BEGIN CONCURRENT [SNAPSHOT]</c> accepted.</summary>
    internal static readonly ParserOptions WithConcurrent = new() { BeginConcurrent = true };

    /// <summary>Accept <c>BEGIN CONCURRENT</c> (the opt-in concurrent-writer extension). Off by default: SQLite itself rejects it.</summary>
    internal bool BeginConcurrent { get; init; }
}
