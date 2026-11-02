using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Oracle;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.Pager;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// Views, triggers, ALTER TABLE, REINDEX and UPDATE ... FROM: results, error text, the file's <c>.dump</c> and its integrity check are compared with the
/// pinned <c>sqlite3</c> shell.
/// </summary>
public sealed class SchemaChangeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-schema-" + Guid.NewGuid().ToString("N"));

    public SchemaChangeTests() => Directory.CreateDirectory(_dir);

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

        // blank lines are NULL-only rows on our side and are dropped from the shell's output above
        ours = string.Concat(ours.Split('\n').Where(l => l.Length > 0).Select(l => l + "\n"));

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
    public void ViewsAndTheirErrors() => SameAsShell("""
        create table t(a integer primary key, b text);
        insert into t values(1,'x'),(2,'y');
        create index ix on t(b);
        create view v as select a, b||'!' c from t;
        select * from v;
        create view v as select 1;
        create view t as select 1;
        create view ix as select 1;
        create table v(x);
        create index v2 on v(a);
        drop table v;
        drop view t;
        drop view nope;
        insert into v values(1,2);
        update v set a=1;
        delete from v;
        create view w(x) as select 1,2;
        select * from w;
        create view bad as select * from nosuch;
        select * from bad;
        create view v4 as select a, a, b b from t;
        select * from v4;
        select * from v, v4 where v.a = v4.a;
        drop view v;
        drop view if exists v;
        select name, type, sql from sqlite_master order by name;
        """);

    [Fact]
    public void TriggersFireInOrderAndRaise() => SameAsShell("""
        create table t(a integer primary key, b text, c int default 5);
        create table log(id integer primary key, msg);
        create trigger t_bi before insert on t begin insert into log(msg) values('bi '||new.a||' '||ifnull(new.b,'null')||' '||new.c); end;
        create trigger t_ai after insert on t when new.c > 5 begin insert into log(msg) values('ai '||new.a||' '||typeof(new.c)); end;
        create trigger t_bu before update on t begin insert into log(msg) values('bu '||old.b||'->'||new.b||' '||old.a||new.a); end;
        create trigger t_au after update of c on t begin insert into log(msg) values('au '||old.c||'->'||new.c); end;
        create trigger t_bd before delete on t begin insert into log(msg) values('bd '||old.a||old.b); end;
        create trigger t_ad after delete on t begin insert into log(msg) values('ad '||old.rowid); end;
        insert into t values(1,'x',3),(2,'y',9),(3,null,5);
        select changes(), last_insert_rowid();
        update t set b=b||'!' where a<3;
        update t set c=c+1;
        update t set a=a+10 where a=3;
        delete from t where a=1;
        select changes();
        select * from log;
        select * from t;
        delete from t;
        create trigger r1 before insert on t begin select raise(ignore); end;
        insert into t values(100,'i',1);
        select count(*) from t;
        drop trigger r1;
        create trigger r2 before insert on t when new.b='bad' begin select raise(abort,'bad value'); end;
        insert into t values(101,'bad',1);
        insert into t values(102,'ok',1),(103,'bad',2);
        select * from t;
        create trigger r3 after insert on t when new.b='fail' begin select raise(fail,'failing'); end;
        insert into t values(110,'ok',1),(111,'fail',2),(112,'x',3);
        select a from t;
        create trigger t_ai after insert on t begin select 1; end;
        create trigger bad before insert on nosuch begin select 1; end;
        create trigger v1 instead of insert on t begin select 1; end;
        drop trigger nosuch;
        drop trigger if exists nosuch;
        drop table t;
        select name from sqlite_master where type='trigger';
        """);

    [Fact]
    public void DropViewDropsItsTriggersAndNewValuesHaveAffinity() => SameAsShell("""
        create table lg(id integer primary key, v);
        create table t(a numeric, b real, c int, d text);
        create view vw as select a, b from t;
        create trigger vi instead of insert on vw begin insert into lg(v) values(new.a); end;
        create trigger tb before insert on t begin insert into lg(v) values(new.a); insert into lg(v) values(new.b); insert into lg(v) values(new.c); insert into lg(v) values(new.d); end;
        create trigger tu before update on t begin insert into lg(v) values(new.a); insert into lg(v) values(new.b); end;
        insert into t values(0.0, 3, '5', 7);
        update t set a = 1.0, b = 2;
        select v, typeof(v) from lg order by id;
        drop view vw;
        select type, name from sqlite_master order by name;
        create index ip on t(a) where a > 1;
        analyze t;
        select * from sqlite_stat1 order by idx;
        """);

    [Fact]
    public void AlterRevalidatesViewsAndTriggers() => SameAsShell("""
        create table t(a, b);
        create table u(x);
        create view v1 as select x.b from t x;
        alter table t drop column b;
        drop view v1;
        create view v2 as select * from nosuch;
        alter table t rename column a to c;
        alter table t drop column b;
        alter table t rename to t2;
        alter table t add column d;
        drop view v2;
        create trigger tr after insert on t begin select new.b; end;
        alter table t drop column b;
        drop trigger tr;
        create trigger tr2 after insert on t begin select 1 from nosuch; end;
        alter table t rename to t3;
        """);

    [Fact]
    public void DropColumnRewritesLargeRowsIntact() => SameAsShell("""
        create table t(a integer primary key, b text, c real, d blob);
        with recursive n(i) as (select 1 union all select i+1 from n where i < 900)
          insert into t select i, 'row'||i||substr('abcdefghijklmnopqrstuvwxyz', 1, i%26), i*1.5, zeroblob(i%7*400) from n;
        create index tb on t(b);
        alter table t drop column d;
        select count(*), sum(length(b)), sum(c) from t;
        select count(*) from t where b is null or b = '';
        select a from t order by b limit 3 offset 700;
        """);

    [Fact]
    public void TempTriggerOrderViewAffinityAddColumnDefaultAndStatRules() => SameAsShell("""
        create table t(a real, b numeric, c text);
        create table l(m);
        create trigger r1 before insert on t begin insert into l values('r1'); end;
        create temp trigger r2 before insert on t begin insert into l values('r2'); end;
        create trigger r3 before insert on t begin insert into l values('r3'); end;
        create temp trigger r4 before insert on t begin insert into l values('r4'); end;
        insert into t values(1, 2, 3);
        select * from l;
        create view v as select a, b, c from t;
        create trigger vi instead of insert on v begin insert into l values(typeof(new.a)||typeof(new.b)||typeof(new.c)); end;
        create trigger vu instead of update on v begin insert into l values(typeof(new.a)||typeof(new.b)||typeof(new.c)); end;
        insert into v values(1, '2', 3);
        update v set a = 7, b = '8.0', c = 9;
        select * from l;
        create table e(x);
        alter table e add column y default (1 + 2);
        alter table t add column z default (1 + 2);
        alter table t add column z2 default -5;
        create table s(c);
        insert into s values (1), (2), (null), (null), (null), (7);
        create unique index su on s(c);
        create unique index sd on s(c desc);
        create index sn on s(c);
        analyze s;
        select idx, stat from sqlite_stat1 where tbl = 's' order by idx;
        create table n(c);
        insert into n values (null), (null);
        create unique index nd on n(c desc);
        create table m(c);
        insert into m values (null), (null), ('a'), ('b');
        create unique index md on m(c desc);
        analyze n;
        analyze m;
        select tbl, idx, stat from sqlite_stat1 where tbl in ('n', 'm') order by tbl;
        """);

    [Fact]
    public void InsteadOfTriggersOnViews() => SameAsShell("""
        create table t(a integer primary key, b text);
        create table log(m);
        create view v as select a, b, a*2 d from t;
        create trigger vi instead of insert on v begin insert into t values(new.a, new.b); insert into log values('vi '||new.a); end;
        create trigger vu instead of update on v begin update t set b=new.b where a=old.a; insert into log values('vu '||old.b||'>'||new.b); end;
        create trigger vd instead of delete on v begin delete from t where a=old.a; insert into log values('vd'||old.d); end;
        insert into v values(1,'x',0),(2,'y',0);
        insert into v(a) values(3);
        insert into v select 10, 'z', 1;
        select * from v;
        update v set b=b||'!' where a>1;
        delete from v where a=10;
        select * from v;
        select * from log;
        create view w as select 1 x;
        insert into w values(1);
        """);

    [Fact]
    public void AlterTableRewritesTheSchema() => SameAsShell("""
        create table t1(a integer primary key autoincrement, b unique, c references t1(a), "d e" text check("d e">0 and t1.a>0), foreign key(b) references t1(a));
        create table p(x, y references t1 (b));
        create index i1 on t1(b, "d e") where a>0;
        create view v as select t1.a, t1.b from t1 as q join t1 on 1 where t1.c is null;
        create trigger tg after insert on t1 when new.a>1 begin update t1 set b=1 where t1.a=new.a; insert into p select a, b from t1; end;
        insert into t1(b) values(1);
        alter table t1 rename to "n n";
        select name, tbl_name, sql from sqlite_master;
        select * from sqlite_sequence;
        alter table "n n" rename column b to bb;
        select name, sql from sqlite_master;
        create table w(a,b,c default 7);
        insert into w values(1,2,3),(4,5,6);
        create index wi on w(a);
        alter table w add column f text default 'new';
        alter table w drop column b;
        select * from w;
        alter table w drop column a;
        alter table w add column zz primary key;
        alter table nosuch rename to z;
        alter table w rename column zz to c;
        reindex;
        reindex w;
        reindex nope;
        """);

    [Fact]
    public void UpdateFromAndGeneratedColumns() => SameAsShell("""
        create table t(a int, b int generated always as (a*2) virtual, c int as (a+b) stored, d text);
        insert into t(a,d) values(1,'x'),(2,'y');
        update t set a=10 where d='x';
        select * from t;
        alter table t add column e int as (a+100);
        select e from t;
        create table s(x int, y);
        insert into s values(1,'a'),(2,'b');
        create table u(k int, v);
        insert into u values(1,'p'),(2,'q'),(3,'r');
        update u set v = s.y from s where u.k = s.x;
        select * from u;
        update u set v = (select 1) from s where u.k = s.x returning k, v;
        """);

    [Fact]
    public void VacuumKeepsContentAndAppliesAutoVacuum() => SameAsShell("""
        create table t(a integer primary key, b text, c blob);
        create index tb on t(b);
        create table w(k text primary key, v) without rowid;
        create table seq(id integer primary key autoincrement, z);
        insert into seq(z) values (1),(2);
        insert into t select value, 'row '||value||hex(zeroblob(value % 300)), zeroblob((value*37) % 5000) from (with recursive s(value) as (select 1 union all select value+1 from s where value<400) select value from s);
        insert into w select 'k'||value, hex(zeroblob(value%200))||value from (with recursive s(value) as (select 1 union all select value+1 from s where value<200) select value from s);
        delete from t where a % 4 != 0;
        drop table w;
        create view v as select a from t;
        vacuum;
        select count(*), sum(length(b)), sum(length(c)) from t;
        select * from sqlite_sequence;
        pragma auto_vacuum;
        pragma auto_vacuum=1;
        pragma auto_vacuum;
        vacuum;
        pragma auto_vacuum;
        select count(*) from t;
        pragma auto_vacuum=2;
        pragma auto_vacuum;
        insert into t values(100000, 'x', 'y');
        vacuum;
        pragma auto_vacuum=0;
        pragma auto_vacuum;
        vacuum;
        pragma auto_vacuum;
        select count(*), max(a) from t;
        begin;
        vacuum;
        rollback;
        """);

    [Fact]
    public void AutoVacuumPragmaOnAnEmptyDatabase() => SameAsShell("""
        pragma auto_vacuum;
        pragma auto_vacuum=2;
        pragma auto_vacuum;
        pragma auto_vacuum=none;
        pragma auto_vacuum;
        pragma auto_vacuum=bogus;
        pragma auto_vacuum=full;
        pragma auto_vacuum;
        create table t(a);
        pragma auto_vacuum=1;
        pragma auto_vacuum=0;
        pragma auto_vacuum;
        vacuum;
        pragma auto_vacuum;
        vacuum main;
        vacuum temp;
        vacuum nosuch;
        """);

    [Fact]
    public void TempObjectsShadowAndRollBack() => SameAsShell("""
        create table t(a); insert into t values(1);
        create temp table t(b); insert into t values(7),(8);
        select * from t; select * from main.t; select * from temp.t;
        create index i on t(b);
        select name, tbl_name, rootpage from sqlite_temp_master order by 1;
        create temp view v as select b*2 x from t; select * from v;
        create temp trigger tg after insert on main.t begin insert into temp.t values(new.a+100); end;
        insert into main.t values(5); select * from t; select * from main.t;
        create temp table w(c, d); alter table temp.w rename column c to cc; alter table w add column e default 3; alter table w rename to w2; select name, sql from sqlite_temp_master order by 1;
        begin; create temp table z(q); insert into z values(1); savepoint s; insert into z values(2); rollback to s; select * from z; rollback;
        select name from sqlite_temp_master order by 1;
        create temp table y(a unique); begin; insert into y values(1); insert into y values(2),(1); select * from y; commit;
        drop table t;
        drop table temp.nosuch;
        drop table nosuch.t;
        drop view v;
        drop trigger tg;
        drop trigger tg;
        select name from sqlite_temp_master order by 1;
        create temp table main.q(a); create temp table temp.q(a);
        """);
}
