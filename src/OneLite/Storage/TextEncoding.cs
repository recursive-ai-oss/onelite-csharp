namespace RecursiveAI.OneLite.Storage;

/// <summary>The text encoding of a database (header offset 56; fileformat2 §1.3.13).</summary>
public enum TextEncoding : byte
{
    /// <summary>UTF-8 (header value 1).</summary>
    Utf8 = 1,

    /// <summary>UTF-16 little-endian (header value 2).</summary>
    Utf16Le = 2,

    /// <summary>UTF-16 big-endian (header value 3).</summary>
    Utf16Be = 3,
}
