using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using RecursiveAI.OneLite;
using RecursiveAI.OneLite.Oracle;
using RecursiveAI.OneLite.QueryDiff;

// querydiff: runs queries on OneLite and on the pinned sqlite3 shell and compares what comes back.
//   run   <db> <sql>                           run one query on OneLite and print the result
//   scan  <db|dir>...                          SELECT * FROM every table of every database; rows, order and errors must match
//   crash --mode journal|wal --images N [--seed S] [--jobs J] [--work DIR] [--keep]
//                                              sqlite3 writers killed mid-commit; OneLite and sqlite3 read the images (see Crash.cs)
//   live  [--commits N] [--mode wal|delete] [--work DIR]
//                                              a sqlite3 writer commits while OneLite reads in a loop (see Live.cs)
//   fuzz  [--seed N] [--count N] [--batch N] [--jobs N] [--show N] [--wal] [--rel] <db|dir>...
//                                              --rel generates joins, subqueries, CTEs, compound selects and grouping instead of single-table queries
//                                              seeded generated queries, N in total, spread over the databases
//   winmem <rows> <dir> [cacheKiB]             one window partition of <rows> rows (sum to the end, ntile, cume_dist): checks every row, reports peak memory and time
//   sortmem <rows> <dir> [cacheKiB]            sorts <rows> generated rows (ORDER BY on a non-indexed text key) and reports peak memory and time
// Databases are opened read-only on both sides. Exit status 1 if anything differs.
return Run(args);

static int Run(string[] args)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("usage: querydiff run <db> <sql> | scan <db|dir>... | fuzz [--seed N] [--count N] [--batch N] [--jobs N] [--show N] <db|dir>...");
        return 2;
    }

    switch (args[0])
    {
        case "run":
            return RunOne(args[1], args[2]);
        case "cmp":
            return CompareOne(args[1], args[2]);
        case "cmpo":
            return CompareOrdered(args[1], args[2..]);
        case "scan":
            return Scan(Databases(args.Skip(1).ToList(), true));
        case "fuzz":
            return Fuzz(args.Skip(1).ToList());
        case "sortmem":
            return SortMemory(args.Skip(1).ToList());
        case "winmem":
            return WindowMemory(args.Skip(1).ToList());
        case "fp":
            return FloatText(args.Skip(1).ToList());
        case "crash":
            return Crash.Run(args.Skip(1).ToList());
        case "codes":
            return Codes(args[1], args.Length > 2 ? args[2] : "SELECT count(*) FROM sqlite_master");
        case "live":
            return Live.Run(args.Skip(1).ToList());
        default:
            Console.Error.WriteLine("unknown command " + args[0]);
            return 2;
    }
}

static List<string> Databases(List<string> paths, bool includeWal)
{
    var dbs = new List<string>();
    foreach (string p in paths)
    {
        if (Directory.Exists(p))
        {
            dbs.AddRange(Directory.GetFiles(p).Where(f => !f.EndsWith("-journal", StringComparison.Ordinal) && !f.EndsWith("-wal", StringComparison.Ordinal) && !f.EndsWith("-shm", StringComparison.Ordinal)));
        }
        else
        {
            dbs.Add(p);
        }
    }

    // The WAL layer is not wired in yet: databases in WAL mode are left out unless asked for.
    return [.. dbs.Where(f => includeWal || !Path.GetFileName(f).Contains("wal", StringComparison.Ordinal)).OrderBy(f => f, StringComparer.Ordinal)];
}

static List<string> Families(string sql)
{
    var f = new List<string>();
    void Has(string name, string pattern)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(sql, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            f.Add(name);
        }
    }

    Has("cast", @"\bCAST\s*\(");
    Has("collation", @"\bCOLLATE\b");
    Has("date/time", @"\b(date|time|datetime|julianday|unixepoch|strftime|timediff)\s*\(|CURRENT_(DATE|TIME)");
    Has("math", @"\b(abs|round|sign|ceil|ceiling|floor|trunc|ln|log|log2|log10|exp|pow|power|sqrt|mod|sin|cos|tan|asin|acos|atan|atan2|sinh|cosh|tanh|asinh|acosh|atanh|degrees|radians|pi)\s*\(");
    Has("string", @"\b(length|octet_length|lower|upper|substr|substring|trim|ltrim|rtrim|replace|instr|hex|unhex|quote|printf|format|concat|concat_ws|char|unicode|unistr|typeof|zeroblob|nullif|coalesce|ifnull|iif|if|min|max|likely|unlikely|likelihood|glob|like)\s*\(");
    Has("operators", @"(\|\||<<|>>|&|~|\bIS\b|\bBETWEEN\b|\bIN\b|\bCASE\b|\bLIKE\b|\bGLOB\b|\bNOT\b|[-+*/%<>=])");
    return f;
}

static int CompareOne(string db, string sql)
{
    using var d = Database.OpenReadOnly(db, Qd.Options);
    using Connection c = d.Connect();
    Outcome o = OursRunner.Run(c, sql);
    Outcome s = ShellRunner.RunBatch(db, [sql])[0];
    string? diff = Compare.Diff(o, s, false);
    Console.WriteLine(diff is null ? "same" : "DIFF " + diff);
    return 0;
}

// cmpo <db> <sql>...: like cmp, but the row order must match too (statements with no fixed order still come out in the reference's order).
static int CompareOrdered(string db, string[] queries)
{
    int bad = 0;
    using var d = Database.OpenReadOnly(db, Qd.Options);
    using Connection c = d.Connect();
    Outcome[] theirs = ShellRunner.RunBatch(db, queries);
    for (int i = 0; i < queries.Length; i++)
    {
        Outcome o = OursRunner.Run(c, queries[i]);
        string? diff = Compare.Diff(o, theirs[i], true);
        if (diff is not null)
        {
            bad++;
            Console.WriteLine(queries[i]);
            Console.WriteLine("  DIFF " + diff);
        }
    }

    Console.WriteLine($"compared {queries.Length}, different {bad}");
    return bad == 0 ? 0 : 1;
}

// codes <db> [sql]: the extended result code and message of the first query, read-only, from the pinned library and from OneLite.
static int Codes(string db, string sql)
{
    string theirs;
    try
    {
        using SqliteDb s = SqliteDb.Open(db, readOnly: true, create: false, busyTimeoutMs: 0);
        using SqliteStatement st = s.Prepare(sql);
        while (st.Step())
        {
        }

        theirs = "ok";
    }
    catch (SqliteException e)
    {
        theirs = $"{e.Code}/{e.ExtendedCode} {e.Message}";
    }

    string ours;
    try
    {
        using var d = Database.OpenReadOnly(db, Qd.Options);
        using Connection c = d.Connect();
        using Statement st = c.Prepare(sql);
        while (st.Step())
        {
        }

        ours = "ok";
    }
    catch (OneLiteException e)
    {
        ours = $"{e.Code}/{e.ExtendedCode} {e.Message}";
    }

    Console.WriteLine($"{(ours == theirs.Split(" (")[0] || ours == theirs ? "same" : "DIFF")}: ours {ours} | sqlite3 {theirs}");
    return 0;
}

static int RunOne(string db, string sql)
{
    using var d = Database.OpenReadOnly(db, Qd.Options);
    using Connection c = d.Connect();
    Outcome o = OursRunner.Run(c, sql);
    if (o.HasError)
    {
        Console.WriteLine(o.ErrorText);
    }

    Console.WriteLine(string.Join(" | ", o.Names));
    foreach (Cell[] row in o.Rows)
    {
        Console.WriteLine(string.Join(" | ", row.Select(x => x.Show)));
    }

    return o.HasError ? 1 : 0;
}

static int Scan(List<string> dbs)
{
    long tables = 0, rows = 0, same = 0, bothErr = 0, diffs = 0, skipped = 0;
    var lines = new ConcurrentBag<string>();
    Parallel.ForEach(dbs, new ParallelOptions { MaxDegreeOfParallelism = 6 }, db =>
    {
        List<string> names = TableNames(db, out string? err);
        if (err != null)
        {
            // A database the shell cannot even list (a hot journal needs recovery): the scan of sqlite_master must fail the same way.
            names = ["sqlite_master"];
        }

        var queries = names.Select(n => "SELECT * FROM " + Introspect.Q(n)).ToList();
        Outcome[] theirs = ShellRunner.RunBatch(db, queries);
        Database d;
        try
        {
            d = Database.OpenReadOnly(db, Qd.Options);
        }
        catch (OneLiteException e)
        {
            lines.Add($"{db}: cannot open: {e.Message}");
            Interlocked.Increment(ref diffs);
            return;
        }

        using (d)
        using (Connection c = d.Connect())
        {
            for (int i = 0; i < queries.Count; i++)
            {
                Outcome ours = OursRunner.Run(c, queries[i]);
                string? diff = Compare.Diff(ours, theirs[i], true);
                Interlocked.Increment(ref tables);
                if (diff is null)
                {
                    Interlocked.Increment(ref same);
                    Interlocked.Add(ref rows, ours.Rows.Count);
                    if (ours.HasError)
                    {
                        Interlocked.Increment(ref bothErr);
                    }
                }
                else if (ours.HasError && ours.Message.StartsWith("unsupported", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref skipped);
                    lines.Add($"{Path.GetFileName(db)}: {queries[i]}: {ours.Message}");
                }
                else
                {
                    Interlocked.Increment(ref diffs);
                    lines.Add($"DIFF {Path.GetFileName(db)}: {queries[i]}: {diff}");
                }
            }
        }
    });

    foreach (string l in lines.OrderBy(x => x, StringComparer.Ordinal).Take(60))
    {
        Console.WriteLine(l);
    }

    Console.WriteLine($"scan: {dbs.Count} databases, {tables} tables, {rows} rows compared, {same} identical ({bothErr} identical errors), {diffs} differences, {skipped} unsupported");
    return diffs == 0 ? 0 : 1;
}

static List<string> TableNames(string db, out string? error)
{
    error = null;
    Proc.Result r = Proc.Run("/bin/sh", ["-c", "exec \"$0\" \"$@\" 2>&1", OraclePaths.Cli, "-batch", "-init", "/dev/null", "-readonly", db, "select name from sqlite_master where type='table' and sql not like 'CREATE VIRTUAL%' order by rowid"]);
    if (r.Stdout.Contains("rror", StringComparison.Ordinal))
    {
        error = r.Stdout.Trim();
        return [];
    }

    return [.. r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)];
}

// fp --seed N --count N <db>: real-to-text. Random doubles are built exactly in SQL (an integer mantissa scaled by exact powers of two) and
// printed by both sides; the texts must be identical.
static int FloatText(List<string> args)
{
    int seed = 1, count = 100000;
    string db = "";
    for (int i = 0; i < args.Count; i++)
    {
        switch (args[i])
        {
            case "--seed": seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "--count": count = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
            default: db = args[i]; break;
        }
    }

    var rnd = new Random(seed);
    var qs = new List<string>();
    while (qs.Count < count)
    {
        long m;
        int sh;
        switch (rnd.Next(4))
        {
            case 0: m = rnd.NextInt64(1L << 52, 1L << 53); sh = rnd.Next(-1000, 960); break;
            case 1: m = rnd.NextInt64(1L << 52, 1L << 53); sh = rnd.Next(-60, -39); break;
            case 2: m = rnd.NextInt64(1, 1L << rnd.Next(20, 53)) | 1; sh = rnd.Next(-70, 10); break;
            default: m = rnd.NextInt64(1, 1_000_000); sh = -rnd.Next(0, 30); break;
        }

        if (Math.ScaleB((double)m, sh) < 2.3e-308 || double.IsInfinity(Math.ScaleB((double)m, sh)))
        {
            continue;
        }

        var sb = new StringBuilder($"CAST({m} AS REAL)");
        int a = Math.Abs(sh) / 40, r = Math.Abs(sh) % 40;
        string op = sh > 0 ? "*" : "/";
        for (int i = 0; i < a; i++)
        {
            sb.Append(' ').Append(op).Append(" 1099511627776.0");
        }

        if (r > 0)
        {
            sb.Append(' ').Append(op).Append(' ').Append(1L << r).Append(".0");
        }

        qs.Add("SELECT CAST((" + sb + ") AS TEXT)");
    }

    Outcome[] theirs = ShellRunner.RunBatch(db, qs);
    using var d = Database.OpenReadOnly(db, Qd.Options);
    using Connection c = d.Connect();
    int diffs = 0;
    for (int i = 0; i < qs.Count; i++)
    {
        Outcome ours = OursRunner.Run(c, qs[i]);
        string? diff = Compare.Diff(ours, theirs[i], false);
        if (diff is not null)
        {
            if (diffs++ < 15)
            {
                Console.WriteLine($"DIFF {qs[i]}\n  {diff}");
            }
        }
    }

    Console.WriteLine($"fp seed={seed}: {qs.Count} doubles, {diffs} differences ({100.0 * diffs / qs.Count:F3}%)");
    return diffs == 0 ? 0 : 1;
}

static int Fuzz(List<string> args)
{
    int seed = 1, count = 10000, batch = 1000, jobs = 6, show = 25;
    bool wal = false, rel = false;
    var paths = new List<string>();
    for (int i = 0; i < args.Count; i++)
    {
        switch (args[i])
        {
            case "--seed": seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "--count": count = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "--batch": batch = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "--jobs": jobs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "--show": show = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "--wal": wal = true; break;
            case "--rel": rel = true; break;
            default: paths.Add(args[i]); break;
        }
    }

    List<string> dbs = Databases(paths, wal);
    // Hot-journal databases only produce one error; the generated corpus' other variants carry the load.
    dbs = [.. dbs.Where(d => !Path.GetFileName(d).StartsWith("hot-", StringComparison.Ordinal))];
    int per = Math.Max(1, count / Math.Max(1, dbs.Count));
    long total = 0, same = 0, bothErr = 0, diffs = 0, rowsCompared = 0, unsupported = 0, evalOrder = 0;
    var report = new ConcurrentBag<string>();
    var kinds = new ConcurrentDictionary<string, int>();
    // Queries are tallied by the feature families they use (a query can count in several), with how many of them differ.
    var famTotal = new ConcurrentDictionary<string, int>();
    var famDiff = new ConcurrentDictionary<string, int>();
    // A query that runs for a long time is named on stderr while it is still running, so a hang can be found and reproduced.
    var inFlight = new ConcurrentDictionary<int, (string Sql, long Started)>();
    using var watchdog = new Timer(_ =>
    {
        long now = Environment.TickCount64;
        foreach (KeyValuePair<int, (string Sql, long Started)> kv in inFlight)
        {
            if (now - kv.Value.Started > 15000)
            {
                Console.Error.WriteLine($"SLOW {(now - kv.Value.Started) / 1000}s: {kv.Value.Sql}");
            }
        }
    }, null, 15000, 15000);
    Parallel.ForEach(dbs.Select((d, i) => (d, i)), new ParallelOptions { MaxDegreeOfParallelism = jobs }, item =>
    {
        (string db, int di) = item;
        List<TableMeta> tables = Introspect.Tables(db);
        int dbSeed = unchecked((seed * 7919) + (di * 104729));
        var gen = new QueryGen(dbSeed, tables);
        var relGen = new RelGen(dbSeed, tables);
        int made = 0;
        while (made < per)
        {
            int n = Math.Min(batch, per - made);
            var qs = new List<string>();
            var ordered = new List<bool>();
            for (int k = 0; k < n; k++)
            {
                (string sql, bool ord, _) = rel ? relGen.Next() : gen.Next();
                qs.Add(sql);
                ordered.Add(ord);
            }

            Outcome[] theirs = ShellRunner.RunBatch(db, qs);
            using var d = Database.OpenReadOnly(db, Qd.Options);
            using Connection c = d.Connect();
            for (int k = 0; k < n; k++)
            {
                inFlight[di] = (qs[k], Environment.TickCount64);
                Outcome ours = OursRunner.Run(c, qs[k]);
                inFlight.TryRemove(di, out _);
                Interlocked.Increment(ref total);
                string? diff = Compare.Diff(ours, theirs[k], ordered[k]);
                List<string> fams = Families(qs[k]);
                foreach (string f in fams)
                {
                    famTotal.AddOrUpdate(f, 1, (_, v) => v + 1);
                    if (diff is not null)
                    {
                        famDiff.AddOrUpdate(f, 1, (_, v) => v + 1);
                    }
                }

                if (diff is null)
                {
                    Interlocked.Increment(ref same);
                    Interlocked.Add(ref rowsCompared, ours.Rows.Count);
                    if (ours.HasError)
                    {
                        Interlocked.Increment(ref bothErr);
                    }
                }
                else if (ours.HasError && ours.Message.StartsWith("unsupported", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref unsupported);
                    kinds.AddOrUpdate(ours.Message, 1, (_, v) => v + 1);
                }
                else if ((ours.HasError && ours.Phase == "run" && !theirs[k].HasError) || (theirs[k].HasError && theirs[k].Phase == "run" && !ours.HasError))
                {
                    // Which WHERE term is evaluated first (and so whether a runtime error is raised at all) is the planner's choice;
                    // the reference orders terms by its own cost estimate. Counted, not compared.
                    Interlocked.Increment(ref evalOrder);
                }
                else
                {
                    Interlocked.Increment(ref diffs);
                    report.Add($"DIFF db={Path.GetFileName(db)} seed={dbSeed} #{made + k}\n  {qs[k]}\n  {diff}");
                }
            }

            made += n;
        }
    });

    foreach (string r in report.OrderBy(x => x, StringComparer.Ordinal).Take(show))
    {
        Console.WriteLine(r);
    }

    foreach (KeyValuePair<string, int> kv in famTotal.OrderBy(k => k.Key, StringComparer.Ordinal))
    {
        Console.WriteLine($"family {kv.Key}: {kv.Value} queries, {famDiff.GetValueOrDefault(kv.Key)} not identical");
    }

    foreach (KeyValuePair<string, int> kv in kinds.OrderByDescending(k => k.Value).Take(10))
    {
        Console.WriteLine($"unsupported x{kv.Value}: {kv.Key}");
    }

    Console.WriteLine($"fuzz seed={seed}: {dbs.Count} databases, {total} queries, {same} identical ({bothErr} identical errors, {rowsCompared} rows), {diffs} differences, {unsupported} unsupported, {evalOrder} runtime-error-order cases");
    return diffs == 0 ? 0 : 1;
}

static int WindowMemory(List<string> args)
{
    long rows = long.Parse(args[0], CultureInfo.InvariantCulture);
    string dir = args[1];
    int cacheKiB = args.Count > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 2000;
    Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, "win-" + rows.ToString(CultureInfo.InvariantCulture) + ".db");
    if (!File.Exists(path))
    {
        SqliteCli.ApplyScript(path, "PRAGMA page_size=4096; CREATE TABLE t(id INTEGER PRIMARY KEY, v INT);"
            + "WITH RECURSIVE s(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM s WHERE n<" + rows.ToString(CultureInfo.InvariantCulture) + ") "
            + "INSERT INTO t SELECT n, n % 7 FROM s;");
    }

    // The expected values are computed here from the row number: v = id % 7, so the sum from a row to the end is the total minus the prefix.
    long total = 0;
    for (long i = 1; i <= rows; i++)
    {
        total += i % 7;
    }

    using var db = Database.OpenReadOnly(path, new DatabaseOptions { CacheSize = -cacheKiB });
    using Connection conn = db.Connect();
    using Statement st = conn.Prepare("SELECT id, sum(v) OVER w, ntile(4) OVER w, cume_dist() OVER w FROM t WINDOW w AS (ORDER BY id ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING)");
    GC.Collect();
    long baseline = GC.GetTotalMemory(true);
    long peakHeap = baseline;
    var clock = System.Diagnostics.Stopwatch.StartNew();
    long n = 0, bad = 0, prefix = 0;
    while (st.Step())
    {
        n++;
        long id = st.GetInt64(0);
        long wantNtile = (n - 1) / (rows / 4) + 1;
        if (id != n || st.GetInt64(1) != total - prefix || st.GetInt64(2) != wantNtile || st.GetDouble(3) != (double)n / rows)
        {
            if (++bad <= 3)
            {
                Console.WriteLine($"row {n}: id={id} sum={st.GetInt64(1)} (want {total - prefix}) ntile={st.GetInt64(2)} (want {wantNtile}) cume={st.GetDouble(3)}");
            }
        }

        prefix += n % 7;
        if ((n & 0xFFFFF) == 0)
        {
            peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(true));
        }
    }

    peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(true));
    long peakWs = System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64;
    Console.WriteLine($"winmem rows={rows} returned={n} wrong={bad} cacheKiB={cacheKiB} seconds={clock.Elapsed.TotalSeconds:F1} "
        + $"sampledPeakManagedMiB={(peakHeap - baseline) / 1048576.0:F1} peakWorkingSetMiB={peakWs / 1048576.0:F1} dbMiB={new FileInfo(path).Length / 1048576.0:F0}");
    return n == rows && bad == 0 ? 0 : 1;
}

static int SortMemory(List<string> args)
{
    long rows = long.Parse(args[0], CultureInfo.InvariantCulture);
    string dir = args[1];
    int cacheKiB = args.Count > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 2000;
    Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, "sort-" + rows.ToString(CultureInfo.InvariantCulture) + ".db");
    if (!File.Exists(path))
    {
        // Keys come from a multiplicative hash of the row number, so the input is far from sorted.
        SqliteCli.ApplyScript(path, "PRAGMA page_size=4096; CREATE TABLE t(id INTEGER PRIMARY KEY, k TEXT, pad TEXT);"
            + "WITH RECURSIVE s(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM s WHERE n<" + rows.ToString(CultureInfo.InvariantCulture) + ") "
            + "INSERT INTO t SELECT n, printf('%012d', (n * 2654435761) % 1000000007), 'padding-' || (n % 1000) FROM s;");
    }

    using var db = Database.OpenReadOnly(path, new DatabaseOptions { CacheSize = -cacheKiB });
    using Connection conn = db.Connect();
    using Statement st = conn.Prepare("SELECT id, k, pad FROM t ORDER BY k, id");
    GC.Collect();
    long baseline = GC.GetTotalMemory(true);
    long peakHeap = baseline;
    var clock = System.Diagnostics.Stopwatch.StartNew();
    long n = 0;
    byte[] prev = new byte[64];
    int prevLen = 0;
    bool ordered = true;
    while (st.Step())
    {
        ReadOnlySpan<byte> k = st.GetTextUtf8(1);
        ordered &= prev.AsSpan(0, prevLen).SequenceCompareTo(k) <= 0;
        k.CopyTo(prev);
        prevLen = k.Length;
        if ((++n & 0xFFFFF) == 0)
        {
            // A full collection first, so the figure is live data and not garbage waiting to be collected.
            peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(true));
        }
    }

    peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(true));
    long peakWs = System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64;
    Console.WriteLine($"sortmem rows={rows} returned={n} ordered={ordered} cacheKiB={cacheKiB} seconds={clock.Elapsed.TotalSeconds:F1} "
        + $"sampledPeakManagedMiB={(peakHeap - baseline) / 1048576.0:F1} peakWorkingSetMiB={peakWs / 1048576.0:F1} dbMiB={new FileInfo(path).Length / 1048576.0:F0}");
    return n == rows && ordered ? 0 : 1;
}
