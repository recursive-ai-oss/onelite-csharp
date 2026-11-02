using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.Differential;

/// <summary>Replays a directory of repro files and judges each against its recorded status.</summary>
internal static class Corpus
{
    internal sealed record Result(string File, bool Ok, string Message);

    internal static List<Result> Replay(string dir, string tmpDir)
    {
        var results = new List<Result>();
        foreach (string file in Directory.EnumerateFiles(dir, "*.sql", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            string rel = Path.GetRelativePath(dir, file);
            Repro r;
            try
            {
                r = Repro.Parse(File.ReadAllText(file));
            }
            catch (Exception e) when (e is FormatException or KeyNotFoundException or ArgumentException)
            {
                results.Add(new Result(rel, false, "cannot read repro: " + e.Message));
                continue;
            }

            Verdict? v = Executor.Replay(r, tmpDir);
            results.Add(Judge(rel, r, v));
        }

        return results;
    }

    private static Result Judge(string rel, Repro r, Verdict? v)
    {
        if (v is null)
        {
            return new Result(rel, false, "the setup statements do not all run on the reference");
        }

        if (r.Status == "pass")
        {
            return v.Kind == VerdictKind.Pass
                ? new Result(rel, true, "pass")
                : new Result(rel, false, $"expected pass, got {v.Kind} {v.Class} {v.Detail}");
        }

        if (v.Kind == VerdictKind.Pass)
        {
            return new Result(rel, false, $"expected-fail ({r.Class}) now passes: set '-- status: pass'");
        }

        if (v.Kind == VerdictKind.Fail && v.Class == r.Class && v.Engine == r.Engine)
        {
            return new Result(rel, true, $"expected-fail ({r.Class}) still fails");
        }

        return new Result(rel, false, $"expected-fail ({r.Class}, {r.Engine}), got {v.Kind} {v.Engine} {v.Class}: {v.Detail}");
    }

    /// <summary>Whether the pinned shell and library are installed (the corpus cannot run without them).</summary>
    internal static bool OraclesAvailable => File.Exists(OraclePaths.Cli) && File.Exists(OraclePaths.NativeLibrary);
}
