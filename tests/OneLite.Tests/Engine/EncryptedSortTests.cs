using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Oracle;
using RecursiveAI.OneLite.Storage.Crypto;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// An encrypted database never writes sort runs to a temporary file: a sort run holds whole rows and temporary files are not encrypted, so it
/// sorts in memory however large the input (docs/encryption-review.md). The control run proves the test can see a spill at all.
/// </summary>
public sealed class EncryptedSortTests : IDisposable
{
    private const string Canary = "CANARY-sort-row-4e1d7a90-do-not-store";
    private const string Pass = "PASSPHRASE-sort-canary";
    private static readonly CipherSettings Fast = new(4096, 2, CipherDigest.Sha512, CipherDigest.Sha512);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-encsort-" + Guid.NewGuid().ToString("N"));

    public EncryptedSortTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    // About 700 KB of canary text: more than the 256 KiB the sorter keeps in memory at the smallest cache size.
    private string MakePlain()
    {
        Assert.SkipUnless(File.Exists(OraclePaths.Cli), "the pinned sqlite3 shell is not installed");
        string path = Path.Combine(_dir, "plain.db");
        string script = "PRAGMA page_size=4096; CREATE TABLE t(id INTEGER PRIMARY KEY, g INT, v TEXT);\n" +
            "WITH RECURSIVE s(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM s WHERE n<3500) " +
            $"INSERT INTO t SELECT n, n % 50, '{Canary}-' || printf('%06d', (n * 7919) % 3500) || '-' || hex(zeroblob(70)) FROM s;\n" +
            ".filectrl reserve_bytes 80\nVACUUM;\n";
        SqliteCli.ApplyScript(path, script);
        return path;
    }

    private string MakeEncrypted()
    {
        string plain = MakePlain();
        string enc = Path.Combine(_dir, "enc.db");
        using (FileStream i = File.OpenRead(plain))
        using (FileStream o = File.Create(enc))
        using (CipherKey k = CipherKey.FromPassphrase(Pass))
        {
            CipherFile.Encrypt(i, o, Fast, k);
        }

        return enc;
    }

    private static readonly string[] s_queries =
    [
        "SELECT v FROM t ORDER BY v",
        "SELECT DISTINCT v FROM t",
        "SELECT v, count(*) FROM t GROUP BY v",
        "SELECT g, group_concat(v) FROM t GROUP BY g ORDER BY g",
    ];

    // Runs the queries with the smallest sort budget on an in-memory copy of the database. Temporary files live in the memory VFS under
    // names starting with NUL while they are open, so each query is checked, after its first step (when a sort has finished writing runs),
    // for any such file; returns every temporary file's bytes seen and the number of rows read.
    private static (List<byte[]> TempFiles, int Rows) RunAll(string path, CipherKey? key)
    {
        var mem = new MemoryVfs();
        mem.Import("x.db", File.ReadAllBytes(path));
        var temps = new List<byte[]>();
        int rows = 0;
        using (var db = Database.OpenReadOnly("x.db", new DatabaseOptions { Vfs = mem, CacheSize = -8, Key = key, Cipher = key == null ? null : Fast }))
        using (Connection conn = db.Connect())
        {
            foreach (string sql in s_queries)
            {
                using Statement st = conn.Prepare(sql);
                bool first = true;
                while (st.Step())
                {
                    rows++;
                    if (first)
                    {
                        first = false;
                        temps.AddRange(TempFiles(mem));
                    }
                }
            }
        }

        return (temps, rows);
    }

    // MemoryVfs keeps its anonymous temporary files out of List(); the dictionary is the only place they show, so a test reaches in for it.
    private static IEnumerable<byte[]> TempFiles(MemoryVfs mem)
    {
        var files = (System.Collections.IDictionary)typeof(MemoryVfs).GetField("_files", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(mem)!;
        lock (typeof(MemoryVfs).GetField("_gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(mem)!)
        {
            foreach (System.Collections.DictionaryEntry e in files)
            {
                if (((string)e.Key).StartsWith('\0'))
                {
                    yield return ((MemoryFileData)e.Value!).ToArray();
                }
            }
        }
    }

    private static bool Holds(List<byte[]> files, string needle)
    {
        byte[] n = Encoding.ASCII.GetBytes(needle);
        return files.Any(w => w.AsSpan().IndexOf(n) >= 0);
    }

    [Fact]
    public void ControlPlainDatabaseSpillsItsSortRunsToATemporaryFile()
    {
        var (temps, rows) = RunAll(MakePlain(), null);
        Assert.True(rows > 3500 * 2);
        Assert.True(Holds(temps, Canary), "the control must spill, or this test proves nothing");
    }

    [Fact]
    public void EncryptedDatabaseSortsInMemoryAndOpensNoTemporaryFile()
    {
        string enc = MakeEncrypted();
        using CipherKey key = CipherKey.FromPassphrase(Pass);
        var (temps, rows) = RunAll(enc, key);
        Assert.True(rows > 3500 * 2);
        Assert.Empty(temps);
    }
}
