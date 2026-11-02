using System.Collections.Generic;
using System.IO;
using System.Linq;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Vfs;
using PagerClass = RecursiveAI.OneLite.Storage.Pager.Pager;

namespace RecursiveAI.OneLite.Tests.Pager;

/// <summary>Helpers shared by the pager tests: databases made only of whole-page patterns, so that content is easy to compare.</summary>
internal static class Kit
{
    public const string Db = "test.db";

    /// <summary>Fills a page with a pattern for (page, generation). Page 1 keeps its first 108 bytes (header and the first B-tree header bytes).</summary>
    public static void Fill(PageFrame f, int gen)
    {
        int start = f.PageNumber == 1 ? 108 : 0;
        for (int i = start; i < f.Data.Length; i++)
        {
            f.Data[i] = (byte)((f.PageNumber * 31 + gen * 7 + i) & 0xFF);
        }
    }

    public static PagerOptions Opts(int pageSize = 512, JournalMode jm = JournalMode.Delete, SynchronousMode sm = SynchronousMode.Full, int cache = 100, int yield = 0, int reserved = 0) =>
        new() { PageSize = pageSize, JournalMode = jm, Synchronous = sm, CacheSize = cache, YieldEvery = yield, ReservedBytes = reserved };

    /// <summary>Creates a database of <paramref name="pages"/> pages at generation 0.</summary>
    public static MemoryVfs Prepare(int pages, int pageSize = 512, int reserved = 0)
    {
        var vfs = new MemoryVfs();
        Create(vfs, pages, pageSize, reserved);
        return vfs;
    }

    public static void Create(IVfs vfs, int pages, int pageSize = 512, int reserved = 0)
    {
        using PagerClass p = PagerClass.Open(vfs, Db, Opts(pageSize, reserved: reserved));
        p.BeginReadSync();
        p.BeginWriteSync();
        PageFrame p1 = p.GetSync(1);
        p.MakeWritableSync(p1);
        Fill(p1, 0);
        p.Release(p1);
        for (int i = 2; i <= pages; i++)
        {
            PageFrame f = p.AllocateSync();
            Fill(f, 0);
            p.Release(f);
        }

        p.CommitSync();
        p.EndReadSync();
    }

    public static void Rewrite(PagerClass p, IEnumerable<uint> pages, int gen)
    {
        foreach (uint n in pages)
        {
            PageFrame f = p.GetSync(n);
            p.MakeWritableSync(f);
            Fill(f, gen);
            p.Release(f);
        }
    }

    public static byte[] ReadFile(IVfs vfs, string path)
    {
        using IVfsFile f = vfs.Open(path, VfsOpenOptions.ReadOnly);
        var b = new byte[f.FileSize()];
        f.Read(0, b);
        return b;
    }

    public static bool Exists(IVfs vfs, string path) => vfs.Access(path, AccessMode.Exists);

    public static IEnumerable<uint> Range(uint from, int count) => Enumerable.Range((int)from, count).Select(static i => (uint)i);

    public static string Describe(byte[] a, byte[] b) => a.Length != b.Length ? $"length {a.Length} vs {b.Length}" : "differences at " + string.Join(",", Enumerable.Range(0, a.Length).Where(i => a[i] != b[i]).Take(6).Select(i => $"{i}:{a[i]}/{b[i]}"));
}
