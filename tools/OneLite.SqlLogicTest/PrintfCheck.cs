using System;
using System.Collections.Generic;
using System.Globalization;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.SqlLogicTest;

/// <summary>
/// Checks <see cref="Rendering.FormatFixed3"/> against the pinned library's own <c>printf('%.3f', x)</c> over many doubles. The scripts
/// were written with SQLite's printf, whose rounding is not that of a textbook exact conversion, so the renderer is tuned against it.
/// </summary>
public static class PrintfCheck
{
    /// <summary>Runs the check. Returns the number of mismatches (printed, first 20).</summary>
    public static int Run(int count, int seed, string? dump = null)
    {
        var rng = new Random(seed);
        using SqliteDb db = SqliteDb.Open(":memory:");
        using SqliteStatement st = db.Prepare("select printf('%.3f', ?1)");
        int bad = 0, n = 0;
        using var dumpFile = dump is null ? null : new System.IO.StreamWriter(dump);
        foreach (double d in Values(rng, count))
        {
            st.Reset();
            st.Bind(1, d);
            _ = st.Step();
            string want = st.TextOrNull(0)!;
            string got = Rendering.FormatFixed3(d);
            dumpFile?.WriteLine(BitConverter.DoubleToInt64Bits(d).ToString("x16", CultureInfo.InvariantCulture) + "\t" + want);
            n++;
            if (want != got)
            {
                bad++;
                if (bad <= 20)
                {
                    Console.WriteLine($"{d.ToString("R", CultureInfo.InvariantCulture)}: sqlite '{want}', ours '{got}'");
                }
            }
        }
        Console.WriteLine($"printf check: {n} values, {bad} mismatches");
        return bad;
    }

    private static IEnumerable<double> Values(Random rng, int count)
    {
        double[] fixedVals = [0.0, -0.0, -0.0004, -0.0001, -1e-10, -4e-324, 0.0005, -0.0005, 0.0015, 0.0625, 0.1875, 2.0005, 1.0005, 0.9995, 1e15, 1e16, 1e17, 123456789012345.678,
            9223372036854775807.0, -9223372036854775808.0, 18446744073709551616.0, 1e22, 1e300, 5e-324, 4.9999999999999995e-4, 0.4995, 1234567.8905];
        foreach (double d in fixedVals)
        {
            yield return d;
        }
        for (int i = 0; i < count; i++)
        {
            double v;
            switch (i % 8)
            {
                case 0: v = BitConverter.Int64BitsToDouble(rng.NextInt64()); if (!double.IsFinite(v)) { v = 1.5; } break;
                case 1: v = (rng.NextDouble() - 0.5) * Math.Pow(10, rng.Next(-6, 24)); break;
                case 2: v = rng.Next(-100000, 100000) / 1000.0 + 0.0005 * rng.Next(0, 2); break;   // decimal ties
                case 3: v = (rng.Next(-1_000_000, 1_000_000)) / 16.0; break;                      // exact binary ties
                case 4: v = rng.Next() / (double)rng.Next(1, 1000); break;                         // ratios
                case 5: v = (double)rng.NextInt64(long.MinValue, long.MaxValue); break;            // big integers as floats
                case 6: v = rng.Next(1, 1 << 30) * Math.Pow(10, rng.Next(-12, -2)) + 0.0005; break;
                default: v = Math.Round(rng.NextDouble() * 1000, rng.Next(0, 8)) + (rng.Next(2) * 0.0005); break;
            }
            yield return v;
        }
    }
}
