using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Storage.Pager;

namespace RecursiveAI.OneLite.Vm;

/// <summary>The built-in window functions and "an aggregate used as a window function" (windowfunctions).</summary>
internal enum WinFn : byte
{
    RowNumber,
    Rank,
    DenseRank,
    PercentRank,
    CumeDist,
    Ntile,
    Lag,
    Lead,
    FirstValue,
    LastValue,
    NthValue,
    Aggregate,
}

internal enum WinUnit : byte { Rows, Range, Groups }

internal enum WinBound : byte { UnboundedPreceding, Preceding, CurrentRow, Following, UnboundedFollowing }

internal enum WinExclude : byte { NoOthers, CurrentRow, Group, Ties }

/// <summary>One window function call as compiled: what it computes, which fields of the carried row it reads and its frame.</summary>
internal sealed class WinFuncSpec
{
    internal WinFn Fn;
    internal AggSpec? Agg;

    /// <summary>The call spells out <c>EXCLUDE NO OTHERS</c>; min/max then keep the first of equal values like any other EXCLUDE.</summary>
    internal bool ExplicitNoOthers;

    /// <summary>Indexes into the carried row of the call's arguments (value, then offset/default or n).</summary>
    internal int[] ArgFields = [];

    internal int FilterField = -1;

    /// <summary>For json_group_array and json_group_object: per argument, the field holding its subtype (the sorter keeps values only).</summary>
    internal int[] FlagFields = [];
    internal WinUnit Unit;
    internal WinBound Start, End;
    internal int StartOffField = -1, EndOffField = -1;
    internal WinExclude Exclude;

    /// <summary>Where the result goes in the carried row.</summary>
    internal int ResultField;

    /// <summary>For lag and lead: how far back into the partition the call can look, or -1 when the offset is not a literal.</summary>
    internal int Back;
}

/// <summary>One sort-and-run stage: the windows that share a PARTITION BY and ORDER BY.</summary>
internal sealed class WinStageSpec
{
    internal int Width;
    internal int[] PartFields = [];
    internal CollKind[] PartColl = [];
    internal int[] OrderFields = [];
    internal SortTerm[] OrderTerms = [];
    internal WinFuncSpec[] Funcs = [];
}

/// <summary>
/// Runs the window functions of one stage over rows that arrive sorted by partition and order (windowfunctions). Rows are pushed one
/// at a time and the finished rows are pulled; a row is finished as soon as every row its frame, peer group or look-ahead needs has
/// arrived (or its partition has ended), so a running total or a bounded frame keeps only the rows the frame still covers. A partition
/// whose frame reaches its end (UNBOUNDED FOLLOWING, ntile, percent_rank, cume_dist) is held whole while it fits the sort memory
/// limit; past the limit (and when the connection has a temporary-file VFS) its rows move into blocks that are written to a temporary
/// file and read back on demand (see WindowSpill.cs). Only that path does I/O, as resumable requests on the connection's slot.
/// </summary>
internal sealed partial class WindowRun
{
    private sealed class Row
    {
        internal Mem[] F = [];
        internal byte[]?[] Own = [];
        internal long Group;
        internal int Size;
    }

    private sealed class FuncState
    {
        internal WinFuncSpec Spec = null!;
        internal AggState? Agg;
        internal long AggLo, AggHi;
        internal bool Started;
        internal long Fs, Fe;
        internal long LastStart, LastEnd;
        internal long RsHint, ReHint = -1;
        internal long StartN, EndN;
        internal Mem StartNum, EndNum;
        internal long NtileN;
        internal Mem FirstVal;
        internal byte[]? FirstOwn;
        internal bool FirstSet;
        internal byte[]? ResOwn;
        internal Mem[] Args = [];
        internal long Off;

        /// <summary>A lag/lead offset that is not a whole number: the target row is worked out in floating point (see Evaluate).</summary>
        internal double OffReal;
        internal bool OffIsReal;
        internal bool OffNull;
    }

    private readonly WinStageSpec _s;
    private readonly TextEncoding _enc;
    private readonly FuncState[] _f;
    private readonly int _w;

    private Row?[] _ring = new Row?[32];
    private int _head, _n;
    private long _first, _count, _cur;
    private bool _active, _ended, _prepared;
    private Row? _last, _pending;
    private readonly Stack<Row> _pool = new();

    private long[] _gs = new long[16];
    private int _gHead, _gN;
    private long _gBase;

    internal WindowRun(WinStageSpec spec, TextEncoding enc, bool canSpill = false, long memLimit = 0)
    {
        _s = spec;
        _enc = enc;
        _canSpill = canSpill;
        _memLimit = memLimit;
        _w = spec.Width;
        _f = new FuncState[spec.Funcs.Length];
        for (int i = 0; i < _f.Length; i++)
        {
            WinFuncSpec fs = spec.Funcs[i];
            _f[i] = new FuncState { Spec = fs, Args = new Mem[Math.Max(fs.ArgFields.Length, 1)] };
            if (fs.Fn == WinFn.Aggregate)
            {
                _f[i].Agg = new AggState(fs.Agg!, enc);
                _f[i].Agg!.EnableInverse();
            }
        }
    }

    /// <summary>Back to the state of a new run, keeping the buffers (a statement run again).</summary>
    internal void Reset()
    {
        ReleasePartition();
        ResetSpillRun();
        _inFlight = false;
        _ended = false;
        _prepared = false;
        _pending = null;
        _first = 0;
        _count = 0;
        _cur = 0;
        foreach (FuncState f in _f)
        {
            f.Agg?.Reset();
            f.AggLo = f.AggHi = 0;
            f.Started = false;
            f.LastStart = f.LastEnd = 0;
            f.RsHint = 0;
            f.ReHint = -1;
            f.FirstSet = false;
        }
    }

    // ------------------------------------------------------------------ rows and groups

    private Row Rent()
    {
        if (_pool.Count > 0)
        {
            return _pool.Pop();
        }

        return new Row { F = new Mem[_w], Own = new byte[_w][] };
    }

    private Row RowAt(long abs) => _spill ? SpillRowAt(abs) : _ring[(_head + (int)(abs - _first)) & (_ring.Length - 1)]!;

    private void AddRow(Row row)
    {
        if (_spill)
        {
            SpillAddRow(row);
            return;
        }

        if (_n == _ring.Length)
        {
            var bigger = new Row?[_ring.Length * 2];
            for (int i = 0; i < _n; i++)
            {
                bigger[i] = _ring[(_head + i) & (_ring.Length - 1)];
            }

            _ring = bigger;
            _head = 0;
        }

        _ring[(_head + _n) & (_ring.Length - 1)] = row;
        _n++;
    }

    private void PushGroupStart(long abs)
    {
        if (_gN == _gs.Length)
        {
            var bigger = new long[_gs.Length * 2];
            for (int i = 0; i < _gN; i++)
            {
                bigger[i] = _gs[(_gHead + i) & (_gs.Length - 1)];
            }

            _gs = bigger;
            _gHead = 0;
        }

        _gs[(_gHead + _gN) & (_gs.Length - 1)] = abs;
        _gN++;
    }

    private long GroupStart(long g) => _spill ? SpillGroupStart(g) : _gs[(_gHead + (int)(g - _gBase)) & (_gs.Length - 1)];

    private long LastGroup => _spill ? _last!.Group : _gBase + _gN - 1;

    /// <summary>The first peer group whose start is known (earlier ones were let go); a group before it starts before the first row held.</summary>
    private long GroupBase => _spill ? _blocks[_dropB]!.GFirst : _gBase;

    private long GroupEnd(long g) => g < LastGroup ? GroupStart(g + 1) - 1 : _count - 1;

    private bool GroupComplete(long g) => g < LastGroup || _ended;

    private void ReleasePartition()
    {
        ReleaseSpill();
        _bytes = 0;
        while (_n > 0)
        {
            _pool.Push(_ring[_head]!);
            _ring[_head] = null;
            _head = (_head + 1) & (_ring.Length - 1);
            _n--;
        }

        _head = 0;
        _last = null;
        _gHead = 0;
        _gN = 0;
        _gBase = 0;
        _active = false;
    }

    private void StartPartition(Row first)
    {
        _first = 0;
        _count = 0;
        _cur = 0;
        _ended = false;
        _active = true;
        _prepared = false;
        _last = null;
        _gHead = 0;
        _gN = 0;
        _gBase = 0;
        foreach (FuncState f in _f)
        {
            f.Agg?.Reset();
            f.AggLo = f.AggHi = 0;
            f.Started = false;
            f.LastStart = f.LastEnd = 0;
            f.RsHint = 0;
            f.ReHint = -1;
            f.FirstSet = false;
        }

        Append(first);
    }

    private bool SamePartition(Row a, Row b)
    {
        for (int i = 0; i < _s.PartFields.Length; i++)
        {
            int fld = _s.PartFields[i];
            if (Values.Compare(a.F[fld], b.F[fld], _s.PartColl[i], _enc) != 0)
            {
                return false;
            }
        }

        return true;
    }

    private bool SamePeer(Row a, Row b)
    {
        for (int i = 0; i < _s.OrderFields.Length; i++)
        {
            int fld = _s.OrderFields[i];
            if (Values.Compare(a.F[fld], b.F[fld], _s.OrderTerms[i].Collation, _enc) != 0)
            {
                return false;
            }
        }

        return true;
    }

    private void Append(Row row)
    {
        if (_last is null)
        {
            row.Group = 0;
            PushGroupStart(0);
        }
        else if (SamePeer(_last, row))
        {
            row.Group = _last.Group;
        }
        else
        {
            row.Group = _last.Group + 1;
            if (_spill)
            {
                _lastGroupStart = _count;
            }
            else
            {
                PushGroupStart(_count);
            }
        }

        AddRow(row);
        _count++;
        _last = row;
        if (_count == 1)
        {
            foreach (FuncState f in _f)
            {
                if (f.Spec.Fn == WinFn.FirstValue && f.Spec.Start == WinBound.UnboundedPreceding && f.Spec.Exclude == WinExclude.NoOthers)
                {
                    CopyMem(row.F[f.Spec.ArgFields[0]], ref f.FirstVal, ref f.FirstOwn);
                    f.FirstSet = true;
                }
            }
        }
    }

    private static void CopyMem(in Mem src, ref Mem dst, ref byte[]? own)
    {
        if (src.Kind is StorageClass.Text or StorageClass.Blob)
        {
            // The subtype (json text that is already JSON) rides along: json_group_array(json(x)) OVER nests it rather than quoting it.
            byte subtype = src.Subtype;
            Values.SetBytes(ref dst, ref own, src.Kind, src.Bytes);
            dst.Subtype = subtype;
        }
        else
        {
            dst = src;
        }
    }

    // ------------------------------------------------------------------ input

    /// <summary>Adds the next sorted row, held in registers <paramref name="b"/> to <paramref name="b"/> + width - 1.</summary>
    internal void Push(Mem[] r, int b)
    {
        Row row = Rent();
        int size = 0;
        for (int i = 0; i < _w; i++)
        {
            CopyMem(r[b + i], ref row.F[i], ref row.Own[i]);
            size += 24 + (row.F[i].Kind is StorageClass.Text or StorageClass.Blob ? row.F[i].Bytes.Length : 0);
        }

        row.Size = size;
        _inFlight = true;
        if (!_active)
        {
            StartPartition(row);
        }
        else if (_ended || !SamePartition(_last!, row))
        {
            _ended = true;
            _pending = row;
        }
        else
        {
            Append(row);
        }

        if (!_spill)
        {
            _bytes += size;
            if (_canSpill && !_spillFailed && _bytes > _memLimit && _active && _pending is null)
            {
                EnterSpill();
            }
        }
    }

    /// <summary>No more input: the running partition is complete.</summary>
    internal void Finish()
    {
        if (_active)
        {
            _ended = true;
        }
    }

    // ------------------------------------------------------------------ output

    /// <summary>
    /// Produces the next finished row into registers <paramref name="b"/>.. (the carried row, with this stage's results filled in).
    /// Returns false when there is none yet (more input is needed) or none is left.
    /// </summary>
    internal SortStatus TryOut(Mem[] r, int b, Pager io, out bool got)
    {
        got = false;
        for (; ; )
        {
            if (_spill && Service(io) == SortStatus.Pending)
            {
                return SortStatus.Pending;
            }

            try
            {
                got = TryOutCore(r, b);
                return SortStatus.Ok;
            }
            catch (NeedBlockException) when (_spill)
            {
                // A row of a block that is on disk was asked for: the next Service reads the block, then the row is tried again.
                NoteMiss();
            }
        }
    }

    private bool TryOutCore(Mem[] r, int b)
    {
        for (; ; )
        {
            if (!_active)
            {
                return false;
            }

            Trim();
            if (_cur >= _count)
            {
                if (!_ended)
                {
                    return false;
                }

                ReleasePartition();
                if (_pending is null)
                {
                    return false;
                }

                Row p = _pending;
                _pending = null;
                StartPartition(p);
                continue;
            }

            if (!_prepared)
            {
                Prepare();
                _prepared = true;
            }

            Row cur = RowAt(_cur);
            foreach (FuncState f in _f)
            {
                if (!Ready(f, cur, _cur))
                {
                    return false;
                }
            }

            for (int i = 0; i < _w; i++)
            {
                r[b + i] = cur.F[i];
            }

            foreach (FuncState f in _f)
            {
                Evaluate(f, cur, _cur, ref r[b + f.Spec.ResultField]);
            }

            _cur++;
            _missRun = 0;
            return true;
        }
    }

    private void Trim()
    {
        long keep = _cur;
        foreach (FuncState f in _f)
        {
            WinFuncSpec sp = f.Spec;
            long need;
            switch (sp.Fn)
            {
                case WinFn.Lag:
                case WinFn.Lead:
                    need = sp.Back < 0 ? 0 : _cur - sp.Back;
                    break;
                case WinFn.FirstValue:
                    need = sp.Start == WinBound.UnboundedPreceding && sp.Exclude == WinExclude.NoOthers
                        ? (sp.Unit == WinUnit.Range && sp.End is WinBound.Preceding or WinBound.Following ? Math.Min(_cur, f.ReHint + 1) : _cur) // a RANGE end bound is found by walking from the rows kept
                        : f.LastStart;
                    break;
                case WinFn.LastValue:
                    need = sp.Exclude == WinExclude.NoOthers ? f.LastEnd : f.LastStart;
                    break;
                case WinFn.NthValue:
                    need = f.LastStart;
                    break;
                case WinFn.Aggregate:
                    if (sp.Exclude != WinExclude.NoOthers)
                    {
                        need = f.LastStart;
                    }
                    else if (IsInvertible(sp.Agg!.Kind))
                    {
                        // A frame that starts at the partition start never gives rows back.
                        need = sp.Start == WinBound.UnboundedPreceding ? f.AggHi : f.AggLo;
                    }
                    else if (sp.Start == WinBound.UnboundedPreceding)
                    {
                        need = f.AggHi;
                    }
                    else
                    {
                        need = f.LastStart;
                    }

                    break;
                default:
                    need = _cur;
                    break;
            }

            keep = Math.Min(keep, need);
        }

        keep = Math.Min(keep, _count - 1);
        if (_spill)
        {
            if (_first < keep)
            {
                _first = keep;
                DropBlocksBefore(keep);
            }

            keep = _first;
        }

        while (_first < keep)
        {
            _bytes -= _ring[_head]!.Size;
            _pool.Push(_ring[_head]!);
            _ring[_head] = null;
            _head = (_head + 1) & (_ring.Length - 1);
            _n--;
            _first++;
        }

        while (_gN > 1 && GroupStart(_gBase + 1) <= _first)
        {
            _gHead = (_gHead + 1) & (_gs.Length - 1);
            _gN--;
            _gBase++;
        }
    }

    private static bool IsInvertible(AggKind k) => k is AggKind.Count or AggKind.CountStar or AggKind.Sum or AggKind.Total or AggKind.Avg or AggKind.GroupConcat or AggKind.Median or AggKind.Percentile
        or AggKind.PercentileCont or AggKind.PercentileDisc;

    // ------------------------------------------------------------------ offsets

    private void Prepare()
    {
        Row first = RowAt(_first);
        foreach (FuncState f in _f)
        {
            WinFuncSpec sp = f.Spec;
            if (sp.StartOffField >= 0)
            {
                CheckOffset(first.F[sp.StartOffField], sp.Unit, "starting", out f.StartN, out f.StartNum);
            }

            if (sp.EndOffField >= 0)
            {
                CheckOffset(first.F[sp.EndOffField], sp.Unit, "ending", out f.EndN, out f.EndNum);
            }

            if (sp.Fn == WinFn.Ntile)
            {
                Mem n = first.F[sp.ArgFields[0]];
                long v = 0;
                if (n.Kind != StorageClass.Null)
                {
                    StorageClass k = Values.ToNumeric(n, out long iv, out double dv);
                    v = k == StorageClass.Integral ? iv : double.IsNaN(dv) ? 0 : dv >= 9.2e18 ? long.MaxValue : dv <= -9.2e18 ? long.MinValue : (long)dv;
                }

                if (v <= 0)
                {
                    throw new OneLiteException(ResultCode.Error, "argument of ntile must be a positive integer");
                }

                f.NtileN = v;
            }
        }
    }

    private static void CheckOffset(in Mem m, WinUnit unit, string which, out long n, out Mem num)
    {
        n = 0;
        num = default;
        bool ok = false;
        bool needInt = unit != WinUnit.Range;
        Mem v = m;
        if (v.Kind == StorageClass.Text)
        {
            StorageClass k = Values.ParseNumber(v.Bytes, out long pi, out double pd, out bool whole);
            if (whole && k == StorageClass.Integral)
            {
                v = Mem.FromInt(pi);
            }
            else if (whole && k == StorageClass.Real)
            {
                v = Mem.FromReal(pd);
            }
        }

        if (v.Kind == StorageClass.Integral)
        {
            ok = v.IntValue >= 0;
            n = v.IntValue;
            num = v;
        }
        else if (v.Kind == StorageClass.Real && v.RealValue >= 0)
        {
            double d = v.RealValue;
            if (needInt)
            {
                ok = d == Math.Floor(d) && d < 9.2e18;
                n = ok ? (long)d : 0;
            }
            else
            {
                ok = true;
                n = d >= 9.2e18 ? long.MaxValue : (long)d;
            }

            num = v;
        }

        if (!ok)
        {
            throw new OneLiteException(ResultCode.Error, "frame " + which + " offset must be a non-negative " + (needInt ? "integer" : "number"));
        }
    }

    // ------------------------------------------------------------------ readiness and frames

    private bool Ready(FuncState f, Row cur, long c)
    {
        WinFuncSpec sp = f.Spec;
        switch (sp.Fn)
        {
            case WinFn.RowNumber:
            case WinFn.Rank:
            case WinFn.DenseRank:
                return true;
            case WinFn.Lag:
            {
                LagOffset(f, cur);
                return f.OffNull || f.Off >= 0 || c - f.Off < _count || _ended;
            }

            case WinFn.PercentRank:
            case WinFn.CumeDist:
            case WinFn.Ntile:
                return _ended;
            case WinFn.Lead:
            {
                LagOffset(f, cur);
                return f.OffNull || f.Off <= 0 || c + f.Off < _count || _ended;
            }

            default:
                return Bounds(f, cur, c);
        }
    }

    private static long SatAdd(long a, long b)
    {
        long s = a + b;
        return ((a ^ s) & (b ^ s)) < 0 ? (b > 0 ? long.MaxValue : long.MinValue) : s;
    }

    private bool Bounds(FuncState f, Row cur, long c)
    {
        WinFuncSpec sp = f.Spec;
        long fs, fe;
        long g = cur.Group;
        switch (sp.Unit)
        {
            case WinUnit.Rows:
                fs = sp.Start switch
                {
                    WinBound.UnboundedPreceding => 0,
                    WinBound.Preceding => SatAdd(c, -f.StartN),
                    WinBound.CurrentRow => c,
                    _ => SatAdd(c, f.StartN),
                };
                switch (sp.End)
                {
                    case WinBound.UnboundedFollowing:
                        if (!_ended)
                        {
                            return false;
                        }

                        fe = _count - 1;
                        break;
                    case WinBound.Following:
                        fe = SatAdd(c, f.EndN);
                        break;
                    case WinBound.Preceding:
                        fe = SatAdd(c, -f.EndN);
                        break;
                    default:
                        fe = c;
                        break;
                }

                if (!_ended && (fe > _count - 1 || fs > _count - 1))
                {
                    return false;
                }

                if (fe > _count - 1)
                {
                    fe = _count - 1;
                }

                break;
            case WinUnit.Groups:
            {
                switch (sp.Start)
                {
                    case WinBound.UnboundedPreceding:
                        fs = 0;
                        break;
                    case WinBound.Preceding:
                    {
                        long gt = g - f.StartN;
                        fs = gt < GroupBase ? (gt < 0 ? 0 : _first) : GroupStart(gt);
                        break;
                    }

                    case WinBound.CurrentRow:
                        fs = GroupStart(g);
                        break;
                    default:
                    {
                        long gt = SatAdd(g, f.StartN);
                        if (gt > LastGroup)
                        {
                            if (!_ended)
                            {
                                return false;
                            }

                            fs = _count;
                        }
                        else
                        {
                            fs = GroupStart(gt);
                        }

                        break;
                    }
                }

                switch (sp.End)
                {
                    case WinBound.UnboundedFollowing:
                        if (!_ended)
                        {
                            return false;
                        }

                        fe = _count - 1;
                        break;
                    case WinBound.CurrentRow:
                        if (!GroupComplete(g))
                        {
                            return false;
                        }

                        fe = GroupEnd(g);
                        break;
                    case WinBound.Following:
                    {
                        long gt = SatAdd(g, f.EndN);
                        if (gt > LastGroup)
                        {
                            if (!_ended)
                            {
                                return false;
                            }

                            fe = _count - 1;
                        }
                        else
                        {
                            if (!GroupComplete(gt))
                            {
                                return false;
                            }

                            fe = GroupEnd(gt);
                        }

                        break;
                    }

                    default:
                    {
                        long gt = g - f.EndN;
                        if (gt >= g && !GroupComplete(g))
                        {
                            return false;
                        }

                        fe = gt < 0 ? -1 : GroupEnd(gt);
                        break;
                    }
                }

                break;
            }

            default:
                if (!RangeBounds(f, cur, out fs, out fe))
                {
                    return false;
                }

                break;
        }

        if (fs < 0)
        {
            fs = 0;
        }

        f.Fs = fs;
        f.Fe = fe;
        return true;
    }

    private int Pos(in Mem m)
    {
        SortTerm t = _s.OrderTerms[0];
        switch (m.Kind)
        {
            case StorageClass.Null:
                return t.NullsFirst ? -1 : 1;
            case StorageClass.Integral:
            case StorageClass.Real:
                return 0;
            default:
                return t.Descending ? -1 : 1;
        }
    }

    /// <summary>
    /// Whether a row with order value <paramref name="k"/> is on the inside of a RANGE offset bound of the row with value
    /// <paramref name="key"/> (start: at or after the bound; end: at or before it). Observed with offsets large enough to swallow the
    /// value: a PRECEDING bound adds the offset to the tested row's value and compares with the current one, a FOLLOWING bound adds it
    /// to the current row's value; in descending order the sign flips.
    /// </summary>
    private bool BoundHolds(in Mem k, in Mem key, in Mem n, bool preceding, bool desc, bool start, CollKind coll)
    {
        int c = preceding
            ? Values.Compare(Shift(k, n, desc), key, coll, _enc)
            : Values.Compare(k, Shift(key, n, desc), coll, _enc);
        // Ascending: a start holds when value >= bound and an end while value <= bound; descending reverses both.
        return start != desc ? c >= 0 : c <= 0;
    }

    private static Mem Shift(in Mem key, in Mem n, bool subtract)
    {
        if (key.Kind == StorageClass.Integral && n.Kind == StorageClass.Integral)
        {
            long a = key.IntValue, b = n.IntValue;
            long s = subtract ? a - b : a + b;
            bool overflow = subtract ? ((a ^ b) & (a ^ s)) < 0 : ((a ^ s) & (b ^ s)) < 0;
            if (!overflow)
            {
                return Mem.FromInt(s);
            }
        }

        double x = key.Kind == StorageClass.Integral ? key.IntValue : key.RealValue;
        double y = n.Kind == StorageClass.Integral ? n.IntValue : n.RealValue;
        return Mem.FromReal(subtract ? x - y : x + y);
    }

    private bool RangeBounds(FuncState f, Row cur, out long fs, out long fe)
    {
        WinFuncSpec sp = f.Spec;
        long g = cur.Group;
        fs = 0;
        fe = 0;
        bool desc = _s.OrderTerms.Length > 0 && _s.OrderTerms[0].Descending;
        CollKind coll = _s.OrderTerms.Length > 0 ? _s.OrderTerms[0].Collation : CollKind.Binary;
        int of = _s.OrderFields.Length > 0 ? _s.OrderFields[0] : 0;
        Mem key = _s.OrderFields.Length > 0 ? cur.F[of] : default;
        bool numeric = key.Kind is StorageClass.Integral or StorageClass.Real;

        switch (sp.Start)
        {
            case WinBound.UnboundedPreceding:
                fs = 0;
                break;
            case WinBound.CurrentRow:
                fs = GroupStart(g);
                break;
            default:
            {
                if (!numeric)
                {
                    fs = GroupStart(g);
                    break;
                }

                long i = Math.Max(f.RsHint, _first);
                while (i < _count)
                {
                    Mem k = RowAt(i).F[of];
                    int pos = Pos(k);
                    if (pos > 0)
                    {
                        break;
                    }

                    if (pos == 0)
                    {
                        if (BoundHolds(k, key, f.StartNum, sp.Start == WinBound.Preceding, desc, true, coll))
                        {
                            break;
                        }
                    }

                    i++;
                }

                if (i >= _count && !_ended)
                {
                    return false;
                }

                fs = i;
                f.RsHint = i;

                // Observed: the current row's group is always inside a frame that starts PRECEDING it, even when rounding in the key
                // arithmetic would put the bound just past it.
                if (sp.Start == WinBound.Preceding && fs > GroupStart(g))
                {
                    fs = GroupStart(g);
                }
                break;
            }
        }

        switch (sp.End)
        {
            case WinBound.UnboundedFollowing:
                if (!_ended)
                {
                    return false;
                }

                fe = _count - 1;
                break;
            case WinBound.CurrentRow:
                if (!GroupComplete(g))
                {
                    return false;
                }

                fe = GroupEnd(g);
                break;
            default:
            {
                if (!numeric)
                {
                    if (!GroupComplete(g))
                    {
                        return false;
                    }

                    fe = GroupEnd(g);
                    break;
                }

                long i = Math.Max(f.ReHint, _first - 1);
                while (i + 1 < _count)
                {
                    Mem k = RowAt(i + 1).F[of];
                    int pos = Pos(k);
                    if (pos > 0)
                    {
                        break;
                    }

                    if (pos == 0)
                    {
                        if (!BoundHolds(k, key, f.EndNum, sp.End == WinBound.Preceding, desc, false, coll))
                        {
                            break;
                        }
                    }

                    i++;
                }

                if (i + 1 >= _count && !_ended)
                {
                    return false;
                }

                fe = i;
                f.ReHint = i;

                // The same for a frame that ends FOLLOWING it.
                if (sp.End == WinBound.Following && fe < GroupEnd(g))
                {
                    if (!GroupComplete(g))
                    {
                        return false;
                    }

                    fe = GroupEnd(g);
                }
                break;
            }
        }

        return true;
    }

    // ------------------------------------------------------------------ values

    private static bool Excluded(WinFuncSpec sp, Row x, long xi, Row cur, long c) => sp.Exclude switch
    {
        WinExclude.CurrentRow => xi == c,
        WinExclude.Group => x.Group == cur.Group,
        WinExclude.Ties => x.Group == cur.Group && xi != c,
        _ => false,
    };

    private static void LagOffset(FuncState f, Row cur)
    {
        WinFuncSpec sp = f.Spec;
        f.OffNull = false;
        f.OffIsReal = false;
        f.Off = 1;
        if (sp.ArgFields.Length < 2)
        {
            return;
        }

        Mem m = cur.F[sp.ArgFields[1]];
        switch (m.Kind)
        {
            case StorageClass.Null:
                f.OffNull = true;
                return;
            case StorageClass.Integral:
                f.Off = m.IntValue;
                return;
            default:
            {
                StorageClass k = Values.ToNumeric(m, out long iv, out double dv);
                if (k == StorageClass.Integral)
                {
                    f.Off = iv;
                }
                else if (dv == Math.Floor(dv) && Math.Abs(dv) < 9.2e18)
                {
                    f.Off = (long)dv;
                }
                else if (!double.IsNaN(dv) && Math.Abs(dv) < 9.2e18)
                {
                    // Observed: the row is position -/+ offset in floating point, and it counts only when that is a whole number
                    // (so 1e-300 names the current row, 1e-10 names none).
                    f.OffIsReal = true;
                    f.OffReal = dv;
                    f.Off = (long)Math.Ceiling(Math.Abs(dv)) * Math.Sign(dv);
                }
                else
                {
                    f.OffNull = true;
                }

                return;
            }
        }
    }

    private void Evaluate(FuncState f, Row cur, long c, ref Mem dst)
    {
        WinFuncSpec sp = f.Spec;
        switch (sp.Fn)
        {
            case WinFn.RowNumber:
                Values.SetInt(ref dst, c + 1);
                return;
            case WinFn.Rank:
                Values.SetInt(ref dst, GroupStart(cur.Group) + 1);
                return;
            case WinFn.DenseRank:
                Values.SetInt(ref dst, cur.Group + 1);
                return;
            case WinFn.PercentRank:
                Values.SetReal(ref dst, _count <= 1 ? 0.0 : (double)GroupStart(cur.Group) / (_count - 1));
                return;
            case WinFn.CumeDist:
                Values.SetReal(ref dst, (double)(GroupEnd(cur.Group) + 1) / _count);
                return;
            case WinFn.Ntile:
            {
                long n = f.NtileN, t = _count;
                long baseSize = t / n, rem = t % n;
                long bucket;
                if (baseSize == 0)
                {
                    bucket = c + 1;
                }
                else if (c < rem * (baseSize + 1))
                {
                    bucket = c / (baseSize + 1) + 1;
                }
                else
                {
                    bucket = rem + (c - rem * (baseSize + 1)) / baseSize + 1;
                }

                Values.SetInt(ref dst, bucket);
                return;
            }

            case WinFn.Lag:
            case WinFn.Lead:
            {
                LagOffset(f, cur);
                // Observed: an offset that is NULL or not a whole number names no row, so the default (or NULL) comes back.
                long off = sp.Fn == WinFn.Lag ? -f.Off : f.Off;
                long target = SatAdd(c, off);
                if (f.OffIsReal)
                {
                    double tp = sp.Fn == WinFn.Lag ? (c + 1) - f.OffReal : (c + 1) + f.OffReal;
                    target = tp == Math.Floor(tp) && tp >= 1 && tp <= _count ? (long)tp - 1 : -1;
                }

                // Observed: lag with a negative offset finds a row only for -1 (lead looks ahead for any negative offset of lag's size).
                if (sp.Fn == WinFn.Lag && (f.OffIsReal ? f.OffReal < -1.0 : f.Off < -1))
                {
                    target = -1;
                }

                if (!f.OffNull && target >= 0 && target < _count)
                {
                    Mem v = RowAt(target).F[sp.ArgFields[0]];
                    CopyMem(v, ref dst, ref f.ResOwn);
                }
                else if (sp.ArgFields.Length > 2)
                {
                    CopyMem(cur.F[sp.ArgFields[2]], ref dst, ref f.ResOwn);
                }
                else
                {
                    Values.SetNull(ref dst);
                }

                return;
            }

            case WinFn.FirstValue:
            case WinFn.LastValue:
            case WinFn.NthValue:
                f.LastStart = f.Fs;
                f.LastEnd = Math.Max(f.Fe, f.Fs);
                EvaluateValue(f, cur, c, ref dst);
                return;
            default:
                f.LastStart = f.Fs;
                f.LastEnd = Math.Max(f.Fe, f.Fs);
                EvaluateAggregate(f, cur, c, ref dst);
                return;
        }
    }

    /// <summary>A value that is a whole number above zero: an integer, a float with no fraction, or text that is wholly one of them.</summary>
    private static bool PositiveInteger(in Mem m, out long v)
    {
        v = 0;
        Mem x = m;
        if (x.Kind == StorageClass.Text)
        {
            StorageClass k = Values.ParseNumber(x.Bytes, out long pi, out double pd, out bool whole);
            if (!whole)
            {
                return false;
            }

            x = k == StorageClass.Integral ? Mem.FromInt(pi) : Mem.FromReal(pd);
        }

        if (x.Kind == StorageClass.Integral)
        {
            v = x.IntValue;
            return v > 0;
        }

        if (x.Kind == StorageClass.Real && x.RealValue == Math.Floor(x.RealValue) && x.RealValue > 0 && x.RealValue < 9.2e18)
        {
            v = (long)x.RealValue;
            return true;
        }

        return false;
    }

    private void EvaluateValue(FuncState f, Row cur, long c, ref Mem dst)
    {
        WinFuncSpec sp = f.Spec;
        long fs = f.Fs, fe = f.Fe;
        long want = 1;
        if (sp.Fn == WinFn.NthValue)
        {
            bool ok = PositiveInteger(cur.F[sp.ArgFields[1]], out want);

            // Observed: with an EXCLUDE clause the argument is checked only when a row of the frame survives the exclusion.
            if (!ok && (sp.Exclude == WinExclude.NoOthers || AnyIncluded(sp, f, cur, c)))
            {
                throw new OneLiteException(ResultCode.Error, "second argument to nth_value must be a positive integer");
            }

            if (!ok)
            {
                Values.SetNull(ref dst);
                return;
            }
        }

        int arg = sp.ArgFields[0];
        if (fe < fs)
        {
            Values.SetNull(ref dst);
            return;
        }

        if (sp.Fn == WinFn.FirstValue && sp.Start == WinBound.UnboundedPreceding && sp.Exclude == WinExclude.NoOthers)
        {
            if (f.FirstSet)
            {
                CopyMem(f.FirstVal, ref dst, ref f.ResOwn);
            }
            else
            {
                Values.SetNull(ref dst);
            }

            return;
        }

        long found = -1;
        if (sp.Fn == WinFn.LastValue)
        {
            for (long i = fe; i >= fs; i--)
            {
                if (!Excluded(sp, RowAt(i), i, cur, c))
                {
                    found = i;
                    break;
                }
            }
        }
        else
        {
            long seen = 0;
            for (long i = fs; i <= fe; i++)
            {
                if (!Excluded(sp, RowAt(i), i, cur, c) && ++seen == want)
                {
                    found = i;
                    break;
                }
            }
        }

        if (found < 0)
        {
            Values.SetNull(ref dst);
        }
        else
        {
            CopyMem(RowAt(found).F[arg], ref dst, ref f.ResOwn);
        }
    }

    private bool AnyIncluded(WinFuncSpec sp, FuncState f, Row cur, long c)
    {
        for (long i = f.Fs; i <= f.Fe; i++)
        {
            if (!Excluded(sp, RowAt(i), i, cur, c))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Included(FuncState f, Row row)
    {
        int ff = f.Spec.FilterField;
        return ff < 0 || Values.IsTrue(row.F[ff]);
    }

    private static void Gather(FuncState f, Row row, out ReadOnlySpan<Mem> args)
    {
        int[] af = f.Spec.ArgFields;
        for (int i = 0; i < af.Length; i++)
        {
            f.Args[i] = row.F[af[i]];
            if (i < f.Spec.FlagFields.Length)
            {
                f.Args[i].Subtype = f.Args[i].Kind is StorageClass.Text or StorageClass.Blob ? (byte)row.F[f.Spec.FlagFields[i]].IntValue : (byte)0;
            }
        }

        args = new ReadOnlySpan<Mem>(f.Args, 0, af.Length);
    }

    private void Add(FuncState f, long i)
    {
        Row row = RowAt(i);
        if (Included(f, row))
        {
            Gather(f, row, out ReadOnlySpan<Mem> a);
            f.Agg!.Step(a);
        }
    }

    private void Remove(FuncState f, long i)
    {
        Row row = RowAt(i);
        if (Included(f, row))
        {
            Gather(f, row, out ReadOnlySpan<Mem> a);
            f.Agg!.Inverse(a);
        }
    }

    private void EvaluateAggregate(FuncState f, Row cur, long c, ref Mem dst)
    {
        WinFuncSpec sp = f.Spec;
        AggState agg = f.Agg!;
        long fs = f.Fs;
        long hi = Math.Max(f.Fe + 1, fs);
        // Observed: a spelled-out EXCLUDE NO OTHERS builds the frame again for every row (a sum that once held a blob is an integer again).
        if (sp.Exclude == WinExclude.NoOthers && !sp.ExplicitNoOthers && (IsInvertible(sp.Agg!.Kind) || sp.Start == WinBound.UnboundedPreceding))
        {
            bool invertible = IsInvertible(sp.Agg.Kind);
            if (!f.Started)
            {
                if (invertible && sp.Unit == WinUnit.Range && sp.Start == WinBound.Following && hi > fs && fs > 0)
                {
                    // Observed: like a ROWS frame that starts after the current row, but every row enters before the first one leaves.
                    agg.Reset();
                    for (long r = 0; r < hi; r++)
                    {
                        Add(f, r);
                    }

                    for (long j = 0; j < fs; j++)
                    {
                        Remove(f, j);
                    }

                    f.AggLo = fs;
                    f.AggHi = hi;
                }
                else if (invertible && sp.Unit == WinUnit.Rows && sp.Start == WinBound.Following && hi > fs && fs > 0)
                {
                    // Observed (float sums differ in the last digits otherwise): a frame that starts after the current row is built from
                    // row 0, and each row before the frame leaves just before the row one frame-width later arrives.
                    long width = sp.End == WinBound.UnboundedFollowing ? long.MaxValue : hi - fs;
                    agg.Reset();
                    for (long r = 0; r < hi; r++)
                    {
                        long j = width == long.MaxValue ? -1 : r - width;
                        if (j >= 0 && j < fs)
                        {
                            Remove(f, j);
                        }

                        Add(f, r);
                    }

                    if (width == long.MaxValue)
                    {
                        for (long j = 0; j < fs; j++)
                        {
                            Remove(f, j);
                        }
                    }

                    f.AggLo = fs;
                    f.AggHi = hi;
                }

                f.Started = true;
            }

            if (fs < f.AggLo || fs > f.AggHi || hi < f.AggHi || (!invertible && fs != f.AggLo))
            {
                agg.Reset();
                f.AggLo = f.AggHi = fs;
            }

            // Observed (float sums differ in the last digits otherwise): rows leaving the frame come out before rows entering go in.
            // The exception is a RANGE frame that need not hold the current row (it starts FOLLOWING or ends PRECEDING): there the rows
            // enter first, which shows in group_concat, whose text the leaving values cut from the front.
            // The bounds move row by row: a row that is on disk suspends the evaluation, which then resumes from them.
            bool addFirst = sp.Unit == WinUnit.Range && (sp.Start == WinBound.Following || sp.End == WinBound.Preceding);
            for (int step = 0; step < 2; step++)
            {
                if ((step == 0) == addFirst)
                {
                    for (long i = f.AggHi; i < hi; i++)
                    {
                        Add(f, i);
                        f.AggHi = i + 1;
                        _missRun = 0;
                    }

                    f.AggHi = hi;
                }
                else
                {
                    if (invertible)
                    {
                        for (long i = f.AggLo; i < fs; i++)
                        {
                            Remove(f, i);
                            f.AggLo = i + 1;
                            _missRun = 0;
                        }
                    }

                    f.AggLo = fs;
                }
            }
        }
        else
        {
            agg.Reset();
            if (sp.Agg!.Kind is AggKind.Min or AggKind.Max && sp.Exclude == WinExclude.NoOthers && !sp.ExplicitNoOthers)
            {
                // Observed: when a frame is built again for each row, of several equal values the last row of the frame is the one min/max
                // returns (so 100.0 is chosen over 100); a frame that starts at the partition start, or has an EXCLUDE clause, keeps the first.
                for (long i = hi - 1; i >= fs; i--)
                {
                    if (!Excluded(sp, RowAt(i), i, cur, c))
                    {
                        Add(f, i);
                    }
                }
            }
            else
            {
                for (long i = fs; i < hi; i++)
                {
                    if (!Excluded(sp, RowAt(i), i, cur, c))
                    {
                        Add(f, i);
                    }
                }
            }
        }

        agg.Final(ref dst, ref f.ResOwn);
    }
}
