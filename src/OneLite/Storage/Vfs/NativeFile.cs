using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace RecursiveAI.OneLite.Storage.Vfs;

/// <summary>
/// The only place the library calls the operating system directly. The .NET base library cannot take byte-range locks that another process
/// honours on macOS (it refuses), takes only exclusive ones on Windows, picks read or write locks from the stream's access mode, cannot ask whether a
/// lock is held, cannot flush a directory and has no macOS full flush; and the runtime's own whole-file <c>flock</c> on open collides with record locks on macOS, so database files are opened here instead. These few calls fill those gaps:
/// <list type="bullet">
/// <item><description>Unix: <c>fcntl</c> record locks (<c>F_SETLK</c>, <c>F_GETLK</c>), <c>open</c> for database and wal-index files, <c>open</c> + <c>fsync</c> + <c>close</c> on a directory, <c>fdatasync</c> (Linux), <c>F_FULLFSYNC</c> (macOS).</description></item>
/// <item><description>Windows: <c>LockFileEx</c> and <c>UnlockFileEx</c> (the Windows locks sqlite.org's lockingv3 and fileformat2 describe for the lock-byte page).</description></item>
/// </list>
/// Every entry is guarded by <c>OperatingSystem.Is*</c>, so other platforms (browser, WASI) never reach it. No database library is loaded; the
/// libraries named are the C library and kernel32. The <c>struct flock</c> layouts and constants come from the platforms' <c>fcntl(2)</c> manual pages and libc headers.
/// </summary>
internal static partial class NativeFile
{
    /// <summary>True where this library can take record locks that other processes honour.</summary>
    internal static bool HasRecordLocks => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();

    /// <summary>Counts directory flushes that reached the operating system, so a test can see that the call was made.</summary>
    internal static long DirectorySyncCalls;

    // ---- Unix: struct flock ----

    // Linux, 64-bit (x86-64 and arm64): short l_type, short l_whence, off_t l_start, off_t l_len, pid_t l_pid (fcntl(2); 32 bytes with padding).
    [StructLayout(LayoutKind.Sequential)]
    private struct FlockLinux
    {
        public short Type;
        public short Whence;
        public long Start;
        public long Len;
        public int Pid;
    }

    // macOS: off_t l_start, off_t l_len, pid_t l_pid, short l_type, short l_whence (24 bytes).
    [StructLayout(LayoutKind.Sequential)]
    private struct FlockMac
    {
        public long Start;
        public long Len;
        public int Pid;
        public short Type;
        public short Whence;
    }

    // Linux: F_RDLCK 0, F_WRLCK 1, F_UNLCK 2, F_GETLK 5, F_SETLK 6. macOS: F_RDLCK 1, F_UNLCK 2, F_WRLCK 3, F_GETLK 7, F_SETLK 8, F_FULLFSYNC 51.
    private static short LockTypeFor(RangeLockKind kind) => OperatingSystem.IsMacOS()
        ? kind switch { RangeLockKind.Read => (short)1, RangeLockKind.Write => (short)3, _ => (short)2 }
        : kind switch { RangeLockKind.Read => (short)0, RangeLockKind.Write => (short)1, _ => (short)2 };

    private static int GetLkCmd => OperatingSystem.IsMacOS() ? 7 : 5;

    private static int SetLkCmd => OperatingSystem.IsMacOS() ? 8 : 6;

    private const int FFullFsync = 51;
    private const int OpenReadOnlyLinux = 0x80000;      // O_RDONLY | O_CLOEXEC
    private const int OpenReadOnlyMac = 0x1000000;      // O_RDONLY | O_CLOEXEC
    private const int ErrnoAgainLinux = 11;
    private const int ErrnoAgainMac = 35;
    private const int ErrnoAccess = 13;

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int FcntlLinux(int fd, int cmd, ref FlockLinux arg);

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int FcntlMac(int fd, int cmd, ref FlockMac arg);

    // fcntl takes its third argument as a C variadic. Apple's arm64 convention passes every variadic argument on the stack, not in registers, so
    // six ignored arguments fill x2-x7 and push the real one into the first stack slot, where fcntl reads it.
    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int FcntlMacArm64(int fd, int cmd, nint r2, nint r3, nint r4, nint r5, nint r6, nint r7, ref FlockMac arg);

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int FcntlNoArg(int fd, int cmd);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenPath(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int ClosePath(int fd);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int fd);

    [LibraryImport("libc", EntryPoint = "fdatasync", SetLastError = true)]
    private static partial int Fdatasync(int fd);

    // open(2) is a C variadic too (the mode follows the flags); the Apple arm64 stack rule applies as for fcntl.
    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenCreate(string path, int flags, uint mode);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenCreateMacArm64(string path, int flags, nint r2, nint r3, nint r4, nint r5, nint r6, nint r7, uint mode);

    // Flags from the platforms' open(2) pages. Linux (x86-64 and arm64): O_RDWR 2, O_CREAT 0x40, O_EXCL 0x80, O_CLOEXEC 0x80000.
    // macOS: O_RDWR 2, O_CREAT 0x200, O_EXCL 0x800, O_CLOEXEC 0x1000000.
    private const uint FileMode0644 = 0x1A4; // rw-r--r--, reduced by the umask

    /// <summary>
    /// Opens a database file the way the reference library does, with <c>open(2)</c>, instead of through the runtime. The runtime takes a whole-file
    /// advisory <c>flock</c> on every file it opens; on macOS that is the same mechanism as record locks, so it would fail the open while another
    /// process holds a write lock on the lock bytes, and would make our own record locks fail. Returns null and the errno on failure.
    /// </summary>
    internal static SafeFileHandle? OpenForLocking(string path, bool write, bool create, bool exclusive, out int errno)
    {
        bool mac = OperatingSystem.IsMacOS();
        int flags = (write ? 2 : 0) | (mac ? 0x1000000 : 0x80000);
        if (create)
        {
            flags |= mac ? 0x200 : 0x40;
            if (exclusive)
            {
                flags |= mac ? 0x800 : 0x80;
            }
        }

        int fd = mac && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? OpenCreateMacArm64(path, flags, 0, 0, 0, 0, 0, 0, FileMode0644)
            : OpenCreate(path, flags, FileMode0644);
        if (fd < 0)
        {
            errno = Marshal.GetLastPInvokeError();
            return null;
        }

        errno = 0;
        return new SafeFileHandle(fd, ownsHandle: true);
    }

    private static int FcntlLock(int fd, int cmd, RangeLockKind kind, long position, long length, out short resultType)
    {
        if (OperatingSystem.IsMacOS())
        {
            var f = new FlockMac { Type = LockTypeFor(kind), Whence = 0, Start = position, Len = length };
            int r = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? FcntlMacArm64(fd, cmd, 0, 0, 0, 0, 0, 0, ref f)
                : FcntlMac(fd, cmd, ref f);
            resultType = f.Type;
            return r;
        }

        var l = new FlockLinux { Type = LockTypeFor(kind), Whence = 0, Start = position, Len = length };
        int rl = FcntlLinux(fd, cmd, ref l);
        resultType = l.Type;
        return rl;
    }

    /// <summary>
    /// Sets, upgrades, downgrades or clears the process's lock on a byte range without waiting. Returns false if another process holds a
    /// conflicting lock (EAGAIN or EACCES, fcntl(2)); throws for other errors.
    /// </summary>
    internal static bool TryUnixLock(int fd, long position, long length, RangeLockKind kind)
    {
        if (FcntlLock(fd, SetLkCmd, kind, position, length, out _) == 0)
        {
            return true;
        }

        int errno = Marshal.GetLastPInvokeError();
        if (kind != RangeLockKind.Unlock && (errno == (OperatingSystem.IsMacOS() ? ErrnoAgainMac : ErrnoAgainLinux) || errno == ErrnoAccess))
        {
            return false;
        }

        int code = kind == RangeLockKind.Unlock ? VfsCodes.IoErrUnlock : kind == RangeLockKind.Read ? VfsCodes.IoErrRdLock : VfsCodes.IoErrLock;
        throw new VfsException(code, $"byte-range lock at {position} failed (errno {errno})");
    }

    /// <summary>Asks whether another process holds a lock that would stop us writing the range (F_GETLK; it never changes any lock).</summary>
    internal static bool UnixHeldByOther(int fd, long position, long length)
    {
        if (FcntlLock(fd, GetLkCmd, RangeLockKind.Write, position, length, out short type) != 0)
        {
            throw new VfsException(VfsCodes.IoErrCheckReservedLock, $"lock query at {position} failed (errno {Marshal.GetLastPInvokeError()})");
        }

        return type != LockTypeFor(RangeLockKind.Unlock);
    }

    /// <summary>fsync, or fdatasync where asked and the platform has it, or on macOS the full flush through the drive cache (then plain fsync if the file system refuses it).</summary>
    internal static void UnixFlush(int fd, SyncMode mode)
    {
        if (OperatingSystem.IsMacOS() && (mode & SyncMode.Full) != 0)
        {
            if (FcntlNoArg(fd, FFullFsync) != -1)
            {
                return;
            }
        }

        int r = OperatingSystem.IsLinux() && (mode & SyncMode.Full) == 0 ? Fdatasync(fd) : Fsync(fd);
        if (r != 0)
        {
            throw new VfsException(VfsCodes.IoErrFsync, $"sync failed (errno {Marshal.GetLastPInvokeError()})");
        }
    }

    /// <summary>
    /// Makes a directory's entries durable: open it read-only, fsync, close. Failing to open the directory is ignored (the file is still synced);
    /// failing to flush it is an error. Does nothing on Windows, where directories cannot be flushed this way.
    /// </summary>
    internal static void SyncDirectory(string? directory)
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) || string.IsNullOrEmpty(directory))
        {
            return;
        }

        int fd = OpenPath(directory, OperatingSystem.IsMacOS() ? OpenReadOnlyMac : OpenReadOnlyLinux);
        if (fd < 0)
        {
            return;
        }

        try
        {
            if (Fsync(fd) != 0)
            {
                throw new VfsException(VfsCodes.IoErrDirFsync, $"sync of directory '{directory}' failed (errno {Marshal.GetLastPInvokeError()})");
            }

            Interlocked.Increment(ref DirectorySyncCalls);
        }
        finally
        {
            _ = ClosePath(fd); // the descriptor was only read-only open; a failed close loses nothing
        }
    }

    // ---- Windows ----

    private const uint LockFailImmediately = 1;
    private const uint LockExclusive = 2;
    private const int ErrorLockViolation = 33;
    private const int ErrorNotLocked = 158;
    private const int ErrorIoPending = 997;

    // OVERLAPPED: the file offset of the range lives in Offset/OffsetHigh (LockFileEx documentation).
    [StructLayout(LayoutKind.Sequential)]
    private struct Overlapped
    {
        public nuint Internal;
        public nuint InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public nint Event;
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LockFileEx(SafeFileHandle file, uint flags, uint reserved, uint lengthLow, uint lengthHigh, ref Overlapped overlapped);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnlockFileEx(SafeFileHandle file, uint reserved, uint lengthLow, uint lengthHigh, ref Overlapped overlapped);

    /// <summary>Takes a shared or exclusive lock on a byte range without waiting. False means another holder has a conflicting lock.</summary>
    [SupportedOSPlatform("windows")]
    internal static bool TryWindowsLock(SafeFileHandle file, long position, long length, bool exclusive)
    {
        Overlapped ov = Range(position);
        uint flags = LockFailImmediately | (exclusive ? LockExclusive : 0);
        if (LockFileEx(file, flags, 0, (uint)length, (uint)(length >> 32), ref ov))
        {
            return true;
        }

        int err = Marshal.GetLastPInvokeError();
        if (err is ErrorLockViolation or ErrorIoPending)
        {
            return false;
        }

        throw new VfsException(exclusive ? VfsCodes.IoErrLock : VfsCodes.IoErrRdLock, $"LockFileEx at {position} failed (error {err})");
    }

    /// <summary>Releases a byte range. Releasing a range that is not locked is not an error.</summary>
    [SupportedOSPlatform("windows")]
    internal static void WindowsUnlock(SafeFileHandle file, long position, long length)
    {
        Overlapped ov = Range(position);
        if (!UnlockFileEx(file, 0, (uint)length, (uint)(length >> 32), ref ov))
        {
            int err = Marshal.GetLastPInvokeError();
            if (err != ErrorNotLocked)
            {
                throw new VfsException(VfsCodes.IoErrUnlock, $"UnlockFileEx at {position} failed (error {err})");
            }
        }
    }

    // The files are opened FileOptions.Asynchronous, so their handles are bound to the runtime's I/O completion port. A lock call given an
    // OVERLAPPED with no event would queue a completion packet for memory the runtime does not own, and the thread pool's completion poller
    // would crash on it. A set low-order bit in hEvent suppresses the packet (GetQueuedCompletionStatus documentation); the lock calls here
    // use LOCKFILE_FAIL_IMMEDIATELY and finish before returning, so nothing waits on the event.
    private const nint NoCompletionPacket = 1;

    private static Overlapped Range(long position) => new() { Offset = (uint)position, OffsetHigh = (uint)(position >> 32), Event = NoCompletionPacket };
}

/// <summary>Record locks through <c>fcntl</c> on a file descriptor (Linux and macOS). One lock owner per process: see <see cref="FileLockState"/>.</summary>
internal sealed class PosixRangeLocks : IRangeLocks
{
    private readonly int _fd;
    private readonly bool _writable;

    public PosixRangeLocks(SafeFileHandle handle, bool writable)
    {
        _fd = (int)handle.DangerousGetHandle(); // the descriptor number; the owner keeps the handle open for as long as this object is used
        _writable = writable;
    }

    public bool Try(long position, long length, RangeLockKind kind)
    {
        if (kind == RangeLockKind.Write && !_writable)
        {
            return false; // a write lock needs a descriptor open for writing
        }

        return NativeFile.TryUnixLock(_fd, position, length, kind);
    }

    public bool HeldByOther(long position, long length) => NativeFile.UnixHeldByOther(_fd, position, length);
}

/// <summary>
/// Locks through <c>LockFileEx</c> (Windows). Windows cannot convert a lock in place, so a change between shared and exclusive on a range releases
/// it first and, if the new lock is refused, takes the old one back (the same dance lockingv3 describes for Windows).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsRangeLocks : IRangeLocks
{
    private readonly SafeFileHandle _file;
    private readonly bool _writable;
    private readonly Dictionary<long, RangeLockKind> _held = [];

    public WindowsRangeLocks(SafeFileHandle file, bool writable)
    {
        _file = file;
        _writable = writable;
    }

    public bool Try(long position, long length, RangeLockKind kind)
    {
        _held.TryGetValue(position, out RangeLockKind before);
        bool had = _held.ContainsKey(position);
        if (kind == RangeLockKind.Unlock)
        {
            if (had)
            {
                NativeFile.WindowsUnlock(_file, position, length);
                _held.Remove(position);
            }

            return true;
        }

        if (kind == RangeLockKind.Write && !_writable)
        {
            return false;
        }

        if (had)
        {
            if (before == kind)
            {
                return true;
            }

            NativeFile.WindowsUnlock(_file, position, length);
        }

        if (NativeFile.TryWindowsLock(_file, position, length, kind == RangeLockKind.Write))
        {
            _held[position] = kind;
            return true;
        }

        if (had)
        {
            if (NativeFile.TryWindowsLock(_file, position, length, before == RangeLockKind.Write))
            {
                _held[position] = before;
            }
            else
            {
                _held.Remove(position);
            }
        }

        return false;
    }

    public bool HeldByOther(long position, long length)
    {
        // There is no query call: try for an exclusive lock and give it back.
        if (_held.ContainsKey(position))
        {
            return false;
        }

        if (!NativeFile.TryWindowsLock(_file, position, length, true))
        {
            return true;
        }

        NativeFile.WindowsUnlock(_file, position, length);
        return false;
    }
}
