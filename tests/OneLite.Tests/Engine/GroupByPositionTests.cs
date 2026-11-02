using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// Which GROUP BY term is called out of range when a constant stands in front of a position. Expected texts were recorded from the
/// pinned sqlite3 3.53.4: an AND with a literal 0 folds to 0, but a sign or COLLATE in front of that 0 stops the folding.
/// </summary>
public sealed class GroupByPositionTests
{
    [Theory]
    [InlineData("NULL AND 0", "1st")]
    [InlineData("(NULL AND (0))", "1st")]
    [InlineData("0 AND NULL", "1st")]
    [InlineData("NULL AND (0 AND 1)", "1st")]
    [InlineData("NULL AND (+0)", "2nd")]
    [InlineData("NULL AND -0", "2nd")]
    [InlineData("(+0) AND NULL", "2nd")]
    [InlineData("NULL AND 0 COLLATE binary", "2nd")]
    [InlineData("NULL AND (1-1)", "2nd")]
    public void ANamedTermIsTheFirstOutOfRangeOnlyWhenTheConstantFolds(string term, string ordinal)
        => Assert.Equal($"Error: {ordinal} GROUP BY term out of range - should be between 1 and 1",
            ColumnDefaultTests.Run($"create table t2(id integer primary key, c0); select null from t2 group by {term}, 0"));
}
