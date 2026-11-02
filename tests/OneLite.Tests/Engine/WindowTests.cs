using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Oracle;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// Window functions: rows against the pinned <c>sqlite3</c> shell for frames, ties, index-ordered stages and the corner cases found
/// by the differential run, the same rows with a suspension forced at every step, and the no-allocation rule for cached scans.
/// </summary>
public sealed class WindowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-window-" + Guid.NewGuid().ToString("N"));

    public WindowTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string MakeDb(int rows = 400)
    {
        Assert.SkipUnless(File.Exists(OraclePaths.Cli), "the pinned sqlite3 shell is not installed");
        string path = Path.Combine(_dir, "w.db");
        var sb = new StringBuilder();
        sb.Append("PRAGMA page_size=1024; CREATE TABLE t(id INTEGER PRIMARY KEY, g INT, a INT, b TEXT COLLATE NOCASE, c REAL);");
        sb.Append("CREATE INDEX tg ON t(g); CREATE INDEX ta_desc ON t(a DESC);");
        sb.Append(string.Format(System.Globalization.CultureInfo.InvariantCulture, "WITH RECURSIVE s(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM s WHERE n<{0}) ", rows));
        sb.Append("INSERT INTO t SELECT n, n % 5, (n * 7) % 11, CASE WHEN n % 9 = 0 THEN NULL ELSE 'k' || (n % 4) END, n / 3.0 FROM s;");
        SqliteCli.ApplyScript(path, sb.ToString());
        return path;
    }

    private static List<string> Run(string path, string sql, int yieldEvery = 0)
    {
        using var db = Database.OpenReadOnly(path, new DatabaseOptions { YieldEvery = yieldEvery });
        using Connection conn = db.Connect();
        using Statement st = conn.Prepare(sql);
        var rows = new List<string>();
        while (st.Step())
        {
            var sb = new StringBuilder();
            for (int i = 0; i < st.ColumnCount; i++)
            {
                sb.Append(st.IsNull(i) ? "NULL" : Encoding.UTF8.GetString(st.GetTextUtf8(i))).Append('|');
            }

            rows.Add(sb.ToString());
        }

        return rows;
    }

    private static readonly string[] s_queries =
    [
        "SELECT id, row_number() OVER (ORDER BY id), sum(a) OVER (ORDER BY id ROWS 3 PRECEDING), lag(a) OVER (ORDER BY id) FROM t",
        "SELECT id, rank() OVER (PARTITION BY g ORDER BY a), dense_rank() OVER (PARTITION BY g ORDER BY a), percent_rank() OVER (PARTITION BY g ORDER BY a) FROM t",
        "SELECT id, cume_dist() OVER (ORDER BY a), ntile(4) OVER (ORDER BY a, id) FROM t",
        "SELECT id, sum(c) OVER (PARTITION BY g ORDER BY id ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING) FROM t",
        "SELECT id, count(*) OVER (ORDER BY a RANGE BETWEEN 2 PRECEDING AND 1 FOLLOWING) FROM t",
        "SELECT id, group_concat(id) OVER (ORDER BY a GROUPS BETWEEN 1 PRECEDING AND 1 FOLLOWING EXCLUDE TIES) FROM t WHERE id < 60",
        "SELECT id, first_value(id) OVER w, last_value(id) OVER w, nth_value(id, 3) OVER w FROM t WINDOW w AS (PARTITION BY g ORDER BY a, id ROWS BETWEEN 2 PRECEDING AND 2 FOLLOWING)",
        "SELECT id, min(a) FILTER (WHERE b IS NOT NULL) OVER (ORDER BY id ROWS 5 PRECEDING), max(b) OVER (PARTITION BY g) FROM t",
        // Observed corner cases from the differential run: ties in an index-ordered stage, descending index, duplicate order terms.
        "SELECT id, group_concat(id) OVER (ORDER BY g DESC) FROM t WHERE id < 40",
        "SELECT id, group_concat(id) OVER (ORDER BY a), group_concat(id) OVER (ORDER BY g) FROM t WHERE id < 40",
        "SELECT id, first_value(id) OVER (PARTITION BY a) FROM t",
        "SELECT id, lead(id, 2) OVER (ORDER BY g DESC, g DESC) FROM t WHERE id < 40",
        "SELECT id, lag(id, 1e-16) OVER (PARTITION BY g ORDER BY id), lag(id, -1) OVER (ORDER BY id), lag(id, -2) OVER (ORDER BY id), lag(id, 2.5, -1) OVER (ORDER BY id) FROM t WHERE id < 30",
        "SELECT id, count(*) OVER (GROUPS BETWEEN 2 PRECEDING AND 0 PRECEDING) FROM t WHERE id < 30",
        "SELECT id, row_number() OVER (ROWS BETWEEN 1 FOLLOWING AND 1 FOLLOWING), sum(a) OVER (PARTITION BY g), last_value(id) OVER () FROM t WHERE id < 30",
        "SELECT id, lag(id) OVER (), sum(a) OVER (PARTITION BY a), lag(id, 2) OVER (ROWS BETWEEN 1 FOLLOWING AND 1 FOLLOWING) FROM t WHERE id < 30",
        "SELECT id, nth_value(id, 0) OVER (ROWS BETWEEN CURRENT ROW AND CURRENT ROW EXCLUDE CURRENT ROW) FROM t WHERE id < 30",
        // group_concat leaves its frame by characters (the value and the next separator), and a RANGE frame that need not hold the current row adds first.
        "SELECT id, group_concat(b, c) OVER (ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW), group_concat(b) OVER (ORDER BY id ROWS BETWEEN CURRENT ROW AND 2 FOLLOWING) FROM t WHERE id < 40",
        "SELECT id, group_concat(b) OVER (ORDER BY id RANGE BETWEEN 1 FOLLOWING AND 2 FOLLOWING), group_concat(b) OVER (ORDER BY id RANGE BETWEEN 2 PRECEDING AND 1 PRECEDING) FROM t WHERE id < 40",
        "SELECT id, count(*) OVER (ORDER BY id ROWS BETWEEN true PRECEDING AND '1' FOLLOWING), lag(id, '1') OVER (ORDER BY id), lead(id, 1+1, -1) OVER (ORDER BY id) FROM t WHERE id < 30",
    ];

    [Fact]
    public void RowsAgreeWithTheShell()
    {
        string path = MakeDb();
        foreach (string sql in s_queries)
        {
            Proc.Result r = SqliteCli.Run(path, sql, "-readonly");
            Assert.Equal(string.Empty, r.Stderr);
            string expected = r.Stdout.TrimEnd('\n');
            string got = string.Join("\n", Run(path, sql).Select(row => row.TrimEnd('|').Replace("NULL", string.Empty, StringComparison.Ordinal)));
            Assert.True(expected == got, sql + "\nexpected:\n" + expected[..Math.Min(expected.Length, 400)] + "\ngot:\n" + got[..Math.Min(got.Length, 400)]);
        }
    }

    [Theory]
    [InlineData("SELECT sum(a) OVER (ROWS 0.5 FOLLOWING), count(DISTINCT a) OVER () FROM t", "unsupported frame specification")]
    [InlineData("SELECT count(DISTINCT a) OVER () FROM t", "DISTINCT is not supported for window functions")]
    [InlineData("SELECT nth_value(a, 0) OVER () FROM t", "second argument to nth_value must be a positive integer")]
    [InlineData("SELECT a FROM t WHERE row_number() OVER () > 1", "misuse of window function row_number()")]
    [InlineData("SELECT sum(a) OVER (ROWS BETWEEN NULL PRECEDING AND CURRENT ROW) FROM t", "frame starting offset must be a non-negative integer")]
    public void ErrorsHaveTheTextOfTheShell(string sql, string message)
    {
        string path = MakeDb(20);
        Proc.Result r = SqliteCli.Run(path, sql, "-readonly");
        Assert.Contains(message, r.Stderr, StringComparison.Ordinal);
        OneLiteException ex = Assert.Throws<OneLiteException>(() => Run(path, sql));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResultsAreTheSameWithForcedYieldsAtEverySuspendPoint()
    {
        string path = MakeDb(150);
        foreach (string sql in s_queries)
        {
            List<string> plain = Run(path, sql);
            Assert.True(plain.Count > 0, sql);
            foreach (int n in new[] { 1, 2, 3, 7 })
            {
                Assert.Equal(plain, Run(path, sql, n));
            }
        }
    }

    private static readonly string[] s_spillQueries =
    [
        "SELECT id, sum(a) OVER (ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING), count(*) OVER (ROWS BETWEEN 3 PRECEDING AND UNBOUNDED FOLLOWING) FROM t",
        "SELECT id, sum(c) OVER (ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING), round(avg(a) OVER (ORDER BY id ROWS BETWEEN 2 FOLLOWING AND UNBOUNDED FOLLOWING), 6) FROM t",
        "SELECT id, ntile(7) OVER (ORDER BY id), percent_rank() OVER (ORDER BY a), cume_dist() OVER (ORDER BY a), rank() OVER (ORDER BY a) FROM t",
        "SELECT id, last_value(id) OVER (ORDER BY a, id ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING), lead(id, 300) OVER (ORDER BY id), lag(id, 300) OVER (ORDER BY id) FROM t",
        "SELECT id, group_concat(a) OVER (ORDER BY id ROWS BETWEEN CURRENT ROW AND 3 FOLLOWING), max(a) OVER (ORDER BY id ROWS BETWEEN 500 PRECEDING AND CURRENT ROW) FROM t",
    ];

    [Fact]
    public async System.Threading.Tasks.Task PartitionsPastTheMemoryLimitSpillAndKeepTheirRowsOnBothDrivers()
    {
        string path = MakeDb(8000);
        long before = Vm.WindowRun.TotalSpilledBytes;
        foreach (string sql in s_spillQueries)
        {
            Proc.Result r = SqliteCli.Run(path, sql, "-readonly");
            Assert.Equal(string.Empty, r.Stderr);
            string expected = r.Stdout.TrimEnd('\n');
            foreach ((int y, bool async) in new[] { (0, false), (0, true), (1, false), (1, true), (2, false), (2, true), (3, false), (3, true), (7, false), (7, true) })
            {
                var opts = new DatabaseOptions { YieldEvery = y, CacheSize = -8 };
                using var db = Database.OpenReadOnly(path, opts);
                using Connection conn = db.Connect();
                using Statement st = conn.Prepare(sql);
                var sb = new StringBuilder();
                while (async ? await st.StepAsync(TestContext.Current.CancellationToken) : st.Step())
                {
                    for (int i = 0; i < st.ColumnCount; i++)
                    {
                        sb.Append(st.IsNull(i) ? "" : Encoding.UTF8.GetString(st.GetTextUtf8(i))).Append(i + 1 < st.ColumnCount ? '|' : '\n');
                    }
                }

                string[] e = expected.Split('\n'), g = sb.ToString().TrimEnd('\n').Split('\n');
                for (int i = 0; i < Math.Min(e.Length, g.Length); i++)
                {
                    Assert.True(e[i] == g[i], sql + " yield " + y + " async " + async + " line " + i + ": expected " + e[i] + " got " + g[i]);
                }

                Assert.Equal(e.Length, g.Length);
            }
        }

        Assert.True(Vm.WindowRun.TotalSpilledBytes > before, "no block was written to the temporary file");
    }

    [Fact]
    public void CachedWindowScansInRowidOrderAllocateNothing()
    {
        string path = MakeDb(2000);
        using var db = Database.OpenReadOnly(path, new DatabaseOptions { CacheSize = 100000 });
        using Connection conn = db.Connect();
        string[] queries =
        [
            "SELECT id, row_number() OVER (ORDER BY id) FROM t",
            "SELECT id, sum(a) OVER (ORDER BY id ROWS BETWEEN 3 PRECEDING AND CURRENT ROW) FROM t",
            "SELECT id, lag(a) OVER (ORDER BY id) FROM t",
        ];
        foreach (string sql in queries)
        {
            using Statement st = conn.Prepare(sql);
            long best = long.MaxValue;
            for (int rep = 0; rep < 5; rep++)
            {
                st.Reset();
                long before = GC.GetAllocatedBytesForCurrentThread();
                long sum = 0;
                while (st.Step())
                {
                    sum += st.GetInt64(0);
                }

                long after = GC.GetAllocatedBytesForCurrentThread();
                Assert.True(sum != 0);
                if (rep >= 2)
                {
                    best = Math.Min(best, after - before);
                }
            }

            Assert.True(best == 0, sql + " allocated " + best);
        }
    }
}
