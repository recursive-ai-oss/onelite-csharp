using System.Buffers.Binary;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Planning;

namespace RecursiveAI.OneLite.VirtualTables;

/// <summary>
/// A rowid B-tree of a shadow table, opened on the table's current root. Calls return <see cref="ModStatus.Pending"/> when a page is missing and
/// are repeated with the same arguments. The root is looked up in the catalog each time a tree is made, because auto-vacuum can move roots.
/// </summary>
internal sealed class ShadowTree : IDisposable
{
    private readonly BTree _tree;
    private readonly RecordView _view = new();
    private bool _parsed;

    internal ShadowTree(Connection conn, string schema, string table)
    {
        Catalog cat = conn.Catalog ?? throw new OneLiteException(ResultCode.Corrupt, "database disk image is malformed", "no schema for shadow table " + table);
        TableInfo t = cat.FindIn(schema, table) ?? throw new OneLiteException(ResultCode.Error, "no such table: " + table);
        _tree = BTree.Table(conn.ContextOf(t.Db), t.Root);
        Cursor = _tree.OpenCursor();
    }

    internal BtCursor Cursor { get; }

    /// <summary>True when the cursor stands on a row.</summary>
    internal bool Valid => Cursor.State == CursorState.Valid;

    internal long Rowid => Cursor.Rowid;

    /// <summary>The columns of the row the cursor stands on, after <see cref="Load"/>.</summary>
    internal RecordView Row => _view;

    private ModStatus Check(BtStatus s)
    {
        if (s == BtStatus.Error)
        {
            throw Vm.Machine.Translate(Cursor.Error ?? new InvalidOperationException("cursor failed without an error"));
        }

        return s == BtStatus.Pending ? ModStatus.Pending : ModStatus.Ok;
    }

    /// <summary>Moves to the row with this rowid; <paramref name="found"/> says whether it exists.</summary>
    internal ModStatus Find(long rowid, out bool found)
    {
        _parsed = false;
        found = false;
        ModStatus s = Check(Cursor.MoveToRowid(rowid, out int cmp));
        if (s == ModStatus.Ok)
        {
            found = Cursor.State == CursorState.Valid && cmp == 0;
        }

        return s;
    }

    /// <summary>Moves to the last row (a tree with no rows leaves <see cref="Valid"/> false).</summary>
    internal ModStatus Last()
    {
        _parsed = false;
        return Check(Cursor.Last());
    }

    /// <summary>Moves to the first row.</summary>
    internal ModStatus First()
    {
        _parsed = false;
        return Check(Cursor.First());
    }

    /// <summary>Moves to the next row.</summary>
    internal ModStatus Advance()
    {
        _parsed = false;
        return Check(Cursor.Next());
    }

    /// <summary>Loads and parses the current row.</summary>
    internal ModStatus Load()
    {
        if (_parsed)
        {
            return ModStatus.Ok;
        }

        ModStatus s = Check(Cursor.LoadPayload());
        if (s != ModStatus.Ok)
        {
            return s;
        }

        if (!Cursor.ParseRecord(_view))
        {
            throw new OneLiteException(ResultCode.Corrupt, "database disk image is malformed", "a record in a shadow table is malformed");
        }

        _parsed = true;
        return ModStatus.Ok;
    }

    /// <summary>Inserts or replaces the row with this rowid.</summary>
    internal ModStatus Put(long rowid, ReadOnlySpan<Mem> values)
    {
        _parsed = false;
        return Check(Cursor.Insert(rowid, RecordWriter.ToArray(values)));
    }

    /// <summary>Deletes the row the cursor stands on.</summary>
    internal ModStatus Delete()
    {
        _parsed = false;
        return Check(Cursor.Delete());
    }

    public void Dispose() => Cursor.Dispose();
}
