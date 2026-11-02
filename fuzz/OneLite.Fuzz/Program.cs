using RecursiveAI.OneLite.Storage;
using System.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Fuzz;

/// <summary>
/// Entry point for the fuzz targets.
///   btree-libfuzzer | record-libfuzzer          SharpFuzz under libFuzzer (stdin/args as the harness provides; needs an instrumented build)
///   btree-mutate [--seconds N] [--seed S] [--out DIR] [--corpus DIR] [--target btree|record]
///                                                a self-contained mutation fuzzer: structure-aware mutations of small generated databases (and of
///                                                any files in --corpus), run through the same targets; crashing inputs are saved to --out
///   forensic-mutate [--seconds N] [--seed S] [--out DIR] [--corpus DIR]   the same for the forensic reader (database + log + journal bundles)
///   forensic-libfuzzer | forensic-run FILE...
///   run FILE...                                   runs saved inputs through the target (regression)
///   header | journal | database, mutate TARGET    the pager targets (see <see cref="PagerFuzz"/>)
///   fts3-query | fts3-data [--seconds N] [--seed S] [--out DIR]   the FTS3/4 query parser and hostile FTS3/4 shadow tables (see <see cref="Fts3Target"/>)
///   fts-query | fts-data [--seconds N] [--seed S] [--out DIR]   the FTS5 query parser and hostile FTS5 shadow tables (see <see cref="FtsTarget"/>)
///   sql [--seconds N] [--seed S] ...              the SQL parser target (see <see cref="SqlTarget"/>)
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("usage: OneLite.Fuzz btree-mutate [--seconds N] [--seed S] [--out DIR] [--corpus DIR] | run FILE... | btree-libfuzzer | record-libfuzzer | sql [--seconds N] [--seed S] [--inputs N] [--slow-ms N] [--out dir] [--seeds dir] | header|journal|database [--libfuzzer | FILE] | mutate header|journal|database SECONDS [SEED]");
            return 0;
        }

        switch (args[0])
        {
            case "btree-libfuzzer":
                SharpFuzz.Fuzzer.LibFuzzer.Run(span => BTreeTarget.Run(span.ToArray()));
                return 0;
            case "forensic-libfuzzer":
                SharpFuzz.Fuzzer.LibFuzzer.Run(span => ForensicTarget.Run(span.ToArray()));
                return 0;
            case "forensic-mutate":
                return ForensicTarget.Mutate(args[1..]);
            case "forensic-run":
                {
                    int bad = 0;
                    foreach (string f in args[1..])
                    {
                        try
                        {
                            ForensicTarget.Run(File.ReadAllBytes(f));
                        }
                        catch (Exception e)
                        {
                            bad++;
                            Console.WriteLine(f + ": " + e.GetType().Name + ": " + e.Message);
                        }
                    }

                    return bad == 0 ? 0 : 1;
                }

            case "record-libfuzzer":
                SharpFuzz.Fuzzer.LibFuzzer.Run(span => BTreeTarget.RunRecord(span.ToArray()));
                return 0;
            case "run":
                {
                    int bad = 0;
                    foreach (string f in args[1..])
                    {
                        try
                        {
                            BTreeTarget.Run(File.ReadAllBytes(f));
                        }
                        catch (Exception e)
                        {
                            bad++;
                            Console.WriteLine(f + ": " + e.GetType().Name + ": " + e.Message);
                            Console.WriteLine(string.Join("\n", (e.StackTrace ?? "").Split('\n').Take(4)));
                        }
                    }

                    return bad == 0 ? 0 : 1;
                }

            case "pager-btree-libfuzzer":
                SharpFuzz.Fuzzer.LibFuzzer.Run(span => PagerBTreeTarget.Run(span.ToArray()));
                return 0;
            case "pager-btree-mutate":
                return PagerBTreeMutate(args[1..]);
            case "pager-btree-run":
                {
                    int bad = 0;
                    foreach (string f in args[1..])
                    {
                        try
                        {
                            PagerBTreeTarget.Run(File.ReadAllBytes(f));
                        }
                        catch (Exception e)
                        {
                            bad++;
                            Console.WriteLine(f + ": " + e.GetType().Name + ": " + e.Message);
                            Console.WriteLine(string.Join("\n", (e.StackTrace ?? "").Split('\n').Take(5)));
                        }
                    }

                    return bad == 0 ? 0 : 1;
                }

            case "btree-mutate":
                return Mutate(args[1..]);
            case "header":
            case "journal":
            case "database":
            case "mutate":
                return PagerFuzz.Run(args);
            case "wal":
            case "wal-libfuzzer":
                return WalFuzz.Run(args);
            case "cipher":
            case "cipher-libfuzzer":
                return CipherFuzz.Run(args);
            case "cipher-pager":
                return CipherPagerFuzz.Run(args);
            case "fts3-query":
            case "fts3-data":
                return Fts3Target.Run(args);
            case "fts-query":
            case "fts-data":
                return FtsTarget.Run(args);
            case "sql":
                return SqlTarget.Run(args[1..]);
            default:
                Console.Error.WriteLine("unknown command " + args[0]);
                return 2;
        }
    }

    /// <summary>pager-btree-mutate [--seconds N] [--seed S] [--out DIR]: mutates database images and journals and runs them through <see cref="PagerBTreeTarget"/>.</summary>
    private static int PagerBTreeMutate(string[] args)
    {
        int seconds = 60, seed = 1;
        string outDir = "crashes";
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seconds": seconds = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--seed": seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--out": outDir = args[++i]; break;
                default: Console.Error.WriteLine("unknown option " + args[i]); return 2;
            }
        }

        Directory.CreateDirectory(outDir);
        var rnd = new Random(seed);
        var seeds = new List<byte[]>();
        for (int i = 0; i < 24; i++)
        {
            seeds.Add(PagerBTreeTarget.Seed(rnd, i));
        }

        var seen = new HashSet<string>();
        long execs = 0, crashes = 0, typed = 0;
        var sw = Stopwatch.StartNew();
        long nextReport = 30_000;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            byte[] seedInput = seeds[rnd.Next(seeds.Count)];
            int dbLen = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(seedInput);
            byte[] db = seedInput[4..(4 + dbLen)], jr = seedInput[(4 + dbLen)..];
            // mutate the image with the structure-aware mutator, the journal bytewise, sometimes both, sometimes drop the journal
            if (db.Length >= 100 && rnd.Next(3) != 0)
            {
                db = Mutator.Mutate(db, rnd, [db]);
            }

            if (jr.Length > 0 && rnd.Next(2) == 0)
            {
                jr = (byte[])jr.Clone();
                for (int k = 1 + rnd.Next(4); k > 0; k--)
                {
                    int at = rnd.Next(jr.Length);
                    switch (rnd.Next(4))
                    {
                        case 0: jr[at] = (byte)rnd.Next(256); break;
                        case 1: jr[at] ^= (byte)(1 << rnd.Next(8)); break;
                        case 2: Array.Resize(ref jr, Math.Max(1, at)); break;
                        default: jr[at] = (byte)(rnd.Next(2) == 0 ? 0xFF : 0); break;
                    }
                }
            }
            else if (rnd.Next(8) == 0)
            {
                jr = [];
            }

            byte[] input = PagerBTreeTarget.Pack(db, jr);
            execs++;
            try
            {
                PagerBTreeTarget.Run(input);
            }
            catch (OneLiteException)
            {
                typed++;
            }
            catch (Exception e)
            {
                crashes++;
                string sig = e.GetType().Name + "@" + (e.StackTrace ?? "").Split('\n').FirstOrDefault(l => l.Contains("RecursiveAI", StringComparison.Ordinal))?.Trim();
                if (seen.Add(sig))
                {
                    string name = Path.Combine(outDir, "pagercrash-" + Convert.ToHexString(SHA256.HashData(input))[..12].ToLowerInvariant());
                    File.WriteAllBytes(name, input);
                    Console.WriteLine($"CRASH {sig}\n  {e.Message}\n  saved {name}");
                }
            }

            if (sw.ElapsedMilliseconds > nextReport)
            {
                nextReport += 30_000;
                Console.WriteLine($"{sw.Elapsed.TotalSeconds:F0}s execs={execs} ({execs / Math.Max(1, sw.Elapsed.TotalSeconds):F0}/s) crashes={crashes} distinctCrashes={seen.Count}");
            }
        }

        Console.WriteLine($"done: seconds={sw.Elapsed.TotalSeconds:F0} execs={execs} crashes={crashes} distinct={seen.Count} typedErrors={typed}");
        return seen.Count == 0 ? 0 : 1;
    }

    private static int Mutate(string[] args)
    {
        int seconds = 60, seed = 1;
        string outDir = "crashes";
        string? corpus = null;
        bool record = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seconds": seconds = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--seed": seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--out": outDir = args[++i]; break;
                case "--corpus": corpus = args[++i]; break;
                case "--target": record = args[++i] == "record"; break;
                default: Console.Error.WriteLine("unknown option " + args[i]); return 2;
            }
        }

        Directory.CreateDirectory(outDir);
        var rnd = new Random(seed);
        var seeds = new List<byte[]>();
        for (int i = 0; i < 24; i++)
        {
            seeds.Add(Seeds.Make(rnd, i));
        }

        if (corpus != null)
        {
            foreach (string f in Directory.GetFiles(corpus, "*.db"))
            {
                if (new FileInfo(f).Length < 400_000)
                {
                    seeds.Add(File.ReadAllBytes(f));
                }
            }
        }

        var seen = new HashSet<string>();
        long execs = 0, crashes = 0, corrupt = 0;
        var sw = Stopwatch.StartNew();
        long nextReport = 30_000;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            byte[] input;
            if (record)
            {
                input = new byte[rnd.Next(1, 120)];
                rnd.NextBytes(input);
                if (rnd.Next(2) == 0)
                {
                    input[0] = (byte)Math.Min(input.Length, 1 + rnd.Next(12));
                }
            }
            else
            {
                input = Mutator.Mutate(seeds[rnd.Next(seeds.Count)], rnd, seeds);
            }

            execs++;
            try
            {
                if (record)
                {
                    BTreeTarget.RunRecord(input);
                }
                else
                {
                    BTreeTarget.Run(input);
                }
            }
            catch (OneLiteException)
            {
                corrupt++;
            }
            catch (Exception e)
            {
                crashes++;
                string sig = e.GetType().Name + "@" + (e.StackTrace ?? "").Split('\n').FirstOrDefault(l => l.Contains("RecursiveAI", StringComparison.Ordinal))?.Trim();
                if (seen.Add(sig))
                {
                    string name = Path.Combine(outDir, "crash-" + Convert.ToHexString(SHA256.HashData(input))[..12].ToLowerInvariant());
                    File.WriteAllBytes(name, input);
                    Console.WriteLine($"CRASH {sig}\n  {e.Message}\n  saved {name}");
                }
            }

            if (sw.ElapsedMilliseconds > nextReport)
            {
                nextReport += 30_000;
                Console.WriteLine($"{sw.Elapsed.TotalSeconds:F0}s execs={execs} ({execs / Math.Max(1, sw.Elapsed.TotalSeconds):F0}/s) crashes={crashes} distinctCrashes={seen.Count}");
            }
        }

        Console.WriteLine($"done: seconds={sw.Elapsed.TotalSeconds:F0} execs={execs} crashes={crashes} distinct={seen.Count} (inputs that surfaced as a typed corruption error from the open: {corrupt})");
        return seen.Count == 0 ? 0 : 1;
    }
}

internal static class Mutator
{
    private static readonly uint[] Special = [0, 1, 2, 3, 4, 0x7f, 0x80, 0xff, 0x100, 0x1ff, 0x200, 0xfff, 0x1000, 0xffff, 0x10000, 0x7fffffff, 0x80000000, 0xffffffff];
    private static readonly int[] HeaderOffsets = [1, 3, 5];
    private static readonly byte[] PageTypes = [0, 1, 2, 5, 10, 13, 8, 255];

    public static byte[] Mutate(byte[] seed, Random rnd, List<byte[]> all)
    {
        byte[] d = (byte[])seed.Clone();
        int ps = Math.Max(512, (d[16] << 8) | d[17]);
        if (ps == 1)
        {
            ps = 65536;
        }

        int pages = Math.Max(1, d.Length / ps);
        int n = 1 + rnd.Next(rnd.Next(4) == 0 ? 30 : 6);
        for (int k = 0; k < n; k++)
        {
            int pg = rnd.Next(pages);
            int hdr = pg == 0 ? 100 : 0;
            int baseOff = pg * ps;
            switch (rnd.Next(9))
            {
                case 0: // a byte anywhere
                    d[rnd.Next(d.Length)] = (byte)rnd.Next(256);
                    break;
                case 1: // a bit
                    d[rnd.Next(d.Length)] ^= (byte)(1 << rnd.Next(8));
                    break;
                case 2: // page header field
                    Put16(d, baseOff + hdr + HeaderOffsets[rnd.Next(HeaderOffsets.Length)], (int)Special[rnd.Next(Special.Length)]);
                    break;
                case 3: // page type
                    d[baseOff + hdr] = (byte)PageTypes[rnd.Next(PageTypes.Length)];
                    break;
                case 4: // a cell pointer
                    {
                        int cells = (d[baseOff + hdr + 3] << 8) | d[baseOff + hdr + 4];
                        int i = rnd.Next(Math.Max(1, Math.Min(cells, 200)));
                        Put16(d, baseOff + hdr + 8 + 2 * i, rnd.Next(2) == 0 ? rnd.Next(ps) : (int)Special[rnd.Next(Special.Length)]);
                        break;
                    }
                case 5: // a 4-byte value (child pointers, right-most pointer, overflow pointers)
                    Put32(d, baseOff + rnd.Next(ps - 4), Special[rnd.Next(Special.Length)] % (uint)(pages + 3));
                    break;
                case 6: // copy a stretch from another page
                    {
                        int from = rnd.Next(pages) * ps + rnd.Next(ps);
                        int len = Math.Min(rnd.Next(1, 64), d.Length - from);
                        int to = baseOff + rnd.Next(ps);
                        len = Math.Min(len, d.Length - to);
                        if (len > 0)
                        {
                            Buffer.BlockCopy(d, from, d, to, len);
                        }

                        break;
                    }
                case 7: // whole-page swap or splice from another seed
                    {
                        byte[] o = all[rnd.Next(all.Count)];
                        int ops = Math.Max(512, (o[16] << 8) | o[17]);
                        if (ops == ps && o.Length >= ps)
                        {
                            int opg = rnd.Next(o.Length / ps);
                            Buffer.BlockCopy(o, opg * ps, d, baseOff, ps);
                        }

                        break;
                    }
                default: // database header
                    {
                        int[] offs = [16, 20, 28, 32, 36, 44, 52, 56, 60, 64];
                        int o = offs[rnd.Next(offs.Length)];
                        if (o < 100 && d.Length > o + 4)
                        {
                            Put32(d, o, Special[rnd.Next(Special.Length)] % 0x1000);
                        }

                        break;
                    }
            }
        }

        if (rnd.Next(50) == 0)
        {
            Array.Resize(ref d, Math.Max(100, d.Length - rnd.Next(ps)));
        }

        return d;
    }

    private static void Put16(byte[] d, int o, int v)
    {
        if (o >= 0 && o + 2 <= d.Length)
        {
            d[o] = (byte)(v >> 8);
            d[o + 1] = (byte)v;
        }
    }

    private static void Put32(byte[] d, int o, uint v)
    {
        if (o >= 0 && o + 4 <= d.Length)
        {
            d[o] = (byte)(v >> 24);
            d[o + 1] = (byte)(v >> 16);
            d[o + 2] = (byte)(v >> 8);
            d[o + 3] = (byte)v;
        }
    }
}

/// <summary>Small valid databases to mutate: rowid tables with overflow, indexes and a WITHOUT ROWID-shaped tree, in every page size and encoding.</summary>
internal static class Seeds
{
    public static byte[] Make(Random rnd, int i)
    {
        int[] sizes = [512, 512, 1024, 2048, 4096, 512];
        TextEncoding enc = (TextEncoding)(1 + (i % 3));
        var src = ImagePageSource.Create(sizes[i % sizes.Length], 0, enc, i % 4 == 3 ? 1 : 0);
        var ctx = new BtContext(src, enc);
        uint tr = Create(src, ctx, true), ix = Create(src, ctx, false);
        var table = BTree.Table(ctx, tr);
        var index = BTree.Index(ctx, ix, KeyInfo.Binary(enc, 2));
        using BtCursor tc = table.OpenCursor();
        using BtCursor ic = index.OpenCursor();
        int rows = 20 + rnd.Next(60);
        for (int r = 1; r <= rows; r++)
        {
            int len = rnd.Next(12) == 0 ? rnd.Next(src.PageSize * 2) : rnd.Next(40);
            var blob = new byte[len];
            rnd.NextBytes(blob);
            byte[] rec = RecordWriter.ToArray([Mem.FromInt(r), Mem.FromBlob(blob), Mem.FromReal(r * 0.5), Mem.FromText("row" + r, enc)]);
            Run(src, () => tc.Insert(r, rec));
            Mem[] key = [Mem.FromText(new string('k', rnd.Next(1, rnd.Next(10) == 0 ? src.PageSize : 20)) + r, enc), Mem.FromInt(r)];
            byte[] krec = RecordWriter.ToArray(key);
            Run(src, () => ic.Insert(krec, key));
        }

        for (int r = 1; r <= rows; r += 3)
        {
            long id = r;
            Run(src, () => tc.MoveToRowid(id, out _));
            Run(src, tc.Delete);
        }

        // the schema table, so SchemaLite has rows to follow
        using BtCursor sc = BTree.Table(ctx, 1).OpenCursor();
        byte[] s1 = RecordWriter.ToArray([Mem.FromText("table", enc), Mem.FromText("t", enc), Mem.FromText("t", enc), Mem.FromInt(tr), Mem.FromText("CREATE TABLE t(a,b,c,d)", enc)]);
        Run(src, () => sc.Insert(1, s1));
        byte[] s2 = RecordWriter.ToArray([Mem.FromText("index", enc), Mem.FromText("i", enc), Mem.FromText("t", enc), Mem.FromInt(ix), Mem.FromText("CREATE INDEX i ON t(d)", enc)]);
        Run(src, () => sc.Insert(2, s2));
        return src.ToBytes();
    }

    private static uint Create(ImagePageSource src, BtContext ctx, bool intKey)
    {
        uint root = 0;
        Run(src, () => BtAdmin.CreateTree(ctx, intKey, out root));
        return root;
    }

    private static void Run(ImagePageSource src, Func<BtStatus> op)
    {
        while (true)
        {
            BtStatus s = op();
            if (s == BtStatus.Ok)
            {
                return;
            }

            if (s == BtStatus.Error)
            {
                throw new InvalidOperationException("seed generation failed");
            }

            src.Complete();
        }
    }
}
