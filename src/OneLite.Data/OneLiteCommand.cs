using System.Data;
using System.Data.Common;

namespace RecursiveAI.OneLite.Data;

/// <summary>
/// An SQL command. The text may hold several statements (run in order; <c>NextResult</c> moves between result sets) and uses <c>?</c>, <c>?NNN</c>, <c>@name</c>,
/// <c>:name</c> or <c>$name</c> parameters. <c>ExecuteReaderAsync</c>, <c>ExecuteNonQueryAsync</c> and <c>ExecuteScalarAsync</c> are really asynchronous.
/// </summary>
public sealed class OneLiteCommand : DbCommand
{
    private readonly OneLiteParameterCollection _parameters = new();
    private string _text = string.Empty;
    private OneLiteConnection? _connection;
    private OneLiteTransaction? _transaction;

    /// <summary>Creates a command with no text and no connection.</summary>
    public OneLiteCommand()
    {
    }

    /// <summary>Creates a command.</summary>
    /// <param name="commandText">The SQL.</param>
    /// <param name="connection">The connection it runs on.</param>
    public OneLiteCommand(string commandText, OneLiteConnection connection)
    {
        _text = commandText;
        _connection = connection;
    }

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string CommandText
    {
        get => _text;
        set => _text = value ?? string.Empty;
    }

    /// <summary>Accepted for compatibility; the wait for a lock is the connection's <c>Default Timeout</c>. Default 30.</summary>
    public override int CommandTimeout { get; set; } = 30;

    /// <inheritdoc />
    public override CommandType CommandType
    {
        get => CommandType.Text;
        set
        {
            if (value != CommandType.Text)
            {
                throw new ArgumentException("only CommandType.Text is supported", nameof(value));
            }
        }
    }

    /// <inheritdoc />
    public override bool DesignTimeVisible { get; set; }

    /// <inheritdoc />
    public override UpdateRowSource UpdatedRowSource { get; set; }

    /// <inheritdoc />
    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => _connection = (OneLiteConnection?)value;
    }

    /// <inheritdoc />
    protected override DbParameterCollection DbParameterCollection => _parameters;

    /// <summary>The parameters.</summary>
    public new OneLiteParameterCollection Parameters => _parameters;

    /// <inheritdoc />
    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set => _transaction = (OneLiteTransaction?)value;
    }

    /// <inheritdoc />
    protected override DbParameter CreateDbParameter() => new OneLiteParameter();

    /// <inheritdoc />
    public override void Prepare() => Check();

    /// <inheritdoc />
    public override void Cancel()
    {
    }

    private OneLiteConnection Check()
    {
        OneLiteConnection c = _connection ?? throw new InvalidOperationException("the command has no connection");
        if (c.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("the command requires an open connection");
        }

        if (_text.Length == 0)
        {
            throw new InvalidOperationException("the command has no CommandText");
        }

        if (c.CurrentTransaction is not null && _transaction is null)
        {
            throw new InvalidOperationException("Execute requires the command to have a transaction object when the connection assigned to the command is in a pending local transaction.  The Transaction property of the command has not been initialized.");
        }

        if (_transaction is not null && !ReferenceEquals(_transaction, c.CurrentTransaction))
        {
            throw new InvalidOperationException("The transaction object is not associated with the same connection object as this command.");
        }

        return c;
    }

    internal StatementSource Source(OneLiteConnection c) => new CommandSource(c, _text, _parameters);

    /// <inheritdoc />
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        OneLiteConnection c = Check();
        var r = new OneLiteDataReader(c, Source(c), behavior);
        try
        {
            r.Start();
        }
        catch
        {
            r.Dispose();
            throw;
        }

        return r;
    }

    /// <inheritdoc />
    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        OneLiteConnection c = Check();
        var r = new OneLiteDataReader(c, Source(c), behavior);
        try
        {
            await r.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            r.Dispose();
            throw;
        }

        return r;
    }

    /// <summary>Executes the text and returns the number of rows changed by its INSERT, UPDATE and DELETE statements, or -1 if it had none.</summary>
    /// <returns>The rows changed.</returns>
    public override int ExecuteNonQuery()
    {
        using var r = (OneLiteDataReader)ExecuteDbDataReader(CommandBehavior.Default);
        do
        {
            while (r.Read())
            {
            }
        }
        while (r.NextResult());
        return r.RecordsAffected;
    }

    /// <inheritdoc />
    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        var r = (OneLiteDataReader)await ExecuteDbDataReaderAsync(CommandBehavior.Default, cancellationToken).ConfigureAwait(false);
        await using (r.ConfigureAwait(false))
        {
            do
            {
                while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                }
            }
            while (await r.NextResultAsync(cancellationToken).ConfigureAwait(false));
            return r.RecordsAffected;
        }
    }

    /// <inheritdoc />
    public override object? ExecuteScalar()
    {
        using var r = (OneLiteDataReader)ExecuteDbDataReader(CommandBehavior.Default);
        return r.Read() && r.FieldCount > 0 ? r.GetValue(0) : null;
    }

    /// <inheritdoc />
    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        var r = (OneLiteDataReader)await ExecuteDbDataReaderAsync(CommandBehavior.Default, cancellationToken).ConfigureAwait(false);
        await using (r.ConfigureAwait(false))
        {
            return await r.ReadAsync(cancellationToken).ConfigureAwait(false) && r.FieldCount > 0 ? r.GetValue(0) : null;
        }
    }
}
