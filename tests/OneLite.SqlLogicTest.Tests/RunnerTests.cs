using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RecursiveAI.OneLite.SqlLogicTest.Tests;

public class RenderingTests
{
    [Fact]
    public void HashIsMd5OfEachValueFollowedByANewline()
    {
        // md5("1\n2\n3\n"), md5("NULL\n(empty)\nabc\n") and md5("") computed independently.
        Assert.Equal("c0710d6b4f15dfa88f600b0e6b624077", Rendering.Hash(["1", "2", "3"]));
        Assert.Equal("9983616900c69066cd59f5cfa7fa9ed3", Rendering.Hash(["NULL", "(empty)", "abc"]));
        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", Rendering.Hash([]));
    }

    [Fact]
    public void NullEmptyAndUnprintable()
    {
        Assert.Equal("NULL", Rendering.Render(SltValue.Null, 'I'));
        Assert.Equal("NULL", Rendering.Render(SltValue.Null, 'T'));
        Assert.Equal("NULL", Rendering.Render(SltValue.Null, 'R'));
        Assert.Equal("(empty)", Rendering.Render(SltValue.FromText(""), 'T'));
        Assert.Equal("a@b@", Rendering.Render(SltValue.FromText("a\tb\u0001"), 'T'));
        Assert.Equal("@@", Rendering.Render(SltValue.FromText("\u00e9"), 'T'));   // two UTF-8 bytes, each unprintable
        Assert.Equal("hello world", Rendering.Render(SltValue.FromText("hello world"), 'T'));
    }

    [Fact]
    public void ColumnTypeLetterDecidesTheConversion()
    {
        Assert.Equal("7", Rendering.Render(SltValue.FromInt(7), 'I'));
        Assert.Equal("7.000", Rendering.Render(SltValue.FromInt(7), 'R'));
        Assert.Equal("7", Rendering.Render(SltValue.FromInt(7), 'T'));
        Assert.Equal("2", Rendering.Render(SltValue.FromReal(2.9), 'I'));
        Assert.Equal("-2", Rendering.Render(SltValue.FromReal(-2.9), 'I'));
        Assert.Equal("2.900", Rendering.Render(SltValue.FromReal(2.9), 'R'));
        Assert.Equal("0", Rendering.Render(SltValue.FromText("false"), 'I'));
        Assert.Equal("12", Rendering.Render(SltValue.FromText("  12abc"), 'I'));
        Assert.Equal("-5", Rendering.Render(SltValue.FromText("-5.9"), 'I'));
        Assert.Equal("1.500", Rendering.Render(SltValue.FromText("1.5e0x"), 'R'));
        Assert.Equal("0.000", Rendering.Render(SltValue.FromText("true"), 'R'));
        Assert.Equal("9223372036854775807", Rendering.Render(SltValue.FromText("99999999999999999999"), 'I'));
    }

    [Theory]
    [InlineData(0.0, "0.000")]
    [InlineData(1.0, "1.000")]
    [InlineData(2.5, "2.500")]
    [InlineData(-2.5, "-2.500")]
    [InlineData(0.0625, "0.063")]                       // an exact tie rounds up
    [InlineData(1.0005, "1.000")]                       // a hair under a tie rounds down
    [InlineData(0.4995, "0.500")]                       // so close to the tie that the 18-digit expansion shows it
    [InlineData(-0.0004, "-0.000")]                     // a negative that rounds to zero keeps its sign
    [InlineData(9223372036854775808.0, "9223372036854776000.000")]
    [InlineData(-18446744073709551616.0, "-18446744073709550000.000")]
    [InlineData(123456789012345.678, "123456789012345.700")]
    [InlineData(1e22, "10000000000000000000000.000")]
    public void FixedThreeDecimalsAsTheScriptsHoldThem(double d, string expected)
        => Assert.Equal(expected, Rendering.FormatFixed3(d));

    [Fact]
    public void NegativeZeroHasNoSign() => Assert.Equal("0.000", Rendering.FormatFixed3(-0.0));

    private static string[] Row(params string[] v) => v;

    [Fact]
    public void RowSortComparesColumnByColumnAsText()
    {
        var rows = new List<string[]> { Row("9", "x"), Row("10", "y"), Row("10", "a") };
        // "10" < "9" as text, then "a" < "y".
        Assert.Equal(["10", "a", "10", "y", "9", "x"], Rendering.Arrange(rows, SortMode.RowSort));
    }

    [Fact]
    public void ValueSortSortsEveryValueOnItsOwn()
    {
        var rows = new List<string[]> { Row("b", "a"), Row("c", "A") };
        Assert.Equal(["A", "a", "b", "c"], Rendering.Arrange(rows, SortMode.ValueSort));
    }

    [Fact]
    public void NoSortKeepsTheOrder()
    {
        var rows = new List<string[]> { Row("b"), Row("a") };
        Assert.Equal(["b", "a"], Rendering.Arrange(rows, SortMode.NoSort));
    }
}

/// <summary>A scripted engine: each SQL text maps to what it returns.</summary>
internal sealed class FakeConnection : ISltConnection
{
    public Dictionary<string, SltError?> Statements { get; } = [];

    public Dictionary<string, SltQueryResult> Queries { get; } = [];

    public SltError? Execute(string sql) => Statements.TryGetValue(sql, out SltError? e) ? e : null;

    public SltQueryResult Query(string sql) => Queries[sql];

    public void Dispose()
    {
    }

    public static SltQueryResult Rows(int columns, params SltValue[][] rows)
    {
        var r = new SltQueryResult { ColumnCount = columns };
        r.Rows.AddRange(rows);
        return r;
    }
}

public class RunnerTests
{
    private static FileResult Run(string script, FakeConnection c, string dialect = "sqlite")
    {
        var res = new FileResult { File = "t.test" };
        Runner.Run(dialect, ScriptParser.Parse(new StringReader(script)), c, res);
        return res;
    }

    private static SltValue I(long v) => SltValue.FromInt(v);

    [Fact]
    public void StatementsAndQueriesPass()
    {
        var c = new FakeConnection();
        c.Statements["bad"] = new SltError(1, "boom");
        c.Queries["SELECT a FROM t"] = FakeConnection.Rows(1, [I(1)], [I(2)]);
        FileResult r = Run("statement ok\nCREATE TABLE t(a)\n\nstatement error\nbad\n\nquery I nosort\nSELECT a FROM t\n----\n1\n2\n", c);
        Assert.Equal((3, 3, 0, 0), (r.Records, r.Passed, r.Failed, r.Skipped));
    }

    [Fact]
    public void WrongExpectedValueIsReportedWithBothSides()
    {
        var c = new FakeConnection();
        c.Queries["SELECT a FROM t"] = FakeConnection.Rows(1, [I(1)], [I(2)]);
        FileResult r = Run("query I nosort\nSELECT a FROM t\n----\n1\n3\n", c);
        Assert.Equal(1, r.Failed);
        Failure f = Assert.Single(r.Failures);
        Assert.Equal("values", f.Kind);
        Assert.Equal(1, f.Line);
        Assert.Equal("SELECT a FROM t", f.Sql);
        Assert.Contains("first difference at value 1: expected '3', actual '2'", f.Expected, StringComparison.Ordinal);
        Assert.Equal("1\n2", f.Actual);
    }

    [Fact]
    public void WrongHashIsReported()
    {
        var c = new FakeConnection();
        c.Queries["Q"] = FakeConnection.Rows(1, [I(1)], [I(2)], [I(3)]);
        // The right digest for 1,2,3 passes ...
        Assert.Equal(0, Run("query I nosort\nQ\n----\n3 values hashing to c0710d6b4f15dfa88f600b0e6b624077\n", c).Failed);
        // ... and one changed digit fails, showing the actual hash and values.
        FileResult r = Run("query I nosort\nQ\n----\n3 values hashing to c0710d6b4f15dfa88f600b0e6b624078\n", c);
        Failure f = Assert.Single(r.Failures);
        Assert.Equal("hash", f.Kind);
        Assert.Contains("3 values hashing to c0710d6b4f15dfa88f600b0e6b624077", f.Actual, StringComparison.Ordinal);
    }

    [Fact]
    public void WrongErrorExpectationIsReported()
    {
        var c = new FakeConnection();
        c.Statements["bad"] = new SltError(19, "constraint failed");
        FileResult r = Run("statement ok\nbad\n\nstatement error\nfine\n", c);
        Assert.Equal(2, r.Failed);
        Assert.Equal(["statement-failed", "statement-should-fail"], r.Failures.Select(f => f.Kind).ToArray());
        Assert.Contains("19", r.Failures[0].Actual, StringComparison.Ordinal);
    }

    [Fact]
    public void QueryErrorAndColumnCountMismatchFail()
    {
        var c = new FakeConnection();
        c.Queries["E"] = new SltQueryResult { Error = new SltError(1, "no such table: x") };
        c.Queries["W"] = FakeConnection.Rows(2, [I(1), I(2)]);
        FileResult r = Run("query I\nE\n----\n1\n\nquery I\nW\n----\n1\n", c);
        Assert.Equal(["query-error", "columns"], r.Failures.Select(f => f.Kind).ToArray());
    }

    [Fact]
    public void ConditionalsSkipAndHaltStops()
    {
        var c = new FakeConnection();
        c.Queries["Q"] = FakeConnection.Rows(1, [I(1)]);
        FileResult r = Run(
            "skipif sqlite\nstatement ok\nnever\n\nonlyif mysql\nquery I\nnever\n----\n9\n\nquery I\nQ\n----\n1\n\nhalt\n\nquery I\nQ\n----\n99\n", c);
        Assert.Equal((3, 1, 0, 2), (r.Records, r.Passed, r.Failed, r.Skipped));
        Assert.True(r.Halted);
    }

    [Fact]
    public void SkipIfSqliteHaltDoesNotStopSqlite()
    {
        var c = new FakeConnection();
        c.Queries["Q"] = FakeConnection.Rows(1, [I(1)]);
        FileResult r = Run("skipif sqlite\nhalt\n\nquery I\nQ\n----\n1\n", c);
        Assert.False(r.Halted);
        Assert.Equal(1, r.Passed);
        Assert.True(Run("skipif sqlite\nhalt\n\nquery I\nQ\n----\n1\n", c, "mysql").Halted);
    }

    [Fact]
    public void LabelsMustAgreeAcrossQueries()
    {
        var c = new FakeConnection();
        c.Queries["A"] = FakeConnection.Rows(1, [I(1)]);
        c.Queries["B"] = FakeConnection.Rows(1, [I(2)]);
        FileResult r = Run("query I nosort same\nA\n----\n1\n\nquery I nosort same\nB\n----\n2\n", c);
        Failure f = Assert.Single(r.Failures);
        Assert.Equal("label", f.Kind);
        Assert.Equal(6, f.Line);
    }

    [Fact]
    public void SkippedQueryStillTakesPartInLabelChecks()
    {
        var c = new FakeConnection();
        c.Queries["A"] = FakeConnection.Rows(1, [I(1)]);
        // The skipped record's recorded result (7) disagrees with the label's first result (1).
        FileResult r = Run("query I nosort L\nA\n----\n1\n\nskipif sqlite\nquery I nosort L\nB\n----\n7\n", c);
        Assert.Equal("label", Assert.Single(r.Failures).Kind);
        Assert.Equal(0, r.Skipped);
    }

    [Fact]
    public void RowsortAndValuesortCompareSortedValues()
    {
        var c = new FakeConnection();
        c.Queries["Q"] = FakeConnection.Rows(2, [I(9), SltValue.FromText("x")], [I(10), SltValue.FromText("y")]);
        Assert.Equal(0, Run("query IT rowsort\nQ\n----\n10\ny\n9\nx\n", c).Failed);
        Assert.Equal(0, Run("query IT valuesort\nQ\n----\n10\n9\nx\ny\n", c).Failed);
        Assert.Equal(1, Run("query IT nosort\nQ\n----\n10\ny\n9\nx\n", c).Failed);
    }

    [Fact]
    public void MalformedRecordsCountAsFailures()
    {
        FileResult r = Run("bogus line\n\nstatement ok\nfine\n", new FakeConnection());
        Assert.Equal((2, 1, 1), (r.Records, r.Passed, r.Failed));
        Assert.Equal("malformed", r.Failures[0].Kind);
    }

    [Fact]
    public void ReportsAreExplainedByTheKnownDifferencesFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "slt-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var r = new FileResult { File = "evidence/x.test", Records = 3, Passed = 2, Failed = 1 };
            r.Failures.Add(new Failure { Line = 12, Kind = "values", Sql = "SELECT 1", Expected = "2", Actual = "1" });
            r.FailedLines.Add(12);
            Directory.CreateDirectory(Path.Combine(dir, "files", "evidence"));
            File.WriteAllText(Path.Combine(dir, "files", "evidence", "x.test.json"), System.Text.Json.JsonSerializer.Serialize(r));
            string known = Path.Combine(dir, "known.json");

            File.WriteAllText(known, "[]");
            Assert.Equal(1, Sweep.Report(dir, null, known));          // unexplained: nonzero exit

            File.WriteAllText(known, "[{\"file\":\"evidence/x.test\",\"line\":12,\"reason\":\"why\"}]");
            Assert.Equal(0, Sweep.Report(dir, null, known));          // explained: clean
            Assert.Contains("why", File.ReadAllText(Path.Combine(dir, "summary.md")), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("select1.test", "select*")]
    [InlineData("evidence/in1.test", "evidence")]
    [InlineData("index/between/100/slt_good_0.test", "index/between")]
    [InlineData("random/expr/slt_good_3.test", "random/expr")]
    public void GroupsFollowTheDirectoryLayout(string path, string group)
        => Assert.Equal(group, Sweep.GroupOf(path));

    [Fact]
    public void ShardsPartitionTheFilesAndAreStable()
    {
        string dir = Path.Combine(Path.GetTempPath(), "slt-shard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var files = new List<string>();
            for (int i = 0; i < 10; i++)
            {
                string n = "f" + i.ToString(CultureInfo.InvariantCulture) + ".test";
                File.WriteAllText(Path.Combine(dir, n), new string('x', (i + 1) * 100));
                files.Add(n);
            }
            var union = new List<string>();
            for (int s = 1; s <= 3; s++)
            {
                List<string> mine = Sweep.ShardFiles(dir, files, s, 3);
                Assert.Equal(mine, Sweep.ShardFiles(dir, files, s, 3));   // same split every time
                union.AddRange(mine);
            }
            Assert.Equal(files.Order(StringComparer.Ordinal), union.Order(StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
