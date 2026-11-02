using RecursiveAI.OneLite.Planning;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Crypto;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite;

// An ATTACHed database (lang_attach): a second file with its own pager, B-trees and schema. Its I/O is satisfied inline, the way the temp
// database's is, so a statement that touches it blocks on that file instead of suspending (docs/attach.md).
internal sealed class AttachedDb : IDisposable
{
    internal readonly string Name;
    internal readonly int Slot;
    internal readonly string Path;

    /// <summary>True for a private in-memory database (":memory:", or an empty file name that stays small).</summary>
    internal bool IsMemory { get; init; }
    private readonly StorageEngine _engine;
    private BtContext? _ctx;
    private uint _lastCookie;

    internal AttachedDb(string name, int slot, string path, StorageEngine engine)
    {
        Name = name;
        Slot = slot;
        Path = path;
        _engine = engine;
    }

    internal Pager Pager => _engine.Pager;

    internal StorageEngine Engine => _engine;

    /// <summary>The B-tree context over a page source that satisfies the pager's I/O on the spot.</summary>
    internal BtContext Context
    {
        get
        {
            if (!Pager.InTransaction)
            {
                BeginRead();
            }

            return _ctx!;
        }
    }

    /// <summary>Takes the read lock if the database is not in a transaction yet, and makes the B-tree context for the geometry of page 1.</summary>
    internal void BeginRead()
    {
        if (!Pager.InTransaction)
        {
            _engine.BeginRead();
        }

        _engine.EnsureContext();
        if (_ctx == null || _ctx.PageSize != Pager.PageSize || _ctx.Usable != Pager.UsableSize || _ctx.Encoding != _engine.Context.Encoding)
        {
            _ctx = new BtContext(new InlineSource(_engine.Source), _engine.Context.Encoding) { Changed = _engine.Changed };
        }
    }

    /// <summary>Starts the write transaction (RESERVED, or the WAL write lock). SQLITE_BUSY if another process holds it.</summary>
    internal void BeginWrite()
    {
        BeginRead();
        if (!Pager.InWriteTransaction)
        {
            try
            {
                _engine.BeginWrite();
            }
            catch (OneLiteException)
            {
                EndReadQuiet();
                throw;
            }
        }
    }

    /// <summary>Ends a read-only transaction; one that has changes stays until it commits or rolls back.</summary>
    internal void EndReadQuiet()
    {
        if (Pager.InTransaction && !Pager.InWriteTransaction)
        {
            try
            {
                _engine.EndRead();
            }
            catch (OneLiteException)
            {
                // A lock that cannot be released goes with the descriptor.
            }
        }
    }

    internal void Rollback()
    {
        if (Pager.InWriteTransaction)
        {
            _engine.Rollback();
        }

        EndReadQuiet();
    }

    /// <summary>The schema cookie of the file (header offset 40); the last one seen if the file is busy.</summary>
    internal uint ReadCookie()
    {
        bool began = !Pager.InTransaction;
        try
        {
            BeginRead();
            if (Pager.PageCount == 0)
            {
                return _lastCookie = 0;
            }

            BtContext c = _ctx!;
            if (c.Source.GetPage(1, out PageHandle? h) != BtStatus.Ok)
            {
                return _lastCookie;
            }

            uint v = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(h!.Data.AsSpan(40, 4));
            c.Source.Release(h);
            return _lastCookie = v;
        }
        catch (OneLiteException)
        {
            return _lastCookie;
        }
        finally
        {
            if (began)
            {
                EndReadQuiet();
            }
        }
    }

    /// <summary>Reads the schema of the database into a catalog of its own.</summary>
    internal Catalog LoadSchema()
    {
        bool began = !Pager.InTransaction;
        try
        {
            BeginRead();
            if (Pager.PageCount == 0)
            {
                var empty = new Catalog();
                empty.Tables["sqlite_master"] = SchemaBuilder.SchemaTable(Slot);
                empty.Tables["sqlite_schema"] = empty.Tables["sqlite_master"];
                return empty;
            }

            var loader = new SchemaLoader(_ctx!, Slot);
            BtStatus s = loader.Step(out Catalog? cat);
            if (s != BtStatus.Ok)
            {
                throw Machine.Translate(loader.Error ?? _engine.Source.LastError ?? new InvalidOperationException("schema load failed"));
            }

            return cat!;
        }
        finally
        {
            if (began)
            {
                EndReadQuiet();
            }
        }
    }

    public void Dispose() => _engine.Dispose();
}
