using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;
using PagerClass = RecursiveAI.OneLite.Storage.Pager.Pager;

namespace RecursiveAI.OneLite.Storage.BTree;

/// <summary>
/// One database on the pager with the B-tree layer attached: the pager, the page source, the B-tree context and the auto-vacuum
/// relocator, wired together. This is the storage engine the SQL layers sit on; it has no SQL of its own. Transactions are the pager's
/// (read, write, commit, rollback). <see cref="Run"/> and <see cref="RunAsync"/> drive any restartable B-tree call to completion, running
/// the pager's outstanding I/O between attempts: inline for the sync driver, awaiting the VFS for the async one.
/// </summary>
public sealed class StorageEngine : IDisposable
{
    private BtContext? _ctx;
    private int _ctxPageSize;
    private int _ctxReserved;
    private TextEncoding _ctxEncoding;

    private StorageEngine(PagerClass pager)
    {
        Pager = pager;
        Source = new PagerPageSource(pager);
    }

    /// <summary>The pager.</summary>
    public PagerClass Pager { get; }

    /// <summary>The B-tree's view of the pager.</summary>
    public PagerPageSource Source { get; }

    /// <summary>
    /// The B-tree context. Valid after the first <see cref="BeginRead"/> (the page size and text encoding come from the file). If the file's
    /// geometry changed between transactions (another process vacuumed it into a different page size), a fresh context is made and trees
    /// built on the old one must be reopened.
    /// </summary>
    public BtContext Context => _ctx ?? throw new InvalidOperationException("the context exists after the first BeginRead");

    /// <summary>Opens (or prepares to create) a database file.</summary>
    /// <param name="vfs">The VFS.</param>
    /// <param name="path">The database path.</param>
    /// <param name="options">Pager options; for a new file they set the page size, encoding and auto-vacuum mode.</param>
    /// <returns>The engine.</returns>
    public static StorageEngine Open(IVfs vfs, string path, PagerOptions? options = null) => new(PagerClass.Open(vfs, path, options));

    /// <summary>Opens a database file, awaiting the VFS.</summary>
    /// <param name="vfs">The VFS.</param>
    /// <param name="path">The database path.</param>
    /// <param name="options">Pager options.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The engine.</returns>
    public static async ValueTask<StorageEngine> OpenAsync(IVfs vfs, string path, PagerOptions? options = null, CancellationToken ct = default) =>
        new(await PagerClass.OpenAsync(vfs, path, options, ct).ConfigureAwait(false));

    /// <summary>Runs <paramref name="op"/> until it stops returning <see cref="BtStatus.Pending"/>, performing the pager's outstanding I/O in between.</summary>
    /// <param name="op">A restartable B-tree or source call; it is repeated unchanged after each Pending.</param>
    /// <returns>Ok or Error (the exception is on the cursor or <see cref="PagerPageSource.LastError"/>).</returns>
    public BtStatus Run(Func<BtStatus> op)
    {
        ArgumentNullException.ThrowIfNull(op);
        for (; ; )
        {
            BtStatus s = op();
            if (s != BtStatus.Pending)
            {
                return s;
            }

            Pager.ExecuteSync();
        }
    }

    /// <summary>The async twin of <see cref="Run"/>.</summary>
    /// <param name="op">The restartable call.</param>
    /// <param name="ct">Cancellation, observed between I/O requests.</param>
    /// <returns>Ok or Error.</returns>
    public async ValueTask<BtStatus> RunAsync(Func<BtStatus> op, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(op);
        for (; ; )
        {
            BtStatus s = op();
            if (s != BtStatus.Pending)
            {
                return s;
            }

            await Pager.ExecuteAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Begins a read transaction (SHARED lock, hot-journal recovery) and makes the context.</summary>
    /// <exception cref="OneLiteException">The pager's error: BUSY, CORRUPT, NOTADB, IOERR, ...</exception>
    public void BeginRead()
    {
        Pager.BeginReadSync();
        EnsureContext();
    }

    /// <summary>The async twin of <see cref="BeginRead"/>.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async ValueTask BeginReadAsync(CancellationToken ct = default)
    {
        await Pager.BeginReadAsync(ct).ConfigureAwait(false);
        EnsureContext();
    }

    /// <summary>Begins a write transaction (RESERVED lock) inside the open read transaction.</summary>
    public void BeginWrite() => Pager.BeginWriteSync();

    /// <summary>Commits the write transaction, including auto-vacuum relocation. Cursors must be closed or saved by the caller.</summary>
    public void Commit() => Pager.CommitSync();

    /// <summary>The async twin of <see cref="Commit"/>.</summary>
    /// <param name="ct">Cancellation (honoured only before the first database write).</param>
    /// <returns>A task.</returns>
    public ValueTask CommitAsync(CancellationToken ct = default) => Pager.CommitAsync(ct);

    /// <summary>The schema cookie (header offset 40) as of the current transaction.</summary>
    /// <returns>The cookie.</returns>
    public uint GetSchemaCookie()
    {
        PageFrame p1 = Pager.GetSync(1);
        try
        {
            return DatabaseHeader.GetUInt32(p1.Data, DatabaseHeader.SchemaCookieOffset);
        }
        finally
        {
            Pager.Release(p1);
        }
    }

    /// <summary>Sets the schema cookie (fileformat2 §1.3.9); other connections reload the schema when it changes. Needs the write transaction.</summary>
    /// <param name="cookie">The new value.</param>
    public void SetSchemaCookie(uint cookie)
    {
        PageFrame p1 = Pager.GetSync(1);
        try
        {
            Pager.MakeWritableSync(p1);
            DatabaseHeader.SetUInt32(p1.Data, DatabaseHeader.SchemaCookieOffset, cookie);
        }
        finally
        {
            Pager.Release(p1);
        }
    }

    /// <summary>Rolls the write transaction back.</summary>
    public void Rollback() => Pager.RollbackSync();

    /// <summary>Ends the read transaction.</summary>
    public void EndRead() => Pager.EndReadSync();

    /// <summary>Closes the pager. A transaction left open is rolled back.</summary>
    public void Dispose() => Pager.Dispose();

    /// <summary>Closes the pager like <see cref="Dispose"/>, awaiting the VFS to release the lock.</summary>
    /// <returns>A task that completes when the files are closed.</returns>
    public ValueTask DisposeAsync() => Pager.DisposeAsync();

    /// <summary>Forwarded to the B-tree context: see the context's Changed member.</summary>
    internal Action<uint, long>? Changed { get; set; }

    /// <summary>
    /// Makes the B-tree context for the geometry (page size, reserved bytes, text encoding) the pager read from page 1, or keeps the current one when
    /// it still fits. A resumable caller that drives <see cref="Pager"/> itself (the SQL engine) calls this after its read transaction began.
    /// </summary>
    public void EnsureContext()
    {
        if (_ctx != null && _ctxPageSize == Pager.PageSize && _ctxReserved == Pager.ReservedBytes && _ctxEncoding == Pager.DatabaseEncoding)
        {
            return;
        }

        _ctx = new BtContext(Source, Pager.DatabaseEncoding) { Changed = Changed };
        _ctxPageSize = Pager.PageSize;
        _ctxReserved = Pager.ReservedBytes;
        _ctxEncoding = Pager.DatabaseEncoding;
        // Only a full auto-vacuum database is compacted at commit; for the others the pager ignores the callback.
        Pager.SetRelocator(new BtRelocator(_ctx));
    }
}
