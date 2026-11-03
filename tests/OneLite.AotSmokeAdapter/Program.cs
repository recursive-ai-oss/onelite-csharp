using Microsoft.Data.Sqlite;
using RecursiveAI.OneLite.SQLitePCLRaw;

// Microsoft.Data.Sqlite on OneLite, published with NativeAOT: parameters, transactions, a user function, an aggregate, a collation,
// hooks and incremental blob I/O all go through the adapter.
OneLiteBatteries.Init();

using var connection = new SqliteConnection("Data Source=:memory:");
connection.Open();
Check(SQLitePCL.raw.GetNativeLibraryName() == "OneLite (managed)", "provider is OneLite");

var updates = new List<string>();
SQLitePCL.raw.sqlite3_update_hook(connection.Handle, (_, type, _, table, rowid) => updates.Add(type + ":" + table + ":" + rowid), null);

connection.CreateFunction("twice", (long x) => x * 2, isDeterministic: true);
connection.CreateAggregate("total", 0L, (long acc, long x) => acc + x, acc => acc);
connection.CreateCollation("reverse", (a, b) => string.CompareOrdinal(b, a));

Run("create table t(id integer primary key, name text collate reverse, v integer, b blob)");
using (SqliteTransaction tx = connection.BeginTransaction())
{
    for (int i = 1; i <= 3; i++)
    {
        using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = "insert into t(name, v, b) values ($n, $v, $b)";
        insert.Parameters.AddWithValue("$n", "name" + i);
        insert.Parameters.AddWithValue("$v", i * 10L);
        insert.Parameters.AddWithValue("$b", new byte[] { 1, 2, 3, (byte)i });
        insert.ExecuteNonQuery();
    }

    tx.Commit();
}

Check(updates.Count == 3 && updates[0] == "18:t:1", "update hook saw the inserts");
Check(Scalar("select total(twice(v)) from t") == 120L, "user function and aggregate");
Check((string)Text("select name from t order by name limit 1") == "name3", "user collation");

using (var blob = new SqliteBlob(connection, "t", "b", 2))
{
    var buffer = new byte[4];
    int n = blob.Read(buffer, 0, 4);
    Check(n == 4 && buffer[3] == 2, "blob read");
}

try
{
    Run("insert into nosuch values (1)");
    Check(false, "missing table throws");
}
catch (SqliteException e)
{
    Check(e.SqliteErrorCode == 1 && e.Message.Contains("no such table: nosuch", StringComparison.Ordinal), "error code and message");
}

Console.WriteLine("OneLite.AotSmokeAdapter: ok");
return 0;

void Run(string sql)
{
    using SqliteCommand c = connection.CreateCommand();
    c.CommandText = sql;
    c.ExecuteNonQuery();
}

long Scalar(string sql)
{
    using SqliteCommand c = connection.CreateCommand();
    c.CommandText = sql;
    return (long)c.ExecuteScalar()!;
}

object Text(string sql)
{
    using SqliteCommand c = connection.CreateCommand();
    c.CommandText = sql;
    return c.ExecuteScalar()!;
}

static void Check(bool ok, string what)
{
    if (!ok)
    {
        Console.Error.WriteLine("FAILED: " + what);
        Environment.Exit(1);
    }
}
