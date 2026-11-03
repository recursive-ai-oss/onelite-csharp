using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using RecursiveAI.OneLite.History;

namespace RecursiveAI.OneLite.History
{
    /// <summary>Command line: <c>check</c> a history file, <c>run</c> a workload against a client and check it.</summary>
    internal static class Program
    {
        private const string Usage = """
            onelite-history: transactional history checker (Adya / Elle style)

            check <history.jsonl> [--level L] [--model list-append|register] [--json out.json]
                  [--no-realtime] [--realtime-versions] [--examples N]
            run   --client memory|sqlite|sqlite-snapshot|sqlite-novalidate|onelite|onelite-snapshot
                  [--lib <sqlite3 shared library>] [--db <file>] [--begin immediate|deferred] [--journal wal|delete] [--think-ms N]
                  [--model list-append|register] [--exec threads|processes] [--workers N] [--txns N-per-worker]
                  [--ops-max N] [--keys N] [--shift N] [--seed N]
                  [--out history.jsonl] [--level L] [--json out.json] [--no-realtime] [--realtime-versions]
            levels: read-committed, snapshot-isolation, serializable (default), strict-serializable
            exit code: 0 the history is valid at the level, 1 it is not, 2 usage or I/O error
            """;

        public static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0)
                {
                    Console.Error.WriteLine(Usage);
                    return 2;
                }

                var opts = Parse(args, 1);
                return args[0] switch
                {
                    "check" => Check(args, opts),
                    "run" => Run(opts),
                    "worker" => Worker(opts),
                    _ => Fail(Usage),
                };
            }
#pragma warning disable CA1031 // top-level handler turns any failure into an exit code
            catch (Exception e) when (e is IOException or HistoryFormatException or ArgumentException or InvalidOperationException or FormatException or KeyNotFoundException)
#pragma warning restore CA1031
            {
                Console.Error.WriteLine("error: " + e.Message);
                return 2;
            }
        }

        private static int Fail(string m)
        {
            Console.Error.WriteLine(m);
            return 2;
        }

        private static Dictionary<string, string> Parse(string[] args, int from)
        {
            var d = new Dictionary<string, string>();
            for (var i = from; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    d["_"] = args[i];
                    continue;
                }

                var name = args[i][2..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    d[name] = args[++i];
                }
                else
                {
                    d[name] = "true";
                }
            }

            return d;
        }

        private static int Int(Dictionary<string, string> o, string k, int def) => o.TryGetValue(k, out var s) ? int.Parse(s, CultureInfo.InvariantCulture) : def;

        private static IsolationLevel Level(Dictionary<string, string> o)
        {
            var s = o.GetValueOrDefault("level", "serializable");
            return Levels.TryParse(s, out var l) ? l : throw new ArgumentException("unknown level '" + s + "'");
        }

        private static CheckOptions CheckOpts(Dictionary<string, string> o) => new()
        {
            Realtime = !o.ContainsKey("no-realtime"),
            RealtimeVersions = o.ContainsKey("realtime-versions"),
            MaxExamples = Int(o, "examples", 8),
        };

        private static int Check(string[] args, Dictionary<string, string> o)
        {
            _ = args;
            var path = o.GetValueOrDefault("_") ?? throw new ArgumentException("check needs a history file");
            HistoryModel? model = null;
            if (o.TryGetValue("model", out var ms))
            {
                model = HistoryIO.TryParseModel(ms, out var m) ? m : throw new ArgumentException("unknown model '" + ms + "'");
            }

            var h = HistoryIO.Read(path, model);
            return Finish(h, o);
        }

        private static int Finish(History h, Dictionary<string, string> o)
        {
            var before = GC.GetTotalMemory(false);
            var sw = Stopwatch.StartNew();
            var r = Checker.Check(h, CheckOpts(o));
            sw.Stop();
            Console.Write(Report.Text(r));
            var proc = Process.GetCurrentProcess();
            Console.WriteLine("check wall time " + sw.ElapsedMilliseconds + " ms, peak working set " + (proc.PeakWorkingSet64 >> 20) + " MiB, managed heap before " + (before >> 20) + " MiB");
            if (o.TryGetValue("json", out var jp))
            {
                File.WriteAllText(jp, Report.Json(r));
            }

            var level = Level(o);
            if (level == IsolationLevel.StrictSerializable && !r.RealtimeChecked)
            {
                throw new ArgumentException("strict-serializable needs real-time checking; drop --no-realtime");
            }

            return r.IsValid(level) ? 0 : 1;
        }

        private static WorkloadOptions Workload(Dictionary<string, string> o)
        {
            var w = new WorkloadOptions
            {
                Workers = Int(o, "workers", 4),
                TxnsPerWorker = Int(o, "txns", 1000),
                MaxOps = Int(o, "ops-max", 4),
                ActiveKeys = Int(o, "keys", 4),
                ShiftEvery = Int(o, "shift", 5),
                Seed = Int(o, "seed", 1),
            };
            if (o.TryGetValue("model", out var ms))
            {
                w.Model = HistoryIO.TryParseModel(ms, out var m) ? m : throw new ArgumentException("unknown model '" + ms + "'");
            }

            return w;
        }

        private static IHistoryClientFactory Factory(Dictionary<string, string> o)
        {
            var client = o.GetValueOrDefault("client", "memory");
            if (client == "memory")
            {
                return new MemoryClientFactory();
            }

            if (client is "onelite" or "onelite-snapshot")
            {
                return new OneLiteClientFactory(o.GetValueOrDefault("db") ?? throw new ArgumentException("--db <file> is required"), client == "onelite-snapshot");
            }

            var kind = client switch
            {
                "sqlite" => SqliteClientKind.Strict,
                "sqlite-snapshot" => SqliteClientKind.SnapshotShim,
                "sqlite-novalidate" => SqliteClientKind.NoValidationShim,
                _ => throw new ArgumentException("unknown client '" + client + "'"),
            };
            var lib = o.GetValueOrDefault("lib") ?? throw new ArgumentException("--lib <sqlite3 shared library> is required");
            var db = o.GetValueOrDefault("db") ?? throw new ArgumentException("--db <file> is required");
            return new SqliteClientFactory(lib, db, o.GetValueOrDefault("begin", "immediate") == "immediate", o.GetValueOrDefault("journal", "wal"), kind, Int(o, "think-ms", 1));
        }

        private static int Run(Dictionary<string, string> o)
        {
            var w = Workload(o);
            var factory = Factory(o);
            if (factory is SqliteClientFactory)
            {
                Console.WriteLine("sqlite library version " + SqliteClientFactory.LibraryVersion());
            }

            var sw = Stopwatch.StartNew();
            History h;
            if (o.GetValueOrDefault("exec", "threads") == "processes")
            {
                h = RunProcesses(o, w, factory);
            }
            else
            {
                h = RecursiveAI.OneLite.History.Workload.RunThreads(factory, w);
            }

            sw.Stop();
            Console.WriteLine("ran " + w.Workers + " workers x " + w.TxnsPerWorker + " transactions in " + sw.ElapsedMilliseconds + " ms");
            if (o.TryGetValue("out", out var outPath))
            {
                HistoryIO.Write(outPath, h);
            }

            return Finish(h, o);
        }

        private static History RunProcesses(Dictionary<string, string> o, WorkloadOptions w, IHistoryClientFactory factory)
        {
            factory.Prepare();
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
            var prefix = new List<string>();
            if (Path.GetFileNameWithoutExtension(exe) == "dotnet")
            {
                prefix.Add(Assembly.GetExecutingAssembly().Location);
            }

            var dir = Path.Combine(Path.GetTempPath(), "onelite-history-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);
            var procs = new List<Process>();
            var startAt = RecursiveAI.OneLite.History.Workload.Now() + (3_000_000_000L + (w.Workers * 200_000_000L)); // let every process finish starting before any begins
            var shardFiles = new List<string>();
            try
            {
                for (var i = 0; i < w.Workers; i++)
                {
                    var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
                    foreach (var p in prefix)
                    {
                        psi.ArgumentList.Add(p);
                    }

                    psi.ArgumentList.Add("worker");
                    foreach (var (k, v) in o)
                    {
                        if (k is not ("_" or "exec" or "out" or "json"))
                        {
                            psi.ArgumentList.Add("--" + k);
                            psi.ArgumentList.Add(v);
                        }
                    }

                    var shard = Path.Combine(dir, "shard" + i.ToString(CultureInfo.InvariantCulture) + ".jsonl");
                    shardFiles.Add(shard);
                    psi.ArgumentList.Add("--start-at");
                    psi.ArgumentList.Add(startAt.ToString(CultureInfo.InvariantCulture));
                    psi.ArgumentList.Add("--index");
                    psi.ArgumentList.Add(i.ToString(CultureInfo.InvariantCulture));
                    psi.ArgumentList.Add("--shard");
                    psi.ArgumentList.Add(shard);
                    procs.Add(Process.Start(psi) ?? throw new InvalidOperationException("could not start a worker process"));
                }

                foreach (var p in procs)
                {
                    p.WaitForExit();
                    if (p.ExitCode != 0)
                    {
                        throw new InvalidOperationException("a worker process exited with code " + p.ExitCode);
                    }
                }

                var shards = new List<List<Txn>>();
                foreach (var f in shardFiles)
                {
                    shards.Add(HistoryIO.ReadShard(f, w.Model));
                }

                return RecursiveAI.OneLite.History.Workload.Merge(factory, w, shards);
            }
            finally
            {
                foreach (var p in procs)
                {
                    p.Dispose();
                }

                Directory.Delete(dir, true);
            }
        }

        // Child process: runs one client's share and writes its shard file.
        private static int Worker(Dictionary<string, string> o)
        {
            var w = Workload(o);
            var index = Int(o, "index", 0);
            var shard = o.GetValueOrDefault("shard") ?? throw new ArgumentException("--shard is required");
            using var client = Factory(o).Create(index);
            var startAt = o.TryGetValue("start-at", out var sa) ? long.Parse(sa, CultureInfo.InvariantCulture) : 0;
            while (RecursiveAI.OneLite.History.Workload.Now() < startAt)
            {
                System.Threading.Thread.Sleep(1);
            }

            var txns = RecursiveAI.OneLite.History.Workload.RunWorker(client, w, index);
            RecursiveAI.OneLite.History.Workload.WriteShard(shard, txns);
            return 0;
        }
    }
}
