using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.VirtualTables;

/// <summary>
/// The operator of a constraint offered to <see cref="IVirtualTable.BestIndex"/>. The numbers are the documented values of the
/// <c>SQLITE_INDEX_CONSTRAINT_*</c> codes (vtab, c3ref/c_index_constraint_eq).
/// </summary>
public enum ConstraintOp : byte
{
    /// <summary><c>column = expr</c>.</summary>
    Eq = 2,

    /// <summary><c>column &gt; expr</c>.</summary>
    Gt = 4,

    /// <summary><c>column &lt;= expr</c>.</summary>
    Le = 8,

    /// <summary><c>column &lt; expr</c>.</summary>
    Lt = 16,

    /// <summary><c>column &gt;= expr</c>.</summary>
    Ge = 32,

    /// <summary><c>column MATCH expr</c>.</summary>
    Match = 64,

    /// <summary><c>column LIKE expr</c>.</summary>
    Like = 65,

    /// <summary><c>column GLOB expr</c>.</summary>
    Glob = 66,

    /// <summary><c>column REGEXP expr</c>.</summary>
    Regexp = 67,

    /// <summary>The query's <c>LIMIT</c> (SQLite's code 73): offered to a table with no other table in the query and no term left over for the engine.</summary>
    Limit = 73,

    /// <summary>
    /// <c>column IN (a, b, ...)</c>, offered once per list element (a OneLite extension, not an SQLite code): a table that takes one element must take them all, and
    /// treats the elements of the same column as alternatives.
    /// </summary>
    In = 150,

    /// <summary><c>column != expr</c> or <c>column &lt;&gt; expr</c>.</summary>
    Ne = 68,

    /// <summary><c>column IS NOT expr</c>.</summary>
    IsNot = 69,

    /// <summary><c>column IS NOT NULL</c>.</summary>
    IsNotNull = 70,

    /// <summary><c>column IS NULL</c>.</summary>
    IsNull = 71,

    /// <summary><c>column IS expr</c>.</summary>
    Is = 72,
}

/// <summary>A WHERE or ON term of the form <c>column OP expr</c> that mentions the table, as <see cref="IVirtualTable.BestIndex"/> sees it.</summary>
public readonly struct IndexConstraint
{
    internal IndexConstraint(int column, ConstraintOp op, bool usable)
    {
        Column = column;
        Op = op;
        Usable = usable;
    }

    /// <summary>The column constrained: its position in the declared table, or -1 for <c>rowid</c>.</summary>
    public int Column { get; }

    /// <summary>The operator, with the column on the left (a term written <c>5 &lt; x</c> is offered as <c>x &gt; 5</c>).</summary>
    public ConstraintOp Op { get; }

    /// <summary>True when the other side only reads constants and tables that come before this one in the loop nest; only a usable constraint can be given an argument.</summary>
    public bool Usable { get; }
}

/// <summary>An ORDER BY term the query would like the table to deliver in order.</summary>
public readonly struct IndexOrderBy
{
    internal IndexOrderBy(int column, bool descending)
    {
        Column = column;
        Descending = descending;
    }

    /// <summary>The column to order by, or -1 for <c>rowid</c>.</summary>
    public int Column { get; }

    /// <summary>True for <c>DESC</c>.</summary>
    public bool Descending { get; }
}

/// <summary>What <see cref="IVirtualTable.BestIndex"/> decides about one constraint.</summary>
public struct IndexConstraintUsage
{
    /// <summary>
    /// 0 (the default) to leave the constraint out of the scan; otherwise the 1-based position of the constraint's right-hand value in the
    /// <c>args</c> that <see cref="IVirtualTableCursor.Filter"/> receives. Positions must start at 1 and have no gaps.
    /// </summary>
    public int ArgvIndex { get; set; }

    /// <summary>True when the table guarantees every row it returns satisfies the constraint, so the engine does not test it again. Only meaningful with an <see cref="ArgvIndex"/>.</summary>
    public bool Omit { get; set; }
}

/// <summary>
/// The conversation between the planner and a table about one scan: the constraints and ordering the query offers, and the plan the table picks.
/// <see cref="IVirtualTable.BestIndex"/> may be called several times for one query, once per candidate loop position.
/// </summary>
public sealed class BestIndexInfo
{
    private readonly IndexConstraint[] _constraints;
    private readonly IndexOrderBy[] _orderBy;

    internal BestIndexInfo(IndexConstraint[] constraints, IndexOrderBy[] orderBy)
    {
        _constraints = constraints;
        _orderBy = orderBy;
        Usage = new IndexConstraintUsage[constraints.Length];
    }

    /// <summary>The constraints offered, in the order of the WHERE clause.</summary>
    public ReadOnlySpan<IndexConstraint> Constraints => _constraints;

    /// <summary>The ORDER BY terms of the query, empty when the table's order does not matter or the query orders by something the table does not hold.</summary>
    public ReadOnlySpan<IndexOrderBy> OrderBy => _orderBy;

    /// <summary>One entry per constraint, all zero at the start; the table fills it in.</summary>
    public Span<IndexConstraintUsage> ConstraintUsage => Usage;

    internal IndexConstraintUsage[] Usage { get; }

    /// <summary>A number the table chooses to identify the plan; passed back to <see cref="IVirtualTableCursor.Filter"/>. Default 0.</summary>
    public int IndexNumber { get; set; }

    /// <summary>A string the table chooses to describe the plan; passed back to <see cref="IVirtualTableCursor.Filter"/>. Default null.</summary>
    public string? IndexString { get; set; }

    /// <summary>True when the rows come out in the order of <see cref="OrderBy"/>, so no sort is needed. Default false.</summary>
    public bool OrderByConsumed { get; set; }

    /// <summary>The cost of the scan in arbitrary units; a plan with a lower cost wins. Default 5e98, a very expensive plan.</summary>
    public double EstimatedCost { get; set; } = 5e98;

    /// <summary>The number of rows the scan is expected to return. Default 25.</summary>
    public long EstimatedRows { get; set; } = 25;

    /// <summary>True when the scan returns at most one row (it uses an equality on a unique key). Default false.</summary>
    public bool ScanIsUnique { get; set; }

    /// <summary>Bit i is set when column i (i &lt; 63) is read by the query, bit 63 for any later one; set by the engine.</summary>
    public ulong ColumnsUsed { get; internal set; } = ulong.MaxValue;
}

/// <summary>Where a cursor writes the value of a column. At most one of the setters is used per call; a call that sets nothing leaves NULL.</summary>
public readonly ref struct ColumnResult
{
    private readonly ref Mem _dst;
    private readonly ref byte[]? _own;

    internal ColumnResult(ref Mem dst, ref byte[]? own)
    {
        _dst = ref dst;
        _own = ref own;
        _dst.Kind = StorageClass.Null;
    }

    /// <summary>Sets NULL.</summary>
    public void SetNull() => _dst.Kind = StorageClass.Null;

    /// <summary>Sets an INTEGER.</summary>
    public void SetInt64(long value)
    {
        _dst.Kind = StorageClass.Integral;
        _dst.IntValue = value;
    }

    /// <summary>Sets a REAL.</summary>
    public void SetDouble(double value)
    {
        _dst.Kind = StorageClass.Real;
        _dst.RealValue = value;
    }

    /// <summary>Sets TEXT from UTF-8 bytes (copied, and converted when the database is UTF-16).</summary>
    public void SetTextUtf8(ReadOnlySpan<byte> utf8)
    {
        if (Vm.Values.BlobEncoding == Storage.TextEncoding.Utf8)
        {
            Copy(StorageClass.Text, utf8);
        }
        else
        {
            SetText(System.Text.Encoding.UTF8.GetString(utf8));
        }
    }

    /// <summary>Sets TEXT (converted to the database encoding).</summary>
    public void SetText(string value)
    {
        System.Text.Encoding e = Vm.Values.BlobEncoding switch
        {
            Storage.TextEncoding.Utf8 => System.Text.Encoding.UTF8,
            Storage.TextEncoding.Utf16Le => System.Text.Encoding.Unicode,
            _ => System.Text.Encoding.BigEndianUnicode,
        };
        Copy(StorageClass.Text, e.GetBytes(value));
    }

    /// <summary>Sets a BLOB (copied).</summary>
    public void SetBlob(ReadOnlySpan<byte> bytes) => Copy(StorageClass.Blob, bytes);

    private void Copy(StorageClass kind, ReadOnlySpan<byte> src) => Vm.Values.SetBytes(ref _dst, ref _own, kind, src);
}

/// <summary>What <see cref="IVirtualTableModule.Create"/> and <see cref="IVirtualTableModule.Connect"/> are told about the table being made or opened.</summary>
public sealed class VirtualTableContext
{
    private readonly Action<string> _declare;

    internal VirtualTableContext(Connection connection, string schema, string name, IReadOnlyList<string> arguments, Action<string> declare)
    {
        Connection = connection;
        Schema = schema;
        Name = name;
        Arguments = arguments;
        _declare = declare;
    }

    /// <summary>The connection the table belongs to. A module may run SQL on it (to create or read its own tables) from <see cref="IVirtualTableModule.Create"/> and <see cref="IVirtualTableModule.Connect"/>.</summary>
    public Connection Connection { get; }

    /// <summary>The schema name the table lives in (<c>main</c>, <c>temp</c> or an attached name).</summary>
    public string Schema { get; }

    /// <summary>The table's name.</summary>
    public string Name { get; }

    /// <summary>The module arguments exactly as written between the parentheses of <c>CREATE VIRTUAL TABLE</c>, trimmed and split at top-level commas; empty for an eponymous table.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>
    /// Tells the engine the columns the table presents, as a <c>CREATE TABLE</c> statement (only the column names, <c>HIDDEN</c> markers,
    /// declared types and collations count). Must be called exactly once, from <c>Create</c> or <c>Connect</c>; a column marked <c>HIDDEN</c>
    /// is left out of <c>*</c> and can be given a value as a table-function argument or an equality.
    /// </summary>
    /// <exception cref="OneLiteException">The statement does not parse (SQLITE_ERROR).</exception>
    public void DeclareTable(string createTableSql) => _declare(createTableSql);
}

/// <summary>
/// A virtual-table module: registered by name with <see cref="Connection.RegisterModule"/>, it makes the tables of
/// <c>CREATE VIRTUAL TABLE t USING name(args)</c>. Methods run synchronously on the thread that drives the statement; they must not
/// block on the same connection's asynchronous work (a module that needs the connection uses <see cref="VirtualTableContext.Connection"/> with the sync driver).
/// </summary>
public interface IVirtualTableModule
{
    /// <summary>Called once by <c>CREATE VIRTUAL TABLE</c>: set up persistent state (for instance shadow tables) and call <see cref="VirtualTableContext.DeclareTable"/>.</summary>
    IVirtualTable Create(VirtualTableContext context);

    /// <summary>Called each time a connection opens an existing table (the first use after the schema is read); must call <see cref="VirtualTableContext.DeclareTable"/> with the same columns as <see cref="Create"/>.</summary>
    IVirtualTable Connect(VirtualTableContext context);

    /// <summary>True when the module's table also exists without <c>CREATE VIRTUAL TABLE</c>, under the module's own name (for example a table-valued function). Default false.</summary>
    bool IsEponymous => false;
}

/// <summary>One virtual table of one connection.</summary>
public interface IVirtualTable : IDisposable
{
    /// <summary>
    /// Chooses a plan for a scan. Offer the planner what the table can do with the constraints and ordering in <paramref name="info"/>; the
    /// engine calls this for candidate plans and keeps the cheapest. Throwing an <see cref="OneLiteException"/> with code
    /// <see cref="ResultCode.Constraint"/> says no plan satisfies the required (hidden-column) constraints.
    /// </summary>
    void BestIndex(BestIndexInfo info);

    /// <summary>Opens a cursor; one query may hold several open cursors on the same table.</summary>
    IVirtualTableCursor Open();

    /// <summary>Called when the table is dropped with <c>DROP TABLE</c>, before <see cref="IDisposable.Dispose"/>: delete persistent state. Default: nothing.</summary>
    void Destroy()
    {
    }
}

/// <summary>A position in a scan of a virtual table.</summary>
public interface IVirtualTableCursor : IDisposable
{
    /// <summary>
    /// Starts (or restarts) a scan with the plan <see cref="BestIndexInfo.IndexNumber"/>/<see cref="BestIndexInfo.IndexString"/> chose.
    /// <paramref name="args"/> holds the right-hand values of the constraints that were given an argv index, in that order.
    /// </summary>
    void Filter(int indexNumber, string? indexString, ReadOnlySpan<Mem> args);

    /// <summary>Moves to the next row.</summary>
    void MoveNext();

    /// <summary>True when the scan has no current row; checked right after <see cref="Filter"/> and after every <see cref="MoveNext"/>.</summary>
    bool Eof { get; }

    /// <summary>Writes column <paramref name="column"/> of the current row. A hidden column or a column the table does not hold is read as NULL.</summary>
    void Column(int column, ColumnResult result);

    /// <summary>The rowid of the current row.</summary>
    long RowId { get; }
}

/// <summary>A table that accepts <c>INSERT</c>, <c>UPDATE</c> and <c>DELETE</c>. A table without it fails them with "table T may not be modified".</summary>
public interface IWritableVirtualTable : IVirtualTable
{
    /// <summary>
    /// Applies one change. <paramref name="values"/>[0] is the rowid of the row to change or delete (NULL for an INSERT), and for an
    /// insert or update <paramref name="values"/>[1] is the new rowid (NULL: the table picks one) and the rest are the new column values in declared
    /// order (hidden columns included). A delete passes only [0]. Returns the rowid of the inserted row (ignored otherwise).
    /// Throw an <see cref="OneLiteException"/> to fail the statement.
    /// </summary>
    long Update(ReadOnlySpan<Mem> values);
}

/// <summary>A table that takes part in transactions. Called once per transaction that writes to the table, in this order: Begin, then at commit Sync and Commit, or Rollback.</summary>
public interface ITransactionalVirtualTable : IVirtualTable
{
    /// <summary>A transaction starts writing to the table.</summary>
    void Begin();

    /// <summary>First phase of commit; an exception here rolls the transaction back.</summary>
    void Sync();

    /// <summary>Second phase of commit; cannot fail the transaction.</summary>
    void Commit();

    /// <summary>The transaction rolls back.</summary>
    void Rollback();
}

/// <summary>A transactional table that also follows savepoints. <c>level</c> is the savepoint's depth, as in the documented xSavepoint/xRelease/xRollbackTo.</summary>
public interface ISavepointVirtualTable : ITransactionalVirtualTable
{
    /// <summary>A savepoint opens.</summary>
    void Savepoint(int level);

    /// <summary>A savepoint is released (kept).</summary>
    void Release(int level);

    /// <summary>The state goes back to what it was when savepoint <paramref name="level"/> opened.</summary>
    void RollbackTo(int level);
}

/// <summary>A table that is told about <c>ALTER TABLE ... RENAME TO</c>.</summary>
public interface IRenamableVirtualTable : IVirtualTable
{
    /// <summary>The table is about to be renamed; rename the shadow state to match. An exception fails the rename.</summary>
    void Rename(string newName);
}
