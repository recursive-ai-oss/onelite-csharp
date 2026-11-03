using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Planning;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Vm;

/// <summary>
/// The instruction set. Registers are numbered from 1. Jump targets always sit in P2. The cursor opcodes follow the suspending-opcode
/// classes of the step contract: <c>OpenRead, Rewind, Last, Next, Prev, Seek*, Idx*, Column, Rowid, IdxRowid</c> may return "I/O pending"
/// without consuming the instruction.
/// </summary>
internal enum Op : byte
{
    // control
    Init,
    Goto,
    Halt,
    Transaction,

    // cursors
    OpenRead,
    Rewind,
    Next,

    /// <summary>Position on the last entry (jump to P2 when the tree is empty), and step backwards (jump to P2 while valid).</summary>
    Last,
    Prev,
    SeekRowid,
    SeekGE,
    SeekGT,
    IdxGT,
    IdxGE,
    IdxRowid,
    Column,
    Rowid,

    // constants and registers
    Integer,
    Real,
    String8,
    Blob,
    Null,
    Variable,
    Copy,

    // arithmetic
    Add,
    Subtract,
    Multiply,
    Divide,
    Remainder,
    Concat,
    BitAnd,
    BitOr,
    ShiftLeft,
    ShiftRight,
    Negate,
    BitNot,
    Not,

    // comparison and logic (value forms)
    Eq,
    Ne,
    Lt,
    Le,
    Gt,
    Ge,
    Is,
    IsNot,
    And,
    Or,
    IsNullV,
    NotNullV,
    Like,
    Glob,
    Function,
    Cast,
    Affinity,
    CompareAffinity,

    // jumps
    If,
    IfNot,
    IfNull,
    NotNull,
    MustBeInt,
    OffsetFix,
    IfPos,
    DecrJumpZero,
    AddImm,

    // output
    ResultRow,
    SorterOpen,
    SorterInsert,
    SorterSort,
    SorterData,
    SorterNext,

    // subroutines, flags
    Gosub,
    Return,
    Once,
    Noop,
    NullRow,
    RowKey,

    // ephemeral tables and sets
    EphOpen,
    EphInsert,
    ERewind,
    ENext,
    EColumn,
    ERowid,

    // table-valued functions (built-in modules)
    VFilter,
    VNext,
    VColumn,
    VRowid,
    ECopyRow,
    SetOpen,
    SetAdd,

    /// <summary>Forget that the set saw a NULL (P1 = set): the reference misses it when it reads the set out of a descending index.</summary>
    SetIgnoreNull,
    SetFound,
    SetToEph,
    InSet,

    // aggregates
    AggReset,
    AggStep,
    AggFinal,

    // window functions: open a stage, push a sorted row, pull a finished row (P2 jumps when none is ready), end the input
    WinOpen,
    WinPush,
    WinOut,
    WinFinish,

    // transactions and errors (write path; implemented in Machine.Write.cs)
    TxControl,
    Raise,
    ResetChanges,

    // writing
    OpenWrite,
    NewRowid,
    RowidFix,
    Insert,
    IdxInsert,
    IdxDelete,
    Delete,
    IdxProbe,
    RowSetAdd,
    RowSetRead,
    SeqLoad,
    SeqStore,
    StrictCheck,
    CreateBtree,
    Destroy,
    BumpCookie,
    RetAdd,
    RetRow,
    SubSelect,
    SubNext,
    PragmaOp,
    PragmaVar,
    PragmaIntegrity,
    Vacuum,

    /// <summary>PRAGMA incremental_vacuum: P1 = database slot, P4 = how many pages to free (0 = all). The number freed goes to register P2.</summary>
    IncrVacuum,
    RecTrig,
    AttachDb,
    DetachDb,
    ClearTree,
    FkCheck,
    FkRun,
    FkAdd,

    /// <summary>Remembers the connection's last_insert_rowid in register P1, for an FkCheck (P3 = P1 + 1) that must give it back.</summary>
    SaveRowid,
    MaxReg,
    SchemaDelete,
    TempView,
    TrigRun,
    TrigIgnore,

    /// <summary>Counts for a query plan: P1 = counter, P2 = 0 for a loop start, 1 for a row.</summary>
    PlanCount,

    /// <summary>Virtual tables: start a scan (P1 cursor, P2 jump if empty, P3 first argument register, P4 plan, P5 argument count).</summary>
    VtFilter,

    /// <summary>Virtual tables: INSERT, UPDATE or DELETE one row (P1 first argument register, P2 argument count, P3 register for the new rowid, P4 table, P5 flags: 1 counts a change, 2 sets last_insert_rowid).</summary>
    VtUpdate,

    /// <summary>Virtual tables: run a user module's Create (P4 = spec).</summary>
    VtCreate,

    /// <summary>Virtual tables: run a table's Destroy before DROP TABLE removes it (P4 = table).</summary>
    VtDestroy,
}

/// <summary>One instruction. P4 carries a constant object (text bytes, a cursor description, a function).</summary>
internal struct Instr
{
    internal Op Op;
    internal int P1;
    internal int P2;
    internal int P3;
    internal object? P4;
    internal int P5;

    public override readonly string ToString() => $"{Op} {P1} {P2} {P3} {P5}" + (P4 is null ? string.Empty : " " + P4);
}

/// <summary>What a cursor reads: a table tree (rowid or WITHOUT ROWID) or a secondary index tree.</summary>
internal sealed class CursorSpec
{
    internal TableInfo Table = null!;

    /// <summary>The index read through this cursor; for a WITHOUT ROWID table, its primary key; null for a rowid table.</summary>
    internal IndexInfo? Index;

    /// <summary>True when the tree is an index tree (secondary index or WITHOUT ROWID table).</summary>
    internal bool IsIndexTree;

    internal uint Root;

    /// <summary>An ephemeral table (a materialized subquery, CTE or compound result) instead of a B-tree.</summary>
    internal bool Ephemeral;

    /// <summary>A table-valued function (json_each, generate_series, ...): the cursor is a module cursor made on first use.</summary>
    internal ModuleDef? Module;
}

/// <summary>A sort key term of ORDER BY.</summary>
internal readonly record struct SortTerm(bool Descending, bool NullsFirst, CollKind Collation);

/// <summary>A compiled statement: code, register and cursor counts and the result description.</summary>
internal sealed class VmProgram
{
    /// <summary>
    /// The schema this program was compiled against. A program is kept for the next prepare of the same text only while the connection still holds
    /// this very schema object, at the same cookie, with the same foreign-key setting (see Connection.TakeProgram).
    /// </summary>
    internal object? CompiledAgainst;

    /// <summary>The foreign_keys setting in force when the program was compiled.</summary>
    internal bool ForeignKeysAtCompile;

    /// <summary>True for a SELECT, INSERT, UPDATE or DELETE that touches no virtual table and records nothing in the connection while compiling.</summary>
    internal bool Reusable;

    /// <summary>The tables whose statistics the planner weighed (PRAGMA optimize counts them as queried), replayed when a kept program is used again.</summary>
    internal string[]? StatsTables;

    internal Instr[] Code = [];
    internal int RegisterCount;
    internal CursorSpec[] Cursors = [];
    internal string[] ColumnNames = [];

    /// <summary>Per result column, the table column it is a plain reference to; null for an expression (sqlite3_column_decltype, _table_name, _origin_name, _database_name).</summary>
    internal ColumnOrigin?[] ColumnOrigin = [];

    /// <summary>The statement writes the temp database, so the temp database must exist when it starts.</summary>
    internal bool ForceTemp;

    /// <summary>Attached databases (bit per slot) the statement writes without an OpenWrite of its own (VACUUM).</summary>
    internal int ExtraWriteMask;

    /// <summary>Attached databases (bit per slot) the statement reads without a cursor of its own (a pragma).</summary>
    internal int ExtraReadMask;

    private int _readMask = -1, _writeMask;

    private void ComputeMasks()
    {
        int r = 0, w = 0;
        foreach (CursorSpec c in Cursors)
        {
            if (c.Table is { Db: >= 2 } && !c.Ephemeral && c.Module is null)
            {
                r |= 1 << c.Table.Db;
            }
        }

        foreach (Instr i in Code)
        {
            if (i.Op == Op.OpenWrite && Cursors[i.P1].Table is { Db: >= 2 } t)
            {
                w |= 1 << t.Db;
            }
        }

        w |= ExtraWriteMask;
        _writeMask = w;
        _readMask = r | w | ExtraReadMask;
    }

    /// <summary>The attached databases (bit per slot) the statement reads or writes, so their transactions start with the statement.</summary>
    internal int AttachedReadMask
    {
        get
        {
            if (_readMask < 0)
            {
                ComputeMasks();
            }

            return _readMask;
        }
    }

    internal int AttachedWriteMask
    {
        get
        {
            if (_readMask < 0)
            {
                ComputeMasks();
            }

            return _writeMask;
        }
    }

    internal bool UsesTemp => ForceTemp || Array.Exists(Cursors, c => c.Table is { Db: 1 });
    internal int ResultBase;
    internal int ResultCount;

    /// <summary>Number of bind parameters (the highest index) and their names (null for ?).</summary>
    internal string?[] ParameterNames = [];

    /// <summary>The schema cookie the program was compiled against.</summary>
    internal uint SchemaCookie;

    /// <summary>Counts of the program's once-flags, sorters, sets and aggregate states.</summary>
    internal int OnceCount;

    /// <summary>The plan recorded while compiling (only when asked for) and the number of row counters the program carries.</summary>
    internal QueryPlan? Plan;
    internal int PlanCounterCount;
    internal int SorterCount;
    internal int SetCount;
    internal AggSpec[] Aggregates = [];

    /// <summary>The window stages of the statement (see <see cref="WindowRun"/>).</summary>
    internal WinStageSpec[] WindowStages = [];

    /// <summary>The text encoding of the database the program was compiled for.</summary>
    internal TextEncoding Encoding;
}

/// <summary>Where a result column comes from: database, table, column and the column's declared type.</summary>
internal sealed record ColumnOrigin(string Database, string Table, string Column, string DeclaredType);
