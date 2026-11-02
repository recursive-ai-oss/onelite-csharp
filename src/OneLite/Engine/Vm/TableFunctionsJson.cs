using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Vm;

/// <summary>
/// json_each, json_tree and their jsonb_ forms (json1 §4.23): one row per element, in document order. A tree row for a container is followed by
/// the rows of its elements. The document is read into JSONB once per scan and walked with an explicit stack, so depth costs no recursion.
/// </summary>
internal sealed class JsonTableCursor : ModuleCursor
{
    private struct Frame
    {
        internal int Next;
        internal int End;
        internal int Index;
        internal bool IsArray;
        internal long ParentId;
        internal byte[] Prefix;
    }

    private readonly bool _tree;
    private readonly bool _blob;
    private readonly JsonBuf _buf = new();

    private byte[] _doc = [];
    private Mem _jsonArg;
    private byte[]? _jsonBytes;
    private byte[] _root = "$"u8.ToArray();

    private Frame[] _stack = new Frame[16];
    private int _depth;
    private bool _eof = true;
    private bool _single;
    private bool _descend;
    private long _row;

    // the current row
    private int _pos;
    private int _idPos;
    private long _parentId;
    private int _keyKind; // 0 NULL, 1 integer, 2 a label of the document (at _labelPos), 3 text in _keyText
    private long _keyInt;
    private int _labelPos;
    private byte[] _keyText = [];
    private byte[] _fullKey = [];
    private byte[] _path = [];

    internal JsonTableCursor(bool tree, bool blob)
    {
        _tree = tree;
        _blob = blob;
    }

    internal override bool AtEnd => _eof;

    internal override long RowNumber => _row;

    internal override void Filter(ReadOnlySpan<Mem> args)
    {
        _eof = true;
        _depth = 0;
        _single = false;
        _descend = false;
        _row = 0;
        _root = "$"u8.ToArray();
        if (args.Length == 0 || args[0].Kind == StorageClass.Null)
        {
            return;
        }

        JsonFunctions.Doc(args[0], _buf, out ReadOnlySpan<byte> d);
        _doc = d.ToArray();

        // The document is read (and a malformed one reported) before a NULL path is noticed.
        if (args.Length > 1)
        {
            if (args[1].Kind == StorageClass.Null)
            {
                return;
            }

            Span<byte> tmp = stackalloc byte[Values.MaxRealText];
            _root = (args[1].Kind == StorageClass.Text ? args[1].Bytes : Values.AsText(args[1], tmp)).ToArray();
        }

        // The hidden "json" column holds the argument: a text, or the blob when it is JSONB; a number or a blob of JSON text reads as text.
        if (args[0].Kind == StorageClass.Blob && Jsonb.LooksValid(args[0].Bytes))
        {
            _jsonArg = args[0];
            _jsonBytes = args[0].Bytes.ToArray();
        }
        else
        {
            Span<byte> jt = stackalloc byte[Values.MaxRealText];
            ReadOnlySpan<byte> src = args[0].Kind is StorageClass.Text or StorageClass.Blob ? args[0].Bytes : Values.AsText(args[0], jt);
            _jsonArg = default;
            _jsonArg.Kind = StorageClass.Text;
            _jsonBytes = src.ToArray();
        }

        if (!JsonPath.Find(_doc, _root, out int pos, out int idPos) || !Jsonb.Header(_doc, pos, out int t, out int h, out int sz))
        {
            return;
        }

        _eof = false;
        bool container = t is JT.Array or JT.Object;
        if (_tree)
        {
            SetStartRow(pos, idPos);
            _descend = container;
            return;
        }

        if (!container)
        {
            _pos = pos;
            _idPos = idPos;
            _parentId = -1;
            _keyKind = 0;
            _fullKey = _root;
            _path = _root;
            _single = true;
            return;
        }

        Push(pos + h, pos + h + sz, t == JT.Array, -1, _root);
        Advance();
    }

    internal override void Next()
    {
        _row++;
        if (_single)
        {
            _eof = true;
            return;
        }

        if (_descend)
        {
            _descend = false;
            Jsonb.Header(_doc, _pos, out int t, out int h, out int sz);
            Push(_pos + h, _pos + h + sz, t == JT.Array, _idPos, _fullKey);
        }

        Advance();
    }

    private void Push(int start, int end, bool isArray, long parentId, byte[] prefix)
    {
        if (_depth == _stack.Length)
        {
            Array.Resize(ref _stack, _depth * 2);
        }

        _stack[_depth++] = new Frame { Next = start, End = end, IsArray = isArray, ParentId = parentId, Prefix = prefix };
    }

    /// <summary>Moves to the next element of the innermost container that has one, leaving finished containers.</summary>
    private void Advance()
    {
        while (_depth > 0 && _stack[_depth - 1].Next >= _stack[_depth - 1].End)
        {
            _depth--;
        }

        if (_depth == 0)
        {
            _eof = true;
            return;
        }

        ref Frame f = ref _stack[_depth - 1];
        int off = f.Next;
        int valueOff;
        if (f.IsArray)
        {
            valueOff = off;
            _keyKind = 1;
            _keyInt = f.Index++;
            _idPos = off;
            var fk = new JsonBuf();
            fk.Put(f.Prefix);
            fk.Put((byte)'[');
            Span<byte> num = stackalloc byte[24];
            fk.Put(num[..Values.FormatInt(_keyInt, num)]);
            fk.Put((byte)']');
            _fullKey = fk.Span.ToArray();
        }
        else
        {
            if (!Jsonb.Header(_doc, off, out int kt, out int kh, out int ks) || kt is < JT.Text or > JT.TextRaw)
            {
                throw JsonRender.Malformed();
            }

            valueOff = off + kh + ks;
            _keyKind = 2;
            _labelPos = off;
            _idPos = off;
            ReadOnlySpan<byte> label = _doc.AsSpan(off + kh, ks);
            var fk = new JsonBuf();
            fk.Put(f.Prefix);
            fk.Put((byte)'.');
            bool plain = label.Length > 0 && IsAsciiLetter(label[0]);
            for (int i = 1; plain && i < label.Length; i++)
            {
                plain = IsAsciiLetter(label[i]) || (label[i] is >= (byte)'0' and <= (byte)'9');
            }

            if (!plain)
            {
                fk.Put((byte)'"');
            }

            fk.Put(label);
            if (!plain)
            {
                fk.Put((byte)'"');
            }

            _fullKey = fk.Span.ToArray();
        }

        if (!Jsonb.Header(_doc, valueOff, out int vt, out int vh, out int vs))
        {
            throw JsonRender.Malformed();
        }

        f.Next = valueOff + vh + vs;
        _pos = valueOff;
        _parentId = f.ParentId;
        _path = f.Prefix;
        _descend = _tree && vt is JT.Array or JT.Object;
    }

    private static bool IsAsciiLetter(byte c) => c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z';

    /// <summary>
    /// The first row of json_tree: the element the root path selects. Its key and path come from the path text, in the way the reference
    /// reads them (observed): a final label gives the key and the path before it; a final index of 0 gives key 0 and the path before it; any other
    /// final index gives the rest of the path as the key (an integer if it starts with an index) and "$" as the path.
    /// </summary>
    private void SetStartRow(int pos, int idPos)
    {
        _pos = pos;
        _idPos = idPos;
        _parentId = -1;
        _fullKey = _root;
        _path = _root;
        _keyKind = 0;
        if (_root.Length <= 1)
        {
            return;
        }

        var pc = new JsonPathCursor(_root);
        int segStart = 1;
        while (!pc.AtEnd)
        {
            segStart = pc.Pos;
            _ = pc.PeekIsKey();
            pc.Next();
        }

        if (pc.IsKey)
        {
            _keyKind = 2;
            _labelPos = idPos;
            _path = _root[..segStart];
        }
        else if (!pc.FromEnd && pc.Index == 0)
        {
            _keyKind = 1;
            _keyInt = 0;
            _path = _root[..segStart];
        }
        else
        {
            _path = "$"u8.ToArray();
            ReadOnlySpan<byte> rest = _root.AsSpan(1);
            if (rest[0] == '.')
            {
                _keyKind = 3;
                _keyText = rest[1..].ToArray();
            }
            else
            {
                _keyKind = 1;
                long n = 0;
                for (int i = 1; i < rest.Length && rest[i] is >= (byte)'0' and <= (byte)'9'; i++)
                {
                    n = (n * 10) + (rest[i] - '0');
                }

                _keyInt = n;
            }
        }
    }

    internal override void Column(int index, ref Mem dst, ref byte[]? own)
    {
        Jsonb.Header(_doc, _pos, out int t, out _, out _);
        bool container = t is JT.Array or JT.Object;
        switch (index)
        {
            case 0:
                switch (_keyKind)
                {
                    case 0:
                        Values.SetNull(ref dst);
                        break;
                    case 1:
                        Values.SetInt(ref dst, _keyInt);
                        break;
                    case 2:
                    {
                        Jsonb.Header(_doc, _labelPos, out int kt, out int kh, out int ks);
                        ReadOnlySpan<byte> label = _doc.AsSpan(_labelPos + kh, ks);
                        if (kt is JT.Text or JT.TextRaw)
                        {
                            Values.SetBytes(ref dst, ref own, StorageClass.Text, label);
                        }
                        else
                        {
                            JsonBuf u = JsonScratch.Get(2);
                            JsonRender.Unescape(kt, label, u);
                            Values.SetBytes(ref dst, ref own, StorageClass.Text, u.Span);
                        }

                        break;
                    }

                    default:
                        Values.SetBytes(ref dst, ref own, StorageClass.Text, _keyText);
                        break;
                }

                break;
            case 1:
                JsonFunctions.ElemToSql(_doc, _pos, _blob, ref dst, ref own);
                break;
            case 2:
                Values.SetBytes(ref dst, ref own, StorageClass.Text, JsonFunctions.s_typeNames[t]);
                break;
            case 3:
                if (container)
                {
                    Values.SetNull(ref dst);
                }
                else
                {
                    JsonFunctions.ElemToSql(_doc, _pos, false, ref dst, ref own);
                }

                break;
            case 4:
                Values.SetInt(ref dst, _idPos);
                break;
            case 5:
                if (_parentId < 0)
                {
                    Values.SetNull(ref dst);
                }
                else
                {
                    Values.SetInt(ref dst, _parentId);
                }

                break;
            case 6:
                Values.SetBytes(ref dst, ref own, StorageClass.Text, _fullKey);
                break;
            case 7:
                Values.SetBytes(ref dst, ref own, StorageClass.Text, _path);
                break;
            case 8:
                if (_jsonBytes is null)
                {
                    dst = _jsonArg;
                }
                else
                {
                    Values.SetBytes(ref dst, ref own, _jsonArg.Kind, _jsonBytes);
                }

                break;
            default:
                Values.SetBytes(ref dst, ref own, StorageClass.Text, _root);
                break;
        }
    }
}
