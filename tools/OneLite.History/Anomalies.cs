using System;
using System.Collections.Generic;

namespace RecursiveAI.OneLite.History;

/// <summary>The anomaly classes the checker reports (Adya, Berenson et al., Fekete et al., Kingsbury and Alvaro).</summary>
public enum AnomalyClass
{
    /// <summary>A read disagrees with the transaction's own earlier writes or with what the same transaction last read plus its own writes.</summary>
    Internal,

    /// <summary>Two reads of one key in a transaction differ although the transaction wrote nothing in between (Berenson et al. P2, fuzzy read).</summary>
    NonRepeatableRead,

    /// <summary>A read returned a value that no transaction ever wrote.</summary>
    GarbageRead,

    /// <summary>List reads of one key are not prefixes of a single order (a lost or reordered append).</summary>
    IncompatibleOrder,

    /// <summary>A list read contains the same element twice.</summary>
    DuplicateElements,

    /// <summary>Aborted read: a committed transaction read a write of a transaction that failed (Adya G1a).</summary>
    G1a,

    /// <summary>Intermediate read: a read saw a transaction's non-final write (Adya G1b).</summary>
    G1b,

    /// <summary>A dependency cycle of write-write edges only (Adya G0, dirty write).</summary>
    G0,

    /// <summary>A cycle of write-write and write-read edges with at least one write-read (Adya G1c).</summary>
    G1c,

    /// <summary>A cycle with exactly one anti-dependency: read skew and its relatives (Adya G-single).</summary>
    GSingle,

    /// <summary>Two transactions read the same version of a key and both wrote it (Berenson et al. P4).</summary>
    LostUpdate,

    /// <summary>A cycle with two or more anti-dependencies, none adjacent; forbidden by snapshot isolation (Fekete et al.).</summary>
    GNonadjacent,

    /// <summary>A cycle with two or more anti-dependencies (write skew is the two-transaction case); allowed by snapshot isolation (Adya G2-item).</summary>
    G2Item,

    /// <summary>A write-write/real-time cycle.</summary>
    G0Realtime,

    /// <summary>A cycle with real-time edges and no anti-dependency.</summary>
    G1cRealtime,

    /// <summary>A cycle with real-time edges and exactly one anti-dependency.</summary>
    GSingleRealtime,

    /// <summary>A cycle with real-time edges and two or more anti-dependencies.</summary>
    G2ItemRealtime,
}

/// <summary>Isolation levels a history can be checked against.</summary>
public enum IsolationLevel
{
    /// <summary>Forbids G0, G1a, G1b, G1c and the data-integrity anomalies.</summary>
    ReadCommitted,

    /// <summary>Adds G-single, G-nonadjacent, lost update and non-repeatable reads; allows write skew (Berenson et al., Fekete et al.). Start-time ordering is not checked.</summary>
    SnapshotIsolation,

    /// <summary>Adds G2-item: no dependency cycle at all.</summary>
    Serializable,

    /// <summary>Serializable plus real-time order.</summary>
    StrictSerializable,
}

/// <summary>Which anomaly classes each level forbids.</summary>
public static class Levels
{
    /// <summary>The level's lower-case name.</summary>
    public static string Name(IsolationLevel l) => l switch
    {
        IsolationLevel.ReadCommitted => "read-committed",
        IsolationLevel.SnapshotIsolation => "snapshot-isolation",
        IsolationLevel.Serializable => "serializable",
        _ => "strict-serializable",
    };

    /// <summary>Parses a level name.</summary>
    public static bool TryParse(string s, out IsolationLevel l)
    {
        for (var i = IsolationLevel.ReadCommitted; i <= IsolationLevel.StrictSerializable; i++)
        {
            if (Name(i) == s)
            {
                l = i;
                return true;
            }
        }

        l = default;
        return false;
    }

    /// <summary>True when <paramref name="level"/> forbids <paramref name="c"/>.</summary>
    public static bool Forbids(IsolationLevel level, AnomalyClass c)
    {
        switch (c)
        {
            case AnomalyClass.Internal:
            case AnomalyClass.GarbageRead:
            case AnomalyClass.IncompatibleOrder:
            case AnomalyClass.DuplicateElements:
            case AnomalyClass.G1a:
            case AnomalyClass.G1b:
            case AnomalyClass.G0:
            case AnomalyClass.G1c:
                return true;
            case AnomalyClass.NonRepeatableRead:
            case AnomalyClass.GSingle:
            case AnomalyClass.LostUpdate:
            case AnomalyClass.GNonadjacent:
                return level >= IsolationLevel.SnapshotIsolation;
            case AnomalyClass.G2Item:
                return level >= IsolationLevel.Serializable;
            default:
                return level >= IsolationLevel.StrictSerializable;
        }
    }

    /// <summary>The class name as printed (Adya's spelling where there is one).</summary>
    public static string Label(AnomalyClass c) => c switch
    {
        AnomalyClass.Internal => "internal",
        AnomalyClass.NonRepeatableRead => "non-repeatable-read",
        AnomalyClass.GarbageRead => "garbage-read",
        AnomalyClass.IncompatibleOrder => "incompatible-order",
        AnomalyClass.DuplicateElements => "duplicate-elements",
        AnomalyClass.G1a => "G1a",
        AnomalyClass.G1b => "G1b",
        AnomalyClass.G0 => "G0",
        AnomalyClass.G1c => "G1c",
        AnomalyClass.GSingle => "G-single",
        AnomalyClass.LostUpdate => "lost-update",
        AnomalyClass.GNonadjacent => "G-nonadjacent",
        AnomalyClass.G2Item => "G2-item",
        AnomalyClass.G0Realtime => "G0-realtime",
        AnomalyClass.G1cRealtime => "G1c-realtime",
        AnomalyClass.GSingleRealtime => "G-single-realtime",
        _ => "G2-item-realtime",
    };
}

/// <summary>One reported anomaly with the transactions involved and, for cycles, the minimal cycle found.</summary>
public sealed class Anomaly
{
    /// <summary>Creates an anomaly.</summary>
    public Anomaly(AnomalyClass cls, string message, int[] txns, IReadOnlyList<CycleStep>? cycle)
    {
        Class = cls;
        Message = message;
        Txns = txns;
        Cycle = cycle;
    }

    /// <summary>The class.</summary>
    public AnomalyClass Class { get; }

    /// <summary>A human-readable explanation.</summary>
    public string Message { get; }

    /// <summary>Indexes (into <see cref="History.Txns"/>) of the transactions involved.</summary>
    public int[] Txns { get; }

    /// <summary>The cycle for dependency-graph anomalies; null otherwise.</summary>
    public IReadOnlyList<CycleStep>? Cycle { get; }
}

/// <summary>Counts every anomaly and keeps the first few examples of each class.</summary>
internal sealed class AnomalySink
{
    private readonly int _maxExamples;
    private readonly int[] _counts = new int[Enum.GetValues<AnomalyClass>().Length];
    private readonly int[] _kept = new int[Enum.GetValues<AnomalyClass>().Length];

    public AnomalySink(int maxExamples) => _maxExamples = maxExamples;

    public List<Anomaly> Examples { get; } = [];

    public int Count(AnomalyClass c) => _counts[(int)c];

    /// <summary>Counts an occurrence and tells the caller whether to build and add an example.</summary>
    public bool Record(AnomalyClass c)
    {
        _counts[(int)c]++;
        return _kept[(int)c] < _maxExamples;
    }

    public void Add(Anomaly a)
    {
        _kept[(int)a.Class]++;
        Examples.Add(a);
    }
}
