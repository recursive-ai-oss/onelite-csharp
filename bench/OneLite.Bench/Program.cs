using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RecursiveAI.OneLite.Bench;

/// <summary>What one child process measured for one row on one side.</summary>
internal sealed record ChildResult
{
    public string Row { get; init; } = "";

    public string Level { get; init; } = "";

    public string Side { get; init; } = "";

    public string Impl { get; init; } = "";

    public double[] RepMs { get; init; } = [];

    public double MedianMs { get; init; }

    public long Ops { get; init; }

    public long AllocBytes { get; init; }

    public long PeakKb { get; init; }

    public long Check { get; init; }

    public double SetupMs { get; init; }

    public double ColdMs { get; init; }

    public string? Skipped { get; init; }
}

internal static class Program
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        Dictionary<string, string> o = ParseOptions(args.AsSpan(1));
        string native = o.GetValueOrDefault("native", Path.Combine(Home(), "onelite/oracles/native-3.53.4", OperatingSystem.IsMacOS() ? "libe_sqlite3.dylib" : "libe_sqlite3.so"));
        Native.Configure(native);
        if (!OperatingSystem.IsWindows())
        {
            SQLitePCL.Batteries_V2.Init();
        }

        return args[0] switch
        {
            "run" => Parent.Run(o, native),
            "child" => Child(o),
            "list" => List(),
            "merge" => Parent.Merge(o, [.. args.Skip(1).Where(a => a.EndsWith(".json", StringComparison.Ordinal))]),
            _ => Fail("unknown command " + args[0]),
        };
    }

    private const string Usage = """
        onelite-bench: OneLite against native SQLite, at two levels.
          run   [--rows a,b|all] [--levels engine,app] [--reps 3] [--n 100000] [--dir DIR] [--out PREFIX]
                [--native LIB] [--vec VEC0_EXT] [--job ID] [--class wq-class] [--label TEXT]
          merge --out PREFIX a.json b.json ...   combine filtered runs into one report
          list  show the row names
        engine = OneLite Statement.Step vs raw P/Invoke sqlite3_step; app = OneLite.Data vs Microsoft.Data.Sqlite.
        Writes PREFIX.json and PREFIX.md. Each row/side/level runs in its own child process (clean peak memory).
        """;

    private static int Fail(string m)
    {
        Console.Error.WriteLine(m);
        return 2;
    }

    private static int List()
    {
        foreach (Row r in Rows.All)
        {
            Console.WriteLine($"{r.Name,-20} {r.Description}");
        }

        Console.WriteLine("cold_start           process start to first query result, 10k-row WAL database (report only)");
        return 0;
    }

    internal static string Home() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static Dictionary<string, string> ParseOptions(ReadOnlySpan<string> a)
    {
        var d = new Dictionary<string, string>();
        for (int i = 0; i + 1 < a.Length + 1; i++)
        {
            if (a[i].StartsWith("--", StringComparison.Ordinal))
            {
                d[a[i][2..]] = i + 1 < a.Length ? a[i + 1] : "";
                i++;
            }
        }

        return d;
    }

    // ---- child: runs one row on one side and prints one RESULT line --------------------------------------------

    private static int Child(Dictionary<string, string> o)
    {
        string rowName = o["row"];
        string level = o["level"];
        string side = o["side"];
        int n = int.Parse(o.GetValueOrDefault("n", "100000"), Inv);
        int reps = int.Parse(o.GetValueOrDefault("reps", "3"), Inv);
        string dir = o["dir"];
        string? vec = o.GetValueOrDefault("vec");
        string phase = o.GetValueOrDefault("phase", "all");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"{rowName}-{level}-{side}.db");

        ChildResult res;
        if (rowName == "cold_start")
        {
            res = ColdStart(phase, level, side, path);
        }
        else
        {
            Row row = Rows.All.First(r => r.Name == rowName);
            res = RunRow(row, level, side, path, n, reps, row.NeedsVec ? vec : null);
        }

        Console.WriteLine("RESULT " + JsonSerializer.Serialize(res));
        return 0;
    }

    private static void Pragmas(IDriver d, Row row)
    {
        d.Exec("pragma page_size=4096");
        d.Exec("pragma journal_mode=" + row.Journal);
        d.Exec("pragma synchronous=" + row.Synchronous);
        d.Exec("pragma cache_size=-2000");
    }

    private static void DeleteDb(string path)
    {
        foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            File.Delete(path + suffix);
        }
    }

    private static ChildResult RunRow(Row row, string level, string side, string path, int n, int reps, string? vec)
    {
        if (row.NeedsVec && vec is null && side == "native")
        {
            return new ChildResult { Row = row.Name, Level = level, Side = side, Skipped = "no sqlite-vec loadable extension for the reference side" };
        }

        DeleteDb(path);
        var ctx = new Ctx(level, side, path, n, vec);
        var sw = Stopwatch.StartNew();
        IDriver d = ctx.Open();
        Pragmas(d, row);
        row.Setup(d, ctx);
        double setupMs = sw.Elapsed.TotalMilliseconds;

        long check = 0;
        var ms = new double[reps];
        var alloc = new long[reps];
        // Short rows are warmed up three times, with a pause longer than the runtime's tier-up delay (100 ms) after each pass, so the timed passes
        // run optimized code: a 10 ms row would otherwise be timed while the JIT is still at its first tier. The first pass is kept as ColdMs.
        double coldMs = 0;
        for (int i = -1; i < reps; i++)
        {
            row.PerRep?.Invoke(d, ctx);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            long t0 = Stopwatch.GetTimestamp();
            check = row.Run(d, ctx);
            double e = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            long a1 = GC.GetAllocatedBytesForCurrentThread();
            if (i == -1)
            {
                coldMs = e;
                if (e < 2000 && reps > 0)
                {
                    // two more warm-up passes: shift the loop so they are not timed
                    for (int w = 0; w < 2; w++)
                    {
                        Thread.Sleep(250);
                        row.PerRep?.Invoke(d, ctx);
                        GC.Collect();
                        check = row.Run(d, ctx);
                    }

                    Thread.Sleep(250);
                }
            }
            else
            {
                ms[i] = e;
                alloc[i] = a1 - a0;
            }
        }

        string impl = d.Label;
        d.Dispose();
        DeleteDb(path);
        return new ChildResult
        {
            Row = row.Name,
            Level = level,
            Side = side,
            Impl = impl,
            RepMs = ms,
            MedianMs = Median(ms),
            Ops = row.Ops(ctx),
            AllocBytes = (long)Median(alloc.Select(x => (double)x).ToArray()),
            PeakKb = PeakKb(),
            Check = check,
            SetupMs = setupMs,
            ColdMs = coldMs,
        };
    }

    /// <summary>Process start to the first query's answer, on a 10k-row database made by a separate earlier process.</summary>
    private static ChildResult ColdStart(string phase, string level, string side, string path)
    {
        var row = new Row { Name = "cold_start", Description = "", Ops = _ => 1, Setup = (_, _) => { }, Run = (_, _) => 0 };
        var ctx = new Ctx(level, side, path, 10_000, null);
        if (phase == "prep")
        {
            DeleteDb(path);
            using IDriver d = ctx.Open();
            Pragmas(d, row);
            d.Exec("create table t(id integer primary key, a integer, b text, c real)");
            d.Exec("begin");
            using (IStmt ins = d.Prepare("insert into t(id, a, b, c) values(@p1, @p2, @p3, @p4)"))
            {
                for (int i = 1; i <= 10_000; i++)
                {
                    ins.Bind(1, i);
                    ins.Bind(2, i * 7L);
                    ins.Bind(3, "row " + i.ToString(Inv));
                    ins.Bind(4, i * 0.5);
                    ins.Exec();
                }
            }

            d.Exec("commit");
            return new ChildResult { Row = "cold_start", Level = level, Side = side, Impl = d.Label };
        }

        long check;
        string impl;
        using (IDriver d = ctx.Open())
        {
            impl = d.Label;
            using IStmt s = d.Prepare("select b from t where id = @p1");
            s.Bind(1, 4242);
            check = 0;
            while (s.Step())
            {
                check += s.TextLength(0);
            }
        }

        double cold = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        return new ChildResult { Row = "cold_start", Level = level, Side = side, Impl = impl, ColdMs = cold, Check = check, PeakKb = PeakKb() };
    }

    /// <summary>Peak resident set of this process in KiB (Process.PeakWorkingSet64 reads 0 on macOS, so getrusage fills in).</summary>
    private static long PeakKb()
    {
        long ws = Process.GetCurrentProcess().PeakWorkingSet64 / 1024;
        if (ws > 0)
        {
            return ws;
        }

        // struct rusage: two timevals (4 longs), then ru_maxrss: bytes on macOS.
        var ru = new long[18];
        return Native.getrusage(0, ru) == 0 ? ru[4] / (OperatingSystem.IsMacOS() ? 1024 : 1) : 0;
    }

    internal static double Median(double[] v)
    {
        if (v.Length == 0)
        {
            return 0;
        }

        double[] s = [.. v.Order()];
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[(s.Length / 2) - 1] + s[s.Length / 2]) / 2;
    }

    internal static string Sb(Action<StringBuilder> f)
    {
        var sb = new StringBuilder();
        f(sb);
        return sb.ToString();
    }
}
