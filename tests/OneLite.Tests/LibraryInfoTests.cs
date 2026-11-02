namespace RecursiveAI.OneLite.Tests;

public class LibraryInfoTests
{
    [Fact]
    public void NamesMatchPackageIds()
    {
        Assert.Equal("RecursiveAI.OneLite", LibraryInfo.Name);
        Assert.Equal("RecursiveAI.OneLite.Data", Data.DataLibraryInfo.Name);
    }
}
