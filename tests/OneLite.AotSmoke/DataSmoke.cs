using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading.Tasks;
using RecursiveAI.OneLite.Data;

namespace RecursiveAI.OneLite.AotSmoke;

[OneLiteRow(TableName = "smoke_users")]
internal sealed record SmokeUser([property: OneLiteKey] long Id, string Name, int Age, string? Email = null);

[OneLiteRow(TableName = "smoke_docs")]
internal sealed record SmokeDoc([property: OneLiteKey] long Id, string Body);

/// <summary>The ADO.NET provider and the happy path (generated mapper, Table, WriteAsync, migrations) under NativeAOT.</summary>
internal static class DataSmoke
{
    internal static bool Run() => Ado() && HappyPathAsync().GetAwaiter().GetResult();

    private static bool Ado()
    {
        using var conn = new OneLiteConnection("Data Source=:memory:");
        conn.Open();
        using (DbCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "create table t(a integer, b text); insert into t values (1, 'x'), (2, null)";
            cmd.ExecuteNonQuery();
        }

        using (DbTransaction tx = conn.BeginTransaction())
        {
            using DbCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "insert into t values (@a, :b)";
            cmd.Parameters.Add(new OneLiteParameter("@a", 3));
            cmd.Parameters.Add(new OneLiteParameter("b", "three"));
            if (cmd.ExecuteNonQuery() != 1)
            {
                return false;
            }

            tx.Commit();
        }

        using DbBatch batch = conn.CreateBatch();
        batch.BatchCommands.Add(new OneLiteBatchCommand("insert into t values (4, 'four')"));
        batch.BatchCommands.Add(new OneLiteBatchCommand("delete from t where a = 1"));
        if (batch.ExecuteNonQuery() != 2)
        {
            return false;
        }

        using DbCommand q = conn.CreateCommand();
        q.CommandText = "select a, b from t order by a";
        using DbDataReader r = q.ExecuteReader();
        var seen = new List<long>();
        while (r.Read())
        {
            seen.Add(r.GetFieldValue<long>(0));
            _ = r.IsDBNull(1) ? null : r.GetString(1);
        }

        _ = r.GetSchemaTable();
        if (seen.Count != 3 || seen[0] != 2 || seen[2] != 4)
        {
            Console.WriteLine("ADO.NET smoke failed");
            return false;
        }

        var builder = new OneLiteConnectionStringBuilder("Data Source=x.db;Mode=ReadOnly;Default Timeout=5");
        return builder.Mode == OneLiteOpenMode.ReadOnly && builder.DefaultTimeout == 5;
    }

    private static async Task<bool> HappyPathAsync()
    {
        await using Database db = await Database.OpenAsync(":memory:");
        await db.MigrateAsync("create table notes(id integer primary key, body text not null) strict");
        Table<SmokeUser> users = db.Table<SmokeUser>();
        await users.EnsureCreatedAsync();
        await users.InsertAsync(new SmokeUser(0, "ann", 30));
        await db.WriteAsync(async tx =>
        {
            await tx.Table<SmokeUser>().UpsertAsync(new SmokeUser(1, "ann", 31, "a@example.com"));
            await tx.ExecuteAsync($"insert into notes(body) values ({"hello"})");
        });
        int minAge = 18;
        List<SmokeUser> found = await db.QueryAsync<SmokeUser>($"select * from smoke_users where age > {minAge}");
        long notes = await db.ScalarAsync<long>($"select count(*) from notes");
        SmokeUser? one = await users.GetAsync(1L);
        if (found.Count != 1 || found[0].Age != 31 || notes != 1 || one?.Email != "a@example.com")
        {
            Console.WriteLine("happy path smoke failed");
            return false;
        }

        return await SearchAsync(db).ConfigureAwait(false);
    }

    private static readonly float[] s_query = [0f, 1f];

    private static async Task<bool> SearchAsync(Database db)
    {
        await db.ExecuteScriptAsync(
            "create table smoke_docs(id integer primary key, body text not null) strict;"
            + "create virtual table smoke_docs_fts using fts5(body);"
            + "create virtual table smoke_docs_vec using vec0(embedding float[2]);");
        string[] bodies = ["overdue invoice", "holiday plans"];
        float[][] vectors = [[0f, 1f], [1f, 0f]];
        for (int i = 0; i < bodies.Length; i++)
        {
            await db.ExecuteAsync($"insert into smoke_docs values ({i + 1}, {bodies[i]})");
            await db.ExecuteAsync($"insert into smoke_docs_fts(rowid, body) values ({i + 1}, {bodies[i]})");
        }

        await db.ExecuteScriptAsync("insert into smoke_docs_vec(rowid, embedding) values (1, '[0,1]'), (2, '[1,0]')");
        List<SearchHit<SmokeDoc>> hits = await db.SearchAsync<SmokeDoc>("smoke_docs", "invoice (overdue*", s_query, 5);
        if (hits.Count != 2 || hits[0].Item.Id != 1 || hits[0].TextRank != 1 || hits[0].VectorRank != 1)
        {
            Console.WriteLine("search smoke failed");
            return false;
        }

        return true;
    }
}
