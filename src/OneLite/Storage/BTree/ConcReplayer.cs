using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Record;
using PagerClass = RecursiveAI.OneLite.Storage.Pager.Pager;

namespace RecursiveAI.OneLite.Storage.BTree;

/// <summary>Why a concurrent commit was refused.</summary>
internal sealed class ConcConflict
{
    public required string Detail { get; init; }

    /// <summary>For an insert that found its rowid taken: the tree and the rowid, so the allocator can skip past it.</summary>
    public uint Root { get; init; }

    public long Rowid { get; init; }

    public bool RowidTaken { get; init; }
}

/// <summary>
/// Runs a concurrent transaction's trace again on the latest snapshot while the writer holds the WAL write lock (docs/concurrency.md, "Commit").
/// Reads must observe what they observed the first time (every read at serializable; constraint checks, schema reads and the state each write
/// depends on at snapshot isolation); writes are applied. Resumable: <see cref="Step"/> returns Pending after a page miss and is called again.
/// </summary>
internal sealed class ConcReplayer
{
    private readonly ConcTrace _t;
    private readonly PagerClass _pager;
    private readonly bool _serializable;
    private readonly Dictionary<int, BtCursor> _cursors = [];
    private readonly Dictionary<long, int> _levels = [];
    private HashSet<int>? _writers;

    private int _i;
    private int _k;
    private ulong _h;
    private int _phase;
    private Mem[]? _keyMems;

    internal ConcReplayer(ConcTrace trace, PagerClass pager, bool serializable)
    {
        _t = trace;
        _pager = pager;
        _serializable = serializable;
    }

    /// <summary>Set when a read or a precondition did not hold.</summary>
    internal ConcConflict? Conflict { get; private set; }

    /// <summary>Entries replayed so far (diagnostics).</summary>
    internal int Position => _i;

    internal void Dispose()
    {
        foreach (BtCursor c in _cursors.Values)
        {
            c.Dispose();
        }

        _cursors.Clear();
    }

    private bool Fail(string detail, uint root = 0, long rowid = 0, bool taken = false)
    {
        Conflict = new ConcConflict { Detail = detail, Root = root, Rowid = rowid, RowidTaken = taken };
        return false;
    }

    private BtCursor Cur(in TraceEntry e)
    {
        if (!_cursors.TryGetValue(e.Cursor, out BtCursor? c))
        {
            c = e.Tree!.OpenCursor();
            _cursors[e.Cursor] = c;
        }

        return c;
    }

    // Under snapshot isolation a cursor that never writes only reads data nobody validates, so its steps are skipped.
    private bool Skippable(in TraceEntry e)
    {
        if (_serializable || e.Required)
        {
            return false;
        }

        if (_writers is null)
        {
            _writers = [];
            for (int j = 0; j < _t.Count; j++)
            {
                if (_t[j].Op is TraceOp.PutRow or TraceOp.PutIndex or TraceOp.DeleteRow or TraceOp.DeleteIndex)
                {
                    _writers.Add(_t[j].Cursor);
                }
            }
        }

        return !_writers.Contains(e.Cursor);
    }

    /// <summary>Runs the rest of the trace.</summary>
    /// <returns>Ok when all of it replayed; Pending to be called again; Error with <see cref="Conflict"/> set (conflict) or the cursor's error.</returns>
    internal BtStatus Step()
    {
        _t.Replaying = true;
        try
        {
            while (_i < _t.Count)
            {
                BtStatus s = One(ref _t[_i], out bool ok);
                if (s == BtStatus.Pending)
                {
                    return s;
                }

                if (s == BtStatus.Error)
                {
                    return s;
                }

                if (!ok)
                {
                    return BtStatus.Error;
                }

                _i++;
                _k = 0;
                _h = 0;
                _phase = 0;
                _keyMems = null;
            }

            return BtStatus.Ok;
        }
        finally
        {
            _t.Replaying = false;
        }
    }

    private static int CmpClass(int cmp) => cmp < 0 ? 0 : cmp == 0 ? 1 : 2;

    private BtStatus One(ref TraceEntry e, out bool ok)
    {
        ok = true;
        if (Skippable(e) && e.Op is not (TraceOp.Reset or TraceOp.Dispose or TraceOp.SavepointOpen or TraceOp.SavepointRollback or TraceOp.SavepointRelease))
        {
            return BtStatus.Ok;
        }

        switch (e.Op)
        {
            case TraceOp.SeekRowid:
            {
                BtCursor c = Cur(e);
                BtStatus s = c.MoveToRowid(e.Arg, out int cmp);
                if (s != BtStatus.Ok)
                {
                    return s;
                }

                if ((_serializable || e.Required) && c.SeekHash(cmp, e.Existed == 1) != e.Hash)
                {
                    ok = Fail($"a row of table (root page {e.Tree!.Root}) near rowid {e.Arg} changed");
                }

                return BtStatus.Ok;
            }

            case TraceOp.SeekKey:
            {
                BtCursor c = Cur(e);
                BtStatus s = c.MoveToKey((Mem[])e.Data!, e.Aux, out int cmp);
                if (s != BtStatus.Ok)
                {
                    return s;
                }

                if ((_serializable || e.Required) && c.SeekHash(cmp, e.Existed == 1) != e.Hash)
                {
                    ok = Fail($"an index (root page {e.Tree!.Root}) changed near a key this transaction looked up");
                }

                return BtStatus.Ok;
            }

            case TraceOp.First:
            case TraceOp.Last:
            {
                BtCursor c = Cur(e);
                BtStatus s = e.Op == TraceOp.First ? c.First() : c.Last();
                if (s != BtStatus.Ok)
                {
                    return s;
                }

                if ((_serializable || e.Required) && TraceHash.Mix(TraceHash.Seed, c.LandingHash()) != e.Hash)
                {
                    ok = Fail($"the {(e.Op == TraceOp.First ? "first" : "last")} entry of a tree (root page {e.Tree!.Root}) changed");
                }

                return BtStatus.Ok;
            }

            case TraceOp.Next:
            case TraceOp.Previous:
            {
                BtCursor c = Cur(e);
                if (_k == 0)
                {
                    _h = TraceHash.Seed;
                }

                while (_k < e.Count)
                {
                    BtStatus s = e.Op == TraceOp.Next ? c.Next() : c.Previous();
                    if (s != BtStatus.Ok)
                    {
                        return s;
                    }

                    _h = TraceHash.Mix(_h, c.LandingHash());
                    _k++;
                }

                if ((_serializable || e.Required) && _h != e.Hash)
                {
                    ok = Fail($"rows of a tree (root page {e.Tree!.Root}) that this transaction scanned changed");
                }

                return BtStatus.Ok;
            }

            case TraceOp.Payload:
            {
                BtCursor c = Cur(e);
                if (c.State != CursorState.Valid)
                {
                    ok = Fail($"a row of a table (root page {e.Tree!.Root}) that this transaction read is gone");
                    return BtStatus.Ok;
                }

                BtStatus s = c.LoadPayload();
                if (s != BtStatus.Ok)
                {
                    return s;
                }

                if (TraceHash.Bytes(c.Payload) != e.Hash)
                {
                    ok = Fail($"a row of a table (root page {e.Tree!.Root}) that this transaction read was changed");
                }

                return BtStatus.Ok;
            }

            case TraceOp.Reset:
                if (_cursors.TryGetValue(e.Cursor, out BtCursor? rc))
                {
                    rc.Reset();
                }

                return BtStatus.Ok;
            case TraceOp.Dispose:
                if (_cursors.Remove(e.Cursor, out BtCursor? dc))
                {
                    dc.Dispose();
                }

                return BtStatus.Ok;
            case TraceOp.PutRow:
                return PutRow(ref e, out ok);
            case TraceOp.PutIndex:
                return PutIndex(ref e, out ok);
            case TraceOp.DeleteRow:
                return DeleteRow(ref e, out ok);
            case TraceOp.DeleteIndex:
                return DeleteIndex(ref e, out ok);
            case TraceOp.SavepointOpen:
                _levels[e.Arg] = _pager.OpenSavepoint();
                return BtStatus.Ok;
            case TraceOp.SavepointRollback:
            {
                PagerStatus ps = _pager.RollbackSavepoint(_levels[e.Arg]);
                return ps == PagerStatus.Pending ? BtStatus.Pending : ps == PagerStatus.Error ? Err() : BtStatus.Ok;
            }

            case TraceOp.SavepointRelease:
            {
                int level = _levels[e.Arg];
                _pager.ReleaseSavepoint(level);
                foreach (long k in System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(System.Linq.Enumerable.Where(_levels, kv => kv.Value >= level), kv => kv.Key)))
                {
                    _levels.Remove(k);
                }

                return BtStatus.Ok;
            }

            case TraceOp.SeqMax:
                return SeqMax(ref e, out ok);
            default:
                throw new InvalidOperationException("unknown trace entry");
        }
    }

    private static BtStatus Err() => BtStatus.Error;

    private BtStatus PutRow(ref TraceEntry e, out bool ok)
    {
        ok = true;
        BtCursor c = Cur(e);
        if (_phase == 0)
        {
            BtStatus s = c.MoveToRowid(e.Arg, out int cmp);
            if (s != BtStatus.Ok)
            {
                return s;
            }

            bool exists = c.State == CursorState.Valid && cmp == 0;
            if (exists != (e.Existed == 1))
            {
                ok = exists
                    ? Fail($"rowid {e.Arg} of a table (root page {e.Tree!.Root}) was inserted by another transaction", e.Tree!.Root, e.Arg, true)
                    : Fail($"rowid {e.Arg} of a table (root page {e.Tree!.Root}) was deleted by another transaction", e.Tree!.Root, e.Arg);
                return BtStatus.Ok;
            }

            _phase = exists ? 1 : 2;
        }

        if (_phase == 1)
        {
            BtStatus s = c.LoadPayload();
            if (s != BtStatus.Ok)
            {
                return s;
            }

            if (TraceHash.Bytes(c.Payload) != e.Hash)
            {
                ok = Fail($"rowid {e.Arg} of a table (root page {e.Tree!.Root}) was changed by another transaction");
                return BtStatus.Ok;
            }

            _phase = 2;
        }

        return c.Insert(e.Arg, (byte[])e.Data!);
    }

    private static Mem[] Decode(byte[] record)
    {
        var rv = new RecordView();
        if (!rv.Parse(record, 0, record.Length))
        {
            throw new InvalidOperationException("a traced index key is not a record");
        }

        var key = new Mem[rv.ColumnCount];
        for (int i = 0; i < key.Length; i++)
        {
            rv.Load(i, ref key[i]);
        }

        return key;
    }

    private BtStatus PutIndex(ref TraceEntry e, out bool ok)
    {
        ok = true;
        BtCursor c = Cur(e);
        _keyMems ??= Decode((byte[])e.Data!);
        if (_phase == 0)
        {
            BtStatus s = c.MoveToKey(_keyMems, 0, out int cmp);
            if (s != BtStatus.Ok)
            {
                return s;
            }

            if (c.State == CursorState.Valid && cmp == 0)
            {
                ok = Fail($"an index entry (index root page {e.Tree!.Root}) was inserted by another transaction");
                return BtStatus.Ok;
            }

            _phase = 1;
        }

        return c.Insert((byte[])e.Data!, _keyMems);
    }

    private BtStatus DeleteRow(ref TraceEntry e, out bool ok)
    {
        ok = true;
        BtCursor c = Cur(e);
        if (_phase == 0)
        {
            BtStatus s = c.MoveToRowid(e.Arg, out int cmp);
            if (s != BtStatus.Ok)
            {
                return s;
            }

            if (c.State != CursorState.Valid || cmp != 0)
            {
                ok = Fail($"rowid {e.Arg} of a table (root page {e.Tree!.Root}) was deleted by another transaction");
                return BtStatus.Ok;
            }

            _phase = 1;
        }

        if (_phase == 1)
        {
            BtStatus s = c.LoadPayload();
            if (s != BtStatus.Ok)
            {
                return s;
            }

            if (TraceHash.Bytes(c.Payload) != e.Hash)
            {
                ok = Fail($"rowid {e.Arg} of a table (root page {e.Tree!.Root}) was changed by another transaction");
                return BtStatus.Ok;
            }

            _phase = 2;
        }

        return c.Delete();
    }

    private BtStatus DeleteIndex(ref TraceEntry e, out bool ok)
    {
        ok = true;
        BtCursor c = Cur(e);
        _keyMems ??= Decode((byte[])e.Data!);
        if (_phase == 0)
        {
            BtStatus s = c.MoveToKey(_keyMems, 0, out int cmp);
            if (s != BtStatus.Ok)
            {
                return s;
            }

            if (c.State != CursorState.Valid || cmp != 0)
            {
                ok = Fail($"an index entry (index root page {e.Tree!.Root}) was deleted by another transaction");
                return BtStatus.Ok;
            }

            _phase = 1;
        }

        return c.Delete();
    }

    // AUTOINCREMENT's counter: a max-merge that is never a read (docs/concurrency.md, "Rowids"). The new value must be above what the table
    // now holds, which keeps an id that was used and deleted by a committed transaction from being handed out again.
    private BtStatus SeqMax(ref TraceEntry e, out bool ok)
    {
        ok = true;
        BtCursor c = Cur(e);
        string name = (string)e.Data!;
        TextEncoding enc = e.Tree!.Context.Encoding;
        if (_phase == 0)
        {
            BtStatus s = c.First();
            if (s != BtStatus.Ok)
            {
                return s;
            }

            _phase = 1;
        }

        // Phase 1: look at the row under the cursor; 2: step on; 5: no row for this table, take the next rowid; 6: write.
        while (_phase is 1 or 2)
        {
            if (_phase == 2)
            {
                BtStatus s = c.Next();
                if (s != BtStatus.Ok)
                {
                    return s;
                }

                _phase = 1;
            }

            if (c.State != CursorState.Valid)
            {
                _phase = 5;
                break;
            }

            BtStatus l = c.LoadPayload();
            if (l != BtStatus.Ok)
            {
                return l;
            }

            var rv = new RecordView();
            if (!c.ParseRecord(rv))
            {
                throw OneLiteException.Corrupt(e.Tree!.Root, "a row of sqlite_sequence is not a record");
            }

            if (rv.ClassOf(0) == StorageClass.Text && string.Equals(TextCodec.Decode(rv.GetBytes(0), enc), name, StringComparison.Ordinal))
            {
                long seq = rv.ColumnCount > 1 ? rv.GetInt64(1) : 0;
                if (e.Arg <= seq)
                {
                    ok = Fail($"the AUTOINCREMENT counter of {name} moved past the id this transaction allocated");
                    return BtStatus.Ok;
                }

                _seqRowid = c.Rowid;
                _phase = 6;
                break;
            }

            _phase = 2;
        }

        if (_phase == 5)
        {
            BtStatus s = c.Last();
            if (s != BtStatus.Ok)
            {
                return s;
            }

            _seqRowid = c.State == CursorState.Valid ? c.Rowid + 1 : 1;
            _phase = 6;
        }

        _keyMems ??= [Mem.FromText(name, enc), Mem.FromInt(e.Arg)];
        int size = RecordWriter.Size(_keyMems);
        var buf = new byte[size];
        RecordWriter.Write(buf, _keyMems);
        return c.Insert(_seqRowid, buf);
    }

    private long _seqRowid;
}
