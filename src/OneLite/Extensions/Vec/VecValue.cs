using System.Globalization;
using System.Text;
using RecursiveAI.OneLite.Internal.Simd;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Extensions.Vec;

/// <summary>The element type of a vector.</summary>
internal enum VecElement : byte
{
    /// <summary>32-bit IEEE floats, little-endian.</summary>
    Float32 = 1,

    /// <summary>Signed 8-bit integers.</summary>
    Int8 = 2,

    /// <summary>Bits, eight to a byte, the first element in the lowest bit.</summary>
    Bit = 3,
}

/// <summary>A vector value in its compact blob form: the bytes, the element type and the number of elements.</summary>
internal readonly struct VecValue
{
    /// <summary>The subtype tag a float32 vector result carries.</summary>
    internal const byte SubtypeFloat32 = 223;

    /// <summary>The subtype tag a bit vector result carries.</summary>
    internal const byte SubtypeBit = 224;

    /// <summary>The subtype tag an int8 vector result carries.</summary>
    internal const byte SubtypeInt8 = 225;

    internal VecValue(VecElement type, ReadOnlyMemory<byte> bytes)
    {
        Type = type;
        Bytes = bytes;
    }

    internal VecElement Type { get; }

    internal ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>The number of elements.</summary>
    internal int Dimensions => Type switch
    {
        VecElement.Float32 => Bytes.Length / 4,
        VecElement.Int8 => Bytes.Length,
        _ => Bytes.Length * 8,
    };

    internal ReadOnlySpan<float> Floats => VectorKernels.AsFloats(Bytes.Span);

    internal ReadOnlySpan<sbyte> Int8s => VectorKernels.AsInt8s(Bytes.Span);

    internal static byte SubtypeOf(VecElement t) => t switch { VecElement.Float32 => SubtypeFloat32, VecElement.Int8 => SubtypeInt8, _ => SubtypeBit };

    internal static string Name(VecElement t) => t switch { VecElement.Float32 => "float32", VecElement.Int8 => "int8", _ => "bit" };

    internal static OneLiteException Error(string message) => new(ResultCode.Error, message);

    /// <summary>
    /// Reads a vector argument: a blob (float32 unless its subtype says int8 or bit) or JSON text (float32). Throws with the
    /// reference extension's wording for anything else.
    /// </summary>
    internal static VecValue Read(in Mem m) => Read(m, true);

    /// <summary>Reads a vector as float32 whatever subtype a blob carries (what <c>vec_f32</c> and the float quantizer do).</summary>
    internal static VecValue ReadFloat32(in Mem m) => Read(m, false);

    private static VecValue Read(in Mem m, bool useSubtype)
    {
        switch (m.Kind)
        {
            case StorageClass.Blob:
            {
                if (m.Length == 0)
                {
                    throw Error("zero-length vectors are not supported.");
                }

                var bytes = new ReadOnlyMemory<byte>(m.Buffer, m.Offset, m.Length);
                switch (useSubtype ? m.Subtype : (byte)0)
                {
                    case SubtypeInt8:
                        return new VecValue(VecElement.Int8, bytes);
                    case SubtypeBit:
                        return new VecValue(VecElement.Bit, bytes);
                    default:
                        if (m.Length % 4 != 0)
                        {
                            throw Error("invalid float32 vector BLOB length. Must be divisible by 4, found " + m.Length.ToString(CultureInfo.InvariantCulture));
                        }

                        return new VecValue(VecElement.Float32, bytes);
                }
            }

            case StorageClass.Text:
                return new VecValue(VecElement.Float32, ParseJson(m.Bytes, false));
            default:
                throw Error("Input must have type BLOB (compact format) or TEXT (JSON), found "
                    + m.Kind switch { StorageClass.Null => "NULL", StorageClass.Integral => "INTEGER", _ => "FLOAT" });
        }
    }

    /// <summary>
    /// Reads a JSON array of numbers into compact bytes: float32 elements, or int8 elements when <paramref name="int8"/> is set (then only
    /// integers from -128 to 127 are accepted).
    /// </summary>
    internal static byte[] ParseJson(ReadOnlySpan<byte> json, bool int8)
    {
        int i = 0;
        SkipSpace(json, ref i);
        if (i >= json.Length && json.Length == 0)
        {
            throw Error("zero-length vectors are not supported.");
        }

        if (i >= json.Length || json[i] != (byte)'[')
        {
            throw Error("JSON array parsing error: Input does not start with '['");
        }

        i++;
        var floats = new List<float>();
        var bytes = new List<byte>();
        bool closed = false;
        SkipSpace(json, ref i);
        if (i < json.Length && json[i] == (byte)']')
        {
            closed = true;
        }

        while (!closed)
        {
            SkipSpace(json, ref i);
            int start = i;
            while (i < json.Length && json[i] is (byte)'+' or (byte)'-' or (byte)'.' or (byte)'e' or (byte)'E' or (>= (byte)'0' and <= (byte)'9'))
            {
                i++;
            }

            ReadOnlySpan<byte> tok = json[start..i];
            if (tok.Length == 0)
            {
                throw Error("JSON parsing error");
            }

            if (int8)
            {
                if (!long.TryParse(tok, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v))
                {
                    throw Error("JSON parsing error");
                }

                if (v < -128 || v > 127)
                {
                    throw Error("JSON parsing error: value out of range for int8");
                }

                bytes.Add((byte)(sbyte)v);
            }
            else
            {
                if (!double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || double.IsInfinity(d))
                {
                    throw Error("JSON parsing error");
                }

                floats.Add((float)d);
            }

            SkipSpace(json, ref i);
            if (i >= json.Length)
            {
                throw Error("JSON parsing error");
            }

            if (json[i] == (byte)',')
            {
                i++;
            }
            else if (json[i] == (byte)']')
            {
                closed = true;
            }
            else
            {
                throw Error("JSON parsing error");
            }
        }

        if (int8)
        {
            if (bytes.Count == 0)
            {
                throw Error("zero-length vectors are not supported.");
            }

            return [.. bytes];
        }

        if (floats.Count == 0)
        {
            throw Error("zero-length vectors are not supported.");
        }

        var result = new byte[floats.Count * 4];
        for (int k = 0; k < floats.Count; k++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(k * 4), floats[k]);
        }

        return result;
    }

    private static void SkipSpace(ReadOnlySpan<byte> s, ref int i)
    {
        while (i < s.Length && s[i] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r')
        {
            i++;
        }
    }

    /// <summary>The encoding of a floating-point text for JSON output: C's <c>%f</c>, with <c>null</c> for NaN.</summary>
    internal static string FormatFloat(float f)
    {
        if (float.IsNaN(f))
        {
            return "null";
        }

        if (float.IsPositiveInfinity(f))
        {
            return "Inf";
        }

        if (float.IsNegativeInfinity(f))
        {
            return "-Inf";
        }

        var args = new Mem[2];
        args[0] = Mem.FromTextBytes("%f"u8.ToArray());
        args[1] = Mem.FromReal(f);
        Mem dst = default;
        byte[]? own = null;
        Vm.Printf.Format(args, ref dst, ref own);
        return Encoding.UTF8.GetString(dst.Bytes);
    }
}
