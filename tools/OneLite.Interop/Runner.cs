using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.Interop;

/// <summary>
/// Two-way interop runner. For each corpus case:
/// <list type="bullet">
/// <item><b>read</b>: the reader command opens a private copy of the case's files and prints the canonical rendering; it must equal
/// the pinned sqlite3's rendering of the same file (stored in the corpus).</item>
/// <item><b>write</b>: the writer command builds a database from the case's SQL script; the pinned sqlite3 must find it sound
/// (<c>integrity_check</c> = ok), with the layout the script asked for, and its <c>.dump</c> must equal the expected dump.</item>
/// </list>
/// The reader and writer are external commands (see docs/interop-and-durability.md), so any implementation plugs in the same
/// way. The defaults are this program's own <c>render</c> and <c>write</c> subcommands, which use the pinned sqlite3.
/// </summary>
internal static class Runner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal sealed record Result(string Case, string Direction, string Status, string Detail, long Millis);

    public static int Run(string[] args)
    {
        string corpus = Path.Combine(OraclePaths.CorpusRoot, "generated");
        string? reader = null, writer = null, realworld = null, jsonOut = null, mdOut = null, work = null, filter = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--corpus": corpus = args[++i]; break;
                case "--reader": reader = args[++i]; break;
                case "--writer": writer = args[++i]; break;
                case "--realworld": realworld = args[++i]; break;
                case "--json": jsonOut = args[++i]; break;
                case "--md": mdOut = args[++i]; break;
                case "--work": work = args[++i]; break;
                case "--filter": filter = args[++i]; break;
                default: Console.Error.WriteLine("run: unknown option " + args[i]); return 2;
            }
        }
        corpus = Path.GetFullPath(corpus);
        work = Path.GetFullPath(work ?? Path.Combine(Path.GetTempPath(), "onelite-interop-" + Environment.ProcessId));
        string header = PinnedSqlite.Header();
        Console.Write(header);
        Manifest m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(corpus, "manifest.json")))
            ?? throw new InvalidOperationException("empty manifest");

        var readerCmd = Command(reader, ["render", "{db}"]);
        var writerCmd = Command(writer, ["write", "{db}", "{script}"]);
        Console.WriteLine("# reader: " + string.Join(' ', readerCmd.Select(Mask)));
        Console.WriteLine("# writer: " + string.Join(' ', writerCmd.Select(Mask)));

        var results = new List<Result>();
        Directory.CreateDirectory(work);
        foreach (CorpusCase c in m.Cases.Where(c => filter == null || c.Name.Contains(filter, StringComparison.Ordinal)))
        {
            results.Add(Read(corpus, work, c, readerCmd));
            if (c.Writable && c.Script != null)
            {
                results.Add(Write(corpus, work, c, writerCmd));
            }
            else
            {
                results.Add(new Result(c.Name, "write", "skip", "needs process behaviour or file-level settings a script cannot express", 0));
            }
        }
        if (realworld != null)
        {
            foreach (string f in Directory.GetFiles(realworld).OrderBy(x => x, StringComparer.Ordinal).Where(f => !f.EndsWith("-journal", StringComparison.Ordinal)))
            {
                results.Add(ReadExternal(work, f, readerCmd));
            }
        }
        try
        {
            Directory.Delete(work, recursive: true);
        }
        catch (IOException)
        {
            // best effort: the directory is scratch
        }

        Report(header, corpus, results, jsonOut, mdOut);
        return results.Any(r => r.Status == "fail") ? 1 : 0;
    }

    private static string Mask(string s) => s;

    private static List<string> Command(string? text, string[] selfArgs)
    {
        if (text == null)
        {
            (string exe, List<string> a) = Proc.Self(selfArgs);
            a.Insert(0, exe);
            return a;
        }
        return [.. text.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }

    private static Proc.Result Invoke(List<string> cmd, string db, string script = "")
    {
        IEnumerable<string> args = cmd.Skip(1).Select(a => a.Replace("{db}", db, StringComparison.Ordinal).Replace("{script}", script, StringComparison.Ordinal));
        return Proc.Run(cmd[0], args, timeout: TimeSpan.FromMinutes(10));
    }

    private static Result Read(string corpus, string work, CorpusCase c, List<string> reader)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // The corpus must be what the manifest says it is.
            foreach (KeyValuePair<string, FileInfoEntry> f in c.Files)
            {
                if (Sha256(Path.Combine(corpus, f.Key)) != f.Value.Sha256)
                {
                    return new Result(c.Name, "read", "fail", "corpus file changed since generation: " + f.Key, sw.ElapsedMilliseconds);
                }
            }
            string db = Path.Combine(work, c.Name + ".r.db");
            PinnedSqlite.CopyFileSet(Path.Combine(corpus, c.Db), db);
            Proc.Result r = Invoke(reader, db);
            PinnedSqlite.DeleteFileSet(db);
            if (r.ExitCode != 0)
            {
                return new Result(c.Name, "read", "fail", "reader exited " + r.ExitCode.ToString(CultureInfo.InvariantCulture) + ": " + First(r.Stderr), sw.ElapsedMilliseconds);
            }
            string expected = File.ReadAllText(Path.Combine(corpus, c.ExpectedRender));
            string? diff = FirstDifference(expected, r.Stdout);
            return diff == null
                ? new Result(c.Name, "read", "pass", "", sw.ElapsedMilliseconds)
                : new Result(c.Name, "read", "fail", diff, sw.ElapsedMilliseconds);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or SqliteException or TimeoutException)
        {
            return new Result(c.Name, "read", "fail", e.Message, sw.ElapsedMilliseconds);
        }
    }

    // A file with no stored expectation (a real-world database): the pinned sqlite3 renders a copy now.
    private static Result ReadExternal(string work, string file, List<string> reader)
    {
        var sw = Stopwatch.StartNew();
        string name = "realworld/" + Path.GetFileName(file);
        try
        {
            string e = Path.Combine(work, "rw-expected.db");
            PinnedSqlite.CopyFileSet(file, e);
            string expected = PinnedSqlite.RenderAll(e);
            PinnedSqlite.DeleteFileSet(e);
            string db = Path.Combine(work, "rw-read.db");
            PinnedSqlite.CopyFileSet(file, db);
            Proc.Result r = Invoke(reader, db);
            PinnedSqlite.DeleteFileSet(db);
            if (r.ExitCode != 0)
            {
                return new Result(name, "read", "fail", "reader exited " + r.ExitCode.ToString(CultureInfo.InvariantCulture) + ": " + First(r.Stderr), sw.ElapsedMilliseconds);
            }
            string? diff = FirstDifference(expected, r.Stdout);
            return diff == null ? new Result(name, "read", "pass", "", sw.ElapsedMilliseconds) : new Result(name, "read", "fail", diff, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or SqliteException or TimeoutException)
        {
            return new Result(name, "read", "fail", ex.Message, sw.ElapsedMilliseconds);
        }
    }

    private static Result Write(string corpus, string work, CorpusCase c, List<string> writer)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string db = Path.Combine(work, c.Name + ".w.db");
            PinnedSqlite.DeleteFileSet(db);
            Proc.Result r = Invoke(writer, db, Path.Combine(corpus, c.Script!));
            if (r.ExitCode != 0)
            {
                return new Result(c.Name, "write", "fail", "writer exited " + r.ExitCode.ToString(CultureInfo.InvariantCulture) + ": " + First(r.Stderr), sw.ElapsedMilliseconds);
            }
            if (!File.Exists(db))
            {
                return new Result(c.Name, "write", "fail", "the writer produced no database file", sw.ElapsedMilliseconds);
            }
            // Work on a copy: opening a file with a journal or log recovers it.
            string chk = Path.Combine(work, c.Name + ".wchk.db");
            PinnedSqlite.CopyFileSet(db, chk);
            if (!PinnedSqlite.IntegrityOk(chk, out string detail))
            {
                return new Result(c.Name, "write", "fail", "integrity_check: " + detail, sw.ElapsedMilliseconds);
            }
            using (SqliteDb d = SqliteDb.Open(chk, readOnly: false, create: false))
            {
                string got = $"{d.Scalar("pragma page_size")}/{d.Scalar("pragma encoding")}/{d.Scalar("pragma auto_vacuum")}";
                string want = $"{c.PageSize}/{c.Encoding}/{c.AutoVacuum}";
                if (!string.Equals(got, want, StringComparison.OrdinalIgnoreCase))
                {
                    return new Result(c.Name, "write", "fail", $"layout {got}, the script asked for {want}", sw.ElapsedMilliseconds);
                }
            }
            string? diff = FirstDifference(SortStat1(File.ReadAllText(Path.Combine(corpus, c.ExpectedDump))), SortStat1(SqliteCli.Dump(chk)));
            PinnedSqlite.DeleteFileSet(db);
            PinnedSqlite.DeleteFileSet(chk);
            return diff == null ? new Result(c.Name, "write", "pass", "", sw.ElapsedMilliseconds) : new Result(c.Name, "write", "fail", ".dump differs: " + diff, sw.ElapsedMilliseconds);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or SqliteException or TimeoutException)
        {
            return new Result(c.Name, "write", "fail", e.Message, sw.ElapsedMilliseconds);
        }
    }

    // The rows of sqlite_stat1 are written in the order ANALYZE visits the tables, which is the order of the reference's own in-memory hash of the
    // schema (not documented), so a writer is compared on the set of those rows and on everything else exactly.
    internal static string SortStat1(string dump)
    {
        string[] lines = dump.Split('\n');
        var result = new List<string>(lines.Length);
        var run = new List<string>();
        void Flush()
        {
            run.Sort(StringComparer.Ordinal);
            result.AddRange(run);
            run.Clear();
        }

        foreach (string line in lines)
        {
            if (line.StartsWith("INSERT INTO sqlite_stat1 ", StringComparison.Ordinal))
            {
                run.Add(line);
            }
            else
            {
                Flush();
                result.Add(line);
            }
        }

        Flush();
        return string.Join('\n', result);
    }

    private static string First(string s) => s.Length > 300 ? s[..300] : s.Trim();

    internal static string? FirstDifference(string expected, string actual)
    {
        if (expected == actual)
        {
            return null;
        }
        string[] a = expected.Split('\n'), b = actual.Split('\n');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (a[i] != b[i])
            {
                return $"line {i + 1}: expected '{Clip(a[i])}', got '{Clip(b[i])}'";
            }
        }
        return $"length differs: expected {a.Length} lines, got {b.Length}";
    }

    private static string Clip(string s) => s.Length > 120 ? s[..120] + "..." : s;

    private static string Sha256(string path)
    {
        using FileStream f = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }

    private static void Report(string header, string corpus, List<Result> results, string? jsonOut, string? mdOut)
    {
        var byCase = results.GroupBy(r => r.Case).ToList();
        var sb = new StringBuilder();
        sb.Append("| case | read | write |\n|---|---|---|\n");
        foreach (IGrouping<string, Result> g in byCase)
        {
            string Cell(string dir)
            {
                Result? r = g.FirstOrDefault(x => x.Direction == dir);
                return r == null ? "" : r.Status == "fail" ? "FAIL " + r.Detail : r.Status;
            }
            sb.Append("| ").Append(g.Key).Append(" | ").Append(Cell("read")).Append(" | ").Append(Cell("write")).Append(" |\n");
        }
        int pass = results.Count(r => r.Status == "pass"), fail = results.Count(r => r.Status == "fail"), skip = results.Count(r => r.Status == "skip");
        string summary = $"results: {pass} pass, {fail} fail, {skip} skip, {byCase.Count} cases ({results.Count(r => r.Direction == "read" && r.Status == "pass")}/{results.Count(r => r.Direction == "read")} read, {results.Count(r => r.Direction == "write" && r.Status == "pass")}/{results.Count(r => r.Direction == "write" && r.Status != "skip")} write)";
        Console.WriteLine(sb.ToString());
        Console.WriteLine(summary);
        if (mdOut != null)
        {
            File.WriteAllText(mdOut, "```\n" + header + "```\n\n" + sb + "\n" + summary + "\n");
        }
        if (jsonOut != null)
        {
            var doc = new { oracle = header, corpus = Path.GetFileName(corpus), pass, fail, skip, results };
            File.WriteAllText(jsonOut, JsonSerializer.Serialize(doc, JsonOptions) + "\n");
        }
    }
}
