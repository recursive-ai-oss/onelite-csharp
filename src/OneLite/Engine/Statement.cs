using RecursiveAI.OneLite.Planning;
using System.Text;
using RecursiveAI.OneLite.Sql.Parsing;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite;

/// <summary>The outcome of <see cref="Statement.Advance"/>.</summary>
public enum StepStatus : byte
{
    /// <summary>A result row is ready; read it with the column readers. It stays valid until the next call to <see cref="Statement.Advance"/>.</summary>
    Row,

    /// <summary>The statement ran to completion.</summary>
    Done,

    /// <summary>One I/O is outstanding (<see cref="Statement.PendingIo"/>): run it, call <see cref="Statement.Complete"/> and call <see cref="Statement.Advance"/> again.</summary>
    Pending,

    /// <summary>The statement failed; see <see cref="Statement.Error"/>. <see cref="Statement.Reset"/> before running it again.</summary>
    Error,
}

/// <summary>The storage class of a result column value; the numbers are SQLite's SQLITE_INTEGER, SQLITE_FLOAT, SQLITE_TEXT, SQLITE_BLOB and SQLITE_NULL.</summary>
public enum ColumnType
{
    /// <summary>A 64-bit integer.</summary>
    Integral = 1,

    /// <summary>An 8-byte float.</summary>
    Real = 2,

    /// <summary>Text.</summary>
    Text = 3,

    /// <summary>A blob.</summary>
    Blob = 4,

    /// <summary>NULL.</summary>
    Null = 5,
}

/// <summary>
/// A compiled SQL statement. Run it with <see cref="Step"/> (blocking driver) or with <see cref="Advance"/> and the I/O slot
/// (<see cref="PendingIo"/>, <see cref="Complete"/>), which never blocks and never awaits.
/// <para>
/// Supported SQL: <c>SELECT</c> from one table or none, with expressions, <c>WHERE</c>, <c>ORDER BY</c>, <c>LIMIT</c>/<c>OFFSET</c> and
/// <c>count(*)</c>; see docs/engine.md. Everything else fails in <see cref="Connection.Prepare(string)"/> with an "unsupported" error.
/// </para>
/// <para>Parameters: <c>?</c>, <c>?NNN</c>, <c>:name</c>, <c>@name</c> and <c>$name</c>; bind them with the <c>Bind</c> methods before the first step. Unbound parameters are NULL.</para>
/// <para>Column readers follow the C API's conversions. Text and blob spans point into the page cache or the statement's registers and are valid until the next <see cref="Advance"/>, <see cref="Step"/> or <see cref="Reset"/>.</para>
/// </summary>
public sealed class Statement : IDisposable, IAsyncDisposable
{
    private enum Phase : byte
    {
        Ready,
        Running,
        Reprepare,
        Done,
        Failed,
    }

    private readonly Connection _conn;
    private readonly string _sql;
    private VmProgram _prog;
    private Machine _machine;
    private Mem[] _vars;
    private byte[]?[] _varBytes;
    private Phase _phase;
    private bool _pending;
    private bool _disposed;
    private OneLiteException? _error;
    private byte[]?[] _convert;

    private readonly PlanMode _planMode;

    /// <summary>How many row counters the program carries (0 unless it was prepared to count).</summary>
    internal int PlanCounterCount => _prog.PlanCounterCount;
    private QueryPlan? _recordedPlan;

    internal Statement(Connection conn, string sql, PlanMode planMode = PlanMode.Off)
    {
        _conn = conn;
        _sql = sql;
        _planMode = planMode;
        string tail;
        try
        {
            _prog = conn.CompileSql(sql, out tail, out _, planMode);
        }
        catch (OneLiteException e)
        {
            Diagnostics.ErrorDiagnostics.Enrich(e, sql, conn.Catalog);
            throw;
        }

        Tail = tail;
        _vars = new Mem[_prog.ParameterNames.Length];
        _varBytes = new byte[_prog.ParameterNames.Length][];
        _machine = new Machine(conn, _prog, _vars) { _owner = this };
        _convert = new byte[_prog.ResultCount][];
        if (_prog.Code.Length == 1 && _prog.Code[0].Op == Op.Halt)
        {
            _phase = Phase.Ready;
        }
    }

    /// <summary>
    /// True if the statement cannot change the database file (it opens no table for writing and runs no DDL), as <c>sqlite3_stmt_readonly</c> reports.
    /// <c>BEGIN</c>, <c>COMMIT</c> and <c>ROLLBACK</c> count as read-only.
    /// </summary>
    public bool IsReadOnly
    {
        get
        {
            foreach (Instr i in _prog.Code)
            {
                switch (i.Op)
                {
                    case Op.OpenWrite:
                    case Op.Insert:
                    case Op.Delete:
                    case Op.IdxInsert:
                    case Op.IdxDelete:
                    case Op.CreateBtree:
                    case Op.Destroy:
                    case Op.ClearTree:
                    case Op.BumpCookie:
                    case Op.SchemaDelete:
                        return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// How this statement runs, as a tree: the loops and the index each uses and why, joins in their order, subqueries, sorts and aggregation. It is
    /// built from the decisions the compiler took for this very program. Null for statements that have no plan (schema changes, pragmas, transaction control).
    /// For a statement prepared with <c>Prepare(sql, collectActualRows: true)</c>, the nodes also carry the rows and loop counts so far (the counters run on
    /// from one execution to the next until the statement is disposed); otherwise the plan is made on first use, by compiling the text again,
    /// and has estimates only. Plans are not part of SQLite compatibility.
    /// </summary>
    public QueryPlan? Plan
    {
        get
        {
            if (_prog.Plan is { } p)
            {
                if (_prog.PlanCounterCount > 0)
                {
                    p.Fill(_machine.PlanCounts);
                }

                return p;
            }

            if (_recordedPlan is null && _planMode == PlanMode.Off)
            {
                try
                {
                    _recordedPlan = _conn.CompileSql(_sql, out _, out _, PlanMode.Record).Plan;
                }
                catch (OneLiteException)
                {
                    return null;
                }
            }

            return _recordedPlan;
        }
    }

    /// <summary>The SQL text after the first statement (empty if there is none).</summary>
    public string Tail { get; }

    /// <summary>Number of result columns.</summary>
    public int ColumnCount => _prog.ResultCount;

    /// <summary>The name of result column <paramref name="index"/> as SQLite names it (the alias, the column name, or the expression text).</summary>
    /// <param name="index">0-based column index.</param>
    /// <returns>The name.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the result.</exception>
    public string ColumnName(int index) => _prog.ColumnNames[index];

    /// <summary>The error behind <see cref="StepStatus.Error"/>, or null.</summary>
    public OneLiteException? Error => _error;

    /// <summary>The outstanding I/O request while <see cref="Advance"/> has returned <see cref="StepStatus.Pending"/>. Valid until <see cref="Complete"/>.</summary>
    public ref readonly IoRequest PendingIo => ref _conn.Pager.PendingIo;

    // ------------------------------------------------------------------ the step contract

    /// <summary>
    /// Runs the statement until it has a row, finishes, fails, or needs a page that is not cached. Never blocks and never awaits.
    /// When it returns <see cref="StepStatus.Pending"/> the instruction that needed the page has not been consumed and no output has changed:
    /// run <see cref="PendingIo"/>, pass the result to <see cref="Complete"/>, and call this method again.
    /// After <see cref="StepStatus.Done"/> or <see cref="StepStatus.Error"/> the next call starts the statement again from the beginning (as sqlite3_step does).
    /// </summary>
    /// <returns>Row, Done, Pending or Error.</returns>
    /// <exception cref="ObjectDisposedException">The statement was disposed.</exception>
    public StepStatus Advance()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (; ; )
        {
            switch (_phase)
            {
                case Phase.Failed:
                    // c3ref/step: since 3.6.23.1 a step after an error resets the statement first and runs it again, so a statement refused
                    // with SQLITE_BUSY can simply be stepped once the other connection is done.
                    ReleaseBlocked(keepHold: true);
                    _machine.ReleaseForReset();
                    _machine.Rewind();
                    _error = null;
                    _phase = Phase.Ready;
                    continue;
                case Phase.Done:
                    _machine.Rewind();
                    _phase = Phase.Ready;
                    continue;
                case Phase.Reprepare:
                {
                    RunResult rr;
                    try
                    {
                        rr = _conn.Catalog is null || _conn.SchemaReloading ? _conn.ReloadSchemaStep() : RunResult.Done;
                    }
                    catch (OneLiteException e)
                    {
                        return Fail(e);
                    }

                    if (rr == RunResult.Pending)
                    {
                        _pending = true;
                        return StepStatus.Pending;
                    }

                    try
                    {
                        Recompile();
                    }
                    catch (OneLiteException e)
                    {
                        return Fail(Diagnostics.ErrorDiagnostics.Enrich(e, _sql, _conn.Catalog));
                    }

                    _phase = Phase.Running;
                    continue;
                }

                default:
                {
                    if (_prog.Code.Length == 1 && _prog.Code[0].Op == Op.Halt)
                    {
                        _phase = Phase.Done;
                        return StepStatus.Done;
                    }

                    _phase = Phase.Running;
                    RunResult r = _machine.Run();
                    switch (r)
                    {
                        case RunResult.Row:
                            return StepStatus.Row;
                        case RunResult.Done:
                            _phase = Phase.Done;
                            return StepStatus.Done;
                        case RunResult.Pending:
                            _pending = true;
                            return StepStatus.Pending;
                        case RunResult.Stale:
                            _machine.ReleaseForReset();
                            if (_conn.Catalog is null || _conn.Catalog.Cookie != _conn.FileCookie)
                            {
                                _conn.InvalidateSchema();
                            }

                            _phase = Phase.Reprepare;
                            continue;
                        default:
                        {
                            StepStatus failed = Fail(Diagnostics.ErrorDiagnostics.Enrich(Wrap(_machine.Error!), _sql, _conn.Catalog));
                            if (_machine.BlockedByBusy && !_blocked)
                            {
                                _blocked = true;
                                _conn.BlockedStatements++;
                                
                            }
                            else if (_machine.BlockedAtRead && !_blockedRead && !_blocked)
                            {
                                _blockedRead = true;
                                _conn.BlockedReadStatements++;
                            }

                            return failed;
                        }
                    }
                }
            }
        }
    }

    private bool _blocked, _blockedRead;

    private void ReleaseBlocked(bool keepHold = false)
    {
        if (_blocked)
        {
            _blocked = false;
            _conn.BlockedStatements--;
            if (!keepHold)
            {
                _conn.ReleaseBlockedHold();
            }
        }

        if (_blockedRead)
        {
            _blockedRead = false;
            _conn.BlockedReadStatements--;
            if (!keepHold)
            {
                _conn.ReleaseBlockedHold();
            }
        }
    }

    private StepStatus Fail(OneLiteException e)
    {
        _error = e;
        _phase = Phase.Failed;
        return StepStatus.Error;
    }

    private static OneLiteException Wrap(Exception e)
    {
        e = Machine.Translate(e);
        return e as OneLiteException ?? new OneLiteException(ResultCode.Internal, "internal error: " + e.Message, e);
    }

    /// <summary>Feeds the result of <see cref="PendingIo"/> back. Legal only while <see cref="Advance"/> has returned <see cref="StepStatus.Pending"/>.</summary>
    /// <param name="completion">The result of the I/O.</param>
    public void Complete(in IoCompletion completion)
    {
        _conn.Pager.Complete(completion);
        _pending = false;
    }

    /// <summary>Drops the outstanding request without completing it, then resets the statement.</summary>
    public void Abandon()
    {
        if (_pending)
        {
            _conn.Pager.Abandon();
            _pending = false;
        }

        Reset();
    }

    /// <summary>
    /// The blocking driver: runs the statement to the next row.
    /// </summary>
    /// <returns>True if a row is ready, false when the statement is done.</returns>
    /// <exception cref="OneLiteException">The statement failed. The code, extended code and message are those of the equivalent C API call.</exception>
    public bool Step()
    {
        for (; ; )
        {
            switch (Advance())
            {
                case StepStatus.Row:
                    return true;
                case StepStatus.Done:
                    return false;
                case StepStatus.Pending:
                    _conn.Pager.ExecuteSync();
                    _pending = false;
                    break;
                default:
                    throw _error!;
            }
        }
    }

    /// <summary>
    /// The async driver: runs the statement to the next row, awaiting the VFS for every page that is not cached. When no I/O is needed (everything is
    /// in the page cache) it returns a completed task without creating an async state machine.
    /// </summary>
    /// <param name="ct">Observed at each outstanding I/O request that can be cancelled. Cancelling ends the statement the way <c>sqlite3_interrupt</c> does:
    /// the statement is reset (its read transaction ends) and <see cref="OperationCanceledException"/> is thrown.</param>
    /// <returns>True if a row is ready, false when the statement is done.</returns>
    /// <exception cref="OneLiteException">The statement failed; code, extended code and message are those of <see cref="Step"/>.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled while the statement waited for I/O.</exception>
    public ValueTask<bool> StepAsync(CancellationToken ct = default)
    {
        switch (Advance())
        {
            case StepStatus.Row:
                return new ValueTask<bool>(true);
            case StepStatus.Done:
                return new ValueTask<bool>(false);
            case StepStatus.Pending:
                return StepAsyncPending(ct);
            default:
                return ValueTask.FromException<bool>(_error!);
        }
    }

    private async ValueTask<bool> StepAsyncPending(CancellationToken ct)
    {
        for (; ; )
        {
            try
            {
                await _conn.Pager.ExecuteAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (_pending)
                {
                    _conn.Pager.Abandon();
                    _pending = false;
                }

                await ResetAsync().ConfigureAwait(false);

                // Cancelled while the read transaction was half begun (the lock taken, page 1 not read): no statement holds it yet, so end it here.
                if (_conn.ActiveReaders == 0)
                {
                    await _conn.WithAsyncAbort(_conn.AbortRead).ConfigureAwait(false);
                }

                throw;
            }

            _pending = false;
            switch (Advance())
            {
                case StepStatus.Row:
                    return true;
                case StepStatus.Done:
                    return false;
                case StepStatus.Pending:
                    break;
                default:
                    throw _error!;
            }
        }
    }

    /// <summary>Returns the statement to its initial state: closes its cursors and ends its read transaction. Bound parameters are kept.</summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pending)
        {
            _conn.Pager.Abandon();
            _pending = false;
        }

        ReleaseBlocked();
        if (_machine.Writing)
        {
            _machine.FinishWriteSync();
        }

        _machine.ReleaseForReset();
        _machine.Rewind();
        _error = null;
        _phase = Phase.Ready;
    }

    private void Recompile()
    {
        VmProgram p = _conn.CompileSql(_sql, out _, out _, _planMode);
        _prog = p;
        _machine = new Machine(_conn, p, _vars) { _owner = this };
    }

    internal void AbortForRollback(bool keepReads = false) => _machine.Abort(keepReads);

    /// <summary>
    /// Returns the statement to its initial state like <see cref="Reset"/>, awaiting the VFS to end the read transaction, so it works on a backend that has
    /// only asynchronous calls. Bound parameters are kept.
    /// </summary>
    /// <returns>A task that completes when the statement is reset.</returns>
    public async ValueTask ResetAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pending)
        {
            _conn.Pager.Abandon();
            _pending = false;
        }

        ReleaseBlocked();
        if (_machine.Writing)
        {
            await _machine.FinishWriteAsync().ConfigureAwait(false);
        }

        await _conn.WithAsyncAbort(ResetAfterWrite).ConfigureAwait(false);
    }

    private void ResetAfterWrite()
    {
        _machine.ReleaseForReset();
        _machine.Rewind();
        _error = null;
        _phase = Phase.Ready;
    }

    /// <summary>Finalizes the statement like <see cref="Dispose"/>, awaiting the VFS to end its read transaction.</summary>
    /// <returns>A task that completes when the statement is finalized.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_pending)
        {
            _conn.Pager.Abandon();
            _pending = false;
        }

        ReleaseBlocked();
        if (_machine.Writing)
        {
            await _machine.FinishWriteAsync().ConfigureAwait(false);
        }

        await _conn.WithAsyncAbort(_machine.Close).ConfigureAwait(false);
        _disposed = true;
        _conn.Forget(this);
    }

    /// <summary>Finalizes the statement.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ReleaseBlocked();
        if (_machine.Writing)
        {
            // finalizing a write statement that was not stepped to its end (rows still to read) commits what it did, as Reset does
            _machine.FinishWriteSync();
        }

        _machine.Close();
        _disposed = true;
        _conn.Forget(this);
    }

    // ------------------------------------------------------------------ columns

    private ref Mem Col(int i)
    {
        if ((uint)i >= (uint)_prog.ResultCount)
        {
            throw new ArgumentOutOfRangeException(nameof(i), "column index out of range");
        }

        return ref _machine.R[_prog.ResultBase + i];
    }

    /// <summary>The storage class of column <paramref name="index"/> of the current row.</summary>
    /// <param name="index">0-based column index.</param>
    /// <returns>The type.</returns>
    public ColumnType ColumnType(int index) => Col(index).Kind switch
    {
        StorageClass.Integral => RecursiveAI.OneLite.ColumnType.Integral,
        StorageClass.Real => RecursiveAI.OneLite.ColumnType.Real,
        StorageClass.Text => RecursiveAI.OneLite.ColumnType.Text,
        StorageClass.Blob => RecursiveAI.OneLite.ColumnType.Blob,
        _ => RecursiveAI.OneLite.ColumnType.Null,
    };

    /// <summary>True if column <paramref name="index"/> is NULL.</summary>
    /// <param name="index">0-based column index.</param>
    /// <returns>True for NULL.</returns>
    public bool IsNull(int index) => Col(index).Kind == StorageClass.Null;

    /// <summary>The column as a 64-bit integer, converted as sqlite3_column_int64 does (reals truncate, text uses its numeric prefix, NULL is 0).</summary>
    /// <param name="index">0-based column index.</param>
    /// <returns>The value.</returns>
    public long GetInt64(int index)
    {
        ref Mem m = ref Col(index);
        return m.Kind == StorageClass.Integral ? m.IntValue : Values.ToInt(m);
    }

    /// <summary>The column as a double, converted as sqlite3_column_double does.</summary>
    /// <param name="index">0-based column index.</param>
    /// <returns>The value.</returns>
    public double GetDouble(int index)
    {
        ref Mem m = ref Col(index);
        return m.Kind == StorageClass.Real ? m.RealValue : Values.ToReal(m);
    }

    /// <summary>The column as UTF-8 text, without copying when it is text already (numbers are rendered the way SQLite renders them). Empty for NULL.</summary>
    /// <param name="index">0-based column index.</param>
    /// <returns>The bytes, valid until the next step.</returns>
    public ReadOnlySpan<byte> GetTextUtf8(int index)
    {
        ref Mem m = ref Col(index);
        if (m.Kind is StorageClass.Text or StorageClass.Blob)
        {
            return m.Bytes;
        }

        if (m.Kind == StorageClass.Null)
        {
            return default;
        }

        ref byte[]? buf = ref _convert[index];
        buf ??= new byte[Values.MaxRealText];
        int n = m.Kind == StorageClass.Integral ? Values.FormatInt(m.IntValue, buf) : Values.FormatReal(m.RealValue, buf);
        return new ReadOnlySpan<byte>(buf, 0, n);
    }

    /// <summary>The column as a blob (the bytes of text, or of the rendered number), without copying. Empty for NULL.</summary>
    /// <param name="index">0-based column index.</param>
    /// <returns>The bytes, valid until the next step.</returns>
    public ReadOnlySpan<byte> GetBlob(int index) => GetTextUtf8(index);

    /// <summary>The column as a string (invalid UTF-8 becomes U+FFFD); null for NULL.</summary>
    /// <param name="index">0-based column index.</param>
    /// <returns>The text.</returns>
    public string? GetString(int index) => IsNull(index) ? null : Encoding.UTF8.GetString(GetTextUtf8(index));

    // ------------------------------------------------------------------ parameters

    /// <summary>Number of parameters (the largest index in the statement).</summary>
    public int ParameterCount => _vars.Length;

    /// <summary>The name of parameter <paramref name="index"/> including its prefix (":name"), or null for "?".</summary>
    /// <param name="index">1-based parameter index.</param>
    /// <returns>The name or null.</returns>
    public string? ParameterName(int index) => _prog.ParameterNames[index - 1];

    /// <summary>The index of the named parameter (with its prefix), or 0 if there is none.</summary>
    /// <param name="name">For example ":id".</param>
    /// <returns>The 1-based index or 0.</returns>
    public int ParameterIndex(string name)
    {
        for (int i = 0; i < _prog.ParameterNames.Length; i++)
        {
            if (_prog.ParameterNames[i] == name)
            {
                return i + 1;
            }
        }

        return 0;
    }

    private void CheckBind(int index)
    {
        if (index < 1 || index > _vars.Length)
        {
            throw new OneLiteException(25, "column index out of range");
        }

        if (_phase is Phase.Running or Phase.Reprepare)
        {
            throw new OneLiteException(ResultCode.Misuse, "bad parameter or other API misuse", "reset the statement before binding");
        }
    }

    /// <summary>Binds NULL.</summary>
    /// <param name="index">1-based parameter index.</param>
    /// <exception cref="OneLiteException">SQLITE_RANGE for a bad index; SQLITE_MISUSE while the statement is running.</exception>
    public void BindNull(int index)
    {
        CheckBind(index);
        _vars[index - 1] = default;
    }

    /// <summary>Binds an integer.</summary>
    /// <param name="index">1-based parameter index.</param>
    /// <param name="value">The value.</param>
    public void Bind(int index, long value)
    {
        CheckBind(index);
        _vars[index - 1] = Mem.FromInt(value);
    }

    /// <summary>Binds a real.</summary>
    /// <param name="index">1-based parameter index.</param>
    /// <param name="value">The value.</param>
    public void Bind(int index, double value)
    {
        CheckBind(index);
        _vars[index - 1] = Mem.FromReal(value);
    }

    /// <summary>Binds text (copied as UTF-8).</summary>
    /// <param name="index">1-based parameter index.</param>
    /// <param name="value">The text.</param>
    public void Bind(int index, string value)
    {
        CheckBind(index);
        byte[] b = Encoding.UTF8.GetBytes(value);
        _varBytes[index - 1] = b;
        _vars[index - 1] = Mem.FromTextBytes(b);
    }

    /// <summary>Binds a blob (copied).</summary>
    /// <param name="index">1-based parameter index.</param>
    /// <param name="value">The bytes.</param>
    public void Bind(int index, ReadOnlySpan<byte> value)
    {
        CheckBind(index);
        byte[] b = value.ToArray();
        _varBytes[index - 1] = b;
        _vars[index - 1] = Mem.FromBlob(b);
    }

    /// <summary>Sets every parameter to NULL.</summary>
    public void ClearBindings()
    {
        CheckBind(1 > _vars.Length ? 0 : 1);
        Array.Clear(_vars);
        Array.Clear(_varBytes);
    }
}
