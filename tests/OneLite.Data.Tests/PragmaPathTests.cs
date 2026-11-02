using System.Data.Common;
using RecursiveAI.OneLite.Storage.Crypto;
using Xunit;

namespace RecursiveAI.OneLite.Data.Tests;

/// <summary>
/// A pragma with an effect must have it however the statement is driven: stepped once and dropped, read for one row,
/// not read at all, or run to the end, sync or async, on the engine, ADO.NET and the happy path. PRAGMA rekey is the one
/// where a silent no-op is a security failure, so each path rekeys, closes, and then the old key must fail and the new one read.
/// </summary>
public sealed class PragmaPathTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-pragmapath-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public PragmaPathTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    public static TheoryData<string, bool, bool> Cases()
    {
        var d = new TheoryData<string, bool, bool>();
        foreach (string path in new[]
        {
            "engine-step-once-reset", "engine-step-once-dispose", "engine-step-done", "engine-stepasync-once", "engine-stepasync-done",
            "data-nonquery", "data-scalar", "data-reader-one", "data-reader-none", "data-reader-all",
            "data-nonquery-async", "data-scalar-async", "data-reader-one-async", "data-reader-none-async",
            "happy-execute", "happy-scalar", "happy-query", "happy-executeasync", "happy-scalarasync", "happy-queryasync", "happy-script",
        })
        {
            foreach (bool wal in new[] { false, true })
            {
                foreach (bool hex in new[] { false, true })
                {
                    d.Add(path, wal, hex);
                }
            }
        }

        return d;
    }

    private const string NewHex = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";

    private string MakeDb(bool wal)
    {
        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");
        using var db = Database.OpenDefault(path, new DatabaseOptions { Key = CipherKey.FromPassphrase("old") });
        db.ExecuteScript((wal ? "PRAGMA journal_mode=wal;" : string.Empty) + "CREATE TABLE t(a); INSERT INTO t VALUES (42);");
        return path;
    }

    private static void ExpectReadable(string path, CipherKey key)
    {
        using var db = Database.OpenDefault(path, new DatabaseOptions { Key = key });
        Assert.Equal(42L, db.Scalar<long>($"SELECT a FROM t"));
    }

    private static void ExpectRefused(string path, CipherKey key)
    {
        Assert.ThrowsAny<Exception>(() =>
        {
            using var db = Database.OpenDefault(path, new DatabaseOptions { Key = key });
            db.Scalar<long>($"SELECT a FROM t");
        });
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RekeyTakesEffectOnEveryPath(string via, bool wal, bool hex)
    {
        string path = MakeDb(wal);
        string pragma = hex ? $"PRAGMA hexrekey='000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f'" : "PRAGMA rekey='new'";
        await Run(via, path, pragma);

        ExpectRefused(path, CipherKey.FromPassphrase("old"));
        ExpectReadable(path, hex ? CipherKey.Parse("x'" + NewHex + "'") : CipherKey.FromPassphrase("new"));
    }

    private static async Task Run(string via, string path, string sql)
    {
        var opts = new DatabaseOptions { Key = CipherKey.FromPassphrase("old") };
        string cs = $"Data Source={path};Password=old";
        switch (via)
        {
            case "engine-step-once-reset":
            {
                using var db = Database.OpenDefault(path, opts);
                using Connection c = db.Connect();
                using Statement st = c.Prepare(sql);
                Assert.True(st.Step());
                Assert.Equal("ok", st.GetString(0));
                st.Reset();
                break;
            }

            case "engine-step-once-dispose":
            {
                using var db = Database.OpenDefault(path, opts);
                using Connection c = db.Connect();
                using (Statement st = c.Prepare(sql))
                {
                    Assert.True(st.Step());
                }

                break;
            }

            case "engine-step-done":
            {
                using var db = Database.OpenDefault(path, opts);
                using Connection c = db.Connect();
                using Statement st = c.Prepare(sql);
                while (st.Step())
                {
                }

                break;
            }

            case "engine-stepasync-once":
            {
                using var db = Database.OpenDefault(path, opts);
                using Connection c = db.Connect();
                using (Statement st = c.Prepare(sql))
                {
                    Assert.True(await st.StepAsync(Ct));
                }

                break;
            }

            case "engine-stepasync-done":
            {
                using var db = Database.OpenDefault(path, opts);
                using Connection c = db.Connect();
                using Statement st = c.Prepare(sql);
                while (await st.StepAsync(Ct))
                {
                }

                break;
            }
        }

        if (via.StartsWith("data-", StringComparison.Ordinal))
        {
            await using var conn = new OneLiteConnection(cs);
            await conn.OpenAsync(Ct);
            await using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            switch (via)
            {
                case "data-nonquery": cmd.ExecuteNonQuery(); break;
                case "data-scalar": Assert.Equal("ok", cmd.ExecuteScalar()); break;
                case "data-reader-one":
                    using (DbDataReader r = cmd.ExecuteReader())
                    {
                        Assert.True(r.Read());
                    }

                    break;
                case "data-reader-none":
                    using (cmd.ExecuteReader())
                    {
                    }

                    break;
                case "data-reader-all":
                    using (DbDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                        }
                    }

                    break;
                case "data-nonquery-async": await cmd.ExecuteNonQueryAsync(Ct); break;
                case "data-scalar-async": Assert.Equal("ok", await cmd.ExecuteScalarAsync(Ct)); break;
                case "data-reader-one-async":
                    await using (DbDataReader r = await cmd.ExecuteReaderAsync(Ct))
                    {
                        Assert.True(await r.ReadAsync(Ct));
                    }

                    break;
                case "data-reader-none-async":
                    await using (await cmd.ExecuteReaderAsync(Ct))
                    {
                    }

                    break;
            }
        }
        else if (via.StartsWith("happy-", StringComparison.Ordinal) )
        {
            bool hex = sql.Contains("hex", StringComparison.Ordinal);
            using var db = Database.OpenDefault(path, opts);
            switch (via)
            {
                case "happy-execute":
                    if (hex) { db.Execute($"PRAGMA hexrekey='000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f'"); } else { db.Execute($"PRAGMA rekey='new'"); }
                    break;
                case "happy-scalar":
                    Assert.Equal("ok", hex ? db.Scalar<string>($"PRAGMA hexrekey='000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f'") : db.Scalar<string>($"PRAGMA rekey='new'"));
                    break;
                case "happy-query":
                    Assert.Single(hex ? db.Query<string>($"PRAGMA hexrekey='000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f'") : db.Query<string>($"PRAGMA rekey='new'"));
                    break;
                case "happy-executeasync":
                    await (hex ? db.ExecuteAsync($"PRAGMA hexrekey='000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f'", Ct) : db.ExecuteAsync($"PRAGMA rekey='new'", Ct));
                    break;
                case "happy-scalarasync":
                    Assert.Equal("ok", hex ? await db.ScalarAsync<string>($"PRAGMA hexrekey='000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f'", Ct) : await db.ScalarAsync<string>($"PRAGMA rekey='new'", Ct));
                    break;
                case "happy-queryasync":
                    Assert.Single(hex ? await db.QueryAsync<string>($"PRAGMA hexrekey='000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f'", Ct) : await db.QueryAsync<string>($"PRAGMA rekey='new'", Ct));
                    break;
                case "happy-script":
                    db.ExecuteScript(sql);
                    break;
            }
        }
    }

    [Fact]
    public void RekeyAnswersOkAfterTheKeyChanged()
    {
        string path = MakeDb(false);
        using (var conn = new OneLiteConnection($"Data Source={path};Password=old"))
        {
            conn.Open();
            using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA rekey='new'";
            Assert.Equal("ok", cmd.ExecuteScalar());
        }

        ExpectRefused(path, CipherKey.FromPassphrase("old"));
        ExpectReadable(path, CipherKey.FromPassphrase("new"));
    }
}
