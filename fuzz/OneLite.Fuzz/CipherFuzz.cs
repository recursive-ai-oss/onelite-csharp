using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using RecursiveAI.OneLite.Storage.Crypto;

namespace RecursiveAI.OneLite.Fuzz;

/// <summary>
/// Fuzz target for the SQLCipher-compatible page codec. The first input byte picks the settings (digest pair, page size, plaintext header)
/// and the operation; the rest is the payload:
///   0 decode a payload-sized page at a payload-chosen page number (must return true or false, never throw),
///   1 open an existing database from a short or odd page 1 and an odd external salt (only typed errors),
///   2 parse key text (never throws),
///   3 and 4 whole-file decrypt and encrypt of an odd stream (only typed errors),
///   5.. mutate a valid encoded page and check the verdict: bytes inside the authenticated area must make the MAC fail, bytes outside it must not.
/// <c>cipher SECONDS [SEED]</c> runs a mutation loop (hosts without clang); <c>cipher-libfuzzer</c> and <c>cipher FILE</c> run one input.
/// </summary>
internal static class CipherFuzz
{
    private static readonly int[] PageSizes = [512, 1024, 2048, 4096];
    private static readonly Dictionary<int, PageCodec> Codecs = [];
    private static readonly Dictionary<int, byte[]> Valid = [];

    internal static int Run(string[] args)
    {
        if (args[0] == "cipher-libfuzzer")
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

    private static CipherSettings Settings(byte cfg)
    {
        (CipherDigest k, CipherDigest h)[] sets = [(CipherDigest.Sha512, CipherDigest.Sha512), (CipherDigest.Sha1, CipherDigest.Sha1), (CipherDigest.Sha256, CipherDigest.Sha256), (CipherDigest.Sha256, CipherDigest.Sha1)];
        var (k, h) = sets[cfg & 3];
        return new CipherSettings(PageSizes[(cfg >> 2) & 3], 1, k, h, ((cfg >> 4) & 1) != 0 ? 32 : 0);
    }

    private static PageCodec Codec(byte cfg)
    {
        int id = cfg & 0x1F;
        if (!Codecs.TryGetValue(id, out PageCodec? c))
        {
            using CipherKey key = CipherKey.FromPassphrase("fuzz");
            c = PageCodec.CreateNew(Settings(cfg), key, new byte[16]);
            Codecs[id] = c;
        }

        return c;
    }

    private static void Typed(Action a)
    {
        try
        {
            a();
        }
        catch (OneLiteException e) when (e.Code is ResultCode.NotADb or ResultCode.Corrupt)
        {
        }
    }

    public static void Target(byte[] input)
    {
        if (input.Length < 2)
        {
            return;
        }

        byte cfg = input[0];
        int op = (cfg >> 5) & 7;
        byte[] rest = input[1..];
        CipherSettings s = Settings(cfg);
        int p = s.PageSize;
        switch (op)
        {
            case 0:
            {
                PageCodec c = Codec(cfg);
                byte[] page = new byte[p];
                rest.AsSpan(1, Math.Min(p, rest.Length - 1)).CopyTo(page);
                uint pgno = 1u + (rest[0] % 4u) * (rest[0] > 200 ? 1_000_000_000u : 1u);
                _ = c.TryDecode(pgno, page, new byte[p]);
                break;
            }

            case 1:
                Typed(() =>
                {
                    using CipherKey k = CipherKey.FromPassphrase("fuzz");
                    int n = Math.Min(rest.Length, 20);
                    using PageCodec c = PageCodec.OpenExisting(s, k, rest, rest.AsSpan(0, n));
                });
                break;
            case 2:
            {
                using CipherKey k = CipherKey.Parse(System.Text.Encoding.Latin1.GetString(rest));
                _ = k.IsRawKey;
                break;
            }

            case 3:
                Typed(() =>
                {
                    using CipherKey k = CipherKey.FromPassphrase("fuzz");
                    CipherFile.Decrypt(new MemoryStream(rest), Stream.Null, s, k, rest.Length > 16 ? rest.AsSpan(0, 16) : default);
                });
                break;
            case 4:
                Typed(() =>
                {
                    using CipherKey k = CipherKey.FromPassphrase("fuzz");
                    CipherFile.Encrypt(new MemoryStream(rest), Stream.Null, s, k);
                });
                break;
            default:
                MutatedValidPage(cfg, s, rest);
                break;
        }
    }

    private static void MutatedValidPage(byte cfg, CipherSettings s, ReadOnlySpan<byte> rest)
    {
        int p = s.PageSize;
        PageCodec c = Codec(cfg);
        uint pgno = 1 + (uint)(rest[0] & 1);
        int key = (cfg & 0x1F) * 4 + (int)pgno;
        if (!Valid.TryGetValue(key, out byte[]? good))
        {
            byte[] plain = new byte[p];
            new Random(key).NextBytes(plain);
            if (pgno == 1)
            {
                "SQLite format 3\0"u8.CopyTo(plain);
            }

            plain.AsSpan(p - s.ReserveSize).Clear();
            good = new byte[p];
            c.Encode(pgno, plain, good);
            Valid[key] = good;
        }

        byte[] page = (byte[])good.Clone();
        int dataEnd = p - s.ReserveSize;
        int authStart = pgno != 1 ? 0 : s.PlaintextHeaderSize != 0 ? s.PlaintextHeaderSize : 16;
        int authEnd = dataEnd + 16 + s.HmacSize;
        for (int i = 1; i + 1 < rest.Length; i += 2)
        {
            int at = (rest[i] | (i + 2 < rest.Length ? rest[i + 1] << 8 : 0)) % p;
            byte x = (byte)(rest[i + 1] | 1);
            page[at] ^= x;
        }

        bool touchedAuth = !page.AsSpan(authStart, authEnd - authStart).SequenceEqual(good.AsSpan(authStart, authEnd - authStart));
        bool ok = c.TryDecode(pgno, page, new byte[p]);
        // A change inside the authenticated area (ciphertext, IV, MAC) must fail the MAC; a change outside it (salt, plaintext header, padding) must not.
        if (touchedAuth && ok)
        {
            throw new InvalidOperationException("a page changed inside the authenticated area still verified");
        }

        if (!touchedAuth && !ok)
        {
            throw new InvalidOperationException("a page changed only outside the authenticated area failed to verify");
        }
    }

    private static int Mutate(int seconds, int seed)
    {
        var rng = new Random(seed);
        var sw = Stopwatch.StartNew();
        long execs = 0;
        int failures = 0;
        var byOp = new long[8];
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            int len = rng.Next(0, 4) switch { 0 => rng.Next(2, 40), 1 => rng.Next(2, 600), 2 => rng.Next(2, 5000), _ => rng.Next(2, 20) };
            byte[] input = new byte[len];
            rng.NextBytes(input);
            if (rng.Next(3) == 0)
            {
                input[0] = (byte)((input[0] & 0x1F) | (5 << 5)); // favour the structured mutation
            }

            byOp[(input[0] >> 5) & 7]++;
            try
            {
                Target(input);
            }
            catch (Exception e)
            {
                failures++;
                Console.Error.WriteLine("FAIL " + e.GetType().Name + ": " + e.Message + " @ " + string.Join(" < ", (e.StackTrace ?? "").Split((char)10).Take(3).Select(l => l.Trim())));
                File.WriteAllBytes($"cipher-crash-{seed}-{failures}.bin", input);
                if (failures > 20)
                {
                    break;
                }
            }

            execs++;
        }

        Console.WriteLine($"cipher fuzz: {execs} executions in {sw.Elapsed.TotalSeconds:F0}s, ops [{string.Join(",", byOp)}], failures {failures}");
        return failures == 0 ? 0 : 1;
    }
}
