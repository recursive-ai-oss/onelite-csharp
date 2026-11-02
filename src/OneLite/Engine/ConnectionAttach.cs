using RecursiveAI.OneLite.Planning;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Crypto;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite;

// ATTACH and DETACH (lang_attach, lang_detach) and the transactions that span databases (atomiccommit §5). Slot 0 is main, slot 1 is temp,
// and attached databases take slots 2 and up, which is the number a table's Db field carries.
public sealed partial class Connection
{
    /// <summary>The most databases that can be attached at once (SQLITE_MAX_ATTACHED).</summary>
    internal const int MaxAttached = 10;

    internal readonly List<AttachedDb> Attached = [];

    /// <summary>Bumped when a database is attached or detached; folded into the schema cookie so prepared statements notice.</summary>
    internal int AttachEpoch;

    /// <summary>True for a connection opened read-only; attached databases are opened the same way.</summary>
    internal bool OpenedReadOnly { get; set; }

    /// <summary>Backups reading from this connection (their destinations are told about its commits).</summary>
    internal readonly List<Backup> Backups = [];

    /// <summary>Incremental blob handles open on this connection.</summary>
    internal readonly List<Blob> Blobs = [];

    /// <summary>True while a writable incremental blob handle is open: like a running writer, it stops COMMIT.</summary>
    internal bool HasWritableBlob => Blobs.Exists(static b => b.CanWrite);

    /// <summary>A row of a table changed (rowid long.MinValue: every row): expires the blob handles pointing at it.</summary>
    internal void BlobChanged(int db, uint root, long rowid)
    {
        for (int i = 0; i < Blobs.Count; i++)
        {
            Blob b = Blobs[i];
            if (b.Slot == db && b.Root == root && (rowid == long.MinValue || rowid == b.Rowid))
            {
                b.Expire(ResultCode.Abort);
            }
        }
    }

    /// <summary>A transaction was rolled back: the blob handles are aborted.</summary>
    internal void AfterRollback()
    {
        foreach (Blob b in Blobs)
        {
            b.Expire(ResultCode.AbortRollback);
        }
    }

    /// <summary>
    /// Opens an incremental blob handle on one column of one row; see <see cref="Blob.Open"/>.
    /// </summary>
    /// <param name="schema">"main", "temp" or an attached name.</param>
    /// <param name="table">The table.</param>
    /// <param name="column">The column.</param>
    /// <param name="rowid">The row.</param>
    /// <param name="writable">True to allow writes.</param>
    /// <returns>The handle.</returns>
    public Blob OpenBlob(string schema, string table, string column, long rowid, bool writable = false) => Blob.Open(this, schema, table, column, rowid, writable);

    /// <summary>True while an unfinished backup holds this connection's main database open for writing.</summary>
    internal bool BackupDestinationHeld { get; set; }

    /// <summary>Tells the backups reading from this connection that it committed.</summary>
    internal void AfterCommit()
    {
        if (ConcTx is not null)
        {
            ConcurrentRowids.NoteCommit(_pager.FullPath);
        }

        if (Backups.Count > 0)
        {
            foreach (Backup b in Backups.ToArray())
            {
                b.SourceCommitted();
            }
        }
    }

    /// <summary>The database slot of a schema name ("main" 0, "temp" 1, attached 2 and up), or -1.</summary>
    internal int SlotOf(string name)
    {
        if (AsciiComparer.Instance.Equals(name, "main"))
        {
            return 0;
        }

        if (AsciiComparer.Instance.Equals(name, "temp"))
        {
            return 1;
        }

        AttachedDb? a = Attached.Find(x => AsciiComparer.Instance.Equals(x.Name, name));
        return a?.Slot ?? -1;
    }

    internal AttachedDb? AttachedBySlot(int slot) => Attached.Find(a => a.Slot == slot);

    internal Pager PagerOf(int db) => db == 0 ? _pager : db == 1 ? GetTemp().Pager : AttachedBySlot(db)!.Pager;

    /// <summary>The sum of the attached databases' schema cookies and the attach epoch.</summary>
    private uint AttachedCookies()
    {
        uint sum = unchecked((uint)AttachEpoch);
        foreach (AttachedDb a in Attached)
        {
            sum = unchecked(sum + a.ReadCookie());
        }

        return sum;
    }

    /// <summary>Adds the attached databases' schemas to a freshly loaded main catalog. Unqualified names find main's objects first.</summary>
    private void MergeAttached(Catalog cat)
    {
        foreach (AttachedDb a in Attached)
        {
            Catalog ac = a.LoadSchema();
            var sch = new AttachedSchema { Name = a.Name, Slot = a.Slot, Path = a.Path, Tables = ac.Tables, Master = ac.Tables["sqlite_master"], Rows = ac.Rows };
            cat.Attached.Add(sch);
            foreach ((string name, TableInfo t) in ac.Tables)
            {
                if (name is not ("sqlite_master" or "sqlite_schema"))
                {
                    cat.Tables.TryAdd(name, t);
                }
            }

            foreach ((string name, TriggerInfo tr) in ac.Triggers)
            {
                cat.Triggers[a.Name + "\0" + name] = tr;
            }
        }
    }

    /// <summary>ATTACH: opens <paramref name="file"/> as schema <paramref name="name"/>. Runs inline.</summary>
    /// <param name="file">The file name; empty for a private temporary database; ":memory:" for an in-memory one.</param>
    /// <param name="name">The schema name.</param>
    /// <param name="key">The KEY clause, or null when there was none (the attached database then uses main's key when main is encrypted).</param>
    internal void AttachDatabase(string file, string name, string? key)
    {
        if (Attached.Count >= MaxAttached)
        {
            throw new OneLiteException(ResultCode.Error, "too many attached databases - max " + MaxAttached);
        }

        if (AsciiComparer.Instance.Equals(name, "main") || AsciiComparer.Instance.Equals(name, "temp") || Attached.Exists(a => AsciiComparer.Instance.Equals(a.Name, name)))
        {
            throw new OneLiteException(ResultCode.Error, "database " + name + " is already in use");
        }

        string path = file;
        bool ro = OpenedReadOnly;
        bool memory = false;
        if (file.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            // A URI: only the path and the mode parameter are read.
            string rest = file[5..];
            int q = rest.IndexOf('?', StringComparison.Ordinal);
            string query = q >= 0 ? rest[(q + 1)..] : string.Empty;
            path = q >= 0 ? rest[..q] : rest;
            if (path.StartsWith("//", StringComparison.Ordinal))
            {
                int sl = path.IndexOf('/', 2);
                path = sl < 0 ? string.Empty : path[sl..];
            }

            path = Uri.UnescapeDataString(path);
            foreach (string kv in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                if (kv.StartsWith("mode=", StringComparison.Ordinal))
                {
                    string m = kv[5..];
                    ro = m == "ro" || (OpenedReadOnly && m != "memory");
                    memory |= m == "memory";
                }
            }
        }

        memory |= path == ":memory:" || path.Length == 0;
        IVfs vfs = memory ? new MemoryVfs() : _sortVfs ?? FileVfs.Instance;
        if (ro && !memory && !vfs.Access(path, AccessMode.Exists))
        {
            throw new OneLiteException(ResultCode.CantOpen, "unable to open database: " + file);
        }

        int slot = 2;
        while (AttachedBySlot(slot) is not null)
        {
            slot++;
        }

        var po = new PagerOptions
        {
            ReadOnly = ro,
            PageSize = _ctx?.PageSize ?? 4096,
            Encoding = _ctx?.Encoding ?? TextEncoding.Utf8,
            JournalMode = memory && path.Length > 0 ? JournalMode.Memory : JournalMode.Delete,
            Synchronous = _pager.Synchronous,
            CacheSize = -2000,
            Busy = Busy is null ? null : Busy.Next,
        };
        StorageEngine eng;
        try
        {
            eng = StorageEngine.Open(vfs, memory ? name : path, po);
        }
        catch (OneLiteException e) when (e.Code == ResultCode.CantOpen)
        {
            throw new OneLiteException(ResultCode.CantOpen, "unable to open database: " + file, e.Detail);
        }

        var db = new AttachedDb(name, slot, memory ? string.Empty : eng.Pager.FullPath, eng) { IsMemory = memory && path.Length > 0 };
        eng.Changed = (root, row) => BlobChanged(slot, root, row);
        try
        {
            CipherKey? k = key is null ? _pager.CloneKey() : key.Length == 0 ? null : CipherKey.Parse(key);
            if (k is not null)
            {
                using (k)
                {
                    eng.Pager.TryApplyKey(k, key is null ? _pager.CipherSettings ?? CipherSettings.V4 : PragmaSettings.DefaultCipher);
                }
            }

            // The file is read now, so a file that is not a database (or a wrong key) fails the ATTACH.
            db.BeginRead();
            db.EndReadQuiet();
        }
        catch
        {
            db.Dispose();
            throw;
        }

        Attached.Add(db);
        Attached.Sort(static (x, y) => x.Slot.CompareTo(y.Slot));
        AttachEpoch++;
        InvalidateSchema();
    }

    /// <summary>DETACH: closes the attached database <paramref name="name"/>.</summary>
    internal void DetachDatabase(string name)
    {
        if (AsciiComparer.Instance.Equals(name, "main"))
        {
            throw new OneLiteException(ResultCode.Error, "cannot detach database " + name);
        }

        AttachedDb? db = Attached.Find(a => AsciiComparer.Instance.Equals(a.Name, name))
            ?? throw new OneLiteException(ResultCode.Error, "no such database: " + name);
        if (db.Pager.InTransaction)
        {
            throw new OneLiteException(ResultCode.Error, "database " + db.Name + " is locked");
        }

        ReleaseCursorsOf(null);
        Attached.Remove(db);
        db.Dispose();
        AttachEpoch++;
        InvalidateSchema();
    }

    /// <summary>Starts the read (and, when asked, write) transactions of the attached databases a statement uses. Throws SQLITE_BUSY.</summary>
    internal void BeginAttached(int readMask, int writeMask)
    {
        foreach (AttachedDb a in Attached)
        {
            int bit = 1 << a.Slot;
            if ((writeMask & bit) != 0)
            {
                if (ConcTx is not null)
                {
                    throw new OneLiteException(ResultCode.Error, "cannot write to an attached database inside BEGIN CONCURRENT");
                }

                a.BeginWrite();
                // A savepoint opened before this database joined the transaction covers it from here on.
                foreach (SavepointEntry e in Savepoints)
                {
                    e.AttachedLevels ??= new int[MaxAttached + 2];
                    if (e.AttachedLevels[a.Slot] == 0)
                    {
                        e.AttachedLevels[a.Slot] = a.Pager.OpenSavepoint();
                    }
                }
            }
            else if ((readMask & bit) != 0)
            {
                a.BeginRead();
            }
        }
    }

    /// <summary>Opens a savepoint on every attached database that is writing; returns the levels (index = slot), or null if none.</summary>
    internal int[]? OpenAttachedSavepoints()
    {
        int[]? levels = null;
        foreach (AttachedDb a in Attached)
        {
            if (a.Pager.InWriteTransaction)
            {
                levels ??= new int[MaxAttached + 2];
                levels[a.Slot] = a.Pager.OpenSavepoint();
            }
        }

        return levels;
    }

    internal void ReleaseAttachedSavepoints(int[]? levels)
    {
        if (levels is null)
        {
            return;
        }

        foreach (AttachedDb a in Attached)
        {
            if (levels[a.Slot] > 0 && a.Pager.InWriteTransaction)
            {
                a.Pager.ReleaseSavepoint(levels[a.Slot]);
            }
        }
    }

    internal void RollbackAttachedSavepoints(int[]? levels)
    {
        if (levels is null)
        {
            return;
        }

        foreach (AttachedDb a in Attached)
        {
            if (levels[a.Slot] > 0 && a.Pager.InWriteTransaction)
            {
                a.Pager.RollbackSavepointSync(levels[a.Slot]);
            }
        }
    }

    /// <summary>True while an attached database is in a write transaction.</summary>
    internal bool AttachedWriting
    {
        get
        {
            foreach (AttachedDb a in Attached)
            {
                if (a.Pager.InWriteTransaction)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Commits main and every attached database that wrote, together: with a super-journal when two or more changed (atomiccommit §5). Runs inline.
    /// </summary>
    internal void CommitAttachedSync()
    {
        var pagers = new List<Pager>();
        if (_pager.InWriteTransaction)
        {
            pagers.Add(_pager);
        }

        foreach (AttachedDb a in Attached)
        {
            if (a.Pager.InWriteTransaction)
            {
                pagers.Add(a.Pager);
            }
        }

        MultiCommit.CommitSync(pagers);
        foreach (AttachedDb a in Attached)
        {
            a.EndReadQuiet();
        }
    }

    /// <summary>Rolls back and releases the attached databases.</summary>
    internal void RollbackAttached()
    {
        foreach (AttachedDb a in Attached)
        {
            a.Rollback();
        }
    }

    /// <summary>Lets go of the read transactions of attached databases when no statement or transaction of the connection reads any more.</summary>
    private void EndAttachedReads()
    {
        foreach (AttachedDb a in Attached)
        {
            a.EndReadQuiet();
        }
    }
}
