using System.Collections.Immutable;
using RecursiveAI.OneLite.Planning;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite.VirtualTables;

/// <summary>
/// A built-in table-valued function (<c>generate_series</c>, <c>json_each</c>, <c>json_tree</c> and the JSONB twins) as an eponymous virtual table
/// (vtab, series, json1): the arguments of <c>FROM f(a, b)</c> and equalities on the hidden columns reach the scan through <c>BestIndex</c>
/// like any other virtual table's constraints. The functions work on memory only, so no call here ever asks for I/O.
/// </summary>
internal sealed class FunctionModule(ModuleDef def) : BuiltinModule
{
    internal override string Name => def.Name;

    internal override bool Eponymous => true;

    internal ModuleDef Def => def;

    internal override VTab Connect(Connection conn, TableInfo table, string schema, ImmutableArray<string> args, Action<string> declare)
    {
        // The columns of the functions carry no declared type, so values keep the class the function gave them.
        var sb = new System.Text.StringBuilder("CREATE TABLE x(");
        for (int i = 0; i < def.Columns.Length; i++)
        {
            sb.Append(i > 0 ? "," : "").Append(def.Columns[i]).Append(i >= def.HiddenFrom ? " HIDDEN" : "");
        }

        declare(sb.Append(')').ToString());
        return new FunctionVTab(conn, def);
    }
}

internal sealed class FunctionVTab(Connection conn, ModuleDef def) : VTab
{
    internal override void BestIndex(BestIndexInfo info)
    {
        // The hidden columns are the function's arguments, in order. Every usable equality on one is passed (the first for each column),
        // in column order, and IndexNumber says which columns they are: bit k is hidden column k.
        int hiddenCount = def.Columns.Length - def.HiddenFrom;
        int mask = 0;
        for (int k = 0; k < hiddenCount; k++)
        {
            for (int i = 0; i < info.Constraints.Length; i++)
            {
                IndexConstraint c = info.Constraints[i];
                if (c.Usable && c.Op == ConstraintOp.Eq && c.Column == def.HiddenFrom + k)
                {
                    info.ConstraintUsage[i].ArgvIndex = BitCount(mask) + 1;
                    info.ConstraintUsage[i].Omit = true;
                    mask |= 1 << k;
                    break;
                }
            }
        }

        // The arguments are a prefix of the hidden columns plus any gaps, which stay NULL; the function decides what too few means.
        int count = mask == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)mask);
        if (def.TooFewMessage(count) is { } msg)
        {
            throw new OneLiteException(ResultCode.Error, msg);
        }

        info.IndexNumber = mask;
        info.EstimatedCost = 1;
        info.EstimatedRows = 1000;
    }

    private static int BitCount(int v) => System.Numerics.BitOperations.PopCount((uint)v);

    internal override VCursor Open() => new FunctionVCursor(conn, def);

    public override void Dispose()
    {
    }
}

internal sealed class FunctionVCursor(Connection conn, ModuleDef def) : VCursor
{
    private readonly ModuleCursor _inner = Make(conn, def);
    private readonly Mem[] _args = new Mem[def.Columns.Length - def.HiddenFrom];

    private static ModuleCursor Make(Connection conn, ModuleDef def)
    {
        ModuleCursor c = def.Create();
        c.Bind(conn);
        return c;
    }

    internal override ModStatus Filter(int indexNumber, string? indexString, ReadOnlySpan<Mem> args)
    {
        // Put each argument in the place of its hidden column; a column nobody gave a value is NULL.
        int used = 0, taken = 0;
        for (int k = 0; k < _args.Length; k++)
        {
            if ((indexNumber & (1 << k)) != 0)
            {
                _args[k] = args[taken++];
                used = k + 1;
            }
            else
            {
                _args[k] = default;
                Values.SetNull(ref _args[k]);
            }
        }

        _inner.Filter(new ReadOnlySpan<Mem>(_args, 0, used));
        return ModStatus.Ok;
    }

    internal override ModStatus Next()
    {
        _inner.Next();
        return ModStatus.Ok;
    }

    internal override bool Eof => _inner.AtEnd;

    internal override ModStatus Column(int index, ref Mem dst, ref byte[]? own)
    {
        _inner.Column(index, ref dst, ref own);
        return ModStatus.Ok;
    }

    internal override ModStatus RowId(out long rowid)
    {
        rowid = _inner.RowNumber;
        return ModStatus.Ok;
    }

    public override void Dispose()
    {
    }
}
