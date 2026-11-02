using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RecursiveAI.OneLite.Oracle;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// ATTACH and DETACH: results, error text, the files' <c>.dump</c> and integrity checks are compared with the pinned <c>sqlite3</c> shell. Each side
/// runs the script in a directory of its own; <c>{D}</c> in a script is that directory.
/// </summary>
public sealed class AttachTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-attach-" + Guid.NewGuid().ToString("N"));

    public AttachTests() => Directory.CreateDirectory(_dir);

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
                // a statement that did not compile has no tail; skip it by the parser's own idea of where it ends (a trigger body holds semicolons)
                int end;
                try
                {
                    _ = RecursiveAI.OneLite.Sql.Parsing.SqlParser.ParseOne(rest, 0, out end);
                }
                catch (OneLiteException)
                {
                    end = rest.IndexOf(';', StringComparison.Ordinal) + 1;
                    if (end == 0)
                    {
                        end = rest.Length;
                    }
                }

                rest = rest[end..];
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
            if ((line.StartsWith("Error near line", StringComparison.Ordinal) || line.StartsWith("Parse error near line", StringComparison.Ordinal) || line.StartsWith("Runtime error near line", StringComparison.Ordinal)) && at > 0)
            {
                line = "Error: " + line[(at + 2)..];
            }

            // the shell echoes the statement and a caret under the error position; and it leaves out nothing else
            if (line.Length > 0 && !line.StartsWith("  ", StringComparison.Ordinal) && !line.Contains("error here ---^", StringComparison.Ordinal))
            {
                o.Append(line).Append('\n');
            }
        }

        return o.ToString();
    }

    private void SameAsShell(string script, params string[] files)
    {
        NeedOracle();
        string id = Guid.NewGuid().ToString("N");
        string da = Path.Combine(_dir, id + "-ol"), db = Path.Combine(_dir, id + "-sq");
        Directory.CreateDirectory(da);
        Directory.CreateDirectory(db);
        string ours;
        using (Database d = Database.Open(Path.Combine(da, "main.db")))
        using (Connection conn = d.Connect())
        {
            ours = RunScript(conn, script.Replace("{D}", da, StringComparison.Ordinal));
        }

        ours = string.Concat(ours.Split('\n').Where(l => l.Length > 0).Select(l => l + "\n"));
        string theirs = ShellOutput(Path.Combine(db, "main.db"), script.Replace("{D}", db, StringComparison.Ordinal))
            .Replace(db, da, StringComparison.Ordinal);
        Assert.Equal(theirs, ours);
        foreach (string f in Enumerable.Repeat("main.db", 1).Concat(files))
        {
            Assert.Equal("ok", SqliteCli.Run(Path.Combine(da, f), "PRAGMA integrity_check;").Stdout.Trim());
            Assert.Equal(SqliteCli.Dump(Path.Combine(db, f)), SqliteCli.Dump(Path.Combine(da, f)));
        }
    }

    [Fact]
    public void QualifiedNamesAcrossDatabases() => SameAsShell("""
        attach '{D}/a.db' as a;
        attach '{D}/b.db' as b;
        create table a.t(x, y);
        create table b.t(x, z);
        create table m(q);
        insert into a.t values (1,'one'),(2,'two');
        insert into b.t values (1,'uno'),(3,'tres');
        insert into m values (10);
        select * from t;
        select a.t.x, b.t.z from a.t join b.t on a.t.x = b.t.x;
        insert into main.m select x from a.t;
        select * from m;
        update b.t set z = 'X' where x = 3;
        delete from a.t where x = 1;
        select * from a.t union all select x,z from b.t;
        create index a.i on t(x);
        create view a.v as select * from t;
        select * from a.v;
        select name, type, tbl_name from a.sqlite_master order by 1;
        select * from nosuch.t;
        select * from a.nosuch;
        create table nosuch.t(a);
        drop table b.t;
        select * from b.sqlite_master;
        detach a;
        select * from a.t;
        attach '{D}/a.db' as a;
        select * from a.t;
        """, "a.db", "b.db");

    [Fact]
    public void AttachAndDetachErrors() => SameAsShell("""
        attach '{D}/a.db' as a;
        attach '{D}/b.db' as A;
        attach '{D}/b.db' as main;
        attach '{D}/b.db' as temp;
        detach main;
        detach temp;
        detach nosuch;
        create table a.t(x);
        begin;
        insert into a.t values(1);
        detach a;
        commit;
        detach a;
        detach a;
        """, "a.db");

    [Fact]
    public void TooManyAttachedDatabases() => SameAsShell("""
        attach '{D}/1.db' as d1; attach '{D}/2.db' as d2; attach '{D}/3.db' as d3; attach '{D}/4.db' as d4; attach '{D}/5.db' as d5;
        attach '{D}/6.db' as d6; attach '{D}/7.db' as d7; attach '{D}/8.db' as d8; attach '{D}/9.db' as d9; attach '{D}/10.db' as d10;
        attach '{D}/11.db' as d11;
        attach '{D}/1.db' as d1;
        detach d10;
        attach '{D}/11.db' as d11;
        select count(*) from pragma_database_list;
        """);

    [Fact]
    public void TransactionsAcrossDatabases() => SameAsShell("""
        attach '{D}/a.db' as a;
        attach '{D}/b.db' as b;
        create table a.t(x); create table b.t(y); create table m(z);
        begin;
        insert into a.t values(1); insert into b.t values(2); insert into m values(3);
        commit;
        select * from a.t, b.t, m;
        create table a.u(k unique);
        insert into a.u values(1);
        insert into a.u select 5 union all select 1;
        select * from a.u;
        begin;
        insert into a.u values(7);
        insert into m values(8);
        savepoint s1;
        insert into b.t values(9);
        savepoint s2;
        insert into a.u values(10);
        rollback to s2;
        select * from a.u; select * from b.t;
        rollback to s1;
        select * from b.t;
        release s1;
        rollback;
        select * from a.u; select * from m; select * from b.t;
        begin;
        insert into a.u values(20); insert into b.t values(21);
        insert into a.u values(1);
        commit;
        select * from a.u; select * from b.t;
        """, "a.db", "b.db");

    [Fact]
    public void TriggersKeysAndSequencesInAttachedDatabase() => SameAsShell("""
        attach '{D}/a.db' as a;
        create table a.t(id integer primary key autoincrement, x);
        create table a.p(id integer primary key); create table a.c(pid references p(id), v);
        insert into a.t(x) values ('a'),('b');
        select * from a.sqlite_sequence;
        create trigger a.tr after insert on t when new.x like 'z%' begin insert into log values(new.x); end;
        create table a.log(m);
        insert into a.t(x) values ('zed');
        select * from a.t; select * from a.log;
        pragma foreign_keys=on;
        insert into a.p values(1);
        insert into a.c values(1,'ok');
        insert into a.c values(2,'bad');
        delete from a.p where id=1;
        alter table a.t rename to t2;
        select * from a.t2;
        drop table a.c;
        vacuum a;
        reindex a.t2;
        """, "a.db");

    [Fact]
    public void PragmasOfAttachedAndTempDatabases() => SameAsShell("""
        create temp table x(a);
        attach '{D}/q.db' as q; attach '' as e; attach ':memory:' as mm;
        pragma temp.journal_mode=wal; pragma temp.journal_mode=memory; pragma temp.journal_mode;
        pragma q.journal_mode; pragma q.journal_mode=wal; pragma q.journal_mode; pragma q.synchronous; pragma q.synchronous=1; pragma q.synchronous; pragma synchronous;
        pragma e.journal_mode; pragma e.journal_mode=wal; pragma mm.journal_mode=truncate;
        create table q.t(a); pragma q.page_size;
        pragma q.user_version=7; pragma q.user_version; pragma q.application_id=9; pragma q.application_id;
        pragma q.page_count; pragma q.freelist_count; pragma q.schema_version; pragma q.auto_vacuum; pragma q.encoding;
        pragma q.wal_checkpoint; pragma temp.page_count; pragma temp.schema_version; pragma q.wal_autocheckpoint;
        pragma q.cache_size; pragma q.cache_size=5; pragma q.cache_size; pragma cache_size;
        pragma q.max_page_count; pragma q.locking_mode; pragma temp.locking_mode;
        pragma temp.user_version=3; pragma temp.user_version; pragma user_version; pragma mm.user_version=4; pragma mm.user_version;
        pragma q.integrity_check;
        """, "q.db");

    [Fact]
    public void PragmaTableValuedFunctions() => SameAsShell("""
        create table t(a integer primary key, b text not null default 'x', c);
        create index i1 on t(b, c);
        create table u(k, f references t(a) on delete cascade);
        create temp table tt(z);
        select * from pragma_table_info('t');
        select * from pragma_table_xinfo('t');
        select name, "unique", origin from pragma_index_list('t');
        select * from pragma_index_info('i1');
        select * from pragma_index_xinfo('i1');
        select * from pragma_foreign_key_list('u');
        select m.name, p.name, p.type from sqlite_master m join pragma_table_info(m.name) p where m.type='table' order by 1, p.cid;
        select * from pragma_table_info('nosuch');
        select * from pragma_table_info;
        select name, arg, schema from pragma_table_info('t','main') limit 2;
        select * from pragma_table_info('tt','temp');
        select * from pragma_table_info('t','nosuch');
        select cid from pragma_table_info('t') where pk=1;
        select * from pragma_table_info where arg='t';
        select * from pragma_index_list('t', 'main');
        """);

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    [InlineData(65536)]
    public void JournalModeTransitionsLeaveAFileTheShellReads(int pageSize) => SameAsShell($"""
        pragma page_size={pageSize};
        pragma journal_mode;
        create table t(a integer primary key, b text);
        insert into t(b) select hex(zeroblob(40)) || value from generate_series(1, 50);
        pragma journal_mode=wal;
        insert into t(b) select hex(zeroblob(300)) || value from generate_series(1, 30);
        pragma journal_mode=truncate;
        insert into t(b) select 'trunc' from generate_series(1, 20);
        pragma journal_mode=wal;
        pragma journal_mode=persist;
        insert into t(b) select 'persist' from generate_series(1, 20);
        pragma journal_mode=memory;
        insert into t(b) select 'memory' from generate_series(1, 20);
        begin;
        pragma journal_mode=wal;
        rollback;
        pragma journal_mode=delete;
        select count(*), sum(length(b)) from t;
        pragma journal_mode;
        pragma page_size;
        pragma integrity_check;
        """);

    [Fact]
    public void AnAttachedDatabaseIsNotLockedBetweenStatements()
    {
        NeedOracle();
        string main = Path.Combine(_dir, "main.db"), other = Path.Combine(_dir, "other.db");
        SqliteCli.ApplyScript(other, "create table t(a); insert into t values(1);");
        using Database db = Database.Open(main);
        using Connection conn = db.Connect();
        Assert.Equal("1\n", RunScript(conn, $"attach '{other}' as o; select * from o.t;"));
        // another process writes the attached file while this connection is idle: no read lock is left behind
        SqliteCli.ApplyScript(other, "insert into t values(2);");
        Assert.Equal("1\n2\n", RunScript(conn, "select * from o.t order by a;"));
        Assert.Equal("ok", SqliteCli.Run(other, "PRAGMA integrity_check;").Stdout.Trim());
    }

    [Fact]
    public async Task TheAsyncDriverRunsStatementsOnAttachedDatabases()
    {
        string main = Path.Combine(_dir, "am.db"), other = Path.Combine(_dir, "ao.db");
        using Database db = Database.Open(main, new DatabaseOptions { YieldEvery = 1 });
        using Connection conn = await db.ConnectAsync(TestContext.Current.CancellationToken);
        foreach (string sql in new[]
        {
            $"attach '{other}' as o", "create table o.t(a); create table m(b)", "begin", "insert into o.t values (1),(2),(3)", "insert into m select a * 10 from o.t", "commit",
        })
        {
            string rest = sql;
            while (!string.IsNullOrWhiteSpace(rest))
            {
                using Statement st = conn.Prepare(rest);
                rest = st.Tail;
                while (await st.StepAsync(TestContext.Current.CancellationToken))
                {
                }
            }
        }

        using Statement q = conn.Prepare("select (select sum(a) from o.t), (select sum(b) from m)");
        Assert.True(await q.StepAsync(TestContext.Current.CancellationToken));
        Assert.Equal(6, q.GetInt64(0));
        Assert.Equal(60, q.GetInt64(1));
    }
}
