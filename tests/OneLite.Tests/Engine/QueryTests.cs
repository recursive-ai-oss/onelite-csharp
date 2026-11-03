using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using RecursiveAI.OneLite.Oracle;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Storage.Vfs;
using RecursiveAI.OneLite.Vm;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// Joins, subqueries, CTEs, compound selects, DISTINCT, grouping, aggregates and the sorter: expected rows, resumption at every suspend
/// point, spilling sorts and the no-allocation rule for cached loops. Databases are written by the pinned <c>sqlite3</c> shell.
/// </summary>
public sealed class QueryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-query-" + Guid.NewGuid().ToString("N"));

    public QueryTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string MakeDb(int rows = 3000)
    {
        Assert.SkipUnless(File.Exists(OraclePaths.Cli), "the pinned sqlite3 shell is not installed");
        string path = Path.Combine(_dir, "q.db");
        var sb = new StringBuilder();
        sb.Append("PRAGMA page_size=1024; CREATE TABLE t(id INTEGER PRIMARY KEY, a INT, b TEXT COLLATE NOCASE, c REAL);");
        sb.Append("CREATE INDEX ta ON t(a);");
        sb.Append("CREATE TABLE u(id INTEGER PRIMARY KEY, tid INT, v TEXT);");
        sb.Append("CREATE INDEX u_tid ON u(tid);");
        sb.Append(CultureInfoFormat("WITH RECURSIVE s(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM s WHERE n<{0}) ", rows));
        sb.Append("INSERT INTO t SELECT n, n % 97, CASE WHEN n % 11 = 0 THEN NULL ELSE 'Row ' || (n % 13) END, n / 7.0 FROM s;");
        sb.Append(CultureInfoFormat("WITH RECURSIVE s(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM s WHERE n<{0}) ", rows * 2));
        sb.Append("INSERT INTO u SELECT n, (n * 7) % ").Append(rows).Append(" + 1, 'v' || (n % 17) FROM s;");
        SqliteCli.ApplyScript(path, sb.ToString());
        return path;
    }

    private static string CultureInfoFormat(string f, int n) => string.Format(System.Globalization.CultureInfo.InvariantCulture, f, n);

    private static List<string> Run(string path, string sql, int yieldEvery = 0, int cacheSize = -2000)
    {
        using var db = Database.OpenReadOnly(path, new DatabaseOptions { YieldEvery = yieldEvery, CacheSize = cacheSize });
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

    private static readonly string[] s_shellQueries =
    [
        "SELECT count(*) FROM t LEFT JOIN u ON u.tid = t.id AND u.v = 'nope'",
        "SELECT count(*) FROM t WHERE a IN (SELECT a FROM t WHERE id < 5)",
        "WITH a AS (SELECT n * 2 AS m FROM b), b AS (SELECT 21 AS n) SELECT m FROM a",
        "SELECT id FROM t NOT INDEXED WHERE a = 3 ORDER BY id LIMIT 4",
        "SELECT a FROM t INDEXED BY ta LIMIT 6",
        "SELECT count(*) FROM u JOIN t INDEXED BY ta ON u.tid = t.a",
        "SELECT 1.0 UNION ALL SELECT 1 UNION SELECT 5",
        "SELECT 1 UNION SELECT 7 UNION ALL SELECT 1.0 UNION SELECT 9",
        "SELECT * FROM t x, (SELECT 5 AS id) x ORDER BY 1 LIMIT 2",
        "WITH RECURSIVE c(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM c) SELECT n FROM c LIMIT 3 OFFSET 2",
        "SELECT a, count(*) FROM t WHERE a < 3 GROUP BY a ORDER BY a DESC",
        "SELECT 1 UNION SELECT 2 UNION ALL SELECT 2 EXCEPT SELECT 1",
        "SELECT (SELECT max(id) FROM u WHERE tid = t.id), id FROM t WHERE id = 3",
        "SELECT t.id, u.v, count(*) OVER () FROM t JOIN u ON u.tid = t.id WHERE t.id <= 2 ORDER BY t.id, u.id",
    ];

    [Fact]
    public void RowsAgreeWithTheShell()
    {
        string path = MakeDb();
        foreach (string sql in s_shellQueries.Where(q => !q.Contains("OVER", StringComparison.Ordinal)).Concat(s_queries))
        {
            Proc.Result r = SqliteCli.Run(path, sql, "-readonly");
            Assert.Equal(string.Empty, r.Stderr);
            string expected = r.Stdout.TrimEnd('\n');
            string got = string.Join("\n", Run(path, sql).Select(row => row.TrimEnd('|').Replace("NULL", string.Empty, StringComparison.Ordinal)));
            Assert.True(expected == got, sql + "\nexpected:\n" + expected + "\ngot:\n" + got);
        }
    }

    private static readonly string[] s_queries =
    [
        "SELECT t.id, u.v FROM t JOIN u ON u.tid = t.id WHERE t.a = 5 ORDER BY t.id, u.id",
        "SELECT t.id, count(u.id) FROM t LEFT JOIN u ON u.tid = t.id GROUP BY t.id HAVING count(u.id) > 1 ORDER BY 2 DESC, 1 LIMIT 20",
        "SELECT b, count(*), sum(a), min(c), max(c), group_concat(DISTINCT a) FROM t GROUP BY b ORDER BY b",
        "SELECT DISTINCT a % 7, b FROM t ORDER BY 1, 2",
        "SELECT id FROM t WHERE id IN (SELECT tid FROM u WHERE v = 'v3') ORDER BY id LIMIT 30",
        "SELECT id, (SELECT count(*) FROM u WHERE u.tid = t.id) FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.tid = t.id AND v = 'v1') ORDER BY id LIMIT 25",
        "WITH RECURSIVE c(n, s) AS (SELECT 1, 1 UNION ALL SELECT n + 1, s + n + 1 FROM c WHERE n < 200) SELECT n, s FROM c WHERE n % 17 = 0",
        "SELECT a FROM t WHERE id < 100 UNION SELECT tid FROM u WHERE id < 100 ORDER BY 1 DESC LIMIT 40",
        "SELECT a FROM t WHERE id < 200 INTERSECT SELECT tid FROM u WHERE id < 400",
        "SELECT x.a, y.a FROM t x, t y WHERE x.id = y.a AND x.id < 50 ORDER BY 1, 2",
        "SELECT t.id, u.id FROM t RIGHT JOIN u ON u.tid = t.id AND t.a > 90 ORDER BY 2 LIMIT 60",
        "SELECT count(t.id), count(u.id), count(id) FROM t FULL JOIN u USING (id)",
        "SELECT a, max(b), min(c) FROM t",
        "SELECT a, min(c), max(b) FROM t",
        "SELECT a, min(b) FROM (SELECT a, NULL AS b FROM t)",
        "SELECT total(v) FROM (SELECT 2 AS v UNION ALL SELECT 9007199254740993)",
        "SELECT sum(x) FROM (SELECT -9223372036854775808 AS x UNION ALL SELECT 9223372036854775807 UNION ALL SELECT 'e')",
        "SELECT x FROM (SELECT -9223372036854775809.0 AS x UNION ALL SELECT -9223372036854775808) UNION SELECT NULL",
        "SELECT 1 UNION SELECT 1.0 UNION SELECT 1",
        "SELECT t.id, count(*) FROM t RIGHT JOIN u USING (id) GROUP BY t.id ORDER BY 1 LIMIT 5",
        "SELECT * FROM (SELECT a, count(*) n FROM t GROUP BY a) WHERE n > 15 ORDER BY a",
    ];

    [Fact]
    public void ResultsAreTheSameWithForcedYieldsAtEverySuspendPoint()
    {
        string path = MakeDb(1500);
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

    [Fact]
    public void UncorrelatedSubqueriesInsideSubqueriesAreBuiltOnceAndEarlyTermsRunEarly()
    {
        // Each of these re-ran an uncorrelated subquery for every row of its enclosing one, which took minutes at this size.
        string path = MakeDb(20000);
        string[] queries =
        [
            "SELECT count(*) FROM u WHERE tid IN (SELECT id FROM t WHERE id IN (SELECT tid FROM u WHERE v = 'v3'))",
            "SELECT count(*) FROM t WHERE EXISTS (SELECT 1 FROM (SELECT tid FROM u JOIN t ON t.id = u.tid WHERE t.a = 5) AS s WHERE s.tid < t.id)",
            "SELECT count(*) FROM t JOIN u ON u.tid = t.id WHERE NOT EXISTS (SELECT 1 FROM u AS w WHERE w.tid = t.id + 100000)",
            "SELECT count(*) FROM u WHERE tid NOT IN (WITH c(k0) AS (SELECT id FROM t) SELECT k0 FROM c WHERE NOT EXISTS (SELECT 1 FROM t AS w WHERE w.a > c.k0 AND w.id = 3))",
        ];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var got = queries.Select(q => Run(path, q)).ToList();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"took {clock.Elapsed}");
        for (int i = 0; i < queries.Length; i++)
        {
            Assert.Equal(SqliteCli.Run(path, queries[i], "-readonly").Stdout.TrimEnd('\n') + "|", string.Join("\n", got[i]));
        }
    }

    [Fact]
    public void ASortLargerThanTheMemoryBudgetSpillsAndGivesTheSameRows()
    {
        string path = MakeDb(20000);
        const string Sql = "SELECT u.id, u.v FROM u ORDER BY v DESC, tid, id";
        List<string> small = Run(path, Sql, cacheSize: -8);        // 256 KiB floor: the sorter writes runs
        List<string> large = Run(path, Sql, cacheSize: -200000);   // everything in memory
        Assert.Equal(40000, small.Count);
        Assert.Equal(large, small);

        string g = "SELECT b, count(*), sum(c) FROM t GROUP BY b || a ORDER BY 1, 2, 3";
        Assert.Equal(Run(path, g, cacheSize: -200000), Run(path, g, cacheSize: -8));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void ASpillingSortGivesTheSameRowsWhenEveryStepMaySuspend(int yieldEvery)
    {
        string path = MakeDb(6000);
        const string Sql = "SELECT u.id, u.v FROM u ORDER BY v DESC, tid, id";
        List<string> expected = Run(path, Sql, cacheSize: -200000);
        Assert.Equal(12000, expected.Count);

        // Driven by hand, completing each request the way an async driver would: the temporary-file requests appear as Pending.
        using var db = Database.OpenReadOnly(path, new DatabaseOptions { YieldEvery = yieldEvery, CacheSize = -8 });
        using Connection conn = db.Connect();
        using Statement st = conn.Prepare(Sql);
        var rows = new List<string>();
        int sortIo = 0;
        for (; ; )
        {
            StepStatus s = st.Advance();
            if (s == StepStatus.Pending)
            {
                if (st.PendingIo.Phase is ResumeState.SortSpillOpen or ResumeState.SortSpillRun or ResumeState.SortMergeRead)
                {
                    sortIo++;
                }

                conn.Pager.ExecuteSync();   // runs the request and feeds its result back, as Statement.Complete would
                continue;
            }

            if (s != StepStatus.Row)
            {
                Assert.Equal(StepStatus.Done, s);
                break;
            }

            rows.Add(Encoding.UTF8.GetString(st.GetTextUtf8(0)) + "|" + Encoding.UTF8.GetString(st.GetTextUtf8(1)) + "|");
        }

        Assert.True(sortIo > 0, "the sort never used its temporary file");
        Assert.Equal(expected, rows);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async System.Threading.Tasks.Task ASpillingSortGivesTheSameRowsOnTheAsyncDriver(int yieldEvery)
    {
        string path = MakeDb(6000);
        const string Sql = "SELECT u.id, u.v FROM u ORDER BY v DESC, tid, id";
        List<string> expected = Run(path, Sql, cacheSize: -200000);

        using var db = Database.OpenReadOnly(path, new DatabaseOptions { YieldEvery = yieldEvery, CacheSize = -8 });
        using Connection conn = db.Connect();
        using Statement st = conn.Prepare(Sql);
        var rows = new List<string>();
        while (await st.StepAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(Encoding.UTF8.GetString(st.GetTextUtf8(0)) + "|" + Encoding.UTF8.GetString(st.GetTextUtf8(1)) + "|");
        }

        Assert.Equal(expected, rows);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void TheSorterWritesRunsAndMergesThemInOrderKeepingTiesStable(int yieldEvery)
    {
        string path = MakeDb(10);
        using var db = Database.OpenReadOnly(path, new DatabaseOptions { YieldEvery = yieldEvery });
        using Connection conn = db.Connect();
        Storage.Pager.Pager pager = conn.Pager;
        var terms = new[] { new SortTerm(false, true, CollKind.Binary) };
        using var sorter = new RecordSorter(terms, TextEncoding.Utf8, true, 64 * 1024);
        int pending = 0;
        // Each step is repeated, as the machine does, until it no longer asks for I/O.
        void Run(Func<SortStatus> step)
        {
            while (step() == SortStatus.Pending)
            {
                pending++;
                pager.ExecuteSync();
            }
        }

        const int N = 30000;
        for (int i = 0; i < N; i++)
        {
            byte[] rec = RecordWriter.ToArray([Mem.FromInt((i * 7919L) % 101), Mem.FromInt(i)]);
            bool added = false;
            Run(() =>
            {
                if (!sorter.SpillInProgress && !added)
                {
                    sorter.Add(rec);
                    added = true;
                }

                return sorter.SpillIfNeeded(pager);
            });
        }

        Assert.True(sorter.SpilledRuns > 3, "expected several runs, got " + sorter.SpilledRuns);
        Assert.Equal(N, sorter.Count);
        bool any = false;
        Run(() => sorter.Sort(pager, out any));
        Assert.True(any);
        var view = new RecordView();
        long prevKey = -1, prevSeq = -1;
        int count = 0;
        for (; ; )
        {
            var row = sorter.Current;
            view.Parse(row.Buf, row.Off, row.Len);
            long key = view.GetInt64(0), seq = view.GetInt64(1);
            Assert.True(key > prevKey || (key == prevKey && seq > prevSeq));
            prevKey = key;
            prevSeq = seq;
            count++;
            bool more = false;
            Run(() => sorter.Next(pager, out more));
            if (!more)
            {
                break;
            }
        }

        Assert.Equal(N, count);
        Assert.True(pending > sorter.SpilledRuns * 2, "the spill and the merge should have suspended, got " + pending);
    }

    [Fact]
    public void CachedJoinAndGroupByLoopsAllocateNothing()
    {
        string path = MakeDb(2000);
        using var db = Database.OpenReadOnly(path, new DatabaseOptions { CacheSize = 100000 });
        using Connection conn = db.Connect();
        using Statement join = conn.Prepare("SELECT t.id, u.v FROM t JOIN u ON u.tid = t.id WHERE t.a < 40");
        using Statement group = conn.Prepare("SELECT a, count(*), sum(c), max(id) FROM t GROUP BY a");

        long Measure(Statement st)
        {
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

            return best;
        }

        long jb = Measure(join);
        Assert.True(jb == 0, "join allocated " + jb);
        long gb = Measure(group);
        Assert.True(gb == 0, "group by allocated " + gb);
    }

    [Theory]
    [InlineData("SELECT 1 UNION SELECT 2 ORDER BY 1 UNION SELECT 3", "ORDER BY clause should come after UNION not before")]
    [InlineData("SELECT 1, 2 UNION SELECT 1", "SELECTs to the left and right of UNION do not have the same number of result columns")]
    [InlineData("SELECT (SELECT 1, 2)", "sub-select returns 2 columns - expected 1")]
    [InlineData("SELECT a FROM t, u WHERE id = 1", "ambiguous column name: id")]
    [InlineData("SELECT sum(a) FROM t WHERE sum(a) > 1", "misuse of aggregate: sum()")]
    [InlineData("SELECT a FROM t GROUP BY count(*)", "aggregate functions are not allowed in the GROUP BY clause")]
    [InlineData("SELECT a FROM t HAVING a > 1", "HAVING clause on a non-aggregate query")]
    [InlineData("SELECT * FROM t JOIN u USING (zz)", "cannot join using column zz - column not present in both tables")]
    [InlineData("SELECT count(DISTINCT) FROM t", "DISTINCT aggregates must have exactly one argument")]
    [InlineData("SELECT * FROM t INDEXED BY nope WHERE a = 1", "no such index: nope")]
    [InlineData("SELECT * FROM t a, t a", "ambiguous column name: main.a.id")]
    [InlineData("SELECT * FROM (SELECT 1 AS k) x, (SELECT 2 AS k) x", "ambiguous column name: *.x.k")]
    [InlineData("SELECT a.id FROM t a, t a", "ambiguous column name: a.id")]
    [InlineData("WITH a AS (SELECT * FROM b), b AS (SELECT * FROM a) SELECT * FROM a", "circular reference: a")]
    [InlineData("WITH a AS (SELECT 1), a AS (SELECT 2) SELECT * FROM a", "duplicate WITH table name: a")]
    [InlineData("WITH a AS (SELECT zz FROM t), b AS (SELECT 2) SELECT 1 FROM a JOIN b", "no such table: b")]
    [InlineData("WITH a AS (SELECT 1 FROM nosuch), b AS (SELECT 2) SELECT 1 FROM a JOIN b", "no such table: nosuch")]
    [InlineData("WITH a AS (SELECT * FROM (SELECT zz FROM t)), b AS (SELECT 2) SELECT 1 FROM a JOIN b", "no such column: zz")]
    public void MisuseGivesTheMessageOfTheShell(string sql, string message)
    {
        string path = MakeDb(10);
        using var db = Database.OpenReadOnly(path);
        using Connection conn = db.Connect();
        OneLiteException e = Assert.Throws<OneLiteException>(() => conn.Prepare(sql));
        Assert.Equal(message, e.Message);
    }
}
