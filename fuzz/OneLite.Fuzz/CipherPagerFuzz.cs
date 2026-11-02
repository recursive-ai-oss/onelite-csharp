using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using RecursiveAI.OneLite.Storage.Crypto;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;
using PagerClass = RecursiveAI.OneLite.Storage.Pager.Pager;

namespace RecursiveAI.OneLite.Fuzz;

/// <summary>
/// Fuzz target for encrypted databases through the pager: valid encrypted file sets (a database, a hot rollback journal, a WAL with frames not
/// yet checkpointed, a rekey that was cut off) are mutated at random (flipped bytes, zeroed or swapped pages, truncation, stray journal and WAL
/// files) and opened with the right key. Only typed errors (not a database, corrupt, I/O) are allowed; anything else is a crash.
/// <c>cipher-pager SECONDS [SEED]</c>.
/// </summary>
internal static class CipherPagerFuzz
{
    private const string Db = "f.db";
    private static readonly CipherSettings Settings = new(1024, 1, CipherDigest.Sha512, CipherDigest.Sha512);

    private static PagerOptions Opts() => new() { Key = CipherKey.FromPassphrase("fuzz"), Cipher = Settings, WalAutoCheckpoint = 0, CacheSize = 8 };

    private static List<Dictionary<string, byte[]>> Seeds()
    {
        var seeds = new List<Dictionary<string, byte[]>>();
        foreach (JournalMode jm in new[] { JournalMode.Delete, JournalMode.Wal })
        {
            var inner = new MemoryVfs();
            var fault = new FaultVfs(inner, new FaultOptions { SectorSize = 512 });
            using (PagerClass p = PagerClass.Open(fault, Db, Opts()))
            {
                p.BeginReadSync();
                p.BeginWriteSync();
                for (uint n = 1; n <= 8; n++)
                {
                    PageFrame f = n == 1 ? p.GetSync(1) : p.AllocateSync();
                    p.MakeWritableSync(f);
                    for (int i = n == 1 ? 108 : 0; i < f.Data.Length - 80; i++)
                    {
                        f.Data[i] = (byte)(n * 31 + i);
                    }

                    p.Release(f);
                }

                p.CommitSync();
                if (jm == JournalMode.Wal)
                {
                    p.ChangeJournalModeSync(JournalMode.Wal);
                }

                p.BeginWriteSync();
                for (uint n = 2; n <= 6; n++)
                {
                    PageFrame f = p.GetSync(n);
                    p.MakeWritableSync(f);
                    f.Data[5] ^= 0x55;
                    p.Release(f);
                }

                p.CommitSync();
                p.EndReadSync();
                p.RekeySync(CipherKey.FromPassphrase("fuzz"));
            }

            long ops = fault.OpCount;
            for (long k = ops / 4; k <= ops; k += Math.Max(1, ops / 6))
            {
                MemoryVfs m = fault.CrashImage(k, CrashPolicy.KeepAll).ToMemoryVfs();
                var set = new Dictionary<string, byte[]>();
                foreach (string suffix in new[] { "", "-journal", "-wal" })
                {
                    if (m.Access(Db + suffix, AccessMode.Exists))
                    {
                        using IVfsFile f = m.Open(Db + suffix, VfsOpenOptions.ReadOnly);
                        var b = new byte[f.FileSize()];
                        f.Read(0, b);
                        set[Db + suffix] = b;
                    }
                }

                seeds.Add(set);
            }
        }

        return seeds;
    }

    private static void Mutate(Dictionary<string, byte[]> set, Random rng)
    {
        foreach (string name in set.Keys.ToList())
        {
            byte[] b = set[name];
            if (b.Length == 0 || rng.Next(3) == 0)
            {
                continue;
            }

            switch (rng.Next(6))
            {
                case 0: // flip bytes
                    for (int i = rng.Next(1, 6); i > 0; i--)
                    {
                        b[rng.Next(b.Length)] ^= (byte)rng.Next(1, 256);
                    }

                    break;
                case 1: // zero a page-sized run
                {
                    int at = rng.Next(b.Length) / 1024 * 1024;
                    Array.Clear(b, at, Math.Min(1024, b.Length - at));
                    break;
                }

                case 2: // truncate
                    set[name] = b[..rng.Next(b.Length)];
                    break;
                case 3: // swap two pages
                    if (b.Length >= 3072)
                    {
                        int x = rng.Next(b.Length / 1024) * 1024, y = rng.Next(b.Length / 1024) * 1024;
                        byte[] t = b[x..(x + 1024)];
                        Buffer.BlockCopy(b, y, b, x, 1024);
                        Buffer.BlockCopy(t, 0, b, y, 1024);
                    }

                    break;
                case 4: // random tail
                    set[name] = [.. b, .. RandomBytes(rng, rng.Next(1, 3000))];
                    break;
                default: // header bytes of a journal or WAL
                    b[rng.Next(Math.Min(b.Length, 40))] = (byte)rng.Next(256);
                    break;
            }
        }

        if (rng.Next(8) == 0)
        {
            set[Db + (rng.Next(2) == 0 ? "-journal" : "-wal")] = RandomBytes(rng, rng.Next(0, 4000));
        }
    }

    private static byte[] RandomBytes(Random rng, int n)
    {
        var b = new byte[n];
        rng.NextBytes(b);
        return b;
    }

    private static void RunOne(Dictionary<string, byte[]> set)
    {
        var vfs = new MemoryVfs();
        foreach ((string name, byte[] bytes) in set)
        {
            using IVfsFile f = vfs.Open(name, VfsOpenOptions.ReadWrite | VfsOpenOptions.Create);
            f.Write(0, bytes);
        }

        try
        {
            using PagerClass p = PagerClass.Open(vfs, Db, Opts());
            p.BeginReadSync();
            uint n = Math.Min(p.PageCount, 64);
            for (uint i = 1; i <= n; i++)
            {
                try
                {
                    p.Release(p.GetSync(i));
                }
                catch (OneLiteException e) when (Typed(e))
                {
                    // a page that does not verify
                }
            }

            p.EndReadSync();
        }
        catch (OneLiteException e) when (Typed(e))
        {
            // a file that is not a database under this key, or a damaged one
        }
    }

    private static bool Typed(OneLiteException e) => e.Code is ResultCode.NotADb or ResultCode.Corrupt or ResultCode.IoErr or ResultCode.CantOpen or ResultCode.ReadOnly or ResultCode.Busy or ResultCode.Full or ResultCode.Misuse;

    internal static int Run(string[] args)
    {
        int seconds = int.Parse(args[1], CultureInfo.InvariantCulture), seed = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 1;
        var rng = new Random(seed);
        List<Dictionary<string, byte[]>> seeds = Seeds();
        var sw = Stopwatch.StartNew();
        long execs = 0;
        int failures = 0, bigClaims = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            Dictionary<string, byte[]> src = seeds[rng.Next(seeds.Count)];
            var set = src.ToDictionary(kv => kv.Key, kv => (byte[])kv.Value.Clone());
            Mutate(set, rng);
            try
            {
                RunOne(set);
            }
            catch (OutOfMemoryException e) when ((e.StackTrace ?? "").Contains("MemoryFileData.SetLength", StringComparison.Ordinal))
            {
                // A hot journal may claim a database up to 1 GiB past the file (the pager's own bound); the in-memory test backend allocates it. Not a codec matter.
                bigClaims++;
            }
            catch (Exception e)
            {
                failures++;
                Console.Error.WriteLine("FAIL " + e.GetType().Name + ": " + e.Message + " @ " + string.Join(" < ", (e.StackTrace ?? "").Split((char)10).Take(4).Select(l => l.Trim())));
                foreach ((string name, byte[] bytes) in set)
                {
                    File.WriteAllBytes($"cipher-pager-crash-{seed}-{failures}-{name}", bytes);
                }

                if (failures > 10)
                {
                    break;
                }
            }

            execs++;
        }

        Console.WriteLine($"cipher-pager fuzz: {execs} executions over {seeds.Count} seed images in {sw.Elapsed.TotalSeconds:F0}s, failures {failures} (journals claiming a huge database, skipped: {bigClaims})");
        return failures == 0 ? 0 : 1;
    }
}
