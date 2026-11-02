using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Storage.Pager;

public sealed partial class Pager
{
    private void RunSync(Func<PagerStatus> op)
    {
        PagerStatus s;
        while ((s = op()) == PagerStatus.Pending)
        {
            ExecuteSync();
        }

        if (s == PagerStatus.Error)
        {
            throw _error!;
        }
    }

    private async ValueTask RunAsync(Func<PagerStatus> op, CancellationToken ct)
    {
        PagerStatus s;
        while ((s = op()) == PagerStatus.Pending)
        {
            await ExecuteAsync(ct).ConfigureAwait(false);
        }

        if (s == PagerStatus.Error)
        {
            throw _error!;
        }
    }

    /// <summary>Starts a read transaction, running any I/O inline. Throws the pager's error.</summary>
    public void BeginReadSync() => RunSync(BeginRead);

    /// <summary>Ends the read transaction, running any I/O inline.</summary>
    public void EndReadSync() => RunSync(EndRead);

    /// <summary>Starts a write transaction, running any I/O inline.</summary>
    public void BeginWriteSync() => RunSync(BeginWrite);

    /// <summary>Commits, running any I/O inline.</summary>
    public void CommitSync() => RunSync(Commit);

    /// <summary>Rolls back, running any I/O inline.</summary>
    public void RollbackSync() => RunSync(Rollback);

    /// <summary>Pins a page, running any I/O inline. Release it with <see cref="Release"/>.</summary>
    /// <param name="pageNumber">The page.</param>
    /// <param name="flags">Options.</param>
    /// <returns>The pinned frame.</returns>
    public PageFrame GetSync(uint pageNumber, GetOptions flags = GetOptions.None)
    {
        PageFrame? f = null;
        RunSync(() => Get(pageNumber, flags, out f));
        return f!;
    }

    /// <summary>Makes a page writable, running any I/O inline.</summary>
    /// <param name="frame">A pinned frame.</param>
    public void MakeWritableSync(PageFrame frame) => RunSync(() => MakeWritable(frame));

    /// <summary>Allocates a page, running any I/O inline.</summary>
    /// <returns>The pinned, zeroed, writable page.</returns>
    public PageFrame AllocateSync()
    {
        PageFrame? f = null;
        RunSync(() => Allocate(out f));
        return f!;
    }

    /// <summary>Frees a page, running any I/O inline.</summary>
    /// <param name="pageNumber">The page.</param>
    public void FreeSync(uint pageNumber) => RunSync(() => Free(pageNumber));

    /// <summary>Rolls back to a savepoint, running any I/O inline.</summary>
    /// <param name="level">The savepoint level.</param>
    public void RollbackSavepointSync(int level) => RunSync(() => RollbackSavepoint(level));

    /// <summary>Starts a read transaction on the async driver.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public ValueTask BeginReadAsync(CancellationToken ct = default) => RunAsync(BeginRead, ct);

    /// <summary>Ends the read transaction on the async driver.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public ValueTask EndReadAsync(CancellationToken ct = default) => RunAsync(EndRead, ct);

    /// <summary>Starts a write transaction on the async driver.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public ValueTask BeginWriteAsync(CancellationToken ct = default) => RunAsync(BeginWrite, ct);

    /// <summary>Commits on the async driver.</summary>
    /// <param name="ct">Cancellation (observed only at points where abandoning is safe).</param>
    /// <returns>A task.</returns>
    public ValueTask CommitAsync(CancellationToken ct = default) => RunAsync(Commit, ct);

    /// <summary>Rolls back on the async driver.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public ValueTask RollbackAsync(CancellationToken ct = default) => RunAsync(Rollback, ct);

    /// <summary>Pins a page on the async driver.</summary>
    /// <param name="pageNumber">The page.</param>
    /// <param name="flags">Options.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The pinned frame.</returns>
    public async ValueTask<PageFrame> GetAsync(uint pageNumber, GetOptions flags = GetOptions.None, CancellationToken ct = default)
    {
        PageFrame? f = null;
        await RunAsync(() => Get(pageNumber, flags, out f), ct).ConfigureAwait(false);
        return f!;
    }

    /// <summary>Makes a page writable on the async driver.</summary>
    /// <param name="frame">A pinned frame.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public ValueTask MakeWritableAsync(PageFrame frame, CancellationToken ct = default) => RunAsync(() => MakeWritable(frame), ct);
}

/// <summary>Commits several pagers (a database and its <c>ATTACH</c>ed databases) atomically with a super-journal (atomiccommit §5).</summary>
public static class MultiCommit
{
    private static bool NeedsSuper(List<Pager> changed)
    {
        // atomiccommit §5.2: with synchronous=OFF or journal_mode=MEMORY/OFF the super-journal is omitted.
        if (changed.Count < 2)
        {
            return false;
        }

        foreach (Pager p in changed)
        {
            if (p.Synchronous == SynchronousMode.Off || p.JournalMode is JournalMode.Memory or JournalMode.Off)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Commits every pager in a write transaction. The first pager is the main database: it names the super-journal. Sequence: write and
    /// sync the super-journal; add its name to each journal and sync each; write and sync each database; delete the super-journal (the commit
    /// point); retire each journal. Runs I/O inline.
    /// </summary>
    /// <param name="pagers">The pagers, main database first.</param>
    public static void CommitSync(IReadOnlyList<Pager> pagers)
    {
        ArgumentNullException.ThrowIfNull(pagers);
        var changed = new List<Pager>();
        foreach (Pager p in pagers)
        {
            if (p.InWriteTransaction && p.HasChanges)
            {
                changed.Add(p);
            }
        }

        if (!NeedsSuper(changed))
        {
            foreach (Pager p in pagers)
            {
                p.CommitSync();
            }

            return;
        }

        IVfs vfs = changed[0].Vfs;
        string mj = Pager.NewSuperJournalName(changed[0].FullPath);
        byte[] content = Pager.SuperJournalContent(changed.ConvertAll(static p => p.JournalFullPath));
        using (IVfsFile f = vfs.Open(mj, VfsOpenOptions.ReadWrite | VfsOpenOptions.Create | VfsOpenOptions.Exclusive | VfsOpenOptions.SuperJournal))
        {
            f.Write(0, content);
            f.Sync(SyncMode.Normal);
            f.SyncDirectory();
        }

        foreach (Pager p in changed)
        {
            Drive(p, () => p.PrepareCommit(mj));
        }

        foreach (Pager p in changed)
        {
            Drive(p, p.WriteDatabase);
        }

        vfs.Delete(mj, syncDirectory: true);
        foreach (Pager p in changed)
        {
            Drive(p, p.FinishCommit);
        }

        foreach (Pager p in pagers)
        {
            if (!changed.Contains(p))
            {
                p.CommitSync();
            }
        }
    }

    private static void Drive(Pager p, Func<PagerStatus> op)
    {
        PagerStatus s;
        while ((s = op()) == PagerStatus.Pending)
        {
            p.ExecuteSync();
        }

        if (s == PagerStatus.Error)
        {
            throw p.Error!;
        }
    }
}
