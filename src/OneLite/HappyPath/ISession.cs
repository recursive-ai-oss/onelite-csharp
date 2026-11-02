namespace RecursiveAI.OneLite;

/// <summary>What <see cref="Table{T}"/> needs from the thing it runs on: the database (each call takes its lock) or a write transaction.</summary>
internal interface ISession
{
    ValueTask<List<T>> QueryAsync<T>(Query q, CancellationToken ct);

    List<T> Query<T>(Query q);

    ValueTask<(long Changes, long LastRowId)> ExecuteAsync(Query q, CancellationToken ct);

    (long Changes, long LastRowId) Execute(Query q);

    ValueTask<T?> ScalarAsync<T>(Query q, CancellationToken ct);

    T? Scalar<T>(Query q);
}
