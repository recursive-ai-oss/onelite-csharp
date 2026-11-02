using System.Text;
using RecursiveAI.OneLite.Planning;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Storage.Wal;

namespace RecursiveAI.OneLite.Vm;

// The write opcodes. Each one follows the step contract: a B-tree call that answers Pending is repeated with the same arguments after the I/O
// completes, and an opcode that needs several calls keeps its progress in _wstep (cleared when the opcode ends). Nothing observable (counters,
// last_insert_rowid) changes before the opcode's last possible suspension.
internal sealed partial class Machine
{
    // changes(), total_changes() and last_insert_rowid() read the connection of the statement that is running (c3ref changes).
    private bool _writing;
    private bool _dml;
    private bool _ddl;
    private int _stmtLevel;
    private int _stmtTempLevel;
    private int[]? _stmtAttLevels;
    private int _wstep;
    private int _fw;
    private bool _fwError;
    private bool _fwWhole;
    private bool _fwStmt;
    private bool _aborted;
    private long _changes;
    private ErrorAction _errAction;
    private byte[] _recBuf = new byte[256];
    private long _tmpRowid;
    private int _attempt;

    private List<long>? _rowSet;
    private List<byte[]>? _rowKeys;
    private int _rowSetPos;
    private List<byte[]>? _retRows;
    private int _retPos;
    private Machine?[] _subs = [];
    private List<byte[]>?[] _subRows = [];
    private int[] _subPos = [];
    private TreeEraser? _eraser;
    private readonly RecordView _wview = new();
    private byte[]? _tmpOwn;

    /// <summary>True while the statement has changes the connection must still commit, roll back or release.</summary>
    internal bool Writing => _writing;

    /// <summary>Ends a write statement that is being reset before it finished (RETURNING rows not all read): its changes stand, as in autocommit mode they commit.</summary>
    internal void FinishWriteSync()
    {
        while (_writing && FinishCleanup(false) == RunResult.Pending)
        {
            _conn.Pager.ExecuteSync();
        }
    }

    /// <summary>Asynchronous twin of <see cref="FinishWriteSync"/>.</summary>
    internal async ValueTask FinishWriteAsync()
    {
        while (_writing && FinishCleanup(false) == RunResult.Pending)
        {
            await _conn.Pager.ExecuteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>COMMIT, or the RELEASE that commits, fails while a deferred foreign key is violated; the transaction stays open.</summary>
    private void CheckDeferredForeignKeys()
    {
        if (_conn.FkDeferred != 0)
        {
            throw ConstraintError(ConstraintCodes.ForeignKey, "FOREIGN KEY constraint failed", ErrorAction.Abort);
        }
    }

    private OneLiteException ConstraintError(int ext, string message, ErrorAction action)
    {
        _errAction = action;
        return new OneLiteException(ext, message);
    }

    /// <summary>Called when a ROLLBACK elsewhere on the connection rewrites pages this statement's cursors point into.</summary>
    internal void Abort()
    {
        foreach (VmCursor c in _cur)
        {
            c.Bt?.Reset();
            c.RecordReady = false;
        }

        if (_pc > 0 && !_finished)
        {
            _aborted = true;
        }
    }

    // ------------------------------------------------------------------ the dispatcher

    private RunResult DoWrite(ref Instr ins)
    {
        Mem[] r = R;
        switch (ins.Op)
        {
            case Op.VtFilter:
            case Op.VtUpdate:
            case Op.VtCreate:
            case Op.VtDestroy:
                return DoVirtual(ref ins);
            case Op.Gosub:
                Values.SetInt(ref r[ins.P1], _pc + 1);
                _pc = ins.P2;
                return RunResult.Done;
            case Op.Return:
                _pc = (int)r[ins.P1].IntValue;
                return RunResult.Done;
            case Op.Raise:
            {
                OneLiteException raised = ConstraintError(ins.P1, (string)ins.P4!, (ErrorAction)ins.P5);
                if (ins.P3 > 0)
                {
                    // The key that clashed, for the added constraint details; read only now that the statement is failing.
                    var values = new string[ins.P3];
                    for (int k = 0; k < values.Length; k++)
                    {
                        values[k] = Diagnostics.ErrorDiagnostics.Literal(R[ins.P2 + k]);
                    }

                    raised.Constraint = new ConstraintInfo(ConstraintKind.Other, null, [], null, values);
                }

                throw raised;
            }
            case Op.OpenWrite:
                OpenCursor(_cur[ins.P1], ins.P2 > 0 ? (uint)R[ins.P2].IntValue : 0);
                _cur[ins.P1].Empty = false;
                _pc++;
                return RunResult.Done;
            case Op.NewRowid:
                return DoNewRowid(ref ins);
            case Op.MaxReg:
                if (r[ins.P2].IntValue > r[ins.P1].IntValue)
                {
                    Values.SetInt(ref r[ins.P1], r[ins.P2].IntValue);
                }

                _pc++;
                return RunResult.Done;
            case Op.RowidFix:
            {
                ref Mem m = ref r[ins.P1];
                Values.ApplyStored(ref m, ref _own[ins.P1], Affinity.Integer);
                if (m.Kind != StorageClass.Integral)
                {
                    throw ConstraintError(ConstraintCodes.Mismatch, "datatype mismatch", ErrorAction.Abort);
                }

                _pc++;
                return RunResult.Done;
            }

            case Op.Insert:
                return DoInsert(ref ins);
            case Op.IdxInsert:
                return DoIdxInsert(ref ins);
            case Op.IdxDelete:
                return DoIdxDelete(ref ins);
            case Op.Delete:
                return DoDelete(ref ins);
            case Op.IdxProbe:
                return DoIdxProbe(ref ins);
            case Op.RowSetAdd:
            {
                if (ins.P2 > 0)
                {
                    (_rowKeys ??= []).Add(RecordWriter.ToArray(new ReadOnlySpan<Mem>(r, ins.P1, ins.P2)));
                }
                else
                {
                    (_rowSet ??= []).Add(r[ins.P1].IntValue);
                }

                _pc++;
                return RunResult.Done;
            }

            case Op.RowSetRead:
            {
                if (ins.P3 > 0)
                {
                    if (_rowKeys is null || _rowSetPos >= _rowKeys.Count)
                    {
                        _pc = ins.P2;
                        return RunResult.Done;
                    }

                    _wview.Parse(_rowKeys[_rowSetPos], 0, _rowKeys[_rowSetPos].Length);
                    for (int i = 0; i < ins.P3; i++)
                    {
                        _wview.Load(i, ref r[ins.P1 + i]);
                    }
                }
                else
                {
                    if (_rowSet is null || _rowSetPos >= _rowSet.Count)
                    {
                        _pc = ins.P2;
                        return RunResult.Done;
                    }

                    Values.SetInt(ref r[ins.P1], _rowSet[_rowSetPos]);
                }

                _rowSetPos++;
                _pc++;
                return RunResult.Done;
            }

            case Op.SeqLoad:
                return DoSeqLoad(ref ins);
            case Op.SeqStore:
                return DoSeqStore(ref ins);
            case Op.StrictCheck:
                StrictCheck(ref ins);
                _pc++;
                return RunResult.Done;
            case Op.CreateBtree:
                return DoCreateBtree(ref ins);
            case Op.Destroy:
            case Op.ClearTree:
                return DoDestroy(ref ins);
            case Op.BumpCookie:
                return DoBumpCookie(ref ins);
            case Op.RetAdd:
                (_retRows ??= []).Add(RecordWriter.ToArray(new ReadOnlySpan<Mem>(r, ins.P1, ins.P2)));
                _pc++;
                return RunResult.Done;
            case Op.RetRow:
            {
                if (_retRows is null || _retPos >= _retRows.Count)
                {
                    _pc = ins.P2;
                    return RunResult.Done;
                }

                byte[] rec = _retRows[_retPos++];
                _wview.Parse(rec, 0, rec.Length);
                for (int i = 0; i < ins.P3; i++)
                {
                    _wview.Load(i, ref r[ins.P1 + i]);
                }

                _pc++;
                return RunResult.Done;
            }

            case Op.PragmaOp:
                return DoPragma(ref ins);
            case Op.PragmaVar:
                return DoPragmaVar(ref ins);
            case Op.PragmaIntegrity:
                return DoPragmaIntegrity(ref ins);
            case Op.Vacuum:
                return ins.P2 != 0 ? DoVacuumInto(ins.P1, ins.P2) : DoVacuum(ins.P1);
            case Op.AttachDb:
            case Op.DetachDb:
                return DoAttach(ref ins);
            case Op.RecTrig:
                // P2: where to go when recursive_triggers is off
                _pc = _conn.RecursiveTriggers ? _pc + 1 : ins.P2;
                return RunResult.Done;
            case Op.SchemaDelete:
                return DoSchemaDelete(ref ins);
            case Op.FkCheck:
                // foreignkeys: an immediate constraint is checked when the statement ends (a deferred one, too, outside a transaction).
                if (!Nested && (_conn.FkImm != 0 || (_conn.AutoCommit && _conn.FkDeferred != 0)))
                {
                    throw ConstraintError(ConstraintCodes.ForeignKey, "FOREIGN KEY constraint failed", ErrorAction.Abort);
                }

                _pc++;
                return RunResult.Done;
            case Op.FkAdd:
            {
                // P1 deferred?, P2 delta; P5 0: add P2 when register P3 is zero (a missing parent); 1: add P2 times register P3.
                long n = ins.P5 == 0 ? (R[ins.P3].IntValue == 0 ? ins.P2 : 0) : ins.P2 * R[ins.P3].IntValue;
                if (ins.P1 != 0)
                {
                    _conn.FkDeferred += n;
                }
                else
                {
                    _conn.FkImm += n;
                }

                _pc++;
                return RunResult.Done;
            }

            case Op.FkRun:
                return DoFkRun(ref ins);
            case Op.TrigRun:
                return DoTrigRun(ref ins);
            case Op.SubSelect:
                return DoSubSelect(ref ins);
            case Op.SubNext:
                return DoSubNext(ref ins);
            case Op.TxControl:
                return DoTxControl(ref ins);
            default:
                throw new InvalidOperationException("unknown opcode " + ins.Op);
        }
    }

    private void OpenCursor(VmCursor c, uint rootOverride = 0)
    {
        if (c.Bt != null)
        {
            c.Bt.Reset();
            c.RecordReady = false;
            return;
        }

        CursorSpec spec = c.Spec;
        BtContext ctx = _conn.ContextOf(spec.Table.Db);
        uint root = rootOverride != 0 ? rootOverride : spec.Root;
        if (spec.IsIndexTree)
        {
            c.KeyInfo = _conn.MakeKeyInfo(spec);
            c.Tree = BTree.Index(ctx, root, c.KeyInfo);
        }
        else
        {
            c.Tree = BTree.Table(ctx, root);
        }

        c.Bt = c.Tree.OpenCursor();
        c.RecordReady = false;
    }

    // ------------------------------------------------------------------ transactions

    private bool _ownKnown;
    private bool _ownRead;
    private int _busyTry;
    private int _busySleepMs;

    private RunResult DoTransactionWrite(bool exclusive)
    {
        if (_txStep == 2 && _conn.PragmaSettings.QueryOnly)
        {
            // pragma query_only: the connection refuses every statement that would write.
            throw new OneLiteException(ResultCode.ReadOnly, "attempt to write a readonly database");
        }

        if (_txStep == 2)
        {
            // c3ref busy_handler: a writer that cannot get the RESERVED lock must not wait while it holds a SHARED lock it took only for this
            // statement, because the process that holds RESERVED may be waiting for exactly that lock to go (both would wait out the whole timeout).
            // It lets go of the read transaction, waits, and starts over. A connection that was already reading gets BUSY at once.
            PagerStatus s = _conn.BeginWriteStep(exclusive, noWait: !_conn.IsWal);
            if (s == PagerStatus.Pending)
            {
                return RunResult.Pending;
            }

            if (s == PagerStatus.Error)
            {
                if (_ownRead && !_conn.IsWal && _conn.PagerError is OneLiteException { Code: ResultCode.Busy })
                {
                    int ms = _conn.Busy?.Next(_busyTry++) ?? -1;
                    if (ms >= 0)
                    {
                        _busySleepMs = ms;
                        _txStep = 20;
                        return RunResult.Done;
                    }
                }

                Error = _conn.PagerError;
                return RunResult.Error;
            }

            _busyTry = 0;
            _txStep = 3;
            if (_conn.Attached.Count > 0 && _prog.AttachedWriteMask != 0)
            {
                try
                {
                    _conn.BeginAttached(0, _prog.AttachedWriteMask);
                }
                catch (OneLiteException e)
                {
                    Error = e;
                    return RunResult.Error;
                }
            }
        }

        if (!_writing)
        {
            _writing = true;
            // A statement inside a transaction is a savepoint of its own, so that a constraint failure undoes just this statement (lang_conflict ABORT).
            _stmtLevel = _conn.AutoCommit ? 0 : _conn.Pager.OpenSavepoint();
            if (_prog.UsesTemp)
            {
                _conn.GetTemp();
            }

            _stmtTempLevel = !_conn.AutoCommit && _conn.Temp is { } tmp ? tmp.Pager.OpenSavepoint() : 0;
            _stmtAttLevels = _conn.AutoCommit ? null : _conn.OpenAttachedSavepoints();
            _fkStartDef = _conn.FkDeferred;
            _conn.FkImm = 0;
        }

        _txStep = 4;
        return RunResult.Done;
    }

    /// <summary>Lets go of the read transaction this statement started, then sleeps; the caller re-runs the Transaction instruction afterwards.</summary>
    private RunResult BusyBackoff()
    {
        if (_txStep == 20)
        {
            PagerStatus s = _conn.EndTxHoldStep();
            if (s == PagerStatus.Pending)
            {
                return RunResult.Pending;
            }

            _txStep = 21;
        }

        if (_txStep == 21)
        {
            PagerStatus s = _conn.EndReadStep();
            if (s == PagerStatus.Pending)
            {
                return RunResult.Pending;
            }

            _conn.ReaderDone();
            _holdsRead = false;
            _txStep = 22;
        }

        PagerStatus w = _conn.Pager.SleepStep(_busySleepMs);
        if (w == PagerStatus.Pending)
        {
            return RunResult.Pending;
        }

        _txStep = 0;
        return RunResult.Done;
    }

    /// <summary>Ends the write side of the statement: commit in autocommit mode, statement rollback or transaction rollback after an error.</summary>
    private RunResult FinishWrite(bool error)
    {
        Pager pg = _conn.Pager;
        for (; ; )
        {
            switch (_fw)
            {
                case 0:
                    _fwError = error;
                    if (error)
                    {
                        // The statement stopped in the middle of a pager call (a throw while an operation was suspended). Nothing will
                        // re-enter that call, so a completion still waiting for it must not be mistaken for the rollback's own.
                        pg.Abandon();
                        if (_errAction == ErrorAction.Fail)
                        {
                            // FAIL keeps what the statement already did; in autocommit mode that is committed (lang_conflict).
                        }
                        else if (_errAction == ErrorAction.Rollback || _conn.AutoCommit)
                        {
                            _fwWhole = true;
                        }
                        else
                        {
                            _fwStmt = true;
                        }
                    }

                    _fw = 1;
                    break;
                case 1:
                    if (_fwWhole)
                    {
                        _conn.ReleaseCursorsOf(_owner);
                        PagerStatus s = pg.Rollback();
                        if (s == PagerStatus.Pending)
                        {
                            return RunResult.Pending;
                        }

                        _conn.Temp?.Rollback();
                        _conn.RollbackAttached();
                        _conn.VtabRollback();
                        _fw = 5;
                    }
                    else if (_fwStmt)
                    {
                        PagerStatus s = pg.RollbackSavepoint(_stmtLevel);
                        if (s == PagerStatus.Pending)
                        {
                            return RunResult.Pending;
                        }

                        pg.ReleaseSavepoint(_stmtLevel);
                        if (_stmtTempLevel > 0 && _conn.Temp is { } ts)
                        {
                            ts.Pager.RollbackSavepointSync(_stmtTempLevel);
                            ts.Pager.ReleaseSavepoint(_stmtTempLevel);
                        }

                        _conn.RollbackAttachedSavepoints(_stmtAttLevels);
                        _conn.ReleaseAttachedSavepoints(_stmtAttLevels);

                        if (_conn.SchemaDirty || _ddl)
                        {
                            _conn.InvalidateSchema();
                        }

                        _fw = 9;
                    }
                    else
                    {
                        if (_stmtLevel > 0)
                        {
                            pg.ReleaseSavepoint(_stmtLevel);
                        }

                        if (_stmtTempLevel > 0 && _conn.Temp is { } tr)
                        {
                            tr.Pager.ReleaseSavepoint(_stmtTempLevel);
                        }

                        _conn.ReleaseAttachedSavepoints(_stmtAttLevels);

                        _fw = _conn.AutoCommit ? 3 : 8;
                    }

                    break;
                case 3:
                {
                    Exception? multiError = null;
                    try
                    {
                        _conn.VtabSync();
                    }
                    catch (Exception ve)
                    {
                        Error = ve;
                        _fwError = true;
                        _fwWhole = true;
                        _fw = 4;
                        break;
                    }

                    PagerStatus s = _conn.AttachedWriting ? CommitAllInline(out multiError) : pg.Commit();
                    if (s == PagerStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    if (s == PagerStatus.Error)
                    {
                        Error = multiError ?? pg.Error;
                        _fwError = true;
                        _fwWhole = true;
                        _fw = 4;
                        break;
                    }

                    _conn.Temp?.Commit();
                    _conn.VtabCommit();
                    _conn.AfterCommit();
                    _fw = 8;
                    break;
                }

                case 4:
                {
                    PagerStatus s = pg.Rollback();
                    if (s == PagerStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    _conn.Temp?.Rollback();
                    _conn.RollbackAttached();
                    _fw = 5;
                    break;
                }

                case 5:
                    _conn.InvalidateSchema();
                    _conn.EndTransactionState();
                    _fw = 6;
                    break;
                case 6:
                {
                    PagerStatus s = _conn.EndTxHoldStep();
                    if (s == PagerStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    _fw = 9;
                    break;
                }

                case 8:
                    if (_ddl)
                    {
                        _conn.InvalidateSchema();
                        _conn.SchemaDirty = !_conn.AutoCommit;
                    }

                    _fw = 9;
                    break;
                default:
                    if (_dml && !_fwWhole && !_fwStmt)
                    {
                        _conn.Changes = _changes;
                        _conn.TotalChanges += _changes;
                    }
                    else if (_dml && !_fwStmt)
                    {
                        _conn.Changes = 0;
                    }

                    _conn.FkImm = 0;
                    if (_fwStmt)
                    {
                        _conn.FkDeferred = _fkStartDef;
                    }

                    _writing = false;
                    _fw = 0;
                    return _fwError ? RunResult.Error : RunResult.Done;
            }
        }
    }

    // Commits main and the attached databases together. The databases' I/O runs inline, so this never suspends (docs/attach.md).
    private PagerStatus CommitAllInline(out Exception? error)
    {
        try
        {
            _conn.CommitAttachedSync();
            error = null;
            return PagerStatus.Ok;
        }
        catch (OneLiteException e)
        {
            error = e;
            return PagerStatus.Error;
        }
    }

    private RunResult DoTxControl(ref Instr ins)
    {
        Pager pg = _conn.Pager;
        string? name = (string?)ins.P4;
        for (; ; )
        {
            switch (_wstep)
            {
                case 0:
                    switch (ins.P1)
                    {
                        case 0:
                            if (!_conn.AutoCommit)
                            {
                                throw new OneLiteException(ResultCode.Error, "cannot start a transaction within a transaction");
                            }

                            _conn.ExplicitTx = true;
                            _wstep = 99;
                            break;
                        case 1:
                            if (_conn.AutoCommit)
                            {
                                throw new OneLiteException(ResultCode.Error, "cannot commit - no transaction is active");
                            }

                            CheckDeferredForeignKeys();
                            _wstep = 10;
                            break;
                        case 2:
                            if (_conn.AutoCommit)
                            {
                                throw new OneLiteException(ResultCode.Error, "cannot rollback - no transaction is active");
                            }

                            _conn.ReleaseCursorsOf(_owner);
                            _wstep = 20;
                            break;
                        case 3:
                        {
                            bool first = _conn.AutoCommit;
                            var e = new SavepointEntry { Name = name!, FkDeferred = _conn.FkDeferred };
                            _conn.Savepoints.Add(e);
                            if (pg.InWriteTransaction)
                            {
                                e.Level = pg.OpenSavepoint();
                            }

                            if (_conn.Temp is { } tsp)
                            {
                                e.TempLevel = tsp.Pager.OpenSavepoint();
                            }

                            e.AttachedLevels = _conn.OpenAttachedSavepoints();
                            _conn.VtabSavepoint(_conn.Savepoints.Count);

                            _wstep = 99;
                            _ = first;
                            break;
                        }

                        case 4:
                        {
                            int i = _conn.FindSavepoint(name!);
                            if (i < 0)
                            {
                                throw new OneLiteException(ResultCode.Error, "no such savepoint: " + name);
                            }

                            _tmpRowid = i;
                            if (i == 0 && !_conn.ExplicitTx)
                            {
                                CheckDeferredForeignKeys();
                            }

                            _conn.VtabRelease(i + 1);
                            int level = _conn.Savepoints[i].Level;
                            int tlevel = _conn.Savepoints[i].TempLevel;
                            int[]? alevels = _conn.Savepoints[i].AttachedLevels;
                            _conn.Savepoints.RemoveRange(i, _conn.Savepoints.Count - i);
                            if (level > 0)
                            {
                                pg.ReleaseSavepoint(level);
                            }

                            if (tlevel > 0 && _conn.Temp is { } trl)
                            {
                                trl.Pager.ReleaseSavepoint(tlevel);
                            }

                            _conn.ReleaseAttachedSavepoints(alevels);

                            // Releasing the outermost savepoint of a transaction that no BEGIN opened commits it (lang_savepoint).
                            _wstep = _conn.AutoCommit ? 10 : 99;
                            break;
                        }

                        default:
                        {
                            int i = _conn.FindSavepoint(name!);
                            if (i < 0)
                            {
                                throw new OneLiteException(ResultCode.Error, "no such savepoint: " + name);
                            }

                            _tmpRowid = i;
                            _conn.ReleaseCursorsOf(_owner);
                            _wstep = 30;
                            break;
                        }
                    }

                    break;
                case 10:
                {
                    // COMMIT
                    if (_conn.HasWritableBlob)
                    {
                        throw new OneLiteException(ResultCode.Busy, "cannot commit transaction - SQL statements in progress");
                    }

                    Exception? multiError = null;
                    _conn.VtabSync();
                    PagerStatus s = _conn.AttachedWriting ? CommitAllInline(out multiError) : pg.InWriteTransaction ? pg.Commit() : PagerStatus.Ok;
                    if (s == PagerStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    if (s == PagerStatus.Error)
                    {
                        // The commit failed; nothing was committed. The transaction stays open so the caller can roll back or retry (SQLITE_BUSY).
                        Error = multiError ?? pg.Error;
                        _wstep = 0;
                        return RunResult.Error;
                    }

                    _conn.Temp?.Commit();
                    _conn.VtabCommit();
                    _conn.AfterCommit();
                    _wstep = 11;
                    break;
                }

                case 11:
                    if (_conn.SchemaDirty)
                    {
                        _conn.InvalidateSchema();
                    }

                    _conn.EndTransactionState();
                    _wstep = 12;
                    break;
                case 12:
                {
                    PagerStatus s = _conn.EndTxHoldStep();
                    if (s == PagerStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    _wstep = 99;
                    break;
                }

                case 20:
                {
                    // ROLLBACK
                    PagerStatus s = pg.InWriteTransaction ? pg.Rollback() : PagerStatus.Ok;
                    if (s == PagerStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    _conn.Temp?.Rollback();
                    _conn.RollbackAttached();
                    _conn.VtabRollback();
                    _conn.AfterRollback();
                    _conn.InvalidateSchema();
                    _conn.EndTransactionState();
                    _wstep = 12;
                    break;
                }

                case 30:
                {
                    // ROLLBACK TO: undo to the savepoint, which stays open
                    int i = (int)_tmpRowid;
                    SavepointEntry e = _conn.Savepoints[i];
                    _conn.FkDeferred = e.FkDeferred;
                    if (e.Level > 0)
                    {
                        PagerStatus s = pg.RollbackSavepoint(e.Level);
                        if (s == PagerStatus.Pending)
                        {
                            return RunResult.Pending;
                        }
                    }

                    if (e.TempLevel > 0 && _conn.Temp is { } trt)
                    {
                        trt.Pager.RollbackSavepointSync(e.TempLevel);
                    }

                    _conn.RollbackAttachedSavepoints(e.AttachedLevels);
                    _conn.VtabRollbackTo(i + 1);

                    if (_conn.SchemaDirty || _conn.Temp != null)
                    {
                        _conn.InvalidateSchema();
                    }

                    _conn.Savepoints.RemoveRange(i + 1, _conn.Savepoints.Count - i - 1);
                    _wstep = 99;
                    break;
                }

                default:
                    _wstep = 0;
                    _pc++;
                    return RunResult.Done;
            }
        }
    }

    // ------------------------------------------------------------------ rowids

    private RunResult DoNewRowid(ref Instr ins)
    {
        VmCursor c = _cur[ins.P1];
        if (_wstep == 0)
        {
            BtStatus s = c.Bt!.Last();
            if (s == BtStatus.Pending)
            {
                return RunResult.Pending;
            }

            if (s == BtStatus.Error)
            {
                return BtFail(c.Bt);
            }

            long max = c.Bt.State == CursorState.Valid ? c.Bt.Rowid : 0;
            if (ins.P3 > 0 && R[ins.P3].IntValue > max)
            {
                max = R[ins.P3].IntValue;
            }

            if (max < long.MaxValue)
            {
                Values.SetInt(ref R[ins.P2], max + 1);
                if (ins.P3 > 0)
                {
                    Values.SetInt(ref R[ins.P3], max + 1);
                }

                _pc++;
                return RunResult.Done;
            }

            if (ins.P3 > 0)
            {
                // autoinc: the largest rowid was used up (autoinc)
                throw new OneLiteException(ResultCode.Full, "database or disk is full");
            }

            _attempt = 0;
            _wstep = 1;
        }

        // The table holds the largest possible rowid: probe random values for a free one (lang_createtable "ROWID", "If the largest ROWID ... is used").
        for (; ; )
        {
            if (_wstep == 1)
            {
                if (++_attempt > 100)
                {
                    throw new OneLiteException(ResultCode.Full, "database or disk is full");
                }

                _tmpRowid = Random.Shared.NextInt64(1, long.MaxValue);
                _wstep = 2;
            }

            BtStatus s = c.Bt!.MoveToRowid(_tmpRowid, out int cmp);
            if (s == BtStatus.Pending)
            {
                return RunResult.Pending;
            }

            if (s == BtStatus.Error)
            {
                return BtFail(c.Bt);
            }

            if (c.Bt.State != CursorState.Valid || cmp != 0)
            {
                Values.SetInt(ref R[ins.P2], _tmpRowid);
                _wstep = 0;
                _pc++;
                return RunResult.Done;
            }

            _wstep = 1;
        }
    }

    // ------------------------------------------------------------------ rows and index entries

    // An insert that suspends is re-entered, and the registers it was built from can point into the page it is changing (a row rewritten
    // from its own columns), so the record is built once and kept until the insert completes.
    private int _pendingRecLen = -1;

    private ReadOnlySpan<byte> BuildRecordOnce(int first, int n)
    {
        if (_pendingRecLen >= 0)
        {
            return new ReadOnlySpan<byte>(_recBuf, 0, _pendingRecLen);
        }

        ReadOnlySpan<byte> rec = BuildRecord(first, n);
        _pendingRecLen = rec.Length;
        return rec;
    }

    private ReadOnlySpan<byte> BuildRecord(int first, int n)
    {
        ReadOnlySpan<Mem> vals = PrepareKey(first, n);
        int size = RecordWriter.Size(vals);
        if (_recBuf.Length < size)
        {
            _recBuf = new byte[Math.Max(size, _recBuf.Length * 2)];
        }

        RecordWriter.Write(_recBuf, vals);
        return new ReadOnlySpan<byte>(_recBuf, 0, size);
    }

    private RunResult DoInsert(ref Instr ins)
    {
        VmCursor c = _cur[ins.P1];
        long rowid = R[ins.P3].IntValue;
        BtStatus s = c.Bt!.Insert(rowid, BuildRecordOnce(ins.P2, ((TableInfo)ins.P4!).RecordWidth));
        if (s == BtStatus.Pending)
        {
            return RunResult.Pending;
        }

        _pendingRecLen = -1;

        if (s == BtStatus.Error)
        {
            return BtFail(c.Bt);
        }

        c.RecordReady = false;
        if ((ins.P5 & 1) != 0)
        {
            _changes++;
        }

        if ((ins.P5 & 2) != 0)
        {
            _conn.LastInsertRowid = rowid;
        }

        _pc++;
        return RunResult.Done;
    }

    private RunResult DoIdxInsert(ref Instr ins)
    {
        VmCursor c = _cur[ins.P1];
        BtStatus s = c.Bt!.Insert(BuildRecordOnce(ins.P2, ins.P3), PrepareKey(ins.P2, ins.P3));
        if (s == BtStatus.Pending)
        {
            return RunResult.Pending;
        }

        _pendingRecLen = -1;

        if (s == BtStatus.Error)
        {
            return BtFail(c.Bt);
        }

        c.RecordReady = false;
        if ((ins.P5 & 1) != 0)
        {
            _changes++;
        }

        _pc++;
        return RunResult.Done;
    }

    private RunResult DoIdxDelete(ref Instr ins)
    {
        VmCursor c = _cur[ins.P1];
        if (_wstep == 0)
        {
            BtStatus s = c.Bt!.MoveToKey(PrepareKey(ins.P2, ins.P3), 0, out int cmp);
            if (s == BtStatus.Pending)
            {
                return RunResult.Pending;
            }

            if (s == BtStatus.Error)
            {
                return BtFail(c.Bt);
            }

            if (c.Bt.State != CursorState.Valid || cmp != 0)
            {
                throw new OneLiteException(ResultCode.Corrupt, "database disk image is malformed", "an index is missing the entry for a row");
            }

            _wstep = 1;
        }

        BtStatus d = c.Bt!.Delete();
        if (d == BtStatus.Pending)
        {
            return RunResult.Pending;
        }

        if (d == BtStatus.Error)
        {
            return BtFail(c.Bt);
        }

        c.RecordReady = false;
        _wstep = 0;
        _pc++;
        return RunResult.Done;
    }

    private RunResult DoDelete(ref Instr ins)
    {
        VmCursor c = _cur[ins.P1];
        BtStatus d = c.Bt!.Delete();
        if (d == BtStatus.Pending)
        {
            return RunResult.Pending;
        }

        if (d == BtStatus.Error)
        {
            return BtFail(c.Bt);
        }

        c.RecordReady = false;
        if ((ins.P5 & 1) != 0)
        {
            _changes++;
        }

        _pc++;
        return RunResult.Done;
    }

    /// <summary>
    /// Looks for an index entry equal to the key columns. Jumps to P2 when there is none (or the key holds a NULL, which a UNIQUE index never
    /// treats as equal, or the entry is the row being updated); otherwise falls through with the cursor on the conflicting entry.
    /// </summary>
    private RunResult DoIdxProbe(ref Instr ins)
    {
        VmCursor c = _cur[ins.P1];
        int n = ins.P5 & 0xFF;
        if (_wstep == 0)
        {
            for (int i = 0; i < n; i++)
            {
                if (R[ins.P3 + i].Kind == StorageClass.Null && (ins.P5 & 0x100) != 0)
                {
                    _pc = ins.P2;
                    return RunResult.Done;
                }
            }

            BtStatus s = c.Bt!.MoveToKey(PrepareKey(ins.P3, n), 0, out int cmp);
            if (s == BtStatus.Pending)
            {
                return RunResult.Pending;
            }

            if (s == BtStatus.Error)
            {
                return BtFail(c.Bt);
            }

            c.RecordReady = false;
            if (c.Bt.State != CursorState.Valid || cmp != 0)
            {
                _pc = ins.P2;
                return RunResult.Done;
            }

            if (ins.P4 is not ProbeSpec)
            {
                _pc++;
                return RunResult.Done;
            }

            _wstep = 1;
        }

        {
            var spec = (ProbeSpec)ins.P4!;
            BtStatus s = EnsureRecord(c);
            if (s == BtStatus.Pending)
            {
                return RunResult.Pending;
            }

            if (s == BtStatus.Error)
            {
                return BtFail(c.Bt!);
            }

            bool self = true;
            for (int i = 0; i < spec.TailCount && self; i++)
            {
                Mem m = default;
                if (n + i >= c.View.ColumnCount)
                {
                    self = false;
                    break;
                }

                c.View.Load(n + i, ref m);
                Mem want = R[spec.TailBase + i];
                if (m.Kind != want.Kind)
                {
                    self = false;
                }
                else if (m.Kind == StorageClass.Integral)
                {
                    self = m.IntValue == want.IntValue;
                }
                else if (m.Kind is StorageClass.Text or StorageClass.Blob)
                {
                    if (m.Kind == StorageClass.Text && _enc != TextEncoding.Utf8)
                    {
                        Values.SetBytes(ref m, ref _tmpOwn, StorageClass.Text, Encoding.UTF8.GetBytes(TextCodec.Decode(m.Bytes, _enc)));
                    }

                    self = m.Bytes.SequenceEqual(want.Bytes);
                }
                else if (m.Kind == StorageClass.Real)
                {
                    self = m.RealValue.Equals(want.RealValue);
                }
            }

            _wstep = 0;
            _pc = self ? ins.P2 : _pc + 1;
            return RunResult.Done;
        }
    }

    private void StrictCheck(ref Instr ins)
    {
        ref Mem m = ref R[ins.P1];
        if (m.Kind == StorageClass.Null)
        {
            return;
        }

        bool ok = ins.P2 switch
        {
            1 => m.Kind == StorageClass.Integral,
            2 => m.Kind == StorageClass.Real,
            3 => m.Kind == StorageClass.Text,
            4 => m.Kind == StorageClass.Blob,
            _ => true,
        };
        if (!ok)
        {
            string kind = m.Kind switch
            {
                StorageClass.Integral => "INT",
                StorageClass.Real => "REAL",
                StorageClass.Text => "TEXT",
                _ => "BLOB",
            };
            throw ConstraintError(ConstraintCodes.DataType, "cannot store " + kind + " value in " + (string)ins.P4!, ErrorAction.Abort);
        }
    }

    // ------------------------------------------------------------------ AUTOINCREMENT (sqlite_sequence)

    private static bool TriggerOnMain(RecordView v, TextEncoding enc)
        => v.ClassOf(4) == StorageClass.Text
            && System.Text.RegularExpressions.Regex.IsMatch(TextCodec.Decode(v.GetBytes(4), enc), @"\bON\s+(""?)main\1\s*\.", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool NameIs(RecordView v, int col, string name, TextEncoding enc)
        => v.ClassOf(col) == StorageClass.Text && string.Equals(TextCodec.Decode(v.GetBytes(col), enc), name, StringComparison.Ordinal);

    // R[P1] = the stored sequence value (0 if the table has no row), R[P1+1] = that row's rowid (NULL if none), R[P1+2] = a copy of the sequence value.
    private RunResult DoSeqLoad(ref Instr ins)
    {
        VmCursor c = _cur[ins.P2];
        string name = (string)ins.P4!;
        for (; ; )
        {
            switch (_wstep)
            {
                case 0:
                {
                    Values.SetInt(ref R[ins.P1], 0);
                    Values.SetNull(ref R[ins.P1 + 1]);
                    BtStatus s = c.Bt!.First();
                    if (s == BtStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    if (s == BtStatus.Error)
                    {
                        return BtFail(c.Bt);
                    }

                    _wstep = 1;
                    break;
                }

                case 1:
                {
                    if (c.Bt!.State != CursorState.Valid)
                    {
                        Values.SetInt(ref R[ins.P1 + 2], R[ins.P1].IntValue);
                        _wstep = 0;
                        _pc++;
                        return RunResult.Done;
                    }

                    c.RecordReady = false;
                    BtStatus s = EnsureRecord(c);
                    if (s == BtStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    if (s == BtStatus.Error)
                    {
                        return BtFail(c.Bt);
                    }

                    if (NameIs(c.View, 0, name, _enc))
                    {
                        Values.SetInt(ref R[ins.P1], c.View.ColumnCount > 1 ? c.View.GetInt64(1) : 0);
                        Values.SetInt(ref R[ins.P1 + 1], c.Bt.Rowid);
                        Values.SetInt(ref R[ins.P1 + 2], R[ins.P1].IntValue);
                        _wstep = 0;
                        _pc++;
                        return RunResult.Done;
                    }

                    _wstep = 2;
                    break;
                }

                default:
                {
                    BtStatus s = c.Bt!.Next();
                    if (s == BtStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    if (s == BtStatus.Error)
                    {
                        return BtFail(c.Bt);
                    }

                    _wstep = 1;
                    break;
                }
            }
        }
    }

    // Writes the sequence back when the statement raised it: R[P1] new value, R[P1+1] rowid of the row or NULL, R[P1+2] the value that was loaded.
    private RunResult DoSeqStore(ref Instr ins)
    {
        VmCursor c = _cur[ins.P2];
        if (R[ins.P1].IntValue == R[ins.P1 + 2].IntValue && R[ins.P1 + 1].Kind != StorageClass.Null)
        {
            _pc++;
            return RunResult.Done;
        }

        if (_wstep == 0)
        {
            if (R[ins.P1 + 1].Kind == StorageClass.Null)
            {
                BtStatus s = c.Bt!.Last();
                if (s == BtStatus.Pending)
                {
                    return RunResult.Pending;
                }

                if (s == BtStatus.Error)
                {
                    return BtFail(c.Bt);
                }

                _tmpRowid = c.Bt.State == CursorState.Valid ? c.Bt.Rowid + 1 : 1;
            }
            else
            {
                _tmpRowid = R[ins.P1 + 1].IntValue;
            }

            _wstep = 1;
        }

        // The row is (name, seq); name is stored in the database's text encoding.
        Mem nm = Mem.FromText((string)ins.P4!, _enc);
        _keyScratch2[0] = nm;
        _keyScratch2[1] = Mem.FromInt(R[ins.P1].IntValue);
        int size = RecordWriter.Size(_keyScratch2);
        if (_recBuf.Length < size)
        {
            _recBuf = new byte[size * 2];
        }

        RecordWriter.Write(_recBuf, _keyScratch2);
        BtStatus w = c.Bt!.Insert(_tmpRowid, new ReadOnlySpan<byte>(_recBuf, 0, size));
        if (w == BtStatus.Pending)
        {
            return RunResult.Pending;
        }

        if (w == BtStatus.Error)
        {
            return BtFail(c.Bt);
        }

        _wstep = 0;
        _pc++;
        return RunResult.Done;
    }

    private readonly Mem[] _keyScratch2 = new Mem[2];

    // ------------------------------------------------------------------ schema changes

    private RunResult DoCreateBtree(ref Instr ins)
    {
        BtContext cctx = _conn.ContextOf(ins.P3);
        BtStatus s = BtAdmin.CreateTree(cctx, ins.P2 != 0, out uint root);
        if (s == BtStatus.Pending)
        {
            return RunResult.Pending;
        }

        if (s == BtStatus.Error)
        {
            return Fail(cctx.Source.LastError ?? new InvalidOperationException("tree creation failed"));
        }

        Values.SetInt(ref R[ins.P1], root);
        _ddl = true;
        _pc++;
        return RunResult.Done;
    }

    // Drops the tree whose root is in R[P1]. In an auto-vacuum database freeing a root can move the last page into its place; the schema row that
    // named the moved root is then rewritten (cursor P2 is a write cursor on sqlite_schema, P3 = 1 if the tree is an index).
    private RunResult DoDestroy(ref Instr ins)
    {
        VmCursor m = _cur[ins.P2];
        BtContext ctx = _conn.ContextOf(ins.P4 is int pdb ? pdb : m.Spec.Table.Db);
        for (; ; )
        {
            switch (_wstep)
            {
                case 0:
                {
                    if (ins.Op == Op.Destroy && !Nested && _conn.Blobs.Count > 0)
                    {
                        // a tree cannot be dropped while an open blob handle has a cursor on the database (the reference refuses the same way)
                        throw new OneLiteException(ResultCode.Locked, "database table is locked");
                    }

                    uint root = (uint)R[ins.P1].IntValue;
                    BTree tree = ins.P3 != 0 ? BTree.Index(ctx, root, KeyInfo.Binary(ctx.Encoding, 1)) : BTree.Table(ctx, root);
                    _eraser = new TreeEraser(tree, ins.Op == Op.Destroy);
                    _wstep = 1;
                    break;
                }

                case 1:
                {
                    BtStatus s = _eraser!.Step();
                    if (s == BtStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    if (s == BtStatus.Error)
                    {
                        return Fail(_eraser.Error ?? new InvalidOperationException("tree erase failed"));
                    }

                    if (_eraser.MovedTo != 0)
                    {
                        // later trees of the same DROP may be the page that moved
                        for (int k = 1; k <= ins.P5; k++)
                        {
                            if ((uint)R[ins.P1 + k].IntValue == _eraser.MovedFrom)
                            {
                                Values.SetInt(ref R[ins.P1 + k], _eraser.MovedTo);
                            }
                        }
                    }

                    if (_eraser.MovedTo == 0)
                    {
                        _eraser = null;
                        _wstep = 0;
                        _ddl = true;
                        _pc++;
                        return RunResult.Done;
                    }

                    _wstep = 2;
                    break;
                }

                case 2:
                {
                    BtStatus s = m.Bt!.First();
                    if (s == BtStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    if (s == BtStatus.Error)
                    {
                        return BtFail(m.Bt);
                    }

                    _wstep = 3;
                    break;
                }

                case 3:
                {
                    if (m.Bt!.State != CursorState.Valid)
                    {
                        _eraser = null;
                        _wstep = 0;
                        _ddl = true;
                        _pc++;
                        return RunResult.Done;
                    }

                    m.RecordReady = false;
                    BtStatus s = EnsureRecord(m);
                    if (s == BtStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    if (s == BtStatus.Error)
                    {
                        return BtFail(m.Bt);
                    }

                    if (m.View.ColumnCount >= 5 && m.View.ClassOf(3) == StorageClass.Integral && (uint)m.View.GetInt64(3) == _eraser!.MovedFrom)
                    {
                        var vals = new Mem[m.View.ColumnCount];
                        for (int i = 0; i < vals.Length; i++)
                        {
                            m.View.Load(i, ref vals[i]);
                        }

                        vals[3] = Mem.FromInt(_eraser.MovedTo);
                        _tmpRowid = m.Bt.Rowid;
                        _pendingRec = RecordWriter.ToArray(vals);
                        _wstep = 4;
                    }
                    else
                    {
                        _wstep = 5;
                    }

                    break;
                }

                case 4:
                {
                    BtStatus s = m.Bt!.Insert(_tmpRowid, _pendingRec);
                    if (s == BtStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    if (s == BtStatus.Error)
                    {
                        return BtFail(m.Bt);
                    }

                    _eraser = null;
                    _pendingRec = null;
                    _wstep = 0;
                    _ddl = true;
                    _pc++;
                    return RunResult.Done;
                }

                default:
                {
                    BtStatus s = m.Bt!.Next();
                    if (s == BtStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    if (s == BtStatus.Error)
                    {
                        return BtFail(m.Bt);
                    }

                    _wstep = 3;
                    break;
                }
            }
        }
    }

    private byte[]? _pendingRec;

    // Deletes schema rows (cursor P1 is a write cursor on sqlite_schema): P3 = 0: every row of table P4 (the table and its indexes);
    // P3 = 1: the index named P4.
    private RunResult DoSchemaDelete(ref Instr ins)
    {
        VmCursor m = _cur[ins.P1];
        string name = (string)ins.P4!;
        for (; ; )
        {
            switch (_wstep)
            {
                case 0:
                {
                    BtStatus s = m.Bt!.First();
                    if (s == BtStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    if (s == BtStatus.Error)
                    {
                        return BtFail(m.Bt);
                    }

                    _wstep = 1;
                    break;
                }

                case 1:
                {
                    if (m.Bt!.State != CursorState.Valid)
                    {
                        _wstep = 0;
                        _ddl = true;
                        _pc++;
                        return RunResult.Done;
                    }

                    m.RecordReady = false;
                    BtStatus s = EnsureRecord(m);
                    if (s == BtStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    if (s == BtStatus.Error)
                    {
                        return BtFail(m.Bt);
                    }

                    bool hit = ins.P3 switch
                    {
                        0 => NameIs(m.View, 2, name, _enc),
                        4 => NameIs(m.View, 2, name, _enc) && NameIs(m.View, 0, "trigger", _enc),
                        // a temp table's rows, except the temp triggers that sit on a table of the same name in main
                        5 => NameIs(m.View, 2, name, _enc) && !(NameIs(m.View, 0, "trigger", _enc) && TriggerOnMain(m.View, _enc)),
                        1 => NameIs(m.View, 1, name, _enc) && NameIs(m.View, 0, "index", _enc),
                        2 => NameIs(m.View, 1, name, _enc) && NameIs(m.View, 0, "view", _enc),
                        _ => NameIs(m.View, 1, name, _enc) && NameIs(m.View, 0, "trigger", _enc),
                    };
                    _wstep = hit ? 2 : 3;
                    break;
                }

                case 2:
                {
                    BtStatus s = m.Bt!.Delete();
                    if (s == BtStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    if (s == BtStatus.Error)
                    {
                        return BtFail(m.Bt);
                    }

                    _wstep = 3;
                    break;
                }

                default:
                {
                    BtStatus s = m.Bt!.Next();
                    if (s == BtStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    if (s == BtStatus.Error)
                    {
                        return BtFail(m.Bt);
                    }

                    _wstep = 1;
                    break;
                }
            }
        }
    }

    // Raises the schema cookie (fileformat2 §1.3.8) so that other connections and prepared statements reload the schema, and sets the schema
    // format number to 4 the first time a schema exists (§1.3.9).
    private string RegText(int r)
    {
        Span<byte> scratch = stackalloc byte[64];
        return System.Text.Encoding.UTF8.GetString(Values.AsText(R[r], scratch));
    }

    // ATTACH file AS name [KEY key]: P1 = file register, P2 = name register, P3 = key register (P5 = 1 when there is a KEY clause).
    // DETACH name: P1 = name register.
    private RunResult DoAttach(ref Instr ins)
    {
        if (ins.Op == Op.DetachDb)
        {
            _conn.DetachDatabase(RegText(ins.P1));
        }
        else
        {
            if (R[ins.P2].Kind == StorageClass.Null)
            {
                throw new OneLiteException(ResultCode.Error, "database is already attached");
            }

            string file = R[ins.P1].Kind == StorageClass.Null ? string.Empty : RegText(ins.P1);
            string? key = ins.P5 != 0 && R[ins.P3].Kind != StorageClass.Null ? RegText(ins.P3) : null;
            _conn.AttachDatabase(file, RegText(ins.P2), ins.P5 != 0 && key is null ? string.Empty : key);
        }

        _pc++;
        return RunResult.Done;
    }

    private RunResult DoBumpCookie(ref Instr ins)
    {
        if (ins.P1 == 1)
        {
            // The temp schema has no header cookie; the connection's epoch stands in for it.
            _conn.TempSchemaChanged();
        }

        IBtPageSource src = ins.P1 != 0 ? _conn.ContextOf(ins.P1).Source : _conn.Source!;
        BtStatus s = src.GetPage(1, out PageHandle? h);
        if (s != BtStatus.Ok)
        {
            return s == BtStatus.Pending ? RunResult.Pending : Fail(src.LastError!);
        }

        s = src.MakeWritable(h!);
        if (s != BtStatus.Ok)
        {
            src.Release(h!);
            return s == BtStatus.Pending ? RunResult.Pending : Fail(src.LastError!);
        }

        Span<byte> d = h!.Data.AsSpan();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.Slice(40, 4), System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(d.Slice(40, 4)) + 1);
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(d.Slice(44, 4)) == 0)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.Slice(44, 4), 4);
        }

        src.Release(h);
        _ddl = true;
        _pc++;
        return RunResult.Done;
    }

    // ------------------------------------------------------------------ nested SELECT as a row source

    private RunResult DoSubSelect(ref Instr ins)
    {
        var spec = (SubSelectSpec)ins.P4!;
        if (_subs.Length <= ins.P1)
        {
            Array.Resize(ref _subs, ins.P1 + 1);
            Array.Resize(ref _subRows, ins.P1 + 1);
            Array.Resize(ref _subPos, ins.P1 + 1);
        }

        _subs[ins.P1]?.Close();
        _subs[ins.P1] = new Machine(_conn, spec.Program, _vars);
        _subRows[ins.P1] = null;
        _subPos[ins.P1] = 0;
        _pc++;
        return RunResult.Done;
    }

    private RunResult DoSubNext(ref Instr ins)
    {
        Machine sub = _subs[ins.P1]!;
        var spec = (SubSelectSpec)ins.P4!;
        int n = spec.Program.ResultCount;
        if (spec.Drain && _subRows[ins.P1] is null)
        {
            List<byte[]> rows = _drain ??= [];
            for (; ; )
            {
                RunResult rr = sub.Run();
                if (rr == RunResult.Pending)
                {
                    return RunResult.Pending;
                }

                if (rr == RunResult.Row)
                {
                    rows.Add(RecordWriter.ToArray(new ReadOnlySpan<Mem>(sub.R, spec.Program.ResultBase, n)));
                    continue;
                }

                if (rr != RunResult.Done)
                {
                    return Fail(sub.Error ?? new InvalidOperationException("nested select failed"));
                }

                break;
            }

            _drain = null;
            _subRows[ins.P1] = rows;
        }

        if (_subRows[ins.P1] is { } list)
        {
            if (_subPos[ins.P1] >= list.Count)
            {
                _pc = ins.P2;
                return RunResult.Done;
            }

            byte[] rec = list[_subPos[ins.P1]++];
            _wview.Parse(rec, 0, rec.Length);
            for (int i = 0; i < n; i++)
            {
                _wview.Load(i, ref R[ins.P3 + i]);
            }

            _pc++;
            return RunResult.Done;
        }

        RunResult res = sub.Run();
        switch (res)
        {
            case RunResult.Pending:
                return RunResult.Pending;
            case RunResult.Row:
                for (int i = 0; i < n; i++)
                {
                    CopyValue(ref R[ins.P3 + i], ref _own[ins.P3 + i], sub.R[spec.Program.ResultBase + i]);
                }

                _pc++;
                return RunResult.Done;
            case RunResult.Done:
                _pc = ins.P2;
                return RunResult.Done;
            default:
                return Fail(sub.Error ?? new InvalidOperationException("nested select failed"));
        }
    }

    private List<byte[]>? _drain;

    /// <summary>True for the program of a foreign key action: it runs inside the statement that fired it and neither starts nor ends a transaction.</summary>
    internal bool Nested;

    private Machine? _fk;
    private long _fkStartDef;

    /// <summary>
    /// Runs a foreign key query or action (P4) with the registers P3.. as its parameters, to completion. A count query leaves its single
    /// result in register P1. Re-entered after Pending, like any nested program.
    /// </summary>
    private RunResult DoFkRun(ref Instr ins)
    {
        var spec = (Planning.FkSpec)ins.P4!;
        if (_fk is null)
        {
            if (_conn.FkDepth >= 1000)
            {
                throw new OneLiteException(ResultCode.Error, "too many levels of trigger recursion");
            }

            VmProgram prog = spec.Get();
            var vars = new Mem[ins.P5];
            for (int i = 0; i < vars.Length; i++)
            {
                vars[i] = R[ins.P3 + i];
            }

            _fk = new Machine(_conn, prog, vars) { Nested = !spec.IsCount };
            _conn.FkDepth++;
            Values.SetInt(ref R[ins.P1], 0);
        }

        for (; ; )
        {
            RunResult rr = _fk.Run();
            switch (rr)
            {
                case RunResult.Pending:
                    return RunResult.Pending;
                case RunResult.Row:
                    if (spec.IsCount)
                    {
                        R[ins.P1] = _fk.R[_fk.ResultBaseOf()];
                    }

                    continue;
                case RunResult.Done:
                    _fk.Close();
                    _fk = null;
                    _conn.FkDepth--;
                    _pc++;
                    return RunResult.Done;
                default:
                {
                    Exception? e = _fk.Error;
                    _fk.Close();
                    _fk = null;
                    _conn.FkDepth--;
                    return Fail(e ?? new InvalidOperationException("foreign key action failed " + rr));
                }
            }
        }
    }

    private Planning.TrigSpec? _trgSpec;
    private Machine? _trgM;
    private int _trgIdx;
    private bool _trgMatched;
    private long _trgRowid;
    private Mem[]? _trgVars;

    // lang_createtrigger: runs the programs of one trigger (its WHEN test, then each body statement) for the row whose OLD and NEW values are
    // in the registers P3.. (P5 of them). A body statement is a nested program like a foreign key action. RAISE(IGNORE) ends the trigger and
    // jumps to P2, the instruction after the row's work. The row the outer statement last inserted stays its last_insert_rowid.
    private RunResult DoTrigRun(ref Instr ins)
    {
        if (_trgSpec is null)
        {
            if (_conn.FkDepth >= 1000)
            {
                throw new OneLiteException(ResultCode.Error, "too many levels of trigger recursion");
            }

            var sp = (Planning.TrigSpec)ins.P4!;
            if (!_conn.RecursiveTriggers && _conn.TrigActive.Contains(sp))
            {
                // lang_createtrigger: with recursive triggers off, a trigger does not fire from inside its own program
                _pc++;
                return RunResult.Done;
            }

            _conn.TrigActive.Add(sp);
            _trgSpec = sp;
            _trgVars = new Mem[ins.P5];
            for (int i = 0; i < _trgVars.Length; i++)
            {
                _trgVars[i] = R[ins.P3 + i];
            }

            _trgIdx = _trgSpec.HasWhen ? -1 : 0;
            _trgMatched = !_trgSpec.HasWhen;
            _trgRowid = _conn.LastInsertRowid;
            _conn.TrigIgnore = false;
            _conn.FkDepth++;
        }

        Planning.TrigSpec spec = _trgSpec;
        for (; ; )
        {
            if (_trgIdx >= spec.BodyCount || (_trgIdx >= 0 && !_trgMatched))
            {
                return TrigFinish(ref ins, false);
            }

            if (_trgM is null)
            {
                VmProgram prog;
                try
                {
                    prog = spec.Get(_trgIdx);
                }
                catch (OneLiteException e)
                {
                    TrigDrop();
                    return Fail(e);
                }

                _trgM = new Machine(_conn, prog, _trgVars!) { Nested = true };
            }

            RunResult rr = _trgM.Run();
            switch (rr)
            {
                case RunResult.Pending:
                    return RunResult.Pending;
                case RunResult.Row:
                    if (_trgIdx == -1)
                    {
                        _trgMatched = true;
                    }

                    continue;
                case RunResult.Done:
                    _trgM.Close();
                    _trgM = null;
                    if (_conn.TrigIgnore)
                    {
                        return TrigFinish(ref ins, true);
                    }

                    _trgIdx++;
                    continue;
                default:
                {
                    Exception? e = _trgM.Error;
                    ErrorAction act = _trgM._errAction;
                    TrigDrop();
                    _errAction = act;
                    return Fail(e ?? new InvalidOperationException("trigger failed"));
                }
            }
        }
    }

    private void TrigDrop()
    {
        _trgM?.Close();
        _trgM = null;
        _conn.TrigActive.Remove(_trgSpec!);
        _trgSpec = null;
        _conn.FkDepth--;
    }

    private RunResult TrigFinish(ref Instr ins, bool ignored)
    {
        _conn.TrigActive.Remove(_trgSpec!);
        _trgSpec = null;
        _conn.FkDepth--;
        _conn.LastInsertRowid = _trgRowid;
        if (ignored)
        {
            _conn.TrigIgnore = false;
            _pc = ins.P2;
        }
        else
        {
            _pc++;
        }

        return RunResult.Done;
    }

    internal int ResultBaseOf() => _prog.ResultBase;

    private static void CopyValue(ref Mem dst, ref byte[]? own, in Mem src)
    {
        switch (src.Kind)
        {
            case StorageClass.Text:
            case StorageClass.Blob:
                Values.SetBytes(ref dst, ref own, src.Kind, src.Bytes);
                break;
            default:
                dst = src;
                dst.Buffer = null;
                break;
        }
    }

    // ------------------------------------------------------------------ pragmas

    private static bool ParseBool(string? v, out bool value)
    {
        value = false;
        switch (v?.ToLowerInvariant())
        {
            case "1" or "on" or "yes" or "true":
                value = true;
                return true;
            case "0" or "off" or "no" or "false":
                return true;
            default:
                return false;
        }
    }

    private static void SetText(ref Mem m, ref byte[]? own, string s) => Values.SetBytes(ref m, ref own, StorageClass.Text, Encoding.UTF8.GetBytes(s));

    // fileformat2 §1.3.11: the user version is a 4-byte signed integer at offset 60 that the application owns.
    private RunResult DoUserVersion(int db, string? arg, int d)
    {
        Pager pager = _conn.PagerOf(db);
        if (pager.PageCount == 0 && arg is null)
        {
            Values.SetInt(ref R[d], 0);
            _pc++;
            return RunResult.Done;
        }

        IBtPageSource src = db == 0 ? _conn.Source! : _conn.ContextOf(db).Source;
        BtStatus s = src.GetPage(1, out PageHandle? h);
        if (s != BtStatus.Ok)
        {
            return s == BtStatus.Pending ? RunResult.Pending : Fail(src.LastError!);
        }

        if (arg is not null)
        {
            s = src.MakeWritable(h!);
            if (s != BtStatus.Ok)
            {
                src.Release(h!);
                return s == BtStatus.Pending ? RunResult.Pending : Fail(src.LastError!);
            }

            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(h!.Data.AsSpan(60, 4), PragmaArgs.Atoi(arg));
        }

        Values.SetInt(ref R[d], System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(h!.Data.AsSpan(60, 4)));
        src.Release(h);
        _pc++;
        return RunResult.Done;
    }

    // The pragmas that answer for an attached database: its own journal mode, synchronous level, header fields and checkpoint. Its I/O runs inline.
    private RunResult DoPragmaAttached(ref Instr ins, int db)
    {
        AttachedDb a = _conn.AttachedBySlot(db)!;
        Pager pg = a.Pager;
        string? arg = (string?)ins.P4;
        int d = ins.P2;
        PragmaSettings st = _conn.PragmaSettings;
        try
        {
            switch (ins.P1)
            {
                case 10:
                    return DoUserVersion(db, arg, d);
                case 1:
                {
                    JournalMode target = pg.JournalMode;
                    if (arg is not null && !a.IsMemory)
                    {
                        JournalMode want = arg.ToLowerInvariant() switch
                        {
                            "delete" => JournalMode.Delete,
                            "truncate" => JournalMode.Truncate,
                            "persist" => JournalMode.Persist,
                            "memory" => JournalMode.Memory,
                            "wal" when a.Path.Length > 0 => JournalMode.Wal,
                            _ => target,
                        };
                        if (want != target)
                        {
                            if (!_conn.AutoCommit || pg.InWriteTransaction)
                            {
                                if (want == JournalMode.Wal)
                                {
                                    throw new OneLiteException(ResultCode.Error, "cannot change into wal mode from within a transaction");
                                }

                                if (target == JournalMode.Wal)
                                {
                                    throw new OneLiteException(ResultCode.Error, "cannot change out of wal mode from within a transaction");
                                }
                            }
                            else
                            {
                                pg.RequireBlockingBackend("PRAGMA journal_mode");
                                PagerStatus ps;
                                while ((ps = pg.ChangeJournalMode(want)) == PagerStatus.Pending)
                                {
                                    pg.ExecuteSync();
                                }

                                if (ps == PagerStatus.Error)
                                {
                                    throw pg.Error!;
                                }
                            }
                        }
                    }

                    SetText(ref R[d], ref _own[d], pg.JournalMode.ToString().ToLowerInvariant());
                    break;
                }

                case 2:
                    if (arg is null)
                    {
                        Values.SetInt(ref R[d], (int)pg.Synchronous);
                    }
                    else if (!_conn.AutoCommit)
                    {
                        throw new OneLiteException(ResultCode.Error, "Safety level may not be changed inside a transaction");
                    }
                    else
                    {
                        int lv = arg.ToLowerInvariant() switch
                        {
                            "off" or "no" or "false" => 0,
                            "on" or "yes" or "true" or "normal" => 1,
                            "full" => 2,
                            "extra" => 3,
                            _ => arg.Length > 0 && arg[0] is >= '0' and <= '9' ? (byte)PragmaArgs.Atoi(arg) : 1,
                        };
                        pg.SetSynchronous(lv switch { 0 => SynchronousMode.Off, 1 => SynchronousMode.Normal, 3 => SynchronousMode.Extra, _ => SynchronousMode.Full });
                    }

                    break;
                case 5:
                    Values.SetInt(ref R[d], pg.PageCount == 0 ? 4096 : pg.PageSize);
                    break;
                case 6:
                    if (arg is not null)
                    {
                        st.Set("db" + db + ".wal_autocheckpoint", PragmaArgs.Atoi(arg));
                    }

                    Values.SetInt(ref R[d], st.Get("db" + db + ".wal_autocheckpoint", 1000));
                    break;
                case 7:
                {
                    CheckpointMode mode = (arg?.ToLowerInvariant()) switch
                    {
                        "full" => CheckpointMode.Full,
                        "restart" => CheckpointMode.Restart,
                        "truncate" => CheckpointMode.Truncate,
                        _ => CheckpointMode.Passive,
                    };
                    if (pg.InWriteTransaction)
                    {
                        throw new OneLiteException(ResultCode.Locked, "database table is locked");
                    }

                    if (!pg.IsWal)
                    {
                        Values.SetInt(ref R[d], 0);
                        Values.SetInt(ref R[d + 1], -1);
                        Values.SetInt(ref R[d + 2], -1);
                        break;
                    }

                    pg.RequireBlockingBackend("PRAGMA wal_checkpoint");
                    a.EndReadQuiet();
                    PagerStatus cs;
                    while ((cs = pg.Checkpoint(mode)) == PagerStatus.Pending)
                    {
                        pg.ExecuteSync();
                    }

                    if (cs == PagerStatus.Error)
                    {
                        throw pg.Error!;
                    }

                    Values.SetInt(ref R[d], pg.LastCheckpoint.Busy ? 1 : 0);
                    Values.SetInt(ref R[d + 1], pg.LastCheckpoint.LogFrames);
                    Values.SetInt(ref R[d + 2], pg.LastCheckpoint.CheckpointedFrames);
                    break;
                }

                case 8:
                    Values.SetInt(ref R[d], pg.AutoVacuum ? (pg.IncrementalVacuum ? 2 : 1) : 0);
                    break;
                default:
                    SetText(ref R[d], ref _own[d], pg.DatabaseEncoding switch { TextEncoding.Utf16Le => "UTF-16le", TextEncoding.Utf16Be => "UTF-16be", _ => "UTF-8" });
                    break;
            }
        }
        catch (SourceFailure e)
        {
            throw Translate(e);
        }

        _pc++;
        return RunResult.Done;
    }

    private RunResult DoPragma(ref Instr ins)
    {
        Pager pg = _conn.Pager;
        string? arg = (string?)ins.P4;
        int d = ins.P2;
        bool temp = ins.P5 == 1;
        PragmaSettings tst = _conn.PragmaSettings;
        if (ins.P5 >= 2 && ins.P1 is 1 or 2 or 5 or 6 or 7 or 8 or 9 or 10)
        {
            return DoPragmaAttached(ref ins, ins.P5);
        }

        if (temp && ins.P1 is 10)
        {
            return DoUserVersion(ins.P5, arg, d);
        }

        if (temp && ins.P1 is 1 or 2 or 5 or 8 or 9 or 10)
        {
            // The temporary database: its own settings, kept on the connection, since it has no file.
            switch (ins.P1)
            {
                case 1:
                {
                    // The temp database changes its journal mode among the rollback modes, and refuses WAL.
                    long jm = tst.Get("temp.journal_mode", 0);
                    if (arg is not null)
                    {
                        jm = arg.ToLowerInvariant() switch { "delete" => 0, "truncate" => 1, "persist" => 2, "memory" => 3, _ => jm };
                        tst.Set("temp.journal_mode", jm);
                    }

                    SetText(ref R[d], ref _own[d], jm switch { 1 => "truncate", 2 => "persist", 3 => "memory", _ => "delete" });
                    break;
                }

                case 2:
                    Values.SetInt(ref R[d], 0);
                    break;
                case 5:
                    Values.SetInt(ref R[d], 4096);
                    break;
                case 8:
                    if (arg is not null)
                    {
                        tst.Set("temp.auto_vacuum", arg.ToLowerInvariant() switch { "none" => 0, "full" => 1, "incremental" => 2, _ => Math.Clamp(PragmaArgs.Atoi(arg), 0, 2) });
                    }

                    Values.SetInt(ref R[d], tst.Get("temp.auto_vacuum", 0));
                    break;
                case 10:
                    if (arg is not null)
                    {
                        tst.Set("temp.user_version", PragmaArgs.Atoi(arg));
                    }

                    Values.SetInt(ref R[d], tst.Get("temp.user_version", 0));
                    break;
                default:
                    SetText(ref R[d], ref _own[d], pg.DatabaseEncoding switch { TextEncoding.Utf16Le => "UTF-16le", TextEncoding.Utf16Be => "UTF-16be", _ => "UTF-8" });
                    break;
            }

            _pc++;
            return RunResult.Done;
        }

        switch (ins.P1)
        {
            case 10:
                return DoUserVersion(0, arg, d);

            case 1:
            {
                JournalMode target = pg.JournalMode;
                if (arg is not null)
                {
                    switch (arg.ToLowerInvariant())
                    {
                        case "delete": target = JournalMode.Delete; break;
                        case "truncate": target = JournalMode.Truncate; break;
                        case "persist": target = JournalMode.Persist; break;
                        case "memory": target = JournalMode.Memory; break;
                        case "off": break; // the shell runs in defensive mode, which refuses journal_mode=off
                        case "wal": target = JournalMode.Wal; break;
                        default: break;
                    }

                    if (_wstep != 5 && (!_conn.AutoCommit || pg.InWriteTransaction))
                    {
                        if (target == JournalMode.Wal && pg.JournalMode != JournalMode.Wal)
                        {
                            throw new OneLiteException(ResultCode.Error, "cannot change into wal mode from within a transaction");
                        }

                        if (pg.JournalMode == JournalMode.Wal && target != JournalMode.Wal)
                        {
                            throw new OneLiteException(ResultCode.Error, "cannot change out of wal mode from within a transaction");
                        }

                        target = pg.JournalMode;
                    }

                    if (_wstep == 5 || target != pg.JournalMode)
                    {
                        _wstep = 5;
                        PagerStatus s = pg.ChangeJournalMode(target);
                        if (s == PagerStatus.Pending)
                        {
                            return RunResult.Pending;
                        }

                        _wstep = 0;

                        if (s == PagerStatus.Error)
                        {
                            Error = pg.Error;
                            return RunResult.Error;
                        }
                    }
                }

                SetText(ref R[d], ref _own[d], pg.JournalMode.ToString().ToLowerInvariant());
                break;
            }

            case 2:
                if (arg is null)
                {
                    Values.SetInt(ref R[d], _conn.PragmaSettings.Get("synchronous", (int)pg.Synchronous));
                }
                else if (!_conn.AutoCommit)
                {
                    throw new OneLiteException(ResultCode.Error, "Safety level may not be changed inside a transaction");
                }
                else
                {
                    // Words name a level; a number is taken as it is (low byte); anything else means NORMAL.
                    int lv = arg.ToLowerInvariant() switch
                    {
                        "off" or "no" or "false" => 0,
                        "on" or "yes" or "true" or "normal" => 1,
                        "full" => 2,
                        "extra" => 3,
                        _ => arg.Length > 0 && arg[0] is >= '0' and <= '9' ? (byte)PragmaArgs.Atoi(arg) : 1,
                    };
                    _conn.PragmaSettings.Set("synchronous", lv);
                    pg.SetSynchronous(lv switch { 0 => SynchronousMode.Off, 1 => SynchronousMode.Normal, 3 => SynchronousMode.Extra, _ => SynchronousMode.Full });
                }

                break;
            case 3:
                if (arg is null)
                {
                    Values.SetInt(ref R[d], _conn.ForeignKeysEnabled ? 1 : 0);
                }
                else if (_conn.AutoCommit)
                {
                    // foreignkeys: the setting cannot change inside a transaction
                    _conn.ForeignKeysEnabled = PragmaArgs.GetBoolean(arg, 0) != 0;
                }

                break;
            case 11:
                if (arg is null)
                {
                    Values.SetInt(ref R[d], _conn.RecursiveTriggers ? 1 : 0);
                }
                else if (ParseBool(arg, out bool rt))
                {
                    _conn.RecursiveTriggers = rt;
                }

                break;
            case 4:
                if (arg is not null)
                {
                    _conn.BusyTimeoutMs = Math.Max(PragmaArgs.Atoi(arg), 0);
                }

                Values.SetInt(ref R[d], _conn.BusyTimeoutMs);
                break;
            case 5:
                if (arg is not null && int.TryParse(arg, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int ps) && pg.PageCount == 0)
                {
                    pg.TrySetPageSize(ps, pg.ReservedBytes);
                }

                Values.SetInt(ref R[d], pg.PageSize);
                break;
            case 6:
                if (arg is not null)
                {
                    pg.WalAutoCheckpoint = Math.Max(PragmaArgs.Atoi(arg), 0);
                }

                Values.SetInt(ref R[d], pg.WalAutoCheckpoint);
                break;
            case 7:
            {
                CheckpointMode mode = (arg?.ToLowerInvariant()) switch
                {
                    "full" => CheckpointMode.Full,
                    "restart" => CheckpointMode.Restart,
                    "truncate" => CheckpointMode.Truncate,
                    _ => CheckpointMode.Passive,
                };
                if (pg.InWriteTransaction)
                {
                    throw new OneLiteException(ResultCode.Locked, "database table is locked");
                }

                if (!pg.IsWal)
                {
                    // A database that is not in WAL mode has nothing to checkpoint: busy 0, log -1, checkpointed -1.
                    Values.SetInt(ref R[d], 0);
                    Values.SetInt(ref R[d + 1], -1);
                    Values.SetInt(ref R[d + 2], -1);
                    break;
                }

                if (_holdsRead)
                {
                    // A checkpoint cannot run inside a read transaction: give ours up first.
                    if (!_conn.AutoCommit)
                    {
                        throw new OneLiteException(ResultCode.Locked, "database table is locked", "a checkpoint cannot run inside a transaction");
                    }

                    PagerStatus e = _conn.EndReadStep();
                    if (e == PagerStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    _holdsRead = false;
                    _conn.ReaderDone();
                }

                PagerStatus s = pg.Checkpoint(mode);
                if (s == PagerStatus.Pending)
                {
                    return RunResult.Pending;
                }

                _wstep = 0;
                if (s == PagerStatus.Error)
                {
                    Error = pg.Error;
                    return RunResult.Error;
                }

                Values.SetInt(ref R[d], pg.LastCheckpoint.Busy ? 1 : 0);
                Values.SetInt(ref R[d + 1], pg.LastCheckpoint.LogFrames);
                Values.SetInt(ref R[d + 2], pg.LastCheckpoint.CheckpointedFrames);
                break;
            }

            case 8:
            {
                // pragma auto_vacuum: a database with no pages takes any mode at once; an auto-vacuum database switches between full and
                // incremental at once (a header flag); every other change waits for the next VACUUM. The get always answers the file's mode.
                int cur = pg.AutoVacuum ? (pg.IncrementalVacuum ? 2 : 1) : 0;
                if (arg is not null && TryParseAutoVacuum(arg, out int want))
                {
                    if (pg.PageCount == 0 && want != 0)
                    {
                        pg.TrySetAutoVacuum(true, want == 2);
                    }
                    else if (cur != 0 && want != 0 && want != cur)
                    {
                        if (!_writing)
                        {
                            RunResult wr = DoTransactionWrite(false);
                            if (wr != RunResult.Done)
                            {
                                return wr;
                            }
                        }

                        PagerStatus fs = pg.SetIncrementalFlag(want == 2);
                        if (fs == PagerStatus.Pending)
                        {
                            return RunResult.Pending;
                        }

                        if (fs == PagerStatus.Error)
                        {
                            Error = pg.Error;
                            return RunResult.Error;
                        }
                    }

                    _conn.PendingAutoVacuum = want;
                    cur = pg.AutoVacuum ? (pg.IncrementalVacuum ? 2 : 1) : 0;
                    break;
                }

                Values.SetInt(ref R[d], cur);
                break;
            }
            default:
                SetText(ref R[d], ref _own[d], pg.DatabaseEncoding switch { TextEncoding.Utf16Le => "UTF-16le", TextEncoding.Utf16Be => "UTF-16be", _ => "UTF-8" });
                break;
        }

        _pc++;
        return RunResult.Done;
    }

    private static bool TryParseAutoVacuum(string s, out int mode)
    {
        mode = -1;
        switch (s.ToLowerInvariant())
        {
            case "0": case "none": mode = 0; break;
            case "1": case "full": mode = 1; break;
            case "2": case "incremental": mode = 2; break;
            default: break;
        }

        return mode >= 0;
    }
}
