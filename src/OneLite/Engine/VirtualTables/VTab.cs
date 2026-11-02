using System.Collections.Immutable;
using RecursiveAI.OneLite.Planning;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.VirtualTables;

/// <summary>What a resumable virtual-table call reports: finished, or waiting for a page (call again with the same arguments).</summary>
internal enum ModStatus : byte
{
    Ok,
    Pending,
}

/// <summary>
/// One virtual table of one connection as the VM sees it. Methods that can touch the database return <see cref="ModStatus"/> and are re-entered
/// with the same arguments after <see cref="ModStatus.Pending"/>; they keep their progress in fields (the suspend-point rules of the engine).
/// Errors are thrown as <see cref="OneLiteException"/>.
/// </summary>
internal abstract class VTab : IDisposable
{
    /// <summary>The table this instance serves (set when it is connected).</summary>
    internal TableInfo Table = null!;

    internal abstract void BestIndex(BestIndexInfo info);

    /// <summary>True when the table wants IN lists and the LIMIT of the query offered as constraints (<see cref="ConstraintOp.In"/>, <see cref="ConstraintOp.Limit"/>); user modules never do.</summary>
    internal virtual bool AcceptsExtendedConstraints => false;

    internal abstract VCursor Open();

    internal virtual bool IsWritable => false;

    /// <summary>Applies one INSERT, UPDATE or DELETE (see <see cref="IWritableVirtualTable.Update"/>).</summary>
    internal virtual ModStatus Update(ReadOnlySpan<Mem> values, out long rowid)
    {
        throw new OneLiteException(ResultCode.Error, "table " + Table.Name + " may not be modified");
    }

    /// <summary>True when the table wants Begin, Sync, Commit and Rollback calls (a table kept entirely in shadow tables does not).</summary>
    internal virtual bool WantsTransactions => false;

    internal virtual void Begin()
    {
    }

    internal virtual void Sync()
    {
    }

    internal virtual void Commit()
    {
    }

    internal virtual void Rollback()
    {
    }

    internal virtual bool WantsSavepoints => false;

    internal virtual void Savepoint(int level)
    {
    }

    internal virtual void Release(int level)
    {
    }

    internal virtual void RollbackTo(int level)
    {
    }

    internal virtual void Rename(string newName)
    {
    }

    /// <summary>DROP TABLE: delete state the module keeps outside the schema.</summary>
    internal virtual void Destroy()
    {
    }

    public abstract void Dispose();
}

/// <summary>A scan position of a <see cref="VTab"/>.</summary>
internal abstract class VCursor : IDisposable
{
    internal abstract ModStatus Filter(int indexNumber, string? indexString, ReadOnlySpan<Mem> args);

    internal abstract ModStatus Next();

    /// <summary>True when there is no current row. Valid after <see cref="Filter"/> or <see cref="Next"/> returned Ok.</summary>
    internal abstract bool Eof { get; }

    /// <summary>Writes column <paramref name="index"/> of the current row into <paramref name="dst"/>.</summary>
    internal abstract ModStatus Column(int index, ref Mem dst, ref byte[]? own);

    /// <summary>The current row's rowid. Valid after <see cref="Filter"/> or <see cref="Next"/> returned Ok.</summary>
    internal abstract ModStatus RowId(out long rowid);

    public abstract void Dispose();
}

/// <summary>A shadow table a built-in module keeps in the database file: created by <c>CREATE VIRTUAL TABLE</c>, dropped with it, renamed with it.</summary>
internal sealed class ShadowTable
{
    /// <summary>The table's name.</summary>
    internal required string Name { get; init; }

    /// <summary>Its <c>CREATE TABLE</c> statement, as stored in the schema.</summary>
    internal required string Sql { get; init; }

    /// <summary>Rows to insert when the table is created: rowid and column values (long, double, string, byte[] or null), in column order.</summary>
    internal (long Rowid, object?[] Values)[] Rows { get; init; } = [];
}

/// <summary>A module the library ships. Its tables keep their data in shadow tables, so transactions, savepoints and rollback come from the pager.</summary>
internal abstract class BuiltinModule
{
    internal abstract string Name { get; }

    /// <summary>True when the module's table exists under the module's name without <c>CREATE VIRTUAL TABLE</c>.</summary>
    internal virtual bool Eponymous => false;

    /// <summary>The shadow tables <c>CREATE VIRTUAL TABLE name USING module(args)</c> makes, in the order they are created; throws for bad arguments with the reference's message.</summary>
    internal virtual ShadowTable[] ShadowTables(string schema, string table, ImmutableArray<string> args, int pageSize) => [];

    /// <summary>The names of the shadow tables of an existing table (for DROP TABLE and RENAME).</summary>
    internal virtual string[] ShadowNames(string table, ImmutableArray<string> args) => [];

    /// <summary>
    /// Makes the table object for a connection. <paramref name="declare"/> must be called once with the <c>CREATE TABLE</c> text that gives the
    /// columns. Called at compile time (for column names) and again to run queries.
    /// </summary>
    internal abstract VTab Connect(Connection conn, TableInfo table, string schema, ImmutableArray<string> args, Action<string> declare);
}

/// <summary>What the catalog knows about a virtual table: its module, its arguments and, once connected, the instance.</summary>
internal sealed class VirtualInfo
{
    internal string Module = "";
    internal ImmutableArray<string> Args;
    internal string Schema = "main";
    internal VTab? Instance;
    internal bool Declared;
}
