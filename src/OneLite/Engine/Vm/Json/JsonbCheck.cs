namespace RecursiveAI.OneLite.Vm;

/// <summary>Strict structural validation of a JSONB blob (json_valid flag 8, json_error_position on blobs).</summary>
internal static class JsonbCheck
{
    internal static bool Strict(ReadOnlySpan<byte> b) => Jsonb.LooksValid(b) && FirstError(b) < 0;

    /// <summary>Offset of the first element that is not well-formed, or -1.</summary>
    internal static int FirstError(ReadOnlySpan<byte> b)
    {
        if (!Jsonb.LooksValid(b))
        {
            return 0;
        }

        return Element(b, 0, 0);
    }

    private static int Element(ReadOnlySpan<byte> b, int pos, int depth)
    {
        if (depth >= JsonTextParser.MaxDepth || !Jsonb.Header(b, pos, out int t, out int h, out int sz))
        {
            return pos;
        }

        ReadOnlySpan<byte> p = b.Slice(pos + h, sz);
        switch (t)
        {
            case JT.Null:
            case JT.True:
            case JT.False:
                return sz == 0 ? -1 : pos;
            case JT.Int:
            case JT.Int5:
            case JT.Float:
            case JT.Float5:
                return NumberOk(t, p) ? -1 : pos;
            case JT.Text:
            case JT.TextJ:
            case JT.Text5:
                return StringOk(t, p) ? -1 : pos;
            case JT.TextRaw:
                return -1;
            case JT.Array:
            {
                int i = 0;
                while (i < p.Length)
                {
                    int e = Element(p, i, depth + 1);
                    if (e >= 0)
                    {
                        return pos + h + e;
                    }

                    Jsonb.Header(p, i, out _, out int eh, out int es);
                    i += eh + es;
                }

                return -1;
            }

            case JT.Object:
            {
                int i = 0;
                bool key = true;
                while (i < p.Length)
                {
                    int e = Element(p, i, depth + 1);
                    if (e >= 0)
                    {
                        return pos + h + e;
                    }

                    Jsonb.Header(p, i, out int et, out int eh, out int es);
                    if (key && et is < JT.Text or > JT.TextRaw)
                    {
                        return pos + h + i;
                    }

                    key = !key;
                    i += eh + es;
                }

                return key ? -1 : pos;
            }

            default:
                return pos;
        }
    }

    /// <summary>A number payload is valid when reading it as JSON text gives one number element of the same type and the same text.</summary>
    internal static bool NumberOk(int type, ReadOnlySpan<byte> p)
    {
        if (p.Length == 0 || p[0] == '+')
        {
            return false;
        }

        if (type == JT.Int)
        {
            int k = p[0] == '-' ? 1 : 0;
            if (k == p.Length || (p[k] == '0' && p.Length > k + 1))
            {
                return false;
            }

            for (; k < p.Length; k++)
            {
                if (p[k] is < (byte)'0' or > (byte)'9')
                {
                    return false;
                }
            }

            return true;
        }

        JsonBuf o = JsonScratch.Get(6);
        var parser = new JsonTextParser(p, o);
        if (!parser.ParseDocument() || !Jsonb.Header(o.Span, 0, out int rt, out int rh, out int rs))
        {
            return false;
        }

        return rt == type && o.Span.Slice(rh, rs).SequenceEqual(p);
    }

    /// <summary>Whether a string payload holds only what its type allows: TEXT no escapes, TEXTJ RFC 8259 escapes, TEXT5 JSON5 escapes.</summary>
    private static bool StringOk(int type, ReadOnlySpan<byte> p)
    {
        for (int i = 0; i < p.Length; i++)
        {
            byte c = p[i];
            if (c == '\\')
            {
                if (type == JT.Text || i + 1 >= p.Length)
                {
                    return false;
                }

                byte e = p[++i];
                switch (e)
                {
                    case (byte)'"':
                    case (byte)'\\':
                    case (byte)'/':
                    case (byte)'b':
                    case (byte)'f':
                    case (byte)'n':
                    case (byte)'r':
                    case (byte)'t':
                        break;
                    case (byte)'u':
                        if (i + 4 >= p.Length)
                        {
                            return false;
                        }

                        for (int k = 1; k <= 4; k++)
                        {
                            if (!Jsonb.IsHexDigit(p[i + k]))
                            {
                                return false;
                            }
                        }

                        i += 4;
                        break;
                    default:
                        if (type != JT.Text5)
                        {
                            return false;
                        }

                        if (e == 'x')
                        {
                            if (i + 2 >= p.Length || !Jsonb.IsHexDigit(p[i + 1]) || !Jsonb.IsHexDigit(p[i + 2]))
                            {
                                return false;
                            }

                            i += 2;
                        }
                        else if (e is not ((byte)'\'' or (byte)'v' or (byte)'0' or 0x0A or 0x0D or 0xE2))
                        {
                            return false;
                        }

                        break;
                }
            }
            else if (c == '"' && type != JT.Text5)
            {
                return false;
            }
            else if (c < 0x20 && type != JT.Text5)
            {
                return false;
            }
        }

        return true;
    }
}
