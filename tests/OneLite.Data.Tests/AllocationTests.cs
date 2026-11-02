using System.Data.Common;
using Xunit;

namespace RecursiveAI.OneLite.Data.Tests;

/// <summary>Allocation per row over a fully cached database: the happy path allocates the result objects and the list, the ADO.NET read loop nothing per row for numbers.</summary>
public sealed class AllocationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Report(string line)
    {
        TestContext.Current.SendDiagnosticMessage(line);
        if (Environment.GetEnvironmentVariable("ONELITE_ALLOC_LOG") is { Length: > 0 } f)
        {
            File.AppendAllText(f, line + "\n");
        }
    }

    private static long Bytes(Action a)
    {
        a();
        a();
        long before = GC.GetAllocatedBytesForCurrentThread();
        a();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public async Task HappyPathQueryAllocatesOnlyResultObjectsAndList()
    {
        await using Database db = await Database.OpenAsync(":memory:", null, Ct);
        await db.Table<User>().EnsureCreatedAsync(Ct);
        await db.WriteAsync(async tx =>
        {
            for (int i = 0; i < 2000; i++)
            {
                await tx.Table<User>().InsertAsync(new User(0, "user" + i, 20 + (i % 50)), Ct);
            }
        }, Ct);

        long Run(int n) => Bytes(() =>
        {
            ValueTask<List<User>> t = db.QueryAsync<User>($"select * from users where id <= {n}", Ct);
            Assert.True(t.IsCompletedSuccessfully, "a cached query must complete synchronously");
            Assert.Equal(n, t.Result.Count);
        });

        long small = Run(100);
        long large = Run(2000);
        double perRow = (double)(large - small) / 1900;
        Report($"happy path QueryAsync<User> (long, string, int, string?) per-row bytes: {perRow:F1} (fixed per call: {small - (perRow * 100):F0} B)");

        long scalarSmall = Bytes(() => Assert.Equal(100, db.QueryAsync<long>($"select id from users where id <= {100}", Ct).AsTask().Result.Count));
        long scalarLarge = Bytes(() => Assert.Equal(2000, db.QueryAsync<long>($"select id from users where id <= {2000}", Ct).AsTask().Result.Count));
        double scalarPerRow = (double)(scalarLarge - scalarSmall) / 1900;
        Report($"happy path QueryAsync<long> per-row bytes: {scalarPerRow:F1}");

        // A User is an object (header + long + int + 2 references = 48 B) and a name string (~34 B for 8 chars); the list adds 8 B per row, doubled while it grows.
        Assert.True(perRow < 150, $"per-row allocation {perRow:F1} B is more than the result objects");
        Assert.True(scalarPerRow < 24, $"per-row allocation {scalarPerRow:F1} B for a boxed-free scalar list is too high");
    }

    [Fact]
    public void AdoReadLoopAllocatesNothingPerRowForNumbers()
    {
        using var c = new OneLiteConnection("Data Source=:memory:");
        c.Open();
        using (DbCommand setup = c.CreateCommand())
        {
            setup.CommandText = "create table t(a integer, b real, s text)";
            setup.ExecuteNonQuery();
        }

        using (DbTransaction tx = c.BeginTransaction())
        {
            using DbCommand ins = c.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "with recursive n(i) as (select 1 union all select i + 1 from n where i < 5000) insert into t select i, i * 0.5, 'row ' || i from n";
            ins.ExecuteNonQuery();
            tx.Commit();
        }

        long Loop(int limit, bool text) => Bytes(() =>
        {
            using DbCommand cmd = c.CreateCommand();
            cmd.CommandText = "select a, b, s from t where a <= " + limit;
            using DbDataReader r = cmd.ExecuteReader();
            long sum = 0;
            int n = 0;
            while (r.Read())
            {
                sum += r.GetInt64(0) + (long)r.GetDouble(1);
                if (text)
                {
                    sum += r.GetString(2).Length;
                }

                n++;
            }

            Assert.Equal(limit, n);
            Assert.True(sum > 0);
        });

        double numbers = (double)(Loop(5000, false) - Loop(500, false)) / 4500;
        double withText = (double)(Loop(5000, true) - Loop(500, true)) / 4500;
        long spans = Bytes(() =>
        {
            using DbCommand cmd = c.CreateCommand();
            cmd.CommandText = "select s from t";
            using var r = (OneLiteDataReader)cmd.ExecuteReader();
            long n = 0;
            while (r.Read())
            {
                n += r.GetUtf8Span(0).Length;
            }

            Assert.True(n > 0);
        });
        Report($"ADO.NET Read loop, GetInt64+GetDouble per-row bytes: {numbers:F2}");
        Report($"ADO.NET Read loop, plus GetString per-row bytes: {withText:F2}");
        Report($"ADO.NET Read loop over 5000 rows with GetUtf8Span: {spans} B in total (all fixed costs)");
        Assert.True(numbers < 1, $"reading numbers allocated {numbers:F2} B per row");
        Assert.True(withText < 80, $"reading a string allocated {withText:F2} B per row");
    }
}
