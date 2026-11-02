using System.Buffers.Binary;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Storage.BTree;

/// <summary>One kind of recorded B-tree operation (docs/concurrency.md, "The trace").</summary>
internal enum TraceOp : byte
{
    SeekRowid,
    SeekKey,
    First,
    Last,
    Next,
    Previous,
    Payload,
    Reset,
    Dispose,
    PutRow,
    PutIndex,
    DeleteRow,
    DeleteIndex,
    SavepointOpen,
    SavepointRollback,
    SavepointRelease,
    SeqMax,
}

/// <summary>
/// One entry of a concurrent transaction's trace. Reads carry what the transaction observed (<see cref="Hash"/>); writes carry the
/// key, the new content and what the row looked like before.
/// </summary>
internal struct TraceEntry
{
    public TraceOp Op;

    /// <summary>True for reads that stand for a constraint check or a schema read: they are validated at every isolation level.</summary>
    public bool Required;

    /// <summary>For writes: 1 if the key existed before the write.</summary>
    public byte Existed;

    public int Cursor;

    /// <summary>For Next and Previous: how many consecutive steps this entry stands for.</summary>
    public int Count;

    /// <summary>The rowid, the savepoint level, or the sequence value.</summary>
    public long Arg;

    /// <summary>For seeks, the prefix result.</summary>
    public int Aux;

    /// <summary>The observed landing (reads) or the digest of the content before the write.</summary>
    public ulong Hash;

    public BTree? Tree;

    /// <summary>Cloned key columns (index seeks), payload or record bytes (writes), or the table name (sequence updates).</summary>
    public object? Data;
}

/// <summary>
/// The log of what a <c>BEGIN CONCURRENT</c> transaction did at the B-tree level: every positioning, every payload it read, every insert and
/// delete, and the savepoint boundaries, in order. At commit, if other writers committed since the snapshot, the transaction is run again
/// from this log on the latest snapshot; if every read sees what it saw the first time, the transaction is exactly what would have
/// happened had it started at that moment, so its writes are applied.
/// </summary>
internal sealed class ConcTrace
{
    /// <summary>Entries beyond this number turn the transaction opaque (a conflict check by page, not by row).</summary>
    internal const int MaxEntries = 16_000_000;

    private TraceEntry[] _e = new TraceEntry[256];
    private int _n;
    private int _nextCursor = 1;

    /// <summary>While set, operations are not recorded (the engine's own bookkeeping reads).</summary>
    internal int Suspended;

    /// <summary>True while the log is being replayed.</summary>
    internal bool Replaying;

    /// <summary>Set when the transaction did something the log cannot replay; the reason is for the error text.</summary>
    internal string? OpaqueReason;

    /// <summary>Rowid blocks this transaction has taken, by table root page.</summary>
    internal Dictionary<uint, RowidBlock>? Blocks;

    /// <summary>True once the transaction wrote a row.</summary>
    internal bool Wrote;

    internal int Count => _n;

    internal ref TraceEntry this[int i] => ref _e[i];

    internal bool Active => Suspended == 0 && !Replaying && OpaqueReason is null;

    internal int NewCursorId() => _nextCursor++;

    internal void MarkOpaque(string reason) => OpaqueReason ??= reason;

    internal ref TraceEntry Add(TraceOp op)
    {
        if (_n == MaxEntries)
        {
            MarkOpaque("the transaction is too large to replay");
            _n = 0;
        }

        if (_n == _e.Length)
        {
            Array.Resize(ref _e, _e.Length * 2);
        }

        ref TraceEntry t = ref _e[_n++];
        t = default;
        t.Op = op;
        return ref t;
    }

    /// <summary>The last entry, if it is a run of <paramref name="op"/> on <paramref name="cursor"/>.</summary>
    internal bool TryLastRun(int cursor, TraceOp op, out int index)
    {
        index = _n - 1;
        return _n > 0 && _e[index].Op == op && _e[index].Cursor == cursor;
    }

    /// <summary>Forgets everything (the transaction ended).</summary>
    internal void Clear()
    {
        Array.Clear(_e, 0, _n);
        _n = 0;
        _nextCursor = 1;
        Wrote = false;
        OpaqueReason = null;
    }
}

/// <summary>A run of rowids a concurrent transaction reserved for one table: <c>Next</c> up to but not including <c>End</c>.</summary>
internal struct RowidBlock
{
    public long Next;
    public long End;
    public int Size;
}

/// <summary>A small, fast 64-bit hash for the observations in the trace. Not cryptographic: it only has to make accidental equality of different content negligible.</summary>
internal static class TraceHash
{
    internal const ulong Seed = 0x9E3779B97F4A7C15UL;

    internal static ulong Mix(ulong h, ulong v)
    {
        h ^= v + Seed + (h << 6) + (h >> 2);
        h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 33;
        return h;
    }

    internal static ulong Bytes(ReadOnlySpan<byte> b)
    {
        ulong h = Mix(Seed, (ulong)b.Length);
        while (b.Length >= 8)
        {
            h = Mix(h, BinaryPrimitives.ReadUInt64LittleEndian(b));
            b = b[8..];
        }

        ulong tail = 0;
        for (int i = 0; i < b.Length; i++)
        {
            tail |= (ulong)b[i] << (8 * i);
        }

        return Mix(h, tail);
    }

    /// <summary>A deep copy of key columns, so the log does not point into pages or registers.</summary>
    internal static Mem[] Clone(ReadOnlySpan<Mem> key)
    {
        var copy = new Mem[key.Length];
        for (int i = 0; i < key.Length; i++)
        {
            copy[i] = key[i];
            if (key[i].Buffer is not null)
            {
                copy[i].Buffer = key[i].Bytes.ToArray();
                copy[i].Offset = 0;
            }
        }

        return copy;
    }
}
