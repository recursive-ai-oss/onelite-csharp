using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace RecursiveAI.OneLite.Internal.Simd;

/// <summary>The instruction width the distance kernels run at. All widths give bit-identical results (see <see cref="VectorKernels"/>).</summary>
internal enum KernelPath : byte
{
    /// <summary>Plain loops; used where no vector unit is accelerated (browser builds included).</summary>
    Scalar,

    /// <summary>128-bit vectors (SSE2, NEON).</summary>
    Vector128,

    /// <summary>256-bit vectors (AVX2).</summary>
    Vector256,

    /// <summary>512-bit vectors (AVX-512).</summary>
    Vector512,
}

/// <summary>One element-pair term of a float distance: <c>(a-b)^2</c>, <c>|a-b|</c> or <c>a*b</c>, in double precision.</summary>
internal interface IFloatTerm
{
    static abstract double Of(double a, double b);

    static abstract Vector128<double> Of(Vector128<double> a, Vector128<double> b);

    static abstract Vector256<double> Of(Vector256<double> a, Vector256<double> b);

    static abstract Vector512<double> Of(Vector512<double> a, Vector512<double> b);
}

internal readonly struct SquaredDifference : IFloatTerm
{
    public static double Of(double a, double b) => (a - b) * (a - b);

    public static Vector128<double> Of(Vector128<double> a, Vector128<double> b)
    {
        Vector128<double> d = a - b;
        return d * d;
    }

    public static Vector256<double> Of(Vector256<double> a, Vector256<double> b)
    {
        Vector256<double> d = a - b;
        return d * d;
    }

    public static Vector512<double> Of(Vector512<double> a, Vector512<double> b)
    {
        Vector512<double> d = a - b;
        return d * d;
    }
}

internal readonly struct AbsoluteDifference : IFloatTerm
{
    public static double Of(double a, double b) => Math.Abs(a - b);

    public static Vector128<double> Of(Vector128<double> a, Vector128<double> b) => Vector128.Abs(a - b);

    public static Vector256<double> Of(Vector256<double> a, Vector256<double> b) => Vector256.Abs(a - b);

    public static Vector512<double> Of(Vector512<double> a, Vector512<double> b) => Vector512.Abs(a - b);
}

internal readonly struct Product : IFloatTerm
{
    public static double Of(double a, double b) => a * b;

    public static Vector128<double> Of(Vector128<double> a, Vector128<double> b) => a * b;

    public static Vector256<double> Of(Vector256<double> a, Vector256<double> b) => a * b;

    public static Vector512<double> Of(Vector512<double> a, Vector512<double> b) => a * b;
}

/// <summary>
/// Distance kernels over float32, int8 and bit vectors, written for the vector tables and free of any engine dependency.
/// <para>
/// <b>Determinism.</b> A float sum is made of 16 partial sums in double precision: element <c>i</c> always goes to partial <c>i % 16</c>, products
/// and sums are separate (never fused), and the 16 partials are folded in one fixed pairwise order. Every path (scalar, 128, 256, 512 bits, on any
/// CPU) therefore returns the same bits. Integer sums are exact. Vectors are read as little-endian, which every supported platform is.
/// </para>
/// <para>Memory safety: only spans and <c>MemoryMarshal.GetReference</c> with offsets proved below the span length before each loop.</para>
/// </summary>
internal static class VectorKernels
{
    private static KernelPath s_path = Detect();

    /// <summary>The path in use; chosen once from what the hardware accelerates.</summary>
    internal static KernelPath Path => s_path;

    /// <summary>The best accelerated path of this machine.</summary>
    internal static KernelPath Detect()
    {
        if (Vector512.IsHardwareAccelerated)
        {
            return KernelPath.Vector512;
        }

        if (Vector256.IsHardwareAccelerated)
        {
            return KernelPath.Vector256;
        }

        return Vector128.IsHardwareAccelerated ? KernelPath.Vector128 : KernelPath.Scalar;
    }

    /// <summary>Forces a path (tests only; every path is correct on every machine, a wider one is just slow where it is not accelerated).</summary>
    internal static void ForcePath(KernelPath path) => s_path = path;

    // ------------------------------------------------------------------ float32

    /// <summary>Sum of squared differences of two float32 vectors of equal length.</summary>
    internal static double L2Squared(ReadOnlySpan<float> a, ReadOnlySpan<float> b) => Sum<SquaredDifference>(a, b, s_path);

    /// <summary>Sum of absolute differences.</summary>
    internal static double L1(ReadOnlySpan<float> a, ReadOnlySpan<float> b) => Sum<AbsoluteDifference>(a, b, s_path);

    /// <summary>Dot product.</summary>
    internal static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b) => Sum<Product>(a, b, s_path);

    /// <summary>Squared length of a vector.</summary>
    internal static double NormSquared(ReadOnlySpan<float> a) => Sum<Product>(a, a, s_path);

    /// <summary>The same as <see cref="L2Squared(ReadOnlySpan{float}, ReadOnlySpan{float})"/> on a given path.</summary>
    internal static double L2Squared(ReadOnlySpan<float> a, ReadOnlySpan<float> b, KernelPath path) => Sum<SquaredDifference>(a, b, path);

    /// <summary>The same as <see cref="L1(ReadOnlySpan{float}, ReadOnlySpan{float})"/> on a given path.</summary>
    internal static double L1(ReadOnlySpan<float> a, ReadOnlySpan<float> b, KernelPath path) => Sum<AbsoluteDifference>(a, b, path);

    /// <summary>The same as <see cref="Dot(ReadOnlySpan{float}, ReadOnlySpan{float})"/> on a given path.</summary>
    internal static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b, KernelPath path) => Sum<Product>(a, b, path);

    /// <summary>Reads a little-endian float32 vector out of bytes (the length must be a multiple of 4).</summary>
    internal static ReadOnlySpan<float> AsFloats(ReadOnlySpan<byte> bytes) => MemoryMarshal.Cast<byte, float>(bytes);

    /// <summary>The same bytes as int8 elements (same length; a reinterpretation, no copy).</summary>
    internal static ReadOnlySpan<sbyte> AsInt8s(ReadOnlySpan<byte> bytes) => MemoryMarshal.Cast<byte, sbyte>(bytes);

    private static double Sum<T>(ReadOnlySpan<float> a, ReadOnlySpan<float> b, KernelPath path)
        where T : struct, IFloatTerm
    {
        int n = Math.Min(a.Length, b.Length);
        int main = n & ~15;
        Span<double> p = stackalloc double[16];
        ref float ra = ref MemoryMarshal.GetReference(a);
        ref float rb = ref MemoryMarshal.GetReference(b);

        // Every load below reads elements [i, i + 16) with i + 16 <= main <= n <= both lengths.
        switch (path)
        {
            case KernelPath.Vector512:
            {
                Vector512<double> acc0 = default, acc1 = default;
                for (int i = 0; i < main; i += 16)
                {
                    (Vector512<double> al, Vector512<double> au) = Vector512.Widen(Vector512.LoadUnsafe(ref ra, (nuint)i));
                    (Vector512<double> bl, Vector512<double> bu) = Vector512.Widen(Vector512.LoadUnsafe(ref rb, (nuint)i));
                    acc0 += T.Of(al, bl);
                    acc1 += T.Of(au, bu);
                }

                acc0.CopyTo(p);
                acc1.CopyTo(p[8..]);
                break;
            }

            case KernelPath.Vector256:
            {
                Vector256<double> acc0 = default, acc1 = default, acc2 = default, acc3 = default;
                for (int i = 0; i < main; i += 16)
                {
                    (Vector256<double> al, Vector256<double> au) = Vector256.Widen(Vector256.LoadUnsafe(ref ra, (nuint)i));
                    (Vector256<double> bl, Vector256<double> bu) = Vector256.Widen(Vector256.LoadUnsafe(ref rb, (nuint)i));
                    acc0 += T.Of(al, bl);
                    acc1 += T.Of(au, bu);
                    (Vector256<double> cl, Vector256<double> cu) = Vector256.Widen(Vector256.LoadUnsafe(ref ra, (nuint)(i + 8)));
                    (Vector256<double> dl, Vector256<double> du) = Vector256.Widen(Vector256.LoadUnsafe(ref rb, (nuint)(i + 8)));
                    acc2 += T.Of(cl, dl);
                    acc3 += T.Of(cu, du);
                }

                acc0.CopyTo(p);
                acc1.CopyTo(p[4..]);
                acc2.CopyTo(p[8..]);
                acc3.CopyTo(p[12..]);
                break;
            }

            case KernelPath.Vector128:
            {
                Vector128<double> a0 = default, a1 = default, a2 = default, a3 = default, a4 = default, a5 = default, a6 = default, a7 = default;
                for (int i = 0; i < main; i += 16)
                {
                    (Vector128<double> l, Vector128<double> u) = Vector128.Widen(Vector128.LoadUnsafe(ref ra, (nuint)i));
                    (Vector128<double> m, Vector128<double> v) = Vector128.Widen(Vector128.LoadUnsafe(ref rb, (nuint)i));
                    a0 += T.Of(l, m);
                    a1 += T.Of(u, v);
                    (l, u) = Vector128.Widen(Vector128.LoadUnsafe(ref ra, (nuint)(i + 4)));
                    (m, v) = Vector128.Widen(Vector128.LoadUnsafe(ref rb, (nuint)(i + 4)));
                    a2 += T.Of(l, m);
                    a3 += T.Of(u, v);
                    (l, u) = Vector128.Widen(Vector128.LoadUnsafe(ref ra, (nuint)(i + 8)));
                    (m, v) = Vector128.Widen(Vector128.LoadUnsafe(ref rb, (nuint)(i + 8)));
                    a4 += T.Of(l, m);
                    a5 += T.Of(u, v);
                    (l, u) = Vector128.Widen(Vector128.LoadUnsafe(ref ra, (nuint)(i + 12)));
                    (m, v) = Vector128.Widen(Vector128.LoadUnsafe(ref rb, (nuint)(i + 12)));
                    a6 += T.Of(l, m);
                    a7 += T.Of(u, v);
                }

                a0.CopyTo(p);
                a1.CopyTo(p[2..]);
                a2.CopyTo(p[4..]);
                a3.CopyTo(p[6..]);
                a4.CopyTo(p[8..]);
                a5.CopyTo(p[10..]);
                a6.CopyTo(p[12..]);
                a7.CopyTo(p[14..]);
                break;
            }

            default:
                for (int i = 0; i < main; i++)
                {
                    p[i & 15] += T.Of(a[i], b[i]);
                }

                break;
        }

        // The tail continues the same 16 partials, so a tail element lands where the wide loop would have put it.
        for (int i = main; i < n; i++)
        {
            p[i - main] += T.Of(a[i], b[i]);
        }

        // Fixed pairwise fold of the 16 partials.
        for (int j = 0; j < 8; j++)
        {
            p[j] += p[j + 8];
        }

        for (int j = 0; j < 4; j++)
        {
            p[j] += p[j + 4];
        }

        for (int j = 0; j < 2; j++)
        {
            p[j] += p[j + 2];
        }

        return p[0] + p[1];
    }

    // ------------------------------------------------------------------ int8 (exact integer sums, so one width serves every machine)

    /// <summary>Sum of squared differences of two int8 vectors.</summary>
    internal static long L2Squared(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b) => SumInt8(a, b, 0);

    /// <summary>Sum of absolute differences of two int8 vectors.</summary>
    internal static long L1(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b) => SumInt8(a, b, 1);

    /// <summary>Dot product of two int8 vectors.</summary>
    internal static long Dot(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b) => SumInt8(a, b, 2);

    private static long SumInt8(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b, int kind)
    {
        int n = Math.Min(a.Length, b.Length);
        long total = 0;
        int i = 0;

        // Per-block int32 accumulators: a block of at most 8192 elements adds at most 8192 * 65025 < 2^31 in total.
        if (Vector128.IsHardwareAccelerated)
        {
            ref sbyte ra = ref MemoryMarshal.GetReference(a);
            ref sbyte rb = ref MemoryMarshal.GetReference(b);
            while (n - i >= 16)
            {
                int blockEnd = i + Math.Min(((n - i) / 16) * 16, 8192);
                Vector128<int> acc = default;
                for (; i < blockEnd; i += 16)
                {
                    // Reads [i, i + 16) with i + 16 <= blockEnd <= n.
                    Vector128<sbyte> va = Vector128.LoadUnsafe(ref ra, (nuint)i);
                    Vector128<sbyte> vb = Vector128.LoadUnsafe(ref rb, (nuint)i);
                    (Vector128<short> al, Vector128<short> au) = Vector128.Widen(va);
                    (Vector128<short> bl, Vector128<short> bu) = Vector128.Widen(vb);
                    acc += Term(al, bl, kind) + Term(au, bu, kind);
                }

                total += Vector128.Sum(acc);
            }
        }

        for (; i < n; i++)
        {
            int x = a[i], y = b[i];
            total += kind switch { 0 => (x - y) * (x - y), 1 => Math.Abs(x - y), _ => x * y };
        }

        return total;
    }

    private static Vector128<int> Term(Vector128<short> a, Vector128<short> b, int kind)
    {
        (Vector128<int> al, Vector128<int> au) = Vector128.Widen(a);
        (Vector128<int> bl, Vector128<int> bu) = Vector128.Widen(b);
        switch (kind)
        {
            case 0:
            {
                Vector128<int> dl = al - bl, du = au - bu;
                return (dl * dl) + (du * du);
            }

            case 1:
                return Vector128.Abs(al - bl) + Vector128.Abs(au - bu);
            default:
                return (al * bl) + (au * bu);
        }
    }

    // ------------------------------------------------------------------ bits

    /// <summary>Number of differing bits between two packed bit vectors of equal length.</summary>
    internal static long Hamming(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int n = Math.Min(a.Length, b.Length);
        long total = 0;
        int i = 0;
        for (; i + 8 <= n; i += 8)
        {
            total += BitOperations.PopCount(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(a[i..]) ^ System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(b[i..]));
        }

        for (; i < n; i++)
        {
            total += BitOperations.PopCount((uint)(a[i] ^ b[i]));
        }

        return total;
    }
}
