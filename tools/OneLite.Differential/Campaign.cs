using RecursiveAI.OneLite.QueryDiff;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.Differential;

internal sealed class CampaignOptions
{
    internal long Seed = 1;

    /// <summary>Cases to run (0 = until the duration is over).</summary>
    internal long Count;
    internal TimeSpan? Duration;
    internal long StartCase;
    internal Fam.Filter Filter = new([]);
    internal HashSet<string> Oracles = ["diff", "pqs", "norec", "tlp"];
    internal int Jobs = 4;
    internal string OutDir = "differential-out";
    internal string TmpDir = Path.Combine(Path.GetTempPath(), "onelite-differential");

    /// <summary>Replays one minimization may spend, and how many unexplained differences get minimized per run.</summary>
    internal int MinimizeBudget = 500;
    internal int MinimizeLimit = 40;

    /// <summary>Build each case's database by running the setup statements on OneLite (the checks then read that file) and compare it with the reference-built one.</summary>
    internal bool SetupOneLite;
}

internal sealed class Counter
{
    public long Checks { get; set; }
    public long Pass { get; set; }
    public long Documented { get; set; }
    public long OpenBugs { get; set; }
    public long Unexplained { get; set; }
    public long Unsupported { get; set; }
    public long Inconclusive { get; set; }
    public long ReferenceErrors { get; set; }

    internal void Add(Verdict v)
    {
        Checks++;
        if (v.ReferenceError)
        {
            ReferenceErrors++;
        }

        switch (v.Kind)
        {
            case VerdictKind.Pass: Pass++; break;
            case VerdictKind.Unsupported: Unsupported++; break;
            case VerdictKind.Inconclusive: Inconclusive++; break;
            default:
                switch (v.Category)
                {
                    case "documented": Documented++; break;
                    case "bug": OpenBugs++; break;
                    default: Unexplained++; break;
                }

                break;
        }
    }
}

/// <summary>Runs cases in parallel, tallies the verdicts and keeps minimized repros.</summary>
internal sealed class Campaign
{
    private readonly CampaignOptions _o;
    private readonly object _lock = new();
    private readonly SortedDictionary<string, Counter> _byFamily = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, Counter> _byOracle = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, long> _byClass = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, long> _setupFamilies = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, long> _engineFails = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, long> _unsupportedReasons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _signatures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _classSamples = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, string> _repros = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, string> _fingerprints = new();
    private readonly List<string> _toolErrors = [];
    private readonly List<string> _setupDiffs = [];
    private long _setupUnsupported;
    private long _setupBuilt;
    private readonly List<string> _firstFails = [];
    private long _cases, _emptyCases, _next, _stmts, _minimized;
    private int _unexplainedFound;

    internal Campaign(CampaignOptions o) => _o = o;

    internal int Run()
    {
        // Load the native library once, before the workers race to do it.
        _ = SqliteDb.LibVersion;
        Directory.CreateDirectory(_o.OutDir);
        Directory.CreateDirectory(Path.Combine(_o.OutDir, "repros"));
        Directory.CreateDirectory(_o.TmpDir);
        _next = _o.StartCase;
        long end = _o.Count > 0 ? _o.StartCase + _o.Count : long.MaxValue;
        var clock = Stopwatch.StartNew();
        DateTime deadline = _o.Duration is { } d ? DateTime.UtcNow + d : DateTime.MaxValue;
        if (_o.Count == 0 && _o.Duration is null)
        {
            end = _o.StartCase + 100;
        }

        var threads = new List<Thread>();
        for (int j = 0; j < Math.Max(1, _o.Jobs); j++)
        {
            var t = new Thread(() =>
            {
                while (DateTime.UtcNow < deadline)
                {
                    long i = Interlocked.Increment(ref _next) - 1;
                    if (i >= end)
                    {
                        Interlocked.Decrement(ref _next);
                        break;
                    }

                    OneCase(i);
                }
            });
            t.Start();
            threads.Add(t);
        }

        foreach (Thread t in threads)
        {
            t.Join();
        }

        clock.Stop();
        return Report(clock.Elapsed);
    }

#pragma warning disable CA1031 // a case that fails to build or run is reported and skipped; the campaign goes on
    private void OneCase(long index)
    {
        string path = Path.Combine(_o.TmpDir, $"c-{_o.Seed}-{index}-{Environment.CurrentManagedThreadId}.db");
        try
        {
            Executor.DeleteFiles(path);
            CaseGen.Plan plan = CaseGen.Generate(_o.Seed, index, _o.Filter, _o.Oracles, path);
            Interlocked.Increment(ref _cases);
            if (plan.Checks.Count == 0)
            {
                Interlocked.Increment(ref _emptyCases);
                _fingerprints[index] = Hashing.Short(string.Join(";", plan.Setup.Statements));
                return;
            }

            var fp = new StringBuilder();
            foreach (string s in plan.Setup.Statements)
            {
                fp.Append(s).Append(';');
            }

            foreach (Check c in plan.Checks)
            {
                foreach ((string k, string v) in c.Fields())
                {
                    fp.Append(k).Append('=').Append(v).Append('|');
                }
            }

            _fingerprints[index] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fp.ToString())));
            Interlocked.Add(ref _stmts, plan.Setup.Statements.Count);
            Classifier.Setup = string.Join(";", plan.Setup.Statements);
            string runPath = path;
            string? olPath = null;
            if (_o.SetupOneLite)
            {
                olPath = path + "-ol.db";
                Executor.DeleteFiles(olPath);
                string? problem = OneLiteSetup.Build(olPath, path, plan.Setup.Statements, plan.Setup.Utf16, out bool unsupported);
                if (unsupported)
                {
                    Interlocked.Increment(ref _setupUnsupported);
                }
                else if (problem is not null)
                {
                    lock (_lock)
                    {
                        if (_setupDiffs.Count < 400)
                        {
                            _setupDiffs.Add($"case {index}: {problem}");
                        }
                        else
                        {
                            _setupDiffs.Add("...");
                        }
                    }
                }
                else
                {
                    Interlocked.Increment(ref _setupBuilt);
                    runPath = olPath;
                }
            }

            Verdict[] verdicts = Executor.Run(runPath, plan.Checks);
            for (int i = 0; i < verdicts.Length; i++)
            {
                Tally(plan, plan.Checks[i], verdicts[i]);
            }

            lock (_lock)
            {
                foreach (string f in plan.Setup.Families)
                {
                    _setupFamilies[f] = _setupFamilies.GetValueOrDefault(f) + 1;
                }
            }
        }
        catch (Exception e)
        {
            lock (_lock)
            {
                if (_toolErrors.Count < 20)
                {
                    _toolErrors.Add($"case {index}: {e.GetType().Name}: {e.Message}");
                }
            }
        }
        finally
        {
            Executor.DeleteFiles(path);
            Executor.DeleteFiles(path + "-ol.db");
        }
    }
#pragma warning restore CA1031

    private void Tally(CaseGen.Plan plan, Check c, Verdict v)
    {
        lock (_lock)
        {
            if (!_byOracle.TryGetValue(c.Oracle, out Counter? oc))
            {
                _byOracle[c.Oracle] = oc = new Counter();
            }

            oc.Add(v);
            foreach (string f in c.Families)
            {
                if (!_byFamily.TryGetValue(f, out Counter? fc))
                {
                    _byFamily[f] = fc = new Counter();
                }

                fc.Add(v);
            }

            if (v.Kind == VerdictKind.Unsupported)
            {
                string why = Regex.Replace(v.Detail, @"'[^']*'|\d+", "_");
                _unsupportedReasons[why] = _unsupportedReasons.GetValueOrDefault(why) + 1;
            }

            if (v.Kind == VerdictKind.Fail)
            {
                string key = $"{c.Oracle}/{v.Class}";
                _byClass[key] = _byClass.GetValueOrDefault(key) + 1;
                _engineFails[v.Engine] = _engineFails.GetValueOrDefault(v.Engine) + 1;
            }
        }

        if (v.Kind != VerdictKind.Fail)
        {
            return;
        }

        // The same defect shows up in many queries; group by what the difference says, not by which query exposed it.
        string sig = $"{c.Oracle}|{v.Engine}|{v.Class}|{Regex.Replace(v.Detail.Length > 90 ? v.Detail[..90] : v.Detail, @"\d+", "N")}";
        bool minimize;
        lock (_lock)
        {
            int seen = _signatures.GetValueOrDefault(sig);
            _signatures[sig] = seen + 1;
            bool documented = v.Category != "unexplained";
            string classKey = $"{c.Oracle}/{v.Class}/{v.Engine}";
            minimize = seen < 2 && (documented ? _classSamples.Add(classKey) : _unexplainedFound++ < _o.MinimizeLimit);
            if (_firstFails.Count < 30 && seen == 0)
            {
                _firstFails.Add($"{c.Oracle} {v.Engine} {v.Class}: {v.Detail}");
            }
        }

        if (!minimize)
        {
            return;
        }

        var repro = new Repro { Setup = plan.Setup.Statements, Check = c, Class = v.Class, Engine = v.Engine, Detail = v.Detail };
        Repro small = Minimizer.Minimize(repro, _o.TmpDir, _o.MinimizeBudget);
        string text = small.Render();
        // Named by what the minimized check says with table and column numbers blanked, so one defect found twice is one file.
        string shape = Regex.Replace(string.Join("|", small.Check.Fields().Select(f => f.Value)), @"\b[tc]\d+\b|\d+", "N");
        string file = Path.Combine("repros", $"{c.Oracle}-{v.Class.Replace(':', '-')}-{Hashing.Short(shape + small.Class)}.sql");
        File.WriteAllText(Path.Combine(_o.OutDir, file), text);
        lock (_lock)
        {
            _repros[file] = $"{c.Oracle} {v.Engine} {v.Class}";
            _minimized++;
        }
    }

    private int Report(TimeSpan elapsed)
    {
        long unexplained = _byOracle.Values.Sum(c => c.Unexplained) + _setupDiffs.Count;
        long documented = _byOracle.Values.Sum(c => c.Documented);
        long bugs = _byOracle.Values.Sum(c => c.OpenBugs);
        long unsupported = _byOracle.Values.Sum(c => c.Unsupported);
        long total = _byOracle.Values.Sum(c => c.Checks);
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", _fingerprints.OrderBy(k => k.Key).Select(k => k.Key.ToString(CultureInfo.InvariantCulture) + ":" + k.Value)))))[..16].ToLowerInvariant();
        long nextCase = _next;

        var summary = new
        {
            seed = _o.Seed,
            startCase = _o.StartCase,
            nextCase,
            cases = _cases,
            emptyCases = _emptyCases,
            setupStatements = _stmts,
            elapsedSeconds = Math.Round(elapsed.TotalSeconds, 1),
            fingerprint,
            families = _o.Filter.Any ? "restricted" : "all",
            oracles = _o.Oracles.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            checks = total,
            documented,
            openBugs = bugs,
            unexplained,
            unsupported,
            byOracle = _byOracle,
            byFamily = _byFamily,
            setupFamilies = _setupFamilies,
            failuresByClass = _byClass,
            failuresByEngine = _engineFails,
            unsupportedReasons = _unsupportedReasons.OrderByDescending(k => k.Value).Take(40).ToDictionary(k => k.Key, k => k.Value),
            minimizedRepros = _repros,
            toolErrors = _toolErrors,
            driver = Qd.Cold ? "async-cold" : Qd.Async ? "async" : "sync",
            asyncCalls = Qd.Cold ? Qd.ColdVfs.Calls : 0,
            syncFallbacks = Qd.Fallbacks,
            setupVia = _o.SetupOneLite ? "onelite" : "reference",
            setupBuiltByOneLite = _setupBuilt,
            setupUnsupported = _setupUnsupported,
            setupDifferences = _setupDiffs,
            sampleFailures = _firstFails,
        };
        string json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(_o.OutDir, $"summary-{_o.Seed}-{_o.StartCase}.json"), json);

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"differential seed={_o.Seed} cases {_o.StartCase}..{nextCase - 1} ({_cases} built, {_emptyCases} empty) in {elapsed.TotalSeconds:F0}s, fingerprint {fingerprint}\n");
        sb.Append(CultureInfo.InvariantCulture, $"checks {total}: {total - documented - bugs - unexplained - unsupported - _byOracle.Values.Sum(c => c.Inconclusive)} pass, {documented} documented classes, {bugs} open bugs (repro filed), {unexplained} UNEXPLAINED, {unsupported} unsupported by OneLite, {_byOracle.Values.Sum(c => c.Inconclusive)} inconclusive\n");
        Table(sb, "oracle", _byOracle);
        Table(sb, "family", _byFamily);
        foreach (KeyValuePair<string, long> kv in _byClass)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  failures {kv.Key}: {kv.Value}\n");
        }

        foreach (KeyValuePair<string, long> kv in _unsupportedReasons.OrderByDescending(k => k.Value).Take(8))
        {
            sb.Append(CultureInfo.InvariantCulture, $"  unsupported x{kv.Value}: {kv.Key}\n");
        }

        foreach (KeyValuePair<string, string> kv in _repros)
        {
            sb.Append("  repro ").Append(kv.Key).Append("  [").Append(kv.Value).Append("]\n");
        }

        foreach (string e in _toolErrors.Take(5))
        {
            sb.Append("  TOOL ERROR ").Append(e).Append('\n');
        }

        sb.Append(CultureInfo.InvariantCulture, $"resume with: --seed {_o.Seed} --start-case {nextCase}\n");
        Console.Write(sb.ToString());
        return unexplained == 0 && _toolErrors.Count == 0 ? 0 : 1;
    }

    private static void Table(StringBuilder sb, string what, SortedDictionary<string, Counter> d)
    {
        sb.Append(CultureInfo.InvariantCulture, $"  {what,-12} {"checks",8} {"pass",8} {"known",7} {"bugs",7} {"UNEXPL",7} {"unsupp",8} {"incon",7} {"refErr",7}\n");
        foreach (KeyValuePair<string, Counter> kv in d)
        {
            Counter c = kv.Value;
            sb.Append(CultureInfo.InvariantCulture, $"  {kv.Key,-12} {c.Checks,8} {c.Pass,8} {c.Documented,7} {c.OpenBugs,7} {c.Unexplained,7} {c.Unsupported,8} {c.Inconclusive,7} {c.ReferenceErrors,7}\n");
        }
    }
}
