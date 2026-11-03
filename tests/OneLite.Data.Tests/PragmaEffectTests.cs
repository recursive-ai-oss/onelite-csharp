using System.Data.Common;
using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Oracle;
using Xunit;

namespace RecursiveAI.OneLite.Data.Tests;

/// <summary>
/// Statements with a side effect (side-effecting pragmas, VACUUM, ANALYZE, ATTACH, savepoints, ...) must have the same effect however they are
/// run: stepped once and dropped, read for one row, not read at all, or run to the end, on the engine and on ADO.NET, sync and async.
/// Each case runs on every path and compares what the statement left behind (the live connection, the reopened file and the files on disk)
/// with the path that runs it to the end, and with the pinned sqlite3 shell when it is installed.
/// </summary>
public sealed class PragmaEffectTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-pragmaeffect-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public PragmaEffectTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private const string Rows = "create table t(a integer primary key, b text)";

    // Name, setup statements, the statement under test, statements run after it, queries run live (same connection) and after reopening.
    private static readonly (string Name, string[] Setup, string Stmt, string[] Then, string[] Live, string[] Reopen)[] s_cases =
    [
        ("user_version", [], "pragma user_version=7", [], ["pragma user_version"], ["pragma user_version"]),
        ("application_id", [], "pragma application_id=5", [], [], ["pragma application_id"]),
        ("schema_version", [], "pragma schema_version=100", [], ["pragma schema_version"], ["pragma schema_version"]),
        ("page_size", [], "pragma page_size=8192", ["create table z(a)"], [], ["pragma page_size"]),
        ("auto_vacuum", [], "pragma auto_vacuum=2", ["create table z(a)"], [], ["pragma auto_vacuum"]),
        ("journal_mode_wal", [Rows, "insert into t(b) values ('x')"], "pragma journal_mode=wal", ["insert into t(b) values ('y')"], ["pragma journal_mode"], ["pragma journal_mode", "select count(*) from t"]),
        ("journal_mode_delete", ["pragma journal_mode=wal", Rows, "insert into t(b) values ('x')"], "pragma journal_mode=delete", [], ["pragma journal_mode"], ["pragma journal_mode", "select count(*) from t"]),
        ("journal_mode_truncate", [Rows], "pragma journal_mode=truncate", ["insert into t(b) values ('y')"], ["pragma journal_mode"], ["pragma journal_mode"]),
        ("wal_checkpoint", ["pragma journal_mode=wal", "pragma wal_autocheckpoint=0", Rows, "insert into t(b) values ('x')", "insert into t(b) values ('y')"], "pragma wal_checkpoint(truncate)", [], [], ["select count(*) from t"]),
        ("incremental_vacuum", ["pragma auto_vacuum=2", Rows, "insert into t(b) select hex(zeroblob(3000)) from (select 1 union all select 2 union all select 3 union all select 4)", "delete from t"], "pragma incremental_vacuum(2)", [], ["pragma freelist_count", "pragma page_count"], ["pragma freelist_count", "pragma page_count"]),
        ("optimize", [Rows, "create index i on t(b)", "insert into t(b) values ('x'),('y')"], "pragma optimize", [], [], ["select count(*) from sqlite_master where name = 'sqlite_stat1'"]),
        ("cache_size", [], "pragma cache_size=-123", [], ["pragma cache_size"], []),
        ("foreign_keys", [], "pragma foreign_keys=on", [], ["pragma foreign_keys"], []),
        ("synchronous", [], "pragma synchronous=1", [], ["pragma synchronous"], []),
        ("busy_timeout", [], "pragma busy_timeout=1234", [], ["pragma busy_timeout"], []),
        ("locking_mode", [], "pragma locking_mode=exclusive", [], ["pragma locking_mode"], []),
        ("secure_delete", [], "pragma secure_delete=on", [], ["pragma secure_delete"], []),
        ("trusted_schema", [], "pragma trusted_schema=on", [], ["pragma trusted_schema"], []),
        ("temp_store", [], "pragma temp_store=2", [], ["pragma temp_store"], []),
        ("recursive_triggers", [], "pragma recursive_triggers=on", [], ["pragma recursive_triggers"], []),
        ("integrity_check", [Rows, "insert into t(b) values ('x')"], "pragma integrity_check", [], [], ["select count(*) from t"]),
        ("quick_check", [Rows, "insert into t(b) values ('x')"], "pragma quick_check", [], [], ["select count(*) from t"]),
        ("vacuum", [Rows, "insert into t(b) select hex(zeroblob(3000)) from (select 1 union all select 2 union all select 3 union all select 4)", "delete from t"], "vacuum", [], ["pragma page_count"], ["pragma page_count", "pragma freelist_count"]),
        ("analyze", [Rows, "create index i on t(b)", "insert into t(b) values ('x'),('y')"], "analyze", [], ["select count(*) from sqlite_stat1"], ["select count(*) from sqlite_stat1"]),
        ("reindex", [Rows, "create index i on t(b)", "insert into t(b) values ('x')"], "reindex", [], [], ["pragma integrity_check"]),
        ("savepoint_release", [Rows, "savepoint s", "insert into t(b) values ('x')"], "release s", [], ["select count(*) from t"], ["select count(*) from t"]),
        ("savepoint_rollback", [Rows, "insert into t(b) values ('keep')", "savepoint s", "insert into t(b) values ('x')"], "rollback to s", ["release s"], ["select count(*) from t"], ["select count(*) from t"]),
        ("savepoint_open", [Rows], "savepoint s", ["insert into t(b) values ('x')", "release s"], ["select count(*) from t"], ["select count(*) from t"]),
        ("begin", [Rows], "begin", ["insert into t(b) values ('x')", "commit"], ["select count(*) from t"], ["select count(*) from t"]),
        ("commit", [Rows, "begin", "insert into t(b) values ('x')"], "commit", [], ["select count(*) from t"], ["select count(*) from t"]),
        // PRAGMA key on a new file makes it an encrypted database; the plain sqlite3 shell has no such pragma, so these compare the paths only.
        ("key", [], "pragma key='k'", ["create table z(a)", "insert into z values (1)"], ["select count(*) from z"], ["pragma key='k'", "select count(*) from z"]),
        ("hexkey", [], "pragma hexkey='000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f'", ["create table z(a)", "insert into z values (1)"], ["select count(*) from z"], ["pragma hexkey='000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f'", "select count(*) from z"]),
        ("cipher_page_size", [], "pragma key='k'", ["pragma cipher_page_size=8192", "create table z(a)"], ["pragma cipher_page_size"], ["pragma key='k'", "pragma cipher_page_size=8192", "pragma cipher_page_size", "select count(*) from z"]),
        ("detach", [Rows, "attach ':memory:' as m"], "detach m", [], ["select count(*) from pragma_database_list"], []),
    ];

    public static TheoryData<string> CaseNames()
    {
        var d = new TheoryData<string>();
        foreach (var c in s_cases)
        {
            d.Add(c.Name);
        }

        return d;
    }

    private static readonly string[] s_paths =
    [
        "engine-step-done", "engine-step-once-reset", "engine-step-once-dispose", "engine-stepasync-once", "engine-stepasync-done",
        "ado-nonquery", "ado-scalar", "ado-reader-one", "ado-reader-none", "ado-reader-all", "ado-nonquery-async", "ado-scalar-async", "ado-reader-one-async",
    ];

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task EveryPathLeavesTheSameEffect(string name)
    {
        var c = s_cases.Single(x => x.Name == name);
        var seen = new Dictionary<string, string>();
        foreach (string path in s_paths)
        {
            seen[path] = await Observe(c, path);
        }

        string baseline = seen["engine-step-done"];
        foreach ((string path, string obs) in seen)
        {
            Assert.True(baseline == obs, $"{name}: path {path} left\n{obs}\nbut running to the end left\n{baseline}");
        }

        // A statement stepped once and then reset or disposed does all of an incremental vacuum's work on that first step (the reference frees one page per
        // step), so the paths agree with each other and with the shell when the statement runs to its end.
        if (File.Exists(OraclePaths.Cli) && name is not ("key" or "hexkey" or "cipher_page_size"))
        {
            string oracle = ObserveOracle(c);
            Assert.Equal(oracle.Replace("\n", "|"), baseline.Replace("\n", "|"));
        }
    }

    private async Task<string> Observe((string Name, string[] Setup, string Stmt, string[] Then, string[] Live, string[] Reopen) c, string path)
    {
        string file = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");
        var sb = new StringBuilder();
        bool engine = path.StartsWith("engine-", StringComparison.Ordinal);
        using (IDriver d = engine ? new EngineDriver(file) : new AdoDriver(file))
        {
            foreach (string s in c.Setup)
            {
                d.Run(s);
            }

            try
            {
                await d.RunVia(c.Stmt, path);
                sb.Append("stmt: ok\n");
            }
            catch (Exception)
            {
                sb.Append("stmt: error\n");
            }

            foreach (string s in c.Then)
            {
                d.Run(s);
            }

            foreach (string q in c.Live)
            {
                sb.Append("live ").Append(q).Append(": ").Append(string.Join(";", d.Run(q))).Append('\n');
            }
        }

        using (IDriver d = engine ? new EngineDriver(file) : new AdoDriver(file))
        {
            foreach (string q in c.Reopen)
            {
                sb.Append("reopen ").Append(q).Append(": ").Append(string.Join(";", d.Run(q))).Append('\n');
            }
        }

        return sb.Append(Files(file)).ToString();
    }

    private static string Files(string file) =>
        $"files: db={FileLength(file)} wal={FileLength(file + "-wal")} journal={FileLength(file + "-journal")}\n";

    private static string FileLength(string f) => File.Exists(f) ? "yes" : "no";

    private string ObserveOracle((string Name, string[] Setup, string Stmt, string[] Then, string[] Live, string[] Reopen) c)
    {
        string file = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");
        var sb = new StringBuilder();
        var script = new StringBuilder();
        foreach (string s in c.Setup)
        {
            script.Append(s).Append(";\n");
        }

        script.Append(c.Stmt).Append(";\n");
        foreach (string s in c.Then)
        {
            script.Append(s).Append(";\n");
        }

        script.Append(".print stmt:live\n");
        foreach (string q in c.Live)
        {
            script.Append(q).Append(";\n");
        }

        Proc.Result r = SqliteCli.Run(file, script.ToString(), "-csv");
        string[] lines = r.Stdout.Replace("\r", "").Split((char)10, StringSplitOptions.RemoveEmptyEntries);
        int cut = Array.IndexOf(lines, "stmt:live");
        sb.Append("stmt: ").Append(r.Stderr.Contains("rror", StringComparison.Ordinal) && r.Stderr.Contains(c.Stmt.Split(' ')[0], StringComparison.OrdinalIgnoreCase) ? "error\n" : "ok\n");
        int idx = cut + 1;
        foreach (string q in c.Live)
        {
            // Each live query yields rows up to the next query's rows; the case lists give one result row per query except where noted.
            sb.Append("live ").Append(q).Append(": ").Append(lines[idx++]).Append('\n');
        }

        foreach (string q in c.Reopen)
        {
            Proc.Result rr = SqliteCli.Run(file, q + ";\n", "-csv");
            sb.Append("reopen ").Append(q).Append(": ").Append(string.Join(";", rr.Stdout.Replace("\r", "").Split((char)10, StringSplitOptions.RemoveEmptyEntries))).Append('\n');
        }

        return sb.Append(Files(file)).ToString();
    }

    private interface IDriver : IDisposable
    {
        List<string> Run(string sql);

        ValueTask RunVia(string sql, string path);
    }

    private static string Cell(string? s) => s ?? string.Empty;

    private sealed class EngineDriver : IDriver
    {
        private readonly Database _db;
        private readonly Connection _c;

        public EngineDriver(string file)
        {
            _db = Database.OpenDefault(file, new DatabaseOptions());
            _c = _db.Connect();
        }

        public void Dispose()
        {
            _c.Dispose();
            _db.Dispose();
        }

        public List<string> Run(string sql)
        {
            var rows = new List<string>();
            using Statement st = _c.Prepare(sql);
            while (st.Step())
            {
                rows.Add(string.Join(",", Enumerable.Range(0, st.ColumnCount).Select(i => Cell(st.GetString(i)))));
            }

            return rows;
        }

        public async ValueTask RunVia(string sql, string path)
        {
            switch (path)
            {
                case "engine-step-done":
                    Run(sql);
                    break;
                case "engine-step-once-reset":
                {
                    using Statement st = _c.Prepare(sql);
                    st.Step();
                    st.Reset();
                    break;
                }

                case "engine-step-once-dispose":
                {
                    using (Statement st = _c.Prepare(sql))
                    {
                        st.Step();
                    }

                    break;
                }

                case "engine-stepasync-once":
                {
                    using (Statement st = _c.Prepare(sql))
                    {
                        await st.StepAsync(Ct);
                    }

                    break;
                }

                default:
                {
                    using Statement st = _c.Prepare(sql);
                    while (await st.StepAsync(Ct))
                    {
                    }

                    break;
                }
            }
        }
    }

    private sealed class AdoDriver : IDriver
    {
        private readonly OneLiteConnection _c;

        public AdoDriver(string file)
        {
            _c = new OneLiteConnection("Data Source=" + file);
            _c.Open();
        }

        public void Dispose() => _c.Dispose();

        public List<string> Run(string sql)
        {
            var rows = new List<string>();
            using DbCommand cmd = _c.CreateCommand();
            cmd.CommandText = sql;
            using DbDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(string.Join(",", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? string.Empty : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
            }

            return rows;
        }

        public async ValueTask RunVia(string sql, string path)
        {
            using DbCommand cmd = _c.CreateCommand();
            cmd.CommandText = sql;
            switch (path)
            {
                case "ado-nonquery": cmd.ExecuteNonQuery(); break;
                case "ado-scalar": cmd.ExecuteScalar(); break;
                case "ado-reader-one":
                    using (DbDataReader r = cmd.ExecuteReader())
                    {
                        r.Read();
                    }

                    break;
                case "ado-reader-none":
                    using (cmd.ExecuteReader())
                    {
                    }

                    break;
                case "ado-reader-all":
                    using (DbDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                        }
                    }

                    break;
                case "ado-nonquery-async": await cmd.ExecuteNonQueryAsync(Ct); break;
                case "ado-scalar-async": await cmd.ExecuteScalarAsync(Ct); break;
                default:
                    await using (DbDataReader r = await cmd.ExecuteReaderAsync(Ct))
                    {
                        await r.ReadAsync(Ct);
                    }

                    break;
            }
        }
    }
}
