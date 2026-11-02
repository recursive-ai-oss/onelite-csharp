namespace RecursiveAI.OneLite;

/// <summary>
/// The open write transaction a <see cref="Database.WriteAsync(Func{WriteTransaction, Task}, CancellationToken)"/> delegate works in. It has the same query methods as
/// <see cref="Database"/>, and <b>each <c>{hole}</c> of an interpolated string is a bound parameter</b>. It is valid only inside the delegate.
/// </summary>
public sealed class WriteTransaction : ISession
{
    private readonly Connection _conn;
    private bool _finished;

    internal WriteTransaction(Connection conn) => _conn = conn;

    internal void Finish() => _finished = true;

    private Connection C()
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        return _conn;
    }

    /// <summary>Runs a query inside the transaction and maps every row. See <see cref="Database.QueryAsync{T}"/>.</summary>
    /// <typeparam name="T">A <see cref="OneLiteRowAttribute"/> type, or a single value type.</typeparam>
    /// <param name="sql">The statement as an interpolated string; holes are bound.</param>
    /// <param name="ct">Observed while the query waits for I/O.</param>
    /// <returns>The rows.</returns>
    public ValueTask<List<T>> QueryAsync<T>(SqlHandler sql, CancellationToken ct = default) => Runner.QueryAsync<T>(C(), sql.Build(), ct);

    /// <summary>The blocking twin of <see cref="QueryAsync{T}"/>.</summary>
    /// <typeparam name="T">See <see cref="QueryAsync{T}"/>.</typeparam>
    /// <param name="sql">The statement as an interpolated string; holes are bound.</param>
    /// <returns>The rows.</returns>
    public List<T> Query<T>(SqlHandler sql) => Runner.Query<T>(C(), sql.Build());

    /// <summary>Runs a statement inside the transaction; returns the rows it changed. See <see cref="Database.ExecuteAsync(SqlHandler, CancellationToken)"/>.</summary>
    /// <param name="sql">The statement as an interpolated string; holes are bound.</param>
    /// <param name="ct">Observed while the statement waits for I/O.</param>
    /// <returns>The rows changed.</returns>
    public ValueTask<long> ExecuteAsync(SqlHandler sql, CancellationToken ct = default) => ChangesOf(Runner.ExecuteAsync(C(), sql.Build(), ct));

    private static async ValueTask<long> ChangesOf(ValueTask<(long Changes, long LastRowId)> run) => (await run.ConfigureAwait(false)).Changes;

    /// <summary>The blocking twin of <see cref="ExecuteAsync(SqlHandler, CancellationToken)"/>.</summary>
    /// <param name="sql">The statement as an interpolated string; holes are bound.</param>
    /// <returns>The rows changed.</returns>
    public long Execute(SqlHandler sql) => Runner.Execute(C(), sql.Build()).Changes;

    /// <summary>Returns the first column of the first row, or default. See <see cref="Database.ScalarAsync{T}"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="sql">The statement as an interpolated string; holes are bound.</param>
    /// <param name="ct">Observed while the query waits for I/O.</param>
    /// <returns>The value.</returns>
    public ValueTask<T?> ScalarAsync<T>(SqlHandler sql, CancellationToken ct = default) => Runner.ScalarAsync<T>(C(), sql.Build(), ct);

    /// <summary>The blocking twin of <see cref="ScalarAsync{T}"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="sql">The statement as an interpolated string; holes are bound.</param>
    /// <returns>The value.</returns>
    public T? Scalar<T>(SqlHandler sql) => Runner.Scalar<T>(C(), sql.Build());

    /// <summary>Runs SQL text with any number of statements as written (no parameters; never build it from user input).</summary>
    /// <param name="sql">The statements.</param>
    /// <param name="ct">Observed while the script waits for I/O.</param>
    /// <returns>A task that completes when every statement has run.</returns>
    public ValueTask ExecuteScriptAsync(string sql, CancellationToken ct = default) => Runner.ScriptAsync(C(), sql, ct);

    /// <summary>The blocking twin of <see cref="ExecuteScriptAsync"/>.</summary>
    /// <param name="sql">The statements, run as written.</param>
    public void ExecuteScript(string sql) => Runner.Script(C(), sql);

    /// <summary>A typed table view that runs inside this transaction.</summary>
    /// <typeparam name="T">A type marked <see cref="OneLiteRowAttribute"/>.</typeparam>
    /// <returns>The table view.</returns>
    public Table<T> Table<T>() => new(this);

    /// <summary>
    /// Hybrid search inside this transaction (it sees the transaction's own writes). See <see cref="Database.SearchAsync{T}(string, string, ReadOnlyMemory{float}, int, SearchOptions, CancellationToken)"/>. Hybrid search: ranks rows of the content table <paramref name="table"/> by full-text relevance (FTS5 bm25) and by vector closeness (<c>vec0</c> exact nearest neighbours), then
    /// fuses the two lists with reciprocal rank fusion (score = sum of <c>1 / (60 + rank)</c> over the lists a row is in). Give text, an embedding, or both.
    /// The text is searched as plain words, never as FTS5 syntax. Index tables follow <see cref="SearchOptions"/>. <b>Interpolated strings bind parameters</b>; the table names are
    /// ordinary strings used as trusted identifiers, so do not build them from user input.
    /// </summary>
    /// <typeparam name="T">A type marked <see cref="OneLiteRowAttribute"/>; its columns are read from the content table by name.</typeparam>
    /// <param name="table">The content table, a rowid table whose rowids are the ids in the index tables.</param>
    /// <param name="text">Words to search for, or null for a vector-only search.</param>
    /// <param name="embedding">The query vector (float32, the dimension of the vec0 column), or empty for a text-only search.</param>
    /// <param name="k">How many rows to return at most. Default 10.</param>
    /// <param name="options">Index names, weights, the fusion constant and list depth; null means the defaults.</param>
    /// <param name="ct">Observed while the search waits for I/O or the connection.</param>
    /// <returns>Up to <paramref name="k"/> hits, best first; equal scores order by row id.</returns>
    /// <exception cref="ArgumentException">Neither text nor an embedding was given.</exception>
    /// <exception cref="OneLiteException">An index table is missing or the vector has the wrong dimension (SQLite's code and message).</exception>
    public ValueTask<List<SearchHit<T>>> SearchAsync<T>(string table, string? text, ReadOnlyMemory<float> embedding, int k = 10, SearchOptions? options = null, CancellationToken ct = default) =>
        Searcher.RunAsync<T>(C(), Searcher.Make(table, text, embedding, k, options, null), ct);

    /// <summary>
    /// Hybrid search with a filter: ranks rows of the content table <paramref name="table"/> by full-text relevance (FTS5 bm25) and by vector closeness (<c>vec0</c> exact nearest neighbours), then
    /// fuses the two lists with reciprocal rank fusion (score = sum of <c>1 / (60 + rank)</c> over the lists a row is in). Give text, an embedding, or both.
    /// The text is searched as plain words, never as FTS5 syntax. Index tables follow <see cref="SearchOptions"/>. <b>Interpolated strings bind parameters</b>; the table names are
    /// ordinary strings used as trusted identifiers, so do not build them from user input.
    /// </summary>
    /// <typeparam name="T">A type marked <see cref="OneLiteRowAttribute"/>; its columns are read from the content table by name.</typeparam>
    /// <param name="table">The content table, a rowid table whose rowids are the ids in the index tables.</param>
    /// <param name="text">Words to search for, or null for a vector-only search.</param>
    /// <param name="embedding">The query vector (float32, the dimension of the vec0 column), or empty for a text-only search.</param>
    /// <param name="where">A condition on the content table's columns as an interpolated string (holes are bound), applied inside both lists, so the result holds the best rows that satisfy it.</param>
    /// <param name="k">How many rows to return at most. Default 10.</param>
    /// <param name="options">Index names, weights, the fusion constant and list depth; null means the defaults.</param>
    /// <param name="ct">Observed while the search waits for I/O or the connection.</param>
    /// <returns>Up to <paramref name="k"/> hits, best first; equal scores order by row id.</returns>
    /// <exception cref="ArgumentException">Neither text nor an embedding was given.</exception>
    /// <exception cref="OneLiteException">An index table is missing or the vector has the wrong dimension (SQLite's code and message).</exception>
    public ValueTask<List<SearchHit<T>>> SearchAsync<T>(string table, string? text, ReadOnlyMemory<float> embedding, SqlHandler where, int k = 10, SearchOptions? options = null, CancellationToken ct = default) =>
        Searcher.RunAsync<T>(C(), Searcher.Make(table, text, embedding, k, options, where.Build()), ct);

    /// <summary>The blocking twin of <see cref="SearchAsync{T}(string, string, ReadOnlyMemory{float}, int, SearchOptions, CancellationToken)"/>.</summary>
    /// <typeparam name="T">The row type.</typeparam>
    /// <param name="table">The content table.</param>
    /// <param name="text">Words, or null.</param>
    /// <param name="embedding">The query vector, or empty.</param>
    /// <param name="k">How many rows at most.</param>
    /// <param name="options">Settings, or null.</param>
    /// <returns>The hits, best first.</returns>
    public List<SearchHit<T>> Search<T>(string table, string? text, ReadOnlyMemory<float> embedding, int k = 10, SearchOptions? options = null) =>
        Searcher.Run<T>(C(), Searcher.Make(table, text, embedding, k, options, null));

    ValueTask<List<T>> ISession.QueryAsync<T>(Query q, CancellationToken ct) => Runner.QueryAsync<T>(C(), q, ct);

    List<T> ISession.Query<T>(Query q) => Runner.Query<T>(C(), q);

    ValueTask<(long Changes, long LastRowId)> ISession.ExecuteAsync(Query q, CancellationToken ct) => Runner.ExecuteAsync(C(), q, ct);

    (long Changes, long LastRowId) ISession.Execute(Query q) => Runner.Execute(C(), q);

    ValueTask<T?> ISession.ScalarAsync<T>(Query q, CancellationToken ct) where T : default => Runner.ScalarAsync<T>(C(), q, ct);

    T? ISession.Scalar<T>(Query q) where T : default => Runner.Scalar<T>(C(), q);
}
