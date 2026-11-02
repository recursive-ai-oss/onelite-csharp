namespace RecursiveAI.OneLite;

/// <summary>Runs queries on one connection, for both the database's own connection and a write transaction. The caller holds whatever lock protects the connection.</summary>
internal static class Runner
{
    private static Statement Prepare(Connection c, in Query q)
    {
        Statement st = c.Prepare(q.Sql);
        try
        {
            if (!string.IsNullOrWhiteSpace(st.Tail.Replace(';', ' ')))
            {
                throw new ArgumentException("an interpolated query runs one statement; use ExecuteScript for several");
            }

            for (int i = 0; i < q.Args.Length; i++)
            {
                st.Bind(i + 1, q.Args[i]);
            }

            q.Binder?.Invoke(st);
            return st;
        }
        catch
        {
            st.Dispose();
            throw;
        }
    }

    internal static async ValueTask<List<T>> QueryAsync<T>(Connection c, Query q, CancellationToken ct)
    {
        using Statement st = Prepare(c, q);
        var list = new List<T>();
        Func<Statement, T>? read = null;
        while (await st.StepAsync(ct).ConfigureAwait(false))
        {
            read ??= RowAccess.Reader<T>(st);
            list.Add(read(st));
            ct.ThrowIfCancellationRequested();
        }

        return list;
    }

    internal static List<T> Query<T>(Connection c, Query q)
    {
        using Statement st = Prepare(c, q);
        var list = new List<T>();
        Func<Statement, T>? read = null;
        while (st.Step())
        {
            read ??= RowAccess.Reader<T>(st);
            list.Add(read(st));
        }

        return list;
    }

    internal static async ValueTask<List<TRow>> QueryRowsAsync<TRow>(Connection c, Query q, Func<Statement, TRow> read, CancellationToken ct)
    {
        using Statement st = Prepare(c, q);
        var list = new List<TRow>();
        while (await st.StepAsync(ct).ConfigureAwait(false))
        {
            list.Add(read(st));
        }

        return list;
    }

    internal static List<TRow> QueryRows<TRow>(Connection c, Query q, Func<Statement, TRow> read)
    {
        using Statement st = Prepare(c, q);
        var list = new List<TRow>();
        while (st.Step())
        {
            list.Add(read(st));
        }

        return list;
    }

    /// <summary>Reads rows whose first column is the rowid and whose other columns map to T, into a dictionary keyed by rowid.</summary>
    internal static async ValueTask FetchAsync<T>(Connection c, Query q, Dictionary<long, T> into, CancellationToken ct)
    {
        using Statement st = Prepare(c, q);
        Func<Statement, T>? read = null;
        while (await st.StepAsync(ct).ConfigureAwait(false))
        {
            read ??= RowAccess.Reader<T>(st);
            into[st.GetFieldValue<long>(0)] = read(st);
        }
    }

    internal static void Fetch<T>(Connection c, Query q, Dictionary<long, T> into)
    {
        using Statement st = Prepare(c, q);
        Func<Statement, T>? read = null;
        while (st.Step())
        {
            read ??= RowAccess.Reader<T>(st);
            into[st.GetFieldValue<long>(0)] = read(st);
        }
    }

    internal static async ValueTask<(long Changes, long LastRowId)> ExecuteAsync(Connection c, Query q, CancellationToken ct)
    {
        using Statement st = Prepare(c, q);
        while (await st.StepAsync(ct).ConfigureAwait(false))
        {
        }

        return (c.ChangesCount, c.LastInsertedRowid);
    }

    internal static (long Changes, long LastRowId) Execute(Connection c, Query q)
    {
        using Statement st = Prepare(c, q);
        while (st.Step())
        {
        }

        return (c.ChangesCount, c.LastInsertedRowid);
    }

    internal static async ValueTask<T?> ScalarAsync<T>(Connection c, Query q, CancellationToken ct)
    {
        using Statement st = Prepare(c, q);
        return await st.StepAsync(ct).ConfigureAwait(false) ? st.GetFieldValue<T>(0) : default;
    }

    internal static T? Scalar<T>(Connection c, Query q)
    {
        using Statement st = Prepare(c, q);
        return st.Step() ? st.GetFieldValue<T>(0) : default;
    }

    internal static async ValueTask ScriptAsync(Connection c, string sql, CancellationToken ct)
    {
        string rest = sql;
        while (!string.IsNullOrWhiteSpace(rest))
        {
            using Statement st = c.Prepare(rest);
            rest = st.Tail;
            while (await st.StepAsync(ct).ConfigureAwait(false))
            {
            }
        }
    }

    internal static void Script(Connection c, string sql)
    {
        string rest = sql;
        while (!string.IsNullOrWhiteSpace(rest))
        {
            using Statement st = c.Prepare(rest);
            rest = st.Tail;
            while (st.Step())
            {
            }
        }
    }
}

/// <summary>Picks how a row becomes a <c>T</c>: the generated mapper, or the first column for a single-value type.</summary>
internal static class RowAccess
{
    internal static Func<Statement, T> Reader<T>(Statement st)
    {
        IRowMapper<T>? m = RowMappers.Find<T>();
        if (m is not null)
        {
            int[] ord = m.Resolve(st);
            return s => m.Read(s, ord);
        }

        if (typeof(T).IsValueType || typeof(T) == typeof(string) || typeof(T) == typeof(byte[]) || typeof(T) == typeof(object))
        {
            return static s => s.GetFieldValue<T>(0);
        }

        throw new InvalidOperationException($"{typeof(T)} has no row mapper: mark the type with [OneLiteRow] (the build then generates one)");
    }
}
