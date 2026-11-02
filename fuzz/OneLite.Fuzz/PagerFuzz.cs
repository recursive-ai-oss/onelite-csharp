using System;
using System.Diagnostics;
using System.IO;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;
using PagerClass = RecursiveAI.OneLite.Storage.Pager.Pager;

namespace RecursiveAI.OneLite.Fuzz;

/// <summary>
/// The pager fuzz commands. Hostile input must end in a typed <see cref="OneLiteException"/> (corrupt, not a database, can't open,
/// I/O, read-only) or in success: never another exception, a hang, or a huge allocation.
/// <para>
/// <c>header</c>, <c>journal</c>, <c>database</c>: one-shot targets that read the input from a file (and run under SharpFuzz's libFuzzer
/// driver when started with <c>--libfuzzer</c>, where clang is available). <c>mutate TARGET SECONDS [SEED]</c> is a self-contained mutational
/// loop over the same targets, for hosts without clang: it mutates generated valid seeds and checks the same rules.
/// </para>
/// </summary>
internal static class PagerFuzz
{
    internal static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("usage: OneLite.Fuzz header|journal|database [--libfuzzer | FILE]  |  mutate TARGET SECONDS [SEED]");
            return 0;
        }

        Action<byte[]>? target = args[0] switch
        {
            "header" => PagerTargets.Header,
            "journal" => PagerTargets.Journal,
            "database" => PagerTargets.Database,
            _ => null,
        };

        if (args[0] == "mutate" && args.Length >= 3 && (target = Pick(args[1])) != null)
        {
            return Mutate(target, int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture), args.Length > 3 ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 1, args[1]);
        }

        if (target == null)
        {
            Console.Error.WriteLine("unknown target " + args[0]);
            return 1;
        }

        if (args.Length > 1 && args[1] == "--libfuzzer")
        {
            SharpFuzz.Fuzzer.LibFuzzer.Run(span => target(span.ToArray()));
            return 0;
        }

        target(File.ReadAllBytes(args[1]));
        return 0;
    }

    private static Action<byte[]>? Pick(string name) => name switch
    {
        "header" => PagerTargets.Header,
        "journal" => PagerTargets.Journal,
        "database" => PagerTargets.Database,
        _ => null,
    };

    private static int Mutate(Action<byte[]> target, int seconds, int seed, string name)
    {
        var rnd = new Random(seed);
        byte[][] seeds = PagerTargets.Seeds(name);
        var clock = Stopwatch.StartNew();
        long runs = 0, typed = 0;
        long worstTicks = 0;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            byte[] input = (byte[])seeds[rnd.Next(seeds.Length)].Clone();
            int edits = 1 + rnd.Next(6);
            for (int e = 0; e < edits && input.Length > 0; e++)
            {
                switch (rnd.Next(6))
                {
                    case 0:
                        input[rnd.Next(input.Length)] = (byte)rnd.Next(256);
                        break;
                    case 1:
                        input[rnd.Next(input.Length)] ^= (byte)(1 << rnd.Next(8));
                        break;
                    case 2:
                    {
                        int at = rnd.Next(input.Length);
                        int n = Math.Min(input.Length - at, 1 + rnd.Next(8));
                        for (int i = 0; i < n; i++)
                        {
                            input[at + i] = rnd.Next(2) == 0 ? (byte)0xFF : (byte)0;
                        }

                        break;
                    }

                    case 3:
                        Array.Resize(ref input, Math.Max(1, input.Length - rnd.Next(Math.Min(input.Length, 600))));
                        break;
                    case 4:
                    {
                        int at = rnd.Next(Math.Max(1, input.Length - 4));
                        uint v = rnd.Next(3) switch { 0 => 0u, 1 => 0xFFFFFFFFu, _ => (uint)rnd.Next() };
                        for (int i = 0; i < 4 && at + i < input.Length; i++)
                        {
                            input[at + i] = (byte)(v >> (24 - 8 * i));
                        }

                        break;
                    }

                    default:
                    {
                        int a = rnd.Next(input.Length), b = rnd.Next(input.Length);
                        (input[a], input[b]) = (input[b], input[a]);
                        break;
                    }
                }
            }

            long t0 = Stopwatch.GetTimestamp();
            try
            {
                target(input);
            }
            catch (OneLiteException)
            {
                typed++;
            }
            catch (Exception e)
            {
                string dir = Environment.GetEnvironmentVariable("ONELITE_FUZZ_OUT") ?? ".";
                string file = Path.Combine(dir, $"crash-{name}-{seed}-{runs}.bin");
                File.WriteAllBytes(file, input);
                Console.WriteLine($"{name}: unexpected {e.GetType().Name} after {runs} executions: {e.Message}\ninput saved to {file}\n{e.StackTrace}");
                return 2;
            }

            worstTicks = Math.Max(worstTicks, Stopwatch.GetTimestamp() - t0);
            runs++;
        }

        Console.WriteLine($"{name}: {runs} executions in {seconds}s, {typed} typed errors, slowest execution {worstTicks * 1000.0 / Stopwatch.Frequency:F1} ms, no unexpected exception");
        return 0;
    }
}

internal static class PagerTargets
{
    // The typed errors hostile input is allowed to produce; anything else escaping (including MISUSE) is a bug.
    private static void Allow(OneLiteException e)
    {
        if (e.Code is not (ResultCode.Corrupt or ResultCode.NotADb or ResultCode.CantOpen or ResultCode.IoErr or ResultCode.ReadOnly or ResultCode.Full))
        {
            throw new InvalidOperationException($"unexpected result code {e.ExtendedCode}: {e.Message} {e.Detail}", e);
        }
    }

    public static void Header(byte[] input)
    {
        try
        {
            _ = DatabaseHeader.Parse(input);
        }
        catch (OneLiteException e)
        {
            Allow(e);
            throw;
        }
    }

    private static PagerOptions Opts() => new() { PageSize = 512, CacheSize = 20, Synchronous = SynchronousMode.Off };

    /// <summary>The input is a rollback journal for a small valid database.</summary>
    public static void Journal(byte[] input)
    {
        // Playback truncates the database to the size the journal claims. The pager accepts claims up to 1 GiB above the file's size (a real
        // file system makes that a sparse extension), but an in-memory file would allocate it, so the harness leaves such inputs to the
        // pager's own sanity test (HostileJournalTests) instead of running out of memory.
        // Journal header (fileformat2): initial page count at offset 16, page size at offset 24, both big-endian.
        if (input.Length >= 28 && (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(16)) * System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(24)) > 64L << 20)
        {
            return;
        }

        var vfs = new MemoryVfs();
        Put(vfs, "f.db", ValidDatabase());
        Put(vfs, "f.db-journal", input);
        Walk(vfs, writes: false);
    }

    /// <summary>The input is a whole database file (opened read-only and then for writing, which walks the freelist).</summary>
    public static void Database(byte[] input)
    {
        var vfs = new MemoryVfs();
        Put(vfs, "f.db", input);
        Walk(vfs, writes: true);
    }

    private static void Walk(MemoryVfs vfs, bool writes)
    {
        try
        {
            using PagerClass p = PagerClass.Open(vfs, "f.db", Opts());
            p.BeginReadSync();
            uint n = Math.Min(p.PageCount, 40u);
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

            if (writes && p.PageCount > 0)
            {
                try
                {
                    p.BeginWriteSync();
                    for (int i = 0; i < 6; i++)
                    {
                        p.Release(p.AllocateSync());
                    }

                    for (uint i = 3; i < 8 && i <= p.PageCount; i++)
                    {
                        p.FreeSync(i);
                    }

                }
                catch (OneLiteException e)
                {
                    Allow(e);
                }
                finally
                {
                    // A failed write transaction must be ended before the read transaction (EndRead refuses to run inside one).
                    if (p.InWriteTransaction)
                    {
                        try
                        {
                            p.RollbackSync();
                        }
                        catch (OneLiteException e)
                        {
                            Allow(e);
                        }
                    }
                }
            }

            try
            {
                p.EndReadSync();
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

    private static void Put(MemoryVfs vfs, string path, byte[] bytes)
    {
        using IVfsFile f = vfs.Open(path, VfsOpenOptions.ReadWrite | VfsOpenOptions.Create);
        f.Write(0, bytes);
    }

    private static byte[]? _valid;

    private static byte[] ValidDatabase()
    {
        if (_valid != null)
        {
            return _valid;
        }

        var vfs = new MemoryVfs();
        using (PagerClass p = PagerClass.Open(vfs, "f.db", Opts()))
        {
            p.BeginReadSync();
            p.BeginWriteSync();
            for (int i = 0; i < 12; i++)
            {
                PageFrame f = i == 0 ? p.GetSync(1) : p.AllocateSync();
                p.MakeWritableSync(f);
                for (int k = i == 0 ? 108 : 0; k < f.Data.Length; k++)
                {
                    f.Data[k] = (byte)(k * 7 + i);
                }

                p.Release(f);
            }

            p.FreeSync(5);
            p.FreeSync(6);
            p.CommitSync();
            p.EndReadSync();
        }

        using IVfsFile r = vfs.Open("f.db", VfsOpenOptions.ReadOnly);
        _valid = new byte[r.FileSize()];
        r.Read(0, _valid);
        return _valid;
    }

    /// <summary>Valid inputs to mutate: a database, and a hot journal made by a real interrupted transaction (with spill, so it has several segments).</summary>
    public static byte[][] Seeds(string target)
    {
        if (target is "header" or "database")
        {
            return [ValidDatabase()];
        }

        var inner = new MemoryVfs();
        var fault = new FaultVfs(inner, new FaultOptions { SectorSize = 512 });
        Put(inner, "f.db", ValidDatabase());
        using (PagerClass p = PagerClass.Open(fault, "f.db", new PagerOptions { PageSize = 512, CacheSize = 3, Synchronous = SynchronousMode.Full }))
        {
            p.BeginReadSync();
            p.BeginWriteSync();
            for (uint i = 2; i <= 11; i++)
            {
                PageFrame f = p.GetSync(i);
                p.MakeWritableSync(f);
                f.Data[0] ^= 0x5A;
                p.Release(f);
            }

            // Stop before the commit completes: the journal is hot.
            fault.HaltAfter(fault.OpCount);
            try
            {
                p.CommitSync();
            }
            catch (PowerLossException)
            {
                // expected: the power-loss stand-in ends the transaction mid-commit
            }
        }

        CrashImage img = fault.CrashImage(fault.OpCount, CrashPolicy.KeepAll);
        return img.Files.TryGetValue("f.db-journal", out byte[]? j) && j.Length > 0 ? [j] : [new byte[512]];
    }
}
