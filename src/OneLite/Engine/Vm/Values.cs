using RecursiveAI.OneLite.Storage;
using System.Globalization;
using System.Text;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Vm;

/// <summary>Column and expression affinity (datatype3 §3). <see cref="None"/> is what the documentation calls BLOB affinity.</summary>
internal enum Affinity : byte
{
    None,
    Text,
    Numeric,
    Integer,
    Real,

    /// <summary>Only for comparisons: a column declared with no type or BLOB. It behaves as no affinity, except that TEXT affinity is not applied to it.</summary>
    Blob,
}

/// <summary>The built-in collating sequences (datatype3 §7.1).</summary>
internal enum CollKind : byte
{
    Binary,
    NoCase,
    RTrim,
}

/// <summary>
/// Value semantics of the engine: numeric conversion, text rendering, affinity, comparison and arithmetic. Every rule here
/// comes from datatype3 (storage classes, affinity, comparison), lang_expr (operators) or was settled by running the pinned shell.
/// Registers hold text as UTF-8; <see cref="Mem"/> values reference their bytes and are never copied implicitly.
/// </summary>
internal static class Values
{
    /// <summary>The longest text a value may have (compile option MAX_LENGTH of the pinned build).</summary>
    internal const int MaxLength = 1_000_000_000;

    // ------------------------------------------------------------------ constructors

    internal static void SetNull(ref Mem m)
    {
        m.Kind = StorageClass.Null;
        m.Subtype = 0;
        m.Buffer = null;
    }

    internal static void SetInt(ref Mem m, long v)
    {
        m.Kind = StorageClass.Integral;
        m.Subtype = 0;
        m.IntValue = v;
        m.Buffer = null;
    }

    internal static void SetReal(ref Mem m, double v)
    {
        m.Kind = StorageClass.Real;
        m.Subtype = 0;
        m.RealValue = v;
        m.Buffer = null;
    }

    /// <summary>Stores bytes in the register's own buffer (grown when needed) and points the value at them.</summary>
    internal static void SetBytes(ref Mem m, ref byte[]? own, StorageClass kind, ReadOnlySpan<byte> src)
    {
        if (own == null || own.Length < src.Length)
        {
            own = new byte[Math.Max(src.Length, 32)];
        }

        src.CopyTo(own);
        m.Kind = kind;
        m.Subtype = 0;
        m.Buffer = own;
        m.Offset = 0;
        m.Length = src.Length;
    }

    /// <summary>Makes a value that references fixed bytes (a literal, a bound parameter): no copy.</summary>
    internal static void SetRef(ref Mem m, StorageClass kind, byte[] bytes)
    {
        m.Kind = kind;
        m.Subtype = 0;
        m.Buffer = bytes;
        m.Offset = 0;
        m.Length = bytes.Length;
    }

    internal static bool IsNumber(in Mem m) => m.Kind is StorageClass.Integral or StorageClass.Real;

    // ------------------------------------------------------------------ text to number

    private static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\n' or 0x0b or 0x0c or (byte)'\r';

    /// <summary>
    /// Reads the longest numeric prefix of a text (datatype3 §3: "well-formed" integer or real literal, leading and trailing spaces allowed).
    /// </summary>
    /// <returns>Integral or Real for a number found, Null when the text has no numeric prefix. <paramref name="whole"/> is true when
    /// nothing but spaces follows the number, which is what affinity conversion requires.</returns>
    internal static StorageClass ParseNumber(ReadOnlySpan<byte> s, out long iv, out double dv, out bool whole)
        => ParseNumber(s, out iv, out dv, out whole, out _);

    // nulNext: true when the number, and the spaces after it, are followed by a NUL byte (the text is read as a C string there).
    internal static StorageClass ParseNumber(ReadOnlySpan<byte> s, out long iv, out double dv, out bool whole, out bool nulNext)
    {
        nulNext = false;
        iv = 0;
        dv = 0;
        whole = false;
        int p = 0;
        int n = s.Length;
        while (p < n && IsSpace(s[p]))
        {
            p++;
        }

        int start = p;
        bool neg = false;
        if (p < n && (s[p] == '-' || s[p] == '+'))
        {
            neg = s[p] == '-';
            p++;
        }

        int digitsStart = p;
        while (p < n && s[p] is >= (byte)'0' and <= (byte)'9')
        {
            p++;
        }

        int intDigits = p - digitsStart;
        bool real = false;
        int fracDigits = 0;
        if (p < n && s[p] == '.')
        {
            int q = p + 1;
            while (q < n && s[q] is >= (byte)'0' and <= (byte)'9')
            {
                q++;
            }

            fracDigits = q - p - 1;
            if (intDigits > 0 || fracDigits > 0)
            {
                real = true;
                p = q;
            }
        }

        if (intDigits == 0 && fracDigits == 0)
        {
            return StorageClass.Null;
        }

        if (p < n && (s[p] == 'e' || s[p] == 'E'))
        {
            int q = p + 1;
            if (q < n && (s[q] == '-' || s[q] == '+'))
            {
                q++;
            }

            int ed = q;
            while (q < n && s[q] is >= (byte)'0' and <= (byte)'9')
            {
                q++;
            }

            if (q > ed)
            {
                real = true;
                p = q;
            }
        }

        int end = p;
        while (p < n && IsSpace(s[p]))
        {
            p++;
        }

        whole = p == n;
        nulNext = p < n && s[p] == 0;
        if (!real)
        {
            // Accumulate as a negative number so that -9223372036854775808 fits.
            ulong acc = 0;
            bool overflow = false;
            for (int i = digitsStart; i < end; i++)
            {
                uint d = (uint)(s[i] - '0');
                if (acc > (ulong.MaxValue - d) / 10)
                {
                    overflow = true;
                    break;
                }

                acc = (acc * 10) + d;
            }

            if (!overflow && (neg ? acc <= 9223372036854775808UL : acc <= 9223372036854775807UL))
            {
                iv = neg ? (long)(0 - acc) : (long)acc;
                return StorageClass.Integral;
            }
        }

        dv = ParseDecimalText(s[start..end]);
        return StorageClass.Real;
    }

    /// <summary>
    /// Reads well-formed decimal text (sign, digits, optional point and exponent) as the reference does: digits go into a 64-bit integer until it
    /// holds about 19 of them, later digits are dropped (before the point they only raise the exponent), then the value is rounded once.
    /// Observed against the pinned shell over 60,000 random strings of 17 to 40 digits: it differs from a correctly rounded parse of all
    /// digits in about one string in a thousand, and this rule matched every one of those.
    /// </summary>
    internal static double ParseRealLiteral(string text)
        => ParseDecimalText(System.Text.Encoding.ASCII.GetBytes(text.Replace("_", string.Empty, StringComparison.Ordinal)));

    internal static double ParseDecimalText(ReadOnlySpan<byte> s)
    {
        const ulong Limit = (long.MaxValue - 9) / 10;
        int p = 0;
        bool neg = false;
        if (p < s.Length && (s[p] == '-' || s[p] == '+'))
        {
            neg = s[p] == '-';
            p++;
        }

        ulong acc = 0;
        long exp10 = 0;
        for (; p < s.Length && s[p] is >= (byte)'0' and <= (byte)'9'; p++)
        {
            if (acc < Limit)
            {
                acc = (acc * 10) + (uint)(s[p] - '0');
            }
            else
            {
                exp10++;
            }
        }

        if (p < s.Length && s[p] == '.')
        {
            for (p++; p < s.Length && s[p] is >= (byte)'0' and <= (byte)'9'; p++)
            {
                if (acc < Limit)
                {
                    acc = (acc * 10) + (uint)(s[p] - '0');
                    exp10--;
                }
            }
        }

        if (p < s.Length && (s[p] == 'e' || s[p] == 'E'))
        {
            p++;
            bool eneg = false;
            if (p < s.Length && (s[p] == '-' || s[p] == '+'))
            {
                eneg = s[p] == '-';
                p++;
            }

            long e = 0;
            for (; p < s.Length && s[p] is >= (byte)'0' and <= (byte)'9'; p++)
            {
                e = Math.Min((e * 10) + (s[p] - '0'), 100000);
            }

            exp10 += eneg ? -e : e;
        }

        if (acc == 0)
        {
            return neg ? -0.0 : 0.0;
        }

        exp10 = Math.Clamp(exp10, -100000, 100000);
        double d = double.Parse(string.Create(CultureInfo.InvariantCulture, $"{acc}e{exp10}"), NumberStyles.Float, CultureInfo.InvariantCulture);
        return neg ? -d : d;
    }

    /// <summary>Converts a value to a number for arithmetic (lang_expr): text and blobs use their numeric prefix, no prefix means 0.</summary>
    internal static StorageClass ToNumeric(in Mem m, out long iv, out double dv)
    {
        if (m.Kind == StorageClass.Blob && (byte)BlobEncoding > (byte)TextEncoding.Utf8)
        {
            StorageClass kb = ParseNumber(BlobAsText(m.Bytes), out iv, out dv, out _);
            if (kb == StorageClass.Null)
            {
                iv = 0;
                dv = 0;
                return StorageClass.Integral;
            }

            return kb;
        }

        switch (m.Kind)
        {
            case StorageClass.Integral:
                iv = m.IntValue;
                dv = 0;
                return StorageClass.Integral;
            case StorageClass.Real:
                iv = 0;
                dv = m.RealValue;
                return StorageClass.Real;
            case StorageClass.Text:
            case StorageClass.Blob:
            {
                StorageClass k = ParseNumber(m.Bytes, out iv, out dv, out _, out bool nulNext);
                if (k == StorageClass.Null)
                {
                    iv = 0;
                    dv = 0;
                    return StorageClass.Integral;
                }

                // Observed: an integer that is followed (after any spaces) directly by a NUL byte reads as a REAL in arithmetic; digits
                // followed by any other byte, even one before a later NUL, stay an integer.
                if (k == StorageClass.Integral && nulNext)
                {
                    dv = iv;
                    return StorageClass.Real;
                }

                return k;
            }

            default:
                iv = 0;
                dv = 0;
                return StorageClass.Integral;
        }
    }

    /// <summary>
    /// The integer a text or blob converts to where an integer is needed (CAST AS INTEGER, bitwise operators): the longest leading
    /// run of an optional sign and decimal digits after blanks, saturating at the 64-bit ends; "1e3" is 1 (lang_expr CAST).
    /// </summary>
    internal static long IntegerPrefix(ReadOnlySpan<byte> s)
    {
        int i = 0;
        while (i < s.Length && (s[i] == ' ' || (s[i] >= 9 && s[i] <= 13)))
        {
            i++;
        }

        bool neg = false;
        if (i < s.Length && (s[i] == '-' || s[i] == '+'))
        {
            neg = s[i] == '-';
            i++;
        }

        ulong v = 0;
        bool over = false;
        for (; i < s.Length && s[i] >= '0' && s[i] <= '9'; i++)
        {
            if (v > (ulong.MaxValue - 9) / 10)
            {
                over = true;
                continue;
            }

            v = (v * 10) + (ulong)(s[i] - '0');
        }

        if (neg)
        {
            return over || v > 9223372036854775808UL ? long.MinValue : unchecked((long)(0 - v));
        }

        return over || v > long.MaxValue ? long.MaxValue : (long)v;
    }

    /// <summary>Real to integer as the C API does it: truncation toward zero, saturating at the ends (lang_expr CAST).</summary>
    internal static long DoubleToInt(double d)
    {
        if (double.IsNaN(d))
        {
            return 0;
        }

        if (d >= 9223372036854775808.0)
        {
            return long.MaxValue;
        }

        if (d <= -9223372036854775808.0)
        {
            return long.MinValue;
        }

        return (long)d;
    }

    internal static long ToInt(in Mem m)
    {
        if (m.Kind == StorageClass.Blob)
        {
            return IntegerPrefix(BlobAsText(m.Bytes));
        }

        if (m.Kind == StorageClass.Text)
        {
            return IntegerPrefix(m.Bytes);
        }

        StorageClass k = ToNumeric(m, out long iv, out double dv);
        return k == StorageClass.Integral ? iv : DoubleToInt(dv);
    }

    internal static double ToReal(in Mem m)
    {
        StorageClass k = ToNumeric(m, out long iv, out double dv);
        return k == StorageClass.Integral ? iv : dv;
    }

    /// <summary>The truth value of a non-NULL value (lang_expr "boolean expressions"): non-zero numbers and text that reads as one.</summary>
    internal static bool IsTrue(in Mem m)
    {
        StorageClass k = ToNumeric(m, out long iv, out double dv);
        return k == StorageClass.Integral ? iv != 0 : dv != 0;
    }

    // ------------------------------------------------------------------ number to text

    /// <summary>Formats an integer as UTF-8 digits.</summary>
    internal static int FormatInt(long v, Span<byte> dst)
    {
        v.TryFormat(dst, out int n, default, CultureInfo.InvariantCulture);
        return n;
    }

    /// <summary>The longest text <see cref="FormatReal"/> produces.</summary>
    internal const int MaxRealText = 40;

    /// <summary>
    /// The decimal digits the reference prints for a positive finite real, without trailing zeros, and the decimal exponent of the first digit.
    /// Settled by black-box measurement against the pinned shell (about 10^6 random doubles; floatingpoint documents only that a value prints with
    /// up to 17 significant digits). The shell prints the 15-digit form when that form reads back as the same value and is accepted by a tail test;
    /// otherwise 17 digits, whose last digit it rounds a little differently from exact rounding when the spacing of doubles is close to the
    /// last digit's unit. See docs/engine.md for the rule, its constants and the measured residual.
    /// </summary>
    private static int RealDigits(double v, Span<byte> digits, out int exp)
    {
        // Exact decimal expansion, 31 significant digits: enough to see the tail at the 15th and 17th digit to better than 1e-12 units.
        Span<byte> buf = stackalloc byte[48];
        Span<char> cs = stackalloc char[48];
        v.TryFormat(cs, out int cn, "E30", CultureInfo.InvariantCulture);
        int bl = 0;
        int p = 0;
        for (; p < cn && cs[p] != 'E'; p++)
        {
            if (cs[p] != '.')
            {
                buf[bl++] = (byte)cs[p];
            }
        }

        int e10 = int.Parse(cs.Slice(p + 1, cn - p - 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        // The 15-digit candidate with its signed tail in units of its last digit.
        long dec15 = 0;
        for (int i = 0; i < 15; i++)
        {
            dec15 = (dec15 * 10) + (buf[i] - '0');
        }

        double frac = 0;
        for (int i = 24; i >= 15; i--)
        {
            frac = (frac + (buf[i] - '0')) / 10;
        }

        double tail = frac;
        int e15 = e10;
        if (frac >= 0.5)
        {
            dec15++;
            tail = frac - 1;
            if (dec15 == 1_000_000_000_000_000)
            {
                dec15 = 100_000_000_000_000;
                e15++;
            }
        }

        int k = 15;
        long strip = dec15;
        while (strip % 10 == 0)
        {
            strip /= 10;
            k--;
        }

        bool subnormal = v < 2.2250738585072014e-308;
        if (!subnormal && (k <= 13 || (k == 14 && tail < 0.005) || (k >= 15 && Math.Abs(tail) < 0.005)))
        {
            Span<char> t = stackalloc char[32];
            dec15.TryFormat(t, out int tl, default, CultureInfo.InvariantCulture);
            t[tl++] = 'E';
            (e15 - 14).TryFormat(t[tl..], out int el, default, CultureInfo.InvariantCulture);
            if (double.TryParse(t[..(tl + el)], NumberStyles.Float, CultureInfo.InvariantCulture, out double back) && back == v)
            {
                long x = strip;
                for (int i = k - 1; i >= 0; i--)
                {
                    digits[i] = (byte)('0' + (x % 10));
                    x /= 10;
                }

                exp = e15;
                return k;
            }
        }

        // 17 digits. Measured against the pinned library on a quarter of a million doubles: the digit string is the 17-digit rounding of the exact
        // value, except that it rounds up already when the discarded part is at least 0.495 of the last digit's unit, or at least 0.45
        // when the spacing between neighbouring doubles is 2.2 units or more (the cut lies between 2.168 and 2.2204 in the data).
        long q = 0;
        for (int i = 0; i < 17; i++)
        {
            q = (q * 10) + (buf[i] - '0');
        }

        double f17 = 0;
        for (int i = 24; i >= 17; i--)
        {
            f17 = (f17 + (buf[i] - '0')) / 10;
        }

        double ulp = Math.BitIncrement(v) - v;
        bool sparse = Math.Log10(ulp) - (e10 - 16) >= 0.3424226808222063; // log10(2.2)
        if (f17 >= (sparse ? 0.45 : 0.495))
        {
            q++;
            if (q == 100_000_000_000_000_000)
            {
                q = 10_000_000_000_000_000;
                e10++;
            }
        }

        int n = 17;
        while (n > 1 && q % 10 == 0)
        {
            q /= 10;
            n--;
        }

        for (int i = n - 1; i >= 0; i--)
        {
            digits[i] = (byte)('0' + (q % 10));
            q /= 10;
        }

        exp = e10;
        return n;
    }

    /// <summary>
    /// Formats a real the way the pinned library renders it as text: 15 significant digits if they read back as the same value,
    /// otherwise 17; exponent form below 1e-4 and from 1e17 on; always a decimal point; "Inf"/"-Inf" for infinities (datatype3, lang_corefunc quote).
    /// </summary>
    internal static int FormatReal(double v, Span<byte> dst)
    {
        if (double.IsPositiveInfinity(v))
        {
            "Inf"u8.CopyTo(dst);
            return 3;
        }

        if (double.IsNegativeInfinity(v))
        {
            "-Inf"u8.CopyTo(dst);
            return 4;
        }

        if (double.IsNaN(v) || v == 0)
        {
            "0.0"u8.CopyTo(dst);
            return 3;
        }

        bool neg = v < 0;
        Span<byte> digits = stackalloc byte[20];
        int nd = RealDigits(Math.Abs(v), digits, out int exp);

        int o = 0;
        if (neg)
        {
            dst[o++] = (byte)'-';
        }

        if (exp < -4 || exp > 16)
        {
            dst[o++] = digits[0];
            dst[o++] = (byte)'.';
            if (nd == 1)
            {
                dst[o++] = (byte)'0';
            }
            else
            {
                for (int i = 1; i < nd; i++)
                {
                    dst[o++] = digits[i];
                }
            }

            dst[o++] = (byte)'e';
            dst[o++] = exp < 0 ? (byte)'-' : (byte)'+';
            int ae = Math.Abs(exp);
            if (ae >= 100)
            {
                dst[o++] = (byte)('0' + (ae / 100));
                ae %= 100;
                dst[o++] = (byte)('0' + (ae / 10));
                dst[o++] = (byte)('0' + (ae % 10));
            }
            else
            {
                dst[o++] = (byte)('0' + (ae / 10));
                dst[o++] = (byte)('0' + (ae % 10));
            }

            return o;
        }

        if (exp < 0)
        {
            dst[o++] = (byte)'0';
            dst[o++] = (byte)'.';
            for (int i = 0; i < -exp - 1; i++)
            {
                dst[o++] = (byte)'0';
            }

            for (int i = 0; i < nd; i++)
            {
                dst[o++] = digits[i];
            }

            return o;
        }

        for (int i = 0; i <= exp; i++)
        {
            dst[o++] = i < nd ? digits[i] : (byte)'0';
        }

        dst[o++] = (byte)'.';
        if (nd <= exp + 1)
        {
            dst[o++] = (byte)'0';
        }
        else
        {
            for (int i = exp + 1; i < nd; i++)
            {
                dst[o++] = digits[i];
            }
        }

        return o;
    }

    /// <summary>The text form of a value as the C API gives it (integers and reals formatted, blobs as their bytes). NULL has none.</summary>
    internal static ReadOnlySpan<byte> AsText(Mem m, Span<byte> scratch)
    {
        switch (m.Kind)
        {
            case StorageClass.Integral:
                return scratch[..FormatInt(m.IntValue, scratch)];
            case StorageClass.Real:
                return scratch[..FormatReal(m.RealValue, scratch)];
            case StorageClass.Text:
                return m.Bytes;
            case StorageClass.Blob:
                return BlobAsText(m.Bytes);
            default:
                return default;
        }
    }

    /// <summary>
    /// The database encoding of the running statement, for reading blob bytes as text. A blob read as text is read in the database
    /// encoding (datatype3, lang_expr CAST), and registers hold text as UTF-8, so UTF-16 databases convert here. Set by the VM per run.
    /// </summary>
    [ThreadStatic]
    internal static TextEncoding BlobEncoding;

    /// <summary>Blob bytes as the UTF-8 text a reader of the database encoding sees; a trailing odd byte of UTF-16 is dropped.</summary>
    internal static ReadOnlySpan<byte> BlobAsText(ReadOnlySpan<byte> blob)
        => (byte)BlobEncoding <= (byte)TextEncoding.Utf8 ? blob : Encoding.UTF8.GetBytes(TextCodec.Decode(blob, BlobEncoding));

    /// <summary>Converts a number to text in place into the register's buffer.</summary>
    internal static void Stringify(ref Mem m, ref byte[]? own)
    {
        if (m.Kind is StorageClass.Integral or StorageClass.Real)
        {
            Span<byte> tmp = stackalloc byte[MaxRealText];
            SetBytes(ref m, ref own, StorageClass.Text, AsText(m, tmp));
        }
    }

    // ------------------------------------------------------------------ affinity

    internal static Affinity AffinityOf(string declaredType, bool strict)
    {
        return Storage.BTree.SchemaLite.AffinityOf(declaredType, strict) switch
        {
            'I' => Affinity.Integer,
            'T' => Affinity.Text,
            'R' => Affinity.Real,
            'N' => Affinity.Numeric,
            _ => Affinity.None,
        };
    }

    /// <summary>
    /// Applies NUMERIC affinity to a text value for a comparison: well-formed numeric text becomes INTEGER or REAL, anything else is left alone
    /// (datatype3 §4.2). A REAL that reads as a whole number stays REAL here; it compares equal to the integer anyway.
    /// </summary>
    internal static void ApplyNumericForCompare(ref Mem m)
    {
        if (m.Kind != StorageClass.Text)
        {
            return;
        }

        // Observed: text that is a number followed by NUL bytes (the C string ends there) is numeric for a comparison.
        StorageClass k = ParseNumber(m.Bytes, out long iv, out double dv, out bool whole, out bool nulNext);
        if (!whole && !nulNext)
        {
            return;
        }

        if (k == StorageClass.Integral)
        {
            SetInt(ref m, iv);
        }
        else if (k == StorageClass.Real)
        {
            SetReal(ref m, dv);
        }
    }

    /// <summary>
    /// Applies a column's affinity to a value the way it is applied when the value is stored (datatype3 §3.4): text that reads as a number becomes
    /// INTEGER or REAL, a REAL with no fraction becomes INTEGER, numbers become text for TEXT affinity, and REAL affinity makes integers REAL.
    /// </summary>
    internal static void ApplyStored(ref Mem m, ref byte[]? own, Affinity aff)
    {
        switch (aff)
        {
            case Affinity.None:
                return;
            case Affinity.Text:
                Stringify(ref m, ref own);
                return;
            default:
                if (m.Kind == StorageClass.Text)
                {
                    StorageClass k = ParseNumber(m.Bytes, out long iv, out double dv, out bool whole, out bool nulNext);
                    if (!(whole || nulNext))
                    {
                        return;
                    }

                    if (k == StorageClass.Integral)
                    {
                        SetInt(ref m, iv);
                    }
                    else
                    {
                        SetReal(ref m, dv);
                    }
                }

                if (m.Kind == StorageClass.Real && aff != Affinity.Real)
                {
                    double d = m.RealValue;
                    // Observed: both ends of the 64-bit range stay REAL; -2^63 itself is not converted either.
                    if (d > -9223372036854775808.0 && d < 9223372036854775808.0 && d == Math.Floor(d))
                    {
                        SetInt(ref m, (long)d);
                    }
                }
                else if (m.Kind == StorageClass.Integral && aff == Affinity.Real)
                {
                    SetReal(ref m, m.IntValue);
                }

                return;
        }
    }

    /// <summary>The affinity applied to the operands of a comparison, from the operands' own affinities (datatype3 §4.2).</summary>
    internal static Affinity CompareAffinity(Affinity left, Affinity right)
    {
        bool ln = left is Affinity.Numeric or Affinity.Integer or Affinity.Real;
        bool rn = right is Affinity.Numeric or Affinity.Integer or Affinity.Real;
        if (ln || rn)
        {
            return Affinity.Numeric;
        }

        // Observed against the shell: TEXT affinity converts the other operand only when that operand has no affinity at all
        // (a literal or an expression); a column declared with no type or BLOB is compared as stored.
        if ((left == Affinity.Text && right != Affinity.Blob) || (right == Affinity.Text && left != Affinity.Blob))
        {
            return Affinity.Text;
        }

        return Affinity.None;
    }

    // ------------------------------------------------------------------ comparison

    private static int Fold(byte c) => c is >= (byte)'A' and <= (byte)'Z' ? c + 32 : c;

    /// <summary>Compares two UTF-8 texts under a collation. For BINARY in a UTF-16 database the comparison is the one the database's indexes use: memcmp of the stored form.</summary>
    internal static int CompareText(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, CollKind coll, TextEncoding dbEnc)
    {
        switch (coll)
        {
            case CollKind.NoCase:
            {
                int n = Math.Min(a.Length, b.Length);
                for (int i = 0; i < n; i++)
                {
                    int d = Fold(a[i]) - Fold(b[i]);
                    if (d != 0)
                    {
                        return d;
                    }
                }

                return a.Length - b.Length;
            }

            case CollKind.RTrim:
                return a.TrimEnd((byte)' ').SequenceCompareTo(b.TrimEnd((byte)' '));
            default:
                if (coll > CollKind.RTrim)
                {
                    return Scalar.Connection!.CompareUserCollation((int)coll, a, b);
                }

                if (dbEnc == TextEncoding.Utf8)
                {
                    return a.SequenceCompareTo(b);
                }

                // The stored form of a UTF-16 database is what its indexes order by (datatype3 §7.1: BINARY compares bytes).
                return TextCodec.Encode(Encoding.UTF8.GetString(a), dbEnc).AsSpan().SequenceCompareTo(TextCodec.Encode(Encoding.UTF8.GetString(b), dbEnc));
        }
    }

    private static int Rank(StorageClass c) => c switch
    {
        StorageClass.Null => 0,
        StorageClass.Integral or StorageClass.Real => 1,
        StorageClass.Text => 2,
        _ => 3,
    };

    /// <summary>Total order of values in SQLite: NULL, then numbers, then text, then blobs (datatype3 §4.1). Both operands may be NULL here.</summary>
    internal static int Compare(in Mem a, in Mem b, CollKind coll, TextEncoding dbEnc)
    {
        int ra = Rank(a.Kind), rb = Rank(b.Kind);
        if (ra != rb)
        {
            return ra < rb ? -1 : 1;
        }

        switch (ra)
        {
            case 0:
                return 0;
            case 1:
                if (a.Kind == StorageClass.Integral)
                {
                    return b.Kind == StorageClass.Integral
                        ? a.IntValue.CompareTo(b.IntValue)
                        : RecordCompare.IntReal(a.IntValue, b.RealValue);
                }

                return b.Kind == StorageClass.Real
                    ? a.RealValue.CompareTo(b.RealValue)
                    : -RecordCompare.IntReal(b.IntValue, a.RealValue);
            case 2:
                return CompareText(a.Bytes, b.Bytes, coll, dbEnc);
            default:
                return a.Bytes.SequenceCompareTo(b.Bytes);
        }
    }

    /// <summary>
    /// A comparison operator's operands after the comparison affinity is applied (datatype3 §4.2): copies on the stack, the registers are untouched.
    /// Text produced by TEXT affinity goes to the two buffers passed by reference.
    /// </summary>
    internal static void PrepareCompare(ref Mem a, ref Mem b, Affinity aff, ref byte[]? ownA, ref byte[]? ownB)
    {
        if (aff == Affinity.Numeric)
        {
            if (a.Kind == StorageClass.Text)
            {
                ApplyNumericForCompare(ref a);
            }

            if (b.Kind == StorageClass.Text)
            {
                ApplyNumericForCompare(ref b);
            }
        }
        else if (aff == Affinity.Text)
        {
            if (a.Kind is StorageClass.Integral or StorageClass.Real && b.Kind == StorageClass.Text)
            {
                Stringify(ref a, ref ownA);
            }

            if (b.Kind is StorageClass.Integral or StorageClass.Real && a.Kind == StorageClass.Text)
            {
                Stringify(ref b, ref ownB);
            }
        }
    }

    // ------------------------------------------------------------------ arithmetic

    /// <summary>An arithmetic result that is not a number (inf - inf, 0 * inf) is NULL (lang_expr).</summary>
    private static void SetRealNaNNull(ref Mem dst, double d)
    {
        if (double.IsNaN(d))
        {
            SetNull(ref dst);
        }
        else
        {
            SetReal(ref dst, d);
        }
    }

    internal static void Add(in Mem a, in Mem b, ref Mem dst)
    {
        StorageClass ka = ToNumeric(a, out long ia, out double da);
        StorageClass kb = ToNumeric(b, out long ib, out double db);
        if (ka == StorageClass.Integral && kb == StorageClass.Integral)
        {
            long r = unchecked(ia + ib);
            if (((ia ^ r) & (ib ^ r)) < 0)
            {
                SetReal(ref dst, (double)ia + ib);
            }
            else
            {
                SetInt(ref dst, r);
            }

            return;
        }

        SetRealNaNNull(ref dst, (ka == StorageClass.Integral ? ia : da) + (kb == StorageClass.Integral ? ib : db));
    }

    internal static void Sub(in Mem a, in Mem b, ref Mem dst)
    {
        StorageClass ka = ToNumeric(a, out long ia, out double da);
        StorageClass kb = ToNumeric(b, out long ib, out double db);
        if (ka == StorageClass.Integral && kb == StorageClass.Integral)
        {
            long r = unchecked(ia - ib);
            if (((ia ^ ib) & (ia ^ r)) < 0)
            {
                SetReal(ref dst, (double)ia - ib);
            }
            else
            {
                SetInt(ref dst, r);
            }

            return;
        }

        SetRealNaNNull(ref dst, (ka == StorageClass.Integral ? ia : da) - (kb == StorageClass.Integral ? ib : db));
    }

    internal static void Mul(in Mem a, in Mem b, ref Mem dst)
    {
        StorageClass ka = ToNumeric(a, out long ia, out double da);
        StorageClass kb = ToNumeric(b, out long ib, out double db);
        if (ka == StorageClass.Integral && kb == StorageClass.Integral)
        {
            long hi = Math.BigMul(ia, ib, out long lo);
            if (hi != (lo >> 63))
            {
                SetReal(ref dst, (double)ia * ib);
            }
            else
            {
                SetInt(ref dst, lo);
            }

            return;
        }

        SetRealNaNNull(ref dst, (ka == StorageClass.Integral ? ia : da) * (kb == StorageClass.Integral ? ib : db));
    }

    internal static void Div(in Mem a, in Mem b, ref Mem dst)
    {
        StorageClass ka = ToNumeric(a, out long ia, out double da);
        StorageClass kb = ToNumeric(b, out long ib, out double db);
        if (ka == StorageClass.Integral && kb == StorageClass.Integral)
        {
            if (ib == 0)
            {
                SetNull(ref dst);
            }
            else if (ia == long.MinValue && ib == -1)
            {
                SetReal(ref dst, -(double)long.MinValue);
            }
            else
            {
                SetInt(ref dst, ia / ib);
            }

            return;
        }

        double x = ka == StorageClass.Integral ? ia : da;
        double y = kb == StorageClass.Integral ? ib : db;
        if (y == 0)
        {
            SetNull(ref dst);
        }
        else
        {
            SetRealNaNNull(ref dst, x / y);
        }
    }

    /// <summary>The % operator: both operands become integers; the result is REAL if either operand was (lang_expr).</summary>
    internal static void Rem(in Mem a, in Mem b, ref Mem dst)
    {
        StorageClass ka = ToNumeric(a, out long ia, out double da);
        StorageClass kb = ToNumeric(b, out long ib, out double db);
        // A text operand gives its integer prefix ("1e3" is 1), a real is truncated.
        long x = ka == StorageClass.Integral ? ia : a.Kind == StorageClass.Real ? DoubleToInt(da) : ToInt(a);
        long y = kb == StorageClass.Integral ? ib : b.Kind == StorageClass.Real ? DoubleToInt(db) : ToInt(b);
        if (y == 0)
        {
            SetNull(ref dst);
            return;
        }

        long r = y == -1 ? 0 : x % y;
        if (ka == StorageClass.Real || kb == StorageClass.Real)
        {
            SetReal(ref dst, r);
        }
        else
        {
            SetInt(ref dst, r);
        }
    }

    internal static long ShiftLeft(long x, long n)
    {
        if (n < 0)
        {
            return n <= -64 ? (x < 0 ? -1 : 0) : x >> (int)-n;
        }

        return n >= 64 ? 0 : (long)((ulong)x << (int)n);
    }

    internal static long ShiftRight(long x, long n)
    {
        if (n < 0)
        {
            return n <= -64 ? 0 : (long)((ulong)x << (int)-n);
        }

        return n >= 64 ? (x < 0 ? -1 : 0) : x >> (int)n;
    }

    // ------------------------------------------------------------------ CAST

    /// <summary>The affinity a CAST target type name stands for (lang_expr CAST: the same rules as a declared column type).</summary>
    // CAST(x AS ) with no type name casts like NUMERIC (observed on the reference; a column with no type has BLOB affinity instead).
    internal static Affinity CastAffinity(string typeName) => typeName.Length == 0 ? Affinity.Numeric : AffinityOf(typeName, false);

    /// <summary>CAST(value AS type) (lang_expr "CAST expressions").</summary>
    internal static void Cast(ref Mem m, ref byte[]? own, Affinity target)
    {
        if (m.Kind == StorageClass.Null)
        {
            return;
        }

        switch (target)
        {
            case Affinity.None: // BLOB
                if (m.Kind != StorageClass.Blob)
                {
                    Span<byte> tmp = stackalloc byte[MaxRealText];
                    ReadOnlySpan<byte> t = AsText(m, tmp);
                    if (m.Kind is StorageClass.Integral or StorageClass.Real)
                    {
                        SetBytes(ref m, ref own, StorageClass.Blob, t);
                    }
                    else
                    {
                        m.Kind = StorageClass.Blob;
                        m.Subtype = 0;
                    }
                }

                return;
            case Affinity.Text:
                if (m.Kind is StorageClass.Integral or StorageClass.Real)
                {
                    Stringify(ref m, ref own);
                }
                else
                {
                    m.Kind = StorageClass.Text;
                }

                return;
            case Affinity.Integer:
            {
                long v;
                if (m.Kind == StorageClass.Integral)
                {
                    return;
                }

                if (m.Kind == StorageClass.Real)
                {
                    v = DoubleToInt(m.RealValue);
                }
                else
                {
                    v = IntegerPrefix(m.Kind == StorageClass.Blob ? BlobAsText(m.Bytes) : m.Bytes);
                }

                SetInt(ref m, v);
                return;
            }

            case Affinity.Real:
            {
                double d;
                if (m.Kind == StorageClass.Real)
                {
                    return;
                }

                d = m.Kind == StorageClass.Integral ? m.IntValue : ToReal(m);
                SetReal(ref m, d);
                return;
            }

            default: // NUMERIC
            {
                // Black-box: a REAL operand stays REAL (CAST(100.0 AS NUMERIC) is 100.0); only text that reads as a whole number
                // becomes an INTEGER.
                bool fromText = m.Kind is StorageClass.Text or StorageClass.Blob;
                if (fromText)
                {
                    StorageClass k = ParseNumber(m.Kind == StorageClass.Blob ? BlobAsText(m.Bytes) : m.Bytes, out long iv, out double dv, out _);
                    if (k == StorageClass.Integral)
                    {
                        SetInt(ref m, iv);
                    }
                    else if (k == StorageClass.Real)
                    {
                        SetReal(ref m, dv);
                    }
                    else
                    {
                        SetInt(ref m, 0);
                    }
                }

                if (fromText && m.Kind == StorageClass.Real)
                {
                    double d = m.RealValue;
                    // Observed: text that reads as a real becomes an INTEGER only when it is whole and lies in [-2^51, 2^51); beyond that
                    // (1e16, -9223372036854775809) it stays REAL. Integer text that fits 64 bits is already INTEGER above.
                    if (d >= -2251799813685248.0 && d < 2251799813685248.0 && d == Math.Floor(d))
                    {
                        SetInt(ref m, (long)d);
                    }
                }

                return;
            }
        }
    }

    // ------------------------------------------------------------------ misc

    /// <summary>Reads an integer literal as written (decimal or 0x hex). Decimal text too large for 64 bits is a REAL (lang_expr literals).</summary>
    internal static bool ParseIntegerLiteral(string text, out Mem result, out bool hexTooBig)
    {
        hexTooBig = false;
        result = default;
        string t = text.Replace("_", string.Empty, StringComparison.Ordinal);
        if (t.Length > 2 && t[0] == '0' && (t[1] == 'x' || t[1] == 'X'))
        {
            string h = t[2..].TrimStart('0');
            if (h.Length > 16)
            {
                hexTooBig = true;
                return false;
            }

            ulong u = h.Length == 0 ? 0 : ulong.Parse(h, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            SetInt(ref result, unchecked((long)u));
            return true;
        }

        if (long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out long v))
        {
            SetInt(ref result, v);
            return true;
        }

        SetReal(ref result, double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture));
        return true;
    }
}
