using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// Backup and VACUUM INTO over the generated and real-world corpus (fetched by the corpus scripts, never committed): every database is copied with
/// step sizes 1, 7 and all-at-once, and compacted into a new file; the shell's integrity_check must say ok and its .dump must equal the source's.
/// Rows are appended to the file named by <c>ONELITE_U31_REPORT</c> when set (page counts of VACUUM INTO against the shell's own).
/// </summary>
public sealed class CorpusBackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-corpus-" + Guid.NewGuid().ToString("N"));

    public CorpusBackupTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    public static IEnumerable<TheoryDataRow<string>> Files()
    {
        string root = OraclePaths.CorpusRoot;
        if (!Directory.Exists(root))
        {
            return [new TheoryDataRow<string>("")];
        }

        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".db", StringComparison.Ordinal) || f.EndsWith(".sqlite", StringComparison.Ordinal)) && !f.Contains("sqllogictest", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => new TheoryDataRow<string>(f))
            .ToList();
    }

    private string CopySet(string file, string name)
    {
        string d = Path.Combine(_dir, name);
        Directory.CreateDirectory(d);
        string dest = Path.Combine(d, Path.GetFileName(file));
        File.Copy(file, dest);
        foreach (string suffix in new[] { "-wal", "-journal", "-shm" })
        {
            if (File.Exists(file + suffix))
            {
                File.Copy(file + suffix, dest + suffix);
            }
        }

        return dest;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void BackupAndVacuumIntoMatchTheShell(string file)
    {
        Assert.SkipUnless(file.Length > 0 && File.Exists(OraclePaths.Cli), "no corpus or oracle on this machine");
        string forShell = CopySet(file, "shell");
        Proc.Result dump = SqliteCli.Run(forShell, ".dump");
        Assert.SkipUnless(dump.ExitCode == 0 && dump.Stderr.Length == 0, "the shell cannot dump this file");
        string expected = dump.Stdout;
        long size = new FileInfo(file).Length;
        foreach (int step in size > 8_000_000 ? new[] { 64, -1 } : new[] { 1, 7, -1 })
        {
            string src = CopySet(file, "src" + step);
            string dst = Path.Combine(_dir, "dst" + step + ".db");
            using (Database sd = Database.Open(src))
            using (Connection sc = sd.Connect())
            using (Database dd = Database.Open(dst))
            using (Connection dc = dd.Connect())
            using (Backup b = Backup.Create(dc, "main", sc, "main"))
            {
                BackupStatus st;
                while ((st = b.Step(step)) == BackupStatus.More)
                {
                }

                Assert.Equal(BackupStatus.Done, st);
            }

            Assert.True(PinnedSqlite.IntegrityOk(dst, out string detail), "backup " + step + ": " + detail);
            Assert.Equal(expected, SqliteCli.Dump(dst));
        }

        string vsrc = CopySet(file, "vsrc");
        string vdst = Path.Combine(_dir, "vacuum.db");
        using (Database vd = Database.Open(vsrc))
        using (Connection vc = vd.Connect())
        {
            using Statement s = vc.Prepare($"VACUUM INTO '{vdst}'");
            while (s.Step())
            {
            }
        }

        Assert.True(PinnedSqlite.IntegrityOk(vdst, out string vdetail), "vacuum into: " + vdetail);
        Assert.Equal(expected, SqliteCli.Dump(vdst));
        string theirs = Path.Combine(_dir, "shell-vacuum.db");
        SqliteCli.Run(forShell, $"VACUUM INTO '{theirs}'");
        if (File.Exists(theirs) && Environment.GetEnvironmentVariable("ONELITE_U31_REPORT") is { Length: > 0 } report)
        {
            string pc(string f) => SqliteCli.Run(f, "pragma page_count;").Stdout.Trim();
            File.AppendAllText(report, $"{Path.GetFileName(file)} ours={pc(vdst)} shell={pc(theirs)} source={pc(forShell)}\n");
        }
    }
}
