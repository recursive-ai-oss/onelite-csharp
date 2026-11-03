using Microsoft.Data.Sqlite;
using RecursiveAI.OneLite.SQLitePCLRaw;

namespace RecursiveAI.OneLite.SQLitePCLRaw.Tests;

// These tests switch the process-wide SQLitePCLRaw provider, so they must not run in parallel with one another.
[Collection("provider")]
public class MdsSmokeTests
{
    public MdsSmokeTests() => OneLiteBatteries.Init();

    [Fact]
    public void RunsOnOneLite()
    {
        using var c = new SqliteConnection("Data Source=:memory:");
        c.Open();
        Assert.Equal("OneLite (managed)", SQLitePCL.raw.GetNativeLibraryName());
        Assert.Equal("3.53.4", c.ServerVersion);
    }

    [Fact]
    public void ForeignKeysDefaultOnlyInTheAdapter()
    {
        // The core keeps SQLite's default (off); the adapter matches the bundled native library (on) unless told otherwise.
        Assert.False(new DatabaseOptions().ForeignKeys);
        static long Pragma()
        {
            using var c = new SqliteConnection("Data Source=:memory:");
            c.Open();
            using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = "pragma foreign_keys";
            return (long)cmd.ExecuteScalar()!;
        }

        Assert.Equal(1, Pragma());
        try
        {
            OneLiteBatteries.ForeignKeysByDefault = false;
            Assert.Equal(0, Pragma());
        }
        finally
        {
            OneLiteBatteries.ForeignKeysByDefault = true;
        }
    }

    [Fact]
    public void TableValuedPragmasWithParameters()
    {
        using var c = new SqliteConnection("Data Source=:memory:");
        c.Open();
        void Run(string sql, params (string, object)[] ps)
        {
            using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read()) { for (int i = 0; i < r.FieldCount; i++) { Assert.False(r.IsDBNull(i), sql + " column " + r.GetName(i)); } }
        }

        Run("create table p(id integer primary key, a text, b int unique); create index ix on p(a desc)");
        Run("SELECT \"name\" FROM pragma_index_list(@table) WHERE \"origin\" = 'u' ORDER BY \"seq\"", ("@table", "p"));
        Run("SELECT \"name\", \"unique\" FROM pragma_index_list(@table) WHERE \"origin\" = 'c' AND instr(\"name\", 'sqlite_') <> 1 ORDER BY \"seq\"", ("@table", "p"));
        Run("SELECT \"name\", \"desc\" FROM pragma_index_xinfo(@index) WHERE key = 1 ORDER BY \"seqno\"", ("@index", "ix"));
    }

    [Fact]
    public void SelectOne()
    {
        using var c = new SqliteConnection("Data Source=:memory:");
        c.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "select 1 + 1, 'a' || 'b'";
        using SqliteDataReader r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(2L, r.GetInt64(0));
        Assert.Equal("ab", r.GetString(1));
        Assert.False(r.Read());
    }

    [Fact]
    public void TableRoundTrip()
    {
        string path = Path.Combine(Path.GetTempPath(), "u54-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var c = new SqliteConnection("Data Source=" + path))
            {
                c.Open();
                using SqliteCommand cmd = c.CreateCommand();
                cmd.CommandText = "create table t(id integer primary key, name text, v real, b blob); insert into t(name, v, b) values ($n, $v, $b)";
                cmd.Parameters.AddWithValue("$n", "héllo");
                cmd.Parameters.AddWithValue("$v", 2.5);
                cmd.Parameters.AddWithValue("$b", new byte[] { 1, 2, 3 });
                Assert.Equal(1, cmd.ExecuteNonQuery());
            }

            using (var c = new SqliteConnection("Data Source=" + path))
            {
                c.Open();
                using SqliteCommand cmd = c.CreateCommand();
                cmd.CommandText = "select name, v, b from t";
                using SqliteDataReader r = cmd.ExecuteReader();
                Assert.True(r.Read());
                Assert.Equal("héllo", r.GetString(0));
                Assert.Equal(2.5, r.GetDouble(1));
                Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])r.GetValue(2));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public void SharedMemoryAcrossConnections()
    {
        const string cs = "Data Source=u54serialized;Mode=Memory;Cache=Shared";
        using var a = new SqliteConnection(cs);
        using var b = new SqliteConnection(cs);
        a.Open();
        b.Open();
        using (SqliteCommand cmd = a.CreateCommand())
        {
            cmd.CommandText = "create table Data(Value); insert into Data values (5)";
            cmd.ExecuteNonQuery();
        }

        using SqliteCommand q = b.CreateCommand();
        q.CommandText = "select Value from Data";
        Assert.Equal(5L, q.ExecuteScalar());
    }
}
