using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using RecursiveAI.OneLite.Internal.Simd;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite.Extensions.Vec;

/// <summary>The scalar SQL functions of the vector extension (<c>vec_f32</c>, <c>vec_distance_l2</c>, ...), following the documented sqlite-vec API.</summary>
internal static class VecFunctions
{
    /// <summary>The version string <c>vec_version()</c> returns.</summary>
    internal const string Version = "v0.1.0-onelite";

    private static void Put(ref Mem dst, ref byte[]? own, VecElement type, ReadOnlySpan<byte> bytes)
    {
        Values.SetBytes(ref dst, ref own, StorageClass.Blob, bytes);
        dst.Subtype = VecValue.SubtypeOf(type);
    }

    internal static void Call(FuncId id, ReadOnlySpan<Mem> args, ref Mem dst, ref byte[]? own)
    {
        switch (id)
        {
            case FuncId.VecF32:
            {
                VecValue v = VecValue.ReadFloat32(args[0]);
                Put(ref dst, ref own, VecElement.Float32, v.Bytes.Span);
                return;
            }

            case FuncId.VecInt8:
            {
                if (args[0].Kind == StorageClass.Text)
                {
                    Put(ref dst, ref own, VecElement.Int8, VecValue.ParseJson(args[0].Bytes, true));
                    return;
                }

                // A blob is taken as int8 bytes whatever its tag.
                ReadOnlySpan<byte> b = RequireBlob(args[0]);
                Put(ref dst, ref own, VecElement.Int8, b);
                return;
            }

            case FuncId.VecBit:
            {
                if (args[0].Kind != StorageClass.Blob)
                {
                    throw VecValue.Error("Unknown type for bitvector.");
                }

                if (args[0].Length == 0)
                {
                    throw VecValue.Error("zero-length vectors are not supported.");
                }

                Put(ref dst, ref own, VecElement.Bit, args[0].Bytes);
                return;
            }

            case FuncId.VecLength:
                Values.SetInt(ref dst, VecValue.Read(args[0]).Dimensions);
                return;
            case FuncId.VecType:
            {
                VecValue v = VecValue.Read(args[0]);
                Values.SetBytes(ref dst, ref own, StorageClass.Text, Encoding.ASCII.GetBytes(VecValue.Name(v.Type)));
                return;
            }

            case FuncId.VecToJson:
                ToJson(VecValue.Read(args[0]), ref dst, ref own);
                return;
            case FuncId.VecAdd:
            case FuncId.VecSub:
                AddSub(id == FuncId.VecAdd, args, ref dst, ref own);
                return;
            case FuncId.VecNormalize:
                Normalize(args[0], ref dst, ref own);
                return;
            case FuncId.VecSlice:
                Slice(args, ref dst, ref own);
                return;
            case FuncId.VecDistanceL2:
            case FuncId.VecDistanceL1:
            case FuncId.VecDistanceCosine:
            case FuncId.VecDistanceHamming:
                Distance(id, args, ref dst);
                return;
            case FuncId.VecQuantizeBinary:
                QuantizeBinary(args[0], ref dst, ref own);
                return;
            case FuncId.VecQuantizeInt8:
                QuantizeInt8(args, ref dst, ref own);
                return;
            case FuncId.VecVersion:
                Values.SetBytes(ref dst, ref own, StorageClass.Text, Encoding.ASCII.GetBytes(Version));
                return;
            default:
                Values.SetBytes(ref dst, ref own, StorageClass.Text, Encoding.ASCII.GetBytes("Version: " + Version + "\nKernel: " + VectorKernels.Path));
                return;
        }
    }

    private static ReadOnlySpan<byte> RequireBlob(in Mem m)
    {
        if (m.Kind != StorageClass.Blob)
        {
            throw VecValue.Error("Input must have type BLOB (compact format) or TEXT (JSON), found "
                + m.Kind switch { StorageClass.Null => "NULL", StorageClass.Integral => "INTEGER", _ => "FLOAT" });
        }

        if (m.Length == 0)
        {
            throw VecValue.Error("zero-length vectors are not supported.");
        }

        return m.Bytes;
    }

    private static void ToJson(VecValue v, ref Mem dst, ref byte[]? own)
    {
        var sb = new StringBuilder("[");
        int n = v.Dimensions;
        for (int i = 0; i < n; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            switch (v.Type)
            {
                case VecElement.Float32:
                    sb.Append(VecValue.FormatFloat(v.Floats[i]));
                    break;
                case VecElement.Int8:
                    sb.Append(v.Int8s[i].ToString(CultureInfo.InvariantCulture));
                    break;
                default:
                    sb.Append(((v.Bytes.Span[i / 8] >> (i % 8)) & 1) != 0 ? '1' : '0');
                    break;
            }
        }

        sb.Append(']');
        Values.SetBytes(ref dst, ref own, StorageClass.Text, Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static void AddSub(bool add, ReadOnlySpan<Mem> args, ref Mem dst, ref byte[]? own)
    {
        VecValue a = VecValue.Read(args[0]);
        VecValue b = VecValue.Read(args[1]);
        CheckSame(a, b);
        if (a.Type == VecElement.Bit)
        {
            throw VecValue.Error(add ? "Cannot add two bitvectors together." : "Cannot subtract two bitvectors together.");
        }

        var r = new byte[a.Bytes.Length];
        if (a.Type == VecElement.Float32)
        {
            ReadOnlySpan<float> x = a.Floats, y = b.Floats;
            for (int i = 0; i < x.Length; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(r.AsSpan(i * 4), add ? x[i] + y[i] : x[i] - y[i]);
            }
        }
        else
        {
            ReadOnlySpan<sbyte> x = a.Int8s, y = b.Int8s;
            for (int i = 0; i < x.Length; i++)
            {
                r[i] = unchecked((byte)(add ? x[i] + y[i] : x[i] - y[i]));
            }
        }

        Put(ref dst, ref own, a.Type, r);
    }

    private static void CheckSame(VecValue a, VecValue b)
    {
        if (a.Type != b.Type)
        {
            throw VecValue.Error("Vector type mistmatch. First vector has type " + VecValue.Name(a.Type) + ", while the second has type " + VecValue.Name(b.Type) + ".");
        }

        if (a.Dimensions != b.Dimensions)
        {
            throw VecValue.Error("Vector dimension mistmatch. First vector has " + a.Dimensions.ToString(CultureInfo.InvariantCulture)
                + " dimensions, while the second has " + b.Dimensions.ToString(CultureInfo.InvariantCulture) + " dimensions.");
        }
    }

    private static void Normalize(in Mem arg, ref Mem dst, ref byte[]? own)
    {
        VecValue v = VecValue.Read(arg);
        if (v.Type != VecElement.Float32)
        {
            throw VecValue.Error("only float32 vectors are supported when normalizing");
        }

        ReadOnlySpan<float> x = v.Floats;
        double norm = Math.Sqrt(VectorKernels.NormSquared(x));
        var r = new byte[v.Bytes.Length];
        for (int i = 0; i < x.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(r.AsSpan(i * 4), (float)(x[i] / norm));
        }

        Put(ref dst, ref own, VecElement.Float32, r);
    }

    private static void Slice(ReadOnlySpan<Mem> args, ref Mem dst, ref byte[]? own)
    {
        VecValue v = VecValue.Read(args[0]);
        long start = Values.ToInt(args[1]), end = Values.ToInt(args[2]);
        int dims = v.Dimensions;
        if (start < 0)
        {
            throw VecValue.Error("slice 'start' index must be a postive number.");
        }

        if (end < 0)
        {
            throw VecValue.Error("slice 'end' index must be a postive number.");
        }

        if (start > dims)
        {
            throw VecValue.Error("slice 'start' index is greater than the number of dimensions");
        }

        if (end > dims)
        {
            throw VecValue.Error("slice 'end' index is greater than the number of dimensions");
        }

        if (start > end)
        {
            throw VecValue.Error("slice 'start' index is greater than 'end' index");
        }

        if (start == end)
        {
            throw VecValue.Error("slice 'start' index is equal to the 'end' index, vectors must have non-zero length");
        }

        int s = (int)start, e = (int)end;
        ReadOnlySpan<byte> b = v.Bytes.Span;
        switch (v.Type)
        {
            case VecElement.Float32:
                Put(ref dst, ref own, v.Type, b[(s * 4)..(e * 4)]);
                break;
            case VecElement.Int8:
                Put(ref dst, ref own, v.Type, b[s..e]);
                break;
            default:
                if (s % 8 != 0)
                {
                    throw VecValue.Error("start index must be divisible by 8.");
                }

                if (e % 8 != 0)
                {
                    throw VecValue.Error("end index must be divisible by 8.");
                }

                Put(ref dst, ref own, v.Type, b[(s / 8)..(e / 8)]);
                break;
        }
    }

    private static void Distance(FuncId id, ReadOnlySpan<Mem> args, ref Mem dst)
    {
        VecValue a = ReadNth(args[0], "1st");
        VecValue b = ReadNth(args[1], "2nd");
        string name = id switch { FuncId.VecDistanceL2 => "L2", FuncId.VecDistanceL1 => "L1", FuncId.VecDistanceCosine => "cosine", _ => "hamming" };
        if (a.Type != b.Type)
        {
            throw VecValue.Error("Vector type mistmatch. First vector has type " + VecValue.Name(a.Type) + ", while the second has type " + VecValue.Name(b.Type) + ".");
        }

        if (id == FuncId.VecDistanceHamming)
        {
            if (a.Type != VecElement.Bit)
            {
                throw VecValue.Error("Cannot calculate hamming distance between two " + VecValue.Name(a.Type) + " vectors.");
            }
        }
        else if (a.Type == VecElement.Bit)
        {
            throw VecValue.Error("Cannot calculate " + name + " distance between two bitvectors.");
        }

        CheckSame(a, b);
        switch (id)
        {
            case FuncId.VecDistanceHamming:
                Values.SetReal(ref dst, VectorKernels.Hamming(a.Bytes.Span, b.Bytes.Span));
                break;
            case FuncId.VecDistanceL1 when a.Type == VecElement.Int8:
                Values.SetInt(ref dst, VectorKernels.L1(a.Int8s, b.Int8s));
                break;
            case FuncId.VecDistanceL1:
                Values.SetReal(ref dst, VectorKernels.L1(a.Floats, b.Floats));
                break;
            case FuncId.VecDistanceL2:
                Values.SetReal(ref dst, Math.Sqrt(a.Type == VecElement.Int8 ? VectorKernels.L2Squared(a.Int8s, b.Int8s) : VectorKernels.L2Squared(a.Floats, b.Floats)));
                break;
            default:
            {
                double d = Cosine(a, b);
                if (double.IsNaN(d))
                {
                    Values.SetNull(ref dst);
                }
                else
                {
                    Values.SetReal(ref dst, d);
                }

                break;
            }
        }
    }

    private static VecValue ReadNth(in Mem m, string nth)
    {
        try
        {
            return VecValue.Read(m);
        }
        catch (OneLiteException e)
        {
            throw VecValue.Error("Error reading " + nth + " vector: " + e.Message);
        }
    }

    /// <summary>Cosine distance <c>1 - a.b / (|a| |b|)</c>; NaN when either vector has length zero.</summary>
    internal static double Cosine(VecValue a, VecValue b)
    {
        double dot, na, nb;
        if (a.Type == VecElement.Int8)
        {
            dot = VectorKernels.Dot(a.Int8s, b.Int8s);
            na = VectorKernels.Dot(a.Int8s, a.Int8s);
            nb = VectorKernels.Dot(b.Int8s, b.Int8s);
        }
        else
        {
            dot = VectorKernels.Dot(a.Floats, b.Floats);
            na = VectorKernels.NormSquared(a.Floats);
            nb = VectorKernels.NormSquared(b.Floats);
        }

        if (na == 0 || nb == 0)
        {
            return double.NaN;
        }

        return 1.0 - (dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
    }

    private static void QuantizeBinary(in Mem arg, ref Mem dst, ref byte[]? own)
    {
        VecValue v = VecValue.Read(arg);
        if (v.Type == VecElement.Bit)
        {
            throw VecValue.Error("Can only binary quantize float or int8 vectors");
        }

        int n = v.Dimensions;
        if (n % 8 != 0)
        {
            throw VecValue.Error("Binary quantization requires vectors with a length divisible by 8");
        }

        var r = new byte[n / 8];
        for (int i = 0; i < n; i++)
        {
            bool positive = v.Type == VecElement.Float32 ? v.Floats[i] > 0 : v.Int8s[i] > 0;
            if (positive)
            {
                r[i / 8] |= (byte)(1 << (i % 8));
            }
        }

        Put(ref dst, ref own, VecElement.Bit, r);
    }

    private static void QuantizeInt8(ReadOnlySpan<Mem> args, ref Mem dst, ref byte[]? own)
    {
        VecValue v = VecValue.ReadFloat32(args[0]);
        if (args[1].Kind != StorageClass.Text || !args[1].Bytes.SequenceEqual("unit"u8))
        {
            throw VecValue.Error("2nd argument to vec_quantize_int8() must be 'unit'.");
        }

        // The unit range [-1, 1] is cut into 255 steps of float32 width; the value truncates toward zero and clamps to the int8 range.
        float step = 2.0f / 255.0f;
        ReadOnlySpan<float> x = v.Floats;
        var r = new byte[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            float q = ((x[i] - -1.0f) / step) - 128.0f;
            r[i] = (byte)(sbyte)(float.IsNaN(q) ? 0 : Math.Clamp(q, -128.0f, 127.0f));
        }

        Put(ref dst, ref own, VecElement.Int8, r);
    }
}
