using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using RecursiveAI.OneLite.Storage.Forensics;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Tests.Vfs;

/// <summary>A small HTTP server in the test process that serves byte arrays with range requests, validators and switches for the ways real servers misbehave.</summary>
internal sealed class RangeServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, Entry> _files = [];
    private readonly Task _loop;
    private int _requests;

    internal sealed class Entry(byte[] data)
    {
        public byte[] Data { get; set; } = data;

        public string? ETag { get; set; }

        public DateTimeOffset? Modified { get; set; }
    }

    public RangeServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        BaseUrl = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _loop = Task.Run(Serve);
    }

    public string BaseUrl { get; }

    /// <summary>Requests received, heads included.</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>Answer a range request with 200 and the whole body, as a server without range support does.</summary>
    public bool IgnoreRange { get; set; }

    public List<string> Log { get; } = [];

    public Entry Put(string name, byte[] data, string? etag, DateTimeOffset? modified)
    {
        lock (_files)
        {
            return _files[name] = new Entry(data) { ETag = etag, Modified = modified };
        }
    }

    public void Replace(string name, byte[] data, string? etag, DateTimeOffset? modified)
    {
        lock (_files)
        {
            _files[name].Data = data;
            _files[name].ETag = etag;
            _files[name].Modified = modified;
        }
    }

    private async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            try
            {
                Handle(ctx);
            }
            catch (Exception e) when (e is HttpListenerException or IOException)
            {
                // The client went away.
            }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        Interlocked.Increment(ref _requests);
        HttpListenerRequest rq = ctx.Request;
        HttpListenerResponse rs = ctx.Response;
        string name = rq.Url!.AbsolutePath.TrimStart('/');
        Entry? e;
        byte[] data;
        string? etag;
        DateTimeOffset? mod;
        lock (_files)
        {
            if (!_files.TryGetValue(name, out e))
            {
                rs.StatusCode = 404;
                rs.Close();
                return;
            }

            data = e.Data;
            etag = e.ETag;
            mod = e.Modified;
        }

        lock (Log)
        {
            Log.Add(rq.HttpMethod + " " + name + " " + rq.Headers["Range"] + " " + rq.Headers["If-Match"]);
        }

        if (etag is not null)
        {
            rs.Headers["ETag"] = etag;
        }

        if (mod is { } m)
        {
            rs.Headers["Last-Modified"] = m.ToString("R", CultureInfo.InvariantCulture);
        }

        rs.Headers["Accept-Ranges"] = "bytes";
        string? ifMatch = rq.Headers["If-Match"];
        if (ifMatch is not null && ifMatch != etag)
        {
            rs.StatusCode = 412;
            rs.Close();
            return;
        }

        string? range = rq.Headers["Range"];
        if (rq.HttpMethod == "HEAD")
        {
            rs.ContentLength64 = data.Length;
            rs.Close();
            return;
        }

        if (range is null || IgnoreRange)
        {
            rs.StatusCode = 200;
            rs.ContentLength64 = data.Length;
            rs.OutputStream.Write(data);
            rs.Close();
            return;
        }

        string[] parts = range["bytes=".Length..].Split('-');
        long from = long.Parse(parts[0], CultureInfo.InvariantCulture);
        long to = parts[1].Length == 0 ? data.Length - 1 : Math.Min(long.Parse(parts[1], CultureInfo.InvariantCulture), data.Length - 1);
        if (from >= data.Length)
        {
            rs.StatusCode = 416;
            rs.Headers["Content-Range"] = "bytes */" + data.Length;
            rs.Close();
            return;
        }

        rs.StatusCode = 206;
        rs.Headers["Content-Range"] = $"bytes {from}-{to}/{data.Length}";
        rs.ContentLength64 = to - from + 1;
        rs.OutputStream.Write(data, (int)from, (int)(to - from + 1));
        rs.Close();
    }

    public void Dispose()
    {
        _listener.Close();
        try
        {
            _loop.Wait(2000);
        }
        catch (AggregateException)
        {
        }
    }
}

/// <summary>Reading a database over HTTP range requests.</summary>
public sealed class HttpRangeVfsTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-http-" + Guid.NewGuid().ToString("N"));
    private readonly RangeServer _server = new();
    private readonly HttpClient _client = new();

    public HttpRangeVfsTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private byte[] BuildDatabase(string name, int rows, bool wal = false, string extraSql = "")
    {
        string path = Path.Combine(_dir, name);
        using (Database db = Database.Open(path, new DatabaseOptions { JournalMode = wal ? JournalMode.Wal : JournalMode.Delete }))
        using (Connection c = db.Connect())
        {
            Run(c, "create table t(a integer primary key, b text, c blob); create index tb on t(b)" + extraSql);
            Run(c, "begin");
            for (int i = 1; i <= rows; i++)
            {
                Run(c, $"insert into t values ({i}, 'row {i}', zeroblob(200))");
            }

            Run(c, "commit");
        }

        return File.ReadAllBytes(path);
    }

    private static void Run(Connection c, string sql)
    {
        string rest = sql;
        while (!string.IsNullOrWhiteSpace(rest))
        {
            using Statement st = c.Prepare(rest);
            rest = st.Tail;
            while (st.Step())
            {
            }
        }
    }

    private string Serve(string name, byte[] data, string? etag = "\"v1\"", bool modified = true)
    {
        _server.Put(name, data, etag, modified ? new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero) : null);
        return _server.BaseUrl + name;
    }

    [Fact]
    public async Task AsyncDriverQueriesARemoteDatabaseWithoutDownloadingIt()
    {
        byte[] bytes = BuildDatabase("a.db", 3000);
        string url = Serve("a.db", bytes);
        var vfs = new HttpRangeVfs(_client, new HttpRangeOptions { BlockSize = 4096, CachedBlocks = 1024 });
        using Database db = await Database.OpenReadOnlyAsync(url, new DatabaseOptions { Vfs = vfs }, Ct);
        await using Connection c = await db.ConnectAsync(Ct);
        await using (Statement st = await c.PrepareAsync("select b from t where a = 1234", Ct))
        {
            Assert.True(await st.StepAsync(Ct));
            Assert.Equal("row 1234", st.GetFieldValue<string>(0));
        }

        Assert.True(vfs.BytesFetched < bytes.Length / 4, $"fetched {vfs.BytesFetched} of {bytes.Length}");
        Assert.True(vfs.RequestCount < 20, $"requests {vfs.RequestCount}");

        await using (Statement st = await c.PrepareAsync("select count(*), sum(length(b)) from t", Ct))
        {
            Assert.True(await st.StepAsync(Ct));
            Assert.Equal(3000, st.GetFieldValue<long>(0));
        }

        // A second pass is served from the block cache.
        long before = vfs.RequestCount;
        await using (Statement st = await c.PrepareAsync("select count(*) from t", Ct))
        {
            Assert.True(await st.StepAsync(Ct));
        }

        Assert.Equal(before, vfs.RequestCount);
    }

    [Fact]
    public void SyncReadOnlyOpenWorksWhereBlockingIsAllowed()
    {
        byte[] bytes = BuildDatabase("s.db", 300);
        string url = Serve("s.db", bytes);
        var vfs = new HttpRangeVfs(_client);
        using Database db = Database.OpenReadOnly(url, new DatabaseOptions { Vfs = vfs });
        using Connection c = db.Connect();
        using Statement st = c.Prepare("select count(*) from t where b like 'row 1%'");
        Assert.True(st.Step());
        Assert.Equal(111, st.GetFieldValue<long>(0));
    }

    [Fact]
    public async Task ResultsMatchTheLocalFile()
    {
        byte[] bytes = BuildDatabase("m.db", 700);
        string url = Serve("m.db", bytes);
        string[] queries =
        [
            "select count(*), min(b), max(b) from t",
            "select b from t where a between 100 and 110 order by b desc",
            "select a from t where b = 'row 555'",
            "select sum(a) from t where a % 7 = 0",
            "select name, type from sqlite_schema order by name",
        ];
        using Database local = Database.OpenReadOnly(Path.Combine(_dir, "m.db"));
        using Connection lc = local.Connect();
        var vfs = new HttpRangeVfs(_client, new HttpRangeOptions { BlockSize = 4096, CachedBlocks = 4 });
        using Database remote = await Database.OpenReadOnlyAsync(url, new DatabaseOptions { Vfs = vfs }, Ct);
        await using Connection rc = await remote.ConnectAsync(Ct);
        foreach (string q in queries)
        {
            var want = new List<string>();
            using (Statement s = lc.Prepare(q))
            {
                while (s.Step())
                {
                    want.Add(Row(s));
                }
            }

            var got = new List<string>();
            await using (Statement s = await rc.PrepareAsync(q, Ct))
            {
                while (await s.StepAsync(Ct))
                {
                    got.Add(Row(s));
                }
            }

            Assert.Equal(want, got);
        }
    }

    private static string Row(Statement s)
    {
        var parts = new List<string>();
        for (int i = 0; i < s.ColumnCount; i++)
        {
            parts.Add(s.GetFieldValue<object?>(i)?.ToString() ?? "NULL");
        }

        return string.Join("|", parts);
    }

    [Fact]
    public void ForensicReaderOpensAndReadsARemoteDatabase()
    {
        byte[] bytes = BuildDatabase("f.db", 200);
        string url = Serve("f.db", bytes);
        var vfs = new HttpRangeVfs(_client);
        using ForensicDatabase f = ForensicDatabase.Open(url, new ForensicOptions { Vfs = vfs });
        Assert.Equal(bytes.Length, f.DatabaseFileLength);
        Assert.Equal(4096, f.PageSize);
        Assert.Null(f.Wal);
        Assert.Null(f.Journal);
    }

    [Fact]
    public async Task AFileThatChangesUnderAReaderFailsWithATypedError()
    {
        byte[] v1 = BuildDatabase("c.db", 2000);
        string url = Serve("c.db", v1);
        var vfs = new HttpRangeVfs(_client, new HttpRangeOptions { BlockSize = 4096, CachedBlocks = 2 });
        using Database db = await Database.OpenReadOnlyAsync(url, new DatabaseOptions { Vfs = vfs }, Ct);
        await using Connection c = await db.ConnectAsync(Ct);
        await using (Statement st = await c.PrepareAsync("select count(*) from t", Ct))
        {
            Assert.True(await st.StepAsync(Ct));
        }

        byte[] v2 = BuildDatabase("c2.db", 2500);
        _server.Replace("c.db", v2, "\"v2\"", new DateTimeOffset(2026, 2, 2, 3, 4, 5, TimeSpan.Zero));

        // Reads that need blocks the two-block cache no longer holds reach the server, which refuses the stale version.
        OneLiteException e = await Assert.ThrowsAsync<OneLiteException>(async () =>
        {
            await using Statement st = await c.PrepareAsync("select max(b), sum(length(c)) from t", Ct);
            while (await st.StepAsync(Ct))
            {
            }
        });
        Assert.Equal(ResultCode.IoErr, e.Code);
        Assert.Equal(266, e.ExtendedCode);
        Exception? inner = e;
        while (inner is not null and not RemoteFileChangedException)
        {
            inner = inner.InnerException;
        }

        Assert.NotNull(inner);

        // Opening again reads the new version.
        using Database fresh = await Database.OpenReadOnlyAsync(url, new DatabaseOptions { Vfs = new HttpRangeVfs(_client) }, Ct);
        await using Connection fc = await fresh.ConnectAsync(Ct);
        await using Statement q = await fc.PrepareAsync("select count(*) from t", Ct);
        Assert.True(await q.StepAsync(Ct));
        Assert.Equal(2500, q.GetFieldValue<long>(0));
    }

    [Fact]
    public async Task ALastModifiedOnlyServerIsCheckedToo()
    {
        byte[] v1 = BuildDatabase("l.db", 1500);
        string url = Serve("l.db", v1, etag: null);
        var vfs = new HttpRangeVfs(_client, new HttpRangeOptions { BlockSize = 4096, CachedBlocks = 1 });
        using Database db = await Database.OpenReadOnlyAsync(url, new DatabaseOptions { Vfs = vfs }, Ct);
        await using Connection c = await db.ConnectAsync(Ct);
        await using (Statement st = await c.PrepareAsync("select count(*) from t", Ct))
        {
            Assert.True(await st.StepAsync(Ct));
        }

        _server.Replace("l.db", BuildDatabase("l2.db", 1600), null, new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        OneLiteException e = await Assert.ThrowsAsync<OneLiteException>(async () =>
        {
            await using Statement st = await c.PrepareAsync("select max(b), sum(length(c)) from t", Ct);
            while (await st.StepAsync(Ct))
            {
            }
        });
        Assert.Equal(ResultCode.IoErr, e.Code);
    }

    [Fact]
    public async Task AWalModeDatabaseIsReadableWithoutALogAndRefusedWithOne()
    {
        byte[] bytes = BuildDatabase("w.db", 100, wal: true);
        Assert.Equal(2, bytes[18]);

        // Closed cleanly, the log is gone and the file is complete: reading it needs no wal-index.
        string url = Serve("w.db", bytes);
        using (Database db = await Database.OpenReadOnlyAsync(url, new DatabaseOptions { Vfs = new HttpRangeVfs(_client) }, Ct))
        await using (Connection c = await db.ConnectAsync(Ct))
        await using (Statement st = await c.PrepareAsync("select count(*) from t", Ct))
        {
            Assert.True(await st.StepAsync(Ct));
            Assert.Equal(100, st.GetFieldValue<long>(0));
        }

        // With a log next to it, committed pages may be only in the log: refuse rather than read an old state.
        string url2 = Serve("w2.db", bytes);
        Serve("w2.db-wal", new byte[32 + 24 + 4096]);
        OneLiteException e = await Assert.ThrowsAsync<OneLiteException>(async () =>
        {
            using Database db2 = await Database.OpenReadOnlyAsync(url2, new DatabaseOptions { Vfs = new HttpRangeVfs(_client) }, Ct);
            await using Connection c2 = await db2.ConnectAsync(Ct);
            await using Statement st = await c2.PrepareAsync("select count(*) from t", Ct);
            await st.StepAsync(Ct);
        });
        Assert.Equal(ResultCode.CantOpen, e.Code);
        Assert.Contains("write-ahead log", e.ToString(), StringComparison.Ordinal);

        // An empty log is a leftover, not data.
        string url3 = Serve("w3.db", bytes);
        Serve("w3.db-wal", []);
        using Database db3 = await Database.OpenReadOnlyAsync(url3, new DatabaseOptions { Vfs = new HttpRangeVfs(_client) }, Ct);
        await using Connection c3 = await db3.ConnectAsync(Ct);
        await using Statement s3 = await c3.PrepareAsync("select count(*) from t", Ct);
        Assert.True(await s3.StepAsync(Ct));
        Assert.Equal(100, s3.GetFieldValue<long>(0));
    }

    [Fact]
    public async Task AHotJournalNextToTheDatabaseIsRefused()
    {
        string url = Serve("j.db", BuildDatabase("j.db", 50));

        // A journal header (atomiccommit): the magic string, one record, a nonce, the original size, the sector size and the page size.
        var journal = new byte[512];
        new byte[] { 0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7 }.CopyTo(journal, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(journal.AsSpan(8), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(journal.AsSpan(16), 3);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(journal.AsSpan(20), 512);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(journal.AsSpan(24), 4096);
        Serve("j.db-journal", journal);
        OneLiteException e = await Assert.ThrowsAsync<OneLiteException>(async () =>
        {
            using Database db = await Database.OpenReadOnlyAsync(url, new DatabaseOptions { Vfs = new HttpRangeVfs(_client) }, Ct);
            await using Connection c = await db.ConnectAsync(Ct);
            await using Statement st = await c.PrepareAsync("select count(*) from t", Ct);
            await st.StepAsync(Ct);
        });
        Assert.Equal(ResultCode.CantOpen, e.Code);
        Assert.Contains("journal", e.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingFileAndMissingValidatorsAreCantOpen()
    {
        var vfs = new HttpRangeVfs(_client);
        OneLiteException e = await Assert.ThrowsAsync<OneLiteException>(async () => await Database.OpenReadOnlyAsync(_server.BaseUrl + "nope.db", new DatabaseOptions { Vfs = vfs }, Ct));
        Assert.Equal(ResultCode.CantOpen, e.Code);

        string url = Serve("nv.db", BuildDatabase("nv.db", 20), etag: null, modified: false);
        OneLiteException v = await Assert.ThrowsAsync<OneLiteException>(async () =>
        {
            using Database db = await Database.OpenReadOnlyAsync(url, new DatabaseOptions { Vfs = vfs }, Ct);
            await using Connection c = await db.ConnectAsync(Ct);
        });
        Assert.Equal(ResultCode.CantOpen, v.Code);
        Assert.Contains("neither ETag nor Last-Modified", v.ToString() + v.InnerException?.Message, StringComparison.Ordinal);

        var lax = new HttpRangeVfs(_client, new HttpRangeOptions { AllowNoValidator = true });
        using Database ok = await Database.OpenReadOnlyAsync(url, new DatabaseOptions { Vfs = lax }, Ct);
        await using Connection oc = await ok.ConnectAsync(Ct);
        await using Statement s = await oc.PrepareAsync("select count(*) from t", Ct);
        Assert.True(await s.StepAsync(Ct));
        Assert.Equal(20, s.GetFieldValue<long>(0));
    }

    [Fact]
    public async Task AServerWithoutRangeSupportServesAOneBlockFileOnly()
    {
        _server.IgnoreRange = true;
        byte[] small = BuildDatabase("small.db", 5);
        Assert.True(small.Length <= 65536);
        string url = Serve("small.db", small);
        using Database db = await Database.OpenReadOnlyAsync(url, new DatabaseOptions { Vfs = new HttpRangeVfs(_client) }, Ct);
        await using Connection c = await db.ConnectAsync(Ct);
        await using Statement st = await c.PrepareAsync("select count(*) from t", Ct);
        Assert.True(await st.StepAsync(Ct));
        Assert.Equal(5, st.GetFieldValue<long>(0));

        string big = Serve("big.db", BuildDatabase("big.db", 2000));
        OneLiteException e = await Assert.ThrowsAsync<OneLiteException>(async () => await Database.OpenReadOnlyAsync(big, new DatabaseOptions { Vfs = new HttpRangeVfs(_client) }, Ct).AsTask()
            .ContinueWith(async t => { using Database d = t.Result; await using Connection cc = await d.ConnectAsync(Ct); await using Statement s = await cc.PrepareAsync("select 1 from t", Ct); await s.StepAsync(Ct); }, Ct, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap());
        Assert.Equal(ResultCode.CantOpen, e.Code);
    }

    [Fact]
    public void WritingIsRefused()
    {
        string url = Serve("r.db", BuildDatabase("r.db", 10));
        var vfs = new HttpRangeVfs(_client);
        var e = Assert.Throws<VfsException>(() => vfs.Open(url, VfsOpenOptions.MainDb | VfsOpenOptions.ReadWrite | VfsOpenOptions.Create));
        Assert.Equal(VfsCodes.CantOpen, e.ExtendedCode);
        using IVfsFile f = vfs.Open(url, VfsOpenOptions.MainDb | VfsOpenOptions.ReadOnly);
        Assert.Equal(VfsCodes.ReadOnly, Assert.Throws<VfsException>(() => f.Write(0, new byte[4])).ExtendedCode);
    }

    [Fact]
    public void BlockSizeIsValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HttpRangeVfs(_client, new HttpRangeOptions { BlockSize = 5000 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HttpRangeVfs(_client, new HttpRangeOptions { CachedBlocks = 0 }));
    }
}
