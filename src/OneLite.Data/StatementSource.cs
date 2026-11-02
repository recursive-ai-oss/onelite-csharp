namespace RecursiveAI.OneLite.Data;

/// <summary>Hands a reader its statements one at a time, prepared and with parameters bound.</summary>
internal abstract class StatementSource
{
    /// <summary>The next statement, or null when there is none.</summary>
    internal abstract Statement? Next();
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

        Statement st = connection.Engine.Prepare(_rest);
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
}
