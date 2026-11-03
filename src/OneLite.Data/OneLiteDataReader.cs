using System.Collections;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;

namespace RecursiveAI.OneLite.Data;

#pragma warning disable CA1010 // the ADO.NET base class fixes the non-generic collection shape
/// <summary>
/// Reads the result sets of a command. <see cref="ReadAsync(CancellationToken)"/> awaits the storage layer when a row needs a page that is not cached;
/// on a cached database it completes synchronously. Column getters read straight from the page cache: <see cref="GetUtf8Span"/> and
/// <see cref="GetBlobSpan"/> copy nothing, and stay valid until the next <c>Read</c>.
/// </summary>
public sealed class OneLiteDataReader : DbDataReader
{
    private readonly OneLiteConnection _connection;
    private readonly StatementSource _source;
    private readonly CommandBehavior _behavior;
    private Statement? _st;
    private bool _firstRowPending;
    private bool _hasRow;
    private bool _exhausted;
    private bool _closed;
    private long _recordsAffected = -1;
    private bool _hadRows;

    internal OneLiteDataReader(OneLiteConnection connection, StatementSource source, CommandBehavior behavior)
    {
        _connection = connection;
        _source = source;
        _behavior = behavior;
        connection.Track(this);
    }

    // ------------------------------------------------------------------ moving between statements

    // A statement without columns that still has rows (PRAGMA incremental_vacuum) does its work as it is stepped, and leaving it early would
    // abandon the work; closing a result set or moving past it runs it to the end, as it did before such rows were visible.
    private void DrainColumnless(Statement st)
    {
        if (st.ColumnCount == 0 && !_exhausted)
        {
            while (st.Step())
            {
            }

            _exhausted = true;
        }
    }

    private async ValueTask DrainColumnlessAsync(Statement st)
    {
        if (st.ColumnCount == 0 && !_exhausted)
        {
            while (await st.StepAsync(CancellationToken.None).ConfigureAwait(false))
            {
            }

            _exhausted = true;
        }
    }

    private void Finish(Statement st)
    {
        Count(st);
        st.Dispose();
    }

    private async ValueTask FinishAsync(Statement st)
    {
        Count(st);
        await st.DisposeAsync().ConfigureAwait(false);
    }

    private void Count(Statement st)
    {
        if (!st.IsReadOnly)
        {
            _recordsAffected = (_recordsAffected < 0 ? 0 : _recordsAffected) + _connection.Engine.ChangesCount;
        }
    }

    /// <summary>Runs statements that return no columns, and stops at the first that does (stepping once to its first row).</summary>
    internal void Start()
    {
        try
        {
            while (true)
            {
                Statement? st = _source.Next();
                if (st is null)
                {
                    _st = null;
                    return;
                }

                if (st.ColumnCount == 0 && (_behavior & CommandBehavior.SchemaOnly) == 0)
                {
                    // A statement without columns normally finishes on its first step. PRAGMA incremental_vacuum(N) instead returns one
                    // column-less row per page freed (as the shell does), so a first row makes this a result set of N rows and no columns.
                    _hasRow = st.Step();
                    if (!_hasRow)
                    {
                        Finish(st);
                        continue;
                    }

                    _st = st;
                    _hadRows = true;
                    _firstRowPending = true;
                    _exhausted = false;
                    return;
                }

                if (st.ColumnCount == 0)
                {
                    Finish(st);
                    continue;
                }

                _st = st;
                _exhausted = false;
                if ((_behavior & CommandBehavior.SchemaOnly) != 0)
                {
                    _hasRow = false;
                    _firstRowPending = false;
                    _exhausted = true;
                    return;
                }

                _hasRow = st.Step();
                _hadRows = _hasRow;
                _firstRowPending = true;
                _exhausted = !_hasRow;
                return;
            }
        }
        catch (OneLiteException e)
        {
            Abort();
            throw new OneLiteDataException(e);
        }
    }

    internal async ValueTask StartAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                Statement? st = await _source.NextAsync(ct).ConfigureAwait(false);
                if (st is null)
                {
                    _st = null;
                    return;
                }

                if (st.ColumnCount == 0 && (_behavior & CommandBehavior.SchemaOnly) == 0)
                {
                    // A statement without columns normally finishes on its first step. PRAGMA incremental_vacuum(N) instead returns one
                    // column-less row per page freed (as the shell does), so a first row makes this a result set of N rows and no columns.
                    _hasRow = await st.StepAsync(ct).ConfigureAwait(false);
                    if (!_hasRow)
                    {
                        await FinishAsync(st).ConfigureAwait(false);
                        continue;
                    }

                    _st = st;
                    _hadRows = true;
                    _firstRowPending = true;
                    _exhausted = false;
                    return;
                }

                if (st.ColumnCount == 0)
                {
                    await FinishAsync(st).ConfigureAwait(false);
                    continue;
                }

                _st = st;
                _exhausted = false;
                if ((_behavior & CommandBehavior.SchemaOnly) != 0)
                {
                    _hasRow = false;
                    _firstRowPending = false;
                    _exhausted = true;
                    return;
                }

                _hasRow = await st.StepAsync(ct).ConfigureAwait(false);
                _hadRows = _hasRow;
                _firstRowPending = true;
                _exhausted = !_hasRow;
                return;
            }
        }
        catch (OneLiteException e)
        {
            await AbortAsync().ConfigureAwait(false);
            throw new OneLiteDataException(e);
        }
        catch (OperationCanceledException)
        {
            await AbortAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask AbortAsync()
    {
        if (_st is { } st)
        {
            _st = null;
            await st.DisposeAsync().ConfigureAwait(false);
        }

        _closed = true;
        _connection.Untrack(this);
    }

    private void Abort()
    {
        _st?.Dispose();
        _st = null;
        _closed = true;
        _connection.Untrack(this);
    }

    /// <inheritdoc />
    public override bool NextResult()
    {
        ThrowIfClosed();
        if (_st is null || (_behavior & CommandBehavior.SingleResult) != 0)
        {
            return false;
        }

        Statement done = _st;
        _st = null;
        DrainColumnless(done);
        Finish(done);
        Start();
        return _st is not null;
    }

    /// <inheritdoc />
    public override async Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        if (_st is null || (_behavior & CommandBehavior.SingleResult) != 0)
        {
            return false;
        }

        Statement done = _st;
        _st = null;
        await DrainColumnlessAsync(done).ConfigureAwait(false);
        await FinishAsync(done).ConfigureAwait(false);
        await StartAsync(cancellationToken).ConfigureAwait(false);
        return _st is not null;
    }

    // ------------------------------------------------------------------ rows

    /// <inheritdoc />
    public override bool Read()
    {
        ThrowIfClosed();
        if (_st is null || _exhausted && !_firstRowPending)
        {
            return false;
        }

        if (_firstRowPending)
        {
            _firstRowPending = false;
            return _hasRow;
        }

        try
        {
            _hasRow = _st.Step();
        }
        catch (OneLiteException e)
        {
            throw new OneLiteDataException(e);
        }

        _exhausted = !_hasRow;
        return _hasRow;
    }

    /// <inheritdoc />
    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        ThrowIfClosed();
        if (_st is null || _exhausted && !_firstRowPending)
        {
            return Task.FromResult(false);
        }

        if (_firstRowPending)
        {
            _firstRowPending = false;
            return Task.FromResult(_hasRow);
        }

        ValueTask<bool> step;
        try
        {
            step = _st.StepAsync(cancellationToken);
        }
        catch (OneLiteException e)
        {
            throw new OneLiteDataException(e);
        }

        if (step.IsCompletedSuccessfully)
        {
            _hasRow = step.Result;
            _exhausted = !_hasRow;
            return _hasRow ? s_true : s_false;
        }

        return ReadSlow(step);
    }

    private static readonly Task<bool> s_true = Task.FromResult(true);
    private static readonly Task<bool> s_false = Task.FromResult(false);

    private async Task<bool> ReadSlow(ValueTask<bool> step)
    {
        try
        {
            _hasRow = await step.ConfigureAwait(false);
        }
        catch (OneLiteException e)
        {
            throw new OneLiteDataException(e);
        }

        _exhausted = !_hasRow;
        return _hasRow;
    }

    // ------------------------------------------------------------------ state

    private void ThrowIfClosed() => ObjectDisposedException.ThrowIf(_closed, this);

    private Statement Row()
    {
        ThrowIfClosed();
        if (_st is null || !_hasRow)
        {
            throw new InvalidOperationException("no current row: call Read first, and check that it returned true");
        }

        return _st;
    }

    private Statement Current() => _st ?? throw new InvalidOperationException("the reader has no result set");

    /// <inheritdoc />
    public override int Depth => 0;

    /// <inheritdoc />
    public override int FieldCount => _closed || _st is null ? 0 : _st.ColumnCount;

    /// <inheritdoc />
    public override bool HasRows => _st is not null && _hadRows;

    /// <inheritdoc />
    public override bool IsClosed => _closed;

    /// <summary>The rows changed by the INSERT, UPDATE and DELETE statements run so far (also those that matched nothing, which count 0); -1 if none ran.</summary>
    public override int RecordsAffected => (int)_recordsAffected;

    /// <inheritdoc />
    public override object this[int ordinal] => GetValue(ordinal);

    /// <inheritdoc />
    public override object this[string name] => GetValue(GetOrdinal(name));

    // ------------------------------------------------------------------ column getters

    /// <inheritdoc />
    public override string GetName(int ordinal)
    {
        if ((uint)ordinal >= (uint)FieldCount)
        {
#pragma warning disable CA2201 // DbDataReader's contract for a bad ordinal is IndexOutOfRangeException, as ADO.NET providers throw it
            throw new IndexOutOfRangeException();
#pragma warning restore CA2201
        }

        return Current().ColumnName(ordinal);
    }

    private int Check(int ordinal)
    {
        if ((uint)ordinal >= (uint)FieldCount)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "column ordinal out of range");
        }

        return ordinal;
    }

    /// <inheritdoc />
    public override int GetOrdinal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Statement st = Current();
        for (int i = 0; i < st.ColumnCount; i++)
        {
            if (string.Equals(st.ColumnName(i), name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        for (int i = 0; i < st.ColumnCount; i++)
        {
            if (string.Equals(st.ColumnName(i), name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(name), name, "no column has this name");
    }

    /// <summary>The storage class of the current value as an SQLite type name: INTEGER, REAL, TEXT, BLOB or NULL-as-empty.</summary>
    /// <param name="ordinal">0-based column index.</param>
    /// <returns>The type name of the current row's value in this column.</returns>
    public override string GetDataTypeName(int ordinal) => Row().ColumnType(Check(ordinal)) switch
    {
        ColumnType.Integral => "INTEGER",
        ColumnType.Real => "REAL",
        ColumnType.Text => "TEXT",
        _ => "BLOB",
    };

    /// <inheritdoc />
    [return: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields | System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)]
    public override Type GetFieldType(int ordinal) => Row().ColumnType(Check(ordinal)) switch
    {
        ColumnType.Integral => typeof(long),
        ColumnType.Real => typeof(double),
        ColumnType.Text => typeof(string),
        _ => typeof(byte[]),
    };

    /// <inheritdoc />
    public override bool IsDBNull(int ordinal) => Row().IsNull(Check(ordinal));

    /// <inheritdoc />
    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) => Task.FromResult(IsDBNull(ordinal));

    /// <inheritdoc />
    public override object GetValue(int ordinal) => Row().GetValue(Check(ordinal));

    /// <inheritdoc />
    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int n = Math.Min(values.Length, FieldCount);
        for (int i = 0; i < n; i++)
        {
            values[i] = GetValue(i);
        }

        return n;
    }

    /// <inheritdoc />
    public override T GetFieldValue<T>(int ordinal)
    {
        Statement st = Row();
        Check(ordinal);
        if (typeof(T) != typeof(object) && typeof(T) != typeof(DBNull) && st.IsNull(ordinal))
        {
            throw new InvalidOperationException($"The data is NULL at ordinal {ordinal}. This method can't be called on NULL values. Check using IsDBNull before calling.");
        }

        return st.GetFieldValue<T>(ordinal);
    }

    /// <inheritdoc />
    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) => Task.FromResult(GetFieldValue<T>(ordinal));

    /// <inheritdoc />
    public override bool GetBoolean(int ordinal) => GetFieldValue<bool>(ordinal);

    /// <inheritdoc />
    public override byte GetByte(int ordinal) => GetFieldValue<byte>(ordinal);

    /// <inheritdoc />
    public override char GetChar(int ordinal) => GetFieldValue<char>(ordinal);

    /// <inheritdoc />
    public override DateTime GetDateTime(int ordinal) => GetFieldValue<DateTime>(ordinal);

    /// <inheritdoc />
    public override decimal GetDecimal(int ordinal) => GetFieldValue<decimal>(ordinal);

    /// <inheritdoc />
    public override double GetDouble(int ordinal) => GetFieldValue<double>(ordinal);

    /// <inheritdoc />
    public override float GetFloat(int ordinal) => GetFieldValue<float>(ordinal);

    /// <inheritdoc />
    public override Guid GetGuid(int ordinal) => GetFieldValue<Guid>(ordinal);

    /// <inheritdoc />
    public override short GetInt16(int ordinal) => GetFieldValue<short>(ordinal);

    /// <inheritdoc />
    public override int GetInt32(int ordinal) => GetFieldValue<int>(ordinal);

    /// <inheritdoc />
    public override long GetInt64(int ordinal) => GetFieldValue<long>(ordinal);

    /// <inheritdoc />
    public override string GetString(int ordinal) => GetFieldValue<string>(ordinal);

    /// <summary>The current value's UTF-8 text without copying (numbers are rendered as SQLite renders them). Valid until the next <c>Read</c>.</summary>
    /// <param name="ordinal">0-based column index.</param>
    /// <returns>The bytes; empty for NULL.</returns>
    public ReadOnlySpan<byte> GetUtf8Span(int ordinal) => Row().GetTextUtf8(Check(ordinal));

    /// <summary>The current value's bytes without copying. Valid until the next <c>Read</c>.</summary>
    /// <param name="ordinal">0-based column index.</param>
    /// <returns>The bytes; empty for NULL.</returns>
    public ReadOnlySpan<byte> GetBlobSpan(int ordinal) => Row().GetBlob(Check(ordinal));

    /// <inheritdoc />
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        ReadOnlySpan<byte> all = GetBlobSpan(ordinal);
        if (buffer is null)
        {
            return all.Length;
        }

        if (dataOffset < 0 || dataOffset > all.Length)
        {
            return 0;
        }

        int n = (int)Math.Min(length, all.Length - dataOffset);
        all.Slice((int)dataOffset, n).CopyTo(buffer.AsSpan(bufferOffset));
        return n;
    }

    /// <inheritdoc />
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        string? s = Row().IsNull(Check(ordinal)) ? string.Empty : Row().GetString(ordinal);
        s ??= string.Empty;
        if (buffer is null)
        {
            return s.Length;
        }

        if (dataOffset < 0 || dataOffset > s.Length)
        {
            return 0;
        }

        int n = (int)Math.Min(length, s.Length - dataOffset);
        s.CopyTo((int)dataOffset, buffer, bufferOffset, n);
        return n;
    }

    /// <inheritdoc />
    public override Stream GetStream(int ordinal) => new MemoryStream(GetBlobSpan(ordinal).ToArray(), false);

    /// <inheritdoc />
    public override TextReader GetTextReader(int ordinal) => new StringReader(Row().IsNull(Check(ordinal)) ? string.Empty : Row().GetString(ordinal)!);

    /// <inheritdoc />
    public override IEnumerator GetEnumerator() => new DbEnumerator(this, false);

    /// <summary>A minimal schema table: name, ordinal, .NET type of the current value and type name for each column.</summary>
    /// <returns>The table.</returns>
    public override DataTable? GetSchemaTable()
    {
        if (_st is null)
        {
            return null;
        }

        var t = new DataTable("SchemaTable");
        t.Columns.Add("ColumnName", typeof(string));
        t.Columns.Add("ColumnOrdinal", typeof(int));
        t.Columns.Add("DataType", typeof(object)); // holds a Type; a column typed Type is not trim-safe
        t.Columns.Add("DataTypeName", typeof(string));
        t.Columns.Add("AllowDBNull", typeof(bool));
        for (int i = 0; i < _st.ColumnCount; i++)
        {
            bool known = _hasRow;
            t.Rows.Add(_st.ColumnName(i), i, known ? GetFieldType(i) : typeof(object), known ? GetDataTypeName(i) : string.Empty, true);
        }

        return t;
    }

    // ------------------------------------------------------------------ closing

    /// <inheritdoc />
    public override void Close() => CloseCore();

    // Closing a reader runs the statements that were not reached, as executing the command would have (a later INSERT in the text still happens).
    private void CloseCore()
    {
        if (_closed)
        {
            return;
        }

        OneLiteDataException? error = null;
        try
        {
            if (_st is { } st)
            {
                _st = null;
                DrainColumnless(st);
                Finish(st);
            }

            if ((_behavior & CommandBehavior.SingleResult) == 0)
            {
                while (_source.Next() is { } next)
                {
                    try
                    {
                        while (next.Step())
                        {
                        }
                    }
                    finally
                    {
                        Finish(next);
                    }
                }
            }
        }
        catch (OneLiteException e)
        {
            error = new OneLiteDataException(e);
        }

        Seal();
        if (error is not null)
        {
            throw error;
        }
    }

    private async ValueTask CloseCoreAsync()
    {
        if (_closed)
        {
            return;
        }

        OneLiteDataException? error = null;
        try
        {
            if (_st is { } st)
            {
                _st = null;
                await DrainColumnlessAsync(st).ConfigureAwait(false);
                await FinishAsync(st).ConfigureAwait(false);
            }

            if ((_behavior & CommandBehavior.SingleResult) == 0)
            {
                while (await _source.NextAsync(CancellationToken.None).ConfigureAwait(false) is { } next)
                {
                    try
                    {
                        while (await next.StepAsync(CancellationToken.None).ConfigureAwait(false))
                        {
                        }
                    }
                    finally
                    {
                        await FinishAsync(next).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OneLiteException e)
        {
            error = new OneLiteDataException(e);
        }

        Seal();
        if (error is not null)
        {
            throw error;
        }
    }

    private void Seal()
    {
        _closed = true;
        _connection.Untrack(this);
        if ((_behavior & CommandBehavior.CloseConnection) != 0)
        {
            _connection.Close();
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CloseCore();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async Task CloseAsync() => await CloseCoreAsync().ConfigureAwait(false);

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await CloseCoreAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
#pragma warning restore CA1010
