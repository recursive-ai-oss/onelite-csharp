using System.Linq;
using System.Text;
using SQLitePCL;

namespace RecursiveAI.OneLite.SQLitePCLRaw.Tests;

/// <summary>
/// Runs the same sqlite3_* calls through SQLitePCL.raw on the native library and on the adapter and compares everything observable:
/// return codes, extended codes, error messages, column names, types, values, changes and hook calls.
/// </summary>
public class ProviderParityTests
{
    private static void UseNative() => raw.SetProvider(new SQLite3Provider_e_sqlite3());

    private static void UseOneLite() => OneLiteBatteries.Init();

    private static void Same(Func<StringBuilder, string> scenario, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        UseNative();
        var a = new StringBuilder();
        scenario(a);
        UseOneLite();
        var b = new StringBuilder();
        scenario(b);
        Assert.NotEmpty(a.ToString());
        if (Environment.GetEnvironmentVariable("ONELITE_PARITY_DUMP") is { Length: > 0 } dir)
        {
            File.WriteAllText(Path.Combine(dir, name + ".native.txt"), a.ToString());
            File.WriteAllText(Path.Combine(dir, name + ".onelite.txt"), b.ToString());
        }

        string[] x = a.ToString().Split('\n');
        string[] y = b.ToString().Split('\n');
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            string l = i < x.Length ? x[i] : "<end>";
            string r = i < y.Length ? y[i] : "<end>";
            if (l != r)
            {
                string ctx = string.Join("\n", x.Skip(Math.Max(0, i - 4)).Take(4));
                Assert.Fail($"first difference at line {i}:\n  native:  {l}\n  onelite: {r}\n  before:\n{ctx}");
            }
        }
    }

    private static string Row(sqlite3_stmt st)
    {
        var sb = new StringBuilder();
        int n = raw.sqlite3_column_count(st);
        for (int i = 0; i < n; i++)
        {
            int t = raw.sqlite3_column_type(st, i);
            sb.Append(raw.sqlite3_column_name(st, i).utf8_to_string()).Append('/').Append(raw.sqlite3_column_decltype(st, i).utf8_to_string() ?? "<null>").Append('/').Append(t).Append('=');
            switch (t)
            {
                case raw.SQLITE_INTEGER: sb.Append(raw.sqlite3_column_int64(st, i)); break;
                case raw.SQLITE_FLOAT: sb.Append(raw.sqlite3_column_double(st, i).ToString("R", System.Globalization.CultureInfo.InvariantCulture)); break;
                case raw.SQLITE_TEXT: sb.Append(raw.sqlite3_column_text(st, i).utf8_to_string()).Append('(').Append(raw.sqlite3_column_bytes(st, i)).Append(')'); break;
                case raw.SQLITE_BLOB: sb.Append(Convert.ToHexString(raw.sqlite3_column_blob(st, i))); break;
                default: sb.Append("null"); break;
            }

            sb.Append(';');
        }

        return sb.ToString();
    }

    private static string Err(sqlite3 db, int rc) => $"rc={rc} ext={raw.sqlite3_extended_errcode(db)} msg={raw.sqlite3_errmsg(db).utf8_to_string()}";

    private static sqlite3 Open(string name = ":memory:")
    {
        int rc = raw.sqlite3_open(name, out sqlite3 db);
        Assert.Equal(raw.SQLITE_OK, rc);
        raw.sqlite3_extended_result_codes(db, 1);
        return db;
    }

    private static void Exec(StringBuilder log, sqlite3 db, string sql)
    {
        int rc = raw.sqlite3_exec(db, sql, null, null, out string? err);
        log.Append("exec ").Append(rc).Append(' ').Append(err).Append(" | ").AppendLine(Err(db, rc));
    }

    private static void Query(StringBuilder log, sqlite3 db, string sql, params object?[] args)
    {
        int rc = raw.sqlite3_prepare_v2(db, sql, out sqlite3_stmt st, out string tail);
        log.Append("prepare ").Append(rc).Append(" tail=[").Append(tail).Append("] ").AppendLine(rc == 0 ? "" : Err(db, rc));
        if (rc != 0)
        {
            return;
        }

        log.Append("params=").Append(raw.sqlite3_bind_parameter_count(st)).Append(" ro=").Append(raw.sqlite3_stmt_readonly(st)).AppendLine();
        for (int i = 0; i < args.Length; i++)
        {
            int br = args[i] switch
            {
                null => raw.sqlite3_bind_null(st, i + 1),
                long l => raw.sqlite3_bind_int64(st, i + 1, l),
                double d => raw.sqlite3_bind_double(st, i + 1, d),
                string s => raw.sqlite3_bind_text(st, i + 1, s),
                byte[] b => raw.sqlite3_bind_blob(st, i + 1, b),
                _ => throw new InvalidOperationException(),
            };
            log.Append("bind ").Append(br).AppendLine();
        }

        for (; ; )
        {
            rc = raw.sqlite3_step(st);
            if (rc == raw.SQLITE_ROW)
            {
                log.Append("row ").AppendLine(Row(st));
                continue;
            }

            log.Append("step ").Append(rc).Append(' ').AppendLine(Err(db, rc));
            break;
        }

        log.Append("reset ").Append(raw.sqlite3_reset(st)).Append(" fin ").Append(raw.sqlite3_finalize(st)).AppendLine();
        log.Append("changes=").Append(raw.sqlite3_changes(db)).Append(" total=").Append(raw.sqlite3_total_changes(db)).Append(" rowid=").Append(raw.sqlite3_last_insert_rowid(db)).Append(" auto=").Append(raw.sqlite3_get_autocommit(db)).AppendLine();
    }

    [Fact]
    public void ValuesAndAffinities() => Same(log =>
    {
        sqlite3 db = Open();
        Exec(log, db, "create table t(id integer primary key, a, i integer, r real, s text, b blob, n numeric, v varchar(10))");
        Query(log, db, "insert into t(a,i,r,s,b,n,v) values (?,?,?,?,?,?,?)", 5L, "12", 3L, 4L, new byte[] { 1, 2 }, "7.50", 99L);
        Query(log, db, "insert into t(a,i,r,s,b,n,v) values (?,?,?,?,?,?,?)", "txt", "x", "1e3", 2.5, null, "abc", "é");
        Query(log, db, "insert into t(a,i,r,s,b,n,v) values (?,?,?,?,?,?,?)", 1.5, 9223372036854775807L, null, "", Array.Empty<byte>(), 0.1, "");
        Query(log, db, "select * from t order by id");
        Query(log, db, "select typeof(a), typeof(n), a+1, i*2, r/3, length(s), hex(b), upper(v), quote(a) from t");
        Query(log, db, "select 1/0, 9223372036854775807+1, 0.1+0.2, 1e300*1e300, -0.0, abs(-9223372036854775807-1)");
        Query(log, db, "select x'00ff', zeroblob(3), 'a'||char(0)||'b', cast('abc' as blob), cast(x'414243' as text)");
        Query(log, db, "select ?1, ?1+?2, :n, @m, $k", 1L, 2L);
        raw.sqlite3_close(db);
        return "";
    });

    [Fact]
    public void ErrorCodesAndMessages() => Same(log =>
    {
        sqlite3 db = Open();
        Exec(log, db, "create table t(id integer primary key, u text unique, nn integer not null, c integer check (c > 0), f references t(id))");
        Exec(log, db, "create table t(x)");
        Exec(log, db, "selec 1");
        Exec(log, db, "select * from nosuch");
        Exec(log, db, "select nosuchcol from t");
        Exec(log, db, "select nosuchfn(1)");
        Exec(log, db, "insert into t(u,nn) values ('a',1)");
        Exec(log, db, "insert into t(u,nn) values ('a',1)");
        Exec(log, db, "insert into t(u) values ('b')");
        Exec(log, db, "insert into t(u,nn,c) values ('c',1,0)");
        Exec(log, db, "pragma foreign_keys=on");
        Exec(log, db, "insert into t(u,nn,f) values ('d',1,999),('e',1,1000)");
        Exec(log, db, "insert into t values(1,2)");
        Exec(log, db, "create index i on nosuch(x)");
        Exec(log, db, "drop table nosuch");
        Exec(log, db, "begin");
        Exec(log, db, "begin");
        Exec(log, db, "commit");
        Exec(log, db, "commit");
        Exec(log, db, "rollback");
        Exec(log, db, "release nosuch");
        Exec(log, db, "select 'unterminated");
        Exec(log, db, "select (1");
        Exec(log, db, "pragma nosuchpragma");
        Exec(log, db, "");
        Exec(log, db, "-- only a comment");
        Query(log, db, "select 1; select 2; ");
        Query(log, db, "  select 1 ;   -- tail\n select 2");
        Query(log, db, "select ?", 1L, 2L);
        raw.sqlite3_exec(db, "select 1", null, null, out string? _);
        log.AppendLine(raw.sqlite3_errstr(raw.SQLITE_BUSY).utf8_to_string());
        log.AppendLine(raw.sqlite3_errstr(raw.SQLITE_CONSTRAINT).utf8_to_string());
        log.AppendLine(raw.sqlite3_errstr(2067).utf8_to_string());
        log.AppendLine(raw.sqlite3_errstr(12345).utf8_to_string());
        raw.sqlite3_close(db);
        return "";
    });

    [Fact]
    public void StepOnFinishedAndMisuse() => Same(log =>
    {
        sqlite3 db = Open();
        Exec(log, db, "create table t(a); insert into t values (1),(2)");
        raw.sqlite3_prepare_v2(db, "select a from t", out sqlite3_stmt st);
        for (int i = 0; i < 4; i++)
        {
            log.Append(raw.sqlite3_step(st)).Append(' ').AppendLine(raw.sqlite3_data_count(st).ToString());
        }

        raw.sqlite3_reset(st);
        log.Append(raw.sqlite3_step(st)).Append(' ').AppendLine(raw.sqlite3_column_int64(st, 0).ToString());
        log.Append("oob type ").Append(raw.sqlite3_column_type(st, 5)).Append(" int ").AppendLine(raw.sqlite3_column_int64(st, 5).ToString());
        log.Append("bind oob ").AppendLine(raw.sqlite3_bind_int64(st, 3, 1).ToString());
        raw.sqlite3_finalize(st);
        raw.sqlite3_close(db);
        return "";
    });

    [Fact]
    public void ChangesRowidTransactions() => Same(log =>
    {
        sqlite3 db = Open();
        Exec(log, db, "create table t(id integer primary key autoincrement, v)");
        Query(log, db, "insert into t(v) values (1),(2),(3)");
        Query(log, db, "update t set v=v+1 where id>1");
        Query(log, db, "delete from t where id=1");
        Query(log, db, "begin");
        Query(log, db, "insert into t(v) values (9)");
        Query(log, db, "rollback");
        Query(log, db, "select count(*), max(id) from t");
        Query(log, db, "insert into t(id,v) values (100,'x') on conflict(id) do update set v='y'");
        Query(log, db, "insert or replace into t(id,v) values (100,'z')");
        Query(log, db, "select * from sqlite_sequence");
        Query(log, db, "delete from t");
        Query(log, db, "insert into t(v) values (1) returning v*2");
        raw.sqlite3_close(db);
        return "";
    });

    [Fact]
    public void UserFunctionsAggregatesCollations() => Same(log =>
    {
        sqlite3 db = Open();
        raw.sqlite3_create_function(db, "twice", 1, null, (ctx, user, args) =>
        {
            raw.sqlite3_result_int64(ctx, raw.sqlite3_value_int64(args[0]) * 2);
        });
        raw.sqlite3_create_function(db, "describe", -1, null, (ctx, user, args) =>
        {
            var sb = new StringBuilder();
            foreach (sqlite3_value v in args)
            {
                sb.Append(raw.sqlite3_value_type(v)).Append(':').Append(raw.sqlite3_value_text(v).utf8_to_string()).Append(',');
            }

            raw.sqlite3_result_text(ctx, sb.ToString());
        });
        raw.sqlite3_create_function(db, "boom", 0, null, (ctx, user, args) => raw.sqlite3_result_error(ctx, "custom failure"));
        raw.sqlite3_create_function(db, "mysum", 1, 0, null,
            (ctx, user, args) =>
            {
                long cur = ctx.state is long l ? l : 0;
                ctx.state = cur + raw.sqlite3_value_int64(args[0]);
            },
            (ctx, user) => raw.sqlite3_result_int64(ctx, ctx.state is long l ? l : -1));
        raw.sqlite3_create_collation(db, "rev", null, (user, a, b) => string.CompareOrdinal(b, a));
        Exec(log, db, "create table t(a text collate rev); insert into t values ('a'),('c'),('b')");
        Query(log, db, "select twice(21), twice('4'), twice(null), twice(2.9)");
        Query(log, db, "select describe(1, 'x', null, 2.5, x'01')");
        Query(log, db, "select boom()");
        Query(log, db, "select a from t order by a");
        Query(log, db, "select 'a' < 'b' collate rev, max(a) from t");
        Query(log, db, "select twice(1, 2)");
        Query(log, db, "select mysum(a) from (select 1 a union all select 2 union all select 39)");
        raw.sqlite3_close(db);
        return "";
    });

    [Fact]
    public void Hooks() => Same(log =>
    {
        sqlite3 db = Open();
        raw.sqlite3_update_hook(db, (user, op, dbn, tbl, rowid) => log.Append($"update {op} {dbn}.{tbl} {rowid}\n"), null);
        int veto = 0;
        raw.sqlite3_commit_hook(db, user => { log.Append("commit-hook\n"); return veto; }, null);
        raw.sqlite3_rollback_hook(db, user => log.Append("rollback-hook\n"), null);
        Exec(log, db, "create table t(id integer primary key, v); create table w(k text primary key, v) without rowid");
        Query(log, db, "insert into t(v) values (1),(2),(3)");
        Query(log, db, "update t set v = v * 2 where id <= 2");
        Query(log, db, "update t set id = id + 10 where id = 3");
        Query(log, db, "delete from t where id = 1");
        Query(log, db, "insert into w values ('a', 1)");
        Query(log, db, "insert or replace into t(id, v) values (2, 5)");
        Query(log, db, "insert into t(id, v) values (2, 7) on conflict(id) do update set v = excluded.v");
        Exec(log, db, "begin; insert into t(v) values (42); rollback");
        Exec(log, db, "begin; insert into t(v) values (43); commit");
        veto = 1;
        Exec(log, db, "begin; insert into t(v) values (44); commit");
        Exec(log, db, "insert into t(v) values (45)");
        veto = 0;
        Query(log, db, "select count(*) from t");
        Exec(log, db, "insert into t(v) values (1) /* fails */, ('x', 2)");
        Exec(log, db, "insert into t(id) values (2)");
        Exec(log, db, "delete from w");
        Exec(log, db, "insert into t(id, v) values (500, 1), (501, 2)");
        Exec(log, db, "delete from t");
        raw.sqlite3_update_hook(db, (SQLitePCL.delegate_update?)null, null);
        Exec(log, db, "insert into t(v) values (50)");
        raw.sqlite3_close(db);
        return "";
    });

    [Fact]
    public void DeclaredTypes() => Same(log =>
    {
        sqlite3 db = Open();
        Exec(log, db, "create table t(a Integer, b int, c INT, d varchar(10), e double precision, f unsigned big int, g Text, h blob, i ANY, j real, k float, l numeric(5,2), m DateTime, n  text  collate nocase, o bool, p , q integer primary key, r Real, s INTEGER NOT NULL, t Boolean)");
        Exec(log, db, "insert into t(q, s) values (1, 2)");
        Query(log, db, "select * from t");
        Query(log, db, "select a+1, cast(a as text), a as z from t");
        Query(log, db, "select * from (select a, g from t)");
        Query(log, db, "select a, count(*) from t group by a");
        Exec(log, db, "create table s(a Integer, b text) strict; insert into s values (1, 'x')");
        Query(log, db, "select * from s");
        raw.sqlite3_close(db);
        return "";
    });

    [Fact]
    public void ExecWithCallback() => Same(log =>
    {
        sqlite3 db = Open();
        int rc = raw.sqlite3_exec(db, "create table t(a, b); insert into t values (1,'x'),(null,'y'); select a, b from t", (user, values, names) =>
        {
            log.Append(string.Join(",", names)).Append('=').Append(string.Join(",", values.Select(v => v ?? "<null>"))).AppendLine();
            return 0;
        }, null, out string? err);
        log.Append(rc).Append(err).AppendLine();
        rc = raw.sqlite3_exec(db, "select a from t", (user, values, names) => 1, null, out err);
        log.Append(rc).Append(' ').Append(err).Append(' ').AppendLine(Err(db, rc));
        raw.sqlite3_close(db);
        return "";
    });

    [Fact]
    public void SharedCacheMemoryDatabase() => Same(log =>
    {
        int flags = raw.SQLITE_OPEN_READWRITE | raw.SQLITE_OPEN_CREATE | raw.SQLITE_OPEN_URI;
        raw.sqlite3_open_v2("file:u54shared?mode=memory&cache=shared", out sqlite3 a, flags, null);
        raw.sqlite3_open_v2("file:u54shared?mode=memory&cache=shared", out sqlite3 b, flags, null);
        Exec(log, a, "create table t(x); insert into t values (1)");
        Query(log, b, "select x from t");
        raw.sqlite3_open_v2("file:u54other?mode=memory&cache=shared", out sqlite3 c, flags, null);
        Query(log, c, "select x from t");
        raw.sqlite3_close(a);
        Query(log, b, "select x from t");
        raw.sqlite3_close(b);
        raw.sqlite3_close(c);
        raw.sqlite3_open_v2("file:u54shared?mode=memory&cache=shared", out sqlite3 d, flags, null);
        Query(log, d, "select x from t");
        raw.sqlite3_close(d);
        flags = raw.SQLITE_OPEN_READWRITE | raw.SQLITE_OPEN_CREATE | 0x80 | 0x20000 | 0x40;
        raw.sqlite3_open_v2("file:u54flagshared", out sqlite3 e, flags, null);
        raw.sqlite3_open_v2("file:u54flagshared", out sqlite3 f, flags, null);
        Exec(log, e, "create table t(x); insert into t values (7)");
        Query(log, f, "select x from t");
        raw.sqlite3_close(e);
        raw.sqlite3_close(f);
        return "";
    });

    [Fact]
    public void SchemaChangeWhileAStatementReads() => Same(log =>
    {
        sqlite3 db = Open();
        Exec(log, db, "create table t(a); create table u(a); create index ti on t(a); insert into t values (1),(2),(3); insert into u values (1)");
        raw.sqlite3_prepare_v2(db, "select a from t", out sqlite3_stmt st);
        log.Append("step ").AppendLine(raw.sqlite3_step(st).ToString());
        Exec(log, db, "drop table t");
        Exec(log, db, "drop table u");
        Exec(log, db, "drop index ti");
        Exec(log, db, "create index tj on t(a)");
        Exec(log, db, "alter table t rename to t2");
        Exec(log, db, "alter table t add column b");
        Exec(log, db, "insert into t values (9)");
        Exec(log, db, "delete from t where a = 9");
        Exec(log, db, "vacuum");
        Exec(log, db, "create table w(a)");
        raw.sqlite3_finalize(st);
        Exec(log, db, "drop table t");
        raw.sqlite3_close(db);
        return "";
    });

    [Fact]
    public void ForeignKeysOnChildUpdateAndParameter() => Same(log =>
    {
        sqlite3 db = Open();
        Exec(log, db, "PRAGMA foreign_keys=ON");
        Exec(log, db, "CREATE TABLE \"Eagle\" (\"Species\" TEXT NOT NULL CONSTRAINT \"PK_Eagle\" PRIMARY KEY, \"Group\" INTEGER NOT NULL)");
        Exec(log, db, "CREATE TABLE \"Kiwi\" (\"Species\" TEXT NOT NULL CONSTRAINT \"PK_Kiwi\" PRIMARY KEY, \"EagleId\" TEXT NULL, CONSTRAINT \"FK_Kiwi_Eagle_EagleId\" FOREIGN KEY (\"EagleId\") REFERENCES \"Eagle\" (\"Species\"))");
        Exec(log, db, "INSERT INTO Eagle VALUES ('Aquila', 1); INSERT INTO Kiwi VALUES ('Apteryx', 'Aquila')");
        Exec(log, db, "UPDATE Kiwi SET EagleId = 'Nope' WHERE Species = 'Apteryx'");
        Query(log, db, "UPDATE \"Kiwi\" SET \"EagleId\" = ?1 WHERE \"Species\" = 'Apteryx' RETURNING 1", "Nope");
        Query(log, db, "UPDATE \"Kiwi\" SET \"EagleId\" = @p WHERE \"Species\" = @k", "Nope", "Apteryx");
        log.Append("insert: ").AppendLine(raw.sqlite3_exec(db, "INSERT INTO \"Kiwi\" (\"Species\", \"EagleId\") VALUES ('x', 'Nope')").ToString());
        Exec(log, db, "PRAGMA foreign_keys");
        raw.sqlite3_prepare_v2(db, "select count(*), group_concat(EagleId) from Kiwi", out sqlite3_stmt q);
        raw.sqlite3_step(q);
        log.AppendLine(Row(q));
        raw.sqlite3_finalize(q);
        raw.sqlite3_close(db);
        return "";
    });

    [Fact]
    public void UpdateFromJoinOnCannotSeeTheTarget() => Same(log =>
    {
        sqlite3 db = Open();
        Exec(log, db, "create table a(id integer primary key, n); create table b(id integer primary key); create table k(id integer primary key)");
        Exec(log, db, "insert into a values (1, 0), (2, 0); insert into b values (1), (2); insert into k values (1)");
        Exec(log, db, "update a set n = 1 from b inner join k on a.id = k.id where a.id = b.id");
        Exec(log, db, "update a as x set n = 1 from b inner join k on x.id = k.id where x.id = b.id");
        Exec(log, db, "update a set n = 5 from b inner join k on b.id = k.id where a.id = b.id");
        Exec(log, db, "update a set n = 6 from b, k where a.id = k.id and a.id = b.id");
        Query(log, db, "select * from a order by id");
        raw.sqlite3_close(db);
        return "";
    });

    [Fact]
    public void ColumnMetadata() => Same(log =>
    {
        sqlite3 db = Open();
        Exec(log, db, "create table t(id integer primary key, name text not null collate nocase default 'x', n numeric)");
        int rc = raw.sqlite3_table_column_metadata(db, "main", "t", "name", out string type, out string coll, out int nn, out int pk, out int ai);
        log.Append($"{rc} {type} {coll} {nn} {pk} {ai}\n");
        rc = raw.sqlite3_table_column_metadata(db, "main", "t", "id", out type, out coll, out nn, out pk, out ai);
        log.Append($"{rc} {type} {coll} {nn} {pk} {ai}\n");
        rc = raw.sqlite3_table_column_metadata(db, "main", "t", "nope", out type, out coll, out nn, out pk, out ai);
        log.Append($"{rc} {Err(db, rc)}\n");
        Query(log, db, "select id, name as nm, n+1, 5, name || 'a' from t");
        raw.sqlite3_prepare_v2(db, "select name, id from t", out sqlite3_stmt st);
        log.Append(raw.sqlite3_column_database_name(st, 0).utf8_to_string()).Append(' ').Append(raw.sqlite3_column_table_name(st, 0).utf8_to_string()).Append(' ').AppendLine(raw.sqlite3_column_origin_name(st, 0).utf8_to_string());
        raw.sqlite3_finalize(st);
        raw.sqlite3_close(db);
        return "";
    });

    [Fact]
    public void LimitsFilenamesAndOpen() => Same(log =>
    {
        sqlite3 db = Open();
        log.Append("limit ").Append(raw.sqlite3_limit(db, raw.SQLITE_LIMIT_SQL_LENGTH, -1)).Append(' ').Append(raw.sqlite3_limit(db, raw.SQLITE_LIMIT_VARIABLE_NUMBER, -1)).Append(' ').AppendLine(raw.sqlite3_limit(db, raw.SQLITE_LIMIT_COLUMN, -1).ToString());
        log.Append("file [").Append(raw.sqlite3_db_filename(db, "main").utf8_to_string()).AppendLine("]");
        log.Append("ro ").AppendLine(raw.sqlite3_db_readonly(db, "main").ToString());
        raw.sqlite3_close(db);

        int rc = raw.sqlite3_open_v2("/nonexistent-dir-u54/x.db", out sqlite3 bad, raw.SQLITE_OPEN_READWRITE, null);
        log.Append("open bad ").Append(rc).Append(' ').AppendLine(raw.sqlite3_errmsg(bad).utf8_to_string());
        raw.sqlite3_close(bad);

        string path = Path.Combine(Path.GetTempPath(), "u54-parity-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            rc = raw.sqlite3_open_v2(path, out sqlite3 f, raw.SQLITE_OPEN_READWRITE | raw.SQLITE_OPEN_CREATE, null);
            Exec(log, f, "create table t(a); insert into t values (1)");
            log.Append("file ends ").AppendLine(raw.sqlite3_db_filename(f, "main").utf8_to_string() == path ? "same" : "different");
            raw.sqlite3_close(f);
            rc = raw.sqlite3_open_v2(path, out sqlite3 ro, raw.SQLITE_OPEN_READONLY, null);
            log.Append("ro open ").AppendLine(rc.ToString());
            log.Append("ro flag ").AppendLine(raw.sqlite3_db_readonly(ro, "main").ToString());
            Exec(log, ro, "insert into t values (2)");
            Query(log, ro, "select a from t");
            raw.sqlite3_close(ro);
        }
        finally
        {
            File.Delete(path);
        }

        return "";
    });

    [Fact]
    public void BackupCopiesADatabase() => Same(log =>
    {
        sqlite3 src = Open();
        Exec(log, src, "create table t(a); insert into t values (1),(2),(3)");
        sqlite3 dst = Open();
        sqlite3_backup b = raw.sqlite3_backup_init(dst, "main", src, "main");
        log.Append("step ").Append(raw.sqlite3_backup_step(b, -1)).Append(" remaining ").Append(raw.sqlite3_backup_remaining(b)).Append(" total ").AppendLine(raw.sqlite3_backup_pagecount(b).ToString());
        log.Append("finish ").AppendLine(raw.sqlite3_backup_finish(b).ToString());
        Query(log, dst, "select a from t order by a");
        raw.sqlite3_close(src);
        raw.sqlite3_close(dst);
        return "";
    });
}
