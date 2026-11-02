using System.Buffers.Text;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Vm;

/// <summary>JSONB to JSON text, to SQL values, and the string escaping rules between them (json1 §3, jsonb §3.2).</summary>
internal static class JsonRender
{
    internal static OneLiteException Malformed() => new(ResultCode.Error, "malformed JSON");

    private static readonly byte[] s_hexDigits = "0123456789abcdef"u8.ToArray();

    // ---------------------------------------------------------------- text

    /// <summary>Appends the canonical RFC 8259 text of the element at <paramref name="pos"/>. Throws "malformed JSON" for a damaged blob.</summary>
    internal static void Render(ReadOnlySpan<byte> b, int pos, JsonBuf o, int depth = 0)
    {
        if (depth > JsonTextParser.MaxDepth + 1 || !Jsonb.Header(b, pos, out int t, out int h, out int sz))
        {
            throw Malformed();
        }

        ReadOnlySpan<byte> p = b.Slice(pos + h, sz);
        switch (t)
        {
            case JT.Null:
                o.Put("null"u8);
                break;
            case JT.True:
                o.Put("true"u8);
                break;
            case JT.False:
                o.Put("false"u8);
                break;
            case JT.Int:
            case JT.Float:
                o.Put(p);
                break;
            case JT.Int5:
                Int5Text(p, o);
                break;
            case JT.Float5:
                Float5Text(p, o);
                break;
            case JT.Text:
            case JT.TextJ:
                o.Put((byte)'"');
                o.Put(p);
                o.Put((byte)'"');
                break;
            case JT.Text5:
                o.Put((byte)'"');
                Text5ToJson(p, o);
                o.Put((byte)'"');
                break;
            case JT.TextRaw:
                o.Put((byte)'"');
                EscapeRaw(p, o);
                o.Put((byte)'"');
                break;
            case JT.Array:
            {
                o.Put((byte)'[');
                int i = 0;
                bool first = true;
                while (i < p.Length)
                {
                    if (!Jsonb.Header(p, i, out _, out int eh, out int es))
                    {
                        throw Malformed();
                    }

                    if (!first)
                    {
                        o.Put((byte)',');
                    }

                    first = false;
                    Render(p, i, o, depth + 1);
                    i += eh + es;
                }

                o.Put((byte)']');
                break;
            }

            case JT.Object:
            {
                o.Put((byte)'{');
                int i = 0;
                bool first = true;
                bool key = true;
                while (i < p.Length)
                {
                    if (!Jsonb.Header(p, i, out int kt, out int eh, out int es))
                    {
                        throw Malformed();
                    }

                    if (key)
                    {
                        if (kt is < JT.Text or > JT.TextRaw)
                        {
                            throw Malformed();
                        }

                        if (!first)
                        {
                            o.Put((byte)',');
                        }

                        first = false;
                    }

                    Render(p, i, o, depth + 1);
                    if (key)
                    {
                        o.Put((byte)':');
                    }

                    key = !key;
                    i += eh + es;
                }

                if (!key)
                {
                    throw Malformed();
                }

                o.Put((byte)'}');
                break;
            }

            default:
                throw Malformed();
        }
    }

    /// <summary>The text of a hexadecimal integer (INT5): decimal when it fits 64 bits unsigned, 9.0e999 beyond.</summary>
    internal static void Int5Text(ReadOnlySpan<byte> p, JsonBuf o)
    {
        int i = 0;
        bool neg = false;
        if (i < p.Length && (p[i] == '-' || p[i] == '+'))
        {
            neg = p[i] == '-';
            i++;
        }

        if (i + 1 < p.Length && p[i] == '0' && (p[i + 1] | 0x20) == 'x')
        {
            ulong v = 0;
            bool over = false;
            for (int k = i + 2; k < p.Length; k++)
            {
                if (!Jsonb.IsHexDigit(p[k]))
                {
                    throw Malformed();
                }

                if ((v >> 60) != 0)
                {
                    over = true;
                }

                v = (v << 4) | (uint)Jsonb.HexVal(p[k]);
            }

            if (over)
            {
                o.Put(neg ? "-9.0e999"u8 : "9.0e999"u8);
                return;
            }

            if (neg)
            {
                o.Put((byte)'-');
            }

            Span<byte> tmp = stackalloc byte[24];
            Utf8Formatter.TryFormat(v, tmp, out int w);
            o.Put(tmp[..w]);
            return;
        }

        o.Put(p);
    }

    /// <summary>The canonical text of a JSON5 float: ".5" gets a leading zero, "5." a trailing one.</summary>
    internal static void Float5Text(ReadOnlySpan<byte> p, JsonBuf o)
    {
        int i = 0;
        if (i < p.Length && (p[i] == '-' || p[i] == '+'))
        {
            if (p[i] == '-')
            {
                o.Put((byte)'-');
            }

            i++;
        }

        if (i < p.Length && p[i] == '.')
        {
            o.Put((byte)'0');
        }

        for (; i < p.Length; i++)
        {
            o.Put(p[i]);
            if (p[i] == '.' && (i + 1 >= p.Length || p[i + 1] is not (>= (byte)'0' and <= (byte)'9')))
            {
                o.Put((byte)'0');
            }
        }
    }

    /// <summary>JSON5 string payload to RFC 8259 text: \x, \v, \0 and \' escapes are rewritten, line continuations dropped, raw control characters escaped.</summary>
    internal static void Text5ToJson(ReadOnlySpan<byte> p, JsonBuf o)
    {
        for (int i = 0; i < p.Length; i++)
        {
            byte c = p[i];
            if (c == '\\' && i + 1 < p.Length)
            {
                byte e = p[++i];
                switch (e)
                {
                    case (byte)'\'':
                        o.Put((byte)'\'');
                        break;
                    case (byte)'v':
                        o.Put("\\u000b"u8);
                        break;
                    case (byte)'0':
                        o.Put("\\u0000"u8);
                        break;
                    case (byte)'x':
                        o.Put("\\u00"u8);
                        if (i + 2 < p.Length)
                        {
                            o.Put(p[i + 1]);
                            o.Put(p[i + 2]);
                            i += 2;
                        }

                        break;
                    case 0x0A:
                        break;
                    case 0x0D:
                        if (i + 1 < p.Length && p[i + 1] == 0x0A)
                        {
                            i++;
                        }

                        break;
                    case 0xE2 when i + 2 < p.Length && p[i + 1] == 0x80 && p[i + 2] is 0xA8 or 0xA9:
                        i += 2;
                        break;
                    default:
                        o.Put((byte)'\\');
                        o.Put(e);
                        break;
                }
            }
            else if (c == '"')
            {
                o.Put("\\\""u8);
            }
            else if (c < 0x20)
            {
                PutControl(c, o);
            }
            else
            {
                o.Put(c);
            }
        }
    }

    private static void PutControl(byte c, JsonBuf o)
    {
        switch (c)
        {
            case 8:
                o.Put("\\b"u8);
                break;
            case 9:
                o.Put("\\t"u8);
                break;
            case 10:
                o.Put("\\n"u8);
                break;
            case 12:
                o.Put("\\f"u8);
                break;
            case 13:
                o.Put("\\r"u8);
                break;
            default:
                o.Put("\\u00"u8);
                o.Put(s_hexDigits[c >> 4]);
                o.Put(s_hexDigits[c & 15]);
                break;
        }
    }

    /// <summary>Escapes an SQL string for a JSON string literal (json_quote): quote, backslash and control characters.</summary>
    internal static void EscapeRaw(ReadOnlySpan<byte> p, JsonBuf o)
    {
        foreach (byte c in p)
        {
            if (c == '"')
            {
                o.Put("\\\""u8);
            }
            else if (c == '\\')
            {
                o.Put("\\\\"u8);
            }
            else if (c < 0x20)
            {
                PutControl(c, o);
            }
            else
            {
                o.Put(c);
            }
        }
    }

    /// <summary>True when an SQL string has to be stored as TEXTRAW rather than TEXT.</summary>
    internal static bool NeedsEscape(ReadOnlySpan<byte> p)
    {
        foreach (byte c in p)
        {
            if (c < 0x20 || c == '"' || c == '\\')
            {
                return true;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------- pretty print

    internal static void Pretty(ReadOnlySpan<byte> b, int pos, ReadOnlySpan<byte> indent, JsonBuf o, int level, JsonBuf scratch)
    {
        if (level > JsonTextParser.MaxDepth + 1 || !Jsonb.Header(b, pos, out int t, out int h, out int sz))
        {
            throw Malformed();
        }

        // An element at the depth limit, scalar or container, is too deep: 1000 nested empty arrays print, a scalar inside them does not.
        if (level >= JsonTextParser.MaxDepth)
        {
            throw new OneLiteException(ResultCode.Error, "JSON nested too deep");
        }

        ReadOnlySpan<byte> p = b.Slice(pos + h, sz);
        if (t is not (JT.Array or JT.Object))
        {
            Render(b, pos, o);
            return;
        }

        bool obj = t == JT.Object;
        o.Put((byte)(obj ? '{' : '['));
        if (p.Length == 0)
        {
            o.Put((byte)(obj ? '}' : ']'));
            return;
        }

        int i = 0;
        bool first = true;
        bool key = true;
        while (i < p.Length)
        {
            if (!Jsonb.Header(p, i, out _, out int eh, out int es))
            {
                throw Malformed();
            }

            if (!obj || key)
            {
                o.Put(first ? "\n"u8 : ",\n"u8);
                first = false;
                for (int k = 0; k <= level; k++)
                {
                    o.Put(indent);
                }
            }

            if (obj && key)
            {
                Render(p, i, o);
                o.Put(": "u8);
            }
            else
            {
                Pretty(p, i, indent, o, level + 1, scratch);
            }

            key = !key;
            i += eh + es;
        }

        o.Put((byte)'\n');
        for (int k = 0; k < level; k++)
        {
            o.Put(indent);
        }

        o.Put((byte)(obj ? '}' : ']'));
    }

    // ---------------------------------------------------------------- SQL values

    /// <summary>Decodes a string payload (any of the four text types) to UTF-8 SQL text.</summary>
    internal static void Unescape(int type, ReadOnlySpan<byte> p, JsonBuf o)
    {
        if (type is JT.Text or JT.TextRaw)
        {
            o.Put(p);
            return;
        }

        for (int i = 0; i < p.Length; i++)
        {
            byte c = p[i];
            if (c != '\\' || i + 1 >= p.Length)
            {
                o.Put(c);
                continue;
            }

            byte e = p[++i];
            switch (e)
            {
                case (byte)'b':
                    o.Put(8);
                    break;
                case (byte)'f':
                    o.Put(12);
                    break;
                case (byte)'n':
                    o.Put(10);
                    break;
                case (byte)'r':
                    o.Put(13);
                    break;
                case (byte)'t':
                    o.Put(9);
                    break;
                case (byte)'v':
                    o.Put(11);
                    break;
                case (byte)'0':
                    o.Put(0);
                    break;
                case (byte)'u':
                {
                    int cp = Hex4(p, i + 1);
                    i += 4;
                    if (cp >= 0xD800 && cp <= 0xDBFF && i + 6 < p.Length && p[i + 1] == '\\' && p[i + 2] == 'u')
                    {
                        int lo = Hex4(p, i + 3);
                        if (lo >= 0xDC00 && lo <= 0xDFFF)
                        {
                            cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                            i += 6;
                        }
                    }

                    o.PutUtf8(cp);
                    break;
                }

                case (byte)'x':
                    o.PutUtf8(i + 2 < p.Length ? (Jsonb.HexVal(p[i + 1]) << 4) | Jsonb.HexVal(p[i + 2]) : 0);
                    i += 2;
                    break;
                case 0x0A:
                    break;
                case 0x0D:
                    if (i + 1 < p.Length && p[i + 1] == 0x0A)
                    {
                        i++;
                    }

                    break;
                case 0xE2 when i + 2 < p.Length && p[i + 1] == 0x80 && p[i + 2] is 0xA8 or 0xA9:
                    i += 2;
                    break;
                default:
                    o.Put(e);
                    break;
            }
        }
    }

    private static int Hex4(ReadOnlySpan<byte> p, int at)
    {
        int v = 0;
        for (int k = 0; k < 4; k++)
        {
            v = (v << 4) | (at + k < p.Length ? Jsonb.HexVal(p[at + k]) : 0);
        }

        return v;
    }

    /// <summary>The numeric value of a number element's payload as SQL sees it: an integer when it is one and fits, else a real.</summary>
    internal static void NumberValue(int type, ReadOnlySpan<byte> p, ref Mem dst)
    {
        if (!JsonbCheck.NumberOk(type, p))
        {
            throw Malformed();
        }

        if (type == JT.Int5)
        {
            int i = 0;
            bool neg = false;
            if (i < p.Length && (p[i] == '-' || p[i] == '+'))
            {
                neg = p[i] == '-';
                i++;
            }

            ulong v = 0;
            bool over = false;
            for (int k = i + 2; k < p.Length; k++)
            {
                if ((v >> 60) != 0)
                {
                    over = true;
                }

                v = (v << 4) | (uint)Jsonb.HexVal(p[k]);
            }

            if (over)
            {
                // The reference rejects a hexadecimal integer wider than 64 bits when it becomes an SQL value.
                throw Malformed();
            }

            if (v > long.MaxValue)
            {
                Values.SetReal(ref dst, neg ? -(double)v : (double)v);
            }
            else
            {
                Values.SetInt(ref dst, neg ? -(long)v : (long)v);
            }

            return;
        }

        if (type == JT.Int)
        {
            if (Utf8Parser.TryParse(p, out long iv, out int used) && used == p.Length)
            {
                Values.SetInt(ref dst, iv);
                return;
            }
        }

        Values.SetReal(ref dst, ParseDouble(p));
    }

    internal static double ParseDouble(ReadOnlySpan<byte> p)
    {
        if (double.TryParse(p, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d))
        {
            return d;
        }

        return 0;
    }
}
