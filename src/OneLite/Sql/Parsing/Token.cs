namespace RecursiveAI.OneLite.Sql.Parsing;

/// <summary>How an identifier token was quoted (lang_keywords: four ways to quote).</summary>
[Flags]
internal enum TokenFlags : byte
{
    None = 0,

    /// <summary>"name" (a doubled quote stands for one).</summary>
    DoubleQuoted = 1,

    /// <summary>[name] (no escape).</summary>
    Bracketed = 2,

    /// <summary>`name` (a doubled grave accent stands for one).</summary>
    Backquoted = 4,

    /// <summary>A number with a digit-separator underscore that is not between two digits (datatype3 / lang_expr numeric literals).</summary>
    BadNumber = 8,
}

/// <summary>One lexical token: a kind and a span of the input, no text copy.</summary>
internal readonly struct Token
{
    internal Token(TokenKind kind, int start, int end, TokenFlags flags = TokenFlags.None)
    {
        Kind = kind;
        Start = start;
        End = end;
        Flags = flags;
    }

    /// <summary>The token kind; keywords have their own kinds.</summary>
    internal TokenKind Kind { get; }

    /// <summary>Index of the first character.</summary>
    internal int Start { get; }

    /// <summary>Index one past the last character.</summary>
    internal int End { get; }

    /// <summary>Quoting and number-validity flags.</summary>
    internal TokenFlags Flags { get; }

    internal bool IsKeyword => Kind >= TokenKind.Abort;
}
