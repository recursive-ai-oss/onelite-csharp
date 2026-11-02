using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RecursiveAI.OneLite.SqlLogicTest;

/// <summary>A failure that is understood and accepted: a corpus expectation the pinned release no longer meets, for example.</summary>
public sealed class KnownDifference
{
    /// <summary>File relative to the corpus root.</summary>
    public string File { get; set; } = "";

    /// <summary>Line of the record.</summary>
    public int Line { get; set; }

    /// <summary>Why the difference is expected.</summary>
    public string Reason { get; set; } = "";
}

/// <summary>Lists, shards and runs corpus files; keeps one result file per script so a sweep can stop and resume.</summary>
public static class Sweep
{
    private static readonly JsonSerializerOptions s_compact = new() { WriteIndented = false };
    private static readonly JsonSerializerOptions s_caseInsensitive = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions s_pretty = new() { WriteIndented = true };

    /// <summary>Settings of a <c>run</c>.</summary>
    public sealed class Options
    {
        /// <summary>The directory holding the scripts (the <c>test/</c> tree of the corpus).</summary>
        public string Corpus { get; set; } = "";

        /// <summary>Where results go.</summary>
        public string Out { get; set; } = "";

        /// <summary>Scratch directory for engine database files.</summary>
        public string Scratch { get; set; } = "";

        /// <summary>The engine.</summary>
        public ISltEngine Engine { get; set; } = null!;

        /// <summary>1-based shard number.</summary>
        public int Shard { get; set; } = 1;

        /// <summary>Shard count.</summary>
        public int Shards { get; set; } = 1;

        /// <summary>Glob filters on the relative path (only <c>*</c>); empty means everything.</summary>
        public List<string> Filters { get; } = [];

        /// <summary>Start no new file after this many minutes (0 = no limit).</summary>
        public double MaxMinutes { get; set; }

        /// <summary>Re-run files that already have a result.</summary>
        public bool Force { get; set; }
    }

    /// <summary>All scripts under the corpus, as relative paths with <c>/</c>, in ordinal order.</summary>
    public static List<string> ListScripts(string corpus, IReadOnlyList<string> filters)
    {
        var res = new List<string>();
        foreach (string f in Directory.EnumerateFiles(corpus, "*.test", SearchOption.AllDirectories))
        {
            res.Add(Path.GetRelativePath(corpus, f).Replace('\\', '/'));
        }
        res.Sort(StringComparer.Ordinal);
        if (filters.Count > 0)
        {
            var rx = filters.Select(Glob).ToList();
            res = res.Where(p => rx.Any(r => r.IsMatch(p))).ToList();
        }
        return res;
    }

    private static Regex Glob(string g)
        => new("^" + Regex.Escape(g).Replace("\\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Splits files over <paramref name="shards"/> by size: largest first onto the lightest shard, ties by name, so every shard gets
    /// about the same bytes and the split is the same on every machine.
    /// </summary>
    public static List<string> ShardFiles(string corpus, List<string> files, int shard, int shards)
    {
        var sized = files.Select(f => (File: f, Size: new FileInfo(Path.Combine(corpus, f)).Length))
            .OrderByDescending(x => x.Size).ThenBy(x => x.File, StringComparer.Ordinal).ToList();
        var load = new long[shards];
        var mine = new List<string>();
        foreach ((string file, long size) in sized)
        {
            int best = 0;
            for (int i = 1; i < shards; i++)
            {
                if (load[i] < load[best])
                {
                    best = i;
                }
            }
            load[best] += size;
            if (best == shard - 1)
            {
                mine.Add(file);
            }
        }
        mine.Sort(StringComparer.Ordinal);
        return mine;
    }

    private static string ResultPath(string outDir, string rel) => Path.Combine(outDir, "files", rel + ".json");

    /// <summary>Runs this shard's files. Returns 0 when all finished, 3 when the time limit stopped it first.</summary>
    public static int Run(Options o)
    {
        Directory.CreateDirectory(o.Out);
        Directory.CreateDirectory(o.Scratch);
        List<string> all = ListScripts(o.Corpus, o.Filters);
        List<string> mine = ShardFiles(o.Corpus, all, o.Shard, o.Shards);
        string headerPath = Path.Combine(o.Out, "header.txt");
        if (!File.Exists(headerPath))
        {
            File.WriteAllText(headerPath,
                "# engine: " + o.Engine.Label + "\n"
                + "# runtime: " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription + " "
                + System.Runtime.InteropServices.RuntimeInformation.OSArchitecture + "\n"
                + "# started: " + DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture) + "\n"
                + o.Engine.Describe());
        }
        Console.WriteLine($"shard {o.Shard}/{o.Shards}: {mine.Count} of {all.Count} files");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        int done = 0, resumed = 0;
        for (int i = 0; i < mine.Count; i++)
        {
            string rel = mine[i];
            string rp = ResultPath(o.Out, rel);
            if (!o.Force && File.Exists(rp))
            {
                resumed++;
                continue;
            }
            if (o.MaxMinutes > 0 && clock.Elapsed.TotalMinutes >= o.MaxMinutes)
            {
                Console.WriteLine($"time limit reached: {mine.Count - i} files left for the next run");
                return 3;
            }
            FileResult r = Runner.RunFile(o.Engine, Path.Combine(o.Corpus, rel), rel, o.Scratch);
            Directory.CreateDirectory(Path.GetDirectoryName(rp)!);
            string tmp = rp + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(r, s_compact));
            File.Move(tmp, rp, overwrite: true);   // a killed run leaves whole files only
            done++;
            Console.WriteLine($"{rel}: {r.Records} records, {r.Passed} passed, {r.Failed} failed, {r.Skipped} skipped, {r.ElapsedMs} ms"
                + (r.Fatal is null ? "" : " FATAL " + r.Fatal));
        }
        Console.WriteLine($"shard {o.Shard}/{o.Shards} finished: {done} run, {resumed} already done");
        return 0;
    }

    /// <summary>The group a file is reported under: <c>select*</c> for the top-level scripts, <c>evidence</c>, <c>index/NAME</c>, <c>random/NAME</c>.</summary>
    public static string GroupOf(string rel)
    {
        string[] parts = rel.Split('/');
        if (parts.Length == 1)
        {
            string name = parts[0];
            int dot = name.IndexOf('.', StringComparison.Ordinal);
            string stem = dot < 0 ? name : name[..dot];
            return stem.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9') + "*";
        }
        return parts[0] is "index" or "random" && parts.Length > 2 ? parts[0] + "/" + parts[1] : parts[0];
    }

    /// <summary>One row of the per-directory table.</summary>
    public sealed class GroupSummary
    {
        /// <summary>Group name.</summary>
        public string Group { get; set; } = "";

        /// <summary>Files that have a result.</summary>
        public int Files { get; set; }

        /// <summary>Records (statements and queries).</summary>
        public long Records { get; set; }

        /// <summary>Passed.</summary>
        public long Passed { get; set; }

        /// <summary>Failed (all, explained or not).</summary>
        public long Failed { get; set; }

        /// <summary>Of the failures, wrong answers (the engine answered, differently); the others are errors.</summary>
        public long Wrong { get; set; }

        /// <summary>Failed and listed in the known-differences file.</summary>
        public long Explained { get; set; }

        /// <summary>Skipped by conditionals.</summary>
        public long Skipped { get; set; }

        /// <summary>Files that stopped on an engine exception.</summary>
        public int Fatal { get; set; }

        /// <summary>Corpus files with no result yet.</summary>
        public int Missing { get; set; }

        /// <summary>Wall-clock milliseconds, summed over files.</summary>
        public long ElapsedMs { get; set; }
    }

    /// <summary>Merges the per-file results into <c>summary.json</c> and <c>summary.md</c>. Returns 0 when every record passed or is explained and no file is missing or fatal.</summary>
    public static int Report(string outDir, string? corpus, string? knownPath)
    {
        var known = new Dictionary<(string, int), string>();
        if (knownPath is not null && File.Exists(knownPath))
        {
            foreach (KnownDifference k in JsonSerializer.Deserialize<List<KnownDifference>>(File.ReadAllText(knownPath), s_caseInsensitive) ?? [])
            {
                known[(k.File, k.Line)] = k.Reason;
            }
        }
        var results = new List<FileResult>();
        string filesDir = Path.Combine(outDir, "files");
        if (Directory.Exists(filesDir))
        {
            foreach (string f in Directory.EnumerateFiles(filesDir, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                results.Add(JsonSerializer.Deserialize<FileResult>(File.ReadAllText(f))!);
            }
        }
        var have = results.Select(r => r.File).ToHashSet(StringComparer.Ordinal);
        var missing = corpus is null ? [] : ListScripts(corpus, []).Where(p => !have.Contains(p)).ToList();

        var groups = new SortedDictionary<string, GroupSummary>(StringComparer.Ordinal);
        GroupSummary G(string name) => groups.TryGetValue(name, out GroupSummary? g) ? g : groups[name] = new GroupSummary { Group = name };
        var unexplained = new List<(FileResult File, Failure F)>();
        var explainedList = new List<(string File, int Line, string Reason)>();
        var staleKnown = new HashSet<(string, int)>(known.Keys);
        foreach (FileResult r in results)
        {
            GroupSummary g = G(GroupOf(r.File));
            g.Files++;
            g.Records += r.Records;
            g.Passed += r.Passed;
            g.Failed += r.Failed;
            g.Wrong += r.Wrong;
            g.Skipped += r.Skipped;
            g.ElapsedMs += r.ElapsedMs;
            if (r.Fatal is not null)
            {
                g.Fatal++;
            }
            foreach (int line in r.FailedLines)
            {
                if (known.TryGetValue((r.File, line), out string? why))
                {
                    g.Explained++;
                    explainedList.Add((r.File, line, why));
                    staleKnown.Remove((r.File, line));
                }
            }
            foreach (Failure f in r.Failures)
            {
                if (!known.ContainsKey((r.File, f.Line)))
                {
                    unexplained.Add((r, f));
                }
            }
        }
        foreach (string m in missing)
        {
            G(GroupOf(m)).Missing++;
        }

        var total = new GroupSummary { Group = "total" };
        foreach (GroupSummary g in groups.Values)
        {
            total.Files += g.Files;
            total.Records += g.Records;
            total.Passed += g.Passed;
            total.Failed += g.Failed;
            total.Wrong += g.Wrong;
            total.Explained += g.Explained;
            total.Skipped += g.Skipped;
            total.Fatal += g.Fatal;
            total.Missing += g.Missing;
            total.ElapsedMs += g.ElapsedMs;
        }
        long unexplainedCount = total.Failed - total.Explained;
        string header = File.Exists(Path.Combine(outDir, "header.txt")) ? File.ReadAllText(Path.Combine(outDir, "header.txt")) : "";

        File.WriteAllText(Path.Combine(outDir, "summary.json"), JsonSerializer.Serialize(new
        {
            header,
            total,
            unexplainedFailures = unexplainedCount,
            groups = groups.Values,
            missingFiles = missing,
            fatalFiles = results.Where(r => r.Fatal is not null).Select(r => new { r.File, r.Fatal }),
            unexplained = unexplained.Select(u => new { u.File.File, u.F }),
            explained = explainedList.Select(e => new { e.File, e.Line, e.Reason }),
            staleKnownDifferences = staleKnown.Select(k => new { File = k.Item1, Line = k.Item2 }),
        }, s_pretty));

        var md = new StringBuilder();
        md.Append("# sqllogictest results\n\n```\n").Append(header.TrimEnd()).Append("\n```\n\n");
        long ran = total.Passed + total.Failed;
        md.Append(CultureInfo.InvariantCulture, $"**{total.Files} files, {total.Records:N0} records: {total.Passed:N0} passed, {total.Failed:N0} failed ({total.Wrong:N0} of them wrong answers, {total.Explained:N0} explained), {total.Skipped:N0} skipped.** ");
        md.Append(CultureInfo.InvariantCulture, $"Pass rate of the records that ran: {(ran == 0 ? 0 : 100.0 * total.Passed / ran):F4}% (counting explained failures as passes: {(ran == 0 ? 0 : 100.0 * (total.Passed + total.Explained) / ran):F4}%). ");
        md.Append(CultureInfo.InvariantCulture, $"Files missing: {total.Missing}; files stopped by an engine exception: {total.Fatal}.\n\n");
        md.Append("| group | files | records | passed | failed | wrong | explained | skipped | missing | fatal | seconds |\n|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|\n");
        foreach (GroupSummary g in groups.Values.Append(total))
        {
            md.Append(CultureInfo.InvariantCulture, $"| {g.Group} | {g.Files} | {g.Records:N0} | {g.Passed:N0} | {g.Failed:N0} | {g.Wrong:N0} | {g.Explained:N0} | {g.Skipped:N0} | {g.Missing} | {g.Fatal} | {g.ElapsedMs / 1000.0:F1} |\n");
        }
        if (explainedList.Count > 0)
        {
            md.Append("\n## Explained failures\n\n");
            foreach (IGrouping<string, (string File, int Line, string Reason)> grp in explainedList.GroupBy(e => e.Reason))
            {
                md.Append(CultureInfo.InvariantCulture, $"- {grp.Count()} records: {grp.Key} (e.g. `{grp.First().File}` line {grp.First().Line})\n");
            }
        }
        if (unexplained.Count > 0)
        {
            md.Append("\n## Unexplained failures (first 25)\n");
            foreach ((FileResult file, Failure f) in unexplained.Take(25))
            {
                md.Append(CultureInfo.InvariantCulture, $"\n### `{file.File}` line {f.Line}: {f.Kind}\n\n```sql\n{f.Sql}\n```\nexpected:\n```\n{f.Expected}\n```\nactual:\n```\n{f.Actual}\n```\n");
            }
        }
        foreach (FileResult r in results.Where(r => r.Fatal is not null).Take(10))
        {
            md.Append(CultureInfo.InvariantCulture, $"\nFATAL `{r.File}`: {r.Fatal}\n");
        }
        if (staleKnown.Count > 0)
        {
            md.Append($"\n{staleKnown.Count} known-difference entries did not occur in this run.\n");
        }
        File.WriteAllText(Path.Combine(outDir, "summary.md"), md.ToString());
        Console.WriteLine(md.ToString().Split("## ")[0]);
        return unexplainedCount == 0 && total.Missing == 0 && total.Fatal == 0 ? 0 : 1;
    }
}
