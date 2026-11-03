using RecursiveAI.OneLite.Storage;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Tests.Vfs;

namespace RecursiveAI.OneLite.Tests.Trees;

/// <summary>
/// Files written by the B-tree layer are opened by the pinned <c>sqlite3</c>: <c>PRAGMA integrity_check</c> must say ok (it also checks every
/// index against its table) and the rows it reads must be exactly the model's. Skipped when the shell is not installed.
/// </summary>
public sealed class SqliteWriteInteropTests
{
    private static readonly object LogLock = new();

    public static IEnumerable<object[]> Configs()
    {
        foreach (int ps in new[] { 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536 })
        {
            foreach (TextEncoding enc in new[] { TextEncoding.Utf8, TextEncoding.Utf16Le, TextEncoding.Utf16Be })
            {
                foreach (int av in new[] { 0, 1, 2 })
                {
                    foreach (int y in new[] { 0, 1, 2, 3 })
                    {
                        yield return new object[] { ps, enc, av, 0, y };
                    }
                }
            }
        }

        // reserved bytes at the end of every page
        yield return new object[] { 1024, TextEncoding.Utf8, 0, 16, 0 };
        yield return new object[] { 4096, TextEncoding.Utf16Le, 1, 32, 2 };
        yield return new object[] { 512, TextEncoding.Utf8, 2, 8, 1 };
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b);

    [Theory]
    [MemberData(nameof(Configs))]
    public void FilesPassSqliteIntegrityCheckAndReadBack(int pageSize, TextEncoding enc, int autoVacuum, int reserved, int yieldEvery)
    {
        string sqlite = SqliteOracle.Require();
        _ = sqlite;
        var db = new TestDb(pageSize, reserved, enc, autoVacuum, yieldEvery);
        var rnd = new Random(pageSize * 7 + (int)enc * 101 + autoVacuum * 13 + reserved);
        int n = pageSize >= 32768 ? 500 : 1500;
        for (int i = 0; i < n; i++)
        {
            db.Step(rnd);
        }

        db.Verify();
        string dir = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "onelite-btree-interop" + "-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"w-{pageSize}-{(int)enc}-{autoVacuum}-{reserved}-{yieldEvery}.db");
        byte[] image = db.Img!.ToBytes();
        File.WriteAllBytes(file, image);
        try
        {
            SqliteChecks(db, file, pageSize, enc, autoVacuum);
            (int reb, int splits, int merges, int deep, int shrunk) = db.Events();
            Log($"ok page={pageSize} enc={(int)enc} av={autoVacuum} reserved={reserved} yield={yieldEvery} sha256={System.Security.Cryptography.SHA256.HashData(image).Aggregate("", (a, b) => a + b.ToString("x2", CultureInfo.InvariantCulture))} pending={db.Img!.PendingCount} ops={db.Ops} rows={db.Rows.Count} wr={db.Wr.Count} pages={db.Img!.PageCount} free={db.Img!.FreelistCount} rebalance={reb} split={splits} merge={merges} deepen={deep} shrink={shrunk}");
        }
        catch
        {
            string? keep = Environment.GetEnvironmentVariable("ONELITE_KEEP");
            if (keep != null)
            {
                Directory.CreateDirectory(keep);
                File.Copy(file, Path.Combine(keep, Path.GetFileName(file)), overwrite: true);
            }

            throw;
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Opens <paramref name="file"/> with the reference shell: integrity_check is ok, geometry matches, and every table and index reads back as the model.</summary>
    internal static void SqliteChecks(TestDb db, string file, int pageSize, TextEncoding enc, int autoVacuum)
    {
        (int exit, string output) = SqliteOracle.Run(file,
            "PRAGMA integrity_check; PRAGMA page_size; PRAGMA encoding; PRAGMA auto_vacuum;"
            + "SELECT 'T', rowid||'|'||ifnull(a,'N')||'|'||hex(b)||'|'||hex(c) FROM t ORDER BY rowid;"
            + "SELECT 'W', hex(k)||'|'||hex(v) FROM w ORDER BY k;"
            + "SELECT 'I', count(*) FROM t INDEXED BY tb; SELECT 'I', count(*) FROM t INDEXED BY tbn; SELECT 'I', count(*) FROM t INDEXED BY tad;"
            + "SELECT 'N', rowid FROM t INDEXED BY tbn ORDER BY b COLLATE NOCASE, rowid;"
            + "SELECT 'D', rowid FROM t INDEXED BY tad ORDER BY a DESC, b, rowid;", 120_000);
        Assert.Equal(0, exit);
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines[0] == "ok", output.Length > 1500 ? output[..1500] : output);
        Assert.Equal(pageSize.ToString(CultureInfo.InvariantCulture), lines[1]);
        Assert.Equal(enc == TextEncoding.Utf8 ? "UTF-8" : enc == TextEncoding.Utf16Le ? "UTF-16le" : "UTF-16be", lines[2]);
        Assert.Equal(autoVacuum.ToString(CultureInfo.InvariantCulture), lines[3]);
        var expectT = db.Rows.Select(r => "T|" + r.Key + "|" + (r.Value.A?.ToString(CultureInfo.InvariantCulture) ?? "N") + "|" + Hex(TextCodec.Encode(r.Value.B, enc)) + "|" + Hex(r.Value.C)).ToList();
        Assert.Equal(expectT, lines.Where(l => l.StartsWith("T|", StringComparison.Ordinal)).ToList());
        var expectW = db.Wr.Select(r => "W|" + Hex(TextCodec.Encode(r.Key, enc)) + "|" + Hex(r.Value)).ToList();
        Assert.Equal(expectW, lines.Where(l => l.StartsWith("W|", StringComparison.Ordinal)).ToList());
        Assert.Equal(3, lines.Count(l => l == "I|" + db.Rows.Count.ToString(CultureInfo.InvariantCulture)));
        var nocase = db.Rows.OrderBy(r => r.Value.B, Comparer<string>.Create(NoCaseUtf8)).ThenBy(r => r.Key).Select(r => "N|" + r.Key);
        Assert.Equal(nocase.ToList(), lines.Where(l => l.StartsWith("N|", StringComparison.Ordinal)).ToList());
    }

    /// <summary>
    /// The same logical content built by the reference shell itself: the model's final state replayed as SQL into a database with the same page
    /// size, encoding and auto-vacuum mode. Text goes in as CAST(X'..' AS TEXT) so the bytes land in the database encoding unchanged.
    /// </summary>
    internal static string ReferenceScript(TestDb db, int pageSize, TextEncoding enc, int autoVacuum)
    {
        var sb = new StringBuilder();
        string encName = enc == TextEncoding.Utf8 ? "UTF-8" : enc == TextEncoding.Utf16Le ? "UTF-16le" : "UTF-16be";
        sb.Append("PRAGMA encoding='").Append(encName).Append("'; PRAGMA page_size=").Append(pageSize).Append("; PRAGMA auto_vacuum=").Append(autoVacuum).Append(";\n");
        sb.Append("CREATE TABLE t(a INT, b TEXT, c BLOB);CREATE INDEX tb ON t(b);CREATE INDEX tbn ON t(b COLLATE NOCASE);CREATE INDEX tad ON t(a DESC, b);\n");
        sb.Append("CREATE TABLE w(k TEXT PRIMARY KEY, v BLOB) WITHOUT ROWID;\nBEGIN;\n");
        foreach (KeyValuePair<long, Row> r in db.Rows)
        {
            sb.Append("INSERT INTO t(rowid,a,b,c) VALUES(").Append(r.Key).Append(',').Append(r.Value.A?.ToString(CultureInfo.InvariantCulture) ?? "NULL")
                .Append(",CAST(X'").Append(Hex(TextCodec.Encode(r.Value.B, enc))).Append("' AS TEXT),X'").Append(Hex(r.Value.C)).Append("');\n");
        }

        foreach (KeyValuePair<string, byte[]> r in db.Wr)
        {
            sb.Append("INSERT INTO w VALUES(CAST(X'").Append(Hex(TextCodec.Encode(r.Key, enc))).Append("' AS TEXT),X'").Append(Hex(r.Value)).Append("');\n");
        }

        sb.Append("COMMIT;\n");
        return sb.ToString();
    }

    /// <summary>Runs a script file through the reference shell on a database and returns its output.</summary>
    internal static string RunScript(string dbPath, string script, string scratchDir)
    {
        string scriptPath = Path.Combine(scratchDir, Path.GetFileName(dbPath) + ".sql");
        File.WriteAllText(scriptPath, script);
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, UseShellExecute = false };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("exec \"$0\" \"$1\" < \"$2\" 2>&1");
            psi.ArgumentList.Add(SqliteOracle.Require());
            psi.ArgumentList.Add(dbPath);
            psi.ArgumentList.Add(scriptPath);
            using System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, output.Length > 800 ? output[..800] : output);
            return output;
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    /// <summary>Asserts that the shell's <c>.dump</c> of our file equals its <c>.dump</c> of the reference database built from the same operations.</summary>
    internal static void DumpsEqual(TestDb db, string file, int pageSize, TextEncoding enc, int autoVacuum)
    {
        string dir = Path.GetDirectoryName(file)!;
        string reference = file + ".ref";
        File.Delete(reference);
        try
        {
            RunScript(reference, ReferenceScript(db, pageSize, enc, autoVacuum), dir);
            string expect = RunScript(reference, ".dump\n", dir);
            string actual = RunScript(file, ".dump\n", dir);
            Assert.True(expect == actual, "dump differs: reference " + expect.Length + " bytes, ours " + actual.Length + " bytes");
        }
        finally
        {
            File.Delete(reference);
        }
    }

    internal static int NoCaseUtf8(string a, string b)
    {
        byte[] x = Encoding.UTF8.GetBytes(a), y = Encoding.UTF8.GetBytes(b);
        return Collations.NoCase(TextEncoding.Utf8)(x, y);
    }

    private static void Log(string line)
    {
        string? path = Environment.GetEnvironmentVariable("ONELITE_INTEROP_LOG");
        if (path == null)
        {
            return;
        }

        lock (LogLock)
        {
            File.AppendAllText(path, line + "\n");
        }
    }
}
