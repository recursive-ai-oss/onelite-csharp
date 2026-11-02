using System;
using System.Globalization;
using System.IO;
using RecursiveAI.OneLite;
using RecursiveAI.OneLite.Storage.Crypto;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;
using PagerClass = RecursiveAI.OneLite.Storage.Pager.Pager;

namespace RecursiveAI.OneLite.CipherTool;

/// <summary>
/// Whole-file SQLCipher-compatible transforms for tests and conversion.
///   encrypt IN OUT [options]   encrypt a plaintext database whose header reserve already equals the cipher's reserve; prints the salt (hex)
///   decrypt IN OUT [options]   decrypt to plaintext
///   check IN [options]         verify every page MAC; prints the page count
///   pager-dump IN OUT [options] read through the pager (hot journal, WAL) and write a plain copy
/// Options: --key TEXT (passphrase, or x'64hex' / x'96hex') --compat 3|4 --page-size N --kdf-iter N --kdf sha1|sha256|sha512
///          --hmac sha1|sha256|sha512 --plaintext-header N --salt HEX32
/// Exit codes: 0 ok, 1 OneLite error (code and message printed), 2 usage.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: OneLite.Cipher encrypt|decrypt IN OUT [options] | check IN [options]");
            return 2;
        }

        string cmd = args[0];
        int pos = cmd == "check" ? 2 : 3;
        if (args.Length < pos)
        {
            Console.Error.WriteLine("missing file arguments");
            return 2;
        }

        string? key = null;
        string? saltHex = null;
        int compat = 4, pageSize = 0, iter = 0, header = 0;
        CipherDigest? kdf = null, hmac = null;
        for (int i = pos; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--key": key = args[++i]; break;
                case "--compat": compat = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--page-size": pageSize = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--kdf-iter": iter = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--kdf": kdf = Digest(args[++i]); break;
                case "--hmac": hmac = Digest(args[++i]); break;
                case "--plaintext-header": header = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--salt": saltHex = args[++i]; break;
                default:
                    Console.Error.WriteLine("unknown option " + args[i]);
                    return 2;
            }
        }

        if (key is null)
        {
            Console.Error.WriteLine("--key is required");
            return 2;
        }

        try
        {
            CipherSettings s = CipherSettings.ForCompatibility(compat);
            if (pageSize != 0) s = s.WithPageSize(pageSize);
            if (iter != 0) s = s.WithKdfIterations(iter);
            if (kdf is not null || hmac is not null) s = s.WithDigests(kdf ?? s.KdfDigest, hmac ?? s.HmacDigest);
            if (header != 0) s = s.WithPlaintextHeaderSize(header);
            byte[] salt = saltHex is null ? [] : Convert.FromHexString(saltHex);
            using CipherKey k = CipherKey.Parse(key);
            if (cmd == "encrypt")
            {
                using var inp = File.OpenRead(args[1]);
                using var outp = File.Create(args[2]);
                Console.WriteLine(Convert.ToHexString(CipherFile.Encrypt(inp, outp, s, k, salt)).ToLowerInvariant());
            }
            else if (cmd == "decrypt")
            {
                using var inp = File.OpenRead(args[1]);
                using var outp = File.Create(args[2]);
                CipherFile.Decrypt(inp, outp, s, k, salt);
            }
            else if (cmd == "pager-dump")
            {
                // Reads through the pager (hot journal recovery, WAL frames not yet checkpointed) and writes a plain rollback-mode copy.
                using PagerClass p = PagerClass.Open(new FileVfs(), args[1], new PagerOptions { Key = k, Cipher = s, CipherSalt = salt.Length == 0 ? null : salt, ReadOnly = false });
                p.BeginReadSync();
                using var outp = File.Create(args[2]);
                for (uint n = 1; n <= p.PageCount; n++)
                {
                    if (n == p.LockBytePage)
                    {
                        outp.Write(new byte[s.PageSize]);
                        continue;
                    }

                    PageFrame f = p.GetSync(n);
                    if (n == 1)
                    {
                        // fileformat2 1.3.9: versions 1 mean rollback journal, so sqlite3 does not look for a WAL that is not there.
                        byte[] first = (byte[])f.Data.Clone();
                        first[18] = 1;
                        first[19] = 1;
                        outp.Write(first);
                    }
                    else
                    {
                        outp.Write(f.Data);
                    }

                    p.Release(f);
                }

                p.EndReadSync();
                Console.WriteLine("ok " + p.PageCount + (p.IsWal ? " wal" : " rollback"));
            }
            else if (cmd == "check")
            {
                using var inp = File.OpenRead(args[1]);
                using var sink = Stream.Null;
                CipherFile.Decrypt(inp, sink, s, k, salt);
                Console.WriteLine("ok " + (inp.Length / s.PageSize));
            }
            else
            {
                Console.Error.WriteLine("unknown command " + cmd);
                return 2;
            }

            return 0;
        }
        catch (OneLiteException e)
        {
            Console.WriteLine($"error {e.ExtendedCode} {e.Message} [{e.Detail}]");
            return 1;
        }
    }

    private static CipherDigest Digest(string s) => s.ToLowerInvariant() switch
    {
        "sha1" => CipherDigest.Sha1,
        "sha256" => CipherDigest.Sha256,
        "sha512" => CipherDigest.Sha512,
        _ => throw new ArgumentException("digest must be sha1, sha256 or sha512"),
    };
}
