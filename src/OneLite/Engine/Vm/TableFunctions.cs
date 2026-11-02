using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Vm;

/// <summary>
/// A table-valued function's cursor (the built-in modules <c>json_each</c>, <c>json_tree</c> and <c>generate_series</c>). The VM calls
/// <see cref="Filter"/> once per scan with the function's arguments, then <see cref="Next"/> until <see cref="AtEnd"/>. The built-in modules work
/// on memory only, so none of these calls can ask for I/O.
/// </summary>
internal abstract class ModuleCursor
{
    /// <summary>Gives the cursor the connection it runs on, before the first <see cref="Filter"/>. Most modules need nothing from it.</summary>
    internal virtual void Bind(Connection connection)
    {
    }

    /// <summary>Starts a scan with the arguments of the FROM item; throws for arguments the function rejects.</summary>
    internal abstract void Filter(ReadOnlySpan<Mem> args);

    /// <summary>True when there is no current row.</summary>
    internal abstract bool AtEnd { get; }

    internal abstract void Next();

    /// <summary>Writes column <paramref name="index"/> of the current row (hidden columns included) into <paramref name="dst"/>.</summary>
    internal abstract void Column(int index, ref Mem dst, ref byte[]? own);

    /// <summary>The value of <c>rowid</c> for the current row.</summary>
    internal abstract long RowNumber { get; }
}

/// <summary>A table-valued function: its columns, which of them are arguments, and how many arguments it takes.</summary>
internal sealed class ModuleDef
{
    internal required string Name { get; init; }

    /// <summary>All columns in order; those from <see cref="HiddenFrom"/> on are hidden arguments (left out of <c>*</c>).</summary>
    internal required string[] Columns { get; init; }

    internal required int HiddenFrom { get; init; }

    internal required int MaxArgs { get; init; }

    internal required Func<ModuleCursor> Create { get; init; }

    /// <summary>The message for a call with too few arguments, or null if none is an error.</summary>
    internal string? TooFewMessage(int args) => Name == "generate_series" && args < 1 ? "first argument to \"generate_series()\" missing or unusable" : null;
}

/// <summary>The built-in table-valued functions, by name.</summary>
internal static class Modules
{
    private static readonly string[] s_jsonColumns = ["key", "value", "type", "atom", "id", "parent", "fullkey", "path", "json", "root"];
    private static readonly string[] s_seriesColumns = ["value", "start", "stop", "step"];

    private static readonly ModuleDef s_each = new() { Name = "json_each", Columns = s_jsonColumns, HiddenFrom = 8, MaxArgs = 2, Create = () => new JsonTableCursor(false, false) };
    private static readonly ModuleDef s_tree = new() { Name = "json_tree", Columns = s_jsonColumns, HiddenFrom = 8, MaxArgs = 2, Create = () => new JsonTableCursor(true, false) };
    private static readonly ModuleDef s_beach = new() { Name = "jsonb_each", Columns = s_jsonColumns, HiddenFrom = 8, MaxArgs = 2, Create = () => new JsonTableCursor(false, true) };
    private static readonly ModuleDef s_btree = new() { Name = "jsonb_tree", Columns = s_jsonColumns, HiddenFrom = 8, MaxArgs = 2, Create = () => new JsonTableCursor(true, true) };
    private static readonly ModuleDef s_series = new() { Name = "generate_series", Columns = s_seriesColumns, HiddenFrom = 1, MaxArgs = 3, Create = () => new SeriesCursor() };

    private static readonly ModuleDef s_vecEach = new() { Name = "vec_each", Columns = ["value", "vector"], HiddenFrom = 1, MaxArgs = 1, Create = () => new Extensions.Vec.VecEachCursor() };

    internal static ModuleDef? Find(string name)
    {
        if (name.Equals("vec_each", StringComparison.OrdinalIgnoreCase))
        {
            return s_vecEach;
        }

        if (name.StartsWith("pragma_", StringComparison.OrdinalIgnoreCase))
        {
            return PragmaCursor.Find(name[7..]);
        }

        return null;
    }

    /// <summary>The functions that run as eponymous virtual tables (see <c>FunctionModule</c>); the others are still driven directly.</summary>
    internal static ModuleDef[] VirtualFunctions => [s_each, s_tree, s_beach, s_btree, s_series];
}

/// <summary>generate_series(start, stop, step) (the series extension): integers from start towards stop by step.</summary>
internal sealed class SeriesCursor : ModuleCursor
{
    private long _start, _stop, _step;
    private long _value;
    private bool _eof;

    internal override bool AtEnd => _eof;

    internal override long RowNumber => _value;

    internal override void Filter(ReadOnlySpan<Mem> args)
    {
        _eof = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Kind == StorageClass.Null)
            {
                // A NULL argument gives no rows.
                _eof = true;
                return;
            }
        }

        _start = args.Length > 0 ? Values.ToInt(args[0]) : 0;
        _stop = args.Length > 1 ? Values.ToInt(args[1]) : 0xFFFFFFFFL;
        _step = args.Length > 2 ? Values.ToInt(args[2]) : 1;
        if (_step == 0)
        {
            // observed: a step of zero counts as one
            _step = 1;
        }

        _value = _start;
        _eof = _step > 0 ? _start > _stop : _start < _stop;
    }

    internal override void Next()
    {
        // The next value is computed without overflow: stepping past the end of the integer range ends the series.
        long remaining = _step > 0 ? (long)((ulong)_stop - (ulong)_value) : (long)((ulong)_value - (ulong)_stop);
        ulong stepSize = _step > 0 ? (ulong)_step : (ulong)(-(_step + 1)) + 1UL;
        if ((ulong)remaining < stepSize)
        {
            _eof = true;
            return;
        }

        _value = unchecked(_value + _step);
    }

    internal override void Column(int index, ref Mem dst, ref byte[]? own)
    {
        switch (index)
        {
            case 0:
                Values.SetInt(ref dst, _value);
                break;
            case 1:
                Values.SetInt(ref dst, _start);
                break;
            case 2:
                Values.SetInt(ref dst, _stop);
                break;
            default:
                Values.SetInt(ref dst, _step);
                break;
        }
    }

}
