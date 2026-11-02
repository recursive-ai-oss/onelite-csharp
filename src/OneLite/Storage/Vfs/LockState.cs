namespace RecursiveAI.OneLite.Storage.Vfs;

/// <summary>The kinds of byte-range lock the operating system is asked for.</summary>
internal enum RangeLockKind : byte
{
    Read,
    Write,
    Unlock,
}

/// <summary>Byte-range locks on one open file at the operating-system level. A backend with no cross-process locking uses <see cref="NoRangeLocks"/>.</summary>
internal interface IRangeLocks
{
    /// <summary>Tries to set (or clear) a lock on <c>[position, position+length)</c> without waiting. Returns false if another process holds a conflicting lock.</summary>
    bool Try(long position, long length, RangeLockKind kind);

    /// <summary>True if another process holds a lock on the range that would stop us from write-locking it. Changes nothing.</summary>
    bool HeldByOther(long position, long length);
}

/// <summary>Range locks that always succeed: the lock state is arbitrated inside this process only.</summary>
internal sealed class NoRangeLocks : IRangeLocks
{
    public static readonly NoRangeLocks Instance = new();

    public bool Try(long position, long length, RangeLockKind kind) => true;

    public bool HeldByOther(long position, long length) => false;
}

/// <summary>One connection's lock level on a file.</summary>
internal sealed class LockHolder
{
    public LockLevel Level;
}

/// <summary>
/// The lock state of one database file in this process, shared by every connection that has the file open. POSIX record locks belong to the
/// process, not to the connection, so two connections in one process never conflict at the operating-system level; this class arbitrates
/// between them and takes OS locks only on behalf of the process as a whole (the same arrangement lockingv3 and howtocorrupt §2.2 imply).
/// The byte ranges are those of fileformat2: the lock-byte page starts at 1 GiB with the PENDING byte, then RESERVED, then 510 SHARED bytes.
/// </summary>
internal sealed class FileLockState
{
    internal const long PendingByte = 0x40000000L;
    internal const long ReservedByte = PendingByte + 1;
    internal const long SharedFirst = PendingByte + 2;
    internal const long SharedSize = 510;

    private readonly object _gate = new();
    private readonly IRangeLocks _os;
    private int _shared;
    private LockHolder? _reserved;
    private LockHolder? _pending;
    private LockHolder? _exclusive;

    public FileLockState(IRangeLocks os)
    {
        _os = os;
    }

    /// <summary>The process-wide count of connections at SHARED or above (for tests and diagnostics).</summary>
    public int SharedCount
    {
        get
        {
            lock (_gate)
            {
                return _shared;
            }
        }
    }

    /// <summary>lockingv3 §3.0-§4.0: move <paramref name="h"/> up to <paramref name="to"/>, or report BUSY. A failed move to EXCLUSIVE can leave <paramref name="h"/> at PENDING.</summary>
    public bool TryLock(LockHolder h, LockLevel to)
    {
        lock (_gate)
        {
            if (h.Level >= to)
            {
                return true;
            }

            switch (to)
            {
                case LockLevel.Shared:
                    // A writer waiting for the readers to leave (PENDING) or writing (EXCLUSIVE) admits no new readers.
                    if (_pending != null || _exclusive != null)
                    {
                        return false;
                    }

                    if (_shared == 0)
                    {
                        // lockingv3 §3.0: take a shared lock on PENDING, then one on SHARED, then drop the PENDING one. Seeing PENDING free proves no writer is queued.
                        if (!_os.Try(PendingByte, 1, RangeLockKind.Read))
                        {
                            return false;
                        }

                        bool got = _os.Try(SharedFirst, SharedSize, RangeLockKind.Read);
                        _os.Try(PendingByte, 1, RangeLockKind.Unlock);
                        if (!got)
                        {
                            return false;
                        }
                    }

                    _shared++;
                    h.Level = LockLevel.Shared;
                    return true;

                case LockLevel.Reserved:
                    if (_reserved != null || _pending != null || _exclusive != null)
                    {
                        return false;
                    }

                    if (!_os.Try(ReservedByte, 1, RangeLockKind.Write))
                    {
                        return false;
                    }

                    _reserved = h;
                    h.Level = LockLevel.Reserved;
                    return true;

                default:
                    return TryPendingOrExclusive(h, to);
            }
        }
    }

    private bool TryPendingOrExclusive(LockHolder h, LockLevel to)
    {
        if ((_pending != null && _pending != h) || (_exclusive != null && _exclusive != h))
        {
            return false;
        }

        if (h.Level < LockLevel.Reserved && _reserved != null)
        {
            return false; // somebody else is the writer
        }

        if (h.Level < LockLevel.Pending)
        {
            if (!_os.Try(PendingByte, 1, RangeLockKind.Write))
            {
                return false;
            }

            _pending = h;
            h.Level = LockLevel.Pending;
        }

        if (to == LockLevel.Pending)
        {
            return true;
        }

        // lockingv3 §4.0: EXCLUSIVE needs every other reader gone. Other connections of this process count, the OS cannot see them.
        if (_shared > 1)
        {
            return false;
        }

        if (!_os.Try(SharedFirst, SharedSize, RangeLockKind.Write))
        {
            return false;
        }

        _exclusive = h;
        h.Level = LockLevel.Exclusive;
        return true;
    }

    /// <summary>Lower <paramref name="h"/> to <paramref name="to"/> (SHARED or NONE).</summary>
    public void Unlock(LockHolder h, LockLevel to)
    {
        lock (_gate)
        {
            if (h.Level <= to)
            {
                return;
            }

            if (h.Level > LockLevel.Shared)
            {
                if (h.Level == LockLevel.Exclusive && to == LockLevel.Shared)
                {
                    // Keep reading: turn the exclusive range lock back into a shared one before letting go of PENDING.
                    _os.Try(SharedFirst, SharedSize, RangeLockKind.Read);
                }

                if (_reserved == h)
                {
                    _os.Try(ReservedByte, 1, RangeLockKind.Unlock);
                    _reserved = null;
                }

                if (_pending == h)
                {
                    _os.Try(PendingByte, 1, RangeLockKind.Unlock);
                    _pending = null;
                }

                if (_exclusive == h)
                {
                    _exclusive = null;
                }

                h.Level = LockLevel.Shared;
            }

            if (to == LockLevel.None)
            {
                _shared--;
                if (_shared == 0)
                {
                    _os.Try(SharedFirst, SharedSize, RangeLockKind.Unlock);
                }

                h.Level = LockLevel.None;
            }
        }
    }

    /// <summary>True if any connection holds RESERVED or higher. Other processes are found by trying the RESERVED byte and letting go at once.</summary>
    public bool CheckReserved()
    {
        lock (_gate)
        {
            if (_reserved != null || _pending != null || _exclusive != null)
            {
                return true;
            }

            // Other processes: ask the operating system without taking anything (F_GETLK), as lockingv3 allows for the hot-journal check.
            return _os.HeldByOther(ReservedByte, 1);
        }
    }
}

/// <summary>
/// The lock slots of one <c>-shm</c> file in this process (walformat: eight slots; a slot is held shared by many or exclusively by one).
/// As with <see cref="FileLockState"/>, the OS lock is taken for the process and this class arbitrates between connections.
/// </summary>
internal sealed class ShmLockState
{
    /// <summary>The byte of the first slot in the shared-memory file; slot <c>i</c> is the byte at <c>FirstSlotByte + i</c>.</summary>
    internal const long FirstSlotByte = 120;

    private readonly object _gate = new();
    private readonly IRangeLocks _os;
    private readonly int[] _shared = new int[IVfsShm.LockSlots];
    private readonly ShmHolder?[] _exclusive = new ShmHolder?[IVfsShm.LockSlots];

    public ShmLockState(IRangeLocks os)
    {
        _os = os;
    }

    public bool TryLock(ShmHolder h, int offset, int count, ShmLockOptions flags)
    {
        if (offset < 0 || count < 1 || offset + count > IVfsShm.LockSlots)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Lock slots are 0 to 7.");
        }

        bool exclusive = (flags & ShmLockOptions.Exclusive) != 0;
        lock (_gate)
        {
            if ((flags & ShmLockOptions.Unlock) != 0)
            {
                for (int i = offset; i < offset + count; i++)
                {
                    ReleaseSlot(h, i, exclusive);
                }

                return true;
            }

            for (int i = offset; i < offset + count; i++)
            {
                if (!AcquireSlot(h, i, exclusive))
                {
                    for (int j = offset; j < i; j++)
                    {
                        ReleaseSlot(h, j, exclusive);
                    }

                    return false;
                }
            }

            return true;
        }
    }

    private bool AcquireSlot(ShmHolder h, int i, bool exclusive)
    {
        if (exclusive)
        {
            if (_shared[i] > 0 || _exclusive[i] != null)
            {
                return false;
            }

            if (!_os.Try(FirstSlotByte + i, 1, RangeLockKind.Write))
            {
                return false;
            }

            _exclusive[i] = h;
            h.Exclusive |= 1 << i;
            return true;
        }

        if (_exclusive[i] != null)
        {
            return false;
        }

        if (_shared[i] == 0 && !_os.Try(FirstSlotByte + i, 1, RangeLockKind.Read))
        {
            return false;
        }

        _shared[i]++;
        h.Shared[i]++;
        return true;
    }

    private void ReleaseSlot(ShmHolder h, int i, bool exclusive)
    {
        if (exclusive)
        {
            if (_exclusive[i] == h)
            {
                _exclusive[i] = null;
                h.Exclusive &= ~(1 << i);
                _os.Try(FirstSlotByte + i, 1, RangeLockKind.Unlock);
            }

            return;
        }

        if (h.Shared[i] > 0)
        {
            h.Shared[i]--;
            if (--_shared[i] == 0)
            {
                _os.Try(FirstSlotByte + i, 1, RangeLockKind.Unlock);
            }
        }
    }

    /// <summary>Drops everything <paramref name="h"/> still holds (when it unmaps).</summary>
    public void ReleaseAll(ShmHolder h)
    {
        lock (_gate)
        {
            for (int i = 0; i < IVfsShm.LockSlots; i++)
            {
                while (h.Shared[i] > 0)
                {
                    ReleaseSlot(h, i, false);
                }

                ReleaseSlot(h, i, true);
            }
        }
    }
}

/// <summary>One connection's holds on the shared-memory lock slots.</summary>
internal sealed class ShmHolder
{
    public readonly int[] Shared = new int[IVfsShm.LockSlots];
    public int Exclusive;
}
