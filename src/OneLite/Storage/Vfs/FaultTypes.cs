namespace RecursiveAI.OneLite.Storage.Vfs;

/// <summary>The calls <see cref="FaultVfs"/> can fail on demand.</summary>
public enum FaultCall
{
    /// <summary>Opening a file.</summary>
    Open,
    /// <summary>A read.</summary>
    Read,
    /// <summary>A write (or the write run).</summary>
    Write,
    /// <summary>A truncate.</summary>
    Truncate,
    /// <summary>A sync.</summary>
    Sync,
    /// <summary>A directory sync.</summary>
    SyncDirectory,
    /// <summary>A file size query.</summary>
    FileSize,
    /// <summary>A delete.</summary>
    Delete,
    /// <summary>A lock request.</summary>
    Lock,
}

/// <summary>The calls that change what is on the device. Each one is a crash point: the log numbers them from 0.</summary>
public enum FaultOpKind
{
    /// <summary>A file was created.</summary>
    Create,
    /// <summary>Bytes were written.</summary>
    Write,
    /// <summary>The file length was set.</summary>
    Truncate,
    /// <summary>A sync was issued.</summary>
    Sync,
    /// <summary>A directory sync was issued.</summary>
    SyncDirectory,
    /// <summary>A file was deleted.</summary>
    Delete,
}

/// <summary>One logged storage call. <see cref="Index"/> is its crash point: a crash "after n ops" has seen the ops with index below n.</summary>
public sealed class FaultOp
{
    internal FaultOp(long index, FaultOpKind kind, string path, int node, long offset, long size, byte[]? data, bool lost, bool syncDirectory)
    {
        Index = index;
        Kind = kind;
        Path = path;
        Node = node;
        Offset = offset;
        Size = size;
        Data = data;
        SyncLost = lost;
        SyncDirectory = syncDirectory;
    }

    /// <summary>The position in the log, from 0.</summary>
    public long Index { get; }

    /// <summary>What kind of call it was.</summary>
    public FaultOpKind Kind { get; }

    /// <summary>The path of the file (the backend's absolute name).</summary>
    public string Path { get; }

    internal int Node { get; }

    /// <summary>For writes, the byte offset.</summary>
    public long Offset { get; }

    /// <summary>For writes, the length; for truncates, the new size.</summary>
    public long Size { get; }

    internal byte[]? Data { get; }

    /// <summary>For syncs: the device ignored it (a "lost fsync").</summary>
    public bool SyncLost { get; }

    /// <summary>For deletes: the directory entry removal was synced in the same call.</summary>
    public bool SyncDirectory { get; }

    /// <inheritdoc/>
    public override string ToString() => $"#{Index} {Kind} {Path}" + (Kind == FaultOpKind.Write ? $" @{Offset}+{Size}" : Kind == FaultOpKind.Truncate ? $" ->{Size}" : string.Empty) + (SyncLost ? " (lost)" : string.Empty);
}

/// <summary>An injected failure: the <see cref="Nth"/> call of kind <see cref="Call"/> throws <see cref="VfsException"/> with <see cref="ExtendedCode"/>.</summary>
public sealed class FaultRule
{
    /// <summary>Which call.</summary>
    public FaultCall Call { get; init; }

    /// <summary>Which occurrence of that call, counting from 1 across the whole VFS.</summary>
    public int Nth { get; init; } = 1;

    /// <summary>The extended result code to throw, for example <see cref="VfsCodes.IoErrWrite"/> or <see cref="VfsCodes.Full"/>.</summary>
    public int ExtendedCode { get; init; } = VfsCodes.IoErr;

    /// <summary>For a failing write: how many leading bytes reach the file before the failure (a partial write, as a full disk produces).</summary>
    public int PartialBytes { get; init; }

    /// <summary>If true the rule keeps firing for every later call of that kind too.</summary>
    public bool Persistent { get; init; }
}

/// <summary>Thrown by every call after <see cref="FaultVfs.HaltAfter"/> has run out: the simulated power cut. Deliberately not a <see cref="VfsException"/>, so no layer mistakes it for an I/O error it may handle.</summary>
public sealed class PowerLossException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PowerLossException()
        : base("simulated power loss")
    {
    }
}

/// <summary>What happens to writes that were not synced when the power goes.</summary>
public enum CrashMode
{
    /// <summary>Every unsynced change is lost.</summary>
    DropUnsynced,
    /// <summary>Every issued change reached the device (a crash of the process, not of the machine).</summary>
    KeepAll,
    /// <summary>Each unsynced change survives or not, as drawn from the seed, in the way <see cref="CrashPolicy"/> allows.</summary>
    Random,
}

/// <summary>What the bytes of a torn sector hold where the write did not arrive.</summary>
public enum TornFill
{
    /// <summary>The old bytes stay.</summary>
    Keep,
    /// <summary>Zeros.</summary>
    Zeros,
    /// <summary>Seeded random bytes.</summary>
    Random,
}

/// <summary>How to turn the log up to a crash point into the files the next start finds (sqlite.org atomiccommit §2: sector-granular, unordered, not atomic).</summary>
public sealed class CrashPolicy
{
    /// <summary>All unsynced changes lost.</summary>
    public static CrashPolicy DropUnsynced { get; } = new() { Mode = CrashMode.DropUnsynced };

    /// <summary>All issued changes kept.</summary>
    public static CrashPolicy KeepAll { get; } = new() { Mode = CrashMode.KeepAll };

    /// <summary>Which changes survive.</summary>
    public CrashMode Mode { get; init; } = CrashMode.Random;

    /// <summary>The seed for <see cref="CrashMode.Random"/>. The same seed on the same log gives the same files.</summary>
    public ulong Seed { get; init; } = 1;

    /// <summary>The chance that an unsynced sector write (or truncate, or directory change) survives.</summary>
    public double Survival { get; init; } = 0.5;

    /// <summary>If true the surviving changes land in any order, so an older write to a sector can end up over a newer one. If false they form a prefix of the issue order.</summary>
    public bool Reorder { get; init; } = true;

    /// <summary>If true a surviving sector write may be cut short: only its first or only its last bytes arrive (the sector is written linearly, atomiccommit §2.2).</summary>
    public bool TearSectors { get; init; }

    /// <summary>What the part of a torn write that did not arrive holds.</summary>
    public TornFill TornFill { get; init; } = TornFill.Keep;
}

/// <summary>The files a restart would find after a simulated crash.</summary>
public sealed class CrashImage
{
    internal CrashImage(SortedDictionary<string, byte[]> files, long afterOps)
    {
        Files = files;
        AfterOps = afterOps;
    }

    /// <summary>The crash point: the ops with a lower index were issued.</summary>
    public long AfterOps { get; }

    /// <summary>Every file that exists, by path (ordinal order), with its bytes.</summary>
    public IReadOnlyDictionary<string, byte[]> Files { get; }

    /// <summary>Creates a fresh in-memory VFS holding these files.</summary>
    public MemoryVfs ToMemoryVfs()
    {
        var vfs = new MemoryVfs();
        foreach (KeyValuePair<string, byte[]> f in Files)
        {
            vfs.Import(f.Key, f.Value);
        }

        return vfs;
    }

    /// <summary>Writes the files into <paramref name="target"/> (for example a <see cref="FileVfs"/>), renaming each path with <paramref name="map"/> if given, and syncs them.</summary>
    public void WriteTo(IVfs target, Func<string, string>? map = null)
    {
        foreach (KeyValuePair<string, byte[]> f in Files)
        {
            string path = map == null ? f.Key : map(f.Key);
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && target is FileVfs)
            {
                Directory.CreateDirectory(dir);
            }

            using IVfsFile file = target.Open(path, VfsOpenOptions.Create | VfsOpenOptions.ReadWrite);
            file.Truncate(0);
            file.Write(0, f.Value);
            file.Sync(SyncMode.Full);
        }
    }

    /// <summary>A hash over every path and content, for comparing images.</summary>
    public string Fingerprint()
    {
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (KeyValuePair<string, byte[]> f in Files)
        {
            sha.AppendData(System.Text.Encoding.UTF8.GetBytes(f.Key));
            sha.AppendData(BitConverter.GetBytes(f.Value.LongLength));
            sha.AppendData(f.Value);
        }

        return Convert.ToHexString(sha.GetHashAndReset());
    }
}

/// <summary>Options of a <see cref="FaultVfs"/>.</summary>
public sealed class FaultOptions
{
    /// <summary>The device sector size used to cut writes into units that survive or not. Default 512, the pessimistic assumption of atomiccommit §2.</summary>
    public int SectorSize { get; init; } = 512;

    /// <summary>If true a created or deleted file is durable only after a directory sync (the strict reading of atomiccommit); if false a sync of the file itself also makes its creation durable, as ordered-metadata file systems do. Default false.</summary>
    public bool RequireDirectorySync { get; init; }

    /// <summary>If true every sync is silently ignored by the device while still reporting success.</summary>
    public bool LoseAllSyncs { get; init; }
}
