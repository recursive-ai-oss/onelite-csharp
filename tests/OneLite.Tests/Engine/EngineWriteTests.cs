using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RecursiveAI.OneLite.Oracle;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.Pager;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// The write path: transactions, savepoints, INSERT/UPDATE/DELETE with constraints and indexes. Results are compared with the pinned
/// <c>sqlite3</c> shell (rows, errors, and the database file's <c>.dump</c>), and every file OneLite writes must pass its integrity check.
/// </summary>
public sealed class EngineWriteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-write-" + Guid.NewGuid().ToString("N"));

    public EngineWriteTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static void NeedOracle() => Assert.SkipUnless(File.Exists(OraclePaths.Cli), "the pinned sqlite3 shell is not installed");

    /// <summary>Runs a script statement by statement the way the shell's list mode prints it: rows as |-joined text, failures as "Error: message".</summary>
    private static string RunScript(Connection conn, string script)
    {
        var o = new StringBuilder();
        string rest = script;
        while (!string.IsNullOrWhiteSpace(rest))
        {
            Statement st;
            try
            {
                st = conn.Prepare(rest);
            }
            catch (OneLiteException e)
            {
                o.Append("Error: ").Append(e.Message).Append('\n');
                int semi = rest.IndexOf(';', StringComparison.Ordinal);
                rest = semi < 0 ? string.Empty : rest[(semi + 1)..];
                continue;
            }

            rest = st.Tail;
            try
            {
                while (st.Step())
                {
                    for (int i = 0; i < st.ColumnCount; i++)
                    {
                        if (i > 0)
                        {
                            o.Append('|');
                        }

                        if (!st.IsNull(i))
                        {
                            o.Append(st.GetString(i));
                        }
                    }

                    o.Append('\n');
                }
            }
            catch (OneLiteException e)
            {
                o.Append("Error: ").Append(e.Message).Append('\n');
            }

            st.Dispose();
        }

        return o.ToString();
    }

    private static string ShellOutput(string db, string script)
    {
        Proc.Result r = Proc.Run("sh", ["-c", "exec \"$0\" \"$1\" 2>&1", OraclePaths.Cli, db], script);
        var o = new StringBuilder();
        foreach (string l in (r.Stdout + r.Stderr).Split('\n'))
        {
            string line = l;
            int at = line.IndexOf(": ", StringComparison.Ordinal);
            if ((line.StartsWith("Error near line", StringComparison.Ordinal) || line.StartsWith("Parse error near line", StringComparison.Ordinal)) && at > 0)
            {
                line = "Error: " + line[(at + 2)..];
            }

            if (line.Length > 0)
            {
                o.Append(line).Append('\n');
            }
        }

        return o.ToString();
    }

    private void SameAsShell(string script, DatabaseOptions? options = null, string preamble = "")
    {
        NeedOracle();
        string a = Path.Combine(_dir, Guid.NewGuid().ToString("N") + "-ol.db");
        string b = Path.Combine(_dir, Guid.NewGuid().ToString("N") + "-sq.db");
        if (Environment.GetEnvironmentVariable("ONELITE_TEST_DUMP") is { Length: > 0 } dump)
        {
            File.WriteAllText(dump, script);
        }

        string ours;
        using (Database db = Database.Open(a, options))
        using (Connection conn = db.Connect())
        {
            ours = RunScript(conn, script);
        }

        if (preamble.Length > 0)
        {
            SqliteCli.Run(b, preamble);
        }

        string theirs = ShellOutput(b, script);
        Assert.Equal(theirs, ours);
        Assert.Equal("ok", SqliteCli.Run(a, "PRAGMA integrity_check;").Stdout.Trim());
        Assert.Equal(SqliteCli.Dump(b), SqliteCli.Dump(a));
    }

    [Fact]
    public void ConstraintsAndConflictClauses()
    {
        SameAsShell("""
            create table t1 ( a integer primary key autoincrement, b text not null, c unique, d check (d>0), e default 5, f references t1(a) on delete cascade);
            create unique index  i1 on t1(b collate nocase desc, c) where c>1;
            insert into t1(b,c,d) values('x',1,1);
            select * from sqlite_sequence;
            insert into t1(b,c,d) values('x',1,1);
            insert into t1(b,c,d) values(null,2,1);
            insert into t1(b,c,d) values('x',3,0);
            insert into t1(b,c,d) values('X',3,1);
            insert into t1(b,c,d) values('x',3,1);
            insert or ignore into t1(b,c,d) values('x',3,1);
            insert or replace into t1(b,c,d) values('y',3,1);
            select * from t1;
            create table t2(a primary key, b, constraint ck check (b>1), unique(a,b));
            insert into t2 values(1,0);
            insert into t2 values(1,2);
            insert into t2 values(1,3);
            create table t3(a integer primary key, b);
            insert into t3 values('x',1);
            insert into t3 values(1.5,1);
            insert into t3 values('7',1);
            create table t4(a int, b text) strict;
            insert into t4 values('x',1);
            insert into t4 values('12',1);
            select a, typeof(a), b, typeof(b) from t4;
            create table t5(a,b, primary key(a,b)) without rowid;
            insert into t5 values(1,null);
            insert into t5 values(1,2),(1,3),(0,1);
            insert into t5 values(1,2);
            update t5 set b=b+10 where a=1;
            select * from t5;
            """);
    }

    [Fact]
    public void UpdatingTheRowidToItsOwnValueIsNoConflict()
    {
        SameAsShell("""
            create table t(a integer primary key, b);
            insert into t values(1,'x'),(2,'y');
            update t set a=1, b='z' where a=1;
            update t set rowid=2 where rowid=2;
            update t set a=2 where a=1;
            create table u(a,b);
            insert into u values(1,2),(2,3);
            update u set rowid=rowid;
            update u set rowid=1 where a=2;
            select * from t order by a;
            select rowid,* from u order by rowid;
            """);
    }

    [Fact]
    public void UpsertReturningReplaceAndUpdateConflicts()
    {
        SameAsShell("""
            create table u(id integer primary key, name text unique, n int default 0, note text);
            insert into u(id,name) values(1,'a'),(2,'b'),(3,'c');
            insert into u(id,name) values(2,'zz') on conflict(id) do update set name=excluded.name||'!', n=n+1 returning id,name,n;
            insert into u(name) values('a') on conflict(name) do nothing;
            insert into u(name,n) values('b',5) on conflict(name) do update set n=u.n+excluded.n where u.n<100 returning *;
            select changes(), total_changes();
            insert or replace into u(id,name,n) values(3,'a',9) returning id,name,n;
            select * from u order by id;
            update u set name='q' where id=2 returning id,name;
            update or ignore u set name='q' where id=1;
            update or replace u set name='q' where id=3;
            select * from u order by id;
            update u set id=id+10 returning id;
            delete from u where id>10 returning id,name;
            create table w(k text primary key, v int, z) without rowid;
            create index wz on w(z);
            create unique index wv on w(v);
            insert into w values('a',1,'x'),('b',2,'y'),('c',3,'x');
            insert into w values('d',2,'y');
            insert or replace into w values('d',2,'q');
            update w set k='zz' where k='a';
            update w set v=3 where k='b';
            update or replace w set v=3 where k='b';
            select * from w order by k;
            delete from w where v=3;
            select * from w;
            """);
    }

    [Fact]
    public void SavepointsStatementRollbackAndConflictActions()
    {
        SameAsShell("""
            create table u(id integer primary key, name text unique, n int default 0);
            insert into u(name) values('a');
            begin;
            insert into u(name) values('s1');
            savepoint sp1;
            insert into u(name) values('s2');
            insert into u(name) values('s1');
            select count(*) from u;
            rollback to sp1;
            select count(*) from u;
            insert into u(name) values('s3');
            release sp1;
            commit;
            select name from u order by id;
            savepoint a;
            insert into u(name) values('s4');
            savepoint b;
            insert into u(name) values('s5');
            release a;
            select count(*) from u;
            insert into u(name,n) values('f1',1),('f2',2),('s4',3),('f3',4);
            insert or fail into u(name,n) values('f1',1),('f4',2),('s4',3),('f5',4);
            select name from u order by id;
            begin;
            insert or rollback into u(name) values('ss1');
            insert or rollback into u(name) values('s4');
            select count(*) from u;
            begin;
            commit; commit;
            rollback;
            release x;
            """);
    }

    private static string BigScript(int rows)
    {
        var rnd = new Random(5);
        var sb = new StringBuilder();
        sb.Append("create table big(id integer primary key, a text, b blob, c real, d int);create index big_a on big(a);\n");
        sb.Append("create unique index big_d on big(d) where d%3=0;\nbegin;\n");
        for (int i = 1; i <= rows; i++)
        {
            int n = i % 7 == 0 ? new[] { 5, 50, 500, 3000, 9000 }[rnd.Next(5)] : 20;
            var a = new StringBuilder();
            for (int k = 0; k < n; k++)
            {
                a.Append("abcxyz"[rnd.Next(6)]);
            }

            var bl = new StringBuilder();
            int bn = new[] { 0, 8, 2000 }[rnd.Next(3)];
            for (int k = 0; k < bn; k++)
            {
                bl.Append("0123456789abcdef"[rnd.Next(16)]);
            }

            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"insert into big values({i},'{a}',x'{bl}',{rnd.NextDouble() * 1000:F6},{(i % 3 == 0 ? i : rnd.Next(11))});\n");
        }

        sb.Append("commit;update big set c=c*2 where id%5=0;update big set a=a||'zz' where id%11=0;delete from big where id%4=1;\n");
        sb.Append("select count(*) from big;delete from big where id>1000;select count(*) from big;\n");
        sb.Append("insert into big(a,d) select a, id*1000 from big where id<50;select count(*) from big;delete from big;select count(*) from big;\n");
        // One statement per line: the shell stops a line at its first error, so errors would hide later statements.
        return sb.ToString().Replace(";", ";\n", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4096, TextEncoding.Utf8, false, JournalMode.Delete)]
    [InlineData(512, TextEncoding.Utf8, false, JournalMode.Delete)]
    [InlineData(65536, TextEncoding.Utf8, false, JournalMode.Delete)]
    [InlineData(1024, TextEncoding.Utf16Be, false, JournalMode.Delete)]
    [InlineData(2048, TextEncoding.Utf16Le, true, JournalMode.Delete)]
    [InlineData(4096, TextEncoding.Utf8, false, JournalMode.Wal)]
    public void ManyRowsOverflowAndDeletesAcrossGeometries(int pageSize, TextEncoding encoding, bool autoVacuum, JournalMode journal)
    {
        string pre = "PRAGMA page_size=" + pageSize + ";PRAGMA encoding='" + (encoding == TextEncoding.Utf8 ? "UTF-8" : encoding == TextEncoding.Utf16Le ? "UTF-16le" : "UTF-16be") + "';"
            + (autoVacuum ? "PRAGMA auto_vacuum=1;" : string.Empty);
        SameAsShell(BigScript(900), new DatabaseOptions { PageSize = pageSize, Encoding = encoding, AutoVacuum = autoVacuum, JournalMode = journal }, pre);
    }

    [Fact]
    public void ChangesLastInsertRowidAndAutocommitFlag()
    {
        string path = Path.Combine(_dir, "c.db");
        using Database db = Database.Open(path);
        using Connection conn = db.Connect();
        Assert.True(conn.AutoCommit);
        RunScript(conn, "create table t(a integer primary key, b); insert into t(b) values(1),(2),(3); update t set b=b+1 where a<3;");
        Assert.Equal(2, conn.ChangesCount);
        Assert.Equal(5, conn.TotalChangesCount);
        Assert.Equal(3, conn.LastInsertedRowid);
        RunScript(conn, "begin;");
        Assert.False(conn.AutoCommit);
        RunScript(conn, "insert into t(b) values(9); rollback;");
        Assert.True(conn.AutoCommit);
        Assert.Equal("3\n", RunScript(conn, "select count(*) from t;"));
    }

    [Fact]
    public void CachedInsertAndUpdateAllocateNothingPerRow()
    {
        string path = Path.Combine(_dir, "alloc.db");
        using Database db = Database.Open(path, new DatabaseOptions { Synchronous = SynchronousMode.Off });
        using Connection conn = db.Connect();
        RunScript(conn, "create table t(id integer primary key, a int, b text); begin;");
        using Statement ins = conn.Prepare("insert into t(a, b) values(?, 'row')");
        using Statement upd = conn.Prepare("update t set a = ? where id = ?");
        for (int i = 0; i < 2000; i++)
        {
            ins.Bind(1, i);
            Assert.False(ins.Step());
            ins.Reset();
        }

        for (int i = 0; i < 200; i++)
        {
            upd.Bind(1, i);
            upd.Bind(2, i + 1);
            Assert.False(upd.Step());
            upd.Reset();
        }

        // Rows that need no new page allocate nothing: statement, cursors, record buffer and savepoint objects are all reused (docs/engine.md, the hot path).
        // A row whose page splits builds new pages and is allowed to allocate, so what is counted is the share of rows that allocate nothing.
        // The runtime and other threads' work can add stray allocations to one repetition, so the best of three repetitions is taken.
        int insZero = 0;
        int updZero = 0;
        for (int rep = 0; rep < 3; rep++)
        {
            int insRep = 0;
            int updRep = 0;
            for (int i = 0; i < 1000; i++)
            {
                ins.Bind(1, i);
                long before = GC.GetAllocatedBytesForCurrentThread();
                ins.Step();
                ins.Reset();
                insRep += GC.GetAllocatedBytesForCurrentThread() == before ? 1 : 0;
            }

            for (int i = 0; i < 1000; i++)
            {
                upd.Bind(1, i);
                upd.Bind(2, i + 1);
                long before = GC.GetAllocatedBytesForCurrentThread();
                upd.Step();
                upd.Reset();
                updRep += GC.GetAllocatedBytesForCurrentThread() == before ? 1 : 0;
            }

            insZero = Math.Max(insZero, insRep);
            updZero = Math.Max(updZero, updRep);
        }

        RunScript(conn, "commit;");
        Assert.True(insZero >= 950, "rows that inserted without allocating: " + insZero);
        Assert.True(updZero >= 995, "rows that updated without allocating: " + updZero);
    }

    [Fact]
    public void ForeignKeysImmediateActionsAndSelfReferences()
    {
        SameAsShell("""
            PRAGMA foreign_keys=ON;
            CREATE TABLE p(a INTEGER PRIMARY KEY, b UNIQUE, c);
            CREATE TABLE c1(x INT REFERENCES p(a), y TEXT REFERENCES p(b));
            INSERT INTO p VALUES(1,'one',0),(2,'two',0);
            INSERT INTO c1 VALUES(1,'one');
            INSERT INTO c1 VALUES(NULL,NULL);
            INSERT INTO c1 VALUES(3,'one');
            INSERT INTO c1 VALUES('2','two');
            SELECT * FROM c1;
            DELETE FROM p WHERE a=1;
            UPDATE p SET a=10 WHERE a=2;
            UPDATE p SET c=5;
            UPDATE c1 SET x=7;
            UPDATE c1 SET x=2 WHERE y='two';
            SELECT * FROM p; SELECT * FROM c1;
            DELETE FROM c1;
            DELETE FROM p;
            SELECT count(*) FROM p;
            CREATE TABLE k(id INTEGER PRIMARY KEY, par REFERENCES k(id) ON DELETE CASCADE ON UPDATE CASCADE);
            INSERT INTO k VALUES(1,NULL),(2,1),(3,2),(4,3),(5,1);
            SELECT * FROM k;
            UPDATE k SET id=20 WHERE id=2;
            SELECT * FROM k ORDER BY id;
            DELETE FROM k WHERE id=1;
            SELECT * FROM k;
            INSERT INTO k VALUES(6,99);
            INSERT INTO k VALUES(7,7);
            SELECT * FROM k;
            """);
    }

    [Fact]
    public void ForeignKeyActionsOnCompositeKeys()
    {
        SameAsShell("""
            PRAGMA foreign_keys=ON;
            CREATE TABLE p(a INT, b INT, v TEXT, PRIMARY KEY(a,b));
            CREATE TABLE c(id INTEGER PRIMARY KEY, x INT, y INT, FOREIGN KEY(x,y) REFERENCES p(a,b) ON DELETE SET NULL ON UPDATE CASCADE);
            CREATE TABLE d(id INTEGER PRIMARY KEY, x INT DEFAULT 9, y INT DEFAULT 9, FOREIGN KEY(x,y) REFERENCES p ON DELETE SET DEFAULT);
            CREATE TABLE r(id INTEGER PRIMARY KEY, x INT, y INT, FOREIGN KEY(x,y) REFERENCES p ON DELETE RESTRICT ON UPDATE RESTRICT);
            INSERT INTO p VALUES(1,1,'a'),(1,2,'b'),(9,9,'dflt');
            INSERT INTO c VALUES(1,1,1),(2,1,2),(3,NULL,1),(4,1,NULL);
            INSERT INTO c VALUES(5,2,2);
            INSERT INTO d VALUES(1,1,1),(2,1,2);
            INSERT INTO r VALUES(1,1,2);
            UPDATE p SET b=5 WHERE a=1 AND b=1;
            SELECT * FROM c; SELECT * FROM p;
            DELETE FROM p WHERE a=1 AND b=1;
            SELECT * FROM c; SELECT * FROM d;
            DELETE FROM p WHERE a=1 AND b=2;
            UPDATE p SET b=7 WHERE a=1 AND b=2;
            UPDATE p SET v='zz' WHERE a=1;
            SELECT * FROM r;
            DELETE FROM r;
            DELETE FROM p WHERE a=1;
            SELECT * FROM p;
            SELECT * FROM c; SELECT * FROM d;
            INSERT OR REPLACE INTO p VALUES(9,9,'again');
            SELECT * FROM d;
            INSERT OR IGNORE INTO c VALUES(20,77,77);
            REPLACE INTO c VALUES(1,5,5);
            INSERT INTO p VALUES(9,9,'x') ON CONFLICT DO UPDATE SET v='up';
            INSERT INTO p VALUES(8,8,'x') ON CONFLICT DO UPDATE SET a=100;
            SELECT * FROM p;
            INSERT INTO c VALUES(30,8,8) ON CONFLICT DO UPDATE SET x=1;
            INSERT INTO c VALUES(30,8,8) ON CONFLICT DO UPDATE SET x=1;
            SELECT * FROM c;
            """);
    }

    [Fact]
    public void DeferredForeignKeysSavepointsAndMismatches()
    {
        SameAsShell("""
            PRAGMA foreign_keys=ON;
            CREATE TABLE p(a TEXT PRIMARY KEY, n INT) WITHOUT ROWID;
            CREATE TABLE c(id INTEGER PRIMARY KEY, a TEXT REFERENCES p DEFERRABLE INITIALLY DEFERRED, b TEXT, FOREIGN KEY(b) REFERENCES p(a) ON DELETE CASCADE);
            CREATE TABLE w(a TEXT, b TEXT, PRIMARY KEY(a,b), FOREIGN KEY(b) REFERENCES p(a) ON UPDATE CASCADE) WITHOUT ROWID;
            BEGIN;
            INSERT INTO c VALUES(1,'x',NULL);
            INSERT INTO p VALUES('x',1);
            COMMIT;
            BEGIN;
            INSERT INTO c VALUES(2,'y',NULL);
            COMMIT;
            SAVEPOINT s1;
            INSERT INTO p VALUES('y',2);
            SAVEPOINT s2;
            DELETE FROM p WHERE a='x';
            ROLLBACK TO s2;
            RELEASE s1;
            SELECT * FROM p; SELECT * FROM c;
            BEGIN;
            DELETE FROM p WHERE a='y';
            INSERT INTO p VALUES('y',3);
            COMMIT;
            BEGIN;
            DELETE FROM p WHERE a='y';
            SELECT * FROM c;
            ROLLBACK;
            SELECT * FROM c;
            INSERT INTO c VALUES(3,NULL,'zz');
            INSERT INTO w VALUES('q','x'),('r','x');
            UPDATE p SET a='x2' WHERE a='x';
            SELECT * FROM w; SELECT * FROM c;
            DELETE FROM p;
            SELECT * FROM w; SELECT * FROM c; SELECT * FROM p;
            CREATE TABLE t(id INTEGER PRIMARY KEY AUTOINCREMENT, par INT REFERENCES t(id));
            INSERT INTO t(par) VALUES(NULL),(1),(2);
            INSERT INTO t(par) VALUES(5),(6);
            INSERT INTO t(id,par) VALUES(10,11),(11,10);
            INSERT INTO t(id,par) VALUES(20,20);
            DELETE FROM t WHERE id=1;
            DELETE FROM t WHERE id IN (3,2,1);
            UPDATE t SET par=999;
            UPDATE t SET id=id+100;
            SELECT * FROM t;
            CREATE TABLE bad(x REFERENCES t(par));
            INSERT INTO bad VALUES(1);
            DELETE FROM bad;
            CREATE TABLE miss(x REFERENCES nonesuch);
            INSERT INTO miss VALUES(NULL);
            DELETE FROM miss;
            PRAGMA foreign_keys=OFF;
            INSERT INTO miss VALUES(3);
            PRAGMA foreign_keys;
            """);
    }

    /// <summary>WITH in front of INSERT, UPDATE and DELETE, and subqueries, joins and compounds in the statements' SELECT and WHERE.</summary>
    [Fact]
    public void WithClausesSubqueriesAndJoinsInWrites()
    {
        SameAsShell("""
            create table t(id integer primary key, a int, b text);
            create table u(x int, y text);
            insert into t(a,b) values(1,'one'),(2,'two'),(3,'three'),(4,'four');
            insert into u values(2,'zwei'),(3,'drei'),(5,'fuenf');
            with c(n) as (select 10 union all select n+10 from c where n<50) insert into t(a,b) select n, 'c'||n from c;
            with c as (select x from u where x>2) update t set b = b || '!' where a in (select x from c);
            update t set a = a + (select count(*) from u where u.x < t.a) where a < 5;
            update t set b = (select y from u where x = t.a) where a in (select x from u);
            with c as (select 20 as n union select 30) delete from t where a in (select n from c);
            delete from t where a > (select max(x) from u) and b like 'c%';
            insert into t(a,b) select u.x, u.y from u join t on t.a = u.x;
            insert into t(a,b) select a, b from t where a < 3 union select x, y from u;
            insert into u select a, b from t where id in (select id from t order by id desc limit 2);
            select a, b from t order by id;
            select * from u order by x, y;
            """);
    }

    /// <summary>INSERT ... SELECT from its own table takes the rows in the order the SELECT's plan visits them, a covering index's order included.</summary>
    [Fact]
    public void InsertSelectFromItsOwnTableKeepsTheSelectsScanOrder()
    {
        SameAsShell("""
            create table t(a,b);
            create index tab on t(a,b);
            insert into t values(3,'c'),(1,'z'),(2,'y'),(1,'a'),(null,'n');
            insert into t select a,b from t;
            insert into t select b,a from t where a >= 1;
            select rowid,a,b from t order by rowid;
            create table w(k text primary key, v) without rowid;
            create index wv on w(v);
            insert into w values('x',3),('a',1),('m',2);
            insert into w select k||'2', v from w;
            insert into t select v,k from w;
            select rowid,a,b from t order by rowid desc limit 6;
            """);
    }

    /// <summary>
    /// A writer waiting for the RESERVED lock does not hold a SHARED lock while it waits: the holder of RESERVED needs every reader gone to commit,
    /// so a waiting writer that kept SHARED would block it until the timeout (c3ref busy_handler).
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task WriterWaitingForReservedDoesNotBlockTheHoldersCommit()
    {
        string path = Path.Combine(_dir, "wait.db");
        var opts = new DatabaseOptions { BusyTimeoutMs = 4000 };
        using Database db = Database.Open(path, opts);
        using Connection a = db.Connect();
        using Connection b = db.Connect();
        RunScript(a, "create table t(x); begin immediate; insert into t values(1);");
        var waiter = System.Threading.Tasks.Task.Run(() => RunScript(b, "begin immediate; insert into t values(2); commit;"), TestContext.Current.CancellationToken);
        await System.Threading.Tasks.Task.Delay(300, TestContext.Current.CancellationToken);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(string.Empty, RunScript(a, "commit;"));
        Assert.True(clock.ElapsedMilliseconds < 2000, "the commit waited " + clock.ElapsedMilliseconds + " ms for the waiting writer's read lock");
        Assert.Equal(string.Empty, await waiter);
        Assert.Equal("2\n", RunScript(a, "select count(*) from t;"));
    }

    [Fact]
    public void ForeignKeyListAndCheckPragmas()
    {
        SameAsShell("""
            create table p(a integer primary key, b text unique, c);
            create table c1(x int references p(a) on delete cascade on update set null, y text references p(b) match simple deferrable initially deferred);
            create table c3(a text, b references p);
            create table nopar(x references nothere);
            create table wr(k text primary key, x references p) without rowid;
            create table tt(a,b, primary key(a,b));
            create table cc(i,j, foreign key(i,j) references tt);
            pragma foreign_key_list(c1);
            pragma foreign_key_list(cc);
            pragma foreign_key_list(c3);
            pragma foreign_key_list(p);
            pragma foreign_key_list(nosuch);
            pragma foreign_key_list=c1;
            insert into p values(1,'one',null),(2,'two','t');
            insert into c1 values(1,'one'),(5,'zz'),(null,'q'),(2,null);
            insert into c3 values('a',1),('b',7);
            insert into nopar values(1),(null);
            insert into wr values('k',4),('j',1);
            insert into tt values(1,1);
            insert into cc values(1,1),(1,2),(null,2),(3,null);
            pragma foreign_key_check(c1);
            pragma foreign_key_check(c3);
            pragma foreign_key_check(nopar);
            pragma foreign_key_check(wr);
            pragma foreign_key_check(cc);
            pragma foreign_key_check(p);
            pragma foreign_key_check(nosuch);
            pragma foreign_key_check=c1;
            """);
    }

    [Fact]
    public void ForeignKeyMismatchInCheckAndDropTableDeletesImplicitly()
    {
        SameAsShell("""
            create table p(a integer primary key, b);
            create table mm(x references p(b));
            pragma foreign_key_check(mm);
            pragma foreign_key_check;
            PRAGMA foreign_keys=ON;
            create table q(a integer primary key, b unique);
            create table c(x references q on delete cascade);
            create table d(x references q on delete set null, y);
            create table e(x references q);
            create table f(x references q deferrable initially deferred);
            insert into q values(1,1),(2,2);
            insert into c values(1);
            insert into d values(1,'k'),(2,'k');
            insert into e values(2);
            drop table q;
            select name from sqlite_master order by 1;
            delete from e;
            drop table q;
            select * from c; select * from d;
            select name from sqlite_master order by 1;
            begin;
            create table q(a integer primary key);
            insert into q values(1);
            insert into f values(1);
            drop table q;
            select * from f;
            commit;
            select * from f;
            rollback;
            create table s(a integer primary key, b references s(a));
            insert into s values(1,1);
            drop table s;
            select name from sqlite_master order by 1;
            """);
    }

    /// <summary>The child's value gets the parent column's affinity before it is compared with the parent key (foreignkeys).</summary>
    [Fact]
    public void ForeignKeyChildValuesTakeTheParentColumnsAffinity()
    {
        SameAsShell("""
            PRAGMA foreign_keys=ON;
            create table q(a integer primary key, t text unique);
            create table r(x text references q(a), y integer references q(t));
            insert into q values(1,'1'),(2,'x'),(3,'12');
            insert into r values('1.0',null);
            insert into r values(' 1',null);
            insert into r values('abc',null);
            insert into r values(null,'x');
            insert into r values(null,12);
            insert into r values(null,1);
            select * from r;
            delete from q where a=1;
            delete from q where t='12';
            update q set a=7 where a=2;
            update q set t='y' where a=2;
            delete from r where x=' 1';
            delete from q where a=1;
            select * from r; select * from q;
            pragma foreign_key_check;
            """);
    }

    /// <summary>BEGIN EXCLUSIVE keeps readers out in rollback mode (lang_transaction); in WAL mode it is IMMEDIATE and readers go on.</summary>
    [Theory]
    [InlineData(JournalMode.Delete, true)]
    [InlineData(JournalMode.Wal, false)]
    public void BeginExclusiveAgainstALiveReader(JournalMode mode, bool readerBlocked)
    {
        NeedOracle();
        string path = Path.Combine(_dir, "ex-" + mode + ".db");
        using Database db = Database.Open(path, new DatabaseOptions { JournalMode = mode });
        using Connection conn = db.Connect();
        _ = RunScript(conn, (mode == JournalMode.Wal ? "pragma journal_mode=wal;" : "") + "create table t(a); insert into t values(1);");
        Assert.Equal(mode == JournalMode.Wal ? "wal" : "delete", SqliteCli.Run(path, "pragma journal_mode;").Stdout.Trim());
        string before = SqliteCli.Run(path, "select count(*) from t;").Stdout.Trim();
        Assert.Equal("1", before);
        RunScript(conn, "begin exclusive;");
        Proc.Result r = SqliteCli.Run(path, "select count(*) from t;");
        if (readerBlocked)
        {
            Assert.Contains("database is locked", r.Stdout + r.Stderr, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal("1", r.Stdout.Trim());
        }

        // a second writer is locked out either way
        Proc.Result w = SqliteCli.Run(path, "begin immediate;");
        Assert.Contains("database is locked", w.Stdout + w.Stderr, StringComparison.Ordinal);
        RunScript(conn, "insert into t values(2); commit;");
        Assert.Equal("2", SqliteCli.Run(path, "select count(*) from t;").Stdout.Trim());
    }

    /// <summary>
    /// Deleting from an index the reference built leaves free space it accepts: a cell freed next to a freeblock with a 1-3 byte fragment between
    /// them merges with it and takes the fragment out of the page header's count (fileformat2 §1.6).
    /// </summary>
    [Fact]
    public void DeletesFromReferenceWrittenIndexKeepFreeSpaceValid()
    {
        NeedOracle();
        string db = Path.Combine(_dir, "ref-index.db");
        var sql = new StringBuilder("PRAGMA page_size=1024;CREATE TABLE a(id INTEGER PRIMARY KEY, t TEXT);CREATE INDEX ai ON a(t);BEGIN;\n");
        var rnd = new Random(0);
        for (int i = 1; i < 400; i++)
        {
            sql.Append("INSERT INTO a VALUES(").Append(i).Append(",'").Append('x', rnd.Next(1, 41)).Append(i).Append("');\n");
        }

        sql.Append("COMMIT;\n");
        SqliteCli.Run(db, sql.ToString());
        string copy = Path.Combine(_dir, "ref-index-ours.db");
        File.Copy(db, copy);
        const string Deletes = "delete from a where id > 350; delete from a where id % 2 = 0; delete from a where id % 3 = 1; update a set t = t || 'z' where id % 5 = 0;";
        using (Database d = Database.Open(copy))
        using (Connection c = d.Connect())
        {
            Assert.Equal(string.Empty, RunScript(c, Deletes));
        }

        SqliteCli.Run(db, Deletes);
        Assert.Equal("ok", SqliteCli.Run(copy, "PRAGMA integrity_check;").Stdout.Trim());
        Assert.Equal(SqliteCli.Dump(db), SqliteCli.Dump(copy));
    }

    /// <summary>An INSERT ... SELECT from the table it writes reads the table as it was, whatever the name is quoted like.</summary>
    [Fact]
    public void InsertSelectFromItsOwnTableDoesNotSeeItsOwnRows()
    {
        var script = new StringBuilder("CREATE TABLE [PT]([A] INTEGER NOT NULL,[B] INTEGER NOT NULL, CONSTRAINT [PK] PRIMARY KEY ([A],[B]));BEGIN;\n");
        for (int i = 1; i <= 400; i++)
        {
            script.Append("INSERT INTO PT VALUES(").Append(i % 17).Append(',').Append(i).Append(");\n");
        }

        script.Append("COMMIT;delete from \"PT\" where rowid % 7 = 0;\n")
            .Append("insert or ignore into \"PT\" select * from \"PT\" where rowid % 11 = 0 order by rowid;\n")
            .Append("insert or replace into \"PT\" select * from \"PT\" where rowid % 13 = 1 order by rowid;\n")
            .Append("select rowid, A, B from PT order by rowid;");
        SameAsShell(script.ToString());
    }

    /// <summary>A value an UPDATE reads from the row is still the same when it goes into an index entry after the row itself has been rewritten.</summary>
    [Fact]
    public void UpdateKeepsUnchangedColumnsReadyForPartialIndexEntries()
    {
        SameAsShell("""
            CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 TEXT, c1 TEXT UNIQUE, c2 BLOB);
            CREATE INDEX i0 ON t0(c0) WHERE c0 IS NOT NULL;
            CREATE INDEX i2 ON t0(c2, c0);
            INSERT INTO t0 VALUES(2,'x','abd',x'0102'),(10,'y',NULL,x'0304');
            UPDATE t0 SET c1 = 7 WHERE c1 IS NULL;
            UPDATE t0 SET c1 = 'a long replacement value that moves the row' WHERE id = 2;
            SELECT id, c0, c1, hex(c2) FROM t0 ORDER BY c0;
            """);
    }
}
