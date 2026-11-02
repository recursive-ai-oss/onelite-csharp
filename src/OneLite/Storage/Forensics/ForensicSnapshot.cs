using System.Linq;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Record;
using BTreeIndex = RecursiveAI.OneLite.Storage.BTree.BTree;

namespace RecursiveAI.OneLite.Storage.Forensics;

/// <summary>A table (or other schema object with a B-tree) found in <c>sqlite_schema</c>.</summary>
public sealed class ForensicTable
{
    internal ForensicTable(SchemaEntry e, TableDef? def)
    {
        Type = e.Type;
        Name = e.Name;
        TableName = e.TableName;
        RootPage = e.RootPage;
        Sql = e.Sql;
        Definition = def;
    }

    /// <summary>"table" or "index".</summary>
    public string Type { get; }

    /// <summary>The object's name.</summary>
    public string Name { get; }

    /// <summary>For an index, the table it belongs to.</summary>
    public string TableName { get; }

    /// <summary>Root page of its B-tree.</summary>
    public uint RootPage { get; }

    /// <summary>The stored CREATE statement.</summary>
    public string? Sql { get; }

    internal TableDef? Definition { get; }

    /// <summary>True for a table whose CREATE text was understood, so <see cref="ForensicSnapshot.ReadRows"/> and carving can use it. False for indexes and virtual tables.</summary>
    public bool CanReadRows => Type == "table" && Definition != null;

    /// <summary>Column names in declared order (empty for an index or when the CREATE text could not be understood).</summary>
    public IReadOnlyList<string> ColumnNames => Definition == null ? [] : Enumerable.ToArray(Enumerable.Select(Definition.Columns, c => c.Name));

    /// <summary>True for WITHOUT ROWID tables, which are stored as index B-trees.</summary>
    public bool WithoutRowid => Definition?.WithoutRowid ?? false;

    /// <summary>Number of columns stored in each record (generated VIRTUAL columns are not stored).</summary>
    internal int StoredColumns => Definition == null ? 0 : Enumerable.Count(Definition.RecordIndex, i => i >= 0);
}

/// <summary>One row read from a table.</summary>
public sealed class ForensicRow
{
    internal ForensicRow(long rowid, object?[] values)
    {
        Rowid = rowid;
        Values = values;
    }

    /// <summary>The rowid; 0 for a WITHOUT ROWID table.</summary>
    public long Rowid { get; }

    /// <summary>One value per declared column: null, <see cref="long"/>, <see cref="double"/>, <see cref="string"/> or <see cref="byte"/>[]. A column that is an alias of the rowid holds the rowid; a generated VIRTUAL column holds null (it is computed, not stored); a column added after the row was written holds null.</summary>
    public object?[] Values { get; }
}

/// <summary>
/// A consistent, read-only view of the database at one point: the database file alone, or the database file with the log's frames up to one commit
/// laid over it. Page images come from the files on demand; nothing is written anywhere.
/// </summary>
public sealed class ForensicSnapshot
{
    private readonly ForensicDatabase _db;
    private readonly Dictionary<uint, int>? _walMap;
    private readonly Dictionary<uint, byte[]> _cache = [];
    private List<ForensicTable>? _tables;
    private BtContext? _ctx;
    private SpaceReport? _space;

    private ForensicSnapshot(ForensicDatabase db, Dictionary<uint, int>? walMap)
    {
        _db = db;
        _walMap = walMap;
    }

    /// <summary>The database this snapshot belongs to.</summary>
    public ForensicDatabase Database => _db;

    /// <summary>The selector that made it.</summary>
    public SnapshotSelector Selector { get; private set; }

    /// <summary>The commit the snapshot is as of, or null for the database file alone.</summary>
    public WalCommit? Commit { get; private set; }

    /// <summary>True if frames after the last commit are laid over the database (they were never committed).</summary>
    public bool IncludesUncommitted { get; private set; }

    /// <summary>Database size in pages as of this snapshot (commit frame, or header, or file length).</summary>
    public uint PageCount { get; private set; }

    /// <summary>Page size in bytes.</summary>
    public int PageSize => _db.PageSize;

    /// <summary>Usable bytes per page.</summary>
    public int UsableSize { get; private set; }

    /// <summary>Text encoding.</summary>
    public TextEncoding Encoding { get; private set; }

    /// <summary>
    /// True if the database file might hold pages written by a later transaction than this snapshot (a checkpoint copied newer frames into it)
    /// and some of those pages have no frame at or before this snapshot, so the snapshot cannot show their older content. Exact for the snapshot
    /// of the latest commit.
    /// </summary>
    public bool DatabaseMayBeNewer { get; private set; }

    /// <summary>Human-readable remarks about how far the snapshot can be trusted (lost log prefix, newer database pages, ignored log, and so on).</summary>
    public IReadOnlyList<string> Notes { get; private set; } = [];

    /// <summary>The log frame (1-based) that provides page <paramref name="pgno"/> in this snapshot, or 0 if the page comes from the database file.</summary>
    public int FrameFor(uint pgno) => _walMap != null && _walMap.TryGetValue(pgno, out int f) ? f : 0;

    internal static ForensicSnapshot Create(ForensicDatabase db, SnapshotSelector sel)
    {
        WalInfo? wal = db.Wal;
        var notes = new List<string>();
        Dictionary<uint, int>? map = null;
        WalCommit? commit = null;
        bool uncommitted = false;
        bool mayBeNewer = false;
        uint size = 0;
        bool useWal = sel.Mode != SnapshotMode.DatabaseOnly && wal is { Usable: true };
        int last = 0;
        if (sel.Mode == SnapshotMode.AsOfCommit)
        {
            commit = wal?.Commits.FirstOrDefault(c => c.Id == sel.CommitId) ?? throw new ArgumentException($"no commit with id {sel.CommitId} in the log", nameof(sel));
            last = commit.LastFrame;
            size = commit.DatabaseSizePages;
            if (commit.PrefixLost)
            {
                notes.Add($"commit {commit.Id} belongs to an earlier log generation whose first frames were overwritten; pages not rewritten by the surviving frames come from the current database file");
            }

            if (commit.Generation != 0)
            {
                mayBeNewer = true;
                notes.Add("the database file has been written since this generation; pages without a frame may be newer than the commit");
            }
        }
        else if (useWal)
        {
            if (wal!.LastCommitFrame > 0)
            {
                commit = wal.Commits.Last(c => c.Generation == 0);
                last = commit.LastFrame;
                size = commit.DatabaseSizePages;
            }

            if (sel.IncludeTrailingFrames && wal.ValidChainEnd > wal.LastCommitFrame)
            {
                last = wal.ValidChainEnd;
                uncommitted = true;
                notes.Add($"frames {wal.LastCommitFrame + 1}..{wal.ValidChainEnd} were written after the last commit and are included");
            }
        }
        else if (sel.Mode == SnapshotMode.DatabaseOnly && wal != null)
        {
            notes.Add("the log was ignored on request");
        }
        else if (wal is { Usable: false })
        {
            notes.Add("the log is unusable: " + wal.UnusableReason);
        }

        if (last > 0)
        {
            map = [];
            int from = commit is { Generation: > 0 } ? commit.Generation : 0;
            for (int i = 1; i <= last; i++)
            {
                WalFrameInfo fr = wal!.Frames[i - 1];
                bool inSet = from == 0 ? fr.State is WalFrameState.Committed or WalFrameState.Trailing : fr.State == WalFrameState.Stale && fr.Generation == from;
                if (inSet)
                {
                    map[fr.PageNumber] = i;
                }
            }

            if (uncommitted && size != 0)
            {
                foreach (uint p in map.Keys)
                {
                    size = Math.Max(size, p);
                }
            }

            if (commit != null && commit.Generation == 0 && !uncommitted)
            {
                // Pages that first appear in later frames and may already be backfilled into the database file: the database file then holds their
                // newer content and the older content is gone.
                uint limit = db.Shm is { Valid: true, MatchesWal: true } s ? s.Backfilled : (uint)wal!.ValidChainEnd;
                var seen = new HashSet<uint>();
                for (int i = last + 1; i <= Math.Min((long)limit, wal!.ValidChainEnd); i++)
                {
                    uint p = wal.Frames[i - 1].PageNumber;
                    if (!map.ContainsKey(p) && seen.Add(p))
                    {
                        mayBeNewer = true;
                    }
                }

                if (mayBeNewer)
                {
                    notes.Add(db.Shm is { Valid: true, MatchesWal: true } ? "a checkpoint has copied later frames into the database file; pages first written after this commit show their newer content" : "no wal-index to say how far a checkpoint got: pages first written after this commit may show their newer content");
                }
            }
        }

        var snap = new ForensicSnapshot(db, map) { Selector = sel, Commit = commit, IncludesUncommitted = uncommitted, DatabaseMayBeNewer = mayBeNewer, Notes = notes };
        snap.Init(size, last > 0);
        return snap;
    }

    private void Init(uint walSize, bool fromWal)
    {
        byte[] p1 = GetPageBytes(1) ?? throw OneLiteException.Corrupt(1, "page 1 is not available in this snapshot");
        // The snapshot's own page 1 gives the geometry: it may be a log frame.
        DatabaseHeaderInfo? hdr = _db.Header;
        if (!p1.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8))
        {
            throw new NotADatabaseException("page 1 of the snapshot does not start with the database magic");
        }

        int reserved = p1[20];
        UsableSize = PageSize - reserved;
        if (UsableSize < 480)
        {
            throw OneLiteException.Corrupt(1, $"reserved bytes {reserved} leave fewer than 480 usable bytes");
        }

        uint enc = Pg.U32(p1, 56);
        if (enc > 3)
        {
            throw OneLiteException.Corrupt(1, $"text encoding {enc} is not 0, 1, 2 or 3");
        }

        Encoding = enc == 0 ? TextEncoding.Utf8 : (TextEncoding)enc;
        uint counter = Pg.U32(p1, 24);
        uint inHeader = Pg.U32(p1, 28);
        bool valid = inHeader != 0 && counter == Pg.U32(p1, 92);
        long filePages = _db.DatabaseFileLength / PageSize;
        if (fromWal && walSize != 0)
        {
            PageCount = walSize;
        }
        else if (valid)
        {
            // A header size larger than the file is a truncated file: read what exists.
            PageCount = (uint)Math.Min(inHeader, Math.Max(filePages, 0));
            if (inHeader > filePages)
            {
                Notes = [.. Notes, $"the header says {inHeader} pages but the file holds {filePages}"];
            }
        }
        else
        {
            PageCount = (uint)Math.Min(filePages, uint.MaxValue);
        }

        _ = hdr;
    }

    /// <summary>Pages the files can supply: the larger of the database file's page count and the highest page number in the frames this snapshot uses.</summary>
    internal long AvailablePages
    {
        get
        {
            long n = _db.DatabaseFileLength / PageSize;
            if (_walMap != null && _walMap.Count > 0)
            {
                n = Math.Max(n, _walMap.Keys.Max());
            }

            return n;
        }
    }

    /// <summary>
    /// Classifies every page (B-tree, overflow, freelist, pointer map, lost) and lists the free bytes inside B-tree pages, the freelist, lost pages and
    /// bytes past the end of the database. Problems met on the way are listed in <see cref="SpaceReport.Anomalies"/>; the walk does not stop for them.
    /// </summary>
    /// <exception cref="OneLiteException">Page 1 is unusable.</exception>
    public SpaceReport Space() => _space ??= SpaceAnalyzer.Analyze(this);

    /// <summary>
    /// Looks for records no live row occupies: in the free space of live pages, on freelist and lost pages, in older images of pages (log frames the
    /// snapshot does not use, database pages the log replaced, journal pages) and past the end of the database. Each candidate carries its location,
    /// values, a confidence between 0 and 1 and the reasons for it. The model is described in docs/forensics.md.
    /// </summary>
    /// <param name="options">Options; null for the defaults.</param>
    /// <returns>Candidates, best first. Identical records found in several places are reported once with <see cref="CarvedRecord.Occurrences"/>.</returns>
    public IReadOnlyList<CarvedRecord> Carve(CarveOptions? options = null) => new Carver(this, options ?? new CarveOptions()).Run();

    /// <summary>Returns the bytes of page <paramref name="pgno"/> in this snapshot, or null if neither the log nor the database file has it. The array is shared: do not modify it.</summary>
    public byte[]? GetPageBytes(uint pgno)
    {
        if (_cache.TryGetValue(pgno, out byte[]? hit))
        {
            return hit;
        }

        byte[]? b = null;
        if (_walMap != null && _walMap.TryGetValue(pgno, out int frame))
        {
            b = _db.ReadWalPage(frame);
        }

        b ??= _db.ReadDbPage(pgno);
        if (b != null)
        {
            if (_cache.Count > 2048)
            {
                _cache.Clear();
            }

            _cache[pgno] = b;
        }

        return b;
    }

    /// <summary>Reads the version of page <paramref name="pgno"/> held by the database file itself, whatever the log says; null if the file lacks it.</summary>
    public byte[]? GetDatabaseFilePage(uint pgno) => _db.ReadDbPage(pgno);

    private BtContext Context => _ctx ??= new BtContext(new SnapshotPages(this), Encoding);

    /// <summary>Tables and indexes in <c>sqlite_schema</c> with a B-tree (views, triggers and virtual tables without storage are left out).</summary>
    /// <exception cref="OneLiteException">The schema table or the page it lives on is damaged.</exception>
    public IReadOnlyList<ForensicTable> Tables
    {
        get
        {
            if (_tables == null)
            {
                var list = new List<ForensicTable>();
                if (PageCount > 0)
                {
                    foreach (SchemaEntry e in SchemaLite.Read(Context, static () => { }))
                    {
                        if (e.RootPage == 0 || (e.Type != "table" && e.Type != "index"))
                        {
                            continue;
                        }

                        TableDef? def = e.Type == "table" ? SchemaLite.ParseTable(e.Sql ?? "") : null;
                        list.Add(new ForensicTable(e, def));
                    }
                }

                _tables = list;
            }

            return _tables;
        }
    }

    /// <summary>Reads every row of a table in rowid (or key) order.</summary>
    /// <exception cref="OneLiteException">A page of the table is damaged. Rows read before the damage are not returned.</exception>
    /// <exception cref="ArgumentException">The object is an index or its CREATE text could not be understood.</exception>
    public List<ForensicRow> ReadRows(ForensicTable table)
    {
        if (table.Type != "table" || table.Definition == null)
        {
            throw new ArgumentException($"'{table.Name}' is not a table with a readable definition", nameof(table));
        }

        TableDef def = table.Definition;
        BtContext ctx = Context;
        BTreeIndex tree = def.WithoutRowid ? BTreeIndex.Index(ctx, table.RootPage, KeyInfo.Binary(ctx.Encoding, 0)) : BTreeIndex.Table(ctx, table.RootPage);
        using BtCursor c = tree.OpenCursor();
        var rows = new List<ForensicRow>();
        var rv = new RecordView();
        Drive(c, c.First);
        while (c.State == CursorState.Valid)
        {
            Drive(c, c.LoadPayload);
            if (!c.ParseRecord(rv))
            {
                throw OneLiteException.Corrupt(table.RootPage, "malformed record in " + table.Name);
            }

            long rowid = def.WithoutRowid ? 0 : c.Rowid;
            var vals = new object?[def.Columns.Count];
            for (int i = 0; i < vals.Length; i++)
            {
                int ri = def.RecordIndex[i];
                if (ri < 0 || ri >= rv.ColumnCount)
                {
                    vals[i] = null;
                }
                else if (i == def.RowidAlias && rv.ClassOf(ri) == StorageClass.Null)
                {
                    vals[i] = rowid;
                }
                else
                {
                    vals[i] = ValueOf(rv, ri, ctx.Encoding);
                }
            }

            rows.Add(new ForensicRow(rowid, vals));
            Drive(c, c.Next);
        }

        return rows;
    }

    internal static object? ValueOf(RecordView rv, int i, TextEncoding enc)
    {
        switch (rv.ClassOf(i))
        {
            case StorageClass.Integral: return rv.GetInt64(i);
            case StorageClass.Real: return rv.GetDouble(i);
            case StorageClass.Text: return TextCodec.Decode(rv.GetBytes(i), enc);
            case StorageClass.Blob: return rv.GetBytes(i).ToArray();
            default: return null;
        }
    }

    private static void Drive(BtCursor c, Func<BtStatus> op)
    {
        BtStatus s = op();
        if (s == BtStatus.Error)
        {
            throw c.Error ?? OneLiteException.Corrupt(0, "cursor failed");
        }
    }

    /// <summary>A read-only page source over the snapshot for the B-tree layer. Every request completes at once.</summary>
    private sealed class SnapshotPages : IBtPageSource
    {
        private sealed class Handle(uint n, byte[] d) : PageHandle
        {
            public override uint Number { get; } = n;

            public override byte[] Data { get; } = d;
        }

        private readonly ForensicSnapshot _s;

        public SnapshotPages(ForensicSnapshot s)
        {
            _s = s;
        }

        public int PageSize => _s.PageSize;

        public int UsableSize => _s.UsableSize;

        public uint PageCount => _s.PageCount;

        public bool AutoVacuum => _s.GetPageBytes(1) is { } p && Pg.U32(p, 52) != 0;

        public Exception? LastError { get; private set; }

        public uint LargestRootPage
        {
            get => _s.GetPageBytes(1) is { } p ? Pg.U32(p, 52) : 0;
            set => throw new NotSupportedException("forensic snapshots are read-only");
        }

        public BtStatus GetPage(uint pgno, out PageHandle? page)
        {
            page = null;
            // The lock-byte page (the one holding file offset 0x40000000) is never part of a database (fileformat2 §1.3.1).
            long start = (long)(pgno - 1) * PageSize;
            if (pgno == 0 || pgno > PageCount || (start <= 0x40000000L && start + PageSize > 0x40000000L))
            {
                LastError = OneLiteException.Corrupt(pgno, $"page {pgno} is outside the database ({PageCount} pages) or is the lock-byte page");
                return BtStatus.Error;
            }

            byte[]? b = _s.GetPageBytes(pgno);
            if (b == null)
            {
                LastError = OneLiteException.Corrupt(pgno, "the page is not present in the files");
                return BtStatus.Error;
            }

            page = new Handle(pgno, b);
            return BtStatus.Ok;
        }

        public void Release(PageHandle page)
        {
        }

        public BtStatus MakeWritable(PageHandle page) => throw new NotSupportedException("forensic snapshots are read-only");

        public BtStatus Reserve(int allocations, int frees) => throw new NotSupportedException("forensic snapshots are read-only");

        public BtStatus ReservePtrmap(uint pgno) => throw new NotSupportedException("forensic snapshots are read-only");

        public PageHandle Allocate() => throw new NotSupportedException("forensic snapshots are read-only");

        public void Free(uint pgno) => throw new NotSupportedException("forensic snapshots are read-only");

        public void PtrmapPut(uint pgno, byte type, uint parent) => throw new NotSupportedException("forensic snapshots are read-only");
    }
}
