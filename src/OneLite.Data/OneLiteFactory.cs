using System.Data.Common;

namespace RecursiveAI.OneLite.Data;

/// <summary>Creates the provider's objects, for code that picks a provider through <see cref="DbProviderFactory"/>.</summary>
public sealed class OneLiteFactory : DbProviderFactory
{
    /// <summary>The one instance.</summary>
    public static readonly OneLiteFactory Instance = new();

    private OneLiteFactory()
    {
    }

    /// <inheritdoc />
    public override bool CanCreateBatch => true;

    /// <inheritdoc />
    public override bool CanCreateDataSourceEnumerator => false;

    /// <inheritdoc />
    public override DbCommand CreateCommand() => new OneLiteCommand();

    /// <inheritdoc />
    public override DbConnection CreateConnection() => new OneLiteConnection();

    /// <inheritdoc />
    public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new OneLiteConnectionStringBuilder();

    /// <inheritdoc />
    public override DbParameter CreateParameter() => new OneLiteParameter();

    /// <inheritdoc />
    public override DbBatch CreateBatch() => new OneLiteBatch();

    /// <inheritdoc />
    public override DbBatchCommand CreateBatchCommand() => new OneLiteBatchCommand();

    /// <inheritdoc />
    public override DbDataSource CreateDataSource(string connectionString) => new OneLiteDataSource(connectionString);
}
