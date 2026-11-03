namespace RecursiveAI.OneLite;

/// <summary>
/// Primary and extended result codes (sqlite.org <c>rescode</c>). The numeric values are the ones the C API reports, so code that
/// switches on them behaves the same against OneLite and the reference library.
/// </summary>
public static class ResultCode
{
    /// <summary>SQLITE_OK.</summary>
    public const int Ok = 0;

    /// <summary>SQLITE_ERROR.</summary>
    public const int Error = 1;

    /// <summary>SQLITE_INTERNAL.</summary>
    public const int Internal = 2;

    /// <summary>SQLITE_PERM.</summary>
    public const int Perm = 3;

    /// <summary>SQLITE_ABORT.</summary>
    public const int Abort = 4;

    /// <summary>SQLITE_BUSY: another connection holds a conflicting lock.</summary>
    public const int Busy = 5;

    /// <summary>SQLITE_LOCKED.</summary>
    public const int Locked = 6;

    /// <summary>SQLITE_NOMEM.</summary>
    public const int NoMem = 7;

    /// <summary>SQLITE_READONLY.</summary>
    public const int ReadOnly = 8;

    /// <summary>SQLITE_INTERRUPT.</summary>
    public const int Interrupt = 9;

    /// <summary>SQLITE_IOERR.</summary>
    public const int IoErr = 10;

    /// <summary>SQLITE_CORRUPT: the database file is malformed.</summary>
    public const int Corrupt = 11;

    /// <summary>SQLITE_NOTFOUND.</summary>
    public const int NotFound = 12;

    /// <summary>SQLITE_FULL.</summary>
    public const int Full = 13;

    /// <summary>SQLITE_CANTOPEN.</summary>
    public const int CantOpen = 14;

    /// <summary>SQLITE_PROTOCOL.</summary>
    public const int Protocol = 15;

    /// <summary>SQLITE_SCHEMA.</summary>
    public const int Schema = 17;

    /// <summary>SQLITE_TOOBIG.</summary>
    public const int TooBig = 18;

    /// <summary>SQLITE_CONSTRAINT.</summary>
    public const int Constraint = 19;

    /// <summary>SQLITE_MISMATCH.</summary>
    public const int Mismatch = 20;

    /// <summary>SQLITE_MISUSE.</summary>
    public const int Misuse = 21;

    /// <summary>SQLITE_NOTADB: the file is not a database.</summary>
    public const int NotADb = 26;

    /// <summary>SQLITE_BUSY_RECOVERY.</summary>
    public const int BusyRecovery = Busy | (1 << 8);

    /// <summary>SQLITE_BUSY_SNAPSHOT.</summary>
    public const int BusySnapshot = Busy | (2 << 8);

    /// <summary>SQLITE_BUSY_TIMEOUT.</summary>
    public const int BusyTimeout = Busy | (3 << 8);

    /// <summary>SQLITE_CANTOPEN_NOTEMPDIR.</summary>
    public const int CantOpenNoTempDir = CantOpen | (1 << 8);

    /// <summary>SQLITE_CANTOPEN_ISDIR.</summary>
    public const int CantOpenIsDir = CantOpen | (2 << 8);

    /// <summary>SQLITE_CORRUPT_VTAB.</summary>
    public const int CorruptVtab = Corrupt | (1 << 8);

    /// <summary>SQLITE_CORRUPT_SEQUENCE.</summary>
    public const int CorruptSequence = Corrupt | (2 << 8);

    /// <summary>SQLITE_CORRUPT_INDEX.</summary>
    public const int CorruptIndex = Corrupt | (3 << 8);

    /// <summary>SQLITE_IOERR_READ.</summary>
    public const int IoErrRead = IoErr | (1 << 8);

    /// <summary>SQLITE_IOERR_SHORT_READ.</summary>
    public const int IoErrShortRead = IoErr | (2 << 8);

    /// <summary>SQLITE_IOERR_WRITE.</summary>
    public const int IoErrWrite = IoErr | (3 << 8);

    /// <summary>SQLITE_IOERR_FSYNC.</summary>
    public const int IoErrFsync = IoErr | (4 << 8);

    /// <summary>SQLITE_IOERR_DIR_FSYNC.</summary>
    public const int IoErrDirFsync = IoErr | (5 << 8);

    /// <summary>SQLITE_IOERR_TRUNCATE.</summary>
    public const int IoErrTruncate = IoErr | (6 << 8);

    /// <summary>SQLITE_IOERR_DELETE.</summary>
    public const int IoErrDelete = IoErr | (10 << 8);

    /// <summary>SQLITE_IOERR_DELETE_NOENT.</summary>
    public const int IoErrDeleteNoEnt = IoErr | (23 << 8);

    /// <summary>SQLITE_ERROR_MISSING_COLLSEQ: a collation name that is not registered.</summary>
    public const int ErrorMissingCollSeq = Error | (1 << 8);

    /// <summary>SQLITE_CONSTRAINT_COMMITHOOK: a commit hook asked for the commit to become a rollback.</summary>
    public const int ConstraintCommitHook = Constraint | (2 << 8);

    /// <summary>SQLITE_ABORT_ROLLBACK: a statement or incremental blob handle was aborted by a ROLLBACK.</summary>
    public const int AbortRollback = Abort | (2 << 8);

    /// <summary>SQLITE_READONLY_DIRECTORY: a file the connection needs (the -wal or -shm) cannot be created because its directory is read-only.</summary>
    public const int ReadOnlyDirectory = ReadOnly | (6 << 8);

    /// <summary>SQLITE_READONLY_ROLLBACK.</summary>
    public const int ReadOnlyRollback = ReadOnly | (3 << 8);

    /// <summary>SQLITE_READONLY_DBMOVED.</summary>
    public const int ReadOnlyDbMoved = ReadOnly | (4 << 8);

    /// <summary>The primary code of an extended code (its low byte).</summary>
    /// <param name="code">A primary or extended result code.</param>
    /// <returns>The primary code.</returns>
    public static int Primary(int code) => code & 0xFF;
}

/// <summary>
/// An error from OneLite, carrying the SQLite result code and extended result code the same failure reports through the C API.
/// The message text for the codes the storage layer raises follows the reference library's wording.
/// </summary>
public class OneLiteException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="extendedCode">The extended result code (or a primary code), for example <see cref="ResultCode.Corrupt"/>.</param>
    /// <param name="message">The message. For corruption it names the page and what was wrong.</param>
    /// <param name="inner">The underlying exception, if any.</param>
    public OneLiteException(int extendedCode, string message, Exception? inner = null) : base(message, inner)
    {
        ExtendedCode = extendedCode;
    }

    /// <summary>Creates the exception with a detail line and an underlying exception.</summary>
    /// <param name="extendedCode">The extended result code (or a primary code).</param>
    /// <param name="message">The message, identical to the reference library's for this code.</param>
    /// <param name="detail">What OneLite adds.</param>
    /// <param name="inner">The underlying exception.</param>
    public OneLiteException(int extendedCode, string message, string detail, Exception? inner) : base(message, inner)
    {
        ExtendedCode = extendedCode;
        Detail = detail;
    }

    /// <summary>Creates the exception with a detail line.</summary>
    /// <param name="extendedCode">The extended result code (or a primary code).</param>
    /// <param name="message">The message, identical to the reference library's for this code.</param>
    /// <param name="detail">What OneLite adds: for corruption, the page and what was wrong.</param>
    public OneLiteException(int extendedCode, string message, string detail) : base(message)
    {
        ExtendedCode = extendedCode;
        Detail = detail;
    }

    /// <summary>A rejected SQL statement: SQLITE_ERROR with the reference text, and the position of the offending token.</summary>
    /// <param name="message">The reference library's message.</param>
    /// <param name="sql">The statement text.</param>
    /// <param name="position">UTF-16 index of the offending token in <paramref name="sql"/>, or -1 when the reference library reports none.</param>
    /// <returns>The exception, with <see cref="ErrorOffset"/> set.</returns>
    internal static OneLiteException SqlSyntax(string message, string sql, int position)
    {
        int offset = position < 0 ? -1 : System.Text.Encoding.UTF8.GetByteCount(sql.AsSpan(0, Math.Min(position, sql.Length)));
        return new OneLiteException(ResultCode.Error, message, offset < 0 ? string.Empty : "at byte offset " + offset) { ErrorOffset = offset };
    }

    /// <summary>SQLITE_CORRUPT with the reference text "database disk image is malformed"; the page and cause go in <see cref="Detail"/>.</summary>
    /// <param name="page">The page the problem was found on, or 0 if not tied to one.</param>
    /// <param name="detail">What was wrong, in words.</param>
    /// <returns>The exception.</returns>
    public static OneLiteException Corrupt(uint page, string detail) =>
        new(ResultCode.Corrupt, "database disk image is malformed", page == 0 ? detail : $"page {page}: {detail}");

    /// <summary>Extra context OneLite adds to the identical message (for corruption: the page and what was wrong); empty if none.</summary>
    public string Detail { get; private set; } = string.Empty;

    /// <summary>The SQL text the error is about, when it came from compiling or running a statement; otherwise null.</summary>
    public string? Sql { get; internal set; }

    /// <summary>Where in <see cref="Sql"/> the error is (UTF-16 index, 1-based line and column), when the error has a place in the text; otherwise null.</summary>
    public SqlPosition? Position { get; internal set; }

    /// <summary>The name the error is about (the unknown table, column or function, the ambiguous column, the token a syntax error stopped at); null if there is none.</summary>
    public string? Identifier { get; internal set; }

    /// <summary>Names that look like what was meant, closest first (at most three); empty if nothing is close.
    /// For an unknown or ambiguous column it also lists the tables that have a column of that name, as <c>table.column</c>.</summary>
    public IReadOnlyList<string> Suggestions { get; internal set; } = [];

    /// <summary>For a failed constraint, which one and on what; otherwise null.</summary>
    public ConstraintInfo? Constraint { get; internal set; }

    /// <summary>The line of <see cref="Sql"/> holding <see cref="Position"/> with a second line of spaces and <c>^~~</c> under the offending text; null when there is no position.</summary>
    public string? Caret { get; internal set; }

    /// <summary>
    /// A readable account of the error: the message, the SQL line with a caret under the place, the constraint details and "did you mean" suggestions.
    /// <see cref="Exception.Message"/> itself is unchanged and identical to the reference library's.
    /// </summary>
    /// <returns>Multi-line text with <c>\n</c> line breaks.</returns>
    public string Explain() => Diagnostics.ErrorDiagnostics.Render(this);

    /// <summary>The exception as <see cref="Exception.ToString"/> prints it, followed by <see cref="Explain"/> when OneLite has anything to add.</summary>
    /// <returns>The text.</returns>
    public override string ToString()
    {
        string extra = Explain();
        string b = base.ToString();
        return extra.Length == 0 ? b : b + "\n" + extra;
    }

    /// <summary>For a rejected SQL statement, the UTF-8 byte offset of the offending token (what <c>sqlite3_error_offset</c> returns); -1 when there is none.</summary>
    public int ErrorOffset { get; internal set; } = -1;

    /// <summary>UTF-16 index in the statement text of the token the compiler blames, or -1; turned into <see cref="ErrorOffset"/> when the statement text is known.</summary>
    internal int NodeStart { get; init; } = -1;

    /// <summary>The extended result code; equals <see cref="Code"/> when the failure has no extended code.</summary>
    public int ExtendedCode { get; }

    /// <summary>The primary result code (the low byte of <see cref="ExtendedCode"/>).</summary>
    public int Code => ExtendedCode & 0xFF;
}
