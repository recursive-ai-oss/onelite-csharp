using System;
using System.IO;
using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Storage.Pager;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>BEGIN CONCURRENT: row-level conflict detection by replaying a transaction's B-tree trace on the latest snapshot (docs/concurrency.md).</summary>
public sealed class ConcurrentTransactionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-conc-" + Guid.NewGuid().ToString("N"));

    public ConcurrentTransactionTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static readonly DatabaseOptions s_opts = new() { JournalMode = JournalMode.Wal, ConcurrentWrites = true, Synchronous = SynchronousMode.Off };

    private Database Open(string name = "c.db", bool wal = true)
    {
        Database db = Database.Open(Path.Combine(_dir, name), wal ? s_opts : new DatabaseOptions { ConcurrentWrites = true });
        return db;
    }

    private static void Run(Connection c, string sql)
    {
        using Statement st = c.Prepare(sql);
        try
        {
            while (st.Step())
            {
            }
        }
        catch (OneLiteException e) when (e.Detail is not null && Environment.GetEnvironmentVariable("ONELITE_TEST_DETAIL") == "1")
        {
            Console.Error.WriteLine($"{sql}: {e.ExtendedCode} {e.Detail}");
            throw;
        }
    }

    private static string Q(Connection c, string sql)
    {
        using Statement st = c.Prepare(sql);
        var sb = new StringBuilder();
        while (st.Step())
        {
            for (int i = 0; i < st.ColumnCount; i++)
            {
                sb.Append(i > 0 ? "|" : sb.Length > 0 ? ";" : "").Append(st.IsNull(i) ? "NULL" : st.GetString(i));
            }
        }

        return sb.ToString();
    }

    private static readonly string[] s_standardFiles = ["std.db", "std.db-shm", "std.db-wal"];

    private static OneLiteException Fails(Action a) => Assert.Throws<OneLiteException>(a);

    private static void Setup(Connection c)
    {
        Run(c, "CREATE TABLE t(id INTEGER PRIMARY KEY, v INT)");
        Run(c, "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30)");
    }

    [Fact]
    public void DisjointRowsBothCommit()
    {
        using Database db = Open();
        using Connection a = db.Connect();
        using Connection b = db.Connect();
        Setup(a);
        Run(a, "BEGIN CONCURRENT");
        Run(b, "BEGIN CONCURRENT");
        Run(a, "UPDATE t SET v = v + 1 WHERE id = 1");
        Run(b, "UPDATE t SET v = v + 1 WHERE id = 3");
        Run(a, "COMMIT");
        Run(b, "COMMIT");
        Assert.Equal("1|11;2|20;3|31", Q(a, "SELECT id, v FROM t ORDER BY id"));
        Assert.Equal("ok", Q(b, "PRAGMA integrity_check"));
    }

    [Fact]
    public void SameRowConflicts()
    {
        using Database db = Open();
        using Connection a = db.Connect();
        using Connection b = db.Connect();
        Setup(a);
        Run(a, "BEGIN CONCURRENT");
        Run(b, "BEGIN CONCURRENT");
        Run(a, "UPDATE t SET v = v + 1 WHERE id = 2");
        Run(b, "UPDATE t SET v = v + 5 WHERE id = 2");
        Run(a, "COMMIT");
        OneLiteException e = Fails(() => Run(b, "COMMIT"));
        Assert.Equal(ResultCode.BusySnapshot, e.ExtendedCode);
        Run(b, "ROLLBACK");
        Assert.Equal("21", Q(b, "SELECT v FROM t WHERE id = 2"));

        // The connection is usable again, and the lock is free.
        Run(b, "BEGIN CONCURRENT");
        Run(b, "UPDATE t SET v = 0 WHERE id = 2");
        Run(b, "COMMIT");
        Assert.Equal("0", Q(a, "SELECT v FROM t WHERE id = 2"));
    }

    [Fact]
    public void WriteSkewIsRefusedWhenSerializableAndAllowedUnderSnapshot()
    {
        foreach (bool snapshot in new[] { false, true })
        {
            string name = snapshot ? "si.db" : "ser.db";
            using Database db = Open(name);
            using Connection a = db.Connect();
            using Connection b = db.Connect();
            Setup(a);
            string begin = snapshot ? "BEGIN CONCURRENT SNAPSHOT" : "BEGIN CONCURRENT";
            Run(a, begin);
            Run(b, begin);
            Assert.Equal("10", Q(a, "SELECT v FROM t WHERE id = 1"));
            Assert.Equal("20", Q(b, "SELECT v FROM t WHERE id = 2"));
            Run(a, "UPDATE t SET v = 11 WHERE id = 2");
            Run(b, "UPDATE t SET v = 21 WHERE id = 1");
            Run(a, "COMMIT");
            if (snapshot)
            {
                Run(b, "COMMIT");
                Assert.Equal("1|21;2|11;3|30", Q(a, "SELECT id, v FROM t ORDER BY id"));
            }
            else
            {
                Assert.Equal(ResultCode.BusySnapshot, Fails(() => Run(b, "COMMIT")).ExtendedCode);
                Run(b, "ROLLBACK");
                Assert.Equal("1|10;2|11;3|30", Q(a, "SELECT id, v FROM t ORDER BY id"));
            }
        }
    }

    [Fact]
    public void ScannedRangeSeesInsertedRow()
    {
        using Database db = Open();
        using Connection a = db.Connect();
        using Connection b = db.Connect();
        Setup(a);
        Run(a, "BEGIN CONCURRENT");
        Run(b, "BEGIN CONCURRENT");
        Assert.Equal("3", Q(a, "SELECT count(*) FROM t"));
        Run(a, "INSERT INTO t VALUES (100, 1)");
        Run(b, "INSERT INTO t VALUES (101, 1)");
        Run(b, "COMMIT");
        Assert.Equal(ResultCode.BusySnapshot, Fails(() => Run(a, "COMMIT")).ExtendedCode);
        Run(a, "ROLLBACK");
    }

    [Fact]
    public void AppendersGetDistinctRowidsAndDoNotConflict()
    {
        using Database db = Open();
        using Connection a = db.Connect();
        using Connection b = db.Connect();
        Setup(a);
        Run(a, "BEGIN CONCURRENT");
        Run(b, "BEGIN CONCURRENT");
        Run(a, "INSERT INTO t(v) VALUES (100)");
        string ra = Q(a, "SELECT last_insert_rowid()");
        Run(b, "INSERT INTO t(v) VALUES (200)");
        string rb = Q(b, "SELECT last_insert_rowid()");
        Assert.NotEqual(ra, rb);
        Run(b, "COMMIT");
        Run(a, "COMMIT");
        Assert.Equal("100", Q(a, $"SELECT v FROM t WHERE id = {ra}"));
        Assert.Equal("200", Q(a, $"SELECT v FROM t WHERE id = {rb}"));
        Assert.Equal("5", Q(a, "SELECT count(*) FROM t"));
    }

    [Fact]
    public void UniqueIndexIsEnforcedAcrossTransactions()
    {
        using Database db = Open();
        using Connection a = db.Connect();
        using Connection b = db.Connect();
        Run(a, "CREATE TABLE u(id INTEGER PRIMARY KEY, k TEXT UNIQUE)");
        foreach (string mode in new[] { "BEGIN CONCURRENT", "BEGIN CONCURRENT SNAPSHOT" })
        {
            Run(a, "DELETE FROM u");
            Run(a, mode);
            Run(b, mode);
            Run(a, "INSERT INTO u(k) VALUES ('x')");
            Run(b, "INSERT INTO u(k) VALUES ('x')");
            Run(a, "COMMIT");
            Assert.Equal(ResultCode.BusySnapshot, Fails(() => Run(b, "COMMIT")).ExtendedCode);
            Run(b, "ROLLBACK");
            Assert.Equal("1", Q(a, "SELECT count(*) FROM u"));
        }
    }

    [Fact]
    public void RolledBackStatementsLeaveNoTrace()
    {
        using Database db = Open();
        using Connection a = db.Connect();
        using Connection b = db.Connect();
        Setup(a);
        Run(a, "BEGIN CONCURRENT");
        Run(a, "INSERT INTO t VALUES (50, 1)");
        Fails(() => Run(a, "INSERT INTO t VALUES (50, 2)"));
        Run(a, "SAVEPOINT s");
        Run(a, "INSERT INTO t VALUES (51, 1)");
        Run(a, "ROLLBACK TO s");
        Run(b, "INSERT INTO t VALUES (7, 7)");
        Run(a, "COMMIT");
        Assert.Equal("1;2;3;7;50", string.Join(';', Q(a, "SELECT id FROM t ORDER BY id").Split(';')));
        Assert.Equal("ok", Q(a, "PRAGMA integrity_check"));
    }

    [Fact]
    public void RequiresWalMode()
    {
        using Database db = Open("rollback.db", wal: false);
        using Connection a = db.Connect();
        Setup(a);
        OneLiteException e = Fails(() => Run(a, "BEGIN CONCURRENT"));
        Assert.Contains("WAL", e.Message, StringComparison.Ordinal);
        Run(a, "BEGIN");
        Run(a, "COMMIT");
    }

    [Fact]
    public void SyntaxErrorWithoutTheOption()
    {
        using Database db = Database.Open(Path.Combine(_dir, "plain.db"), new DatabaseOptions { JournalMode = JournalMode.Wal });
        using Connection a = db.Connect();
        Assert.Throws<OneLiteException>(() => a.Prepare("BEGIN CONCURRENT"));
    }

    [Fact]
    public void AutoincrementNeverReusesAnId()
    {
        using Database db = Open();
        using Connection a = db.Connect();
        using Connection b = db.Connect();
        Run(a, "CREATE TABLE s(id INTEGER PRIMARY KEY AUTOINCREMENT, v INT)");
        Run(a, "INSERT INTO s(v) VALUES (1),(2),(3)");
        Run(a, "DELETE FROM s WHERE id = 3");
        Run(a, "BEGIN CONCURRENT");
        Run(b, "BEGIN CONCURRENT");
        Run(a, "INSERT INTO s(v) VALUES (4)");
        Run(b, "INSERT INTO s(v) VALUES (5)");
        long ia = long.Parse(Q(a, "SELECT last_insert_rowid()"), System.Globalization.CultureInfo.InvariantCulture);
        long ib = long.Parse(Q(b, "SELECT last_insert_rowid()"), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(ia > 3 && ib > 3 && ia != ib);
        Run(a, "COMMIT");
        try
        {
            Run(b, "COMMIT");
        }
        catch (OneLiteException e)
        {
            // Out-of-order commits may be refused to keep the counter monotone.
            Assert.Equal(ResultCode.BusySnapshot, e.ExtendedCode);
            Run(b, "ROLLBACK");
        }

        Assert.Equal("ok", Q(a, "PRAGMA integrity_check"));
        Assert.Equal(Q(a, "SELECT max(id) FROM s"), Q(a, "SELECT seq FROM sqlite_sequence WHERE name = 's'"));
    }

    [Fact]
    public void RowidsAndForeignKeyChildrenSurviveReplay()
    {
        using Database db = Open();
        using Connection a = db.Connect();
        using Connection b = db.Connect();
        Run(a, "PRAGMA foreign_keys = ON");
        Run(b, "PRAGMA foreign_keys = ON");
        Run(a, "CREATE TABLE p(id INTEGER PRIMARY KEY, x INT)");
        Run(a, "CREATE TABLE c(id INTEGER PRIMARY KEY, pid INT REFERENCES p(id))");
        Run(a, "BEGIN CONCURRENT");
        Run(b, "BEGIN CONCURRENT");
        Run(a, "INSERT INTO p(x) VALUES (1)");
        string id = Q(a, "SELECT last_insert_rowid()");
        Run(a, $"INSERT INTO c(pid) VALUES ({id})");
        Run(b, "INSERT INTO p(x) VALUES (2)");
        Run(b, "COMMIT");
        Run(a, "COMMIT");
        Assert.Equal("1", Q(a, $"SELECT x FROM p WHERE id = {id}"));
        Assert.Equal(id, Q(a, "SELECT pid FROM c"));
        Assert.Equal(string.Empty, Q(a, "PRAGMA foreign_key_check"));
    }

    [Fact]
    public void ParentDeletedMeanwhileRefusesTheChild()
    {
        using Database db = Open();
        using Connection a = db.Connect();
        using Connection b = db.Connect();
        Run(a, "PRAGMA foreign_keys = ON");
        Run(b, "PRAGMA foreign_keys = ON");
        Run(a, "CREATE TABLE p(id INTEGER PRIMARY KEY)");
        Run(a, "CREATE TABLE c(id INTEGER PRIMARY KEY, pid INT REFERENCES p(id))");
        Run(a, "INSERT INTO p VALUES (1), (2)");
        Run(a, "BEGIN CONCURRENT");
        Run(b, "BEGIN CONCURRENT");
        Run(a, "INSERT INTO c(pid) VALUES (1)");
        Run(b, "DELETE FROM p WHERE id = 1");
        Run(b, "COMMIT");
        Assert.Equal(ResultCode.BusySnapshot, Fails(() => Run(a, "COMMIT")).ExtendedCode);
        Run(a, "ROLLBACK");
        Assert.Equal("0", Q(a, "SELECT count(*) FROM c"));
    }

    [Fact]
    public void TriggerEffectsAreReplayed()
    {
        using Database db = Open();
        using Connection a = db.Connect();
        using Connection b = db.Connect();
        Run(a, "CREATE TABLE t(id INTEGER PRIMARY KEY, v INT)");
        Run(a, "CREATE TABLE log(id INTEGER PRIMARY KEY, tid INT)");
        Run(a, "CREATE TRIGGER tr AFTER INSERT ON t BEGIN INSERT INTO log(tid) VALUES (new.id); END");
        Run(a, "BEGIN CONCURRENT");
        Run(b, "BEGIN CONCURRENT");
        Run(a, "INSERT INTO t(v) VALUES (1)");
        Run(b, "INSERT INTO t(v) VALUES (2)");
        Run(b, "COMMIT");
        Run(a, "COMMIT");
        Assert.Equal("2", Q(a, "SELECT count(*) FROM log"));
        Assert.Equal("0", Q(a, "SELECT count(*) FROM log WHERE tid NOT IN (SELECT id FROM t)"));
    }

    [Fact]
    public void MaxRowidReadConflictsWithAnAppendOnlyWhenSerializable()
    {
        foreach (bool snapshot in new[] { false, true })
        {
            using Database db = Open(snapshot ? "mx-si.db" : "mx-ser.db");
            using Connection a = db.Connect();
            using Connection b = db.Connect();
            Setup(a);
            string begin = snapshot ? "BEGIN CONCURRENT SNAPSHOT" : "BEGIN CONCURRENT";
            Run(a, begin);
            Run(b, begin);
            Assert.Equal("3", Q(a, "SELECT max(id) FROM t"));
            Run(a, "INSERT INTO t(v) VALUES (0)");
            Run(b, "INSERT INTO t(v) VALUES (1)");
            Run(b, "COMMIT");
            if (snapshot)
            {
                Run(a, "COMMIT");
            }
            else
            {
                Assert.Equal(ResultCode.BusySnapshot, Fails(() => Run(a, "COMMIT")).ExtendedCode);
                Run(a, "ROLLBACK");
            }
        }
    }

    [Fact]
    public void SameExplicitRowidCommitsOnce()
    {
        using Database db = Open();
        using Connection a = db.Connect();
        using Connection b = db.Connect();
        Setup(a);
        Run(a, "BEGIN CONCURRENT SNAPSHOT");
        Run(b, "BEGIN CONCURRENT SNAPSHOT");
        Run(a, "INSERT INTO t VALUES (900, 1)");
        Run(b, "INSERT INTO t VALUES (900, 2)");
        Run(a, "COMMIT");
        Assert.Equal(ResultCode.BusySnapshot, Fails(() => Run(b, "COMMIT")).ExtendedCode);
        Run(b, "ROLLBACK");
        Assert.Equal("1", Q(a, "SELECT v FROM t WHERE id = 900"));
    }

    [Fact]
    public void RowidsAtTheTopOfTheRangeFollowTheOrdinaryRule()
    {
        using Database db = Open();
        using Connection a = db.Connect();
        Run(a, "CREATE TABLE t(id INTEGER PRIMARY KEY, v INT)");
        Run(a, "CREATE TABLE s(id INTEGER PRIMARY KEY AUTOINCREMENT, v INT)");
        Run(a, "INSERT INTO t VALUES (9223372036854775807, 0)");
        Run(a, "INSERT INTO s VALUES (9223372036854775807, 0)");
        Run(a, "BEGIN CONCURRENT");
        Run(a, "INSERT INTO t(v) VALUES (1)");
        Run(a, "INSERT INTO t(v) VALUES (2)");
        OneLiteException full = Fails(() => Run(a, "INSERT INTO s(v) VALUES (1)"));
        Assert.Equal(ResultCode.Full, full.Code);
        Run(a, "COMMIT");
        Assert.Equal("3", Q(a, "SELECT count(*) FROM t"));
        Assert.Equal("3", Q(a, "SELECT count(DISTINCT id) FROM t"));
    }

    [Fact]
    public void WithoutRowidTablesConflictOnTheSameKeyOnly()
    {
        using Database db = Open();
        using Connection a = db.Connect();
        using Connection b = db.Connect();
        Run(a, "CREATE TABLE w(k TEXT PRIMARY KEY, v INT) WITHOUT ROWID");
        Run(a, "BEGIN CONCURRENT SNAPSHOT");
        Run(b, "BEGIN CONCURRENT SNAPSHOT");
        Run(a, "INSERT INTO w VALUES ('a', 1)");
        Run(b, "INSERT INTO w VALUES ('b', 2)");
        Run(a, "COMMIT");
        Run(b, "COMMIT");
        Assert.Equal("2", Q(a, "SELECT count(*) FROM w"));
        Run(a, "BEGIN CONCURRENT SNAPSHOT");
        Run(b, "BEGIN CONCURRENT SNAPSHOT");
        Run(a, "INSERT OR REPLACE INTO w VALUES ('c', 1)");
        Run(b, "INSERT OR REPLACE INTO w VALUES ('c', 2)");
        Run(a, "COMMIT");
        Assert.Equal(ResultCode.BusySnapshot, Fails(() => Run(b, "COMMIT")).ExtendedCode);
        Run(b, "ROLLBACK");
    }

    [Fact]
    public void AutoincrementDoesNotReuseAnIdADeletedRowHeld()
    {
        using Database db = Open();
        using Connection a = db.Connect();
        using Connection stale = db.Connect();
        Run(a, "CREATE TABLE s(id INTEGER PRIMARY KEY AUTOINCREMENT, v INT)");
        Run(a, "INSERT INTO s(v) VALUES (1)");
        Run(stale, "BEGIN CONCURRENT SNAPSHOT");
        Run(stale, "INSERT INTO s(v) VALUES (9)");
        long id = long.Parse(Q(stale, "SELECT last_insert_rowid()"), System.Globalization.CultureInfo.InvariantCulture);

        // Another connection uses that id and deletes it again before the stale transaction commits.
        Run(a, $"INSERT INTO s(id, v) VALUES ({id}, 2)");
        Run(a, $"DELETE FROM s WHERE id = {id}");
        Assert.Equal(ResultCode.BusySnapshot, Fails(() => Run(stale, "COMMIT")).ExtendedCode);
        Run(stale, "ROLLBACK");
        Assert.Equal("1", Q(a, "SELECT count(*) FROM s"));
    }

    [Fact]
    public void FilesStayStandardForTheShell()
    {
        Assert.SkipUnless(File.Exists(RecursiveAI.OneLite.Oracle.OraclePaths.Cli), "the pinned sqlite3 shell is not installed");
        string path = Path.Combine(_dir, "std.db");
        using (Database db = Database.Open(path, s_opts))
        using (Connection a = db.Connect())
        using (Connection b = db.Connect())
        {
            Setup(a);
            Run(a, "BEGIN CONCURRENT");
            Run(b, "BEGIN CONCURRENT");
            Run(a, "INSERT INTO t(v) VALUES (4)");
            Run(b, "INSERT INTO t(v) VALUES (5)");
            Run(b, "UPDATE t SET v = 99 WHERE id = 1");
            Run(a, "COMMIT");
            Run(b, "COMMIT");
        }

        string dump = RecursiveAI.OneLite.Oracle.SqliteCli.Dump(path);
        Assert.Contains("99", dump, StringComparison.Ordinal);
        Assert.True(RecursiveAI.OneLite.Oracle.PinnedSqlite.IntegrityOk(path, out string detail), detail);
        Assert.All(Directory.GetFiles(_dir).Select(Path.GetFileName), f => Assert.Contains(f, s_standardFiles));
    }
}
