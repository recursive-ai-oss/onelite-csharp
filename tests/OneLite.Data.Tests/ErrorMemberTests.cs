using System.Data.Common;
using Xunit;

namespace RecursiveAI.OneLite.Data.Tests;

public sealed class ErrorMemberTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheDataExceptionCarriesPositionCaretAndSuggestions()
    {
        await using var source = new OneLiteDataSource("Data Source=:memory:");
        await using DbConnection conn = await source.OpenConnectionAsync(Ct);
        await using (DbCommand setup = conn.CreateCommand())
        {
            setup.CommandText = "create table users(id integer primary key, name text not null)";
            await setup.ExecuteNonQueryAsync(Ct);
        }

        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "select nme from users";
        OneLiteDataException e = await Assert.ThrowsAsync<OneLiteDataException>(() => cmd.ExecuteScalarAsync(Ct));
        Assert.Equal("SQLite Error 1: 'no such column: nme'.", e.Message);
        Assert.Equal("nme", e.Identifier);
        Assert.Equal(8, e.Position!.Value.Column);
        Assert.Contains("name", e.Suggestions);
        Assert.Equal("select nme from users\n       ^~~", e.Caret);
        Assert.Contains("did you mean \"name\"?", e.Explanation);
        Assert.Contains("did you mean \"name\"?", e.ToString());

        cmd.CommandText = "insert into users(id) values (1)";
        e = await Assert.ThrowsAsync<OneLiteDataException>(() => cmd.ExecuteNonQueryAsync(Ct));
        Assert.Equal(ConstraintKind.NotNull, e.Constraint!.Kind);
        Assert.Equal("users", e.Constraint.Table);
        Assert.Equal(["name"], e.Constraint.Columns);
    }
}
