using System;
using System.Threading.Tasks;
using RecursiveAI.OneLite.Storage.Vfs;
using RecursiveAI.OneLite.Storage.Vfs.Browser;

namespace RecursiveAI.OneLite.BrowserTests;

/// <summary>One of the two OPFS file systems under test, and how to drive it: the async one by awaiting, the sync one inline.</summary>
internal sealed class Backend : IDisposable
{
    private static int s_counter;

    private Backend(string name, bool sync, IVfs vfs)
    {
        Name = name;
        Sync = sync;
        Vfs = vfs;
    }

    public string Name { get; }

    /// <summary>True for the worker backend: the pager runs on its synchronous driver.</summary>
    public bool Sync { get; }

    public IVfs Vfs { get; }

    public static async Task<Backend> Create(string kind) => kind switch
    {
        "async" => new Backend("async", false, await OpfsAsyncVfs.CreateAsync().ConfigureAwait(false)),
        "sync" => new Backend("sync", true, await OpfsSyncVfs.CreateAsync().ConfigureAwait(false)),
        _ => throw new ArgumentException(kind),
    };

    /// <summary>A path no other test uses, in a directory of its own.</summary>
    public static string Path(string label) => $"t{++s_counter}-{Environment.TickCount64 % 100000}/{label}.db";

    /// <summary>Waits for background closes (async backend) so the next open sees the files settled.</summary>
    public async Task Settle()
    {
        if (Vfs is OpfsAsyncVfs a)
        {
            await a.DrainAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (Vfs is OpfsSyncVfs s)
        {
            s.Dispose();
        }
    }
}
