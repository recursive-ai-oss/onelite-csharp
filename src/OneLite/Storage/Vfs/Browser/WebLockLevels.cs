namespace RecursiveAI.OneLite.Storage.Vfs.Browser;

/// <summary>
/// Named try-locks with shared and exclusive modes and no waiting: the shape of the Web Locks API (<c>navigator.locks.request</c> with
/// <c>{ mode, ifAvailable: true }</c>). The lock levels of lockingv3 are built on this and nothing else, so they can be checked off the browser
/// against <see cref="LocalLockTable"/>.
/// </summary>
internal interface IWebLockProvider
{
    /// <summary>Takes <paramref name="name"/> in the given mode if that is possible at once. Returns a handle greater than zero, or 0 when another holder prevents it.</summary>
    ValueTask<int> TryAcquireAsync(string name, bool exclusive);

    /// <summary>Releases a handle and completes when the lock can be taken again by anyone.</summary>
    ValueTask ReleaseAsync(int handle);

    /// <summary>Starts releasing a handle without waiting for it to complete. For the synchronous close path, which cannot await.</summary>
    void ReleaseNow(int handle);
}

/// <summary>An in-process lock table with the Web Locks rules for <c>ifAvailable</c> requests: exclusive excludes everyone, shared admits other shared. Completes synchronously.</summary>
internal sealed class LocalLockTable : IWebLockProvider
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (int Shared, bool Exclusive)> _state = new(StringComparer.Ordinal);
    private readonly Dictionary<int, (string Name, bool Exclusive)> _handles = [];
    private int _next = 1;

    public ValueTask<int> TryAcquireAsync(string name, bool exclusive)
    {
        lock (_gate)
        {
            (int shared, bool excl) = _state.GetValueOrDefault(name);
            if (excl || (exclusive && shared > 0))
            {
                return new ValueTask<int>(0);
            }

            _state[name] = exclusive ? (0, true) : (shared + 1, false);
            int id = _next++;
            _handles[id] = (name, exclusive);
            return new ValueTask<int>(id);
        }
    }

    public ValueTask ReleaseAsync(int handle)
    {
        ReleaseNow(handle);
        return default;
    }

    public void ReleaseNow(int handle)
    {
        lock (_gate)
        {
            if (!_handles.Remove(handle, out (string Name, bool Exclusive) h))
            {
                return;
            }

            (int shared, _) = _state[h.Name];
            if (h.Exclusive || shared <= 1)
            {
                _state.Remove(h.Name);
            }
            else
            {
                _state[h.Name] = (shared - 1, false);
            }
        }
    }
}

/// <summary>
/// The five lock levels of one connection to one database file (lockingv3 §2.0), built from three named locks: PENDING, RESERVED and SHARED.
/// They stand for the three byte ranges lockingv3 locks in the file, and the moves are the table in that document:
/// <list type="bullet">
/// <item>None to Shared: shared on PENDING, shared on SHARED, then let PENDING go. A writer holding PENDING therefore keeps new readers out.</item>
/// <item>Shared to Reserved: exclusive on RESERVED.</item>
/// <item>to Pending: exclusive on PENDING.</item>
/// <item>Pending to Exclusive: exclusive on SHARED. A Web Lock cannot be upgraded, so the holder's own shared hold is released first and taken back if the exclusive request is refused; the failed move leaves the holder at Pending, as lockingv3 §4.0 requires.</item>
/// </list>
/// Connections in one page are different holders to the Web Locks manager, so they exclude each other exactly as connections in different tabs do.
/// </summary>
internal sealed class LevelLocks
{
    private readonly IWebLockProvider _locks;
    private readonly string _pending, _reserved, _shared;
    private int _p, _r, _s;

    public LevelLocks(IWebLockProvider locks, string key)
    {
        _locks = locks;
        _pending = key + "\0pending";
        _reserved = key + "\0reserved";
        _shared = key + "\0shared";
    }

    /// <summary>The level this holder has reached.</summary>
    public LockLevel Level { get; private set; }

    /// <summary>Tries to raise the lock to <paramref name="level"/>. False means another holder prevents it (BUSY); the level is then whatever was reached.</summary>
    public async ValueTask<bool> LockAsync(LockLevel level)
    {
        if (level <= Level)
        {
            return true;
        }

        switch (level)
        {
            case LockLevel.Shared:
                {
                    int p = await _locks.TryAcquireAsync(_pending, false).ConfigureAwait(false);
                    if (p == 0)
                    {
                        return false;
                    }

                    int s = await _locks.TryAcquireAsync(_shared, false).ConfigureAwait(false);
                    await _locks.ReleaseAsync(p).ConfigureAwait(false);
                    if (s == 0)
                    {
                        return false;
                    }

                    _s = s;
                    Level = LockLevel.Shared;
                    return true;
                }

            case LockLevel.Reserved:
                {
                    if (Level < LockLevel.Shared)
                    {
                        throw new VfsException(VfsCodes.Misuse, "RESERVED needs SHARED first");
                    }

                    int r = await _locks.TryAcquireAsync(_reserved, true).ConfigureAwait(false);
                    if (r == 0)
                    {
                        return false;
                    }

                    _r = r;
                    Level = LockLevel.Reserved;
                    return true;
                }

            default:
                {
                    if (Level < LockLevel.Shared)
                    {
                        throw new VfsException(VfsCodes.Misuse, "PENDING and EXCLUSIVE need SHARED first");
                    }

                    if (Level < LockLevel.Pending)
                    {
                        int p = await _locks.TryAcquireAsync(_pending, true).ConfigureAwait(false);
                        if (p == 0)
                        {
                            return false;
                        }

                        _p = p;
                        Level = LockLevel.Pending;
                    }

                    if (level == LockLevel.Pending)
                    {
                        return true;
                    }

                    await _locks.ReleaseAsync(_s).ConfigureAwait(false);
                    _s = 0;
                    int x = await _locks.TryAcquireAsync(_shared, true).ConfigureAwait(false);
                    if (x == 0)
                    {
                        // Back to reading. PENDING is ours, so no new reader or writer can have taken SHARED in between.
                        _s = await _locks.TryAcquireAsync(_shared, false).ConfigureAwait(false);
                        if (_s == 0)
                        {
                            throw new VfsException(VfsCodes.IoErrLock, "lost the shared lock while waiting for the readers to leave");
                        }

                        return false;
                    }

                    _s = x;
                    Level = LockLevel.Exclusive;
                    return true;
                }
        }
    }

    /// <summary>Lowers the lock to None or Shared. Never fails with BUSY.</summary>
    public async ValueTask UnlockAsync(LockLevel level)
    {
        if (level > LockLevel.Shared)
        {
            throw new VfsException(VfsCodes.Misuse, "a lock can only be lowered to SHARED or NONE");
        }

        if (Level <= level)
        {
            return;
        }

        if (level == LockLevel.None)
        {
            await ReleaseAsync(ref _r).ConfigureAwait(false);
            await ReleaseAsync(ref _p).ConfigureAwait(false);
            await ReleaseAsync(ref _s).ConfigureAwait(false);
            Level = LockLevel.None;
            return;
        }

        if (Level == LockLevel.Exclusive)
        {
            // lockingv3 §4.0: keep reading. Swap the exclusive hold for a shared one before PENDING goes.
            await ReleaseAsync(ref _s).ConfigureAwait(false);
            _s = await _locks.TryAcquireAsync(_shared, false).ConfigureAwait(false);
            if (_s == 0)
            {
                throw new VfsException(VfsCodes.IoErrUnlock, "could not turn the exclusive lock into a shared one");
            }
        }

        await ReleaseAsync(ref _r).ConfigureAwait(false);
        await ReleaseAsync(ref _p).ConfigureAwait(false);
        Level = LockLevel.Shared;
    }

    /// <summary>Starts releasing every hold without waiting. For closing a file from synchronous code.</summary>
    public void ReleaseAllNow()
    {
        foreach (int h in (ReadOnlySpan<int>)[_r, _p, _s])
        {
            if (h != 0)
            {
                _locks.ReleaseNow(h);
            }
        }

        _r = _p = _s = 0;
        Level = LockLevel.None;
    }

    /// <summary>True if this holder or another has RESERVED or higher.</summary>
    public async ValueTask<bool> CheckReservedAsync()
    {
        if (Level >= LockLevel.Reserved)
        {
            return true;
        }

        int r = await _locks.TryAcquireAsync(_reserved, true).ConfigureAwait(false);
        if (r == 0)
        {
            return true;
        }

        await _locks.ReleaseAsync(r).ConfigureAwait(false);
        return false;
    }

    private ValueTask ReleaseAsync(ref int handle)
    {
        int h = handle;
        handle = 0;
        return h == 0 ? default : _locks.ReleaseAsync(h);
    }
}
