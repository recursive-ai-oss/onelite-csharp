using System.Globalization;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Vm;

/// <summary>
/// The printf of the pinned library (printf.html), as the format() and printf() SQL functions use it. Floating-point conversions render at most
/// 16 significant digits, or 26 with the "!" flag, and fill the rest of a longer precision with zeros.
/// </summary>
internal static class Printf
{
    private const int MaxDigitsPlain = 16;
    private const int MaxDigitsAlt = 26;

    // Observed: string conversions stop at the first NUL byte of their argument, as C strings do.
    private static ReadOnlySpan<byte> CString(ReadOnlySpan<byte> s)
    {
        int nul = s.IndexOf((byte)0);
        return nul < 0 ? s : s[..nul];
    }

    internal static void Format(ReadOnlySpan<Mem> args, ref Mem dst, ref byte[]? own)
    {
        Span<byte> tmp = stackalloc byte[Values.MaxRealText];
        if (args.Length == 0 || args[0].Kind == StorageClass.Null)
        {
            Values.SetNull(ref dst);
            return;
        }

        ReadOnlySpan<byte> f = Values.AsText(args[0], tmp);
        if (f.Length == 0)
        {
            // An empty format string gives NULL, not an empty text (observed).
            Values.SetNull(ref dst);
            return;
        }

        var sk = new Sink(ref own, f.Length + 16);
        int argi = 1;
        for (int i = 0; i < f.Length; i++)
        {
            byte c = f[i];
            if (c != (byte)'%')
            {
                sk.Put(c);
                continue;
            }

            i++;
            if (i >= f.Length)
            {
                // A lone '%' at the very end is kept as it is (observed).
                sk.Put((byte)'%');
                break;
            }

            // flags
            bool left = false, plus = false, space = false, zero = false, alt1 = false, alt2 = false, comma = false;
            for (; i < f.Length; i++)
            {
                switch ((char)f[i])
                {
                    case '-': left = true; continue;
                    case '+': plus = true; continue;
                    case ' ': space = true; continue;
                    case '0': zero = true; continue;
                    case '#': alt1 = true; continue;
                    case '!': alt2 = true; continue;
                    case ',': comma = true; continue;
                }

                break;
            }

            // width
            long width = 0;
            if (i < f.Length && f[i] == (byte)'*')
            {
                i++;
                width = NextArg(args, ref argi, out Mem wa) ? Values.ToInt(wa) : 0;
                if (width < 0)
                {
                    left = true;
                    width = -width;
                }
            }
            else
            {
                while (i < f.Length && f[i] is >= (byte)'0' and <= (byte)'9')
                {
                    width = Math.Min((width * 10) + (f[i] - '0'), int.MaxValue);
                    i++;
                }
            }

            // precision
            long prec = -1;
            if (i < f.Length && f[i] == (byte)'.')
            {
                i++;
                prec = 0;
                if (i < f.Length && f[i] == (byte)'*')
                {
                    i++;
                    prec = NextArg(args, ref argi, out Mem pa) ? Values.ToInt(pa) : 0;
                    if (prec < 0)
                    {
                        prec = -1;
                    }
                }
                else
                {
                    while (i < f.Length && f[i] is >= (byte)'0' and <= (byte)'9')
                    {
                        prec = Math.Min((prec * 10) + (f[i] - '0'), int.MaxValue);
                        i++;
                    }
                }
            }

            // length modifiers are accepted and ignored: format() always uses 64-bit integers
            while (i < f.Length && f[i] == (byte)'l')
            {
                i++;
            }

            if (i >= f.Length)
            {
                // A conversion cut off after its flags, width or precision gives NULL (observed).
                Values.SetNull(ref dst);
                return;
            }

            char type = (char)f[i];
            if (type == '%')
            {
                PadText(ref sk, "%"u8, -1, width, left, false);
                continue;
            }

            bool has = NextArg(args, ref argi, out Mem a);
            switch (type)
            {
                case 'd':
                case 'i':
                case 'u':
                case 'x':
                case 'X':
                case 'o':
                {
                    long v = has ? Values.ToInt(a) : 0;
                    Span<byte> digits = stackalloc byte[80];
                    int nd = 0;
                    bool neg = false;
                    ulong u;
                    if (type is 'd' or 'i')
                    {
                        neg = v < 0;
                        u = neg ? (ulong)(-(v + 1)) + 1 : (ulong)v;
                    }
                    else
                    {
                        u = (ulong)v;
                    }

                    ulong radix = type is 'x' or 'X' ? 16UL : type == 'o' ? 8UL : 10UL;
                    string alphabet = type == 'X' ? "0123456789ABCDEF" : "0123456789abcdef";
                    do
                    {
                        digits[nd++] = (byte)alphabet[(int)(u % radix)];
                        u /= radix;
                    }
                    while (u != 0);

                    Span<byte> body = stackalloc byte[160];
                    int bl = 0;
                    // precision is the minimum digit count
                    int pad = prec > nd ? (int)Math.Min(prec - nd, 70) : 0;
                    int total = nd + pad;
                    for (int k = 0; k < total; k++)
                    {
                        int idx = total - 1 - k;
                        body[bl++] = idx < nd ? digits[idx] : (byte)'0';
                    }

                    if (comma && type is 'd' or 'i' or 'u')   // thousands separators
                    {
                        Span<byte> withCommas = stackalloc byte[240];
                        int wl = 0;
                        for (int k = 0; k < bl; k++)
                        {
                            if (k > 0 && (bl - k) % 3 == 0)
                            {
                                withCommas[wl++] = (byte)',';
                            }

                            withCommas[wl++] = body[k];
                        }

                        withCommas[..wl].CopyTo(body);
                        bl = wl;
                    }

                    string prefix = neg ? "-" : (type is 'd' or 'i') && plus ? "+" : (type is 'd' or 'i') && space ? " " : string.Empty;
                    if (alt1 && v != 0)
                    {
                        prefix += type == 'x' ? "0x" : type == 'X' ? "0X" : type == 'o' ? "0" : string.Empty;
                    }

                    Pad(ref sk, prefix, body[..bl], width, left && !zero, zero && prec < 0);
                    break;
                }

                case 'f':
                case 'e':
                case 'E':
                case 'g':
                case 'G':
                {
                    double v = has ? (a.Kind == StorageClass.Null ? 0.0 : Values.ToReal(a)) : 0.0;
                    FloatConv(ref sk, v, type, Math.Min(prec, 100_000_000), width, left, plus, space, zero, alt1, alt2, comma);
                    break;
                }

                case 's':
                case 'z':
                {
                    ReadOnlySpan<byte> s = has && a.Kind != StorageClass.Null ? CString(Values.AsText(a, tmp)) : default;
                    PadText(ref sk, s, prec, width, left, alt2);
                    break;
                }

                case 'c':
                {
                    ReadOnlySpan<byte> s = has && a.Kind != StorageClass.Null ? Values.AsText(a, tmp) : default;
                    // The first character of the argument; with none (a NULL, empty or missing argument) a NUL byte. A precision repeats it.
                    ReadOnlySpan<byte> one = s.Length == 0 ? "\0"u8 : s.Slice(0, Math.Min(CharLen(s, 0), s.Length));
                    long reps = prec > 1 ? Math.Min(prec, 1_000_000) : 1;
                    long pad = width - reps;
                    if (!left)
                    {
                        sk.Put((byte)' ', (int)Math.Max(0, Math.Min(pad, int.MaxValue)));
                    }

                    for (long r = 0; r < reps; r++)
                    {
                        sk.Put(one);
                    }

                    if (left)
                    {
                        sk.Put((byte)' ', (int)Math.Max(0, Math.Min(pad, int.MaxValue)));
                    }

                    break;
                }

                case 'q':
                case 'Q':
                case 'w':
                {
                    if (has && a.Kind == StorageClass.Null)
                    {
                        if (type == 'Q')
                        {
                            PadText(ref sk, "NULL"u8, -1, width, left, alt2);
                        }
                        else
                        {
                            // %q and %w of NULL print "(NULL)"
                            PadText(ref sk, "(NULL)"u8, prec, width, left, alt2);
                        }

                        break;
                    }

                    ReadOnlySpan<byte> s = has ? CString(Values.AsText(a, tmp)) : default;
                    int take = Take(s, prec, alt2);
                    s = s[..take];
                    byte qc = type == 'w' ? (byte)'"' : (byte)'\'';
                    var inner = new List<byte>(s.Length + 2);
                    if (type == 'Q')
                    {
                        inner.Add((byte)'\'');
                    }

                    foreach (byte b in s)
                    {
                        if (b == qc)
                        {
                            inner.Add(qc);
                        }

                        inner.Add(b);
                    }

                    if (type == 'Q')
                    {
                        inner.Add((byte)'\'');
                    }

                    PadText(ref sk, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(inner), -1, width, left, alt2);
                    break;
                }

                default:
                    // An unknown conversion makes the whole result NULL, as the reference does.
                    Values.SetNull(ref dst);
                    return;
            }
        }

        sk.Finish(ref dst, ref own, StorageClass.Text);
    }

    private static bool NextArg(ReadOnlySpan<Mem> args, ref int argi, out Mem a)
    {
        if (argi < args.Length)
        {
            a = args[argi++];
            return true;
        }

        a = default; // a missing argument reads as NULL
        return true;
    }

    private static int CharLen(ReadOnlySpan<byte> s, int i)
    {
        int j = i + 1;
        if (s[i] >= 0xC0)
        {
            while (j < s.Length && (s[j] & 0xC0) == 0x80)
            {
                j++;
            }
        }

        return j - i;
    }

    private static int Take(ReadOnlySpan<byte> s, long prec, bool chars)
    {
        if (prec < 0)
        {
            return s.Length;
        }

        if (!chars)
        {
            return (int)Math.Min(prec, s.Length);
        }

        int i = 0;
        long n = 0;
        while (i < s.Length && n < prec)
        {
            i += CharLen(s, i);
            n++;
        }

        return i;
    }

    private static void PadText(ref Sink sk, ReadOnlySpan<byte> s, long prec, long width, bool left, bool chars)
    {
        s = s[..Take(s, prec, chars)];
        long len = s.Length;
        if (chars)
        {
            len = 0;
            for (int i = 0; i < s.Length; i += CharLen(s, i))
            {
                len++;
            }
        }

        long pad = width - len;
        if (!left)
        {
            sk.Put((byte)' ', (int)Math.Max(0, Math.Min(pad, int.MaxValue)));
        }

        sk.Put(s);
        if (left)
        {
            sk.Put((byte)' ', (int)Math.Max(0, Math.Min(pad, int.MaxValue)));
        }
    }

    private static void Pad(ref Sink sk, string prefix, ReadOnlySpan<byte> body, long width, bool left, bool zero)
    {
        long len = prefix.Length + body.Length;
        long pad = Math.Max(0, width - len);
        if (!left && !zero)
        {
            sk.Put((byte)' ', (int)Math.Min(pad, int.MaxValue));
        }

        sk.PutAscii(prefix);
        if (!left && zero)
        {
            sk.Put((byte)'0', (int)Math.Min(pad, int.MaxValue));
        }

        sk.Put(body);
        if (left)
        {
            sk.Put((byte)' ', (int)Math.Min(pad, int.MaxValue));
        }
    }

    /// <summary>
    /// %f, %e, %g with the digit limit of the reference: the decimal expansion is cut at 16 significant digits (26 with "!"), rounded half up
    /// at the digit asked for, and anything beyond the limit is zero.
    /// </summary>
    private static void FloatConv(ref Sink sk, double v, char type, long prec, long width, bool left, bool plus, bool space, bool zero, bool alt1, bool alt2, bool comma)
    {
        bool neg = v < 0;
        string sign = neg ? "-" : plus ? "+" : space ? " " : string.Empty;
        if (double.IsNaN(v))
        {
            Pad(ref sk, string.Empty, "NaN"u8, width, left, false);
            return;
        }

        if (double.IsInfinity(v))
        {
            Pad(ref sk, sign, "Inf"u8, width, left, false);
            return;
        }

        int limit = alt2 ? MaxDigitsAlt : MaxDigitsPlain;
        if (prec < 0)
        {
            prec = 6;
        }

        double a = Math.Abs(v);
        bool isE = type is 'e' or 'E';
        bool isG = type is 'g' or 'G';
        if (isG && prec == 0)
        {
            prec = 1;
        }

        // Significant digits and the decimal exponent of the first one.
        string d;
        int exp10;
        if (a == 0)
        {
            d = new string('0', limit);
            exp10 = 0;
        }
        else
        {
            string e = a.ToString("E" + (limit + 3), CultureInfo.InvariantCulture);
            int ePos = e.IndexOf('E', StringComparison.Ordinal);
            d = e[..ePos].Replace(".", string.Empty, StringComparison.Ordinal);
            exp10 = int.Parse(e.AsSpan(ePos + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        }

        // How many significant digits the conversion asks for.
        long want;
        if (isE)
        {
            want = prec + 1;
        }
        else if (isG)
        {
            want = prec;
        }
        else
        {
            want = exp10 + 1 + prec; // digits up to prec places after the point
        }

        // Round to `want` digits (at most `limit`), carrying into the exponent.
        Round(ref d, ref exp10, (int)Math.Max(0, Math.Min(want, limit)));
        string digs = d;

        string body;
        if (isG)
        {
            long p = prec;
            // %g chooses by the exponent of the rounded value
            if (exp10 < -4 || exp10 >= p)
            {
                body = ExpForm(digs, exp10, (int)p - 1, alt1, alt2, true, type == 'G' ? 'E' : 'e');
            }
            else
            {
                body = FixedForm(digs, exp10, (int)(p - 1 - exp10), alt1, alt2, true, comma);
            }
        }
        else if (isE)
        {
            body = ExpForm(digs, exp10, (int)Math.Min(prec, int.MaxValue / 2), alt1, alt2, alt2, type == 'E' ? 'E' : 'e'); // observed: the ! flag also drops trailing zeros, keeping one digit after the point
        }
        else
        {
            body = FixedForm(digs, exp10, (int)Math.Min(prec, int.MaxValue / 2), alt1, alt2, alt2, comma);
        }

        Pad(ref sk, sign, System.Text.Encoding.ASCII.GetBytes(body), width, left && !zero, zero);
    }

    /// <summary>Rounds the digit string to <paramref name="n"/> significant digits (half up on the next digit), padding with zeros; handles the carry out.</summary>
    private static void Round(ref string d, ref int exp10, int n)
    {
        if (n <= 0)
        {
            // Rounding at a position before the first digit: the value is 0 or one unit of that position.
            bool up = n == 0 && d.Length > 0 && d[0] >= '5';
            d = up ? "1" : "0";
            if (up)
            {
                exp10++;
            }
            else
            {
                exp10 = 0;
            }

            return;
        }

        if (d.Length <= n)
        {
            d = d.PadRight(n, '0');
            return;
        }

        bool roundUp = d[n] >= '5';
        char[] c = d[..n].ToCharArray();
        if (roundUp)
        {
            int i = n - 1;
            while (i >= 0)
            {
                if (c[i] == '9')
                {
                    c[i] = '0';
                    i--;
                }
                else
                {
                    c[i]++;
                    break;
                }
            }

            if (i < 0)
            {
                d = "1" + new string(c[..(n - 1)]);
                exp10++;
                return;
            }
        }

        d = new string(c);
    }

    private static string FixedForm(string digs, int exp10, int places, bool alt1, bool alt2, bool stripZeros, bool comma)
    {
        var sb = new System.Text.StringBuilder();
        // integer part
        if (exp10 < 0)
        {
            sb.Append('0');
        }
        else
        {
            for (int i = 0; i <= exp10; i++)
            {
                sb.Append(i < digs.Length ? digs[i] : '0');
            }
        }

        if (comma)
        {
            string ip = sb.ToString();
            sb.Clear();
            for (int k = 0; k < ip.Length; k++)
            {
                if (k > 0 && (ip.Length - k) % 3 == 0)
                {
                    sb.Append(',');
                }

                sb.Append(ip[k]);
            }
        }

        var frac = new System.Text.StringBuilder();
        for (int k = 0; k < places; k++)
        {
            int idx = exp10 + 1 + k;
            frac.Append(idx >= 0 && idx < digs.Length ? digs[idx] : '0');
        }

        string fs = frac.ToString();
        if (stripZeros && !alt1)
        {
            fs = fs.TrimEnd('0');
        }

        if (fs.Length == 0 && alt2)
        {
            fs = "0";
        }

        if (fs.Length > 0 || alt1)
        {
            sb.Append('.').Append(fs);
        }

        return sb.ToString();
    }

    private static string ExpForm(string digs, int exp10, int places, bool alt1, bool alt2, bool stripZeros, char e)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(digs[0]);
        var frac = new System.Text.StringBuilder();
        for (int k = 0; k < places; k++)
        {
            frac.Append(1 + k < digs.Length ? digs[1 + k] : '0');
        }

        string fs = frac.ToString();
        if (stripZeros && !alt1)
        {
            fs = fs.TrimEnd('0');
        }

        if (fs.Length == 0 && alt2)
        {
            fs = "0";
        }

        if (fs.Length > 0 || alt1)
        {
            sb.Append('.').Append(fs);
        }

        sb.Append(e).Append(exp10 < 0 ? '-' : '+');
        int ae = Math.Abs(exp10);
        sb.Append(ae < 10 ? "0" + ae.ToString(CultureInfo.InvariantCulture) : ae.ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
