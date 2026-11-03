using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace RecursiveAI.OneLite.Storage.Vfs;

/// <summary>
/// The local-file VFS. Works on any platform with files; it also takes the byte-range locks of fileformat2 and lockingv3, so a process using it and
/// a <c>sqlite3</c> process can use the same database file at the same time, where the platform allows it.
/// <para>
/// <b>Locks.</b> POSIX advisory record locks (<c>fcntl</c>) on Linux and macOS, <c>LockFileEx</c> on Windows, on the byte ranges of fileformat2;
/// <see cref="VfsCaps.MultiProcessLocks"/> is reported where the platform has them (see <c>NativeFile</c>). Elsewhere connections in the same process
/// are still arbitrated, but other processes are not locked out; do not open one file from several processes there.
/// </para>
/// <para>
/// <b>Descriptor hazard.</b> POSIX locks belong to the process and a close of any descriptor of the file drops all of them (sqlite.org howtocorrupt
/// §2.2). All connections in the process therefore share one descriptor per main database file, closed only when the last connection closes.
/// This VFS never opens a main database file just to probe it. Code outside this library that opens and closes the same file while a connection holds
/// a lock defeats this protection.
/// </para>
/// <para>
/// <b>Durability.</b> <see cref="IVfsFile.Sync"/> is <c>fdatasync</c> for <see cref="SyncMode.Normal"/> and <see cref="SyncMode.DataOnly"/> on Linux (what the
/// reference shell is observed to issue), <c>fsync</c> otherwise, and on macOS <see cref="SyncMode.Full"/> is <c>F_FULLFSYNC</c>. <see cref="IVfsFile.SyncDirectory"/>
/// opens the directory, flushes it and closes it on Linux and macOS (an unopenable directory is skipped, a failed flush is an error); on Windows it does nothing.
/// </para>
/// </summary>
public sealed class FileVfs : VfsBase
{
    private static readonly object RegistryGate = new();
    private static readonly Dictionary<string, FileEntry> Registry = new(StringComparer.Ordinal);

    /// <summary>The default instance; the VFS holds no state of its own, because lock state is per process.</summary>
    public static FileVfs Instance { get; } = new();

    /// <summary>True where record locks reach other processes: Linux, macOS and Windows.</summary>
    public static bool HasMultiProcessLocks => NativeFile.HasRecordLocks;

    /// <inheritdoc/>
    public override string Name => "file";

    /// <inheritdoc/>
    public override VfsCaps Caps => FileCaps;

    internal static VfsCaps FileCaps =>
        VfsCaps.Sync | VfsCaps.Async | VfsCaps.SharedMemory | VfsCaps.BlockingOk | (HasMultiProcessLocks ? VfsCaps.MultiProcessLocks : 0);

    /// <inheritdoc/>
    public override string FullPathname(string path)
    {
        string full = Path.GetFullPath(path);
        // Two names for one file must not become two lock tables: follow symbolic links. Hard links and case-folding aliases are not detected (howtocorrupt §2.3).
        // On Linux and macOS every directory component is resolved too, as the reference does (c3ref db_filename reports /private/var/... for /var/... on macOS);
        // components that do not exist are kept as written.
        return OperatingSystem.IsWindows() ? ResolveFinal(full) : ResolveComponents(full);
    }

    private static string ResolveFinal(string full)
    {
        try
        {
            FileSystemInfo? target = new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true);
            return target != null ? target.FullName : full;
        }
        catch (IOException)
        {
            return full;
        }
    }

    private static string ResolveComponents(string full)
    {
        const int MaxLinks = 40;
        string root = Path.GetPathRoot(full) ?? "/";
        string[] pending = full[root.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        var todo = new Stack<string>(System.Linq.Enumerable.Reverse(pending));
        string current = root;
        int links = 0;
        while (todo.Count > 0)
        {
            string part = todo.Pop();
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                current = Path.GetDirectoryName(current.TrimEnd('/')) is { Length: > 0 } up ? up : root;
                continue;
            }

            string next = Path.Combine(current, part);
            string? target = null;
            try
            {
                target = File.ResolveLinkTarget(next, returnFinalTarget: false)?.FullName;
                if (target == null && Directory.Exists(next))
                {
                    target = Directory.ResolveLinkTarget(next, returnFinalTarget: false)?.FullName;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            if (target == null)
            {
                current = next;
                continue;
            }

            if (++links > MaxLinks)
            {
                return full;
            }

            // A link target replaces the component; its own components are resolved in turn.
            current = root;
            foreach (string p in System.Linq.Enumerable.Reverse(target[(Path.GetPathRoot(target) ?? "/").Length..].Split('/', StringSplitOptions.RemoveEmptyEntries)))
            {
                todo.Push(p);
            }
        }

        return current;
    }

    /// <inheritdoc/>
    public override bool Access(string path, AccessMode mode)
    {
        // Never open the file here: closing a descriptor of a locked file would drop the process's locks.
        string full = Path.GetFullPath(path);
        bool exists = File.Exists(full) || Directory.Exists(full);
        if (!exists || mode == AccessMode.Exists)
        {
            return exists;
        }

        return (File.GetAttributes(full) & FileAttributes.ReadOnly) == 0;
    }

    /// <inheritdoc/>
    public override void Delete(string path, bool syncDirectory)
    {
        string full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new VfsException(VfsCodes.IoErrDeleteNoEnt, $"cannot delete '{path}': no such file");
        }

        try
        {
            File.Delete(full);
            if (syncDirectory)
            {
                NativeFile.SyncDirectory(Path.GetDirectoryName(full));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new VfsException(VfsCodes.IoErrDelete, $"cannot delete '{path}': {e.Message}", e);
        }
    }

    /// <inheritdoc/>
    public override IVfsFile Open(string path, VfsOpenOptions flags)
    {
        if (path.Length == 0)
        {
            string temp = Path.Combine(Path.GetTempPath(), "onelite-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) + ".tmp");
            return OpenPrivate(temp, flags | VfsOpenOptions.Create | VfsOpenOptions.ReadWrite | VfsOpenOptions.Exclusive | VfsOpenOptions.DeleteOnClose);
        }

        string full = FullPathname(path);
        return (flags & VfsOpenOptions.MainDb) != 0 ? OpenMain(full, flags) : OpenPrivate(full, flags);
    }

    private static FileVfsFile OpenPrivate(string full, VfsOpenOptions flags)
    {
        SafeFileHandle h = OpenHandle(full, flags, flags);
        var entry = new FileEntry(full, h, writable: (flags & (VfsOpenOptions.ReadWrite | VfsOpenOptions.Create)) != 0, shared: false);
        return new FileVfsFile(full, entry, flags);
    }

    private static FileVfsFile OpenMain(string full, VfsOpenOptions flags)
    {
        lock (RegistryGate)
        {
            if (Registry.TryGetValue(full, out FileEntry? entry))
            {
                if (File.Exists(full))
                {
                    if ((flags & (VfsOpenOptions.Create | VfsOpenOptions.Exclusive)) == (VfsOpenOptions.Create | VfsOpenOptions.Exclusive))
                    {
                        throw new VfsException(VfsCodes.CantOpen, $"unable to open database file '{full}': it already exists");
                    }

                    if ((flags & (VfsOpenOptions.ReadWrite | VfsOpenOptions.Create)) != 0 && !entry.Writable)
                    {
                        throw new VfsException(VfsCodes.CantOpen, $"unable to open database file '{full}' for writing");
                    }

                    entry.Refs++;
                    return new FileVfsFile(full, entry, flags);
                }

                // The file was removed or replaced behind our back: later opens see a new file, the old descriptor lives on for its holders.
                Registry.Remove(full);
                entry.Shared = false;
            }

            // A connection that only reads still opens read-write when it can, so a later writer in this process can share the descriptor.
            bool wantWrite = (flags & (VfsOpenOptions.ReadWrite | VfsOpenOptions.Create)) != 0;
            SafeFileHandle h;
            bool writable;
            if (wantWrite)
            {
                h = OpenHandle(full, flags, flags);
                writable = true;
            }
            else
            {
                try
                {
                    h = OpenHandle(full, flags, flags | VfsOpenOptions.ReadWrite);
                    writable = true;
                }
                catch (VfsException) when (File.Exists(full))
                {
                    h = OpenHandle(full, flags, flags);
                    writable = false;
                }
            }

            var created = new FileEntry(full, h, writable, shared: true);
            Registry[full] = created;
            return new FileVfsFile(full, created, flags);
        }
    }

    private static SafeFileHandle OpenHandle(string full, VfsOpenOptions flags, VfsOpenOptions effective)
    {
        bool write = (effective & (VfsOpenOptions.ReadWrite | VfsOpenOptions.Create)) != 0;
        FileMode mode = (effective & VfsOpenOptions.Create) == 0 ? FileMode.Open
            : (effective & VfsOpenOptions.Exclusive) != 0 ? FileMode.CreateNew : FileMode.OpenOrCreate;
        // On Windows a synchronous read or write on an overlapped (Asynchronous) handle allocates per call in the runtime, and local disk gains nothing
        // from overlapped I/O; the files there are opened synchronously and the async methods run inline (FileVfsFile checks Handle.IsAsync).
        FileOptions options = (OperatingSystem.IsWindows() ? FileOptions.None : FileOptions.Asynchronous) | ((flags & VfsOpenOptions.DeleteOnClose) != 0 ? FileOptions.DeleteOnClose : 0);
        try
        {
            // FileShare.ReadWrite|Delete keeps the runtime's own advisory locking from blocking another process (sqlite.org lockingv3 covers all sharing).
            if ((flags & VfsOpenOptions.MainDb) != 0 && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
            {
                SafeFileHandle? native = NativeFile.OpenForLocking(full, write, (effective & VfsOpenOptions.Create) != 0, (effective & VfsOpenOptions.Exclusive) != 0, out int errno);
                if (native == null)
                {
                    throw new VfsException(errno == 21 ? VfsCodes.CantOpenIsDir : VfsCodes.CantOpen, $"unable to open file '{full}' (errno {errno})");
                }

                return native;
            }

            return File.OpenHandle(full, mode, write ? FileAccess.ReadWrite : FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, options);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            int code = Directory.Exists(full) ? VfsCodes.CantOpenIsDir : VfsCodes.CantOpen;
            throw new VfsException(code, $"unable to open file '{full}': {e.Message}", e);
        }
    }

    internal static void Release(FileEntry entry)
    {
        lock (RegistryGate)
        {
            if (--entry.Refs > 0)
            {
                return;
            }

            if (entry.Shared && Registry.TryGetValue(entry.Key, out FileEntry? cur) && ReferenceEquals(cur, entry))
            {
                Registry.Remove(entry.Key);
            }

            // Every connection has let go of its locks by now, so closing the descriptor drops nothing that is still wanted.
            entry.CloseAll();
        }
    }
}

/// <summary>One open file shared by every connection of this process that has it open (see the descriptor hazard in <see cref="FileVfs"/>).</summary>
internal sealed class FileEntry
{
    public FileEntry(string key, SafeFileHandle handle, bool writable, bool shared)
    {
        Key = key;
        Handle = handle;
        Writable = writable;
        Shared = shared;
        Refs = 1;
        Locks = new FileLockState(shared ? RangeLocksFor(handle, writable) : NoRangeLocks.Instance);
    }

    public string Key { get; }

    public SafeFileHandle Handle { get; }

    public bool Writable { get; }

    public bool Shared { get; set; }

    public int Refs { get; set; }

    public FileLockState Locks { get; }

    /// <summary>Record locks on this descriptor where the platform has them: fcntl on Linux and macOS, LockFileEx on Windows.</summary>
    internal static IRangeLocks RangeLocksFor(SafeFileHandle handle, bool writable)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsRangeLocks(handle, writable);
        }

        return OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() ? new PosixRangeLocks(handle, writable) : NoRangeLocks.Instance;
    }

    public void CloseAll()
    {
        Handle.Dispose();
    }
}
