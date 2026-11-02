using System;
using System.Collections.Generic;

namespace RecursiveAI.OneLite.History;

/// <summary>The kind of one micro-operation inside a transaction.</summary>
public enum MopKind : byte
{
    /// <summary>A read. List-append: the whole list. Register: the value (0 is the initial nil).</summary>
    Read,

    /// <summary>A register write. Values are unique per key and never 0.</summary>
    Write,

    /// <summary>A list append. Values are unique per key.</summary>
    Append,
}

/// <summary>What a client knows about how a transaction ended (Kingsbury and Alvaro, "Elle", section 2).</summary>
public enum TxnOutcome : byte
{
    /// <summary>Committed.</summary>
    Ok,

    /// <summary>Definitely did not commit; none of its writes may ever be observed.</summary>
    Fail,

    /// <summary>Unknown: it may or may not have committed. Its writes may be observed; its reads are not trusted.</summary>
    Info,
}

/// <summary>Which data model the history uses.</summary>
public enum HistoryModel
{
    /// <summary>Each key holds a list; transactions append unique values and read whole lists. Version orders are recoverable from reads (Elle's list-append).</summary>
    ListAppend,

    /// <summary>Each key holds a register; transactions write unique non-zero values and read values (0 = initial nil).</summary>
    Register,
}

/// <summary>One micro-operation with its result.</summary>
public readonly struct Mop
{
    /// <summary>Creates a micro-operation.</summary>
    public Mop(MopKind kind, int key, int value, int[]? list)
    {
        Kind = kind;
        Key = key;
        Value = value;
        List = list;
    }

    /// <summary>The kind.</summary>
    public MopKind Kind { get; }

    /// <summary>The key.</summary>
    public int Key { get; }

    /// <summary>For writes and appends the value written; for a register read the value read.</summary>
    public int Value { get; }

    /// <summary>For a list read, the elements read (never null for a completed read in an ok transaction).</summary>
    public int[]? List { get; }

    /// <summary>A list read.</summary>
    public static Mop ReadList(int key, int[] list) => new(MopKind.Read, key, 0, list);

    /// <summary>A register read.</summary>
    public static Mop ReadRegister(int key, int value) => new(MopKind.Read, key, value, null);

    /// <summary>A register write.</summary>
    public static Mop WriteRegister(int key, int value) => new(MopKind.Write, key, value, null);

    /// <summary>A list append.</summary>
    public static Mop AppendList(int key, int value) => new(MopKind.Append, key, value, null);
}

/// <summary>A transaction as a client saw it.</summary>
public sealed class Txn
{
    /// <summary>Marks a transaction that never completed (no completion time).</summary>
    public const long NeverCompleted = long.MaxValue;

    /// <summary>Creates a transaction record.</summary>
    public Txn(int id, int process, long invoke, long complete, TxnOutcome outcome, Mop[] mops)
    {
        Id = id;
        Process = process;
        Invoke = invoke;
        Complete = complete;
        Outcome = outcome;
        Mops = mops;
    }

    /// <summary>A label unique within the history.</summary>
    public int Id { get; set; }

    /// <summary>The client (worker) that ran it.</summary>
    public int Process { get; }

    /// <summary>Time the client invoked it, in nanoseconds on a clock shared by all clients.</summary>
    public long Invoke { get; }

    /// <summary>Time the client learned the outcome; <see cref="NeverCompleted"/> for an unknown outcome.</summary>
    public long Complete { get; }

    /// <summary>The outcome.</summary>
    public TxnOutcome Outcome { get; }

    /// <summary>The operations in program order.</summary>
    public Mop[] Mops { get; }
}

/// <summary>A recorded history: transactions plus the model that interprets them.</summary>
public sealed class History
{
    /// <summary>Creates a history.</summary>
    public History(HistoryModel model, List<Txn> txns)
    {
        Model = model;
        Txns = txns;
    }

    /// <summary>The data model.</summary>
    public HistoryModel Model { get; }

    /// <summary>All transactions (any order).</summary>
    public List<Txn> Txns { get; }
}

/// <summary>Thrown when a history breaks the workload's own rules (for example a value written twice), so no verdict is meaningful.</summary>
public sealed class HistoryFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    public HistoryFormatException(string message)
        : base(message)
    {
    }
}
