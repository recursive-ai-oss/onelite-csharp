using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>x IS TRUE followed by a tighter operator: TRUE starts the right operand. Expected values recorded from the pinned sqlite3 3.53.4.</summary>
public sealed class IsTruthTests
{
    [Fact]
    public void ATighterOperatorAfterTrueMakesItTheStartOfTheOperand()
        => Assert.Equal("0|0|1|1|0|0|1|1|1|1", ColumnDefaultTests.Run("select 5 is true % true, 0 is true + 0, 1 is not true * 5, 2 is true + 1, 6 is true < 2, 7 is false << 1, 7 is not false << 1, 5 is true = 1, 5 is true between 0 and 1, 5 is true collate binary"));
}
