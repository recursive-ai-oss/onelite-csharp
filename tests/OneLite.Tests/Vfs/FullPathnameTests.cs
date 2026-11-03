using System.IO;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Tests.Vfs;

/// <summary>The file VFS reports the same full path as the reference: symbolic links in any directory component are followed (c3ref db_filename).</summary>
public sealed class FullPathnameTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-fullpath-" + Guid.NewGuid().ToString("N"));

    public FullPathnameTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void SymbolicLinkInADirectoryComponentIsFollowed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string real = Path.Combine(Path.GetFullPath(_dir) is var d ? FileVfs.Instance.FullPathname(d) : d, "real");
        Directory.CreateDirectory(real);
        string link = Path.Combine(_dir, "link");
        Directory.CreateSymbolicLink(link, "real");
        string viaLink = Path.Combine(link, "sub", "x.db");

        Assert.Equal(Path.Combine(real, "sub", "x.db"), FileVfs.Instance.FullPathname(viaLink));
        Assert.Equal(Path.Combine(real, "x.db"), FileVfs.Instance.FullPathname(Path.Combine(link, ".", "sub", "..", "x.db")));
    }
}
