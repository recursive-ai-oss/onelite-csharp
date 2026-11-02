namespace RecursiveAI.OneLite.Storage.Pager;

// Whole-page writes for the backup API and VACUUM INTO (backup, lang_vacuum): a database is rebuilt from page images, a few pages per call.
public sealed partial class Pager
{
    /// <summary>
    /// Counts how often a read transaction found the database changed by someone else (another connection or process committed, or a hot journal was
    /// rolled back) since this pager last looked. Commits of this pager's own never count. The backup API restarts when it moves.
    /// </summary>
    public long ForeignChanges { get; private set; }

    private uint _spIdx;
    private bool _spActive;

    /// <summary>
    /// Replaces page <paramref name="pageNumber"/> with <paramref name="data"/> in the open write transaction, growing the database if the page lies
    /// beyond its end (pages should be written in ascending order; the lock-byte page is skipped and never stored). Re-enter after Pending.
    /// </summary>
    /// <param name="pageNumber">The page, from 1.</param>
    /// <param name="data">Exactly one page.</param>
    /// <returns>The status; repeat the same call after <see cref="PagerStatus.Pending"/>.</returns>
    /// <exception cref="OneLiteException">SQLITE_MISUSE outside a write transaction.</exception>
    public PagerStatus PutPage(uint pageNumber, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Guard();
        if (_state != TxState.Write || pageNumber == 0)
        {
            throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "PutPage needs a write transaction");
        }

        if (data.Length != _pageSize)
        {
            throw new ArgumentException("a page image must be one page long", nameof(data));
        }

        if (pageNumber == LockBytePage)
        {
            return PagerStatus.Ok;
        }

        if (pageNumber > _dbSize)
        {
            _dbSize = pageNumber;
        }

        PagerStatus s = GetCore(pageNumber, pageNumber <= _origSize ? GetOptions.None : GetOptions.NoContent, out PageFrame? f);
        if (s != PagerStatus.Ok)
        {
            return s;
        }

        PagerStatus w = MakeWritable(f!);
        if (w != PagerStatus.Ok)
        {
            f!.Pins--;
            return w;
        }

        Buffer.BlockCopy(data, 0, f!.Data, 0, _pageSize);
        f.Pins--;
        return PagerStatus.Ok;
    }

    /// <summary>Runs <see cref="PutPage"/> to completion, satisfying I/O inline.</summary>
    /// <param name="pageNumber">The page.</param>
    /// <param name="data">Exactly one page.</param>
    public void PutPageSync(uint pageNumber, byte[] data) => RunSync(() => PutPage(pageNumber, data));

    /// <summary>
    /// Cuts the database to <paramref name="pages"/> pages. In a rollback journal the pages cut off are journaled first, so a crash during the commit
    /// can still restore them. Re-enter after Pending.
    /// </summary>
    /// <param name="pages">The new size in pages, at least 1.</param>
    /// <returns>The status; repeat the same call after <see cref="PagerStatus.Pending"/>.</returns>
    public PagerStatus ShrinkTo(uint pages)
    {
        Guard();
        if (_state != TxState.Write || pages == 0)
        {
            throw new OneLiteException(ResultCode.Misuse, Msg(ResultCode.Misuse), "ShrinkTo needs a write transaction");
        }

        if (!_spActive)
        {
            _spActive = true;
            _spIdx = pages + 1;
        }

        uint top = Math.Min(_dbSize, _origSize);
        while (_wal == null && _spIdx <= top)
        {
            if (_spIdx != LockBytePage)
            {
                PagerStatus s = GetCore(_spIdx, GetOptions.None, out PageFrame? f);
                if (s != PagerStatus.Ok)
                {
                    return s;
                }

                PagerStatus w = MakeWritable(f!);
                f!.Pins--;
                if (w != PagerStatus.Ok)
                {
                    return w;
                }
            }

            _spIdx++;
        }

        if (pages < _dbSize)
        {
            _dbSize = pages;
            DropFramesBeyond(pages);
        }

        _spActive = false;
        return PagerStatus.Ok;
    }

    /// <summary>
    /// Takes the auto-vacuum fields from the page 1 now in the cache, after a database was rebuilt from page images. The page must be cached in the open transaction.
    /// </summary>
    public void AdoptHeader()
    {
        PageFrame f = GetSync(1);
        try
        {
            DatabaseHeader h = DatabaseHeader.Parse(f.Data.AsSpan(0, DatabaseHeader.Size));
            _largestRoot = h.LargestRootPage;
            _autoVacuum = h.AutoVacuum;
            _incremental = h.AutoVacuum && h.IncrementalVacuum;
            _encoding = h.Encoding;
        }
        finally
        {
            Release(f);
        }
    }
}
