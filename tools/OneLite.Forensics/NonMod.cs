using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Forensics;

namespace RecursiveAI.OneLite.ForensicsTool;

/// <summary>
/// Runs a complete forensic pass over every database file under the given folders and proves that no file of the set changed: not a byte, not a
/// modification time, and no file appeared (a <c>-shm</c> or journal that did not exist before). With <c>--ro</c> the files are first copied to a
/// folder whose files are mode 0444 and which is itself mode 0555.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
internal static class NonMod
{
    public sealed class Counts
    {
        public int Files, Identical, Mismatch, Refused, Failed;
        public long Snapshots, Tables, Rows, Carved, Anomalies;
        public readonly List<string> Problems = [];
    }

    private static bool IsSideFile(string f) => f.EndsWith("-wal", StringComparison.Ordinal) || f.EndsWith("-shm", StringComparison.Ordinal) || f.EndsWith("-journal", StringComparison.Ordinal);

    public static int Run(string[] args)
    {
        bool ro = args.Contains("--ro");
        bool onlyPass = args.Contains("--pass-only");
        var dirs = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        var files = new List<string>();
        foreach (string d in dirs)
        {
            files.AddRange(File.Exists(d) ? [d] : Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)
                .Where(f => !IsSideFile(f) && (f.EndsWith(".db", StringComparison.Ordinal) || f.EndsWith(".sqlite", StringComparison.Ordinal) || f.EndsWith(".sqlite3", StringComparison.Ordinal))));
        }

        var c = new Counts();
        string? work = ro ? Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "nonmod-ro-" + Environment.ProcessId) : null;
        foreach (string f in files.Order(StringComparer.Ordinal))
        {
            string target = f;
            if (ro)
            {
                Directory.CreateDirectory(work!);
                target = Path.Combine(work!, Path.GetFileName(f));
                Common.CopySet(f, target);
                foreach (string x in Common.FileSet(target))
                {
                    File.SetUnixFileMode(x, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                }

                File.SetUnixFileMode(work!, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            }

            try
            {
                List<Common.FileState> before = Common.Snapshot(target);
                string[] namesBefore = Directory.GetFiles(Path.GetDirectoryName(Path.GetFullPath(target))!, Path.GetFileName(target) + "*").Order(StringComparer.Ordinal).ToArray();
                c.Files++;
                try
                {
                    Pass(target, c);
                }
                catch (Exception e) when (e is OneLiteException { Code: ResultCode.Corrupt } or NotADatabaseException)
                {
                    c.Refused++;
                }
                catch (Exception e)
                {
                    c.Failed++;
                    c.Problems.Add($"{Path.GetFileName(f)}: unexpected {e.GetType().Name}: {e.Message}");
                }

                if (!onlyPass)
                {
                    List<Common.FileState> after = Common.Snapshot(target);
                    string[] namesAfter = Directory.GetFiles(Path.GetDirectoryName(Path.GetFullPath(target))!, Path.GetFileName(target) + "*").Order(StringComparer.Ordinal).ToArray();
                    if (before.SequenceEqual(after) && namesBefore.SequenceEqual(namesAfter))
                    {
                        c.Identical++;
                    }
                    else
                    {
                        c.Mismatch++;
                        c.Problems.Add($"{Path.GetFileName(f)}: files changed or appeared");
                    }
                }
            }
            finally
            {
                if (ro && work != null)
                {
                    File.SetUnixFileMode(work, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    foreach (string x in Directory.GetFiles(work))
                    {
                        File.SetUnixFileMode(x, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                        File.Delete(x);
                    }
                }
            }
        }

        if (work != null && Directory.Exists(work))
        {
            Directory.Delete(work, true);
        }

        Console.WriteLine($"files={c.Files} identical={c.Identical} mismatch={c.Mismatch} refused={c.Refused} failed={c.Failed} snapshots={c.Snapshots} tables={c.Tables} rows={c.Rows} carved={c.Carved} anomalies={c.Anomalies} readonly={ro}");
        foreach (string p in c.Problems.Take(30))
        {
            Console.WriteLine("  " + p);
        }

        return c.Mismatch + c.Failed == 0 ? 0 : 1;
    }

    /// <summary>Everything the API can do: every snapshot, every table, space, carving.</summary>
    public static void Pass(string path, Counts c)
    {
        using ForensicDatabase db = ForensicDatabase.Open(path);
        var sels = new List<SnapshotSelector> { SnapshotSelector.DatabaseOnly, SnapshotSelector.Latest, SnapshotSelector.LatestWithTrailingFrames };
        if (db.Wal != null)
        {
            sels.AddRange(db.Wal.Commits.Select(k => SnapshotSelector.AsOf(k.Id)));
        }

        foreach (SnapshotSelector sel in sels)
        {
            try
            {
                ForensicSnapshot s = db.OpenSnapshot(sel);
                c.Snapshots++;
                foreach (ForensicTable t in s.Tables)
                {
                    c.Tables++;
                    if (t.CanReadRows)
                    {
                        try
                        {
                            c.Rows += s.ReadRows(t).Count;
                        }
                        catch (OneLiteException cex) when (cex.Code == ResultCode.Corrupt)
                        {
                            c.Refused++;
                        }
                    }
                }

                c.Anomalies += s.Space().Anomalies.Count;
                c.Carved += s.Carve(new CarveOptions { MinConfidence = 0 }).Count;
            }
            catch (Exception e) when (e is OneLiteException { Code: ResultCode.Corrupt } or NotADatabaseException)
            {
                c.Refused++;
            }
        }
    }
}
