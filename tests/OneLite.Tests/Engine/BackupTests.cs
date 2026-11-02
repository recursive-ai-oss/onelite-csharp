using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Text;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>The online backup API: copies are checked with the pinned shell (integrity_check, .dump) and error behaviour against the native library.</summary>
public sealed class BackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-backup-" + Guid.NewGuid().ToString("N"));

    public BackupTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string P(string name) => Path.Combine(_dir, name);

    private static void Need() => Assert.SkipUnless(File.Exists(OraclePaths.Cli) && NativeApi.Available, "the pinned oracle is not installed");

    private static string Fill(int rows) =>
        "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT, c BLOB); CREATE INDEX tb ON t(b); CREATE TABLE u(x, y); CREATE VIEW v AS SELECT a FROM t;" +
        $"WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<{rows}) INSERT INTO t SELECT i, 'row ' || i || hex(randomblob(i % 40)), zeroblob((i * 97) % 9000) FROM n;" +
        $"INSERT INTO u SELECT a, b FROM t WHERE a % 3 = 0; DELETE FROM t WHERE a % 7 = 0;";

    private string MakeSource(string name, int pageSize, string mode, int autoVacuum, string encoding, int rows = 600)
    {
        string path = P(name);
        string pre = $"PRAGMA page_size={pageSize}; PRAGMA auto_vacuum={autoVacuum}; PRAGMA encoding='{encoding}'; PRAGMA journal_mode={mode};";
        SqliteCli.ApplyScript(path, pre + Fill(rows));
        return path;
    }

    private static void CheckCopy(string src, string dst)
    {
        Assert.True(PinnedSqlite.IntegrityOk(dst, out string detail), detail);
        Assert.Equal(SqliteCli.Dump(src), SqliteCli.Dump(dst));
    }

    public static IEnumerable<TheoryDataRow<int, string, int, string>> Shapes() =>
    [
        new(512, "delete", 0, "UTF-8"), new(1024, "wal", 0, "UTF-8"), new(4096, "delete", 1, "UTF-8"), new(4096, "wal", 2, "UTF-16le"),
        new(8192, "delete", 0, "UTF-16be"), new(65536, "delete", 0, "UTF-8"), new(2048, "truncate", 1, "UTF-8"),
    ];

    [Theory]
    [MemberData(nameof(Shapes))]
    public void CopiesEveryShape(int pageSize, string mode, int av, string enc)
    {
        Need();
        string src = MakeSource($"s-{pageSize}-{mode}-{av}-{enc}.db", pageSize, mode, av, enc);
        foreach (int step in new[] { 1, 7, -1 })
        {
            string dst = P($"d-{pageSize}-{mode}-{av}-{enc}-{step}.db");
            using (Database sd = Database.OpenReadOnly(src))
            using (Connection sc = sd.Connect())
            using (Database dd = Database.Open(dst))
            using (Connection dc = dd.Connect())
            using (Backup b = Backup.Create(dc, "main", sc, "main"))
            {
                BackupStatus st;
                int steps = 0;
                while ((st = b.Step(step)) == BackupStatus.More)
                {
                    steps++;
                    Assert.True(steps < 100000);
                }

                Assert.Equal(BackupStatus.Done, st);
                // the destination connection reads what it just received
                using Statement q = dc.Prepare("SELECT count(*) FROM t");
                Assert.True(q.Step());
            }

            CheckCopy(src, dst);
        }
    }

    [Fact]
    public void ProgressCounters()
    {
        Need();
        string src = MakeSource("p.db", 1024, "delete", 0, "UTF-8");
        using Database sd = Database.Open(src);
        using Connection sc = sd.Connect();
        using Database dd = Database.Open(P("pd.db"));
        using Connection dc = dd.Connect();
        using Backup b = Backup.Create(dc, "main", sc, "main");
        Assert.Equal(0, b.PageCount);
        Assert.Equal(BackupStatus.More, b.Step(5));
        int total = b.PageCount;
        Assert.True(total > 10);
        Assert.Equal(total - 5, b.Remaining);

        IntPtr ns = NativeApi.OpenRw(src), nd = NativeApi.OpenRw(P("nd.db"));
        IntPtr nb = NativeApi.BackupInit(nd, "main", ns, "main");
        Assert.Equal(0, NativeApi.BackupStep(nb, 5));
        Assert.Equal(NativeApi.BackupPageCount(nb), total);
        Assert.Equal(NativeApi.BackupRemaining(nb), b.Remaining);
        _ = NativeApi.BackupFinish(nb);
        _ = NativeApi.Close(ns);
        _ = NativeApi.Close(nd);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("wal")]
    public void AnotherConnectionWritingRestartsTheCopy(string mode)
    {
        Need();
        string src = MakeSource("w-" + mode + ".db", 1024, mode, 0, "UTF-8");
        string dst = P("wd-" + mode + ".db");
        using Database sd = Database.Open(src);
        using Connection sc = sd.Connect();
        using Connection other = sd.Connect();
        using Database dd = Database.Open(dst);
        using Connection dc = dd.Connect();
        using Backup b = Backup.Create(dc, "main", sc, "main");
        Assert.Equal(BackupStatus.More, b.Step(10));
        int before = b.Remaining;
        Exec(other, "INSERT INTO u VALUES (-1, 'late')");
        Assert.Equal(BackupStatus.More, b.Step(3));
        Assert.True(b.Remaining >= before - 3 + 10 - 3 || b.Remaining > before - 3, $"restart expected: {before} -> {b.Remaining}");
        Assert.Equal(b.PageCount - 3, b.Remaining);
        while (b.Step(50) == BackupStatus.More)
        {
        }

        b.Dispose();
        CheckCopy(src, dst);
        Assert.Contains("'late'", SqliteCli.Dump(dst), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSourceConnectionWritingUpdatesTheCopyInPlace()
    {
        Need();
        string src = MakeSource("i.db", 1024, "delete", 0, "UTF-8");
        string dst = P("id.db");
        using Database sd = Database.Open(src);
        using Connection sc = sd.Connect();
        using Database dd = Database.Open(dst);
        using Connection dc = dd.Connect();
        using Backup b = Backup.Create(dc, "main", sc, "main");
        Assert.Equal(BackupStatus.More, b.Step(20));
        int total = b.PageCount;
        Exec(sc, "UPDATE t SET b = 'changed ' || a WHERE a < 100");
        Assert.Equal(BackupStatus.More, b.Step(5));
        // no restart: the count of remaining pages kept falling (the source may have grown by a page or two)
        Assert.True(b.Remaining >= b.PageCount - 25 - 2 && b.Remaining <= total - 25 + 4, $"{b.Remaining} of {b.PageCount}");
        while (b.Step(50) == BackupStatus.More)
        {
        }

        b.Dispose();
        CheckCopy(src, dst);
    }

    [Fact]
    public void AnUnfinishedBackupLeavesTheDestinationAlone()
    {
        Need();
        string src = MakeSource("u.db", 4096, "delete", 0, "UTF-8");
        string dst = P("ud.db");
        SqliteCli.ApplyScript(dst, "PRAGMA page_size=4096; CREATE TABLE keep(k); INSERT INTO keep VALUES (1),(2);");
        string before = SqliteCli.Dump(dst);
        using (Database sd = Database.OpenReadOnly(src))
        using (Connection sc = sd.Connect())
        using (Database dd = Database.Open(dst))
        using (Connection dc = dd.Connect())
        {
            Backup b = Backup.Create(dc, "main", sc, "main");
            Assert.Equal(BackupStatus.More, b.Step(2));
            OneLiteException inUse = Assert.Throws<OneLiteException>(() =>
            {
                using Statement s = dc.Prepare("SELECT count(*) FROM keep");
                s.Step();
            });
            // (the reference leaves this undefined: it reads the half-written file; here the destination connection refuses with SQLITE_LOCKED)
            Assert.Equal(ResultCode.Locked, inUse.Code);

            b.Dispose();
            using Statement s2 = dc.Prepare("SELECT count(*) FROM keep");
            Assert.True(s2.Step());
        }

        Assert.Equal(before, SqliteCli.Dump(dst));
    }

    [Fact]
    public void ANonEmptyWalDestinationWithAnotherPageSizeIsRefusedAsTheReferenceDoes()
    {
        Need();
        string src = MakeSource("e.db", 1024, "delete", 0, "UTF-8", 50);
        string setup = "PRAGMA page_size=4096; PRAGMA journal_mode=WAL; CREATE TABLE k(x); INSERT INTO k VALUES(1);";
        string d1 = P("e1.db"), d2 = P("e2.db");
        SqliteCli.ApplyScript(d1, setup);
        SqliteCli.ApplyScript(d2, setup);
        IntPtr ns = NativeApi.OpenRw(src), nd = NativeApi.OpenRw(d1);
        IntPtr nb = NativeApi.BackupInit(nd, "main", ns, "main");
        int rc = NativeApi.BackupStep(nb, -1);
        int fin = NativeApi.BackupFinish(nb);
        string msg = NativeApi.ErrMsg(nd);
        _ = NativeApi.Close(ns);
        _ = NativeApi.Close(nd);
        using Database sd = Database.OpenReadOnly(src);
        using Connection sc = sd.Connect();
        using Database dd = Database.Open(d2);
        using Connection dc = dd.Connect();
        using Backup b = Backup.Create(dc, "main", sc, "main");
        OneLiteException e = Assert.Throws<OneLiteException>(() => b.Step(-1));
        Assert.Equal(rc, e.Code);
        Assert.Equal(msg, e.Message);
        Assert.Equal(fin, rc);
    }

    [Fact]
    public void InitErrorsMatchTheReference()
    {
        Need();
        string src = MakeSource("x.db", 4096, "delete", 0, "UTF-8", 20);
        IntPtr ns = NativeApi.OpenRw(src), nd = NativeApi.OpenRw(P("xd.db"));
        Assert.Equal(IntPtr.Zero, NativeApi.BackupInit(nd, "main", nd, "main"));
        string same = NativeApi.ErrMsg(nd);
        Assert.Equal(IntPtr.Zero, NativeApi.BackupInit(nd, "main", ns, "nope"));
        string unknown = NativeApi.ErrMsg(nd);
        NativeApi.Run(nd, "BEGIN; CREATE TABLE z(a);");
        Assert.Equal(IntPtr.Zero, NativeApi.BackupInit(nd, "main", ns, "main"));
        string busy = NativeApi.ErrMsg(nd);
        _ = NativeApi.Close(ns);
        _ = NativeApi.Close(nd);

        using Database sd = Database.Open(src);
        using Connection sc = sd.Connect();
        using Database dd = Database.Open(P("xd2.db"));
        using Connection dc = dd.Connect();
        Assert.Equal(same, Assert.Throws<OneLiteException>(() => Backup.Create(sc, "main", sc, "main")).Message);
        Assert.Equal(unknown, Assert.Throws<OneLiteException>(() => Backup.Create(dc, "main", sc, "nope")).Message);
        Exec(dc, "BEGIN");
        Exec(dc, "CREATE TABLE z(a)");
        Assert.Equal(busy, Assert.Throws<OneLiteException>(() => Backup.Create(dc, "main", sc, "main")).Message);
    }

    [Fact]
    public async Task TheAsyncDriverCopiesTheSame()
    {
        Need();
        string src = MakeSource("a.db", 2048, "wal", 0, "UTF-8");
        string dst = P("ad.db");
        using (Database sd = Database.OpenReadOnly(src))
        using (Connection sc = sd.Connect())
        using (Database dd = Database.Open(dst))
        using (Connection dc = dd.Connect())
        using (Backup b = Backup.Create(dc, "main", sc, "main"))
        {
            while (await b.StepAsync(9, TestContext.Current.CancellationToken) == BackupStatus.More)
            {
            }
        }

        CheckCopy(src, dst);
    }

    [Fact]
    public void AnAttachedDatabaseIsASourceAndADestination()
    {
        Need();
        string src = MakeSource("m.db", 4096, "delete", 0, "UTF-8", 80);
        string dst = P("md.db");
        using Database md = Database.Open(P("host.db"));
        using Connection host = md.Connect();
        Exec(host, $"ATTACH '{src}' AS s");
        using Database dd = Database.Open(P("md2.db"));
        using Connection dc = dd.Connect();
        using (Backup b = Backup.Create(dc, "main", host, "s"))
        {
            Assert.Equal(BackupStatus.Done, b.Step(-1));
        }

        dd.Dispose();
        CheckCopy(src, P("md2.db"));
        _ = dst;
    }

    [Fact]
    public void OurFileIsReadBackByTheShellsBackup()
    {
        Need();
        string ours = P("o.db");
        using (Database d = Database.Open(ours))
        using (Connection c = d.Connect())
        {
            Exec(c, "CREATE TABLE t(a INTEGER PRIMARY KEY, b); INSERT INTO t VALUES (1, 'x'), (2, zeroblob(5000))");
        }

        string copy = P("o-copy.db");
        Assert.Equal(0, SqliteCli.Run(ours, $".backup '{copy}'").ExitCode);
        string back = P("o-back.db");
        using (Database sd = Database.OpenReadOnly(copy))
        using (Connection sc = sd.Connect())
        using (Database dd = Database.Open(back))
        using (Connection dc = dd.Connect())
        using (Backup b = Backup.Create(dc, "main", sc, "main"))
        {
            Assert.Equal(BackupStatus.Done, b.Step(-1));
        }

        CheckCopy(ours, back);
    }

    private static void Exec(Connection c, string sql)
    {
        string rest = sql;
        while (!string.IsNullOrWhiteSpace(rest))
        {
            using Statement s = c.Prepare(rest);
            while (s.Step())
            {
            }

            rest = s.Tail;
        }
    }

    [Fact]
    public void AnEncryptedSourceGivesAnEncryptedCopyUnderTheSameKey()
    {
        string src = P("enc-src.db"), dst = P("enc-dst.db"), vac = P("enc-vac.db");
        var key = RecursiveAI.OneLite.Storage.Crypto.CipherKey.FromPassphrase("correct horse");
        using (Database d = Database.Open(src, new DatabaseOptions { Key = key }))
        using (Connection c = d.Connect())
        {
            Exec(c, "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT); INSERT INTO t SELECT value, 'secret ' || value || zeroblob(700) FROM generate_series(1, 400);");
        }

        using (Database sd = Database.Open(src, new DatabaseOptions { Key = RecursiveAI.OneLite.Storage.Crypto.CipherKey.FromPassphrase("correct horse") }))
        using (Connection sc = sd.Connect())
        using (Database dd = Database.Open(dst, new DatabaseOptions { Key = RecursiveAI.OneLite.Storage.Crypto.CipherKey.FromPassphrase("correct horse") }))
        using (Connection dc = dd.Connect())
        {
            using (Backup b = Backup.Create(dc, "main", sc, "main"))
            {
                Assert.Equal(BackupStatus.Done, b.Step(-1));
            }

            using Statement q = dc.Prepare("SELECT count(*), max(a) FROM t");
            Assert.True(q.Step());
            Assert.Equal(400, q.GetInt64(0));
            Exec(sc, $"VACUUM INTO '{vac}'");
        }

        foreach (string f in new[] { dst, vac })
        {
            // the copy is encrypted: no plaintext anywhere in the file, and it opens under the key only
            Assert.False(File.ReadAllBytes(f).AsSpan().IndexOf("secret 1"u8) >= 0);
            using Database rd = Database.OpenReadOnly(f, new DatabaseOptions { Key = RecursiveAI.OneLite.Storage.Crypto.CipherKey.FromPassphrase("correct horse") });
            using Connection rc = rd.Connect();
            using Statement q = rc.Prepare("SELECT count(*), max(a) FROM t");
            Assert.True(q.Step());
            Assert.Equal(400, q.GetInt64(0));
            using Database bad = Database.OpenReadOnly(f, new DatabaseOptions { Key = RecursiveAI.OneLite.Storage.Crypto.CipherKey.FromPassphrase("wrong") });
            using Connection bc = bad.Connect();
            Assert.Throws<OneLiteException>(() =>
            {
                using Statement wrongKey = bc.Prepare("SELECT count(*) FROM t");
                wrongKey.Step();
            });
            string? shell = Environment.GetEnvironmentVariable("ONELITE_SQLCIPHER");
            if (shell is { Length: > 0 } && File.Exists(shell))
            {
                var psi = new System.Diagnostics.ProcessStartInfo(shell) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                psi.ArgumentList.Add(f);
                if (Environment.GetEnvironmentVariable("ONELITE_SQLCIPHER_LIB") is { Length: > 0 } lib)
                {
                    psi.Environment["LD_LIBRARY_PATH"] = lib;
                }

                using System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi)!;
                p.StandardInput.Write("PRAGMA key='correct horse'; PRAGMA integrity_check; SELECT count(*) FROM t;\n");
                p.StandardInput.Close();
                string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                Assert.Contains("ok", o, StringComparison.Ordinal);
                Assert.Contains("400", o, StringComparison.Ordinal);
            }
        }
    }
}
