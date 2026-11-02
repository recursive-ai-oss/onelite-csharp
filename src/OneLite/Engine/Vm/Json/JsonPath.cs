using System.Text;

namespace RecursiveAI.OneLite.Vm;

/// <summary>How a path/value pair edits a document (json1 §4.11, §4.5, §4.18).</summary>
internal enum JsonEdit : byte
{
    Replace,
    Insert,
    Set,
    Remove,
    ArrayInsert,
}

/// <summary>
/// A JSON path read one segment at a time. A malformed segment is reported only when a walk reaches it: a walk that has already
/// failed to find an element never looks at the rest of the path (observed on the pinned shell).
/// </summary>
internal ref struct JsonPathCursor
{
    internal readonly ReadOnlySpan<byte> Path;
    internal int Pos;

    // the current segment
    internal bool IsKey;
    internal bool Quoted;
    internal int KeyStart;
    internal int KeyLen;

    /// <summary>For an index segment: the number, counted from the front, or from the end when <see cref="FromEnd"/> (#-n; plain # is from the end with 0).</summary>
    internal long Index;
    internal bool FromEnd;

    internal JsonPathCursor(ReadOnlySpan<byte> path)
    {
        Path = path;
        Pos = 0;
        IsKey = false;
        KeyStart = 0;
        KeyLen = 0;
        Index = 0;
        FromEnd = false;
        if (path.Length == 0 || path[0] != '$')
        {
            throw Bad(path);
        }

        Pos = 1;
    }

    internal static string Esc(ReadOnlySpan<byte> path) => Encoding.UTF8.GetString(path).Replace("'", "''", StringComparison.Ordinal);

    internal static OneLiteException Bad(ReadOnlySpan<byte> path)
        => new(ResultCode.Error, "bad JSON path: '" + Encoding.UTF8.GetString(path).Replace("'", "''", StringComparison.Ordinal) + "'");

    internal readonly bool AtEnd => Pos >= Path.Length;

    /// <summary>The kind of the next segment without reading it: true for a key (.), false for an index ([). Anything else throws, as a path must go on with one or the other.</summary>
    internal readonly bool PeekIsKey()
    {
        byte c = Path[Pos];
        if (c == '.')
        {
            return true;
        }

        if (c == '[')
        {
            return false;
        }

        throw Bad(Path);
    }

    /// <summary>Reads the next segment: .key, ."key", [n], [#] or [#-n]. Throws "bad JSON path" for anything else.</summary>
    internal void Next()
    {
        byte c = Path[Pos];
        if (c == '.')
        {
            IsKey = true;
            Quoted = false;
            Pos++;
            if (Pos < Path.Length && Path[Pos] == '"')
            {
                int s = ++Pos;
                while (Pos < Path.Length && Path[Pos] != '"')
                {
                    if (Path[Pos] == '\\' && Pos + 1 < Path.Length)
                    {
                        Pos++;
                    }

                    Pos++;
                }

                if (Pos >= Path.Length)
                {
                    throw Bad(Path);
                }

                KeyStart = s;
                KeyLen = Pos - s;
                Quoted = true;
                Pos++;
                return;
            }

            int ks = Pos;
            while (Pos < Path.Length && Path[Pos] is not ((byte)'.' or (byte)'['))
            {
                Pos++;
            }

            if (Pos == ks)
            {
                throw Bad(Path);
            }

            KeyStart = ks;
            KeyLen = Pos - ks;
            return;
        }

        if (c == '[')
        {
            IsKey = false;
            FromEnd = false;
            Index = 0;
            Pos++;
            if (Pos < Path.Length && Path[Pos] == '#')
            {
                FromEnd = true;
                Pos++;
                if (Pos < Path.Length && Path[Pos] == '-')
                {
                    Pos++;
                    Index = Digits();
                }
            }
            else
            {
                Index = Digits();
            }

            if (Pos >= Path.Length || Path[Pos] != ']')
            {
                throw Bad(Path);
            }

            Pos++;
            return;
        }

        throw Bad(Path);
    }

    private long Digits()
    {
        int s = Pos;
        long v = 0;
        while (Pos < Path.Length && Path[Pos] is >= (byte)'0' and <= (byte)'9')
        {
            if (v < int.MaxValue)
            {
                v = (v * 10) + (Path[Pos] - '0');
            }

            Pos++;
        }

        if (Pos == s)
        {
            throw Bad(Path);
        }

        return Math.Min(v, int.MaxValue);
    }

    internal readonly ReadOnlySpan<byte> Key => Path.Slice(KeyStart, KeyLen);
}

/// <summary>Walks JSONB along paths, and rewrites it for the editing functions.</summary>
internal static class JsonPath
{
    /// <summary>Finds the element a path selects. False when there is none; throws for a malformed path the walk reaches.</summary>
    internal static bool Find(ReadOnlySpan<byte> b, ReadOnlySpan<byte> path, out int pos) => Find(b, path, out pos, out _);

    /// <summary>As <see cref="Find(ReadOnlySpan{byte}, ReadOnlySpan{byte}, out int)"/>; <paramref name="idPos"/> is where the element's label starts when it is an object member, else where it starts (json_each's id).</summary>
    internal static bool Find(ReadOnlySpan<byte> b, ReadOnlySpan<byte> path, out int pos, out int idPos)
    {
        var pc = new JsonPathCursor(path);
        pos = 0;
        idPos = 0;
        if (!Jsonb.Header(b, 0, out _, out _, out _))
        {
            throw JsonRender.Malformed();
        }

        while (!pc.AtEnd)
        {
            bool wantKey = pc.PeekIsKey();
            if (!Jsonb.Header(b, pos, out int t, out int h, out int sz))
            {
                throw JsonRender.Malformed();
            }

            // A key is read in full (its closing quote included) before the element is looked at; an index is read only for an array.
            if (wantKey)
            {
                pc.Next();
            }

            if (t != (wantKey ? JT.Object : JT.Array))
            {
                return false;
            }

            if (!wantKey)
            {
                pc.Next();
            }

            if (pc.IsKey)
            {
                int c = FindKey(b.Slice(pos + h, sz), pc.Key, pc.Quoted, out int off);
                if (c < 0)
                {
                    return false;
                }

                idPos = pos + h + c;
                pos += h + off;
            }
            else
            {
                ReadOnlySpan<byte> p = b.Slice(pos + h, sz);
                int count = Count(p);
                long idx = pc.FromEnd ? count - pc.Index : pc.Index;
                if (idx < 0 || idx >= count)
                {
                    return false;
                }

                pos += h + ElemOffset(p, (int)idx);
                idPos = pos;
            }
        }

        return true;
    }

    internal static int Count(ReadOnlySpan<byte> payload)
    {
        int n = 0;
        int i = 0;
        while (i < payload.Length)
        {
            if (!Jsonb.Header(payload, i, out _, out int h, out int s))
            {
                throw JsonRender.Malformed();
            }

            i += h + s;
            n++;
        }

        return n;
    }

    internal static int ElemOffset(ReadOnlySpan<byte> payload, int index)
    {
        int i = 0;
        for (int k = 0; k < index; k++)
        {
            if (!Jsonb.Header(payload, i, out _, out int h, out int s))
            {
                throw JsonRender.Malformed();
            }

            i += h + s;
        }

        return i;
    }

    /// <summary>Finds the first member whose label equals <paramref name="key"/>. Returns the offset of its value in the object payload via <paramref name="valueOff"/>, or -1.</summary>
    internal static int FindKey(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> key, bool quoted, out int valueOff)
    {
        int i = 0;
        valueOff = 0;
        while (i < payload.Length)
        {
            if (!Jsonb.Header(payload, i, out int kt, out int kh, out int ks))
            {
                throw JsonRender.Malformed();
            }

            int vi = i + kh + ks;
            if (!Jsonb.Header(payload, vi, out _, out int vh, out int vs))
            {
                throw JsonRender.Malformed();
            }

            if (kt is >= JT.Text and <= JT.TextRaw && KeyEquals(kt, payload.Slice(i + kh, ks), key, quoted))
            {
                valueOff = vi;
                return i;
            }

            i = vi + vh + vs;
        }

        return -1;
    }

    /// <summary>A label matches when its decoded text equals the path's key; a quoted key has its own escapes decoded first, a bare key is taken as written.</summary>
    internal static bool KeyEquals(int type, ReadOnlySpan<byte> label, ReadOnlySpan<byte> key, bool quoted)
    {
        if (quoted && key.IndexOf((byte)'\\') >= 0)
        {
            JsonBuf k = JsonScratch.Get(7);
            JsonRender.Unescape(JT.TextJ, key, k);
            key = k.Span;
        }

        if (type is JT.Text or JT.TextRaw)
        {
            return label.SequenceEqual(key);
        }

        JsonBuf tmp = JsonScratch.Get(6);
        JsonRender.Unescape(type, label, tmp);
        return tmp.Span.SequenceEqual(key);
    }

    // ---------------------------------------------------------------- editing

    /// <summary>Applies one edit to the document in <paramref name="b"/>, writing the result to <paramref name="o"/>. False when the edit changed nothing (nothing is written).</summary>
    internal static bool Edit(ReadOnlySpan<byte> b, ReadOnlySpan<byte> path, JsonEdit mode, ReadOnlySpan<byte> value, JsonBuf o)
    {
        var pc = new JsonPathCursor(path);
        int start = o.Len;
        bool changed = Walk(b, 0, true, ref pc, mode, value, o);
        if (!changed)
        {
            o.Len = start;
        }

        return changed;
    }

    /// <summary>
    /// Rewrites the element at <paramref name="pos"/> (or a missing one, when <paramref name="exists"/> is false and <paramref name="b"/> is ignored)
    /// along the rest of the path. Writes the new element and returns true, or writes nothing and returns false when nothing changes.
    /// </summary>
    private static bool Walk(ReadOnlySpan<byte> b, int pos, bool exists, ref JsonPathCursor pc, JsonEdit mode, ReadOnlySpan<byte> value, JsonBuf o)
    {
        if (pc.AtEnd)
        {
            if (mode is JsonEdit.Replace or JsonEdit.Set)
            {
                if (exists && Jsonb.Header(b, pos, out _, out int oh, out int os))
                {
                    Jsonb.PutPadded(o, value, oh + os);
                }
                else
                {
                    o.Put(value);
                }

                return true;
            }

            if (!exists && mode == JsonEdit.Insert)
            {
                o.Put(value);
                return true;
            }

            return false;
        }

        int type = JT.Object;
        int hdr = 0;
        int size = 0;
        if (exists && !Jsonb.Header(b, pos, out type, out hdr, out size))
        {
            throw JsonRender.Malformed();
        }

        bool wantKey = pc.PeekIsKey();
        if (wantKey)
        {
            pc.Next();
        }

        if (exists && type != (wantKey ? JT.Object : JT.Array))
        {
            return false;
        }

        if (!wantKey)
        {
            pc.Next();
        }

        ReadOnlySpan<byte> payload = exists ? b.Slice(pos + hdr, size) : default;
        bool creating = mode is JsonEdit.Set or JsonEdit.Insert or JsonEdit.ArrayInsert;
        if (pc.IsKey)
        {
            if (exists && type != JT.Object)
            {
                return false;
            }

            if (!exists)
            {
                // A missing member to be created: its container is a new, empty object.
                if (!creating)
                {
                    return false;
                }
            }

            ReadOnlySpan<byte> key = pc.Key;
            int at = FindKey(payload, key, pc.Quoted, out int valueOff);
            bool last = pc.AtEnd;
            if (at >= 0)
            {
                if (!Jsonb.Header(payload, at, out _, out int kh, out int ks) || !Jsonb.Header(payload, valueOff, out _, out int vh, out int vs))
                {
                    throw JsonRender.Malformed();
                }

                if (last && mode == JsonEdit.ArrayInsert)
                {
                    throw new OneLiteException(ResultCode.Error, "not an array element: '" + JsonPathCursor.Esc(pc.Path) + "'");
                }

                if (last && mode == JsonEdit.Insert)
                {
                    return false;
                }

                int mark = o.Len;
                int cont = Jsonb.BeginContainer(o);
                o.Put(payload[..valueOff]);
                bool ch;
                if (last && mode == JsonEdit.Remove)
                {
                    o.Len = cont + 1;
                    o.Put(payload[..at]);
                    o.Put(payload[(valueOff + vh + vs)..]);
                    ch = true;
                }
                else
                {
                    JsonPathCursor save = pc;
                    ch = Walk(payload, valueOff, true, ref pc, mode, value, o);
                    if (!ch)
                    {
                        pc = save;
                        o.Len = mark;
                        return false;
                    }

                    o.Put(payload[(valueOff + vh + vs)..]);
                }

                Jsonb.EndContainer(o, cont, JT.Object);
                return ch;
            }

            // not found
            if (mode == JsonEdit.ArrayInsert && (last || pc.Path[^1] != (byte)']'))
            {
                // observed: json_array_insert refuses a path that does not end in an array index once a key of it is missing
                throw new OneLiteException(ResultCode.Error, "not an array element: '" + JsonPathCursor.Esc(pc.Path) + "'");
            }

            if (!creating)
            {
                return false;
            }

            int m2 = o.Len;
            int c2 = Jsonb.BeginContainer(o);
            o.Put(payload);
            PutKey(o, key, pc.Quoted);
            if (!Walk(default, 0, false, ref pc, mode, value, o))
            {
                o.Len = m2;
                return false;
            }

            Jsonb.EndContainer(o, c2, JT.Object);
            return true;
        }

        // an index segment
        if (exists && type != JT.Array)
        {
            return false;
        }

        if (!exists && !creating)
        {
            return false;
        }

        int count = exists ? Count(payload) : 0;
        long idx = pc.FromEnd ? count - pc.Index : pc.Index;
        bool isLast = pc.AtEnd;
        if (idx < 0 || idx > count)
        {
            return false;
        }

        if (idx < count)
        {
            int eo = ElemOffset(payload, (int)idx);
            if (!Jsonb.Header(payload, eo, out _, out int eh, out int es))
            {
                throw JsonRender.Malformed();
            }

            int mark = o.Len;
            int cont = Jsonb.BeginContainer(o);
            if (isLast && mode == JsonEdit.ArrayInsert)
            {
                o.Put(payload[..eo]);
                o.Put(value);
                o.Put(payload[eo..]);
                Jsonb.EndContainer(o, cont, JT.Array);
                return true;
            }

            if (isLast && mode == JsonEdit.Insert)
            {
                o.Len = mark;
                return false;
            }

            if (isLast && mode == JsonEdit.Remove)
            {
                o.Put(payload[..eo]);
                o.Put(payload[(eo + eh + es)..]);
                Jsonb.EndContainer(o, cont, JT.Array);
                return true;
            }

            o.Put(payload[..eo]);
            JsonPathCursor save = pc;
            if (!Walk(payload, eo, true, ref pc, mode, value, o))
            {
                pc = save;
                o.Len = mark;
                return false;
            }

            o.Put(payload[(eo + eh + es)..]);
            Jsonb.EndContainer(o, cont, JT.Array);
            return true;
        }

        // idx == count: append (a later key segment fails when the new element is built, a later index segment may simply find nothing)
        if (mode == JsonEdit.Remove || mode == JsonEdit.Replace)
        {
            return false;
        }

        int m3 = o.Len;
        int c3 = Jsonb.BeginContainer(o);
        o.Put(payload);
        if (isLast)
        {
            o.Put(value);
        }
        else if (!Walk(default, 0, false, ref pc, mode, value, o))
        {
            o.Len = m3;
            return false;
        }

        Jsonb.EndContainer(o, c3, JT.Array);
        return true;
    }

    /// <summary>Writes a new object label for a path key: the key as written (TEXTRAW), or, for a quoted key with escapes, those escapes kept as JSON5 text (TEXT5).</summary>
    private static void PutKey(JsonBuf o, ReadOnlySpan<byte> key, bool quoted)
    {
        Jsonb.PutElem(o, quoted && key.IndexOf((byte)'\\') >= 0 ? JT.Text5 : JT.TextRaw, key);
    }
}
