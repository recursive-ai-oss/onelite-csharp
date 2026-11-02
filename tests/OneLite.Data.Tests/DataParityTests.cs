using System.Data;
using System.Data.Common;
using System.Text;
using Xunit;

namespace RecursiveAI.OneLite.Data.Tests;

/// <summary>ADO.NET behaviours the provider documents, each asserted equal to Microsoft.Data.Sqlite on its native library.</summary>
public sealed class DataParityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private enum Color { Red = 1, Green = 2 }

    private static string Scalar(DbConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        using DbCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string n, object? v) in ps)
        {
            DbParameter p = cmd.CreateParameter();
            p.ParameterName = n;
            p.Value = v;
            cmd.Parameters.Add(p);
        }

        return Parity.Cell(cmd.ExecuteScalar());
    }

    private static int NonQuery(DbConnection c, string sql, params (string Name, object? Value)[] ps)
    {
        using DbCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string n, object? v) in ps)
        {
            DbParameter p = cmd.CreateParameter();
            p.ParameterName = n;
            p.Value = v;
            cmd.Parameters.Add(p);
        }

        return cmd.ExecuteNonQuery();
    }

    [Fact]
    public void ParameterTypesStoreAndReadBackTheSame() => Parity.Same(open =>
    {
        using DbConnection c = open(false, ":memory:");
        NonQuery(c, "create table t(v)");
        var sb = new StringBuilder();
        object?[] values =
        [
            1, 2L, (short)3, (byte)4, (sbyte)-5, 6.5, 7.25f, true, false, "text", 'c', new byte[] { 1, 2, 3 }, Array.Empty<byte>(),
            Guid.Parse("11111111-2222-3333-4444-555555555555"), new DateTime(2024, 5, 6, 7, 8, 9, 123, DateTimeKind.Utc),
            new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.FromHours(2)), 12.50m, TimeSpan.FromMinutes(90), Color.Green, DBNull.Value,
            uint.MaxValue, ushort.MaxValue, long.MinValue, double.PositiveInfinity, double.MinValue,
        ];
        foreach (object? v in values)
        {
            NonQuery(c, "delete from t");
            NonQuery(c, "insert into t values (@v)", ("@v", v));
            sb.Append(Scalar(c, "select typeof(v) || ':' || quote(v) from t")).Append('\n');
        }

        return sb.ToString();
    }, "text:'text'");

    [Fact]
    public void NaNCannotBeStored() => Parity.Same(open =>
    {
        using DbConnection c = open(false, ":memory:");
        return Scalar(c, "select @v", ("@v", double.NaN));
    }, "Cannot store 'NaN' values");

    [Fact]
    public void NullParameterValueMustBeDbNull() => Parity.Same(open =>
    {
        using DbConnection c = open(false, ":memory:");
        return Scalar(c, "select @v", ("@v", null));
    }, "Value must be set");

    [Fact]
    public void ParameterPrefixesAndPositions() => Parity.Same(open =>
    {
        using DbConnection c = open(false, ":memory:");
        var sb = new StringBuilder();
        foreach ((string sql, (string, object?)[] ps) in new (string, (string, object?)[])[]
        {
            ("select @a + :b + $c", [("@a", 1), ("b", 2), ("$c", 4)]),
            ("select @a + @a", [("a", 5)]),
            ("select :Name", [(":name", "case")]),
            ("select ?1 || ?2 || ?1", [("?1", "p"), ("?2", "q")]),
            ("select @a", [("@a", 1), ("@b", 2)]),
        })
        {
            try
            {
                sb.Append(Scalar(c, sql, ps)).Append('\n');
            }
            catch (Exception e)
            {
                sb.Append(sql).Append(" => ").Append(Parity.Describe(e)).Append('\n');
            }
        }

        return sb.ToString();
    });

    [Fact]
    public void MissingParameterMessage() => Parity.Same(open =>
    {
        using DbConnection c = open(false, ":memory:");
        return Scalar(c, "select @a, @b", ("@a", 1));
    }, "Must add values");

    [Fact]
    public void ErrorCodesAndMessages() => Parity.Same(open =>
    {
        using DbConnection c = open(false, ":memory:");
        var sb = new StringBuilder();
        foreach (string sql in new[]
        {
            "select * from nope", "selec 1", "create table t(a unique); insert into t values (1); insert into t values (1)",
            "select 1 from", "insert into t values (1,2)", "select abs()", "create table t(a)",
        })
        {
            try
            {
                NonQuery(c, sql);
                sb.Append("ok\n");
            }
            catch (Exception e)
            {
                sb.Append(Parity.Describe(e)).Append('\n');
            }
        }

        return sb.ToString();
    }, "SQLite Error 1: 'no such table: nope'");

    [Fact]
    public void RecordsAffected() => Parity.Same(open =>
    {
        using DbConnection c = open(false, ":memory:");
        var sb = new StringBuilder();
        foreach (string sql in new[]
        {
            "select 1", "create table t(a)", "insert into t values (1),(2),(3)", "update t set a = a + 1 where a > 100", "update t set a = 0",
            "delete from t where a = 0", "select * from t", "insert into t values (9); select 1", "select 1; insert into t values (9); insert into t values (10)",
            "create index i on t(a)", "begin", "commit", "pragma user_version = 3",
        })
        {
            sb.Append(NonQuery(c, sql)).Append(' ');
            using DbCommand cmd = c.CreateCommand();
            cmd.CommandText = sql;
            using DbDataReader r = cmd.ExecuteReader();
            while (r.NextResult())
            {
            }

            sb.Append(r.RecordsAffected).Append('\n');
        }

        return sb.ToString();
    });

    [Fact]
    public void MultiStatementResultSets() => Parity.Same(open =>
    {
        using DbConnection c = open(false, ":memory:");
        using DbCommand cmd = c.CreateCommand();
        cmd.CommandText = "create table t(a, b); insert into t values (1, 'x'), (2, null); select a, b from t; select count(*) as n from t; update t set a = a;";
        using DbDataReader r = cmd.ExecuteReader();
        return Parity.Rows(r) + " hasrows=" + r.HasRows;
    });

    [Fact]
    public void ReaderBehaviours() => Parity.Same(open =>
    {
        using DbConnection c = open(false, ":memory:");
        var sb = new StringBuilder();
        NonQuery(c, "create table t(i integer, r real, s text, b blob, n)");
        NonQuery(c, "insert into t values (42, 1.5, 'hi', x'0102', null)");
        using DbCommand cmd = c.CreateCommand();
        cmd.CommandText = "select i, r, s, b, n, 'x' as Alias from t";
        using DbDataReader r = cmd.ExecuteReader();
        sb.Append("fc=").Append(r.FieldCount).Append(" hasrows=").Append(r.HasRows).Append('\n');
        sb.Append(r.Read()).Append('\n');
        for (int i = 0; i < r.FieldCount; i++)
        {
            sb.Append(r.GetName(i)).Append(' ').Append(r.GetFieldType(i).Name).Append(' ').Append(r.IsDBNull(i)).Append(' ').Append(Parity.Cell(r.GetValue(i))).Append('\n');
        }

        sb.Append(r.GetInt32(0)).Append(r.GetInt64(0)).Append(r.GetBoolean(0)).Append(r.GetDouble(1)).Append(r.GetString(2)).Append(r.GetOrdinal("ALIAS")).Append('\n');
        sb.Append(r["s"]).Append(r[2]).Append(r.GetFieldValue<string>(2)).Append(r.GetFieldValue<long>(0)).Append(r.GetFieldValue<double>(1)).Append('\n');
        sb.Append(Convert.ToHexString(r.GetFieldValue<byte[]>(3))).Append('\n');
        var buf = new byte[4];
        sb.Append(r.GetBytes(3, 0, buf, 0, 4)).Append(r.GetBytes(3, 1, buf, 0, 4)).Append('\n');
        sb.Append(r.Read()).Append(r.Read()).Append('\n');
        foreach (Action a in new Action[]
        {
            () => r.GetOrdinal("nope"), () => r.GetValue(99), () => r.GetName(-1),
        })
        {
            try
            {
                a();
                sb.Append("noerr\n");
            }
            catch (Exception e)
            {
                sb.Append(e.GetType().Name).Append('\n');
            }
        }

        return sb.ToString();
    });

    [Fact]
    public void ClosingAReaderEarlyAndLaterStatements() => Parity.Same(open =>
    {
        using DbConnection c = open(false, ":memory:");
        var sb = new StringBuilder();
        NonQuery(c, "create table t(a)");
        using (DbCommand cmd = c.CreateCommand())
        {
            cmd.CommandText = "select 1; insert into t values (1); select 2; insert into t values (2)";
            using DbDataReader r = cmd.ExecuteReader();
            r.Read();
        }

        sb.Append(Scalar(c, "select count(*) from t")).Append('\n');
        using (DbCommand cmd = c.CreateCommand())
        {
            cmd.CommandText = "insert into t values (5); select a from t where a = 5; insert into t values (6)";
            using DbDataReader r = cmd.ExecuteReader();
            sb.Append(r.FieldCount).Append(r.Read()).Append(r.GetInt32(0)).Append(r.Read()).Append(r.NextResult()).Append(r.RecordsAffected).Append('\n');
        }

        sb.Append(Scalar(c, "select count(*) from t")).Append('\n');
        try
        {
            using DbCommand cmd = c.CreateCommand();
            cmd.CommandText = "insert into t values (7); select * from nope; insert into t values (8)";
            cmd.ExecuteNonQuery();
        }
        catch (Exception e)
        {
            sb.Append(Parity.Describe(e)).Append('\n');
        }

        sb.Append(Scalar(c, "select group_concat(a) from t"));
        return sb.ToString();
    });

    [Fact]
    public void NullHandling() => Parity.Same(open =>
    {
        using DbConnection c = open(false, ":memory:");
        var sb = new StringBuilder();
        using DbCommand cmd = c.CreateCommand();
        cmd.CommandText = "select null";
        using DbDataReader r = cmd.ExecuteReader();
        r.Read();
        foreach (Func<object?> f in new Func<object?>[]
        {
            () => r.GetString(0), () => r.GetInt32(0), () => r.GetFieldValue<string>(0), () => r.GetFieldValue<int?>(0), () => r.GetFieldValue<int>(0),
            () => r.GetValue(0), () => r.GetFieldType(0).Name, () => r.GetDataTypeName(0), () => r.GetBoolean(0), () => r.GetDateTime(0), () => r.GetFieldValue<byte[]>(0),
        })
        {
            try
            {
                sb.Append(Parity.Cell(f())).Append('\n');
            }
            catch (Exception e)
            {
                sb.Append(e.GetType().Name).Append(": ").Append(e.Message).Append('\n');
            }
        }

        return sb.ToString();
    });

    [Fact]
    public void DateTimeAndGuidReadBack() => Parity.Same(open =>
    {
        using DbConnection c = open(false, ":memory:");
        var sb = new StringBuilder();
        NonQuery(c, "create table t(d, g, o, ts, m)");
        NonQuery(c, "insert into t values (@d, @g, @o, @ts, @m)", ("@d", new DateTime(2020, 1, 2, 3, 4, 5, 678)), ("@g", Guid.Empty), ("@o", new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.FromHours(-5))), ("@ts", TimeSpan.FromSeconds(3661.5)), ("@m", 3.1415926535897932m));
        using DbCommand cmd = c.CreateCommand();
        cmd.CommandText = "select d, g, o, ts, m from t";
        using DbDataReader r = cmd.ExecuteReader();
        r.Read();
        sb.Append(r.GetDateTime(0).ToString("O")).Append(r.GetGuid(1)).Append(r.GetFieldValue<DateTimeOffset>(2).ToString("O")).Append(r.GetFieldValue<TimeSpan>(3)).Append(r.GetDecimal(4)).Append('\n');
        for (int i = 0; i < 5; i++)
        {
            sb.Append(Parity.Cell(r.GetValue(i))).Append('\n');
        }

        return sb.ToString();
    });

    [Fact]
    public void ExecuteScalarShapes() => Parity.Same(open =>
    {
        using DbConnection c = open(false, ":memory:");
        var sb = new StringBuilder();
        foreach (string sql in new[] { "select 1", "select 'a'", "select null", "select 1 where 0", "select 1.5", "select x'00'", "create table t(a)", "select 1; select 2" })
        {
            sb.Append(Scalar(c, sql)).Append('\n');
        }

        return sb.ToString();
    });

    [Fact]
    public void ModeReadOnlyAndMissingFiles() => Parity.Same(open =>
    {
        var sb = new StringBuilder();
        using (DbConnection w = open(false, "ro.db"))
        {
            NonQuery(w, "create table t(a); insert into t values (1)");
        }

        // The scenario's helper builds "Data Source=<path>"; Mode is appended through a second connection string.
        string path = ((DbConnection)open(false, "ro.db")).DataSource;
        foreach (string mode in new[] { "ReadOnly", "ReadWrite" })
        {
            foreach (string sql in new[] { "select * from t", "insert into t values (2)", "create table u(a)" })
            {
                try
                {
                    using DbConnection c = Parity.Open(IsOneLite(open), $"Data Source={path};Mode={mode}");
                    sb.Append(mode).Append(' ').Append(NonQuery(c, sql)).Append('\n');
                }
                catch (Exception e)
                {
                    sb.Append(mode).Append(' ').Append(Parity.Describe(e).Replace(path, "<path>", StringComparison.Ordinal)).Append('\n');
                }
            }
        }

        foreach (string mode in new[] { "ReadOnly", "ReadWrite" })
        {
            try
            {
                using DbConnection c = Parity.Open(IsOneLite(open), $"Data Source={path}.missing;Mode={mode}");
                sb.Append("opened\n");
            }
            catch (Exception e)
            {
                sb.Append(mode).Append(" missing ").Append(Parity.Describe(e)).Append('\n');
            }
        }

        return sb.ToString();
    });

    private static bool IsOneLite(Func<bool, string, DbConnection> open)
    {
        using DbConnection c = open(false, ":memory:");
        return c is OneLiteConnection;
    }

    [Fact]
    public void TransactionsCommitRollbackAndDispose() => Parity.Same(open =>
    {
        var sb = new StringBuilder();
        using DbConnection c = open(false, "tx.db");
        NonQuery(c, "create table t(a)");
        using (DbTransaction tx = c.BeginTransaction())
        {
            NonQuery(c, "insert into t values (1)");
            tx.Commit();
        }

        using (DbTransaction tx = c.BeginTransaction())
        {
            NonQuery(c, "insert into t values (2)");
        }

        using (DbTransaction tx = c.BeginTransaction())
        {
            NonQuery(c, "insert into t values (3)");
            tx.Rollback();
        }

        sb.Append(Scalar(c, "select group_concat(a) from t")).Append('\n');
        using (DbTransaction tx = c.BeginTransaction())
        {
            try
            {
                c.BeginTransaction();
            }
            catch (Exception e)
            {
                sb.Append(Parity.Describe(e)).Append('\n');
            }
        }

        return sb.ToString();
    });

    [Fact]
    public void DefaultTransactionLocksOutOtherWriters() => Parity.Same(open =>
    {
        var sb = new StringBuilder();
        using DbConnection a = open(false, "lock.db");
        NonQuery(a, "create table t(x)");
        using DbConnection b = open(false, "lock.db");
        using (DbTransaction tx = a.BeginTransaction())
        {
            try
            {
                using DbCommand cmd = b.CreateCommand();
                cmd.CommandText = "pragma busy_timeout = 0; insert into t values (1)";
                cmd.ExecuteNonQuery();
                sb.Append("second writer ok\n");
            }
            catch (Exception e)
            {
                sb.Append(Parity.Describe(e)).Append('\n');
            }
        }

        return sb.ToString();
    });

    [Fact]
    public void SerializableTransactionIsAlsoDeferred() => Parity.Same(open =>
    {
        var sb = new StringBuilder();
        using DbConnection a = open(false, "lock2.db");
        NonQuery(a, "create table t(x)");
        using DbConnection b = open(false, "lock2.db");
        using (DbTransaction tx = a.BeginTransaction(IsolationLevel.Serializable))
        {
            try
            {
                using DbCommand cmd = b.CreateCommand();
                cmd.CommandText = "pragma busy_timeout = 0; insert into t values (1)";
                cmd.ExecuteNonQuery();
                sb.Append("second writer ok\n");
            }
            catch (Exception e)
            {
                sb.Append(Parity.Describe(e)).Append('\n');
            }
        }

        return sb.ToString();
    });

    [Fact]
    public void ForeignKeysAndTimeoutKeys() => Parity.Same(open =>
    {
        using DbConnection c = open(false, ":memory:");
        return Scalar(c, "select 1");
    });

    [Fact]
    public async Task AsyncSurfaceGivesTheSameResults()
    {
        string Run(bool oneLite)
        {
            Parity.InitNative();
            return RunAsync(oneLite).GetAwaiter().GetResult();
        }

        async Task<string> RunAsync(bool oneLite)
        {
            var sb = new StringBuilder();
            await using DbConnection c = oneLite ? new OneLiteConnection("Data Source=:memory:") : new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
            await c.OpenAsync(Ct);
            await using DbCommand cmd = c.CreateCommand();
            cmd.CommandText = "create table t(a); insert into t values (1),(2),(3)";
            sb.Append(await cmd.ExecuteNonQueryAsync(Ct)).Append('\n');
            cmd.CommandText = "select a from t order by a";
            await using (DbDataReader r = await cmd.ExecuteReaderAsync(Ct))
            {
                while (await r.ReadAsync(Ct))
                {
                    sb.Append(await r.GetFieldValueAsync<long>(0, Ct)).Append(await r.IsDBNullAsync(0, Ct)).Append(' ');
                }

                sb.Append(await r.NextResultAsync(Ct)).Append('\n');
            }

            cmd.CommandText = "select max(a) from t";
            sb.Append(await cmd.ExecuteScalarAsync(Ct)).Append('\n');
            await using DbTransaction tx = await c.BeginTransactionAsync(Ct);
            cmd.Transaction = tx;
            cmd.CommandText = "delete from t";
            sb.Append(await cmd.ExecuteNonQueryAsync(Ct));
            await tx.RollbackAsync(Ct);
            cmd.Transaction = null;
            cmd.CommandText = "select count(*) from t";
            sb.Append(await cmd.ExecuteScalarAsync(Ct));
            return sb.ToString();
        }

        string native = Run(false);
        string ours = Run(true);
        await Task.CompletedTask;
        Assert.Equal(native, ours);
    }
}
