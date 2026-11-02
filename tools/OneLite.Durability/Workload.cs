using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.Durability;

/// <summary>
/// The scripted transaction stream every writer runs, and the check applied to a database after the writer was killed.
/// A writer commits transaction 1, 2, 3, ... and appends the number to its ledger only after COMMIT has returned, so the ledger
/// never claims more than was committed. Transaction i:
/// <list type="number">
/// <item>inserts <see cref="RowCount"/> rows into <c>data</c> (payload bytes from <see cref="Payload"/>), some of them large enough to overflow;</item>
/// <item>inserts its row into <c>txlog</c> with the row count and the sum of the row values;</item>
/// <item>sets <c>counter.n = i</c>;</item>
/// <item>deletes the rows of transaction i-3 (so deletes, freed pages and reuse are exercised);</item>
/// </list>
/// All of it in one transaction. After a kill the file must show exactly transactions 1..M for some M, with all-or-nothing content.
/// </summary>
public static class Workload
{
    /// <summary>Schema; run inside one transaction so a kill during setup leaves either nothing or everything.</summary>
    public const string Schema = """
        CREATE TABLE IF NOT EXISTS txlog(txn INTEGER PRIMARY KEY, nrows INTEGER NOT NULL, sumv INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS data(id INTEGER PRIMARY KEY, txn INTEGER NOT NULL, seq INTEGER NOT NULL, v INTEGER NOT NULL, payload BLOB NOT NULL);
        CREATE INDEX IF NOT EXISTS data_txn ON data(txn);
        CREATE TABLE IF NOT EXISTS counter(id INTEGER PRIMARY KEY CHECK (id = 1), n INTEGER NOT NULL);
        INSERT OR IGNORE INTO counter VALUES (1, 0);
        """;

    /// <summary>How many transactions back a transaction deletes.</summary>
    public const int DeleteLag = 3;

    /// <summary>Rows inserted by transaction <paramref name="txn"/>: 1 to 5, 150 every 11th, 600 every 29th.</summary>
    public static int RowCount(long txn)
        => txn % 29 == 0 ? 600 : txn % 11 == 0 ? 150 : 1 + (int)(txn * 7 % 5);

    /// <summary>The value column of row <paramref name="seq"/> of transaction <paramref name="txn"/>.</summary>
    public static long Value(long txn, int seq) => (txn * 1_000_003 + seq * 7919L) % 1_000_000_007;

    /// <summary>The payload of a row: 20 to 199 bytes, or about 3 KB for some rows (an overflow chain on small pages).</summary>
    public static byte[] Payload(long txn, int seq)
    {
        int len = 20 + (int)((txn * 31 + seq * 17) % 180);
        if ((txn + seq) % 37 == 0)
        {
            len = 3000 + (int)(txn % 200);
        }
        var b = new byte[len];
        for (int k = 0; k < b.Length; k++)
        {
            b[k] = (byte)(txn * 131 + seq * 7 + k);
        }
        return b;
    }

    /// <summary>Runs transaction <paramref name="txn"/> on an open connection. Returns after COMMIT has returned.</summary>
    public static void Run(SqliteDb db, SqliteStatement insert, long txn)
    {
        db.Exec("BEGIN;");
        int rows = RowCount(txn);
        long sum = 0;
        for (int j = 0; j < rows; j++)
        {
            long v = Value(txn, j);
            sum += v;
            insert.Reset();
            insert.Bind(1, txn);
            insert.Bind(2, j);
            insert.Bind(3, v);
            insert.Bind(4, Payload(txn, j));
            insert.Step();
        }
        db.Exec(string.Create(CultureInfo.InvariantCulture,
            $"INSERT INTO txlog VALUES ({txn}, {rows}, {sum}); UPDATE counter SET n = {txn}; DELETE FROM data WHERE txn = {txn - DeleteLag};"));
        db.Exec("COMMIT;");
    }

    /// <summary>The insert statement <see cref="Run"/> expects.</summary>
    public const string InsertSql = "INSERT INTO data(txn, seq, v, payload) VALUES (?1, ?2, ?3, ?4)";

    /// <summary>What a check found.</summary>
    public sealed record Verdict(long LastTxn, List<string> Violations);

    /// <summary>
    /// Opens the database (which recovers a hot journal or a WAL), runs <c>integrity_check</c> and checks the all-or-nothing
    /// predicate: txlog holds exactly 1..M, counter = M, and the rows of the last <see cref="DeleteLag"/> transactions are all
    /// present and byte-exact, with no other rows.
    /// </summary>
    public static Verdict Check(string dbPath)
    {
        var bad = new List<string>();
        if (!File.Exists(dbPath) || new FileInfo(dbPath).Length == 0)
        {
            // Nothing was ever committed (a kill before or during the first transaction): the empty database is a valid state.
            return new Verdict(0, bad);
        }
        using SqliteDb db = SqliteDb.Open(dbPath, create: false);
        string? tables = db.Scalar("select count(*) from sqlite_schema where name in ('txlog','data','counter')");
        List<string> integrity = [];
        using (SqliteStatement st = db.Prepare("pragma integrity_check"))
        {
            while (st.Step())
            {
                integrity.Add(st.TextOrNull(0) ?? "");
            }
        }
        if (integrity.Count != 1 || integrity[0] != "ok")
        {
            bad.Add("integrity_check: " + string.Join("; ", integrity));
            return new Verdict(-1, bad);
        }
        if (tables == "0")
        {
            return new Verdict(0, bad);   // the setup transaction did not commit
        }
        if (tables != "3")
        {
            bad.Add("partial schema: " + tables + " of 3 tables (the setup transaction is not atomic)");
            return new Verdict(-1, bad);
        }
        long m = long.Parse(db.Scalar("select n from counter where id = 1") ?? "-1", CultureInfo.InvariantCulture);
        long count = long.Parse(db.Scalar("select count(*) from txlog")!, CultureInfo.InvariantCulture);
        long maxTxn = long.Parse(db.Scalar("select coalesce(max(txn), 0) from txlog")!, CultureInfo.InvariantCulture);
        if (count != m || maxTxn != m)
        {
            bad.Add($"counter says {m}, txlog has {count} rows and max {maxTxn}");
        }
        long expectedRows = 0;
        for (long t = Math.Max(1, m - DeleteLag + 1); t <= m; t++)
        {
            int rows = RowCount(t);
            expectedRows += rows;
            using SqliteStatement st = db.Prepare(string.Create(CultureInfo.InvariantCulture, $"select seq, v, payload from data where txn = {t} order by seq"));
            int j = 0;
            long sum = 0;
            while (st.Step())
            {
                if (st.GetInt64(0) != j || st.GetInt64(1) != Value(t, j) || !st.Bytes(2).AsSpan().SequenceEqual(Payload(t, j)))
                {
                    bad.Add($"txn {t} row {j} has wrong content");
                    break;
                }
                sum += st.GetInt64(1);
                j++;
            }
            if (j != rows)
            {
                bad.Add($"txn {t}: {j} rows present, {rows} committed");
            }
            string? logged = db.Scalar(string.Create(CultureInfo.InvariantCulture, $"select nrows || ':' || sumv from txlog where txn = {t}"));
            if (logged != string.Create(CultureInfo.InvariantCulture, $"{rows}:{sum}") && j == rows)
            {
                bad.Add($"txn {t}: txlog says {logged}, rows say {rows}:{sum}");
            }
        }
        long total = long.Parse(db.Scalar("select count(*) from data")!, CultureInfo.InvariantCulture);
        if (total != expectedRows)
        {
            bad.Add($"data has {total} rows, the last transactions account for {expectedRows}");
        }
        if (db.Scalar("select count(*) from sqlite_schema where name = 'ddlmark'") == "1")
        {
            CheckDdl(db, m, bad);
        }

        if (db.Scalar("select count(*) from sqlite_schema where name = 'rtmark'") == "1")
        {
            CheckRTree(db, m, bad);
        }

        if (db.Scalar("select count(*) from sqlite_schema where name = 'vcmark'") == "1")
        {
            CheckVec(db, m, bad);
        }

        if (db.Scalar("select count(*) from sqlite_schema where name = 'ftsmark'") == "1")
        {
            CheckFts(db, m, bad, ["fta", "ftb"], "fts5");
        }

        if (db.Scalar("select count(*) from sqlite_schema where name = 'fts3mark'") == "1")
        {
            CheckFts(db, m, bad, ["f3a", "f4b"], "fts3/4");
        }

        return new Verdict(m, bad);
    }

    /// <summary>Vectors transaction t adds to the vec0 tables (EngineWriter option vec=1).</summary>
    public static int VecCount(long t) => 6 + (int)(t * 5 % 11);

    /// <summary>Component k of vector j of transaction t; <paramref name="gen"/> 1 is the rewritten version.</summary>
    public static float VecComponent(long t, int j, int k, int gen) => ((t * 31 + j * 7 + k * 3 + gen * 11) % 97) / 4f;

    public static string VecJson(long t, int j, int dim, int gen)
    {
        var sb = new System.Text.StringBuilder("[");
        for (int k = 0; k < dim; k++)
        {
            sb.Append(k > 0 ? "," : "").Append(VecComponent(t, j, k, gen).ToString("R", CultureInfo.InvariantCulture));
        }

        return sb.Append(']').ToString();
    }

    // The shell has no vec0 module, so the judge reads the shadow tables (the OneLite layout, docs/vec.md) with plain SQL: the rows table says where each
    // vector lives, the chunk table says which slots are live, the vector chunk holds the float32 bytes.
    private static void CheckVec(SqliteDb db, long m, List<string> bad)
    {
        foreach ((string tn, int dim) in new[] { ("va", 4), ("vb", 300) })
        {
            long want = 0;
            for (long t = Math.Max(1, m - DeleteLag + 1); t <= m; t++)
            {
                want += VecCount(t);
            }

            string? have = db.Scalar($"select count(*) from {tn}_rowids");
            if (have != want.ToString(CultureInfo.InvariantCulture))
            {
                bad.Add($"{tn} holds {have} vectors, the last transactions account for {want}");
                continue;
            }

            long live = 0;
            using (SqliteStatement st = db.Prepare($"select size, validity from {tn}_chunks"))
            {
                while (st.Step())
                {
                    foreach (byte b in st.Bytes(1))
                    {
                        live += System.Numerics.BitOperations.PopCount(b);
                    }
                }
            }

            if (live != want)
            {
                bad.Add($"{tn}: the chunks mark {live} slots live, {want} vectors exist");
            }

            for (long t = Math.Max(1, m - DeleteLag + 1); t <= m; t++)
            {
                int n = VecCount(t);
                using SqliteStatement st = db.Prepare(string.Create(CultureInfo.InvariantCulture, $"select r.rowid, substr(v.vectors, r.chunk_offset * {dim * 4} + 1, {dim * 4}) from {tn}_rowids r join {tn}_vector_chunks00 v on v.rowid = r.chunk_id where r.rowid >= {t * 1000} and r.rowid < {t * 1000 + 1000} order by r.rowid"));
                int j = 0;
                while (st.Step())
                {
                    byte[] got = st.Bytes(1);
                    int gen = j % 3 == 0 ? 1 : 0;
                    bool ok = st.GetInt64(0) == t * 1000 + j && got.Length == dim * 4;
                    for (int k = 0; ok && k < dim; k++)
                    {
                        ok = BitConverter.ToSingle(got, k * 4) == VecComponent(t, j, k, gen);
                    }

                    if (!ok)
                    {
                        bad.Add($"{tn} id {t * 1000 + j}: wrong vector");
                        return;
                    }

                    j++;
                }

                if (j != n)
                {
                    bad.Add($"{tn} txn {t}: {j} vectors, {n} committed");
                    return;
                }
            }
        }
    }

    /// <summary>Documents transaction t adds to the FTS5 tables (EngineWriter option fts=1).</summary>
    public static int FtsCount(long t) => 6 + (int)(t * 5 % 11);

    private static readonly string[] s_ftsWords = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa", "lambda", "mu", "running", "runs", "ran", "cats", "dog", "overdue", "invoice", "paid"];

    /// <summary>The text of document j of transaction t; <paramref name="updated"/> is the state after the transaction's own UPDATE.</summary>
    public static string FtsText(long t, int j, bool updated, int column)
    {
        long z = (t * 2654435761L + j * 40503L + column * 977L + (updated ? 12345 : 0)) & 0x7fffffffL;
        var sb = new System.Text.StringBuilder();
        int n = 3 + (int)(z % 40);
        for (int k = 0; k < n; k++)
        {
            z = (z * 1103515245L + 12345L) & 0x7fffffffL;
            sb.Append(s_ftsWords[(int)(z >> 8) % s_ftsWords.Length]).Append(k % 9 == 8 ? ". " : " ");
        }

        sb.Append('t').Append(t.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(updated ? "upd" : "new");
        return sb.ToString();
    }

    private static void CheckFts(SqliteDb db, long m, List<string> bad, string[] tables, string label)
    {
        foreach (string tn in tables)
        {
            string? chk;
            try
            {
                db.Scalar($"insert into {tn}({tn}) values('integrity-check')");
                chk = "ok";
            }
            catch (Exception e)
            {
                chk = e.Message;
            }

            if (chk != "ok")
            {
                bad.Add($"{label} integrity-check({tn}): {chk}");
                continue;
            }

            long want = 0;
            var perWord = new Dictionary<string, int>();
            for (long t = Math.Max(1, m - DeleteLag + 1); t <= m; t++)
            {
                want += FtsCount(t);
                for (int j = 0; j < FtsCount(t); j++)
                {
                    bool upd = j % 4 == 0;
                    var seen = new HashSet<string>();
                    for (int c = 0; c < 2; c++)
                    {
                        foreach (string tok in FtsText(t, j, upd, c).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        {
                            seen.Add(tok.TrimEnd('.'));
                        }
                    }

                    foreach (string wd in new[] { "alpha", "overdue", "upd", "new" })
                    {
                        if (seen.Contains(wd))
                        {
                            perWord[wd] = perWord.GetValueOrDefault(wd) + 1;
                        }
                    }
                }
            }

            if (db.Scalar($"select count(*) from {tn}") != want.ToString(CultureInfo.InvariantCulture))
            {
                bad.Add($"{tn} holds {db.Scalar($"select count(*) from {tn}")} documents, the last transactions account for {want}");
                continue;
            }

            foreach (string wd in new[] { "alpha", "overdue", "upd", "new" })
            {
                string got = db.Scalar($"select count(*) from {tn} where {tn} match '{wd}'") ?? "?";
                string exp = perWord.GetValueOrDefault(wd).ToString(CultureInfo.InvariantCulture);
                if (got != exp)
                {
                    bad.Add($"{tn} match '{wd}': {got} documents, want {exp}");
                }
            }

            for (long t = Math.Max(1, m - DeleteLag + 1); t <= m; t++)
            {
                for (int j = 0; j < FtsCount(t); j += 3)
                {
                    bool upd = j % 4 == 0;
                    string? got = db.Scalar(string.Create(CultureInfo.InvariantCulture, $"select a || '|' || b from {tn} where rowid = {t * 1000 + j}"));
                    if (got != FtsText(t, j, upd, 0) + "|" + FtsText(t, j, upd, 1))
                    {
                        bad.Add($"{tn} rowid {t * 1000 + j} has wrong content");
                        return;
                    }
                }
            }
        }
    }

    /// <summary>Boxes transaction t adds to the R-tree tables (EngineWriter option rtree=1).</summary>
    public static int RTreeCount(long t) => 20 + (int)(t * 7 % 31);

    /// <summary>The j-th box of transaction t: clustered corners so the tree has overlapping and nested entries.</summary>
    public static void RTreeBox(long t, int j, out int x, out int y, out int w, out int h)
    {
        long z = (t * 2654435761L + j * 40503L) & 0x7fffffffL;
        x = (int)(z % 997);
        y = (int)(z / 997 % 991);
        w = 1 + (int)(z % 13);
        h = 1 + (int)(z / 13 % 17);
    }

    private static void CheckRTree(SqliteDb db, long m, List<string> bad)
    {
        foreach (string tn in new[] { "rta", "rtb" })
        {
            string? chk = db.Scalar($"select rtreecheck('{tn}')");
            if (chk != "ok")
            {
                bad.Add($"rtreecheck({tn}): {chk}");
            }

            long want = 0;
            for (long t = Math.Max(1, m - DeleteLag + 1); t <= m; t++)
            {
                want += RTreeCount(t);
            }

            if (db.Scalar($"select count(*) from {tn}") != want.ToString(CultureInfo.InvariantCulture))
            {
                bad.Add($"{tn} holds {db.Scalar($"select count(*) from {tn}")} entries, the last transactions account for {want}");
                continue;
            }

            for (long t = Math.Max(1, m - DeleteLag + 1); t <= m; t++)
            {
                for (int j = 0; j < RTreeCount(t); j += 5)
                {
                    RTreeBox(t, j, out int x, out int y, out int w, out int h);
                    int shift = tn == "rta" && j % 4 == 0 ? 7 : 0;
                    string? got = db.Scalar(string.Create(CultureInfo.InvariantCulture, $"select cast(x0 as int) || ',' || cast(x1 as int) || ',' || cast(y0 as int) || ',' || cast(y1 as int) from {tn} where id = {t * 1000 + j}"));
                    string exp = string.Create(CultureInfo.InvariantCulture, $"{x + shift},{x + w + shift},{y},{y + h}");
                    if (got != exp)
                    {
                        bad.Add($"{tn} id {t * 1000 + j}: have {got}, want {exp}");
                        return;
                    }
                }
            }
        }
    }

    /// <summary>
    /// The side objects of the DDL workload (<see cref="EngineWriter"/> option ddl=1): transaction t creates table d(t) with an index and a view,
    /// t+1 adds a column to it, t+2 renames it to e(t), t+3 drops it. After transaction m exactly d(m), d(m-1) and e(m-2) exist.
    /// </summary>
    private static void CheckDdl(SqliteDb db, long m, List<string> bad)
    {
        var want = new List<string>();
        void Add(string n, long t)
        {
            if (t >= 1)
            {
                want.Add(n);
            }
        }

        Add("table d" + m, m);
        Add("table d" + (m - 1), m - 1);
        Add("table e" + (m - 2), m - 2);
        Add("view dv" + m, m);
        Add("view dv" + (m - 1), m - 1);
        Add("view dv" + (m - 2), m - 2);
        Add("index di" + m, m);
        Add("index di" + (m - 1), m - 1);
        Add("index di" + (m - 2), m - 2);
        want.Sort(StringComparer.Ordinal);
        var have = new List<string>();
        using (SqliteStatement st = db.Prepare("select type || ' ' || name from sqlite_schema where (name glob 'd[0-9]*' or name glob 'e[0-9]*' or name glob 'dv[0-9]*' or name glob 'di[0-9]*') and type in ('table','view','index')"))
        {
            while (st.Step())
            {
                have.Add(st.TextOrNull(0) ?? "");
            }
        }

        have.Sort(StringComparer.Ordinal);
        if (string.Join(",", want) != string.Join(",", have))
        {
            bad.Add($"ddl objects after transaction {m}: have [{string.Join(", ", have)}], want [{string.Join(", ", want)}]");
            return;
        }

        if (m >= 1 && db.Scalar($"select count(*) from d{m}") != "40")
        {
            bad.Add($"d{m} does not hold its 40 rows");
        }

        if (m >= 2 && db.Scalar($"select sum(e) from d{m - 1}") != "40")
        {
            bad.Add($"d{m - 1} lacks the column added in transaction {m}");
        }

        if (m >= 3 && db.Scalar($"select count(*) from e{m - 2} where cc is not null") != "40")
        {
            bad.Add($"e{m - 2} lost a renamed column");
        }
    }
}
