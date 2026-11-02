using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using RecursiveAI.OneLite.Oracle;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// The engine over WAL-mode files (with the log left behind, with and without its -shm) and over rollback files: the same rows at every forced
/// suspend point, from the sync and the async driver, with no allocation on the cached path, and without changing a byte of the files.
/// </summary>
public sealed class EngineWalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-enginewal-" + Guid.NewGuid().ToString("N"));

    public EngineWalTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static readonly string[] s_queries =
    [
        "SELECT * FROM t",
        "SELECT id, a * 2 + 1, upper(b), typeof(c), length(d) FROM t WHERE a BETWEEN 10 AND 20 ORDER BY b DESC, id LIMIT 50 OFFSET 3",
        "SELECT id FROM t WHERE id = 1234 OR id > 2990",
        "SELECT a, b FROM t WHERE a = 5 ORDER BY id DESC",
        "SELECT length(v), substr(v, 4990, 20) FROM big",
    ];

    /// <summary>Makes a WAL-mode database whose log still holds every frame: the shell copies the files while it is open, so the copy has no -shm.</summary>
    private string MakeWalDb(bool keepShm)
    {
        Assert.SkipUnless(File.Exists(OraclePaths.Cli), "the pinned sqlite3 shell is not installed");
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "the log is copied with cp");
        string live = Path.Combine(_dir, "live.db");
        string path = Path.Combine(_dir, keepShm ? "wal-shm.db" : "wal.db");
        var sb = new StringBuilder();
        sb.Append("PRAGMA page_size=1024; PRAGMA journal_mode=wal; PRAGMA wal_autocheckpoint=0;");
        sb.Append("CREATE TABLE t(id INTEGER PRIMARY KEY, a INT, b TEXT COLLATE NOCASE, c REAL, d BLOB); CREATE INDEX ta ON t(a);");
        sb.Append("WITH RECURSIVE s(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM s WHERE n<3000) ");
        sb.Append("INSERT INTO t SELECT n, n % 97, CASE WHEN n % 11 = 0 THEN NULL ELSE 'Row ' || n END, n / 7.0, CASE WHEN n % 5 = 0 THEN x'00ff' || zeroblob(n % 40) END FROM s;");
        sb.Append("CREATE TABLE big(id INTEGER PRIMARY KEY, v TEXT); INSERT INTO big VALUES (1, hex(zeroblob(5000))), (2, 'x');");
        // Some rows are rewritten after a first copy to the database file, so the newest version of a page is in the log and an older one in the file.
        sb.Append("PRAGMA wal_checkpoint(PASSIVE); UPDATE t SET a = a + 1 WHERE id % 3 = 0; DELETE FROM t WHERE id % 101 = 0;");
        string cp = $"cp '{live}' '{path}' && cp '{live}-wal' '{path}-wal'" + (keepShm ? $" && cp '{live}-shm' '{path}-shm'" : string.Empty);
        sb.Append("\n.shell ").Append(cp).Append('\n');
        SqliteCli.ApplyScript(live, sb.ToString());
        Assert.True(new FileInfo(path + "-wal").Length > 100_000, "the log should still hold the frames");
        return path;
    }

    private string MakeRollbackDb()
    {
        Assert.SkipUnless(File.Exists(OraclePaths.Cli), "the pinned sqlite3 shell is not installed");
        string path = Path.Combine(_dir, "rb.db");
        var sb = new StringBuilder();
        sb.Append("PRAGMA page_size=1024; CREATE TABLE t(id INTEGER PRIMARY KEY, a INT, b TEXT COLLATE NOCASE, c REAL, d BLOB); CREATE INDEX ta ON t(a);");
        sb.Append("WITH RECURSIVE s(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM s WHERE n<3000) ");
        sb.Append("INSERT INTO t SELECT n, n % 97, CASE WHEN n % 11 = 0 THEN NULL ELSE 'Row ' || n END, n / 7.0, CASE WHEN n % 5 = 0 THEN x'00ff' || zeroblob(n % 40) END FROM s;");
        sb.Append("CREATE TABLE big(id INTEGER PRIMARY KEY, v TEXT); INSERT INTO big VALUES (1, hex(zeroblob(5000))), (2, 'x');");
        SqliteCli.ApplyScript(path, sb.ToString());
        return path;
    }

    private static string RowText(Statement st)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < st.ColumnCount; i++)
        {
            sb.Append(st.ColumnType(i)).Append(':');
            sb.Append(st.IsNull(i) ? "NULL" : Convert.ToHexString(st.GetTextUtf8(i))).Append('|');
        }

        return sb.ToString();
    }

    private static List<string> RunSync(string path, string sql, int yieldEvery)
    {
        using var db = Database.OpenReadOnly(path, new DatabaseOptions { YieldEvery = yieldEvery });
        using Connection conn = db.Connect();
        using Statement st = conn.Prepare(sql);
        var rows = new List<string>();
        while (st.Step())
        {
            rows.Add(RowText(st));
        }

        return rows;
    }

    private static async Task<List<string>> RunAsync(string path, string sql, int yieldEvery)
    {
        using var db = Database.OpenReadOnly(path, new DatabaseOptions { YieldEvery = yieldEvery });
        using Connection conn = db.Connect();
        using Statement st = conn.Prepare(sql);
        var rows = new List<string>();
        while (await st.StepAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(RowText(st));
        }

        return rows;
    }

    private static string FileState(string path)
    {
        var sb = new StringBuilder();
        foreach (string suffix in new[] { string.Empty, "-wal", "-journal" })
        {
            sb.Append(File.Exists(path + suffix) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path + suffix))) : "-").Append('|');
        }

        return sb.ToString();
    }

    [Theory]
    [InlineData("rollback")]
    [InlineData("wal-shm")]
    [InlineData("wal")]
    public async Task EveryDriverAndEverySuspendPointGivesTheSameRows(string kind)
    {
        string path = kind == "rollback" ? MakeRollbackDb() : MakeWalDb(kind == "wal-shm");
        string before = FileState(path);
        foreach (string sql in s_queries)
        {
            List<string> plain = RunSync(path, sql, 0);
            Assert.NotEmpty(plain);
            Assert.Equal(plain, await RunAsync(path, sql, 0));
            foreach (int n in new[] { 1, 2, 3, 7 })
            {
                Assert.Equal(plain, RunSync(path, sql, n));
                Assert.Equal(plain, await RunAsync(path, sql, n));
            }
        }

        Assert.Equal(before, FileState(path));
    }

    [Fact]
    public void AWalDatabaseWithoutItsSharedMemoryIsReadAndTheIndexIsCreated()
    {
        string path = MakeWalDb(false);
        Assert.False(File.Exists(path + "-shm"));
        long walLength = new FileInfo(path + "-wal").Length;
        List<string> rows = RunSync(path, "SELECT count(*) FROM t", 0);
        // 3000 rows less every 101st; the update touched every third id (sqlite_master tells the story, the numbers come from the log).
        Assert.Single(rows);
        Assert.True(File.Exists(path + "-shm"), "a read-only connection starts the wal-index like the reference does");
        Assert.Equal(walLength, new FileInfo(path + "-wal").Length);
        // A second connection finds the index the first one left, and reads the same.
        Assert.Equal(rows, RunSync(path, "SELECT count(*) FROM t", 0));
    }

    [Fact]
    public void CachedStepLoopsOverAWalDatabaseAllocateNothing()
    {
        string path = MakeWalDb(true);
        using var db = Database.OpenReadOnly(path, new DatabaseOptions { CacheSize = 10000 });
        using Connection conn = db.Connect();
        using Statement scan = conn.Prepare("SELECT id, a, b, c, d FROM t");
        using Statement range = conn.Prepare("SELECT id, a FROM t WHERE a >= 10 AND a < 20");

        long Measure(Func<long> body)
        {
            body();
            body();
            long best = long.MaxValue;
            for (int rep = 0; rep < 3; rep++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                Assert.True(body() != 0);
                best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
            }

            return best;
        }

        long Scan()
        {
            long sum = 0;
            scan.Reset();
            while (scan.Step())
            {
                sum += scan.GetInt64(0) + scan.GetInt64(1);
                sum += scan.IsNull(2) ? 0 : scan.GetTextUtf8(2).Length;
            }

            return sum;
        }

        long Range()
        {
            long sum = 0;
            range.Reset();
            while (range.Step())
            {
                sum += range.GetInt64(0);
            }

            return sum;
        }

        // The loops keep their read transaction between steps; the begin and end of a WAL read transaction is not part of the claim.
        using Statement hold = conn.Prepare("SELECT id FROM t");
        Assert.True(hold.Step());
        Assert.Equal(0, Measure(Range));
        Assert.Equal(0, Measure(Scan));
    }

    [Fact]
    public async Task TheAsyncDriverReturnsCompletedTasksWhenEverythingIsCached()
    {
        string path = MakeRollbackDb();
        using var db = Database.OpenReadOnly(path, new DatabaseOptions { CacheSize = 10000 });
        using Connection conn = db.Connect();
        using Statement st = conn.Prepare("SELECT id FROM t");
        while (await st.StepAsync(TestContext.Current.CancellationToken))
        {
        }

        // The first pass loaded the pages; now no step may need a task of its own.
        st.Reset();
        Assert.True(await st.StepAsync(TestContext.Current.CancellationToken));
        for (int i = 0; i < 100; i++)
        {
            System.Threading.Tasks.ValueTask<bool> t = st.StepAsync(TestContext.Current.CancellationToken);
            Assert.True(t.IsCompletedSuccessfully);
            Assert.True(await t);
        }
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void AWalDatabaseInADirectoryThatCannotBeWrittenGivesTheShellsCodes()
    {
        string path = MakeWalDb(false);
        string dir = Path.Combine(_dir, "ro");
        Directory.CreateDirectory(dir);
        string db = Path.Combine(dir, "d.db");
        File.Copy(path, db);
        File.Copy(path + "-wal", db + "-wal");
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            bool canWrite;
            try
            {
                File.WriteAllBytes(Path.Combine(dir, "probe"), []);
                canWrite = true;
            }
            catch (UnauthorizedAccessException)
            {
                canWrite = false;
            }

            Assert.SkipWhen(canWrite, "the directory cannot be made unwritable for this user");

            // A log but no wal-index, and no way to create one: the reference cannot open the database (c3ref open; wal "Read-Only Databases").
            OneLiteException e = Assert.Throws<OneLiteException>(() => RunSync(db, "SELECT count(*) FROM t", 0));
            Assert.Equal(ResultCode.CantOpen, e.Code);
            Assert.Equal("unable to open database file", e.Message);

            // Neither a log nor a wal-index: SQLITE_READONLY_DIRECTORY.
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.Delete(db + "-wal");
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            e = Assert.Throws<OneLiteException>(() => RunSync(db, "SELECT count(*) FROM t", 0));
            Assert.Equal(ResultCode.ReadOnlyDirectory, e.ExtendedCode);
            Assert.Equal("attempt to write a readonly database", e.Message);
        }
        finally
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void AnEncryptedWalDatabaseWrittenBySqlCipherIsReadByAReadOnlyConnection()
    {
        string? shell = Environment.GetEnvironmentVariable("ONELITE_SQLCIPHER");
        Assert.SkipUnless(shell != null && File.Exists(shell), "set ONELITE_SQLCIPHER to the SQLCipher shell");
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "the log is copied with cp");
        string live = Path.Combine(_dir, "enc-live.db");
        string path = Path.Combine(_dir, "enc.db");
        string script = "PRAGMA key='correct horse'; PRAGMA cipher_page_size=4096; PRAGMA journal_mode=wal; PRAGMA wal_autocheckpoint=0;\n" +
            "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT); " +
            "WITH RECURSIVE s(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM s WHERE n<500) INSERT INTO t SELECT n, 'row ' || n FROM s;\n" +
            "UPDATE t SET v = v || '!' WHERE id % 2 = 0;\n" +
            $".shell cp '{live}' '{path}' && cp '{live}-wal' '{path}-wal'\n";
        var psi = new System.Diagnostics.ProcessStartInfo(shell!) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(live);
        using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi)!)
        {
            p.StandardInput.Write(script);
            p.StandardInput.Close();
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(File.Exists(path + "-wal"), output);
        }

        Assert.True(new FileInfo(path + "-wal").Length > 4096, "the encrypted log should still hold the frames");
        string before = FileState(path);
        using Database db = Database.OpenReadOnly(path, new DatabaseOptions { Key = RecursiveAI.OneLite.Storage.Crypto.CipherKey.FromPassphrase("correct horse") });
        using Connection conn = db.Connect();
        using Statement st = conn.Prepare("SELECT count(*) FROM t WHERE v LIKE '%!'");
        Assert.True(st.Step());
        Assert.Equal(250, st.GetInt64(0));
        using Statement one = conn.Prepare("SELECT id, v FROM t WHERE id = 500");
        Assert.True(one.Step());
        Assert.Equal("row 500!", one.GetString(1));
        st.Reset();
        one.Reset();
        Assert.Equal(before, FileState(path));
    }
}
