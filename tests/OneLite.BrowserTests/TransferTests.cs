using System;
using System.IO;
using System.Threading.Tasks;
using RecursiveAI.OneLite.Storage.Vfs;
using RecursiveAI.OneLite.Storage.Vfs.Browser;

namespace RecursiveAI.OneLite.BrowserTests;

/// <summary><c>.db</c> import and export through each backend.</summary>
internal static class TransferTests
{
    public static void Register()
    {
        foreach ((string kind, string where) in new[] { ("async", "any"), ("sync", "worker") })
        {
            string k = kind;
            void Add(string name, Func<Backend, Task> body) =>
                Registry.Add($"{k}.transfer.{name}", where, async () =>
                {
                    using Backend b = await Backend.Create(k).ConfigureAwait(false);
                    await body(b).ConfigureAwait(false);
                });

            Add("export-import-roundtrip", RoundTrip);
            Add("export-includes-wal", ExportWal);
            Add("export-to-stream", ExportStream);
            Add("import-from-stream", ImportStream);
            Add("import-refused-while-open", ImportRefused);
            Add("import-rejects-non-database", ImportRejects);
            Add("import-truncated-stream", ImportTruncated);
            Add("import-removes-stale-journal", ImportStaleJournal);
            Add("export-missing", ExportMissing);
        }
    }

    private static async Task RoundTrip(Backend b)
    {
        string src = Backend.Path("src"), dst = Backend.Path("dst");
        await DbTests.BuildDb(b, src, 600, false).ConfigureAwait(false);
        byte[] bytes = await DatabaseTransfer.ExportAsync(b.Vfs, src).ConfigureAwait(false);
        await b.Settle().ConfigureAwait(false);
        Check.True(bytes.Length > 0 && bytes.Length % 1024 == 0, $"the export is a whole number of pages ({bytes.Length} bytes)");
        Check.True(bytes.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8), "the export starts with the SQLite header");
        await DatabaseTransfer.ImportAsync(b.Vfs, dst, bytes).ConfigureAwait(false);
        await b.Settle().ConfigureAwait(false);
        byte[] again = await DatabaseTransfer.ExportAsync(b.Vfs, dst).ConfigureAwait(false);
        await b.Settle().ConfigureAwait(false);
        Check.BytesEqual(bytes, again, "export after import");
        await Verify(b, dst, 600).ConfigureAwait(false);
    }

    /// <summary>On the main-thread backend a WAL database is closed without a checkpoint, so its newest rows live only in the log: the export must have them.</summary>
    private static async Task ExportWal(Backend b)
    {
        string src = Backend.Path("walsrc"), dst = Backend.Path("waldst");
        await DbTests.BuildDb(b, src, 500, true).ConfigureAwait(false);
        byte[] bytes = await DatabaseTransfer.ExportAsync(b.Vfs, src).ConfigureAwait(false);
        await b.Settle().ConfigureAwait(false);
        await DatabaseTransfer.ImportAsync(b.Vfs, dst, bytes).ConfigureAwait(false);
        await b.Settle().ConfigureAwait(false);
        await Verify(b, dst, 500).ConfigureAwait(false);
    }

    private static async Task ExportStream(Backend b)
    {
        string src = Backend.Path("streamsrc");
        await DbTests.BuildDb(b, src, 300, false).ConfigureAwait(false);
        byte[] bytes = await DatabaseTransfer.ExportAsync(b.Vfs, src).ConfigureAwait(false);
        await b.Settle().ConfigureAwait(false);
        using var ms = new MemoryStream();
        await DatabaseTransfer.ExportAsync(b.Vfs, src, ms).ConfigureAwait(false);
        await b.Settle().ConfigureAwait(false);
        Check.BytesEqual(bytes, ms.ToArray(), "stream export equals array export");
    }

    private static async Task ImportStream(Backend b)
    {
        string src = Backend.Path("isrc"), dst = Backend.Path("idst");
        await DbTests.BuildDb(b, src, 300, false).ConfigureAwait(false);
        byte[] bytes = await DatabaseTransfer.ExportAsync(b.Vfs, src).ConfigureAwait(false);
        await b.Settle().ConfigureAwait(false);
        using var ms = new SlowStream(bytes, 777);
        await DatabaseTransfer.ImportAsync(b.Vfs, dst, ms).ConfigureAwait(false);
        await b.Settle().ConfigureAwait(false);
        Check.BytesEqual(bytes, await DatabaseTransfer.ExportAsync(b.Vfs, dst).ConfigureAwait(false), "import from a stream that returns short reads");
        await b.Settle().ConfigureAwait(false);
    }

    private static async Task ImportRefused(Backend b)
    {
        string src = Backend.Path("rsrc"), dst = Backend.Path("rdst");
        await DbTests.BuildDb(b, src, 100, false).ConfigureAwait(false);
        byte[] bytes = await DatabaseTransfer.ExportAsync(b.Vfs, src).ConfigureAwait(false);
        await b.Settle().ConfigureAwait(false);
        await DatabaseTransfer.ImportAsync(b.Vfs, dst, bytes).ConfigureAwait(false);
        await b.Settle().ConfigureAwait(false);

        // A connection that is reading holds SHARED: the import has to refuse (BUSY) and leave the file alone.
        IVfsFile reader = await b.Vfs.OpenAsync(dst, VfsOpenOptions.ReadWrite | VfsOpenOptions.MainDb).ConfigureAwait(false);
        Check.True(await reader.LockAsync(LockLevel.Shared).ConfigureAwait(false), "the reader locks");
        OneLiteException e = await Check.Throws<OneLiteException>(() => DatabaseTransfer.ImportAsync(b.Vfs, dst, bytes).AsTask(), "import beside a reader").ConfigureAwait(false);
        Check.Equal(ResultCode.Busy, e.ExtendedCode & 0xFF, "import beside a reader is BUSY");
        await reader.UnlockAsync(LockLevel.None).ConfigureAwait(false);
        reader.Dispose();
        await b.Settle().ConfigureAwait(false);
        Check.BytesEqual(bytes, await DatabaseTransfer.ExportAsync(b.Vfs, dst).ConfigureAwait(false), "the file is unchanged by the refused import");
        await b.Settle().ConfigureAwait(false);
        await DatabaseTransfer.ImportAsync(b.Vfs, dst, bytes).ConfigureAwait(false);
    }

    private static async Task ImportRejects(Backend b)
    {
        string dst = Backend.Path("junk");
        byte[][] bad = [[], new byte[50], new byte[4096], "SQLite format 3\0"u8.ToArray()];
        foreach (byte[] junk in bad)
        {
            OneLiteException e = await Check.Throws<OneLiteException>(() => DatabaseTransfer.ImportAsync(b.Vfs, dst, junk).AsTask(), $"importing {junk.Length} junk bytes").ConfigureAwait(false);
            Check.Equal(ResultCode.NotADb, e.ExtendedCode & 0xFF, $"{junk.Length} junk bytes are NOTADB");
        }

        // A good header with a length that is not a whole number of pages.
        string src = Backend.Path("good");
        await DbTests.BuildDb(b, src, 50, false).ConfigureAwait(false);
        byte[] ok = await DatabaseTransfer.ExportAsync(b.Vfs, src).ConfigureAwait(false);
        await b.Settle().ConfigureAwait(false);
        OneLiteException cut = await Check.Throws<OneLiteException>(() => DatabaseTransfer.ImportAsync(b.Vfs, dst, ok.AsMemory(0, ok.Length - 10)).AsTask(), "a partial page").ConfigureAwait(false);
        Check.Equal(ResultCode.NotADb, cut.ExtendedCode & 0xFF, "a partial last page is NOTADB");
        await b.Settle().ConfigureAwait(false);
        Check.True(!await b.Vfs.AccessAsync(dst, AccessMode.Exists).ConfigureAwait(false) || await FileLength(b, dst).ConfigureAwait(false) == 0, "nothing was written for the rejected files");
    }

    private static async Task ImportTruncated(Backend b)
    {
        string src = Backend.Path("tsrc"), dst = Backend.Path("tdst");
        await DbTests.BuildDb(b, src, 50, false).ConfigureAwait(false);
        byte[] ok = await DatabaseTransfer.ExportAsync(b.Vfs, src).ConfigureAwait(false);
        await b.Settle().ConfigureAwait(false);
        using var ms = new MemoryStream(ok, 0, ok.Length - 100);
        OneLiteException e = await Check.Throws<OneLiteException>(() => DatabaseTransfer.ImportAsync(b.Vfs, dst, ms).AsTask(), "a stream that ends inside a page").ConfigureAwait(false);
        Check.Equal(ResultCode.NotADb, e.ExtendedCode & 0xFF, "a stream that ends inside a page is NOTADB");
        await b.Settle().ConfigureAwait(false);
        Check.Equal(0L, await FileLength(b, dst).ConfigureAwait(false), "the partial file was emptied");
    }

    /// <summary>A journal left over from the file being replaced would be played back over the imported pages. Import removes it.</summary>
    private static async Task ImportStaleJournal(Backend b)
    {
        string src = Backend.Path("jsrc"), dst = Backend.Path("jdst");
        await DbTests.BuildDb(b, src, 80, false).ConfigureAwait(false);
        byte[] ok = await DatabaseTransfer.ExportAsync(b.Vfs, src).ConfigureAwait(false);
        await b.Settle().ConfigureAwait(false);
        await DatabaseTransfer.ImportAsync(b.Vfs, dst, ok).ConfigureAwait(false);
        await b.Settle().ConfigureAwait(false);
        IVfsFile main = await b.Vfs.OpenAsync(dst, VfsOpenOptions.ReadWrite | VfsOpenOptions.MainDb).ConfigureAwait(false);
        IVfsFile j = await b.Vfs.OpenAsync(dst + "-journal", VfsOpenOptions.ReadWrite | VfsOpenOptions.Create | VfsOpenOptions.MainJournal).ConfigureAwait(false);
        await j.WriteAsync(0, new byte[2048]).ConfigureAwait(false);
        await j.SyncAsync(SyncMode.Full).ConfigureAwait(false);
        j.Dispose();
        main.Dispose();
        await b.Settle().ConfigureAwait(false);
        Check.True(await b.Vfs.AccessAsync(dst + "-journal", AccessMode.Exists).ConfigureAwait(false), "the stale journal is there");
        await DatabaseTransfer.ImportAsync(b.Vfs, dst, ok).ConfigureAwait(false);
        await b.Settle().ConfigureAwait(false);
        Check.True(!await b.Vfs.AccessAsync(dst + "-journal", AccessMode.Exists).ConfigureAwait(false), "import removed the stale journal");
        await Verify(b, dst, 80).ConfigureAwait(false);
    }

    private static async Task ExportMissing(Backend b)
    {
        OneLiteException e = await Check.Throws<OneLiteException>(() => DatabaseTransfer.ExportAsync(b.Vfs, Backend.Path("nothing")).AsTask(), "export of a missing file").ConfigureAwait(false);
        Check.Equal(ResultCode.CantOpen, e.ExtendedCode & 0xFF, "export of a missing file is CANTOPEN");
    }

    private static async Task<long> FileLength(Backend b, string path)
    {
        if (!await b.Vfs.AccessAsync(path, AccessMode.Exists).ConfigureAwait(false))
        {
            return 0;
        }

        IVfsFile f = await b.Vfs.OpenAsync(path, VfsOpenOptions.ReadOnly | VfsOpenOptions.MainDb).ConfigureAwait(false);
        long n = await f.FileSizeAsync().ConfigureAwait(false);
        f.Dispose();
        await b.Settle().ConfigureAwait(false);
        return n;
    }

    private static async Task Verify(Backend b, string path, int rows)
    {
        Drive d = await DbTests.OpenPrepared(b, path, DbTests.Opts()).ConfigureAwait(false);
        await d.BeginRead().ConfigureAwait(false);
        await DbContent.Verify(d, rows).ConfigureAwait(false);
        await d.EndRead().ConfigureAwait(false);
        await DbTests.Close(b, d).ConfigureAwait(false);
    }

    /// <summary>A stream that never returns more than a few bytes per read.</summary>
    private sealed class SlowStream(byte[] data, int chunk) : Stream
    {
        private int _at;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position { get => _at; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = Math.Min(Math.Min(buffer.Length, chunk), data.Length - _at);
            data.AsSpan(_at, n).CopyTo(buffer);
            _at += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, System.Threading.CancellationToken cancellationToken = default) => new(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
