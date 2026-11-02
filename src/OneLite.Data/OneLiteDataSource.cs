using System.Data.Common;

namespace RecursiveAI.OneLite.Data;

/// <summary>A factory for connections to one database, for dependency injection: <c>await using var c = await source.OpenConnectionAsync();</c>.</summary>
/// <param name="connectionString">See <see cref="OneLiteConnectionStringBuilder"/>.</param>
public sealed class OneLiteDataSource(string connectionString) : DbDataSource
{
    /// <inheritdoc />
    public override string ConnectionString { get; } = connectionString;

    /// <inheritdoc />
    protected override DbConnection CreateDbConnection() => new OneLiteConnection(ConnectionString);
}
