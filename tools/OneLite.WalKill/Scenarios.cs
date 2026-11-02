using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;
using RecursiveAI.OneLite.Storage.Wal;
using PagerClass = RecursiveAI.OneLite.Storage.Pager.Pager;

internal static partial class Program
{
    // Run through `dotnet x.dll` the process path is the host; the apphost next to the dll is the program itself.
    private static string Self
    {
        get
        {
            string host = Path.Combine(AppContext.BaseDirectory, "OneLite.WalKill");
            return File.Exists(host) ? host : Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        }
    }

    private sealed class Child : IDisposable
    {
        private readonly Process _p;
        public readonly ConcurrentQueue<string> Lines = new();

        public Child(string file, IEnumerable<string> args, string? stdin = null)
        {
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin != null, UseShellExecute = false };
            foreach (string a in args)
            {
                psi.ArgumentList.Add(a);
            }

            _p = Process.Start(psi)!;
            _p.OutputDataReceived += (_, e) => { if (e.Data != null) { Lines.Enqueue(e.Data); } };
            _p.ErrorDataReceived += (_, e) => { if (e.Data != null) { Lines.Enqueue("stderr: " + e.Data); } };
            _p.BeginOutputReadLine();
            _p.BeginErrorReadLine();
            if (stdin != null)
            {
                Task.Run(() =>
                {
                    try
                    {
                        _p.StandardInput.Write(stdin);
                        _p.StandardInput.Close();
                    }
                    catch (IOException)
                    {
                        // the child went away
                    }
                });
            }
        }

        public bool HasExited => _p.HasExited;

        public int Pid => _p.Id;

        public bool WaitForExit(int ms) => _p.WaitForExit(ms);

        public void Kill()
        {
            try
            {
                _p.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }

            _p.WaitForExit();
        }

        public bool WaitReady(int ms = 20000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                if (Lines.Any(l => l == "READY"))
                {
                    return true;
                }

                if (_p.HasExited)
                {
                    return false;
                }

                Thread.Sleep(2);
            }

            return false;
        }

        public void Dispose()
        {
            Kill();
            _p.Dispose();
        }
    }

    private static string Repeat(string line, int n) => string.Join('\n', Enumerable.Repeat(line, n));

    // ---- live coexistence ----------------------------------------------------------------------------------------------------------

    private static int Live(Dictionary<string, string> o)
    {
        string dir = o["dir"];
        string scenario = o["scenario"];
        int commits = Int(o, "commits", 10000);
        int ps = Int(o, "ps", 1024);
        int rows = Int(o, "rows", 40);
        string sync = Str(o, "sync", "full");
        Directory.CreateDirectory(dir);
        string db = Path.Combine(dir, scenario + ".db");
        string stop = Path.Combine(dir, scenario + ".stop");
        File.Delete(stop);
        MakeDb(db, ps, rows);
        long start = 0;
        var sw = Stopwatch.StartNew();
        var readers = new List<(string Kind, Child C)>();
        var writers = new List<(string Kind, Child C)>();
        string[] common = ["--db", db, "--ps", ps.ToString(CultureInfo.InvariantCulture), "--cache", "40"];
        string ShellReadScript(int n) => ".timeout 20000\n" + Repeat("select count(*), count(distinct v), min(v), max(v) from c;", n);

        void StartOursReaders(int n)
        {
            for (int i = 0; i < n; i++)
            {
                readers.Add(("ours", new Child(Self, ["reader", .. common, "--stop", stop, "--label", "ours" + i, "--hold-ms", "20"])));
            }
        }

        // The shell readers run this script over and over until the stop file appears, so they overlap every commit of a long run.
        string readScript = Path.Combine(dir, "reader.sql");
        File.WriteAllText(readScript, ShellReadScript(20_000));

        void StartShellReaders(int n)
        {
            for (int i = 0; i < n; i++)
            {
                readers.Add(("shell", new Child("bash", ["-c", $"while [ ! -e '{stop}' ]; do '{Sqlite3}' '{db}' < '{readScript}'; done"])));
            }
        }

        void StartOursWriter(int n) => writers.Add(("ours", new Child(Self, ["writer", .. common, "--sync", sync, "--autockpt", "200", "--max", n.ToString(CultureInfo.InvariantCulture), "--ckpt-every", "0"])));
        void StartShellWriter(int n) => writers.Add(("shell", new Child(Sqlite3, [db, "-cmd", $"pragma synchronous={(sync == "full" ? 2 : 1)}; pragma secure_delete=1;"], ".timeout 20000\n" + Repeat(UpdateSql, n))));

        long oursCommits = 0;
        long shellCommits = 0;
        switch (scenario)
        {
            case "ours-write":
                StartOursReaders(1);
                StartShellReaders(2);
                StartOursWriter(commits);
                oursCommits = commits;
                break;
            case "shell-write":
                StartOursReaders(2);
                StartShellReaders(1);
                StartShellWriter(commits);
                shellCommits = commits;
                break;
            case "mixed":
                StartOursReaders(1);
                StartShellReaders(1);
                StartOursWriter(commits / 2);
                StartShellWriter(commits - commits / 2);
                oursCommits = commits / 2;
                shellCommits = commits - commits / 2;
                break;
            case "alternate":
                return Alternate(db, ps, commits, sync, rows);
            default:
                throw new InvalidOperationException("unknown scenario " + scenario);
        }

        foreach ((string _, Child c) in readers.Where(r => r.Kind == "ours"))
        {
            if (!c.WaitReady())
            {
                throw new InvalidOperationException("a reader did not start: " + string.Join(" | ", c.Lines));
            }
        }

        // The writers run to completion; the readers read throughout.
        foreach ((string kind, Child c) in writers)
        {
            if (!c.WaitForExit(1_800_000))
            {
                throw new InvalidOperationException(kind + " writer did not finish");
            }
        }

        File.WriteAllText(stop, "stop");
        Thread.Sleep(300);
        foreach ((string kind, Child c) in readers)
        {
            if (kind == "shell")
            {
                c.Kill();
            }
            else if (!c.WaitForExit(30000))
            {
                c.Kill();
            }
        }

        // ---- verdicts --------------------------------------------------------------------------------------------------------------
        int problems = 0;
        long shellErrors = 0;
        long shellBusyAtWriter = 0;
        foreach ((string kind, Child c) in writers)
        {
            string[] lines = [.. c.Lines];
            if (kind == "shell")
            {
                shellBusyAtWriter = lines.Count(l => l.Contains("locked", StringComparison.OrdinalIgnoreCase));
                shellErrors = lines.Count(l => l.Contains("Error", StringComparison.OrdinalIgnoreCase));
                shellCommits -= shellErrors;
                foreach (string l in lines.Where(l => l != "1" && !l.Contains("locked", StringComparison.OrdinalIgnoreCase)).Take(5))
                {
                    Console.WriteLine("shell writer says: " + l);
                }
            }
            else
            {
                Console.WriteLine(lines.LastOrDefault(l => l.StartsWith("WRITER", StringComparison.Ordinal)) ?? "WRITER ?");
                foreach (string l in lines.Where(l => l.StartsWith("stderr", StringComparison.Ordinal)))
                {
                    Console.WriteLine(l);
                    problems++;
                }
            }
        }

        long shellSnaps = 0;
        long shellBusyAtReader = 0;
        long lastShell = -1;
        foreach ((string kind, Child c) in readers)
        {
            string[] lines = [.. c.Lines];
            if (kind == "ours")
            {
                string summary = lines.LastOrDefault(l => l.StartsWith("READER", StringComparison.Ordinal)) ?? "READER ? (no summary)";
                Console.WriteLine(summary);
                if (summary.Contains("(no summary)", StringComparison.Ordinal) || !summary.Contains("anomalies=0", StringComparison.Ordinal))
                {
                    problems++;
                    foreach (string l in lines.TakeLast(3))
                    {
                        Console.WriteLine("  reader output: " + l);
                    }

                    foreach (string l in lines.Where(l => l.StartsWith("ANOMALY", StringComparison.Ordinal)).Take(5))
                    {
                        Console.WriteLine(l);
                    }
                }
            }
            else
            {
                long last = -1;
                long snaps = 0;
                long anomalies = 0;
                foreach (string l in lines.Take(lines.Length - 1))
                {
                    string[] f = l.Split('|');
                    if (f.Length == 4 && f[3].StartsWith('V'))
                    {
                        long mx = long.Parse(f[3][1..], CultureInfo.InvariantCulture);
                        snaps++;
                        if (f[0] != rows.ToString(CultureInfo.InvariantCulture) || f[1] != "1" || f[2] != f[3] || mx < last)
                        {
                            anomalies++;
                            if (anomalies < 5)
                            {
                                Console.WriteLine("ANOMALY shell reader: " + l + " after " + last);
                            }
                        }

                        last = Math.Max(last, mx);
                    }
                    else if (l.Contains("locked", StringComparison.OrdinalIgnoreCase))
                    {
                        shellBusyAtReader++;
                    }
                    else
                    {
                        anomalies++;
                        Console.WriteLine("shell reader says: " + l);
                    }
                }

                shellSnaps += snaps;
                lastShell = Math.Max(lastShell, last);
                Console.WriteLine($"READER shell snapshots={snaps} last={last} anomalies={anomalies}");
                problems += anomalies > 0 ? 1 : 0;
            }
        }

        // Equal state on both sides: our reading of the file and the shell's, and the number of commits that went in.
        (int exit, string output) = Sql(db, "pragma integrity_check; select count(*), count(distinct v), min(v) from c;");
        string[] fin = output.Split('\n');
        long finalValue = fin.Length > 1 && fin[1].Split('|') is [_, "1", var m] ? long.Parse(m[1..], CultureInfo.InvariantCulture) : -1;
        long expect = start + oursCommits + shellCommits;
        long ours;
        using (PagerClass p = PagerClass.Open(new FileVfs(), db, Opts("full", 100, 0, ps)))
        {
            p.BeginReadSync();
            (int count, long min, long max) = Scan(p);
            p.EndReadSync();
            ours = min == max && count == rows ? min : -1;
        }

        Console.WriteLine($"FINAL scenario={scenario} commits={commits} expected={expect} shell_sees={finalValue} we_see={ours} integrity={(fin[0] == "ok" ? "ok" : fin[0])} shell_busy_at_writer={shellBusyAtWriter} shell_busy_at_reader={shellBusyAtReader} shell_reader_snapshots={shellSnaps} seconds={sw.Elapsed.TotalSeconds:F1}");
        if (exit != 0 || fin[0] != "ok" || finalValue != expect || ours != expect || shellBusyAtReader > 0)
        {
            problems++;
        }

        foreach ((string _, Child c) in writers.Concat(readers))
        {
            c.Dispose();
        }

        Console.WriteLine(problems == 0 ? "PASS" : "FAIL");
        return problems == 0 ? 0 : 1;
    }

    // Strictly alternating writers on one live file: a long-lived shell and our connection in this process take turns, each checking the other's commit.
    private static int Alternate(string db, int ps, int commits, string sync, int rows)
    {
        var sw = Stopwatch.StartNew();
        using PagerClass p = PagerClass.Open(new FileVfs(), db, Opts(sync, 100, 1000, ps));
        var psi = new ProcessStartInfo(Sqlite3) { RedirectStandardOutput = true, RedirectStandardInput = true, UseShellExecute = false };
        psi.ArgumentList.Add(db);
        psi.ArgumentList.Add("-cmd");
        psi.ArgumentList.Add("pragma secure_delete=1;");
        using Process sh = Process.Start(psi)!;
        void Shell(string sql)
        {
            sh.StandardInput.WriteLine(sql);
            sh.StandardInput.WriteLine(".print @@");
            sh.StandardInput.Flush();
        }

        string ReadUntilMarker()
        {
            var sb = new StringBuilder();
            string? l;
            while ((l = sh.StandardOutput.ReadLine()) != null && l != "@@")
            {
                sb.AppendLine(l);
            }

            return sb.ToString().Trim();
        }

        Shell(".timeout 20000");
        ReadUntilMarker();
        long expect = 0;
        int problems = 0;
        for (int i = 1; i <= commits; i++)
        {
            if (i % 2 == 1)
            {
                p.BeginReadSync();
                if (Drive(p, p.BeginWrite) != PagerStatus.Ok)
                {
                    throw p.Error!;
                }

                (int c0, long mn0, long mx0) = Scan(p);
                if (c0 != rows || mn0 != mx0 || mx0 != expect)
                {
                    problems++;
                    Console.WriteLine($"ANOMALY ours before commit {i}: {c0}/{mn0}/{mx0} expected {expect}");
                }

                SetAll(p, ++expect);
                p.CommitSync();
                p.EndReadSync();
            }
            else
            {
                Shell(UpdateSql + " select count(*), count(distinct v), min(v) from c;");
                string r = ReadUntilMarker();
                expect++;
                if (r != $"{rows}|1|V{expect:D8}")
                {
                    problems++;
                    Console.WriteLine($"ANOMALY shell after commit {i}: '{r}' expected V{expect:D8}");
                }
            }

            if (i % 2 == 1 && i % 50 == 1)
            {
                Shell("select count(*), count(distinct v), min(v) from c;");
                string r = ReadUntilMarker();
                if (r != $"{rows}|1|V{expect:D8}")
                {
                    problems++;
                    Console.WriteLine($"ANOMALY shell read after our commit {i}: '{r}' expected V{expect:D8}");
                }
            }

            if (problems > 5)
            {
                break;
            }
        }

        Shell("pragma integrity_check;");
        string integ = ReadUntilMarker();
        Console.WriteLine($"FINAL scenario=alternate commits={commits} expected={expect} integrity={integ} seconds={sw.Elapsed.TotalSeconds:F1}");
        sh.StandardInput.WriteLine(".quit");
        sh.StandardInput.Close();
        sh.WaitForExit(10000);
        problems += integ == "ok" ? 0 : 1;
        Console.WriteLine(problems == 0 ? "PASS" : "FAIL");
        return problems == 0 ? 0 : 1;
    }

    // ---- kills ---------------------------------------------------------------------------------------------------------------------

    private static int Kill(Dictionary<string, string> o)
    {
        string dir = o["dir"];
        int kills = Int(o, "kills", 100);
        int ps = Int(o, "ps", 1024);
        int rows = Int(o, "rows", 40);
        int cache = Int(o, "cache", 12);
        int autockpt = Int(o, "autockpt", 8);
        int maxDelay = Int(o, "max-delay-ms", 60);
        string sync = Str(o, "sync", "full");
        int seed = Int(o, "seed", 1);
        var rnd = new Random(seed);
        Directory.CreateDirectory(dir);
        string db = Path.Combine(dir, "k.db");
        string ledger = Path.Combine(dir, "k.ledger");
        File.Delete(ledger);
        MakeDb(db, ps, rows);
        long acknowledged = 0;
        int failures = 0;
        int[] kindCounts = new int[3];
        long maxValue = 0;
        var sw = Stopwatch.StartNew();
        string work = Path.Combine(dir, "verify");
        for (int k = 1; k <= kills; k++)
        {
            using (var w = new Child(Self, ["writer", "--db", db, "--ledger", ledger, "--sync", sync, "--autockpt", autockpt.ToString(CultureInfo.InvariantCulture), "--cache", cache.ToString(CultureInfo.InvariantCulture),
                "--ps", ps.ToString(CultureInfo.InvariantCulture), "--seed", rnd.Next().ToString(CultureInfo.InvariantCulture), "--ckpt-every", "5"]))
            {
                if (!w.WaitReady())
                {
                    Console.WriteLine($"kill {k}: the writer did not start: {string.Join(" | ", w.Lines)}");
                    failures++;
                    break;
                }

                // Mostly short delays so kills land in commits and checkpoints; now and then a longer run.
                int delay = rnd.Next(10) == 0 ? rnd.Next(maxDelay * 4) : rnd.Next(maxDelay);
                Thread.Sleep(delay);
                w.Kill();
            }

            long last = File.Exists(ledger) ? File.ReadLines(ledger).LastOrDefault() is { } s && long.TryParse(s, out long l) ? l : 0 : 0;
            int kind = k % 3;
            kindCounts[kind]++;
            // A state an earlier verifier saw must never go away, even if its writer was killed before it wrote the ledger line.
            string verdict = Verify(db, work, rows, ps, kind, Math.Max(last, maxValue), out long value);
            maxValue = Math.Max(maxValue, value);
            acknowledged = last;
            if (verdict != "ok")
            {
                failures++;
                Console.WriteLine($"kill {k}: {verdict} (ledger {last}, value {value}, verifier {kind})");
                if (failures > 5)
                {
                    break;
                }
            }

            if (k % 50 == 0)
            {
                Console.WriteLine($"kills={k} ledger={last} value={value} failures={failures} elapsed={sw.Elapsed.TotalSeconds:F0}s");
            }
        }

        Console.WriteLine($"KILLS sync={sync} done={kindCounts.Sum()} failures={failures} commits_acknowledged={acknowledged} max_value={maxValue} verifiers(copy+shell, real+shell, ours-real)={string.Join("/", kindCounts)} seconds={sw.Elapsed.TotalSeconds:F0}");
        return failures == 0 ? 0 : 1;
    }

    // After a kill: no lost acknowledged commit (the value is the ledger's last line or one more), all rows alike, integrity ok.
    private static string Verify(string db, string work, int rows, int ps, int kind, long ledger, out long value)
    {
        value = -1;
        string check = "pragma integrity_check; select count(*), count(distinct v), min(v) from c;";
        if (kind == 0)
        {
            // On copies of the files without the -shm: the shell recovers one copy, we recover another; the images must agree.
            if (Directory.Exists(work))
            {
                Directory.Delete(work, true);
            }

            foreach (string d in new[] { "sh", "us" })
            {
                Directory.CreateDirectory(Path.Combine(work, d));
                foreach (string sfx in new[] { "", "-wal" })
                {
                    if (File.Exists(db + sfx))
                    {
                        File.Copy(db + sfx, Path.Combine(work, d, "k.db" + sfx));
                    }
                }
            }

            (int exit, string outp) = Sql(Path.Combine(work, "sh", "k.db"), check + " pragma wal_checkpoint(truncate);");
            string[] r = outp.Split('\n');
            string v = ParseValue(r, rows, out value);
            if (exit != 0 || v != "ok")
            {
                return "the shell: " + (v == "ok" ? outp : v);
            }

            using (PagerClass p = PagerClass.Open(new FileVfs(), Path.Combine(work, "us", "k.db"), Opts("full", 100, 0, ps)))
            {
                p.BeginReadSync();
                (int count, long min, long max) = Scan(p);
                uint pages = p.PageCount;
                var ours = new List<byte[]>();
                for (uint n = 1; n <= pages; n++)
                {
                    PageFrame f = p.GetSync(n);
                    ours.Add((byte[])f.Data.Clone());
                    p.Release(f);
                }

                p.EndReadSync();
                if (count != rows || min != max || min != value)
                {
                    return $"we read {count}/{min}/{max}, the shell {value}";
                }

                byte[] theirs = File.ReadAllBytes(Path.Combine(work, "sh", "k.db"));
                if (theirs.Length != (long)pages * ps)
                {
                    return $"the shell's database has {theirs.Length / ps} pages, ours {pages}";
                }

                for (int i = 0; i < ours.Count; i++)
                {
                    // Page 1 is excluded from the byte comparison: the shell may have bumped counters while checkpointing.
                    if (i > 0 && !ours[i].AsSpan().SequenceEqual(theirs.AsSpan(i * ps, ps)))
                    {
                        return $"page {i + 1} differs between our recovery and the shell's";
                    }
                }
            }
        }
        else if (kind == 1)
        {
            // The shell on the real files: it recovers, reads, and on its close checkpoints and removes the log.
            (int exit, string outp) = Sql(db, check);
            string v = ParseValue(outp.Split('\n'), rows, out value);
            if (exit != 0 || v != "ok")
            {
                return "the shell: " + (v == "ok" ? outp : v);
            }
        }
        else
        {
            using PagerClass p = PagerClass.Open(new FileVfs(), db, Opts("full", 100, 0, ps));
            p.BeginReadSync();
            (int count, long min, long max) = Scan(p);
            p.EndReadSync();
            if (count != rows || min != max)
            {
                return $"we read {count}/{min}/{max}";
            }

            value = min;
        }

        if (value < ledger || value > ledger + 1)
        {
            return $"value {value} is not the ledger's {ledger} or one more (a lost or invented commit)";
        }

        return "ok";
    }

    private static string ParseValue(string[] lines, int rows, out long value)
    {
        value = -1;
        if (lines.Length < 2 || lines[0] != "ok")
        {
            return lines.Length > 0 ? lines[0] : "no output";
        }

        string[] f = lines[1].Split('|');
        if (f.Length != 3 || f[0] != rows.ToString(CultureInfo.InvariantCulture) || f[1] != "1")
        {
            return "rows are not alike: " + lines[1];
        }

        value = long.Parse(f[2][1..], CultureInfo.InvariantCulture);
        return "ok";
    }
}
