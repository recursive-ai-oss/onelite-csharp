using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Pager;

namespace RecursiveAI.OneLite;

// BEGIN CONCURRENT on the connection (docs/concurrency.md).
public sealed partial class Connection
{
    /// <summary>True if the connection accepts <c>BEGIN CONCURRENT</c> (<see cref="DatabaseOptions.ConcurrentWrites"/>).</summary>
    internal bool ConcurrentEnabled { get; init; }

    /// <summary>The trace of the open concurrent transaction, or null.</summary>
    internal ConcTrace? ConcTx;

    /// <summary>True for serializable (the default), false for snapshot isolation.</summary>
    internal bool ConcSerializable = true;

    /// <summary>Set when the commit of the open concurrent transaction was refused: the transaction can only be rolled back.</summary>
    internal bool ConcDoomed;

    private ConcReplayer? _replayer;

    /// <summary>True while the open transaction is a concurrent one.</summary>
    public bool InConcurrentTransaction => ConcTx is not null;

    internal void StartConcurrent(bool snapshotIsolation)
    {
        var t = new ConcTrace();
        ConcTx = t;
        ConcSerializable = !snapshotIsolation;
        ConcDoomed = false;
        _ctx!.Trace = t;
        _pager.Trace = t;
        _pager.ConcurrentReplay = ReplayStep;
    }

    internal void EndConcurrent()
    {
        if (ConcTx is null)
        {
            return;
        }

        _replayer?.Dispose();
        _replayer = null;
        if (_ctx is not null)
        {
            _ctx.Trace = null;
        }

        _pager.Trace = null;
        _pager.ConcurrentReplay = null;
        _pager.ConcurrentAbandoned = false;
        ConcTx.Clear();
        ConcTx = null;
        ConcDoomed = false;
    }

    /// <summary>Marks the open concurrent transaction as one that cannot be replayed row by row (it is then conflict-checked by snapshot).</summary>
    internal void ConcOpaque(string what) => ConcTx?.MarkOpaque(what);

    private PagerStatus ReplayStep()
    {
        ConcTrace t = ConcTx!;
        if (_replayer is null)
        {
            // Cursors of other statements point into pages the rebase dropped.
            ReleaseCursorsOf(null, true);
            _replayer = new ConcReplayer(t, _pager, ConcSerializable);
        }

        BtStatus s = _replayer.Step();
        switch (s)
        {
            case BtStatus.Pending:
                return PagerStatus.Pending;
            case BtStatus.Ok:
                _replayer.Dispose();
                _replayer = null;
                t.Replaying = false;
                return PagerStatus.Ok;
            default:
            {
                ConcConflict? c = _replayer.Conflict;
                bool timedOut = _replayer.TimedOut;
                _replayer.Dispose();
                _replayer = null;
                t.Replaying = false;
                if (timedOut)
                {
                    _pager.ConcurrentConflict = new OneLiteException(ResultCode.BusyTimeout, Pager.Msg(ResultCode.BusyTimeout), "the commit held the write lock longer than ConcurrentCommitHoldLimitMs while replaying; nothing was written");
                }
                else if (c is not null)
                {
                    ConcurrentRowids.NoteCollision(_pager.Identity, c);
                    _pager.ConcurrentConflict = new OneLiteException(ResultCode.BusySnapshot, Pager.Msg(ResultCode.BusySnapshot), "another transaction changed data this one used: " + c.Detail);
                }
                else
                {
                    _pager.ConcurrentConflict = _pager.Error as OneLiteException ?? new OneLiteException(ResultCode.BusySnapshot, Pager.Msg(ResultCode.BusySnapshot), "the transaction could not be replayed");
                }

                return PagerStatus.Error;
            }
        }
    }
}
