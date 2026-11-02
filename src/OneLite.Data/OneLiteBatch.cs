using System.Collections;
using System.Data;
using System.Data.Common;

namespace RecursiveAI.OneLite.Data;

/// <summary>One command of a <see cref="OneLiteBatch"/>.</summary>
public sealed class OneLiteBatchCommand : DbBatchCommand
{
    private readonly OneLiteParameterCollection _parameters = new();

    /// <summary>Creates an empty command.</summary>
    public OneLiteBatchCommand()
    {
    }

    /// <summary>Creates a command.</summary>
    /// <param name="commandText">The SQL.</param>
    public OneLiteBatchCommand(string commandText) => CommandText = commandText;

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string CommandText { get; set; } = string.Empty;

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
    public override int RecordsAffected { get; }

    /// <inheritdoc />
    protected override DbParameterCollection DbParameterCollection => _parameters;

    /// <summary>The parameters.</summary>
    public new OneLiteParameterCollection Parameters => _parameters;
}

/// <summary>The commands of a batch.</summary>
public sealed class OneLiteBatchCommandCollection : DbBatchCommandCollection
{
    private readonly List<DbBatchCommand> _items = [];

    /// <inheritdoc />
    public override int Count => _items.Count;

    /// <inheritdoc />
    public override bool IsReadOnly => false;

    /// <inheritdoc />
    public override void Add(DbBatchCommand item) => _items.Add(item);

    /// <inheritdoc />
    public override void Clear() => _items.Clear();

    /// <inheritdoc />
    public override bool Contains(DbBatchCommand item) => _items.Contains(item);

    /// <inheritdoc />
    public override void CopyTo(DbBatchCommand[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

    /// <inheritdoc />
    public override IEnumerator<DbBatchCommand> GetEnumerator() => _items.GetEnumerator();

    /// <inheritdoc />
    public override int IndexOf(DbBatchCommand item) => _items.IndexOf(item);

    /// <inheritdoc />
    public override void Insert(int index, DbBatchCommand item) => _items.Insert(index, item);

    /// <inheritdoc />
    public override bool Remove(DbBatchCommand item) => _items.Remove(item);

    /// <inheritdoc />
    public override void RemoveAt(int index) => _items.RemoveAt(index);

    /// <inheritdoc />
    protected override DbBatchCommand GetBatchCommand(int index) => _items[index];

    /// <inheritdoc />
    protected override void SetBatchCommand(int index, DbBatchCommand batchCommand) => _items[index] = batchCommand;
}

/// <summary>
/// Several commands executed in one call. <c>ExecuteNonQuery</c> and <c>ExecuteScalar</c> run them atomically (in a transaction of their own when the connection
/// has none: all commands apply or none). <c>ExecuteReader</c> returns each command's result sets one after another and does not add a transaction.
/// </summary>
public sealed class OneLiteBatch : DbBatch
{
    private readonly OneLiteBatchCommandCollection _commands = new();

    /// <summary>The connection the batch runs on.</summary>
    public new OneLiteConnection? Connection { get; set; }

    /// <summary>The transaction the batch runs in, or null.</summary>
    public new OneLiteTransaction? Transaction { get; set; }

    /// <summary>The commands.</summary>
    public new OneLiteBatchCommandCollection BatchCommands => _commands;

    /// <inheritdoc />
    protected override DbBatchCommandCollection DbBatchCommands => _commands;

    /// <inheritdoc />
    protected override DbConnection? DbConnection
    {
        get => Connection;
        set => Connection = (OneLiteConnection?)value;
    }

    /// <inheritdoc />
    protected override DbTransaction? DbTransaction
    {
        get => Transaction;
        set => Transaction = (OneLiteTransaction?)value;
    }

    /// <inheritdoc />
    public override int Timeout { get; set; } = 30;

    /// <inheritdoc />
    protected override DbBatchCommand CreateDbBatchCommand() => new OneLiteBatchCommand();

    /// <inheritdoc />
    public override void Prepare() => Check();

    /// <inheritdoc />
    public override Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        Check();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void Cancel()
    {
    }

    private OneLiteConnection Check()
    {
        OneLiteConnection c = Connection ?? throw new InvalidOperationException("the batch has no connection");
        if (c.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("the batch requires an open connection");
        }

        if (_commands.Count == 0)
        {
            throw new InvalidOperationException("the batch has no commands");
        }

        return c;
    }

    private ChainSource Source(OneLiteConnection c)
    {
        var parts = new List<StatementSource>(_commands.Count);
        foreach (OneLiteBatchCommand bc in _commands)
        {
            parts.Add(new CommandSource(c, bc.CommandText, bc.Parameters));
        }

        return new ChainSource(parts);
    }

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

    /// <inheritdoc />
    public override int ExecuteNonQuery()
    {
        OneLiteConnection c = Check();
        bool own = c.CurrentTransaction is null;
        using OneLiteTransaction? tx = own ? c.BeginTransaction(false) : null;
        int total = -1;
        foreach (OneLiteBatchCommand bc in _commands)
        {
            using var cmd = new OneLiteCommand(bc.CommandText, c) { Transaction = c.CurrentTransaction };
            foreach (OneLiteParameter p in bc.Parameters)
            {
                cmd.Parameters.Add(p);
            }

            int n = cmd.ExecuteNonQuery();
            if (n >= 0)
            {
                total = (total < 0 ? 0 : total) + n;
            }
        }

        tx?.Commit();
        return total;
    }

    /// <inheritdoc />
    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default)
    {
        OneLiteConnection c = Check();
        bool own = c.CurrentTransaction is null;
        OneLiteTransaction? tx = own ? c.BeginTransaction(false) : null;
        try
        {
            int total = -1;
            foreach (OneLiteBatchCommand bc in _commands)
            {
                using var cmd = new OneLiteCommand(bc.CommandText, c) { Transaction = c.CurrentTransaction };
                foreach (OneLiteParameter p in bc.Parameters)
                {
                    cmd.Parameters.Add(p);
                }

                int n = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (n >= 0)
                {
                    total = (total < 0 ? 0 : total) + n;
                }
            }

            if (tx is not null)
            {
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return total;
        }
        finally
        {
            if (tx is not null)
            {
                await tx.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public override object? ExecuteScalar()
    {
        using DbDataReader r = ExecuteDbDataReader(CommandBehavior.Default);
        return r.Read() ? r.GetValue(0) : null;
    }

    /// <inheritdoc />
    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken = default)
    {
        DbDataReader r = await ExecuteDbDataReaderAsync(CommandBehavior.Default, cancellationToken).ConfigureAwait(false);
        await using (r.ConfigureAwait(false))
        {
            return await r.ReadAsync(cancellationToken).ConfigureAwait(false) ? r.GetValue(0) : null;
        }
    }
}
