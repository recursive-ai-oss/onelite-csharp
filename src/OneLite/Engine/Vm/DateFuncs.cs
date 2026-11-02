using System.Globalization;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Vm;

/// <summary>
/// The date and time functions (lang_datefunc). A time value is kept as a Julian day count in milliseconds (iJD), the unit the documentation
/// describes: milliseconds since noon on -4713-11-24 (proleptic Gregorian), valid from 0000-01-01 00:00:00 to 9999-12-31 23:59:59.999.
/// </summary>
internal static class DateFuncs
{
    private const long MaxJD = 464269060799999;
    private const long UnixEpochJD = 210866760000000;

    private struct DT
    {
        internal long IJD;
        internal int Y, M, D, H, Mi;
        internal double S;
        internal int Tz;
        internal bool RawBad, ValidJD, ValidYMD, ValidHMS, ValidTz, RawS, IsError, UseSubsec, IsUtc, IsLocal;
        internal bool HasFloor; // the previous modifier was a month/year shift that overflowed the month: FloorJD is the last day instead
        internal long FloorJD;
    }

    [ThreadStatic]
    private static long t_now;

    /// <summary>Called when a statement starts running: 'now' is the same instant for the whole statement (lang_datefunc).</summary>
    internal static void BeginStatement()
    {
        t_now = 0;
        JsonCache.Clear();
    }

    /// <summary>The current time as iJD. Tests may set <see cref="NowOverride"/>.</summary>
    internal static long NowJD()
    {
        if (t_now == 0)
        {
            t_now = NowOverride ?? (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds + UnixEpochJD;
        }

        return t_now;
    }

    internal static long? NowOverride;

    // ------------------------------------------------------------------ calendar

    private static void ComputeJD(ref DT p)
    {
        if (p.ValidJD)
        {
            return;
        }

        int y, m, d;
        if (p.ValidYMD)
        {
            y = p.Y;
            m = p.M;
            d = p.D;
        }
        else
        {
            y = 2000;
            m = 1;
            d = 1;
        }

        if (m <= 2)
        {
            y--;
            m += 12;
        }

        // Observed: years before the Julian century boundary (-100 and earlier) come out as floor division, not truncation toward zero.
        int a = (int)Math.Floor(y / 100.0);
        int b = 2 - a + (int)Math.Floor(a / 4.0);
        long x1 = (long)Math.Floor(36525L * (y + 4716) / 100.0);
        long x2 = 306001L * (m + 1) / 10000;
        p.IJD = (long)((x1 + x2 + d + b - 1524.5) * 86400000);
        p.ValidJD = true;
        if (p.ValidHMS)
        {
            p.IJD += (p.H * 3600000L) + (p.Mi * 60000L) + (long)((p.S * 1000) + 0.5);
            if (p.ValidTz)
            {
                p.IJD -= p.Tz * 60000L;
                p.ValidYMD = false;
                p.ValidHMS = false;
                p.ValidTz = false;
            }
        }
    }

    private static void ComputeYMD(ref DT p)
    {
        if (p.ValidYMD)
        {
            return;
        }

        if (!p.ValidJD)
        {
            p.Y = 2000;
            p.M = 1;
            p.D = 1;
        }
        else if (!ValidJulianDay(p.IJD))
        {
            p.IsError = true;
            return;
        }
        else
        {
            int z = (int)(((p.IJD + 43200000) / 86400000));
            int alpha = (int)((z + 32044.75) / 36524.25) - 52;
            int a = z + 1 + alpha - ((alpha + 100) / 4) + 25;
            int b = a + 1524;
            int c = (int)((b - 122.1) / 365.25);
            int d = (int)((36525L * (c & 32767)) / 100);
            int e = (int)((b - d) / 30.6001);
            int x1 = (int)(30.6001 * e);
            p.D = b - d - x1;
            p.M = e < 14 ? e - 1 : e - 13;
            p.Y = p.M > 2 ? c - 4716 : c - 4715;
        }

        p.ValidYMD = true;
    }

    private static void ComputeHMS(ref DT p)
    {
        if (p.ValidHMS)
        {
            return;
        }

        ComputeJD(ref p);
        int s = (int)((p.IJD + 43200000) % 86400000);
        p.S = (s % 60000) / 1000.0;
        s /= 60000;
        p.Mi = s % 60;
        p.H = s / 60;
        p.ValidHMS = true;
    }

    private static void ComputeYMD_HMS(ref DT p)
    {
        ComputeYMD(ref p);
        ComputeHMS(ref p);
    }

    private static void ClearYMD_HMS_TZ(ref DT p)
    {
        p.ValidYMD = false;
        p.ValidHMS = false;
        p.ValidTz = false;
    }

    private static bool ValidJulianDay(long jd) => jd >= 0 && jd <= MaxJD;

    private static bool IsLeap(int y) => (y % 4 == 0 && y % 100 != 0) || y % 400 == 0;

    private static int DaysInMonth(int y, int m) => m switch
    {
        2 => IsLeap(y) ? 29 : 28,
        4 or 6 or 9 or 11 => 30,
        _ => 31,
    };

    // ------------------------------------------------------------------ parsing

    private static bool IsDigit(ReadOnlySpan<byte> z, int i) => i < z.Length && z[i] is >= (byte)'0' and <= (byte)'9';

    private static bool IsSpace(ReadOnlySpan<byte> z, int i) => i < z.Length && z[i] is (byte)' ' or (byte)'\t' or (byte)'\n' or 0x0B or 0x0C or (byte)'\r';

    /// <summary>Exactly <paramref name="n"/> digits whose value lies in [lo, hi]; false otherwise.</summary>
    private static bool Digits(ReadOnlySpan<byte> z, ref int i, int n, int lo, int hi, out int v)
    {
        v = 0;
        for (int k = 0; k < n; k++)
        {
            if (!IsDigit(z, i + k))
            {
                return false;
            }

            v = (v * 10) + (z[i + k] - '0');
        }

        if (v < lo || v > hi)
        {
            return false;
        }

        i += n;
        return true;
    }

    private static bool ParseTimezone(ReadOnlySpan<byte> z, ref int i, ref DT p)
    {
        while (IsSpace(z, i))
        {
            i++;
        }

        int sgn;
        if (i < z.Length && z[i] == (byte)'-')
        {
            sgn = -1;
        }
        else if (i < z.Length && z[i] == (byte)'+')
        {
            sgn = 1;
        }
        else if (i < z.Length && z[i] is (byte)'Z' or (byte)'z')
        {
            i++;
            goto zulu;
        }
        else
        {
            return i < z.Length; // anything else but the end is an error
        }

        i++;
        if (!Digits(z, ref i, 2, 0, 14, out int hr) || i >= z.Length || z[i] != (byte)':')
        {
            return true;
        }

        i++;
        if (!Digits(z, ref i, 2, 0, 59, out int mn))
        {
            return true;
        }

        p.Tz = sgn * ((hr * 60) + mn);
zulu:
        while (IsSpace(z, i))
        {
            i++;
        }

        return i < z.Length;
    }

    /// <summary>HH:MM[:SS[.fff]] with an optional zone; true on error.</summary>
    private static bool ParseHhMmSs(ReadOnlySpan<byte> z, int i, ref DT p)
    {
        if (!Digits(z, ref i, 2, 0, 24, out int h) || i >= z.Length || z[i] != (byte)':')
        {
            return true;
        }

        i++;
        if (!Digits(z, ref i, 2, 0, 59, out int m))
        {
            return true;
        }

        double s = 0;
        if (i < z.Length && z[i] == (byte)':')
        {
            i++;
            if (!Digits(z, ref i, 2, 0, 59, out int sec))
            {
                return true;
            }

            double ms = sec;
            if (i < z.Length && z[i] == (byte)'.' && IsDigit(z, i + 1))
            {
                i++;
                double scale = 1.0;
                ms = sec;
                while (IsDigit(z, i))
                {
                    ms = (ms * 10.0) + (z[i] - '0');
                    scale *= 10.0;
                    i++;
                }

                ms /= scale;
                // Keep sub-millisecond rounding from carrying into the next second (lang_datefunc: only the first three digits count).
                if (ms - sec > 0.999)
                {
                    ms = sec + 0.999;
                }
            }

            s = ms;
        }

        p.ValidJD = false;
        p.RawS = false;
        p.ValidHMS = true;
        p.H = h;
        p.Mi = m;
        p.S = s;
        if (ParseTimezone(z, ref i, ref p))
        {
            return true;
        }

        p.ValidTz = p.Tz != 0;
        return false;
    }

    // shift: the text is a [+-]YYYY-MM-DD modifier, whose month and day are amounts (00-11 and 00-30) rather than a calendar position.
    private static bool ParseYyyyMmDd(ReadOnlySpan<byte> z, ref DT p, bool shift = false)
    {
        int i = 0;
        bool neg = false;
        if (z.Length > 0 && z[0] == (byte)'-')
        {
            i++;
            neg = true;
        }

        if (!Digits(z, ref i, 4, 0, 9999, out int y) || i >= z.Length || z[i] != (byte)'-')
        {
            return false;
        }

        i++;
        if (!Digits(z, ref i, 2, shift ? 0 : 1, shift ? 11 : 12, out int m) || i >= z.Length || z[i] != (byte)'-')
        {
            return false;
        }

        i++;
        if (!Digits(z, ref i, 2, shift ? 0 : 1, shift ? 30 : 31, out int d))
        {
            return false;
        }

        while (IsSpace(z, i) || (!shift && i < z.Length && z[i] == (byte)'T'))
        {
            i++;
        }

        if (ParseHhMmSs(z, i, ref p) == false)
        {
            // time parsed
        }
        else if (i < z.Length)
        {
            return false;
        }
        else
        {
            p.ValidHMS = false;
        }

        p.ValidJD = false;
        p.ValidYMD = true;
        p.Y = neg ? -y : y;
        p.M = m;
        p.D = d;
        return true;
    }

    private static bool ParseDateOrTime(ReadOnlySpan<byte> z, ref DT p)
    {
        if (ParseYyyyMmDd(z, ref p))
        {
            return true;
        }

        if (!ParseHhMmSs(z, 0, ref p))
        {
            return true;
        }

        if (z.SequenceEqual("now"u8))
        {
            p.IJD = NowJD();
            p.ValidJD = true;
            return true;
        }

        StorageClass k = Values.ParseNumber(z, out long iv, out double dv, out bool whole);
        if (whole && k is StorageClass.Integral or StorageClass.Real)
        {
            SetRaw(ref p, k == StorageClass.Integral ? iv : dv);
            return true;
        }

        return false;
    }

    private static void SetRaw(ref DT p, double r)
    {
        p.S = r;
        p.RawS = true;
        p.RawBad = !(r >= 0.0 && r < 5373484.5);
        if (r >= 0.0 && r < 5373484.5)
        {
            p.IJD = (long)((r * 86400000.0) + 0.5);
            p.ValidJD = true;
        }
    }

    // ------------------------------------------------------------------ modifiers

    private static readonly string[] s_units = ["second", "minute", "hour", "day", "month", "year"];
    private static readonly double[] s_unitScale = [1000.0, 60000.0, 3600000.0, 86400000.0, 0, 0];

    /// <summary>Applies one modifier; false when it is unknown, malformed or out of range (the whole function then gives NULL).</summary>
    private static bool ParseModifier(ReadOnlySpan<byte> zMod, ref DT p, int idx)
    {
        if (zMod.Length == 0 || zMod.Length > 30)
        {
            return false;
        }

        // lang_datefunc: modifier names are case-insensitive (ASCII).
        string z = System.Text.Encoding.UTF8.GetString(zMod);
        if (z.AsSpan().ContainsAnyInRange('A', 'Z'))
        {
            z = string.Create(z.Length, z, static (d, src) =>
            {
                for (int i = 0; i < d.Length; i++)
                {
                    char c = src[i];
                    d[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
                }
            });
        }

        // A bare number outside the Julian-day range is only meaningful to 'auto', 'unixepoch' and 'julianday'; any other modifier fails.
        if (p.RawS && p.RawBad && z != "auto" && z != "unixepoch" && z != "julianday")
        {
            return false;
        }

        if (z != "floor" && z != "ceiling")
        {
            p.HasFloor = false;
        }

        switch (z[0])
        {
            case 'a':
                if (z == "auto")
                {
                    if (idx > 1 || !p.RawS)
                    {
                        // 'auto' must directly follow the time value; on text it does nothing
                        return idx <= 1 && !p.RawS;
                    }

                    if (p.S >= 0.0 && p.S < 5373484.5)
                    {
                        return true; // already a Julian day
                    }

                    if (p.S >= -210866760000.0 && p.S <= 253402300799.0)
                    {
                        p.IJD = (long)((p.S * 1000.0) + UnixEpochJD + 0.5); // the sum is positive, so +0.5 rounds negative seconds correctly too
                        p.ValidJD = true;
                        p.RawS = false;
                        ClearYMD_HMS_TZ(ref p);
                        return true;
                    }

                    return false;
                }

                return false;
            case 'j':
                if (z == "julianday")
                {
                    if (idx > 1 || !p.RawS)
                    {
                        return false;
                    }

                    return p.ValidJD;
                }

                return false;
            case 'l':
                if (z == "localtime")
                {
                    ComputeJD(ref p);
                    if (!ValidJulianDay(p.IJD))
                    {
                        return false;
                    }

                    p.IJD += LocalOffsetMs(p.IJD, true);
                    ClearYMD_HMS_TZ(ref p);
                    p.IsLocal = true;
                    p.IsUtc = false;
                    return true;
                }

                return false;
            case 'u':
                if (z == "unixepoch")
                {
                    if (idx > 1 || !p.RawS)
                    {
                        return false;
                    }

                    if (p.S < -210866760000.0 || p.S > 253402300799.0)
                    {
                        return false;
                    }

                    p.IJD = (long)((p.S * 1000.0) + UnixEpochJD + 0.5); // the sum is positive, so +0.5 rounds negative seconds correctly too
                    p.ValidJD = true;
                    p.RawS = false;
                    ClearYMD_HMS_TZ(ref p);
                    return true;
                }

                if (z == "utc")
                {
                    ComputeJD(ref p);
                    if (!ValidJulianDay(p.IJD))
                    {
                        return false;
                    }

                    p.IJD -= LocalOffsetMs(p.IJD, false);
                    ClearYMD_HMS_TZ(ref p);
                    p.IsUtc = true;
                    p.IsLocal = false;
                    return true;
                }

                return false;
            case 'w':
                if (z.StartsWith("weekday ", StringComparison.Ordinal))
                {
                    if (!TryParseNumber(z.AsSpan(8), out double r) || r < 0 || r >= 7 || r != Math.Floor(r))
                    {
                        return false;
                    }

                    int n = (int)r;
                    ComputeYMD_HMS(ref p);
                    p.Tz = 0;
                    p.ValidJD = false;
                    ComputeJD(ref p);
                    int wd = (int)(((p.IJD + 129600000) / 86400000) % 7);
                    if (wd > n)
                    {
                        wd -= 7;
                    }

                    p.IJD += (long)(n - wd) * 86400000;
                    ClearYMD_HMS_TZ(ref p);
                    return true;
                }

                return false;
            case 's':
                if (z.StartsWith("start of ", StringComparison.Ordinal))
                {
                    if (!ValidJulianDay(p.IJD) && p.ValidJD)
                    {
                        return false;
                    }

                    ComputeYMD(ref p);
                    if (p.IsError)
                    {
                        return false;
                    }

                    p.ValidHMS = true;
                    p.H = 0;
                    p.Mi = 0;
                    p.S = 0;
                    p.Tz = 0;
                    p.ValidJD = false;
                    switch (z[9..])
                    {
                        case "month":
                            p.D = 1;
                            break;
                        case "year":
                            p.M = 1;
                            p.D = 1;
                            break;
                        case "day":
                            break;
                        default:
                            return false;
                    }

                    return true;
                }

                if (z == "subsec" || z == "subsecond")
                {
                    p.UseSubsec = true;
                    return true;
                }

                return false;
            case 'c':
                if (z == "ceiling")
                {
                    return ApplyFloorCeiling(ref p, 1);
                }

                return false;
            case 'f':
                if (z == "floor")
                {
                    return ApplyFloorCeiling(ref p, -1);
                }

                return false;
            case '+':
            case '-':
            case '0':
            case '1':
            case '2':
            case '3':
            case '4':
            case '5':
            case '6':
            case '7':
            case '8':
            case '9':
                return ParseShift(z, ref p);
            default:
                return false;
        }
    }

    private static bool ApplyFloorCeiling(ref DT p, int which)
    {
        // 'floor' resolves a day that does not exist in the shifted month to the month's last day; 'ceiling' keeps the overflow (the default).
        // Anywhere else they do nothing, and are not errors.
        if (which < 0 && p.HasFloor)
        {
            p.IJD = p.FloorJD;
            ClearYMD_HMS_TZ(ref p);
        }

        p.HasFloor = false;
        return true;
    }

    private static bool TryParseNumber(ReadOnlySpan<char> s, out double r)
    {
        // A number as the date functions read it: optional sign, digits with an optional fraction; nothing else.
        r = 0;
        if (s.Length == 0)
        {
            return false;
        }

        int i = 0;
        if (s[0] is '+' or '-')
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

        if (digits == 0 || i != s.Length)
        {
            return false;
        }

        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out r);
    }

    private static bool ParseShift(string z, ref DT p)
    {
        // forms: NNN unit | [+-]HH:MM[:SS[.fff]] | [+-]YYYY-MM-DD[ HH:MM[:SS[.fff]]]
        int i = 0;
        if (z[0] is '+' or '-')
        {
            i = 1;
        }

        int j = i;
        while (j < z.Length && z[j] is >= '0' and <= '9')
        {
            j++;
        }

        if (j < z.Length && z[j] == ':')
        {
            return ParseTimeShift(z, ref p);
        }

        if (j - i == 4 && j < z.Length && z[j] == '-')
        {
            return ParseDateShift(z, ref p);
        }

        // NNN unit
        int sp = z.IndexOf(' ', StringComparison.Ordinal);
        if (sp < 0)
        {
            return false;
        }

        if (!TryParseNumber(z.AsSpan(0, sp), out double r))
        {
            return false;
        }

        string unit = z[(sp + 1)..];
        if (unit.Length > 1 && unit[^1] == 's')
        {
            unit = unit[..^1];
        }

        int u = Array.IndexOf(s_units, unit);
        if (u < 0)
        {
            return false;
        }

        ComputeJD(ref p);

        // Seconds to days may pass through an out-of-range day number (the end result is checked); months and years need a date.
        double rounder = r < 0 ? -0.5 : 0.5;
        if (u == 4 || u == 5)
        {
            // Observed: a calendar date before the Julian-day range can still be shifted by months and years ('-4713-11-01', '+1 year');
            // only the end result must be in range. A day count that is itself out of range has no calendar date and fails here.
            ComputeYMD_HMS(ref p);
            if (p.IsError)
            {
                return false;
            }

            int whole = (int)r;
            if (u == 4)
            {
                p.M += whole;
                int x = p.M > 0 ? (p.M - 1) / 12 : (p.M - 12) / 12;
                p.Y += x;
                p.M -= x * 12;
            }
            else
            {
                p.Y += whole;
            }

            bool overflow = p.D > DaysInMonth(p.Y, p.M);
            DT alt = p;
            p.ValidJD = false;
            ComputeJD(ref p);
            if (overflow)
            {
                alt.D = DaysInMonth(alt.Y, alt.M);
                alt.ValidJD = false;
                ComputeJD(ref alt);
            }

            r -= whole;
            long frac = r == 0 ? 0 : (long)((r * (u == 4 ? 30.0 : 365.0) * 86400000.0) + (r < 0 ? -0.5 : 0.5));
            p.IJD += frac;
            ClearYMD_HMS_TZ(ref p);
            p.HasFloor = overflow && frac == 0;
            p.FloorJD = alt.IJD;
            return ValidJulianDay(p.IJD);
        }

        p.IJD += (long)((r * s_unitScale[u]) + rounder);
        ClearYMD_HMS_TZ(ref p);
        p.HasFloor = false;
        return true;
    }

    private static bool ParseTimeShift(string z, ref DT p)
    {
        // [+-]HH:MM[:SS[.fff]]
        int sign = 1;
        int i = 0;
        if (z[0] == '-')
        {
            sign = -1;
            i = 1;
        }
        else if (z[0] == '+')
        {
            i = 1;
        }

        var b = System.Text.Encoding.ASCII.GetBytes(z);
        var t = new DT();
        if (ParseHhMmSs(b, i, ref t))
        {
            return false;
        }

        if (t.ValidTz)
        {
            return false;
        }

        ComputeJD(ref p);
        if (!ValidJulianDay(p.IJD))
        {
            return false;
        }

        long ms = (long)(((t.H * 3600000.0) + (t.Mi * 60000.0) + (t.S * 1000.0)) + 0.5);
        p.IJD += sign * ms;
        ClearYMD_HMS_TZ(ref p);
        p.HasFloor = false;
        return true;
    }

    private static bool ParseDateShift(string z, ref DT p)
    {
        int sign = z[0] == '-' ? -1 : 1;
        int start = z[0] is '+' or '-' ? 1 : 0;
        var b = System.Text.Encoding.ASCII.GetBytes(z[start..]);
        var t = new DT();
        if (!ParseYyyyMmDd(b, ref t, shift: true))
        {
            return false;
        }

        ComputeYMD_HMS(ref p);
        if (p.IsError)
        {
            return false;
        }

        // The year is shifted first, then the month, then the day; the time of day after that.
        p.Y += sign * t.Y;
        p.M += sign * t.M;
        int x = p.M > 0 ? (p.M - 1) / 12 : (p.M - 12) / 12;
        p.Y += x;
        p.M -= x * 12;
        p.ValidJD = false;
        ComputeJD(ref p);
        p.IJD += sign * (t.D * 86400000L);
        if (t.ValidHMS)
        {
            p.IJD += sign * (long)(((t.H * 3600000.0) + (t.Mi * 60000.0) + (t.S * 1000.0)) + 0.5);
        }

        ClearYMD_HMS_TZ(ref p);
        p.HasFloor = false;
        return ValidJulianDay(p.IJD);
    }

    private static long LocalOffsetMs(long jd, bool toLocal)
    {
        long unixMs = jd - UnixEpochJD;
        DateTime utc = DateTime.UnixEpoch.AddMilliseconds(Math.Clamp(unixMs, -62135596800000L, 253402300799999L));
        TimeSpan off;
        if (toLocal)
        {
            off = TimeZoneInfo.Local.GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        }
        else
        {
            off = TimeZoneInfo.Local.GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Unspecified));
        }

        return (long)off.TotalMilliseconds;
    }

    // ------------------------------------------------------------------ evaluation

    private static bool IsDate(ReadOnlySpan<Mem> args, int first, ref DT p)
    {
        Span<byte> tmp = stackalloc byte[Values.MaxRealText];
        if (args.Length <= first)
        {
            p.IJD = NowJD();
            p.ValidJD = true;
        }
        else
        {
            ref readonly Mem a = ref args[first];
            switch (a.Kind)
            {
                case StorageClass.Null:
                    return false;
                case StorageClass.Integral:
                    SetRaw(ref p, a.IntValue);
                    break;
                case StorageClass.Real:
                    SetRaw(ref p, a.RealValue);
                    break;
                default:
                    ReadOnlySpan<byte> t = Values.AsText(a, tmp);
                    int nul = t.IndexOf((byte)0);
                    if (nul >= 0)
                    {
                        t = t[..nul];
                    }

                    if (!ParseDateOrTime(t, ref p))
                    {
                        return false;
                    }

                    break;
            }
        }

        // 'subsec' may stand where the time value would be: it then means 'now'.
        for (int i = first + 1; i < args.Length; i++)
        {
            if (args[i].Kind == StorageClass.Null)
            {
                return false;
            }

            ReadOnlySpan<byte> m = Values.AsText(args[i], tmp);
            if (!ParseModifier(m, ref p, i - first))
            {
                return false;
            }
        }

        if (p.RawS && !p.ValidJD && !p.ValidYMD)
        {
            return false;
        }

        ComputeJD(ref p);
        NormalizeDay(ref p);
        return !p.IsError && ValidJulianDay(p.IJD);
    }

    // A day past the end of its month ('2024-02-30') is carried into the next month when the date is printed, because the
    // output is derived from the Julian day number.
    private static void NormalizeDay(ref DT p)
    {
        if (p.ValidYMD && p.ValidJD && p.M >= 1 && p.M <= 12 && p.D > DaysInMonth(p.Y, p.M))
        {
            p.ValidYMD = false;
        }
    }

    private static bool PrepareFirstSubsec(ReadOnlySpan<Mem> args, int first, out bool nowSubsec)
    {
        nowSubsec = false;
        if (args.Length > first && args[first].Kind == StorageClass.Text)
        {
            ReadOnlySpan<byte> t = args[first].Bytes;
            if (t.SequenceEqual("subsec"u8) || t.SequenceEqual("subsecond"u8))
            {
                nowSubsec = true;
            }
        }

        return nowSubsec;
    }

    internal static void Call(FuncId id, ReadOnlySpan<Mem> args, ref Mem dst, ref byte[]? own)
    {
        int first = id == FuncId.Strftime ? 1 : 0;
        if (id == FuncId.TimeDiff)
        {
            TimeDiff(args, ref dst, ref own);
            return;
        }

        if (id == FuncId.Strftime && (args.Length < 1 || args[0].Kind == StorageClass.Null))
        {
            Values.SetNull(ref dst);
            return;
        }

        ReadOnlySpan<Mem> eff = args;
        var p = new DT();
        if (PrepareFirstSubsec(args, first, out _))
        {
            // The time value is 'now' and the first argument is the first modifier.
            p.IJD = NowJD();
            p.ValidJD = true;
            bool ok = true;
            Span<byte> tmp = stackalloc byte[Values.MaxRealText];
            for (int i = first; i < args.Length && ok; i++)
            {
                if (args[i].Kind == StorageClass.Null)
                {
                    ok = false;
                    break;
                }

                ok = ParseModifier(Values.AsText(args[i], tmp), ref p, i - first + 1);
            }

            if (!ok)
            {
                Values.SetNull(ref dst);
                return;
            }

            ComputeJD(ref p);
            NormalizeDay(ref p);
        }
        else if (!IsDate(eff, first, ref p))
        {
            Values.SetNull(ref dst);
            return;
        }

        Output(id, args, ref p, ref dst, ref own);
    }

    private static void Output(FuncId id, ReadOnlySpan<Mem> args, ref DT p, ref Mem dst, ref byte[]? own)
    {
        var sk = new Sink(ref own, 32);
        switch (id)
        {
            case FuncId.Date:
                ComputeYMD(ref p);
                PutDate(ref sk, p);
                break;
            case FuncId.Time:
                ComputeHMS(ref p);
                PutTime(ref sk, p, p.UseSubsec);
                break;
            case FuncId.DateTime:
                ComputeYMD_HMS(ref p);
                PutDate(ref sk, p);
                sk.Put((byte)' ');
                PutTime(ref sk, p, p.UseSubsec);
                break;
            case FuncId.JulianDay:
                Values.SetReal(ref dst, p.IJD / 86400000.0);
                return;
            case FuncId.UnixEpoch:
                if (p.UseSubsec)
                {
                    Values.SetReal(ref dst, (p.IJD - UnixEpochJD) / 1000.0);
                }
                else
                {
                    Values.SetInt(ref dst, (p.IJD / 1000) - (UnixEpochJD / 1000));
                }

                return;
            default:
                if (!Strftime(ref sk, args, ref p))
                {
                    Values.SetNull(ref dst);
                    return;
                }

                break;
        }

        sk.Finish(ref dst, ref own, StorageClass.Text);
    }

    // signCounts: the width includes the '-' as C's %04d does (strftime's %Y and %F); date() and datetime() put four digits after it.
    private static void PutNum(ref Sink sk, long v, int width, char pad = '0', bool signCounts = false)
    {
        if (v < 0)
        {
            // a negative year prints as '-' and then four digits (-0001)
            sk.Put((byte)'-');
            v = -v;
            if (signCounts)
            {
                width--;
            }
        }

        Span<byte> t = stackalloc byte[24];
        v.TryFormat(t, out int n, default, CultureInfo.InvariantCulture);
        sk.Put((byte)pad, width - n);
        sk.Put(t[..n]);
    }

    private static void PutDate(ref Sink sk, in DT p, bool signCounts = false)
    {
        PutNum(ref sk, p.Y, 4, signCounts: signCounts);
        sk.Put((byte)'-');
        PutNum(ref sk, p.M, 2);
        sk.Put((byte)'-');
        PutNum(ref sk, p.D, 2);
    }

    private static void PutTime(ref Sink sk, in DT p, bool subsec)
    {
        PutNum(ref sk, p.H, 2);
        sk.Put((byte)':');
        PutNum(ref sk, p.Mi, 2);
        sk.Put((byte)':');
        if (subsec)
        {
            sk.PutAscii(p.S.ToString("00.000", CultureInfo.InvariantCulture));
        }
        else
        {
            PutNum(ref sk, (int)p.S, 2);
        }
    }

    private static bool Strftime(ref Sink sk, ReadOnlySpan<Mem> args, ref DT p)
    {
        Span<byte> tmp = stackalloc byte[Values.MaxRealText];
        ReadOnlySpan<byte> f = Values.AsText(args[0], tmp);
        ComputeJD(ref p);
        ComputeYMD_HMS(ref p);
        for (int i = 0; i < f.Length; i++)
        {
            if (f[i] != (byte)'%')
            {
                sk.Put(f[i]);
                continue;
            }

            i++;
            if (i >= f.Length)
            {
                return false;
            }

            switch ((char)f[i])
            {
                case 'd': PutNum(ref sk, p.D, 2); break;
                case 'e': PutNum(ref sk, p.D, 2, ' '); break;
                case 'f':
                {
                    double s = p.S;
                    sk.PutAscii(s.ToString("00.000", CultureInfo.InvariantCulture));
                    break;
                }

                case 'F': PutDate(ref sk, p, true); break;
                case 'G':
                case 'g':
                {
                    IsoWeek(p, out int isoYear, out _);
                    if (f[i] == (byte)'G')
                    {
                        PutNum(ref sk, isoYear, 4, signCounts: true);
                    }
                    else
                    {
                        PutNum(ref sk, isoYear % 100, 2);
                    }

                    break;
                }

                case 'H': PutNum(ref sk, p.H, 2); break;
                case 'k': PutNum(ref sk, p.H, 2, ' '); break;
                case 'I':
                case 'l':
                {
                    int h = p.H % 12;
                    if (h == 0)
                    {
                        h = 12;
                    }

                    PutNum(ref sk, h, 2, f[i] == (byte)'I' ? '0' : ' ');
                    break;
                }

                case 'j':
                    PutNum(ref sk, DayOfYear(p), 3);
                    break;
                case 'J':
                    sk.PutAscii(FormatG16(p.IJD / 86400000.0));
                    break;
                case 'm': PutNum(ref sk, p.M, 2); break;
                case 'M': PutNum(ref sk, p.Mi, 2); break;
                case 'p': sk.PutAscii(p.H >= 12 ? "PM" : "AM"); break;
                case 'P': sk.PutAscii(p.H >= 12 ? "pm" : "am"); break;
                case 'R':
                    PutNum(ref sk, p.H, 2);
                    sk.Put((byte)':');
                    PutNum(ref sk, p.Mi, 2);
                    break;
                case 's':
                    if (p.UseSubsec)
                    {
                        sk.PutAscii(((p.IJD - UnixEpochJD) / 1000.0).ToString("0.000", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        PutNum(ref sk, (p.IJD / 1000) - (UnixEpochJD / 1000), 1);
                    }

                    break;
                case 'S': PutNum(ref sk, (int)p.S, 2); break;
                case 'T':
                    PutNum(ref sk, p.H, 2);
                    sk.Put((byte)':');
                    PutNum(ref sk, p.Mi, 2);
                    sk.Put((byte)':');
                    PutNum(ref sk, (int)p.S, 2);
                    break;
                case 'u':
                {
                    int w = WeekdayFromSunday(p);
                    PutNum(ref sk, w == 0 ? 7 : w, 1);
                    break;
                }

                case 'w': PutNum(ref sk, WeekdayFromSunday(p), 1); break;
                case 'U':
                    PutNum(ref sk, (DayOfYear(p) - 1 + 7 - WeekdayFromSunday(p)) / 7, 2);
                    break;
                case 'W':
                    PutNum(ref sk, (DayOfYear(p) - 1 + 7 - ((WeekdayFromSunday(p) + 6) % 7)) / 7, 2);
                    break;
                case 'V':
                {
                    IsoWeek(p, out _, out int wk);
                    PutNum(ref sk, wk, 2);
                    break;
                }

                case 'Y': PutNum(ref sk, p.Y, 4, signCounts: true); break;
                case '%': sk.Put((byte)'%'); break;
                default: return false;
            }
        }

        return true;
    }

    private static string FormatG16(double v)
    {
        // "%.16g" as C prints it
        string e = v.ToString("E15", CultureInfo.InvariantCulture);
        int ePos = e.IndexOf('E', StringComparison.Ordinal);
        int exp = int.Parse(e.AsSpan(ePos + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        if (exp < -4 || exp >= 16)
        {
            string m = e[..ePos].TrimEnd('0').TrimEnd('.');
            return m + "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture);
        }

        string f = v.ToString("F" + Math.Max(0, 15 - exp), CultureInfo.InvariantCulture);
        return f.Contains('.', StringComparison.Ordinal) ? f.TrimEnd('0').TrimEnd('.') : f;
    }

    private static int WeekdayFromSunday(in DT p) => (int)(((p.IJD + 129600000) / 86400000) % 7);

    private static int DayOfYear(in DT p)
    {
        int doy = p.D;
        for (int m = 1; m < p.M; m++)
        {
            doy += DaysInMonth(p.Y, m);
        }

        return doy;
    }

    private static void IsoWeek(in DT p, out int isoYear, out int week)
    {
        int wd = WeekdayFromSunday(p);
        int mondayBased = (wd + 6) % 7; // Monday = 0
        int doy = DayOfYear(p);
        // the Thursday of this week decides the year
        int thursdayDoy = doy - mondayBased + 3;
        isoYear = p.Y;
        if (thursdayDoy < 1)
        {
            isoYear--;
            thursdayDoy += IsLeap(isoYear) ? 366 : 365;
        }
        else if (thursdayDoy > (IsLeap(p.Y) ? 366 : 365))
        {
            thursdayDoy -= IsLeap(p.Y) ? 366 : 365;
            isoYear++;
        }

        week = ((thursdayDoy - 1) / 7) + 1;
    }

    private static void TimeDiff(ReadOnlySpan<Mem> args, ref Mem dst, ref byte[]? own)
    {
        var a = new DT();
        var b = new DT();
        if (!IsDate(args.Slice(0, 1), 0, ref a) || !IsDate(args.Slice(1, 1), 0, ref b))
        {
            Values.SetNull(ref dst);
            return;
        }

        ComputeYMD_HMS(ref a);
        ComputeYMD_HMS(ref b);
        // The amount to add to B to reach A (lang_datefunc): the result is the period for which date(B, sign || period) is A. Observed:
        // the whole months are the most that can be shifted from B without passing A (a day past the end of the month carries into
        // the next one, as in the date modifiers), then the rest is counted in days and the exact time of day.
        int sign = a.IJD >= b.IJD ? 1 : -1;
        long target = a.IJD;
        int months = ((a.Y - b.Y) * 12) + (a.M - b.M);
        months = Math.Abs(months);
        long shifted = 0;
        for (; months >= 0; months--)
        {
            DT t = b;
            int total = (b.M - 1) + (sign * months);
            t.Y = b.Y + (total >= 0 ? total / 12 : -((11 - total) / 12));
            t.M = total - ((t.Y - b.Y) * 12) + 1;
            t.ValidJD = false;
            ComputeJD(ref t);
            shifted = t.IJD;
            if (sign > 0 ? shifted <= target : shifted >= target)
            {
                break;
            }
        }

        long rest = sign > 0 ? target - shifted : shifted - target;
        int dy = months / 12;
        int dm = months % 12;
        long dd = rest / 86400000;
        long dms = rest % 86400000;

        var sk = new Sink(ref own, 32);
        sk.Put(sign < 0 ? (byte)'-' : (byte)'+');
        PutNum(ref sk, dy, 4);
        sk.Put((byte)'-');
        PutNum(ref sk, dm, 2);
        sk.Put((byte)'-');
        PutNum(ref sk, dd, 2);
        sk.Put((byte)' ');
        PutNum(ref sk, dms / 3600000, 2);
        sk.Put((byte)':');
        PutNum(ref sk, (dms / 60000) % 60, 2);
        sk.Put((byte)':');
        sk.PutAscii(((dms % 60000) / 1000.0).ToString("00.000", CultureInfo.InvariantCulture));
        sk.Finish(ref dst, ref own, StorageClass.Text);
    }
}
