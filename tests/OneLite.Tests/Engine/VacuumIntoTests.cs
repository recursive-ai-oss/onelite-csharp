using System.Linq;
using System.Collections.Generic;
using System.IO;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>VACUUM INTO: the copy is checked with the pinned shell, and the misuse errors against the native library.</summary>
public sealed class VacuumIntoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-vinto-" + Guid.NewGuid().ToString("N"));

    public VacuumIntoTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string P(string name) => Path.Combine(_dir, name);

    private static void Need() => Assert.SkipUnless(File.Exists(OraclePaths.Cli) && NativeApi.Available, "the pinned oracle is not installed");

    private static string Fill(int rows) =>
        "CREATE TABLE t(a INTEGER PRIMARY KEY, b TEXT, c BLOB); CREATE INDEX tb ON t(b); CREATE TABLE u(x, y); CREATE VIEW v AS SELECT a FROM t;" +
        "CREATE TABLE w(k TEXT PRIMARY KEY, v) WITHOUT ROWID; INSERT INTO w SELECT 'k' || value, value FROM generate_series(1, 300);" +
        $"WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<{rows}) INSERT INTO t SELECT i, 'row ' || i || hex(randomblob(i % 40)), zeroblob((i * 97) % 9000) FROM n;" +
        "INSERT INTO u SELECT a, b FROM t WHERE a % 3 = 0; DELETE FROM t WHERE a % 7 = 0; PRAGMA user_version = 41; PRAGMA application_id = 77;";

    private string MakeSource(string name, int pageSize, string mode, int av, string enc, int rows = 700)
    {
        string path = P(name);
        SqliteCli.ApplyScript(path, $"PRAGMA page_size={pageSize}; PRAGMA auto_vacuum={av}; PRAGMA encoding='{enc}'; PRAGMA journal_mode={mode};" + Fill(rows));
        return path;
    }

    public static IEnumerable<TheoryDataRow<int, string, int, string>> Shapes() =>
    [
        new(512, "delete", 0, "UTF-8"), new(1024, "wal", 0, "UTF-8"), new(4096, "delete", 1, "UTF-8"), new(4096, "wal", 2, "UTF-16le"),
        new(8192, "delete", 0, "UTF-16be"), new(65536, "delete", 0, "UTF-8"), new(2048, "truncate", 2, "UTF-8"),
    ];

    private static void Exec(Connection c, string sql)
    {
        using Statement s = c.Prepare(sql);
        while (s.Step())
        {
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void TheCopyMatchesTheSourceAndTheShellsOwn(int pageSize, string mode, int av, string enc)
    {
        Need();
        string src = MakeSource($"s-{pageSize}-{mode}-{av}-{enc}.db", pageSize, mode, av, enc);
        string ours = P($"o-{pageSize}-{mode}-{av}-{enc}.db"), theirs = P($"t-{pageSize}-{mode}-{av}-{enc}.db");
        using (Database d = Database.OpenReadOnly(src))
        using (Connection c = d.Connect())
        {
            Exec(c, $"VACUUM INTO '{ours}'");
        }

        SqliteCli.Run(src, $"VACUUM INTO '{theirs}'");
        Assert.True(PinnedSqlite.IntegrityOk(ours, out string detail), detail);
        Assert.Equal(SqliteCli.Dump(src), SqliteCli.Dump(ours));
        string pragmas = "pragma page_size; pragma auto_vacuum; pragma journal_mode; pragma user_version; pragma application_id; pragma freelist_count; pragma encoding;";
        Assert.Equal(SqliteCli.Run(theirs, pragmas).Stdout, SqliteCli.Run(ours, pragmas).Stdout);
        long po = long.Parse(SqliteCli.Run(ours, "pragma page_count;").Stdout.Trim(), System.Globalization.CultureInfo.InvariantCulture);
        long pt = long.Parse(SqliteCli.Run(theirs, "pragma page_count;").Stdout.Trim(), System.Globalization.CultureInfo.InvariantCulture);
        File.AppendAllText(P("counts.txt"), $"{pageSize} {mode} {av} {enc}: ours {po} shell {pt}\n");
        Assert.InRange(po, pt - 3, pt + 3);
        Assert.Equal("0", SqliteCli.Run(ours, "pragma freelist_count;").Stdout.Trim());
        // the source was only read
        Assert.True(PinnedSqlite.IntegrityOk(src, out detail), detail);
    }

    [Fact]
    public void MisuseErrorsMatchTheReference()
    {
        Need();
        string src = MakeSource("m.db", 4096, "delete", 0, "UTF-8", 20);
        string existing = P("existing.db");
        SqliteCli.ApplyScript(existing, "CREATE TABLE z(a);");
        File.WriteAllBytes(P("empty.db"), []);
        string[] cases =
        [
            $"VACUUM INTO '{existing}'", "VACUUM INTO NULL", "VACUUM INTO 5", "VACUUM INTO x'6162'", "VACUUM INTO 'file:r.db?mode=ro'",
            "VACUUM INTO '/nonexistent/dir/q.db'", "VACUUM nope INTO 'n1.db'", "BEGIN; VACUUM INTO 'n2.db'", $"VACUUM INTO '{P("empty.db")}'", "VACUUM INTO ''",
            "VACUUM INTO (SELECT 'sub.db')", "VACUUM main INTO 'm1.db'",
        ];
        foreach (string sql in cases)
        {
            IntPtr nd = NativeApi.OpenRw(src);
            int rc = NativeApi.Exec(nd, sql.Replace("'n", "'" + P("n"), StringComparison.Ordinal).Replace("'sub", "'" + P("sub"), StringComparison.Ordinal).Replace("'m1", "'" + P("m1"), StringComparison.Ordinal), IntPtr.Zero, IntPtr.Zero, out IntPtr err);
            string nmsg = NativeApi.ErrMsg(nd);
            int ext = NativeApi.ExtErr(nd);
            if (err != IntPtr.Zero)
            {
                NativeApi.Free(err);
            }

            _ = NativeApi.Close(nd);
            foreach (string f in Directory.GetFiles(_dir, "n?.db").Concat(Directory.GetFiles(_dir, "sub.db")).Concat(Directory.GetFiles(_dir, "m1.db")))
            {
                File.Delete(f);
            }

            File.WriteAllBytes(P("empty.db"), []);
            using Database d = Database.Open(src);
            using Connection c = d.Connect();
            string text = sql.Replace("'n", "'" + P("n"), StringComparison.Ordinal).Replace("'sub", "'" + P("sub"), StringComparison.Ordinal).Replace("'m1", "'" + P("m1"), StringComparison.Ordinal);
            OneLiteException? e = null;
            try
            {
                rc = 0;
                string rest = text;
                while (!string.IsNullOrWhiteSpace(rest))
                {
                    using Statement s = c.Prepare(rest);
                    while (s.Step())
                    {
                    }

                    rest = s.Tail;
                }
            }
            catch (OneLiteException ex)
            {
                e = ex;
            }

            if (rc == 0 && nmsg == "not an error")
            {
                Assert.True(e is null, $"{sql}: reference succeeded, ours threw {e?.Message}");
            }
            else
            {
                Assert.True(e is not null, $"{sql}: reference failed ({nmsg}), ours succeeded");
                Assert.Equal(nmsg, e.Message);
                Assert.Equal(ext, e.Code);
            }
        }
    }

    [Fact]
    public void TheCopyIsAnOrdinaryFileForOurOwnReader()
    {
        Need();
        string src = MakeSource("r.db", 1024, "delete", 0, "UTF-8", 200);
        string copy = P("r-copy.db");
        using (Database d = Database.Open(src))
        using (Connection c = d.Connect())
        {
            Exec(c, $"VACUUM INTO '{copy}'");
        }

        using Database d2 = Database.OpenReadOnly(copy);
        using Connection c2 = d2.Connect();
        using Statement q = c2.Prepare("SELECT count(*), sum(length(c)) FROM t");
        Assert.True(q.Step());
        Assert.Equal(172, q.GetInt64(0));
    }

    /// <summary>Runs only when <c>ONELITE_BIG_SOURCE</c> names a large database (a gigabyte or more): VACUUM INTO must not hold the database in memory.</summary>
    [Fact]
    public void ALargeSourceIsCopiedWithBoundedMemory()
    {
        string? big = Environment.GetEnvironmentVariable("ONELITE_BIG_SOURCE");
        Assert.SkipUnless(big is { Length: > 0 } && File.Exists(big), "set ONELITE_BIG_SOURCE to a large database");
        string dst = P("big-copy.db");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using (Database d = Database.OpenReadOnly(big))
        using (Connection c = d.Connect())
        {
            Exec(c, $"VACUUM INTO '{dst}'");
        }

        long peak = System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64;
        long gc = GC.GetGCMemoryInfo().HeapSizeBytes;
        string line = $"source {new FileInfo(big).Length >> 20} MiB, copy {new FileInfo(dst).Length >> 20} MiB, {sw.Elapsed.TotalSeconds:F1} s, peak working set {peak >> 20} MiB, last GC heap {gc >> 20} MiB\n";
        if (Environment.GetEnvironmentVariable("ONELITE_U31_REPORT") is { Length: > 0 } report)
        {
            File.AppendAllText(report, line);
        }

        Assert.True(PinnedSqlite.IntegrityOk(dst, out string detail), detail);
        Assert.True(peak < new FileInfo(big).Length / 3, line);
    }
}
