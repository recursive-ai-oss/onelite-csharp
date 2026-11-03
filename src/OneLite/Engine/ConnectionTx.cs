using RecursiveAI.OneLite.Planning;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite;

/// <summary>A SAVEPOINT on the connection's stack. <c>Level</c> is the pager savepoint behind it, or 0 while no write transaction has begun.</summary>
internal sealed class SavepointEntry
{
    internal string Name = "";
    internal int Level;

    /// <summary>The matching savepoint level on the temp database's pager (0 if it has none).</summary>
    internal int TempLevel;

    /// <summary>The matching savepoint levels on the attached databases' pagers, by slot (0 where a database has none); null until one is needed.</summary>
    internal int[]? AttachedLevels;

    /// <summary>The deferred foreign key counter when the savepoint was set; ROLLBACK TO restores it.</summary>
    internal long FkDeferred;
}

// Transaction state of the connection (lang_transaction, lang_savepoint). A transaction ends when COMMIT, ROLLBACK or the outermost RELEASE
// runs; while the stack of savepoints is empty and no BEGIN is open, each statement is its own transaction ("autocommit").
public sealed partial class Connection
{
    /// <summary>True between BEGIN and COMMIT/ROLLBACK.</summary>
    internal bool ExplicitTx;

    /// <summary>True while the connection keeps a read transaction open across statements (it counts as one reader).</summary>
    internal bool TxHeld;

    internal readonly List<SavepointEntry> Savepoints = [];

    internal long LastInsertRowid;
    internal long Changes;
    internal long TotalChanges;

    /// <summary>Schema statements ran in the open transaction; a rollback must reload the schema.</summary>
    internal bool SchemaDirty;

    internal bool ForeignKeysEnabled { get; set; }

    /// <summary>The settings that PRAGMA statements keep on this connection.</summary>
    internal PragmaSettings PragmaSettings { get; } = new();

    /// <summary>
    /// Foreign key violation counters (foreignkeys): rows whose parent is missing, for immediate constraints within the running statement and
    /// for deferred constraints within the transaction. A statement fails if the first is not zero at its end; COMMIT fails if the second is not.
    /// </summary>
    internal long FkImm, FkDeferred;

    /// <summary>Nesting depth of foreign key actions, bounded like trigger recursion.</summary>
    internal int FkDepth;

    /// <summary>Set by RAISE(IGNORE) in a trigger program, consumed by the instruction that ran the trigger.</summary>
    internal bool TrigIgnore;

    /// <summary><c>PRAGMA recursive_triggers</c> (off by default, as in the pinned shell) and the triggers whose programs are running.</summary>
    internal bool RecursiveTriggers { get; set; }

    internal readonly List<Planning.TrigSpec> TrigActive = [];

    internal BusyPolicy? Busy;

    /// <summary>The main-database tables whose indexes a query of this connection has been planned to seek through; PRAGMA optimize looks only at these unless asked to check every table.</summary>
    internal readonly HashSet<string> StatsUsed = new(AsciiComparer.Instance);

    /// <summary>The busy timeout in milliseconds (<c>PRAGMA busy_timeout</c>); 0 means the busy callback alone decides.</summary>
    internal int BusyTimeoutMs
    {
        get => Busy?.TimeoutMs ?? 0;
        set
        {
            if (Busy is not null)
            {
                Busy.TimeoutMs = value;
            }
        }
    }

    /// <summary>True when no transaction is open and the next statement runs alone (<c>sqlite3_get_autocommit</c>).</summary>
    public bool AutoCommit => !ExplicitTx && Savepoints.Count == 0;

    /// <summary>The number of rows the most recently completed INSERT, UPDATE or DELETE changed (<c>changes()</c>).</summary>
    public long ChangesCount => Changes;

    /// <summary>The rowid of the most recent successful INSERT (<c>last_insert_rowid()</c>).</summary>
    public long LastInsertedRowid => LastInsertRowid;

    /// <summary>The number of rows changed since the connection opened (<c>total_changes()</c>).</summary>
    public long TotalChangesCount => TotalChanges;

    private VmProgram CompileWrite(Sql.Statement stmt, string sql, PlanMode mode)
    {
        EnsureCatalogSync();
        Catalog cat = _catalog ?? new Catalog();
        TextEncoding enc = _ctx?.Encoding ?? TextEncoding.Utf8;
        var compiler = new Compiler(cat, sql, enc) { ForeignKeysEnabled = ForeignKeysEnabled, NoCover = _noCover };
        if (mode != PlanMode.Off && stmt is Sql.InsertStatement or Sql.UpdateStatement or Sql.DeleteStatement)
        {
            compiler.StartPlan(stmt switch { Sql.InsertStatement => "INSERT", Sql.UpdateStatement => "UPDATE", _ => "DELETE" }, mode == PlanMode.Count);
        }

        CompileTaint = false;
        CompileStatsTables.Clear();
        VmProgram p = stmt switch
        {
            Sql.InsertStatement i => compiler.CompileInsert(i),
            Sql.UpdateStatement u => compiler.CompileUpdate(u),
            Sql.DeleteStatement d => compiler.CompileDelete(d),
            Sql.CreateStatement c => compiler.CompileCreate(c),
            Sql.DropStatement dr => compiler.CompileDrop(dr),
            Sql.AlterTableStatement al => compiler.CompileAlter(al),
            Sql.ReindexStatement ri => compiler.CompileReindex(ri),
            Sql.AnalyzeStatement an => compiler.CompileAnalyze(an),
            Sql.VacuumStatement vs => compiler.CompileVacuum(vs),
            _ => throw new OneLiteException(ResultCode.Error, "unsupported statement"),
        };
        p.Plan = compiler.FinishPlan();
        p.PlanCounterCount = p.Plan?.CounterCount ?? 0;
        p.SchemaCookie = cat.Cookie;
        p.Encoding = enc;
        if (mode == PlanMode.Off && stmt is Sql.InsertStatement or Sql.UpdateStatement or Sql.DeleteStatement)
        {
            MarkReusable(p, cat);
        }

        return p;
    }

    /// <summary>Starts the pager's write transaction (the RESERVED lock or the WAL write lock) and opens the pager savepoints for the stack entries that wait for it.</summary>
    internal PagerStatus BeginWriteStep(bool exclusive = false, bool noWait = false)
    {
        if (ConcDoomed || _pager.ConcurrentAbandoned)
        {
            throw new OneLiteException(ResultCode.BusySnapshot, Pager.Msg(ResultCode.BusySnapshot), "this transaction's commit was refused by a conflict; roll it back");
        }

        PagerStatus s = ConcTx is not null ? _pager.BeginWriteConcurrent() : _pager.BeginWrite(exclusive, noWait);
        if (s != PagerStatus.Ok)
        {
            return s;
        }

        foreach (SavepointEntry e in Savepoints)
        {
            if (e.Level == 0)
            {
                e.Level = _pager.OpenSavepoint();
            }
        }

        return PagerStatus.Ok;
    }

    /// <summary>Gives up the read transaction a BEGIN kept open. Re-enter after Pending.</summary>
    internal bool IsWal => _pager.IsWal;

    /// <summary>True when no statement or open transaction of this connection holds a read transaction.</summary>
    internal bool HoldsNoRead => _readers == 0 && !TxHeld;

    /// <summary>True when a read transaction is held by an open transaction or by another running statement, not only by the caller's own statement.</summary>
    internal bool ReadsBeyondThisStatement => ReadsHeldBesidesThisStatement || BlockedStatements > 0;

    /// <summary>Like <see cref="ReadsBeyondThisStatement"/> without the statements refused with SQLITE_BUSY: those hold no read, so a WAL checkpoint is not blocked by them.</summary>
    internal bool ReadsHeldBesidesThisStatement => _readers > 1 || TxHeld;

    /// <summary>
    /// Write statements that returned SQLITE_BUSY while asking for the write lock. In the reference such a statement stays active until it is reset or
    /// stepped again (c3ref/step): it holds no lock, but a checkpoint, a change of journal mode or a COMMIT still counts it as running.
    /// </summary>
    internal int BlockedStatements;

    /// <summary>True when the read transaction was kept open for the sake of a statement refused with SQLITE_BUSY.</summary>
    internal bool BlockedHold;

    /// <summary>
    /// Called when the last running statement is about to end its read transaction. If a refused statement is still active the transaction stays
    /// open (the reference ends it only when no statement is active); returns true then.
    /// </summary>
    internal bool HoldReadForBlocked(Vm.Machine m)
    {
        if (_readers != 1 || TxHeld)
        {
            return false;
        }

        if (!BlockedHold && (m.BlockedByBusy || m.BlockedAtRead || BlockedStatements + BlockedReadStatements == 0))
        {
            return false;
        }

        BlockedHold = true;
        return true;
    }

    /// <summary>Ends the read transaction kept for refused statements once the last of them is reset.</summary>
    internal void ReleaseBlockedHold()
    {
        if (BlockedHold && BlockedStatements + BlockedReadStatements == 0 && _readers == 0 && !TxHeld)
        {
            BlockedHold = false;
            AbortRead();
        }
    }

    internal bool BlockedCountsForCheckpoint(int ownReads) => BlockedHold;

    /// <summary>Statements refused with SQLITE_BUSY asking for the read lock; they block COMMIT, RELEASE and SAVEPOINT until reset.</summary>
    internal int BlockedReadStatements;

    /// <summary>Statements that have begun writing and not finished (a RETURNING statement not read to its end, or one in the middle of a step).</summary>
    internal int ActiveWriters;

    internal PagerStatus EndTxHoldStep()
    {
        if (!TxHeld)
        {
            return PagerStatus.Ok;
        }

        if (_readers > 1)
        {
            _readers--;
            TxHeld = false;
            return PagerStatus.Ok;
        }

        PagerStatus s = _pager.EndRead();
        if (s != PagerStatus.Pending)
        {
            _readers--;
            TxHeld = false;
        }

        return s;
    }

    /// <summary>Keeps the read transaction open after the statement that started it ends, because a transaction is open.</summary>
    internal void HoldForTransaction()
    {
        if (!AutoCommit && !TxHeld)
        {
            TxHeld = true;
            _readers++;
        }
    }

    /// <summary>Forgets the open transaction (after COMMIT or ROLLBACK).</summary>
    internal void EndTransactionState()
    {
        EndConcurrent();
        ExplicitTx = false;
        FkDeferred = 0;
        Savepoints.Clear();
        SchemaDirty = false;
    }

    internal int FindSavepoint(string name)
    {
        for (int i = Savepoints.Count - 1; i >= 0; i--)
        {
            if (AsciiComparer.Instance.Equals(Savepoints[i].Name, name))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Cursors of statements other than <paramref name="except"/> point into pages a rollback is about to rewrite; let go of them (pending reads survive when <paramref name="keepReads"/>).</summary>
    internal void ReleaseCursorsOf(Statement? except, bool keepReads = false)
    {
        foreach (Statement s in _statements)
        {
            if (!ReferenceEquals(s, except))
            {
                s.AbortForRollback(keepReads);
            }
        }
    }
}

/// <summary>Decides whether to retry a lock that another connection holds: a user callback, or a timeout with the usual growing delays (c3ref busy_timeout).</summary>
internal sealed class BusyPolicy
{
    private static readonly int[] s_delays = [1, 2, 5, 10, 15, 20, 25, 25, 25, 50, 50, 100];
    private readonly Func<int, int>? _user;
    private int _waited;

    internal BusyPolicy(Func<int, int>? user, int timeoutMs)
    {
        _user = user;
        TimeoutMs = timeoutMs;
    }

    internal int TimeoutMs { get; set; }

    private int _spun;

    /// <summary>Microseconds of yielding retries a concurrent commit may spend on a busy write lock: up to 1 ms, never more than the timeout, none if this policy would give up at once.</summary>
    internal int SpinBudgetUs()
    {
        if (TimeoutMs > 0)
        {
            return Math.Min(1000, TimeoutMs * 1000);
        }

        return _user is null ? 0 : 1000;
    }

    /// <summary>Counts time spent spinning against the timeout of the wait that follows.</summary>
    internal void Spun(int ms) => _spun = ms;

    /// <summary>The number of milliseconds to wait before retry number <paramref name="retries"/>, or a negative number to give up.</summary>
    internal int Next(int retries)
    {
        if (retries == 0)
        {
            _waited = _spun;
            _spun = 0;
        }

        if (TimeoutMs > 0)
        {
            if (_waited >= TimeoutMs)
            {
                return -1;
            }

            int delay = Math.Min(retries < s_delays.Length ? s_delays[retries] : 100, TimeoutMs - _waited);
            _waited += delay;
            return delay;
        }

        return _user?.Invoke(retries) ?? -1;
    }
}
