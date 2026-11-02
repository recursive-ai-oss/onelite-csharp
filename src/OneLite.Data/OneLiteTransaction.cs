using System.Data;
using System.Data.Common;

namespace RecursiveAI.OneLite.Data;

/// <summary>A transaction on a <see cref="OneLiteConnection"/>. Disposing it without committing rolls it back.</summary>
public sealed class OneLiteTransaction : DbTransaction
{
    private readonly OneLiteConnection _connection;
    private readonly IsolationLevel _level;
    private bool _completed;

    internal OneLiteTransaction(OneLiteConnection connection, IsolationLevel level)
    {
        _connection = connection;
        _level = level;
    }

    /// <inheritdoc />
    protected override DbConnection? DbConnection => _completed ? null : _connection;

    /// <inheritdoc />
    public override IsolationLevel IsolationLevel => _level == IsolationLevel.Unspecified ? IsolationLevel.Serializable : _level;

    internal void Begin(bool deferred)
    {
        try
        {
            Run(_connection.Engine, deferred ? "BEGIN DEFERRED" : "BEGIN IMMEDIATE");
        }
        catch (OneLiteException e)
        {
            throw new OneLiteDataException(e);
        }

        _connection.SetTransaction(this);
    }

    private static void Run(Connection c, string sql)
    {
        using Statement st = c.Prepare(sql);
        while (st.Step())
        {
        }
    }

    private static async Task RunAsync(Connection c, string sql, CancellationToken ct)
    {
        await using Statement st = await c.PrepareAsync(sql, ct).ConfigureAwait(false);
        while (await st.StepAsync(ct).ConfigureAwait(false))
        {
        }
    }

    private void CheckOpen() => ObjectDisposedException.ThrowIf(_completed, this);

    /// <inheritdoc />
    public override void Commit()
    {
        CheckOpen();
        try
        {
            Run(_connection.Engine, "COMMIT");
        }
        catch (OneLiteException e)
        {
            throw new OneLiteDataException(e);
        }

        Complete();
    }

    /// <inheritdoc />
    public override async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        CheckOpen();
        try
        {
            await RunAsync(_connection.Engine, "COMMIT", cancellationToken).ConfigureAwait(false);
        }
        catch (OneLiteException e)
        {
            throw new OneLiteDataException(e);
        }

        Complete();
    }

    /// <inheritdoc />
    public override void Rollback()
    {
        CheckOpen();
        try
        {
            Run(_connection.Engine, "ROLLBACK");
        }
        catch (OneLiteException e)
        {
            throw new OneLiteDataException(e);
        }

        Complete();
    }

    /// <inheritdoc />
    public override async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        CheckOpen();
        try
        {
            await RunAsync(_connection.Engine, "ROLLBACK", cancellationToken).ConfigureAwait(false);
        }
        catch (OneLiteException e)
        {
            throw new OneLiteDataException(e);
        }

        Complete();
    }

    private void Complete()
    {
        _completed = true;
        _connection.SetTransaction(null);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_completed)
        {
            if (_connection.State == ConnectionState.Open && !_connection.Engine.AutoCommit)
            {
                try
                {
                    Run(_connection.Engine, "ROLLBACK");
                }
                catch (OneLiteException)
                {
                    // Disposing must not throw; the connection drops its locks when it closes.
                }
            }

            Complete();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            if (_connection.State == ConnectionState.Open && !_connection.Engine.AutoCommit)
            {
                try
                {
                    await RunAsync(_connection.Engine, "ROLLBACK", CancellationToken.None).ConfigureAwait(false);
                }
                catch (OneLiteException)
                {
                    // As in Dispose.
                }
            }

            Complete();
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
