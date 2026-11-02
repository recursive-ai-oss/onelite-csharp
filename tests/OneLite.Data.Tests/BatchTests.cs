using System.Data.Common;
using Xunit;

namespace RecursiveAI.OneLite.Data.Tests;

public sealed class BatchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BatchRunsAtomicallyWithParametersAndSourcesFromTheDataSource()
    {
        await using var source = new OneLiteDataSource("Data Source=file:batch?mode=memory&cache=shared;Mode=Memory;Cache=Shared");
        await using DbConnection keep = await source.OpenConnectionAsync(Ct);
        await using (DbCommand setup = keep.CreateCommand())
        {
            setup.CommandText = "create table t(a integer primary key, b text not null)";
            await setup.ExecuteNonQueryAsync(Ct);
        }

        // A second connection of the same source sees the shared in-memory database.
        await using DbConnection other = await source.OpenConnectionAsync(Ct);
        await using (DbBatch batch = other.CreateBatch())
        {
            var one = new OneLiteBatchCommand("insert into t values (@a, @b)");
            one.Parameters.AddWithValue("@a", 1);
            one.Parameters.AddWithValue("@b", "x");
            batch.BatchCommands.Add(one);
            batch.BatchCommands.Add(new OneLiteBatchCommand("insert into t values (2, 'y')"));
            Assert.Equal(2, await batch.ExecuteNonQueryAsync(Ct));
        }

        await using (DbBatch failing = other.CreateBatch())
        {
            failing.BatchCommands.Add(new OneLiteBatchCommand("insert into t values (3, 'z')"));
            failing.BatchCommands.Add(new OneLiteBatchCommand("insert into t values (4, null)"));
            await Assert.ThrowsAsync<OneLiteDataException>(() => failing.ExecuteNonQueryAsync(Ct));
        }

        await using DbCommand count = keep.CreateCommand();
        count.CommandText = "select count(*) from t";
        Assert.Equal(2L, await count.ExecuteScalarAsync(Ct));

        await using DbBatch reads = keep.CreateBatch();
        reads.BatchCommands.Add(new OneLiteBatchCommand("select a from t order by a"));
        reads.BatchCommands.Add(new OneLiteBatchCommand("select b from t order by a"));
        await using DbDataReader r = await reads.ExecuteReaderAsync(Ct);
        Assert.True(await r.ReadAsync(Ct));
        Assert.Equal(1L, r.GetInt64(0));
        Assert.True(await r.ReadAsync(Ct));
        Assert.False(await r.ReadAsync(Ct));
        Assert.True(await r.NextResultAsync(Ct));
        Assert.True(await r.ReadAsync(Ct));
        Assert.Equal("x", r.GetString(0));
    }

    [Fact]
    public void ConnectionStringBuilderRoundTrips()
    {
        var b = new OneLiteConnectionStringBuilder { DataSource = "a.db", Mode = OneLiteOpenMode.ReadWrite, Cache = OneLiteCacheMode.Shared, Password = "pw", ForeignKeys = true, DefaultTimeout = 7 };
        var parsed = new OneLiteConnectionStringBuilder(b.ConnectionString);
        Assert.Equal("a.db", parsed.DataSource);
        Assert.Equal(OneLiteOpenMode.ReadWrite, parsed.Mode);
        Assert.Equal(OneLiteCacheMode.Shared, parsed.Cache);
        Assert.Equal("pw", parsed.Password);
        Assert.True(parsed.ForeignKeys);
        Assert.Equal(7, parsed.DefaultTimeout);
        Assert.Equal("Filename-form", new OneLiteConnectionStringBuilder("Filename=Filename-form").DataSource);
        Assert.Equal(30, new OneLiteConnectionStringBuilder("Data Source=x").DefaultTimeout);
    }

    [Fact]
    public void OpenFailuresAreDbExceptionsWithCodes()
    {
        using var c = new OneLiteConnection("Data Source=/nonexistent-dir/x.db;Mode=ReadOnly");
        OneLiteDataException e = Assert.Throws<OneLiteDataException>(c.Open);
        Assert.Equal(ResultCode.CantOpen, e.ResultCode);
        Assert.Equal("SQLite Error 14: 'unable to open database file'.", e.Message);
        Assert.IsAssignableFrom<DbException>(e);
        using var none = new OneLiteConnection();
        Assert.Throws<InvalidOperationException>(none.Open);
    }

    [Fact]
    public void PasswordOpensAnEncryptedDatabase()
    {
        string dir = Path.Combine(Path.GetTempPath(), "onelite-pw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "enc.db");
            using (var c = new OneLiteConnection($"Data Source={path};Password=secret"))
            {
                c.Open();
                using DbCommand cmd = c.CreateCommand();
                cmd.CommandText = "create table t(a); insert into t values (42)";
                cmd.ExecuteNonQuery();
            }

            Assert.NotEqual("SQLite format 3\0", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path), 0, 16));
            using (var bad = new OneLiteConnection($"Data Source={path};Password=wrong"))
            {
                bad.Open();
                using DbCommand cmd = bad.CreateCommand();
                cmd.CommandText = "select a from t";
                Assert.Throws<OneLiteDataException>(() => cmd.ExecuteScalar());
            }

            using var good = new OneLiteConnection($"Data Source={path};Password=secret");
            good.Open();
            using DbCommand q = good.CreateCommand();
            q.CommandText = "select a from t";
            Assert.Equal(42L, q.ExecuteScalar());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AQuotedHexPasswordIsARawKey()
    {
        string dir = Path.Combine(Path.GetTempPath(), "onelite-rawkey-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "raw.db");
            string key = "x'" + new string('a', 64) + "'";
            using (var c = new OneLiteConnection($"Data Source={path};Password=\"{key}\""))
            {
                c.Open();
                using DbCommand cmd = c.CreateCommand();
                cmd.CommandText = "create table t(a); insert into t values (7)";
                cmd.ExecuteNonQuery();
            }

            // the same 64 digits as a passphrase are a different key
            using (var other = new OneLiteConnection($"Data Source={path};Password={new string('a', 64)}"))
            {
                other.Open();
                using DbCommand cmd = other.CreateCommand();
                cmd.CommandText = "select a from t";
                Assert.Throws<OneLiteDataException>(() => cmd.ExecuteScalar());
            }

            using var good = new OneLiteConnection($"Data Source={path};Password=\"{key}\"");
            good.Open();
            using DbCommand q = good.CreateCommand();
            q.CommandText = "select a from t";
            Assert.Equal(7L, q.ExecuteScalar());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
