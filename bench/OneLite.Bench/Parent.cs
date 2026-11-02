using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RecursiveAI.OneLite.Bench;

/// <summary>The orchestrating process: starts one child per row, level and side, and writes the JSON and markdown reports.</summary>
internal static class Parent
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    // Rows that are reported but left out of the geometric mean: they time process-level behaviour, not the step loop.
    private static readonly HashSet<string> ReportOnly = ["open_time"];

    internal sealed record Env
    {
        public string Machine { get; init; } = "";

        public string Cpu { get; init; } = "";

        public int Cores { get; init; }

        public string Os { get; init; } = "";

        public string Runtime { get; init; } = "";

        public string Sdk { get; init; } = "";

        public string NativeVersion { get; init; } = "";

        public string AppNativeVersion { get; init; } = "";

        public string MdsVersion { get; init; } = "";

        public string Job { get; set; } = "";

        public string WqClass { get; init; } = "";

        public string Label { get; init; } = "";

        public double LoadBefore { get; init; }

        public double LoadMax { get; set; }

        public double LoadAfter { get; set; }

        public int Reps { get; init; }

        public int N { get; init; }

        public string Commit { get; init; } = "";

        public string LoadNote { get; set; } = "";
    }

    internal sealed record Doc(Env Env, List<ChildResult> Results);

    /// <summary>Joins the JSON files of several filtered runs into one report (the geometric mean then covers all rows).</summary>
    public static int Merge(Dictionary<string, string> o, string[] files)
    {
        var docs = files.Select(f => JsonSerializer.Deserialize<Doc>(File.ReadAllText(f), Web)!).ToList();
        Env env = docs[0].Env;
        env.Job = string.Join(", ", docs.Select(d => d.Env.Job));
        env.LoadNote = string.Join("; ", docs.Select((d, i) => string.Create(Inv, $"group {i + 1} before {d.Env.LoadBefore:F2}, highest between rows {d.Env.LoadMax:F2}, after {d.Env.LoadAfter:F2}")));
        var all = docs.SelectMany(d => d.Results).ToList();
        string prefix = o.GetValueOrDefault("out", "bench-merged");
        File.WriteAllText(prefix + ".json", JsonSerializer.Serialize(new { env, results = all }, Indented));
        File.WriteAllText(prefix + ".md", Markdown(env, all, o.GetValueOrDefault("levels", "engine,app").Split(',')));
        Console.WriteLine("wrote " + prefix + ".json and .md");
        return 0;
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static int Run(Dictionary<string, string> o, string native)
    {
        string[] rowNames = o.GetValueOrDefault("rows", "all") == "all"
            ? [.. Rows.All.Select(r => r.Name), "cold_start"]
            : o["rows"].Split(',', StringSplitOptions.RemoveEmptyEntries);
        string[] levels = o.GetValueOrDefault("levels", "engine,app").Split(',', StringSplitOptions.RemoveEmptyEntries);
        int reps = int.Parse(o.GetValueOrDefault("reps", "3"), Inv);
        int n = int.Parse(o.GetValueOrDefault("n", "100000"), Inv);
        string dir = o.GetValueOrDefault("dir", Path.Combine(Program.Home(), "onelite/bench-data"));
        string outPrefix = o.GetValueOrDefault("out", "bench-result");
        string? vec = o.GetValueOrDefault("vec");

        string mdsServer;
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:"))
        {
            c.Open();
            mdsServer = c.ServerVersion;
        }

        var env = new Env
        {
            Machine = Environment.GetEnvironmentVariable("BENCH_MACHINE") ?? "",
            Cpu = CpuName(),
            Cores = Environment.ProcessorCount,
            Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription + " " + System.Runtime.InteropServices.RuntimeInformation.OSArchitecture,
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Sdk = Environment.GetEnvironmentVariable("BENCH_SDK") ?? "",
            NativeVersion = Native.Version(),
            AppNativeVersion = mdsServer,
            MdsVersion = typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly.GetName().Version?.ToString() ?? "?",
            Job = o.GetValueOrDefault("job", Environment.GetEnvironmentVariable("REMOTE_RUN_JOB_ID") ?? ""),
            WqClass = o.GetValueOrDefault("class", ""),
            Label = o.GetValueOrDefault("label", ""),
            LoadBefore = Load(),
            Reps = reps,
            N = n,
            Commit = o.GetValueOrDefault("commit", Environment.GetEnvironmentVariable("BENCH_COMMIT") ?? ""),
        };
        env.LoadMax = env.LoadBefore;

        var results = new List<ChildResult>();
        foreach (string row in rowNames)
        {
            foreach (string level in levels)
            {
                if (row == "cold_start")
                {
                    foreach (string side in new[] { "onelite", "native" })
                    {
                        ChildResult? prep = Spawn(native, row, level, side, dir, n, reps, vec, "prep");
                        var cold = new List<double>();
                        ChildResult? last = null;
                        for (int k = 0; k < 3 && prep is not null; k++)
                        {
                            last = Spawn(native, row, level, side, dir, n, reps, vec, "first");
                            if (last is not null)
                            {
                                cold.Add(last.ColdMs);
                            }
                        }

                        if (last is not null)
                        {
                            results.Add(last with { ColdMs = Program.Median([.. cold]), MedianMs = Program.Median([.. cold]), RepMs = [.. cold] });
                        }
                    }

                    if (level == "engine" && o.TryGetValue("aot", out string? aot))
                    {
                        results.AddRange(ColdAot(aot, native, dir));
                    }

                    continue;
                }

                foreach (string side in new[] { "onelite", "native" })
                {
                    ChildResult? r = Spawn(native, row, level, side, dir, n, reps, vec, "all");
                    if (r is not null)
                    {
                        results.Add(r);
                    }

                    env.LoadMax = Math.Max(env.LoadMax, Load());
                }
            }
        }

        env.LoadAfter = Load();
        File.WriteAllText(outPrefix + ".json", JsonSerializer.Serialize(new { env, results }, Indented));
        File.WriteAllText(outPrefix + ".md", Markdown(env, results, levels));
        Console.WriteLine("wrote " + outPrefix + ".json and .md");
        return 0;
    }

    // The NativeAOT cold-start executable, run on the databases the JIT cold-start rows just prepared.
    private static List<ChildResult> ColdAot(string exe, string native, string dir)
    {
        var list = new List<ChildResult>();
        foreach (string side in new[] { "onelite", "native" })
        {
            var ms = new List<double>();
            long check = 0;
            for (int k = 0; k < 3; k++)
            {
                var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false };
                foreach (string a in new[] { side, Path.Combine(dir, $"cold_start-engine-{side}.db"), native })
                {
                    psi.ArgumentList.Add(a);
                }

                using Process p = Process.Start(psi)!;
                string text = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                string[] parts = text.Trim().Split(' ');
                if (parts.Length == 3 && parts[0] == "COLD")
                {
                    ms.Add(double.Parse(parts[1], Inv));
                    check = long.Parse(parts[2], Inv);
                }
            }

            double m = Program.Median([.. ms]);
            Console.WriteLine($"cold_start_aot       engine {side,-8} {m,10:F1} ms");
            list.Add(new ChildResult { Row = "cold_start_aot", Level = "engine", Side = side, Impl = side == "onelite" ? "OneLite (NativeAOT)" : "native sqlite3 via P/Invoke (NativeAOT)", ColdMs = m, MedianMs = m, RepMs = [.. ms], Check = check });
        }

        return list;
    }

    private static ChildResult? Spawn(string native, string row, string level, string side, string dir, int n, int reps, string? vec, string phase)
    {
        string exe = Environment.ProcessPath!;
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(exe) == "dotnet")
        {
            psi.ArgumentList.Add(typeof(Program).Assembly.Location);
        }

        foreach (string a in new[] { "child", "--row", row, "--level", level, "--side", side, "--dir", dir, "--n", n.ToString(Inv), "--reps", reps.ToString(Inv), "--phase", phase, "--native", native })
        {
            psi.ArgumentList.Add(a);
        }

        if (vec is not null)
        {
            psi.ArgumentList.Add("--vec");
            psi.ArgumentList.Add(vec);
        }

        using Process p = Process.Start(psi)!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        string text = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(15 * 60 * 1000))
        {
            p.Kill(true);
        }

        string line = text.Split('\n').FirstOrDefault(l => l.StartsWith("RESULT ", StringComparison.Ordinal)) ?? "";
        if (line.Length == 0)
        {
            Console.Error.WriteLine($"FAILED {row}/{level}/{side}/{phase}: {Tail(err.Result)}");
            return null;
        }

        ChildResult r = JsonSerializer.Deserialize<ChildResult>(line[7..])!;
        Console.WriteLine($"{row,-20} {level,-6} {side,-8} {r.MedianMs,10:F2} ms  peak {r.PeakKb / 1024} MB  {r.Skipped}");
        return r;
    }

    private static string Tail(string s) => s.Length > 600 ? s[^600..] : s;

    private static string CpuName()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                return Capture("sysctl", "-n machdep.cpu.brand_string");
            }

            foreach (string l in File.ReadLines("/proc/cpuinfo"))
            {
                if (l.StartsWith("model name", StringComparison.Ordinal))
                {
                    return l[(l.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
                }
            }
        }
        catch (IOException)
        {
        }

        return "?";
    }

    private static string Capture(string exe, string args)
    {
        using Process p = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardOutput = true })!;
        string s = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return s;
    }

    /// <summary>One-minute load average.</summary>
    private static double Load()
    {
        try
        {
            string s = OperatingSystem.IsMacOS()
                ? Capture("sysctl", "-n vm.loadavg").Trim('{', '}', ' ')
                : File.ReadAllText("/proc/loadavg");
            return double.Parse(s.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], Inv);
        }
        catch (Exception e) when (e is IOException or FormatException or IndexOutOfRangeException)
        {
            return double.NaN;
        }
    }

    // ---- report -----------------------------------------------------------------------------------------------

    internal static string Markdown(Env env, IReadOnlyList<ChildResult> results, string[] levels)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### {env.Cpu}, {env.Cores} cores, {env.Os}");
        sb.AppendLine();
        sb.AppendLine($"- Runtime {env.Runtime}; reference library SQLite {env.NativeVersion} (engine rows, raw P/Invoke); `Microsoft.Data.Sqlite` {env.MdsVersion} on SQLite {env.AppNativeVersion} (app rows).");
        sb.AppendLine($"- Medians of {env.Reps} timed repetitions after one warm-up; base table {env.N:N0} rows; commit {env.Commit}; job {env.Job} (wq class `{env.WqClass}`); {env.Label}.");
        sb.AppendLine(env.LoadNote.Length > 0 ? "- Load average (1 min): " + env.LoadNote : $"- Load average (1 min): before {env.LoadBefore:F2}, highest between rows {env.LoadMax:F2}, after {env.LoadAfter:F2}.");
        foreach (string level in levels)
        {
            sb.AppendLine();
            sb.AppendLine(level == "engine"
                ? "#### Engine level: `Statement.Step` against native `sqlite3_step`"
                : "#### App level: `RecursiveAI.OneLite.Data` against `Microsoft.Data.Sqlite`");
            sb.AppendLine();
            sb.AppendLine(level == "engine"
                ? "| Row | Ops | OneLite ms | Native ms | Ratio | OneLite B/op | Peak MB (OneLite / native) | Results agree |"
                : "| Row | Ops | OneLite.Data ms | Microsoft.Data.Sqlite ms | Ratio | OneLite B/op | Peak MB (OneLite / MDS) | Results agree |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|:-:|");
            var ratios = new List<(string Row, double Ratio)>();
            foreach (IGrouping<string, ChildResult> g in results.Where(r => r.Level == level && !r.Row.StartsWith("cold_start", StringComparison.Ordinal)).GroupBy(r => r.Row))
            {
                ChildResult? a = g.FirstOrDefault(r => r.Side == "onelite");
                ChildResult? b = g.FirstOrDefault(r => r.Side == "native");
                if (a is null)
                {
                    continue;
                }

                bool have = b is { Skipped: null };
                double ratio = have && b!.MedianMs > 0 ? a.MedianMs / b.MedianMs : double.NaN;
                if (!double.IsNaN(ratio) && !ReportOnly.Contains(g.Key))
                {
                    ratios.Add((g.Key, ratio));
                }

                string agree = !have ? "n/a" : a.Check == b!.Check ? "yes" : "NO";
                sb.AppendLine(string.Create(Inv, $"| {g.Key}{(ReportOnly.Contains(g.Key) ? " (report only)" : "")} | {a.Ops:N0} | {a.MedianMs:F2} | {(have ? b!.MedianMs.ToString("F2", Inv) : "n/a")} | {(double.IsNaN(ratio) ? "n/a" : ratio.ToString("F2", Inv) + "x")} | {(a.Ops > 0 ? ((double)a.AllocBytes / a.Ops).ToString("F1", Inv) : "")} | {a.PeakKb / 1024} / {(have ? (b!.PeakKb / 1024).ToString(Inv) : "n/a")} | {agree} |"));
            }

            if (ratios.Count > 0)
            {
                double geo = Math.Exp(ratios.Average(x => Math.Log(x.Ratio)));
                sb.AppendLine();
                sb.AppendLine(string.Create(Inv, $"Geometric mean of the ratios over {ratios.Count} rows: **{geo:F2}x**; worst: {ratios.Max(x => x.Ratio):F2}x ({ratios.MaxBy(x => x.Ratio).Row})."));
            }

            sb.AppendLine();
            sb.AppendLine("Cold start (process start to the first query's answer, JIT, median of 3 fresh processes, report only):");
            foreach (ChildResult c in results.Where(r => r.Level == level && r.Row.StartsWith("cold_start", StringComparison.Ordinal)))
            {
                sb.AppendLine(string.Create(Inv, $"- {c.Impl}: {c.ColdMs:F0} ms"));
            }
        }

        return sb.ToString();
    }
}
