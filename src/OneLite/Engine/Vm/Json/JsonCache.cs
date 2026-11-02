namespace RecursiveAI.OneLite.Vm;

/// <summary>
/// The last few JSONB documents the editing functions produced, keyed by the text they returned. The reference hands the stored form to the next
/// function that is given the same long text, so a nested call sees the editor's own bytes (string values as TEXTRAW, overwritten elements padded)
/// and not a fresh parse. A short text is remembered only when its document holds a hexadecimal integer, which the plain text cannot show; the threshold and the size of the cache were found by experiment.
/// </summary>
internal static class JsonCache
{
    internal const int MinText = 100;

    // A result holding a raw string with a tab, newline or carriage return is remembered from this length (observed).
    private const int MinTextWithEscape = 97;
    private const int Size = 4;

    [ThreadStatic] private static byte[]?[]? t_text;
    [ThreadStatic] private static byte[]?[]? t_json;
    [ThreadStatic] private static bool[]? t_json5;
    [ThreadStatic] private static int t_next;

    // fromJson: the argument is the result of a JSON function; only then can a short text be the remembered one.
    internal static bool TryGet(ReadOnlySpan<byte> text, bool fromJson, out byte[]? jsonb, out bool json5)
    {
        jsonb = null;
        json5 = false;
        if (t_text is null || (text.Length < MinTextWithEscape && !fromJson))
        {
            return false;
        }

        for (int i = 0; i < Size; i++)
        {
            byte[]? t = t_text[i];
            if (t is not null && text.SequenceEqual(t))
            {
                jsonb = t_json![i];
                json5 = t_json5![i];
                return true;
            }
        }

        return false;
    }

    /// <summary>Forgets everything: the reference matches by the identity of the value passed on, which never outlives the row being computed.</summary>
    internal static void Clear()
    {
        if (t_text is not null)
        {
            Array.Clear(t_text);
            Array.Clear(t_json!);
        }
    }

    private static bool HasRawEscape(ReadOnlySpan<byte> b, int pos)
    {
        if (!Jsonb.Header(b, pos, out int t, out int h, out int sz))
        {
            return false;
        }

        if (t == JT.TextRaw)
        {
            ReadOnlySpan<byte> p = b.Slice(pos + h, sz);
            return p.IndexOfAny((byte)9, (byte)10, (byte)13) >= 0;
        }

        if (t is JT.Array or JT.Object)
        {
            int i = pos + h;
            int end = pos + h + sz;
            while (i < end)
            {
                if (!Jsonb.Header(b, i, out _, out int eh, out int es))
                {
                    return false;
                }

                if (HasRawEscape(b, i))
                {
                    return true;
                }

                i += eh + es;
            }
        }

        return false;
    }

    private static bool HasHexInteger(ReadOnlySpan<byte> b)
    {
        // Elements are walked flat: a container's payload is a run of elements, so every header found while scanning forward is one.
        return Scan(b, 0);
    }

    private static bool Scan(ReadOnlySpan<byte> b, int pos)
    {
        if (!Jsonb.Header(b, pos, out int t, out int h, out int sz))
        {
            return false;
        }

        if (t == JT.Int5)
        {
            return true;
        }

        if (t is JT.Array or JT.Object)
        {
            int i = pos + h;
            int end = pos + h + sz;
            while (i < end)
            {
                if (!Jsonb.Header(b, i, out _, out int eh, out int es))
                {
                    return false;
                }

                if (Scan(b, i))
                {
                    return true;
                }

                i += eh + es;
            }
        }

        return false;
    }

    internal static void Put(ReadOnlySpan<byte> text, ReadOnlySpan<byte> jsonb, bool json5)
    {
        if (text.Length < (HasRawEscape(jsonb, 0) ? MinTextWithEscape : MinText) && !HasHexInteger(jsonb))
        {
            return;
        }

        t_text ??= new byte[]?[Size];
        t_json ??= new byte[]?[Size];
        t_json5 ??= new bool[Size];
        t_text[t_next] = text.ToArray();
        t_json[t_next] = jsonb.ToArray();
        t_json5[t_next] = json5;
        t_next = (t_next + 1) % Size;
    }
}
