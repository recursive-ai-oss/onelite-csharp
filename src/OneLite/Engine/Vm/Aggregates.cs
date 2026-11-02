using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Vm;

internal enum AggKind : byte
{
    Count,
    CountStar,
    Sum,
    Total,
    Avg,
    Min,
    Max,
    GroupConcat,
    JsonGroupArray,
    JsonbGroupArray,
    JsonGroupObject,
    JsonbGroupObject,
}

/// <summary>An aggregate call as compiled: which function, whether its arguments are de-duplicated, how its input is ordered (lang_aggfunc).</summary>
internal sealed class AggSpec
{
    internal AggKind Kind;
    internal bool Distinct;
    internal CollKind Coll;
    internal int NArgs;

    /// <summary>Terms of <c>f(x ORDER BY ...)</c>; empty when the call has no ORDER BY. The sorted input is [keys..., args...].</summary>
    internal SortTerm[] Order = [];
}

/// <summary>The running state of one aggregate call for one group.</summary>
internal sealed class AggState
{
    private readonly AggSpec _spec;
    private readonly TextEncoding _enc;
    private long _count;
    private long _isum;
    private bool _overflow;
    private bool _approx;
    private bool _compensated;
    private double _rsum, _rerr;
    private bool _any;
    private Mem _best;
    private byte[]? _bestOwn;
    private List<byte>? _concat;
    private bool _sepNext;

    // group_concat as a window function: the length of each value in the frame and of the separator given with it, oldest first.
    private List<(int Value, int Sep)>? _frameLens;
    private int _frameHead;
    private JsonBuf? _json;
    private SetStore? _seen;
    private List<byte[]>? _ordered;
    private bool _bestSet;

    internal AggState(AggSpec spec, TextEncoding enc)
    {
        _spec = spec;
        _enc = enc;
        Reset();
    }

    internal void Reset()
    {
        _count = 0;
        _isum = 0;
        _overflow = false;
        _approx = false;
        _compensated = false;
        _rsum = 0;
        _rerr = 0;
        _any = false;
        _bestSet = false;
        _best = default;
        _concat?.Clear();
        _sepNext = false;
        _frameLens?.Clear();
        _frameHead = 0;
        _json?.Clear();
        _seen = _spec.Distinct ? new SetStore(new SetSpec { Collations = [_spec.Coll] }, _enc) : null;
        _ordered = _spec.Order.Length > 0 ? [] : null;
    }

    /// <summary>Makes a group_concat take rows back out (a window frame); see <see cref="Inverse"/>.</summary>
    internal void EnableInverse()
    {
        if (_spec.Kind == AggKind.GroupConcat)
        {
            _frameLens ??= [];
        }
    }

    /// <summary>Feeds one row. Returns true if the call changed its "best" value (min and max), which is what picks the row bare columns come from.</summary>
    internal bool Step(ReadOnlySpan<Mem> args)
    {
        if (_ordered is not null)
        {
            if (_spec.Kind >= AggKind.JsonGroupArray)
            {
                // The sorted rows are stored as records, which do not carry the JSON subtype; one flag column per argument does.
                var withFlags = new Mem[args.Length + _spec.NArgs];
                args.CopyTo(withFlags);
                for (int i = 0; i < _spec.NArgs; i++)
                {
                    Values.SetInt(ref withFlags[args.Length + i], args[args.Length - _spec.NArgs + i].Subtype == JsonFunctions.JsonSubtype ? 1 : 0);
                }

                _ordered.Add(RecordWriter.ToArray(withFlags));
                return false;
            }

            _ordered.Add(RecordWriter.ToArray(args));
            return false;
        }

        return Apply(args);
    }

    /// <summary>
    /// Takes a row back out of a sliding window frame (windowfunctions: an aggregate with an inverse step). Only count, sum, total and
    /// avg have one; a float seen once keeps the sum a float, as in a plain run.
    /// </summary>
    internal void Inverse(ReadOnlySpan<Mem> args)
    {
        if (_spec.Kind != AggKind.CountStar && args[0].Kind == StorageClass.Null)
        {
            return;
        }

        switch (_spec.Kind)
        {
            case AggKind.CountStar:
            case AggKind.Count:
                _count--;
                return;
            case AggKind.Sum:
            case AggKind.Total:
            case AggKind.Avg:
                RemoveNumber(args[0]);
                _any = _count > 0;
                return;
            case AggKind.GroupConcat:
            {
                // Observed: the oldest value leaves together with the separator that came after it (the next value's), as many characters
                // off the front of the text as they are long, whatever is there; when nothing is left the next row starts the text again
                // without a separator. The result is NULL only when no value is left in the frame.
                if (_frameLens is { } lens && _frameHead < lens.Count && _concat is { } cc)
                {
                    int n = lens[_frameHead].Value + (_frameHead + 1 < lens.Count ? lens[_frameHead + 1].Sep : 0);
                    _frameHead++;
                    cc.RemoveRange(0, Math.Min(n, cc.Count));
                    if (cc.Count == 0)
                    {
                        _sepNext = false;
                    }

                    _any = _frameHead < lens.Count;
                }

                return;
            }

            default:
                throw new InvalidOperationException("aggregate has no inverse");
        }
    }

    private void RemoveNumber(in Mem m)
    {
        _count--;
        StorageClass k = NumericKind(m, out long iv, out double dv);
        if (k == StorageClass.Integral)
        {
            if (iv is <= -4503599627370496L or >= 4503599627370496L)
            {
                long low = iv & 0xfff;
                KahanAdd(-(double)(iv - low));
                KahanAdd(-(double)low);
            }
            else
            {
                KahanAdd(-(double)iv);
            }

            if (!_approx)
            {
                long s = _isum - iv;
                if (((_isum ^ iv) & (_isum ^ s)) < 0)
                {
                    _overflow = true;
                }
                else
                {
                    _isum = s;
                }
            }
        }
        else
        {
            _approx = true;
            KahanAdd(-dv);
        }
    }

    private bool Apply(ReadOnlySpan<Mem> args)
    {
        if (_spec.Kind >= AggKind.JsonGroupArray)
        {
            return ApplyJson(args);
        }

        if (_spec.Kind != AggKind.CountStar)
        {
            if (args[0].Kind == StorageClass.Null)
            {
                // Observed: until min() or max() has a value, every row counts as a change, so bare columns come from the last row.
                return _spec.Kind is AggKind.Min or AggKind.Max && !_bestSet;
            }

            if (_seen is not null && !_seen.Add(args[..1]))
            {
                return false;
            }
        }

        switch (_spec.Kind)
        {
            case AggKind.CountStar:
            case AggKind.Count:
                _count++;
                return false;
            case AggKind.Sum:
            case AggKind.Total:
            case AggKind.Avg:
                AddNumber(args[0]);
                return false;
            case AggKind.Min:
            case AggKind.Max:
            {
                if (_bestSet)
                {
                    int c = Values.Compare(args[0], _best, _spec.Coll, _enc);
                    if (_spec.Kind == AggKind.Min ? c >= 0 : c <= 0)
                    {
                        return false;
                    }
                }

                _bestSet = true;
                CopyValue(args[0], ref _best, ref _bestOwn);
                return true;
            }

            default:
            {
                _concat ??= [];
                Span<byte> scratch = stackalloc byte[Values.MaxRealText];
                if (_sepNext)
                {
                    if (args.Length > 1)
                    {
                        if (args[1].Kind != StorageClass.Null)
                        {
                            _concat.AddRange(Values.AsText(args[1], scratch).ToArray());
                        }
                    }
                    else
                    {
                        _concat.Add((byte)',');
                    }
                }

                _any = true;
                _sepNext = true;
                int before = _concat.Count;
                _concat.AddRange(args[0].Kind == StorageClass.Blob ? args[0].Bytes.ToArray() : Values.AsText(args[0], scratch).ToArray());
                _frameLens?.Add((_concat.Count - before, args.Length > 1 ? (args[1].Kind == StorageClass.Null ? 0 : Values.AsText(args[1], scratch).Length) : 1));
                return false;
            }
        }
    }

    /// <summary>json_group_array and json_group_object keep the elements as JSONB payload and wrap them in a container at the end (json1).</summary>
    private bool ApplyJson(ReadOnlySpan<Mem> args)
    {
        bool obj = _spec.Kind is AggKind.JsonGroupObject or AggKind.JsonbGroupObject;
        if (obj && args[0].Kind == StorageClass.Null)
        {
            // A NULL label skips the row.
            return false;
        }

        if (_seen is not null && !_seen.Add(args[..1]))
        {
            return false;
        }

        _json ??= new JsonBuf();
        _any = true;
        if (obj)
        {
            Span<byte> scratch = stackalloc byte[Values.MaxRealText];
            JsonFunctions.PutString(_json, args[0].Kind == StorageClass.Blob ? args[0].Bytes : Values.AsText(args[0], scratch));
            JsonFunctions.PutValue(args[1], _json);
        }
        else
        {
            JsonFunctions.PutValue(args[0], _json);
        }

        return false;
    }

    private static void CopyValue(in Mem src, ref Mem dst, ref byte[]? own)
    {
        if (src.Kind is StorageClass.Text or StorageClass.Blob)
        {
            Values.SetBytes(ref dst, ref own, src.Kind, src.Bytes);
        }
        else
        {
            dst = src;
        }
    }

    /// <summary>A NaN result (infinity minus infinity) is stored as NULL, as everywhere in the reference.</summary>
    private static void SetRealNullIfNaN(ref Mem dst, double v)
    {
        if (double.IsNaN(v))
        {
            Values.SetNull(ref dst);
        }
        else
        {
            Values.SetReal(ref dst, v);
        }
    }

    private void KahanAdd(double r)
    {
        // Kahan-Babuska-Neumaier compensated summation.
        double s = _rsum;
        double t = s + r;
        if (double.IsFinite(t))
        {
            _rerr += Math.Abs(s) > Math.Abs(r) ? (s - t) + r : (r - t) + s;
        }

        _rsum = t;
    }

    private void AddNumber(in Mem m)
    {
        _count++;
        _any = true;
        StorageClass k = NumericKind(m, out long iv, out double dv);
        if (k == StorageClass.Integral)
        {
            if (!_compensated)
            {
                long s = _isum + iv;
                if (((s ^ _isum) & (s ^ iv)) >= 0)
                {
                    _isum = s;
                    return;
                }

                // The exact integer sum overflowed: sum() will report it unless a real turns up, and the compensated sum takes over.
                _overflow = true;
                StartCompensated();
            }

            AddIntCompensated(iv);
        }
        else
        {
            _approx = true;
            if (!_compensated)
            {
                StartCompensated();
            }

            KahanAdd(dv);
        }
    }

    // Observed against the pinned shell (random mixes of integers beyond 2^52 and reals that cancel): the exact integer sum is kept until a
    // real or an overflow, then it enters the compensated sum as a multiple of 16384 and the remainder (the remainder keeps the sign).
    private void StartCompensated()
    {
        _compensated = true;
        long v = _isum;
        if (v is <= -4503599627370496L or >= 4503599627370496L)
        {
            long low = v % 16384;
            _rsum = v - low;
            _rerr = low;
        }
        else
        {
            _rsum = v;
            _rerr = 0;
        }
    }

    private void AddIntCompensated(long iv)
    {
        if (iv is <= -4503599627370496L or >= 4503599627370496L)
        {
            long low = iv % 16384;
            KahanAdd((double)(iv - low));
            KahanAdd((double)low);
        }
        else
        {
            KahanAdd(iv);
        }
    }

    private double SumValue() => _compensated ? _rsum + _rerr : (double)_isum;

    /// <summary>The numeric class sum() sees: integers and text that is wholly an integer count as integers, everything else as a float (lang_aggfunc "sum").</summary>
    private static StorageClass NumericKind(in Mem m, out long iv, out double dv)
    {
        iv = 0;
        dv = 0;
        switch (m.Kind)
        {
            case StorageClass.Integral:
                iv = m.IntValue;
                return StorageClass.Integral;
            case StorageClass.Real:
                dv = m.RealValue;
                return StorageClass.Real;
            case StorageClass.Text:
            {
                StorageClass k = Values.ParseNumber(m.Bytes, out long pi, out double pd, out bool whole);
                if (k == StorageClass.Integral && whole)
                {
                    iv = pi;
                    return StorageClass.Integral;
                }

                dv = k == StorageClass.Integral ? pi : k == StorageClass.Real ? pd : 0;
                return StorageClass.Real;
            }

            default:
            {
                StorageClass k = Values.ToNumeric(m, out long pi, out double pd);
                dv = k == StorageClass.Integral ? pi : pd;
                return StorageClass.Real;
            }
        }
    }

    /// <summary>Writes the result into <paramref name="dst"/>.</summary>
    internal void Final(ref Mem dst, ref byte[]? own)
    {
        if (_ordered is not null)
        {
            SortTerm[] terms = _spec.Order;
            var sorter = new RecordSorter(terms, _enc, false, long.MaxValue);
            foreach (byte[] r in _ordered)
            {
                sorter.Add(r);
            }

            _ordered = null;
            var view = new RecordView();
            var args = new Mem[_spec.NArgs];
            if (sorter.SortInMemory())
            {
                do
                {
                    byte[] row = sorter.Current;
                    view.Parse(row, 0, row.Length);
                    for (int i = 0; i < args.Length; i++)
                    {
                        view.Load(terms.Length + i, ref args[i]);
                        if (_spec.Kind >= AggKind.JsonGroupArray)
                        {
                            Mem flag = default;
                            view.Load(terms.Length + args.Length + i, ref flag);
                            if (flag.IntValue == 1)
                            {
                                args[i].Subtype = JsonFunctions.JsonSubtype;
                            }
                        }
                    }

                    Apply(args);
                }
                while (sorter.NextInMemory());
            }
        }

        switch (_spec.Kind)
        {
            case AggKind.CountStar:
            case AggKind.Count:
                Values.SetInt(ref dst, _count);
                return;
            case AggKind.Sum:
                if (!_any)
                {
                    Values.SetNull(ref dst);
                }
                else if (_approx)
                {
                    SetRealNullIfNaN(ref dst, _rsum + _rerr);
                }
                else if (_overflow)
                {
                    throw new OneLiteException(ResultCode.Error, "integer overflow");
                }
                else
                {
                    Values.SetInt(ref dst, _isum);
                }

                return;
            case AggKind.Total:
                SetRealNullIfNaN(ref dst, _any ? SumValue() : 0.0);
                return;
            case AggKind.Avg:
                if (_count == 0)
                {
                    Values.SetNull(ref dst);
                }
                else
                {
                    SetRealNullIfNaN(ref dst, SumValue() / _count);
                }

                return;
            case AggKind.Min:
            case AggKind.Max:
                if (!_bestSet)
                {
                    Values.SetNull(ref dst);
                }
                else if (_best.Kind is StorageClass.Text or StorageClass.Blob)
                {
                    Values.SetBytes(ref dst, ref own, _best.Kind, _best.Bytes);
                }
                else
                {
                    dst = _best;
                }

                return;
            case AggKind.JsonGroupArray:
            case AggKind.JsonbGroupArray:
            case AggKind.JsonGroupObject:
            case AggKind.JsonbGroupObject:
                JsonFunctions.GroupResult(_json, _spec.Kind, ref dst, ref own);
                return;
            default:
                if (!_any)
                {
                    Values.SetNull(ref dst);
                }
                else
                {
                    Values.SetBytes(ref dst, ref own, StorageClass.Text, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_concat));
                }

                return;
        }
    }
}
