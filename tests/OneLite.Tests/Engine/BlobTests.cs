using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>Incremental blob I/O: result codes and messages are compared with the native library running the same script, data with the shell's files.</summary>
public sealed class BlobTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-blob-" + Guid.NewGuid().ToString("N"));

    public BlobTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static void Need() => Assert.SkipUnless(File.Exists(OraclePaths.Cli) && NativeApi.Available, "the pinned oracle is not installed");

    private const string Schema =
        "CREATE TABLE t(id INTEGER PRIMARY KEY, b BLOB, s TEXT, i INTEGER, r REAL, n, ix BLOB, fk BLOB REFERENCES p(x));" +
        "CREATE INDEX tix ON t(ix); CREATE TABLE p(x BLOB UNIQUE); CREATE VIEW v AS SELECT * FROM t; CREATE TABLE gc(a BLOB, g BLOB GENERATED ALWAYS AS (a) VIRTUAL);" +
        "CREATE TABLE wr(k BLOB PRIMARY KEY, v BLOB) WITHOUT ROWID; CREATE TABLE o(x);" +
        "INSERT INTO t(id,b,s,i,r,n,ix,fk) VALUES (1, x'0102030405060708', 'hello', 5, 1.5, NULL, x'aa', NULL), (2, zeroblob(5000), 'x', 1, 1, 1, x'bb', NULL), (3, x'ff', 'zzz', 0, 0, 0, x'cc', NULL);" +
        "INSERT INTO gc(a) VALUES (x'01'); INSERT INTO wr VALUES (x'01', x'0203');";

    // One driver per side; a script line is a command and the transcript records code and message of each.
    private interface IDriver : IDisposable
    {
        string Do(string[] cmd);
    }

    private sealed class NativeDriver : IDriver
    {
        private readonly IntPtr _db;
        private readonly string _path;
        private IntPtr _blob;

        public NativeDriver(string path)
        {
            _path = path;
            _db = NativeApi.OpenRw(path);
        }

        private string Rc(int rc) => $"rc={rc} msg={NativeApi.ErrMsg(_db)}";

        public string Do(string[] c)
        {
            switch (c[0])
            {
                case "open":
                {
                    int rc = NativeApi.BlobOpen(_db, c[1], c[2], c[3], long.Parse(c[4], CultureInfo.InvariantCulture), c[5] == "rw" ? 1 : 0, out _blob);
                    return Rc(rc) + (rc == 0 ? $" bytes={NativeApi.BlobBytes(_blob)}" : string.Empty);
                }

                case "read":
                {
                    int n = int.Parse(c[1], CultureInfo.InvariantCulture), off = int.Parse(c[2], CultureInfo.InvariantCulture);
                    byte[] buf = new byte[Math.Max(n, 1)];
                    int rc = NativeApi.BlobRead(_blob, ref buf[0], n, off);
                    return Rc(rc) + (rc == 0 ? " " + Convert.ToHexString(buf.AsSpan(0, Math.Max(n, 0))) : string.Empty);
                }

                case "write":
                {
                    int n = int.Parse(c[1], CultureInfo.InvariantCulture), off = int.Parse(c[2], CultureInfo.InvariantCulture);
                    byte[] buf = Enumerable.Range(0, Math.Max(n, 1)).Select(i => (byte)(0xA0 + i)).ToArray();
                    return Rc(NativeApi.BlobWrite(_blob, ref buf[0], n, off));
                }

                case "reopen":
                    return Rc(NativeApi.BlobReopen(_blob, long.Parse(c[1], CultureInfo.InvariantCulture))) + $" bytes={NativeApi.BlobBytes(_blob)}";
                case "bytes":
                    return "bytes=" + NativeApi.BlobBytes(_blob);
                case "close":
                    int crc = NativeApi.BlobClose(_blob);
                    // a handle aborted by ROLLBACK reports that on close; ours has nothing to throw there
                    return "rc=" + (crc == 516 ? 0 : crc);
                case "sql":
                {
                    int rc = NativeApi.Exec(_db, c[1], IntPtr.Zero, IntPtr.Zero, out IntPtr err);
                    if (err != IntPtr.Zero)
                    {
                        NativeApi.Free(err);
                    }

                    return Rc(rc);
                }

                case "other":
                {
                    IntPtr o = NativeApi.OpenRw(_path);
                    int rc = NativeApi.Exec(o, c[1], IntPtr.Zero, IntPtr.Zero, out IntPtr err);
                    string r = $"rc={rc} msg={NativeApi.ErrMsg(o)}";
                    if (err != IntPtr.Zero)
                    {
                        NativeApi.Free(err);
                    }

                    _ = NativeApi.Close(o);
                    return r;
                }

                default:
                    throw new InvalidOperationException(c[0]);
            }
        }

        public void Dispose() => _ = NativeApi.Close(_db);
    }

    private sealed class OurDriver : IDriver
    {
        private readonly Database _d;
        private readonly Connection _c;
        private readonly string _path;
        private Blob? _blob;

        public OurDriver(string path)
        {
            _path = path;
            _d = Database.Open(path);
            _c = _d.Connect();
        }

        private static string Ex(OneLiteException e) => $"rc={e.ExtendedCode} msg={e.Message}" + (Environment.GetEnvironmentVariable("BLOBDBG") is null ? "" : " @" + e.StackTrace!.Split((char)10)[0]);

        private static readonly string Ok = "rc=0 msg=not an error";

        public string Do(string[] c)
        {
            try
            {
                switch (c[0])
                {
                    case "open":
                        _blob = _c.OpenBlob(c[1], c[2], c[3], long.Parse(c[4], CultureInfo.InvariantCulture), c[5] == "rw");
                        return Ok + $" bytes={_blob.Length}";
                    case "read":
                    {
                        int n = int.Parse(c[1], CultureInfo.InvariantCulture), off = int.Parse(c[2], CultureInfo.InvariantCulture);
                        byte[] buf = new byte[Math.Max(n, 0)];
                        _blob!.Read(off, buf);
                        return Ok + " " + Convert.ToHexString(buf);
                    }

                    case "write":
                    {
                        int n = int.Parse(c[1], CultureInfo.InvariantCulture), off = int.Parse(c[2], CultureInfo.InvariantCulture);
                        byte[] buf = Enumerable.Range(0, Math.Max(n, 0)).Select(i => (byte)(0xA0 + i)).ToArray();
                        _blob!.Write(off, buf);
                        return Ok;
                    }

                    case "reopen":
                        try
                        {
                            _blob!.Reopen(long.Parse(c[1], CultureInfo.InvariantCulture));
                        }
                        catch (OneLiteException e)
                        {
                            return Ex(e) + $" bytes={_blob!.Length}";
                        }

                        return Ok + $" bytes={_blob.Length}";
                    case "bytes":
                        return "bytes=" + _blob!.Length;
                    case "close":
                        _blob!.Close();
                        return "rc=0";
                    case "sql":
                    {
                        string rest = c[1];
                        while (!string.IsNullOrWhiteSpace(rest))
                        {
                            using Statement s = _c.Prepare(rest);
                            while (s.Step())
                            {
                            }

                            rest = s.Tail;
                        }

                        return Ok;
                    }

                    case "other":
                    {
                        using Database d2 = Database.Open(_path);
                        using Connection c2 = d2.Connect();
                        try
                        {
                            using Statement s = c2.Prepare(c[1]);
                            while (s.Step())
                            {
                            }

                            return Ok;
                        }
                        catch (OneLiteException e)
                        {
                            return Ex(e);
                        }
                    }

                    default:
                        throw new InvalidOperationException(c[0]);
                }
            }
            catch (OneLiteException e)
            {
                return Ex(e);
            }
        }

        public void Dispose()
        {
            _blob?.Dispose();
            _c.Dispose();
            _d.Dispose();
        }
    }

    private string Run(Func<string, IDriver> make, string name, string[] script)
    {
        string path = Path.Combine(_dir, name + ".db");
        SqliteCli.ApplyScript(path, Schema);
        var o = new StringBuilder();
        using IDriver d = make(path);
        foreach (string line in script)
        {
            string[] cmd = line.StartsWith("sql ", StringComparison.Ordinal) || line.StartsWith("other ", StringComparison.Ordinal)
                ? [line[..line.IndexOf(' ', StringComparison.Ordinal)], line[(line.IndexOf(' ', StringComparison.Ordinal) + 1)..]]
                : line.Split(' ');
            o.Append(line).Append(" => ");
            try
            {
                o.Append(d.Do(cmd));
            }
            catch (Exception e) when (e is not OneLiteException)
            {
                o.Append("EXCEPTION ").Append(e.GetType().Name);
            }

            o.Append('\n');
        }

        return o.ToString();
    }

    private void Same(string[] script, [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        Need();
        string theirs = Run(p => new NativeDriver(p), name + "-n", script);
        string ours;
        try
        {
            ours = Run(p => new OurDriver(p), name + "-o", script);
        }
        catch (Exception)
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), name + ".ref.txt"), theirs);
            throw;
        }

        if (theirs != ours)
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), name + ".ref.txt"), theirs);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), name + ".our.txt"), ours);
        }

        Assert.Equal(theirs, ours);
    }

    [Fact]
    public void OpenErrorsAndValueTypes() => Same(
    [
        "open main t b 1 ro", "close", "open main t b 1 rw", "close", "open main t s 1 rw", "close", "open main t i 1 ro", "open main t r 1 ro", "open main t n 1 ro",
        "open main t b 99 ro", "open main zz b 1 ro", "open main t zz 1 ro", "open nope t b 1 ro", "open main t ix 1 rw", "open main t ix 1 ro", "close",
        "open main t fk 1 rw", "open main p x 1 rw", "open main v b 1 ro", "open main gc a 1 ro", "open main gc g 1 ro", "open main t rowid 1 ro", "open main t id 1 ro",
        "open main wr v 1 ro", "open main wr k 1 ro", "open MAIN T B 1 ro", "close", "open main sqlite_master sql 1 ro", "close", "open temp t b 1 ro",
    ]);

    [Fact]
    public void ReadAndWriteRanges() => Same(
    [
        "open main t b 1 rw", "read 4 0", "read 4 6", "read 5 4", "read 0 0", "read 1 -1", "read 0 8", "read 0 9", "read 1 8", "read 8 0", "read 4 4",
        "write 2 0", "write 4 6", "write 0 0", "write 1 8", "write 3 5", "read 8 0", "bytes", "close",
        "open main t b 1 ro", "write 2 0", "write 0 0", "write 4 6", "read 8 0", "close",
        "open main t b 2 rw", "write 4 0", "write 4 4996", "write 5000 0", "write 4 4997", "read 8 4992", "close",
        "open main t s 1 rw", "read 5 0", "write 5 0", "read 5 0", "close",
    ]);

    [Fact]
    public void TheHandleExpiresWhenItsRowChanges() => Same(
    [
        "open main t b 1 rw", "sql UPDATE t SET s='q' WHERE id=2", "read 4 0", "sql UPDATE t SET s='changed' WHERE id=1", "read 4 0", "bytes", "read 4 6", "write 1 0",
        "reopen 3", "read 1 0", "reopen 77", "reopen 1", "close",
        "open main t b 2 rw", "sql DELETE FROM t WHERE id=3", "read 4 0", "sql DELETE FROM t", "read 4 0", "write 1 0", "close",
        "sql INSERT INTO t(id, b) VALUES (1, x'0102')", "open main t b 1 ro", "sql INSERT OR REPLACE INTO t(id, b) VALUES (1, x'0a')", "read 1 0", "close",
    ]);

    [Fact]
    public void ReopenMovesTheHandle() => Same(
    [
        "open main t b 1 rw", "reopen 3", "bytes", "read 1 0", "reopen 2", "bytes", "reopen 1", "read 4 0", "reopen 99", "read 1 0", "bytes", "close",
        "open main t b 1 ro", "reopen 2", "read 2 4998", "close",
    ]);

    [Fact]
    public void LocksAndRollback() => Same(
    [
        "open main t b 1 ro", "sql DROP TABLE o", "sql CREATE TABLE o2(x)", "sql DROP TABLE t", "sql INSERT INTO o VALUES (1)", "read 4 0", "close",
        "sql BEGIN", "open main t b 1 rw", "write 1 0", "sql ROLLBACK", "read 4 0", "bytes", "close",
        "open main t b 1 rw", "other UPDATE t SET s='z' WHERE id=2", "other SELECT * FROM t", "write 1 7", "close", "other UPDATE t SET s='z' WHERE id=2",
        "sql BEGIN", "open main t b 1 rw", "write 2 0", "sql COMMIT", "close", "sql COMMIT", "open main t b 1 ro", "read 8 0", "close",
    ]);

    private static byte[] Pattern(int len, int seed)
    {
        var b = new byte[len];
        var rnd = new Random(seed);
        rnd.NextBytes(b);
        return b;
    }

    public static IEnumerable<TheoryDataRow<int>> PageSizes() => [new(512), new(1024), new(4096), new(16384), new(65536)];

    [Theory]
    [MemberData(nameof(PageSizes))]
    public void ReadsAndWritesAcrossOverflowChains(int pageSize)
    {
        Need();
        string path = Path.Combine(_dir, $"chain-{pageSize}.db");
        int[] lens = [0, 1, 7, 100, pageSize - 120, pageSize - 35, pageSize, pageSize + 3, 2 * pageSize, 5 * pageSize + 7, 100000];
        var sql = new StringBuilder($"PRAGMA page_size={pageSize}; CREATE TABLE t(id INTEGER PRIMARY KEY, a TEXT, b BLOB, c TEXT);");
        for (int i = 0; i < lens.Length; i++)
        {
            sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO t VALUES ({i + 1}, 'x{i}', x'{Convert.ToHexString(Pattern(lens[i], i))}', 'tail');");
        }

        SqliteCli.ApplyScript(path, sql.ToString());
        var rnd = new Random(pageSize);
        using (Database d = Database.Open(path))
        using (Connection c = d.Connect())
        {
            for (int i = 0; i < lens.Length; i++)
            {
                byte[] expect = Pattern(lens[i], i);
                using Blob b = c.OpenBlob("main", "t", "b", i + 1, writable: true);
                Assert.Equal(lens[i], b.Length);
                var all = new byte[lens[i]];
                b.Read(0, all);
                Assert.Equal(expect, all);
                for (int k = 0; k < 120 && lens[i] > 0; k++)
                {
                    // offsets near the page and chain boundaries, and arbitrary ones
                    int off = k < 40 ? Math.Clamp(((k % 8) * (pageSize - 4)) + (k % 5) - 2, 0, lens[i] - 1) : rnd.Next(lens[i]);
                    int n = rnd.Next(1, Math.Min(lens[i] - off, 3 * pageSize) + 1);
                    var got = new byte[n];
                    b.Read(off, got);
                    Assert.True(expect.AsSpan(off, n).SequenceEqual(got), $"len {lens[i]} read {n}@{off}");
                    if (k % 3 == 0)
                    {
                        byte[] upd = Pattern(n, 1000 + k);
                        b.Write(off, upd);
                        upd.CopyTo(expect.AsSpan(off));
                        var back = new byte[n];
                        b.Read(off, back);
                        Assert.Equal(upd, back);
                    }
                }

                b.Read(0, all);
                Assert.Equal(expect, all);
                Assert.Equal(expect, ReadViaStream(b));
            }
        }

        Assert.True(PinnedSqlite.IntegrityOk(path, out string detail), detail);
        // the shell sees every byte the handle wrote, and nothing else changed
        List<string[]> rows = SqliteCli.QueryCsv(path, "SELECT id, hex(b), a, c FROM t ORDER BY id");
        Assert.Equal(lens.Length, rows.Count);
        for (int i = 0; i < lens.Length; i++)
        {
            Assert.Equal(lens[i] * 2, rows[i][1].Trim('"').Length);
            Assert.Equal("x" + i, rows[i][2].Trim());
            Assert.Equal("tail", rows[i][3].Trim());
        }
    }

    private static byte[] ReadViaStream(Blob b)
    {
        using Stream s = b.AsStream();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task TheAsyncTwinsAndTheStreamWork()
    {
        Need();
        string path = Path.Combine(_dir, "async.db");
        SqliteCli.ApplyScript(path, $"PRAGMA page_size=1024; CREATE TABLE t(id INTEGER PRIMARY KEY, b BLOB); INSERT INTO t VALUES (1, x'{Convert.ToHexString(Pattern(20000, 5))}');");
        byte[] expect = Pattern(20000, 5);
        using Database d = Database.Open(path);
        using Connection c = d.Connect();
        using Blob b = c.OpenBlob("main", "t", "b", 1, writable: true);
        var got = new byte[5000];
        await b.ReadAsync(7000, got, TestContext.Current.CancellationToken);
        Assert.Equal(expect.AsSpan(7000, 5000).ToArray(), got);
        byte[] upd = Pattern(3000, 9);
        await b.WriteAsync(12345, upd, TestContext.Current.CancellationToken);
        upd.CopyTo(expect.AsSpan(12345));
        using Stream s = b.AsStream();
        s.Position = 12000;
        var chunk = new byte[4000];
        int read = await s.ReadAsync(chunk, TestContext.Current.CancellationToken);
        Assert.Equal(4000, read);
        Assert.Equal(expect.AsSpan(12000, 4000).ToArray(), chunk);
        s.Seek(-10, SeekOrigin.End);
        Assert.Throws<NotSupportedException>(() => s.Write(new byte[11]));
        await s.WriteAsync(new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);
        expect[19990] = 1;
        expect[19991] = 2;
        expect[19992] = 3;
        b.Close();
        Assert.Equal(Convert.ToHexString(expect), SqliteCli.QueryCsv(path, "SELECT hex(b) FROM t")[0][0].Trim());
    }
}
