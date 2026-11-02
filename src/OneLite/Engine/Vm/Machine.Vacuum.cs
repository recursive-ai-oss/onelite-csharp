using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Crypto;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Vm;

// VACUUM (lang_vacuum): the database is rewritten without free pages, and a pending PRAGMA auto_vacuum change takes effect.
// The work runs to completion inside one opcode and satisfies its page I/O inline (an async-only backend blocks here); the new image is built
// in memory, so an encrypted database writes no plaintext anywhere but through the pager.
internal sealed partial class Machine
{
    private RunResult DoVacuum(int db)
    {
        Connection c = _conn;
        Pager pg = c.PagerOf(db);
        if (!c.AutoCommit)
        {
            throw new OneLiteException(ResultCode.Error, "cannot VACUUM from within a transaction");
        }

        if (c.ActiveReaders > 1)
        {
            throw new OneLiteException(ResultCode.Error, "cannot VACUUM - SQL statements in progress");
        }

        int mode = db == 0 && c.PendingAutoVacuum >= 0 ? c.PendingAutoVacuum : (pg.AutoVacuum ? (pg.IncrementalVacuum ? 2 : 1) : 0);
        if (db == 0)
        {
            c.PendingAutoVacuum = -1;
        }

        if (pg.PageCount == 0)
        {
            pg.TrySetAutoVacuum(mode != 0, mode == 2);
            _pc++;
            return RunResult.Done;
        }

        void Pump() => pg.ExecuteSync();
        try
        {
            BtContext ctx = c.ContextOf(db);
            IBtPageSource src = ctx.Source;
            BtStatus Run(Func<BtStatus> op)
            {
                BtStatus s;
                while ((s = op()) == BtStatus.Pending)
                {
                    Pump();
                }

                if (s == BtStatus.Error)
                {
                    throw src.LastError!;
                }

                return s;
            }

            var vi = new VacuumImage(ctx, Pump, mode != 0);
            var rootMap = new Dictionary<uint, uint>();
            var updates = new List<(long Rowid, byte[] Record)>();

            // sqlite_schema in rowid order: every table and index gets the next root page number, and its row is rewritten with it.
            using (BtCursor cur = BTree.Table(ctx, 1).OpenCursor())
            {
                var view = new RecordView();
                var vals = new Mem[5];
                Run(cur.First);
                while (cur.State == CursorState.Valid)
                {
                    Run(cur.LoadPayload);
                    if (!cur.ParseRecord(view) || view.ColumnCount < 5)
                    {
                        throw OneLiteException.Corrupt(1, "a sqlite_schema row is not a five-column record");
                    }

                    for (int i = 0; i < 5; i++)
                    {
                        view.Load(i, ref vals[i]);
                    }

                    long old = vals[3].Kind == StorageClass.Integral ? vals[3].IntValue : 0;
                    if (old > 1)
                    {
                        if (old > pg.PageCount || rootMap.ContainsKey((uint)old))
                        {
                            throw OneLiteException.Corrupt(1, $"sqlite_schema names root page {old} twice or outside the database");
                        }

                        uint nr = vi.NextRoot();
                        rootMap[(uint)old] = nr;
                        if (nr != old)
                        {
                            Values.SetInt(ref vals[3], nr);
                            updates.Add((cur.Rowid, RecordWriter.ToArray(vals)));
                        }
                    }

                    Run(cur.Next);
                }
            }

            if (updates.Count > 0)
            {
                using BtCursor w = BTree.Table(ctx, 1).OpenCursor();
                foreach ((long rowid, byte[] rec) in updates)
                {
                    Run(() => w.Insert(rowid, rec));
                }
            }

            PageHandle? p1 = null;
            byte[] header;
            try
            {
                Run(() => src.GetPage(1, out p1));
                header = (byte[])p1!.Data.Clone();
            }
            finally
            {
                if (p1 is not null)
                {
                    src.Release(p1);
                }
            }

            List<byte[]> image = vi.Build(rootMap, header, mode == 2);
            PagerStatus rs;
            while ((rs = pg.ReplaceContents(image, mode != 0, mode == 2)) == PagerStatus.Pending)
            {
                Pump();
            }

            if (rs == PagerStatus.Error)
            {
                throw pg.Error!;
            }

            c.InvalidateSchema();
            _ddl = true;
        }
        catch (SourceFailure e)
        {
            throw Translate(e);
        }

        _pc++;
        return RunResult.Done;
    }

    // VACUUM [schema] INTO file (lang_vacuum): the same compacted image, written to a new file instead of over the database. The source is only read.
    // The image is handed to the new file's pager tree by tree, so memory holds the largest tree rather than the whole database; sqlite_schema is
    // written last, by ordinary inserts, because its rows carry the new root pages.
    private RunResult DoVacuumInto(int db, int fileReg)
    {
        Connection c = _conn;
        Pager pg = c.PagerOf(db);
        if (!c.AutoCommit)
        {
            throw new OneLiteException(ResultCode.Error, "cannot VACUUM from within a transaction");
        }

        if (R[fileReg].Kind != StorageClass.Text)
        {
            throw new OneLiteException(ResultCode.Error, "non-text filename");
        }

        string name = RegText(fileReg);
        string path = name;
        if (name.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            // A URI: only the path and the mode parameter are read; a read-only target cannot be written.
            string rest = name[5..];
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
                if (kv is "mode=ro")
                {
                    throw new OneLiteException(ResultCode.CantOpen, "unable to open database: " + name);
                }
            }
        }

        if (path.Length == 0 || path == ":memory:" || pg.PageCount == 0)
        {
            // a private temporary copy nobody can see, or nothing to copy
            _pc++;
            return RunResult.Done;
        }

        IVfs vfs = c.AuxiliaryVfs;
        if (vfs.Access(path, AccessMode.Exists))
        {
            long size;
            try
            {
                using IVfsFile f = vfs.Open(path, VfsOpenOptions.ReadOnly | VfsOpenOptions.MainDb);
                size = f.FileSize();
            }
            catch (VfsException)
            {
                throw new OneLiteException(ResultCode.CantOpen, "unable to open database: " + name);
            }

            if (size > 0)
            {
                throw new OneLiteException(ResultCode.Error, "output file already exists");
            }
        }

        BtContext ctx = c.ContextOf(db);
        IBtPageSource src = ctx.Source;
        void Pump() => pg.ExecuteSync();
        BtStatus Run(Func<BtStatus> op)
        {
            BtStatus s;
            while ((s = op()) == BtStatus.Pending)
            {
                Pump();
            }

            if (s == BtStatus.Error)
            {
                throw src.LastError!;
            }

            return s;
        }

        var po = new PagerOptions
        {
            PageSize = pg.PageSize,
            ReservedBytes = pg.IsEncrypted ? 0 : pg.ReservedBytes,
            Encoding = ctx.Encoding,
            AutoVacuum = pg.AutoVacuum,
            IncrementalVacuum = pg.AutoVacuum && pg.IncrementalVacuum,
            JournalMode = JournalMode.Off,
            Synchronous = pg.Synchronous,
            CacheSize = -2000,
        };
        StorageEngine eng;
        try
        {
            eng = StorageEngine.Open(vfs, path, po);
        }
        catch (OneLiteException e) when ((e.Code & 0xFF) == ResultCode.CantOpen)
        {
            throw new OneLiteException(ResultCode.CantOpen, "unable to open database: " + name, e.Detail);
        }

        bool ok = false;
        try
        {
            using (CipherKey? k = pg.CloneKey())
            {
                if (k is not null)
                {
                    eng.Pager.TryApplyKey(k, pg.CipherSettings ?? CipherSettings.V4);
                }
            }

            eng.BeginRead();
            eng.BeginWrite();
            var vi = new VacuumImage(ctx, Pump, pg.AutoVacuum);
            var rootMap = new Dictionary<uint, uint>();
            var rows = new List<(long Rowid, byte[] Record)>();
            using (BtCursor cur = BTree.Table(ctx, 1).OpenCursor())
            {
                var view = new RecordView();
                var vals = new Mem[5];
                Run(cur.First);
                while (cur.State == CursorState.Valid)
                {
                    Run(cur.LoadPayload);
                    if (!cur.ParseRecord(view) || view.ColumnCount < 5)
                    {
                        throw OneLiteException.Corrupt(1, "a sqlite_schema row is not a five-column record");
                    }

                    for (int i = 0; i < 5; i++)
                    {
                        view.Load(i, ref vals[i]);
                    }

                    long old = vals[3].Kind == StorageClass.Integral ? vals[3].IntValue : 0;
                    if (old > 1)
                    {
                        if (old > pg.PageCount || rootMap.ContainsKey((uint)old))
                        {
                            throw OneLiteException.Corrupt(1, $"sqlite_schema names root page {old} twice or outside the database");
                        }

                        uint nr = vi.NextRoot();
                        rootMap[(uint)old] = nr;
                        Values.SetInt(ref vals[3], nr);
                    }

                    rows.Add((cur.Rowid, RecordWriter.ToArray(vals)));
                    Run(cur.Next);
                }
            }

            PageHandle? p1 = null;
            byte[] header;
            try
            {
                Run(() => src.GetPage(1, out p1));
                header = (byte[])p1!.Data.Clone();
            }
            finally
            {
                if (p1 is not null)
                {
                    src.Release(p1);
                }
            }

            Pager dp = eng.Pager;
            vi.Build(rootMap, header, pg.AutoVacuum && pg.IncrementalVacuum, dp.PutPageSync, emptySchemaTree: true, rollbackJournalHeader: true);
            dp.AdoptHeader();
            eng.EnsureContext();
            using (BtCursor w = BTree.Table(eng.Context, 1).OpenCursor())
            {
                foreach ((long rowid, byte[] rec) in rows)
                {
                    eng.Run(() => w.Insert(rowid, rec));
                }
            }

            eng.Commit();
            ok = true;
        }
        catch (SourceFailure e)
        {
            throw Translate(e);
        }
        finally
        {
            try
            {
                if (!ok && eng.Pager.InWriteTransaction)
                {
                    eng.Rollback();
                }
            }
            catch (OneLiteException)
            {
                // The failure being reported is the one that matters.
            }

            eng.Dispose();
            if (!ok)
            {
                try
                {
                    vfs.Delete(path, false);
                }
                catch (VfsException)
                {
                    // A half-written copy that cannot be removed is left for the caller to see.
                }
            }
        }

        _pc++;
        return RunResult.Done;
    }
}
