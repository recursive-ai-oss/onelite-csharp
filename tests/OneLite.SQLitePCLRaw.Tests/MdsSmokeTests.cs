using Microsoft.Data.Sqlite;
using RecursiveAI.OneLite.SQLitePCLRaw;

namespace RecursiveAI.OneLite.SQLitePCLRaw.Tests;

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
}
