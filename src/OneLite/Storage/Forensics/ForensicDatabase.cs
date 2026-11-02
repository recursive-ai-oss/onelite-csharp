using System.Linq;
using System.Buffers.Binary;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Storage.Forensics;

/// <summary>
/// A database file set (database, <c>-wal</c>, <c>-shm</c>, <c>-journal</c>) opened for recovery work. Nothing is ever modified: the files are opened
/// read-only through the VFS without the database role, so no lock is requested, no wal-index is mapped and nothing is created. The log is
/// scanned but never applied, the journal is read but never played back, and the <c>-shm</c> is read as ordinary bytes.
/// </summary>
/// <remarks>
/// Reads are not synchronised with writers. A live writer can change the files between two reads; a snapshot taken from the log is stable
/// because frames before the last commit never change, but a database file read while a checkpoint runs can show newer pages
/// (<see cref="ForensicSnapshot.DatabaseMayBeNewer"/> reports when that can matter). Instances are not thread-safe.
/// </remarks>
public sealed class ForensicDatabase : IDisposable
{
    private readonly IVfsFile _db;
    private readonly IVfsFile? _walFile;
    private readonly IVfsFile? _journalFile;
    private readonly long _dbLength;
    private IVfs _vfs = null!;

    private ForensicDatabase(string path, IVfsFile db, long dbLength, IVfsFile? wal, WalInfo? walInfo, ShmInfo? shm, IVfsFile? journal, JournalInfo? journalInfo, int pageSize)
    {
        Path = path;
        _db = db;
        _dbLength = dbLength;
        _walFile = wal;
        Wal = walInfo;
        Shm = shm;
        _journalFile = journal;
        Journal = journalInfo;
        PageSize = pageSize;
    }

    /// <summary>The database path as given.</summary>
    public string Path { get; }

    /// <summary>Page size in bytes (from the database header, or from the log when the database file is empty).</summary>
    public int PageSize { get; }

    /// <summary>Length of the database file in bytes.</summary>
    public long DatabaseFileLength => _dbLength;

    /// <summary>The fixed header fields of the database file, or null when the file is empty (everything then lives in the log).</summary>
    public DatabaseHeaderInfo? Header { get; private init; }

    /// <summary>What the log holds, or null when there is none (or <see cref="ForensicOptions.IncludeWal"/> was false).</summary>
    public WalInfo? Wal { get; }

    /// <summary>What the wal-index file said, or null if there is none.</summary>
    public ShmInfo? Shm { get; }

    /// <summary>What the rollback journal holds, or null if there is none.</summary>
    public JournalInfo? Journal { get; }

    /// <summary>
    /// Looks at the files again and says whether anything that could change what a snapshot shows has changed since <see cref="Open(string, ForensicOptions?)"/>:
    /// the log was restarted (new salts) or shortened, a checkpoint finished (the wal-index backfill count moved), or the database file changed length.
    /// Call it after a pass over a live database and discard the pass if it returns true; open again and repeat. New commits appended to the log
    /// do not count: a snapshot never includes them. A checkpoint that is still running when this is called cannot be seen (no lock is taken).
    /// </summary>
    public bool HasChangedSinceOpen()
    {
        if (_db.FileSize() != _dbLength)
        {
            return true;
        }

        if (Wal is not { Usable: true } || _walFile == null)
        {
            return false;
        }

        Span<byte> h = stackalloc byte[WalScanner.HeaderSize];
        long len = _walFile.FileSize();
        if (len < Wal.FileLength - Wal.TornTailBytes || _walFile.Read(0, h) < WalScanner.HeaderSize
            || BinaryPrimitives.ReadUInt32BigEndian(h[16..]) != Wal.Salt1 || BinaryPrimitives.ReadUInt32BigEndian(h[20..]) != Wal.Salt2)
        {
            return true;
        }

        string shmPath = Path + "-shm";
        if (Shm is { Valid: true } before && _vfs.Access(shmPath, AccessMode.Exists))
        {
            using IVfsFile sf = _vfs.Open(shmPath, VfsOpenOptions.ReadOnly);
            byte[] b = new byte[(int)Math.Min(sf.FileSize(), 4096)];
            ShmInfo now = WalScanner.ParseShm(b.AsSpan(0, sf.Read(0, b)), Wal);
            return now.Valid && now.Backfilled != before.Backfilled;
        }

        return false;
    }

    internal IVfsFile DatabaseFile => _db;

    internal IVfsFile? WalFile => _walFile;

    internal IVfsFile? JournalFile => _journalFile;

    /// <summary>
    /// Opens a database and the side files that sit beside it.
    /// </summary>
    /// <param name="path">The database file. It must exist unless a usable <c>-wal</c> file beside it holds the database.</param>
    /// <param name="options">Options; null for the defaults.</param>
    /// <exception cref="VfsException">The file cannot be opened (SQLITE_CANTOPEN).</exception>
    /// <exception cref="NotADatabaseException">The file is not a database (SQLITE_NOTADB, 26).</exception>
    /// <exception cref="OneLiteException">A header field is outside its documented range (SQLITE_CORRUPT, 11).</exception>
    public static ForensicDatabase Open(string path, ForensicOptions? options = null)
    {
        options ??= new ForensicOptions();
        IVfs vfs = options.Vfs ?? FileVfs.Instance;
        if (!vfs.Access(path, AccessMode.Exists))
        {
            throw new VfsException(VfsCodes.CantOpen, $"unable to open database file '{path}': no such file");
        }

        IVfsFile db = vfs.Open(path, VfsOpenOptions.ReadOnly);
        IVfsFile? wal = null, journal = null;
        try
        {
            long len = db.FileSize();
            DatabaseHeaderInfo? header = null;
            if (len > 0)
            {
                header = ParseHeader(db, len);
            }

            WalInfo? walInfo = null;
            string walPath = path + "-wal";
            if (options.IncludeWal && vfs.Access(walPath, AccessMode.Exists))
            {
                wal = vfs.Open(walPath, VfsOpenOptions.ReadOnly);
                walInfo = WalScanner.Scan(wal, wal.FileSize());
                if (walInfo.Usable && header != null && walInfo.PageSize != header.PageSize)
                {
                    walInfo.Usable = false;
                    walInfo.UnusableReason = $"log page size {walInfo.PageSize} differs from the database page size {header.PageSize}";
                }
            }

            if (header == null && walInfo is not { Usable: true })
            {
                throw new NotADatabaseException(len == 0 ? "the database file is empty and there is no usable log" : "file is shorter than a database header");
            }

            ShmInfo? shm = null;
            string shmPath = path + "-shm";
            if (walInfo != null && vfs.Access(shmPath, AccessMode.Exists))
            {
                using IVfsFile sf = vfs.Open(shmPath, VfsOpenOptions.ReadOnly);
                byte[] b = new byte[(int)Math.Min(sf.FileSize(), 4096)];
                int got = sf.Read(0, b);
                shm = WalScanner.ParseShm(b.AsSpan(0, got), walInfo);
            }

            int ps = header?.PageSize ?? walInfo!.PageSize;
            JournalInfo? ji = null;
            string jPath = path + "-journal";
            if (options.IncludeJournal && vfs.Access(jPath, AccessMode.Exists))
            {
                journal = vfs.Open(jPath, VfsOpenOptions.ReadOnly);
                ji = ScanJournal(journal, ps);
            }

            return new ForensicDatabase(path, db, len, wal, walInfo, shm, journal, ji, ps) { Header = header, _vfs = vfs };
        }
        catch
        {
            db.Dispose();
            wal?.Dispose();
            journal?.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _db.Dispose();
        _walFile?.Dispose();
        _journalFile?.Dispose();
    }

    private static DatabaseHeaderInfo ParseHeader(IVfsFile f, long len)
    {
        Span<byte> h = stackalloc byte[100];
        int got = f.Read(0, h);
        ReadOnlySpan<byte> magic = "SQLite format 3\0"u8;
        if (got < 100 || !h[..16].SequenceEqual(magic))
        {
            throw new NotADatabaseException(got < 100 ? "file is shorter than a database header" : "the 16-byte magic string is missing");
        }

        int ps = BinaryPrimitives.ReadUInt16BigEndian(h[16..]);
        if (ps == 1)
        {
            ps = 65536;
        }

        if (ps < 512 || (ps & (ps - 1)) != 0)
        {
            throw OneLiteException.Corrupt(1, $"page size {ps} is not a power of two in 512..65536");
        }

        int reserved = h[20];
        if (ps - reserved < 480)
        {
            throw OneLiteException.Corrupt(1, $"reserved bytes {reserved} leave fewer than 480 usable bytes");
        }

        uint enc = BinaryPrimitives.ReadUInt32BigEndian(h[56..]);
        // 0 is what a database that has not been written yet holds; its pages then live in the log.
        if (enc > 3)
        {
            throw OneLiteException.Corrupt(1, $"text encoding {enc} is not 0, 1, 2 or 3");
        }

        uint counter = BinaryPrimitives.ReadUInt32BigEndian(h[24..]);
        return new DatabaseHeaderInfo
        {
            PageSize = ps,
            ReservedBytes = reserved,
            ReadVersion = h[19],
            ChangeCounter = counter,
            SizeInHeader = BinaryPrimitives.ReadUInt32BigEndian(h[28..]),
            // fileformat2 §1.3.8: the in-header size is trusted only when "version-valid-for" equals the change counter.
            SizeFieldValid = counter == BinaryPrimitives.ReadUInt32BigEndian(h[92..]) && BinaryPrimitives.ReadUInt32BigEndian(h[28..]) != 0,
            FirstFreelistTrunk = BinaryPrimitives.ReadUInt32BigEndian(h[32..]),
            FreelistCount = BinaryPrimitives.ReadUInt32BigEndian(h[36..]),
            SchemaCookie = BinaryPrimitives.ReadUInt32BigEndian(h[40..]),
            LargestRootPage = BinaryPrimitives.ReadUInt32BigEndian(h[52..]),
            Encoding = enc == 0 ? TextEncoding.Utf8 : (TextEncoding)enc,
        };
    }

    private static JournalInfo ScanJournal(IVfsFile f, int dbPageSize)
    {
        var ji = new JournalInfo { FileLength = f.FileSize() };
        Span<byte> h = stackalloc byte[28];
        if (ji.FileLength < 28 || f.Read(0, h) < 28 || !h[..8].SequenceEqual(new byte[] { 0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7 }))
        {
            return ji;
        }

        uint nonce = BinaryPrimitives.ReadUInt32BigEndian(h[12..]);
        ji.OriginalDatabasePages = BinaryPrimitives.ReadUInt32BigEndian(h[16..]);
        uint sector = BinaryPrimitives.ReadUInt32BigEndian(h[20..]);
        uint ps = BinaryPrimitives.ReadUInt32BigEndian(h[24..]);
        if (ps < 512 || ps > 65536 || (ps & (ps - 1)) != 0 || sector < 512 || sector > 65536 || (sector & (sector - 1)) != 0)
        {
            return ji;
        }

        ji.HeaderValid = true;
        ji.PageSize = (int)ps;
        // The header is padded to one sector; each record is page number, page image, checksum (fileformat2 "rollback journal").
        long recSize = 4 + ps + 4;
        long n = (ji.FileLength - sector) / recSize;
        var list = new List<JournalRecord>();
        byte[] rec = new byte[recSize];
        for (long i = 0; i < n && ps == dbPageSize; i++)
        {
            long off = sector + (i * recSize);
            if (f.Read(off, rec) < recSize)
            {
                break;
            }

            uint pg = BinaryPrimitives.ReadUInt32BigEndian(rec);
            uint sum = nonce;
            for (long k = ps - 200; k >= 0; k -= 200)
            {
                sum += rec[4 + k];
            }

            if (pg != 0)
            {
                list.Add(new JournalRecord(pg, off + 4, sum == BinaryPrimitives.ReadUInt32BigEndian(rec.AsSpan((int)(4 + ps)))));
            }
        }

        ji.Records = list;
        return ji;
    }

    /// <summary>
    /// Opens a snapshot: a consistent view of the database as of one point.
    /// </summary>
    /// <param name="selector">Which state to show. Default: <see cref="SnapshotSelector.Latest"/>.</param>
    /// <exception cref="ArgumentException">The commit id is not in <see cref="WalInfo.Commits"/>.</exception>
    /// <exception cref="OneLiteException">Page 1 of the snapshot is unusable.</exception>
    public ForensicSnapshot OpenSnapshot(SnapshotSelector selector = default)
    {
        return ForensicSnapshot.Create(this, selector);
    }

    /// <summary>Reads page <paramref name="pgno"/> of the database file; null if the file does not contain the whole page.</summary>
    internal byte[]? ReadDbPage(uint pgno)
    {
        if (pgno == 0)
        {
            return null;
        }

        long off = (long)(pgno - 1) * PageSize;
        if (off + PageSize > _dbLength)
        {
            return null;
        }

        byte[] b = new byte[PageSize];
        return _db.Read(off, b) == PageSize ? b : null;
    }

    /// <summary>Reads the page image of log frame <paramref name="frame"/> (1-based).</summary>
    internal byte[]? ReadWalPage(int frame)
    {
        if (_walFile == null || Wal is not { Usable: true } || frame < 1 || frame > Wal.Frames.Count)
        {
            return null;
        }

        WalFrameInfo fi = Wal.Frames[frame - 1];
        byte[] full = new byte[WalScanner.FrameHeaderSize + PageSize];
        if (_walFile.Read(fi.Offset, full) != full.Length)
        {
            return null;
        }

        // The log of a live database can be restarted under us: the frame must still be the one the scan saw.
        if (BinaryPrimitives.ReadUInt32BigEndian(full) != fi.PageNumber || BinaryPrimitives.ReadUInt32BigEndian(full.AsSpan(8)) != fi.Salt1
            || BinaryPrimitives.ReadUInt32BigEndian(full.AsSpan(12)) != fi.Salt2 || BinaryPrimitives.ReadUInt32BigEndian(full.AsSpan(16)) != fi.Checksum1
            || BinaryPrimitives.ReadUInt32BigEndian(full.AsSpan(20)) != fi.Checksum2)
        {
            throw new ForensicChangedException(frame);
        }

        return full.AsSpan(WalScanner.FrameHeaderSize).ToArray();
    }

    /// <summary>Reads a journal page image.</summary>
    internal byte[]? ReadJournalPage(in JournalRecord r)
    {
        if (_journalFile == null)
        {
            return null;
        }

        byte[] b = new byte[PageSize];
        return _journalFile.Read(r.Offset, b) == PageSize ? b : null;
    }
}
