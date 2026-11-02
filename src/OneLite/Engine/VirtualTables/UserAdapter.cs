using System.Linq;
using System.Collections.Immutable;
using RecursiveAI.OneLite.Planning;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.VirtualTables;

/// <summary>The built-in modules, by name.</summary>
internal static class VirtualModules
{
    private static readonly BuiltinModule[] s_all =
    [
        new Extensions.RTree.RTreeModule(false),
        new Extensions.RTree.RTreeModule(true),
        new DbStatModule(),
        new Extensions.Vec.VecModule(),
        new Extensions.Fts3.Fts3Module(false),
        new Extensions.Fts3.Fts3Module(true),
        new Extensions.Fts3.Fts4AuxModule(),
        new Extensions.Fts5.Fts5Module(),
        new Extensions.Fts5.Fts5VocabModule(),
        .. Vm.Modules.VirtualFunctions.Select(d => new FunctionModule(d)),
    ];

    internal static BuiltinModule? FindBuiltin(string name)
    {
        foreach (BuiltinModule m in s_all)
        {
            if (string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return m;
            }
        }

        return null;
    }
}

/// <summary>Runs a user module's synchronous table as a <see cref="VTab"/>: every call completes at once.</summary>
internal sealed class UserVTab : VTab
{
    private readonly IVirtualTable _inner;

    internal UserVTab(IVirtualTable inner) => _inner = inner;

    internal IVirtualTable Inner => _inner;

    internal override void BestIndex(BestIndexInfo info) => _inner.BestIndex(info);

    internal override VCursor Open() => new UserVCursor(_inner.Open());

    internal override bool IsWritable => _inner is IWritableVirtualTable;

    internal override ModStatus Update(ReadOnlySpan<Mem> values, out long rowid)
    {
        if (_inner is not IWritableVirtualTable w)
        {
            return base.Update(values, out rowid);
        }

        rowid = w.Update(values);
        return ModStatus.Ok;
    }

    internal override bool WantsTransactions => _inner is ITransactionalVirtualTable;

    internal override void Begin() => ((ITransactionalVirtualTable)_inner).Begin();

    internal override void Sync() => ((ITransactionalVirtualTable)_inner).Sync();

    internal override void Commit() => ((ITransactionalVirtualTable)_inner).Commit();

    internal override void Rollback() => ((ITransactionalVirtualTable)_inner).Rollback();

    internal override bool WantsSavepoints => _inner is ISavepointVirtualTable;

    internal override void Savepoint(int level) => ((ISavepointVirtualTable)_inner).Savepoint(level);

    internal override void Release(int level) => ((ISavepointVirtualTable)_inner).Release(level);

    internal override void RollbackTo(int level) => ((ISavepointVirtualTable)_inner).RollbackTo(level);

    internal override void Rename(string newName)
    {
        if (_inner is IRenamableVirtualTable r)
        {
            r.Rename(newName);
        }
    }

    internal override void Destroy() => _inner.Destroy();

    public override void Dispose() => _inner.Dispose();
}

internal sealed class UserVCursor : VCursor
{
    private readonly IVirtualTableCursor _inner;

    internal UserVCursor(IVirtualTableCursor inner) => _inner = inner;

    internal override ModStatus Filter(int indexNumber, string? indexString, ReadOnlySpan<Mem> args)
    {
        _inner.Filter(indexNumber, indexString, args);
        return ModStatus.Ok;
    }

    internal override ModStatus Next()
    {
        _inner.MoveNext();
        return ModStatus.Ok;
    }

    internal override bool Eof => _inner.Eof;

    internal override ModStatus Column(int index, ref Mem dst, ref byte[]? own)
    {
        _inner.Column(index, new ColumnResult(ref dst, ref own));
        return ModStatus.Ok;
    }

    internal override ModStatus RowId(out long rowid)
    {
        rowid = _inner.RowId;
        return ModStatus.Ok;
    }

    public override void Dispose() => _inner.Dispose();
}
