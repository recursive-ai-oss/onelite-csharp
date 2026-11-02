namespace RecursiveAI.OneLite.Vm;

/// <summary>JSONB element types (jsonb: "Element Type").</summary>
internal static class JT
{
    internal const int Null = 0, True = 1, False = 2, Int = 3, Int5 = 4, Float = 5, Float5 = 6, Text = 7, TextJ = 8, Text5 = 9, TextRaw = 10, Array = 11, Object = 12;
}

/// <summary>JSONB element headers (jsonb §3.1): the low nibble is the type, the high nibble a size or the width of the size that follows.</summary>
internal static class Jsonb
{
    /// <summary>Reads the header at <paramref name="pos"/>. False when it runs past the buffer or its payload does.</summary>
    internal static bool Header(ReadOnlySpan<byte> b, int pos, out int type, out int hdr, out int size)
    {
        type = 0;
        hdr = 0;
        size = 0;
        if ((uint)pos >= (uint)b.Length)
        {
            return false;
        }

        int b0 = b[pos];
        type = b0 & 15;
        int hi = b0 >> 4;
        if (hi <= 11)
        {
            hdr = 1;
            size = hi;
        }
        else
        {
            int w = hi switch { 12 => 1, 13 => 2, 14 => 4, _ => 8 };
            if (pos + 1 + w > b.Length)
            {
                return false;
            }

            ulong v = 0;
            for (int k = 0; k < w; k++)
            {
                v = (v << 8) | b[pos + 1 + k];
            }

            if (v > int.MaxValue)
            {
                return false;
            }

            hdr = 1 + w;
            size = (int)v;
        }

        return (long)pos + hdr + size <= b.Length;
    }

    /// <summary>Superficial check (json_valid flag 4): one well-formed header whose element fills the blob.</summary>
    internal static bool LooksValid(ReadOnlySpan<byte> b)
        => b.Length > 0 && Header(b, 0, out int t, out int h, out int s) && h + s == b.Length && t <= 12;

    internal static int HeaderLen(int size) => size <= 11 ? 1 : size <= 0xFF ? 2 : size <= 0xFFFF ? 3 : 5;

    /// <summary>Appends an element header for a payload of <paramref name="size"/> bytes in the shortest form.</summary>
    internal static void PutHeader(JsonBuf o, int type, int size)
    {
        if (size <= 11)
        {
            o.Put((byte)((size << 4) | type));
        }
        else if (size <= 0xFF)
        {
            o.Put((byte)(0xC0 | type));
            o.Put((byte)size);
        }
        else if (size <= 0xFFFF)
        {
            o.Put((byte)(0xD0 | type));
            o.Put((byte)(size >> 8));
            o.Put((byte)size);
        }
        else
        {
            o.Put((byte)(0xE0 | type));
            o.Put((byte)(size >> 24));
            o.Put((byte)(size >> 16));
            o.Put((byte)(size >> 8));
            o.Put((byte)size);
        }
    }

    /// <summary>
    /// Writes <paramref name="elem"/> over an element that was <paramref name="oldTotal"/> bytes long. When the new payload is shorter, its header is
    /// widened (2, 3, 5 or 9 bytes) so the element keeps its length, as the reference does when it overwrites in place; otherwise the header is minimal.
    /// </summary>
    internal static void PutPadded(JsonBuf o, ReadOnlySpan<byte> elem, int oldTotal)
    {
        if (!Header(elem, 0, out int type, out int hdr, out int size))
        {
            o.Put(elem);
            return;
        }

        int h = oldTotal - size;
        bool ok = type > JT.False && h > hdr && ((h == 2 && size <= 0xFF) || (h == 3 && size <= 0xFFFF) || h == 5 || h == 9);
        if (!ok)
        {
            o.Put(elem);
            return;
        }

        int w = h - 1;
        o.Put((byte)(((w == 1 ? 12 : w == 2 ? 13 : w == 4 ? 14 : 15) << 4) | type));
        for (int k = w - 1; k >= 0; k--)
        {
            o.Put(k >= 4 ? (byte)0 : (byte)(size >> (8 * k)));
        }

        o.Put(elem.Slice(hdr, size));
    }

    internal static void PutElem(JsonBuf o, int type, ReadOnlySpan<byte> payload)
    {
        PutHeader(o, type, payload.Length);
        o.Put(payload);
    }

    /// <summary>Starts a container: one byte is reserved for the header and replaced by <see cref="EndContainer"/> once the payload size is known.</summary>
    internal static int BeginContainer(JsonBuf o)
    {
        int at = o.Len;
        o.Put(0);
        return at;
    }

    internal static void EndContainer(JsonBuf o, int at, int type)
    {
        int size = o.Len - at - 1;
        int hl = HeaderLen(size);
        if (hl > 1)
        {
            o.Open(at + 1, hl - 1);
        }

        int save = o.Len;
        o.Len = at;
        PutHeader(o, type, size);
        o.Len = save;
    }

    // ---------------------------------------------------------------- text to number

    private static bool IsHex(byte c) => c is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

    internal static int HexVal(byte c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

    internal static bool IsHexDigit(byte c) => IsHex(c);
}

/// <summary>Reads JSON text, with the JSON5 extensions of json1 §3.6, into JSONB.</summary>
internal ref struct JsonTextParser
{
    private readonly ReadOnlySpan<byte> _s;
    private readonly JsonBuf _o;
    private int _i;
    private int _depth;

    /// <summary>True when the text used anything outside canonical JSON (RFC 8259).</summary>
    internal bool Json5;

    /// <summary>Byte offset of the first error.</summary>
    internal int ErrorAt;

    // Offset of the last closing bracket seen where a member could have started (an empty container, or after a trailing comma).
    // The reference reports a document followed by extra text at this position, so json_error_position agrees.
    private int _slotClose;
    private int _wsStart;

    // Start of the first comment or non-ASCII/JSON5 white space in the last SkipWs run, or -1: where a separator was expected but something else
    // followed, the reference reports that token rather than the character after it (observed: [1 /*c*/ 2] points at the slash).
    private int _odd;

    internal const int MaxDepth = 1000;

    internal JsonTextParser(ReadOnlySpan<byte> s, JsonBuf o)
    {
        _s = s;
        _o = o;
        _i = 0;
        _depth = 0;
        Json5 = false;
        ErrorAt = 0;
        _slotClose = 0;
        _wsStart = 0;
        _odd = -1;
    }

    private bool FailAfterSeparator(int at) => Fail(_odd >= 0 ? _odd : at);

    private bool Fail(int at)
    {
        ErrorAt = at;
        return false;
    }

    internal bool ParseDocument()
    {
        if (!SkipWs())
        {
            return false;
        }

        if (_i < _s.Length && _s[_i] is (byte)':' or (byte)',' or (byte)'}' or (byte)']')
        {
            // Unlike inside a container, a separator that opens the document is reported at its own position (observed).
            return Fail(_i);
        }

        if (!Value())
        {
            return false;
        }

        if (!SkipWs())
        {
            return Fail(_slotClose);
        }

        return _i == _s.Length || Fail(_slotClose);
    }

    /// <summary>Skips white space and comments; false for an unterminated comment.</summary>
    private bool SkipWs()
    {
        int n = _s.Length;
        _wsStart = _i;
        _odd = -1;
        while (_i < n)
        {
            byte c = _s[_i];
            if (c is 0x20 or 0x09 or 0x0A or 0x0D)
            {
                _i++;
                continue;
            }

            if (c is 0x0B or 0x0C)
            {
                Json5 = true;
                if (_odd < 0)
                {
                    _odd = _i;
                }

                _i++;
                continue;
            }

            if (c == '/' && _i + 1 < n)
            {
                if (_s[_i + 1] == '/')
                {
                    Json5 = true;
                    if (_odd < 0)
                    {
                        _odd = _i;
                    }

                    _i += 2;
                    while (_i < n && _s[_i] != 0x0A && _s[_i] != 0x0D)
                    {
                        _i++;
                    }

                    continue;
                }

                if (_s[_i + 1] == '*')
                {
                    Json5 = true;
                    if (_odd < 0)
                    {
                        _odd = _i;
                    }

                    int start = _i;
                    _i += 2;
                    while (_i + 1 < n && !(_s[_i] == '*' && _s[_i + 1] == '/'))
                    {
                        _i++;
                    }

                    if (_i + 1 >= n)
                    {
                        return Fail(start);
                    }

                    _i += 2;
                    continue;
                }

                return true;
            }

            int w = Space(_i);
            if (w > 0)
            {
                Json5 = true;
                if (_odd < 0)
                {
                    _odd = _i;
                }

                _i += w;
                continue;
            }

            return true;
        }

        return true;
    }

    /// <summary>Length of a non-ASCII white space character at <paramref name="i"/> (json1 §3.6), or 0.</summary>
    private int Space(int i)
    {
        int n = _s.Length;
        if (_s[i] == 0xC2 && i + 1 < n && _s[i + 1] == 0xA0)
        {
            return 2;
        }

        if (_s[i] == 0xE1 && i + 2 < n && _s[i + 1] == 0x9A && _s[i + 2] == 0x80)
        {
            return 3;
        }

        if (_s[i] == 0xE2 && i + 2 < n)
        {
            byte b1 = _s[i + 1];
            byte b2 = _s[i + 2];
            if (b1 == 0x80 && (b2 <= 0x8A || b2 is 0xA8 or 0xA9 or 0xAF) && b2 >= 0x80)
            {
                return 3;
            }

            if (b1 == 0x81 && b2 == 0x9F)
            {
                return 3;
            }
        }

        if (_s[i] == 0xE3 && i + 2 < n && _s[i + 1] == 0x80 && _s[i + 2] == 0x80)
        {
            return 3;
        }

        if (_s[i] == 0xEF && i + 2 < n && _s[i + 1] == 0xBB && _s[i + 2] == 0xBF)
        {
            return 3;
        }

        return 0;
    }

    private bool Literal(string word, int type)
    {
        if (_i + word.Length > _s.Length)
        {
            return false;
        }

        for (int k = 0; k < word.Length; k++)
        {
            if (_s[_i + k] != word[k])
            {
                return false;
            }
        }

        if (_i + word.Length < _s.Length && IsIdentByte(_s[_i + word.Length]))
        {
            return false;
        }

        _i += word.Length;
        _o.Put((byte)type);
        return true;
    }

    private static bool IsIdentByte(byte c) => c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'_' or (byte)'$';

    private bool MatchCi(string word)
    {
        if (_i + word.Length > _s.Length)
        {
            return false;
        }

        for (int k = 0; k < word.Length; k++)
        {
            if ((_s[_i + k] | 0x20) != word[k])
            {
                return false;
            }
        }

        _i += word.Length;
        return true;
    }

    private bool Value()
    {
        if (_i >= _s.Length)
        {
            return Fail(_wsStart);
        }

        byte c = _s[_i];
        if (c is (byte)':' or (byte)',' or (byte)'}' or (byte)']')
        {
            // A separator where a value belongs is reported where the white space before it began (observed).
            return Fail(_wsStart);
        }

        switch (c)
        {
            case (byte)'{':
                return Obj();
            case (byte)'[':
                return Arr();
            case (byte)'"':
            case (byte)'\'':
                return Str(c) >= 0;
            case (byte)'t':
                return Literal("true", JT.True) || Fail(_i);
            case (byte)'f':
                return Literal("false", JT.False) || Fail(_i);
            case (byte)'n':
                if (Literal("null", JT.Null))
                {
                    return true;
                }

                break;
        }

        if (c is (byte)'n' or (byte)'N' or (byte)'q' or (byte)'Q' or (byte)'s' or (byte)'S')
        {
            int word = _i;
            if ((MatchCi("nan") || MatchCi("qnan") || MatchCi("snan")) && (_i >= _s.Length || !IsIdentByte(_s[_i])))
            {
                Json5 = true;
                _o.Put(JT.Null);
                return true;
            }

            return Fail(word);
        }

        return Number();
    }

    private bool Number()
    {
        int n = _s.Length;
        int start = _i;
        bool neg = false;
        byte c = _s[_i];
        if (c == '-')
        {
            neg = true;
            _i++;
        }
        else if (c == '+')
        {
            Json5 = true;
            _i++;
        }

        if (_i >= n)
        {
            return Fail(_i - 1);
        }

        c = _s[_i];
        if (c is (byte)'I' or (byte)'i')
        {
            // observed: after a sign, Infinity or Inf is a complete value whatever follows (the caller reports what follows as the error);
            // without a sign a word that runs on is an error at its start, and so is any other word
            if ((MatchCi("infinity") || MatchCi("inf")) && (_s[start] is (byte)'+' or (byte)'-' || _i >= n || !IsIdentByte(_s[_i])))
            {
                Json5 = true;
                Jsonb.PutElem(_o, JT.Float, neg ? "-9e999"u8 : "9e999"u8);
                return true;
            }

            int word = start;
            return Fail(word);
        }

        int ds = _i;
        if (c == '0' && _i + 2 < n && (_s[_i + 1] | 0x20) == 'x' && Jsonb.IsHexDigit(_s[_i + 2]))
        {
            _i += 2;
            int hs = _i;
            while (_i < n && Jsonb.IsHexDigit(_s[_i]))
            {
                _i++;
            }

            if (_i == hs)
            {
                return Fail(_i - 1);
            }

            Json5 = true;
            WriteNumber(JT.Int5, neg, ds);
            return true;
        }

        int ip = _i;
        while (_i < n && _s[_i] is >= (byte)'0' and <= (byte)'9')
        {
            _i++;
        }

        int intDigits = _i - ip;
        if (intDigits > 1 && _s[ip] == '0')
        {
            // observed: reported at the second digit, or at the zero itself after a minus sign
            return Fail(neg ? ip : ip + 1);
        }

        int fracDigits = 0;
        bool dot = false;
        bool exp = false;
        if (_i < n && _s[_i] == '.')
        {
            dot = true;
            _i++;
            int fs = _i;
            while (_i < n && _s[_i] is >= (byte)'0' and <= (byte)'9')
            {
                _i++;
            }

            fracDigits = _i - fs;
        }

        if (intDigits == 0 && fracDigits == 0)
        {
            return Fail(_i > start ? _i - 1 : _i);
        }

        if (_i < n && (_s[_i] | 0x20) == 'e')
        {
            exp = true;
            _i++;
            if (_i < n && _s[_i] is (byte)'+' or (byte)'-')
            {
                _i++;
            }

            int es = _i;
            while (_i < n && _s[_i] is >= (byte)'0' and <= (byte)'9')
            {
                _i++;
            }

            if (_i == es)
            {
                return Fail(_i - 1);
            }
        }

        int type = JT.Int;
        if (dot || exp)
        {
            type = JT.Float;
            if (intDigits == 0 || (dot && fracDigits == 0))
            {
                type = JT.Float5;
                Json5 = true;
            }
        }

        // A second '.' glued to a number is an error at that character; other trailing text is reported as trailing text.
        // A second exponent letter after a complete exponent is likewise an error at that letter (observed: 1e1E-3 points at the E).
        if (_i < n && (_s[_i] == '.' || (exp && (_s[_i] | 0x20) == 'e')))
        {
            return Fail(_i);
        }

        WriteNumber(type, neg, ds);
        return true;
    }

    private void WriteNumber(int type, bool neg, int digitsStart)
    {
        int len = _i - digitsStart + (neg ? 1 : 0);
        Jsonb.PutHeader(_o, type, len);
        if (neg)
        {
            _o.Put((byte)'-');
        }

        _o.Put(_s.Slice(digitsStart, _i - digitsStart));
    }

    /// <summary>Reads a quoted string at the opening quote and writes it; returns the JSONB type, or -1.</summary>
    private int Str(byte quote)
    {
        int n = _s.Length;
        int open = _i;
        if (quote == '\'')
        {
            Json5 = true;
        }

        int start = ++_i;
        int type = JT.Text;
        while (true)
        {
            if (_i >= n)
            {
                Fail(n);
                return -1;
            }

            byte c = _s[_i];
            if (c == quote)
            {
                break;
            }

            if (c == '\\')
            {
                if (_i + 1 >= n)
                {
                    Fail(n);
                    return -1;
                }

                byte e = _s[_i + 1];
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
                        type = Math.Max(type, JT.TextJ);
                        _i += 2;
                        break;
                    case (byte)'u':
                        if (!Hex(_i + 2, 4))
                        {
                            Fail(_i + 1);
                            return -1;
                        }

                        type = Math.Max(type, JT.TextJ);
                        _i += 6;
                        break;
                    case (byte)'x':
                        if (!Hex(_i + 2, 2))
                        {
                            Fail(_i + 1);
                            return -1;
                        }

                        type = JT.Text5;
                        _i += 4;
                        break;
                    case (byte)'\'':
                    case (byte)'v':
                    case 0x0A:
                        type = JT.Text5;
                        _i += 2;
                        break;
                    case 0x0D:
                        type = JT.Text5;
                        _i += _i + 2 < n && _s[_i + 2] == 0x0A ? 3 : 2;
                        break;
                    case (byte)'0':
                        if (_i + 2 < n && _s[_i + 2] is >= (byte)'0' and <= (byte)'9')
                        {
                            Fail(_i + 1);
                            return -1;
                        }

                        type = JT.Text5;
                        _i += 2;
                        break;
                    case 0xE2 when _i + 3 < n && _s[_i + 2] == 0x80 && _s[_i + 3] is 0xA8 or 0xA9:
                        type = JT.Text5;
                        _i += 4;
                        break;
                    default:
                        Fail(_i + 1);
                        return -1;
                }
            }
            else
            {
                if (c < 0x20 || (c == '"' && quote == '\''))
                {
                    type = JT.Text5;
                }

                _i++;
            }
        }

        if (type == JT.Text5)
        {
            Json5 = true;
        }

        Jsonb.PutElem(_o, type, _s.Slice(start, _i - start));
        _i++;
        return type;
    }

    private bool Hex(int at, int count)
    {
        if (at + count > _s.Length)
        {
            return false;
        }

        for (int k = 0; k < count; k++)
        {
            if (!Jsonb.IsHexDigit(_s[at + k]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>An unquoted object key: ECMAScript identifier characters, with any non-space character above U+007F allowed (json1 §3.6).</summary>
    private bool Ident()
    {
        int n = _s.Length;
        int start = _i;
        int type = JT.Text;
        while (_i < n)
        {
            byte c = _s[_i];
            if (c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or (byte)'_' or (byte)'$' || (_i > start && c is >= (byte)'0' and <= (byte)'9'))
            {
                _i++;
            }
            else if (c == '\\' && _i + 1 < n && _s[_i + 1] == 'u' && Hex(_i + 2, 4))
            {
                type = JT.TextJ;
                _i += 6;
            }
            else if (c >= 0x80 && Space(_i) == 0)
            {
                _i++;
            }
            else
            {
                break;
            }
        }

        if (_i == start)
        {
            // Anything else in a key position is read as a value; whatever it is, it is not a label (observed: reported where the
            // white space before it began).
            if (_s[_i] is (byte)':' or (byte)',' or (byte)']')
            {
                // A separator where a label belongs is reported at its own position, with or without white space before it (observed).
                return Fail(_i);
            }

            int ws = _wsStart;
            int mark = _o.Len;
            if (!Value())
            {
                return false;
            }

            _o.Len = mark;
            return Fail(ws);
        }

        ReadOnlySpan<byte> word = _s.Slice(start, _i - start);
        if (type == JT.Text && IsKeyword(word))
        {
            _i = start;
            return Fail(_wsStart);
        }

        Json5 = true;
        _slotClose = start; // observed: text after a document that held an unquoted label is reported at the last such label
        Jsonb.PutElem(_o, type, _s.Slice(start, _i - start));
        return true;
    }

    /// <summary>True, false, null, NaN and Infinity (in their spellings) are values, not labels.</summary>
    private static bool IsKeyword(ReadOnlySpan<byte> w)
    {
        if (w.SequenceEqual("true"u8) || w.SequenceEqual("false"u8) || w.SequenceEqual("null"u8))
        {
            return true;
        }

        Span<byte> low = stackalloc byte[9];
        if (w.Length > 8)
        {
            return false;
        }

        for (int i = 0; i < w.Length; i++)
        {
            low[i] = (byte)(w[i] | 0x20);
        }

        ReadOnlySpan<byte> l = low[..w.Length];
        return l.SequenceEqual("nan"u8) || l.SequenceEqual("qnan"u8) || l.SequenceEqual("snan"u8) || l.SequenceEqual("inf"u8) || l.SequenceEqual("infinity"u8);
    }

    private bool Arr()
    {
        if (++_depth > MaxDepth)
        {
            return Fail(_i);
        }

        int at = Jsonb.BeginContainer(_o);
        _i++;
        if (!SkipWs())
        {
            return false;
        }

        if (_i < _s.Length && _s[_i] == ']')
        {
            _slotClose = _i;
            _i++;
        }
        else
        {
            while (true)
            {
                if (!Value() || !SkipWs())
                {
                    return false;
                }

                if (_i >= _s.Length)
                {
                    return FailAfterSeparator(_i);
                }

                if (_s[_i] == ',')
                {
                    _i++;
                    if (!SkipWs())
                    {
                        return false;
                    }

                    if (_i < _s.Length && _s[_i] == ']')
                    {
                        Json5 = true;
                        _slotClose = _i;
                        _i++;
                        break;
                    }

                    continue;
                }

                if (_s[_i] == ']')
                {
                    _i++;
                    break;
                }

                return FailAfterSeparator(_i);
            }
        }

        _depth--;
        Jsonb.EndContainer(_o, at, JT.Array);
        return true;
    }

    private bool Obj()
    {
        if (++_depth > MaxDepth)
        {
            return Fail(_i);
        }

        int at = Jsonb.BeginContainer(_o);
        _i++;
        if (!SkipWs())
        {
            return false;
        }

        if (_i < _s.Length && _s[_i] == '}')
        {
            _slotClose = _i;
            _i++;
        }
        else
        {
            while (true)
            {
                if (_i >= _s.Length)
                {
                    return Fail(_i);
                }

                byte c = _s[_i];
                if (c is (byte)'"' or (byte)'\'')
                {
                    if (Str(c) < 0)
                    {
                        return false;
                    }
                }
                else if (!Ident())
                {
                    return false;
                }

                if (!SkipWs())
                {
                    return false;
                }

                if (_i < _s.Length && _s[_i] is (byte)'"' or (byte)'\'')
                {
                    // A string where the colon belongs is read through first: if it is damaged, that is the error reported.
                    int quoteAt = _i;
                    int mark = _o.Len;
                    if (Str(_s[_i]) < 0)
                    {
                        return false;
                    }

                    _o.Len = mark;
                    return FailAfterSeparator(quoteAt);
                }

                if (_i < _s.Length && _s[_i] is (byte)'[' or (byte)'{')
                {
                    // And a container: it is read through, so damage inside it is the error reported (observed: {"b"[:{}} points at the colon).
                    int contAt = _i;
                    int mark = _o.Len;
                    if (!Value())
                    {
                        return false;
                    }

                    _o.Len = mark;
                    return FailAfterSeparator(contAt);
                }

                if (_i < _s.Length && _s[_i] is (byte)'-' or (byte)'+' or (byte)'.' or (>= (byte)'0' and <= (byte)'9'))
                {
                    // Likewise a number where the colon belongs: a damaged number is the error reported (observed: {"a"01:1} points at the 1).
                    int numAt = _i;
                    int mark = _o.Len;
                    if (!Number())
                    {
                        return false;
                    }

                    _o.Len = mark;
                    return FailAfterSeparator(numAt);
                }

                if (_i >= _s.Length || _s[_i] != ':')
                {
                    return FailAfterSeparator(_i);
                }

                _i++;
                if (!SkipWs() || !Value() || !SkipWs())
                {
                    return false;
                }

                if (_i >= _s.Length)
                {
                    return FailAfterSeparator(_i);
                }

                if (_s[_i] == ',')
                {
                    _i++;
                    if (!SkipWs())
                    {
                        return false;
                    }

                    if (_i < _s.Length && _s[_i] == '}')
                    {
                        Json5 = true;
                        _slotClose = _i;
                        _i++;
                        break;
                    }

                    continue;
                }

                if (_s[_i] == '}')
                {
                    _i++;
                    break;
                }

                return FailAfterSeparator(_i);
            }
        }

        _depth--;
        Jsonb.EndContainer(_o, at, JT.Object);
        return true;
    }
}
