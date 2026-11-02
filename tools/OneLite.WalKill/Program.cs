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
using RecursiveAI.OneLite.Storage.Wal;
using PagerClass = RecursiveAI.OneLite.Storage.Pager.Pager;

// onelite-walkill: live-coexistence and kill runs for WAL mode against the pinned `sqlite3` (ONELITE_SQLITE3).
//
// The database holds one table c(id, v, pad) of K rows. Each row is wide enough to fill its own leaf page, and v is the text 'V' plus eight digits.
// A transaction sets every v to the same next value, so a consistent snapshot shows K equal values, and the value counts the commits. Our side edits
// the eight digits in place on the pages (the pager has no SQL), the reference shell runs an UPDATE; both are then ordinary commits.
//
//   setup  --db DB [--ps N] [--rows K]
//   writer --db DB --ledger F --sync full|normal --autockpt N --cache N --seed S --ckpt-every M [--max N] [--delay-us U]   (child process)
//   reader --db DB --stop F --cache N --label L [--hold-ms M]                                                         (child process)
//   live   --dir DIR --scenario ours-write|shell-write|mixed|alternate --commits N [--ps N] [--rows K] [--sync full|normal]
//   kill   --dir DIR --kills N [--seed S] [--sync full|normal] [--ps N] [--rows K] [--cache N] [--autockpt N] [--max-delay-ms MS]
internal static partial class Program
{
    internal static string Sqlite3 => Environment.GetEnvironmentVariable("ONELITE_SQLITE3") ?? throw new InvalidOperationException("set ONELITE_SQLITE3 to the pinned sqlite3");

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                return 2;
            }

            Dictionary<string, string> o = Opts(args);
            return args[0] switch
            {
                "setup" => Setup(o),
                "writer" => Writer(o),
                "reader" => Reader(o),
                "live" => Live(o),
                "kill" => Kill(o),
                _ => 2,
            };
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or OneLiteException or FormatException or KeyNotFoundException)
        {
            Console.Error.WriteLine("error: " + e.Message + (e is OneLiteException ole ? $" (extended code {ole.ExtendedCode}, detail: {ole.Detail}) " + (e.StackTrace ?? "").Replace('\n', ' ')[..Math.Min(500, (e.StackTrace ?? "").Length)] : ""));
            return 1;
        }
    }

    internal static Dictionary<string, string> Opts(string[] a)
    {
        var d = new Dictionary<string, string>();
        for (int i = 1; i + 1 < a.Length; i += 2)
        {
            d[a[i].TrimStart('-')] = a[i + 1];
        }

        return d;
    }

    internal static int Int(Dictionary<string, string> o, string key, int dflt) => o.TryGetValue(key, out string? v) ? int.Parse(v, CultureInfo.InvariantCulture) : dflt;

    internal static string Str(Dictionary<string, string> o, string key, string dflt) => o.TryGetValue(key, out string? v) ? v : dflt;

    internal static (int Exit, string Output) Sql(string db, string sql, int busyMs = 5000)
    {
        var psi = new ProcessStartInfo(Sqlite3) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        psi.ArgumentList.Add(db);
        psi.ArgumentList.Add("-cmd");
        psi.ArgumentList.Add(".timeout " + busyMs);
        psi.ArgumentList.Add("-cmd");
        psi.ArgumentList.Add("pragma secure_delete=1;");
        using Process p = Process.Start(psi)!;
        p.StandardInput.Write(sql);
        p.StandardInput.Close();
        Task<string> err = p.StandardError.ReadToEndAsync();
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        // The secure_delete setting echoes its value as the first line of output.
        o = o.StartsWith("1\n", StringComparison.Ordinal) ? o[2..] : o;
        return (p.ExitCode, (o + err.Result).Trim());
    }

    internal const string UpdateSql = "update c set v = (select printf('V%08d', cast(substr(max(v), 2) as integer) + 1) from c);";

    private static int Setup(Dictionary<string, string> o)
    {
        MakeDb(o["db"], Int(o, "ps", 1024), Int(o, "rows", 40));
        return 0;
    }

    internal static void MakeDb(string db, int ps, int rows)
    {
        foreach (string s in new[] { "", "-wal", "-shm", "-journal" })
        {
            File.Delete(db + s);
        }

        (int exit, string output) = Sql(db, $"pragma page_size={ps}; pragma journal_mode=wal; create table c(id integer primary key, v text, pad blob); " +
            $"insert into c select value, 'V00000000', zeroblob({ps / 2}) from generate_series(1,{rows}); pragma wal_checkpoint(truncate); " +
            UpdateSql + " select count(distinct v), min(v) from c; pragma wal_checkpoint(truncate);");
        if (exit != 0 || !output.EndsWith("1|V00000001\n0|0|0", StringComparison.Ordinal) && !output.Contains("1|V00000001", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("could not build the database: " + output);
        }

        // Start from 'V00000000' with no log: the first UPDATE above proved the statement sets every row alike.
        (exit, output) = Sql(db, "update c set v = 'V00000000'; pragma wal_checkpoint(truncate);");
        if (exit != 0)
        {
            throw new InvalidOperationException(output);
        }
    }

    // ---- reading and editing the value markers on pages -----------------------------------------------------------------------------

    internal static int FindMarker(ReadOnlySpan<byte> page, int from)
    {
        for (int i = from; i + 9 <= page.Length; i++)
        {
            if (page[i] != (byte)'V')
            {
                continue;
            }

            bool ok = true;
            for (int j = 1; j <= 8 && ok; j++)
            {
                ok = page[i + j] is >= (byte)'0' and <= (byte)'9';
            }

            if (ok)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Reads every marker of the snapshot the pager holds: how many there are and the lowest and highest value.</summary>
    internal static (int Count, long Min, long Max) Scan(PagerClass p)
    {
        int count = 0;
        long min = long.MaxValue;
        long max = long.MinValue;
        for (uint n = 2; n <= p.PageCount; n++)
        {
            PageFrame f = p.GetSync(n);
            for (int at = FindMarker(f.Data, 0); at >= 0; at = FindMarker(f.Data, at + 9))
            {
                long v = long.Parse(Encoding.ASCII.GetString(f.Data, at + 1, 8), CultureInfo.InvariantCulture);
                count++;
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }

            p.Release(f);
        }

        return (count, min, max);
    }

    /// <summary>Inside a write transaction: set every marker to <paramref name="value"/>.</summary>
    internal static void SetAll(PagerClass p, long value)
    {
        byte[] text = Encoding.ASCII.GetBytes("V" + value.ToString("D8", CultureInfo.InvariantCulture));
        for (uint n = 2; n <= p.PageCount; n++)
        {
            PageFrame f = p.GetSync(n);
            int at = FindMarker(f.Data, 0);
            if (at >= 0)
            {
                p.MakeWritableSync(f);
                for (; at >= 0; at = FindMarker(f.Data, at + 9))
                {
                    text.CopyTo(f.Data, at);
                }
            }

            p.Release(f);
        }
    }

    internal static PagerOptions Opts(string sync, int cache, int autockpt, int ps) => new()
    {
        PageSize = ps,
        Synchronous = Enum.Parse<SynchronousMode>(sync, true),
        CacheSize = cache,
        WalAutoCheckpoint = autockpt,
        Busy = static n => n < 20000 ? 1 : -1,
    };

    // ---- the writer process ---------------------------------------------------------------------------------------------------------

    private static int Writer(Dictionary<string, string> o)
    {
        string db = o["db"];
        string ledger = Str(o, "ledger", "");
        int ckptEvery = Int(o, "ckpt-every", 0);
        int max = Int(o, "max", int.MaxValue);
        int delayUs = Int(o, "delay-us", 0);
        var rnd = new Random(Int(o, "seed", Environment.ProcessId));
        using PagerClass p = PagerClass.Open(new FileVfs(), db, Opts(Str(o, "sync", "full"), Int(o, "cache", 100), Int(o, "autockpt", 1000), Int(o, "ps", 1024)));
        Console.Out.WriteLine("READY");
        Console.Out.Flush();
        long done = 0;
        long busy = 0;
        long stale = 0;
        for (int i = 0; i < max; i++)
        {
            p.BeginReadSync();
            if (p.BeginWriteOrStatus() != PagerStatus.Ok)
            {
                OneLiteException e = p.Error!;
                p.EndReadSync();
                if ((e.ExtendedCode & 0xFF) != ResultCode.Busy)
                {
                    throw e;
                }

                if (e.ExtendedCode == ResultCode.BusySnapshot)
                {
                    stale++;
                }
                else
                {
                    busy++;
                }

                i--;
                continue;
            }

            (_, _, long cur) = Scan(p);
            SetAll(p, cur + 1);
            p.CommitSync();
            done++;
            if (ledger.Length > 0)
            {
                using var f = new FileStream(ledger, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                f.Write(Encoding.ASCII.GetBytes((cur + 1).ToString(CultureInfo.InvariantCulture) + "\n"));
            }

            // The auto-checkpoint runs here, after the commit was acknowledged.
            p.EndReadSync();

            if (ckptEvery > 0 && done % ckptEvery == 0)
            {
                p.CheckpointSync((CheckpointMode)rnd.Next(4));
            }

            if (delayUs > 0)
            {
                Thread.Sleep(TimeSpan.FromMicroseconds(delayUs));
            }
        }

        Console.Out.WriteLine($"WRITER commits={done} busy={busy} busy_snapshot={stale}");
        return 0;
    }

    // ---- the reader process ----------------------------------------------------------------------------------------------------------

    private static int Reader(Dictionary<string, string> o)
    {
        string stop = o["stop"];
        string label = Str(o, "label", "ours");
        int hold = Int(o, "hold-ms", 0);
        var rnd = new Random(Environment.ProcessId);
        using PagerClass p = PagerClass.Open(new FileVfs(), o["db"], Opts("full", Int(o, "cache", 100), 0, Int(o, "ps", 1024)));
        Console.Out.WriteLine("READY");
        Console.Out.Flush();
        long snaps = 0;
        long anomalies = 0;
        long last = -1;
        long first = -1;
        long busy = 0;
        while (!File.Exists(stop))
        {
            PagerStatus s = Drive(p, p.BeginRead);
            if (s != PagerStatus.Ok)
            {
                if ((p.Error!.ExtendedCode & 0xFF) == ResultCode.Busy)
                {
                    busy++;
                    continue;
                }

                throw p.Error;
            }

            (int count, long min, long max) = Scan(p);
            if (hold > 0 && rnd.Next(4) == 0)
            {
                // Keep the snapshot open while writers move on: checkpoints must stop at it, and pages must still read as of it.
                Thread.Sleep(rnd.Next(hold));
                (int c2, long mn2, long mx2) = Scan(p);
                if (c2 != count || mn2 != min || mx2 != max)
                {
                    anomalies++;
                    Console.Out.WriteLine($"ANOMALY {label}: the snapshot changed while held: {count}/{min}/{max} then {c2}/{mn2}/{mx2}");
                }
            }

            p.EndReadSync();
            snaps++;
            if (first < 0)
            {
                first = min;
            }

            if (count == 0 || min != max || min < last)
            {
                anomalies++;
                Console.Out.WriteLine($"ANOMALY {label}: count={count} min={min} max={max} previous={last}");
            }

            last = Math.Max(last, max);
        }

        Console.Out.WriteLine($"READER {label} snapshots={snaps} first={first} last={last} anomalies={anomalies} busy={busy}");
        return anomalies == 0 ? 0 : 3;
    }

    internal static PagerStatus Drive(PagerClass p, Func<PagerStatus> op)
    {
        PagerStatus s;
        while ((s = op()) == PagerStatus.Pending)
        {
            p.ExecuteSync();
        }

        return s;
    }
}

internal static class PagerExtensions
{
    /// <summary>BeginWrite driven to its end without throwing: Ok, or Error with <see cref="PagerClass.Error"/> set.</summary>
    public static PagerStatus BeginWriteOrStatus(this PagerClass p) => Program.Drive(p, p.BeginWrite);
}
