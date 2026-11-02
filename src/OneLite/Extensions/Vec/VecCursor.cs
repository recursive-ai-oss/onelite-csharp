using System.Buffers.Binary;
using System.Globalization;
using RecursiveAI.OneLite.Internal.Simd;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Vm;
using RecursiveAI.OneLite.VirtualTables;

namespace RecursiveAI.OneLite.Extensions.Vec;

/// <summary>
/// A scan of a vec0 table. A KNN query reads every chunk (skipping chunks whose partition fails the query), measures each live vector against
/// the query and keeps the k nearest in a bounded heap ordered by (distance, rowid). Other scans walk the rowid table. Every call that can touch a
/// page returns Pending and is repeated with the same arguments; progress sits in fields.
/// </summary>
internal sealed class VecCursor : VCursor
{
    private sealed class Term
    {
        internal VecColumn? Column;      // null: the rowid
        internal ConstraintOp Op;
        internal List<Mem> Args = [];
    }

    private readonly VecTable _t;
    private readonly VecSchema _s;

    private int _mode;
    private bool _eof = true;
    private bool _started;

    // KNN
    private VecColumn _vcol = null!;
    private byte[] _query = [];
    private double _queryNorm;
    private int _k;
    private readonly List<Term> _filters = [];
    private ShadowTree? _chunks;
    private ShadowTree? _vchunk;
    private ShadowTree? _meta;
    private int _ph;
    private int _slot;
    private int _metaStep;
    private long _chunkId;
    private int _chunkSize;
    private byte[] _validity = [];
    private byte[] _chunkRowids = [];
    private bool _partitionOk;

    // The k best so far, as a binary max-heap: the root is the worst kept row.
    private double[] _hd = [];
    private long[] _hr = [];
    private long[] _hc = [];
    private int[] _ho = [];
    private int _hn;

    // Current row
    private long _rowid;
    private double _distance = double.NaN;
    private long _curChunk;
    private int _curOffset;
    private int _pos;
    private ShadowTree? _scan;
    private ShadowTree? _info;
    private long _infoFor = long.MinValue;
    private ShadowTree?[] _vc = [];
    private ShadowTree? _chk;
    private ShadowTree? _aux;
    private IdsProbe? _probe;
    private ShadowTree? _ids;

    internal VecCursor(VecTable t)
    {
        _t = t;
        _s = t.Shape;
    }

    internal override bool Eof => _eof;

    internal override ModStatus RowId(out long rowid)
    {
        rowid = _rowid;
        return ModStatus.Ok;
    }

    // ------------------------------------------------------------------ Filter

    internal override ModStatus Filter(int indexNumber, string? indexString, ReadOnlySpan<Mem> args)
    {
        if (!_started)
        {
            _started = true;
            _mode = indexNumber;
            _eof = false;
            _ph = 0;
            _hn = 0;
            _pos = 0;
            _infoFor = long.MinValue;
            _filters.Clear();
            if (indexNumber == 1)
            {
                SetUpKnn(indexString ?? string.Empty, args);
            }
            else if (indexNumber == 2)
            {
                _filters.Add(new Term { Op = ConstraintOp.Eq, Args = [VecTable.Own(args[0])] });
                _text = indexString == "T";
            }
        }

        ModStatus s = Run();
        if (s == ModStatus.Ok)
        {
            _started = false;
        }

        return s;
    }

    private bool _text;

    private void SetUpKnn(string plan, ReadOnlySpan<Mem> args)
    {
        string[] tokens = plan.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        long k = -1;
        long limit = -1;
        bool kNull = false;
        for (int i = 0; i < tokens.Length; i++)
        {
            string t = tokens[i];
            switch (t[0])
            {
                case 'M':
                {
                    _vcol = _s.Vectors[int.Parse(t.AsSpan(1), CultureInfo.InvariantCulture)];
                    _query = ReadQuery(_vcol, args[i]);
                    break;
                }

                case 'K':
                    if (args[i].Kind == StorageClass.Null)
                    {
                        kNull = true;
                    }
                    else
                    {
                        k = Values.ToInt(args[i]);
                    }

                    break;
                case 'L':
                    limit = args[i].Kind == StorageClass.Null ? -1 : Values.ToInt(args[i]);
                    break;
                default:
                {
                    int colon = t.IndexOf(':', StringComparison.Ordinal);
                    int ord = int.Parse(t.AsSpan(1, colon - 1), CultureInfo.InvariantCulture);
                    var op = (ConstraintOp)int.Parse(t.AsSpan(colon + 1), CultureInfo.InvariantCulture);
                    VecColumn? col = ord < 0 ? null : _s.Columns[ord];
                    Term? f = _filters.Find(x => x.Column == col && x.Op == op && op == ConstraintOp.In);
                    if (f is null)
                    {
                        f = new Term { Column = col, Op = op };
                        _filters.Add(f);
                    }

                    f.Args.Add(VecTable.Own(args[i]));
                    break;
                }
            }
        }

        if (k < 0 && !kNull && limit >= 0)
        {
            k = limit;
        }
        else if (k >= 0 && limit >= 0)
        {
            k = Math.Min(k, limit);
        }

        if (k < 0 && !kNull)
        {
            throw new OneLiteException(ResultCode.Error, "k value in knn queries must be greater than or equal to 0.");
        }

        if (k > VecSchema.MaxK)
        {
            throw new OneLiteException(ResultCode.Error, "k value in knn query too large, provided " + k.ToString(CultureInfo.InvariantCulture) + " and the limit is " + VecSchema.MaxK.ToString(CultureInfo.InvariantCulture));
        }

        _k = kNull ? 0 : (int)k;
        if (_vcol.Metric == VecMetric.Cosine)
        {
            _queryNorm = _vcol.Element == VecElement.Int8
                ? VectorKernels.Dot(Int8Of(_query), Int8Of(_query))
                : VectorKernels.NormSquared(VectorKernels.AsFloats(_query));
        }

        if (_hd.Length < _k)
        {
            _hd = new double[_k];
            _hr = new long[_k];
            _hc = new long[_k];
            _ho = new int[_k];
        }
    }

    private static ReadOnlySpan<sbyte> Int8Of(byte[] b) => VectorKernels.AsInt8s(b);

    private static byte[] ReadQuery(VecColumn col, in Mem arg)
    {
        string name = col.Name;
        VecValue v;
        try
        {
            v = VecValue.Read(arg);
        }
        catch (OneLiteException e)
        {
            throw new OneLiteException(ResultCode.Error, "Query vector on the \"" + name + "\" column is invalid: " + e.Message);
        }

        if (v.Type != col.Element)
        {
            throw new OneLiteException(ResultCode.Error, "Query vector for the \"" + name + "\" column is expected to be of type " + VecValue.Name(col.Element)
                + ", but a " + VecValue.Name(v.Type) + " vector was provided.");
        }

        if (v.Dimensions != col.Dimensions)
        {
            throw new OneLiteException(ResultCode.Error, "Dimension mismatch for query vector for the \"" + name + "\" column. Expected " + col.Dimensions.ToString(CultureInfo.InvariantCulture)
                + " dimensions but received " + v.Dimensions.ToString(CultureInfo.InvariantCulture) + ".");
        }

        return v.Bytes.ToArray();
    }

    // ------------------------------------------------------------------ the scan

    internal override ModStatus Next()
    {
        switch (_mode)
        {
            case 1:
                _pos++;
                LoadResult();
                return ModStatus.Ok;
            case 2:
                _eof = true;
                return ModStatus.Ok;
            default:
            {
                ModStatus s = _scan!.Advance();
                if (s != ModStatus.Ok)
                {
                    return s;
                }

                SetScanRow();
                return ModStatus.Ok;
            }
        }
    }

    private ModStatus Run() => _mode switch { 1 => RunKnn(), 2 => RunPoint(), _ => RunScan() };

    private void SetScanRow()
    {
        if (_scan!.Valid)
        {
            _rowid = _scan.Rowid;
            _eof = false;
        }
        else
        {
            _eof = true;
        }

        _infoFor = long.MinValue;
    }

    private ModStatus RunScan()
    {
        _scan ??= _t.OpenRowids();
        ModStatus s = _scan.First();
        if (s != ModStatus.Ok)
        {
            return s;
        }

        SetScanRow();
        return ModStatus.Ok;
    }

    private ModStatus RunPoint()
    {
        Mem key = _filters[0].Args[0];
        _scan ??= _t.OpenRowids();
        long rowid;
        if (_text)
        {
            if (key.Kind != StorageClass.Text)
            {
                _eof = true;
                return ModStatus.Ok;
            }

            _ids ??= _t.OpenIds();
            _probe ??= new IdsProbe();
            ModStatus ps = _probe.Run(_ids, key.Bytes, out bool hit, out long vr, out _);
            if (ps != ModStatus.Ok)
            {
                return ps;
            }

            if (!hit)
            {
                _eof = true;
                return ModStatus.Ok;
            }

            rowid = vr;
        }
        else
        {
            if (key.Kind == StorageClass.Null)
            {
                _eof = true;
                return ModStatus.Ok;
            }

            StorageClass cls = Values.ToNumeric(key, out long iv, out double dv);
            if (cls != StorageClass.Integral && !(cls == StorageClass.Real && dv == Math.Floor(dv) && Math.Abs(dv) < 9e18))
            {
                _eof = true;
                return ModStatus.Ok;
            }

            rowid = cls == StorageClass.Integral ? iv : (long)dv;
        }

        ModStatus s = _scan.Find(rowid, out bool found);
        if (s != ModStatus.Ok)
        {
            return s;
        }

        _eof = !found;
        _rowid = rowid;
        _infoFor = long.MinValue;
        return ModStatus.Ok;
    }

    private bool FilterRowid(long rowid)
    {
        foreach (Term f in _filters)
        {
            if (f.Column is not null && f.Column.Kind != VecColumnKind.Primary)
            {
                continue;
            }

            if (f.Column is { Type: VecScalarType.Text })
            {
                continue;    // a text key is checked through the key table when the row is returned
            }

            bool any = false;
            foreach (Mem a in f.Args)
            {
                if (a.Kind == StorageClass.Null)
                {
                    continue;
                }

                StorageClass cls = Values.ToNumeric(a, out long iv, out double dv);
                if ((cls == StorageClass.Integral && iv == rowid) || (cls == StorageClass.Real && dv == rowid))
                {
                    any = true;
                    break;
                }
            }

            if (!any)
            {
                return false;
            }
        }

        return true;
    }

    private bool PartitionPasses(RecordView chunkRow)
    {
        foreach (Term f in _filters)
        {
            if (f.Column is not { Kind: VecColumnKind.Partition } col)
            {
                continue;
            }

            Mem stored = default;
            chunkRow.Load(2 + col.Slot, ref stored);
            bool any = false;
            foreach (Mem a in f.Args)
            {
                if (VecTable.Matches(col, stored, f.Op == ConstraintOp.In ? ConstraintOp.Eq : f.Op, a))
                {
                    any = true;
                    break;
                }
            }

            if (!any)
            {
                return false;
            }
        }

        return true;
    }

    private bool HasMetadataFilter()
    {
        foreach (Term f in _filters)
        {
            if (f.Column is { Kind: VecColumnKind.Metadata } || f.Column is { Kind: VecColumnKind.Primary, Type: VecScalarType.Text })
            {
                return true;
            }
        }

        return false;
    }

    private bool MetadataPasses(RecordView row)
    {
        foreach (Term f in _filters)
        {
            if (f.Column is not { Kind: VecColumnKind.Metadata } col)
            {
                continue;
            }

            Mem stored = default;
            if (VecTable.RowidsFirstMetadataCol + col.Slot < row.ColumnCount)
            {
                row.Load(VecTable.RowidsFirstMetadataCol + col.Slot, ref stored);
            }

            bool any = false;
            foreach (Mem a in f.Args)
            {
                if (VecTable.Matches(col, stored, f.Op == ConstraintOp.In ? ConstraintOp.Eq : f.Op, a))
                {
                    any = true;
                    break;
                }
            }

            if (!any)
            {
                return false;
            }
        }

        return true;
    }

    private bool TextKeyPasses(RecordView row)
    {
        foreach (Term f in _filters)
        {
            if (f.Column is not { Kind: VecColumnKind.Primary, Type: VecScalarType.Text })
            {
                continue;
            }

            Mem stored = default;
            row.Load(VecTable.RowidsIdCol, ref stored);
            bool any = false;
            foreach (Mem a in f.Args)
            {
                if (a.Kind == StorageClass.Text && stored.Kind == StorageClass.Text && VecTable.ToUtf8(stored.Bytes).AsSpan().SequenceEqual(a.Bytes))
                {
                    any = true;
                    break;
                }
            }

            if (!any)
            {
                return false;
            }
        }

        return true;
    }

    private ModStatus RunKnn()
    {
        if (_k == 0)
        {
            _hn = 0;
            _eof = true;
            return ModStatus.Ok;
        }

        _chunks ??= _t.OpenChunks();
        _vchunk ??= _t.OpenVectors(_vcol.Slot);
        bool needMeta = HasMetadataFilter();
        if (needMeta)
        {
            _meta ??= _t.OpenRowids();
        }

        for (; ; )
        {
            ModStatus s;
            switch (_ph)
            {
                case 0:
                    s = _chunks.First();
                    if (s != ModStatus.Ok)
                    {
                        return s;
                    }

                    _ph = 1;
                    break;
                case 1:
                {
                    if (!_chunks.Valid)
                    {
                        _ph = 9;
                        break;
                    }

                    s = _chunks.Load();
                    if (s != ModStatus.Ok)
                    {
                        return s;
                    }

                    _chunkId = _chunks.Rowid;
                    Mem sz = default;
                    _chunks.Row.Load(VecTable.ChunkSizeCol, ref sz);
                    _chunkSize = (int)sz.IntValue;
                    _partitionOk = PartitionPasses(_chunks.Row);
                    if (!_partitionOk)
                    {
                        _ph = 8;
                        break;
                    }

                    Mem val = default, rid = default;
                    _chunks.Row.Load(_t.ValidityCol, ref val);
                    _chunks.Row.Load(_t.ChunkRowidsCol, ref rid);
                    if (_chunkSize <= 0 || _chunkSize > 1 << 20 || val.Length < (_chunkSize + 7) / 8 || rid.Length < _chunkSize * 8)
                    {
                        throw VecTable.Corrupt("chunk " + _chunkId.ToString(CultureInfo.InvariantCulture) + " of " + _s.Table + " is malformed");
                    }

                    if (_validity.Length < val.Length)
                    {
                        _validity = new byte[val.Length];
                    }

                    if (_chunkRowids.Length < rid.Length)
                    {
                        _chunkRowids = new byte[rid.Length];
                    }

                    val.Bytes.CopyTo(_validity);
                    rid.Bytes.CopyTo(_chunkRowids);
                    _ph = 2;
                    break;
                }

                case 2:
                    s = _vchunk.Find(_chunkId, out bool found);
                    if (s != ModStatus.Ok)
                    {
                        return s;
                    }

                    if (!found)
                    {
                        throw VecTable.Corrupt("vector chunk " + _chunkId.ToString(CultureInfo.InvariantCulture) + " of " + _s.Table + " is missing");
                    }

                    s = _vchunk.Load();
                    if (s != ModStatus.Ok)
                    {
                        return s;
                    }

                    _slot = 0;
                    _metaStep = 0;
                    _ph = 3;
                    break;
                case 3:
                    s = ScanSlots(needMeta);
                    if (s != ModStatus.Ok)
                    {
                        return s;
                    }

                    _ph = 8;
                    break;
                case 8:
                    s = _chunks.Advance();
                    if (s != ModStatus.Ok)
                    {
                        return s;
                    }

                    _ph = 1;
                    break;
                default:
                    SortResults();
                    _pos = 0;
                    _ph = 10;
                    LoadResult();
                    return ModStatus.Ok;
            }
        }
    }

    private ModStatus ScanSlots(bool needMeta)
    {
        Mem blob = default;
        _vchunk!.Row.Load(1, ref blob);
        int bytes = _vcol.VectorBytes;
        if (blob.Length < _chunkSize * bytes)
        {
            throw VecTable.Corrupt("vector chunk " + _chunkId.ToString(CultureInfo.InvariantCulture) + " of " + _s.Table + " is too short");
        }

        while (_slot < _chunkSize)
        {
            int slot = _slot;
            if ((_validity[slot >> 3] & (1 << (slot & 7))) == 0)
            {
                _slot++;
                continue;
            }

            long rowid = BinaryPrimitives.ReadInt64LittleEndian(_chunkRowids.AsSpan(slot * 8));
            if (!FilterRowid(rowid))
            {
                _slot++;
                continue;
            }

            if (needMeta)
            {
                if (_metaStep == 0)
                {
                    ModStatus s = _meta!.Find(rowid, out bool f);
                    if (s != ModStatus.Ok)
                    {
                        return s;
                    }

                    if (!f)
                    {
                        _slot++;
                        continue;
                    }

                    _metaStep = 1;
                }

                ModStatus ls = _meta!.Load();
                if (ls != ModStatus.Ok)
                {
                    return ls;
                }

                _metaStep = 0;
                if (!MetadataPasses(_meta.Row) || !TextKeyPasses(_meta.Row))
                {
                    _slot++;
                    continue;
                }
            }

            Mem fresh = default;
            _vchunk.Row.Load(1, ref fresh);
            double d = Measure(fresh.Bytes.Slice(slot * bytes, bytes));
            Offer(d, rowid, _chunkId, slot);
            _slot++;
        }

        return ModStatus.Ok;
    }

    private double Measure(ReadOnlySpan<byte> v)
    {
        switch (_vcol.Element)
        {
            case VecElement.Float32:
            {
                ReadOnlySpan<float> a = VectorKernels.AsFloats(_query);
                ReadOnlySpan<float> b = VectorKernels.AsFloats(v);
                switch (_vcol.Metric)
                {
                    case VecMetric.L1:
                        return VectorKernels.L1(a, b);
                    case VecMetric.Cosine:
                    {
                        double nb = VectorKernels.NormSquared(b);
                        if (_queryNorm == 0 || nb == 0)
                        {
                            return double.NaN;
                        }

                        return 1.0 - (VectorKernels.Dot(a, b) / (Math.Sqrt(_queryNorm) * Math.Sqrt(nb)));
                    }

                    default:
                        return Math.Sqrt(VectorKernels.L2Squared(a, b));
                }
            }

            case VecElement.Int8:
            {
                ReadOnlySpan<sbyte> a = Int8Of(_query);
                ReadOnlySpan<sbyte> b = VectorKernels.AsInt8s(v);
                switch (_vcol.Metric)
                {
                    case VecMetric.L1:
                        return VectorKernels.L1(a, b);
                    case VecMetric.Cosine:
                    {
                        double nb = VectorKernels.Dot(b, b);
                        if (_queryNorm == 0 || nb == 0)
                        {
                            return double.NaN;
                        }

                        return 1.0 - (VectorKernels.Dot(a, b) / (Math.Sqrt(_queryNorm) * Math.Sqrt(nb)));
                    }

                    default:
                        return Math.Sqrt(VectorKernels.L2Squared(a, b));
                }
            }

            default:
                return VectorKernels.Hamming(_query, v);
        }
    }

    // ------------------------------------------------------------------ the heap

    /// <summary>True when (d1, r1) ranks after (d2, r2): a larger distance, or the same distance and a larger rowid; NaN ranks last.</summary>
    private static bool Worse(double d1, long r1, double d2, long r2)
    {
        bool n1 = double.IsNaN(d1), n2 = double.IsNaN(d2);
        if (n1 != n2)
        {
            return n1;
        }

        if (!n1 && d1 != d2)
        {
            return d1 > d2;
        }

        return r1 > r2;
    }

    private void Offer(double d, long rowid, long chunk, int offset)
    {
        if (_hn == _k)
        {
            if (!Worse(_hd[0], _hr[0], d, rowid))
            {
                return;
            }

            Set(0, d, rowid, chunk, offset);
            SiftDown(0);
            return;
        }

        int i = _hn++;
        Set(i, d, rowid, chunk, offset);
        while (i > 0)
        {
            int p = (i - 1) / 2;
            if (!Worse(_hd[i], _hr[i], _hd[p], _hr[p]))
            {
                break;
            }

            Swap(i, p);
            i = p;
        }
    }

    private void Set(int i, double d, long r, long c, int o)
    {
        _hd[i] = d;
        _hr[i] = r;
        _hc[i] = c;
        _ho[i] = o;
    }

    private void Swap(int a, int b)
    {
        (_hd[a], _hd[b]) = (_hd[b], _hd[a]);
        (_hr[a], _hr[b]) = (_hr[b], _hr[a]);
        (_hc[a], _hc[b]) = (_hc[b], _hc[a]);
        (_ho[a], _ho[b]) = (_ho[b], _ho[a]);
    }

    private void SiftDown(int i)
    {
        for (; ; )
        {
            int l = (2 * i) + 1, r = l + 1, w = i;
            if (l < _hn && Worse(_hd[l], _hr[l], _hd[w], _hr[w]))
            {
                w = l;
            }

            if (r < _hn && Worse(_hd[r], _hr[r], _hd[w], _hr[w]))
            {
                w = r;
            }

            if (w == i)
            {
                return;
            }

            Swap(i, w);
            i = w;
        }
    }

    /// <summary>Heap sort in place: afterwards the arrays run from nearest to farthest.</summary>
    private void SortResults()
    {
        int n = _hn;
        for (int end = n - 1; end > 0; end--)
        {
            Swap(0, end);
            _hn = end;
            SiftDown(0);
        }

        _hn = n;
    }

    private void LoadResult()
    {
        if (_pos >= _hn)
        {
            _eof = true;
            return;
        }

        _eof = false;
        _rowid = _hr[_pos];
        _distance = _hd[_pos];
        _curChunk = _hc[_pos];
        _curOffset = _ho[_pos];
        _infoFor = long.MinValue;
    }

    // ------------------------------------------------------------------ columns

    private ModStatus EnsureInfo()
    {
        if (_infoFor == _rowid)
        {
            return ModStatus.Ok;
        }

        _info ??= _t.OpenRowids();
        ModStatus s = _info.Find(_rowid, out bool found);
        if (s != ModStatus.Ok)
        {
            return s;
        }

        if (!found)
        {
            throw VecTable.Corrupt("row " + _rowid.ToString(CultureInfo.InvariantCulture) + " of " + _s.Table + " has no entry in its rowid table");
        }

        s = _info.Load();
        if (s != ModStatus.Ok)
        {
            return s;
        }

        Mem c = default, o = default;
        _info.Row.Load(VecTable.RowidsChunkCol, ref c);
        _info.Row.Load(VecTable.RowidsOffsetCol, ref o);
        _curChunk = c.IntValue;
        _curOffset = (int)o.IntValue;
        _infoFor = _rowid;
        return ModStatus.Ok;
    }

    private static void CopyOut(in Mem stored, ref Mem dst, ref byte[]? own, bool text)
    {
        switch (stored.Kind)
        {
            case StorageClass.Null:
                Values.SetNull(ref dst);
                break;
            case StorageClass.Integral:
                Values.SetInt(ref dst, stored.IntValue);
                break;
            case StorageClass.Real:
                Values.SetReal(ref dst, stored.RealValue);
                break;
            case StorageClass.Text:
                Values.SetBytes(ref dst, ref own, StorageClass.Text, VecTable.Enc == TextEncoding.Utf8 ? stored.Bytes : TextCodec.ToUtf8(stored.Bytes, VecTable.Enc));
                break;
            default:
                Values.SetBytes(ref dst, ref own, text ? StorageClass.Text : StorageClass.Blob, stored.Bytes);
                break;
        }
    }

    internal override ModStatus Column(int index, ref Mem dst, ref byte[]? own)
    {
        if (index >= _s.Columns.Length)
        {
            if (index == _s.DistanceColumn && _mode == 1 && !double.IsNaN(_distance))
            {
                Values.SetReal(ref dst, _distance);
            }
            else
            {
                Values.SetNull(ref dst);
            }

            return ModStatus.Ok;
        }

        VecColumn col = _s.Columns[index];
        switch (col.Kind)
        {
            case VecColumnKind.Primary when col.Type == VecScalarType.Integer:
                Values.SetInt(ref dst, _rowid);
                return ModStatus.Ok;
            case VecColumnKind.Primary:
            {
                ModStatus s = EnsureInfo();
                if (s != ModStatus.Ok)
                {
                    return s;
                }

                Mem id = default;
                _info!.Row.Load(VecTable.RowidsIdCol, ref id);
                CopyOut(id, ref dst, ref own, true);
                return ModStatus.Ok;
            }

            case VecColumnKind.Metadata:
            {
                ModStatus s = EnsureInfo();
                if (s != ModStatus.Ok)
                {
                    return s;
                }

                Mem m = default;
                int at = VecTable.RowidsFirstMetadataCol + col.Slot;
                if (at < _info!.Row.ColumnCount)
                {
                    _info.Row.Load(at, ref m);
                }

                CopyOut(m, ref dst, ref own, true);
                return ModStatus.Ok;
            }

            case VecColumnKind.Vector:
                return VectorColumn(col, ref dst, ref own);
            case VecColumnKind.Partition:
            {
                ModStatus s = EnsureInfo();
                if (s != ModStatus.Ok)
                {
                    return s;
                }

                _chk ??= _t.OpenChunks();
                s = _chk.Find(_curChunk, out bool f);
                if (s != ModStatus.Ok)
                {
                    return s;
                }

                if (!f)
                {
                    Values.SetNull(ref dst);
                    return ModStatus.Ok;
                }

                s = _chk.Load();
                if (s != ModStatus.Ok)
                {
                    return s;
                }

                Mem p = default;
                _chk.Row.Load(2 + col.Slot, ref p);
                CopyOut(p, ref dst, ref own, true);
                return ModStatus.Ok;
            }

            default:
            {
                _aux ??= _t.OpenAux();
                ModStatus s = _aux.Find(_rowid, out bool f);
                if (s != ModStatus.Ok)
                {
                    return s;
                }

                if (!f)
                {
                    Values.SetNull(ref dst);
                    return ModStatus.Ok;
                }

                s = _aux.Load();
                if (s != ModStatus.Ok)
                {
                    return s;
                }

                Mem a = default;
                _aux.Row.Load(1 + col.Slot, ref a);
                CopyOut(a, ref dst, ref own, col.Type == VecScalarType.Text);
                return ModStatus.Ok;
            }
        }
    }

    private ModStatus VectorColumn(VecColumn col, ref Mem dst, ref byte[]? own)
    {
        ModStatus s = EnsureInfo();
        if (s != ModStatus.Ok)
        {
            return s;
        }

        if (_vc.Length == 0)
        {
            _vc = new ShadowTree?[_s.Vectors.Length];
        }

        ShadowTree tree = _vc[col.Slot] ??= _t.OpenVectors(col.Slot);
        s = tree.Find(_curChunk, out bool found);
        if (s != ModStatus.Ok)
        {
            return s;
        }

        if (!found)
        {
            throw VecTable.Corrupt("vector chunk " + _curChunk.ToString(CultureInfo.InvariantCulture) + " of " + _s.Table + " is missing");
        }

        s = tree.Load();
        if (s != ModStatus.Ok)
        {
            return s;
        }

        Mem blob = default;
        tree.Row.Load(1, ref blob);
        int bytes = col.VectorBytes;
        long start = (long)_curOffset * bytes;
        if (_curOffset < 0 || start + bytes > blob.Length)
        {
            throw VecTable.Corrupt("row " + _rowid.ToString(CultureInfo.InvariantCulture) + " of " + _s.Table + " points outside its chunk");
        }

        Values.SetBytes(ref dst, ref own, StorageClass.Blob, blob.Bytes.Slice((int)start, bytes));
        dst.Subtype = VecValue.SubtypeOf(col.Element); // the reference extension marks the vector it returns, and UPDATE reads it back
        return ModStatus.Ok;
    }

    public override void Dispose()
    {
        _chunks?.Dispose();
        _vchunk?.Dispose();
        _meta?.Dispose();
        _scan?.Dispose();
        _info?.Dispose();
        _chk?.Dispose();
        _aux?.Dispose();
        _ids?.Dispose();
        foreach (ShadowTree? t in _vc)
        {
            t?.Dispose();
        }
    }
}
