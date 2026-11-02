using System.Linq;
using System.Text;

namespace RecursiveAI.OneLite;

/// <summary>
/// A thin typed view of one table, built on the generated row mapper: create it from the type, insert, upsert, get by key, delete, and select with an SQL fragment.
/// There is no LINQ provider and no change tracking. Tables it creates are <c>STRICT</c>; raw SQL creates exactly what it says.
/// <b>Interpolated strings bind parameters</b>, here too.
/// </summary>
/// <typeparam name="T">A type marked <see cref="OneLiteRowAttribute"/>.</typeparam>
public sealed class Table<T>
{
    private readonly ISession _session;
    private readonly IRowMapper<T> _map;
    private string? _columnList;

    internal Table(ISession session)
    {
        _session = session;
        _map = RowMappers.Get<T>();
    }

    /// <summary>The table name (from <see cref="OneLiteRowAttribute.TableName"/>, or the type name lower-cased with an "s").</summary>
    public string Name => _map.TableName;

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private string ColumnList => _columnList ??= string.Join(", ", _map.Columns.Select(c => Quote(c.Name)));

    private string CreateSql()
    {
        var sb = new StringBuilder("CREATE TABLE IF NOT EXISTS ").Append(Quote(Name)).Append(" (");
        int keys = _map.Columns.Count(c => c.IsKey);
        for (int i = 0; i < _map.Columns.Count; i++)
        {
            RowColumn c = _map.Columns[i];
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(Quote(c.Name)).Append(' ').Append(c.SqlType);
            if (c.IsKey && keys == 1)
            {
                sb.Append(" PRIMARY KEY");
            }
            else if (c.NotNull)
            {
                sb.Append(" NOT NULL");
            }
        }

        if (keys > 1)
        {
            sb.Append(", PRIMARY KEY (").Append(string.Join(", ", _map.Columns.Where(c => c.IsKey).Select(c => Quote(c.Name)))).Append(')');
        }

        return sb.Append(") STRICT").ToString();
    }

    private string Placeholders() => string.Join(", ", Enumerable.Range(1, _map.Columns.Count).Select(i => "?" + i));

    private string InsertSql() => $"INSERT INTO {Quote(Name)} ({ColumnList}) VALUES ({Placeholders()})";

    private string UpsertSql()
    {
        RowColumn[] keys = _map.Columns.Where(c => c.IsKey).ToArray();
        if (keys.Length == 0)
        {
            throw new InvalidOperationException($"{typeof(T)} has no key: mark a property [OneLiteKey] or name it Id");
        }

        string sets = string.Join(", ", _map.Columns.Where(c => !c.IsKey).Select(c => Quote(c.Name) + " = excluded." + Quote(c.Name)));
        return InsertSql() + " ON CONFLICT (" + string.Join(", ", keys.Select(c => Quote(c.Name))) + ") DO " + (sets.Length == 0 ? "NOTHING" : "UPDATE SET " + sets);
    }

    private Query KeyQuery(string prefix, object? key)
    {
        RowColumn[] keys = _map.Columns.Where(c => c.IsKey).ToArray();
        if (keys.Length != 1)
        {
            throw new NotSupportedException($"{typeof(T)} must have exactly one key property for lookups by key");
        }

        return new Query(prefix + Quote(keys[0].Name) + " = ?1", [key]);
    }

    /// <summary>Creates the table if it does not exist: a <c>STRICT</c> table with a column per mapped property (integers and booleans INTEGER, floats REAL,
    /// strings, <c>Guid</c>, dates and <c>decimal</c> TEXT, <c>byte[]</c> BLOB), NOT NULL where the C# type cannot be null, and the key as PRIMARY KEY.</summary>
    /// <param name="ct">Observed while the statement waits for I/O.</param>
    /// <returns>A task that completes when the table exists.</returns>
    public async ValueTask EnsureCreatedAsync(CancellationToken ct = default) =>
        await _session.ExecuteAsync(new Query(CreateSql(), []), ct).ConfigureAwait(false);

    /// <summary>The blocking twin of <see cref="EnsureCreatedAsync"/>.</summary>
    public void EnsureCreated() => _session.Execute(new Query(CreateSql(), []));

    /// <summary>Inserts a row. A single integer key with the value 0 is left for the database to choose.</summary>
    /// <param name="row">The row.</param>
    /// <param name="ct">Observed while the statement waits for I/O.</param>
    /// <returns>The rowid of the new row (<c>last_insert_rowid()</c>).</returns>
    /// <exception cref="OneLiteException">A constraint failed (SQLITE_CONSTRAINT with its extended code), for example a duplicate key.</exception>
    public async ValueTask<long> InsertAsync(T row, CancellationToken ct = default) =>
        (await _session.ExecuteAsync(new Query(InsertSql(), [], st => _map.Bind(st, 1, row)), ct).ConfigureAwait(false)).LastRowId;

    /// <summary>The blocking twin of <see cref="InsertAsync"/>.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The rowid of the new row.</returns>
    public long Insert(T row) => _session.Execute(new Query(InsertSql(), [], st => _map.Bind(st, 1, row))).LastRowId;

    /// <summary>Inserts the row, or updates every non-key column of the row with the same key (<c>INSERT ... ON CONFLICT (key) DO UPDATE</c>).</summary>
    /// <param name="row">The row.</param>
    /// <param name="ct">Observed while the statement waits for I/O.</param>
    /// <returns>A task that completes when the row is stored.</returns>
    /// <exception cref="InvalidOperationException">The type has no key.</exception>
    public async ValueTask UpsertAsync(T row, CancellationToken ct = default) =>
        await _session.ExecuteAsync(new Query(UpsertSql(), [], st => _map.Bind(st, 1, row)), ct).ConfigureAwait(false);

    /// <summary>The blocking twin of <see cref="UpsertAsync"/>.</summary>
    /// <param name="row">The row.</param>
    public void Upsert(T row) => _session.Execute(new Query(UpsertSql(), [], st => _map.Bind(st, 1, row)));

    /// <summary>Gets the row with the given key.</summary>
    /// <typeparam name="TKey">The key's type.</typeparam>
    /// <param name="key">The key value (bound as a parameter).</param>
    /// <param name="ct">Observed while the query waits for I/O.</param>
    /// <returns>The row, or default when there is none.</returns>
    /// <exception cref="NotSupportedException">The type does not have exactly one key property.</exception>
    public async ValueTask<T?> GetAsync<TKey>(TKey key, CancellationToken ct = default)
    {
        Query q = KeyQuery($"SELECT {ColumnList} FROM {Quote(Name)} WHERE ", key);
        List<T> rows = await _session.QueryAsync<T>(q, ct).ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : default;
    }

    /// <summary>The blocking twin of <see cref="GetAsync{TKey}"/>.</summary>
    /// <typeparam name="TKey">The key's type.</typeparam>
    /// <param name="key">The key value.</param>
    /// <returns>The row, or default.</returns>
    public T? Get<TKey>(TKey key)
    {
        List<T> rows = _session.Query<T>(KeyQuery($"SELECT {ColumnList} FROM {Quote(Name)} WHERE ", key));
        return rows.Count > 0 ? rows[0] : default;
    }

    /// <summary>Deletes the row with the given key.</summary>
    /// <typeparam name="TKey">The key's type.</typeparam>
    /// <param name="key">The key value.</param>
    /// <param name="ct">Observed while the statement waits for I/O.</param>
    /// <returns>True if a row was deleted.</returns>
    public async ValueTask<bool> DeleteAsync<TKey>(TKey key, CancellationToken ct = default) =>
        (await _session.ExecuteAsync(KeyQuery($"DELETE FROM {Quote(Name)} WHERE ", key), ct).ConfigureAwait(false)).Changes > 0;

    /// <summary>Selects the rows matching an SQL fragment. <b>Each hole is bound as a parameter:</b>
    /// <c>Where($"age &gt; {18} order by name")</c> runs <c>select ... from users where age &gt; ?1 order by name</c>.</summary>
    /// <param name="fragment">Everything after <c>WHERE</c>, as an interpolated string; an empty fragment selects every row.</param>
    /// <param name="ct">Observed while the query waits for I/O.</param>
    /// <returns>The rows.</returns>
    public ValueTask<List<T>> WhereAsync(SqlHandler fragment, CancellationToken ct = default) => _session.QueryAsync<T>(WhereQuery(fragment.Build()), ct);

    /// <summary>The blocking twin of <see cref="WhereAsync"/>.</summary>
    /// <param name="fragment">Everything after <c>WHERE</c>, as an interpolated string; holes are bound.</param>
    /// <returns>The rows.</returns>
    public List<T> Where(SqlHandler fragment) => _session.Query<T>(WhereQuery(fragment.Build()));

    private Query WhereQuery(Query fragment)
    {
        string head = $"SELECT {ColumnList} FROM {Quote(Name)}";
        return new Query(string.IsNullOrWhiteSpace(fragment.Sql) ? head : head + " WHERE " + fragment.Sql, fragment.Args);
    }
}
