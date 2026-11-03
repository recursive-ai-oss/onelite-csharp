using System.Linq;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Planning;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.VirtualTables;

namespace RecursiveAI.OneLite.Extensions.Fts5;

internal enum FetchKind : byte
{
    Data,
    Content,
    DocSize,
    Config,
    MaxRowid,

    /// <summary>The pages a segment search for <see cref="Fetch.Key"/> probes, and the page it starts on (with one page of read-ahead).</summary>
    SegSeek,
}

/// <summary>A request for rows of a shadow table that the computation needs and that are not loaded yet.</summary>
internal readonly record struct Fetch(FetchKind Kind, long Lo, long Hi, Seg? Seg = null, byte[]? Key = null, bool Point = false, List<(Seg Seg, byte[] Key)>? Many = null);

/// <summary>Thrown by the store when a computation reads something that has not been loaded; the driver loads it and runs the computation again.</summary>
internal sealed class NeedFetch(Fetch f) : Exception("fts5: data not loaded")
{
    internal Fetch Request { get; } = f;
}

internal enum OpKind : byte
{
    PutData,
    DelDataRange,
    PutIdx,
    DelIdxSeg,
    PutDocSize,
    DelDocSize,
    PutContent,
    DelContent,
    PutConfig,
}

/// <summary>One change to a shadow table, applied after the whole change has been worked out.</summary>
internal sealed class WriteOp
{
    internal OpKind Kind;
    internal long Id;
    internal long Id2;
    internal byte[]? Blob;
    internal Mem[]? Row;
    internal string? Name;
    internal long Extra;
}

/// <summary>
/// Reads and writes the shadow tables of one fts5 table. Reads are served from memory; a read of something not loaded throws
/// <see cref="NeedFetch"/>, the driver calls <see cref="Satisfy"/> (which may return Pending and is then called again) and the computation
/// restarts. Writes are collected as <see cref="WriteOp"/>s and applied with <see cref="Apply"/>. This keeps every page access restartable
/// while the index code itself stays ordinary sequential code.
/// </summary>
internal sealed class Fts5Store : IBlobSource
{
    private readonly Connection _conn;
    private readonly Fts5Config _cfg;
    private readonly Dictionary<long, byte[]?> _data = [];
    private readonly List<(long Lo, long Hi)> _dataCovered = [];
    private readonly Dictionary<long, Mem[]?> _content = [];
    private readonly List<(long Lo, long Hi)> _contentCovered = [];
    private readonly Dictionary<long, (byte[]? Sz, long Origin)> _docsize = [];
    private readonly List<(long Lo, long Hi)> _docsizeCovered = [];
    private Dictionary<string, Mem>? _config;
    private readonly Dictionary<long, byte[]?> _ovData = [];
    private readonly List<(long Lo, long Hi)> _ovRemoved = [];
    private long? _maxRowid;

    internal Fts5Store(Connection conn, Fts5Config cfg)
    {
        _conn = conn;
        _cfg = cfg;
    }

    internal TextEncoding Enc => _conn.ContextOf(Db).Encoding;

    internal int Db { get; set; }

    internal void Clear()
    {
        _data.Clear();
        _dataCovered.Clear();
        _content.Clear();
        _contentCovered.Clear();
        _docsize.Clear();
        _docsizeCovered.Clear();
        _seeked.Clear();
        _pages.Clear();
        _readAhead = 0;
        _config = null;
        _maxRowid = null;
        BeginPlan();
    }

    /// <summary>Starts a planning attempt: changes the plan makes to %_data are visible to its own reads but never reach the loaded rows.</summary>
    internal void BeginPlan()
    {
        _ovData.Clear();
        _ovRemoved.Clear();
    }

    /// <summary>The largest rowid of the table's own row store (content, else docsize).</summary>
    internal long MaxRowid()
    {
        if (_maxRowid is { } m)
        {
            return m;
        }

        throw new NeedFetch(new Fetch(FetchKind.MaxRowid, 0, 0));
    }

    private static bool Covered(List<(long Lo, long Hi)> l, long a, long b)
    {
        foreach ((long lo, long hi) in l)
        {
            if (lo <= a && b <= hi)
            {
                return true;
            }
        }

        return false;
    }

    // ---- reads (throwing)

    public byte[]? Data(long id)
    {
        if (TryData(id, out byte[]? v))
        {
            return v;
        }

        // A miss right after a loaded page is a sequential read (a long doclist): ask for a growing run of pages so the number of
        // restarts stays logarithmic in the doclist length. Any other miss loads just that page.
        long hi = id;
        if (_data.ContainsKey(id - 1))
        {
            _readAhead = _readAhead == 0 ? 2 : Math.Min(_readAhead * 2, 32);
            hi = id + _readAhead - 1;
        }

        throw new NeedFetch(new Fetch(FetchKind.Data, id, hi, Point: hi == id));
    }

    private int _readAhead;

    private readonly Dictionary<long, SegPage> _pages = [];

    public bool TryPage(int segid, int pgno, out SegPage? page)
    {
        long id = F5.DataId(segid, pgno);
        bool overlay = _ovData.Count > 0 || _ovRemoved.Count > 0; // a plan in progress changes what a page reads as: never cache then
        if (!overlay && _pages.TryGetValue(id, out page))
        {
            return true;
        }

        if (!TryData(id, out byte[]? b))
        {
            return false;
        }

        if (b is null)
        {
            page = null;
            return true;
        }

        page = SegPage.Parse(b, segid, pgno);
        if (!overlay)
        {
            _pages[id] = page; // parsed pages are kept with the loaded data, so repeated searches do not parse them again
        }

        return true;
    }

    public bool TryData(long id, out byte[]? value)
    {
        if (_ovData.TryGetValue(id, out value))
        {
            return true;
        }

        foreach ((long lo, long hi) in _ovRemoved)
        {
            if (id >= lo && id <= hi)
            {
                value = null;
                return true;
            }
        }

        if (_data.TryGetValue(id, out value))
        {
            return true;
        }

        if (Covered(_dataCovered, id, id))
        {
            value = null;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Notes that a search of segment <paramref name="g"/> for <paramref name="key"/> is coming. When its first pages (the binary search's
    /// probes, the start page and the one after it) are not loaded they are added to <paramref name="need"/>. The rest of a doclist loads
    /// as it is read.
    /// </summary>
    internal void CollectSeek(Seg g, byte[] key, List<(Seg Seg, byte[] Key)> need)
    {
        _readAhead = 0;
        if (_ovData.Count > 0 || _ovRemoved.Count > 0 || _seeked.Contains((g.Id, key, g.First, g.Last)))
        {
            return;
        }

        if (!SeekReady(g, key, out _))
        {
            need.Add((g, key));
        }
        else
        {
            _seeked.Add((g.Id, key, g.First, g.Last));
        }
    }

    /// <summary>Signals the fetch of everything <see cref="CollectSeek"/> found missing (one restart for all of them).</summary>
    internal static void FetchSeeks(List<(Seg Seg, byte[] Key)> need)
    {
        if (need.Count > 0)
        {
            throw new NeedFetch(new Fetch(FetchKind.SegSeek, 0, 0, Many: need));
        }
    }

    private readonly HashSet<(int, byte[], int, int)> _seeked = new(new SeekKeyCmp());

    private sealed class SeekKeyCmp : IEqualityComparer<(int, byte[], int, int)>
    {
        public bool Equals((int, byte[], int, int) x, (int, byte[], int, int) y) =>
            x.Item1 == y.Item1 && x.Item3 == y.Item3 && x.Item4 == y.Item4 && x.Item2.AsSpan().SequenceEqual(y.Item2);

        public int GetHashCode((int, byte[], int, int) o)
        {
            var h = new HashCode();
            h.Add(o.Item1);
            h.AddBytes(o.Item2);
            return h.ToHashCode();
        }
    }

    /// <summary>True when everything the seek preparation wants is loaded; otherwise the id of the page to load next.</summary>
    private bool SeekReady(Seg g, byte[] key, out long missing)
    {
        missing = 0;
        if (g.First == 0 && g.Last == 0)
        {
            return TryData(F5.DataId(g.Id, 0), out _) || Miss(F5.DataId(g.Id, 0), out missing);
        }

        int best = SegScanner.FindStartPage(this, g, key, out long m);
        if (best < 0)
        {
            missing = m;
            return false;
        }

        if (best < g.Last)
        {
            long next = F5.DataId(g.Id, best + 1);
            if (!TryData(next, out _))
            {
                missing = next;
                return false;
            }
        }

        return true;
    }

    private static bool Miss(long id, out long missing)
    {
        missing = id;
        return false;
    }

    /// <summary>Makes sure rows lo..hi of %_data are loaded.</summary>
    internal void NeedDataRange(long lo, long hi)
    {
        if (_ovData.ContainsKey(lo))
        {
            return;
        }

        if (!Covered(_dataCovered, lo, hi))
        {
            throw new NeedFetch(new Fetch(FetchKind.Data, lo, hi));
        }
    }

    internal Mem[]? Content(long id)
    {
        if (_content.TryGetValue(id, out Mem[]? v))
        {
            return v;
        }

        if (Covered(_contentCovered, id, id))
        {
            return null;
        }

        throw new NeedFetch(new Fetch(FetchKind.Content, id, id));
    }

    internal void NeedContentRange(long lo, long hi)
    {
        if (!Covered(_contentCovered, lo, hi))
        {
            throw new NeedFetch(new Fetch(FetchKind.Content, lo, hi));
        }
    }

    /// <summary>The loaded content rows with rowid in lo..hi, in order.</summary>
    internal IEnumerable<KeyValuePair<long, Mem[]?>> ContentRows(long lo, long hi) => _content.Where(kv => kv.Key >= lo && kv.Key <= hi).OrderBy(kv => kv.Key);

    internal (byte[]? Sz, long Origin) DocSize(long id)
    {
        if (_docsize.TryGetValue(id, out var v))
        {
            return v;
        }

        if (Covered(_docsizeCovered, id, id))
        {
            return (null, 0);
        }

        throw new NeedFetch(new Fetch(FetchKind.DocSize, id, id));
    }

    /// <summary>The docsize record of a row if it is loaded (<paramref name="sz"/> null when the row has none); false when it must be fetched.</summary>
    internal bool TryDocSize(long id, out byte[]? sz)
    {
        if (_docsize.TryGetValue(id, out var v))
        {
            sz = v.Sz;
            return true;
        }

        sz = null;
        return Covered(_docsizeCovered, id, id);
    }

    internal void NeedDocSizeRange(long lo, long hi)
    {
        if (!Covered(_docsizeCovered, lo, hi))
        {
            throw new NeedFetch(new Fetch(FetchKind.DocSize, lo, hi));
        }
    }

    internal IEnumerable<KeyValuePair<long, (byte[]? Sz, long Origin)>> DocSizeRows(long lo, long hi) => _docsize.Where(kv => kv.Key >= lo && kv.Key <= hi).OrderBy(kv => kv.Key);

    internal IReadOnlyDictionary<string, Mem> Config()
    {
        if (_config is null)
        {
            throw new NeedFetch(new Fetch(FetchKind.Config, 0, 0));
        }

        return _config;
    }

    // ---- write-through helpers used while planning, so later reads in the same computation see the change

    internal void SetData(long id, byte[]? v)
    {
        _ovData[id] = v;
    }

    internal void RemoveDataRange(long lo, long hi)
    {
        foreach (long k in _ovData.Keys.Where(k => k >= lo && k <= hi).ToArray())
        {
            _ovData.Remove(k);
        }

        _ovRemoved.Add((lo, hi));
    }

    // ---- satisfying a fetch

    private ShadowTree? _tree;
    private string? _treeSuffix;
    private BtCursor? _icur;
    private int _ph;
    private bool _found;

    private ModStatus Open(string suffix, out ShadowTree tree)
    {
        if (_tree is not null && _treeSuffix != suffix)
        {
            _tree.Dispose();
            _tree = null;
        }

        _treeSuffix = suffix;
        _tree ??= new ShadowTree(_conn, _cfg.Schema, _cfg.Table + suffix);
        tree = _tree;
        return ModStatus.Ok;
    }

    private void Done()
    {
        _tree?.Dispose();
        _tree = null;
        _icur?.Dispose();
        _icur = null;
        _ph = 0;
    }

    /// <summary>Loads what <paramref name="f"/> asks for. Returns Pending when a page is missing; call again with the same request.</summary>
    internal ModStatus Satisfy(Fetch f)
    {
        if (f.Kind == FetchKind.Config)
        {
            return LoadConfig();
        }

        if (f.Kind == FetchKind.MaxRowid)
        {
            return LoadMax();
        }

        if (f.Kind == FetchKind.SegSeek)
        {
            // Load one missing page at a time and look again: each look is a pure memory search over the pages loaded so far.
            foreach ((Seg g, byte[] key) in f.Many!)
            {
                while (!SeekReady(g, key, out long missing))
                {
                    ModStatus s = Satisfy(new Fetch(FetchKind.Data, missing, missing, Point: true));
                    if (s == ModStatus.Pending)
                    {
                        return s;
                    }
                }
            }

            return ModStatus.Ok;
        }

        string suffix = f.Kind switch { FetchKind.Data => "_data", FetchKind.Content => "_content", _ => "_docsize" };
        if (f.Kind == FetchKind.DocSize && !_cfg.HasDocSize || f.Kind == FetchKind.Content && !_cfg.HasContentTable)
        {
            MarkCovered(f);
            return ModStatus.Ok;
        }

        Open(suffix, out ShadowTree t);
        for (; ; )
        {
            switch (_ph)
            {
                case 0:
                {
                    ModStatus s = t.Find(f.Lo, out bool found);
                    if (s == ModStatus.Pending)
                    {
                        return s;
                    }

                    _found = found;
                    _ph = found ? 2 : 1;
                    break;
                }

                case 1:
                {
                    // standing on the neighbour of lo: step forward when it is before lo
                    if (t.Valid && t.Rowid < f.Lo)
                    {
                        ModStatus s = t.Advance();
                        if (s == ModStatus.Pending)
                        {
                            return s;
                        }
                    }

                    _ph = 2;
                    break;
                }

                case 2:
                {
                    if (!t.Valid || t.Rowid > f.Hi)
                    {
                        MarkCovered(f);
                        Done();
                        return ModStatus.Ok;
                    }

                    ModStatus s = t.Load();
                    if (s == ModStatus.Pending)
                    {
                        return s;
                    }

                    Store(f.Kind, t.Rowid, t.Row);
                    if (f.Point)
                    {
                        // a single record: done, and the tree stays open for the next lookup (a per-row fetch would otherwise reopen it)
                        _ph = 0;
                        return ModStatus.Ok;
                    }

                    _ph = 3;
                    break;
                }

                default:
                {
                    ModStatus s = t.Advance();
                    if (s == ModStatus.Pending)
                    {
                        return s;
                    }

                    _ph = 2;
                    break;
                }
            }
        }
    }

    private ModStatus LoadMax()
    {
        string suffix = _cfg.HasContentTable ? "_content" : _cfg.HasDocSize ? "_docsize" : "_data";
        Open(suffix, out ShadowTree t);
        ModStatus s = t.Last();
        if (s == ModStatus.Pending)
        {
            return s;
        }

        _maxRowid = suffix == "_data" ? 0 : t.Valid ? t.Rowid : 0;
        Done();
        return ModStatus.Ok;
    }

    private void MarkCovered(Fetch f)
    {
        switch (f.Kind)
        {
            case FetchKind.Data:
                _dataCovered.Add((f.Lo, f.Hi));
                break;
            case FetchKind.Content:
                _contentCovered.Add((f.Lo, f.Hi));
                break;
            default:
                _docsizeCovered.Add((f.Lo, f.Hi));
                break;
        }
    }

    private static byte[]? Blob(RecordView v, int i)
    {
        Mem m = default;
        v.Load(i, ref m);
        return m.Kind == StorageClass.Blob || m.Kind == StorageClass.Text ? m.Bytes.ToArray() : null;
    }

    private void Store(FetchKind k, long rowid, RecordView v)
    {
        switch (k)
        {
            case FetchKind.Data:
                _pages.Remove(rowid);
                _data[rowid] = Blob(v, 1) ?? [];
                break;
            case FetchKind.DocSize:
            {
                Mem o = default;
                if (v.ColumnCount > 2)
                {
                    v.Load(2, ref o);
                }

                _docsize[rowid] = (Blob(v, 1) ?? [], o.Kind == StorageClass.Integral ? o.IntValue : 0);
                break;
            }

            default:
            {
                var row = new Mem[Math.Max(0, v.ColumnCount - 1)];
                for (int i = 1; i < v.ColumnCount; i++)
                {
                    Mem m = default;
                    v.Load(i, ref m);
                    if (m.Buffer is not null)
                    {
                        m.Buffer = m.Bytes.ToArray();
                        m.Offset = 0;
                    }

                    row[i - 1] = m;
                }

                _content[rowid] = row;
                break;
            }
        }
    }

    private BtCursor OpenIndex(string suffix, int keyCols)
    {
        Catalog cat = _conn.Catalog ?? throw new OneLiteException(ResultCode.Corrupt, "database disk image is malformed", "no schema for shadow table");
        TableInfo t = cat.FindIn(_cfg.Schema, _cfg.Table + suffix) ?? throw new OneLiteException(ResultCode.Error, "no such table: " + _cfg.Table + suffix);
        BtContext ctx = _conn.ContextOf(t.Db);
        return BTree.Index(ctx, t.Root, KeyInfo.Binary(ctx.Encoding, keyCols)).OpenCursor();
    }

    private static ModStatus Chk(BtStatus s, BtCursor c)
    {
        if (s == BtStatus.Error)
        {
            throw Vm.Machine.Translate(c.Error ?? new InvalidOperationException("cursor failed without an error"));
        }

        return s == BtStatus.Pending ? ModStatus.Pending : ModStatus.Ok;
    }

    private ModStatus LoadConfig()
    {
        _icur ??= OpenIndex("_config", 1);
        BtCursor c = _icur;
        var view = new RecordView();
        _config ??= [];
        for (; ; )
        {
            switch (_ph)
            {
                case 0:
                {
                    if (Chk(c.First(), c) == ModStatus.Pending)
                    {
                        return ModStatus.Pending;
                    }

                    _ph = 1;
                    break;
                }

                case 1:
                {
                    if (c.State != CursorState.Valid)
                    {
                        Done();
                        return ModStatus.Ok;
                    }

                    if (Chk(c.LoadPayload(), c) == ModStatus.Pending)
                    {
                        return ModStatus.Pending;
                    }

                    if (c.ParseRecord(view) && view.ColumnCount >= 2)
                    {
                        Mem k = default;
                        Mem v = default;
                        view.Load(0, ref k);
                        view.Load(1, ref v);
                        if (v.Buffer is not null)
                        {
                            v.Buffer = v.Bytes.ToArray();
                            v.Offset = 0;
                        }

                        _config[System.Text.Encoding.UTF8.GetString(TextCodec.ToUtf8(k.Bytes, Enc))] = v;
                    }

                    _ph = 2;
                    break;
                }

                default:
                {
                    if (Chk(c.Next(), c) == ModStatus.Pending)
                    {
                        return ModStatus.Pending;
                    }

                    _ph = 1;
                    break;
                }
            }
        }
    }

    // ---- applying writes

    private int _opIdx;

    /// <summary>Applies the operations in order. Returns Pending when a page is missing; call again with the same list.</summary>
    internal ModStatus Apply(List<WriteOp> ops)
    {
        while (_opIdx < ops.Count)
        {
            ModStatus s = ApplyOne(ops[_opIdx]);
            if (s == ModStatus.Pending)
            {
                return s;
            }

            Done();
            _opIdx++;
        }

        _opIdx = 0;
        return ModStatus.Ok;
    }

    private ModStatus ApplyOne(WriteOp op)
    {
        switch (op.Kind)
        {
            case OpKind.PutData:
            {
                Open("_data", out ShadowTree t);
                return t.Put(op.Id, [default, Mem.FromBlob(op.Blob ?? [])]);
            }

            case OpKind.PutDocSize:
            {
                Open("_docsize", out ShadowTree t);
                return t.Put(op.Id, op.Row is { } r ? r : [default, Mem.FromBlob(op.Blob ?? [])]);
            }

            case OpKind.PutContent:
            {
                Open("_content", out ShadowTree t);
                var vals = new Mem[op.Row!.Length + 1];
                op.Row.CopyTo(vals, 1);
                return t.Put(op.Id, vals);
            }

            case OpKind.DelDocSize:
                return DelRows("_docsize", op.Id, op.Id2);
            case OpKind.DelContent:
                return DelRows("_content", op.Id, op.Id2);
            case OpKind.DelDataRange:
                return DelRows("_data", op.Id, op.Id2);
            case OpKind.PutIdx:
                return PutIdx(op);
            case OpKind.DelIdxSeg:
                return DelIdx(op.Id);
            case OpKind.PutConfig:
                return PutConfig(op);
            default:
                throw new InvalidOperationException("unknown fts5 write");
        }
    }

    private ModStatus DelRows(string suffix, long lo, long hi)
    {
        Open(suffix, out ShadowTree t);
        for (; ; )
        {
            switch (_ph)
            {
                case 0:
                {
                    ModStatus s = t.Find(lo, out bool found);
                    if (s == ModStatus.Pending)
                    {
                        return s;
                    }

                    _ph = found ? 2 : 1;
                    break;
                }

                case 1:
                {
                    if (t.Valid && t.Rowid < lo)
                    {
                        ModStatus s = t.Advance();
                        if (s == ModStatus.Pending)
                        {
                            return s;
                        }
                    }

                    _ph = 2;
                    break;
                }

                case 2:
                {
                    if (!t.Valid || t.Rowid > hi)
                    {
                        return ModStatus.Ok;
                    }

                    ModStatus s = t.Delete();
                    if (s == ModStatus.Pending)
                    {
                        return s;
                    }

                    _ph = 3;
                    break;
                }

                default:
                {
                    // the cursor stands on the deleted key; step to what follows it
                    ModStatus s = t.Advance();
                    if (s == ModStatus.Pending)
                    {
                        return s;
                    }

                    _ph = 2;
                    break;
                }
            }
        }
    }

    private ModStatus PutIdx(WriteOp op)
    {
        _icur ??= OpenIndex("_idx", 2);
        Mem[] key = [Mem.FromInt(op.Id), Mem.FromBlob(op.Blob ?? [])];
        byte[] rec = RecordWriter.ToArray([key[0], key[1], Mem.FromInt(op.Id2)]);
        return Chk(_icur.Insert(rec, key), _icur);
    }

    private ModStatus DelIdx(long segid)
    {
        _icur ??= OpenIndex("_idx", 2);
        BtCursor c = _icur;
        var view = new RecordView();
        Mem[] key = [Mem.FromInt(segid)];
        for (; ; )
        {
            switch (_ph)
            {
                case 0:
                {
                    if (Chk(c.MoveToKey(key, 1, out int cmp), c) == ModStatus.Pending)
                    {
                        return ModStatus.Pending;
                    }

                    _found = cmp < 0;
                    _ph = _found ? 1 : 2;
                    break;
                }

                case 1:
                {
                    if (Chk(c.Next(), c) == ModStatus.Pending)
                    {
                        return ModStatus.Pending;
                    }

                    _ph = 2;
                    break;
                }

                case 2:
                {
                    if (c.State != CursorState.Valid)
                    {
                        return ModStatus.Ok;
                    }

                    if (Chk(c.LoadPayload(), c) == ModStatus.Pending)
                    {
                        return ModStatus.Pending;
                    }

                    Mem k = default;
                    if (!c.ParseRecord(view) || view.ColumnCount < 1)
                    {
                        return ModStatus.Ok;
                    }

                    view.Load(0, ref k);
                    if (k.Kind != StorageClass.Integral || k.IntValue != segid)
                    {
                        return ModStatus.Ok;
                    }

                    _ph = 3;
                    break;
                }

                case 3:
                {
                    if (Chk(c.Delete(), c) == ModStatus.Pending)
                    {
                        return ModStatus.Pending;
                    }

                    _ph = 4;
                    break;
                }

                default:
                {
                    if (Chk(c.Next(), c) == ModStatus.Pending)
                    {
                        return ModStatus.Pending;
                    }

                    _ph = 2;
                    break;
                }
            }
        }
    }

    private ModStatus PutConfig(WriteOp op)
    {
        _icur ??= OpenIndex("_config", 1);
        BtCursor c = _icur;
        Mem k = Mem.FromText(op.Name!, Enc);
        Mem[] key = [k];
        for (; ; )
        {
            switch (_ph)
            {
                case 0:
                {
                    if (Chk(c.MoveToKey(key, 0, out int cmp), c) == ModStatus.Pending)
                    {
                        return ModStatus.Pending;
                    }

                    _ph = c.State == CursorState.Valid && cmp == 0 ? 1 : 2;
                    break;
                }

                case 1:
                {
                    if (Chk(c.Delete(), c) == ModStatus.Pending)
                    {
                        return ModStatus.Pending;
                    }

                    _ph = 2;
                    break;
                }

                default:
                {
                    Mem v = op.Row![0];
                    byte[] rec = RecordWriter.ToArray([k, v]);
                    return Chk(c.Insert(rec, key), c);
                }
            }
        }
    }

    internal void Dispose() => Done();
}
