namespace RecursiveAI.OneLite.Data;

/// <summary>Hands a reader its statements one at a time, prepared and with parameters bound.</summary>
internal abstract class StatementSource
{
    /// <summary>The next statement, or null when there is none.</summary>
    internal abstract Statement? Next();

    /// <summary>As <see cref="Next"/>, awaiting the VFS for the I/O of loading the schema.</summary>
    internal abstract ValueTask<Statement?> NextAsync(CancellationToken ct);
}

/// <summary>The statements of one command's text.</summary>
internal sealed class CommandSource(OneLiteConnection connection, string text, OneLiteParameterCollection parameters) : StatementSource
{
    private string _rest = text;

    internal override Statement? Next()
    {
        if (string.IsNullOrWhiteSpace(_rest))
        {
            return null;
        }

        return Bind(connection.Engine.Prepare(_rest));
    }

    internal override async ValueTask<Statement?> NextAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_rest))
        {
            return null;
        }

        return Bind(await connection.Engine.PrepareAsync(_rest, ct).ConfigureAwait(false));
    }

    private Statement Bind(Statement st)
    {
        _rest = st.Tail;
        try
        {
            var missing = new List<string>();
            parameters.BindTo(st, missing);
            if (missing.Count > 0)
            {
                throw new InvalidOperationException("Must add values for the following parameters: " + string.Join(", ", missing));
            }
        }
        catch
        {
            st.Dispose();
            throw;
        }

        return st;
    }
}

/// <summary>The statements of several commands, in order.</summary>
internal sealed class ChainSource(IReadOnlyList<StatementSource> parts) : StatementSource
{
    private int _at;

    internal override Statement? Next()
    {
        while (_at < parts.Count)
        {
            Statement? st = parts[_at].Next();
            if (st is not null)
            {
                return st;
            }

            _at++;
        }

        return null;
    }

    internal override async ValueTask<Statement?> NextAsync(CancellationToken ct)
    {
        while (_at < parts.Count)
        {
            Statement? st = await parts[_at].NextAsync(ct).ConfigureAwait(false);
            if (st is not null)
            {
                return st;
            }

            _at++;
        }

        return null;
    }
}
