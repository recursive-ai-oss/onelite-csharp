using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Durability;

/// <summary>
/// The kill campaign's writer built on OneLite's own storage engine: the same scripted transactions as <see cref="Workload"/>, made with raw
/// B-tree calls on the pager (no SQL layer exists yet), so a SIGKILL lands in the middle of inserts, deletes, balances, overflow chains and
/// auto-vacuum relocation. The result is judged by the pinned library, which recovers the file and runs <c>integrity_check</c>.
/// <c>btree-writer DB LEDGER JOURNAL_MODE SYNCHRONOUS [ps=N] [av=0|1|2] [cache=N] [enc=utf8|utf16le|utf16be]</c>
/// </summary>
internal static class BTreeWriter
{
    public static PagerOptions ParseOptions(string[] a)
    {
        int ps = 4096, av = 0, cache = 2000;
        TextEncoding enc = TextEncoding.Utf8;
        foreach (string extra in a.Skip(4))
        {
            string[] kv = extra.Split('=', 2);
            switch (kv[0])
            {
                case "ps": ps = int.Parse(kv[1], CultureInfo.InvariantCulture); break;
                case "av": av = int.Parse(kv[1], CultureInfo.InvariantCulture); break;
                case "ddl": break;
                case "attach": break;
                case "jmode": break;
                case "vacuum": break;
                case "rtree": case "vec": break;
                case "backup": break;
                case "blob": break;
                case "fts": break;
                case "fts3": break;
                case "cache": cache = int.Parse(kv[1], CultureInfo.InvariantCulture); break;
                case "enc": enc = kv[1] switch { "utf16le" => TextEncoding.Utf16Le, "utf16be" => TextEncoding.Utf16Be, _ => TextEncoding.Utf8 }; break;
                default: throw new InvalidOperationException("unknown writer option " + extra);
            }
        }

        return new PagerOptions
        {
            PageSize = ps,
            AutoVacuum = av != 0,
            IncrementalVacuum = av == 2,
            Encoding = enc,
            CacheSize = cache,
            JournalMode = Enum.Parse<JournalMode>(a[2], ignoreCase: true),
            Synchronous = Enum.Parse<SynchronousMode>(a[3], ignoreCase: true),
        };
    }

    public static int Run(string[] a)
    {
        string dbPath = a[0], ledger = a[1];
        using StorageEngine eng = StorageEngine.Open(new FileVfs(), dbPath, ParseOptions(a));
        eng.BeginRead();
        var db = new BTreeWorkloadDb(eng);
        db.Setup();
        long next = db.ReadCounter() + 1;
        Console.Out.WriteLine("READY");
        Console.Out.Flush();
        using var led = new FileStream(ledger, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        for (long i = next; ; i++)
        {
            db.RunTransaction(i);
            byte[] line = System.Text.Encoding.ASCII.GetBytes(i.ToString(CultureInfo.InvariantCulture) + "\n");
            led.Write(line, 0, line.Length);
            led.Flush();
        }
    }
}
