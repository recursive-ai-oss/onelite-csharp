using System.Linq;
using RecursiveAI.OneLite.Storage;
using System.Collections.Concurrent;
using System.Text;
using RecursiveAI.OneLite.Internal.Text;
using RecursiveAI.OneLite.Planning;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.VirtualTables;

namespace RecursiveAI.OneLite.Extensions.Fts5;

/// <summary>A scan of an fts5 table: either the rows a query matches, or every row.</summary>
internal sealed class Fts5Cursor : VCursor
{
    internal const byte RefSubtype = 0xF5;

    private static readonly ConcurrentDictionary<long, Fts5Cursor> s_registry = new();
    private static long s_nextId;

    private readonly long _id;
    private readonly Fts5Table _t;
    private readonly Fts5Config _cfg;
    private readonly Fts5Store _store;
    private readonly Fts5Index _idx;
    private readonly Fts5Run _run;

    private bool _started;
    private string[] _spec = [];
    private string? _query;
    private int _queryCol = -1;
    private string? _rankArg;
    private long _lo = long.MinValue;
    private long _hi = long.MaxValue;
    private bool _hasMatch;

    private List<DocMatch> _docs = [];
    private int _pos;
    private List<QPhrase> _phrases = [];
    private long[] _phraseDocs = [];
    private long[] _avg = [];

    private List<long> _scanIds = [];
    private ShadowTree? _scan;
    private int _sph;

    private bool _rowLoaded;
    private Mem[]? _row;
    private long[]? _sizes;
    private bool _eof = true;

    internal Fts5Cursor(Fts5Table t)
    {
        _t = t;
        _cfg = t.Config;
        _store = new Fts5Store(t.Conn, _cfg) { Db = t.Table.Db };
        _idx = new Fts5Index(_cfg, _store);
        _run = new Fts5Run(_store);
        _id = Interlocked.Increment(ref s_nextId);
        s_registry[_id] = this;
    }

    /// <summary>The cursor an auxiliary function's first argument refers to, or null.</summary>
    internal static Fts5Cursor? FromRef(in Mem m)
    {
        if (m.Kind != StorageClass.Blob || m.Subtype != RefSubtype || m.Length != 8)
        {
            return null;
        }

        s_registry.TryGetValue(BitConverter.ToInt64(m.Bytes), out Fts5Cursor? c);
        return c;
    }

    internal Fts5Config Config => _cfg;

    // A scan has no matches; the auxiliary functions still work on its rows, with nothing to match (bm25 is 0, highlight shows the text).
    private readonly DocMatch _scanMatch = new() { Resolved = true };

    internal DocMatch? Current => _mode == 1 ? (_pos < _docs.Count ? _docs[_pos] : null) : _mode == 2 && !_eof ? _scanMatch : null;

    private int _mode; // 1 = matches, 2 = scan

    public override void Dispose()
    {
        s_registry.TryRemove(_id, out _);
        _scan?.Dispose();
        _store.Dispose();
    }

    private string ArgText(Mem m)
    {
        Span<byte> scratch = stackalloc byte[64];
        return Encoding.UTF8.GetString(Vm.Values.AsText(m, scratch));
    }

    internal override bool Eof => _eof;

    internal override ModStatus Filter(int indexNumber, string? indexString, ReadOnlySpan<Mem> args)
    {
        if (!_started)
        {
            _started = true;
            _spec = string.IsNullOrEmpty(indexString) ? [] : indexString.Split(',');
            for (int i = 0; i < _spec.Length; i++)
            {
                Mem a = args[i];
                switch (_spec[i][0])
                {
                    case 'M':
                        _query = a.Kind == StorageClass.Null ? null : ArgText(a);
                        _hasMatch = true;
                        break;
                    case 'C':
                        _query = a.Kind == StorageClass.Null ? null : ArgText(a);
                        _queryCol = int.Parse(_spec[i].AsSpan(1), System.Globalization.CultureInfo.InvariantCulture);
                        _hasMatch = true;
                        break;
                    case 'R':
                        _rankArg = ArgText(a);
                        break;
                    case 'E':
                        _lo = Math.Max(_lo, a.IntValue);
                        _hi = Math.Min(_hi, a.IntValue);
                        break;
                    case 'G':
                        _lo = Math.Max(_lo, a.IntValue == long.MaxValue ? a.IntValue : a.IntValue + 1);
                        if (a.IntValue == long.MaxValue)
                        {
                            _hi = long.MinValue;
                        }

                        break;
                    case 'g':
                        _lo = Math.Max(_lo, a.IntValue);
                        break;
                    case 'L':
                        _hi = Math.Min(_hi, a.IntValue == long.MinValue ? a.IntValue : a.IntValue - 1);
                        if (a.IntValue == long.MinValue)
                        {
                            _lo = long.MaxValue;
                        }

                        break;
                    case 'l':
                        _hi = Math.Min(_hi, a.IntValue);
                        break;
                }
            }

            _mode = _hasMatch ? 1 : 2;
            _sph = 0;
        }

        if (_mode == 1)
        {
            ModStatus s = _run.Drive(ComputeMatches);
            if (s == ModStatus.Pending)
            {
                return s;
            }

            _pos = 0;
            _eof = _docs.Count == 0;
            _rowLoaded = false;
            return ModStatus.Ok;
        }

        return ScanStart();
    }

    private void ComputeMatches()
    {
        _t.ApplyPersisted(_store.Config());
        Structure st = _idx.ReadStructure();
        _avg = _idx.ReadAverages();
        (QNode? root, List<QPhrase> phrases) = QueryParser.Parse(_cfg, _query ?? string.Empty, _queryCol);
        _phrases = phrases;
        var ev = new QueryEval(_cfg, _idx, st, phrases.Count);
        List<DocMatch> docs = root is null ? [] : ev.EvalRoot(root);
        if (_lo != long.MinValue || _hi != long.MaxValue)
        {
            docs = docs.Where(d => d.Rowid >= _lo && d.Rowid <= _hi).ToList();
        }

        _docs = docs;
        _phraseDocs = new long[phrases.Count];
        for (int i = 0; i < phrases.Count; i++)
        {
            _phraseDocs[i] = ev.Eval(phrases[i]).Count;
        }
    }

    // ---- full scans

    private ModStatus ScanStart()
    {
        if (_cfg.Content == ContentMode.Normal)
        {
            _scan ??= new ShadowTree(_t.Conn, _cfg.Schema, _cfg.Table + "_content");
            return ScanStep(true);
        }

        if (_cfg.Content == ContentMode.External)
        {
            _scanIds = [.. _t.Ext!.All().Select(r => r.Rowid).Where(r => r >= _lo && r <= _hi)];
        }
        else if (_cfg.HasDocSize)
        {
            ModStatus s = _run.Drive(() =>
            {
                _store.NeedDocSizeRange(_lo, _hi);
                _scanIds = [.. _store.DocSizeRows(_lo, _hi).Select(kv => kv.Key)];
            });
            if (s == ModStatus.Pending)
            {
                return s;
            }
        }
        else
        {
            throw Fts5Config.Err(_cfg.Table + ": table does not support scanning");
        }

        _pos = 0;
        _eof = _scanIds.Count == 0;
        _rowLoaded = false;
        return ModStatus.Ok;
    }

    private ModStatus ScanStep(bool first)
    {
        ShadowTree t = _scan!;
        for (; ; )
        {
            switch (_sph)
            {
                case 0:
                {
                    ModStatus s = t.Find(_lo, out bool found);
                    if (s == ModStatus.Pending)
                    {
                        return s;
                    }

                    _sph = found ? 2 : 1;
                    break;
                }

                case 1:
                {
                    if (t.Valid && t.Rowid < _lo)
                    {
                        ModStatus s = t.Advance();
                        if (s == ModStatus.Pending)
                        {
                            return s;
                        }
                    }

                    _sph = 2;
                    break;
                }

                case 2:
                {
                    if (!t.Valid || t.Rowid > _hi)
                    {
                        _eof = true;
                        _sph = 9;
                        return ModStatus.Ok;
                    }

                    ModStatus s = t.Load();
                    if (s == ModStatus.Pending)
                    {
                        return s;
                    }

                    var row = new Mem[Math.Max(0, t.Row.ColumnCount - 1)];
                    for (int i = 1; i < t.Row.ColumnCount; i++)
                    {
                        Mem m = default;
                        t.Row.Load(i, ref m);
                        if (m.Buffer is not null)
                        {
                            m.Buffer = m.Bytes.ToArray();
                            m.Offset = 0;
                        }

                        row[i - 1] = m;
                    }

                    _row = row;
                    _rowLoaded = true;
                    _eof = false;
                    _sph = 3;
                    return ModStatus.Ok;
                }

                default:
                {
                    // 3: standing on a delivered row; Next moves on
                    return ModStatus.Ok;
                }
            }
        }
    }

    internal override ModStatus Next()
    {
        _rowLoaded = false;
        _sizes = null;
        if (_mode == 1)
        {
            _pos++;
            _eof = _pos >= _docs.Count;
            return ModStatus.Ok;
        }

        if (_cfg.Content == ContentMode.Normal)
        {
            if (_sph == 3)
            {
                _sph = 4;
            }

            if (_sph == 4)
            {
                ModStatus s = _scan!.Advance();
                if (s == ModStatus.Pending)
                {
                    return s;
                }

                _sph = 2;
            }

            return ScanStep(false);
        }

        _pos++;
        _eof = _pos >= _scanIds.Count;
        return ModStatus.Ok;
    }

    internal override ModStatus RowId(out long rowid)
    {
        rowid = _mode == 1 ? _docs[_pos].Rowid : _cfg.Content == ContentMode.Normal ? _scan!.Rowid : _scanIds[_pos];
        return ModStatus.Ok;
    }

    private long CurId => _mode == 1 ? _docs[_pos].Rowid : _cfg.Content == ContentMode.Normal ? _scan!.Rowid : _scanIds[_pos];

    // ---- columns

    private ModStatus LoadRow()
    {
        if (_rowLoaded)
        {
            return ModStatus.Ok;
        }

        long id = CurId;
        switch (_cfg.Content)
        {
            case ContentMode.Normal:
            case ContentMode.None when _cfg.ContentlessUnindexed:
            {
                ModStatus s = _run.Drive(() => _row = _store.Content(id));
                if (s == ModStatus.Pending)
                {
                    return s;
                }

                break;
            }

            case ContentMode.External:
                _row = _t.Ext!.Get(id);
                break;
            default:
                _row = null;
                break;
        }

        _rowLoaded = true;
        return ModStatus.Ok;
    }

    private void SetMem(Mem m, ref Mem dst, ref byte[]? own)
    {
        switch (m.Kind)
        {
            case StorageClass.Integral:
                Vm.Values.SetInt(ref dst, m.IntValue);
                break;
            case StorageClass.Real:
                Vm.Values.SetReal(ref dst, m.RealValue);
                break;
            case StorageClass.Text:
                Vm.Values.SetBytes(ref dst, ref own, StorageClass.Text, _store.Enc == TextEncoding.Utf8 ? m.Bytes : TextCodec.ToUtf8(m.Bytes, _store.Enc));
                break;
            case StorageClass.Blob:
                Vm.Values.SetBytes(ref dst, ref own, StorageClass.Blob, m.Bytes);
                break;
            default:
                Vm.Values.SetNull(ref dst);
                break;
        }
    }

    internal override ModStatus Column(int index, ref Mem dst, ref byte[]? own)
    {
        int n = _cfg.ColumnCount;
        if (index < n)
        {
            ModStatus s = LoadRow();
            if (s == ModStatus.Pending)
            {
                return s;
            }

            int ri = index;
            if (_cfg.Content == ContentMode.None)
            {
                ri = _cfg.ContentlessUnindexed && _cfg.Unindexed[index] ? _cfg.Unindexed.Take(index).Count(u => u) : -1;
            }

            if (_row is null || ri < 0 || ri >= _row.Length)
            {
                Vm.Values.SetNull(ref dst);
            }
            else
            {
                SetMem(_row[ri], ref dst, ref own);
            }

            return ModStatus.Ok;
        }

        if (index == n)
        {
            ModStatus s = Prepare();
            if (s == ModStatus.Pending)
            {
                return s;
            }

            Vm.Values.SetBytes(ref dst, ref own, StorageClass.Blob, BitConverter.GetBytes(_id));
            dst.Subtype = RefSubtype;
            return ModStatus.Ok;
        }

        // rank
        if (_mode != 1)
        {
            Vm.Values.SetNull(ref dst);
            return ModStatus.Ok;
        }

        ModStatus ps = Prepare();
        if (ps == ModStatus.Pending)
        {
            return ps;
        }

        Vm.Values.SetReal(ref dst, Fts5Aux.RankValue(this, _rankArg ?? _cfg.Rank));
        return ModStatus.Ok;
    }

    /// <summary>Loads what an auxiliary function needs from the current row: its text and column sizes.</summary>
    private ModStatus Prepare()
    {
        if (_mode != 1)
        {
            return ModStatus.Ok;
        }

        ModStatus s = LoadRow();
        if (s != ModStatus.Ok)
        {
            return s;
        }

        if (_sizes is not null)
        {
            return ModStatus.Ok;
        }

        long id = CurId;
        if (_cfg.HasDocSize)
        {
            long[]? sz = null;
            s = _run.Drive(() =>
            {
                byte[]? b = _store.DocSize(id).Sz;
                if (b is not null)
                {
                    sz = new long[_cfg.ColumnCount];
                    int p = 0;
                    for (int i = 0; i < sz.Length && p < b.Length; i++)
                    {
                        sz[i] = F5.GetVarint(b, ref p);
                    }
                }
            });
            if (s == ModStatus.Pending)
            {
                return s;
            }

            _sizes = sz ?? new long[_cfg.ColumnCount];
        }
        else if (_row is not null)
        {
            _sizes = _idx.Tokenize(_row, _store.Enc).ColSizes;
        }
        else
        {
            // a contentless table without size records: the reference's lookup fails and every column size reads as -1 (observed:
            // bm25 then equals the formula with a document length of -1 per column)
            // (unindexed columns have no size and add nothing)
            _sizes = new long[_cfg.ColumnCount];
            for (int i = 0; i < _sizes.Length; i++)
            {
                _sizes[i] = _cfg.Unindexed[i] ? 0 : -1L;
            }
        }

        return ModStatus.Ok;
    }

    // ---- what the auxiliary functions read

    internal List<QPhrase> Phrases => _phrases;

    internal long[] PhraseDocs => _phraseDocs;

    internal long[] Averages => _avg;

    internal long[] Sizes => _sizes ?? new long[_cfg.ColumnCount];

    internal TextEncoding Enc => _store.Enc;

    /// <summary>The UTF-8 text of a column of the current row, or null.</summary>
    internal byte[]? ColumnText(int col)
    {
        if (_row is null)
        {
            return null;
        }

        int ri = col;
        if (_cfg.Content == ContentMode.None)
        {
            return null;
        }

        if (ri >= _row.Length || _row[ri].Kind == StorageClass.Null)
        {
            return null;
        }

        Span<byte> scratch = stackalloc byte[64];
        return Fts5Index.TextOf(_row[ri], _store.Enc, scratch).ToArray();
    }
}
