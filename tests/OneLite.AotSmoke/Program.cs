using System.Linq;
using RecursiveAI.OneLite.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using RecursiveAI.OneLite;
using RecursiveAI.OneLite.Data;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Sql.Parsing;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Wal;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Forensics;
using RecursiveAI.OneLite.Storage.Record;
using TextEncoding = RecursiveAI.OneLite.Storage.TextEncoding;
using RecursiveAI.OneLite.Storage.Vfs;
using RecursiveAI.OneLite.Storage.Crypto;

namespace RecursiveAI.OneLite.AotSmoke;

/// <summary>Runs the shipped libraries under NativeAOT. Published with warnings as errors, so any trim or AOT warning fails the build.</summary>
internal static class Program
{
    private static int Main()
    {
        Console.WriteLine($"{LibraryInfo.Name} {DataLibraryInfo.Name}");
        return VfsSmoke() && PagerSmoke() && WalSmoke() && BTreeSmoke() && SqlSmoke() && EngineSmoke() && ForensicSmoke() && CipherSmoke() && PagerBTreeSmoke() && EncryptedPagerSmoke() && RTreeSmoke() && VecSmoke() && Fts5Smoke() && Fts3Smoke() && DataSmoke.Run() ? 0 : 1;
    }

    /// <summary>
    /// An encrypted database through the pager: written under a key, rekeyed, reopened under the new key (and not the old one), in WAL mode too.
    /// </summary>
    private static bool EncryptedPagerSmoke()
    {
        string dir = Path.Combine(Path.GetTempPath(), "onelite-aot-enc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new CipherSettings(1024, 2, CipherDigest.Sha512, CipherDigest.Sha512);
            foreach (bool wal in new[] { false, true })
            {
                string path = Path.Combine(dir, wal ? "w.db" : "r.db");
                PagerOptions Opts(string key) => new() { Key = CipherKey.FromPassphrase(key), Cipher = settings };
                using (Pager p = Pager.Open(FileVfs.Instance, path, Opts("one")))
                {
                    p.BeginReadSync();
                    p.BeginWriteSync();
                    PageFrame f1 = p.GetSync(1);
                    p.MakeWritableSync(f1);
                    f1.Data[200] = 7;
                    p.Release(f1);
                    for (int i = 2; i <= 6; i++)
                    {
                        PageFrame f = p.AllocateSync();
                        f.Data[10] = (byte)i;
                        p.Release(f);
                    }

                    p.CommitSync();
                    if (wal)
                    {
                        p.ChangeJournalModeSync(JournalMode.Wal);
                    }

                    p.EndReadSync();
                    p.RekeySync(CipherKey.FromPassphrase("two"));
                }

                using (Pager q = Pager.Open(FileVfs.Instance, path, Opts("two")))
                {
                    q.BeginReadSync();
                    PageFrame f = q.GetSync(4);
                    bool ok = q.PageCount == 6 && f.Data[10] == 4;
                    q.Release(f);
                    q.EndReadSync();
                    if (!ok)
                    {
                        return false;
                    }
                }

                try
                {
                    using Pager r = Pager.Open(FileVfs.Instance, path, Opts("one"));
                    r.BeginReadSync();
                    return false;
                }
                catch (OneLiteException e) when (e.Code == ResultCode.NotADb)
                {
                    // the old key no longer opens the file
                }
            }

            return true;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>An FTS5 table through the engine: tokenizers, prefix and phrase queries, bm25, highlight, snippet, a vocab table, merge and integrity-check.</summary>
    private static bool Fts5Smoke()
    {
        string dir = Path.Combine(Path.GetTempPath(), "onelite-aot-fts5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var db = Database.Open(Path.Combine(dir, "f.db"));
            using Connection conn = db.Connect();
            foreach (string sql in new[]
            {
                "CREATE VIRTUAL TABLE ft USING fts5(a, b, tokenize='porter unicode61', prefix='2')",
                "WITH RECURSIVE s(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM s WHERE i < 300) INSERT INTO ft(rowid, a, b) SELECT i, 'running dogs number ' || i || ' overdue invoice', 'café ' || (i % 7) FROM s",
                "DELETE FROM ft WHERE rowid % 10 = 0",
                "INSERT INTO ft(ft, rank) VALUES('merge', 4)",
                "INSERT INTO ft(ft) VALUES('optimize')",
                "INSERT INTO ft(ft) VALUES('integrity-check')",
                "CREATE VIRTUAL TABLE fv USING fts5vocab(ft, 'row')",
            })
            {
                using Statement run = conn.Prepare(sql);
                while (run.Step())
                {
                }
            }

            using Statement q = conn.Prepare("SELECT count(*), min(rowid), (SELECT count(*) FROM ft WHERE ft MATCH '\"overdue invoice\" AND run*'), (SELECT doc FROM fv WHERE term = 'overdu'), "
                + "(SELECT highlight(ft, 0, '[', ']') FROM ft WHERE ft MATCH 'dog' AND rowid = 5), (SELECT snippet(ft, 0, '<', '>', '..', 4) FROM ft WHERE ft MATCH 'invoice' AND rowid = 7), "
                + "(SELECT round(bm25(ft), 4) FROM ft WHERE ft MATCH 'dogs' ORDER BY rank LIMIT 1) FROM ft WHERE ft MATCH 'caf*'");
            return q.Step() && q.GetInt64(0) > 0 && q.GetInt64(2) == 270 && q.GetInt64(3) == 270 && (q.GetString(4) ?? "").Contains("[dogs]", StringComparison.Ordinal)
                && (q.GetString(5) ?? "").Contains('<', StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>FTS3 and FTS4 tables through the engine: porter and prefix indexes, phrase and NEAR queries, offsets, snippet, matchinfo, merge and integrity-check.</summary>
    private static bool Fts3Smoke()
    {
        string dir = Path.Combine(Path.GetTempPath(), "onelite-aot-fts3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var db = Database.Open(Path.Combine(dir, "f.db"));
            using Connection conn = db.Connect();
            foreach (string sql in new[]
            {
                "CREATE VIRTUAL TABLE f3 USING fts3(a, b)",
                "CREATE VIRTUAL TABLE f4 USING fts4(a, b, tokenize=porter, prefix='2,3')",
                "WITH RECURSIVE s(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM s WHERE i < 300) INSERT INTO f4(docid, a, b) SELECT i, 'running dogs number ' || i || ' overdue invoice', 'cats ' || i FROM s",
                "INSERT INTO f3(docid, a, b) SELECT docid, a, b FROM f4",
                "DELETE FROM f4 WHERE docid % 10 = 0",
                "INSERT INTO f4(f4) VALUES('merge=2,3')",
                "INSERT INTO f4(f4) VALUES('optimize')",
                "INSERT INTO f4(f4) VALUES('integrity-check')",
            })
            {
                using Statement run = conn.Prepare(sql);
                while (run.Step())
                {
                }
            }

            using Statement q = conn.Prepare("SELECT (SELECT count(*) FROM f4 WHERE f4 MATCH '\"overdue invoice\" run*'), (SELECT count(*) FROM f3 WHERE f3 MATCH 'dogs NEAR/2 overdue'), "
                + "(SELECT offsets(f4) FROM f4 WHERE f4 MATCH 'dog' AND docid = 5), (SELECT snippet(f3, '<', '>', '..', -1, 4) FROM f3 WHERE f3 MATCH 'invoice' AND docid = 7), "
                + "(SELECT hex(matchinfo(f4, 'nc')) FROM f4 WHERE f4 MATCH 'dogs' LIMIT 1)");
            return q.Step() && q.GetInt64(0) == 270 && q.GetInt64(1) == 300 && (q.GetString(2) ?? "").StartsWith("0 0", StringComparison.Ordinal)
                && (q.GetString(3) ?? "").Contains('<', StringComparison.Ordinal) && (q.GetString(4) ?? "").Length == 16;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>An R*Tree and dbstat through the engine: create, insert past one node, move, delete, query, rtreecheck.</summary>
    private static bool RTreeSmoke()
    {
        string dir = Path.Combine(Path.GetTempPath(), "onelite-aot-rtree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var db = Database.Open(Path.Combine(dir, "r.db"));
            using Connection conn = db.Connect();
            foreach (string sql in new[]
            {
                "CREATE VIRTUAL TABLE rt USING rtree(id, x0, x1, y0, y1, +name)",
                "WITH RECURSIVE s(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM s WHERE i < 300) INSERT INTO rt SELECT i, i, i + 2, i % 50, i % 50 + 3, 'n' || i FROM s",
                "UPDATE rt SET x0 = 1000, x1 = 1001 WHERE id = 7",
                "DELETE FROM rt WHERE id % 3 = 0",
            })
            {
                using Statement run = conn.Prepare(sql);
                while (run.Step())
                {
                }
            }

            using Statement q = conn.Prepare("SELECT count(*), min(id), max(name), rtreecheck('rt'), (SELECT count(*) FROM rt WHERE x0 >= 999) FROM rt WHERE x1 >= 10 AND y0 <= 20");
            if (!q.Step() || q.GetInt64(0) <= 0 || q.GetString(3) != "ok" || q.GetInt64(4) != 1)
            {
                return false;
            }

            using Statement st = conn.Prepare("SELECT count(*) FROM dbstat");
            return st.Step() && st.GetInt64(0) > 0;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>A vec0 table through the engine: float and bit columns, a chunk that spans overflow pages, k-NN with a metadata filter, update, delete, reopen.</summary>
    private static bool VecSmoke()
    {
        string dir = Path.Combine(Path.GetTempPath(), "onelite-aot-vec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "v.db");
            using (var db = Database.Open(path))
            using (Connection conn = db.Connect())
            {
                foreach (string sql in new[]
                {
                    "CREATE VIRTUAL TABLE v USING vec0(id integer primary key, e float[300], b bit[16], tag text, chunk_size=8)",
                    "INSERT INTO v VALUES " + string.Join(",", Enumerable.Range(1, 40).Select(i => $"({i}, vec_f32('[{i},{i % 7}" + string.Concat(Enumerable.Repeat(",0", 298)) + $"]'), vec_bit(x'ff00'), 't{i % 2}')")),
                    "UPDATE v SET tag = 'z' WHERE id = 3",
                    "DELETE FROM v WHERE id % 5 = 0",
                })
                {
                    using Statement run = conn.Prepare(sql);
                    while (run.Step())
                    {
                    }
                }
            }

            using var db2 = Database.Open(path);
            using Connection c2 = db2.Connect();
            using Statement q = c2.Prepare("SELECT id, distance FROM v WHERE e MATCH vec_f32('[7,0" + string.Concat(Enumerable.Repeat(",0", 298)) + "]') AND k = 3 AND tag = 't1' ORDER BY distance");
            if (!q.Step() || q.GetInt64(0) != 7 || q.GetDouble(1) != 0)
            {
                return false;
            }

            using Statement n = c2.Prepare("SELECT count(*), vec_length(vec_f32('[1,2,3]')), vec_distance_l2('[1,2]', '[4,6]') FROM v");
            return n.Step() && n.GetInt64(0) == 32 && n.GetInt64(1) == 3 && n.GetDouble(2) == 5;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// Opens a hand-built two-page database read-only and runs queries through the engine (parser, planner, VM, B-tree, pager).
    /// </summary>
    private static bool EngineSmoke()
    {
        string dir = Path.Combine(Path.GetTempPath(), "onelite-aot-engine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "e.db");
            File.WriteAllBytes(path, BuildTinyDatabase());
            using var db = Database.OpenReadOnly(path);
            using Connection conn = db.Connect();
            using (Statement st = conn.Prepare("SELECT b, a * 2 FROM t WHERE a >= 2 ORDER BY a DESC"))
            {
                if (!st.Step() || st.GetString(0) != "three" || st.GetInt64(1) != 6)
                {
                    return false;
                }

                if (!st.Step() || st.GetString(0) != "two" || st.GetInt64(1) != 4 || st.Step())
                {
                    return false;
                }
            }

            // Clearer errors and readable plans, under NativeAOT.
            try
            {
                _ = conn.Prepare("SELECT bb FROM t");
                return false;
            }
            catch (OneLiteException e)
            {
                if (e.Message != "no such column: bb" || e.Position is not { Column: 8 } || e.Caret is null || !System.Linq.Enumerable.Contains(e.Suggestions, "b") || !e.ToString().Contains("did you mean", StringComparison.Ordinal))
                {
                    return false;
                }
            }

            QueryPlan? plan = conn.Explain("SELECT b FROM t WHERE a >= 2", analyze: true);
            if (plan is null || !plan.HasActual || plan.Root.Children.Count != 1 || plan.Root.Children[0].ActualRows != 3 || !plan.Render().Contains("SCAN t", StringComparison.Ordinal))
            {
                return false;
            }

            using Statement count = conn.Prepare("SELECT count(*), upper('ok') FROM t");
            return count.Step() && count.GetInt64(0) == 3 && count.GetString(1) == "OK";
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>A 1024-byte-page database: page 1 is sqlite_schema with table t(a INTEGER, b TEXT), page 2 holds three rows.</summary>
    private static byte[] BuildTinyDatabase()
    {
        const int PageSize = 1024;
        var file = new byte[PageSize * 2];
        System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(file, 0);
        file[16] = 0x04; // page size 1024, big endian
        file[18] = 1;
        file[19] = 1;
        file[21] = 64;
        file[22] = 32;
        file[23] = 32;
        file[27] = 1;    // file change counter
        file[31] = 2;    // pages in the database
        file[43] = 1;    // schema cookie
        file[47] = 4;    // schema format
        file[59] = 1;    // UTF-8
        file[95] = 1;    // version-valid-for
        byte[] schemaRow = Record("table"u8.ToArray(), "t"u8.ToArray(), "t"u8.ToArray(), 2, "CREATE TABLE t(a INTEGER, b TEXT)"u8.ToArray());
        PutLeaf(file, 0, 100, [(1, schemaRow)]);
        PutLeaf(file, PageSize, 0, [(1, Record(null, "one"u8.ToArray(), null, 1, null)), (2, Record(null, "two"u8.ToArray(), null, 2, null)), (3, Record(null, "three"u8.ToArray(), null, 3, null))]);
        return file;
    }

    // Record layout (fileformat2 section 2.1): header size, one serial type per column, then the column bodies.
    // Columns here: either text (serial 13 + 2n) or a one-byte integer (serial 1); a null first/third column means "use the integer column".
    private static byte[] Record(byte[]? c0, byte[]? c1, byte[]? c2, int integer, byte[]? c4)
    {
        var types = new List<byte>();
        var body = new List<byte>();
        if (c0 is null)
        {
            // a table row: (a INTEGER, b TEXT) -> columns are (integer, text)
            types.Add(1);
            body.Add((byte)integer);
            types.Add((byte)(13 + (2 * c1!.Length)));
            body.AddRange(c1);
        }
        else
        {
            foreach (byte[] t in new[] { c0, c1!, c2! })
            {
                types.Add((byte)(13 + (2 * t.Length)));
                body.AddRange(t);
            }

            types.Add(1);
            body.Add((byte)integer);
            types.Add((byte)(13 + (2 * c4!.Length)));
            body.AddRange(c4);
        }

        var rec = new List<byte> { (byte)(types.Count + 1) };
        rec.AddRange(types);
        rec.AddRange(body);
        return [.. rec];
    }

    private static void PutLeaf(byte[] file, int pageStart, int headerAt, (int Rowid, byte[] Record)[] rows)
    {
        int end = pageStart + 1024;
        int h = pageStart + headerAt;
        file[h] = 0x0D; // table b-tree leaf page
        file[h + 3] = 0;
        file[h + 4] = (byte)rows.Length;
        int content = end;
        for (int i = 0; i < rows.Length; i++)
        {
            byte[] cell = [(byte)rows[i].Record.Length, (byte)rows[i].Rowid, .. rows[i].Record];
            content -= cell.Length;
            cell.CopyTo(file, content);
            int rel = content - pageStart;
            file[h + 8 + (2 * i)] = (byte)(rel >> 8);
            file[h + 9 + (2 * i)] = (byte)rel;
        }

        int contentRel = content - pageStart;
        file[h + 5] = (byte)(contentRel >> 8);
        file[h + 6] = (byte)contentRel;
    }

    /// <summary>Writes a few pages through the pager on a real file with a journal, a commit and a rolled-back change.</summary>
    private static bool PagerSmoke()
    {
        string dir = Path.Combine(Path.GetTempPath(), "onelite-aot-pager-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "p.db");
            var options = new PagerOptions { PageSize = 1024, JournalMode = JournalMode.Delete, Synchronous = SynchronousMode.Full };
            using (var p = Pager.Open(FileVfs.Instance, path, options))
            {
                p.BeginReadSync();
                p.BeginWriteSync();
                for (int i = 0; i < 4; i++)
                {
                    PageFrame f = i == 0 ? p.GetSync(1) : p.AllocateSync();
                    p.MakeWritableSync(f);
                    f.Data[200] = (byte)(i + 1);
                    p.Release(f);
                }

                p.CommitSync();
                p.BeginWriteSync();
                PageFrame g = p.GetSync(3);
                p.MakeWritableSync(g);
                g.Data[200] = 99;
                p.Release(g);
                p.RollbackSync();
                p.EndReadSync();
            }

            using var q = Pager.Open(FileVfs.Instance, path, options);
            q.BeginReadSync();
            PageFrame r = q.GetSync(3);
            bool ok = q.PageCount == 4 && r.Data[200] == 3;
            q.Release(r);
            q.EndReadSync();
            return ok;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>Switches a file database to WAL mode, commits through the log, checkpoints it and reads the result back through a second pager.</summary>
    private static bool WalSmoke()
    {
        string dir = Path.Combine(Path.GetTempPath(), "onelite-aot-wal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "w.db");
            var options = new PagerOptions { PageSize = 1024, Synchronous = SynchronousMode.Full, WalAutoCheckpoint = 0 };
            using (var p = Pager.Open(FileVfs.Instance, path, options))
            {
                p.BeginReadSync();
                p.BeginWriteSync();
                for (int i = 0; i < 4; i++)
                {
                    PageFrame f = i == 0 ? p.GetSync(1) : p.AllocateSync();
                    p.MakeWritableSync(f);
                    f.Data[200] = (byte)(i + 1);
                    p.Release(f);
                }

                p.CommitSync();
                p.ChangeJournalModeSync(JournalMode.Wal);
                p.EndReadSync();
                p.BeginReadSync();
                p.BeginWriteSync();
                PageFrame g = p.GetSync(3);
                p.MakeWritableSync(g);
                g.Data[200] = 77;
                p.Release(g);
                p.CommitSync();
                p.EndReadSync();

                // A second connection reads the committed page through the wal-index while the first is still open.
                using var q = Pager.Open(FileVfs.Instance, path, options);
                q.BeginReadSync();
                PageFrame r = q.GetSync(3);
                bool seen = r.Data[200] == 77;
                q.Release(r);
                q.EndReadSync();
                CheckpointResult c = p.CheckpointSync(CheckpointMode.Truncate);
                if (!seen || c.Busy)
                {
                    return false;
                }
            }

            using var z = Pager.Open(FileVfs.Instance, path, options);
            z.BeginReadSync();
            PageFrame y = z.GetSync(3);
            bool ok = z.PageCount == 4 && y.Data[200] == 77;
            z.Release(y);
            z.EndReadSync();
            return ok;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>Uses the memory, file and fault-injecting VFS, including a mapped shared-memory region, so the AOT run executes them.</summary>
    private static bool VfsSmoke()
    {
        const VfsOpenOptions Db = VfsOpenOptions.MainDb | VfsOpenOptions.ReadWrite | VfsOpenOptions.Create;
        string dir = Path.Combine(Path.GetTempPath(), "onelite-aot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var fault = new FaultVfs(new MemoryVfs());
            using (IVfsFile m = fault.Open("m.db", Db))
            {
                m.Write(0, new byte[] { 1, 2, 3 });
                m.Sync(SyncMode.Full);
            }

            if (fault.CrashImage(CrashPolicy.DropUnsynced).Files["m.db"].Length != 3)
            {
                return false;
            }

            using IVfsFile f = FileVfs.Instance.Open(Path.Combine(dir, "f.db"), Db);
            f.Write(0, new byte[] { 4, 5, 6, 7 });
            var back = new byte[4];
            if (f.Read(0, back) != 4 || back[3] != 7 || !f.Lock(LockLevel.Shared) || !f.Lock(LockLevel.Reserved))
            {
                return false;
            }

            ShmRegion r = f.Shm!.Map(0, true);
            ShmAccessor.WriteUInt32(r.Memory, 8, 42);
            bool ok = ShmAccessor.ReadUInt32(r.Memory, 8) == 42 && f.Shm.Lock(0, 1, ShmLockOptions.Lock | ShmLockOptions.Exclusive);
            f.Shm.Unmap(true);
            f.Unlock(LockLevel.None);
            return ok;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>Builds a small rowid table through the B-tree layer (enough rows to split and deepen), scans it back and runs the structural check.</summary>
    private static bool BTreeSmoke()
    {
        ImagePageSource src = ImagePageSource.Create(512);
        var ctx = new BtContext(src, TextEncoding.Utf8);
        uint root = 0;
        Run(src, () => BtAdmin.CreateTree(ctx, true, out root));
        BTree table = BTree.Table(ctx, root);
        using BtCursor c = table.OpenCursor();
        for (int n = 1; n <= 400; n++)
        {
            byte[] rec = RecordWriter.ToArray([Mem.FromInt(n), Mem.FromText("row " + n, TextEncoding.Utf8)]);
            long rowid = n;
            Run(src, () => c.Insert(rowid, rec));
        }

        var view = new RecordView();
        long sum = 0;
        Run(src, c.First);
        while (c.State == CursorState.Valid)
        {
            if (!c.ParseRecord(view))
            {
                return false;
            }

            sum += view.GetInt64(0);
            Run(src, c.Next);
        }

        return sum == 400L * 401 / 2 && new BtIntegrity(ctx).Check([BTree.Table(ctx, 1), table]);
    }

    /// <summary>
    /// The B-tree on the pager, on a real file in auto-vacuum FULL mode with a journal: rows are inserted and committed, half are deleted (so
    /// commit relocates pages), the file is reopened, scanned and checked.
    /// </summary>
    private static bool PagerBTreeSmoke()
    {
        string dir = Path.Combine(Path.GetTempPath(), "onelite-aot-btree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "b.db");
            var options = new PagerOptions { PageSize = 1024, AutoVacuum = true, JournalMode = JournalMode.Delete, Synchronous = SynchronousMode.Full };
            uint root = 0;
            using (StorageEngine eng = StorageEngine.Open(FileVfs.Instance, path, options))
            {
                eng.BeginRead();
                eng.BeginWrite();
                eng.Run(() => BtAdmin.CreateTree(eng.Context, true, out root));
                using (BtCursor c = BTree.Table(eng.Context, root).OpenCursor())
                {
                    for (int n = 1; n <= 600; n++)
                    {
                        byte[] rec = RecordWriter.ToArray([Mem.FromInt(n), Mem.FromBlob(new byte[n % 7 == 0 ? 2500 : 60])]);
                        long rowid = n;
                        if (eng.Run(() => c.Insert(rowid, rec)) != BtStatus.Ok)
                        {
                            return false;
                        }
                    }
                }

                eng.Commit();
                eng.BeginWrite();
                using (BtCursor c = BTree.Table(eng.Context, root).OpenCursor())
                {
                    for (int n = 1; n <= 600; n += 2)
                    {
                        long rowid = n;
                        if (eng.Run(() => c.MoveToRowid(rowid, out _)) != BtStatus.Ok || eng.Run(c.Delete) != BtStatus.Ok)
                        {
                            return false;
                        }
                    }
                }

                eng.Commit();
                eng.EndRead();
            }

            using (StorageEngine eng = StorageEngine.Open(FileVfs.Instance, path, options))
            {
                eng.BeginRead();
                BTree table = BTree.Table(eng.Context, root);
                using BtCursor c = table.OpenCursor();
                var view = new RecordView();
                long sum = 0;
                eng.Run(c.First);
                while (c.State == CursorState.Valid)
                {
                    if (eng.Run(c.LoadPayload) != BtStatus.Ok || !c.ParseRecord(view))
                    {
                        return false;
                    }

                    sum += view.GetInt64(0);
                    eng.Run(c.Next);
                }

                bool ok = sum == 300L * 301 && new BtIntegrity(eng.Context, eng.Pager.ExecuteSync).Check([BTree.Table(eng.Context, 1), table]);
                eng.EndRead();
                return ok;
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>Builds a small database with a schema row, deletes some rows, then opens it with the forensic reader: tables, rows, space and carving.</summary>
    private static bool ForensicSmoke()
    {
        ImagePageSource src = ImagePageSource.Create(1024);
        var ctx = new BtContext(src, TextEncoding.Utf8);
        uint root = 0;
        Run(src, () => BtAdmin.CreateTree(ctx, true, out root));
        BTree table = BTree.Table(ctx, root);
        using (BtCursor sc = BTree.Table(ctx, 1).OpenCursor())
        {
            byte[] schema = RecordWriter.ToArray([Mem.FromText("table", TextEncoding.Utf8), Mem.FromText("t", TextEncoding.Utf8), Mem.FromText("t", TextEncoding.Utf8),
                Mem.FromInt(root), Mem.FromText("CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT)", TextEncoding.Utf8)]);
            Run(src, () => sc.Insert(1, schema));
        }

        using (BtCursor c = table.OpenCursor())
        {
            for (int n = 1; n <= 60; n++)
            {
                byte[] rec = RecordWriter.ToArray([Mem.Null, Mem.FromText("forensic-name-" + n, TextEncoding.Utf8)]);
                long rowid = n;
                Run(src, () => c.Insert(rowid, rec));
            }

            for (int n = 10; n <= 20; n++)
            {
                long rowid = n;
                Run(src, () => c.MoveToRowid(rowid, out _));
                Run(src, c.Delete);
            }
        }

        var vfs = new MemoryVfs();
        vfs.Import("f.db", src.ToBytes());
        using ForensicDatabase db = ForensicDatabase.Open("f.db", new ForensicOptions { Vfs = vfs });
        ForensicSnapshot snap = db.OpenSnapshot();
        foreach (ForensicTable t in snap.Tables)
        {
            if (t.Name == "t" && snap.ReadRows(t).Count == 49)
            {
                return snap.Carve().Count > 0;
            }
        }

        return false;
    }

    private static void Run(ImagePageSource src, Func<BtStatus> op)
    {
        BtStatus s;
        while ((s = op()) == BtStatus.Pending)
        {
            src.Complete();
        }

        if (s != BtStatus.Ok)
        {
            throw new InvalidOperationException("B-tree call failed");
        }
    }

    /// <summary>Parses, writes and re-parses statements from every part of the grammar, and checks an error and its offset.</summary>
    private static bool SqlSmoke()
    {
        string[] statements =
        [
            "with c(x) as (select 1 union all select x+1 from c where x < 5) select x, sum(x) over (order by x rows between unbounded preceding and current row) from c where x in (1,2,3) order by 1 limit 2",
            "insert into t(a,b) values (1,'x') on conflict(a) do update set b = excluded.b returning *",
            "create table t(a integer primary key, b text not null default 'x' check (b <> ''), c as (a*2) stored) strict",
            "create trigger tr after update on t begin update u set b = new.b; end",
            "alter table t add column d int",
            "pragma journal_mode = wal",
        ];
        foreach (string sql in statements)
        {
            Sql.Statement? s = SqlParser.ParseOne(sql, 0, out _);
            if (s == null)
            {
                return false;
            }

            string written = SqlWriter.Write(s);
            Sql.Statement? again = SqlParser.ParseOne(written, 0, out _);
            if (again == null || SqlWriter.Write(again) != written)
            {
                return false;
            }
        }

        try
        {
            _ = SqlParser.ParseOne("select 1 from where", 0, out _);
            return false;
        }
        catch (OneLiteException e)
        {
            return e.Message == "near \"where\": syntax error" && e.ErrorOffset == 14;
        }
    }

    /// <summary>Encodes and decodes pages with the SQLCipher-compatible codec (SHA-512 and SHA-1 settings) and checks a flipped bit is refused.</summary>
    private static bool CipherSmoke()
    {
        foreach (CipherSettings s in new[] { CipherSettings.V4.WithKdfIterations(2), CipherSettings.V3.WithKdfIterations(2) })
        {
            using CipherKey key = CipherKey.Parse("smoke");
            using PageCodec codec = PageCodec.CreateNew(s, key);
            byte[] plain = new byte[s.PageSize], stored = new byte[s.PageSize], back = new byte[s.PageSize];
            for (int i = 0; i < plain.Length - s.ReserveSize; i++)
            {
                plain[i] = (byte)(i * 7);
            }

            codec.Encode(2, plain, stored);
            codec.Decode(2, stored, back);
            stored[10] ^= 1;
            if (!plain.AsSpan().SequenceEqual(back) || codec.TryDecode(2, stored, back))
            {
                Console.WriteLine("cipher smoke failed");
                return false;
            }
        }

        return true;
    }
}
