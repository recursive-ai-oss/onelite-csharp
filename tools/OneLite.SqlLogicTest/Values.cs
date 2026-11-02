using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace RecursiveAI.OneLite.SqlLogicTest;

/// <summary>The storage class of a result value.</summary>
public enum ValueKind : byte
{
    /// <summary>SQL NULL.</summary>
    Null,

    /// <summary>A 64-bit integer.</summary>
    WholeNumber,

    /// <summary>A 64-bit float.</summary>
    FloatingPoint,

    /// <summary>Text.</summary>
    Text,

    /// <summary>A blob (rendered through its bytes read as text).</summary>
    Blob,
}

/// <summary>One result value as an engine reports it: its storage class and content.</summary>
public readonly struct SltValue
{
    /// <summary>The storage class.</summary>
    public ValueKind Kind { get; }

    /// <summary>The integer, when <see cref="Kind"/> is Integer.</summary>
    public long AsInteger { get; }

    /// <summary>The float, when <see cref="Kind"/> is FloatingPoint.</summary>
    public double Real { get; }

    /// <summary>The text, when <see cref="Kind"/> is Text or Blob (a blob as its bytes decoded as UTF-8).</summary>
    public string? Text { get; }

    private SltValue(ValueKind k, long i, double r, string? t)
    {
        Kind = k;
        AsInteger = i;
        Real = r;
        Text = t;
    }

    /// <summary>NULL.</summary>
    public static SltValue Null => default;

    /// <summary>An integer.</summary>
    public static SltValue FromInt(long v) => new(ValueKind.WholeNumber, v, 0, null);

    /// <summary>A float.</summary>
    public static SltValue FromReal(double v) => new(ValueKind.FloatingPoint, 0, v, null);

    /// <summary>Text.</summary>
    public static SltValue FromText(string v) => new(ValueKind.Text, 0, 0, v);

    /// <summary>A blob, given as text.</summary>
    public static SltValue FromBlob(string v) => new(ValueKind.Blob, 0, 0, v);
}

/// <summary>
/// How result values become the text the scripts hold. The rules are the documented ones (integers as <c>%d</c>, floats as
/// <c>%.3f</c>, <c>NULL</c>, <c>(empty)</c>, unprintable characters as <c>@</c>); the details the description leaves open are
/// written in docs/sqllogictest.md.
/// </summary>
public static class Rendering
{
    /// <summary>
    /// Renders one value for a column whose type letter is <paramref name="type"/> (<c>I</c>, <c>T</c> or <c>R</c>). NULL is always
    /// <c>NULL</c>. Otherwise the value is converted to the column's declared type first, as the result-column getters do:
    /// <c>I</c> takes the integer a float truncates to or the integer prefix of text (0 when there is none), <c>R</c> the float of an
    /// integer or of the numeric prefix of text, <c>T</c> the text of the value.
    /// </summary>
    public static string Render(in SltValue v, char type)
    {
        if (v.Kind == ValueKind.Null)
        {
            return "NULL";
        }
        switch (type)
        {
            case 'I':
                return v.Kind switch
                {
                    ValueKind.WholeNumber => v.AsInteger.ToString(CultureInfo.InvariantCulture),
                    ValueKind.FloatingPoint => RealToInteger(v.Real),
                    _ => IntegerPrefix(v.Text ?? "").ToString(CultureInfo.InvariantCulture),
                };
            case 'R':
                return v.Kind switch
                {
                    ValueKind.WholeNumber => FormatFixed3(v.AsInteger),
                    ValueKind.FloatingPoint => FormatFixed3(v.Real),
                    _ => FormatFixed3(RealPrefix(v.Text ?? "")),
                };
            default:
                return v.Kind switch
                {
                    ValueKind.WholeNumber => v.AsInteger.ToString(CultureInfo.InvariantCulture),
                    ValueKind.FloatingPoint => Printable(FloatText(v.Real)),
                    _ => Printable(v.Text ?? ""),
                };
        }
    }

    // The text of a float as sqlite3_column_text gives it: 15 significant digits, a ".0" when it is whole.
    private static string FloatText(double d)
    {
        if (double.IsInfinity(d))
        {
            return d > 0 ? "Inf" : "-Inf";
        }
        string g = d.ToString("G15", CultureInfo.InvariantCulture);
        if (g.Contains('E', StringComparison.Ordinal))
        {
            // 1E+20 -> 1.0e+20
            string[] p = g.Split('E');
            string mant = p[0].Contains('.', StringComparison.Ordinal) ? p[0] : p[0] + ".0";
            string ex = p[1];
            char sign = ex[0] == '-' ? '-' : '+';
            string digits = ex.TrimStart('+', '-').TrimStart('0').PadLeft(2, '0');
            return mant + "e" + sign + digits;
        }
        return g.Contains('.', StringComparison.Ordinal) ? g : g + ".0";
    }

    /// <summary>The integer a text starts with: optional blanks, a sign, digits; 0 if there are none; saturating at the 64-bit limits.</summary>
    public static long IntegerPrefix(string s)
    {
        int i = 0;
        while (i < s.Length && (s[i] is ' ' or '\t' or '\n' or '\r' or '\f' or '\v'))
        {
            i++;
        }
        bool neg = false;
        if (i < s.Length && (s[i] == '-' || s[i] == '+'))
        {
            neg = s[i] == '-';
            i++;
        }
        BigInteger v = BigInteger.Zero;
        while (i < s.Length && s[i] is >= '0' and <= '9')
        {
            v = (v * 10) + (s[i] - '0');
            i++;
        }
        if (neg)
        {
            v = -v;
        }
        return v > long.MaxValue ? long.MaxValue : v < long.MinValue ? long.MinValue : (long)v;
    }

    /// <summary>The float a text starts with (blanks, sign, digits, fraction, exponent); 0 if there is none.</summary>
    public static double RealPrefix(string s)
    {
        int i = 0;
        while (i < s.Length && (s[i] is ' ' or '\t' or '\n' or '\r' or '\f' or '\v'))
        {
            i++;
        }
        int start = i;
        if (i < s.Length && (s[i] == '-' || s[i] == '+'))
        {
            i++;
        }
        int digits = 0;
        while (i < s.Length && s[i] is >= '0' and <= '9')
        {
            i++;
            digits++;
        }
        if (i < s.Length && s[i] == '.')
        {
            i++;
            while (i < s.Length && s[i] is >= '0' and <= '9')
            {
                i++;
                digits++;
            }
        }
        if (digits == 0)
        {
            return 0;
        }
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            int j = i + 1;
            if (j < s.Length && (s[j] == '-' || s[j] == '+'))
            {
                j++;
            }
            if (j < s.Length && s[j] is >= '0' and <= '9')
            {
                while (j < s.Length && s[j] is >= '0' and <= '9')
                {
                    j++;
                }
                i = j;
            }
        }
        return double.Parse(s.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static string RealToInteger(double d)
    {
        if (double.IsNaN(d))
        {
            return "0";
        }
        if (d >= 9223372036854775807.0)
        {
            return long.MaxValue.ToString(CultureInfo.InvariantCulture);
        }
        if (d <= -9223372036854775808.0)
        {
            return long.MinValue.ToString(CultureInfo.InvariantCulture);
        }
        return ((long)d).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Text for a value: empty is <c>(empty)</c>; every byte outside 0x20..0x7e is shown as <c>@</c>.</summary>
    public static string Printable(string s)
    {
        if (s.Length == 0)
        {
            return "(empty)";
        }
        bool plain = true;
        foreach (char c in s)
        {
            if (c < 0x20 || c > 0x7e)
            {
                plain = false;
                break;
            }
        }
        if (plain)
        {
            return s;
        }
        var sb = new StringBuilder(s.Length);
        foreach (byte b in Encoding.UTF8.GetBytes(s))
        {
            sb.Append(b is < 0x20 or > 0x7e ? '@' : (char)b);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The text SQLite's own printf gives for <c>%.3f</c>, which is what the scripts hold. Settled against the pinned library
    /// (<c>check-printf</c>): the exact binary value is first cut to 18 significant digits, then rounded half up at the third decimal
    /// or, for values of 16 or more integer digits, at the sixteenth significant digit (the rest are zeros). So 0.0625 gives 0.063,
    /// 1.0005 (a hair under) gives 1.000, 0.4995 (a hair under, within the 18 digits) gives 0.500 and 2^63 gives
    /// 9223372036854776000.000. A negative that rounds to zero keeps its sign (<c>-0.000</c>); negative zero does not.
    /// This reproduces the library on all but about 0.03% of a tie-heavy test set; see docs/sqllogictest.md.
    /// </summary>
    public static string FormatFixed3(double d)
    {
        if (double.IsNaN(d))
        {
            return "NaN";
        }
        if (double.IsInfinity(d))
        {
            return d > 0 ? "Inf" : "-Inf";
        }
        bool neg = d < 0;
        double a = Math.Abs(d);
        if (a == 0)
        {
            return "0.000";
        }
        long bits = BitConverter.DoubleToInt64Bits(a);
        int exp = (int)((bits >> 52) & 0x7ff);
        long frac = bits & 0xFFFFFFFFFFFFFL;
        long m = exp == 0 ? frac : frac | (1L << 52);
        int e = exp == 0 ? -1074 : exp - 1075;

        // value = m * 2^e. Find k so that p = round(value * 10^k) has exactly 18 digits; value is about p * 10^-k.
        int k = 17 - (int)Math.Floor(Math.Log10(a));
        BigInteger p = ScaledRound(m, e, k);
        while (p >= Pow10(18))
        {
            k--;
            p = ScaledRound(m, e, k);
        }
        while (p < Pow10(17))
        {
            k++;
            p = ScaledRound(m, e, k);
        }
        int lastKept = Math.Max(-3, (17 - k) - 15);   // exponent of the last digit kept
        int drop = lastKept + k;                      // digits of p to remove
        BigInteger r;
        if (drop > 0)
        {
            BigInteger div = Pow10(drop);
            r = BigInteger.DivRem(p, div, out BigInteger rem);
            if (rem * 2 >= div)
            {
                r += 1;
            }
        }
        else
        {
            r = p * Pow10(-drop);
        }
        BigInteger q = r * Pow10(lastKept + 3);   // thousandths
        string digits = q.ToString(CultureInfo.InvariantCulture).PadLeft(4, '0');
        return (neg ? "-" : "") + digits[..^3] + "." + digits[^3..];
    }

    private static BigInteger Pow10(int n) => BigInteger.Pow(10, n);

    // round-half-even(m * 2^e * 10^k), exactly.
    private static BigInteger ScaledRound(long m, int e, int k)
    {
        BigInteger num = new BigInteger(m);
        BigInteger den = BigInteger.One;
        if (e >= 0)
        {
            num <<= e;
        }
        else
        {
            den <<= -e;
        }
        if (k >= 0)
        {
            num *= Pow10(k);
        }
        else
        {
            den *= Pow10(-k);
        }
        BigInteger n = BigInteger.DivRem(num, den, out BigInteger rem);
        int cmp = (rem * 2).CompareTo(den);
        if (cmp > 0 || (cmp == 0 && !n.IsEven))
        {
            n += 1;
        }
        return n;
    }

    /// <summary>
    /// Sorts rendered rows as <paramref name="sort"/> asks and flattens them to the list of values the script holds: rows by their
    /// text column by column (ordinal), or every value on its own.
    /// </summary>
    public static List<string> Arrange(List<string[]> rows, SortMode sort)
    {
        if (sort == SortMode.RowSort)
        {
            rows.Sort(CompareRows);
        }
        var flat = new List<string>();
        foreach (string[] r in rows)
        {
            flat.AddRange(r);
        }
        if (sort == SortMode.ValueSort)
        {
            flat.Sort(string.CompareOrdinal);
        }
        return flat;
    }

    private static int CompareRows(string[] a, string[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int c = string.CompareOrdinal(a[i], b[i]);
            if (c != 0)
            {
                return c;
            }
        }
        return a.Length.CompareTo(b.Length);
    }

    /// <summary>The MD5 of the values, each followed by a newline, as 32 lower-case hex digits.</summary>
    public static string Hash(List<string> values)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        foreach (string v in values)
        {
            md5.AppendData(Encoding.UTF8.GetBytes(v));
            md5.AppendData("\n"u8);
        }
        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }
}
