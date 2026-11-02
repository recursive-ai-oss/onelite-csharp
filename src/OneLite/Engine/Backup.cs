using RecursiveAI.OneLite.Storage.Pager;

namespace RecursiveAI.OneLite;

/// <summary>What one <see cref="Backup.Step(int)"/> achieved (c3ref backup_step).</summary>
public enum BackupStatus
{
    /// <summary>SQLITE_OK: pages were copied and more remain.</summary>
    More,

    /// <summary>SQLITE_DONE: every page is copied and the destination is committed.</summary>
    Done,

    /// <summary>SQLITE_BUSY: the source or destination is locked by another connection or process. Call Step again later; nothing is lost.</summary>
    Busy,

    /// <summary>SQLITE_LOCKED: the destination connection is in use. Call Step again later.</summary>
    Locked,
}

/// <summary>
/// The online backup API (backup, c3ref backup_init): copies one database of a source connection into one database of a destination connection,
/// a few pages per <see cref="Step(int)"/>. The destination's write transaction stays open between steps; the source is read under a lock only while
/// a step runs, so other connections can use it in between.
/// <para>
/// A write to the source through another connection or process restarts the copy at the next step. A write through the source's own connection
/// is copied into the destination in place, so a copy that has begun stays correct without restarting. The destination's old content is replaced
/// all at once when the last step commits: until then, and after <see cref="Dispose"/> of an unfinished backup, it is untouched. A crash leaves
/// either the old or the complete new content.
/// </para>
/// <para>
/// Limits: the destination's page size can differ from the source's only when the destination has no pages yet and is not in WAL mode (otherwise
/// the step fails with SQLITE_READONLY, as the reference does for WAL and in-memory destinations); usable page sizes must be equal otherwise. A
/// source with no pages leaves the destination as it is. While a backup is unfinished its destination connection refuses statements on its
/// main database with SQLITE_LOCKED.
/// </para>
/// </summary>
public sealed class Backup : IDisposable
{
    private readonly Connection _src;
    private readonly Connection _dst;
    private readonly int _srcSlot;
    private readonly int _dstSlot;
    private readonly Pager _sp;
    private readonly Pager _dp;
    private uint _next = 1;
    private uint _srcPages;
    private long _lastForeign = -1;
    private bool _destOpen;
    private uint _destOldCookie;
    private bool _done;
    private bool _closed;
    private byte[] _buf = [];
    private byte[] _cmp = [];

    private Backup(Connection dest, int dstSlot, Connection source, int srcSlot)
    {
        _dst = dest;
        _src = source;
        _dstSlot = dstSlot;
        _srcSlot = srcSlot;
        _sp = source.PagerOf(srcSlot);
        _dp = dest.PagerOf(dstSlot);
    }

    /// <summary>Starts a backup (sqlite3_backup_init).</summary>
    /// <param name="destination">The connection to write to.</param>
    /// <param name="destinationName">The destination's schema name: "main", "temp" or an attached name.</param>
    /// <param name="source">The connection to read from. Must not be the destination connection.</param>
    /// <param name="sourceName">The source's schema name.</param>
    /// <returns>The backup, which holds nothing until the first <see cref="Step(int)"/>.</returns>
    /// <exception cref="OneLiteException">SQLITE_ERROR "source and destination must be distinct", "destination database is in use" when the destination connection is in a transaction, or "unknown database NAME".</exception>
    public static Backup Create(Connection destination, string destinationName, Connection source, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(destinationName);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceName);
        if (ReferenceEquals(destination, source))
        {
            throw new OneLiteException(ResultCode.Error, "source and destination must be distinct");
        }

        if (!destination.AutoCommit || destination.ActiveReaders > 0 || destination.BackupDestinationHeld)
        {
            throw new OneLiteException(ResultCode.Error, "destination database is in use");
        }

        int ss = source.SlotOf(sourceName);
        int ds = destination.SlotOf(destinationName);
        if (ss < 0)
        {
            throw new OneLiteException(ResultCode.Error, "unknown database " + sourceName);
        }

        if (ds < 0)
        {
            throw new OneLiteException(ResultCode.Error, "unknown database " + destinationName);
        }

        var b = new Backup(destination, ds, source, ss);
        source.Backups.Add(b);
        return b;
    }

    /// <summary>Pages still to copy after the last step (sqlite3_backup_remaining); 0 before the first step.</summary>
    public int Remaining => _srcPages == 0 ? 0 : (int)(_srcPages - Math.Min(_next - 1, _srcPages));

    /// <summary>Pages in the source as of the last step (sqlite3_backup_pagecount); 0 before the first step.</summary>
    public int PageCount => (int)_srcPages;

    /// <summary>True once the last step committed the destination.</summary>
    public bool IsComplete => _done;

    /// <summary>Copies up to <paramref name="pages"/> pages (a negative number copies all that remain).</summary>
    /// <param name="pages">Pages to copy this step.</param>
    /// <returns>The outcome. <see cref="BackupStatus.Busy"/> and <see cref="BackupStatus.Locked"/> are retryable.</returns>
    /// <exception cref="OneLiteException">Any other failure, with the reference's code and message (SQLITE_READONLY for a page-size or reserved-byte mismatch the destination cannot take, SQLITE_IOERR, SQLITE_CORRUPT, ...). The destination is rolled back and the backup ends.</exception>
    public BackupStatus Step(int pages)
    {
        // The sync driver never suspends, so this task is already complete.
        return StepCore(pages, true, default).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>The async twin of <see cref="Step(int)"/>: page I/O awaits the storage backend.</summary>
    /// <param name="pages">Pages to copy this step.</param>
    /// <param name="ct">Observed between pages; a cancelled step keeps the pages already copied.</param>
    /// <returns>The outcome.</returns>
    public ValueTask<BackupStatus> StepAsync(int pages, CancellationToken ct = default) => StepCore(pages, false, ct);

    private static async ValueTask Pump(Pager p, bool sync, CancellationToken ct)
    {
        if (sync)
        {
            p.ExecuteSync();
        }
        else
        {
            await p.ExecuteAsync(ct).ConfigureAwait(false);
        }
    }

    private static async ValueTask Run(Pager p, Func<PagerStatus> op, bool sync, CancellationToken ct)
    {
        PagerStatus s;
        while ((s = op()) == PagerStatus.Pending)
        {
            await Pump(p, sync, ct).ConfigureAwait(false);
        }

        if (s == PagerStatus.Error)
        {
            throw p.Error!;
        }
    }

    private async ValueTask ReadPage(uint pgno, byte[] into, bool sync, CancellationToken ct)
    {
        PageFrame? f = null;
        await Run(_sp, () => _sp.Get(pgno, GetOptions.None, out f), sync, ct).ConfigureAwait(false);
        try
        {
            Buffer.BlockCopy(f!.Data, 0, into, 0, _sp.PageSize);
        }
        finally
        {
            _sp.Release(f!);
        }
    }

    private async ValueTask<BackupStatus> StepCore(int pages, bool sync, CancellationToken ct)
    {
        if (_done)
        {
            return BackupStatus.Done;
        }

        if (_closed)
        {
            throw new OneLiteException(ResultCode.Misuse, "backup is finished");
        }

        bool srcOwn = false;
        try
        {
            // Source: a read transaction for the length of the step, unless the source connection already has one open.
            if (!_sp.InTransaction)
            {
                try
                {
                    await Run(_sp, _sp.BeginRead, sync, ct).ConfigureAwait(false);
                }
                catch (OneLiteException e) when ((e.Code & 0xFF) == ResultCode.Busy)
                {
                    return BackupStatus.Busy;
                }

                srcOwn = true;
            }

            try
            {
                return await StepLocked(pages, sync, ct).ConfigureAwait(false);
            }
            finally
            {
                if (srcOwn && _sp.InTransaction && !_sp.InWriteTransaction)
                {
                    try
                    {
                        await Run(_sp, _sp.EndRead, sync, ct).ConfigureAwait(false);
                    }
                    catch (OneLiteException)
                    {
                        // A read lock that cannot be released goes with the descriptor.
                    }
                }
            }
        }
        catch (OneLiteException e) when ((e.Code & 0xFF) != ResultCode.Busy)
        {
            Abort();
            throw;
        }
    }

    private async ValueTask<BackupStatus> StepLocked(int pages, bool sync, CancellationToken ct)
    {
        if (_srcPages != 0 && _lastForeign != _sp.ForeignChanges)
        {
            // Another connection changed the source since the last step: what was copied may be stale (backup, "restarts").
            _next = 1;
        }

        _lastForeign = _sp.ForeignChanges;
        uint count = _sp.PageCount;
        if (count == 0)
        {
            _srcPages = 0;
            _done = true;
            Close();
            return BackupStatus.Done;
        }

        _srcPages = count;
        int ps = _sp.PageSize;
        if (_buf.Length != ps)
        {
            _buf = new byte[ps];
            _cmp = new byte[ps];
        }

        if (!_destOpen)
        {
            try
            {
                await Run(_dp, _dp.BeginRead, sync, ct).ConfigureAwait(false);
                await Run(_dp, _dp.BeginWrite, sync, ct).ConfigureAwait(false);
            }
            catch (OneLiteException e) when ((e.Code & 0xFF) == ResultCode.Busy)
            {
                if (_dp.InTransaction && !_dp.InWriteTransaction)
                {
                    await Run(_dp, _dp.EndRead, sync, ct).ConfigureAwait(false);
                }

                return BackupStatus.Busy;
            }

            _destOpen = true;
            _dst.BackupDestinationHeld = true;
            _destOldCookie = 0;
            if (_dp.PageCount > 0)
            {
                PageFrame? f1 = null;
                await Run(_dp, () => _dp.Get(1, GetOptions.None, out f1), sync, ct).ConfigureAwait(false);
                _destOldCookie = DatabaseHeader.GetUInt32(f1!.Data, DatabaseHeader.SchemaCookieOffset);
                _dp.Release(f1);
            }
        }

        if (_dp.PageSize != ps || _dp.ReservedBytes != _sp.ReservedBytes)
        {
            // Only a database with no pages yet can take another page geometry (the reference refuses for WAL and in-memory destinations too).
            if (!_dp.TrySetPageSize(ps, _sp.ReservedBytes))
            {
                throw new OneLiteException(ResultCode.ReadOnly, "attempt to write a readonly database");
            }
        }

        if (_dp.PageSize != ps)
        {
            throw new OneLiteException(ResultCode.ReadOnly, "attempt to write a readonly database");
        }

        int n = pages < 0 ? int.MaxValue : pages;
        while (n > 0 && _next <= count)
        {
            ct.ThrowIfCancellationRequested();
            uint pg = _next;
            if (pg != _dp.LockBytePage)
            {
                await ReadPage(pg, _buf, sync, ct).ConfigureAwait(false);
                byte[] data = _buf;
                await Run(_dp, () => _dp.PutPage(pg, data), sync, ct).ConfigureAwait(false);
            }

            _next++;
            n--;
        }

        if (_next <= count)
        {
            return BackupStatus.More;
        }

        await Finish(count, sync, ct).ConfigureAwait(false);
        return BackupStatus.Done;
    }

    private async ValueTask Finish(uint count, bool sync, CancellationToken ct)
    {
        // Page 1 carries the source's header; the destination keeps its own journal-mode bytes and bumps its schema cookie so that other
        // connections reload what they cached (backup).
        PageFrame? f = null;
        await Run(_dp, () => _dp.Get(1, GetOptions.None, out f), sync, ct).ConfigureAwait(false);
        try
        {
            await Run(_dp, () => _dp.MakeWritable(f!), sync, ct).ConfigureAwait(false);
            byte[] d = f!.Data;
            DatabaseHeader.SetUInt32(d, DatabaseHeader.SchemaCookieOffset, unchecked(_destOldCookie + 1));
            if (_dp.JournalMode == JournalMode.Wal)
            {
                d[18] = 2;
                d[19] = 2;
            }
        }
        finally
        {
            _dp.Release(f!);
        }

        await Run(_dp, () => _dp.ShrinkTo(count), sync, ct).ConfigureAwait(false);
        await Run(_dp, _dp.Commit, sync, ct).ConfigureAwait(false);

        // The destination goes back to holding nothing, so its next read looks at page 1 again (page size, text encoding and auto-vacuum may all have changed).
        if (_dp.InTransaction)
        {
            await Run(_dp, _dp.EndRead, sync, ct).ConfigureAwait(false);
        }

        _done = true;
        _dst.BackupDestinationHeld = false;
        _destOpen = false;
        _dst.InvalidateSchema();
        Close();
    }

    /// <summary>
    /// Called after the source connection committed: pages the backup has already copied are brought up to date in the destination (the "same
    /// connection" rule of the backup API). Runs inline.
    /// </summary>
    internal void SourceCommitted()
    {
        if (_closed || _done || !_destOpen || _next <= 1)
        {
            return;
        }

        bool own = !_sp.InTransaction;
        try
        {
            if (own)
            {
                _sp.BeginReadSync();
            }

            uint count = _sp.PageCount;
            _srcPages = count;
            uint upto = Math.Min(_next - 1, count);
            for (uint pg = 1; pg <= upto; pg++)
            {
                if (pg == _dp.LockBytePage)
                {
                    continue;
                }

                PageFrame f = _sp.GetSync(pg);
                try
                {
                    Buffer.BlockCopy(f.Data, 0, _buf, 0, _buf.Length);
                }
                finally
                {
                    _sp.Release(f);
                }

                PageFrame g = _dp.GetSync(pg);
                bool same;
                try
                {
                    same = g.Data.AsSpan(0, _buf.Length).SequenceEqual(_buf);
                }
                finally
                {
                    _dp.Release(g);
                }

                if (!same)
                {
                    _dp.PutPageSync(pg, _buf);
                }
            }

            if (_next - 1 > count)
            {
                _next = count + 1;
            }
        }
        finally
        {
            if (own && _sp.InTransaction && !_sp.InWriteTransaction)
            {
                _sp.EndReadSync();
            }
        }
    }

    private void Abort()
    {
        if (_destOpen)
        {
            _destOpen = false;
            try
            {
                if (_dp.InWriteTransaction)
                {
                    _dp.RollbackSync();
                }

                if (_dp.InTransaction)
                {
                    _dp.EndReadSync();
                }
            }
            catch (OneLiteException)
            {
                // The rollback of a failed backup has nothing more to report.
            }

            _dst.BackupDestinationHeld = false;
        }

        Close();
    }

    private void Close()
    {
        _closed = true;
        _src.Backups.Remove(this);
    }

    /// <summary>Ends the backup (sqlite3_backup_finish). An unfinished backup rolls the destination back to its old content.</summary>
    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        Abort();
    }
}
