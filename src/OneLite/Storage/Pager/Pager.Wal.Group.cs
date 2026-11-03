using System.Diagnostics;
using RecursiveAI.OneLite.Storage.Vfs;
using RecursiveAI.OneLite.Storage.Wal;

namespace RecursiveAI.OneLite.Storage.Pager;

// Group commit for concurrent transactions (docs/wal.md, "Group commit"). The shared state is a WalGroup per wal-index; this file holds a pager's side of it.
//
// A concurrent commit under synchronous=FULL no longer holds the write lock through its own sync alone. The first committer to get the lock (the anchor) keeps it
// for a generation. Committers of this process (followers) take turns, one at a time, to replay and append their frames behind the previous commit's frames. Each
// appended commit then waits for a sync that began after its frames were written; whoever finds no sync running and commits waiting runs one, publishes the
// wal-index header for everything it covered, and wakes the rest. Nothing is published before it is synced, so a reader never sees a commit that a crash could
// take back, and the anchor keeps the lock until every commit of the generation is published, so no other process appends over unpublished frames.
public sealed partial class Pager
{
    private enum GroupRole : byte
    {
        None,
        Anchor,
        Follower,
    }

    private WalGroup? _grp;
    private GroupRole _grpRole;
    private bool _grpBaton;
    private long _grpTicket;
    private long _grpGeneration;
    private long _grpTicketGeneration;
    private static int s_grpIds;
    private readonly int _grpId = Interlocked.Increment(ref s_grpIds);

    // A sync waits up to this long for the committers that appended during the last few syncs to append again, when syncs are slow enough for that to pay.
    // Without it the writers fall into two groups that take turns, each sync covering half of them. Early wake-ups end the wait as soon as they have all arrived.
    private const int GroupGatherMaxMs = 1;

    // How many syncs a generation may run before its anchor leaves, so that the anchor's own COMMIT returns in bounded time under a steady stream of commits.
    private const int GroupMaxSyncs = 2;

    private bool GroupEligible =>
        _opts.GroupCommit && _sync >= SynchronousMode.Full && _wal is { ReadOnly: false } w && !w.Index.IsHeap && w.Index.HasRegion(0);

    private static IoRequest GroupWaitRq(Task wait, bool cancellable) =>
        new() { Kind = IoKind.GroupWait, Phase = ResumeState.WalGroupWait, Wait = wait, Cancellable = cancellable };

    // Takes the write lock for a concurrent commit, or joins the generation that holds it. Sets _ccStale: whether the transaction's snapshot is behind the log
    // (counting commits that are appended but not yet published).
    private IEnumerable<IoRequest> ConcGroupLockOp()
    {
        WalConnection w = _wal!;
        WalGroup g = w.Index.Group;
        int tries = 0;
        long spinStart = 0;
        long spinBudget = (_opts.BusySpinBudgetUs?.Invoke() ?? 0) * Stopwatch.Frequency / 1_000_000;
        WalIndexHeader tail = default;
        for (; ; )
        {
            Task? wait = null;
            bool joined = false;
            lock (g.Gate)
            {
                if (g.Open)
                {
                    if (!g.Closing && !g.BatonHeld)
                    {
                        g.BatonHeld = true;
                        tail = g.Tail;
                        joined = true;
                    }
                    else
                    {
                        wait = g.WaitTask;
                        g.Waiters++;
                    }
                }
            }

            if (joined)
            {
                _grp = g;
                _grpRole = GroupRole.Follower;
                _grpBaton = true;
                _ccStale = tail != w.Snap;
                if (!_ccStale)
                {
                    w.Hdr = tail;
                }

                break;
            }

            if (wait != null)
            {
                bool counted = true;
                try
                {
                    yield return GroupWaitRq(wait, cancellable: true);
                }
                finally
                {
                    // Also runs when the wait is abandoned.
                    if (counted)
                    {
                        lock (g.Gate)
                        {
                            g.Waiters--;
                        }
                    }
                }

                counted = false;
                if (_comp.Status != 0)
                {
                    WFail(_comp.Status, "waiting for the group commit failed");
                    yield break;
                }

                continue;
            }

            // After a generation the write lock stays free for a moment, so that a plain writer (or another process) polling for it can get in; without the gap
            // a steady stream of grouped commits would hand the lock from one generation to the next and starve them.
            long coolTicks = g.NoAnchorBefore - Stopwatch.GetTimestamp();
            if (coolTicks > 0)
            {
                int coolMs = (int)Math.Max(1, coolTicks * 1000 / Stopwatch.Frequency);
                yield return GroupWaitRq(Task.Delay(coolMs), cancellable: true);
                if (_comp.Status != 0)
                {
                    WFail(_comp.Status, "waiting for the group commit failed");
                    yield break;
                }

                continue;
            }

            foreach (IoRequest r in WalLock(WalIndex.WriteLock, 1, true, false))
            {
                yield return r;
            }

            if (_wFailCode != 0)
            {
                yield break;
            }

            if (_wGot)
            {
                if (w.Index.TryReadHeader(out WalIndexHeader cur))
                {
                    lock (g.Gate)
                    {
                        _grpGeneration = ++g.Generation;
                        g.Open = true;
                        g.Closing = false;
                        g.Failed = false;
                        g.Syncing = false;
                        g.Syncs = 0;
                        g.BatonHeld = true;
                        g.Tail = cur;
                        g.Published = cur;
                    }

                    tail = cur;
                    _grp = g;
                    _grpRole = GroupRole.Anchor;
                    _grpBaton = true;
                    _ccStale = cur != w.Snap;
                    if (!_ccStale)
                    {
                        w.Hdr = cur;
                    }
                }
                else
                {
                    // The wal-index needs recovery: take the ordinary path, which rebases and recovers, without a group.
                    _ccStale = true;
                }

                break;
            }

            // The lock is busy: a plain writer or another process holds it. Spin briefly (the holder is usually about to let go), then ask the busy policy.
            if (spinBudget > 0)
            {
                long now = Stopwatch.GetTimestamp();
                if (spinStart == 0)
                {
                    spinStart = now;
                }

                if (now - spinStart < spinBudget)
                {
                    yield return Rq(ResumeState.WalSleep, IoKind.Sleep, length: 0, cancellable: true);
                    continue;
                }

                spinBudget = 0;
                _opts.BusySpun?.Invoke((int)((now - spinStart) * 1000 / Stopwatch.Frequency) + 1);
            }

            int ms = _opts.Busy?.Invoke(tries++) ?? -1;
            if (ms < 0)
            {
                WFail(ResultCode.Busy, "the write lock of the WAL is held by another connection");
                yield break;
            }

            yield return Rq(ResumeState.WalSleep, IoKind.Sleep, length: ms, cancellable: true);
        }

        // From here the lock (or the turn) belongs to the transaction: a failure further on releases it in Rollback.
        w.Writing = true;
        w.RestartChecked = false;
        w.Restarted = false;
        ConcurrentHoldDeadline = _opts.ConcurrentCommitHoldLimitMs > 0
            ? Stopwatch.GetTimestamp() + (_opts.ConcurrentCommitHoldLimitMs * Stopwatch.Frequency / 1000)
            : 0;
        if (_grpRole != GroupRole.None)
        {
            // The log may have grown past the regions this connection has mapped, by commits that are not published yet.
            uint mx = tail.MxFrame;
            for (int t = 0; t < WalIndex.RegionsFor(mx); t++)
            {
                foreach (IoRequest r in WalMap(t))
                {
                    yield return r;
                }

                if (_wFailCode != 0)
                {
                    GroupAbandonSync();
                    yield break;
                }
            }
        }
    }

    // After a rebase onto the published snapshot: a follower (or an anchor) works on the log including commits that are appended but not yet published.
    private void GroupAdoptTail()
    {
        if (_grp == null || _grpRole == GroupRole.None)
        {
            return;
        }

        WalConnection w = _wal!;
        WalIndexHeader tail;
        lock (_grp.Gate)
        {
            tail = _grp.Tail;
        }

        if (tail != w.Snap)
        {
            w.Snap = tail;
        }

        w.Hdr = w.Snap;

        // Pages cached under an older snapshot may be stale against the commits that are only appended so far.
        if (!_walLastSnapValid || _walLastSnap != w.Snap)
        {
            DropCleanFrames();
            if (_walLastSnapValid)
            {
                ForeignChanges++;
            }

            _walLastSnap = w.Snap;
            _walLastSnapValid = true;
        }
    }

    // Called with this commit's frames written and w.Hdr describing the log up to and including them. Gives up the turn and waits until a sync that covers
    // the frames has published them. Runs the sync itself if nobody is.
    private IEnumerable<IoRequest> GroupAckOp()
    {
        WalConnection w = _wal!;
        WalGroup g = _grp!;
        lock (g.Gate)
        {
            _grpTicket = ++g.TailTicket;
            _grpTicketGeneration = g.Generation;
            g.Tail = w.Hdr;
            g.Active[_grpId] = g.SyncSeq;
            g.BatonHeld = false;
            _grpBaton = false;
            g.Wake();
        }

        long gatherDeadline = 0;
        for (; ; )
        {
            Task? wait = null;
            int waitMs = 0;
            bool sync = false;
            bool failed = false;
            WalIndexHeader target = default;
            long targetTicket = 0;
            lock (g.Gate)
            {
                // A sync that failed in this commit's generation fails it, even when a later generation has since synced past its ticket: the anchor
                // that left a failed generation cut these frames off the log.
                bool lost = g.FailedAt.TryGetValue(_grpTicketGeneration, out long ackedAtFailure) && _grpTicket > ackedAtFailure;
                if (!lost && _grpTicket <= g.AckedTicket)
                {
                    break;
                }

                if (lost || g.Failed)
                {
                    failed = true;
                }
                else if (!g.Syncing)
                {
                    // Slow syncs: give the writers that are about to append again a moment, so that one sync covers all of them.
                    long now = Stopwatch.GetTimestamp();
                    if (gatherDeadline == 0 && g.LastSyncTicks * 4 >= Stopwatch.Frequency * GroupGatherMaxMs / 500 && g.TailTicket - g.AckedTicket < g.ActiveCount())
                    {
                        gatherDeadline = now + (Stopwatch.Frequency * GroupGatherMaxMs / 1000);
                    }

                    if (gatherDeadline != 0 && now < gatherDeadline && g.TailTicket - g.AckedTicket < g.ActiveCount())
                    {
                        wait = g.WaitTask;
                        waitMs = Math.Max(1, (int)((gatherDeadline - now) * 1000 / Stopwatch.Frequency));
                    }
                    else
                    {
                        g.Syncing = true;
                        target = g.Tail;
                        targetTicket = g.TailTicket;
                        sync = true;
                    }
                }
                else
                {
                    wait = g.WaitTask;
                }
            }

            if (failed)
            {
                WFail(ResultCode.IoErr, "a sync of the WAL covering this commit failed");
                yield break;
            }

            if (sync)
            {
                foreach (IoRequest r in GroupSyncOp(g, target, targetTicket))
                {
                    yield return r;
                }

                if (_wFailCode != 0)
                {
                    yield break;
                }

                continue;
            }

            Task toWait = wait!;
            IoRequest rq = GroupWaitRq(toWait, cancellable: false);
            rq.Length = waitMs;
            yield return rq;
        }

        w.Snap = w.Hdr;
        w.ReadMx = w.Hdr.MxFrame;
    }

    // One sync of the log for every commit appended up to 'target', then the publication of the header that describes them (the commit point of all of them).
    // Marks the generation failed if the sync fails: the commits not published yet are lost, and each reports the error.
    private IEnumerable<IoRequest> GroupSyncOp(WalGroup g, WalIndexHeader target, long targetTicket)
    {
        WalConnection w = _wal!;
        long began = Stopwatch.GetTimestamp();
        foreach (IoRequest r in WalSyncOp(ResumeState.WalSync))
        {
            yield return r;
        }

        if (_wFailCode != 0)
        {
            lock (g.Gate)
            {
                g.Failed = true;
                g.FailedAt.TryAdd(g.Generation, g.AckedTicket);
                if (g.FailedAt.Count > 256)
                {
                    foreach (long old in new List<long>(g.FailedAt.Keys))
                    {
                        if (old < g.Generation - 4096)
                        {
                            g.FailedAt.Remove(old);
                        }
                    }
                }

                g.Closing = true;
                g.Syncing = false;
                g.Wake();
            }

            yield break;
        }

        lock (g.Gate)
        {
            w.Index.WriteHeader(ref target);
            g.Published = target;
            g.AckedTicket = targetTicket;
            g.Syncing = false;
            g.Syncs++;
            g.SyncSeq++;
            g.LastSyncTicks = Stopwatch.GetTimestamp() - began;
            g.Wake();
        }
    }

    // The anchor leaves: no more followers, finish what was appended, and only then give the write lock back.
    private IEnumerable<IoRequest> GroupDrainOp()
    {
        WalConnection w = _wal!;
        WalGroup g = _grp!;
        lock (g.Gate)
        {
            if (_grpBaton)
            {
                g.BatonHeld = false;
                _grpBaton = false;
            }

            g.Wake();
        }

        for (; ; )
        {
            Task? wait = null;
            bool sync = false;
            WalIndexHeader target = default;
            long targetTicket = 0;
            lock (g.Gate)
            {
                if (g.BatonHeld || g.Syncing)
                {
                    wait = g.WaitTask;
                }
                else if (!g.Failed && g.TailTicket > g.AckedTicket)
                {
                    g.Syncing = true;
                    target = g.Tail;
                    targetTicket = g.TailTicket;
                    sync = true;
                }
                else if (g.Waiters > 0 && g.Syncs < GroupMaxSyncs && !g.Failed)
                {
                    // Committers are queued for a turn: stay for them, so their commits share syncs too, but not for ever.
                    wait = g.WaitTask;
                }
                else
                {
                    g.Closing = true;
                    break;
                }
            }

            if (sync)
            {
                foreach (IoRequest r in GroupSyncOp(g, target, targetTicket))
                {
                    yield return r;
                }

                // A failure belongs to the followers whose commits were in it; the anchor's own commit is already decided.
                _wFailCode = 0;
                _wFailDetail = null;
                continue;
            }

            yield return GroupWaitRq(wait!, cancellable: false);
        }

        bool failed;
        WalIndexHeader published;
        lock (g.Gate)
        {
            failed = g.Failed;
            published = g.Published;
        }

        if (failed)
        {
            // Frames appended after the last published commit are dead: take their hash entries out so the next generation starts clean.
            w.Index.Cleanup(published.MxFrame);
        }
    }

    // The anchor has given the write lock back (or never got far enough to matter): the next committer may take it.
    private void GroupCloseGeneration()
    {
        WalGroup g = _grp!;
        lock (g.Gate)
        {
            if (g.Generation == _grpGeneration)
            {
                g.Open = false;
                g.Closing = false;
                // A gap of an eighth of a sync (at most 2 ms) when syncs are slow enough for it to matter: about 10% of throughput at most.
                if (g.LastSyncTicks >= Stopwatch.Frequency / 1000)
                {
                    g.NoAnchorBefore = Stopwatch.GetTimestamp() + Math.Min(g.LastSyncTicks / 8, Stopwatch.Frequency / 500);
                }
            }

            g.Wake();
        }

        _grp = null;
        _grpRole = GroupRole.None;
    }

    private void GroupLeaveFollower()
    {
        WalGroup g = _grp!;
        lock (g.Gate)
        {
            if (_grpBaton)
            {
                g.BatonHeld = false;
                _grpBaton = false;
            }

            g.Wake();
        }

        _grp = null;
        _grpRole = GroupRole.None;
    }

    // Gives up a turn or a generation that has nothing appended (an abandoned or failed start of a commit), synchronously: releasing never waits.
    private void GroupAbandonSync()
    {
        if (_grp == null || _grpRole == GroupRole.None)
        {
            return;
        }

        WalConnection w = _wal!;
        if (_grpRole == GroupRole.Follower)
        {
            GroupLeaveFollower();
        }
        else
        {
            WalGroup g = _grp;
            lock (g.Gate)
            {
                g.BatonHeld = false;
                _grpBaton = false;
                g.Closing = true;
            }

            try
            {
                if ((_wHeldExcl & (1 << WalIndex.WriteLock)) != 0)
                {
                    w.Index.Shm.Lock(WalIndex.WriteLock, 1, ShmLockOptions.Unlock | ShmLockOptions.Exclusive);
                    _wHeldExcl &= ~(1 << WalIndex.WriteLock);
                }
            }
            catch (VfsException)
            {
                // Unlocking cannot fail in the backends here; if one does, the lock goes with the handle.
            }

            GroupCloseGeneration();
        }

        w.Writing = false;
    }
}
