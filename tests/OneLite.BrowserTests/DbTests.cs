using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;
using RecursiveAI.OneLite.Storage.Wal;

namespace RecursiveAI.OneLite.BrowserTests;

/// <summary>Pager-level tests (commit, roll back, reopen, hot-journal recovery, cache spill, WAL) and B-tree round trips, through each OPFS backend.</summary>
internal static class DbTests
{
    public static void Register()
    {
        foreach ((string kind, string where) in new[] { ("async", "any"), ("sync", "worker") })
        {
            string k = kind;
            void Add(string name, Func<Backend, Task> body) =>
                Registry.Add($"{k}.{name}", where, async () =>
                {
                    using Backend b = await Backend.Create(k).ConfigureAwait(false);
                    await body(b).ConfigureAwait(false);
                });

            Add("pager.commit-reopen.delete", b => CommitReopen(b, JournalMode.Delete, false));
            Add("pager.commit-reopen.truncate", b => CommitReopen(b, JournalMode.Truncate, false));
            Add("pager.commit-reopen.persist", b => CommitReopen(b, JournalMode.Persist, false));
            Add("pager.commit-reopen.wal", b => CommitReopen(b, JournalMode.Delete, true));
            Add("pager.rollback", Rollback);
            Add("pager.hot-journal-recovery", HotJournal);
            Add("pager.cache-spill", Spill);
            Add("pager.page-size-65536", b => PageSizes(b, 65536));
            Add("pager.page-size-512", b => PageSizes(b, 512));
            Add("btree.insert-scan.delete", b => BTreeRoundTrip(b, false));
            Add("btree.insert-scan.wal", b => BTreeRoundTrip(b, true));
            Add("pager.wal-left-behind-is-recovered", x => WalLeftBehind(x, 500, 200));
        }
    }

    internal static PagerOptions Opts(int pageSize = 1024, JournalMode jm = JournalMode.Delete, int cache = 200) =>
        new() { PageSize = pageSize, JournalMode = jm, CacheSize = cache, Synchronous = SynchronousMode.Full };

    /// <summary>Opens through <c>OpenAsync</c> on both backends: for the worker backend that is what prepares the handles.</summary>
    internal static async Task<Drive> OpenPrepared(Backend b, string path, PagerOptions o)
    {
        Pager p = await Pager.OpenAsync(b.Vfs, path, o).ConfigureAwait(false);
        return new Drive(p, b.Sync);
    }

    internal static async Task Close(Backend b, Drive d)
    {
        d.Pager.Dispose();
        await b.Settle().ConfigureAwait(false);
    }

    // ---- page-pattern databases -------------------------------------------------------------------------------------------------------------

    private static void Fill(PageFrame f, int gen)
    {
        int start = f.PageNumber == 1 ? 108 : 0;
        for (int i = start; i < f.Data.Length; i++)
        {
            f.Data[i] = (byte)((f.PageNumber * 31) + (gen * 7) + i);
        }
    }

    private static void CheckPage(byte[] data, uint pgno, int gen, string what)
    {
        int start = pgno == 1 ? 108 : 0;
        for (int i = start; i < data.Length; i++)
        {
            byte want = (byte)((pgno * 31) + (gen * 7) + i);
            if (data[i] != want)
            {
                throw new CheckFailed($"{what}: page {pgno} byte {i} is {data[i]}, expected {want} (generation {gen})");
            }
        }
    }

    private static async Task Rewrite(Drive d, IEnumerable<uint> pages, int gen)
    {
        foreach (uint n in pages)
        {
            PageFrame f = await d.Get(n).ConfigureAwait(false);
            await d.MakeWritable(f).ConfigureAwait(false);
            Fill(f, gen);
            d.Pager.Release(f);
        }
    }

    private static async Task Create(Backend b, string path, int pages, int pageSize, int cache = 200)
    {
        Drive d = await OpenPrepared(b, path, Opts(pageSize, cache: cache)).ConfigureAwait(false);
        await d.BeginRead().ConfigureAwait(false);
        await d.BeginWrite().ConfigureAwait(false);
        await Rewrite(d, [1], 0).ConfigureAwait(false);
        for (int i = 2; i <= pages; i++)
        {
            PageFrame f = await d.Allocate().ConfigureAwait(false);
            Fill(f, 0);
            d.Pager.Release(f);
        }

        await d.Commit().ConfigureAwait(false);
        await d.EndRead().ConfigureAwait(false);
        await Close(b, d).ConfigureAwait(false);
    }

    private static async Task VerifyAll(Backend b, string path, int pageSize, uint pages, Func<uint, int> generation, string what)
    {
        Drive d = await OpenPrepared(b, path, Opts(pageSize)).ConfigureAwait(false);
        await d.BeginRead().ConfigureAwait(false);
        Check.Equal(pages, d.Pager.PageCount, what + ": page count");
        for (uint n = 1; n <= pages; n++)
        {
            CheckPage(await d.Page(n).ConfigureAwait(false), n, generation(n), what);
        }

        await d.EndRead().ConfigureAwait(false);
        await Close(b, d).ConfigureAwait(false);
    }

    private static async Task CommitReopen(Backend b, JournalMode jm, bool wal)
    {
        string path = Backend.Path("commit");
        const int ps = 1024;
        {
            Drive d = await OpenPrepared(b, path, Opts(ps, jm)).ConfigureAwait(false);
            await d.BeginRead().ConfigureAwait(false);
            await d.BeginWrite().ConfigureAwait(false);
            await Rewrite(d, [1], 0).ConfigureAwait(false);
            for (int i = 2; i <= 40; i++)
            {
                PageFrame f = await d.Allocate().ConfigureAwait(false);
                Fill(f, 0);
                d.Pager.Release(f);
            }

            await d.Commit().ConfigureAwait(false);
            await d.EndRead().ConfigureAwait(false);
            if (wal)
            {
                await d.BeginRead().ConfigureAwait(false);
                await d.Run(() => d.Pager.ChangeJournalMode(JournalMode.Wal)).ConfigureAwait(false);
                await d.EndRead().ConfigureAwait(false);
                Check.True(d.Pager.IsWal, "the pager is in WAL mode");
            }

            await Close(b, d).ConfigureAwait(false);
        }

        await VerifyAll(b, path, ps, 40, _ => 0, "after the first commit").ConfigureAwait(false);

        {
            Drive d = await OpenPrepared(b, path, Opts(ps, jm)).ConfigureAwait(false);
            await d.BeginRead().ConfigureAwait(false);
            Check.Equal(wal, d.Pager.IsWal, "the journal mode is remembered in the file");
            await d.BeginWrite().ConfigureAwait(false);
            await Rewrite(d, [1, 5, 17, 40], 1).ConfigureAwait(false);
            for (int i = 41; i <= 45; i++)
            {
                PageFrame f = await d.Allocate().ConfigureAwait(false);
                Fill(f, 1);
                d.Pager.Release(f);
            }

            await d.Commit().ConfigureAwait(false);
            await d.EndRead().ConfigureAwait(false);
            // On the main-thread backend the pager cannot checkpoint while closing (its close is synchronous), so a WAL stays behind; the next open recovers from it.
            await Close(b, d).ConfigureAwait(false);
        }

        await VerifyAll(b, path, ps, 45, n => n is 1 or 5 or 17 or 40 or > 40 ? 1 : 0, "after the second commit").ConfigureAwait(false);
        if (wal)
        {
            // A WAL left behind is still a valid log: a third connection also sees it.
            Drive d = await OpenPrepared(b, path, Opts(ps, jm)).ConfigureAwait(false);
            await d.Run(() => d.Pager.Checkpoint(CheckpointMode.Truncate)).ConfigureAwait(false);
            await Close(b, d).ConfigureAwait(false);
            await VerifyAll(b, path, ps, 45, n => n is 1 or 5 or 17 or 40 or > 40 ? 1 : 0, "after a checkpoint").ConfigureAwait(false);
        }
    }

    private static async Task Rollback(Backend b)
    {
        string path = Backend.Path("rollback");
        await Create(b, path, 30, 1024).ConfigureAwait(false);
        Drive d = await OpenPrepared(b, path, Opts(1024)).ConfigureAwait(false);
        await d.BeginRead().ConfigureAwait(false);
        await d.BeginWrite().ConfigureAwait(false);
        await Rewrite(d, [2, 3, 9, 20], 2).ConfigureAwait(false);
        PageFrame extra = await d.Allocate().ConfigureAwait(false);
        Fill(extra, 2);
        d.Pager.Release(extra);
        await d.Rollback().ConfigureAwait(false);
        CheckPage(await d.Page(3).ConfigureAwait(false), 3, 0, "inside the connection after the rollback");
        Check.Equal(30u, d.Pager.PageCount, "the database is the old size again");
        await d.EndRead().ConfigureAwait(false);
        await Close(b, d).ConfigureAwait(false);
        await VerifyAll(b, path, 1024, 30, _ => 0, "after a rolled back transaction").ConfigureAwait(false);
    }

    /// <summary>atomiccommit §4: a journal that is complete and synced while the database was being overwritten is played back by the next opener.</summary>
    private static async Task HotJournal(Backend b)
    {
        string path = Backend.Path("hot");
        await Create(b, path, 40, 1024).ConfigureAwait(false);
        Drive d = await OpenPrepared(b, path, Opts(1024)).ConfigureAwait(false);
        await d.BeginRead().ConfigureAwait(false);
        await d.BeginWrite().ConfigureAwait(false);
        await Rewrite(d, Enumerable.Range(2, 29).Select(x => (uint)x), 3).ConfigureAwait(false);
        await d.Run(() => d.Pager.PrepareCommit(null)).ConfigureAwait(false);
        await d.Run(d.Pager.WriteDatabase).ConfigureAwait(false);
        // The process "dies" here: the database holds the new pages and the journal is the only way back.
        await Close(b, d).ConfigureAwait(false);
        Check.True(await b.Vfs.AccessAsync(path + "-journal", AccessMode.Exists).ConfigureAwait(false), "a journal was left behind");
        await VerifyAll(b, path, 1024, 40, _ => 0, "after hot-journal recovery").ConfigureAwait(false);
        Check.True(!await b.Vfs.AccessAsync(path + "-journal", AccessMode.Exists).ConfigureAwait(false), "recovery removed the journal");
    }

    private static async Task Spill(Backend b)
    {
        string path = Backend.Path("spill");
        await Create(b, path, 150, 512, cache: 6).ConfigureAwait(false);
        await VerifyAll(b, path, 512, 150, _ => 0, "after a transaction larger than the cache").ConfigureAwait(false);
        Drive d = await OpenPrepared(b, path, Opts(512, cache: 6)).ConfigureAwait(false);
        await d.BeginRead().ConfigureAwait(false);
        await d.BeginWrite().ConfigureAwait(false);
        await Rewrite(d, Enumerable.Range(1, 150).Select(x => (uint)x), 1).ConfigureAwait(false);
        await d.Commit().ConfigureAwait(false);
        await d.EndRead().ConfigureAwait(false);
        await Close(b, d).ConfigureAwait(false);
        await VerifyAll(b, path, 512, 150, _ => 1, "after rewriting every page through a small cache").ConfigureAwait(false);
    }

    private static async Task PageSizes(Backend b, int pageSize)
    {
        string path = Backend.Path("ps" + pageSize);
        await Create(b, path, 20, pageSize).ConfigureAwait(false);
        await VerifyAll(b, path, pageSize, 20, _ => 0, $"page size {pageSize}").ConfigureAwait(false);
    }

    /// <summary>Builds the standard test database (see <see cref="DbContent"/>) in two commits; with <paramref name="wal"/> the second one goes through the log, and the database is closed with the log still holding it where the backend cannot checkpoint on close.</summary>
    internal static async Task BuildDb(Backend b, string path, int rows, bool wal, int cache = 200)
    {
        uint t, ix;
        int half = Math.Max(1, rows / 2);
        Drive d = await OpenPrepared(b, path, Opts(1024, cache: cache)).ConfigureAwait(false);
        await d.BeginRead().ConfigureAwait(false);
        await d.BeginWrite().ConfigureAwait(false);
        (t, ix) = await DbContent.Insert(d, 1, half, 0, 0).ConfigureAwait(false);
        await d.Commit().ConfigureAwait(false);
        await d.EndRead().ConfigureAwait(false);
        if (wal)
        {
            await d.BeginRead().ConfigureAwait(false);
            await d.Run(() => d.Pager.ChangeJournalMode(JournalMode.Wal)).ConfigureAwait(false);
            await d.EndRead().ConfigureAwait(false);
        }

        await d.BeginRead().ConfigureAwait(false);
        await d.BeginWrite().ConfigureAwait(false);
        await DbContent.Insert(d, half + 1, rows, t, ix).ConfigureAwait(false);
        await d.Commit().ConfigureAwait(false);
        await d.EndRead().ConfigureAwait(false);
        await Close(b, d).ConfigureAwait(false);
    }

    /// <summary>A WAL that a close left behind (the main-thread backend cannot checkpoint while closing) is recovered by the next open: every committed row is there.</summary>
    private static async Task WalLeftBehind(Backend b, int rows, int cache)
    {
        string path = Backend.Path("dbg");
        await BuildDb(b, path, rows, true, cache).ConfigureAwait(false);
        string dir = path[..path.LastIndexOf('/')];
        string listing = (await Storage.Vfs.Browser.BrowserStorage.ListAsync(dir).ConfigureAwait(false)).Replace('\n', ' ');
        Drive d = await OpenPrepared(b, path, Opts(1024)).ConfigureAwait(false);
        await d.BeginRead().ConfigureAwait(false);
        string state = $"files [{listing}] wal={d.Pager.IsWal} frames={d.Pager.WalFrameCount} pages={d.Pager.PageCount}";
        try
        {
            await DbContent.Verify(d, rows).ConfigureAwait(false);
        }
        catch (CheckFailed e)
        {
            throw new CheckFailed(state + ": " + e.Message);
        }

        await d.EndRead().ConfigureAwait(false);
        await Close(b, d).ConfigureAwait(false);
    }

    // ---- B-tree round trips -----------------------------------------------------------------------------------------------------------------

    private static async Task BTreeRoundTrip(Backend b, bool wal)
    {
        const int rows = 1200;
        string path = Backend.Path("btree");
        uint t, ix;
        {
            Drive d = await OpenPrepared(b, path, Opts(1024)).ConfigureAwait(false);
            await d.BeginRead().ConfigureAwait(false);
            await d.BeginWrite().ConfigureAwait(false);
            (t, ix) = await DbContent.Insert(d, 1, rows / 2, 0, 0).ConfigureAwait(false);
            await d.Commit().ConfigureAwait(false);
            await d.EndRead().ConfigureAwait(false);
            if (wal)
            {
                await d.BeginRead().ConfigureAwait(false);
                await d.Run(() => d.Pager.ChangeJournalMode(JournalMode.Wal)).ConfigureAwait(false);
                await d.EndRead().ConfigureAwait(false);
            }

            await Close(b, d).ConfigureAwait(false);
        }

        {
            Drive d = await OpenPrepared(b, path, Opts(1024)).ConfigureAwait(false);
            await d.BeginRead().ConfigureAwait(false);
            await DbContent.Verify(d, rows / 2).ConfigureAwait(false);
            await d.BeginWrite().ConfigureAwait(false);
            await DbContent.Insert(d, (rows / 2) + 1, rows, t, ix).ConfigureAwait(false);
            await d.Commit().ConfigureAwait(false);
            await d.EndRead().ConfigureAwait(false);
            await Close(b, d).ConfigureAwait(false);
        }

        {
            Drive d = await OpenPrepared(b, path, Opts(1024)).ConfigureAwait(false);
            await d.BeginRead().ConfigureAwait(false);
            await DbContent.Verify(d, rows).ConfigureAwait(false);
            await d.EndRead().ConfigureAwait(false);
            await Close(b, d).ConfigureAwait(false);
        }
    }
}
