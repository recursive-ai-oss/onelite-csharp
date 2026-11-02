using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RecursiveAI.OneLite.Oracle;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Crypto;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Durability;

/// <summary>
/// The encrypted settings a writer, builder or judge works with, written as <c>name=value</c> words so they pass through a command line:
/// <c>key= compat=3|4 ps= iter= kdf= hmac= mode=delete|truncate|persist|wal sync= cache= av=</c>.
/// </summary>
internal sealed class CipherSpec
{
    public string Key = "pw";
    public int Compat = 4, PageSize, Iter = 1000, Cache = 2000, AutoVacuum;
    public CipherDigest? Kdf, Hmac;
    public string Mode = "delete", Sync = "full";

    public static CipherSpec Parse(IEnumerable<string> words)
    {
        var s = new CipherSpec();
        foreach (string w in words)
        {
            string[] kv = w.Split('=', 2);
            if (kv.Length != 2)
            {
                throw new InvalidOperationException("expected name=value, got " + w);
            }

            switch (kv[0])
            {
                case "key": s.Key = kv[1]; break;
                case "compat": s.Compat = int.Parse(kv[1], CultureInfo.InvariantCulture); break;
                case "ps": s.PageSize = int.Parse(kv[1], CultureInfo.InvariantCulture); break;
                case "iter": s.Iter = int.Parse(kv[1], CultureInfo.InvariantCulture); break;
                case "kdf": s.Kdf = Digest(kv[1]); break;
                case "hmac": s.Hmac = Digest(kv[1]); break;
                case "mode": s.Mode = kv[1]; break;
                case "sync": s.Sync = kv[1]; break;
                case "cache": s.Cache = int.Parse(kv[1], CultureInfo.InvariantCulture); break;
                case "av": s.AutoVacuum = int.Parse(kv[1], CultureInfo.InvariantCulture); break;
                default: throw new InvalidOperationException("unknown setting " + w);
            }
        }

        return s;
    }

    private static CipherDigest Digest(string v) => v switch
    {
        "sha1" => CipherDigest.Sha1,
        "sha256" => CipherDigest.Sha256,
        _ => CipherDigest.Sha512,
    };

    public CipherSettings Settings()
    {
        CipherSettings c = CipherSettings.ForCompatibility(Compat).WithKdfIterations(Iter);
        if (PageSize != 0)
        {
            c = c.WithPageSize(PageSize);
        }

        return Kdf is null && Hmac is null ? c : c.WithDigests(Kdf ?? c.KdfDigest, Hmac ?? c.HmacDigest);
    }

    // key=none builds the same database without encryption (the reference the encrypted dumps are compared with).
    public PagerOptions Options(string? key = null)
    {
        string k = key ?? Key;
        bool plain = k == "none";
        return new()
        {
            Key = plain ? null : CipherKey.Parse(k),
            Cipher = plain ? null : Settings(),
            PageSize = PageSize != 0 ? PageSize : 4096,
            AutoVacuum = AutoVacuum != 0,
            IncrementalVacuum = AutoVacuum == 2,
            CacheSize = Cache,
            JournalMode = Mode == "wal" ? JournalMode.Delete : Enum.Parse<JournalMode>(Mode, ignoreCase: true),
            Synchronous = Enum.Parse<SynchronousMode>(Sync, ignoreCase: true),
            WalAutoCheckpoint = 1000,
        };
    }

    // The pragmas that make the SQLCipher shell read a file written with these settings: every setting spelled out, so no default can differ.
    public string ShellPragmas(string? key = null)
    {
        CipherSettings c = Settings();
        static string Alg(CipherDigest d, string prefix) => prefix + d switch { CipherDigest.Sha1 => "SHA1", CipherDigest.Sha256 => "SHA256", _ => "SHA512" };
        return $"PRAGMA key=\"{key ?? Key}\"; PRAGMA cipher_page_size={c.PageSize}; PRAGMA kdf_iter={c.KdfIterations}; " +
               $"PRAGMA cipher_kdf_algorithm={Alg(c.KdfDigest, "PBKDF2_HMAC_")}; PRAGMA cipher_hmac_algorithm={Alg(c.HmacDigest, "HMAC_")};";
    }

    public StorageEngine Open(string db, string? key = null) => StorageEngine.Open(new FileVfs(), db, Options(key));
}

/// <summary>The SQLCipher shell as the judge of encrypted files: it opens a file set (recovering a hot journal or a WAL itself) and reports.</summary>
internal static class CipherShell
{
    public static string Exe => Environment.GetEnvironmentVariable("ONELITE_SQLCIPHER") ?? throw new InvalidOperationException("set ONELITE_SQLCIPHER to the SQLCipher shell");

    public static Proc.Result Run(string db, string script) => Proc.Run(Exe, [db], script, timeout: TimeSpan.FromMinutes(5));

    /// <summary>Checks a database after a writer was killed, as <see cref="Workload.Check"/> does, but through the SQLCipher shell with the given key.</summary>
    public static Workload.Verdict Check(string db, CipherSpec spec, string? key, out bool wrongKey)
    {
        wrongKey = false;
        var bad = new List<string>();
        if (!File.Exists(db) || new FileInfo(db).Length == 0)
        {
            return new Workload.Verdict(0, bad);
        }

        string pre = spec.ShellPragmas(key);
        Proc.Result a = Run(db, pre + "\nselect '@tables', count(*) from sqlite_schema where name in ('txlog','data','counter');\n" +
                                "select '@integrity'; PRAGMA integrity_check; select '@checkpoint'; PRAGMA wal_checkpoint(TRUNCATE); select '@cipher'; PRAGMA cipher_integrity_check; select '@end';\n");
        string all = a.Stdout + a.Stderr;
        if (all.Contains("file is not a database", StringComparison.Ordinal))
        {
            wrongKey = true;
            return new Workload.Verdict(-1, ["file is not a database"]);
        }

        string[] lines = a.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string? tables = lines.FirstOrDefault(l => l.StartsWith("@tables|", StringComparison.Ordinal))?.Split('|')[1];
        int i0 = Array.IndexOf(lines, "@integrity"), i1 = Array.IndexOf(lines, "@cipher"), i2 = Array.IndexOf(lines, "@end");
        int ic = Array.IndexOf(lines, "@checkpoint");
        if (tables is null || i0 < 0 || ic < i0 || i1 < ic || i2 < i1)
        {
            bad.Add("cannot open: " + all.Trim().Replace('\n', ' ')[..Math.Min(200, all.Trim().Length)]);
            return new Workload.Verdict(-1, bad);
        }

        // cipher_integrity_check reads the database file itself, not the WAL: checkpoint first, so frames not yet in the file are checked too.
        string[] integrity = lines[(i0 + 1)..ic];
        string[] cipher = lines[(i1 + 1)..i2];
        if (integrity.Length != 1 || integrity[0] != "ok")
        {
            bad.Add("integrity_check: " + string.Join("; ", integrity));
            return new Workload.Verdict(-1, bad);
        }

        if (cipher.Length != 0)
        {
            bad.Add("integrity_check: cipher_integrity_check: " + string.Join("; ", cipher));
            return new Workload.Verdict(-1, bad);
        }

        if (tables == "0")
        {
            return new Workload.Verdict(0, bad);
        }

        if (tables != "3")
        {
            bad.Add("partial schema: " + tables + " of 3 tables");
            return new Workload.Verdict(-1, bad);
        }

        Proc.Result b = Run(db, pre + "\nselect '@counter', n from counter where id = 1;\nselect '@log', count(*), coalesce(max(txn), 0) from txlog;\n" +
            "select '@row', txn, seq, v, hex(payload) from data where txn > (select n from counter where id = 1) - 3 order by txn, seq;\n" +
            "select '@total', count(*) from data;\nselect '@txlog', txn, nrows, sumv from txlog where txn > (select n from counter where id = 1) - 3 order by txn;\n");
        long m = -1, count = -1, maxTxn = -1, total = -1;
        var rows = new Dictionary<long, List<(long Seq, long V, string Hex)>>();
        var logged = new Dictionary<long, (long Rows, long Sum)>();
        foreach (string l in b.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] f = l.Split('|');
            switch (f[0])
            {
                case "@counter": m = long.Parse(f[1], CultureInfo.InvariantCulture); break;
                case "@log": count = long.Parse(f[1], CultureInfo.InvariantCulture); maxTxn = long.Parse(f[2], CultureInfo.InvariantCulture); break;
                case "@total": total = long.Parse(f[1], CultureInfo.InvariantCulture); break;
                case "@row":
                {
                    long t = long.Parse(f[1], CultureInfo.InvariantCulture);
                    if (!rows.TryGetValue(t, out List<(long, long, string)>? list))
                    {
                        rows[t] = list = [];
                    }

                    list.Add((long.Parse(f[2], CultureInfo.InvariantCulture), long.Parse(f[3], CultureInfo.InvariantCulture), f[4]));
                    break;
                }

                case "@txlog": logged[long.Parse(f[1], CultureInfo.InvariantCulture)] = (long.Parse(f[2], CultureInfo.InvariantCulture), long.Parse(f[3], CultureInfo.InvariantCulture)); break;
            }
        }

        if (m < 0 || count < 0 || total < 0)
        {
            bad.Add("cannot read the workload tables: " + (b.Stdout + b.Stderr).Trim().Replace('\n', ' '));
            return new Workload.Verdict(-1, bad);
        }

        if (count != m || maxTxn != m)
        {
            bad.Add($"counter says {m}, txlog has {count} rows and max {maxTxn}");
        }

        long expected = 0;
        for (long t = Math.Max(1, m - Workload.DeleteLag + 1); t <= m; t++)
        {
            int n = Workload.RowCount(t);
            expected += n;
            List<(long Seq, long V, string Hex)> got = rows.GetValueOrDefault(t) ?? [];
            long sum = 0;
            for (int j = 0; j < got.Count; j++)
            {
                if (got[j].Seq != j || got[j].V != Workload.Value(t, j) || got[j].Hex != Convert.ToHexString(Workload.Payload(t, j)))
                {
                    bad.Add($"txn {t} row {j} has wrong content");
                    break;
                }

                sum += got[j].V;
            }

            if (got.Count != n)
            {
                bad.Add($"txn {t}: {got.Count} rows present, {n} committed");
            }
            else if (!logged.TryGetValue(t, out (long Rows, long Sum) lg) || lg.Rows != n || lg.Sum != sum)
            {
                bad.Add($"txn {t}: txlog disagrees with the rows");
            }
        }

        if (total != expected)
        {
            bad.Add($"data has {total} rows, the last transactions account for {expected}");
        }

        return new Workload.Verdict(m, bad);
    }
}

/// <summary>Writers for the encrypted kill campaigns: the btree workload on an encrypted database, and a process that rekeys it again and again.</summary>
internal static class CipherWriters
{
    // cipher-writer DB LEDGER settings...   transactions forever; WAL mode is entered through the pager when mode=wal
    public static int Run(string[] a)
    {
        var spec = CipherSpec.Parse(a.Skip(2));
        using StorageEngine eng = spec.Open(a[0]);
        eng.BeginRead();
        if (spec.Mode == "wal" && eng.Pager.PageCount != 0 && !eng.Pager.IsWal)
        {
            eng.Pager.ChangeJournalModeSync(JournalMode.Wal);
        }

        var db = new BTreeWorkloadDb(eng);
        db.Setup();
        if (spec.Mode == "wal" && !eng.Pager.IsWal)
        {
            eng.Pager.ChangeJournalModeSync(JournalMode.Wal);
        }

        long next = db.ReadCounter() + 1;
        Console.Out.WriteLine("READY");
        Console.Out.Flush();
        using var led = new FileStream(a[1], FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        for (long i = next; ; i++)
        {
            db.RunTransaction(i);
            byte[] line = System.Text.Encoding.ASCII.GetBytes(i.ToString(CultureInfo.InvariantCulture) + "\n");
            led.Write(line, 0, line.Length);
            led.Flush();
        }
    }

    // cipher-build DB txns=N settings...   N transactions, then a clean close
    public static int Build(string[] a)
    {
        int txns = 30;
        var rest = new List<string>();
        foreach (string w in a.Skip(1))
        {
            if (w.StartsWith("txns=", StringComparison.Ordinal))
            {
                txns = int.Parse(w[5..], CultureInfo.InvariantCulture);
            }
            else
            {
                rest.Add(w);
            }
        }

        var spec = CipherSpec.Parse(rest);
        using StorageEngine eng = spec.Open(a[0]);
        eng.BeginRead();
        var db = new BTreeWorkloadDb(eng);
        db.Setup();
        if (spec.Mode == "wal" && !eng.Pager.IsWal)
        {
            eng.Pager.ChangeJournalModeSync(JournalMode.Wal);
        }

        for (long i = db.ReadCounter() + 1; i <= txns; i++)
        {
            db.RunTransaction(i);
        }

        eng.EndRead();
        return 0;
    }

    // rekey-writer DB LEDGER first=L settings...   key=rk{i}; each loop rekeys to rk{i+1}, then appends i+1 to the ledger
    public static int Rekey(string[] a)
    {
        int first = 0;
        var rest = new List<string>();
        foreach (string w in a.Skip(2))
        {
            if (w.StartsWith("first=", StringComparison.Ordinal))
            {
                first = int.Parse(w[6..], CultureInfo.InvariantCulture);
            }
            else
            {
                rest.Add(w);
            }
        }

        var spec = CipherSpec.Parse(rest);
        int cur = first;
        StorageEngine? eng = null;
        foreach (int cand in Enumerable.Range(first, 9))
        {
            // A kill can land after a rekey committed and before its ledger line, and the next writer can be killed the same way before it
            // writes one: the file may hold a later key than the ledger says. Probe a window of keys, then record what was found.
            StorageEngine e = spec.Open(a[0], $"rk{cand}");
            try
            {
                e.BeginRead();
                cur = cand;
                eng = e;
                break;
            }
            catch (OneLiteException ex) when (ex.Code == ResultCode.NotADb)
            {
                e.Dispose();
            }
        }

        if (eng is null)
        {
            Console.Error.WriteLine($"none of rk{first}..rk{first + 8} opens the database");
            return 3;
        }

        using (eng)
        {
            using var led = new FileStream(a[1], FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            if (cur != first)
            {
                // The file demonstrably holds rk{cur}: acknowledge it, so the judge's window (last line, or the one after) stays right.
                byte[] seen = System.Text.Encoding.ASCII.GetBytes(cur.ToString(CultureInfo.InvariantCulture) + "\n");
                led.Write(seen, 0, seen.Length);
                led.Flush();
            }

            Console.Out.WriteLine("READY");
            Console.Out.Flush();
            eng.Pager.EndReadSync();
            for (; ; )
            {
                cur++;
                eng.Pager.RekeySync(CipherKey.FromPassphrase($"rk{cur}"));
                byte[] line = System.Text.Encoding.ASCII.GetBytes(cur.ToString(CultureInfo.InvariantCulture) + "\n");
                led.Write(line, 0, line.Length);
                led.Flush();
            }
        }
    }
}

/// <summary>
/// Kill campaigns on encrypted databases, judged by the SQLCipher shell. <c>cipher-kill</c> kills the transaction writer; <c>rekey-kill</c> kills a
/// process that rekeys the database over and over and requires the file to open under exactly one of the two possible keys, with all the data.
/// </summary>
internal static class CipherCampaign
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static int Run(string[] args, bool rekey)
    {
        string work = Path.Combine(Path.GetTempPath(), "onelite-cipherkill-" + Environment.ProcessId), jsonOut = "", keepBad = "";
        int kills = 1000, episode = 25, maxDelay = 300, seed = 1;
        var words = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--kills": kills = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--episode": episode = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--seed": seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--max-delay-ms": maxDelay = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--work": work = args[++i]; break;
                case "--keep-bad": keepBad = args[++i]; break;
                case "--json": jsonOut = args[++i]; break;
                default: words.Add(args[i]); break;
            }
        }

        var spec = CipherSpec.Parse(words);
        Directory.CreateDirectory(work);
        work = Path.GetFullPath(work);
        string db = Path.Combine(work, "kill.db"), ledger = Path.Combine(work, "ledger.txt"), chk = Path.Combine(work, "chk");
        Directory.CreateDirectory(chk);
        var rnd = new Random(seed);
        int corrupt = 0, lost = 0, partial = 0, phantom = 0, hot = 0, startFailures = 0, bothOrNeither = 0;
        long acked = 0, seen = 0;
        var failures = new List<string>();
        var sw = Stopwatch.StartNew();
        for (int k = 0; k < kills; k++)
        {
            if (k % episode == 0)
            {
                PinnedSqlite.DeleteFileSet(db);
                File.Delete(ledger);
                seen = 0;
                if (rekey)
                {
                    var build = new List<string> { "cipher-build", db, "txns=30" };
                    build.AddRange(words.Where(w => !w.StartsWith("key=", StringComparison.Ordinal)));
                    build.Add("key=rk0");
                    (string e0, List<string> a0) = Proc.Self(build);
                    Proc.Result r = Proc.Run(e0, a0);
                    if (r.ExitCode != 0)
                    {
                        Console.Error.WriteLine("build failed: " + r.Stderr);
                        return 2;
                    }

                    File.WriteAllText(ledger, "0\n");
                }
            }

            long before = LastLedger(ledger);
            var cmd = new List<string> { rekey ? "rekey-writer" : "cipher-writer", db, ledger };
            if (rekey)
            {
                cmd.Add("first=" + before);
                cmd.AddRange(words.Where(w => !w.StartsWith("key=", StringComparison.Ordinal)));
            }
            else
            {
                cmd.AddRange(words);
            }

            if (keepBad.Length != 0)
            {
                PinnedSqlite.DeleteFileSet(Path.Combine(chk, "pre.db"));
                PinnedSqlite.CopyFileSet(db, Path.Combine(chk, "pre.db"));
            }

            (string exe, List<string> ca) = Proc.Self(cmd);
            using Process p = Proc.Start(exe, ca);
            Task<string> err = p.StandardError.ReadToEndAsync();
            bool early = rnd.Next(10) == 0;
            if (!early)
            {
                Task<string?> line = p.StandardOutput.ReadLineAsync();
                if (!(line.Wait(TimeSpan.FromSeconds(120)) && line.Result == "READY"))
                {
                    startFailures++;
                    if (keepBad.Length != 0)
                    {
                        // The image the writer started from, kept for replay; the campaign stops here.
                        Directory.CreateDirectory(keepBad);
                        PinnedSqlite.CopyFileSet(Path.Combine(chk, "pre.db"), Path.Combine(keepBad, "start.db"));
                        File.Copy(ledger, Path.Combine(keepBad, "ledger.txt"), true);
                        failures.Add($"kill {k}: writer start failed, starting image kept in {keepBad}");
                        p.Kill();
                        p.WaitForExit();
                        k = kills;
                        break;
                    }

                    p.WaitForExit(3000);
                    failures.Add($"kill {k}: writer did not report READY: {(p.HasExited && err.Wait(2000) ? err.Result.Trim() : "(running)")}");
                }
            }

            System.Threading.Thread.Sleep(early ? rnd.Next(20, 150) : rnd.Next(4) == 0 ? rnd.Next(0, 6) : rnd.Next(0, maxDelay + 1));
            if (!p.HasExited)
            {
                p.Kill();
            }

            p.WaitForExit();
            long acknowledged = LastLedger(ledger);
            acked += acknowledged - before;
            if ((File.Exists(db + "-journal") && new FileInfo(db + "-journal").Length > 0) || (File.Exists(db + "-wal") && new FileInfo(db + "-wal").Length > 0))
            {
                hot++;
            }

            string target = db;
            if (k % 2 == 0)
            {
                target = Path.Combine(chk, "copy.db");
                PinnedSqlite.CopyFileSet(db, target);
            }

            Workload.Verdict v;
            if (!rekey)
            {
                v = CipherShell.Check(target, spec, null, out _);
            }
            else
            {
                // The file must open under exactly one of rk{L} (the last acknowledged) and rk{L+1} (a rekey committed just before the kill).
                Workload.Verdict a1 = CipherShell.Check(target, spec, $"rk{acknowledged}", out bool wrong1);
                Workload.Verdict a2 = CipherShell.Check(target, spec, $"rk{acknowledged + 1}", out bool wrong2);
                if (wrong1 == wrong2)
                {
                    bothOrNeither++;
                    if (keepBad.Length != 0)
                    {
                        // Keep the first image that opens under neither key, with the ledger, and stop: the file set is the reproduction.
                        Directory.CreateDirectory(keepBad);
                        PinnedSqlite.CopyFileSet(db, Path.Combine(keepBad, "bad.db"));
                        File.Copy(ledger, Path.Combine(keepBad, "ledger.txt"), true);
                        failures.Add($"kill {k}: image kept in {keepBad}");
                        k = kills;
                        break;
                    }

                    failures.Add($"kill {k}: rk{acknowledged} {(wrong1 ? "and" : "and also")} rk{acknowledged + 1} {(wrong1 ? "both fail: file is not a database" : "both open")}");
                    v = new Workload.Verdict(30, []);
                }
                else
                {
                    v = wrong1 ? a2 : a1;
                    if (v.Violations.Count == 0 && v.LastTxn != 30)
                    {
                        v = new Workload.Verdict(-1, [$"data changed: counter {v.LastTxn}, expected 30"]);
                    }

                    acknowledged = 30;
                    seen = Math.Max(seen, 0);
                }
            }

            if (target != db)
            {
                PinnedSqlite.DeleteFileSet(target);
            }

            if (v.Violations.Count > 0)
            {
                bool isCorrupt = v.Violations.Any(x => x.StartsWith("integrity_check", StringComparison.Ordinal) || x.StartsWith("cannot open", StringComparison.Ordinal) || x.StartsWith("partial schema", StringComparison.Ordinal));
                if (isCorrupt)
                {
                    corrupt++;
                }
                else
                {
                    partial++;
                }

                failures.Add($"kill {k}: ledger {acknowledged}, found {v.LastTxn}: {string.Join("; ", v.Violations)}");
            }
            else if (v.LastTxn < Math.Max(acknowledged, seen))
            {
                lost++;
                failures.Add($"kill {k}: acknowledged {acknowledged} (earlier checks saw {seen}) but the file holds {v.LastTxn}");
            }
            else if (!rekey && v.LastTxn > Math.Max(acknowledged, seen) + 1)
            {
                phantom++;
                failures.Add($"kill {k}: the file holds {v.LastTxn}, more than one past the ledger's {acknowledged}");
            }

            if (v.Violations.Count == 0)
            {
                seen = Math.Max(seen, v.LastTxn);
            }

            if ((k + 1) % 100 == 0)
            {
                Console.WriteLine($"  {k + 1}/{kills} kills, {acked} acknowledged, failures {failures.Count}, {sw.Elapsed.TotalSeconds:F0}s");
            }
        }

        string label = (rekey ? "rekey" : "writer") + " " + string.Join(' ', words);
        Console.WriteLine($"{label}: kills={kills} acknowledged={acked} hot-at-kill={hot}: corruptions {corrupt}, lost {lost}, partial {partial}, beyond-ledger {phantom}, " +
                          $"not-exactly-one-key {bothOrNeither}, start failures {startFailures}");
        foreach (string f in failures.Take(20))
        {
            Console.WriteLine("  FAIL " + f);
        }

        if (jsonOut.Length != 0)
        {
            File.WriteAllText(jsonOut, JsonSerializer.Serialize(new { label, kills, seed, acked, hot, corrupt, lost, partial, phantom, bothOrNeither, startFailures, failures, seconds = sw.Elapsed.TotalSeconds }, JsonOptions) + "\n");
        }

        PinnedSqlite.DeleteFileSet(db);
        try
        {
            Directory.Delete(work, recursive: true);
        }
        catch (IOException)
        {
            // scratch
        }

        return corrupt + lost + partial + phantom + bothOrNeither + startFailures == 0 ? 0 : 1;
    }

    private static long LastLedger(string ledger)
    {
        if (!File.Exists(ledger))
        {
            return 0;
        }

        string[] lines = File.ReadAllText(ledger).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? 0 : long.Parse(lines[^1], CultureInfo.InvariantCulture);
    }
}
