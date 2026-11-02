using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.RTreeDiff;

/// <summary>
/// Runs seeded random r-tree scripts on OneLite and on the pinned shell, statement batch by batch, and compares rows and error text. At the end of each
/// case the file OneLite wrote must pass the shell's integrity_check and rtreecheck, and OneLite must read the shell's file the same way.
/// Usage: RTreeDiff --seed N --minutes M [--cases K] [--out DIR]
/// </summary>
internal static partial class Program
{
    [GeneratedRegex(@"^(?:Parse e|E|Runtime e)rror in \S+ command line argument: (.*)$", RegexOptions.Multiline)]
    private static partial Regex ShellError();

    private static readonly string[] WildCoords = ["'7'", "NULL", "'abc'", "-0.0", "1e300", "3.7", "x'41'"];
    private static readonly string[] AuxValues = ["'p'", "NULL", "5", "2.5", "x'4142'", "'long text value'"];
    private static readonly int[] PageSizes = [512, 1024, 4096];

    private static int Main(string[] args)
    {
        int seed = 1, minutes = 1, cases = int.MaxValue, from = 0;
        string outDir = Path.Combine(Path.GetTempPath(), "rtreediff");
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--seed": seed = int.Parse(args[i + 1]); break;
                case "--minutes": minutes = int.Parse(args[i + 1]); break;
                case "--cases": cases = int.Parse(args[i + 1]); break;
                case "--from": from = int.Parse(args[i + 1]); break;
                case "--out": outDir = args[i + 1]; break;
            }
        }

        Directory.CreateDirectory(outDir);
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--replay")
            {
                return Replay(File.ReadAllLines(args[i + 1]), args.Length > i + 3 && args[i + 2] == "--upto" ? int.Parse(args[i + 3]) : int.MaxValue, outDir);
            }
        }

        var clock = Stopwatch.StartNew();
        int done = 0, failed = 0;
        long statements = 0;
        for (int c = from; c < from + cases && clock.Elapsed.TotalMinutes < minutes; c++, done++)
        {
            var rnd = new Random(HashCode.Combine(seed, c));
            List<string> script = Generate(rnd, out int pageSize);
            string a = Path.Combine(outDir, $"s{seed}-c{c}-shell.db"), b = Path.Combine(outDir, $"s{seed}-c{c}-ours.db");
            foreach (string f in (string[])[a, b, a + "-journal", b + "-journal"])
            {
                File.Delete(f);
            }

            string? problem;
            try
            {
                problem = RunCase(script, pageSize, a, b, ref statements);
            }
            catch (InvalidOperationException e)
            {
                problem = e.Message + " :: " + e.InnerException?.GetType().Name + " " + e.InnerException?.Message;
            }

            if (problem is null)
            {
                File.Delete(a);
                File.Delete(b);
                continue;
            }

            failed++;
            File.WriteAllText(Path.Combine(outDir, $"s{seed}-c{c}.sql"), $"-- page_size {pageSize}\n" + string.Join("\n", script) + "\n");
            Console.WriteLine($"case {c}: {problem}");
        }

        Console.WriteLine($"seed {seed}: {done} cases, {statements} statements, {failed} failed, {clock.Elapsed.TotalMinutes:F1} min");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Runs every statement of the batch (the shell goes on after an error); rows first, then the error messages, as <see cref="Shell"/> prints them.</summary>
    private static string Run(Connection conn, string batch)
    {
        var o = new StringBuilder();
        var errors = new StringBuilder();
        string rest = batch;
        while (!string.IsNullOrWhiteSpace(rest))
        {
            bool prepared = false;
            try
            {
                using Statement st = conn.Prepare(rest);
                prepared = true;
                rest = st.Tail;
                while (st.Step())
                {
                    for (int i = 0; i < st.ColumnCount; i++)
                    {
                        o.Append(i > 0 ? "|" : "").Append(st.IsNull(i) ? string.Empty : st.GetString(i));
                    }

                    o.Append('\n');
                }
            }
            catch (Exception e) when (e is not OneLiteException)
            {
                throw new InvalidOperationException("crash on: " + batch, e);
            }
            catch (OneLiteException e)
            {
                errors.Append("Error: ").Append(e.Message).Append('\n');
                if (!prepared)
                {
                    int semi = rest.IndexOf(';', StringComparison.Ordinal);
                    rest = semi < 0 ? string.Empty : rest[(semi + 1)..];
                }
            }
        }

        if (!conn.AutoCommit)
        {
            using Statement rb = conn.Prepare("ROLLBACK");
            rb.Step();
        }

        return o.ToString() + errors;
    }

    private static string Shell(string db, string batch)
    {
        Proc.Result r = Proc.Run(OraclePaths.Cli, [db, batch]);
        var errs = new StringBuilder();
        foreach (string line in r.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            errs.Append("Error: ").Append(ShellError().Match(line) is { Success: true } m ? m.Groups[1].Value : line).Append('\n');
        }

        return r.Stdout + errs;
    }

    private static string? RunCase(List<string> script, int pageSize, string shellDb, string oursDb, ref long statements, bool finalChecks = true)
    {
        using var db = Database.Open(oursDb, new DatabaseOptions { PageSize = pageSize });
        using Connection conn = db.Connect();
        Shell(shellDb, $"PRAGMA page_size={pageSize};");
        for (int i = 0; i < script.Count; i++)
        {
            string batch = script[i];
            statements++;
            string expect = Shell(shellDb, batch);
            string got = Run(conn, batch);
            if (expect != got)
            {
                // A scan returns rows in no promised order; equal as sets is equal.
                if (!(expect.Split('\n').Order().SequenceEqual(got.Split('\n').Order()) && batch.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) && !batch.Contains("ORDER BY", StringComparison.Ordinal)))
                {
                    string[] el = expect.Split('\n'), gl = got.Split('\n');
                    int at = 0;
                    while (at < el.Length && at < gl.Length && el[at] == gl[at])
                    {
                        at++;
                    }

                    return $"batch {i}: {Trunc(batch)}\n  first difference at line {at}\n  shell: {(at < el.Length ? Trunc(el[at]) : "<end>")}\n  ours:  {(at < gl.Length ? Trunc(gl[at]) : "<end>")}";
                }
            }
        }

        conn.Dispose();
        db.Dispose();
        if (!finalChecks)
        {
            return null;
        }

        string check = Shell(oursDb, "PRAGMA integrity_check; SELECT rtreecheck('t');");
        if (check != "ok\nok\n")
        {
            return "shell rejects our file: " + Trunc(check);
        }

        // The shell's own file, read and then changed by OneLite, must still pass its check.
        using var db2 = Database.Open(shellDb);
        using Connection c2 = db2.Connect();
        string full = "SELECT * FROM t ORDER BY id";
        if (Shell(shellDb, full) != Run(c2, full))
        {
            return "OneLite reads the shell's file differently";
        }

        Run(c2, "DELETE FROM t WHERE id % 2 = 0; INSERT INTO t SELECT id + 100000, x0, x1, y0, y1 FROM t LIMIT 0");
        c2.Dispose();
        db2.Dispose();
        string check2 = Shell(shellDb, "PRAGMA integrity_check; SELECT rtreecheck('t');");
        return check2 == "ok\nok\n" ? null : "shell's file fails after OneLite changed it: " + Trunc(check2);
    }

    private static int Replay(string[] script, int upto, string outDir)
    {
        string a = Path.Combine(outDir, "replay-shell.db"), b = Path.Combine(outDir, "replay-ours.db");
        foreach (string f in new[] { a, b })
        {
            File.Delete(f);
        }

        long n = 0;
        int ps = script[0].StartsWith("-- page_size ", StringComparison.Ordinal) ? int.Parse(script[0][13..]) : 4096;
        script = [.. script.Where(l => !l.StartsWith("--", StringComparison.Ordinal))];
        string? problem = RunCase([.. script.Take(upto)], ps, a, b, ref n, false);
        Console.WriteLine(problem ?? "same");
        using var db = Database.Open(b);
        using Connection c = db.Connect();
        Console.WriteLine("shell:\n" + Shell(a, "SELECT * FROM t ORDER BY id"));
        Console.WriteLine("ours:\n" + Run(c, "SELECT * FROM t ORDER BY id"));
        return 0;
    }

    private static string Trunc(string s) => s.Length > 300 ? s[..300] + "..." : s;

    private static List<string> Generate(Random r, out int pageSize)
    {
        int dims = r.Next(1, 6);
        bool i32 = r.Next(4) == 0;
        int aux = r.Next(3) == 0 ? r.Next(1, 3) : 0;
        pageSize = PageSizes[r.Next(PageSizes.Length)];
        var cols = new List<string> { "id" };
        for (int d = 0; d < dims; d++)
        {
            cols.Add("a" + d);
            cols.Add("b" + d);
        }

        for (int k = 0; k < aux; k++)
        {
            cols.Add("+x" + k);
        }

        var s = new List<string> { $"CREATE VIRTUAL TABLE t USING {(i32 ? "rtree_i32" : "rtree")}({string.Join(",", cols)});" };
        string[] names = [.. cols.Select(c => c.TrimStart('+'))];
        int ops = r.Next(40, 400);
        long nextId = 1;
        string Coord(bool wild) => wild && r.Next(8) == 0
            ? WildCoords[r.Next(WildCoords.Length)]
            : (r.Next(3) == 0 ? (r.NextDouble() * 1000).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : r.Next(0, 1000).ToString(System.Globalization.CultureInfo.InvariantCulture));
        string Row(string id)
        {
            var v = new List<string> { id };
            for (int d = 0; d < dims; d++)
            {
                string lo = Coord(true), hi = Coord(true);
                if (r.Next(10) != 0 && double.TryParse(lo, out double l) && double.TryParse(hi, out double h) && l > h)
                {
                    (lo, hi) = (hi, lo);
                }

                v.Add(lo);
                v.Add(hi);
            }

            for (int k = 0; k < aux; k++)
            {
                v.Add(AuxValues[r.Next(AuxValues.Length)]);
            }

            return "(" + string.Join(",", v) + ")";
        }

        string Range()
        {
            var terms = new List<string>();
            for (int d = 0; d < dims; d++)
            {
                if (r.Next(3) == 0)
                {
                    continue;
                }

                int lo = r.Next(0, 900), hi = lo + r.Next(0, 300);
                switch (r.Next(3))
                {
                    case 0: terms.Add($"a{d} <= {hi} AND b{d} >= {lo}"); break;
                    case 1: terms.Add($"a{d} >= {lo} AND b{d} <= {hi}"); break;
                    default: terms.Add($"b{d} > {lo}"); break;
                }
            }

            return terms.Count == 0 ? "1" : string.Join(" AND ", terms);
        }

        for (int i = 0; i < ops; i++)
        {
            switch (r.Next(14))
            {
                case 0:
                case 1:
                case 2:
                case 3:
                {
                    var rows = new List<string>();
                    for (int k = r.Next(1, 25); k > 0; k--)
                    {
                        rows.Add(Row(r.Next(30) == 0 ? "NULL" : r.Next(25) == 0 ? r.Next(1, (int)nextId + 1).ToString() : (nextId++).ToString()));
                    }

                    s.Add($"INSERT INTO t VALUES {string.Join(",", rows)};");
                    break;
                }

                case 4:
                    s.Add($"DELETE FROM t WHERE {Range()};");
                    break;
                case 5:
                    s.Add($"DELETE FROM t WHERE id = {r.Next(1, (int)nextId + 1)};");
                    break;
                case 6:
                    s.Add($"UPDATE t SET a0 = a0 + {r.Next(-5, 5)}, b0 = b0 + {r.Next(0, 9)} WHERE {Range()};");
                    break;
                case 7:
                    s.Add($"UPDATE t SET id = {r.Next(1, (int)nextId + 40)} WHERE id = {r.Next(1, (int)nextId + 1)};");
                    break;
                case 8:
                    s.Add(aux > 0 ? $"UPDATE t SET x0 = 'u{i}' WHERE {Range()};" : $"SELECT count(*) FROM t WHERE {Range()};");
                    break;
                case 9:
                    s.Add($"SELECT id FROM t WHERE {Range()} ORDER BY id;");
                    break;
                case 10:
                    s.Add($"SELECT count(*), sum(id), min({names[1]}), max({names[2]}) FROM t WHERE {Range()};");
                    break;
                case 11:
                    s.Add($"SELECT * FROM t WHERE id = {r.Next(1, (int)nextId + 1)};");
                    break;
                case 12:
                    s.Add($"SELECT t1.id, t2.id FROM t t1, t t2 WHERE t1.id < t2.id AND t1.b0 >= t2.a0 AND t1.a0 <= t2.b0 AND t1.id % 17 = {r.Next(17)} ORDER BY 1, 2 LIMIT 50;");
                    break;
                default:
                    s.Add($"BEGIN; INSERT INTO t VALUES {Row((nextId++).ToString())}; DELETE FROM t WHERE {Range()}; {(r.Next(2) == 0 ? "ROLLBACK" : "COMMIT")}; SELECT count(*) FROM t;");
                    break;
            }
        }

        s.Add("SELECT * FROM t ORDER BY id;");
        s.Add("SELECT rtreecheck('t');");
        return s;
    }
}
