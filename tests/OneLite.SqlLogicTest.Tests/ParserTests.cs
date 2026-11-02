using System.Collections.Generic;
using System.Linq;

namespace RecursiveAI.OneLite.SqlLogicTest.Tests;

public class ParserTests
{
    private static List<Record> P(string text) => ScriptParser.ParseAll(text);

    [Fact]
    public void StatementOkAndError()
    {
        List<Record> r = P("statement ok\nCREATE TABLE t1(a INTEGER)\n\nstatement error\nINSERT INTO nope VALUES(1)\n");
        Assert.Equal(2, r.Count);
        Assert.Equal(RecordKind.StatementOk, r[0].Kind);
        Assert.Equal("CREATE TABLE t1(a INTEGER)", r[0].Sql);
        Assert.Equal(1, r[0].Line);
        Assert.Equal(RecordKind.StatementError, r[1].Kind);
        Assert.Equal(4, r[1].Line);
    }

    [Fact]
    public void MultiLineSqlIsJoinedAndCommentsInsideAreDropped()
    {
        List<Record> r = P("statement ok\nSELECT 1,\n# a comment in the middle\n  2\n");
        Assert.Equal("SELECT 1,\n  2", Assert.Single(r).Sql);
    }

    [Fact]
    public void CommentLinesDoNotSeparateRecords()
    {
        // The comment sits between the two lines of one record, so this is a single statement.
        List<Record> r = P("statement ok\nSELECT 1\n# not a separator\nFROM t1\n");
        Assert.Single(r);
        Assert.Equal("SELECT 1\nFROM t1", r[0].Sql);
    }

    [Fact]
    public void QueryWithTypesSortAndLabel()
    {
        Record q = Assert.Single(P("query ITR rowsort my-label\nSELECT a,b,c FROM t\n----\n1\nabc\n2.500\n"));
        Assert.Equal(RecordKind.Query, q.Kind);
        Assert.Equal("ITR", q.Types);
        Assert.Equal(SortMode.RowSort, q.Sort);
        Assert.Equal("my-label", q.Label);
        Assert.Equal(["1", "abc", "2.500"], q.Expected);
        Assert.False(q.Hashed);
    }

    [Theory]
    [InlineData("query I nosort", SortMode.NoSort, null)]
    [InlineData("query I valuesort", SortMode.ValueSort, null)]
    [InlineData("query I", SortMode.NoSort, null)]
    [InlineData("query I label-x", SortMode.NoSort, "label-x")]            // the sort mode is optional
    [InlineData("query I nosort label-x", SortMode.NoSort, "label-x")]
    [InlineData("query I rowsort label-x # trailing note", SortMode.RowSort, "label-x")]
    public void QueryHeaderForms(string header, SortMode sort, string? label)
    {
        Record q = Assert.Single(P(header + "\nSELECT 1\n----\n1\n"));
        Assert.Equal(RecordKind.Query, q.Kind);
        Assert.Equal(sort, q.Sort);
        Assert.Equal(label, q.Label);
    }

    [Fact]
    public void QueryWithoutResultsExpectsAnEmptySet()
    {
        Record a = Assert.Single(P("query I nosort\nSELECT a FROM empty\n"));
        Assert.Empty(a.Expected);
        Assert.False(a.Hashed);
        Record b = Assert.Single(P("query I nosort\nSELECT a FROM empty\n----\n"));
        Assert.Empty(b.Expected);
    }

    [Fact]
    public void HashedResult()
    {
        Record q = Assert.Single(P("query I nosort\nSELECT a FROM t\n----\n30 values hashing to 0123456789abcdef0123456789abcdef\n"));
        Assert.True(q.Hashed);
        Assert.Equal(30, q.HashedCount);
        Assert.Equal("0123456789abcdef0123456789abcdef", q.HashedDigest);
        Assert.Empty(q.Expected);
    }

    [Theory]
    [InlineData("30 values hashing to 0123456789ABCDEF0123456789abcdef")]   // digest is lower-case hex
    [InlineData("30 values hashing to 0123")]
    [InlineData("x values hashing to 0123456789abcdef0123456789abcdef")]
    [InlineData("30 rows hashing to 0123456789abcdef0123456789abcdef")]
    public void NearMissesOfTheHashFormAreOrdinaryValues(string line)
    {
        Record q = Assert.Single(P("query T nosort\nSELECT 1\n----\n" + line + "\n"));
        Assert.False(q.Hashed);
        Assert.Equal([line], q.Expected);
    }

    [Fact]
    public void ControlRecords()
    {
        List<Record> r = P("hash-threshold 8\n\nhalt\n");
        Assert.Equal(RecordKind.HashThreshold, r[0].Kind);
        Assert.Equal(8, r[0].Threshold);
        Assert.Equal(RecordKind.Halt, r[1].Kind);
    }

    [Fact]
    public void ConditionalsPrefixTheRecordAndIgnoreTrailingNotes()
    {
        Record s = Assert.Single(P("skipif mysql # not compatible\nonlyif sqlite\nstatement ok\nSELECT 1\n"));
        Assert.Equal(["mysql"], s.SkipIf);
        Assert.Equal(["sqlite"], s.OnlyIf);
        Assert.Equal(RecordKind.StatementOk, s.Kind);
        Assert.Equal(3, s.Line);
        Assert.False(s.IsSkippedOn("sqlite"));
        Assert.True(s.IsSkippedOn("mysql"));
        Assert.True(s.IsSkippedOn("postgresql"));   // onlyif sqlite excludes every other engine
    }

    [Fact]
    public void SkipIfSqliteSkipsOnSqlite()
    {
        Record s = Assert.Single(P("skipif sqlite\nstatement ok\nSELECT 1\n"));
        Assert.True(s.IsSkippedOn("sqlite"));
        Assert.False(s.IsSkippedOn("mysql"));
    }

    [Fact]
    public void ConditionalsApplyToHalt()
    {
        // "skipif sqlite / halt" stops every engine except sqlite.
        Record h = Assert.Single(P("skipif sqlite\nhalt\n"));
        Assert.Equal(RecordKind.Halt, h.Kind);
        Assert.True(h.IsSkippedOn("sqlite"));
        Assert.False(h.IsSkippedOn("mysql"));
    }

    [Fact]
    public void ManyBlankLinesAndWindowsLineEndings()
    {
        List<Record> r = P("\n\n\r\nstatement ok\r\nSELECT 1\r\n\r\n\r\n\r\nquery I nosort\r\nSELECT 2\r\n----\r\n2\r\n");
        Assert.Equal(2, r.Count);
        Assert.Equal("SELECT 1", r[0].Sql);
        Assert.Equal(["2"], r[1].Expected);
    }

    [Fact]
    public void StatementErrorMayCarryAMessageAfterTheDashes()
    {
        Record s = Assert.Single(P("statement error\nSELECT nope\n----\nno such column: nope\n"));
        Assert.Equal("SELECT nope", s.Sql);
        Assert.Equal(["no such column: nope"], s.ExpectedErrorText);
    }

    [Theory]
    [InlineData("frobnicate\n", "unknown record")]
    [InlineData("statement\nSELECT 1\n", "'ok' or 'error'")]
    [InlineData("statement maybe\nSELECT 1\n", "'ok' or 'error'")]
    [InlineData("statement ok\n", "without SQL")]
    [InlineData("query I nosort\n", "without SQL")]
    [InlineData("query\nSELECT 1\n", "type string")]
    [InlineData("query IX nosort\nSELECT 1\n", "only I, T and R")]
    [InlineData("query I nosort lbl extra\nSELECT 1\n", "after the label")]
    [InlineData("hash-threshold\n", "hash-threshold")]
    [InlineData("hash-threshold -3\n", "hash-threshold")]
    [InlineData("skipif\nstatement ok\nSELECT 1\n", "needs an engine name")]
    [InlineData("skipif mysql\n", "conditional without a record")]
    public void MalformedRecordsAreReportedAndParsingGoesOn(string text, string problem)
    {
        List<Record> r = P(text + "\nstatement ok\nSELECT 99\n");
        Assert.Equal(2, r.Count);
        Assert.Equal(RecordKind.Malformed, r[0].Kind);
        Assert.Contains(problem, r[0].Problem, StringComparison.Ordinal);
        Assert.Equal(RecordKind.StatementOk, r[1].Kind);   // the next record is still read
        Assert.Equal("SELECT 99", r[1].Sql);
    }

    [Fact]
    public void LineNumbersCountCommentsAndBlanks()
    {
        List<Record> r = P("# header\n\nstatement ok\nSELECT 1\n\n# gap\nquery I\nSELECT 2\n----\n2\n");
        Assert.Equal([3, 7], r.Select(x => x.Line).ToArray());
    }

    [Fact]
    public void EmptyAndCommentOnlyScripts()
    {
        Assert.Empty(P(""));
        Assert.Empty(P("# nothing\n\n# here\n"));
    }
}
