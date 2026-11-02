using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RecursiveAI.OneLite.Oracle;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>R*Tree: results against the pinned shell, and the files OneLite writes checked by the shell (integrity_check, rtreecheck).</summary>
public sealed partial class RTreeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-rtree-" + Guid.NewGuid().ToString("N"));

    public RTreeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("ONELITE_KEEP") != "1")
        {
            Directory.Delete(_dir, true);
        }
    }

    private static void NeedOracle() => Assert.SkipUnless(File.Exists(OraclePaths.Cli), "the pinned sqlite3 shell is not installed");

    [GeneratedRegex(@"^(?:Parse e|E|Runtime e)rror in \S+ command line argument: (.*)$", RegexOptions.Multiline)]
    private static partial Regex ShellError();

    internal static string Ours(Connection conn, string sql)
    {
        var o = new StringBuilder();
        try
        {
            using Statement st = conn.Prepare(sql);
            while (st.Step())
            {
                for (int i = 0; i < st.ColumnCount; i++)
                {
                    o.Append(i > 0 ? "|" : "").Append(st.IsNull(i) ? string.Empty : st.GetString(i));
                }

                o.Append('\n');
            }
        }
        catch (OneLiteException e)
        {
            o.Append("Error: ").Append(e.Message).Append('\n');
            if (Environment.GetEnvironmentVariable("ONELITE_TEST_DETAIL") == "1")
            {
                o.Append(e.Detail).Append('\n');
            }
        }

        return o.ToString();
    }

    internal static string Shell(string db, string sql)
    {
        Proc.Result r = Proc.Run(OraclePaths.Cli, [db, sql]);
        string err = r.Stderr.Trim();
        return r.Stdout + (err.Length == 0 ? string.Empty : "Error: " + (ShellError().Match(err) is { Success: true } m ? m.Groups[1].Value : err) + "\n");
    }

    private static string SplitScript()
    {
        var rnd = new Random(5);
        var sb = new StringBuilder("BEGIN;INSERT INTO t VALUES ");
        for (int i = 1; i <= 400; i++)
        {
            int x = rnd.Next(0, 1000), y = rnd.Next(0, 1000), w = rnd.Next(0, 50), h = rnd.Next(0, 50);
            sb.Append(i > 1 ? "," : "").Append('(').Append(i).Append(',').Append(x).Append(',').Append(x + w).Append(',').Append(y).Append(',').Append(y + h).Append(')');
        }

        return sb.Append(";COMMIT;").ToString();
    }

    [Fact]
    public void ScratchRepro()
    {
        using var db = Database.Open(Path.Combine(_dir, "r.db"));
        using Connection c = db.Connect();
        Assert.Equal(string.Empty, Ours(c, "CREATE VIRTUAL TABLE t USING rtree(id,a,b)"));
        Assert.Equal(string.Empty, Ours(c, "INSERT INTO t VALUES (1,1,2)"));
        using Statement st = c.Prepare("SELECT rtreecheck('t')");
        try
        {
            st.Step();
        }
        catch (OneLiteException e)
        {
            throw new InvalidOperationException(e.ToString() + " // " + e.InnerException?.ToString());
        }

        Assert.Equal("ok", st.GetString(0));
    }

    private static async System.Threading.Tasks.Task<string> Workload(string path, int yieldEvery, bool async)
    {
        using var db = Database.Open(path, new DatabaseOptions { PageSize = 512, YieldEvery = yieldEvery });
        using Connection c = db.Connect();
        var o = new StringBuilder();
        string[] script =
        [
            "CREATE VIRTUAL TABLE t USING rtree(id, x0, x1, y0, y1, +name)",
            "INSERT INTO t SELECT value, (value * 37) % 500, ((value * 37) % 500) + (value % 40), (value * 91) % 700, ((value * 91) % 700) + (value % 25), 'n' || value FROM generate_series(1, 150)",
            "DELETE FROM t WHERE id % 4 = 0",
            "UPDATE t SET x1 = x1 + 3, name = 'u' WHERE id % 9 = 0",
            "INSERT INTO t VALUES (NULL, 1, 2, 3, 4, 'auto'), (1000, 5, 6, 7, 8, NULL)",
            "SELECT id, name FROM t WHERE x0 <= 300 AND x1 >= 100 AND y0 <= 400 AND y1 >= 50 ORDER BY id",
            "SELECT count(*), sum(id) FROM t",
            "SELECT * FROM t WHERE id = 1000",
            "SELECT rtreecheck('t')",
        ];
        foreach (string sql in script)
        {
            using Statement st = c.Prepare(sql);
            while (async ? await st.StepAsync() : st.Step())
            {
                for (int i = 0; i < st.ColumnCount; i++)
                {
                    o.Append(st.IsNull(i) ? "~" : st.GetString(i)).Append('|');
                }

                o.Append('\n');
            }
        }

        return o.ToString();
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(7, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public async System.Threading.Tasks.Task ResumingAtEveryPageGivesTheSameRows(int yieldEvery, bool async)
    {
        string baseline = await Workload(Path.Combine(_dir, "base.db"), 0, false);
        Assert.EndsWith("ok|\n", baseline, StringComparison.Ordinal);
        Assert.Equal(baseline, await Workload(Path.Combine(_dir, "y.db"), yieldEvery, async));
    }

    [Fact]
    public void TenThousandEntriesAfterDeletesInteropBothWays()
    {
        NeedOracle();
        string ours = Path.Combine(_dir, "big-ours.db"), theirs = Path.Combine(_dir, "big-theirs.db");
        const string Fill = "INSERT INTO t SELECT value, (value * 7919) % 100000, ((value * 7919) % 100000) + (value % 97), (value * 104729) % 100000, ((value * 104729) % 100000) + (value % 53), (value * 31) % 1000, ((value * 31) % 1000) + 5 FROM generate_series(1, 12000);";
        const string Churn = "DELETE FROM t WHERE id % 3 = 0; DELETE FROM t WHERE x0 < 20000 AND y0 < 50000; UPDATE t SET x1 = x1 + 1 WHERE id % 50 = 1;";
        Shell(theirs, "CREATE VIRTUAL TABLE t USING rtree(id, x0, x1, y0, y1, z0, z1);" + Fill + Churn);
        using (var db = Database.Open(ours))
        using (Connection c = db.Connect())
        {
            Assert.Equal(string.Empty, Ours(c, "CREATE VIRTUAL TABLE t USING rtree(id, x0, x1, y0, y1, z0, z1)"));
            Assert.Equal(string.Empty, Ours(c, Fill));
            Assert.Equal(string.Empty, Ours(c, "DELETE FROM t WHERE id % 3 = 0"));
            Assert.Equal(string.Empty, Ours(c, "DELETE FROM t WHERE x0 < 20000 AND y0 < 50000"));
            Assert.Equal(string.Empty, Ours(c, "UPDATE t SET x1 = x1 + 1 WHERE id % 50 = 1"));
        }

        Assert.Equal("ok\nok\n", Shell(ours, "PRAGMA integrity_check; SELECT rtreecheck('t');"));
        string q = "SELECT count(*), sum(id), min(x0), max(y1) FROM t; SELECT id FROM t WHERE x0 <= 50000 AND x1 >= 49000 AND y0 <= 60000 AND y1 >= 59000 AND z0 <= 600 AND z1 >= 100 ORDER BY id;";
        Assert.Equal(Shell(theirs, q), Shell(ours, q));

        // The shell's file, read and changed by OneLite, still passes the shell's checks.
        using (var db = Database.Open(theirs))
        using (Connection c = db.Connect())
        {
            Assert.Equal(Shell(theirs, q), Ours(c, q.Split(';')[0]) + Ours(c, q.Split(';')[1]));
            Assert.Equal(string.Empty, Ours(c, "DELETE FROM t WHERE id % 5 = 0; INSERT INTO t VALUES (500000, 1, 2, 3, 4, 5, 6);"));
        }

        Assert.Equal("ok\nok\n", Shell(theirs, "PRAGMA integrity_check; SELECT rtreecheck('t');"));
    }

    [Theory]
    [InlineData(1024, 0)]
    [InlineData(4096, 0)]
    [InlineData(1024, 1)]
    [InlineData(512, 2)]
    public void DbstatEqualsTheShell(int pageSize, int autoVacuum)
    {
        NeedOracle();
        string path = Path.Combine(_dir, "d" + pageSize + "-" + autoVacuum + ".db");
        Shell(path, $"PRAGMA page_size={pageSize}; PRAGMA auto_vacuum={autoVacuum}; CREATE TABLE a(x INTEGER PRIMARY KEY, y); CREATE INDEX ai ON a(y); " +
            "INSERT INTO a SELECT value, hex(randomblob(CASE WHEN value%7=0 THEN 3000 ELSE 30 END)) FROM generate_series(1,300); DELETE FROM a WHERE x%11=0; CREATE TABLE e(z); CREATE TABLE w(k TEXT PRIMARY KEY, v) WITHOUT ROWID; INSERT INTO w SELECT hex(value), value FROM generate_series(1,200); DROP TABLE e;");
        using var db = Database.OpenReadOnly(path);
        using Connection c = db.Connect();
        foreach (string q in new[] { "SELECT * FROM dbstat", "SELECT * FROM dbstat WHERE aggregate = 1", "SELECT name, pageno, ncell, payload, unused, mx_payload, pgsize FROM dbstat('main', 1)", "SELECT count(*) FROM dbstat WHERE name = 'a'" })
        {
            Assert.Equal(Shell(path, q), Ours(c, q));
        }
    }

    [Fact]
    public void OurFileIsAcceptedByTheShellAndAnswersTheSame()
    {
        NeedOracle();
        string path = Path.Combine(_dir, "a.db");
        using (var db = Database.Open(path, new DatabaseOptions { PageSize = 512 }))
        using (Connection c = db.Connect())
        {
            Assert.Equal(string.Empty, Ours(c, "CREATE VIRTUAL TABLE t USING rtree(id, x0, x1, y0, y1)"));
            foreach (string s in SplitScript().Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                Assert.Equal(string.Empty, Ours(c, s));
            }

            Assert.Equal(string.Empty, Ours(c, "DELETE FROM t WHERE id % 3 = 0"));
            Assert.Equal(string.Empty, Ours(c, "UPDATE t SET x1 = x1 + 10, y1 = y1 + 5 WHERE id % 5 = 1"));
            Assert.Equal(string.Empty, Ours(c, "INSERT INTO t VALUES (1000, 5, 6, 7, 8), (NULL, 1, 2, 3, 4)"));
            foreach (string q in new[] { "SELECT count(*) FROM t", "SELECT id FROM t WHERE x0 <= 500 AND x1 >= 400 AND y0 <= 300 AND y1 >= 100 ORDER BY id", "SELECT * FROM t WHERE id = 1000", "SELECT id, x0, y1 FROM t ORDER BY id" })
            {
                Assert.Equal(Shell(path, q), Ours(c, q));
            }
        }

        Assert.Equal("ok\nok\n", Shell(path, "PRAGMA integrity_check; SELECT rtreecheck('t');"));
    }
}
