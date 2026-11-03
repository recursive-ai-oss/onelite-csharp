namespace RecursiveAI.OneLite.Storage.Wal;

/// <summary>
/// The state connections of one process share for one wal-index so that their concurrent commits can share a WAL sync (docs/wal.md, group commit).
/// A <em>generation</em> starts when a committer takes the WAL write lock (the <em>anchor</em>) and ends when the anchor gives it back. In between the lock
/// belongs to the generation: committers of this process (<em>followers</em>) take turns appending their frames after the previous commit's frames, whether or
/// not those are synced and published yet, and the next sync covers every commit appended before it began. The wal-index header is written only after
/// the sync, so no reader of any process sees a commit that is not on disk. Other processes keep seeing a held write lock, as they do today.
/// </summary>
internal sealed class WalGroup
{
    private static readonly object RegistryGate = new();
    private static readonly Dictionary<Memory<byte>, WeakReference<WalGroup>> Registry = [];
    private static int _sinceSweep;

    private TaskCompletionSource _wake = NewWake();
    private bool _waited;

    private WalGroup(Memory<byte> key)
    {
        Key = key;
    }

    /// <summary>The first region of the wal-index this group belongs to (kept so the registry entry lives as long as the group).</summary>
    public Memory<byte> Key { get; }

    /// <summary>Guards every field below. Never held across I/O.</summary>
    public readonly object Gate = new();

    /// <summary>Counts generations, so that a late release of an old one cannot close a new one.</summary>
    public long Generation;

    /// <summary>True from the moment the anchor holds the write lock until it gives it back.</summary>
    public bool Open;

    /// <summary>True once the anchor is leaving: no new follower may join; a new generation starts after the anchor releases.</summary>
    public bool Closing;

    /// <summary>The number of committers waiting for a turn (or for the next generation).</summary>
    public int Waiters;

    /// <summary>The number of syncs run in this generation.</summary>
    public int Syncs;

    /// <summary>Counts syncs for ever (not reset per generation).</summary>
    public long SyncSeq;

    /// <summary>How long the last sync took, in <see cref="System.Diagnostics.Stopwatch"/> ticks.</summary>
    public long LastSyncTicks;

    /// <summary>Stopwatch timestamp before which a new generation does not take the write lock (a gap for other writers).</summary>
    public long NoAnchorBefore;

    /// <summary>For each committer that appended recently, the value of <see cref="SyncSeq"/> when it last did: the writers a sync may expect to cover.</summary>
    public readonly Dictionary<int, long> Active = [];

    /// <summary>How many committers appended within the last few syncs (prunes older entries). Call under <see cref="Gate"/>.</summary>
    public int ActiveCount()
    {
        int n = 0;
        List<int>? old = null;
        foreach (KeyValuePair<int, long> e in Active)
        {
            long age = SyncSeq - e.Value;
            if (age <= 2)
            {
                n++;
            }
            else if (age > 16)
            {
                (old ??= []).Add(e.Key);
            }
        }

        if (old != null)
        {
            foreach (int k in old)
            {
                Active.Remove(k);
            }
        }

        return n;
    }

    /// <summary>True while one committer is replaying and appending (it owns the end of the log).</summary>
    public bool BatonHeld;

    /// <summary>True while one committer is syncing the log.</summary>
    public bool Syncing;

    /// <summary>True after a sync failed: every commit not yet acknowledged fails, and the generation ends.</summary>
    public bool Failed;

    /// <summary>
    /// For each generation in which a sync failed: the value of <see cref="AckedTicket"/> when it did. A commit of that generation with a larger ticket failed,
    /// even if a later generation's sync moves <see cref="AckedTicket"/> past it (its frames were cut off the log). Call under <see cref="Gate"/>.
    /// </summary>
    public readonly Dictionary<long, long> FailedAt = [];

    /// <summary>The header describing the log with every commit appended so far, published or not.</summary>
    public WalIndexHeader Tail;

    /// <summary>The header last written to the wal-index.</summary>
    public WalIndexHeader Published;

    /// <summary>The number of commits appended in this process, counted up for ever.</summary>
    public long TailTicket;

    /// <summary>Every commit with a ticket up to this one is synced and published.</summary>
    public long AckedTicket;

    /// <summary>The group for the wal-index whose first region is <paramref name="region0"/>.</summary>
    public static WalGroup For(Memory<byte> region0)
    {
        lock (RegistryGate)
        {
            if (Registry.TryGetValue(region0, out WeakReference<WalGroup>? w) && w.TryGetTarget(out WalGroup? g))
            {
                return g;
            }

            if (++_sinceSweep >= 64)
            {
                _sinceSweep = 0;
                List<Memory<byte>>? dead = null;
                foreach (KeyValuePair<Memory<byte>, WeakReference<WalGroup>> e in Registry)
                {
                    if (!e.Value.TryGetTarget(out _))
                    {
                        (dead ??= []).Add(e.Key);
                    }
                }

                if (dead != null)
                {
                    foreach (Memory<byte> k in dead)
                    {
                        Registry.Remove(k);
                    }
                }
            }

            g = new WalGroup(region0);
            Registry[region0] = new WeakReference<WalGroup>(g);
            return g;
        }
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A task that completes at the next <see cref="Wake"/>. Take it under <see cref="Gate"/> while checking the state it waits on; wait outside.</summary>
    public Task WaitTask
    {
        get
        {
            _waited = true;
            return _wake.Task;
        }
    }

    /// <summary>Wakes everybody waiting on <see cref="WaitTask"/>. Call under <see cref="Gate"/> after a state change.</summary>
    public void Wake()
    {
        if (!_waited)
        {
            return;
        }

        TaskCompletionSource old = _wake;
        _wake = NewWake();
        _waited = false;
        old.TrySetResult();
    }
}
