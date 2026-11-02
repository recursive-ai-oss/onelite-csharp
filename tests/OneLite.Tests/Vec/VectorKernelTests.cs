using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using RecursiveAI.OneLite.Internal.Simd;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Vec;

/// <summary>The distance kernels: every width gives the same bits, and the value agrees with a plain double-precision loop.</summary>
public sealed class VectorKernelTests : IDisposable
{
    private static readonly KernelPath[] s_paths = [KernelPath.Scalar, KernelPath.Vector128, KernelPath.Vector256, KernelPath.Vector512];

    public void Dispose() => VectorKernels.ForcePath(VectorKernels.Detect());

    private static float[] RandomFloats(Random r, int n, double scale)
    {
        var a = new float[n];
        for (int i = 0; i < n; i++)
        {
            a[i] = (float)((r.NextDouble() - 0.5) * scale * (r.Next(8) == 0 ? 1000 : 1));
        }

        return a;
    }

    public static IEnumerable<object[]> Lengths()
    {
        var all = new List<int>();
        for (int i = 0; i <= 70; i++)
        {
            all.Add(i);
        }

        all.AddRange([95, 127, 128, 129, 255, 256, 257, 768, 1000, 1536, 3072, 4097, 8192]);
        foreach (int n in all)
        {
            yield return [n];
        }
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void FloatKernelsGiveTheSameBitsOnEveryPath(int n)
    {
        var r = new Random(1000 + n);
        float[] a = RandomFloats(r, n, 4), b = RandomFloats(r, n, 4);
        double refL2 = 0, refL1 = 0, refDot = 0;
        for (int i = 0; i < n; i++)
        {
            double d = (double)a[i] - b[i];
            refL2 += d * d;
            refL1 += Math.Abs(d);
            refDot += (double)a[i] * b[i];
        }

        long l2 = 0, l1 = 0, dot = 0;
        foreach (KernelPath p in s_paths)
        {
            long x = BitConverter.DoubleToInt64Bits(VectorKernels.L2Squared(a, b, p));
            long y = BitConverter.DoubleToInt64Bits(VectorKernels.L1(a, b, p));
            long z = BitConverter.DoubleToInt64Bits(VectorKernels.Dot(a, b, p));
            if (p == KernelPath.Scalar)
            {
                (l2, l1, dot) = (x, y, z);
            }
            else
            {
                Assert.Equal(l2, x);
                Assert.Equal(l1, y);
                Assert.Equal(dot, z);
            }
        }

        Assert.True(Math.Abs(BitConverter.Int64BitsToDouble(l2) - refL2) <= 1e-12 * Math.Max(refL2, 1e-30));
        Assert.True(Math.Abs(BitConverter.Int64BitsToDouble(l1) - refL1) <= 1e-12 * Math.Max(refL1, 1e-30));
        Assert.True(Math.Abs(BitConverter.Int64BitsToDouble(dot) - refDot) <= 1e-12 * Math.Max(Math.Abs(refDot), Math.Abs(refL1)) + 1e-300);
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void Int8AndBitKernelsAreExact(int n)
    {
        var r = new Random(2000 + n);
        var a = new sbyte[n];
        var b = new sbyte[n];
        for (int i = 0; i < n; i++)
        {
            a[i] = (sbyte)r.Next(-128, 128);
            b[i] = (sbyte)r.Next(-128, 128);
        }

        long l2 = 0, l1 = 0, dot = 0, ham = 0;
        var ba = new byte[n];
        var bb = new byte[n];
        r.NextBytes(ba);
        r.NextBytes(bb);
        for (int i = 0; i < n; i++)
        {
            l2 += (a[i] - b[i]) * (a[i] - b[i]);
            l1 += Math.Abs(a[i] - b[i]);
            dot += a[i] * b[i];
            ham += BitOperations.PopCount((uint)(ba[i] ^ bb[i]));
        }

        Assert.Equal(l2, VectorKernels.L2Squared(a, b));
        Assert.Equal(l1, VectorKernels.L1(a, b));
        Assert.Equal(dot, VectorKernels.Dot(a, b));
        Assert.Equal(ham, VectorKernels.Hamming(ba, bb));
    }

    [Fact]
    public void ExtremeInt8ValuesDoNotOverflowALongBlock()
    {
        var a = new sbyte[8192 * 3 + 5];
        var b = new sbyte[a.Length];
        Array.Fill(a, sbyte.MinValue);
        Array.Fill(b, sbyte.MaxValue);
        Assert.Equal(255L * 255 * a.Length, VectorKernels.L2Squared(a, b));
        Assert.Equal(255L * a.Length, VectorKernels.L1(a, b));
        Assert.Equal(-128L * 127 * a.Length, VectorKernels.Dot(a, b));
    }

    [Fact]
    public void ReadsFloatsFromUnalignedBytes()
    {
        var bytes = new byte[4 * 40 + 1];
        var r = new Random(5);
        float[] a = RandomFloats(r, 40, 2);
        MemoryMarshal.AsBytes(a.AsSpan()).CopyTo(bytes.AsSpan(1));
        double viaBytes = VectorKernels.NormSquared(VectorKernels.AsFloats(bytes.AsSpan(1, 160)));
        Assert.Equal(VectorKernels.NormSquared(a), viaBytes);
    }
}
