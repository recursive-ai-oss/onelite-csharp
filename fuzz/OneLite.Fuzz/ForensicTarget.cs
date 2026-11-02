using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Forensics;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Fuzz;

/// <summary>
/// Builds write-ahead logs and bundles database, log and journal bytes into one fuzz input:
/// <c>[u32 db length][u32 log length][u32 journal length][db][log][journal]</c> (little-endian lengths).
/// </summary>
public static class ForensicSeeds
{
    /// <summary>Joins the three files into one input.</summary>
    public static byte[] Bundle(byte[] db, byte[]? wal, byte[]? journal)
    {
        wal ??= [];
        journal ??= [];
        byte[] r = new byte[12 + db.Length + wal.Length + journal.Length];
        BitConverter.TryWriteBytes(r.AsSpan(0), (uint)db.Length);
        BitConverter.TryWriteBytes(r.AsSpan(4), (uint)wal.Length);
        BitConverter.TryWriteBytes(r.AsSpan(8), (uint)journal.Length);
        db.CopyTo(r, 12);
        wal.CopyTo(r, 12 + db.Length);
        journal.CopyTo(r, 12 + db.Length + wal.Length);
        return r;
    }

    /// <summary>
    /// Writes a log: header, then one frame per entry. Frames are chained with the log's checksum; an entry with <c>commitSize</c> above 0 is a
    /// commit frame. <paramref name="salt1"/>/<paramref name="salt2"/> are the header salts; a frame may carry other salts to make a stale frame.
    /// </summary>
    public static byte[] BuildWal(int pageSize, bool bigEndian, uint salt1, uint salt2, IEnumerable<(uint Page, byte[] Image, uint CommitSize, uint FrameSalt1)> frames)
    {
        using var ms = new MemoryStream();
        byte[] h = new byte[32];
        uint magic = bigEndian ? 0x377f0683u : 0x377f0682u;
        Put(h, 0, magic);
        Put(h, 4, 3007000);
        Put(h, 8, (uint)pageSize);
        Put(h, 12, 0);
        Put(h, 16, salt1);
        Put(h, 20, salt2);
        uint s0 = 0, s1 = 0;
        WalScannerChecksum(h.AsSpan(0, 24), bigEndian, ref s0, ref s1);
        Put(h, 24, s0);
        Put(h, 28, s1);
        ms.Write(h);
        foreach ((uint page, byte[] image, uint commit, uint fs1) in frames)
        {
            byte[] fh = new byte[24];
            Put(fh, 0, page);
            Put(fh, 4, commit);
            Put(fh, 8, fs1 == 0 ? salt1 : fs1);
            Put(fh, 12, salt2);
            WalScannerChecksum(fh.AsSpan(0, 8), bigEndian, ref s0, ref s1);
            WalScannerChecksum(image, bigEndian, ref s0, ref s1);
            Put(fh, 16, s0);
            Put(fh, 20, s1);
            ms.Write(fh);
            ms.Write(image);
        }

        return ms.ToArray();
    }

    private static void Put(byte[] b, int o, uint v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(o), v);

    // The checksum is the log format's own; tests and the target need it without access to the library's internals.
    private static void WalScannerChecksum(ReadOnlySpan<byte> data, bool big, ref uint s0, ref uint s1)
    {
        for (int i = 0; i + 8 <= data.Length; i += 8)
        {
            uint x0 = big ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data[i..]) : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
            uint x1 = big ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data[(i + 4)..]) : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 4)..]);
            s0 += x0 + s1;
            s1 += x1 + s0;
        }
    }

    /// <summary>A database image from the B-tree seed generator plus a log that rewrites some of its pages, as one input.</summary>
    public static byte[] Make(Random rnd, int i)
    {
        byte[] db = Seeds.Make(rnd, i);
        int ps = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(db.AsSpan(16));
        if (ps == 1)
        {
            ps = 65536;
        }

        int pages = db.Length / ps;
        var frames = new List<(uint, byte[], uint, uint)>();
        int txns = 2 + rnd.Next(4);
        for (int t = 0; t < txns; t++)
        {
            int n = 1 + rnd.Next(3);
            for (int k = 0; k < n; k++)
            {
                uint pg = (uint)(1 + rnd.Next(pages));
                byte[] img = db.AsSpan((int)(pg - 1) * ps, ps).ToArray();
                if (pg != 1)
                {
                    rnd.NextBytes(img.AsSpan(img.Length - 16));
                }

                frames.Add((pg, img, k == n - 1 ? (uint)pages : 0u, (i % 5 == 4 && t == txns - 1) ? 0x1234u : 0u));
            }
        }

        byte[] wal = BuildWal(ps, i % 2 == 1, 0xAABBCCDDu, 0x11223344u, frames);
        return Bundle(db, wal, i % 3 == 0 ? null : db.AsSpan(0, Math.Min(db.Length, 512 + ps * 2)).ToArray());
    }
}

/// <summary>
/// The forensic reader's target: a hostile database, log and journal are opened read-only (through the memory VFS) and everything the API can do is
/// run on every snapshot: tables, rows, space analysis and carving. The only acceptable outcomes are success and a typed refusal
/// (<see cref="OneLiteException"/>, <see cref="NotADatabaseException"/>, <see cref="VfsException"/> for a missing file,
/// <see cref="ForensicChangedException"/>). Anything else, a hang or a huge allocation escapes to the caller as a bug.
/// </summary>
public static class ForensicTarget
{
    /// <summary>Runs one input.</summary>
    public static void Run(byte[] data)
    {
        if (data.Length < 12)
        {
            return;
        }

        long dl = BitConverter.ToUInt32(data, 0), wl = BitConverter.ToUInt32(data, 4), jl = BitConverter.ToUInt32(data, 8);
        if (12 + dl + wl + jl > data.Length)
        {
            dl = Math.Min(dl, data.Length - 12);
            wl = Math.Min(wl, data.Length - 12 - dl);
            jl = Math.Min(jl, data.Length - 12 - dl - wl);
        }

        var vfs = new MemoryVfs();
        vfs.Import("f.db", data.AsSpan(12, (int)dl));
        if (wl > 0)
        {
            vfs.Import("f.db-wal", data.AsSpan(12 + (int)dl, (int)wl));
        }

        if (jl > 0)
        {
            vfs.Import("f.db-journal", data.AsSpan(12 + (int)(dl + wl), (int)jl));
        }

        long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        try
        {
            using ForensicDatabase db = ForensicDatabase.Open("f.db", new ForensicOptions { Vfs = vfs });
            var sels = new List<SnapshotSelector> { SnapshotSelector.DatabaseOnly, SnapshotSelector.Latest, SnapshotSelector.LatestWithTrailingFrames };
            if (db.Wal != null)
            {
                sels.AddRange(db.Wal.Commits.Take(12).Select(c => SnapshotSelector.AsOf(c.Id)));
            }

            foreach (SnapshotSelector sel in sels)
            {
                try
                {
                    ForensicSnapshot s = db.OpenSnapshot(sel);
                    foreach (ForensicTable t in s.Tables.Take(20))
                    {
                        if (t.CanReadRows)
                        {
                            try
                            {
                                _ = s.ReadRows(t);
                            }
                            catch (OneLiteException cex) when (cex.Code == ResultCode.Corrupt)
                            {
                            }
                        }
                    }

                    _ = s.Space();
                    _ = s.Carve(new CarveOptions { MinConfidence = 0, IncludeLiveDuplicates = true });
                }
                catch (Exception e) when (e is OneLiteException { Code: ResultCode.Corrupt } or NotADatabaseException or ForensicChangedException)
                {
                }
            }
        }
        catch (Exception e) when (e is OneLiteException { Code: ResultCode.Corrupt } or NotADatabaseException or VfsException)
        {
        }

        if (sw.ElapsedMilliseconds > 8000)
        {
            throw new InvalidOperationException($"forensic pass took {sw.ElapsedMilliseconds} ms on {data.Length} bytes");
        }

        if (GC.GetAllocatedBytesForCurrentThread() - alloc0 > 1L << 30)
        {
            throw new InvalidOperationException("forensic pass allocated over 1 GiB");
        }
    }

    /// <summary>A seeded mutation loop: byte flips, inserts, deletes, splices and cuts over the seeds and any corpus files. Crashing inputs are written to the output folder.</summary>
    public static int Mutate(string[] args)
    {
        int seconds = 60, seed = 1;
        string outDir = "forensic-crashes";
        string? corpus = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seconds": seconds = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--seed": seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--out": outDir = args[++i]; break;
                case "--corpus": corpus = args[++i]; break;
                default: Console.Error.WriteLine("unknown option " + args[i]); return 2;
            }
        }

        Directory.CreateDirectory(outDir);
        var rnd = new Random(seed);
        var seeds = new List<byte[]>();
        for (int i = 0; i < 40; i++)
        {
            seeds.Add(ForensicSeeds.Make(rnd, i));
        }

        if (corpus != null)
        {
            foreach (string f in Directory.EnumerateFiles(corpus, "*.db").Order(StringComparer.Ordinal))
            {
                if (new FileInfo(f).Length > 600_000)
                {
                    continue;
                }

                byte[] db = File.ReadAllBytes(f);
                byte[]? wal = File.Exists(f + "-wal") && new FileInfo(f + "-wal").Length < 600_000 ? File.ReadAllBytes(f + "-wal") : null;
                byte[]? jr = File.Exists(f + "-journal") && new FileInfo(f + "-journal").Length < 600_000 ? File.ReadAllBytes(f + "-journal") : null;
                seeds.Add(ForensicSeeds.Bundle(db, wal, jr));
            }
        }

        var seen = new HashSet<string>();
        long execs = 0, crashes = 0;
        var sw = Stopwatch.StartNew();
        long nextReport = 30_000;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            byte[] input = (byte[])seeds[rnd.Next(seeds.Count)].Clone();
            int muts = 1 + rnd.Next(6);
            for (int m = 0; m < muts && input.Length > 16; m++)
            {
                // Mutations favour the headers: the first bytes of each section.
                int pos = rnd.Next(4) == 0 ? rnd.Next(Math.Min(input.Length, 200)) : rnd.Next(input.Length);
                switch (rnd.Next(7))
                {
                    case 0: input[pos] ^= (byte)(1 << rnd.Next(8)); break;
                    case 1: input[pos] = (byte)rnd.Next(256); break;
                    case 2: input[pos] = 0xFF; break;
                    case 3:
                        input[pos] = 0;
                        break;
                    case 4:
                        {
                            int len = Math.Min(input.Length - pos, 1 + rnd.Next(64));
                            rnd.NextBytes(input.AsSpan(pos, len));
                            break;
                        }

                    case 5:
                        {
                            int src = rnd.Next(input.Length), len = Math.Min(Math.Min(input.Length - src, input.Length - pos), 1 + rnd.Next(512));
                            Buffer.BlockCopy(input, src, input, pos, len);
                            break;
                        }

                    default:
                        if (rnd.Next(8) == 0)
                        {
                            Array.Resize(ref input, Math.Max(16, input.Length - rnd.Next(input.Length / 3 + 1)));
                        }

                        break;
                }
            }

            execs++;
            try
            {
                Run(input);
            }
            catch (Exception e)
            {
                string key = e.GetType().Name + ":" + (e.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim();
                if (seen.Add(key))
                {
                    crashes++;
                    string name = Convert.ToHexString(SHA256.HashData(input))[..16].ToLowerInvariant();
                    File.WriteAllBytes(Path.Combine(outDir, name), input);
                    Console.WriteLine($"CRASH {name}: {e.GetType().Name}: {e.Message}\n{string.Join('\n', (e.StackTrace ?? "").Split('\n').Take(5))}");
                }
            }

            if (sw.ElapsedMilliseconds > nextReport)
            {
                Console.WriteLine($"[{sw.Elapsed.TotalSeconds:0}s] execs={execs} distinct crashes={crashes}");
                nextReport += 30_000;
            }
        }

        Console.WriteLine($"done: execs={execs} crashes={crashes} seconds={sw.Elapsed.TotalSeconds:0}");
        return crashes == 0 ? 0 : 1;
    }
}
