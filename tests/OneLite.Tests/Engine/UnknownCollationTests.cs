using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>An unknown COLLATE name is an error where something uses the collating sequence, not where it is merely written. Recorded from the pinned sqlite3 3.53.4.</summary>
public sealed class UnknownCollationTests
{
    [Theory]
    [InlineData("select 'a' collate nosuch")]
    [InlineData("select abs(1 collate nosuch), 'a' collate nosuch || 'b'")]
    [InlineData("select (select 'a' collate nosuch)")]
    [InlineData("select x from (select 'a' x union select 'b') where x collate nosuch is not null and 1")]
    public void MerelyWrittenIsNotAnError(string sql)
        => Assert.DoesNotContain("Error", ColumnDefaultTests.Run(sql));

    [Theory]
    [InlineData("select 'a' < 'b' collate nosuch")]
    [InlineData("select 1 order by 'a' collate nosuch")]
    [InlineData("select x from (select 'a' collate nosuch x)")]
    [InlineData("select 'a' collate nosuch union select 'b'")]
    [InlineData("select distinct 'a' collate nosuch")]
    public void UsedIsAnError(string sql)
        => Assert.Equal("Error: no such collation sequence: nosuch", ColumnDefaultTests.Run(sql).Trim());
}
