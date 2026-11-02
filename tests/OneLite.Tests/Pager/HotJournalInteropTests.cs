using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;
using PagerClass = RecursiveAI.OneLite.Storage.Pager.Pager;

namespace RecursiveAI.OneLite.Tests.Pager;

/// <summary>
/// Hot journals left by a killed reference shell (made by <c>tools/pager-experiments/gen-hot.py</c>; point ONELITE_HOT_CORPUS at its output).
/// Our pager must find each journal hot, play it back, and leave the database byte-identical to what the shell makes of a copy.
/// </summary>
public class HotJournalInteropTests
{
    private readonly ITestOutputHelper _out;

    public HotJournalInteropTests(ITestOutputHelper output) => _out = output;

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from))
        {
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        }
    }

    private static void Recover(FileVfs vfs, string path)
    {
        using PagerClass p = PagerClass.Open(vfs, path, Kit.Opts(4096));
        p.BeginReadSync();
        p.EndReadSync();
    }

    [Fact]
    public void RecoveryMatchesTheReferenceShellByteForByte()
    {
        string? dir = Environment.GetEnvironmentVariable("ONELITE_HOT_CORPUS");
        Assert.SkipWhen(dir == null, "ONELITE_HOT_CORPUS is not set");
        string backupRoot = Path.Combine(Path.GetTempPath(), "hotbackup-" + Environment.ProcessId);
        var vfs = new FileVfs();
        var table = new SortedDictionary<string, int[]>();
        var failures = new List<string>();
        try
        {
            foreach (string caseDir in Directory.GetDirectories(dir!, "c*").Order())
            {
                string[] meta = File.ReadAllText(Path.Combine(caseDir, "meta.txt")).Split(' ', '\n');
                string key = $"{meta[0]} ps={meta[1]} {(meta[4] == "multi" ? "multi-file" : "single")}";
                string backup = Path.Combine(backupRoot, Path.GetFileName(caseDir));
                CopyDir(caseDir, backup);
                bool ok = true;
                try
                {
                    string db = Path.Combine(caseDir, "db");
                    string db2 = Path.Combine(caseDir, "db2");
                    string expected = Path.Combine(caseDir, "expected.db");
                    Recover(vfs, db);
                    if (File.Exists(db2))
                    {
                        Recover(vfs, db2);
                    }

                    ok &= File.ReadAllBytes(db).AsSpan().SequenceEqual(File.ReadAllBytes(expected));
                    if (File.Exists(db2) && File.Exists(Path.Combine(caseDir, "expected2.db")))
                    {
                        ok &= File.ReadAllBytes(db2).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(caseDir, "expected2.db")));
                    }

                    if (!ok)
                    {
                        failures.Add(caseDir[^5..] + " (" + string.Join(' ', meta) + ") " + Kit.Describe(File.ReadAllBytes(db), File.ReadAllBytes(expected)) + " journal left: " + File.Exists(db + "-journal"));
                    }
                }
                finally
                {
                    foreach (string f in Directory.GetFiles(caseDir).Where(static f => !Path.GetFileName(f).StartsWith("expected", StringComparison.Ordinal)))
                    {
                        File.Delete(f);
                    }

                    foreach (string f in Directory.GetFiles(backup).Where(static f => !Path.GetFileName(f).StartsWith("expected", StringComparison.Ordinal)))
                    {
                        File.Copy(f, Path.Combine(caseDir, Path.GetFileName(f)), true);
                    }
                }

                if (!table.TryGetValue(key, out int[]? c))
                {
                    table[key] = c = new int[2];
                }

                c[ok ? 0 : 1]++;
            }
        }
        finally
        {
            if (Directory.Exists(backupRoot))
            {
                Directory.Delete(backupRoot, true);
            }
        }

        foreach ((string k, int[] c) in table)
        {
            _out.WriteLine($"{k}: identical {c[0]}, different {c[1]}");
        }

        _out.WriteLine($"total {table.Values.Sum(static c => c[0] + c[1])} cases, {table.Values.Sum(static c => c[1])} different");
        foreach (string f in failures)
        {
            _out.WriteLine("DIFF " + f);
        }

        Assert.Empty(failures);
        Assert.NotEmpty(table);
    }
}
