using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RecursiveAI.OneLite.Oracle;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Diagnostics;

/// <summary>
/// Erroneous statements, run on OneLite and on the pinned SQLite library: the result code, extended code and message must be identical, and the
/// added members (position, caret, identifier, suggestions, constraint) must say the right thing.
/// </summary>
public sealed class ErrorDiagnosticsTests : IDisposable
{
    private const string Setup = """
        pragma foreign_keys = on;
        create table users(id integer primary key, name text not null, email text unique, age int check(age >= 0));
        create table orders(id integer primary key, user_id int references users(id), total real, note text);
        create table items(order_id int, sku text, qty int, primary key(order_id, sku)) without rowid;
        create table s(a integer, b text, c real, d blob, e any) strict;
        create table t(x, y);
        create table e(id integer primary key, c0 char(3));
        create table u(x, z);
        create view v as select id, name from users;
        create index orders_user on orders(user_id);
        create unique index users_name_age on users(name, age);
        insert into users values(1,'ann','a@x',30),(2,'bob','b@x',40);
        insert into orders values(1,1,9.5,'x');
        insert into items values(1,'k',1);
        insert into t values(1,2);
        insert into u values(1,3);
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-errors-" + Guid.NewGuid().ToString("N"));

    public ErrorDiagnosticsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    /// <summary>One erroneous statement and what the added members should report (null/0 = not checked).</summary>
    public sealed record ErrCase(string Sql, string? Identifier = null, int Column = 0, string? Suggest = null, ConstraintKind? Kind = null, int Line = 1);

    public static IEnumerable<ErrCase> Cases()
    {
        // syntax
        yield return new("selec 1", "selec", 1, "SELECT");
        yield return new("select from users", "from", 8);
        yield return new("select * form users", "form", 10, "FROM");
        yield return new("select * from", null);
        yield return new("select * from users where", null);
        yield return new("select * from users wher id = 1", "id", 26);
        yield return new("select * from users order id", "id", 27);
        yield return new("select * from users group name", "name", 27);
        yield return new("select (1", null);
        yield return new("select 1 +", null);
        yield return new("select 1 2", "2", 10);
        yield return new("select 'abc", "'abc", 8);
        yield return new("select \"abc", null);
        yield return new("select * from users where id in (", null);
        yield return new("insert users values (1)", "users", 8);
        yield return new("insert into users values (1,", null);
        yield return new("insert into users (id name) values (1, 'x')", "name", 26);
        yield return new("update users name = 'x'", "name", 14);
        yield return new("update set x = 1", "set", 8);
        yield return new("delete users", "users", 8);
        yield return new("delete from where id = 1", "where", 13);
        yield return new("create table", null);
        yield return new("create table x(", null);
        yield return new("create table x(a,)", ")", 18);
        yield return new("create index on t(x)", "on", 14);
        yield return new("drop users", "users", 6);
        yield return new("select 1 from users join", null);
        yield return new("select case when 1 then 2", null);
        yield return new("select 1 @ 2", null);
        yield return new("select #", null);
        yield return new("select * from users limit", null);
        yield return new("select * from users;; select", null);
        yield return new("with x as select 1", "select", 11);
        yield return new("select * from (select 1", null);
        yield return new("select count(*", null);
        yield return new("select 1 union", null);
        yield return new("pragma", null);
        yield return new("alter table users", null);
        yield return new("create view w", null);
        yield return new("select\n  *\nfrom users\nwhre id = 1", "id", 1, Line: 4);

        // names: tables
        yield return new("select * from user", "user", 15, "users");
        yield return new("select * from userz", "userz", 15, "users");
        yield return new("select * from order", "order", 15);
        yield return new("select * from orderz", "orderz", 15, "orders");
        yield return new("select * from main.nope", "nope", 20);
        yield return new("select * from temp.users", "users", 20);
        yield return new("select * from nosuch.users", "users", 21);
        yield return new("insert into usres values (1)", "usres", 13, "users");
        yield return new("update ordres set total = 1", "ordres", 8, "orders");
        yield return new("delete from itms", "itms", 13, "items");
        yield return new("drop table nope", "nope", 12);
        yield return new("drop view vv", "vv", 11, "v");
        yield return new("drop index orders_usr", "orders_usr", 12, "orders_user");
        yield return new("alter table usrs add column q", "usrs", 13, "users");
        yield return new("create index i on usres(x)", "usres", 17, "users");
        yield return new("select * from users join ordrs on 1", "ordrs", 26, "orders");
        yield return new("select * from users, itemz", "itemz", 22, "items");
        yield return new("select * from (select * from nope)", "nope", 29);
        yield return new("with c as (select 1) select * from cc", "cc", 36, "c");
        yield return new("select (select 1 from nothere)", "nothere", 24);
        yield return new("select 1 where exists (select 1 from missing)", "missing", 38);
        yield return new("select * from users where id in (select id from userz)", "userz", 50, "users");
        yield return new("create trigger tr after insert on usrs begin select 1; end", "usrs", 35, "users");
        yield return new("drop trigger nope", "nope", 14);
        yield return new("select * from sqlite_masterr", "sqlite_masterr", 15);
        yield return new("analyze nope", null);
        yield return new("reindex nope", null);

        // names: columns
        yield return new("select nme from users", "nme", 8, "name");
        yield return new("select naem from users", "naem", 8, "name");
        yield return new("select emial from users", "emial", 8, "email");
        yield return new("select users.nme from users", "nme", 14, "name");
        yield return new("select uu.nme from users uu", "nme", 10, "name");
        yield return new("select * from users where agee > 3", "agee", 21, "age");
        yield return new("select * from users order by nam", "nam", 30, "name");
        yield return new("select name from users group by nm", "nm", 34, "name");
        yield return new("select * from users having zz > 1", null);
        yield return new("select count(idd) from users", "idd", 14, "id");
        yield return new("select max(zz) from users", "zz", 12);
        yield return new("select z from t", "z", 8, "u.z");
        yield return new("select t.z from t, u", "z", 10, "u.z");
        yield return new("select y from u", "y", 8, "t.y");
        yield return new("select t.z from t join u on t.x = u.x", "z", 10, "u.z");
        yield return new("select t.x from t join u on t.x = u.xx", "xx", 33);
        yield return new("select * from t join u using (y)", null);
        yield return new("select \"nme\" from users", "nme", 8, "name");
        yield return new("select \"abc\" from users", "abc", 8);
        yield return new("select * from users where name = \"bob\"", "bob", 33);
        yield return new("insert into users(id, nme) values (1, 'x')", "nme", 22, "name");
        yield return new("insert into users(idd, name) values (1, 'x')", "idd", 19, "id");
        yield return new("update users set nme = 'x'", "nme", 18, "name");
        yield return new("update users set name = 'x' where agee = 3", "agee", 38, "age");
        yield return new("delete from users where emial = 'x'", "emial", 25, "email");
        yield return new("select 1 from users order by 2", null);
        yield return new("select 1 from users group by 3", null);
        yield return new("select * from users where nme is null", "nme", 21, "name");
        yield return new("select case when nme then 1 end from users", "nme", 13, "name");
        yield return new("select name from users where id = (select idd from orders)", "idd", 45, "id");
        yield return new("select * from users uu where uu.agee = 1", "agee", 25, "age");
        yield return new("select new.x from users", "x", 12);
        yield return new("select * from v where age = 1", "age", 17);
        yield return new("create index ix on users(nme)", "nme", 25, "name");
        yield return new("create table z(a, primary key(b))", "b", 31);
        yield return new("select row_number() over (order by nme) from users", "nme", 35, "name");
        yield return new("select * from users where users.id = orders.idd", "idd", 44, "id");

        // ambiguous
        yield return new("select x from t, u", "x", 8);
        yield return new("select x from t join u on 1", "x", 8);
        yield return new("select id from users, orders", "id", 8);
        yield return new("select * from users, orders where id = 1", "id", 37);
        yield return new("select t.x from t, u where x = 1", "x", 30);

        // functions
        yield return new("select nosuch(1)", "nosuch", 8);
        yield return new("select lenght('x')", "lenght", 8, "length");
        yield return new("select substrr('x', 1)", "substrr", 8, "substr");
        yield return new("select upperr(name) from users", "upperr", 8, "upper");
        yield return new("select abs()", "abs", 8);
        yield return new("select abs(1, 2)", "abs", 8);
        yield return new("select length()", "length", 8);
        yield return new("select length(1, 2)", "length", 8);
        yield return new("select substr('x')", "substr", 8);
        yield return new("select substr('x', 1, 2, 3)", "substr", 8);
        yield return new("select replace('x', 'y')", "replace", 8);
        yield return new("select ifnull(1)", "ifnull", 8);
        yield return new("select nullif(1)", "nullif", 8);
        yield return new("select round()", "round", 8);
        yield return new("select round(1, 2, 3)", "round", 8);
        yield return new("select typeof()", "typeof", 8);
        yield return new("select count(1, 2) from users", "count", 8);
        yield return new("select sum() from users", "sum", 8);
        yield return new("select avg(1, 2) from users", "avg", 8);
        yield return new("select max() from users", "max", 8);
        yield return new("select * from users where lower(name, 1) = 'x'", "lower", 21);
        yield return new("select * from pragma_nosuch", "pragma_nosuch", 15);
        yield return new("select * from nosuchfn(1)", "nosuchfn", 15);
        yield return new("create table cz(a text collate nope)", "nope", 33);
        yield return new("select * from users order by name collate rtrimm", "rtrimm", 42, "RTRIM");

        // misuse
        yield return new("select sum(age) from users where sum(age) > 1", "sum", 36);
        yield return new("select * from users where count(*) > 1", "count", 21);
        yield return new("select * from users group by count(*)", null);
        yield return new("select sum(sum(age)) from users", "sum", 12);
        yield return new("select name from users having age > 1", null);
        yield return new("select row_number() from users", "row_number", 8);
        yield return new("select * from users where row_number() over () = 1", null);
        yield return new("select ntile(0) over () from users", null);
        yield return new("select * from users limit 'a'", null);
        yield return new("select * from users limit null", null);
        yield return new("select * from users limit 1 offset null", null);
        yield return new("select * from users limit 1.5", null);
        yield return new("select * from users order by id limit null", null);
        yield return new("select null from e order by id limit null", null);
        yield return new("select null from e limit null", null);
        yield return new("select null from e limit 'a'", null);
        yield return new("select * from users order by name limit 'a'", null);
        yield return new("select * from users order by id limit 1 offset null", null);
        yield return new("select * from users order by id limit 1.5", null);
        yield return new("select distinct name from users limit null", null);
        yield return new("select count(*) from users limit null", null);
        yield return new("select name from users group by name limit null", null);
        yield return new("select * from users where id > 0 limit null", null);
        yield return new("select * from users union select * from users limit null", null);
        yield return new("select * from users order by id desc limit 'x'", null);
        yield return new("select * from users order by age limit 1 offset 'x'", null);
        yield return new("select * from users limit x'00'", null);
        yield return new("select 1 from users limit -1 offset 'x'", null);
        yield return new("select * from users u, orders u", null);
        yield return new("select 1 union select 1, 2", null);
        yield return new("select 1, 2 intersect select 1", null);
        yield return new("select * from users except select 1", null);
        yield return new("values (1), (1, 2)", null);
        yield return new("insert into users(id) values (1, 2)", null);
        yield return new("insert into users values (1)", null);
        yield return new("insert into users(id, name) values (3)", null);
        yield return new("insert into users select 1", null);
        yield return new("insert into v values (1, 'x')", "v", 13);
        yield return new("update v set name = 'x'", "v", 8);
        yield return new("delete from v", "v", 13);
        yield return new("insert into sqlite_master values (1)", null);
        yield return new("update sqlite_master set name = 'x'", null);
        yield return new("create table users(a)", "users", 14);
        yield return new("create table if not exists users(a, a)", "a", 38);
        yield return new("create index orders_user on orders(note)", "orders_user", 14);
        yield return new("create view v as select 1", "v", 13);
        yield return new("create table q(a, a)", "a", 19);
        yield return new("create table q(a primary key, b primary key)", null);
        yield return new("create table q(a, primary key(a), primary key(a))", null);
        yield return new("create table q(a int, b) strict", null);
        yield return new("create table q(a foo) strict", "foo", 18);
        yield return new("create table sqlite_q(a)", null);
        yield return new("alter table users rename to orders", "orders", 30);
        yield return new("alter table users add column name text", "name", 31);
        yield return new("alter table users drop column nope", "nope", 31);
        yield return new("alter table users drop column id", null);
        yield return new("alter table users rename column nope to x", "nope", 31);
        yield return new("alter table nope rename to x", "nope", 13);
        yield return new("select ?1, ?0", null);
        yield return new("select ?99999", null);
        yield return new("select 1 from users where id = :a and id = $", null);
        yield return new("savepoint", null);
        yield return new("release nope", null);
        yield return new("rollback to nope", null);
        yield return new("commit", null);
        yield return new("rollback", null);
        yield return new("vacuum nope", null);

        // constraints (runtime)
        yield return new("insert into users values (1, 'cy', 'c@x', 1)", "id", 0, null, ConstraintKind.PrimaryKey);
        yield return new("insert into users values (null, null, 'c@x', 1)", "name", 0, null, ConstraintKind.NotNull);
        yield return new("insert into users values (3, 'cy', 'a@x', 1)", "email", 0, null, ConstraintKind.Unique);
        yield return new("insert into users values (3, 'cy', 'c@x', -1)", null, 0, null, ConstraintKind.Check);
        yield return new("insert into users values (3, 'ann', 'c@x', 30)", "name", 0, null, ConstraintKind.Unique);
        yield return new("insert into users(id, email) values (3, 'c@x')", "name", 0, null, ConstraintKind.NotNull);
        yield return new("update users set email = 'b@x' where id = 1", "email", 0, null, ConstraintKind.Unique);
        yield return new("update users set name = null where id = 1", "name", 0, null, ConstraintKind.NotNull);
        yield return new("update users set age = -5", null, 0, null, ConstraintKind.Check);
        yield return new("update users set id = 2 where id = 1", "id", 0, null, ConstraintKind.PrimaryKey);
        yield return new("insert into orders values (2, 99, 1.0, 'n')", null, 0, null, ConstraintKind.ForeignKey);
        yield return new("insert into orders values (1, 1, 1.0, 'n')", "id", 0, null, ConstraintKind.PrimaryKey);
        yield return new("delete from users where id = 1", null, 0, null, ConstraintKind.ForeignKey);
        yield return new("update users set id = 10 where id = 1", null, 0, null, ConstraintKind.ForeignKey);
        yield return new("insert into items values (1, 'k', 5)", "order_id", 0, null, ConstraintKind.PrimaryKey);
        yield return new("insert into items values (1, 'k2', 5), (1, 'k2', 6)", "order_id", 0, null, ConstraintKind.PrimaryKey);
        yield return new("insert into users values (3, 'cy', 'c@x', 1), (4, 'cy', 'd@x', 1), (5, 'cy', 'd@x', 1)", "name", 0, null, ConstraintKind.Unique);
        yield return new("insert or fail into users values (1, 'q', 'q@x', 1)", "id", 0, null, ConstraintKind.PrimaryKey);
        yield return new("insert or abort into users values (1, 'q', 'q@x', 1)", "id", 0, null, ConstraintKind.PrimaryKey);
        yield return new("insert into users select 1, 'q', 'q@x', 1", "id", 0, null, ConstraintKind.PrimaryKey);
        yield return new("create unique index uz on users(age)", null, 0, null, null);
        yield return new("create unique index ue on orders(note, user_id) where 1; insert into orders values (5, 1, 1, 'x')", null);

        // strict tables
        yield return new("insert into s(a) values ('x')", "a", 0, null, ConstraintKind.DataType);
        yield return new("insert into s(a) values (1.5)", "a", 0, null, ConstraintKind.DataType);
        yield return new("insert into s(b) values (1)", null);
        yield return new("insert into s(b) values (x'00')", "b", 0, null, ConstraintKind.DataType);
        yield return new("insert into s(c) values ('abc')", "c", 0, null, ConstraintKind.DataType);
        yield return new("insert into s(d) values ('abc')", "d", 0, null, ConstraintKind.DataType);
        yield return new("insert into s(d) values (5)", "d", 0, null, ConstraintKind.DataType);
        yield return new("update s set a = 'x'", null);
        yield return new("insert into s values (1, 'a', 1.0, x'', 1), ('z', 'a', 1.0, x'', 1)", "a", 0, null, ConstraintKind.DataType);
        yield return new("insert into s(a) values (9223372036854775808)", null);

        // runtime, not constraints
        yield return new("select abs(-9223372036854775808)", null);
        yield return new("select 9223372036854775807 + 1 from (select 1) where 0 or sum(9223372036854775807) is null", null);
        yield return new("select sum(x) from (select 9223372036854775807 x union all select 1)", null);
        yield return new("select randomblob(-1) is null", null);
        yield return new("select zeroblob(2000000000)", null);
        yield return new("select json('{')", null);
        yield return new("select json_extract('{}', 'x')", null);
        yield return new("select load_extension('x')", null);
        yield return new("select * from users where id = (select id from users)", null);
        yield return new("select 1/0", null);
        yield return new("select like('a')", "like", 8);
        yield return new("select printf()", null);
        yield return new("select strftime()", null);
        yield return new("select unicode()", "unicode", 8);
    }

    private static (int Code, int Ext, string Message)? Theirs(SqliteDb db, string sql)
    {
        try
        {
            using SqliteStatement st = db.Prepare(sql);
            while (st.Step())
            {
            }

            return null;
        }
        catch (SqliteException e)
        {
            return (e.Code, e.ExtendedCode, e.Message);
        }
    }

    private static OneLiteException? Ours(Connection c, string sql)
    {
        try
        {
            using Statement st = c.Prepare(sql);
            while (st.Step())
            {
            }

            return null;
        }
        catch (OneLiteException e)
        {
            return e;
        }
    }

    private (Connection Ours, Database Db, SqliteDb Theirs) Open()
    {
        string a = Path.Combine(_dir, Guid.NewGuid().ToString("N") + "-ol.db");
        string b = Path.Combine(_dir, Guid.NewGuid().ToString("N") + "-sq.db");
        SqliteDb theirs = SqliteDb.Open(b);
        theirs.Exec(Setup);
        Database db = Database.Open(a);
        Connection c = db.Connect();
        foreach (string stmt in Setup.Split(";\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using Statement st = c.Prepare(stmt);
            while (st.Step())
            {
            }
        }

        return (c, db, theirs);
    }

    [Fact]
    public void AtLeast150Cases() => Assert.True(Cases().Count() >= 150);

    [Fact]
    public void ErrorsMatchTheReferenceAndSayWhere()
    {
        Assert.SkipUnless(File.Exists(OraclePaths.NativeLibrary), "the pinned SQLite library is not installed");
        (Connection c, Database db, SqliteDb theirs) = Open();
        using (c)
        using (db)
        using (theirs)
        {
            var bad = new List<string>();
            int errors = 0;
            foreach (ErrCase k in Cases())
            {
                (int Code, int Ext, string Message)? t = Theirs(theirs, k.Sql);
                OneLiteException? o = Ours(c, k.Sql);
                if (t is null)
                {
                    if (o is not null)
                    {
                        bad.Add($"{k.Sql}\n   reference ok, ours {o.Code}/{o.ExtendedCode} {o.Message}");
                    }

                    continue;
                }

                errors++;
                if (o is null)
                {
                    bad.Add($"{k.Sql}\n   reference {t.Value.Code}/{t.Value.Ext} {t.Value.Message}, ours ok");
                    continue;
                }

                if (o.Code != t.Value.Code || o.ExtendedCode != t.Value.Ext || o.Message != t.Value.Message)
                {
                    bad.Add($"{k.Sql}\n   reference {t.Value.Code}/{t.Value.Ext} {t.Value.Message}\n   ours      {o.Code}/{o.ExtendedCode} {o.Message}");
                    continue;
                }

                string? why = Check(k, o);
                if (why is not null)
                {
                    bad.Add($"{k.Sql}\n   {why}");
                }
            }

            Assert.True(errors >= 150, $"only {errors} statements failed in the reference");
            File.WriteAllLines(Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "error-diagnostics-problems.txt"), bad);
            Assert.True(bad.Count == 0, bad.Count + " problems, first:\n" + string.Join("\n", bad.Take(25)));
        }
    }

    /// <summary>Every prefix of every case and every case with one character removed: errors stay typed, positions stay inside the text, the rendering never throws.</summary>
    [Fact]
    public void MangledStatementsNeverBreakTheDiagnostics()
    {
        (Connection c, Database db, SqliteDb theirs) = Open();
        using (c)
        using (db)
        using (theirs)
        {
            int errors = 0, positioned = 0;
            foreach (ErrCase k in Cases())
            {
                var variants = new List<string>();
                for (int i = 1; i < k.Sql.Length; i++)
                {
                    variants.Add(k.Sql[..i]);
                    variants.Add(k.Sql.Remove(i, 1));
                }

                foreach (string sql in variants)
                {
                    if (sql.Contains("insert", StringComparison.OrdinalIgnoreCase) || sql.Contains("update", StringComparison.OrdinalIgnoreCase)
                        || sql.Contains("delete", StringComparison.OrdinalIgnoreCase) || sql.Contains("drop", StringComparison.OrdinalIgnoreCase)
                        || sql.Contains("alter", StringComparison.OrdinalIgnoreCase) || sql.Contains("create", StringComparison.OrdinalIgnoreCase)
                        || sql.Contains("vacuum", StringComparison.OrdinalIgnoreCase) || sql.Contains("reindex", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    OneLiteException? e = Ours(c, sql);
                    if (e is null)
                    {
                        continue;
                    }

                    errors++;
                    Assert.False(e.Message is null or "", sql);
                    Assert.NotNull(e.ToString());
                    Assert.NotNull(e.Explain());
                    if (e.Position is { } p)
                    {
                        positioned++;
                        Assert.InRange(p.Offset, 0, sql.Length);
                        Assert.True(p.Line >= 1 && p.Column >= 1, sql);
                        Assert.NotNull(e.Caret);
                    }
                }
            }

            Assert.True(errors > 2000 && positioned > 500, $"{errors} errors, {positioned} positioned");
        }
    }

    private static string? Check(ErrCase k, OneLiteException o)
    {
        if (o.Sql != k.Sql)
        {
            return "Sql not set";
        }

        if (k.Identifier is not null && !string.Equals(o.Identifier, k.Identifier, StringComparison.OrdinalIgnoreCase))
        {
            return $"identifier {o.Identifier ?? "(null)"}, expected {k.Identifier}";
        }

        if (k.Column > 0)
        {
            if (o.Position is not { } p)
            {
                return "no position";
            }

            string at = o.Sql.Substring(p.Offset, Math.Min(p.Length, o.Sql.Length - p.Offset)).Trim('"', '`', '[', ']');
            if (!string.Equals(at, k.Identifier, StringComparison.OrdinalIgnoreCase) || p.Line != k.Line)
            {
                return $"position {p} points at '{at}', expected '{k.Identifier}' on line {k.Line}";
            }

            string[] lines = o.Caret is null ? [] : o.Caret.Split('\n');
            int caret = lines.Length == 2 ? lines[1].IndexOf('^', StringComparison.Ordinal) : -1;
            if (caret < 0 || !lines[0].Substring(caret).StartsWith(o.Sql.Substring(p.Offset, 1), StringComparison.Ordinal) || lines[1].Substring(0, caret).Trim(' ', '\t').Length != 0)
            {
                return "caret misplaced: " + (o.Caret ?? "(null)").Replace("\n", "|");
            }
        }

        if (k.Suggest is not null && !o.Suggestions.Contains(k.Suggest, StringComparer.OrdinalIgnoreCase))
        {
            return $"suggestions [{string.Join(", ", o.Suggestions)}] lack {k.Suggest}";
        }

        if (k.Kind is { } kind)
        {
            if (o.Constraint is not { } c || c.Kind != kind)
            {
                return $"constraint {o.Constraint?.Kind.ToString() ?? "(null)"}, expected {kind}";
            }
        }

        if (!o.ToString().Contains(o.Message, StringComparison.Ordinal))
        {
            return "ToString lost the message";
        }

        return null;
    }
}
