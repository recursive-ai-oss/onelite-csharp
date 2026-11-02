using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using RecursiveAI.OneLite;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;
using PagerClass = RecursiveAI.OneLite.Storage.Pager.Pager;

// onelite-pagerkill: page-level kill and coexistence runs for the pager against the pinned `sqlite3` (ONELITE_SQLITE3).
//
// Scheme. A real database A is copied; B is A after an in-place SQL update that changes values but no lengths, so A and B are
// both valid databases of the same size. One transaction of the writer rewrites every page that differs between A and B (plus a few
// no-op page rewrites) so the file flips A <-> B; transaction i leaves B when i is odd and A when i is even. The pager stamps the
// change counter itself, so the counter minus its starting value is the number of transactions the file holds. After a kill
// the file must be exactly A or B (ignoring the counter fields of page 1), the transaction count must be the ledger's last line L
// or L + 1 (a commit can return just before the ledger line is written), and sqlite3's integrity_check must say ok.
//
//   kill  --src DB --sql SQL --work DIR [--kills N] [--seed S] [--mode delete|truncate|persist] [--sync full|normal] [--cache N] [--max-delay-ms MS]
//   crashimages --src DB --sql SQL --work DIR [--cache N] [--modes delete,truncate,persist] [--syncs full,normal]
//       every crash point of one A -> B transaction on the fault-injecting VFS, under several loss policies, in every journal mode and
//       synchronous FULL/NORMAL: each crash image is written to disk and recovered by sqlite3, which must pass integrity_check and hold A or B.
//   live  --src DB --sql SQL --other-sql SQL --probe SQL --work DIR [--rounds N]
//   writer DB A B LEDGER MODE SYNC CACHE   (internal)
internal static class Program
{
    private static string Sqlite3 => Environment.GetEnvironmentVariable("ONELITE_SQLITE3") ?? throw new InvalidOperationException("set ONELITE_SQLITE3 to the pinned sqlite3");

    private static int Main(string[] args)
    {
        try
        {
            return args.Length == 0 ? 2 : args[0] switch
            {
                "kill" => Kill(Opts(args)),
                "live" => Live(Opts(args)),
                "crashimages" => CrashImages(Opts(args)),
                "writer" => Writer(args),
                _ => 2,
            };
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or OneLiteException)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 1;
        }
    }

    private static Dictionary<string, string> Opts(string[] a)
    {
        var d = new Dictionary<string, string>();
        for (int i = 1; i + 1 < a.Length; i += 2)
        {
            d[a[i].TrimStart('-')] = a[i + 1];
        }

        return d;
    }

    private static string Run(string db, string sql, int busyMs = 0)
    {
        var psi = new ProcessStartInfo(Sqlite3) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        psi.ArgumentList.Add(db);
        psi.ArgumentList.Add("-cmd");
        psi.ArgumentList.Add(".timeout " + busyMs);
        using Process p = Process.Start(psi)!;
        p.StandardInput.Write(sql);
        p.StandardInput.Close();
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return o.Trim();
    }

    // ---- state images ------------------------------------------------------------------------------------------------------------

    private sealed class Images
    {
        public byte[] A = [];
        public byte[] B = [];
        public int PageSize;
        public List<int> Diff = [];
        public uint Base;

        public byte[] Of(long txCount) => (txCount & 1) == 1 ? B : A;
    }

    private static Images Load(string a, string b)
    {
        var im = new Images { A = File.ReadAllBytes(a), B = File.ReadAllBytes(b) };
        im.PageSize = DatabaseHeader.Parse(im.A).PageSize;
        if (im.A.Length != im.B.Length)
        {
            throw new InvalidOperationException("the update changed the database size; choose an update that keeps lengths");
        }

        for (int p = 1; p <= im.A.Length / im.PageSize; p++)
        {
            ReadOnlySpan<byte> x = im.A.AsSpan((p - 1) * im.PageSize, im.PageSize);
            ReadOnlySpan<byte> y = im.B.AsSpan((p - 1) * im.PageSize, im.PageSize);
            if (p == 1)
            {
                // the counter fields differ between A and B by construction
                byte[] x1 = Normalize(im.A, im.PageSize);
                byte[] y1 = Normalize(im.B, im.PageSize);
                if (!x1.AsSpan(0, im.PageSize).SequenceEqual(y1.AsSpan(0, im.PageSize)))
                {
                    throw new InvalidOperationException("page 1 differs beyond its counters; choose another update");
                }

                continue;
            }

            if (!x.SequenceEqual(y))
            {
                im.Diff.Add(p);
            }
        }

        im.Base = DatabaseHeader.GetUInt32(im.A, DatabaseHeader.ChangeCounterOffset);
        return im;
    }

    private static byte[] Normalize(byte[] db, int pageSize)
    {
        byte[] c = (byte[])db.Clone();
        Array.Clear(c, DatabaseHeader.ChangeCounterOffset, 4);
        Array.Clear(c, DatabaseHeader.VersionValidForOffset, 8); // version-valid-for and the library version number
        _ = pageSize;
        return c;
    }

    private static string Prepare(string src, string work, string sql, out string a, out string b)
    {
        Directory.CreateDirectory(work);
        a = Path.Combine(work, "a.db");
        b = Path.Combine(work, "b.db");
        File.Copy(src, a, true);
        File.Delete(a + "-journal");
        File.Copy(a, b, true);
        string o = Run(b, "pragma journal_mode=delete;\n" + sql);
        return o;
    }

    // ---- the writer process --------------------------------------------------------------------------------------------------------

    private static PagerOptions Options(string mode, string sync, int cache) => new()
    {
        PageSize = 4096,
        JournalMode = Enum.Parse<JournalMode>(mode, true),
        Synchronous = Enum.Parse<SynchronousMode>(sync, true),
        CacheSize = cache,
        Busy = static n => n < 5000 ? 1 : -1,
    };

    private static int Writer(string[] a)
    {
        string db = a[1], ledger = a[4];
        Images im = Load(a[2], a[3]);
        var rnd = new Random(Environment.ProcessId);
        using PagerClass p = PagerClass.Open(new FileVfs(), db, Options(a[5], a[6], int.Parse(a[7], CultureInfo.InvariantCulture)));
        p.BeginReadSync();
        PageFrame p1 = p.GetSync(1);
        long done = DatabaseHeader.GetUInt32(p1.Data, DatabaseHeader.ChangeCounterOffset) - (long)im.Base;
        p.Release(p1);
        p.EndReadSync();
        if (done > 0)
        {
            // A predecessor killed between its commit and its ledger line leaves the ledger one behind; what this process just read is committed.
            using var f = new FileStream(ledger, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            f.Write(Encoding.ASCII.GetBytes(done.ToString(CultureInfo.InvariantCulture) + "\n"));
        }

        Console.Out.WriteLine("READY");
        Console.Out.Flush();
        for (; ; )
        {
            long i = done + 1;
            FlipTransaction(p, im, i, rnd);
            using (var f = new FileStream(ledger, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                f.Write(Encoding.ASCII.GetBytes(i.ToString(CultureInfo.InvariantCulture) + "\n"));
            }

            done = i;
        }
    }

    private static void FlipTransaction(PagerClass p, Images im, long i, Random rnd)
    {
        byte[] target = im.Of(i);
        p.BeginReadSync();
        p.BeginWriteSync();
        foreach (int pg in im.Diff.Concat(Enumerable.Range(0, 12).Select(_ => 2 + rnd.Next(Math.Max(1, im.A.Length / im.PageSize - 1)))).Distinct().Order())
        {
            PageFrame f = p.GetSync((uint)pg);
            p.MakeWritableSync(f);
            if (im.Diff.Contains(pg))
            {
                Buffer.BlockCopy(target, (pg - 1) * im.PageSize, f.Data, 0, im.PageSize);
            }

            p.Release(f);
        }

        p.CommitSync();
        p.EndReadSync();
    }

    // ---- kill campaign ---------------------------------------------------------------------------------------------------------------

    private static int Kill(Dictionary<string, string> o)
    {
        string work = o["work"];
        string setup = Prepare(o["src"], work, o["sql"], out string a, out string b);
        Images im = Load(a, b);
        string baseOk = Run(a, "pragma integrity_check;") + "/" + Run(b, "pragma integrity_check;");
        Console.WriteLine($"images: {im.Diff.Count} differing pages of {im.A.Length / im.PageSize}, integrity {baseOk} {setup}");
        int kills = int.Parse(o.GetValueOrDefault("kills", "100"), CultureInfo.InvariantCulture);
        int seed = int.Parse(o.GetValueOrDefault("seed", "1"), CultureInfo.InvariantCulture);
        string mode = o.GetValueOrDefault("mode", "delete"), sync = o.GetValueOrDefault("sync", "full"), cache = o.GetValueOrDefault("cache", "2000");
        int maxDelay = int.Parse(o.GetValueOrDefault("max-delay-ms", "250"), CultureInfo.InvariantCulture);
        var rnd = new Random(seed);
        string db = Path.Combine(work, "db"), ledger = Path.Combine(work, "ledger");
        int bad = 0, maxTx = 0, hot = 0;
        string self = Environment.ProcessPath!;
        string dll = typeof(Program).Assembly.Location;
        for (int k = 0; k < kills; k++)
        {
            if (k % 25 == 0)
            {
                File.Copy(a, db, true);
                File.Delete(db + "-journal");
                File.Delete(ledger);
            }

            var psi = new ProcessStartInfo(self) { RedirectStandardOutput = true, UseShellExecute = false };
            if (Path.GetFileNameWithoutExtension(self) == "dotnet")
            {
                psi.ArgumentList.Add(dll);
            }

            foreach (string s in new[] { "writer", db, a, b, ledger, mode, sync, cache })
            {
                psi.ArgumentList.Add(s);
            }

            using Process child = Process.Start(psi)!;
            bool startupKill = rnd.Next(10) == 0;
            if (!startupKill)
            {
                string? line = child.StandardOutput.ReadLine();
                if (line != "READY")
                {
                    throw new InvalidOperationException("writer did not start: " + line);
                }
            }

            Thread.Sleep(rnd.Next(maxDelay + 1));
            child.Kill(entireProcessTree: true);
            child.WaitForExit();
            if (File.Exists(db + "-journal") && new FileInfo(db + "-journal").Length > 0)
            {
                hot++;
            }

            long l = File.Exists(ledger) ? File.ReadLines(ledger).Select(static x => long.Parse(x, CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max() : 0;
            string how = (k % 3) switch { 0 => "sqlite3 in place", 1 => "sqlite3 on a copy", _ => "our pager in place" };
            string? err = Check(db, im, l, how, work, mode, sync);
            if (err != null)
            {
                bad++;
                Console.WriteLine($"KILL {k} FAILED ({how}): {err}");
                File.Copy(db, Path.Combine(work, $"failed-{k}.db"), true);
                File.Copy(a, db, true);
                File.Delete(ledger);
            }

            maxTx = (int)Math.Max(maxTx, l);
        }

        Console.WriteLine($"mode={mode} sync={sync} cache={cache}: {kills} kills, {bad} failures, {hot} kills that left a non-empty journal, highest committed transaction {maxTx}");
        return bad == 0 ? 0 : 1;
    }

    private static string? Check(string db, Images im, long ledger, string how, string work, string mode, string sync)
    {
        string target = db;
        if (how == "sqlite3 on a copy")
        {
            target = Path.Combine(work, "copy");
            File.Copy(db, target, true);
            File.Delete(target + "-journal");
            if (File.Exists(db + "-journal"))
            {
                File.Copy(db + "-journal", target + "-journal", true);
            }
        }

        if (how == "our pager in place")
        {
            using PagerClass p = PagerClass.Open(new FileVfs(), db, Options(mode, sync, 100));
            p.BeginReadSync();
            p.EndReadSync();
        }

        string ic = Run(target, "pragma integrity_check;");
        if (ic != "ok")
        {
            return "integrity_check: " + ic;
        }

        byte[] now = File.ReadAllBytes(target);
        long m = DatabaseHeader.GetUInt32(now, DatabaseHeader.ChangeCounterOffset) - (long)im.Base;
        if (m < ledger || m > ledger + 1)
        {
            return $"database holds {m} transactions, ledger says {ledger}" + (m < ledger ? " (lost acknowledged commit)" : " (commit beyond ledger)");
        }

        if (!Normalize(now, im.PageSize).AsSpan().SequenceEqual(Normalize(im.Of(m), im.PageSize)))
        {
            return "page contents are not image " + ((m & 1) == 1 ? "B" : "A");
        }

        return null;
    }

    // ---- crash images recovered by sqlite3 ------------------------------------------------------------------------------------------

    private static int CrashImages(Dictionary<string, string> o)
    {
        string work = o["work"];
        Prepare(o["src"], work, o["sql"], out string a, out string b);
        Images im = Load(a, b);
        int cache = int.Parse(o.GetValueOrDefault("cache", "2000"), CultureInfo.InvariantCulture);
        var policies = new List<CrashPolicy> { CrashPolicy.DropUnsynced, CrashPolicy.KeepAll };
        for (ulong seed = 1; seed <= 3; seed++)
        {
            policies.Add(new CrashPolicy { Seed = seed, Reorder = true });
            policies.Add(new CrashPolicy { Seed = seed, Reorder = false });
        }

        policies.Add(new CrashPolicy { Seed = 9, TearSectors = true, TornFill = TornFill.Random });
        string dir = Path.Combine(work, "ci");
        int total = 0, bad = 0, gotA = 0, gotB = 0, lostAck = 0;
        string[] modes = o.GetValueOrDefault("modes", "delete,truncate,persist").Split(',');
        string[] syncs = o.GetValueOrDefault("syncs", "full,normal").Split(',');
        foreach (string mode in modes)
        {
            foreach (string sync in syncs)
            {
                var inner = new MemoryVfs();
                inner.Import("db", im.A);
                var fault = new FaultVfs(inner, new FaultOptions { SectorSize = 512 });
                long ack;
                using (PagerClass p = PagerClass.Open(fault, "db", Options(mode, sync, cache)))
                {
                    FlipTransaction(p, im, 1, new Random(1));
                    ack = fault.OpCount;
                }

                long points = fault.OpCount;
                int cfgTotal = 0, cfgBad = 0;
                for (long k = 0; k <= points; k++)
                {
                    foreach (CrashPolicy policy in policies)
                    {
                        Directory.CreateDirectory(dir);
                        foreach (string f in Directory.GetFiles(dir))
                        {
                            File.Delete(f);
                        }

                        CrashImage img = fault.CrashImage(k, policy);
                        foreach (KeyValuePair<string, byte[]> f in img.Files)
                        {
                            File.WriteAllBytes(Path.Combine(dir, Path.GetFileName(f.Key)), f.Value);
                        }

                        string path = Path.Combine(dir, "db");
                        string ic = Run(path, "pragma integrity_check;");
                        byte[] now = File.ReadAllBytes(path);
                        long m = DatabaseHeader.GetUInt32(now, DatabaseHeader.ChangeCounterOffset) - (long)im.Base;
                        bool isA = Normalize(now, im.PageSize).AsSpan().SequenceEqual(Normalize(im.A, im.PageSize));
                        bool isB = Normalize(now, im.PageSize).AsSpan().SequenceEqual(Normalize(im.B, im.PageSize));
                        string? err = null;
                        if (ic != "ok")
                        {
                            err = "integrity_check: " + ic;
                        }
                        else if (!isA && !isB)
                        {
                            err = "pages are neither image A nor image B";
                        }
                        else if (sync == "full" && k >= ack && policy.Mode == CrashMode.DropUnsynced && !isB)
                        {
                            err = "acknowledged commit lost";
                            lostAck++;
                        }

                        _ = m;
                        cfgTotal++;
                        gotA += isA ? 1 : 0;
                        gotB += isB ? 1 : 0;
                        if (err != null)
                        {
                            cfgBad++;
                            Console.WriteLine($"FAILED {mode}/{sync} cache {cache} crash after {k} of {points} ops, policy {policy.Mode} seed {policy.Seed} reorder {policy.Reorder} tear {policy.TearSectors}: {err}");
                        }
                    }
                }

                Console.WriteLine($"{mode}/{sync} cache {cache}: {points + 1} crash points x {policies.Count} policies = {cfgTotal} images recovered by sqlite3, {cfgBad} failures");
                total += cfgTotal;
                bad += cfgBad;
            }
        }

        Console.WriteLine($"crash images: {total} recovered by sqlite3, {bad} failures ({lostAck} lost acknowledged commits); recovered to image A {gotA}, image B {gotB}");
        return bad == 0 ? 0 : 1;
    }

    // ---- coexistence with live sqlite3 processes ----------------------------------------------------------------------------------

    private static int Live(Dictionary<string, string> o)
    {
        string work = o["work"];
        Prepare(o["src"], work, o["sql"], out string a, out string b);
        Images im = Load(a, b);
        string db = Path.Combine(work, "db");
        File.Copy(a, db, true);
        string probe = o["probe"];
        string pa = Run(a, probe + ";"), pb = Run(b, probe + ";");
        if (pa == pb)
        {
            throw new InvalidOperationException("the probe query cannot tell the two images apart");
        }

        // Phase 1: a sqlite3 reader loops SELECTs while our pager commits flips.
        int rounds = int.Parse(o.GetValueOrDefault("rounds", "300"), CultureInfo.InvariantCulture);
        // A new sqlite3 process per probe leaves the writer gaps (a reader that never lets go would starve it, as the docs describe).
        var results = new List<string>();
        bool writing = true;
        bool noReader = o.GetValueOrDefault("noreader", "0") == "1";
        Task readerTask = Task.Run(() =>
        {
            while (!noReader && Volatile.Read(ref writing))
            {
                results.Add(Run(db, probe + ";", busyMs: 0));
                Thread.Sleep(2);
            }
        });
        var rnd = new Random(3);
        var clock = Stopwatch.StartNew();
        using (PagerClass p = PagerClass.Open(new FileVfs(), db, Options("delete", "full", 100)))
        {
            for (long i = 1; i <= rounds; i++)
            {
                FlipTransaction(p, im, i, rnd);
            }
        }

        Console.WriteLine($"phase 1: {rounds} commits in {clock.Elapsed.TotalSeconds:F1}s ({clock.Elapsed.TotalMilliseconds / rounds:F1} ms each){(noReader ? ", no reader" : string.Empty)}");

        Volatile.Write(ref writing, false);
        readerTask.Wait();
        int torn = results.Count(r => r != pa && r != pb && !r.Contains("database is locked", StringComparison.Ordinal));
        int busy = results.Count(static r => r.Contains("database is locked", StringComparison.Ordinal));
        Console.WriteLine($"probe A={pa} B={pb}");
        Console.WriteLine($"reader: {results.Count} probes ({results.Count(r => r == pa)} image A, {results.Count(r => r == pb)} image B), {torn} torn, {busy} 'database is locked' errors");
        string ic = Run(db, "pragma integrity_check;");
        Console.WriteLine("integrity_check after phase 1: " + ic);

        // Phase 2: a sqlite3 writer (another table) and our pager alternate commits on one file. The pager object stays open throughout.
        string other = o["other-sql"];
        int alt = int.Parse(o.GetValueOrDefault("alternations", "60"), CultureInfo.InvariantCulture);
        File.Copy(a, db, true);
        string otherProbe = o.GetValueOrDefault("other-probe", probe);
        string o0 = Run(db, otherProbe + ";");
        long ours = 0, theirs = 0;
        using (PagerClass p = PagerClass.Open(new FileVfs(), db, Options("delete", "full", 100)))
        {
            for (int i = 0; i < alt; i++)
            {
                ours++;
                FlipTransaction(p, im, ours, rnd);
                string r = Run(db, other + ";", busyMs: 5000);
                if (r.Length != 0)
                {
                    Console.WriteLine("sqlite3 writer said: " + r);
                }

                theirs++;
            }
        }

        string ic2 = Run(db, "pragma integrity_check;");
        byte[] fin = File.ReadAllBytes(db);
        bool mine = Enumerable.Range(0, im.Diff.Count).All(k => fin.AsSpan((im.Diff[k] - 1) * im.PageSize, im.PageSize).SequenceEqual(im.Of(ours).AsSpan((im.Diff[k] - 1) * im.PageSize, im.PageSize)));
        string o1 = Run(db, otherProbe + ";");
        Console.WriteLine($"alternating: {ours} commits by the pager, {theirs} by sqlite3; integrity_check {ic2}; pager's pages hold image {((ours & 1) == 1 ? "B" : "A")}: {mine}; sqlite3's table {(((theirs & 1) == 0) == (o0 == o1) ? "in the expected state" : "NOT in the expected state")}");
        return torn == 0 && ic == "ok" && ic2 == "ok" && mine ? 0 : 1;
    }
}
