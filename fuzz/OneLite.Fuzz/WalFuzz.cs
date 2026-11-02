using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;
using RecursiveAI.OneLite.Storage.Wal;
using PagerClass = RecursiveAI.OneLite.Storage.Pager.Pager;

namespace RecursiveAI.OneLite.Fuzz;

/// <summary>
/// WAL fuzzing. The input is a wal-index region plus a -wal file (u16 little-endian length of the region bytes, the region bytes, the rest is
/// the log) next to a fixed valid database in WAL mode. The pager opens it, reads pages, writes and commits a page, and checkpoints. Hostile
/// bytes must end in a typed <see cref="OneLiteException"/> or in success: never another exception, a hang or a huge allocation.
/// <c>wal SECONDS [SEED]</c> runs a mutational loop over generated seeds (hosts without clang); <c>wal-libfuzzer</c> and <c>wal FILE</c> run one input.
/// </summary>
internal static class WalFuzz
{
    private static byte[]? _db;

    private static void Allow(OneLiteException e)
    {
        if (e.Code is not (ResultCode.Corrupt or ResultCode.NotADb or ResultCode.CantOpen or ResultCode.IoErr or ResultCode.ReadOnly or ResultCode.Full or ResultCode.Busy))
        {
            throw new InvalidOperationException($"unexpected result code {e.ExtendedCode}: {e.Message} {e.Detail}", e);
        }
    }

    private static PagerOptions Opts() => new() { PageSize = 512, CacheSize = 20, Synchronous = SynchronousMode.Off, WalAutoCheckpoint = 0 };

    internal static int Run(string[] args)
    {
        if (args[0] == "wal-libfuzzer")
        {
            SharpFuzz.Fuzzer.LibFuzzer.Run(span => Target(span.ToArray()));
            return 0;
        }

        if (args.Length >= 2 && int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int seconds))
        {
            return Mutate(seconds, args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 1);
        }

        Target(File.ReadAllBytes(args[1]));
        return 0;
    }

    private static void Put(MemoryVfs vfs, string path, byte[] bytes)
    {
        using IVfsFile f = vfs.Open(path, VfsOpenOptions.ReadWrite | VfsOpenOptions.Create);
        f.Write(0, bytes);
    }

    /// <summary>A database of a few pages in WAL mode, closed cleanly (so there is no -wal or -shm).</summary>
    private static byte[] Db()
    {
        if (_db != null)
        {
            return _db;
        }

        var vfs = new MemoryVfs();
        using (PagerClass p = PagerClass.Open(vfs, "f.db", Opts()))
        {
            p.BeginReadSync();
            p.BeginWriteSync();
            for (int i = 0; i < 8; i++)
            {
                PageFrame f = i == 0 ? p.GetSync(1) : p.AllocateSync();
                p.MakeWritableSync(f);
                for (int k = i == 0 ? 108 : 0; k < f.Data.Length; k++)
                {
                    f.Data[k] = (byte)(k * 5 + i);
                }

                p.Release(f);
            }

            p.CommitSync();
            p.ChangeJournalModeSync(JournalMode.Wal);
            p.EndReadSync();
        }

        return _db = ReadAll(vfs, "f.db");
    }

    private static byte[] ReadAll(MemoryVfs vfs, string path)
    {
        using IVfsFile f = vfs.Open(path, VfsOpenOptions.ReadOnly);
        var b = new byte[f.FileSize()];
        _ = f.Read(0, b);
        return b;
    }

    public static void Target(byte[] input)
    {
        if (input.Length < 2)
        {
            return;
        }

        int shmLen = input[0] | input[1] << 8;
        shmLen = Math.Min(shmLen, input.Length - 2);
        var vfs = new MemoryVfs();
        Put(vfs, "f.db", Db());
        if (input.Length - 2 - shmLen > 0)
        {
            Put(vfs, "f.db-wal", input.AsSpan(2 + shmLen).ToArray());
        }

        // This handle stays open for the whole run: the in-memory wal-index lives while any handle uses it, and the pager must find these bytes.
        using IVfsFile d = vfs.Open("f.db", VfsOpenOptions.ReadWrite | VfsOpenOptions.MainDb);
        if (shmLen > 0 && d.Shm is { } shm)
        {
            ShmRegion r = shm.Map(0, true);
            input.AsSpan(2, Math.Min(shmLen, r.Memory.Length)).CopyTo(r.Memory.Span);
        }

        try
        {
            using PagerClass p = PagerClass.Open(vfs, "f.db", Opts());
            p.BeginReadSync();
            uint n = Math.Min(p.PageCount, 24u);
            for (uint i = 1; i <= n; i++)
            {
                try
                {
                    p.Release(p.GetSync(i));
                }
                catch (OneLiteException e)
                {
                    Allow(e);
                }
            }

            try
            {
                p.EndReadSync();
                p.BeginReadSync();
                p.BeginWriteSync();
                PageFrame f = p.GetSync(2);
                p.MakeWritableSync(f);
                f.Data[0] ^= 1;
                p.Release(f);
                p.CommitSync();
                p.EndReadSync();
                _ = p.CheckpointSync(CheckpointMode.Passive);
            }
            catch (OneLiteException e)
            {
                Allow(e);
            }
        }
        catch (OneLiteException e)
        {
            Allow(e);
        }
    }

    private static byte[][] Seeds()
    {
        var vfs = new MemoryVfs();
        Put(vfs, "f.db", Db());
        var seeds = new List<byte[]>();
        using (PagerClass p = PagerClass.Open(vfs, "f.db", Opts()))
        {
            p.BeginReadSync();
            for (int gen = 1; gen <= 3; gen++)
            {
                p.BeginWriteSync();
                for (uint i = 2; i <= (uint)(2 + gen * 2); i++)
                {
                    PageFrame f = p.GetSync(i);
                    p.MakeWritableSync(f);
                    f.Data[3] = (byte)gen;
                    p.Release(f);
                }

                p.CommitSync();
                if (gen == 2)
                {
                    // A second seed with a restarted log: new salts after a full checkpoint.
                    seeds.Add(Snapshot(vfs, shmBytes: 512));
                    p.EndReadSync();
                    _ = p.CheckpointSync(CheckpointMode.Restart);
                    p.BeginReadSync();
                }
            }

            seeds.Add(Snapshot(vfs, shmBytes: 512));
            p.EndReadSync();
        }

        return [.. seeds];
    }

    // The first 512 bytes of the wal-index hold both header copies, the checkpoint info and the lock-slot area; the page-number array and hash
    // slots follow, and the frames of a small log only touch their first few bytes, so a short prefix of the region is what is worth mutating.
    private static byte[] Snapshot(MemoryVfs vfs, int shmBytes)
    {
        byte[] wal = vfs.Access("f.db-wal", AccessMode.Exists) ? ReadAll(vfs, "f.db-wal") : [];
        var shm = new byte[shmBytes];
        using (IVfsFile d = vfs.Open("f.db", VfsOpenOptions.ReadWrite | VfsOpenOptions.MainDb))
        {
            if (d.Shm is { } s)
            {
                s.Map(0, false).Memory.Span[..shmBytes].CopyTo(shm);
            }
        }

        var all = new byte[2 + shm.Length + wal.Length];
        all[0] = (byte)shm.Length;
        all[1] = (byte)(shm.Length >> 8);
        shm.CopyTo(all, 2);
        wal.CopyTo(all, 2 + shm.Length);
        return all;
    }

    private static int Mutate(int seconds, int seed)
    {
        var rnd = new Random(seed);
        byte[][] seeds = Seeds();
        foreach (byte[] sd in seeds)
        {
            int shmLen = sd[0] | sd[1] << 8;
            Console.WriteLine($"seed: {sd.Length} bytes, wal-index prefix {shmLen} bytes ({sd.AsSpan(2, shmLen).Count((byte)0)} zero), log {sd.Length - 2 - shmLen} bytes");
        }

        var clock = Stopwatch.StartNew();
        long runs = 0, typed = 0, worst = 0;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            byte[] input = (byte[])seeds[rnd.Next(seeds.Length)].Clone();
            int edits = 1 + rnd.Next(5);
            for (int e = 0; e < edits && input.Length > 2; e++)
            {
                // Edits land in the wal-index prefix, in frame and log headers, or anywhere.
                int shm = input[0] | input[1] << 8;
                int at = rnd.Next(3) switch
                {
                    0 => 2 + rnd.Next(Math.Max(1, Math.Min(shm, 140))),
                    1 => 2 + shm + rnd.Next(Math.Max(1, Math.Min(32 + 24 * 3, input.Length - 2 - shm))),
                    _ => 2 + rnd.Next(input.Length - 2),
                };
                at = Math.Min(at, input.Length - 1);
                switch (rnd.Next(6))
                {
                    case 0:
                        input[at] = (byte)rnd.Next(256);
                        break;
                    case 1:
                        input[at] ^= (byte)(1 << rnd.Next(8));
                        break;
                    case 2:
                        for (int i = 0; i < 4 && at + i < input.Length; i++)
                        {
                            input[at + i] = rnd.Next(2) == 0 ? (byte)0xFF : (byte)0;
                        }

                        break;
                    case 3:
                        Array.Resize(ref input, Math.Max(3, input.Length - rnd.Next(Math.Min(input.Length - 2, 700))));
                        break;
                    case 4:
                    {
                        uint v = rnd.Next(3) switch { 0 => 0u, 1 => 0xFFFFFFFFu, _ => (uint)rnd.Next() };
                        for (int i = 0; i < 4 && at + i < input.Length; i++)
                        {
                            input[at + i] = (byte)(v >> (8 * i));
                        }

                        break;
                    }

                    default:
                        input[at] = input[rnd.Next(2, input.Length)];
                        break;
                }
            }

            long t0 = Stopwatch.GetTimestamp();
            try
            {
                Target(input);
            }
            catch (OneLiteException)
            {
                typed++;
            }
            catch (Exception e)
            {
                string dir = Environment.GetEnvironmentVariable("ONELITE_FUZZ_OUT") ?? ".";
                string file = Path.Combine(dir, $"crash-wal-{seed}-{runs}.bin");
                File.WriteAllBytes(file, input);
                Console.WriteLine($"wal: unexpected {e.GetType().Name} after {runs} executions: {e.Message}\ninput saved to {file}\n{e.StackTrace}");
                return 2;
            }

            worst = Math.Max(worst, Stopwatch.GetTimestamp() - t0);
            runs++;
        }

        Console.WriteLine($"wal: {runs} executions in {seconds}s, {typed} typed errors escaping, slowest {worst * 1000.0 / Stopwatch.Frequency:F1} ms, no unexpected exception");
        return 0;
    }
}
