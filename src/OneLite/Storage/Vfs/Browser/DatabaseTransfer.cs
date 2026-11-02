using RecursiveAI.OneLite.Storage.Pager;

namespace RecursiveAI.OneLite.Storage.Vfs.Browser;

/// <summary>
/// Moves whole database files between the application and a VFS: <c>.db</c> import (bytes or a stream into a file) and export (a file out as bytes
/// or into a stream). It works on any <see cref="IVfs"/>; the browser is where it matters, because OPFS files are not visible to the user.
/// </summary>
/// <remarks>
/// Import takes the exclusive lock, so it refuses (BUSY) while any connection has the database open, removes a stale <c>-journal</c> and
/// <c>-wal</c> (a leftover journal would be replayed over the imported pages), and replaces the file. It is not atomic: an import that is interrupted
/// leaves a partial file, so import to a new name and open that. Export reads through a pager, so it sees the committed state: a hot journal is
/// rolled back first, and in WAL mode the committed frames of the log are included. The result is a standalone file; open it with any SQLite-compatible tool.
/// </remarks>
public static class DatabaseTransfer
{
    private const int Chunk = 64 * 1024;
    private const string Magic = "SQLite format 3\0";

    /// <summary>Replaces the database at <paramref name="path"/> with <paramref name="database"/>.</summary>
    /// <param name="vfs">The storage backend.</param>
    /// <param name="path">The database path.</param>
    /// <param name="database">The whole database file.</param>
    /// <param name="ct">Cancellation, observed before each chunk is written.</param>
    /// <returns>A task that completes when the file is written and synced.</returns>
    /// <exception cref="OneLiteException">NOTADB if the bytes are not a database file (no header, or a length that is not a whole number of pages); BUSY if a connection has the database open.</exception>
    /// <exception cref="VfsException">The backend's error.</exception>
    public static async ValueTask ImportAsync(IVfs vfs, string path, ReadOnlyMemory<byte> database, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        ArgumentNullException.ThrowIfNull(path);
        int pageSize = CheckHeader(database.Span);
        if (database.Length % pageSize != 0)
        {
            throw NotADb($"the length {database.Length} is not a whole number of {pageSize}-byte pages");
        }

        int at = 0;
        await ImportCoreAsync(vfs, path, (buf, _) =>
        {
            int n = Math.Min(buf.Length, database.Length - at);
            database.Slice(at, n).CopyTo(buf);
            at += n;
            return new ValueTask<int>(n);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Replaces the database at <paramref name="path"/> with the contents of <paramref name="source"/>, which is read once, from its current position.</summary>
    /// <param name="vfs">The storage backend.</param>
    /// <param name="path">The database path.</param>
    /// <param name="source">A readable stream of the whole database file.</param>
    /// <param name="ct">Cancellation, observed before each chunk is written.</param>
    /// <returns>A task that completes when the file is written and synced.</returns>
    /// <exception cref="OneLiteException">NOTADB if the stream does not start with a database header, or ends in the middle of a page (the file is then emptied); BUSY if a connection has the database open.</exception>
    /// <exception cref="VfsException">The backend's error.</exception>
    public static async ValueTask ImportAsync(IVfs vfs, string path, Stream source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(source);

        // The header is checked before anything is destroyed.
        byte[] head = new byte[100];
        int got = await ReadFullAsync(source, head, ct).ConfigureAwait(false);
        int pageSize = CheckHeader(head.AsSpan(0, got));
        bool first = true;
        long total = 0;
        await ImportCoreAsync(vfs, path, async (buf, token) =>
            {
                int n;
                if (first)
                {
                    first = false;
                    head.AsSpan(0, got).CopyTo(buf.Span);
                    n = got + await ReadFullAsync(source, buf[got..], token).ConfigureAwait(false);
                }
                else
                {
                    n = await ReadFullAsync(source, buf, token).ConfigureAwait(false);
                }

                total += n;
                return n;
            }, ct).ConfigureAwait(false);

        if (total % pageSize != 0)
        {
            await EmptyAsync(vfs, path).ConfigureAwait(false);
            throw NotADb($"the stream ended in the middle of a page ({total} bytes of {pageSize}-byte pages)");
        }
    }

    /// <summary>Returns the committed content of the database at <paramref name="path"/> as one array.</summary>
    /// <param name="vfs">The storage backend.</param>
    /// <param name="path">The database path.</param>
    /// <param name="ct">Cancellation, observed between pages.</param>
    /// <returns>The database file; empty if it holds no pages yet.</returns>
    /// <exception cref="OneLiteException">CANTOPEN if the database does not exist; BUSY if a writer holds it and the pager cannot get a shared lock; NOTADB or CORRUPT for a damaged file.</exception>
    public static async ValueTask<byte[]> ExportAsync(IVfs vfs, string path, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await ExportAsync(vfs, path, ms, ct).ConfigureAwait(false);
        return ms.ToArray();
    }

    /// <summary>Writes the committed content of the database at <paramref name="path"/> to <paramref name="destination"/>.</summary>
    /// <param name="vfs">The storage backend.</param>
    /// <param name="path">The database path.</param>
    /// <param name="destination">A writable stream.</param>
    /// <param name="ct">Cancellation, observed between pages.</param>
    /// <returns>A task that completes when the last page is written to the stream.</returns>
    /// <exception cref="OneLiteException">As <see cref="ExportAsync(IVfs, string, CancellationToken)"/>.</exception>
    public static async ValueTask ExportAsync(IVfs vfs, string path, Stream destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(destination);
        if (!await vfs.AccessAsync(path, AccessMode.Exists, ct).ConfigureAwait(false))
        {
            throw new OneLiteException(ResultCode.CantOpen, "unable to open database file", $"'{path}' does not exist");
        }

        // The pager is told the file's own page size: a database in WAL mode opened with a different size would not see its log.
        int fileSize = await HeaderPageSizeAsync(vfs, path, ct).ConfigureAwait(false);
        if (fileSize == 0)
        {
            return; // an empty file is an empty database
        }

        using Pager.Pager pager = await Pager.Pager.OpenAsync(vfs, path, new PagerOptions { PageSize = fileSize }, ct).ConfigureAwait(false);
        await pager.BeginReadAsync(ct).ConfigureAwait(false);
        try
        {
            byte[] zero = new byte[pager.PageSize];
            uint count = pager.PageCount;
            for (uint pgno = 1; pgno <= count; pgno++)
            {
                ct.ThrowIfCancellationRequested();
                if (pgno == pager.LockBytePage)
                {
                    // fileformat2 §1.3: the page that holds file offset 0x40000000 is never used; its space in the file reads as zeros here.
                    await destination.WriteAsync(zero, ct).ConfigureAwait(false);
                    continue;
                }

                PageFrame frame = await pager.GetAsync(pgno, GetOptions.None, ct).ConfigureAwait(false);
                try
                {
                    await destination.WriteAsync(frame.Data.AsMemory(0, pager.PageSize), ct).ConfigureAwait(false);
                }
                finally
                {
                    pager.Release(frame);
                }
            }
        }
        finally
        {
            await pager.EndReadAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async ValueTask ImportCoreAsync(IVfs vfs, string path, Func<Memory<byte>, CancellationToken, ValueTask<int>> next, CancellationToken ct)
    {
        IVfsFile file = await vfs.OpenAsync(path, VfsOpenOptions.ReadWrite | VfsOpenOptions.Create | VfsOpenOptions.MainDb, ct).ConfigureAwait(false);
        try
        {
            if (!await file.LockAsync(LockLevel.Shared, ct).ConfigureAwait(false) || !await file.LockAsync(LockLevel.Exclusive, ct).ConfigureAwait(false))
            {
                throw new OneLiteException(ResultCode.Busy, "database is locked", "another connection has the database open");
            }

            // A journal or log left over from the old file would be applied to the new one.
            foreach (string aux in new[] { path + "-journal", path + "-wal" })
            {
                if (await vfs.AccessAsync(aux, AccessMode.Exists, ct).ConfigureAwait(false))
                {
                    await vfs.DeleteAsync(aux, false, ct).ConfigureAwait(false);
                }
            }

            await file.TruncateAsync(0, ct).ConfigureAwait(false);
            byte[] buffer = new byte[Chunk];
            long offset = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int n = await next(buffer, ct).ConfigureAwait(false);
                if (n <= 0)
                {
                    break;
                }

                await file.WriteAsync(offset, buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                offset += n;
            }

            await file.SyncAsync(SyncMode.Full, ct).ConfigureAwait(false);
        }
        finally
        {
            await file.UnlockAsync(LockLevel.None, CancellationToken.None).ConfigureAwait(false);
            file.Dispose();
        }
    }

    /// <summary>The page size in the first 100 bytes of the file, or 0 if the file is empty.</summary>
    private static async ValueTask<int> HeaderPageSizeAsync(IVfs vfs, string path, CancellationToken ct)
    {
        IVfsFile file = await vfs.OpenAsync(path, VfsOpenOptions.ReadOnly | VfsOpenOptions.MainDb, ct).ConfigureAwait(false);
        try
        {
            byte[] head = new byte[100];
            int n = await file.ReadAsync(0, head, ct).ConfigureAwait(false);
            return n == 0 ? 0 : CheckHeader(head.AsSpan(0, n));
        }
        finally
        {
            file.Dispose();
        }
    }

    private static async ValueTask EmptyAsync(IVfs vfs, string path)
    {
        IVfsFile file = await vfs.OpenAsync(path, VfsOpenOptions.ReadWrite | VfsOpenOptions.Create | VfsOpenOptions.MainDb, CancellationToken.None).ConfigureAwait(false);
        try
        {
            await file.TruncateAsync(0).ConfigureAwait(false);
            await file.SyncAsync(SyncMode.Full).ConfigureAwait(false);
        }
        finally
        {
            file.Dispose();
        }
    }

    private static async ValueTask<int> ReadFullAsync(Stream s, Memory<byte> buf, CancellationToken ct)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = await s.ReadAsync(buf[total..], ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    // fileformat2 §1.3: the 16-byte magic string, the page size at offset 16 (a power of two from 512 to 32768, or 1 for 65536).
    private static int CheckHeader(ReadOnlySpan<byte> head)
    {
        if (head.Length < 100)
        {
            throw NotADb("shorter than the 100-byte database header");
        }

        for (int i = 0; i < Magic.Length; i++)
        {
            if (head[i] != Magic[i])
            {
                throw NotADb("the header does not start with \"SQLite format 3\"");
            }
        }

        int raw = (head[16] << 8) | head[17];
        int size = raw == 1 ? 65536 : raw;
        if (size < 512 || size > 65536 || (size & (size - 1)) != 0)
        {
            throw NotADb($"the page size {raw} in the header is not valid");
        }

        return size;
    }

    private static OneLiteException NotADb(string detail) => new(ResultCode.NotADb, "file is not a database", detail);
}
