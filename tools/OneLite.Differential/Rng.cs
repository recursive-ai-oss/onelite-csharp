using System.Security.Cryptography;
using System.Text;

namespace RecursiveAI.OneLite.Differential;

/// <summary>
/// A small deterministic generator (SplitMix64). It is written out here instead of using <see cref="Random"/> so that a seed gives the
/// same statements on every runtime version and machine: the campaign fingerprints depend on it.
/// </summary>
internal sealed class Rng
{
    private ulong _s;

    internal Rng(ulong seed) => _s = seed;

    /// <summary>A stream for case <paramref name="index"/> of campaign <paramref name="seed"/>.</summary>
    internal static Rng ForCase(long seed, long index) => new(Mix((ulong)seed, (ulong)index + 0x9E3779B97F4A7C15UL));

    internal static ulong Mix(ulong a, ulong b)
    {
        ulong z = a * 0xBF58476D1CE4E5B9UL + b + 0x94D049BB133111EBUL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    internal ulong NextU64()
    {
        _s += 0x9E3779B97F4A7C15UL;
        ulong z = _s;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>A value in [0, <paramref name="n"/>).</summary>
    internal int Next(int n) => n <= 1 ? 0 : (int)(NextU64() % (ulong)n);

    /// <summary>A value in [lo, hi].</summary>
    internal int Range(int lo, int hi) => lo + Next(hi - lo + 1);

    /// <summary>True <paramref name="pct"/> times in a hundred.</summary>
    internal bool Chance(int pct) => Next(100) < pct;

    internal T Pick<T>(IReadOnlyList<T> items) => items[Next(items.Count)];

    /// <summary>Picks by weight: <c>(weight, value)</c> pairs.</summary>
    internal T Weighted<T>(params (int Weight, T Value)[] items)
    {
        int total = 0;
        foreach ((int w, _) in items)
        {
            total += w;
        }

        int r = Next(total);
        foreach ((int w, T v) in items)
        {
            if (r < w)
            {
                return v;
            }

            r -= w;
        }

        return items[^1].Value;
    }
}

internal static class Hashing
{
    internal static string Short(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..10].ToLowerInvariant();
}
