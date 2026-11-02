using System;
using System.Globalization;
using System.IO;
using System.Linq;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.Pager;

namespace RecursiveAI.OneLite.Durability;

/// <summary>
/// The kill campaign's writer built on OneLite's SQL engine: the same transactions as <see cref="Workload"/>, written as SQL, with a savepoint that
/// is rolled back and a multi-row INSERT that fails half way (a statement rollback) inside every third transaction, so a SIGKILL also lands
/// in the middle of those. The result is judged by the pinned library, which recovers the file and runs <c>integrity_check</c>.
/// <c>engine-writer DB LEDGER JOURNAL_MODE SYNCHRONOUS [ps=N] [av=0|1|2] [cache=N] [enc=utf8|utf16le|utf16be]</c>
/// </summary>
public static class EngineWriter
{
    private static readonly string[] s_modeCycle = ["wal", "truncate", "wal", "persist", "delete", "wal", "delete"];

    public static int Run(string[] a)
    {
        string dbPath = a[0], ledger = a[1];
        PagerOptions po = BTreeWriter.ParseOptions(a);
        var options = new DatabaseOptions
        {
            PageSize = po.PageSize,
            AutoVacuum = po.AutoVacuum,
            IncrementalVacuum = po.IncrementalVacuum,
            Encoding = po.Encoding,
            CacheSize = po.CacheSize,
            JournalMode = po.JournalMode,
            Synchronous = po.Synchronous,
            BusyTimeoutMs = 5000,
        };
        using Database db = Database.Open(dbPath, options);
        using Connection conn = db.Connect();
        bool attach = a.Contains("attach=1");
        if (attach)
        {
            // A second database in the same transactions: every commit is a multi-database commit with a super-journal.
            Exec(conn, "ATTACH '" + dbPath.Replace("kill.db", "kill-aux.db", StringComparison.Ordinal) + "' AS aux");
            Exec(conn, "PRAGMA aux.journal_mode=" + a[2]);
        }

        Exec(conn, "BEGIN");
        foreach (string stmt in Workload.Schema.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Exec(conn, stmt);
            if (attach)
            {
                Exec(conn, stmt.Replace("IF NOT EXISTS ", "IF NOT EXISTS aux.", StringComparison.Ordinal).Replace("INSERT OR IGNORE INTO ", "INSERT OR IGNORE INTO aux.", StringComparison.Ordinal));
            }
        }

        Exec(conn, "COMMIT");
        long next = Scalar(conn, "SELECT n FROM counter WHERE id = 1") + 1;
        bool ddl = a.Contains("ddl=1");
        bool vacuum = a.Contains("vacuum=1");
        if (ddl)
        {
            Exec(conn, "CREATE TABLE IF NOT EXISTS ddlmark(x)");
        }

        bool rtree = a.Contains("rtree=1");
        if (rtree)
        {
            Exec(conn, "BEGIN");
            Exec(conn, "CREATE TABLE IF NOT EXISTS rtmark(x)");
            Exec(conn, "CREATE VIRTUAL TABLE IF NOT EXISTS rta USING rtree(id, x0, x1, y0, y1)");
            Exec(conn, "CREATE VIRTUAL TABLE IF NOT EXISTS rtb USING rtree_i32(id, x0, x1, y0, y1, z0, z1, +tag)");
            Exec(conn, "COMMIT");
        }

        bool vec = a.Contains("vec=1");
        if (vec)
        {
            Exec(conn, "BEGIN");
            Exec(conn, "CREATE TABLE IF NOT EXISTS vcmark(x)");
            Exec(conn, "CREATE VIRTUAL TABLE IF NOT EXISTS va USING vec0(id integer primary key, e float[4], chunk_size=8)");
            Exec(conn, "CREATE VIRTUAL TABLE IF NOT EXISTS vb USING vec0(id integer primary key, e float[300], chunk_size=8)");
            Exec(conn, "COMMIT");
        }

        bool blob = a.Contains("blob=1");
        if (a.Contains("backup=1"))
        {
            return RunBackupWriter(options, conn, ledger, next, blob);
        }

        bool fts = a.Contains("fts=1");
        if (fts)
        {
            Exec(conn, "BEGIN");
            Exec(conn, "CREATE TABLE IF NOT EXISTS ftsmark(x)");
            Exec(conn, "CREATE VIRTUAL TABLE IF NOT EXISTS fta USING fts5(a, b)");
            Exec(conn, "CREATE VIRTUAL TABLE IF NOT EXISTS ftb USING fts5(a, b, prefix='2,3', tokenize='porter unicode61', detail=column)");
            Exec(conn, "COMMIT");
        }

        bool fts3 = a.Contains("fts3=1");
        if (fts3)
        {
            Exec(conn, "BEGIN");
            Exec(conn, "CREATE TABLE IF NOT EXISTS fts3mark(x)");
            Exec(conn, "CREATE VIRTUAL TABLE IF NOT EXISTS f3a USING fts3(a, b)");
            Exec(conn, "CREATE VIRTUAL TABLE IF NOT EXISTS f4b USING fts4(a, b, prefix='2,3', tokenize=porter)");
            Exec(conn, "COMMIT");
        }

        using Statement insert = conn.Prepare(Workload.InsertSql);
        Console.Out.WriteLine("READY");
        Console.Out.Flush();
        using var led = new FileStream(ledger, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        bool jmode = a.Contains("jmode=1");
        for (long i = next; ; i++)
        {
            if (jmode && i % 7 == 0)
            {
                // The journal mode changes between commits (rollback modes and WAL, both directions); a kill lands inside the switch too.
                Exec(conn, "PRAGMA journal_mode=" + s_modeCycle[(int)(i / 7 % s_modeCycle.Length)]);
            }

            RunTransaction(conn, insert, i, ddl, attach, rtree, blob, vec, fts, fts3);
            byte[] line = System.Text.Encoding.ASCII.GetBytes(i.ToString(CultureInfo.InvariantCulture) + "\n");
            led.Write(line, 0, line.Length);
            led.Flush();
            if (vacuum && i % 5 == 0)
            {
                // a kill inside VACUUM must leave the file exactly as the last acknowledged commit left it
                Exec(conn, "VACUUM");
            }
        }
    }

    /// <summary>Creates the workload tables (one transaction) and returns the number of the last committed transaction.</summary>
    public static long Setup(Connection conn)
    {
        Exec(conn, "BEGIN");
        foreach (string stmt in Workload.Schema.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Exec(conn, stmt);
        }

        Exec(conn, "COMMIT");
        return Scalar(conn, "SELECT n FROM counter WHERE id = 1");
    }

    private static void Exec(Connection c, string sql)
    {
        using Statement s = c.Prepare(sql);
        while (s.Step())
        {
        }
    }

    private static long Scalar(Connection c, string sql)
    {
        using Statement s = c.Prepare(sql);
        return s.Step() ? s.GetInt64(0) : 0;
    }

    /// <summary>Runs transaction <paramref name="txn"/> of the workload as SQL and commits it.</summary>
    public static void RunTransaction(Connection conn, Statement insert, long txn, bool ddl = false, bool attach = false, bool rtree = false, bool blob = false, bool vec = false, bool fts = false, bool fts3 = false)
    {
        Exec(conn, "BEGIN");
        if (txn % 3 == 0)
        {
            Exec(conn, "SAVEPOINT sp1");
            Exec(conn, "INSERT INTO data(txn, seq, v, payload) VALUES (-1, 0, 0, x'00'), (-1, 1, 1, zeroblob(3500)), (-1, 2, 2, x'02')");
            Exec(conn, "ROLLBACK TO sp1");
            Exec(conn, "RELEASE sp1");
            try
            {
                Exec(conn, "INSERT INTO data(id, txn, seq, v, payload) VALUES (2000000000, -1, 0, 0, x'00'), (2000000001, -1, 0, 0, zeroblob(3200)), (2000000000, -1, 0, 0, x'00')");
                throw new InvalidOperationException("a duplicate primary key was accepted");
            }
            catch (OneLiteException e) when (e.Code == ResultCode.Constraint)
            {
                // The statement is undone as a whole; the transaction carries on.
            }
        }

        int rows = Workload.RowCount(txn);
        long sum = 0;
        for (int j = 0; j < rows; j++)
        {
            long v = Workload.Value(txn, j);
            sum += v;
            insert.Reset();
            insert.Bind(1, txn);
            insert.Bind(2, j);
            insert.Bind(3, v);
            insert.Bind(4, Workload.Payload(txn, j));
            insert.Step();
        }

        Exec(conn, string.Create(CultureInfo.InvariantCulture, $"INSERT INTO txlog VALUES ({txn}, {rows}, {sum})"));
        Exec(conn, string.Create(CultureInfo.InvariantCulture, $"UPDATE counter SET n = {txn}"));
        Exec(conn, string.Create(CultureInfo.InvariantCulture, $"DELETE FROM data WHERE txn = {txn - Workload.DeleteLag}"));
        if (ddl)
        {
            RunDdl(conn, txn);
        }

        if (rtree)
        {
            RunRTree(conn, txn, rows);
        }

        if (vec)
        {
            RunVec(conn, txn);
        }

        if (fts)
        {
            RunFts(conn, txn, ["fta", "ftb"], false);
        }

        if (fts3)
        {
            RunFts(conn, txn, ["f3a", "f4b"], true);
        }

        if (attach)
        {
            // The same transaction's rows in the attached database, so the two files must always agree on the last transaction.
            long auxSum = 0;
            for (int j = 0; j < rows; j++)
            {
                long v = Workload.Value(txn, j);
                auxSum += v;
                Exec(conn, string.Create(CultureInfo.InvariantCulture, $"INSERT INTO aux.data(txn, seq, v, payload) VALUES ({txn}, {j}, {v}, x'{Convert.ToHexString(Workload.Payload(txn, j))}')"));
            }

            Exec(conn, string.Create(CultureInfo.InvariantCulture, $"INSERT INTO aux.txlog VALUES ({txn}, {rows}, {auxSum})"));
            Exec(conn, string.Create(CultureInfo.InvariantCulture, $"UPDATE aux.counter SET n = {txn}"));
            Exec(conn, string.Create(CultureInfo.InvariantCulture, $"DELETE FROM aux.data WHERE txn = {txn - Workload.DeleteLag}"));
        }

        if (blob)
        {
            RewriteThroughBlob(conn, txn);
        }

        Exec(conn, "COMMIT");
    }

    // Incremental blob I/O inside the transaction: the first row's payload is written back, in two pieces, through a writable handle, so a
    // SIGKILL can land between the pieces. The bytes are the ones already there, so the workload's own checks still apply.
    private static void RewriteThroughBlob(Connection conn, long txn)
    {
        byte[] payload = Workload.Payload(txn, 0);
        long rowid = Scalar(conn, string.Create(CultureInfo.InvariantCulture, $"SELECT id FROM data WHERE txn = {txn} AND seq = 0"));
        if (rowid == 0 || payload.Length < 2)
        {
            return;
        }

        Blob b = Blob.Open(conn, "main", "data", "payload", rowid, writable: true);
        try
        {
            int half = Math.Min(payload.Length, b.Length) / 2;
            b.Write(0, payload.AsSpan(0, half));
            b.Write(half, payload.AsSpan(half, Math.Min(payload.Length, b.Length) - half));
        }
        finally
        {
            b.Close();
        }
    }

    // The workload runs on a private in-memory database and each transaction is then copied to the file with the backup API, two pages a step, so
    // a SIGKILL lands between the steps. The file must always hold a complete committed transaction; the ledger is written after the final step.
    private static int RunBackupWriter(DatabaseOptions options, Connection fileConn, string ledger, long next, bool blob)
    {
        using Database mem = Database.Open("mem.db", new DatabaseOptions { Vfs = new RecursiveAI.OneLite.Storage.Vfs.MemoryVfs(), PageSize = options.PageSize, AutoVacuum = options.AutoVacuum, IncrementalVacuum = options.IncrementalVacuum, Encoding = options.Encoding });
        using Connection m = mem.Connect();
        using (Backup load = Backup.Create(m, "main", fileConn, "main"))
        {
            load.Step(-1);
        }

        using Statement insert = m.Prepare(Workload.InsertSql);
        Console.Out.WriteLine("READY");
        Console.Out.Flush();
        using var led = new FileStream(ledger, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        for (long i = next; ; i++)
        {
            RunTransaction(m, insert, i, blob: blob);
            using (Backup save = Backup.Create(fileConn, "main", m, "main"))
            {
                while (save.Step(2) == BackupStatus.More)
                {
                }
            }

            byte[] line = System.Text.Encoding.ASCII.GetBytes(i.ToString(CultureInfo.InvariantCulture) + "\n");
            led.Write(line, 0, line.Length);
            led.Flush();
        }
    }

    // R-tree entries (see Workload.CheckRTree): transaction t inserts RTreeCount(t) boxes into rta and rtb, moves every fourth one, and removes what
    // transaction t - DeleteLag inserted, so nodes fill, split, reinsert and condense while a kill lands in the middle.
    private static void RunRTree(Connection conn, long t, int rows)
    {
        int n = Workload.RTreeCount(t);
        for (int j = 0; j < n; j++)
        {
            long id = t * 1000 + j;
            Workload.RTreeBox(t, j, out int x, out int y, out int w, out int h);
            Exec(conn, string.Create(CultureInfo.InvariantCulture, $"INSERT INTO rta VALUES ({id}, {x}, {x + w}, {y}, {y + h})"));
            Exec(conn, string.Create(CultureInfo.InvariantCulture, $"INSERT INTO rtb VALUES ({id}, {x}, {x + w}, {y}, {y + h}, {j}, {j + 1}, 'tag{j}')"));
        }

        for (int j = 0; j < n; j += 4)
        {
            long id = t * 1000 + j;
            Exec(conn, string.Create(CultureInfo.InvariantCulture, $"UPDATE rta SET x0 = x0 + 7, x1 = x1 + 7 WHERE id = {id}"));
        }

        long old = t - Workload.DeleteLag;
        Exec(conn, string.Create(CultureInfo.InvariantCulture, $"DELETE FROM rta WHERE id >= {old * 1000} AND id < {old * 1000 + 1000}"));
        Exec(conn, string.Create(CultureInfo.InvariantCulture, $"DELETE FROM rtb WHERE id >= {old * 1000} AND id < {old * 1000 + 1000}"));
        _ = rows;
    }

    // vec0 entries (see Workload.CheckVec): transaction t inserts VecCount(t) vectors into va (4 floats) and vb (300 floats, so a chunk row spans overflow pages),
    // rewrites every third one, and removes what transaction t - DeleteLag inserted, so chunks fill, empty and are recreated while a kill lands.
    private static void RunVec(Connection conn, long t)
    {
        int n = Workload.VecCount(t);
        foreach ((string tn, int dim) in new[] { ("va", 4), ("vb", 300) })
        {
            for (int j = 0; j < n; j++)
            {
                Exec(conn, string.Create(CultureInfo.InvariantCulture, $"INSERT INTO {tn}(id, e) VALUES ({t * 1000 + j}, '{Workload.VecJson(t, j, dim, 0)}')"));
            }

            for (int j = 0; j < n; j += 3)
            {
                Exec(conn, string.Create(CultureInfo.InvariantCulture, $"UPDATE {tn} SET e = '{Workload.VecJson(t, j, dim, 1)}' WHERE id = {t * 1000 + j}"));
            }

            long old = t - Workload.DeleteLag;
            Exec(conn, string.Create(CultureInfo.InvariantCulture, $"DELETE FROM {tn} WHERE id >= {old * 1000} AND id < {old * 1000 + 1000}"));
        }
    }

    // FTS5 documents (see Workload.CheckFts): transaction t inserts FtsCount(t) documents into two tables, rewrites every fourth, removes what
    // transaction t - DeleteLag inserted, and now and then merges or optimizes the index, so segments are written, merged and dropped under a kill.
    // With fts34 set the tables are FTS3/FTS4: documents are keyed by docid and the merge command is merge=2,4.
    private static void RunFts(Connection conn, long t, string[] tables, bool fts34)
    {
        int n = Workload.FtsCount(t);
        string key = fts34 ? "docid" : "rowid";
        foreach (string tn in tables)
        {
            for (int j = 0; j < n; j++)
            {
                long id = t * 1000 + j;
                Exec(conn, $"INSERT INTO {tn}({key}, a, b) VALUES ({id}, '{Workload.FtsText(t, j, false, 0)}', '{Workload.FtsText(t, j, false, 1)}')");
            }

            for (int j = 0; j < n; j += 4)
            {
                long id = t * 1000 + j;
                Exec(conn, $"UPDATE {tn} SET a = '{Workload.FtsText(t, j, true, 0)}', b = '{Workload.FtsText(t, j, true, 1)}' WHERE {key} = {id}");
            }

            long old = t - Workload.DeleteLag;
            Exec(conn, $"DELETE FROM {tn} WHERE {key} >= {old * 1000} AND {key} < {old * 1000 + 1000}");
            if (t % 5 == 0)
            {
                Exec(conn, fts34 ? $"INSERT INTO {tn}({tn}) VALUES('merge=2,4')" : $"INSERT INTO {tn}({tn}, rank) VALUES('merge', 4)");
            }

            if (t % 11 == 0)
            {
                Exec(conn, $"INSERT INTO {tn}({tn}) VALUES('optimize')");
            }
        }
    }

    // Schema changes in the same transaction (see Workload.CheckDdl for what must exist afterwards).
    private static void RunDdl(Connection conn, long t)
    {
        string d = t.ToString(CultureInfo.InvariantCulture);
        Exec(conn, $"CREATE TABLE d{d}(a INTEGER PRIMARY KEY, b TEXT, c)");
        Exec(conn, $"WITH RECURSIVE s(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM s WHERE i<40) INSERT INTO d{d} SELECT i, 'row'||i||zeroblob(60), i FROM s");
        Exec(conn, $"CREATE INDEX di{d} ON d{d}(b)");
        Exec(conn, $"CREATE VIEW dv{d} AS SELECT a, c FROM d{d}");
        if (t > 1)
        {
            long p = t - 1;
            Exec(conn, $"ALTER TABLE d{p} ADD COLUMN e DEFAULT 1");
            Exec(conn, $"ALTER TABLE d{p} RENAME COLUMN c TO cc");
        }

        if (t > 2)
        {
            Exec(conn, $"ALTER TABLE d{t - 2} RENAME TO e{t - 2}");
        }

        if (t > 3)
        {
            Exec(conn, $"DROP VIEW dv{t - 3}");
            Exec(conn, $"DROP TABLE e{t - 3}");
        }
    }
}
