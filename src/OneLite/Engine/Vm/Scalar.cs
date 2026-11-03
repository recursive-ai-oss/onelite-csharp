using System.Linq;
using RecursiveAI.OneLite.Storage;
using System.Text;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Vm;

/// <summary>The scalar functions the engine implements.</summary>
internal enum FuncId : byte
{
    Length,
    Lower,
    Upper,
    Typeof,
    Coalesce,
    Ifnull,
    Substr,
    Abs,
    Hex,
    Quote,

    // lang_corefunc
    Char,
    Concat,
    ConcatWs,
    Format,
    Glob,
    Iif,
    Instr,
    Changes,
    LastInsertRowid,
    TotalChanges,
    Likelihood,
    Likely,
    Unlikely,
    Like,
    LoadExtension,
    Ltrim,
    Rtrim,
    Trim,
    MaxN,
    MinN,
    Nullif,
    OctetLength,
    Random,
    Randomblob,
    Replace,
    Round,
    Sign,
    Soundex,
    CompileOptionGet,
    CompileOptionUsed,
    SourceId,
    Version,
    Unhex,
    Unicode,
    Unistr,
    UnistrQuote,
    Zeroblob,

    // lang_mathfunc
    Acos,
    Acosh,
    Asin,
    Asinh,
    Atan,
    Atan2,
    Atanh,
    Ceil,
    Cos,
    Cosh,
    Degrees,
    Exp,
    Floor,
    Ln,
    Log,
    Log10,
    Log2,
    Mod,
    Pi,
    Pow,
    Radians,
    Sin,
    Sinh,
    Sqrt,
    Tan,
    Tanh,
    Trunc,

    // lang_datefunc
    Date,
    Time,
    DateTime,
    JulianDay,
    UnixEpoch,
    Strftime,
    TimeDiff,

    // json1
    Json,
    Jsonb,
    JsonArray,
    JsonbArray,
    JsonArrayInsert,
    JsonbArrayInsert,
    JsonArrayLength,
    JsonErrorPosition,
    JsonExtract,
    JsonbExtract,
    JsonArrow,
    JsonArrow2,
    JsonInsert,
    JsonbInsert,
    JsonObject,
    JsonbObject,
    JsonPatch,
    JsonbPatch,
    JsonPretty,
    JsonQuote,
    JsonRemove,
    JsonbRemove,
    JsonReplace,
    JsonbReplace,
    JsonSet,
    JsonbSet,
    JsonType,
    JsonValid,
    RtreeCheck,
    RtreeDepth,
    RtreeNode,
    VecF32,
    VecInt8,
    VecBit,
    VecLength,
    VecType,
    VecToJson,
    VecAdd,
    VecSub,
    VecNormalize,
    VecSlice,
    VecDistanceL2,
    VecDistanceL1,
    VecDistanceCosine,
    VecDistanceHamming,
    VecQuantizeBinary,
    VecQuantizeInt8,
    VecVersion,
    VecDebug,
    Subtype,
    Fts5Bm25,
    Fts5Highlight,
    Fts5Snippet,
    Fts5SourceId,
    Fts3Offsets,
    Fts3Matchinfo,
    Fts3Optimize,
    MatchContext,
}

/// <summary>A built-in function: its name, accepted argument counts and implementation id.</summary>
internal sealed class FuncDef
{
    internal FuncDef(string name, int min, int max, FuncId id)
    {
        Name = name;
        MinArgs = min;
        MaxArgs = max;
        Id = id;
    }

    internal string Name { get; }

    internal int MinArgs { get; }

    internal int MaxArgs { get; }

    internal FuncId Id { get; }

    /// <summary>The implementation of a function registered through Connection.CreateFunction; null for a built-in.</summary>
    internal UserScalarFunction? User { get; init; }

    /// <summary>For a user function: registered as deterministic.</summary>
    internal bool Deterministic { get; init; }
}

/// <summary>The function table (lang_corefunc). A static table: no reflection, nothing registered at run time.</summary>
internal static class FunctionTable
{
    /// <summary>The names of the scalar functions, for "did you mean" suggestions.</summary>
    internal static IEnumerable<string> Names() => s_defs.Select(d => d.Name);

    private static readonly FuncDef[] s_defs =
    [
        new("length", 1, 1, FuncId.Length),
        new("lower", 1, 1, FuncId.Lower),
        new("upper", 1, 1, FuncId.Upper),
        new("typeof", 1, 1, FuncId.Typeof),
        new("coalesce", 2, 1000, FuncId.Coalesce),
        new("ifnull", 2, 2, FuncId.Ifnull),
        new("substr", 2, 3, FuncId.Substr),
        new("substring", 2, 3, FuncId.Substr),
        new("abs", 1, 1, FuncId.Abs),
        new("hex", 1, 1, FuncId.Hex),
        new("quote", 1, 1, FuncId.Quote),
        new("char", 0, 1000, FuncId.Char),
        new("concat", 1, 1000, FuncId.Concat),
        new("concat_ws", 2, 1000, FuncId.ConcatWs),
        new("format", 0, 1000, FuncId.Format),
        new("printf", 0, 1000, FuncId.Format),
        new("glob", 2, 2, FuncId.Glob),
        new("iif", 2, 1000, FuncId.Iif),
        new("if", 2, 1000, FuncId.Iif),
        new("instr", 2, 2, FuncId.Instr),
        new("changes", 0, 0, FuncId.Changes),
        new("last_insert_rowid", 0, 0, FuncId.LastInsertRowid),
        new("total_changes", 0, 0, FuncId.TotalChanges),
        new("likelihood", 2, 2, FuncId.Likelihood),
        new("likely", 1, 1, FuncId.Likely),
        new("unlikely", 1, 1, FuncId.Unlikely),
        new("like", 2, 3, FuncId.Like),
        new("load_extension", 1, 2, FuncId.LoadExtension),
        new("ltrim", 1, 2, FuncId.Ltrim),
        new("rtrim", 1, 2, FuncId.Rtrim),
        new("trim", 1, 2, FuncId.Trim),
        new("max", 2, 1000, FuncId.MaxN),
        new("min", 2, 1000, FuncId.MinN),
        new("nullif", 2, 2, FuncId.Nullif),
        new("octet_length", 1, 1, FuncId.OctetLength),
        new("random", 0, 0, FuncId.Random),
        new("randomblob", 1, 1, FuncId.Randomblob),
        new("replace", 3, 3, FuncId.Replace),
        new("round", 1, 2, FuncId.Round),
        new("sign", 1, 1, FuncId.Sign),
        new("sqlite_compileoption_get", 1, 1, FuncId.CompileOptionGet),
        new("sqlite_compileoption_used", 1, 1, FuncId.CompileOptionUsed),
        new("sqlite_source_id", 0, 0, FuncId.SourceId),
        new("sqlite_version", 0, 0, FuncId.Version),
        new("rtreecheck", 1, 2, FuncId.RtreeCheck),
        new("rtreedepth", 1, 1, FuncId.RtreeDepth),
        new("rtreenode", 2, 2, FuncId.RtreeNode),
        new("vec_f32", 1, 1, FuncId.VecF32),
        new("vec_int8", 1, 1, FuncId.VecInt8),
        new("vec_bit", 1, 1, FuncId.VecBit),
        new("vec_length", 1, 1, FuncId.VecLength),
        new("vec_type", 1, 1, FuncId.VecType),
        new("vec_to_json", 1, 1, FuncId.VecToJson),
        new("vec_add", 2, 2, FuncId.VecAdd),
        new("vec_sub", 2, 2, FuncId.VecSub),
        new("vec_normalize", 1, 1, FuncId.VecNormalize),
        new("vec_slice", 3, 3, FuncId.VecSlice),
        new("vec_distance_l2", 2, 2, FuncId.VecDistanceL2),
        new("vec_distance_l1", 2, 2, FuncId.VecDistanceL1),
        new("vec_distance_cosine", 2, 2, FuncId.VecDistanceCosine),
        new("vec_distance_hamming", 2, 2, FuncId.VecDistanceHamming),
        new("vec_quantize_binary", 1, 1, FuncId.VecQuantizeBinary),
        new("vec_quantize_int8", 2, 2, FuncId.VecQuantizeInt8),
        new("vec_version", 0, 0, FuncId.VecVersion),
        new("vec_debug", 0, 0, FuncId.VecDebug),
        new("subtype", 1, 1, FuncId.Subtype),
        new("match", 2, 2, FuncId.MatchContext),
        new("bm25", 1, 1000, FuncId.Fts5Bm25),
        new("highlight", 4, 4, FuncId.Fts5Highlight),
        new("snippet", 1, 6, FuncId.Fts5Snippet),
        new("offsets", 1, 1, FuncId.Fts3Offsets),
        new("matchinfo", 1, 2, FuncId.Fts3Matchinfo),
        new("optimize", 1, 1, FuncId.Fts3Optimize),
        new("fts5_source_id", 0, 0, FuncId.Fts5SourceId),
        new("unhex", 1, 2, FuncId.Unhex),
        new("unicode", 1, 1, FuncId.Unicode),
        new("unistr", 1, 1, FuncId.Unistr),
        new("unistr_quote", 1, 1, FuncId.UnistrQuote),
        new("zeroblob", 1, 1, FuncId.Zeroblob),
        new("acos", 1, 1, FuncId.Acos),
        new("acosh", 1, 1, FuncId.Acosh),
        new("asin", 1, 1, FuncId.Asin),
        new("asinh", 1, 1, FuncId.Asinh),
        new("atan", 1, 1, FuncId.Atan),
        new("atan2", 2, 2, FuncId.Atan2),
        new("atanh", 1, 1, FuncId.Atanh),
        new("ceil", 1, 1, FuncId.Ceil),
        new("ceiling", 1, 1, FuncId.Ceil),
        new("cos", 1, 1, FuncId.Cos),
        new("cosh", 1, 1, FuncId.Cosh),
        new("degrees", 1, 1, FuncId.Degrees),
        new("exp", 1, 1, FuncId.Exp),
        new("floor", 1, 1, FuncId.Floor),
        new("ln", 1, 1, FuncId.Ln),
        new("log", 1, 2, FuncId.Log),
        new("log10", 1, 1, FuncId.Log10),
        new("log2", 1, 1, FuncId.Log2),
        new("mod", 2, 2, FuncId.Mod),
        new("pi", 0, 0, FuncId.Pi),
        new("pow", 2, 2, FuncId.Pow),
        new("power", 2, 2, FuncId.Pow),
        new("radians", 1, 1, FuncId.Radians),
        new("sin", 1, 1, FuncId.Sin),
        new("sinh", 1, 1, FuncId.Sinh),
        new("sqrt", 1, 1, FuncId.Sqrt),
        new("tan", 1, 1, FuncId.Tan),
        new("tanh", 1, 1, FuncId.Tanh),
        new("trunc", 1, 1, FuncId.Trunc),
        new("date", 0, 1000, FuncId.Date),
        new("time", 0, 1000, FuncId.Time),
        new("datetime", 0, 1000, FuncId.DateTime),
        new("julianday", 0, 1000, FuncId.JulianDay),
        new("unixepoch", 0, 1000, FuncId.UnixEpoch),
        new("strftime", 0, 1000, FuncId.Strftime),
        new("timediff", 2, 2, FuncId.TimeDiff),
        new("json", 1, 1, FuncId.Json),
        new("jsonb", 1, 1, FuncId.Jsonb),
        new("json_array", 0, 1000, FuncId.JsonArray),
        new("jsonb_array", 0, 1000, FuncId.JsonbArray),
        new("json_array_insert", 0, 1000, FuncId.JsonArrayInsert),
        new("jsonb_array_insert", 0, 1000, FuncId.JsonbArrayInsert),
        new("json_array_length", 1, 2, FuncId.JsonArrayLength),
        new("json_error_position", 1, 1, FuncId.JsonErrorPosition),
        new("json_extract", 0, 1000, FuncId.JsonExtract),
        new("jsonb_extract", 0, 1000, FuncId.JsonbExtract),
        new("->", 2, 2, FuncId.JsonArrow),
        new("->>", 2, 2, FuncId.JsonArrow2),
        new("json_insert", 0, 1000, FuncId.JsonInsert),
        new("jsonb_insert", 0, 1000, FuncId.JsonbInsert),
        new("json_object", 0, 1000, FuncId.JsonObject),
        new("jsonb_object", 0, 1000, FuncId.JsonbObject),
        new("json_patch", 2, 2, FuncId.JsonPatch),
        new("jsonb_patch", 2, 2, FuncId.JsonbPatch),
        new("json_pretty", 1, 2, FuncId.JsonPretty),
        new("json_quote", 1, 1, FuncId.JsonQuote),
        new("json_remove", 0, 1000, FuncId.JsonRemove),
        new("jsonb_remove", 0, 1000, FuncId.JsonbRemove),
        new("json_replace", 0, 1000, FuncId.JsonReplace),
        new("jsonb_replace", 0, 1000, FuncId.JsonbReplace),
        new("json_set", 0, 1000, FuncId.JsonSet),
        new("jsonb_set", 0, 1000, FuncId.JsonbSet),
        new("json_type", 1, 2, FuncId.JsonType),
        new("json_valid", 1, 2, FuncId.JsonValid),
    ];

    // Functions the reference library has (lang_corefunc, lang_aggfunc, lang_datefunc, json1, math) that this slice leaves out. They fail with
    // an "unsupported" error instead of "no such function", so a missing feature is never mistaken for a typo.
    private static readonly HashSet<string> s_known = new(StringComparer.OrdinalIgnoreCase)
    {
        "sqlite_offset", "avg", "count", "group_concat", "string_agg", "sum", "total", "median", "mode", "percentile", "percentile_cont",
        "percentile_disc", "row_number", "rank", "dense_rank", "percent_rank", "cume_dist", "ntile", "lag", "lead", "first_value",
        "last_value", "nth_value", "match", "highlight", "snippet", "bm25", "rtreecheck",
    };

    internal static bool IsKnownUnsupported(string name)
        => s_known.Contains(name) || name.StartsWith("json", StringComparison.OrdinalIgnoreCase) || name.StartsWith("geopoly", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("fts5", StringComparison.OrdinalIgnoreCase) || name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Finds a function by name (ASCII case-insensitive).</summary>
    internal static FuncDef? Find(string name)
    {
        foreach (FuncDef d in s_defs)
        {
            if (string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return d;
            }
        }

        return null;
    }
}

/// <summary>Implementations of the scalar functions over register values.</summary>
internal static partial class Scalar
{
    private static readonly byte[] s_typeNull = "null"u8.ToArray();
    private static readonly byte[] s_typeInteger = "integer"u8.ToArray();
    private static readonly byte[] s_typeReal = "real"u8.ToArray();
    private static readonly byte[] s_typeText = "text"u8.ToArray();
    private static readonly byte[] s_typeBlob = "blob"u8.ToArray();

    /// <summary>Number of characters in UTF-8 text up to the first NUL (lang_corefunc length).</summary>
    internal static long CharLength(ReadOnlySpan<byte> s)
    {
        long len = 0;
        int i = 0;
        while (i < s.Length && s[i] != 0)
        {
            byte c = s[i++];
            if (c >= 0xC0)
            {
                while (i < s.Length && (s[i] & 0xC0) == 0x80)
                {
                    i++;
                }
            }

            len++;
        }

        return len;
    }

    /// <summary>Byte offset of the start of the <paramref name="chars"/>-th character from <paramref name="from"/>.</summary>
    private static int SkipChars(ReadOnlySpan<byte> s, int from, long chars)
    {
        int i = from;
        while (chars > 0 && i < s.Length)
        {
            byte c = s[i++];
            if (c >= 0xC0)
            {
                while (i < s.Length && (s[i] & 0xC0) == 0x80)
                {
                    i++;
                }
            }

            chars--;
        }

        return i;
    }

    /// <summary>Evaluates a function other than coalesce/ifnull (which the compiler turns into jumps). Arguments are <paramref name="args"/>[0..n).</summary>
    internal static void Call(FuncId id, ReadOnlySpan<Mem> args, ref Mem dst, ref byte[]? own, TextEncoding dbEnc, int flags = 0)
    {
        if (id >= FuncId.Char)
        {
            CallExtra(id, args, ref dst, ref own, dbEnc, flags);
            return;
        }

        Span<byte> tmp = stackalloc byte[Values.MaxRealText];
        switch (id)
        {
            case FuncId.Typeof:
                Values.SetRef(ref dst, StorageClass.Text, args[0].Kind switch
                {
                    StorageClass.Null => s_typeNull,
                    StorageClass.Integral => s_typeInteger,
                    StorageClass.Real => s_typeReal,
                    StorageClass.Text => s_typeText,
                    _ => s_typeBlob,
                });
                return;
            case FuncId.Length:
            {
                ref readonly Mem a = ref args[0];
                switch (a.Kind)
                {
                    case StorageClass.Null:
                        Values.SetNull(ref dst);
                        return;
                    case StorageClass.Blob:
                        Values.SetInt(ref dst, a.Length);
                        return;
                    default:
                    {
                        // length() of text counts characters up to the first NUL (lang_corefunc).
                        ReadOnlySpan<byte> lt = Values.AsText(a, tmp);
                        int nul = lt.IndexOf((byte)0);
                        Values.SetInt(ref dst, CharLength(nul >= 0 ? lt[..nul] : lt));
                        return;
                    }
                }
            }

            case FuncId.Lower:
            case FuncId.Upper:
            {
                ref readonly Mem a = ref args[0];
                if (a.Kind == StorageClass.Null)
                {
                    Values.SetNull(ref dst);
                    return;
                }

                ReadOnlySpan<byte> t = Values.AsText(a, tmp);
                // Copy first, then fold in place: the source may be the register's own buffer.
                Values.SetBytes(ref dst, ref own, StorageClass.Text, t);
                Span<byte> w = dst.Buffer.AsSpan(0, dst.Length);
                for (int i = 0; i < w.Length; i++)
                {
                    byte c = w[i];
                    if (id == FuncId.Lower)
                    {
                        if (c is >= (byte)'A' and <= (byte)'Z')
                        {
                            w[i] = (byte)(c + 32);
                        }
                    }
                    else if (c is >= (byte)'a' and <= (byte)'z')
                    {
                        w[i] = (byte)(c - 32);
                    }
                }

                return;
            }

            case FuncId.Abs:
            {
                ref readonly Mem a = ref args[0];
                switch (a.Kind)
                {
                    case StorageClass.Null:
                        Values.SetNull(ref dst);
                        return;
                    case StorageClass.Integral:
                        if (a.IntValue == long.MinValue)
                        {
                            throw new OneLiteException(ResultCode.Error, "integer overflow");
                        }

                        Values.SetInt(ref dst, Math.Abs(a.IntValue));
                        return;
                    case StorageClass.Real:
                        Values.SetReal(ref dst, a.RealValue < 0 ? -a.RealValue : a.RealValue); // abs(-0.0) stays negative zero
                        return;
                    default:
                        {
                            double av = Values.ToReal(a);
                            Values.SetReal(ref dst, av < 0 ? -av : av);
                            return;
                        }
                }
            }

            case FuncId.Hex:
            {
                ref readonly Mem a = ref args[0];
                if (a.Kind == StorageClass.Null)
                {
                    Values.SetBytes(ref dst, ref own, StorageClass.Text, default);
                }
                else if (a.Kind == StorageClass.Text && dbEnc != TextEncoding.Utf8)
                {
                    // The value's bytes are those of the database encoding (lang_corefunc hex reads the BLOB form).
                    HexInto(TextCodec.Encode(Encoding.UTF8.GetString(a.Bytes), dbEnc), ref dst, ref own, null);
                }
                else if (a.Kind == StorageClass.Blob)
                {
                    HexInto(a.Bytes, ref dst, ref own, a.Buffer);
                }
                else
                {
                    HexInto(Values.AsText(a, tmp), ref dst, ref own, a.Buffer);
                }

                return;
            }

            case FuncId.Quote:
                Quote(args[0], ref dst, ref own, dbEnc);
                return;
            case FuncId.Substr:
                Substr(args, ref dst, ref own);
                return;
            default:
                throw new InvalidOperationException("function is compiled to jumps");
        }
    }

    /// <summary>Writes the upper-case hex of <paramref name="src"/> into the result's own buffer (a new one only if it is too small or is the source).</summary>
    private static void HexInto(ReadOnlySpan<byte> src, ref Mem dst, ref byte[]? own, byte[]? srcBuffer)
    {
        int n = src.Length * 2;
        if (own == null || own.Length < n || ReferenceEquals(own, srcBuffer))
        {
            own = new byte[Math.Max(n, 32)];
        }

        const string Digits = "0123456789ABCDEF";
        for (int i = 0; i < src.Length; i++)
        {
            own[2 * i] = (byte)Digits[src[i] >> 4];
            own[(2 * i) + 1] = (byte)Digits[src[i] & 15];
        }

        dst.Kind = StorageClass.Text;

        dst.Subtype = 0;
        dst.Buffer = own;
        dst.Offset = 0;
        dst.Length = n;
    }

    private static void Quote(in Mem a, ref Mem dst, ref byte[]? own, TextEncoding dbEnc)
    {
        Span<byte> tmp = stackalloc byte[Values.MaxRealText];
        switch (a.Kind)
        {
            case StorageClass.Null:
                Values.SetBytes(ref dst, ref own, StorageClass.Text, "NULL"u8);
                return;
            case StorageClass.Real when double.IsInfinity(a.RealValue):
                // lang_corefunc quote(): infinities are written as out-of-range literals so they read back as infinities.
                Values.SetBytes(ref dst, ref own, StorageClass.Text, a.RealValue > 0 ? "9.0e+999"u8 : "-9.0e+999"u8);
                return;
            case StorageClass.Integral:
            case StorageClass.Real:
                Values.SetBytes(ref dst, ref own, StorageClass.Text, Values.AsText(a, tmp));
                return;
            case StorageClass.Blob:
            {
                ReadOnlySpan<byte> b = a.Bytes;
                byte[] o = new byte[(b.Length * 2) + 3];
                o[0] = (byte)'X';
                o[1] = (byte)'\'';
                const string Digits = "0123456789ABCDEF";
                for (int i = 0; i < b.Length; i++)
                {
                    o[2 + (2 * i)] = (byte)Digits[b[i] >> 4];
                    o[3 + (2 * i)] = (byte)Digits[b[i] & 15];
                }

                o[^1] = (byte)'\'';
                Values.SetBytes(ref dst, ref own, StorageClass.Text, o);
                return;
            }

            default:
            {
                ReadOnlySpan<byte> t = a.Bytes;
                int nul = t.IndexOf((byte)0);
                if (nul >= 0)
                {
                    t = t[..nul]; // quote() reads text as a C string and stops at an embedded NUL
                }

                int quotes = 0;
                foreach (byte c in t)
                {
                    if (c == '\'')
                    {
                        quotes++;
                    }
                }

                byte[] o = new byte[t.Length + quotes + 2];
                int p = 0;
                o[p++] = (byte)'\'';
                foreach (byte c in t)
                {
                    o[p++] = c;
                    if (c == '\'')
                    {
                        o[p++] = c;
                    }
                }

                o[p] = (byte)'\'';
                Values.SetBytes(ref dst, ref own, StorageClass.Text, o);
                return;
            }
        }
    }

    private static void Substr(ReadOnlySpan<Mem> args, ref Mem dst, ref byte[]? own)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Kind == StorageClass.Null)
            {
                Values.SetNull(ref dst);
                return;
            }
        }

        ref readonly Mem x = ref args[0];
        bool blob = x.Kind == StorageClass.Blob;
        Span<byte> tmp = stackalloc byte[Values.MaxRealText];
        ReadOnlySpan<byte> s = blob ? x.Bytes : Values.AsText(x, tmp);
        if (blob && s.Length == 0)
        {
            // Black-box: substr of a zero-length blob is NULL (a zero-length text stays text).
            Values.SetNull(ref dst);
            return;
        }

        long y = Values.ToInt(args[1]);
        bool hasZ = args.Length > 2;
        long z = hasZ ? Values.ToInt(args[2]) : 0;
        long len = blob ? s.Length : CharLength(s);

        // lang_corefunc substr: positions count from 1, a negative Y counts from the right, Y = 0 is the position before the first
        // character, and a negative Z takes the characters that precede position Y.
        Int128 start = y < 0 ? (Int128)len + y + 1 : y;
        Int128 first, last;
        if (!hasZ)
        {
            // Without Y the length defaults to the largest string the reference allows, so a hugely negative start gives "".
            first = start;
            last = start + 1_000_000_000 - 1;
        }
        else if (z >= 0)
        {
            first = start;
            last = start + z - 1;
        }
        else
        {
            first = start + z;
            last = start - 1;
        }

        if (first < 1)
        {
            first = 1;
        }

        if (last > len)
        {
            last = len;
        }

        if (last < first)
        {
            Values.SetBytes(ref dst, ref own, blob ? StorageClass.Blob : StorageClass.Text, default);
            return;
        }

        int from, to;
        if (blob)
        {
            from = (int)(first - 1);
            to = (int)last;
        }
        else
        {
            from = SkipChars(s, 0, (long)(first - 1));
            to = SkipChars(s, from, (long)(last - first + 1));
        }

        Values.SetBytes(ref dst, ref own, blob ? StorageClass.Blob : StorageClass.Text, s[from..to]);
    }

    // ------------------------------------------------------------------ LIKE and GLOB

    /// <summary>The longest LIKE/GLOB pattern the pinned build accepts (compile option MAX_LIKE_PATTERN_LENGTH).</summary>
    internal const int MaxPattern = 50000;

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

    private static int FoldAscii(int c) => c is >= 'A' and <= 'Z' ? c + 32 : c;

    private static bool SameChar(ReadOnlySpan<byte> a, int i, ReadOnlySpan<byte> b, int j, bool fold)
    {
        int la = CharLen(a, i), lb = CharLen(b, j);
        if (la != lb)
        {
            return false;
        }

        if (la == 1)
        {
            return fold ? FoldAscii(a[i]) == FoldAscii(b[j]) : a[i] == b[j];
        }

        return a.Slice(i, la).SequenceEqual(b.Slice(j, lb));
    }

    /// <summary>X LIKE Y ESCAPE E (lang_expr "The LIKE, GLOB, REGEXP, MATCH, and extended operators"): % any run, _ any one character, ASCII case-insensitive.</summary>
    internal static bool Like(ReadOnlySpan<byte> pattern, ReadOnlySpan<byte> text, int escape)
    {
        // Iterative match with a single backtrack point (the last %).
        int p = 0, t = 0, starP = -1, starT = -1;
        while (t < text.Length)
        {
            if (p < pattern.Length)
            {
                byte pc = pattern[p];
                if (pc == (byte)'%')
                {
                    while (p < pattern.Length && pattern[p] == (byte)'%')
                    {
                        p++;
                    }

                    if (p == pattern.Length)
                    {
                        return true;
                    }

                    starP = p;
                    starT = t;
                    continue;
                }

                if (escape >= 0 && pc == escape && p + 1 < pattern.Length)
                {
                    int ep = p + 1;
                    if (SameChar(pattern, ep, text, t, true))
                    {
                        p = ep + CharLen(pattern, ep);
                        t += CharLen(text, t);
                        continue;
                    }
                }
                else if (pc == (byte)'_')
                {
                    p++;
                    t += CharLen(text, t);
                    continue;
                }
                else if (SameChar(pattern, p, text, t, true))
                {
                    p += CharLen(pattern, p);
                    t += CharLen(text, t);
                    continue;
                }
            }

            if (starP < 0)
            {
                return false;
            }

            starT += CharLen(text, starT);
            t = starT;
            p = starP;
        }

        while (p < pattern.Length && pattern[p] == (byte)'%')
        {
            p++;
        }

        return p == pattern.Length;
    }

    /// <summary>X GLOB Y: * any run, ? any one character, [set] with ranges and ^ negation; case-sensitive.</summary>
    internal static bool Glob(ReadOnlySpan<byte> pattern, ReadOnlySpan<byte> text)
    {
        int p = 0, t = 0, starP = -1, starT = -1;
        while (t < text.Length)
        {
            if (p < pattern.Length)
            {
                byte pc = pattern[p];
                if (pc == (byte)'*')
                {
                    while (p < pattern.Length && pattern[p] == (byte)'*')
                    {
                        p++;
                    }

                    if (p == pattern.Length)
                    {
                        return true;
                    }

                    starP = p;
                    starT = t;
                    continue;
                }

                if (pc == (byte)'?')
                {
                    p++;
                    t += CharLen(text, t);
                    continue;
                }

                if (pc == (byte)'[')
                {
                    int np = MatchSet(pattern, p, text, t, out bool ok);
                    if (np >= 0)
                    {
                        if (ok)
                        {
                            p = np;
                            t += CharLen(text, t);
                            continue;
                        }
                    }
                    else
                    {
                        return false; // an unterminated set matches nothing
                    }
                }
                else if (SameChar(pattern, p, text, t, false))
                {
                    p += CharLen(pattern, p);
                    t += CharLen(text, t);
                    continue;
                }
            }

            if (starP < 0)
            {
                return false;
            }

            starT += CharLen(text, starT);
            t = starT;
            p = starP;
        }

        while (p < pattern.Length && pattern[p] == (byte)'*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    private static int Decode(ReadOnlySpan<byte> s, int i, out int len)
    {
        len = CharLen(s, i);
        if (len == 1)
        {
            return s[i];
        }

        int c = s[i] & (0xFF >> (len + 1));
        for (int k = 1; k < len; k++)
        {
            c = (c << 6) | (s[i + k] & 0x3F);
        }

        return c;
    }

    /// <summary>Matches the set starting at <paramref name="p"/> ('[') against the character at <paramref name="t"/>. Returns the index after ']' or -1 if unterminated.</summary>
    private static int MatchSet(ReadOnlySpan<byte> pat, int p, ReadOnlySpan<byte> text, int t, out bool matched)
    {
        int q = p + 1;
        bool negate = false;
        if (q < pat.Length && pat[q] == (byte)'^')
        {
            negate = true;
            q++;
        }

        int tc = Decode(text, t, out _);
        bool found = false;
        bool first = true;
        while (q < pat.Length && (pat[q] != (byte)']' || first))
        {
            first = false;
            int lo = Decode(pat, q, out int l1);
            q += l1;
            if (q + 1 < pat.Length && pat[q] == (byte)'-' && pat[q + 1] != (byte)']')
            {
                int hi = Decode(pat, q + 1, out int l2);
                q += 1 + l2;
                if (tc >= lo && tc <= hi)
                {
                    found = true;
                }
            }
            else if (tc == lo)
            {
                found = true;
            }
        }

        if (q >= pat.Length)
        {
            matched = false;
            return -1;
        }

        matched = found != negate;
        return q + 1;
    }
}
