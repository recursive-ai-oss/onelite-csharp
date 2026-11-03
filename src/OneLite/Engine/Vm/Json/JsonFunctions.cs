using System.Text;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Vm;

/// <summary>The JSON scalar functions of json1: they all work on JSONB and render text only for results.</summary>
internal static class JsonFunctions
{
    /// <summary>The subtype json functions mark their results with, so other JSON functions take them as JSON and not as strings.</summary>
    internal const byte JsonSubtype = 74;

    private static OneLiteException Err(string m) => new(ResultCode.Error, m);

    // ---------------------------------------------------------------- argument readers

    /// <summary>Reads a JSON argument as JSONB. False for NULL. A BLOB that is not JSONB is read as text (json1 §3.8).</summary>
    internal static bool Doc(in Mem a, JsonBuf buf, out ReadOnlySpan<byte> doc) => Doc(a, buf, out doc, out _);

    /// <summary>As <see cref="Doc(in Mem, JsonBuf, out ReadOnlySpan{byte})"/>, and says whether the text used anything beyond canonical JSON.</summary>
    internal static bool Doc(in Mem a, JsonBuf buf, out ReadOnlySpan<byte> doc, out bool json5)
    {
        json5 = false;
        switch (a.Kind)
        {
            case StorageClass.Null:
                doc = default;
                return false;
            case StorageClass.Blob:
            {
                ReadOnlySpan<byte> bl = a.Bytes;
                if (Jsonb.LooksValid(bl))
                {
                    doc = bl;
                    return true;
                }

                json5 = ParseText(bl, buf);
                doc = buf.Span;
                return true;
            }

            case StorageClass.Text:
            {
                ReadOnlySpan<byte> txt = a.Bytes;
                if (JsonCache.TryGet(txt, a.Subtype == JsonSubtype, out byte[]? cached, out json5))
                {
                    doc = cached;
                    return true;
                }

                json5 = ParseText(txt, buf);
                doc = buf.Span;
                return true;
            }
            default:
            {
                Span<byte> tmp = stackalloc byte[Values.MaxRealText];
                json5 = ParseText(Values.AsText(a, tmp), buf);
                doc = buf.Span;
                return true;
            }
        }
    }

    private static bool ParseText(ReadOnlySpan<byte> text, JsonBuf buf)
    {
        int nul = text.IndexOf((byte)0);
        if (nul >= 0)
        {
            text = text[..nul];
        }

        buf.Clear();
        var p = new JsonTextParser(text, buf);
        if (!p.ParseDocument())
        {
            throw JsonRender.Malformed();
        }

        return p.Json5;
    }

    /// <summary>A path (or any text) argument: text as it is, a number as its text form.</summary>
    private static ReadOnlySpan<byte> PathArg(in Mem a)
    {
        if (a.Kind == StorageClass.Text)
        {
            return a.Bytes;
        }

        Span<byte> tmp = stackalloc byte[Values.MaxRealText];
        JsonBuf b = JsonScratch.Get(7);
        b.Put(Values.AsText(a, tmp));
        return b.Span;
    }

    /// <summary>Appends the element for an SQL value used as a JSON value (json_array, json_object, json_set, ...).</summary>
    internal static void PutValue(in Mem v, JsonBuf o, bool rawText = false)
    {
        switch (v.Kind)
        {
            case StorageClass.Null:
                o.Put(JT.Null);
                break;
            case StorageClass.Integral:
            {
                Span<byte> t = stackalloc byte[24];
                int n = Values.FormatInt(v.IntValue, t);
                Jsonb.PutElem(o, JT.Int, t[..n]);
                break;
            }

            case StorageClass.Real:
                PutReal(v.RealValue, o, rawText);
                break;
            case StorageClass.Text:
            {
                ReadOnlySpan<byte> s = v.Bytes;
                if (v.Subtype == JsonSubtype)
                {
                    var p = new JsonTextParser(s, o);
                    if (!p.ParseDocument())
                    {
                        throw JsonRender.Malformed();
                    }

                    break;
                }

                if (rawText)
                {
                    // json_set and its relatives store an SQL string as TEXTRAW.
                    Jsonb.PutElem(o, JT.TextRaw, s);
                }
                else
                {
                    PutString(o, s);
                }

                break;
            }

            default:
                // A BLOB that is JSONB is JSON, whatever produced it. It goes in as the canonical text it stands for, read again: a JSON5
                // number or a padded header in the blob comes out in its plain form (observed).
                if (Jsonb.LooksValid(v.Bytes))
                {
                    if (rawText)
                    {
                        // json_set and its relatives splice the blob's element as it is, JSON5 forms and padding included (observed).
                        o.Put(v.Bytes);
                        break;
                    }

                    JsonBuf text = JsonScratch.Get(6);
                    JsonRender.Render(v.Bytes, 0, text);
                    var p = new JsonTextParser(text.Span, o);
                    if (!p.ParseDocument())
                    {
                        throw JsonRender.Malformed();
                    }

                    break;
                }

                throw Err("JSON cannot hold BLOB values");
        }
    }

    /// <summary>An SQL string as a JSON string element: TEXT as it is, or TEXTJ with its quote, backslash and control characters escaped.</summary>
    internal static void PutString(JsonBuf o, ReadOnlySpan<byte> s)
    {
        if (!JsonRender.NeedsEscape(s))
        {
            Jsonb.PutElem(o, JT.Text, s);
            return;
        }

        JsonBuf e = JsonScratch.Get(6);
        JsonRender.EscapeRaw(s, e);
        Jsonb.PutElem(o, JT.TextJ, e.Span);
    }

    internal static void PutReal(double d, JsonBuf o, bool forEdit = false)
    {
        if (double.IsNaN(d))
        {
            o.Put(JT.Null);
        }
        else if (double.IsInfinity(d))
        {
            if (forEdit)
            {
                Jsonb.PutElem(o, JT.Float, d > 0 ? "9e999"u8 : "-9e999"u8);
            }
            else
            {
                Jsonb.PutElem(o, JT.Float, d > 0 ? "9.0e+999"u8 : "-9.0e+999"u8);
            }
        }
        else
        {
            Span<byte> t = stackalloc byte[Values.MaxRealText];
            int n = Values.FormatReal(d, t);
            Jsonb.PutElem(o, JT.Float, t[..n]);
        }
    }

    // ---------------------------------------------------------------- results

    private static void ResultText(ReadOnlySpan<byte> text, ref Mem dst, ref byte[]? own)
    {
        Values.SetBytes(ref dst, ref own, StorageClass.Text, text);
        dst.Subtype = JsonSubtype;
    }

    private static void ResultBlob(ReadOnlySpan<byte> jsonb, ref Mem dst, ref byte[]? own)
    {
        Values.SetBytes(ref dst, ref own, StorageClass.Blob, jsonb);
        dst.Subtype = JsonSubtype;
    }

    private static void ResultJson(ReadOnlySpan<byte> jsonb, bool asBlob, ref Mem dst, ref byte[]? own, bool remember = false, bool json5 = false)
    {
        if (asBlob)
        {
            ResultBlob(jsonb, ref dst, ref own);
            return;
        }

        JsonBuf t = JsonScratch.Get(4);
        JsonRender.Render(jsonb, 0, t);
        ResultText(t.Span, ref dst, ref own);
        if (remember)
        {
            JsonCache.Put(t.Span, jsonb, json5);
        }
    }

    /// <summary>The SQL value of the element at <paramref name="pos"/>; arrays and objects come out as text JSON, or JSONB when <paramref name="asBlob"/>.</summary>
    internal static void ElemToSql(ReadOnlySpan<byte> b, int pos, bool asBlob, ref Mem dst, ref byte[]? own, bool markJson = true)
    {
        if (!Jsonb.Header(b, pos, out int t, out int h, out int sz))
        {
            throw JsonRender.Malformed();
        }

        ReadOnlySpan<byte> p = b.Slice(pos + h, sz);
        switch (t)
        {
            case JT.Null:
                Values.SetNull(ref dst);
                break;
            case JT.True:
                Values.SetInt(ref dst, 1);
                break;
            case JT.False:
                Values.SetInt(ref dst, 0);
                break;
            case JT.Int:
            case JT.Int5:
            case JT.Float:
            case JT.Float5:
                JsonRender.NumberValue(t, p, ref dst);
                break;
            case JT.Text:
            case JT.TextRaw:
                Values.SetBytes(ref dst, ref own, StorageClass.Text, p);
                break;
            case JT.TextJ:
            case JT.Text5:
            {
                JsonBuf u = JsonScratch.Get(3);
                JsonRender.Unescape(t, p, u);
                Values.SetBytes(ref dst, ref own, StorageClass.Text, u.Span);
                break;
            }

            case JT.Array:
            case JT.Object:
                if (asBlob)
                {
                    ResultBlob(b.Slice(pos, h + sz), ref dst, ref own);
                }
                else
                {
                    JsonBuf r = JsonScratch.Get(4);
                    JsonRender.Render(b, pos, r);
                    ResultText(r.Span, ref dst, ref own);
                    if (!markJson)
                    {
                        dst.Subtype = 0;
                    }
                }

                break;
            default:
                throw JsonRender.Malformed();
        }
    }

    /// <summary>The result of json_group_array and json_group_object: the accumulated elements wrapped in a container (an empty group gives <c>[]</c> or <c>{}</c>).</summary>
    internal static void GroupResult(JsonBuf? elems, AggKind kind, ref Mem dst, ref byte[]? own)
    {
        bool obj = kind is AggKind.JsonGroupObject or AggKind.JsonbGroupObject;
        JsonBuf o = JsonScratch.Get(1);
        int at = Jsonb.BeginContainer(o);
        if (elems is not null)
        {
            o.Put(elems.Span);
        }

        Jsonb.EndContainer(o, at, obj ? JT.Object : JT.Array);
        dst.Subtype = 0;
        ResultJson(o.Span, kind is AggKind.JsonbGroupArray or AggKind.JsonbGroupObject, ref dst, ref own);
    }

    // ---------------------------------------------------------------- dispatch

    internal static void Call(FuncId id, ReadOnlySpan<Mem> args, ref Mem dst, ref byte[]? own)
    {
        dst.Subtype = 0;
        JsonBuf docBuf = JsonScratch.Get(0);
        switch (id)
        {
            case FuncId.Json:
            case FuncId.Jsonb:
            {
                if (!Doc(args[0], docBuf, out ReadOnlySpan<byte> d))
                {
                    Values.SetNull(ref dst);
                    return;
                }

                ResultJson(d, id == FuncId.Jsonb, ref dst, ref own);
                return;
            }

            case FuncId.JsonArray:
            case FuncId.JsonbArray:
            {
                JsonBuf o = JsonScratch.Get(1);
                int at = Jsonb.BeginContainer(o);
                foreach (Mem a in args)
                {
                    PutValue(a, o);
                }

                Jsonb.EndContainer(o, at, JT.Array);
                ResultJson(o.Span, id == FuncId.JsonbArray, ref dst, ref own);
                return;
            }

            case FuncId.JsonObject:
            case FuncId.JsonbObject:
            {
                if ((args.Length & 1) != 0)
                {
                    throw Err("json_object() requires an even number of arguments");
                }

                JsonBuf o = JsonScratch.Get(1);
                int at = Jsonb.BeginContainer(o);
                for (int i = 0; i < args.Length; i += 2)
                {
                    if (args[i].Kind != StorageClass.Text)
                    {
                        throw Err("json_object() labels must be TEXT");
                    }

                    PutString(o, args[i].Bytes);
                    PutValue(args[i + 1], o);
                }

                Jsonb.EndContainer(o, at, JT.Object);
                ResultJson(o.Span, id == FuncId.JsonbObject, ref dst, ref own);
                return;
            }

            case FuncId.JsonQuote:
            {
                ref readonly Mem a = ref args[0];
                if (a.Kind == StorageClass.Text && a.Subtype == JsonSubtype)
                {
                    ResultText(a.Bytes, ref dst, ref own);
                    return;
                }

                JsonBuf o = JsonScratch.Get(1);
                PutValue(a, o);
                ResultJson(o.Span, false, ref dst, ref own);
                return;
            }

            case FuncId.JsonExtract:
            case FuncId.JsonbExtract:
                Extract(args, id == FuncId.JsonbExtract, ref dst, ref own, docBuf);
                return;
            case FuncId.JsonArrow:
            case FuncId.JsonArrow2:
                Arrow(args, id == FuncId.JsonArrow2, ref dst, ref own, docBuf);
                return;
            case FuncId.JsonType:
            case FuncId.JsonArrayLength:
                TypeOrLength(id, args, ref dst, ref own, docBuf);
                return;
            case FuncId.JsonSet:
            case FuncId.JsonbSet:
            case FuncId.JsonInsert:
            case FuncId.JsonbInsert:
            case FuncId.JsonReplace:
            case FuncId.JsonbReplace:
            case FuncId.JsonArrayInsert:
            case FuncId.JsonbArrayInsert:
            case FuncId.JsonRemove:
            case FuncId.JsonbRemove:
                Edit(id, args, ref dst, ref own, docBuf);
                return;
            case FuncId.JsonPatch:
            case FuncId.JsonbPatch:
                Patch(args, id == FuncId.JsonbPatch, ref dst, ref own, docBuf);
                return;
            case FuncId.JsonPretty:
            {
                if (!Doc(args[0], docBuf, out ReadOnlySpan<byte> d))
                {
                    Values.SetNull(ref dst);
                    return;
                }

                ReadOnlySpan<byte> indent = "    "u8;
                if (args.Length > 1 && args[1].Kind != StorageClass.Null)
                {
                    indent = PathArg(args[1]);
                }

                JsonBuf o = JsonScratch.Get(1);
                JsonRender.Pretty(d, 0, indent, o, 0, JsonScratch.Get(2));
                Values.SetBytes(ref dst, ref own, StorageClass.Text, o.Span);
                return;
            }

            case FuncId.JsonValid:
                Valid(args, ref dst, docBuf);
                return;
            case FuncId.JsonErrorPosition:
                ErrorPosition(args[0], ref dst);
                return;
            default:
                throw new OneLiteException(ResultCode.Error, "unsupported JSON function");
        }
    }

    // ---------------------------------------------------------------- extraction

    private static void Extract(ReadOnlySpan<Mem> args, bool asBlob, ref Mem dst, ref byte[]? own, JsonBuf docBuf)
    {
        if (args.Length < 2 || !Doc(args[0], docBuf, out ReadOnlySpan<byte> d))
        {
            Values.SetNull(ref dst);
            return;
        }

        if (args.Length == 2)
        {
            if (args[1].Kind == StorageClass.Null)
            {
                Values.SetNull(ref dst);
                return;
            }

            if (!JsonPath.Find(d, PathArg(args[1]), out int pos))
            {
                Values.SetNull(ref dst);
                return;
            }

            ElemToSql(d, pos, asBlob, ref dst, ref own);
            return;
        }

        JsonBuf o = JsonScratch.Get(1);
        int at = Jsonb.BeginContainer(o);
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].Kind == StorageClass.Null)
            {
                Values.SetNull(ref dst);
                return;
            }

            if (JsonPath.Find(d, PathArg(args[i]), out int pos))
            {
                Jsonb.Header(d, pos, out _, out int h, out int s);
                o.Put(d.Slice(pos, h + s));
            }
            else
            {
                o.Put(JT.Null);
            }
        }

        Jsonb.EndContainer(o, at, JT.Array);
        ResultJson(o.Span, asBlob, ref dst, ref own);
    }

    private static void Arrow(ReadOnlySpan<Mem> args, bool sql, ref Mem dst, ref byte[]? own, JsonBuf docBuf)
    {
        if (!Doc(args[0], docBuf, out ReadOnlySpan<byte> d) || args[1].Kind == StorageClass.Null)
        {
            Values.SetNull(ref dst);
            return;
        }

        int pos;
        ref readonly Mem r = ref args[1];
        bool found;
        switch (r.Kind)
        {
            case StorageClass.Text:
            {
                ReadOnlySpan<byte> rt = r.Bytes;
                if (rt.Length > 0 && rt[0] != '$')
                {
                    found = FindLabel(d, rt, out pos);
                }
                else
                {
                    found = JsonPath.Find(d, rt, out pos);
                }

                break;
            }

            case StorageClass.Integral:
                found = FindIndex(d, r.IntValue, out pos);
                break;
            default:
                found = false;
                pos = 0;
                break;
        }

        if (!found)
        {
            Values.SetNull(ref dst);
            return;
        }

        if (sql)
        {
            ElemToSql(d, pos, false, ref dst, ref own, false);
            return;
        }

        JsonBuf o = JsonScratch.Get(1);
        JsonRender.Render(d, pos, o);
        ResultText(o.Span, ref dst, ref own);
    }

    private static bool FindLabel(ReadOnlySpan<byte> d, ReadOnlySpan<byte> label, out int pos)
    {
        pos = 0;
        if (!Jsonb.Header(d, 0, out int t, out int h, out int sz))
        {
            throw JsonRender.Malformed();
        }

        if (t != JT.Object)
        {
            return false;
        }

        if (JsonPath.FindKey(d.Slice(h, sz), label, false, out int off) < 0)
        {
            return false;
        }

        pos = h + off;
        return true;
    }

    private static bool FindIndex(ReadOnlySpan<byte> d, long n, out int pos)
    {
        pos = 0;
        if (!Jsonb.Header(d, 0, out int t, out int h, out int sz))
        {
            throw JsonRender.Malformed();
        }

        if (t != JT.Array)
        {
            return false;
        }

        ReadOnlySpan<byte> p = d.Slice(h, sz);
        int count = JsonPath.Count(p);
        long idx = n >= 0 ? n : count + n;
        if (idx < 0 || idx >= count)
        {
            return false;
        }

        pos = h + JsonPath.ElemOffset(p, (int)idx);
        return true;
    }

    internal static readonly byte[][] s_typeNames =
    [
        "null"u8.ToArray(), "true"u8.ToArray(), "false"u8.ToArray(), "integer"u8.ToArray(), "integer"u8.ToArray(), "real"u8.ToArray(), "real"u8.ToArray(),
        "text"u8.ToArray(), "text"u8.ToArray(), "text"u8.ToArray(), "text"u8.ToArray(), "array"u8.ToArray(), "object"u8.ToArray(),
    ];

    private static void TypeOrLength(FuncId id, ReadOnlySpan<Mem> args, ref Mem dst, ref byte[]? own, JsonBuf docBuf)
    {
        if (!Doc(args[0], docBuf, out ReadOnlySpan<byte> d))
        {
            Values.SetNull(ref dst);
            return;
        }

        int pos = 0;
        if (args.Length > 1)
        {
            if (args[1].Kind == StorageClass.Null)
            {
                Values.SetNull(ref dst);
                return;
            }

            if (!JsonPath.Find(d, PathArg(args[1]), out pos))
            {
                Values.SetNull(ref dst);
                return;
            }
        }

        if (!Jsonb.Header(d, pos, out int t, out int h, out int sz) || t > JT.Object)
        {
            throw JsonRender.Malformed();
        }

        if (id == FuncId.JsonType)
        {
            Values.SetRef(ref dst, StorageClass.Text, s_typeNames[t]);
            return;
        }

        Values.SetInt(ref dst, t == JT.Array ? JsonPath.Count(d.Slice(pos + h, sz)) : 0);
    }

    // ---------------------------------------------------------------- editing

    private static void Edit(FuncId id, ReadOnlySpan<Mem> args, ref Mem dst, ref byte[]? own, JsonBuf docBuf)
    {
        bool remove = id is FuncId.JsonRemove or FuncId.JsonbRemove;
        if (args.Length == 0)
        {
            // Black-box: json_set() and its relatives called with no arguments give NULL.
            Values.SetNull(ref dst);
            return;
        }

        if (!remove && (args.Length & 1) == 0)
        {
            throw Err(FunctionName(id) + "() needs an odd number of arguments");
        }

        if (!Doc(args[0], docBuf, out ReadOnlySpan<byte> d, out bool docJson5))
        {
            Values.SetNull(ref dst);
            return;
        }

        JsonEdit mode = id switch
        {
            FuncId.JsonSet or FuncId.JsonbSet => JsonEdit.Set,
            FuncId.JsonInsert or FuncId.JsonbInsert => JsonEdit.Insert,
            FuncId.JsonReplace or FuncId.JsonbReplace => JsonEdit.Replace,
            FuncId.JsonArrayInsert or FuncId.JsonbArrayInsert => JsonEdit.ArrayInsert,
            _ => JsonEdit.Remove,
        };
        bool asBlob = id is FuncId.JsonbSet or FuncId.JsonbInsert or FuncId.JsonbReplace or FuncId.JsonbArrayInsert or FuncId.JsonbRemove;
        JsonBuf a = JsonScratch.Get(1);
        JsonBuf b = JsonScratch.Get(2);
        JsonBuf val = JsonScratch.Get(5);
        ReadOnlySpan<byte> cur = d;
        JsonBuf into = a;
        int step = remove ? 1 : 2;
        for (int i = 1; i < args.Length; i += step)
        {
            if (args[i].Kind == StorageClass.Null)
            {
                if (remove)
                {
                    Values.SetNull(ref dst);
                    return;
                }

                continue;
            }

            ReadOnlySpan<byte> path = PathArg(args[i]);
            if (remove && path.Length == 1 && path[0] == '$')
            {
                Values.SetNull(ref dst);
                return;
            }

            ReadOnlySpan<byte> v = default;
            if (path.Length == 0 || path[0] != (byte)'$')
            {
                throw JsonPathCursor.Bad(path);
            }

            if (!remove)
            {
                val.Clear();
                PutValue(args[i + 1], val, true);
                v = val.Span;
            }

            into.Clear();
            if (JsonPath.Edit(cur, path, mode, v, into))
            {
                cur = into.Span;
                into = ReferenceEquals(into, a) ? b : a;
            }
        }

        ResultJson(cur, asBlob, ref dst, ref own, true, docJson5);
    }

    private static string FunctionName(FuncId id) => id switch
    {
        FuncId.JsonSet => "json_set",
        FuncId.JsonbSet => "jsonb_set",
        FuncId.JsonInsert => "json_insert",
        FuncId.JsonbInsert => "jsonb_insert",
        FuncId.JsonReplace => "json_replace",
        FuncId.JsonbReplace => "jsonb_replace",
        FuncId.JsonArrayInsert => "json_array_insert",
        _ => "jsonb_array_insert",
    };

    // ---------------------------------------------------------------- merge patch (RFC 7396)

    private static void Patch(ReadOnlySpan<Mem> args, bool asBlob, ref Mem dst, ref byte[]? own, JsonBuf docBuf)
    {
        if (!Doc(args[0], docBuf, out ReadOnlySpan<byte> t, out bool targetJson5))
        {
            Values.SetNull(ref dst);
            return;
        }

        JsonBuf pb = JsonScratch.Get(1);
        if (!Doc(args[1], pb, out ReadOnlySpan<byte> p))
        {
            Values.SetNull(ref dst);
            return;
        }

        byte[] res = MergePatch(t, true, p, 0);
        ResultJson(res, asBlob, ref dst, ref own, true, targetJson5);
    }

    private static byte[] MergePatch(ReadOnlySpan<byte> target, bool exists, ReadOnlySpan<byte> patch, int depth)
    {
        if (depth > JsonTextParser.MaxDepth || !Jsonb.Header(patch, 0, out int pt, out int ph, out int ps))
        {
            throw JsonRender.Malformed();
        }

        if (pt != JT.Object)
        {
            if (depth == 0 && exists && Jsonb.Header(target, 0, out _, out int oth, out int ots))
            {
                var pad = new JsonBuf();
                Jsonb.PutPadded(pad, patch.Slice(0, ph + ps), oth + ots);
                return pad.Span.ToArray();
            }

            return patch.Slice(0, ph + ps).ToArray();
        }

        // members of the result so far: label bytes and value bytes
        var labels = new List<byte[]>();
        var values = new List<byte[]>();
        if (exists && Jsonb.Header(target, 0, out int tt, out int th, out int ts) && tt == JT.Object)
        {
            ReadOnlySpan<byte> tp = target.Slice(th, ts);
            int i = 0;
            while (i < tp.Length)
            {
                if (!Jsonb.Header(tp, i, out _, out int kh, out int ks) || !Jsonb.Header(tp, i + kh + ks, out _, out int vh, out int vs))
                {
                    throw JsonRender.Malformed();
                }

                labels.Add(tp.Slice(i, kh + ks).ToArray());
                values.Add(tp.Slice(i + kh + ks, vh + vs).ToArray());
                i += kh + ks + vh + vs;
            }
        }

        ReadOnlySpan<byte> pp = patch.Slice(ph, ps);
        int j = 0;
        while (j < pp.Length)
        {
            if (!Jsonb.Header(pp, j, out int kt, out int kh, out int ks) || !Jsonb.Header(pp, j + kh + ks, out int vt, out int vh, out int vs))
            {
                throw JsonRender.Malformed();
            }

            ReadOnlySpan<byte> key = pp.Slice(j + kh, ks);
            ReadOnlySpan<byte> val = pp.Slice(j + kh + ks, vh + vs);
            int at = -1;
            for (int k = 0; k < labels.Count; k++)
            {
                Jsonb.Header(labels[k], 0, out int lt, out int lh, out int ls);
                if (lt is >= JT.Text and <= JT.TextRaw && LabelsEqual(lt, labels[k].AsSpan(lh, ls), kt, key))
                {
                    at = k;
                    break;
                }
            }

            if (vt == JT.Null)
            {
                if (at >= 0)
                {
                    labels.RemoveAt(at);
                    values.RemoveAt(at);
                }
            }
            else if (at >= 0)
            {
                if (vt == JT.Object)
                {
                    values[at] = MergePatch(values[at], true, val, depth + 1);
                }
                else
                {
                    var pad = new JsonBuf();
                    Jsonb.PutPadded(pad, val, values[at].Length);
                    values[at] = pad.Span.ToArray();
                }
            }
            else
            {
                labels.Add(pp.Slice(j, kh + ks).ToArray());
                values.Add(MergePatch(default, false, val, depth + 1));
            }

            j += kh + ks + vh + vs;
        }

        var o = new JsonBuf();
        int c = Jsonb.BeginContainer(o);
        for (int k = 0; k < labels.Count; k++)
        {
            o.Put(labels[k]);
            o.Put(values[k]);
        }

        Jsonb.EndContainer(o, c, JT.Object);
        return o.Span.ToArray();
    }

    private static bool LabelsEqual(int t1, ReadOnlySpan<byte> a, int t2, ReadOnlySpan<byte> b)
    {
        if (t1 == t2 || (t1 is JT.Text or JT.TextRaw && t2 is JT.Text or JT.TextRaw))
        {
            if (a.SequenceEqual(b))
            {
                return true;
            }
        }

        var x = new JsonBuf();
        var y = new JsonBuf();
        JsonRender.Unescape(t1, a, x);
        JsonRender.Unescape(t2, b, y);
        return x.Span.SequenceEqual(y.Span);
    }

    // ---------------------------------------------------------------- validity

    private static void Valid(ReadOnlySpan<Mem> args, ref Mem dst, JsonBuf docBuf)
    {
        int flags = 1;
        if (args.Length > 1)
        {
            long f = Values.ToInt(args[1]);
            if (f < 1 || f > 15)
            {
                throw Err("FLAGS parameter to json_valid() must be between 1 and 15");
            }

            flags = (int)f;
        }

        ref readonly Mem a = ref args[0];
        if (a.Kind == StorageClass.Null)
        {
            Values.SetNull(ref dst);
            return;
        }

        bool ok = false;
        if (a.Kind == StorageClass.Blob)
        {
            ReadOnlySpan<byte> bl = a.Bytes;
            if (Jsonb.LooksValid(bl))
            {
                // a blob that looks like JSONB is JSONB, even if its bytes would also read as JSON text
                ok = (flags & 8) != 0 ? JsonbCheck.Strict(bl) : (flags & 4) != 0;
            }
            else if ((flags & 3) != 0)
            {
                ok = TextValid(bl, flags, docBuf, false);
            }
        }
        else if ((flags & 3) != 0)
        {
            Span<byte> tmp = stackalloc byte[Values.MaxRealText];
            ReadOnlySpan<byte> text = a.Kind == StorageClass.Text ? a.Bytes : Values.AsText(a, tmp);
            ok = TextValid(text, flags, docBuf, false);
        }

        Values.SetInt(ref dst, ok ? 1 : 0);
    }

    private static bool TextValid(ReadOnlySpan<byte> text, int flags, JsonBuf buf, bool fromJson)
    {
        int nul = text.IndexOf((byte)0);
        if (nul >= 0)
        {
            text = text[..nul];
        }

        if (JsonCache.TryGet(text, fromJson, out _, out bool cachedJson5))
        {
            // the stored form remembers the text it was first read from: a JSON5 input leaves its result "not strict" (observed)
            return (flags & 2) != 0 || !cachedJson5;
        }

        buf.Clear();
        var p = new JsonTextParser(text, buf);
        if (!p.ParseDocument())
        {
            return false;
        }

        return (flags & 2) != 0 || !p.Json5;
    }

    private static void ErrorPosition(in Mem a, ref Mem dst)
    {
        if (a.Kind == StorageClass.Null)
        {
            Values.SetNull(ref dst);
            return;
        }

        Span<byte> tmp = stackalloc byte[Values.MaxRealText];
        ReadOnlySpan<byte> text = a.Kind is StorageClass.Text or StorageClass.Blob ? a.Bytes : Values.AsText(a, tmp);
        if (a.Kind == StorageClass.Blob && Jsonb.LooksValid(text))
        {
            int bad = JsonbCheck.FirstError(text);
            Values.SetInt(ref dst, bad + 1);
            return;
        }

        int nul = text.IndexOf((byte)0);
        if (nul >= 0)
        {
            text = text[..nul];
        }

        JsonBuf buf = JsonScratch.Get(0);
        var p = new JsonTextParser(text, buf);
        if (p.ParseDocument())
        {
            Values.SetInt(ref dst, 0);
            return;
        }

        long chars = 0;
        for (int i = 0; i < p.ErrorAt && i < text.Length; i++)
        {
            if ((text[i] & 0xC0) != 0x80)
            {
                chars++;
            }
        }

        Values.SetInt(ref dst, chars + 1);
    }
}
