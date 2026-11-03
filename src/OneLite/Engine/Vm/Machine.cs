using System.Runtime.CompilerServices;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Planning;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Vm;

/// <summary>How a run of the VM ended or paused.</summary>
internal enum RunResult : byte
{
    Row,
    Done,
    Pending,
    Error,

    /// <summary>The schema (or page geometry) changed since the program was compiled; the statement must be compiled again.</summary>
    Stale,
}

/// <summary>The state of one open cursor of a running program.</summary>
internal sealed class VmCursor
{
    internal CursorSpec Spec = null!;
    internal BTree? Tree;
    internal BtCursor? Bt;
    internal KeyInfo? KeyInfo;
    internal readonly RecordView View = new();
    internal bool RecordReady;

    /// <summary>The database has no pages yet (an empty file), so every tree is empty.</summary>
    internal bool Empty;

    /// <summary>Progress of a multi-call op (seek then step): 0 = not started.</summary>
    internal int Step;

    /// <summary>The ephemeral table this cursor reads, for cursors whose spec is ephemeral.</summary>
    internal EphTable? Eph;

    /// <summary>An outer join's missing row: every column reads as NULL.</summary>
    internal bool NullRow;

    /// <summary>The module cursor of a table-valued function, made on first use.</summary>
    internal ModuleCursor? Mod;

    /// <summary>The scan position of a virtual table, made on first use.</summary>
    internal VirtualTables.VCursor? VCur;
}

/// <summary>
/// Executes a <see cref="VmProgram"/>. <see cref="Run"/> returns Row, Done, Pending or Error and never blocks. An instruction that needs a page
/// that is not cached returns Pending without moving <c>pc</c>; when run again it repeats the same B-tree call, which resumes where it was
/// (spec: reserve, then effect). Output registers, counters and the result row change only after the last call that can suspend.
/// </summary>
internal sealed partial class Machine
{
    private readonly Connection _conn;
    private readonly VmProgram _prog;
    private readonly Instr[] _code;
    private readonly Mem[] _vars;
    private readonly TextEncoding _enc;

    internal readonly Mem[] R;
    private readonly byte[]?[] _own;
    private readonly VmCursor[] _cur;
    private int _pc;
    private int _txStep;
    private bool _holdsRead;
    private bool _finished;
    private bool _failed;
    private byte[]? _tmpA, _tmpB;
    private Mem[] _keyScratch = [];
    private readonly RecordView _sv = new();

    // sorters, sets, aggregates, once-flags
    private readonly RecordSorter?[] _sorters;
    private readonly SetStore?[] _sets;
    private readonly AggState?[] _aggs;
    private readonly WindowRun?[] _wins;
    private readonly bool[] _once;

    internal Machine(Connection conn, VmProgram prog, Mem[] vars)
    {
        _conn = conn;
        _prog = prog;
        _code = prog.Code;
        _vars = vars;
        _enc = prog.Encoding;
        R = new Mem[prog.RegisterCount + 1];
        _own = new byte[]?[prog.RegisterCount + 1];
        _cur = new VmCursor[prog.Cursors.Length];
        for (int i = 0; i < _cur.Length; i++)
        {
            _cur[i] = new VmCursor { Spec = prog.Cursors[i] };
            if (prog.Cursors[i].Ephemeral)
            {
                _cur[i].Eph = new EphTable();
            }
        }

        _sorters = new RecordSorter?[prog.SorterCount];
        _sets = new SetStore?[prog.SetCount];
        _aggs = new AggState?[prog.Aggregates.Length];
        _wins = new WindowRun?[prog.WindowStages.Length];
        _once = new bool[prog.OnceCount];
        PlanCounts = prog.PlanCounterCount == 0 ? [] : new long[prog.PlanCounterCount * 2];
    }

    internal Exception? Error { get; private set; }

    /// <summary>The statement this machine belongs to (null for a nested row source).</summary>
    internal Statement? _owner;

    internal bool Finished => _finished;

    /// <summary>Starts again from the first instruction (cursors and read transaction must already be closed).</summary>
    internal void Rewind()
    {
        _pc = 0;
        _txStep = 0;
        _ownKnown = false;
        _busyTry = 0;
        _finished = false;
        _failed = false;
        BlockedByBusy = false;
        BlockedAtRead = false;
        _cleanupFailed = false;
        Error = null;
        DropTransient();
        _rowSet?.Clear();
        _rowKeys?.Clear();
        _rowSetPos = 0;
        _retRows?.Clear();
        _retPos = 0;
        _changes = 0;
        _fw = 0;
        _fwReloading = false;
        _fwReloaded = false;
        _wstep = 0;
        _errAction = ErrorAction.Abort;
        _fwWhole = false;
        _fwStmt = false;
        _drain = null;
        _dml = false;
        _ddl = false;
        _writing = false;
        _stmtLevel = 0;
        Array.Clear(R);
    }

    private void DropTransient()
    {
        for (int i = 0; i < _sorters.Length; i++)
        {
            _sorters[i]?.Dispose();
            _sorters[i] = null;
        }

        Array.Clear(_sets);
        foreach (WindowRun? w in _wins)
        {
            w?.CloseSpill();
        }

        // The window runs are kept (their buffers are reused); WinOpen resets each one.
        Array.Clear(_once);
        foreach (VmCursor c in _cur)
        {
            c.Eph?.Clear();
            c.NullRow = false;
        }
    }

    // ------------------------------------------------------------------ helpers

    private RunResult Fail(Exception e)
    {
        Error = e is SourceFailure ? Translate(e) : e;
        return RunResult.Error;
    }

    internal static Exception Translate(Exception e)
    {
        switch (e)
        {
            case SourceFailure sf:
                return Translate(sf.Inner);
            default:
                return e;
        }
    }

    private RunResult BtFail(BtCursor c) => Fail(c.Error ?? c.SourceError ?? new InvalidOperationException("cursor failed without an error"));

    private VmCursor Cursor(int i) => _cur[i];

    /// <summary>
    /// Puts a cursor that a write by another cursor of the connection saved back on its entry before its row is read, as the reference does for a
    /// cursor that "requires seek" (lang_transaction: a pending SELECT sees the changes its own connection makes). The decoded record of the old
    /// position is dropped, since the page may have been edited since.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static BtStatus Revive(VmCursor c)
    {
        if (c.Bt is not { State: CursorState.Saved } bt)
        {
            return BtStatus.Ok;
        }

        return ReviveSaved(c, bt);
    }

    private static BtStatus ReviveSaved(VmCursor c, BtCursor bt)
    {
        BtStatus s = bt.Restore();
        if (s == BtStatus.Ok)
        {
            c.RecordReady = false;
        }

        return s;
    }

    /// <summary>Loads and parses the current entry so its columns can be read. Returns Ok, Pending or Error.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static BtStatus EnsureRecord(VmCursor c)
    {
        BtStatus rs = Revive(c);
        if (rs != BtStatus.Ok)
        {
            return rs;
        }

        if (c.RecordReady)
        {
            return BtStatus.Ok;
        }

        BtStatus s = c.Bt!.LoadRecord(c.View, out bool wellFormed);
        if (s != BtStatus.Ok)
        {
            return s;
        }

        if (!wellFormed)
        {
            throw new OneLiteException(ResultCode.Corrupt, "database disk image is malformed", "a record in the b-tree is malformed");
        }

        c.RecordReady = true;
        return BtStatus.Ok;
    }

    // Whether this cleanup has already seen a failure. The cleanup can suspend after the write side has failed (while the read transaction
    // ends), and the re-entry arrives with error=false; without this memory a failed commit (SQLITE_BUSY at the EXCLUSIVE lock) came back as success.
    private bool _cleanupFailed;

    private RunResult FinishCleanup(bool error)
    {
        error |= _cleanupFailed;
        // Pins first, then the read transaction (pager rule: all pages released before EndRead).
        foreach (VmCursor c in _cur)
        {
            c.Bt?.Reset();
            c.RecordReady = false;
            c.Step = 0;
            c.VCur?.Dispose();
            c.VCur = null;
        }

        foreach (Machine? sm in _subs)
        {
            sm?.Close();
        }

        if (_writing)
        {
            RunResult fw = FinishWrite(error);
            if (fw == RunResult.Pending)
            {
                return fw;
            }

            error |= fw == RunResult.Error;
            _cleanupFailed = error;
        }

        DropTransient();
        if (_holdsRead && _conn.HoldReadForBlocked(this))
        {
            // c3ref/step: a statement refused with SQLITE_BUSY is still active, so the read transaction of the statements that ran after it
            // does not end when they finish; it ends when the refused statement is reset.
            _holdsRead = false;
            _conn.ReaderDone();
        }
        else if (_holdsRead)
        {
            PagerStatus s = _conn.EndReadStep();
            if (s == PagerStatus.Pending)
            {
                return RunResult.Pending;
            }

            _holdsRead = false;
            _conn.ReaderDone();
            _conn.BlockedHold = false;
            if (s == PagerStatus.Error && !error)
            {
                Error = _conn.PagerError;
                _finished = true;
                _cleanupFailed = false;
                return RunResult.Error;
            }
        }

        _finished = true;
        _cleanupFailed = false;
        return error ? RunResult.Error : RunResult.Done;
    }

    /// <summary>Releases everything the run holds; used by Reset and Dispose. Runs any I/O inline.</summary>
    internal void Close()
    {
        _writing = false;
        DropTransient();
        foreach (VmCursor c in _cur)
        {
            c.Bt?.Dispose();
            c.Bt = null;
            c.Tree = null;
            c.VCur?.Dispose();
            c.VCur = null;
        }

        if (_holdsRead)
        {
            _conn.AbortRead();
            _holdsRead = false;
            _conn.ReaderDone();
        }

        _finished = true;
    }

    /// <summary>
    /// Like <see cref="Close"/>, but the cursors and the machine's buffers are kept for the next statement that runs the same program
    /// (see <see cref="Unpark"/>). The cursors are parked: they hold no pages and are not told about changes.
    /// </summary>
    internal void Park()
    {
        _writing = false;
        DropTransient();
        foreach (VmCursor c in _cur)
        {
            c.Bt?.Park();
            c.VCur?.Dispose();
            c.VCur = null;
        }

        if (_holdsRead)
        {
            _conn.AbortRead();
            _holdsRead = false;
            _conn.ReaderDone();
        }

        _finished = true;
    }

    /// <summary>Makes a parked machine ready to run its program from the start for a new statement.</summary>
    internal void Unpark(Statement owner)
    {
        foreach (VmCursor c in _cur)
        {
            if (c.Bt is null)
            {
                continue;
            }

            // A cursor is kept only while the connection still has the context it was opened in (a page size change, say, builds a new one).
            if (ReferenceEquals(_conn.ContextOf(c.Spec.Table.Db), c.Bt.Context))
            {
                c.Bt.Unpark();
            }
            else
            {
                c.Bt = null;
                c.Tree = null;
            }
        }

        _owner = owner;
        Rewind();
    }

    internal void ReleaseForReset()
    {
        DropTransient();
        foreach (VmCursor c in _cur)
        {
            c.Bt?.Reset();
            c.RecordReady = false;
            c.Step = 0;
            c.VCur?.Dispose();
            c.VCur = null;
        }

        if (_holdsRead)
        {
            _conn.AbortRead();
            _holdsRead = false;
            _conn.ReaderDone();
        }
    }

    // ------------------------------------------------------------------ the loop

    internal RunResult Run()
    {
        Values.BlobEncoding = _enc;
        Scalar.Connection = _conn;
        DateFuncs.BeginStatement();
        if (_finished)
        {
            throw new InvalidOperationException("the program has finished; reset the statement first");
        }

        if (_failed)
        {
            return FinishCleanup(true);
        }

        if (_aborted)
        {
            _aborted = false;
            Error = new OneLiteException(ResultCode.Abort, "query aborted");
            _failed = true;
            return FinishCleanup(true);
        }

        try
        {
            if (_savedCursors)
            {
                RunResult back = RestoreSavedCursors();
                if (back == RunResult.Error)
                {
                    _failed = true;
                    return FinishCleanup(true);
                }

                if (back == RunResult.Pending)
                {
                    return back;
                }
            }

            RunResult r = Loop();
            if (r == RunResult.Error)
            {
                _failed = true;
                return FinishCleanup(true);
            }

            return r;
        }
        catch (OneLiteException e)
        {
            Error = e;
            _failed = true;
            return FinishCleanup(true);
        }
    }

    /// <summary>Loop starts and rows per counter of the program's query plan (two slots each); empty unless the program counts.</summary>
    internal readonly long[] PlanCounts;

    private RunResult Loop()
    {
        Mem[] r = R;
        Instr[] code = _code;
        for (; ; )
        {
            ref Instr ins = ref code[_pc];
            switch (ins.Op)
            {
                case Op.Goto:
                    if (ins.P2 <= _pc && _conn.InterruptRequested)
                    {
                        throw _conn.TakeInterrupt();
                    }

                    _pc = ins.P2;
                    continue;
                case Op.TrigIgnore:
                    // lang_createtrigger RAISE(IGNORE): this trigger program ends; whoever started it skips the row.
                    _conn.TrigIgnore = true;
                    return FinishCleanup(false);
                case Op.Halt:
                {
                    RunResult h = FinishCleanup(false);
                    if (h == RunResult.Done && ins.P1 == 1)
                    {
                        // Commit first, answer after: a caller that reads the one row and stops must still have the statement's effect
                        // (PRAGMA rekey changes the key only when its transaction commits).
                        _finished = false;
                        _pc++;
                        continue;
                    }

                    return h;
                }

                case Op.Transaction:
                {
                    RunResult t = DoTransaction(ref ins);
                    if (t != RunResult.Done)
                    {
                        return t;
                    }

                    _pc++;
                    continue;
                }

                case Op.OpenRead:
                {
                    VmCursor c = _cur[ins.P1];
                    if (c.Bt == null)
                    {
                        CursorSpec spec = c.Spec;
                        BtContext ctx = _conn.ContextOf(spec.Table.Db);
                        if (spec.IsIndexTree)
                        {
                            c.KeyInfo = _conn.MakeKeyInfo(spec);
                            c.Tree = BTree.Index(ctx, spec.Root, c.KeyInfo);
                        }
                        else
                        {
                            c.Tree = BTree.Table(ctx, spec.Root);
                        }

                        c.Bt = c.Tree.OpenCursor();
                    }

                    c.RecordReady = false;
                    c.NullRow = false;
                    c.Empty = _conn.PagerOf(c.Spec.Table.Db).PageCount == 0;
                    _pc++;
                    continue;
                }

                case Op.Rewind:
                {
                    VmCursor c = _cur[ins.P1];
                    if (c.Empty)
                    {
                        _pc = ins.P2;
                        continue;
                    }

                    BtStatus s = c.Bt!.First();
                    if (s == BtStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    if (s == BtStatus.Error)
                    {
                        return BtFail(c.Bt);
                    }

                    c.RecordReady = false;
                    _pc = c.Bt.State == CursorState.Valid ? _pc + 1 : ins.P2;
                    continue;
                }

                case Op.Last:
                {
                    VmCursor c = _cur[ins.P1];
                    if (c.Empty)
                    {
                        _pc = ins.P2;
                        continue;
                    }

                    BtStatus s = c.Bt!.Last();
                    if (s == BtStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    if (s == BtStatus.Error)
                    {
                        return BtFail(c.Bt);
                    }

                    c.RecordReady = false;
                    _pc = c.Bt.State == CursorState.Valid ? _pc + 1 : ins.P2;
                    continue;
                }

                case Op.Prev:
                {
                    VmCursor c = _cur[ins.P1];
                    BtStatus s = c.Bt!.Previous();
                    if (s == BtStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    if (s == BtStatus.Error)
                    {
                        return BtFail(c.Bt);
                    }

                    c.RecordReady = false;
                    _pc = c.Bt.State == CursorState.Valid ? ins.P2 : _pc + 1;
                    continue;
                }

                case Op.Next:
                {
                    if (_conn.InterruptRequested)
                    {
                        throw _conn.TakeInterrupt();
                    }

                    VmCursor c = _cur[ins.P1];
                    BtStatus s = c.Bt!.Next();
                    if (s == BtStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    if (s == BtStatus.Error)
                    {
                        return BtFail(c.Bt);
                    }

                    c.RecordReady = false;
                    _pc = c.Bt.State == CursorState.Valid ? ins.P2 : _pc + 1;
                    continue;
                }

                case Op.SeekRowid:
                {
                    RunResult s = DoSeekRowid(ref ins);
                    if (s != RunResult.Done)
                    {
                        return s;
                    }

                    continue;
                }

                case Op.SeekGE:
                case Op.SeekGT:
                {
                    RunResult s = _cur[ins.P1].Spec.IsIndexTree ? DoSeekIndex(ref ins) : DoSeekRange(ref ins);
                    if (s != RunResult.Done)
                    {
                        return s;
                    }

                    continue;
                }

                case Op.IdxGT:
                case Op.IdxGE:
                {
                    VmCursor c = _cur[ins.P1];
                    BtStatus s = EnsureRecord(c);
                    if (s == BtStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    if (s == BtStatus.Error)
                    {
                        return BtFail(c.Bt!);
                    }

                    ReadOnlySpan<Mem> key = PrepareKey(ins.P3, ins.P5);
                    int cmp = RecordCompare.Compare(c.KeyInfo!, c.View, key, 0);
                    bool jump = ins.Op == Op.IdxGT ? cmp > 0 : cmp >= 0;
                    _pc = jump ? ins.P2 : _pc + 1;
                    continue;
                }

                case Op.IdxRowid:
                {
                    VmCursor c = _cur[ins.P1];
                    BtStatus s = EnsureRecord(c);
                    if (s == BtStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    if (s == BtStatus.Error)
                    {
                        return BtFail(c.Bt!);
                    }

                    int n = c.View.ColumnCount;
                    if (n == 0 || c.View.ClassOf(n - 1) != StorageClass.Integral)
                    {
                        throw new OneLiteException(ResultCode.Corrupt, "database disk image is malformed", "an index entry does not end in a rowid");
                    }

                    Values.SetInt(ref r[ins.P2], c.View.GetInt64(n - 1));
                    _pc++;
                    continue;
                }

                case Op.Column:
                {
                    VmCursor c = _cur[ins.P1];
                    if (c.NullRow)
                    {
                        Values.SetNull(ref r[ins.P3]);
                        _pc++;
                        continue;
                    }

                    BtStatus s = EnsureRecord(c);
                    if (s == BtStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    if (s == BtStatus.Error)
                    {
                        return BtFail(c.Bt!);
                    }

                    if (c.Bt is { IsOnNeighbour: true })
                    {
                        Values.SetNull(ref r[ins.P3]);
                        _pc++;
                        continue;
                    }

                    LoadColumn(c, ref ins);
                    _pc++;
                    continue;
                }

                case Op.Rowid:
                {
                    VmCursor c = _cur[ins.P1];
                    BtStatus rs = c.NullRow ? BtStatus.Ok : Revive(c);
                    if (rs == BtStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    if (rs == BtStatus.Error)
                    {
                        return BtFail(c.Bt!);
                    }

                    if (c.NullRow || c.Bt is { IsOnNeighbour: true })
                    {
                        Values.SetNull(ref r[ins.P2]);
                    }
                    else
                    {
                        Values.SetInt(ref r[ins.P2], c.Bt!.Rowid);
                    }

                    _pc++;
                    continue;
                }

                case Op.Integer:
                    Values.SetInt(ref r[ins.P2], (long)ins.P4!);
                    _pc++;
                    continue;
                case Op.Real:
                    Values.SetReal(ref r[ins.P2], (double)ins.P4!);
                    _pc++;
                    continue;
                case Op.String8:
                    Values.SetRef(ref r[ins.P2], StorageClass.Text, (byte[])ins.P4!);
                    _pc++;
                    continue;
                case Op.Blob:
                    Values.SetRef(ref r[ins.P2], StorageClass.Blob, (byte[])ins.P4!);
                    _pc++;
                    continue;
                case Op.Null:
                    Values.SetNull(ref r[ins.P2]);
                    _pc++;
                    continue;
                case Op.Variable:
                    r[ins.P2] = ins.P1 <= _vars.Length ? _vars[ins.P1 - 1] : default;
                    _pc++;
                    continue;
                case Op.Copy:
                    if (r[ins.P1].Kind is StorageClass.Text or StorageClass.Blob && r[ins.P1].Buffer is { } cb && ReferenceEquals(cb, _own[ins.P1]))
                    {
                        // The source is rewritten in place by its next load; a copy that must outlive that needs its own bytes.
                        byte subtype = r[ins.P1].Subtype;
                        Values.SetBytes(ref r[ins.P2], ref _own[ins.P2], r[ins.P1].Kind, r[ins.P1].Bytes);
                        r[ins.P2].Subtype = subtype;
                    }
                    else
                    {
                        r[ins.P2] = r[ins.P1];
                    }

                    _pc++;
                    continue;

                case Op.Add:
                    Arith(ref ins, 0);
                    continue;
                case Op.Subtract:
                    Arith(ref ins, 1);
                    continue;
                case Op.Multiply:
                    Arith(ref ins, 2);
                    continue;
                case Op.Divide:
                    Arith(ref ins, 3);
                    continue;
                case Op.Remainder:
                    Arith(ref ins, 4);
                    continue;
                case Op.BitAnd:
                case Op.BitOr:
                case Op.ShiftLeft:
                case Op.ShiftRight:
                    BitOp(ref ins);
                    continue;
                case Op.Concat:
                    Concat(ref ins);
                    continue;
                case Op.Negate:
                {
                    ref Mem x = ref r[ins.P1];
                    if (x.Kind == StorageClass.Null)
                    {
                        Values.SetNull(ref r[ins.P2]);
                    }
                    else
                    {
                        Mem zero = Mem.FromInt(0);
                        Values.Sub(zero, x, ref r[ins.P2]);
                    }

                    _pc++;
                    continue;
                }

                case Op.BitNot:
                {
                    ref Mem x = ref r[ins.P1];
                    if (x.Kind == StorageClass.Null)
                    {
                        Values.SetNull(ref r[ins.P2]);
                    }
                    else
                    {
                        Values.SetInt(ref r[ins.P2], ~Values.ToInt(x));
                    }

                    _pc++;
                    continue;
                }

                case Op.Not:
                {
                    ref Mem x = ref r[ins.P1];
                    if (x.Kind == StorageClass.Null)
                    {
                        Values.SetNull(ref r[ins.P2]);
                    }
                    else
                    {
                        Values.SetInt(ref r[ins.P2], Values.IsTrue(x) ? 0 : 1);
                    }

                    _pc++;
                    continue;
                }

                case Op.Eq:
                case Op.Ne:
                case Op.Lt:
                case Op.Le:
                case Op.Gt:
                case Op.Ge:
                case Op.Is:
                case Op.IsNot:
                    Compare(ref ins);
                    _pc++;
                    continue;
                case Op.And:
                {
                    ref Mem a = ref r[ins.P1];
                    ref Mem b = ref r[ins.P2];
                    bool af = a.Kind != StorageClass.Null && !Values.IsTrue(a);
                    bool bf = b.Kind != StorageClass.Null && !Values.IsTrue(b);
                    if (af || bf)
                    {
                        Values.SetInt(ref r[ins.P3], 0);
                    }
                    else if (a.Kind == StorageClass.Null || b.Kind == StorageClass.Null)
                    {
                        Values.SetNull(ref r[ins.P3]);
                    }
                    else
                    {
                        Values.SetInt(ref r[ins.P3], 1);
                    }

                    _pc++;
                    continue;
                }

                case Op.Or:
                {
                    ref Mem a = ref r[ins.P1];
                    ref Mem b = ref r[ins.P2];
                    bool at = a.Kind != StorageClass.Null && Values.IsTrue(a);
                    bool bt = b.Kind != StorageClass.Null && Values.IsTrue(b);
                    if (at || bt)
                    {
                        Values.SetInt(ref r[ins.P3], 1);
                    }
                    else if (a.Kind == StorageClass.Null || b.Kind == StorageClass.Null)
                    {
                        Values.SetNull(ref r[ins.P3]);
                    }
                    else
                    {
                        Values.SetInt(ref r[ins.P3], 0);
                    }

                    _pc++;
                    continue;
                }

                case Op.IsNullV:
                    Values.SetInt(ref r[ins.P2], r[ins.P1].Kind == StorageClass.Null ? 1 : 0);
                    _pc++;
                    continue;
                case Op.NotNullV:
                    Values.SetInt(ref r[ins.P2], r[ins.P1].Kind == StorageClass.Null ? 0 : 1);
                    _pc++;
                    continue;
                case Op.Like:
                case Op.Glob:
                    LikeOp(ref ins);
                    _pc++;
                    continue;
                case Op.Function:
                    if (((FuncDef)ins.P4!).User is { } userFn)
                    {
                        Connection.CallUser(userFn, new ReadOnlySpan<Mem>(r, ins.P1, ins.P2), ref r[ins.P3], ref _own[ins.P3]);
                    }
                    else
                    {
                        Scalar.Call(((FuncDef)ins.P4!).Id, new ReadOnlySpan<Mem>(r, ins.P1, ins.P2), ref r[ins.P3], ref _own[ins.P3], _enc, ins.P5);
                    }

                    _pc++;
                    continue;
                case Op.Cast:
                    CastReg(ref ins);
                    _pc++;
                    continue;
                case Op.Affinity:
                    Values.ApplyStored(ref r[ins.P1], ref _own[ins.P1], (Affinity)ins.P5);
                    _pc++;
                    continue;
                case Op.CompareAffinity:
                {
                    ref Mem x = ref r[ins.P1];
                    if ((Affinity)ins.P5 == Affinity.Numeric)
                    {
                        Values.ApplyNumericForCompare(ref x);
                    }
                    else if ((Affinity)ins.P5 == Affinity.Real)
                    {
                        // REAL affinity is NUMERIC that also turns integers into floating point (datatype3 §3)
                        Values.ApplyNumericForCompare(ref x);
                        if (x.Kind == StorageClass.Integral)
                        {
                            Values.SetReal(ref x, x.IntValue);
                        }
                    }
                    else if (x.Kind is StorageClass.Integral or StorageClass.Real)
                    {
                        Values.Stringify(ref x, ref _own[ins.P1]);
                    }

                    _pc++;
                    continue;
                }

                case Op.If:
                case Op.IfNot:
                {
                    ref Mem x = ref r[ins.P1];
                    bool jump;
                    if (x.Kind == StorageClass.Null)
                    {
                        jump = ins.P3 != 0;
                    }
                    else
                    {
                        jump = Values.IsTrue(x) == (ins.Op == Op.If);
                    }

                    _pc = jump ? ins.P2 : _pc + 1;
                    continue;
                }

                case Op.IfNull:
                    _pc = r[ins.P1].Kind == StorageClass.Null ? ins.P2 : _pc + 1;
                    continue;
                case Op.NotNull:
                    _pc = r[ins.P1].Kind != StorageClass.Null ? ins.P2 : _pc + 1;
                    continue;
                case Op.MustBeInt:
                {
                    ref Mem x = ref r[ins.P1];
                    Values.ApplyStored(ref x, ref _own[ins.P1], Affinity.Integer);
                    if (x.Kind != StorageClass.Integral)
                    {
                        // SQLITE_MISMATCH (20) for any value that is not an integer, NULL included (observed through the pinned library; the shell prints no code).
                        throw new OneLiteException(ConstraintCodes.Mismatch, "datatype mismatch");
                    }

                    _pc++;
                    continue;
                }

                case Op.OffsetFix:
                    if (r[ins.P1].IntValue < 0)
                    {
                        Values.SetInt(ref r[ins.P1], 0);
                    }

                    _pc++;
                    continue;
                case Op.IfPos:
                    if (r[ins.P1].IntValue > 0)
                    {
                        r[ins.P1].IntValue -= ins.P3;
                        _pc = ins.P2;
                    }
                    else
                    {
                        _pc++;
                    }

                    continue;
                case Op.DecrJumpZero:
                    r[ins.P1].IntValue--;
                    _pc = r[ins.P1].IntValue == 0 ? ins.P2 : _pc + 1;
                    continue;
                case Op.AddImm:
                    r[ins.P1].IntValue += ins.P2;
                    _pc++;
                    continue;

                case Op.ResultRow:
                    _pc++;
                    return RunResult.Row;

                case Op.SorterOpen:
                    _sorters[ins.P1]?.Dispose();
                    _sorters[ins.P1] = new RecordSorter((SortTerm[])ins.P4!, _enc, _conn.SortVfs is not null, _conn.SortMemoryLimit);
                    _pc++;
                    continue;
                case Op.SorterInsert:
                {
                    // Sort.SpillRun: the row is added once; a spill that suspends is resumed on re-entry without adding it again.
                    RecordSorter sorter = _sorters[ins.P1]!;
                    if (!sorter.SpillInProgress)
                    {
                        sorter.Add(new ReadOnlySpan<Mem>(r, ins.P2, ins.P3));
                    }

                    if (sorter.SpillIfNeeded(_conn.Pager) == SortStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    _pc++;
                    continue;
                }

                case Op.SorterSort:
                {
                    if (_sorters[ins.P1]!.Sort(_conn.Pager, out bool any) == SortStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    _pc = any ? _pc + 1 : ins.P2;
                    continue;
                }

                case Op.SorterData:
                {
                    RecordSorter.SortRow row = _sorters[ins.P1]!.Current;
                    _sv.Parse(row.Buf, row.Off, row.Len);
                    for (int i = 0; i < ins.P3; i++)
                    {
                        _sv.Load(i, ref r[ins.P2 + i]);
                    }

                    _pc++;
                    continue;
                }

                case Op.SorterNext:
                {
                    if (_sorters[ins.P1]!.Next(_conn.Pager, out bool more) == SortStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    _pc = more ? ins.P2 : _pc + 1;
                    continue;
                }

                case Op.Gosub:
                    Values.SetInt(ref r[ins.P1], _pc + 1);
                    _pc = ins.P2;
                    continue;
                case Op.Return:
                    _pc = (int)r[ins.P1].IntValue;
                    continue;
                case Op.PlanCount:
                    PlanCounts[(ins.P1 * 2) + ins.P2]++;
                    _pc++;
                    continue;
                case Op.Once:
                    if (_once[ins.P1])
                    {
                        _pc = ins.P2;
                    }
                    else
                    {
                        _once[ins.P1] = true;
                        _pc++;
                    }

                    continue;
                case Op.Noop:
                    _pc++;
                    continue;
                case Op.VFilter:
                {
                    VmCursor c = _cur[ins.P1];
                    if (c.Mod is null)
                    {
                        c.Mod = ((ModuleDef)ins.P4!).Create();
                        c.Mod.Bind(_conn);
                    }

                    ModuleCursor mod = c.Mod;
                    c.NullRow = false;
                    mod.Filter(new ReadOnlySpan<Mem>(r, ins.P3, ins.P5));
                    if (mod.AtEnd)
                    {
                        _pc = ins.P2;
                        continue;
                    }

                    _pc++;
                    continue;
                }

                case Op.VNext:
                {
                    if (_cur[ins.P1].VCur is { } vnc)
                    {
                        if (vnc.Next() == VirtualTables.ModStatus.Pending)
                        {
                            return RunResult.Pending;
                        }

                        _pc = vnc.Eof ? _pc + 1 : ins.P2;
                        continue;
                    }

                    ModuleCursor mod = _cur[ins.P1].Mod!;
                    mod.Next();
                    _pc = mod.AtEnd ? _pc + 1 : ins.P2;
                    continue;
                }

                case Op.VColumn:
                {
                    VmCursor c = _cur[ins.P1];
                    if (c.VCur is { } vcc && !c.NullRow)
                    {
                        if (vcc.Column(ins.P2, ref r[ins.P3], ref _own[ins.P3]) == VirtualTables.ModStatus.Pending)
                        {
                            return RunResult.Pending;
                        }

                        _pc++;
                        continue;
                    }

                    if (c.NullRow || c.Mod is null)
                    {
                        Values.SetNull(ref r[ins.P3]);
                    }
                    else
                    {
                        c.Mod.Column(ins.P2, ref r[ins.P3], ref _own[ins.P3]);
                    }

                    _pc++;
                    continue;
                }

                case Op.VRowid:
                {
                    VmCursor c = _cur[ins.P1];
                    if (c.VCur is { } vrc && !c.NullRow)
                    {
                        if (vrc.RowId(out long vrid) == VirtualTables.ModStatus.Pending)
                        {
                            return RunResult.Pending;
                        }

                        Values.SetInt(ref r[ins.P2], vrid);
                        _pc++;
                        continue;
                    }

                    if (c.NullRow || c.Mod is null)
                    {
                        Values.SetNull(ref r[ins.P2]);
                    }
                    else
                    {
                        Values.SetInt(ref r[ins.P2], c.Mod.RowNumber);
                    }

                    _pc++;
                    continue;
                }

                case Op.NullRow:
                    _cur[ins.P1].NullRow = true;
                    _pc++;
                    continue;
                case Op.RowKey:
                {
                    VmCursor c = _cur[ins.P1];
                    if (c.NullRow)
                    {
                        Values.SetNull(ref r[ins.P2]);
                    }
                    else if (c.Eph is null && _prog.Cursors[ins.P1].IsIndexTree)
                    {
                        // A WITHOUT ROWID row is identified by its whole key record (the primary key is unique, so the bytes are).
                        BtStatus ks = EnsureRecord(c);
                        if (ks == BtStatus.Pending)
                        {
                            return RunResult.Pending;
                        }

                        if (ks == BtStatus.Error)
                        {
                            return BtFail(c.Bt!);
                        }

                        Values.SetBytes(ref r[ins.P2], ref _own[ins.P2], StorageClass.Blob, c.View.Bytes);
                    }
                    else
                    {
                        BtStatus rs = c.Eph is null ? Revive(c) : BtStatus.Ok;
                        if (rs == BtStatus.Pending)
                        {
                            return RunResult.Pending;
                        }

                        if (rs == BtStatus.Error)
                        {
                            return BtFail(c.Bt!);
                        }

                        if (c.Bt is { IsOnNeighbour: true })
                        {
                            Values.SetNull(ref r[ins.P2]);
                        }
                        else
                        {
                            Values.SetInt(ref r[ins.P2], c.Eph is { } ke ? ke.Pos + 1 : c.Bt!.Rowid);
                        }
                    }

                    _pc++;
                    continue;
                }

                case Op.EphOpen:
                    _cur[ins.P1].Eph!.Clear();
                    _cur[ins.P1].Eph!.Queue = ins.P5 != 0;
                    _cur[ins.P1].NullRow = false;
                    _pc++;
                    continue;
                case Op.EphInsert:
                    _cur[ins.P1].Eph!.Rows.Add(RecordWriter.ToArray(new ReadOnlySpan<Mem>(r, ins.P2, ins.P3)));
                    _pc++;
                    continue;
                case Op.ERewind:
                {
                    VmCursor c = _cur[ins.P1];
                    EphTable e = c.Eph!;
                    e.Pos = 0;
                    c.NullRow = false;
                    if (e.Rows.Count == 0)
                    {
                        _pc = ins.P2;
                        continue;
                    }

                    byte[] row = e.Rows[0]!;
                    c.View.Parse(row, 0, row.Length);
                    _pc++;
                    continue;
                }

                case Op.ENext:
                {
                    VmCursor c = _cur[ins.P1];
                    EphTable e = c.Eph!;
                    if (e.Queue)
                    {
                        e.Rows[e.Pos] = null;
                    }

                    e.Pos++;
                    if (e.Pos >= e.Rows.Count)
                    {
                        _pc++;
                        continue;
                    }

                    byte[] row = e.Rows[e.Pos]!;
                    c.View.Parse(row, 0, row.Length);
                    _pc = ins.P2;
                    continue;
                }

                case Op.EColumn:
                {
                    VmCursor c = _cur[ins.P1];
                    if (c.NullRow || ins.P2 >= c.View.ColumnCount)
                    {
                        Values.SetNull(ref r[ins.P3]);
                    }
                    else
                    {
                        c.View.Load(ins.P2, ref r[ins.P3]);
                    }

                    _pc++;
                    continue;
                }

                case Op.ERowid:
                    Values.SetInt(ref r[ins.P2], _cur[ins.P1].NullRow ? 0 : _cur[ins.P1].Eph!.Pos + 1);
                    _pc++;
                    continue;
                case Op.ECopyRow:
                {
                    EphTable src = _cur[ins.P2].Eph!;
                    EphTable dst = _cur[ins.P1].Eph!;
                    dst.Clear();
                    dst.Rows.Add(src.Rows[src.Pos]);
                    _pc++;
                    continue;
                }

                case Op.SetOpen:
                    _sets[ins.P1] = new SetStore((SetSpec)ins.P4!, _enc);
                    _pc++;
                    continue;
                case Op.SetAdd:
                    _sets[ins.P1]!.Add(new ReadOnlySpan<Mem>(r, ins.P2, ins.P3), ins.P5 != 0);
                    _pc++;
                    continue;
                case Op.SetIgnoreNull:
                    // A set of nothing but NULLs still answers NULL: the reference lands on the NULL entries when no other entry is there to stop it.
                    if (_sets[ins.P1]!.HasNonNull)
                    {
                        _sets[ins.P1]!.HasNull = false;
                    }

                    _pc++;
                    continue;
                case Op.SetFound:
                    _pc = _sets[ins.P1]!.Contains(new ReadOnlySpan<Mem>(r, ins.P3, ins.P5)) ? ins.P2 : _pc + 1;
                    continue;
                case Op.SetToEph:
                {
                    EphTable e = _cur[ins.P2].Eph!;
                    e.Clear();
                    e.Rows.AddRange(_sets[ins.P1]!.Items);
                    _pc++;
                    continue;
                }

                case Op.InSet:
                {
                    SetStore set = _sets[ins.P1]!;
                    bool neg = (ins.P5 & 1) != 0;
                    int width = ins.P5 >> 1;
                    if (width > 1)
                    {
                        InSetRow(set, ins.P2, width, ins.P3, neg);
                        _pc++;
                        continue;
                    }

                    if (r[ins.P2].Kind == StorageClass.Null)
                    {
                        if (set.Items.Count == 0)
                        {
                            Values.SetInt(ref r[ins.P3], neg ? 1 : 0);
                        }
                        else
                        {
                            Values.SetNull(ref r[ins.P3]);
                        }
                    }
                    else if (set.Contains(new ReadOnlySpan<Mem>(r, ins.P2, 1)))
                    {
                        Values.SetInt(ref r[ins.P3], neg ? 0 : 1);
                    }
                    else if (set.HasNull)
                    {
                        Values.SetNull(ref r[ins.P3]);
                    }
                    else
                    {
                        Values.SetInt(ref r[ins.P3], neg ? 1 : 0);
                    }

                    _pc++;
                    continue;
                }

                case Op.AggReset:
                    (_aggs[ins.P1] ??= new AggState(_prog.Aggregates[ins.P1], _enc)).Reset();
                    _pc++;
                    continue;
                case Op.AggStep:
                {
                    bool changed = (_aggs[ins.P1] ??= new AggState(_prog.Aggregates[ins.P1], _enc)).Step(new ReadOnlySpan<Mem>(r, ins.P2, ins.P3));
                    if (ins.P5 != 0)
                    {
                        Values.SetInt(ref r[ins.P5], changed ? 1 : 0);
                    }

                    _pc++;
                    continue;
                }

                case Op.AggFinal:
                    (_aggs[ins.P1] ??= new AggState(_prog.Aggregates[ins.P1], _enc)).Final(ref r[ins.P2], ref _own[ins.P2]);
                    _pc++;
                    continue;

                case Op.WinOpen:
                    if (_wins[ins.P1] is { } run)
                    {
                        run.Reset();
                    }
                    else
                    {
                        _wins[ins.P1] = new WindowRun(_prog.WindowStages[ins.P1], _enc, _conn.SortVfs is not null, _conn.SortMemoryLimit);
                    }

                    _pc++;
                    continue;
                case Op.WinPush:
                {
                    // Window.Spill: the row is pushed once; a spill that suspends is resumed on re-entry without pushing it again.
                    WindowRun wrun = _wins[ins.P1]!;
                    if (!wrun.InFlight)
                    {
                        wrun.Push(r, ins.P2);
                    }

                    if (wrun.Service(_conn.Pager) == SortStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    _pc++;
                    continue;
                }

                case Op.WinOut:
                {
                    if (_wins[ins.P1]!.TryOut(r, ins.P3, _conn.Pager, out bool got) == SortStatus.Pending)
                    {
                        return RunResult.Pending;
                    }

                    _pc = got ? _pc + 1 : ins.P2;
                    continue;
                }
                case Op.WinFinish:
                    _wins[ins.P1]!.Finish();
                    _pc++;
                    continue;

                default:
                {
                    RunResult w = DoWrite(ref ins);
                    if (w != RunResult.Done)
                    {
                        return w;
                    }

                    continue;
                }
            }
        }
    }

    // ------------------------------------------------------------------ transaction

    /// <summary>(a, b, ...) IN (SELECT ...): true if some row equals the probe; otherwise NULL if some row could equal it were its NULLs known; else false (lang_expr "Row Values").</summary>
    private void InSetRow(SetStore set, int first, int width, int dest, bool neg)
    {
        bool anyUnknown = false, found = false;
        var view = new RecordView();
        foreach (byte[] row in set.Items)
        {
            view.Parse(row, 0, row.Length);
            bool unknown = false, equal = true;
            for (int i = 0; i < width && equal; i++)
            {
                Mem x = R[first + i], y = default;
                view.Load(i, ref y);
                if (x.Kind == StorageClass.Null || y.Kind == StorageClass.Null)
                {
                    unknown = true;
                }
                else if (Values.Compare(x, y, set.Collation(i), _enc) != 0)
                {
                    equal = false;
                    unknown = false;
                }
            }

            if (equal && !unknown)
            {
                found = true;
                break;
            }

            anyUnknown |= equal && unknown;
        }

        if (found)
        {
            Values.SetInt(ref R[dest], neg ? 0 : 1);
        }
        else if (anyUnknown)
        {
            Values.SetNull(ref R[dest]);
        }
        else
        {
            Values.SetInt(ref R[dest], neg ? 1 : 0);
        }
    }

    // A pragma that reads only the connection's settings runs without a read transaction, as in the reference, where another connection's lock does not
    // stop it. The few that look at the file (a journal mode that would change, a checkpoint of a WAL database, anything about an attached database, or
    // a connection that has not read the header yet) take the lock as every statement does.
    private bool PragmaNeedsFile(in Instr next)
    {
        switch (next.Op)
        {
            case Op.PragmaVar:
                return next.P4 is PragmaCall { Db: >= 2 };
            case Op.PragmaOp:
            {
                if (next.P5 >= 2)
                {
                    return true;
                }

                if (next.P5 == 1)
                {
                    return false;
                }

                string? arg = next.P4 as string;
                return next.P1 switch
                {
                    1 => arg is not null && (_conn.Pager.IsWal || string.Equals(arg, "wal", StringComparison.OrdinalIgnoreCase)),
                    5 => arg is not null && _conn.Pager.PageCount == 0 || _conn.Context is null,
                    8 => arg is not null || _conn.Context is null,
                    7 => _conn.Pager.IsWal || _conn.Context is null,
                    9 => _conn.Context is null,
                    2 or 3 or 4 or 6 or 11 => false,
                    _ => true,
                };
            }

            default:
                return false;
        }
    }

    private RunResult DoTransaction(ref Instr ins)
    {
        if (Nested)
        {
            return RunResult.Done;
        }

        if (_txStep == 0 && (_conn.ConcDoomed || _conn.Pager.ConcurrentAbandoned))
        {
            throw new OneLiteException(ResultCode.BusySnapshot, Pager.Msg(ResultCode.BusySnapshot), "this transaction's commit was refused or given up; roll it back");
        }

        if ((ins.P5 & 64) != 0 && _txStep == 0 && !PragmaNeedsFile(_code[_pc + 1]))
        {
            return RunResult.Done;
        }

        if (_txStep >= 20)
        {
            RunResult back = BusyBackoff();
            if (back != RunResult.Done)
            {
                return back;
            }
        }

        if (_txStep == 0)
        {
            if (!_ownKnown)
            {
                // Whether this statement starts the connection's read transaction itself: only then may it let the read lock go to wait (see DoTransactionWrite).
                _ownRead = _conn.HoldsNoRead;
                _ownKnown = true;
            }

            PagerStatus s = _conn.BeginReadStep();
            if (s == PagerStatus.Pending)
            {
                return RunResult.Pending;
            }

            if (s == PagerStatus.Error)
            {
                Error = _conn.PagerError;
                BlockedAtRead = (ins.P5 & 1) != 0 && Error is OneLiteException { Code: ResultCode.Busy };
                return RunResult.Error;
            }

            if (!_holdsRead)
            {
                _holdsRead = true;
                _conn.ReaderStarted();
            }

            if (_conn.Attached.Count > 0 && _prog.AttachedReadMask != 0)
            {
                try
                {
                    _conn.BeginAttached(_prog.AttachedReadMask, 0);
                }
                catch (OneLiteException e)
                {
                    Error = e;
                    return RunResult.Error;
                }
            }

            if ((ins.P5 & 1) == 0 && (ins.P5 & 32) == 0)
            {
                _conn.HoldForTransaction();
            }

            // A write statement keeps the read transaction for the open transaction only once it has the write lock: when the lock is refused
            // the read lock taken just for it goes too (lockingv3), so a failed write does not block the connection that holds RESERVED.
            _dml |= (ins.P5 & 8) != 0;
            _txStep = 1;
        }

        if (_txStep == 1)
        {
            // fileformat2 §1.3.8: the schema cookie says whether the schema this program was compiled from is still the file's.
            if (!_conn.GeometryMatches() && (ins.P5 & 4) == 0)
            {
                return RunResult.Stale;
            }

            BtStatus s = _conn.ReadCookie(out uint cookie);
            if (s == BtStatus.Pending)
            {
                return RunResult.Pending;
            }

            if (s == BtStatus.Error)
            {
                Error = Translate(_conn.Source!.LastError!);
                return RunResult.Error;
            }

            if (cookie != _prog.SchemaCookie && (ins.P5 & 4) == 0)
            {
                _conn.FileCookie = cookie;
                return RunResult.Stale;
            }

            _txStep = 2;
        }

        if ((ins.P5 & 1) == 0)
        {
            return RunResult.Done;
        }

        _beginsTransaction = (ins.P5 & 4) != 0;
        RunResult w = DoTransactionWrite((ins.P5 & 16) != 0);
        if (w == RunResult.Done && _txStep >= 20)
        {
            return DoTransaction(ref ins);
        }

        if (w == RunResult.Done)
        {
            _conn.HoldForTransaction();
        }

        return w;
    }

    // ------------------------------------------------------------------ seeks

    private RunResult DoSeekRowid(ref Instr ins)
    {
        VmCursor c = _cur[ins.P1];
        if (c.Empty)
        {
            _pc = ins.P2;
            return RunResult.Done;
        }

        ref Mem k = ref R[ins.P3];
        long target;
        bool possible = true;
        switch (k.Kind)
        {
            case StorageClass.Integral:
                target = k.IntValue;
                break;
            case StorageClass.Real:
            {
                double d = k.RealValue;
                possible = d >= -9223372036854775808.0 && d < 9223372036854775808.0 && d == Math.Floor(d);
                target = possible ? (long)d : 0;
                break;
            }

            default:
                possible = false;
                target = 0;
                break;
        }

        if (!possible)
        {
            _pc = ins.P2;
            return RunResult.Done;
        }

        c.Bt!.TraceRequired = (ins.P5 & 0x200) != 0;
        c.Bt.TraceExact = true;
        BtStatus s = c.Bt.MoveToRowid(target, out int cmp);
        c.Bt.TraceRequired = false;
        c.Bt.TraceExact = false;
        if (s == BtStatus.Pending)
        {
            return RunResult.Pending;
        }

        if (s == BtStatus.Error)
        {
            return BtFail(c.Bt);
        }

        c.RecordReady = false;
        _pc = c.Bt.State == CursorState.Valid && cmp == 0 ? _pc + 1 : ins.P2;
        return RunResult.Done;
    }

    /// <summary>SeekGE / SeekGT on a rowid table: the key may be any value; integers compare with reals exactly and with text and blobs by storage class (datatype3 §4.1).</summary>
    private RunResult DoSeekRange(ref Instr ins)
    {
        VmCursor c = _cur[ins.P1];
        BtCursor bt = c.Bt!;
        bool gt = ins.Op == Op.SeekGT;
        if (c.Empty)
        {
            _pc = ins.P2;
            return RunResult.Done;
        }

        // Step 1: seek (or first). Step 2: move past entries that are too small.
        if (c.Step == 0)
        {
            ref Mem k = ref R[ins.P3];
            long target = 0;
            int action; // 0 none, 1 first, 2 seek
            switch (k.Kind)
            {
                case StorageClass.Integral:
                    target = k.IntValue;
                    action = 2;
                    break;
                case StorageClass.Real:
                {
                    double d = k.RealValue;
                    if (double.IsNaN(d) || d >= 9223372036854775808.0)
                    {
                        action = 0;
                    }
                    else if (d < -9223372036854775808.0)
                    {
                        action = 1;
                    }
                    else
                    {
                        target = (long)(gt ? Math.Floor(d) : Math.Ceiling(d));
                        action = 2;
                    }

                    break;
                }

                default:
                    action = 0; // NULL never compares true; text and blobs sort after every number
                    break;
            }

            if (action == 0)
            {
                _pc = ins.P2;
                return RunResult.Done;
            }

            if (action == 1)
            {
                BtStatus f = bt.First();
                if (f == BtStatus.Pending)
                {
                    return RunResult.Pending;
                }

                if (f == BtStatus.Error)
                {
                    return BtFail(bt);
                }

                c.RecordReady = false;
                _pc = bt.State == CursorState.Valid ? _pc + 1 : ins.P2;
                return RunResult.Done;
            }

            BtStatus s = bt.MoveToRowid(target, out int cmp);
            if (s == BtStatus.Pending)
            {
                return RunResult.Pending;
            }

            if (s == BtStatus.Error)
            {
                return BtFail(bt);
            }

            c.RecordReady = false;
            bool tooSmall = bt.State == CursorState.Valid && (gt ? cmp <= 0 : cmp < 0);
            if (!tooSmall)
            {
                _pc = bt.State == CursorState.Valid ? _pc + 1 : ins.P2;
                return RunResult.Done;
            }

            c.Step = 1;
        }

        BtStatus n = bt.Next();
        if (n == BtStatus.Pending)
        {
            return RunResult.Pending;
        }

        c.Step = 0;
        if (n == BtStatus.Error)
        {
            return BtFail(bt);
        }

        c.RecordReady = false;
        _pc = bt.State == CursorState.Valid ? _pc + 1 : ins.P2;
        return RunResult.Done;
    }

    private RunResult DoSeekIndex(ref Instr ins)
    {
        VmCursor c = _cur[ins.P1];
        BtCursor bt = c.Bt!;
        bool gt = ins.Op == Op.SeekGT;
        if (c.Step == 0)
        {
            ReadOnlySpan<Mem> key = PrepareKey(ins.P3, ins.P5);
            BtStatus s = bt.MoveToKey(key, gt ? -1 : 1, out int cmp);
            if (s == BtStatus.Pending)
            {
                return RunResult.Pending;
            }

            if (s == BtStatus.Error)
            {
                return BtFail(bt);
            }

            c.RecordReady = false;
            if (bt.State != CursorState.Valid)
            {
                _pc = ins.P2;
                return RunResult.Done;
            }

            if (cmp >= 0)
            {
                _pc++;
                return RunResult.Done;
            }

            c.Step = 1;
        }

        BtStatus n = bt.Next();
        if (n == BtStatus.Pending)
        {
            return RunResult.Pending;
        }

        c.Step = 0;
        if (n == BtStatus.Error)
        {
            return BtFail(bt);
        }

        c.RecordReady = false;
        _pc = bt.State == CursorState.Valid ? _pc + 1 : ins.P2;
        return RunResult.Done;
    }

    /// <summary>The key registers as the index stores them: text converted to the database encoding when that is not UTF-8.</summary>
    private ReadOnlySpan<Mem> PrepareKey(int first, int n)
    {
        if (_enc == TextEncoding.Utf8)
        {
            return new ReadOnlySpan<Mem>(R, first, n);
        }

        if (_keyScratch.Length < n)
        {
            _keyScratch = new Mem[n];
        }

        for (int i = 0; i < n; i++)
        {
            Mem m = R[first + i];
            if (m.Kind == StorageClass.Text)
            {
                m = Mem.FromTextBytes(TextCodec.Encode(System.Text.Encoding.UTF8.GetString(m.Bytes), _enc));
            }

            _keyScratch[i] = m;
        }

        return new ReadOnlySpan<Mem>(_keyScratch, 0, n);
    }

    // ------------------------------------------------------------------ columns

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void LoadColumn(VmCursor c, ref Instr ins)
    {
        int col = ins.P2;
        ref Mem dst = ref R[ins.P3];
        RecordView v = c.View;
        if (col >= v.ColumnCount)
        {
            // A record written before ALTER TABLE ADD COLUMN is shorter than the table: the missing columns read as their default (lang_altertable).
            dst = ins.P4 is DefaultMem d ? d.Value : default;
            return;
        }

        v.Load(col, ref dst);
        if (dst.Kind == StorageClass.Text && _enc != TextEncoding.Utf8)
        {
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(TextCodec.Decode(dst.Bytes, _enc));
            Values.SetBytes(ref dst, ref _own[ins.P3], StorageClass.Text, utf8);
        }
        else if (ins.P5 == 1 && dst.Kind == StorageClass.Integral)
        {
            // A REAL column stores whole numbers as integers; they read back as REAL (datatype3 §3.1).
            Values.SetReal(ref dst, dst.IntValue);
        }
        else if ((_dml || Nested) && dst.Kind is StorageClass.Text or StorageClass.Blob)
        {
            // A value read by a statement that writes is copied: it points into a page otherwise, and the statement's own writes move the bytes
            // under it before an index entry made from it is stored.
            Values.SetBytes(ref dst, ref _own[ins.P3], dst.Kind, dst.Bytes);
        }
    }

    // ------------------------------------------------------------------ operators

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Arith(ref Instr ins, int kind)
    {
        ref Mem a = ref R[ins.P1];
        ref Mem b = ref R[ins.P2];
        ref Mem d = ref R[ins.P3];
        if (a.Kind == StorageClass.Null || b.Kind == StorageClass.Null)
        {
            Values.SetNull(ref d);
        }
        else
        {
            switch (kind)
            {
                case 0:
                    Values.Add(a, b, ref d);
                    break;
                case 1:
                    Values.Sub(a, b, ref d);
                    break;
                case 2:
                    Values.Mul(a, b, ref d);
                    break;
                case 3:
                    Values.Div(a, b, ref d);
                    break;
                default:
                    Values.Rem(a, b, ref d);
                    break;
            }
        }

        _pc++;
    }

    private void BitOp(ref Instr ins)
    {
        ref Mem a = ref R[ins.P1];
        ref Mem b = ref R[ins.P2];
        ref Mem d = ref R[ins.P3];
        if (a.Kind == StorageClass.Null || b.Kind == StorageClass.Null)
        {
            Values.SetNull(ref d);
        }
        else
        {
            long x = Values.ToInt(a), y = Values.ToInt(b);
            Values.SetInt(ref d, ins.Op switch
            {
                Op.BitAnd => x & y,
                Op.BitOr => x | y,
                Op.ShiftLeft => Values.ShiftLeft(x, y),
                _ => Values.ShiftRight(x, y),
            });
        }

        _pc++;
    }

    private void Concat(ref Instr ins)
    {
        ref Mem a = ref R[ins.P1];
        ref Mem b = ref R[ins.P2];
        ref Mem d = ref R[ins.P3];
        if (a.Kind == StorageClass.Null || b.Kind == StorageClass.Null)
        {
            Values.SetNull(ref d);
            _pc++;
            return;
        }

        Span<byte> ta = stackalloc byte[Values.MaxRealText];
        Span<byte> tb = stackalloc byte[Values.MaxRealText];
        scoped ReadOnlySpan<byte> x, y;
        if (_enc != TextEncoding.Utf8 && (a.Kind == StorageClass.Blob || b.Kind == StorageClass.Blob))
        {
            // Blob bytes join the other operand's text as stored, i.e. in the database encoding, and the whole is read as text.
            byte[] ea = a.Kind == StorageClass.Blob ? a.Bytes.ToArray() : TextCodec.Encode(System.Text.Encoding.UTF8.GetString(Values.AsText(a, ta)), _enc);
            byte[] eb = b.Kind == StorageClass.Blob ? b.Bytes.ToArray() : TextCodec.Encode(System.Text.Encoding.UTF8.GetString(Values.AsText(b, tb)), _enc);
            x = System.Text.Encoding.UTF8.GetBytes(TextCodec.Decode(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Concat(ea, eb)), _enc));
            y = default;
        }
        else
        {
            x = Values.AsText(a, ta);
            y = Values.AsText(b, tb);
        }

        long total = (long)x.Length + y.Length;
        if (total > Values.MaxLength)
        {
            throw new OneLiteException(ResultCode.TooBig, "string or blob too big");
        }

        // Build in the destination register's own buffer so a cached scan allocates nothing; a fresh buffer only when the
        // operands live in that buffer (the register is also a source) or it is too small.
        ref byte[]? own = ref _own[ins.P3];
        if (own == null || own.Length < total || ReferenceEquals(own, a.Buffer) || ReferenceEquals(own, b.Buffer))
        {
            own = new byte[Math.Max((int)total, 32)];
        }

        x.CopyTo(own);
        y.CopyTo(own.AsSpan(x.Length));
        int len = (int)total;
        d.Kind = StorageClass.Text;
        d.Subtype = 0;
        d.Buffer = own;
        d.Offset = 0;
        d.Length = len;
        _pc++;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Compare(ref Instr ins)
    {
        // Two numbers of the same kind compare the same whatever the affinity and collation are, so they skip the general path.
        ref Mem fa = ref R[ins.P1];
        ref Mem fb = ref R[ins.P2];
        if (fa.Kind == fb.Kind && fa.Kind is StorageClass.Integral or StorageClass.Real)
        {
            int fc = fa.Kind == StorageClass.Integral ? fa.IntValue.CompareTo(fb.IntValue) : fa.RealValue.CompareTo(fb.RealValue);
            bool fres = ins.Op switch
            {
                Op.Eq or Op.Is => fc == 0,
                Op.Ne or Op.IsNot => fc != 0,
                Op.Lt => fc < 0,
                Op.Le => fc <= 0,
                Op.Gt => fc > 0,
                _ => fc >= 0,
            };
            Values.SetInt(ref R[ins.P3], fres ? 1 : 0);
            return;
        }

        Mem a = R[ins.P1], b = R[ins.P2];
        ref Mem d = ref R[ins.P3];
        bool an = a.Kind == StorageClass.Null, bn = b.Kind == StorageClass.Null;
        if (an || bn)
        {
            if (ins.Op == Op.Is)
            {
                Values.SetInt(ref d, an && bn ? 1 : 0);
            }
            else if (ins.Op == Op.IsNot)
            {
                Values.SetInt(ref d, an && bn ? 0 : 1);
            }
            else
            {
                Values.SetNull(ref d);
            }

            return;
        }

        var aff = (Affinity)(ins.P5 & 15);
        var coll = (CollKind)((ins.P5 >> 4) & 15);
        Values.PrepareCompare(ref a, ref b, aff, ref _tmpA, ref _tmpB);
        int c = Values.Compare(a, b, coll, _enc);
        bool res = ins.Op switch
        {
            Op.Eq or Op.Is => c == 0,
            Op.Ne or Op.IsNot => c != 0,
            Op.Lt => c < 0,
            Op.Le => c <= 0,
            Op.Gt => c > 0,
            _ => c >= 0,
        };
        Values.SetInt(ref d, res ? 1 : 0);
    }

    private void CastReg(ref Instr ins)
    {
        ref Mem x = ref R[ins.P1];
        var aff = (Affinity)ins.P5;
        // The BLOB form of text is its bytes in the database encoding, and bytes become text by reading them in that encoding (lang_expr CAST).
        if (_enc != TextEncoding.Utf8)
        {
            if (aff == Affinity.None && x.Kind == StorageClass.Text)
            {
                Values.SetBytes(ref x, ref _own[ins.P1], StorageClass.Blob, TextCodec.Encode(System.Text.Encoding.UTF8.GetString(x.Bytes), _enc));
                return;
            }

            if (aff == Affinity.None && x.Kind is StorageClass.Integral or StorageClass.Real)
            {
                // A number's BLOB form is its text in the database encoding.
                Span<byte> nb = stackalloc byte[Values.MaxRealText];
                string num = System.Text.Encoding.ASCII.GetString(Values.AsText(x, nb));
                Values.SetBytes(ref x, ref _own[ins.P1], StorageClass.Blob, TextCodec.Encode(num, _enc));
                return;
            }

            if (aff == Affinity.Text && x.Kind == StorageClass.Blob)
            {
                Values.SetBytes(ref x, ref _own[ins.P1], StorageClass.Text, System.Text.Encoding.UTF8.GetBytes(TextCodec.Decode(x.Bytes, _enc)));
                return;
            }
        }

        Values.Cast(ref x, ref _own[ins.P1], aff);
    }

    private void LikeOp(ref Instr ins)
    {
        ref Mem t = ref R[ins.P1];
        ref Mem p = ref R[ins.P2];
        ref Mem d = ref R[ins.P3];

        // Black-box order (lang_corefunc): a bad ESCAPE and an over-long pattern raise their errors even when another
        // operand is NULL; only then do NULLs make the result NULL.
        int esc = -1;
        bool escNull = false;
        if (ins.P5 != 0)
        {
            ref Mem e = ref R[ins.P5];
            if (e.Kind == StorageClass.Null)
            {
                escNull = true;
            }
            else
            {
                Span<byte> te = stackalloc byte[Values.MaxRealText];
                ReadOnlySpan<byte> eb = Values.AsText(e, te);
                if (Scalar.CharLength(eb) != 1 || eb.Length == 0)
                {
                    throw new OneLiteException(ResultCode.Error, "ESCAPE expression must be a single character");
                }

                if (eb[0] >= 0x80)
                {
                    throw new OneLiteException(ResultCode.Error, "unsupported: a non-ASCII ESCAPE character");
                }

                esc = eb[0];
            }
        }

        Span<byte> tt = stackalloc byte[Values.MaxRealText];
        Span<byte> tp = stackalloc byte[Values.MaxRealText];
        ReadOnlySpan<byte> pat = p.Kind == StorageClass.Null ? default : Values.AsText(p, tp);
        if (pat.Length > Scalar.MaxPattern)
        {
            throw new OneLiteException(ResultCode.Error, "LIKE or GLOB pattern too complex");
        }

        if (t.Kind == StorageClass.Null || p.Kind == StorageClass.Null || escNull)
        {
            Values.SetNull(ref d);
            return;
        }

        ReadOnlySpan<byte> text = Values.AsText(t, tt);

        // Black-box: LIKE and GLOB read both operands as C strings, so a NUL ends the text or the pattern.
        int nulT = text.IndexOf((byte)0);
        if (nulT >= 0)
        {
            text = text[..nulT];
        }

        int nulP = pat.IndexOf((byte)0);
        if (nulP >= 0)
        {
            pat = pat[..nulP];
        }
        bool m = ins.Op == Op.Like ? Scalar.Like(pat, text, esc) : Scalar.Glob(pat, text);
        Values.SetInt(ref d, m ? 1 : 0);
    }
}
