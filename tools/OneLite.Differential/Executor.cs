using System.Text;
using RecursiveAI.OneLite.Oracle;
using RecursiveAI.OneLite.QueryDiff;

namespace RecursiveAI.OneLite.Differential;

/// <summary>Runs checks against a database file on OneLite and on the pinned sqlite3 shell.</summary>
internal static class Executor
{
    /// <summary>Runs every query the checks ask for, phase by phase, and returns one verdict per check.</summary>
    internal static Verdict[] Run(string dbPath, IReadOnlyList<Check> checks)
    {
        var seen = new Lookup();
        Database? ours = null;
        Connection? conn = null;
        string openError = "";
        try
        {
            ours = Database.OpenReadOnly(dbPath);
            conn = ours.Connect();
        }
        catch (OneLiteException e)
        {
            openError = e.Message;
        }

        try
        {
            for (int phase = 0; phase < 3; phase++)
            {
                List<string> all = [.. checks.SelectMany(c => c.Queries(phase, seen))];
                if (all.Count == 0)
                {
                    break;
                }

                List<string> fresh = [.. all.Where(q => !seen.Has(q)).Distinct(StringComparer.Ordinal)];
                if (fresh.Count == 0)
                {
                    continue;
                }

                Outcome[] theirs = ShellRunner.RunBatch(dbPath, fresh);
                for (int i = 0; i < fresh.Count; i++)
                {
                    seen.Add(fresh[i], conn is null ? OpenFailure(openError) : RunOurs(conn, fresh[i]), theirs[i]);
                }
            }

            return [.. checks.Select(c => c.Judge(seen))];
        }
        finally
        {
            conn?.Dispose();
            ours?.Dispose();
        }
    }

    private static Outcome OpenFailure(string message) => new() { HasError = true, Phase = "open", Code = 1, Message = message };

#pragma warning disable CA1031 // any failure inside the engine under test is a finding, never a reason to stop the campaign
    private static Outcome RunOurs(Connection conn, string sql)
    {
        try
        {
            return OursRunner.Run(conn, sql);
        }
        catch (Exception e)
        {
            return new Outcome { HasError = true, Phase = "crash", Code = -1, Message = e.GetType().Name + ": " + e.Message };
        }
    }
#pragma warning restore CA1031

    /// <summary>Builds the database of a repro from its setup statements, runs its check and returns the verdict (null when the setup does not run).</summary>
    internal static Verdict? Replay(Repro repro, string tmpDir)
    {
        Directory.CreateDirectory(tmpDir);
        string path = Path.Combine(tmpDir, "r-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (SqliteDb db = SqliteDb.Open(path))
            {
                foreach (string s in repro.Setup)
                {
                    try
                    {
                        db.Exec(s);
                    }
                    catch (SqliteException)
                    {
                        return null;
                    }
                }
            }

            Classifier.Setup = string.Join(";", repro.Setup);
            return Run(path, [repro.Check])[0];
        }
        finally
        {
            DeleteFiles(path);
        }
    }

    internal static void DeleteFiles(string path)
    {
        foreach (string suffix in new[] { "", "-journal", "-wal", "-shm" })
        {
            try
            {
                File.Delete(path + suffix);
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>A self-contained, replayable difference: the statements that build the database and the check that failed.</summary>
internal sealed class Repro
{
    internal List<string> Setup = [];
    internal Check Check = null!;

    /// <summary>"pass" or "expected-fail".</summary>
    internal string Status = "expected-fail";
    internal string Class = Verdict.Unexplained;
    internal string Engine = "ours";
    internal string Detail = "";

    /// <summary>The file text. It also runs as a script in the sqlite3 shell: everything but the setup statements and the final queries is a comment.</summary>
    internal string Render()
    {
        var sb = new StringBuilder();
        sb.Append("-- OneLite differential repro\n");
        sb.Append("-- status: ").Append(Status).Append('\n');
        sb.Append("-- class: ").Append(Class).Append('\n');
        sb.Append("-- engine: ").Append(Engine).Append('\n');
        sb.Append("-- families: ").Append(string.Join(",", Check.Families.OrderBy(x => x, StringComparer.Ordinal))).Append('\n');
        sb.Append("-- utf16: ").Append(Check.Utf16 ? "true" : "false").Append('\n');
        foreach ((string k, string v) in Check.Fields())
        {
            sb.Append("-- ").Append(k).Append(": ").Append(v).Append('\n');
        }

        sb.Append("-- detail: ").Append(Detail.Replace('\n', ' ').Replace('\r', ' ')).Append('\n');
        sb.Append("-- setup\n");
        foreach (string s in Setup)
        {
            sb.Append(s).Append(";\n-- go\n");
        }

        sb.Append("-- run\n");
        var none = new Lookup();
        foreach (string q in Check.Queries(0, none))
        {
            sb.Append(q).Append(";\n");
        }

        return sb.ToString();
    }

    internal static Repro Parse(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var setup = new List<string>();
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int i = 0;
        for (; i < lines.Length && lines[i] != "-- setup"; i++)
        {
            string l = lines[i];
            if (l.StartsWith("-- ", StringComparison.Ordinal))
            {
                int c = l.IndexOf(": ", 3, StringComparison.Ordinal);
                if (c > 0)
                {
                    fields[l[3..c]] = l[(c + 2)..];
                }
            }
        }

        var cur = new StringBuilder();
        for (i++; i < lines.Length && lines[i] != "-- run"; i++)
        {
            if (lines[i] == "-- go")
            {
                string s = cur.ToString();
                cur.Clear();
                setup.Add(s.EndsWith(';') ? s[..^1] : s);
            }
            else
            {
                if (cur.Length > 0)
                {
                    cur.Append('\n');
                }

                cur.Append(lines[i]);
            }
        }

        return new Repro
        {
            Setup = setup,
            Check = Check.FromFields(fields),
            Status = fields.GetValueOrDefault("status", "expected-fail"),
            Class = fields.GetValueOrDefault("class", Verdict.Unexplained),
            Engine = fields.GetValueOrDefault("engine", "ours"),
            Detail = fields.GetValueOrDefault("detail", ""),
        };
    }

    internal Repro With(List<string> setup, Check check) => new()
    {
        Setup = setup,
        Check = check,
        Status = Status,
        Class = Class,
        Engine = Engine,
        Detail = Detail,
    };
}

/// <summary>
/// Delta reduction of a failing repro: drop setup statements (chunks first, then one by one), then simplify the check, and repeat
/// while the same failure (same engine, same class) persists.
/// </summary>
internal static class Minimizer
{
    internal static Repro Minimize(Repro start, string tmpDir, int budget)
    {
        int used = 0;
        bool Fails(Repro r)
        {
            if (used++ >= budget)
            {
                return false;
            }

            Verdict? v = Executor.Replay(r, tmpDir);
            return v is { Kind: VerdictKind.Fail } && v.Engine == start.Engine && v.Class == start.Class;
        }

        Repro cur = start;
        for (int round = 0; round < 3; round++)
        {
            bool progress = false;
            for (int chunk = Math.Max(1, cur.Setup.Count / 2); chunk >= 1; chunk /= 2)
            {
                for (int at = 0; at < cur.Setup.Count;)
                {
                    int len = Math.Min(chunk, cur.Setup.Count - at);
                    List<string> smaller = [.. cur.Setup.Take(at), .. cur.Setup.Skip(at + len)];
                    Repro cand = cur.With(smaller, cur.Check);
                    if (Fails(cand))
                    {
                        cur = cand;
                        progress = true;
                    }
                    else
                    {
                        at += len;
                    }
                }

                if (chunk == 1)
                {
                    break;
                }
            }

            bool shrunk = true;
            while (shrunk && used < budget)
            {
                shrunk = false;
                foreach (Check c in cur.Check.Shrinks())
                {
                    Repro cand = cur.With(cur.Setup, c);
                    if (Fails(cand))
                    {
                        cur = cand;
                        shrunk = true;
                        progress = true;
                        break;
                    }

                    if (used >= budget)
                    {
                        break;
                    }
                }
            }

            if (!progress || used >= budget)
            {
                break;
            }
        }

        // Refresh the detail from the minimized repro.
        Verdict? last = Executor.Replay(cur, tmpDir);
        if (last is { Kind: VerdictKind.Fail })
        {
            cur.Detail = last.Detail;
        }

        return cur;
    }
}
