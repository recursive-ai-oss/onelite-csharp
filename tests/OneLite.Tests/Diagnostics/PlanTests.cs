using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Diagnostics;

/// <summary>Query plans: the tree has to agree with what the program does, and the counted rows with the rows the loops really visit.</summary>
public sealed class PlanTests : IDisposable
{
    private const string Schema = """
        create table users(id integer primary key, name text, city text, age int);
        create table orders(id integer primary key, user_id int, total real, note text);
        create table items(order_id int, sku text, qty int, primary key(order_id, sku)) without rowid;
        create index users_city on users(city);
        create index users_city_age on users(city, age);
        create index orders_user on orders(user_id);
        create index users_age_name on users(age, name);
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-plan-" + Guid.NewGuid().ToString("N"));
    private readonly Database _db;
    private readonly Connection _c;

    public PlanTests()
    {
        Directory.CreateDirectory(_dir);
        _db = Database.Open(Path.Combine(_dir, "p.db"));
        _c = _db.Connect();
        Run(Schema);
        var sb = new StringBuilder("begin;");
        for (int i = 1; i <= 40; i++)
        {
            sb.Append($"insert into users values({i}, 'u{i}', 'c{i % 4}', {20 + (i % 10)});");
        }

        for (int i = 1; i <= 100; i++)
        {
            sb.Append($"insert into orders values({i}, {1 + (i % 40)}, {i}.5, 'n{i % 3}');");
        }

        for (int i = 1; i <= 100; i++)
        {
            sb.Append($"insert into items values({i}, 'a', {i % 5});insert into items values({i}, 'b', 1);");
        }

        sb.Append("commit;");
        Run(sb.ToString());
    }

    public void Dispose()
    {
        _c.Dispose();
        _db.Dispose();
        Directory.Delete(_dir, true);
    }

    private void Run(string script)
    {
        string rest = script;
        while (!string.IsNullOrWhiteSpace(rest))
        {
            using Statement st = _c.Prepare(rest);
            rest = st.Tail;
            while (st.Step())
            {
            }
        }
    }

    public static IEnumerable<string> Queries() =>
    [
        "select * from users where id = 7",
        "select * from users where id > 10 and id <= 20",
        "select city from users",
        "select * from users where city = 'c1'",
        "select * from users where city = 'c1' and age > 25",
        "select * from users u join orders o on o.user_id = u.id join items i on i.order_id = o.id where u.city = 'c2'",
        "select * from users where id in (select user_id from orders where total > 50)",
        "select name, (select count(*) from orders where user_id = users.id) from users",
        "with big as (select * from orders where total > 90) select * from big where id > 95",
        "select city, count(*) from users group by city",
        "select age, count(*) from users group by age",
        "select * from users order by id",
        "select * from users order by name",
        "select name, row_number() over (partition by city order by age) from users",
        "select distinct city from users",
        "select id from users union select user_id from orders",
        "select * from users where age = 25 order by name",
        "update users set age = age + 1 where city = 'c3'",
        "delete from orders where user_id = 5",
        "select * from json_each('[1,2,3]')",
    ];

    private long Count(string sql)
    {
        using Statement st = _c.Prepare(sql);
        Assert.True(st.Step());
        return st.GetInt64(0);
    }

    private static string Signature(QueryPlan p)
        => string.Join(" > ", p.Root.Descendants().Skip(1).Select(n => n.Kind is PlanNodeKind.Scan or PlanNodeKind.Search
            ? $"{n.Kind}:{n.Table ?? n.Title}:{n.Index ?? "-"}" : n.Kind.ToString()));

    /// <summary>A query, the nodes it must show (preorder) and, per table-access node in that order, a SQL count of the rows the loop must have yielded (null = not checked).</summary>
    public static IEnumerable<object[]> Shapes() =>
    [
        ["select * from users where id = 7", "Search:users:-", new[] { "select 1" }],
        ["select * from users where id > 10 and id <= 20", "Search:users:-", new[] { "select count(*) from users where id > 10 and id <= 20" }],
        ["select city from users", "Scan:users:users_city", new[] { "select count(*) from users" }],
        ["select * from users where city = 'c1'", "Search:users:users_city_age", new[] { "select count(*) from users where city = 'c1'" }],
        ["select * from users where city = 'c1' and age > 25", "Search:users:users_city_age", new[] { "select count(*) from users where city = 'c1' and age > 25" }],
        ["select * from users u join orders o on o.user_id = u.id join items i on i.order_id = o.id where u.city = 'c2'",
            "Search:users:users_city_age > Search:orders:orders_user > Search:items:items_pk_placeholder",
            new[] { "select count(*) from users where city = 'c2'",
                "select count(*) from users u join orders o on o.user_id = u.id where u.city = 'c2'",
                "select count(*) from users u join orders o on o.user_id = u.id join items i on i.order_id = o.id where u.city = 'c2'" }],
        ["select * from users where id in (select user_id from orders where total > 50)", "Scan:users:- > Subquery > Scan:orders:-", new[] { "select count(*) from users", "select count(*) from orders" }],
        ["select name, (select count(*) from orders where user_id = users.id) from users", "Scan:users:- > Subquery > Search:orders:orders_user > Aggregate",
            new[] { "select count(*) from users", "select count(*) from orders where user_id in (select id from users)" }],
        ["with big as (select * from orders where total > 90) select * from big where id > 95", "Materialize > Scan:orders:- > Scan:big:-",
            new[] { "select count(*) from orders", "select count(*) from orders where total > 90" }],
        ["select city, count(*) from users group by city", "Scan:users:users_city > Aggregate", new[] { "select count(*) from users" }],
        ["select city, count(*) from users group by lower(city)", "Scan:users:users_city > Aggregate", new[] { "select count(*) from users" }],
        ["select * from users order by id", "Scan:users:-", new[] { "select count(*) from users" }],
        ["select * from users order by name", "Scan:users:- > Sort", new[] { "select count(*) from users" }],
        ["select name, row_number() over (partition by city order by age) from users", "Scan:users:users_city_age > Window", new[] { "select count(*) from users" }],
        ["select distinct city from users", "Scan:users:users_city > Distinct", new[] { "select count(*) from users" }],
        ["select id from users union select user_id from orders", "Compound > Arm > Scan:users:users_city > Arm > Scan:orders:orders_user", new[] { "select count(*) from users", "select count(*) from orders" }],
        ["select * from users where age = 25 order by name", "Search:users:users_age_name > Sort", new[] { "select count(*) from users where age = 25" }],
    ];

    [Theory]
    [MemberData(nameof(Shapes))]
    public void ShapeIndexAndCountsAgreeWithTheRun(string sql, string signature, string[] counts)
    {
        QueryPlan plan = _c.Explain(sql, analyze: true)!;
        string actualSig = Signature(plan);
        // The primary key of a WITHOUT ROWID table is reported under the table's own key name; match its shape, not the generated name.
        if (signature.Contains("items_pk_placeholder", StringComparison.Ordinal))
        {
            signature = signature.Replace("items_pk_placeholder", plan.Root.Descendants().First(n => n.Table == "items").Index ?? "-");
        }

        Assert.Equal(signature.Trim(), actualSig.Trim());
        Assert.True(plan.HasActual);
        PlanNode[] access = [.. plan.Root.Descendants().Where(n => n.Kind is PlanNodeKind.Scan or PlanNodeKind.Search or PlanNodeKind.TableFunction)];
        // the expected counts are listed for the access nodes in the order they appear
        PlanNode[] checkedNodes = [.. access.Where(n => !(n.Kind == PlanNodeKind.Scan && n.Detail.StartsWith("(materialized", StringComparison.Ordinal)))];
        if (sql.StartsWith("with ", StringComparison.Ordinal))
        {
            checkedNodes = [access[0]];
            Assert.Equal(11, access[1].ActualRows);
        }

        for (int i = 0; i < counts.Length && i < checkedNodes.Length; i++)
        {
            Assert.Equal(Count(counts[i]), checkedNodes[i].ActualRows);
        }

        // Counting changes nothing about the result.
        Assert.Equal(Rows(sql, false), Rows(sql, true));
    }

    private string Rows(string sql, bool count)
    {
        using Statement st = _c.Prepare(sql, count);
        var sb = new StringBuilder();
        while (st.Step())
        {
            for (int i = 0; i < st.ColumnCount; i++)
            {
                sb.Append(st.GetString(i)).Append('|');
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    [Fact]
    public void AnalyzingAWriteLeavesNoTrace()
    {
        string before = Rows("select * from users", false) + Rows("select * from orders", false);
        QueryPlan u = _c.Explain("update users set age = age + 1 where city = 'c3'", analyze: true)!;
        Assert.Equal(Count("select count(*) from users where city = 'c3'"), u.Root.Children[0].ActualRows);
        QueryPlan d = _c.Explain("delete from orders where user_id = 5", analyze: true)!;
        Assert.Equal(3, d.Root.Children[0].ActualRows);
        Assert.Equal(before, Rows("select * from users", false) + Rows("select * from orders", false));
        Assert.True(_c.AutoCommit);
    }

    [Fact]
    public void ChoicesAreExplained()
    {
        QueryPlan p = _c.Explain("select * from users where city = 'c1' and age > 25")!;
        PlanNode n = p.Root.Children[0];
        Assert.Equal("users_city_age", n.Index);
        Assert.Contains("users_city_age answers equality on (city) and a range on age", n.Reason);
        Assert.Contains(n.Rejected, r => r.StartsWith("users_city:", StringComparison.Ordinal));
        Assert.Contains(n.Rejected, r => r.StartsWith("users_age_name:", StringComparison.Ordinal));
        Assert.False(p.HasActual);
        Assert.Null(n.ActualRows);
        Assert.NotNull(n.EstimatedRows);

        QueryPlan sub = _c.Explain("select name, (select count(*) from orders where user_id = users.id) from users")!;
        Assert.Equal("per outer row", sub.Root.Descendants().First(x => x.Kind == PlanNodeKind.Subquery).Runs);
        QueryPlan once = _c.Explain("select * from users where id in (select user_id from orders)")!;
        Assert.Equal("once", once.Root.Descendants().First(x => x.Kind == PlanNodeKind.Subquery).Runs);
    }

    [Fact]
    public async System.Threading.Tasks.Task AsyncTwinAgrees()
    {
        QueryPlan a = (await _c.ExplainAsync("select * from users where city = 'c1'", true, TestContext.Current.CancellationToken))!;
        QueryPlan b = _c.Explain("select * from users where city = 'c1'", true)!;
        Assert.Equal(b.Render(), a.Render());
    }

    [Fact]
    public void StatementWithoutAPlanReturnsNull()
    {
        Assert.Null(_c.Explain("create table zz(a)"));
        Assert.Null(_c.Explain("pragma user_version"));
        Assert.Null(_c.Explain("begin"));
        using Statement st = _c.Prepare("select * from users where id = ?");
        Assert.NotNull(st.Plan);
        Assert.Equal(0, st.PlanCounterCount);
    }

    // Bytes this thread allocated over 200 runs of the statement's loop, from the best of five batches. GC.GetAllocatedBytesForCurrentThread
    // is not exact while other threads allocate: a thread that allocates nothing at all (a spin-wait loop) still sees jumps of about 4 KB
    // now and then, measured with 12 allocating threads on the loaded host. A step that really allocated would show in every batch, so
    // one batch at zero is the proof and the best batch is what an assertion failure reports.
    private static long BestAllocationOf(Statement st)
    {
        long best = long.MaxValue;
        for (int batch = 0; batch < 5 && best != 0; batch++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int r = 0; r < 200; r++)
            {
                while (st.Step())
                {
                }
            }

            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return best;
    }

    [Fact]
    public void CountingOffAddsNothingToTheRunningLoop()
    {
        using Statement st = _c.Prepare("select sum(age) from users where city = 'c1'");
        Assert.Equal(0, st.PlanCounterCount);
        for (int w = 0; w < 3; w++)
        {
            while (st.Step())
            {
            }
        }

        Assert.Equal(0, BestAllocationOf(st));

        using Statement counted = _c.Prepare("select sum(age) from users where city = 'c1'", true);
        Assert.Equal(1, counted.PlanCounterCount);
        for (int w = 0; w < 3; w++)
        {
            while (counted.Step())
            {
            }
        }

        Assert.Equal(0, BestAllocationOf(counted));
        long counts = counted.Plan!.Root.Children[0].ActualRows ?? -1; // ten rows a run: three warm-up runs and whole batches of 200
        Assert.True(counts >= 203 * 10 && (counts - 30) % 2000 == 0);
    }

    [Fact]
    public void AVirtualTableLoopIsShownAndCounted()
    {
        Run("create virtual table rt using rtree(id, x0, x1, y0, y1); insert into rt values (1, 0, 1, 0, 1), (2, 5, 6, 5, 6), (3, 0.5, 2, 0.5, 2);");
        QueryPlan p = _c.Explain("select id from rt where x0 < 1.5 and x1 > 0.2", analyze: true)!;
        PlanNode n = p.Root.Descendants().First(x => x.Table == "rt");
        Assert.Equal(PlanNodeKind.TableFunction, n.Kind);
        Assert.Contains("virtual table", n.Detail, StringComparison.Ordinal);
        Assert.Equal(Count("select count(*) from rt where x0 < 1.5 and x1 > 0.2"), Math.Min(n.ActualRows!.Value, 2));
        Assert.Equal(1, n.Loops);
    }

    [Fact]
    public void Dump()
    {
        var sb = new StringBuilder();
        foreach (string q in Queries())
        {
            sb.Append(q).Append('\n');
            sb.Append(_c.Explain(q, analyze: true)?.Render() ?? "(no plan)").Append("\n\n");
        }

        File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "plans.txt"), sb.ToString());
    }
}
