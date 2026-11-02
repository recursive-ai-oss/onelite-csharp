using System.Diagnostics;
using System.IO;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Tests.Vfs;

/// <summary>The directory flush that follows creating or deleting a journal (sqlite.org atomiccommit) really reaches the operating system.</summary>
public sealed class DirectorySyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-dsync-" + Guid.NewGuid().ToString("N"));

    public DirectorySyncTests()
    {
        Directory.CreateDirectory(_dir);
    }

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
    public void SyncDirectoryAndDeleteWithSyncCountAsDirectoryFlushes()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "directories are flushed on Linux and macOS");
        string p = Path.Combine(_dir, "j-journal");
        using IVfsFile f = FileVfs.Instance.Open(p, VfsOpenOptions.MainJournal | VfsOpenOptions.ReadWrite | VfsOpenOptions.Create);
        f.Write(0, new byte[512]);
        f.Sync(SyncMode.Normal);
        long before = NativeFile.DirectorySyncCalls;
        f.SyncDirectory();
        // other tests in the process may flush directories too, so the counter is only ever checked for "at least"
        Assert.True(NativeFile.DirectorySyncCalls >= before + 1);
        f.Dispose();
        long mid = NativeFile.DirectorySyncCalls;
        FileVfs.Instance.Delete(p, syncDirectory: true);
        Assert.True(NativeFile.DirectorySyncCalls >= mid + 1);
    }

    [Fact]
    public void FullAndNormalSyncsSucceedOnEveryFlavour()
    {
        using IVfsFile f = FileVfs.Instance.Open(Path.Combine(_dir, "s.db"), VfsOpenOptions.MainDb | VfsOpenOptions.ReadWrite | VfsOpenOptions.Create);
        f.Write(0, new byte[100]);
        f.Sync(SyncMode.Normal);
        f.Sync(SyncMode.Full);
        f.Sync(SyncMode.DataOnly | SyncMode.Normal);
    }

    [Fact]
    public void StraceShowsTheDirectoryOpenedAndFlushed()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists("/usr/bin/strace"), "needs Linux with strace");
        string log = Path.Combine(_dir, "strace.log");
        string exe = Environment.ProcessPath!;
        var psi = new ProcessStartInfo("/usr/bin/strace") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in new[] { "-f", "-e", "trace=openat,fsync,fdatasync,close", "-o", log, exe })
        {
            psi.ArgumentList.Add(a);
        }

        if (Path.GetFileNameWithoutExtension(exe) == "dotnet")
        {
            psi.ArgumentList.Add(typeof(DirectorySyncTests).Assembly.Location);
        }

        psi.ArgumentList.Add("-method");
        psi.ArgumentList.Add("*SyncDirectoryChildProbe");
        psi.Environment["ONELITE_DSYNC_CHILD"] = _dir;
        using Process p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(120000));

        string[] lines = File.ReadAllLines(log);
        // the probe's journal is named "probe-journal"; its directory is _dir
        int create = Array.FindIndex(lines, l => l.Contains("probe-journal", StringComparison.Ordinal) && l.Contains("O_CREAT", StringComparison.Ordinal));
        Assert.True(create >= 0, "journal open not seen");
        int dirOpen = Array.FindIndex(lines, create, l => l.Contains($"openat(AT_FDCWD, \"{_dir}\", O_RDONLY", StringComparison.Ordinal));
        Assert.True(dirOpen > create, "directory open not seen after journal creation");
        string fdText = lines[dirOpen][(lines[dirOpen].LastIndexOf('=') + 1)..].Trim();
        Assert.Contains(lines[(dirOpen + 1)..], l => l.Contains($"fsync({fdText})", StringComparison.Ordinal));
    }

    [Fact]
    public void SyncDirectoryChildProbe()
    {
        string? dir = Environment.GetEnvironmentVariable("ONELITE_DSYNC_CHILD");
        Assert.SkipUnless(dir != null, "runs only as the child of the strace test");
        using IVfsFile f = FileVfs.Instance.Open(Path.Combine(dir!, "probe-journal"), VfsOpenOptions.MainJournal | VfsOpenOptions.ReadWrite | VfsOpenOptions.Create);
        f.Write(0, new byte[512]);
        f.Sync(SyncMode.Normal);
        f.SyncDirectory();
    }
}
