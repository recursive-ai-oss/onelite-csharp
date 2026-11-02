using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite.Extensions.Vec;

/// <summary><c>vec_each(vector)</c>: one row per element, with the element as <c>value</c> and its position as <c>rowid</c>. Bits come most significant first, unlike <c>vec_to_json</c>, as in the reference.</summary>
internal sealed class VecEachCursor : ModuleCursor
{
    private VecValue _v;
    private int _i;
    private int _n;

    internal override bool AtEnd => _i >= _n;

    internal override long RowNumber => _i;

    internal override void Filter(ReadOnlySpan<Mem> args)
    {
        _v = VecValue.Read(args[0]);
        _n = _v.Dimensions;
        _i = 0;
    }

    internal override void Next() => _i++;

    internal override void Column(int index, ref Mem dst, ref byte[]? own)
    {
        if (index != 0)
        {
            Values.SetNull(ref dst);
            return;
        }

        switch (_v.Type)
        {
            case VecElement.Float32:
                Values.SetReal(ref dst, _v.Floats[_i]);
                break;
            case VecElement.Int8:
                Values.SetInt(ref dst, _v.Int8s[_i]);
                break;
            default:
                Values.SetInt(ref dst, (_v.Bytes.Span[_i / 8] >> (7 - (_i % 8))) & 1);
                break;
        }
    }
}
