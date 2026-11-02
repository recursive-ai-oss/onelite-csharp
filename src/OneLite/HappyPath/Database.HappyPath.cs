using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite;

// The happy path: one connection per Database, guarded by an async lock, with interpolated queries, WriteAsync and Table<T>.
public sealed partial class Database : ISession
{
    // Write transactions are BEGIN CONCURRENT when the database was opened for it (the happy-path options do, in WAL mode): a conflict is a
    // SQLITE_BUSY_SNAPSHOT at COMMIT, which WriteAsync retries. Otherwise they take the write lock up front with BEGIN IMMEDIATE.
    private string BeginWrite => _options.ConcurrentWrites && _options.JournalMode == JournalMode.Wal ? "BEGIN CONCURRENT" : "BEGIN IMMEDIATE";

    private static readonly AsyncLocal<Database?> s_inWrite = new();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Connection? _primary;
    private bool _closed;

    /// <summary>
    /// The options <see cref="OpenAsync"/> and <see cref="OpenDefault"/> use when you pass none: write-ahead log, <c>foreign_keys=ON</c>, and a 5 s busy timeout.
    /// </summary>
    /// <returns>New options.</returns>
    public static DatabaseOptions HappyPathOptions() => new() { JournalMode = JournalMode.Wal, ForeignKeys = true, BusyTimeoutMs = 5000, ConcurrentWrites = true };

    /// <summary>The most times <see cref="WriteAsync(Func{WriteTransaction, Task}, CancellationToken)"/> runs its delegate when the database is busy. Default 8; at least 1.</summary>
    public int MaxWriteAttempts { get; set; } = 8;

    /// <summary>
    /// Opens (creating if missing) a database for the happy path and returns once it is ready: write-ahead log, <c>foreign_keys=ON</c>, a 5 s busy timeout.
    /// Plain <see cref="Open"/> keeps the reference defaults instead (rollback journal, foreign keys off).
    /// <para>
    /// <b>Interpolated strings bind parameters.</b> <c>db.QueryAsync&lt;User&gt;($"select * from users where age &gt; {age}")</c> runs <c>... age &gt; ?1</c> with
    /// <c>age</c> bound; it never pastes the value into the SQL.
    /// </para>
    /// <para>One <see cref="Database"/> serializes its calls on one connection. For parallel readers, open more connections with <see cref="Connect"/>.</para>
    /// </summary>
    /// <param name="path">The database file, or ":memory:" for a private in-memory database.</param>
    /// <param name="options">Settings; null means <see cref="HappyPathOptions"/>. Options you pass are used as they are.</param>
    /// <param name="ct">Observed while the open waits for I/O.</param>
    /// <returns>The database.</returns>
    /// <exception cref="OneLiteException">SQLITE_CANTOPEN or another code if the file cannot be opened.</exception>
    public static async ValueTask<Database> OpenAsync(string path, DatabaseOptions? options = null, CancellationToken ct = default)
    {
        Database db = CreateHappy(path, options);
        try
        {
            db._primary = await db.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            db.Dispose();
            throw;
        }

        return db;
    }

    /// <summary>The blocking twin of <see cref="OpenAsync"/>.</summary>
    /// <param name="path">The database file, or ":memory:".</param>
    /// <param name="options">Settings; null means <see cref="HappyPathOptions"/>.</param>
    /// <returns>The database.</returns>
    public static Database OpenDefault(string path, DatabaseOptions? options = null)
    {
        Database db = CreateHappy(path, options);
        try
        {
            db._primary = db.Connect();
        }
        catch
        {
            db.Dispose();
            throw;
        }

        return db;
    }

    private static Database CreateHappy(string path, DatabaseOptions? options)
    {
        ArgumentNullException.ThrowIfNull(path);
        options ??= HappyPathOptions();
        // ":memory:" is handled by Open: a private in-memory database with no WAL.
        return Open(path, options);
    }

    private Connection Primary()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        return _primary ?? throw new InvalidOperationException("this Database was opened with Open; use OpenAsync or OpenDefault for the happy-path methods");
    }

    private void NotInsideWrite()
    {
        if (ReferenceEquals(s_inWrite.Value, this))
        {
            throw new InvalidOperationException("inside WriteAsync, use the transaction passed to the delegate, not the Database");
        }
    }

    private async ValueTask Enter(CancellationToken ct)
    {
        NotInsideWrite();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
    }

    private void DisposePrimary()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        if (_primary is { } c)
        {
            if (!c.AutoCommit)
            {
                try
                {
                    Runner.Script(c, "ROLLBACK");
                }
                catch (OneLiteException)
                {
                    // Closing releases the locks either way.
                }
            }

            c.Dispose();
        }
    }

    private async ValueTask DisposePrimaryAsync()
    {
        if (_closed)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_primary is { } c && !c.AutoCommit)
            {
                try
                {
                    await Runner.ScriptAsync(c, "ROLLBACK", CancellationToken.None).ConfigureAwait(false);
                }
                catch (OneLiteException)
                {
                    // Closing releases the locks either way.
                }
            }

            if (!_closed)
            {
                _closed = true;
                if (_primary is { } primary)
                {
                    await primary.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------ queries

    /// <summary>Runs a query and maps every row to <typeparamref name="T"/>. <b>Each <c>{hole}</c> in the interpolated string is bound as a parameter.</b></summary>
    /// <typeparam name="T">A type marked <see cref="OneLiteRowAttribute"/> (columns map by name), or a single value type such as <c>long</c> or <c>string</c> (the first column).</typeparam>
    /// <param name="sql">The statement as an interpolated string.</param>
    /// <param name="ct">Observed while the query waits for I/O or the connection.</param>
    /// <returns>The rows.</returns>
    /// <exception cref="OneLiteException">The statement failed; code and message are SQLite's.</exception>
    /// <exception cref="OneLiteMappingException">A row cannot be mapped; names the column and the member.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled; the connection stays usable.</exception>
    public ValueTask<List<T>> QueryAsync<T>(SqlHandler sql, CancellationToken ct = default) => ((ISession)this).QueryAsync<T>(sql.Build(), ct);

    /// <summary>The blocking twin of <see cref="QueryAsync{T}"/>.</summary>
    /// <typeparam name="T">See <see cref="QueryAsync{T}"/>.</typeparam>
    /// <param name="sql">The statement as an interpolated string; holes are bound.</param>
    /// <returns>The rows.</returns>
    public List<T> Query<T>(SqlHandler sql) => ((ISession)this).Query<T>(sql.Build());

    /// <summary>Runs a statement and returns the number of rows it changed (<c>changes()</c>; stale for statements that change nothing, as in SQLite). <b>Each hole is bound.</b></summary>
    /// <param name="sql">The statement as an interpolated string.</param>
    /// <param name="ct">Observed while the statement waits for I/O or the connection.</param>
    /// <returns>The rows changed.</returns>
    public ValueTask<long> ExecuteAsync(SqlHandler sql, CancellationToken ct = default) => Changes(((ISession)this).ExecuteAsync(sql.Build(), ct));

    private static async ValueTask<long> Changes(ValueTask<(long Changes, long LastRowId)> run) => (await run.ConfigureAwait(false)).Changes;

    /// <summary>The blocking twin of <see cref="ExecuteAsync(SqlHandler, CancellationToken)"/>.</summary>
    /// <param name="sql">The statement as an interpolated string; holes are bound.</param>
    /// <returns>The rows changed.</returns>
    public long Execute(SqlHandler sql) => ((ISession)this).Execute(sql.Build()).Changes;

    /// <summary>Runs a query and returns the first column of the first row, or default when there is no row. <b>Each hole is bound.</b></summary>
    /// <typeparam name="T">The value type (<c>long</c>, <c>string</c>, <c>double</c>, <c>Guid</c>, ...).</typeparam>
    /// <param name="sql">The statement as an interpolated string.</param>
    /// <param name="ct">Observed while the query waits for I/O or the connection.</param>
    /// <returns>The value.</returns>
    public ValueTask<T?> ScalarAsync<T>(SqlHandler sql, CancellationToken ct = default) => ((ISession)this).ScalarAsync<T>(sql.Build(), ct);

    /// <summary>The blocking twin of <see cref="ScalarAsync{T}"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="sql">The statement as an interpolated string; holes are bound.</param>
    /// <returns>The value.</returns>
    public T? Scalar<T>(SqlHandler sql) => ((ISession)this).Scalar<T>(sql.Build());

    /// <summary>Runs SQL text containing any number of statements, with no parameters. The text is run as written, so never build it from user input.</summary>
    /// <param name="sql">The statements.</param>
    /// <param name="ct">Observed while the script waits for I/O or the connection.</param>
    /// <returns>A task that completes when every statement has run.</returns>
    public async ValueTask ExecuteScriptAsync(string sql, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sql);
        await Enter(ct).ConfigureAwait(false);
        try
        {
            await Runner.ScriptAsync(Primary(), sql, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The blocking twin of <see cref="ExecuteScriptAsync"/>.</summary>
    /// <param name="sql">The statements, run as written.</param>
    public void ExecuteScript(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        NotInsideWrite();
        _gate.Wait();
        try
        {
            Runner.Script(Primary(), sql);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A thin typed view of a table: create-from-type, insert, upsert, get by key, delete, and <c>Where</c> with an SQL fragment. There is no LINQ and no change tracking.</summary>
    /// <typeparam name="T">A type marked <see cref="OneLiteRowAttribute"/>.</typeparam>
    /// <returns>The table view.</returns>
    public Table<T> Table<T>() => new(this);

    // ------------------------------------------------------------------ ISession (each call takes the lock)

    async ValueTask<List<T>> ISession.QueryAsync<T>(Query q, CancellationToken ct)
    {
        await Enter(ct).ConfigureAwait(false);
        try
        {
            return await Runner.QueryAsync<T>(Primary(), q, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    List<T> ISession.Query<T>(Query q)
    {
        NotInsideWrite();
        _gate.Wait();
        try
        {
            return Runner.Query<T>(Primary(), q);
        }
        finally
        {
            _gate.Release();
        }
    }

    async ValueTask<(long Changes, long LastRowId)> ISession.ExecuteAsync(Query q, CancellationToken ct)
    {
        await Enter(ct).ConfigureAwait(false);
        try
        {
            return await Runner.ExecuteAsync(Primary(), q, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    (long Changes, long LastRowId) ISession.Execute(Query q)
    {
        NotInsideWrite();
        _gate.Wait();
        try
        {
            return Runner.Execute(Primary(), q);
        }
        finally
        {
            _gate.Release();
        }
    }

    async ValueTask<T?> ISession.ScalarAsync<T>(Query q, CancellationToken ct) where T : default
    {
        await Enter(ct).ConfigureAwait(false);
        try
        {
            return await Runner.ScalarAsync<T>(Primary(), q, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    T? ISession.Scalar<T>(Query q) where T : default
    {
        NotInsideWrite();
        _gate.Wait();
        try
        {
            return Runner.Scalar<T>(Primary(), q);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------ search

    /// <summary>
    /// Hybrid search: ranks rows of the content table <paramref name="table"/> by full-text relevance (FTS5 bm25) and by vector closeness (<c>vec0</c> exact nearest neighbours), then
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
    public async ValueTask<List<SearchHit<T>>> SearchAsync<T>(string table, string? text, ReadOnlyMemory<float> embedding, int k = 10, SearchOptions? options = null, CancellationToken ct = default)
    {
        Searcher.Plan p = Searcher.Make(table, text, embedding, k, options, null);
        await Enter(ct).ConfigureAwait(false);
        try
        {
            return await Searcher.RunAsync<T>(Primary(), p, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Hybrid search: ranks rows of the content table <paramref name="table"/> by full-text relevance (FTS5 bm25) and by vector closeness (<c>vec0</c> exact nearest neighbours), then
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
        SearchPlanAsync<T>(Searcher.Make(table, text, embedding, k, options, where.Build()), ct);

    private async ValueTask<List<SearchHit<T>>> SearchPlanAsync<T>(Searcher.Plan p, CancellationToken ct)
    {
        await Enter(ct).ConfigureAwait(false);
        try
        {
            return await Searcher.RunAsync<T>(Primary(), p, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The blocking twin of <see cref="SearchAsync{T}(string, string, ReadOnlyMemory{float}, int, SearchOptions, CancellationToken)"/>.</summary>
    /// <typeparam name="T">The row type.</typeparam>
    /// <param name="table">The content table.</param>
    /// <param name="text">Words, or null.</param>
    /// <param name="embedding">The query vector, or empty.</param>
    /// <param name="k">How many rows at most.</param>
    /// <param name="options">Settings, or null.</param>
    /// <returns>The hits, best first.</returns>
    public List<SearchHit<T>> Search<T>(string table, string? text, ReadOnlyMemory<float> embedding, int k = 10, SearchOptions? options = null)
    {
        Searcher.Plan p = Searcher.Make(table, text, embedding, k, options, null);
        NotInsideWrite();
        _gate.Wait();
        try
        {
            return Searcher.Run<T>(Primary(), p);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The blocking twin of <see cref="SearchAsync{T}(string, string, ReadOnlyMemory{float}, int, SearchOptions, CancellationToken)"/>.</summary>
    /// <typeparam name="T">The row type.</typeparam>
    /// <param name="table">The content table.</param>
    /// <param name="text">Words, or null.</param>
    /// <param name="embedding">The query vector, or empty.</param>
    /// <param name="where">The filter, as in the async overload.</param>
    /// <param name="k">How many rows at most.</param>
    /// <param name="options">Settings, or null.</param>
    /// <returns>The hits, best first.</returns>
    public List<SearchHit<T>> Search<T>(string table, string? text, ReadOnlyMemory<float> embedding, SqlHandler where, int k = 10, SearchOptions? options = null)
    {
        Searcher.Plan p = Searcher.Make(table, text, embedding, k, options, where.Build());
        NotInsideWrite();
        _gate.Wait();
        try
        {
            return Searcher.Run<T>(Primary(), p);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------ write transactions

    /// <summary>
    /// Runs <paramref name="body"/> in a write transaction and commits it. If the database is busy (SQLITE_BUSY, including a busy snapshot), the transaction is
    /// rolled back and the delegate <b>runs again</b> after a short jittered wait, up to <see cref="MaxWriteAttempts"/> times, so
    /// <b>the delegate must have no side effects outside the database</b> (no sending mail, no counters, no state it cannot redo).
    /// An exception from the delegate rolls the transaction back and propagates. Use the <see cref="WriteTransaction"/> it receives, not the <see cref="Database"/>.
    /// </summary>
    /// <param name="body">The work.</param>
    /// <param name="ct">Observed while waiting for I/O, the lock and between attempts.</param>
    /// <returns>A task that completes when the transaction has committed.</returns>
    /// <exception cref="OneLiteException">The last busy error when the attempts run out, or the failure of a statement.</exception>
    public async Task WriteAsync(Func<WriteTransaction, Task> body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        await WriteAsync<object?>(async tx =>
        {
            await body(tx).ConfigureAwait(false);
            return null;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Like <see cref="WriteAsync(Func{WriteTransaction, Task}, CancellationToken)"/>, returning the delegate's result from the attempt that committed.</summary>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="body">The work. It may run more than once.</param>
    /// <param name="ct">Observed while waiting for I/O, the lock and between attempts.</param>
    /// <returns>The result.</returns>
    public async Task<TResult> WriteAsync<TResult>(Func<WriteTransaction, Task<TResult>> body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        int max = Math.Max(1, MaxWriteAttempts);
        for (int attempt = 1; ; attempt++)
        {
            (bool ok, TResult? result, OneLiteException? busy) = await AttemptAsync(body, ct).ConfigureAwait(false);
            if (ok)
            {
                return result!;
            }

            if (attempt >= max)
            {
                throw busy!;
            }

            await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
        }
    }

    private async Task<(bool Ok, TResult? Result, OneLiteException? Busy)> AttemptAsync<TResult>(Func<WriteTransaction, Task<TResult>> body, CancellationToken ct)
    {
        NotInsideWrite();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        Connection c;
        try
        {
            c = Primary();
        }
        catch
        {
            _gate.Release();
            throw;
        }

        WriteTransaction? tx = null;
        try
        {
            s_inWrite.Value = this;
            await Runner.ScriptAsync(c, BeginWrite, ct).ConfigureAwait(false);
            tx = new WriteTransaction(c);
            TResult r = await body(tx).ConfigureAwait(false);
            tx.Finish();
            await Runner.ScriptAsync(c, "COMMIT", ct).ConfigureAwait(false);
            return (true, r, null);
        }
        catch (OneLiteException e) when (e.Code == ResultCode.Busy)
        {
            await RollbackQuietly(c).ConfigureAwait(false);
            return (false, default, e);
        }
        catch
        {
            await RollbackQuietly(c).ConfigureAwait(false);
            throw;
        }
        finally
        {
            tx?.Finish();
            s_inWrite.Value = null;
            _gate.Release();
        }
    }

    private static async ValueTask RollbackQuietly(Connection c)
    {
        if (c.AutoCommit)
        {
            return;
        }

        try
        {
            await Runner.ScriptAsync(c, "ROLLBACK", CancellationToken.None).ConfigureAwait(false);
        }
        catch (OneLiteException)
        {
            // The original failure is the one worth reporting.
        }
    }

    /// <summary>The blocking twin of <see cref="WriteAsync{TResult}"/>. The delegate may run more than once and must have no side effects outside the database.</summary>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="body">The work.</param>
    /// <returns>The result.</returns>
    public TResult Write<TResult>(Func<WriteTransaction, TResult> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        NotInsideWrite();
        int max = Math.Max(1, MaxWriteAttempts);
        for (int attempt = 1; ; attempt++)
        {
            _gate.Wait();
            Connection c;
            try
            {
                c = Primary();
            }
            catch
            {
                _gate.Release();
                throw;
            }

            try
            {
                s_inWrite.Value = this;
                Runner.Script(c, BeginWrite);
                var tx = new WriteTransaction(c);
                try
                {
                    TResult r = body(tx);
                    tx.Finish();
                    Runner.Script(c, "COMMIT");
                    return r;
                }
                finally
                {
                    tx.Finish();
                }
            }
            catch (OneLiteException e) when (e.Code == ResultCode.Busy && attempt < max)
            {
                RollbackQuietlySync(c);
                s_inWrite.Value = null;
                _gate.Release();
                Thread.Sleep(Backoff(attempt));
                continue;
            }
            catch
            {
                RollbackQuietlySync(c);
                throw;
            }
            finally
            {
                if (s_inWrite.Value == this)
                {
                    s_inWrite.Value = null;
                    _gate.Release();
                }
            }
        }
    }

    /// <summary>The blocking twin of <see cref="WriteAsync(Func{WriteTransaction, Task}, CancellationToken)"/>.</summary>
    /// <param name="body">The work. It may run more than once and must have no side effects outside the database.</param>
    public void Write(Action<WriteTransaction> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        Write<object?>(tx =>
        {
            body(tx);
            return null;
        });
    }

    private static void RollbackQuietlySync(Connection c)
    {
        if (c.AutoCommit)
        {
            return;
        }

        try
        {
            Runner.Script(c, "ROLLBACK");
        }
        catch (OneLiteException)
        {
            // The original failure is the one worth reporting.
        }
    }

    // Exponential backoff with full jitter: attempt n waits a random time up to min(10 ms * 2^(n-1), 500 ms). The jitter only spreads out retries.
    private static TimeSpan Backoff(int attempt)
    {
        int cap = (int)Math.Min(500, 10L << Math.Min(attempt - 1, 6));
        return TimeSpan.FromMilliseconds(1 + Random.Shared.Next(cap));
    }

    // ------------------------------------------------------------------ migrations

    /// <summary>
    /// Brings the database up to date with an ordered list of SQL scripts, using <c>PRAGMA user_version</c> as the counter: script number <c>n</c> (1-based) runs
    /// when <c>user_version</c> is below <c>n</c>, and the version is set to <c>n</c> in the same transaction. All pending scripts commit together or not at all.
    /// A script may hold several statements and runs as written (no parameters).
    /// </summary>
    /// <param name="steps">The scripts, oldest first. Never reorder or edit one that has shipped; append.</param>
    /// <returns>A task that completes when the database is at version <c>steps.Length</c>.</returns>
    /// <exception cref="InvalidOperationException">The database's version is higher than the number of scripts (it was written by a newer program).</exception>
    public Task MigrateAsync(params string[] steps) => MigrateAsync(CancellationToken.None, steps);

    /// <summary>Like <see cref="MigrateAsync(string[])"/>, with a cancellation token.</summary>
    /// <param name="ct">Observed while the migration waits.</param>
    /// <param name="steps">The scripts, oldest first.</param>
    /// <returns>A task that completes when the database is up to date.</returns>
    public Task MigrateAsync(CancellationToken ct, params string[] steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return WriteAsync(async tx =>
        {
            long current = await tx.ScalarAsync<long>($"PRAGMA user_version", ct).ConfigureAwait(false);
            if (current > steps.Length)
            {
                throw new InvalidOperationException($"the database is at version {current} but only {steps.Length} migrations are known: it was written by a newer version of the program");
            }

            for (long n = current + 1; n <= steps.Length; n++)
            {
                await tx.ExecuteScriptAsync(steps[n - 1], ct).ConfigureAwait(false);
                await tx.ExecuteScriptAsync("PRAGMA user_version = " + n.ToString(System.Globalization.CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
            }
        }, ct);
    }

    /// <summary>The blocking twin of <see cref="MigrateAsync(string[])"/>.</summary>
    /// <param name="steps">The scripts, oldest first.</param>
    public void Migrate(params string[] steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        Write(tx =>
        {
            long current = tx.Scalar<long>($"PRAGMA user_version");
            if (current > steps.Length)
            {
                throw new InvalidOperationException($"the database is at version {current} but only {steps.Length} migrations are known: it was written by a newer version of the program");
            }

            for (long n = current + 1; n <= steps.Length; n++)
            {
                tx.ExecuteScript(steps[n - 1]);
                tx.ExecuteScript("PRAGMA user_version = " + n.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        });
    }
}
