using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RecursiveAI.OneLite.Oracle;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// The read-only SELECT engine: the step contract (yield injection, no allocation on the cached path) and error behaviour.
/// Databases are written by the pinned <c>sqlite3</c> shell, so the tests skip where that oracle is not installed.
/// </summary>
public sealed class EngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-engine-" + Guid.NewGuid().ToString("N"));

    public EngineTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string MakeDb(string name = "e.db")
    {
        Assert.SkipUnless(File.Exists(OraclePaths.Cli), "the pinned sqlite3 shell is not installed");
        string path = Path.Combine(_dir, name);
        var sb = new StringBuilder();
        sb.Append("PRAGMA page_size=1024; CREATE TABLE t(id INTEGER PRIMARY KEY, a INT, b TEXT COLLATE NOCASE, c REAL, d BLOB);");
        sb.Append("CREATE INDEX ta ON t(a);");
        sb.Append("WITH RECURSIVE s(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM s WHERE n<3000) ");
        sb.Append("INSERT INTO t SELECT n, n % 97, CASE WHEN n % 11 = 0 THEN NULL ELSE 'Row ' || n END, n / 7.0, CASE WHEN n % 5 = 0 THEN x'00ff' || zeroblob(n % 40) END FROM s;");
        sb.Append("CREATE TABLE big(id INTEGER PRIMARY KEY, v TEXT); INSERT INTO big VALUES (1, hex(zeroblob(5000))), (2, 'x');");
        SqliteCli.ApplyScript(path, sb.ToString());
        return path;
    }

    private static readonly string[] s_queries =
    [
        "SELECT * FROM t",
        "SELECT id, a * 2 + 1, upper(b), typeof(c), length(d), hex(d) FROM t WHERE a BETWEEN 10 AND 20 ORDER BY b DESC, id LIMIT 50 OFFSET 3",
        "SELECT count(*) FROM t WHERE b LIKE 'row 1%' AND d IS NOT NULL",
        "SELECT id FROM t WHERE id = 1234 OR id > 2990",
        "SELECT a, b FROM t WHERE a = 5 ORDER BY id DESC",
        "SELECT length(v), substr(v, 4990, 20) FROM big",
        "SELECT 1 + 1, 'x' || 2.5, quote(x'41')",
    ];

    private static List<string> Run(string path, string sql, int yieldEvery)
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
                sb.Append(st.ColumnType(i)).Append(':');
                sb.Append(st.IsNull(i) ? "NULL" : Convert.ToHexString(st.GetTextUtf8(i))).Append('|');
            }

            rows.Add(sb.ToString());
        }

        return rows;
    }

    [Fact]
    public void ResultsAreTheSameWithForcedYieldsAtEverySuspendPoint()
    {
        string path = MakeDb();
        foreach (string sql in s_queries)
        {
            List<string> plain = Run(path, sql, 0);
            Assert.NotEmpty(plain);
            foreach (int n in new[] { 1, 2, 3, 7 })
            {
                Assert.Equal(plain, Run(path, sql, n));
            }
        }
    }

    [Fact]
    public void CachedStepLoopsAllocateNothing()
    {
        string path = MakeDb();
        using var db = Database.OpenReadOnly(path, new DatabaseOptions { CacheSize = 10000 });
        using Connection conn = db.Connect();
        using Statement scan = conn.Prepare("SELECT id, a, b, c, d FROM t");
        using Statement point = conn.Prepare("SELECT id, b FROM t WHERE id = ?");
        using Statement range = conn.Prepare("SELECT id, a FROM t WHERE a >= 10 AND a < 20");

        long Measure(Func<long> body)
        {
            body(); // warm the page cache and the JIT
            body();
            long best = long.MaxValue;

            // The runtime may allocate once in the middle of a run (tiered compilation, thread statics); a loop that allocates per
            // row allocates in every repetition, so the smallest of three is the loop's own cost.
            for (int rep = 0; rep < 3; rep++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                long sum = body();
                long after = GC.GetAllocatedBytesForCurrentThread();
                Assert.True(sum != 0);
                best = Math.Min(best, after - before);
            }

            return best;
        }

        long ScanBody()
        {
            long sum = 0;
            scan.Reset();
            while (scan.Step())
            {
                sum += scan.GetInt64(0) + scan.GetInt64(1);
                sum += scan.IsNull(2) ? 0 : scan.GetTextUtf8(2).Length;
                sum += scan.GetBlob(4).Length;
            }

            return sum;
        }

        long PointBody()
        {
            // An open scan keeps the read transaction alive, so the lookups below are steps inside one transaction (beginning and
            // ending a transaction takes file locks, which is the pager's allocation, not the step loop's).
            long sum = 0;
            scan.Reset();
            Assert.True(scan.Step());
            for (int k = 1; k <= 2000; k++)
            {
                point.Reset();
                point.Bind(1, (long)k);
                if (point.Step())
                {
                    sum += point.GetInt64(0);
                }
            }

            scan.Reset();
            return sum;
        }

        long RangeBody()
        {
            long sum = 0;
            range.Reset();
            while (range.Step())
            {
                sum += range.GetInt64(0) + range.GetInt64(1);
            }

            return sum;
        }

        Assert.Equal(0, Measure(ScanBody));
        Assert.Equal(0, Measure(PointBody));
        Assert.Equal(0, Measure(RangeBody));
    }

    [Fact]
    public void CachedExpressionScansAllocateNothing()
    {
        string path = MakeDb();
        using var db = Database.OpenReadOnly(path, new DatabaseOptions { CacheSize = 10000 });
        using Connection conn = db.Connect();
        // Comparison, arithmetic, CASE, CAST, LIKE and the common text and numeric functions in WHERE and in the result columns.
        // (printf, round and quote build their text in new buffers and are not expected to be allocation-free.)
        const string Where = "(a BETWEEN 10 AND 80 OR b LIKE '%7') AND length(b) > 3 AND c < 400 AND a NOT IN (3, 4, 5) AND b IS NOT NULL";
        string[] items =
        [
            "id", "id + a * 2", "abs(c - 3)", "length(b)", "upper(b)", "substr(b, 2, 3)", "coalesce(b, 'x')",
            "CASE WHEN a > 50 THEN 'hi' ELSE 'lo' END", "b || '!'", "CAST(a AS TEXT)", "typeof(c)", "a IS NOT NULL", "b LIKE 'row 1%'",
            "lower(b) = 'row 5'", "min(a, 5)", "a % 7", "a << 1", "~a", "instr(b, 'w')", "trim(b)", "a BETWEEN 3 AND 9",
            "b = 'ROW 7' COLLATE NOCASE", "ifnull(c, 0)", "hex(d)", "replace(b, 'o', '0')", "c * 2.5", "CAST(c AS INTEGER)",
        ];
        var allocating = new StringBuilder();
        foreach (string item in items)
        {
            using Statement st = conn.Prepare("SELECT " + item + " FROM t WHERE " + Where);
            long Body()
            {
                long sum = 0;
                st.Reset();
                while (st.Step())
                {
                    sum += st.IsNull(0) ? 1 : st.ColumnType(0) is ColumnType.Text or ColumnType.Blob ? st.GetTextUtf8(0).Length : 1;
                }

                return sum;
            }

            Body();
            Body();
            long best = long.MaxValue;
            for (int rep = 0; rep < 3; rep++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                Assert.True(Body() != 0);
                best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
            }

            if (best != 0)
            {
                allocating.Append(item).Append(" => ").Append(best).Append(" B; ");
            }
        }

        Assert.True(allocating.Length == 0, allocating.ToString());
    }

    [Fact]
    public void ColumnTextIsAViewOfThePageNotACopy()
    {
        string path = MakeDb();
        using var db = Database.OpenReadOnly(path);
        using Connection conn = db.Connect();
        using Statement st = conn.Prepare("SELECT b FROM t WHERE id = 1");
        Assert.True(st.Step());
        ReadOnlySpan<byte> a = st.GetTextUtf8(0);
        ReadOnlySpan<byte> b = st.GetTextUtf8(0);
        Assert.Equal("Row 1", Encoding.UTF8.GetString(a));
        Assert.True(System.Runtime.CompilerServices.Unsafe.AreSame(in a[0], in b[0]));
    }

    [Theory]
    [InlineData("SELECT * FROM nope", 1, "no such table: nope")]
    [InlineData("SELECT zzz FROM t", 1, "no such column: zzz")]
    [InlineData("SELECT FROM t", 1, "near \"FROM\": syntax error")]
    [InlineData("SELECT 1 +", 1, "incomplete input")]
    [InlineData("SELECT lower(1, 2)", 1, "wrong number of arguments to function lower()")]
    [InlineData("SELECT 1 ORDER BY 2", 1, "1st ORDER BY term out of range - should be between 1 and 1")]
    public void PrepareErrorsCarryTheCodeAndMessageOfTheShell(string sql, int code, string message)
    {
        string path = MakeDb();
        using var db = Database.OpenReadOnly(path);
        using Connection conn = db.Connect();
        OneLiteException e = Assert.Throws<OneLiteException>(() => conn.Prepare(sql));
        Assert.Equal(code, e.Code);
        Assert.Equal(message, e.Message);
    }

    [Fact]
    public void UnsupportedConstructsFailAtPrepareNamingTheConstruct()
    {
        string path = MakeDb();
        using var db = Database.OpenReadOnly(path);
        using Connection conn = db.Connect();
        OneLiteException e = Assert.Throws<OneLiteException>(() => conn.Prepare("SELECT 1 FROM t WHERE 1 REGEXP 2"));
        Assert.Equal(ResultCode.Error, e.Code);
        Assert.Contains("unsupported", e.Message, StringComparison.Ordinal);
        Assert.Contains("REGEXP", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatIsNotADatabaseIsReportedAsSuch()
    {
        string path = Path.Combine(_dir, "junk.db");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(new string('x', 4096)));
        using var db = Database.OpenReadOnly(path);
        using Connection conn = db.Connect();
        OneLiteException e = Assert.Throws<OneLiteException>(() =>
        {
            using Statement st = conn.Prepare("SELECT * FROM t");
            st.Step();
        });
        Assert.Equal(ResultCode.NotADb, e.Code);
        Assert.Equal("file is not a database", e.Message);
    }

    [Fact]
    public void ADamagedTablePageIsReportedAsCorruptionNotAsACrash()
    {
        string path = MakeDb();
        byte[] bytes = File.ReadAllBytes(path);
        // Smash the interior of the file: every b-tree page past the schema page gets an impossible cell count and offsets.
        var rnd = new Random(7);
        for (int off = 2048; off + 1024 <= bytes.Length; off += 1024)
        {
            for (int k = 0; k < 12; k++)
            {
                bytes[off + 3 + k] = (byte)rnd.Next(256);
            }
        }

        File.WriteAllBytes(path, bytes);
        using var db = Database.OpenReadOnly(path);
        using Connection conn = db.Connect();
        OneLiteException? e = null;
        try
        {
            using Statement st = conn.Prepare("SELECT * FROM t");
            while (st.Step())
            {
            }
        }
        catch (OneLiteException ex)
        {
            e = ex;
        }

        Assert.NotNull(e);
        Assert.Equal(ResultCode.Corrupt, e!.Code);
    }

    [Fact]
    public void AnAnonymousParameterUsedByThePlanAndTheFilterIsOneParameter()
    {
        string path = MakeDb();
        using var db = Database.OpenReadOnly(path);
        using Connection conn = db.Connect();
        using Statement st = conn.Prepare("SELECT id, b FROM t WHERE id = ?");
        Assert.Equal(1, st.ParameterCount);
        foreach (long k in new long[] { 7, 8, 1500, 3000 })
        {
            st.Reset();
            st.Bind(1, k);
            Assert.True(st.Step(), "no row for " + k);
            Assert.Equal(k, st.GetInt64(0));
            Assert.False(st.Step());
        }
    }

    [Fact]
    public void ParametersBindByNumberAndName()
    {
        string path = MakeDb();
        using var db = Database.OpenReadOnly(path);
        using Connection conn = db.Connect();
        using Statement st = conn.Prepare("SELECT id FROM t WHERE a = :a AND id > ?2 ORDER BY id LIMIT 1");
        Assert.Equal(2, st.ParameterCount);
        st.Bind(st.ParameterIndex(":a"), 5L);
        st.Bind(2, 100L);
        Assert.True(st.Step());
        Assert.Equal(102, st.GetInt64(0));
    }
}
