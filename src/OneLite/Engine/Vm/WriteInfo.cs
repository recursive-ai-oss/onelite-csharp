namespace RecursiveAI.OneLite.Vm;

/// <summary>
/// Operand of <see cref="Op.IdxProbe"/>: an index entry whose trailing columns equal the registers <c>TailBase..TailBase+TailCount</c> is the row
/// being updated itself, not a conflicting one (an UPDATE that keeps a unique key must not collide with its own old entry).
/// </summary>
internal sealed class ProbeSpec
{
    internal int TailBase;
    internal int TailCount;
}

/// <summary>Operand of <see cref="Op.SubSelect"/>: a compiled SELECT run as a row source of a write statement.</summary>
internal sealed class SubSelectSpec
{
    internal VmProgram Program = null!;

    /// <summary>True to read every row before the first is used (INSERT ... SELECT from the table being written must not see its own new rows).</summary>
    internal bool Drain;
}

/// <summary>Which SQLite constraint kind a <see cref="Op.Raise"/> reports (rescode).</summary>
internal static class ConstraintCodes
{
    internal const int Check = 275;
    internal const int ForeignKey = 787;
    internal const int NotNull = 1299;
    internal const int PrimaryKey = 1555;
    internal const int Unique = 2067;
    internal const int DataType = 3091;
    internal const int Mismatch = 20;
}

/// <summary>What a failed constraint does to the statement (lang_conflict).</summary>
internal enum ErrorAction : byte
{
    /// <summary>ABORT: undo this statement's changes, keep the transaction.</summary>
    Abort,

    /// <summary>FAIL: stop, keep the changes this statement already made.</summary>
    Fail,

    /// <summary>ROLLBACK: undo the whole transaction.</summary>
    Rollback,
}
