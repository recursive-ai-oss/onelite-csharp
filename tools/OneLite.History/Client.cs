using System;
using System.Collections.Generic;

namespace RecursiveAI.OneLite.History;

/// <summary>How an operation or transaction step ended.</summary>
public enum OpStatus
{
    /// <summary>It worked.</summary>
    Ok,

    /// <summary>The transaction definitely did not and will not commit. The client has already rolled it back.</summary>
    Fail,

    /// <summary>The client cannot tell whether the transaction committed (for example an I/O error during commit).</summary>
    Info,
}

/// <summary>
/// One database client: it runs one transaction at a time. After any step returns <see cref="OpStatus.Fail"/> or
/// <see cref="OpStatus.Info"/> the transaction is over and the client is ready for <see cref="Begin"/> again.
/// A client holds its own connection and is used from one thread.
/// </summary>
public interface IHistoryClient : IDisposable
{
    /// <summary>Starts a transaction.</summary>
    OpStatus Begin();

    /// <summary>Reads the whole list stored under <paramref name="key"/> (empty if the key was never appended to).</summary>
    OpStatus ReadList(int key, out int[] list);

    /// <summary>Reads the register under <paramref name="key"/> (0 if never written).</summary>
    OpStatus ReadRegister(int key, out int value);

    /// <summary>Writes the register under <paramref name="key"/>.</summary>
    OpStatus Write(int key, int value);

    /// <summary>Appends <paramref name="value"/> to the list under <paramref name="key"/>.</summary>
    OpStatus Append(int key, int value);

    /// <summary>Commits. Ok means committed; Fail means rolled back; Info means unknown.</summary>
    OpStatus Commit();

    /// <summary>Rolls back a transaction the workload decided to abandon. Never fails the caller.</summary>
    void Abort();
}

/// <summary>Creates clients, one per worker.</summary>
public interface IHistoryClientFactory
{
    /// <summary>One-time set-up before any client exists (create tables, pick the journal mode).</summary>
    void Prepare();

    /// <summary>Creates the client for worker <paramref name="worker"/>.</summary>
    IHistoryClient Create(int worker);
}

/// <summary>
/// A client over an in-process store guarded by one lock held for the whole transaction. It is strictly serializable by construction,
/// so it is the baseline for checker performance runs and for the runner's own tests.
/// </summary>
public sealed class MemoryClientFactory : IHistoryClientFactory
{
    private readonly object _lock = new();
    private readonly Dictionary<int, List<int>> _lists = [];
    private readonly Dictionary<int, int> _regs = [];

    /// <inheritdoc/>
    public void Prepare()
    {
    }

    /// <inheritdoc/>
    public IHistoryClient Create(int worker) => new Client(this);

    private sealed class Client : IHistoryClient
    {
        private readonly MemoryClientFactory _f;
        private bool _held;

        public Client(MemoryClientFactory f) => _f = f;

        public OpStatus Begin()
        {
            System.Threading.Monitor.Enter(_f._lock);
            _held = true;
            return OpStatus.Ok;
        }

        public OpStatus ReadList(int key, out int[] list)
        {
            list = _f._lists.TryGetValue(key, out var l) ? l.ToArray() : [];
            return OpStatus.Ok;
        }

        public OpStatus ReadRegister(int key, out int value)
        {
            value = _f._regs.GetValueOrDefault(key);
            return OpStatus.Ok;
        }

        public OpStatus Write(int key, int value)
        {
            _f._regs[key] = value;
            return OpStatus.Ok;
        }

        public OpStatus Append(int key, int value)
        {
            if (!_f._lists.TryGetValue(key, out var l))
            {
                l = [];
                _f._lists[key] = l;
            }

            l.Add(value);
            return OpStatus.Ok;
        }

        public OpStatus Commit()
        {
            Release();
            return OpStatus.Ok;
        }

        public void Abort() => Release();

        public void Dispose() => Release();

        private void Release()
        {
            if (_held)
            {
                _held = false;
                System.Threading.Monitor.Exit(_f._lock);
            }
        }
    }
}
