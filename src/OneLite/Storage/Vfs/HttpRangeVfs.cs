using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace RecursiveAI.OneLite.Storage.Vfs;

/// <summary>Settings for <see cref="HttpRangeVfs"/>.</summary>
public sealed class HttpRangeOptions
{
    /// <summary>
    /// Bytes fetched per request: reads are served from aligned blocks of this size. A power of two from 4096 to 16 MiB. Default 65536. A larger block
    /// means fewer round trips on a scan and more bytes for a point lookup.
    /// </summary>
    public int BlockSize { get; init; } = 64 * 1024;

    /// <summary>Blocks kept per remote file; the least recently used block is dropped beyond this. Default 128 (8 MiB at the default block size).</summary>
    public int CachedBlocks { get; init; } = 128;

    /// <summary>
    /// Accept a server that sends neither an <c>ETag</c> nor a <c>Last-Modified</c> header. Default false: without a validator a file that changes
    /// between two requests cannot be told from an unchanged one, so opening it fails with CANTOPEN.
    /// </summary>
    public bool AllowNoValidator { get; init; }
}

/// <summary>
/// The remote file changed while a connection was reading it: the server answered a range request for a different version (its <c>ETag</c> or
/// <c>Last-Modified</c> differs from the one seen when the file was opened, or it refused the <c>If-Match</c>/<c>If-Unmodified-Since</c> condition).
/// Mixing pages of two versions would read a database that never existed, so the read fails. Reaches the caller as an
/// <c>OneLiteException</c> with SQLITE_IOERR_READ whose <c>InnerException</c> is this exception; open the file again to read the new version.
/// </summary>
public sealed class RemoteFileChangedException : VfsException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="url">The file's URL.</param>
    /// <param name="expected">The validator seen at open.</param>
    /// <param name="actual">The validator the server answered with, or a description of the refusal.</param>
    public RemoteFileChangedException(string url, string expected, string actual)
        : base(VfsCodes.IoErrRead, $"the remote file changed while it was open ({url}: expected {expected}, server has {actual})")
    {
        Url = url;
    }

    /// <summary>The file's URL.</summary>
    public string Url { get; }
}

/// <summary>
/// A read-only storage backend that reads a database from an HTTP server with range requests (RFC 9110 section 14), so a large database can be queried
/// without downloading it. Every call is asynchronous; on a platform that may block a thread (anything but the browser) a synchronous caller
/// (<c>OpenReadOnly</c>, the forensic reader) is served by waiting for the request.
/// </summary>
/// <remarks>
/// <para><b>Paths are URLs</b> (<c>https://host/app.db</c>, query string allowed). The file is opened read-only; opening for writing fails with CANTOPEN.</para>
/// <para><b>One version per open.</b> Opening fetches the first block and records the file's size and its <c>ETag</c> and <c>Last-Modified</c>. Every later
/// request carries <c>If-Match</c> (strong ETag) or <c>If-Unmodified-Since</c>, and the answer's validators are compared with the recorded ones; a difference
/// fails the read with <see cref="RemoteFileChangedException"/>. Blocks are cached per VFS instance and file version.</para>
/// <para><b>Rollback-journal databases only.</b> A <c>-journal</c> next to the file makes opening fail (it may be hot, and nothing can be written to play it
/// back), and a database in WAL mode fails at the first read with CANTOPEN, because its wal-index needs shared memory and a writable <c>-shm</c>. Convert a
/// database to <c>journal_mode=DELETE</c> before publishing it.</para>
/// <para>The server must answer <c>Range</c> requests with 206, or the whole file with 200 when it is at most one block. Cross-origin use from a browser
/// needs CORS to allow the <c>Range</c> and <c>If-Match</c> headers.</para>
/// </remarks>
public sealed class HttpRangeVfs : VfsBase
{
    private readonly HttpClient _client;
    private readonly HttpRangeOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<string, RemoteFile> _files = new(StringComparer.Ordinal);
    private long _requests;
    private long _bytes;

    /// <summary>Creates the backend over an <see cref="HttpClient"/> the caller owns (and configures: base address, headers, handler, timeout).</summary>
    /// <param name="client">The client. Relative paths are resolved against its <see cref="HttpClient.BaseAddress"/>.</param>
    /// <param name="options">Settings; null means the defaults.</param>
    /// <exception cref="ArgumentOutOfRangeException">The block size is not a power of two between 4096 and 16 MiB, or the block count is below 1.</exception>
    public HttpRangeVfs(HttpClient client, HttpRangeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _options = options ?? new HttpRangeOptions();
        int bs = _options.BlockSize;
        if (bs < 4096 || bs > 16 * 1024 * 1024 || (bs & (bs - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "BlockSize must be a power of two from 4096 to 16 MiB");
        }

        if (_options.CachedBlocks < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "CachedBlocks must be at least 1");
        }
    }

    /// <inheritdoc/>
    public override string Name => "http-range";

    /// <inheritdoc/>
    public override VfsCaps Caps => VfsCaps.Async | VfsCaps.ReadOnly | (OperatingSystem.IsBrowser() ? 0 : VfsCaps.BlockingOk);

    /// <summary>How many HTTP requests this backend has sent (heads and ranges).</summary>
    public long RequestCount => Interlocked.Read(ref _requests);

    /// <summary>How many body bytes this backend has downloaded.</summary>
    public long BytesFetched => Interlocked.Read(ref _bytes);

    /// <inheritdoc/>
    public override string FullPathname(string path) => path;

    /// <inheritdoc/>
    public override async ValueTask<IVfsFile> OpenAsync(string path, VfsOpenOptions flags, CancellationToken ct = default)
    {
        if ((flags & (VfsOpenOptions.ReadWrite | VfsOpenOptions.Create)) != 0 || (flags & VfsOpenOptions.ReadOnly) == 0)
        {
            throw new VfsException(VfsCodes.CantOpen, "HTTP storage is read-only: '" + path + "' cannot be opened for writing");
        }

        const VfsOpenOptions engineAux = VfsOpenOptions.Wal | VfsOpenOptions.MainJournal | VfsOpenOptions.SubJournal | VfsOpenOptions.SuperJournal
            | VfsOpenOptions.TempDb | VfsOpenOptions.TempJournal;
        if ((flags & engineAux) != 0)
        {
            // The engine asks for these only to take part in a transaction: a log or journal that exists next to the file cannot be used by a read-only backend.
            string kind = (flags & VfsOpenOptions.Wal) != 0 ? "a write-ahead log (a WAL-mode database can be read over HTTP only when it has no -wal file: checkpoint it, or convert it to journal_mode=DELETE)"
                : "a journal (a journal next to a remote database may be hot, and a read-only backend cannot play it back)";
            throw new VfsException(VfsCodes.CantOpen, "unable to open " + kind + ": " + path);
        }

        Uri uri = Resolve(path);
        string key = uri.AbsoluteUri;
        using HttpRequestMessage req = RangeRequest(uri, 0, _options.BlockSize - 1, null);
        using HttpResponseMessage resp = await SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.Gone)
        {
            throw new VfsException(VfsCodes.CantOpen, $"unable to open database file: {key} answered {(int)resp.StatusCode}");
        }

        long size;
        byte[] first;
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // An empty file has no byte 0 to ask for; the answer carries "bytes */0".
            size = resp.Content.Headers.ContentRange?.Length ?? 0;
            if (size != 0)
            {
                throw new VfsException(VfsCodes.CantOpen, $"unable to open database file: {key} answered 416 for a file of {size} bytes");
            }

            first = [];
        }
        else if (resp.StatusCode == HttpStatusCode.PartialContent)
        {
            first = await ReadBody(resp, ct).ConfigureAwait(false);
            size = resp.Content.Headers.ContentRange?.Length
                ?? throw new VfsException(VfsCodes.CantOpen, $"unable to open database file: {key} sent a 206 without the file's size");
        }
        else if (resp.StatusCode == HttpStatusCode.OK)
        {
            // A server that ignores Range sends the whole file: acceptable only when that is no more than one block, which is then the whole file.
            long? len = resp.Content.Headers.ContentLength;
            if (len is null || len > _options.BlockSize)
            {
                throw new VfsException(VfsCodes.CantOpen, $"unable to open database file: {key} does not honour range requests (answered 200 with {(len is null ? "an unknown length" : len + " bytes")})");
            }

            first = await ReadBody(resp, ct).ConfigureAwait(false);
            size = first.Length;
        }
        else
        {
            throw new VfsException(VfsCodes.CantOpen, $"unable to open database file: {key} answered {(int)resp.StatusCode}");
        }

        string? etag = resp.Headers.ETag?.ToString();
        DateTimeOffset? modified = resp.Content.Headers.LastModified;
        if (etag is null && modified is null && !_options.AllowNoValidator)
        {
            throw new VfsException(VfsCodes.CantOpen, $"unable to open database file: {key} sent neither ETag nor Last-Modified, so a change under a reader could not be detected");
        }

        RemoteFile file;
        lock (_gate)
        {
            // The same version opened again shares its blocks; a different version starts over (the old handle fails at its next request).
            if (!_files.TryGetValue(key, out file!) || file.Size != size || file.ETag != etag || file.Modified != modified)
            {
                file = new RemoteFile(this, uri, size, etag, modified);
                _files[key] = file;
            }
        }

        file.Seed(0, first);
        if ((flags & VfsOpenOptions.MainDb) != 0)
        {
            await RefuseUnusableLogsAsync(uri, key, first, ct).ConfigureAwait(false);
        }

        return new HttpFile(file);
    }

    // A journal that holds data may be hot (an unfinished commit that a writer would play back), and a log that holds frames may carry commits the database
    // file does not have yet. A read-only backend can do neither, and reading the file anyway would show a state that never was; so it refuses at open
    // (atomiccommit "hot journal", wal "Read-Only Databases"). Empty ones are leftovers and harmless.
    private async ValueTask RefuseUnusableLogsAsync(Uri uri, string key, byte[] first, CancellationToken ct)
    {
        string suffixed(string suffix) => key.Contains('?', StringComparison.Ordinal) ? key[..key.IndexOf('?', StringComparison.Ordinal)] + suffix + key[key.IndexOf('?', StringComparison.Ordinal)..] : key + suffix;
        if (await NonEmptyAsync(new Uri(suffixed("-journal")), ct).ConfigureAwait(false))
        {
            throw new VfsException(VfsCodes.CantOpen, $"unable to open database file: {key} has a journal next to it, which may be hot; a read-only HTTP backend cannot play it back");
        }

        bool walMode = first.Length > 19 && (first[18] == 2 || first[19] == 2);
        if (walMode && await NonEmptyAsync(new Uri(suffixed("-wal")), ct).ConfigureAwait(false))
        {
            throw new VfsException(VfsCodes.CantOpen, $"unable to open database file: {key} is in WAL mode and has a write-ahead log next to it; a read-only HTTP backend cannot read a log (checkpoint the database first)");
        }
    }

    private async ValueTask<bool> NonEmptyAsync(Uri uri, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Head, uri);
        using HttpResponseMessage resp = await SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.Gone)
        {
            return false;
        }

        if (!resp.IsSuccessStatusCode)
        {
            throw new VfsException(VfsCodes.IoErrAccess, $"{uri} answered {(int)resp.StatusCode} to a HEAD request");
        }

        return resp.Content.Headers.ContentLength is not 0;
    }

    /// <inheritdoc/>
    public override async ValueTask<bool> AccessAsync(string path, AccessMode mode, CancellationToken ct = default)
    {
        if (mode == AccessMode.ReadWrite)
        {
            return false;
        }

        Uri uri = Resolve(path);
        string? main = AuxOwner(uri);
        RemoteFile? owner = null;
        lock (_gate)
        {
            if (main is not null && _files.TryGetValue(main, out RemoteFile? f))
            {
                owner = f;
                if (f.AuxExists.TryGetValue(uri.AbsoluteUri, out bool known))
                {
                    return known;
                }
            }
        }

        bool exists = await HeadAsync(uri, path, ct).ConfigureAwait(false);
        if (owner is not null)
        {
            lock (_gate)
            {
                owner.AuxExists[uri.AbsoluteUri] = exists;
            }
        }

        return exists;
    }

    // The -journal, -wal and -shm names of an open file are looked up once per file version: a later change is caught by the validators anyway, and the
    // engine looks for a hot journal at the start of every read transaction.
    private static string? AuxOwner(Uri uri)
    {
        string s = uri.AbsoluteUri;
        int q = s.IndexOf('?', StringComparison.Ordinal);
        string head = q < 0 ? s : s[..q];
        string tail = q < 0 ? string.Empty : s[q..];
        foreach (string suffix in (ReadOnlySpan<string>)["-journal", "-wal", "-shm"])
        {
            if (head.EndsWith(suffix, StringComparison.Ordinal))
            {
                return head[..^suffix.Length] + tail;
            }
        }

        return null;
    }

    private async ValueTask<bool> HeadAsync(Uri uri, string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Head, uri);
        using HttpResponseMessage resp = await SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.Gone)
        {
            return false;
        }

        if (!resp.IsSuccessStatusCode)
        {
            throw new VfsException(VfsCodes.IoErrAccess, $"{path} answered {(int)resp.StatusCode} to a HEAD request");
        }

        return true;
    }

    /// <inheritdoc/>
    public override ValueTask DeleteAsync(string path, bool syncDirectory, CancellationToken ct = default)
        => throw new VfsException(VfsCodes.ReadOnly, "HTTP storage is read-only");

    /// <inheritdoc/>
    public override IVfsFile Open(string path, VfsOpenOptions flags) => Flavours.Block(OpenAsync(path, flags, default), Caps);

    /// <inheritdoc/>
    public override bool Access(string path, AccessMode mode) => Flavours.Block(AccessAsync(path, mode, default), Caps);

    /// <inheritdoc/>
    public override void Delete(string path, bool syncDirectory) => throw new VfsException(VfsCodes.ReadOnly, "HTTP storage is read-only");

    private Uri Resolve(string path)
    {
        // The pager names a journal or log by appending to the database path, which puts the suffix after a query string; move it before the query.
        foreach (string suffix in (ReadOnlySpan<string>)["-journal", "-wal", "-shm"])
        {
            if (path.EndsWith(suffix, StringComparison.Ordinal) && path.Contains('?', StringComparison.Ordinal))
            {
                string head = path[..^suffix.Length];
                int q = head.IndexOf('?', StringComparison.Ordinal);
                path = head[..q] + suffix + head[q..];
                break;
            }
        }

        if (Uri.TryCreate(path, UriKind.Absolute, out Uri? abs) && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
        {
            return abs;
        }

        if (_client.BaseAddress is not null && Uri.TryCreate(_client.BaseAddress, path, out Uri? rel))
        {
            return rel;
        }

        throw new VfsException(VfsCodes.CantOpen, $"unable to open database file: '{path}' is not an http or https URL");
    }

    private static HttpRequestMessage RangeRequest(Uri uri, long from, long to, RemoteFile? version)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Range = new RangeHeaderValue(from, to);
        if (version is not null)
        {
            if (version.ETag is { } tag && !tag.StartsWith("W/", StringComparison.Ordinal))
            {
                req.Headers.TryAddWithoutValidation("If-Match", tag);
            }
            else if (version.Modified is { } m)
            {
                req.Headers.IfUnmodifiedSince = m;
            }
        }

        return req;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        try
        {
            return await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new VfsException(VfsCodes.IoErrRead, $"request for {req.RequestUri} failed: {e.Message}", e);
        }
    }

    private async Task<byte[]> ReadBody(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            byte[] body = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            Interlocked.Add(ref _bytes, body.Length);
            return body;
        }
        catch (HttpRequestException e)
        {
            throw new VfsException(VfsCodes.IoErrRead, $"reading the body from {resp.RequestMessage?.RequestUri} failed: {e.Message}", e);
        }
    }

    /// <summary>One version of one remote file and its cached blocks.</summary>
    private sealed class RemoteFile(HttpRangeVfs owner, Uri uri, long size, string? etag, DateTimeOffset? modified)
    {
        private readonly Dictionary<long, (Task<byte[]> Block, long Stamp)> _blocks = [];
        private long _clock;

        public long Size { get; } = size;

        public string? ETag { get; } = etag;

        public DateTimeOffset? Modified { get; } = modified;

        public Uri Uri { get; } = uri;

        /// <summary>Whether the names next to the file (-journal, -wal, -shm) exist, by absolute URL, as last asked.</summary>
        public Dictionary<string, bool> AuxExists { get; } = [];

        public void Seed(long block, byte[] data)
        {
            lock (owner._gate)
            {
                if (!_blocks.ContainsKey(block))
                {
                    Insert(block, Task.FromResult(data));
                }
            }
        }

        public ValueTask<byte[]> GetBlockAsync(long block, CancellationToken ct)
        {
            Task<byte[]> t;
            lock (owner._gate)
            {
                if (_blocks.TryGetValue(block, out (Task<byte[]> Block, long Stamp) hit))
                {
                    _blocks[block] = (hit.Block, ++_clock);
                    t = hit.Block;
                }
                else
                {
                    // The fetch is shared by every caller that wants this block; it is not cancelled by one caller's token (a cancelled caller just stops waiting).
                    t = FetchAsync(block);
                    Insert(block, t);
                }
            }

            return new ValueTask<byte[]>(t.WaitAsync(ct));
        }

        private void Insert(long block, Task<byte[]> t)
        {
            _blocks[block] = (t, ++_clock);
            if (_blocks.Count > owner._options.CachedBlocks)
            {
                long oldest = -1;
                long stamp = long.MaxValue;
                foreach (KeyValuePair<long, (Task<byte[]> Block, long Stamp)> kv in _blocks)
                {
                    if (kv.Key != block && kv.Value.Stamp < stamp)
                    {
                        stamp = kv.Value.Stamp;
                        oldest = kv.Key;
                    }
                }

                _blocks.Remove(oldest);
            }
        }

        private async Task<byte[]> FetchAsync(long block)
        {
            try
            {
                return await DoFetch(block).ConfigureAwait(false);
            }
            catch
            {
                // A failed block must not be remembered: the next read asks again.
                lock (owner._gate)
                {
                    _blocks.Remove(block);
                }

                throw;
            }
        }

        private async Task<byte[]> DoFetch(long block)
        {
            long from = block * owner._options.BlockSize;
            long to = Math.Min(Size, from + owner._options.BlockSize) - 1;
            using HttpRequestMessage req = RangeRequest(Uri, from, to, this);
            using HttpResponseMessage resp = await owner.SendAsync(req, CancellationToken.None).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                throw new RemoteFileChangedException(Uri.ToString(), Describe(ETag, Modified), "a refused If-Match/If-Unmodified-Since condition (412)");
            }

            if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                throw new RemoteFileChangedException(Uri.ToString(), $"{Size} bytes", $"fewer than {from + 1} bytes (416)");
            }

            if (resp.StatusCode != HttpStatusCode.PartialContent)
            {
                throw new VfsException(VfsCodes.IoErrRead, $"{Uri} answered {(int)resp.StatusCode} to a range request");
            }

            string? tag = resp.Headers.ETag?.ToString();
            DateTimeOffset? mod = resp.Content.Headers.LastModified;
            if ((ETag is not null && tag != ETag) || (ETag is null && Modified is not null && mod != Modified))
            {
                throw new RemoteFileChangedException(Uri.ToString(), Describe(ETag, Modified), Describe(tag, mod));
            }

            ContentRangeHeaderValue? cr = resp.Content.Headers.ContentRange;
            if (cr?.Length is { } total && total != Size)
            {
                throw new RemoteFileChangedException(Uri.ToString(), $"{Size} bytes", $"{total} bytes");
            }

            byte[] body = await owner.ReadBody(resp, CancellationToken.None).ConfigureAwait(false);
            long want = to - from + 1;
            if (body.Length != want || cr?.From != from)
            {
                throw new VfsException(VfsCodes.IoErrRead, $"{Uri} answered bytes {cr?.From}-{cr?.To} ({body.Length} bytes) for a request of bytes {from}-{to}");
            }

            return body;
        }

        private static string Describe(string? tag, DateTimeOffset? mod)
            => tag ?? (mod is { } m ? "Last-Modified " + m.ToString("R", CultureInfo.InvariantCulture) : "no validator");

        public async ValueTask<int> ReadAsync(long offset, Memory<byte> dst, CancellationToken ct)
        {
            if (offset < 0 || offset >= Size || dst.Length == 0)
            {
                return 0;
            }

            int bs = owner._options.BlockSize;
            int total = (int)Math.Min(dst.Length, Size - offset);
            int done = 0;
            while (done < total)
            {
                long pos = offset + done;
                long block = pos / bs;
                byte[] data = await GetBlockAsync(block, ct).ConfigureAwait(false);
                int within = (int)(pos - block * bs);
                int n = Math.Min(total - done, data.Length - within);
                data.AsMemory(within, n).CopyTo(dst[done..]);
                done += n;
            }

            return total;
        }
    }

    private sealed class HttpFile(RemoteFile file) : VfsFileBase
    {
        private const string ReadOnlyMessage = "HTTP storage is read-only";

        public override VfsCaps Caps => VfsCaps.Async | VfsCaps.ReadOnly | (OperatingSystem.IsBrowser() ? 0 : VfsCaps.BlockingOk);

        public override int SectorSize => 4096;

        public override DeviceCharacteristics Device => DeviceCharacteristics.Immutable;

        public override ValueTask<int> ReadAsync(long offset, Memory<byte> dst, CancellationToken ct = default) => file.ReadAsync(offset, dst, ct);

        public override ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> src, CancellationToken ct = default) => throw new VfsException(VfsCodes.ReadOnly, ReadOnlyMessage);

        public override void Write(long offset, ReadOnlySpan<byte> src) => throw new VfsException(VfsCodes.ReadOnly, ReadOnlyMessage);

        public override ValueTask SyncAsync(SyncMode mode, CancellationToken ct = default) => ValueTask.CompletedTask;

        public override void Sync(SyncMode mode)
        {
        }

        public override ValueTask SyncDirectoryAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public override void SyncDirectory()
        {
        }

        public override ValueTask TruncateAsync(long size, CancellationToken ct = default) => throw new VfsException(VfsCodes.ReadOnly, ReadOnlyMessage);

        public override void Truncate(long size) => throw new VfsException(VfsCodes.ReadOnly, ReadOnlyMessage);

        public override ValueTask<long> FileSizeAsync(CancellationToken ct = default) => new(file.Size);

        public override long FileSize() => file.Size;

        // The file never changes under an open handle (a changed file fails the read), so a shared lock is always granted and there is no writer to exclude.
        public override ValueTask<bool> LockAsync(LockLevel level, CancellationToken ct = default) => new(level <= LockLevel.Shared);

        public override bool Lock(LockLevel level) => level <= LockLevel.Shared;

        public override ValueTask UnlockAsync(LockLevel level, CancellationToken ct = default) => ValueTask.CompletedTask;

        public override void Unlock(LockLevel level)
        {
        }

        public override ValueTask<bool> CheckReservedLockAsync(CancellationToken ct = default) => new(false);

        public override bool CheckReservedLock() => false;

        public override void Dispose()
        {
        }
    }
}
