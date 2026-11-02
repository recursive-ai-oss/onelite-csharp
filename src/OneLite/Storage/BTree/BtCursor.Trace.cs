using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Storage.BTree;

// What a cursor writes into the trace of a concurrent transaction (ConcTrace). All of it is skipped when the context has no trace.
public sealed partial class BtCursor
{
    private ConcTrace? _traceOwner;
    private int _traceId;
    private bool _payloadSeen;

    // Pre-write facts gathered across the suspend points of an insert or delete.
    private bool _logBeforeReady;
    private ulong _logBeforeHash;
    private bool _logExisted;
    private byte[]? _logDeleteKey;
    private long _logDeleteRowid;

    /// <summary>Set by the engine around a probe that enforces a constraint: the trace marks such reads as required at every isolation level.</summary>
    internal bool TraceRequired { get; set; }

    private int TraceCursorId(ConcTrace t)
    {
        if (!ReferenceEquals(_traceOwner, t))
        {
            _traceOwner = t;
            _traceId = t.NewCursorId();
        }

        return _traceId;
    }

    // Hash of where the cursor stands: the rowid for a table, the size and local bytes of the key for an index, a fixed value off the ends.
    // A table row's content is not part of it: the content enters the trace only when the transaction reads it (TracePayload).
    internal ulong LandingHash()
    {
        if (State != CursorState.Valid)
        {
            return 1;
        }

        CellInfo ci = CurrentCell(out PageHandle pg, out _);
        if (Tree.IntKey)
        {
            return TraceHash.Mix(2, (ulong)ci.Rowid);
        }

        return TraceHash.Mix(TraceHash.Bytes(new ReadOnlySpan<byte>(pg.Data, ci.PayloadOff, ci.Local)), (ulong)ci.PayloadSize);
    }

    private bool IsRequiredRead() => TraceRequired || Tree.Root == 1;

    private void TraceSeek(ConcTrace t, BtStatus st, long rowid, ReadOnlySpan<Mem> key, int prefix, int cmp)
    {
        _payloadSeen = false;
        if (st != BtStatus.Ok)
        {
            return;
        }

        ref TraceEntry e = ref t.Add(Tree.IntKey ? TraceOp.SeekRowid : TraceOp.SeekKey);
        e.Cursor = TraceCursorId(t);
        e.Tree = Tree;
        e.Arg = rowid;
        e.Aux = prefix;
        e.Required = IsRequiredRead();
        e.Data = Tree.IntKey ? null : TraceHash.Clone(key);
        e.Hash = TraceHash.Mix(LandingHash(), (ulong)(cmp < 0 ? 0 : cmp == 0 ? 1 : 2));
    }

    private BtStatus TraceMove(TraceOp op, BtStatus st)
    {
        if (_ctx.Trace is not { Active: true } t)
        {
            return st;
        }

        _payloadSeen = false;
        if (st != BtStatus.Ok)
        {
            return st;
        }

        int id = TraceCursorId(t);
        ulong land = LandingHash();
        if ((op == TraceOp.Next || op == TraceOp.Previous) && t.TryLastRun(id, op, out int last) && t[last].Required == IsRequiredRead())
        {
            ref TraceEntry r = ref t[last];
            r.Count++;
            r.Hash = TraceHash.Mix(r.Hash, land);
            return st;
        }

        ref TraceEntry e = ref t.Add(op);
        e.Cursor = id;
        e.Tree = Tree;
        e.Required = IsRequiredRead();
        e.Count = 1;
        e.Hash = TraceHash.Mix(TraceHash.Seed, land);
        return st;
    }

    private void TracePayload(ReadOnlySpan<byte> payload)
    {
        if (_ctx.Trace is not { Active: true } t || !Tree.IntKey || _payloadSeen)
        {
            return;
        }

        _payloadSeen = true;
        ref TraceEntry e = ref t.Add(TraceOp.Payload);
        e.Cursor = TraceCursorId(t);
        e.Tree = Tree;
        e.Required = IsRequiredRead();
        e.Hash = TraceHash.Bytes(payload);
    }

    private void TraceReset(TraceOp op)
    {
        if (_ctx.Trace is not { Active: true } t || !ReferenceEquals(_traceOwner, t))
        {
            return;
        }

        _payloadSeen = false;
        if (op == TraceOp.Reset && State == CursorState.Invalid && _op == Op.None && _wop == WOp.None)
        {
            return;
        }

        ref TraceEntry e = ref t.Add(op);
        e.Cursor = _traceId;
        e.Tree = Tree;
        if (op == TraceOp.Dispose)
        {
            _traceOwner = null;
        }
    }

    // Before an insert over an existing row or a delete: the digest of the row as this transaction saw it. May need overflow pages.
    private BtStatus TraceLoadBefore(ConcTrace t)
    {
        if (_logBeforeReady)
        {
            return BtStatus.Ok;
        }

        CellInfo ci = CurrentCell(out PageHandle pg, out int off);
        BtStatus s = PayloadOf(pg, ci, off, out byte[] buf, out int bo);
        if (s != BtStatus.Ok)
        {
            return s;
        }

        var span = new ReadOnlySpan<byte>(buf, bo, (int)ci.PayloadSize);
        _logBeforeHash = TraceHash.Bytes(span);
        _logDeleteRowid = Tree.IntKey ? ci.Rowid : 0;
        _logDeleteKey = Tree.IntKey ? null : span.ToArray();
        _logBeforeReady = true;
        _ = t;
        return BtStatus.Ok;
    }

    private void TraceInsertDone(ConcTrace t, long rowid, ReadOnlySpan<byte> payload)
    {
        t.Wrote = true;
        ref TraceEntry e = ref t.Add(Tree.IntKey ? TraceOp.PutRow : TraceOp.PutIndex);
        e.Cursor = TraceCursorId(t);
        e.Tree = Tree;
        e.Required = true;
        e.Arg = rowid;
        e.Existed = (byte)(_logExisted ? 1 : 0);
        e.Hash = _logBeforeHash;
        e.Data = payload.ToArray();
        _logBeforeReady = false;
        _logExisted = false;
        _payloadSeen = false;
    }

    private void TraceDeleteDone(ConcTrace t)
    {
        t.Wrote = true;
        ref TraceEntry e = ref t.Add(Tree.IntKey ? TraceOp.DeleteRow : TraceOp.DeleteIndex);
        e.Cursor = TraceCursorId(t);
        e.Tree = Tree;
        e.Required = true;
        e.Arg = _logDeleteRowid;
        e.Hash = _logBeforeHash;
        e.Data = _logDeleteKey;
        _logBeforeReady = false;
        _logDeleteKey = null;
        _payloadSeen = false;
    }
}
