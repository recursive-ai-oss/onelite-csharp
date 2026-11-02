using System.Globalization;
using RecursiveAI.OneLite.Differential;
using RecursiveAI.OneLite.Oracle;

return Cli.Execute(args);

namespace RecursiveAI.OneLite.Differential
{
    internal static class Cli
    {
        private const string Usage = """
            onelite-differential: differential fuzzing of OneLite against the pinned sqlite3

              run     --seed S [--count CASES | --duration 30m] [--start-case K] [--families a,b] [--oracles diff,pqs,norec,tlp]
                      [--jobs N] [--out DIR] [--tmp DIR] [--minimize-budget N] [--minimize-limit N]
              gen     --seed S --case K [--families a,b] [--oracles ...]     print the statements of one case
              replay  DIR_OR_FILE...                                        replay repro files (default: testdata/differential)
              probe   [--db FILE] SQL...                                    run statements on OneLite and on the pinned sqlite3 side by side
              families                                                      list the family names
            """;

        internal static int Execute(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine(Usage);
                return 2;
            }

            List<string> rest = [.. args.Skip(1)];
            switch (args[0])
            {
                case "run": return Run(rest);
                case "gen": return Gen(rest);
                case "replay": return Replay(rest);
                case "probe": return Probe(rest);
                case "families":
                    Console.WriteLine(string.Join("\n", Fam.All));
                    return 0;
                default:
                    Console.WriteLine(Usage);
                    return 2;
            }
        }

        private static TimeSpan ParseDuration(string s)
        {
            string t = s.Trim();
            double n = double.Parse(t[..^1], CultureInfo.InvariantCulture);
            return t[^1] switch
            {
                's' => TimeSpan.FromSeconds(n),
                'm' => TimeSpan.FromMinutes(n),
                'h' => TimeSpan.FromHours(n),
                _ => throw new FormatException("duration must end in s, m or h: " + s),
            };
        }

        private static CampaignOptions Parse(List<string> a)
        {
            var o = new CampaignOptions();
            for (int i = 0; i < a.Count; i++)
            {
                switch (a[i])
                {
                    case "--seed": o.Seed = long.Parse(a[++i], CultureInfo.InvariantCulture); break;
                    case "--count": o.Count = long.Parse(a[++i], CultureInfo.InvariantCulture); break;
                    case "--duration": o.Duration = ParseDuration(a[++i]); break;
                    case "--start-case": o.StartCase = long.Parse(a[++i], CultureInfo.InvariantCulture); break;
                    case "--families": o.Filter = new Fam.Filter(a[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                    case "--oracles": o.Oracles = [.. a[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]; break;
                    case "--setup": o.SetupOneLite = a[++i] == "onelite"; break;
                    case "--jobs": o.Jobs = int.Parse(a[++i], CultureInfo.InvariantCulture); break;
                    case "--out": o.OutDir = a[++i]; break;
                    case "--tmp": o.TmpDir = a[++i]; break;
                    case "--minimize-budget": o.MinimizeBudget = int.Parse(a[++i], CultureInfo.InvariantCulture); break;
                    case "--minimize-limit": o.MinimizeLimit = int.Parse(a[++i], CultureInfo.InvariantCulture); break;
                    case "--case": o.StartCase = long.Parse(a[++i], CultureInfo.InvariantCulture); break;
                    default: throw new ArgumentException("unknown option " + a[i]);
                }
            }

            foreach (string x in o.Oracles)
            {
                if (x is not ("diff" or "pqs" or "norec" or "tlp"))
                {
                    throw new ArgumentException("unknown oracle " + x);
                }
            }

            return o;
        }

        private static int Run(List<string> a)
        {
            if (!Corpus.OraclesAvailable)
            {
                Console.Error.WriteLine($"the pinned sqlite3 ({OraclePaths.Cli}) or its library is not installed");
                return 2;
            }

            return new Campaign(Parse(a)).Run();
        }

        private static int Gen(List<string> a)
        {
            CampaignOptions o = Parse(a);
            Directory.CreateDirectory(o.TmpDir);
            string path = Path.Combine(o.TmpDir, $"gen-{o.Seed}-{o.StartCase}.db");
            Executor.DeleteFiles(path);
            try
            {
                CaseGen.Plan plan = CaseGen.Generate(o.Seed, o.StartCase, o.Filter, o.Oracles, path);
                foreach (string s in plan.Setup.Statements)
                {
                    Console.WriteLine(s + ";");
                }

                Console.WriteLine("-- checks");
                foreach (Check c in plan.Checks)
                {
                    Console.WriteLine($"-- {c.Oracle} [{string.Join(",", c.Families.OrderBy(x => x, StringComparer.Ordinal))}]");
                    foreach ((string k, string v) in c.Fields().Where(f => f.Key != "oracle"))
                    {
                        Console.WriteLine($"--   {k}: {v}");
                    }
                }
            }
            finally
            {
                Executor.DeleteFiles(path);
            }

            return 0;
        }

        private static int Probe(List<string> a)
        {
            string? db = null;
            var sqls = new List<string>();
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] == "--db")
                {
                    db = a[++i];
                }
                else
                {
                    sqls.Add(a[i]);
                }
            }

            string path = db ?? Path.Combine(Path.GetTempPath(), "onelite-probe-" + Guid.NewGuid().ToString("N") + ".db");
            using (SqliteDb d = SqliteDb.Open(path))
            {
                d.Exec("PRAGMA user_version=0");
            }

            try
            {
                var checks = sqls.Select(q => (Check)new DiffCheck(q, null, true)).ToList();
                Verdict[] v = Executor.Run(path, checks);
                string[] queries = [.. sqls];
                RecursiveAI.OneLite.QueryDiff.Outcome[] theirs = RecursiveAI.OneLite.QueryDiff.ShellRunner.RunBatch(path, queries);
                for (int i = 0; i < queries.Length; i++)
                {
                    Console.WriteLine($"{queries[i]}\n  verdict: {v[i].Kind} {v[i].Class} {v[i].Detail}\n  sqlite3: {Show(theirs[i])}");
                }
            }
            finally
            {
                if (db is null)
                {
                    Executor.DeleteFiles(path);
                }
            }

            return 0;
        }

        private static string Show(RecursiveAI.OneLite.QueryDiff.Outcome o)
            => o.HasError ? o.ErrorText : string.Join(" | ", o.Rows.Select(r => string.Join(", ", r.Select(c => c.Show))));

        private static int Replay(List<string> a)
        {
            if (!Corpus.OraclesAvailable)
            {
                Console.Error.WriteLine("the pinned sqlite3 or its library is not installed");
                return 2;
            }

            string tmp = Path.Combine(Path.GetTempPath(), "onelite-differential-replay");
            int bad = 0, n = 0;
            foreach (string p in a.Count > 0 ? a : ["testdata/differential"])
            {
                string dir = File.Exists(p) ? Path.GetDirectoryName(Path.GetFullPath(p))! : p;
                foreach (Corpus.Result r in Corpus.Replay(dir, tmp).Where(r => !File.Exists(p) || Path.GetFileName(p) == Path.GetFileName(r.File)))
                {
                    n++;
                    if (!r.Ok)
                    {
                        bad++;
                    }

                    Console.WriteLine($"{(r.Ok ? "ok  " : "FAIL")} {r.File}: {r.Message}");
                }
            }

            Console.WriteLine($"{n} repros, {bad} not as recorded");
            return bad == 0 ? 0 : 1;
        }
    }
}
