using System.Linq;
using System.Data;
using System.Data.Common;
using System.Globalization;
using RecursiveAI.OneLite.Storage.Vfs;
using Xunit;

namespace RecursiveAI.OneLite.Data.Tests;

/// <summary>A memory backend that counts the bytes read from the database file, so a test can bound what a query touches.</summary>
internal sealed class CountingVfs(MemoryVfs inner) : VfsBase
{
    private long _bytes;
    private long _reads;

    public override string Name => "counting";

    public override VfsCaps Caps => VfsCaps.Sync | VfsCaps.Async | VfsCaps.BlockingOk;

    public long BytesRead => Interlocked.Read(ref _bytes);

    public long Reads => Interlocked.Read(ref _reads);

    public void Reset()
    {
        Interlocked.Exchange(ref _bytes, 0);
        Interlocked.Exchange(ref _reads, 0);
    }

    public override IVfsFile Open(string path, VfsOpenOptions flags) => new CountingFile(this, inner.Open(path, flags));

    public override bool Access(string path, AccessMode mode) => inner.Access(path, mode);

    public override void Delete(string path, bool syncDirectory) => inner.Delete(path, syncDirectory);

    public override string FullPathname(string path) => inner.FullPathname(path);

    private sealed class CountingFile(CountingVfs owner, IVfsFile file) : VfsFileBase
    {
        public override VfsCaps Caps => owner.Caps | (file.Caps & VfsCaps.SharedMemory);

        public override int SectorSize => file.SectorSize;

        public override DeviceCharacteristics Device => file.Device;

        public override IVfsShm? Shm => file.Shm;

        public override int Read(long offset, Span<byte> dst)
        {
            int n = file.Read(offset, dst);
            Interlocked.Add(ref owner._bytes, n);
            Interlocked.Increment(ref owner._reads);
            return n;
        }

        public override void Write(long offset, ReadOnlySpan<byte> src) => file.Write(offset, src);

        public override void Sync(SyncMode mode) => file.Sync(mode);

        public override void SyncDirectory() => file.SyncDirectory();

        public override void Truncate(long size) => file.Truncate(size);

        public override long FileSize() => file.FileSize();

        public override bool Lock(LockLevel level) => file.Lock(level);

        public override void Unlock(LockLevel level) => file.Unlock(level);

        public override bool CheckReservedLock() => file.CheckReservedLock();

        public override void Dispose() => file.Dispose();
    }
}

/// <summary>Fixes in the patch release: the read-only happy path, IN answered by seeks, and column-less rows through ADO.NET.</summary>
public sealed class PatchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MemoryVfs Build(string sql)
    {
        var mem = new MemoryVfs();
        using Database db = Database.Open("p.db", new DatabaseOptions { Vfs = mem });
        using Connection c = db.Connect();
        string rest = sql;
        while (!string.IsNullOrWhiteSpace(rest))
        {
            using Statement st = c.Prepare(rest);
            rest = st.Tail;
            while (st.Step())
            {
            }
        }

        return mem;
    }

    // ------------------------------------------------------------------ a read-only Database serves the happy path

    private const string UsersSql = "create table users(id integer primary key, name text not null, age integer not null, email text);"
        + "insert into users values (1,'ann',30,null),(2,'bob',41,'b@x'),(3,'cy',25,null);";

    [Fact]
    public async Task ReadOnlyDatabaseAnswersQueriesAsynchronously()
    {
        var vfs = new CountingVfs(Build(UsersSql));
        await using Database db = await Database.OpenReadOnlyAsync("p.db", new DatabaseOptions { Vfs = vfs }, Ct);
        int age = 26;
        Assert.Equal(["ann", "bob"], await db.QueryAsync<string>($"select name from users where age > {age} order by id", Ct));
        Assert.Equal(3L, await db.ScalarAsync<long>($"select count(*) from users", Ct));
        User? bob = await db.Table<User>().GetAsync(2L, Ct);
        Assert.Equal("bob", bob?.Name);
        List<User> old = await db.Table<User>().WhereAsync($"age > {28}", Ct);
        Assert.Equal(2, old.Count);

        OneLiteException e = await Assert.ThrowsAsync<OneLiteException>(async () => await db.ExecuteAsync($"insert into users values (9, 'z', 1, null)", Ct));
        Assert.Equal(ResultCode.ReadOnly, e.Code);
        Assert.Equal("attempt to write a readonly database", e.Message);
        Assert.Equal(3L, await db.ScalarAsync<long>($"select count(*) from users", Ct));
    }

    [Fact]
    public void ReadOnlyDatabaseAnswersQueriesSynchronously()
    {
        var vfs = new CountingVfs(Build(UsersSql));
        using Database db = Database.OpenReadOnly("p.db", new DatabaseOptions { Vfs = vfs });
        Assert.Equal(["ann", "bob", "cy"], db.Query<string>($"select name from users order by id"));
        Assert.Equal(3L, db.Scalar<long>($"select count(*) from users"));
        Assert.Equal("cy", db.Table<User>().Get(3L)?.Name);
        Assert.Single(db.Table<User>().Where($"age < {30}"));
        OneLiteException e = Assert.Throws<OneLiteException>(() => db.Execute($"delete from users"));
        Assert.Equal(ResultCode.ReadOnly, e.Code);
        Assert.Equal("attempt to write a readonly database", e.Message);
    }

    [Fact]
    public async Task ReadOnlyDatabaseSearches()
    {
        MemoryVfs mem = Build("create table docs(id integer primary key, body text not null, year integer not null) strict;"
            + "create virtual table docs_fts using fts5(body);"
            + "insert into docs values (1,'overdue invoice',2023),(2,'holiday schedule',2024);"
            + "insert into docs_fts(rowid, body) values (1,'overdue invoice'),(2,'holiday schedule');"
            + "create virtual table docs_vec using vec0(embedding float[2]);");
        await using Database db = await Database.OpenReadOnlyAsync("p.db", new DatabaseOptions { Vfs = new CountingVfs(mem) }, Ct);
        List<SearchHit<Doc>> hits = await db.SearchAsync<Doc>("docs", "invoice", default, 5, null, Ct);
        Assert.Single(hits);
        Assert.Equal(1L, hits[0].Item.Id);
    }

    [Fact]
    public void WriteTransactionOnAReadOnlyDatabaseFailsWithTheReadOnlyCode()
    {
        using Database db = Database.OpenReadOnly("p.db", new DatabaseOptions { Vfs = new CountingVfs(Build(UsersSql)) });
        OneLiteException e = Assert.Throws<OneLiteException>(() => db.Write(tx => tx.Execute($"delete from users")));
        Assert.Equal(ResultCode.ReadOnly, e.Code);
    }

    [Fact]
    public void HandWrittenOpenSaysWhichMethodsToUse()
    {
        using Database db = Database.Open("m.db", new DatabaseOptions { Vfs = new MemoryVfs() });
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => db.Query<long>($"select 1"));
        Assert.Contains("OpenReadOnly", e.Message, StringComparison.Ordinal);
        Assert.Contains("OpenAsync", e.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ IN is answered by seeks

    private const string BigSql = "create table t(id integer primary key, a text, b integer, c blob);"
        + "create index ta on t(a); create index tb on t(b);"
        + "create table w(k text primary key, v integer) without rowid;"
        + "begin;"
        + "with recursive r(i) as (select 1 union all select i+1 from r where i < 20000) insert into t select i, 'k' || (i % 5000), i % 3000, zeroblob(100) from r;"
        + "with recursive r(i) as (select 1 union all select i+1 from r where i < 20000) insert into w select 'w' || i, i from r;"
        + "commit;";

    private static (long Bytes, List<string> Rows) Count(MemoryVfs mem, string sql)
    {
        var vfs = new CountingVfs(mem);
        using Database db = Database.OpenReadOnly("p.db", new DatabaseOptions { Vfs = vfs });
        using Connection c = db.Connect();
        using (Statement warm = c.Prepare("select 1 from sqlite_schema limit 1"))
        {
            while (warm.Step())
            {
            }
        }

        vfs.Reset();
        var rows = new List<string>();
        using Statement st = c.Prepare(sql);
        while (st.Step())
        {
            rows.Add(st.GetFieldValue<string>(0));
        }

        return (vfs.BytesRead, rows);
    }

    [Theory]
    [InlineData("select id from t where id in (7, 19000)", "select id from t where +id in (7, 19000)", 8)]
    [InlineData("select id from t where id in (select 7 union all select 19000)", "select id from t where +id in (7, 19000)", 8)]
    [InlineData("select id from t where a in ('k3', 'k4999')", "select id from t where +a in ('k3', 'k4999')", 40)]
    [InlineData("select id from t where b in (2999, 5)", "select id from t where +b in (2999, 5)", 80)]
    [InlineData("select k from w where k in ('w5', 'w19999')", "select k from w where +k in ('w5', 'w19999')", 12)]
    public void InSeeksInsteadOfScanning(string seek, string scan, int maxPages)
    {
        MemoryVfs mem = Build(BigSql);
        (long seekBytes, List<string> seekRows) = Count(mem, seek);
        (long scanBytes, List<string> scanRows) = Count(mem, scan);
        // The same rows in the same order as a scan filtering them (a rowid scan is in key order), at a small fraction of the reads.
        if (!seek.Contains(" a in", StringComparison.Ordinal) && !seek.Contains(" b in", StringComparison.Ordinal))
        {
            Assert.Equal(scanRows, seekRows);
        }
        else
        {
            Assert.Equal(scanRows.Order(StringComparer.Ordinal), seekRows.Order(StringComparer.Ordinal));
        }

        Assert.True(seekBytes <= maxPages * 4096L, $"{seek}: read {seekBytes} bytes (limit {maxPages} pages); the scan read {scanBytes}");
        Assert.True(scanBytes > 3 * seekBytes, "the control scan should read a lot more");
    }

    [Fact]
    public void InOrderDuplicatesAndNullsFollowTheReference()
    {
        MemoryVfs mem = Build("create table t(id integer primary key, a text, b integer); create index ta on t(a); create index tb on t(b);"
            + "insert into t select value, 'k' || (value % 7), value % 5 from generate_series(1, 50);");
        Assert.Equal(["1", "3", "9", "40"], Count(mem, "select id from t where id in (9, 3, 3, 40, null, 1, 77)").Rows);
        Assert.Equal(["1", "3", "9"], Count(mem, "select id from t where id in ('9', 3.0, 1, 'x', 2.5)").Rows);
        Assert.Equal(Count(mem, "select id from t where +a in ('k3', 'k1', 'k3')").Rows.Order(StringComparer.Ordinal), Count(mem, "select id from t where a in ('k3', 'k1', 'k3')").Rows.Order(StringComparer.Ordinal));
        Assert.Equal(["5", "1", "6", "2", "7", "3", "8"], Count(mem, "select id from t where b in (3, 1, '1', 2.0, null, 0) and id < 9 order by b, id").Rows);
    }

    // ------------------------------------------------------------------ PRAGMA incremental_vacuum through ADO.NET

    private const string FreeSql = "pragma auto_vacuum=2; create table t(a integer primary key, b text);"
        + "insert into t(b) select hex(zeroblob(3000)) from generate_series(1, 60); delete from t;";

    private static int ReaderRows(DbConnection conn, string sql)
    {
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using DbDataReader r = cmd.ExecuteReader();
        Assert.Equal(0, r.FieldCount);
        int n = 0;
        while (r.Read())
        {
            n++;
        }

        return n;
    }

    [Fact]
    public async Task IncrementalVacuumReturnsOneRowPerPageThroughEverySurface()
    {
        await using var conn = new OneLiteConnection("Data Source=:memory:");
        conn.Open();
        using (DbCommand setup = conn.CreateCommand())
        {
            setup.CommandText = FreeSql;
            setup.ExecuteNonQuery();
        }

        long free = Convert.ToInt64(Scalar(conn, "pragma freelist_count"), CultureInfo.InvariantCulture);
        Assert.True(free > 40, $"setup should free many pages, freed {free}");
        Assert.Equal(10, ReaderRows(conn, "pragma incremental_vacuum(10)"));
        Assert.Equal(free - 10, Convert.ToInt64(Scalar(conn, "pragma freelist_count"), CultureInfo.InvariantCulture));

        // The non-query path drains the rows, so the work is done in full.
        using (DbCommand nq = conn.CreateCommand())
        {
            nq.CommandText = "pragma incremental_vacuum(10)";
            nq.ExecuteNonQuery();
        }

        Assert.Equal(free - 20, Convert.ToInt64(Scalar(conn, "pragma freelist_count"), CultureInfo.InvariantCulture));
        using (DbCommand nq = conn.CreateCommand())
        {
            nq.CommandText = "pragma incremental_vacuum(10)";
            await nq.ExecuteNonQueryAsync(Ct);
        }

        Assert.Equal(free - 30, Convert.ToInt64(Scalar(conn, "pragma freelist_count"), CultureInfo.InvariantCulture));

        using (DbCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "pragma incremental_vacuum(5)";
            await using DbDataReader r = await cmd.ExecuteReaderAsync(Ct);
            Assert.True(r.HasRows);
            int n = 0;
            while (await r.ReadAsync(Ct))
            {
                n++;
            }

            Assert.Equal(5, n);
            Assert.False(await r.NextResultAsync(Ct));
        }

        // Nothing left to free after a large request: the count is the pages that were there, then no rows.
        Assert.Equal(free - 35, Convert.ToInt64(Scalar(conn, "pragma freelist_count"), CultureInfo.InvariantCulture));
        Assert.Equal((int)(free - 35), ReaderRows(conn, "pragma incremental_vacuum(100000)"));
        Assert.Equal(0, ReaderRows(conn, "pragma incremental_vacuum(3)"));
    }

    [Fact]
    public void LeavingAColumnlessResultEarlyStillRunsItToTheEnd()
    {
        using var conn = new OneLiteConnection("Data Source=:memory:");
        conn.Open();
        using DbCommand setup = conn.CreateCommand();
        setup.CommandText = FreeSql;
        setup.ExecuteNonQuery();
        long free = Convert.ToInt64(Scalar(conn, "pragma freelist_count"), CultureInfo.InvariantCulture);

        // Disposing the reader without reading, reading one row, or asking for a scalar all finish the work, as a statement without rows would.
        using (DbCommand a = conn.CreateCommand())
        {
            a.CommandText = "pragma incremental_vacuum(5)";
            a.ExecuteReader().Dispose();
        }

        Assert.Equal(free - 5, Convert.ToInt64(Scalar(conn, "pragma freelist_count"), CultureInfo.InvariantCulture));
        using (DbCommand b = conn.CreateCommand())
        {
            b.CommandText = "pragma incremental_vacuum(5)";
            using DbDataReader r = b.ExecuteReader();
            Assert.True(r.Read());
        }

        Assert.Equal(free - 10, Convert.ToInt64(Scalar(conn, "pragma freelist_count"), CultureInfo.InvariantCulture));
        using (DbCommand c = conn.CreateCommand())
        {
            c.CommandText = "pragma incremental_vacuum(5)";
            Assert.Null(c.ExecuteScalar());
        }

        Assert.Equal(free - 15, Convert.ToInt64(Scalar(conn, "pragma freelist_count"), CultureInfo.InvariantCulture));
    }

    [Fact]
    public void IncrementalVacuumInsideABatchOfStatements()
    {
        using var conn = new OneLiteConnection("Data Source=:memory:");
        conn.Open();
        using DbCommand setup = conn.CreateCommand();
        setup.CommandText = FreeSql;
        setup.ExecuteNonQuery();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "insert into t(b) values ('x'); pragma incremental_vacuum(4); select count(*) from t";
        using DbDataReader r = cmd.ExecuteReader();
        int rows = 0;
        while (r.Read())
        {
            rows++;
        }

        Assert.Equal(4, rows);
        Assert.True(r.NextResult());
        Assert.True(r.Read());
        Assert.Equal(1L, r.GetInt64(0));
    }

    private static object? Scalar(DbConnection conn, string sql)
    {
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
