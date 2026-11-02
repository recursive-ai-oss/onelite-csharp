using System.Linq;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Storage.Forensics;

/// <summary>Where a carved record was found.</summary>
public enum CarveSource : byte
{
    /// <summary>Unallocated gap of a live B-tree page of the snapshot.</summary>
    UnallocatedSpace,

    /// <summary>Freeblock of a live B-tree page of the snapshot.</summary>
    Freeblock,

    /// <summary>A freelist trunk or leaf page (a freed page keeps its old content).</summary>
    FreelistPage,

    /// <summary>A page reachable from no root and not on the freelist.</summary>
    LostPage,

    /// <summary>An older image of a page, from a log frame the snapshot does not use (an earlier version, an earlier generation, a frame after a damaged one).</summary>
    OlderWalFrame,

    /// <summary>The database file's copy of a page that the snapshot replaces with a newer log frame.</summary>
    SupersededDatabasePage,

    /// <summary>A page image saved in a rollback journal (the page as it was before the interrupted transaction).</summary>
    JournalPage,

    /// <summary>Bytes in the database file past the last page.</summary>
    TrailingBytes,
}

/// <summary>Options for <see cref="ForensicSnapshot.Carve"/>.</summary>
public sealed class CarveOptions
{
    /// <summary>Candidates below this confidence (0 to 1) are dropped. Default 0.5; see docs/forensics.md for what the number means.</summary>
    public double MinConfidence { get; init; } = 0.5;

    /// <summary>Keep candidates identical to a row that is still live (same table, rowid and values). Default false: such a candidate is an old copy of a living row.</summary>
    public bool IncludeLiveDuplicates { get; init; }

    /// <summary>Look at older log frames, superseded database pages and journal pages. Default true.</summary>
    public bool IncludeHistory { get; init; } = true;

    /// <summary>Largest file tail (bytes past the last page) to scan. Default 16 MiB.</summary>
    public long MaxTrailingBytes { get; init; } = 16 * 1024 * 1024;
}

/// <summary>A row-like record recovered from space that no live row occupies, with where it was found and how sure the reader is.</summary>
public sealed class CarvedRecord
{
    /// <summary>Where it was found.</summary>
    public CarveSource Source { get; internal set; }

    /// <summary>"database", "wal" or "journal".</summary>
    public string File { get; internal set; } = "database";

    /// <summary>Byte offset of the cell's first byte in that file.</summary>
    public long FileOffset { get; internal set; }

    /// <summary>Database page number the bytes belong to (the page they are an image of), or 0 for trailing bytes.</summary>
    public uint Page { get; internal set; }

    /// <summary>Offset of the cell within the page.</summary>
    public int PageOffset { get; internal set; }

    /// <summary>Log frame number when the bytes come from the log, else 0.</summary>
    public int WalFrame { get; internal set; }

    /// <summary>The best-matching table, or null.</summary>
    public string? Table { get; internal set; }

    /// <summary>How many other tables fit about as well (0 means the table is unambiguous).</summary>
    public int OtherTableCandidates { get; internal set; }

    /// <summary>The rowid, when it survived. Null when the freeblock header overwrote it.</summary>
    public long? Rowid { get; internal set; }

    /// <summary>One value per declared column (see <see cref="ForensicRow.Values"/>). The rowid-alias column holds the rowid when known, else null. A value that could not be recovered is null.</summary>
    public object?[] Values { get; internal set; } = [];

    /// <summary>Confidence between 0 and 1 that this is a genuine record of that table; see docs/forensics.md.</summary>
    public double Confidence { get; internal set; }

    /// <summary>Evidence in bits behind <see cref="Confidence"/>.</summary>
    public double EvidenceBits { get; internal set; }

    /// <summary>The reasons for the score, one per scoring step.</summary>
    public IReadOnlyList<string> Reasons { get; internal set; } = [];

    /// <summary>True if the first bytes of the cell were overwritten by a freeblock header and the header was rebuilt from the table definition.</summary>
    public bool PrefixReconstructed { get; internal set; }

    /// <summary>True if some values are missing because an overflow chain could not be followed to its end.</summary>
    public bool Truncated { get; internal set; }

    /// <summary>True if a live row of the same table has the same rowid and values (an old copy of a living row).</summary>
    public bool MatchesLiveRow { get; internal set; }

    /// <summary>How many places held an identical record (the same table, rowid and values); this entry is the best-scored of them.</summary>
    public int Occurrences { get; internal set; } = 1;

    internal string Signature = "";
}

internal sealed class TableModel
{
    public string Name = "";
    public uint Root;
    public int N;
    public char[] Aff = [];
    public int AliasPos = -1;
    public bool HasRowid = true;
    public int[] DeclToRec = [];
    public int DeclCount;
    public long MinRowid, MaxRowid;
    public long LiveCount;
    public readonly HashSet<string> LiveExact = [];
    public readonly HashSet<string> LiveValues = [];
}

/// <summary>The carving engine. See docs/forensics.md for the model; the constants here are the model's weights.</summary>
internal sealed class Carver
{
    private const int MaxHeader = 4096;
    private readonly ForensicSnapshot _snap;
    private readonly CarveOptions _opt;
    private readonly SpaceReport _space;
    private readonly BtContext _geo;
    private readonly List<TableModel> _tables = [];
    private readonly Dictionary<string, CarvedRecord> _found = [];
    private readonly ulong[] _types = new ulong[MaxHeader];
    private readonly int _usable;
    private readonly int _maxN;
    private TableModel[] _all = [];
    private readonly long _maxPayload;

    public Carver(ForensicSnapshot snap, CarveOptions opt)
    {
        _snap = snap;
        _opt = opt;
        _space = snap.Space();
        _usable = snap.UsableSize;
        // A payload can be no larger than the pages that exist: this bounds the buffer a hostile length could make us allocate.
        _maxPayload = Math.Min(1_000_000_000L, (snap.AvailablePages * _usable) + _usable);
        _geo = new BtContext(new CellGeometry(snap), snap.Encoding);
        foreach (ForensicTable t in snap.Tables)
        {
            if (t.Type != "table" || t.Definition == null)
            {
                continue;
            }

            TableDef d = t.Definition;
            int n = Enumerable.Count(d.RecordIndex, i => i >= 0);
            if (n == 0 || n > 2000)
            {
                continue;
            }

            var m = new TableModel
            {
                Name = t.Name,
                Root = t.RootPage,
                N = n,
                HasRowid = !d.WithoutRowid,
                DeclCount = d.Columns.Count,
                Aff = new char[n],
                DeclToRec = [.. d.RecordIndex],
            };
            for (int i = 0; i < d.Columns.Count; i++)
            {
                int ri = d.RecordIndex[i];
                if (ri >= 0 && ri < n)
                {
                    m.Aff[ri] = d.Columns[i].Affinity;
                    if (i == d.RowidAlias)
                    {
                        m.AliasPos = ri;
                    }
                }
            }

            try
            {
                var rows = snap.ReadRows(t);
                m.LiveCount = rows.Count;
                if (rows.Count > 0 && m.HasRowid)
                {
                    m.MinRowid = rows.Min(r => r.Rowid);
                    m.MaxRowid = rows.Max(r => r.Rowid);
                }

                foreach (ForensicRow r in rows)
                {
                    string vs = Sig(r.Values, m.DeclToRec, m.AliasPos >= 0 ? AliasDecl(d) : -1);
                    m.LiveValues.Add(vs);
                    m.LiveExact.Add(r.Rowid.ToString(CultureInfo.InvariantCulture) + "|" + vs);
                }
            }
            catch (OneLiteException cex) when (cex.Code == ResultCode.Corrupt)
            {
                // A damaged table has no live-row set; its candidates are simply not deduplicated.
            }

            _tables.Add(m);
        }

        _maxN = _tables.Count == 0 ? 0 : _tables.Max(t => t.N);
        _all = [.. _tables];
    }

    private static int AliasDecl(TableDef d) => d.RowidAlias;

    /// <summary>A signature of a row's values for comparisons. The rowid-alias column is left out: it is the rowid.</summary>
    private static string Sig(object?[] vals, int[] declToRec, int aliasDecl)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < vals.Length; i++)
        {
            if (i == aliasDecl || (i < declToRec.Length && declToRec[i] < 0))
            {
                continue;
            }

            switch (vals[i])
            {
                case null: sb.Append('N'); break;
                case long l: sb.Append('I').Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case double r: sb.Append('R').Append(BitConverter.DoubleToInt64Bits(r).ToString(CultureInfo.InvariantCulture)); break;
                case string s: sb.Append('T').Append(s.Length).Append(':').Append(s); break;
                case byte[] b: sb.Append('B').Append(Convert.ToHexString(b)); break;
                default: break;
            }

            sb.Append('\u001f');
        }

        return sb.ToString();
    }

    public List<CarvedRecord> Run()
    {
        if (_tables.Count == 0)
        {
            return [];
        }

        LiveSpace();
        PageSet();
        if (_opt.IncludeHistory)
        {
            History();
        }

        Trailing();
        var res = new List<CarvedRecord>();
        foreach (CarvedRecord c in _found.Values)
        {
            if (c.Confidence >= _opt.MinConfidence && (_opt.IncludeLiveDuplicates || !c.MatchesLiveRow))
            {
                res.Add(c);
            }
        }

        res.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
        return res;
    }

    // ---- sources -------------------------------------------------------------------------------------------------------------------------------

    private void LiveSpace()
    {
        foreach (FreeRegion r in _space.FreeRegions)
        {
            PageKind k = _space.KindOf(r.Page);
            if (!_space.OwnerRoot.TryGetValue(r.Page, out uint root))
            {
                continue;
            }

            TableModel? owner = _tables.FirstOrDefault(t => t.Root == root);
            // An interior page keeps the cells it had as a leaf before a split made it a parent, so its free space is searched too.
            bool tablePage = k is PageKind.TableLeaf or PageKind.TableInterior;
            bool indexPage = k is PageKind.IndexLeaf or PageKind.IndexInterior;
            if (owner == null || !((tablePage && owner.HasRowid) || (indexPage && !owner.HasRowid)))
            {
                continue;
            }

            byte[]? d = _snap.GetPageBytes(r.Page);
            if (d == null)
            {
                continue;
            }

            var ctx = new Origin(r.Kind == FreeKind.Freeblock ? CarveSource.Freeblock : CarveSource.UnallocatedSpace, "database", (long)(r.Page - 1) * _snap.PageSize, r.Page, 0, [owner]);
            ScanRegion(d, r.Offset, r.Offset + r.Length, ctx, false, r.Kind == FreeKind.Freeblock);
        }
    }

    private void PageSet()
    {
        foreach (uint p in _space.FreelistTrunks.Concat(_space.FreelistLeaves))
        {
            Image(_snap.GetPageBytes(p), p, CarveSource.FreelistPage, "database", (long)(p - 1) * _snap.PageSize, 0);
        }

        foreach (uint p in _space.LostPages)
        {
            Image(_snap.GetPageBytes(p), p, CarveSource.LostPage, "database", (long)(p - 1) * _snap.PageSize, 0);
        }
    }

    private void History()
    {
        WalInfo? wal = _snap.Database.Wal;
        if (wal is { Usable: true })
        {
            for (int i = 1; i <= wal.Frames.Count; i++)
            {
                WalFrameInfo f = wal.Frames[i - 1];
                if (f.PageNumber == 0 || _snap.FrameFor(f.PageNumber) == i)
                {
                    continue;
                }

                byte[]? img = _snap.Database.ReadWalPage(i);
                byte[]? cur = _snap.GetPageBytes(f.PageNumber);
                if (img == null || (cur != null && img.AsSpan().SequenceEqual(cur)))
                {
                    continue;
                }

                Image(img, f.PageNumber, CarveSource.OlderWalFrame, "wal", f.Offset + WalScanner.FrameHeaderSize, i);
            }

            // The database file's own copy of a page the snapshot takes from the log.
            long filePages = _snap.Database.DatabaseFileLength / _snap.PageSize;
            for (uint p = 1; p <= filePages; p++)
            {
                if (_snap.FrameFor(p) == 0)
                {
                    continue;
                }

                byte[]? img = _snap.Database.ReadDbPage(p);
                byte[]? cur = _snap.GetPageBytes(p);
                if (img != null && cur != null && !img.AsSpan().SequenceEqual(cur))
                {
                    Image(img, p, CarveSource.SupersededDatabasePage, "database", (long)(p - 1) * _snap.PageSize, 0);
                }
            }
        }

        JournalInfo? j = _snap.Database.Journal;
        if (j is { HeaderValid: true })
        {
            foreach (JournalRecord r in j.Records)
            {
                byte[]? img = _snap.Database.ReadJournalPage(r);
                byte[]? cur = _snap.GetPageBytes(r.PageNumber);
                if (img != null && (cur == null || !img.AsSpan().SequenceEqual(cur)))
                {
                    Image(img, r.PageNumber, CarveSource.JournalPage, "journal", r.Offset, 0);
                }
            }
        }
    }

    private void Trailing()
    {
        long len = _snap.Database.DatabaseFileLength;
        long start = (long)_snap.PageCount * _snap.PageSize;
        if (len <= start || _snap.Database.Header == null)
        {
            return;
        }

        int n = (int)Math.Min(len - start, _opt.MaxTrailingBytes);
        byte[] b = new byte[n];
        int got = _snap.Database.DatabaseFile.Read(start, b);
        var ctx = new Origin(CarveSource.TrailingBytes, "database", start, 0, 0, _all);
        ScanRegion(b, 0, got, ctx, false, false, absoluteBase: start);
    }

    private readonly record struct Origin(CarveSource Source, string File, long FileBase, uint Page, int Frame, TableModel[] Tables);

    /// <summary>Handles one old or unowned page image: structured when it still looks like a table leaf, else a plain scan.</summary>
    private void Image(byte[]? d, uint pgno, CarveSource src, string file, long fileBase, int frame)
    {
        if (d == null || d.Length < 512)
        {
            return;
        }

        _space.OwnerRoot.TryGetValue(pgno, out uint root);
        TableModel[] cand = root != 0 ? _tables.Where(t => t.Root == root).ToArray() : _all;
        if (cand.Length == 0)
        {
            cand = _all;
        }

        var o = new Origin(src, file, fileBase, pgno, frame, cand);
        int h = pgno == 1 ? 100 : 0;
        byte type = d[h];
        if (type is Pg.TableLeaf or Pg.IndexLeaf)
        {
            int ncell = Pg.NCell(d, h);
            int ptrEnd = Pg.PtrArray(type, h) + (2 * ncell);
            int cs = Pg.ContentStart(d, h);
            if (ptrEnd <= _usable && cs >= ptrEnd && cs <= _usable)
            {
                TableModel[] fit = cand.Where(t => t.HasRowid == (type == Pg.TableLeaf)).ToArray();
                if (fit.Length == 0)
                {
                    return;
                }

                o = o with { Tables = fit };
                var accepted = new List<Cand>();
                for (int i = 0; i < ncell; i++)
                {
                    int off = Pg.CellOffset(d, h, type, i);
                    if (off >= ptrEnd && off <= _usable - 4)
                    {
                        Cand? c = CellAt(d, off, _usable, o, fit, structured: true);
                        if (c != null)
                        {
                            accepted.Add(c);
                        }
                    }
                }

                Emit(accepted, o, d);
                if (cs > ptrEnd)
                {
                    ScanRegion(d, ptrEnd, cs, o, false, false);
                }

                int fb = Pg.FirstFree(d, h);
                int prev = 0;
                for (int hops = 0; fb != 0 && hops < _usable / 4; hops++)
                {
                    if (fb < cs || fb > _usable - 4 || fb <= prev)
                    {
                        break;
                    }

                    int size = Pg.U16(d, fb + 2);
                    if (size < 4 || fb + size > _usable)
                    {
                        break;
                    }

                    ScanRegion(d, fb, fb + size, o, false, true);
                    prev = fb;
                    fb = Pg.U16(d, fb);
                }

                return;
            }
        }

        if (type is Pg.TableInterior or Pg.IndexInterior or Pg.IndexLeaf or Pg.TableLeaf)
        {
            // Index pages hold keys, interior table pages hold child pointers, and a leaf with a damaged header is not worth guessing at.
            return;
        }

        ScanRegion(d, 0, _usable, o, false, false);
    }

    // ---- scanning ------------------------------------------------------------------------------------------------------------------------------

    private sealed class Cand
    {
        public int Off, Size, PageOff;
        public TableModel Table = null!;
        public long? Rowid;
        public object?[] Values = [];
        public double Bits;
        public List<string> Reasons = [];
        public bool Prefix, Truncated, Unconfirmed;
        public int Others;
    }

    /// <summary>Looks for cells at every offset of [from, to) and, for a freeblock, for a cell whose first four bytes the freeblock header overwrote.</summary>
    private void ScanRegion(byte[] d, int from, int to, Origin o, bool structured, bool freeblock, long absoluteBase = -1)
    {
        var cands = new List<Cand>();
        if (to - from < 6)
        {
            return;
        }

        if (freeblock)
        {
            cands.AddRange(HeadCells(d, from, to, o));
        }

        // Rowid tables and WITHOUT ROWID tables store cells differently, so each group is tried on its own.
        var groups = new List<TableModel[]>();
        TableModel[] withRowid = o.Tables.Where(t => t.HasRowid).ToArray();
        TableModel[] withoutRowid = o.Tables.Where(t => !t.HasRowid).ToArray();
        if (withRowid.Length > 0)
        {
            groups.Add(withRowid);
        }

        if (withoutRowid.Length > 0)
        {
            groups.Add(withoutRowid);
        }

        int start = freeblock ? from + 4 : from;
        for (int p = start; p + 6 <= to; p++)
        {
            // A cell must start with a plausible payload-size varint; cheap rejects first.
            if (d[p] == 0)
            {
                continue;
            }

            foreach (TableModel[] group in groups)
            {
                Cand? c = CellAt(d, p, to, o, group, structured);
                if (c != null)
                {
                    cands.Add(c);
                }
            }
        }

        // A head-damaged cell whose end no free-block header vouches for is kept only if another cell starts right where it ends.
        if (cands.Any(c => c.Unconfirmed))
        {
            var starts = new HashSet<int>(cands.Where(c => !c.Unconfirmed).Select(c => c.Off));
            foreach (Cand c in cands.Where(c => c.Unconfirmed))
            {
                starts.Add(c.Off);
            }

            cands.RemoveAll(c =>
            {
                if (!c.Unconfirmed)
                {
                    return false;
                }

                int e = c.Off + c.Size;
                for (int g = 0; g <= 3; g++)
                {
                    if (starts.Contains(e + g))
                    {
                        c.Bits += 2;
                        c.Reasons.Add("another cell starts where this one ends (+2)");
                        return false;
                    }
                }

                return true;
            });
        }

        if (absoluteBase >= 0)
        {
            o = o with { FileBase = 0 };
            foreach (Cand c in cands)
            {
                c.PageOff = c.Off;
            }
        }

        Emit(Resolve(cands), o, d, absoluteBase);
    }

    /// <summary>Greedy non-overlapping choice: best evidence first.</summary>
    private static List<Cand> Resolve(List<Cand> cands)
    {
        cands.Sort((a, b) => b.Bits.CompareTo(a.Bits));
        var keep = new List<Cand>();
        foreach (Cand c in cands)
        {
            bool clash = false;
            foreach (Cand k in keep)
            {
                if (c.Off < k.Off + k.Size && k.Off < c.Off + c.Size)
                {
                    clash = true;
                    break;
                }
            }

            if (!clash)
            {
                keep.Add(c);
            }
        }

        return keep;
    }

    private void Emit(List<Cand> cands, Origin o, byte[] d, long absoluteBase = -1)
    {
        foreach (Cand c in cands)
        {
            if (c.Bits < 3)
            {
                continue;
            }

            var ints = new List<string>(c.Reasons);
            string vsig = Sig(c.Values, c.Table.DeclToRec, c.Table.AliasPos >= 0 ? AliasDeclIndex(c.Table) : -1);
            bool live = false;
            if (c.Rowid.HasValue)
            {
                live = c.Table.LiveExact.Contains(c.Rowid.Value.ToString(CultureInfo.InvariantCulture) + "|" + vsig);
            }
            else if (c.Table.LiveValues.Contains(vsig))
            {
                ints.Add("values equal a live row's, rowid unknown: probably an old copy of it (-4)");
                c.Bits -= 4;
            }

            double conf = 1.0 / (1.0 + Math.Pow(2.0, (8.0 - c.Bits) / 1.5));
            var rec = new CarvedRecord
            {
                Source = o.Source,
                File = o.File,
                FileOffset = (absoluteBase >= 0 ? absoluteBase : o.FileBase) + c.Off,
                Page = o.Page,
                PageOffset = absoluteBase >= 0 ? 0 : c.Off,
                WalFrame = o.Frame,
                Table = c.Table.Name,
                OtherTableCandidates = c.Others,
                Rowid = c.Rowid,
                Values = c.Values,
                Confidence = conf,
                EvidenceBits = c.Bits,
                Reasons = ints,
                PrefixReconstructed = c.Prefix,
                Truncated = c.Truncated,
                MatchesLiveRow = live,
                Signature = c.Table.Name + "|" + (c.Rowid?.ToString(CultureInfo.InvariantCulture) ?? "?") + "|" + vsig,
            };
            if (_found.TryGetValue(rec.Signature, out CarvedRecord? prev))
            {
                prev.Occurrences++;
                if (rec.Confidence > prev.Confidence)
                {
                    rec.Occurrences = prev.Occurrences;
                    _found[rec.Signature] = rec;
                }
            }
            else
            {
                _found[rec.Signature] = rec;
            }
        }
    }

    private static int AliasDeclIndex(TableModel m)
    {
        for (int i = 0; i < m.DeclToRec.Length; i++)
        {
            if (m.DeclToRec[i] == m.AliasPos)
            {
                return i;
            }
        }

        return -1;
    }

    // ---- cells ---------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Parses a complete cell (payload size, rowid, record) at <paramref name="p"/>. Table leaf cells have a rowid; WITHOUT ROWID cells do not
    /// (fileformat2 §1.6). Accepted only if the record header and body add up to exactly the payload size, the cell fits before
    /// <paramref name="limit"/>, and some table can hold that many columns.
    /// </summary>
    private Cand? CellAt(byte[] d, int p, int limit, Origin o, TableModel[] tables, bool structured)
    {
        if (limit - p < 4)
        {
            return null;
        }

        int a = Varint.Read(d.AsSpan(p, Math.Min(9, limit - p)), out ulong plenU);
        if (a == 0 || a > 4 || plenU < 2 || plenU > (ulong)_maxPayload)
        {
            return null;
        }

        long plen = (long)plenU;
        bool rowidFormat = tables.Length > 0 && tables[0].HasRowid;
        long rowid = 0;
        int b = 0;
        if (rowidFormat)
        {
            b = Varint.Read(d.AsSpan(p + a, Math.Min(9, limit - p - a)), out ulong rid);
            if (b == 0 || b > 5)
            {
                return null;
            }

            rowid = (long)rid;
        }

        int hdr = p + a + b;
        int local = _geo.LocalSize(plen, rowidFormat);
        int avail = (int)Math.Min(local, limit - hdr);
        if (avail < 2 || !Header(d, hdr, hdr + avail, _maxN, out int hsz, out int ncols, out long body))
        {
            return null;
        }

        if (hsz + body != plen)
        {
            return null;
        }

        int size = a + b + local;
        uint ovf = 0;
        if (plen > local)
        {
            if (hdr + local + 4 > limit)
            {
                return null;
            }

            ovf = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(hdr + local));
            if (ovf < 2 || ovf > _snap.PageCount)
            {
                return null;
            }

            size += 4;
        }
        else if (hdr + plen > limit)
        {
            return null;
        }

        byte[] payload;
        bool truncated = false;
        string? ovNote = null;
        if (ovf == 0)
        {
            payload = d.AsSpan(hdr, (int)plen).ToArray();
        }
        else
        {
            payload = new byte[plen];
            d.AsSpan(hdr, local).CopyTo(payload);
            int got = local + FollowOverflow(ovf, payload.AsSpan(local), out bool complete);
            truncated = got < plen || !complete;
            ovNote = complete ? "overflow chain followed to its end (+3)" : "overflow chain does not end cleanly; the tail may belong to another page (-4)";
            if (got < plen)
            {
                Array.Clear(payload, got, payload.Length - got);
            }
        }

        Cand? best = null;
        foreach (TableModel t in tables)
        {
            if (ncols > t.N || (t.HasRowid != rowidFormat))
            {
                continue;
            }

            var c = new Cand { Off = p, Size = size, PageOff = p, Table = t, Rowid = rowidFormat ? rowid : null, Truncated = truncated };
            Score(c, payload, hsz, ncols, o, structured, exactLen: true);
            if (ovNote != null)
            {
                c.Reasons.Add(ovNote);
                c.Bits += ovNote.Contains("does not end", StringComparison.Ordinal) ? -4 : 3;
            }

            if (best == null || c.Bits > best.Bits)
            {
                if (best != null && best.Bits > c.Bits - 1)
                {
                    c.Others = best.Others + 1;
                }

                best = c;
            }
            else if (c.Bits > best.Bits - 1)
            {
                best.Others++;
            }
        }

        if (best != null && best.Others > 0)
        {
            best.Bits -= 1.5 * best.Others;
            best.Reasons.Add($"{best.Others} other table(s) fit as well (-{1.5 * best.Others:0.#})");
        }

        return best;
    }

    /// <summary>
    /// A deleted cell whose first four bytes (payload size, rowid, and maybe the record header size and first serial types) were overwritten by the
    /// freeblock's next-offset and size (fileformat2 §1.6). The record header is rebuilt from the table's column count and the intact bytes after it.
    /// </summary>
    private List<Cand> HeadCells(byte[] d, int from, int end, Origin o)
    {
        var res = new List<Cand>();
        foreach (TableModel t in o.Tables)
        {
            if (!t.HasRowid)
            {
                continue;
            }

            // A cell inside a merged freeblock can have its first four bytes replaced by the header of an earlier, smaller freeblock, so every
            // offset is a possible start; the end-of-cell checks in HeadEnd keep that from guessing.
            for (int p = from; p + 8 < end; p++)
            {
                Cand? best = null;
                for (int j = p + 2; j <= p + 10 && j + 2 < end; j++)
                {
                    Cand? c = j >= p + 4 ? HeadIntact(d, p, end, j, t, o, p == from) : HeadLost(d, p, end, j, t, o, p == from);
                    if (c != null && (best == null || c.Bits > best.Bits))
                    {
                        best = c;
                    }
                }

                if (best != null)
                {
                    res.Add(best);
                }
            }
        }

        return res;
    }

    /// <summary>
    /// Judges where a head-damaged cell ends. It must end within 3 bytes of the end of the free region (a lone deleted cell, possibly with absorbed
    /// fragments), or where the 2-byte size in its own overwritten head says (the size of the freeblock the cell once was). The first cell of a
    /// region may also end earlier, because later cells follow it. Returns the evidence in bits, or null to reject.
    /// </summary>
    private static double? HeadEnd(byte[] d, int p, int end, int cellEnd, bool first, List<string> why)
    {
        int slack = end - cellEnd;
        if (slack is >= 0 and <= 3)
        {
            why.Add($"record ends {slack} byte(s) before the free region's end (+3)");
            return 3;
        }

        // The overwritten head of a cell inside a merged freeblock is the header of the block that cell once began: its size reaches the end of
        // that block, which is the cell's own end or a later cell's end (cells above it had been freed first).
        int stale = Pg.U16(d, p + 2);
        if (stale >= 4 && p + stale <= end && Math.Abs((cellEnd - p) - stale) <= 3)
        {
            why.Add("record ends where the size in the overwritten head says the cell ended (+3)");
            return 3;
        }

        if (stale >= 4 && p + stale <= end && cellEnd < p + stale && end - (p + stale) <= 3 && !first)
        {
            why.Add("record ends inside the block the overwritten head describes; cells above it were freed first (+1.5)");
            return 1.5;
        }

        if (first && slack >= 0)
        {
            why.Add($"record ends {slack} bytes before the free region's end; later cells may follow (+0.5)");
            return 0.5;
        }

        return null;
    }

    private Cand? HeadIntact(byte[] d, int fb, int end, int j, TableModel t, Origin o, bool first)
    {
        if (!Header(d, j, end, t.N, out int hsz, out int ncols, out long body) || ncols > t.N)
        {
            return null;
        }

        long plen = hsz + body;
        int a = Varint.Length((ulong)plen);
        int bl = j - fb - a;
        if (bl < 1 || a > 4)
        {
            return null;
        }

        int local = _geo.LocalSize(plen, true);
        byte[] payload;
        bool truncated = false;
        int cellEnd;
        string? ovNote = null;
        bool complete = true;
        if (plen <= local)
        {
            cellEnd = j + (int)plen;
            if (cellEnd > end)
            {
                return null;
            }

            payload = d.AsSpan(j, (int)plen).ToArray();
        }
        else
        {
            if (j + local + 4 > end || plen > _maxPayload)
            {
                return null;
            }

            uint ovf = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(j + local));
            if (ovf < 2 || ovf > _snap.PageCount)
            {
                return null;
            }

            cellEnd = j + local + 4;
            payload = new byte[plen];
            d.AsSpan(j, local).CopyTo(payload);
            int got = local + FollowOverflow(ovf, payload.AsSpan(local), out complete);
            truncated = got < plen || !complete;
            ovNote = complete ? "overflow chain followed to its end (+3)" : "overflow chain does not end cleanly; the tail may belong to another page (-4)";
        }

        var c = new Cand { Off = fb, Size = cellEnd - fb, PageOff = fb, Table = t, Rowid = null, Prefix = true, Truncated = truncated };
        Score(c, payload, hsz, ncols, o, false, exactLen: false);
        c.Reasons.Add("first bytes of the cell were overwritten; payload size and rowid are lost");
        double? fit = HeadEnd(d, fb, end, cellEnd, first, c.Reasons);
        if (fit == null)
        {
            c.Unconfirmed = true;
            fit = 0;
        }

        c.Bits += fit.Value;
        if (ovNote != null)
        {
            c.Reasons.Add(ovNote);
            c.Bits += complete ? 3 : -4;
        }

        return c;
    }

    /// <summary>j is p+3 (header size lost) or p+2 (header size and the first serial type lost).</summary>
    private Cand? HeadLost(byte[] d, int p0, int end, int j, TableModel t, Origin o, bool first)
    {
        int lost = j == p0 + 3 ? 0 : 1;
        int want = t.N - lost;
        if (want < 0)
        {
            return null;
        }

        int p = p0 + 4;
        long b1 = 0;
        var types = _types;
        for (int i = 0; i < want; i++)
        {
            int n = Varint.Read(d.AsSpan(p, Math.Min(9, end - p)), out ulong ty);
            long cs;
            if (n == 0 || (cs = SerialType.ContentSize(ty)) < 0)
            {
                return null;
            }

            types[lost + i] = ty;
            b1 += cs;
            p += n;
        }

        int hsz = 1 + lost + (p - (p0 + 4));
        // The body starts right after the intact serial types, which end at p.
        int bodyStart = p;
        long totalBody = b1;
        double penalty = -1;
        string note = "header size byte lost, rebuilt from the column count (-1)";
        if (lost == 1)
        {
            long s0 = -1;
            if (t.AliasPos == 0)
            {
                // The rowid alias is stored as NULL, which has no body bytes: nothing needs solving, and the cell may end well before the region does.
                s0 = 0;
            }
            else
            {
                // Solve the first column's content size from where the cell must end.
                int stale = Pg.U16(d, p0 + 2);
                for (int pass = 0; pass < 2 && s0 < 0; pass++)
                {
                    int target = pass == 0 ? end : (stale >= 4 ? p0 + stale : -1);
                    for (int slack = 0; slack <= 3 && target > 0; slack++)
                    {
                        long s = target - slack - (bodyStart + b1);
                        if (s is >= 0 and <= 4096 && ValidSize0(s, t))
                        {
                            s0 = s;
                            break;
                        }
                    }
                }
            }

            if (s0 < 0)
            {
                return null;
            }

            ulong ty0 = GuessType(s0, t);
            types[0] = ty0;
            totalBody += s0;
            if (t.AliasPos == 0)
            {
                note = "header size and first serial type lost; the first column is the rowid alias, which is stored as NULL (-1)";
                penalty = -1;
            }
            else
            {
                note = $"header size and first serial type lost; first column inferred as {DescribeType(ty0)} from the cell length (-3)";
                penalty = -3;
            }
        }

        long plen = hsz + totalBody;
        int a = Varint.Length((ulong)plen);
        if (a + 1 > j - p0 || plen > _geo.LocalSize(plen, true) || bodyStart + totalBody > end || hsz > 127)
        {
            return null;
        }

        byte[] payload = new byte[plen];
        payload[0] = (byte)hsz;
        int w = 1;
        for (int i = 0; i < t.N; i++)
        {
            w += Varint.Write(payload.AsSpan(w), types[i]);
        }

        if (w != hsz)
        {
            return null;
        }

        d.AsSpan(bodyStart, (int)totalBody).CopyTo(payload.AsSpan(hsz));
        var c = new Cand { Off = p0, Size = bodyStart + (int)totalBody - p0, PageOff = p0, Table = t, Prefix = true };
        Score(c, payload, hsz, t.N, o, false, exactLen: false);
        c.Bits += penalty;
        c.Reasons.Add("first bytes of the cell were overwritten; payload size and rowid are lost");
        c.Reasons.Add(note);
        double? fit = HeadEnd(d, p0, end, bodyStart + (int)totalBody, first, c.Reasons);
        if (fit == null)
        {
            c.Unconfirmed = true;
            fit = 0;
        }

        c.Bits += fit.Value;
        return c;
    }

    private static bool ValidSize0(long s, TableModel t)
    {
        if (s < 0)
        {
            return false;
        }

        char aff = t.Aff[0];
        if (t.AliasPos == 0)
        {
            return s == 0;
        }

        return aff switch
        {
            'I' or 'N' => s is 0 or 1 or 2 or 3 or 4 or 6 or 8,
            'R' => s is 0 or 1 or 2 or 3 or 4 or 6 or 8,
            _ => true,
        };
    }

    /// <summary>Picks the serial type for a first column whose type byte was overwritten but whose content size is known.</summary>
    private static ulong GuessType(long s, TableModel t)
    {
        char aff = t.Aff[0];
        if (s == 0)
        {
            return 0;
        }

        if (t.AliasPos == 0)
        {
            return 0;
        }

        if (aff is 'I' or 'N' or 'R')
        {
            return s switch { 1 => 1, 2 => 2, 3 => 3, 4 => 4, 6 => 5, 8 => aff == 'R' ? 7UL : 6UL, _ => (ulong)(13 + (2 * s)) };
        }

        return aff == 'T' ? (ulong)(13 + (2 * s)) : (ulong)(12 + (2 * s));
    }

    private static string DescribeType(ulong ty) => ty == 0 ? "NULL" : ty <= 9 ? "a number" : ty % 2 == 0 ? "a blob" : "text";

    /// <summary>
    /// Reads and checks a record header at <paramref name="p"/>: size varint, then serial types up to exactly that size. Types 10 and 11 are reserved
    /// (fileformat2 §2.1). Fills the shared types array.
    /// </summary>
    private bool Header(byte[] d, int p, int limit, int maxCols, out int hsz, out int ncols, out long body)
    {
        hsz = 0;
        ncols = 0;
        body = 0;
        if (p >= limit)
        {
            return false;
        }

        int n = Varint.Read(d.AsSpan(p, Math.Min(9, limit - p)), out ulong h);
        if (n == 0 || h < (ulong)n + 1 || h > MaxHeader || p + (int)h > limit)
        {
            return false;
        }

        int q = p + n;
        int endH = p + (int)h;
        int cols = 0;
        long total = 0;
        while (q < endH)
        {
            if (cols >= maxCols || cols >= MaxHeader)
            {
                return false;
            }

            int m = Varint.Read(d.AsSpan(q, Math.Min(9, endH - q)), out ulong ty);
            if (m == 0)
            {
                return false;
            }

            long cs = SerialType.ContentSize(ty);
            if (cs < 0)
            {
                return false;
            }

            _types[cols++] = ty;
            total += cs;
            if (total > 64 * 1024 * 1024)
            {
                return false;
            }

            q += m;
        }

        if (cols == 0)
        {
            return false;
        }

        hsz = (int)h;
        ncols = cols;
        body = total;
        return true;
    }

    /// <summary>Follows an overflow chain through the snapshot's pages (fileformat2 §1.7), filling <paramref name="dst"/>; returns the bytes obtained.</summary>
    private int FollowOverflow(uint pgno, Span<byte> dst, out bool complete)
    {
        int got = 0;
        int guard = 0;
        complete = false;
        int per = _usable - 4;
        while (pgno != 0 && got < dst.Length && guard++ < (dst.Length / per) + 2)
        {
            if (pgno > _snap.PageCount || _space.KindOf(pgno) is PageKind.Unavailable)
            {
                return got;
            }

            byte[]? d = _snap.GetPageBytes(pgno);
            if (d == null)
            {
                return got;
            }

            int take = Math.Min(per, dst.Length - got);
            d.AsSpan(4, take).CopyTo(dst[got..]);
            got += take;
            uint next = Pg.U32(d, 0);
            if (got == dst.Length)
            {
                // The last page of a chain ends with a zero link; anything else means the page was reused or became a trunk.
                complete = next == 0;
                return got;
            }

            pgno = next;
        }

        return got;
    }

    // ---- scoring -------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Adds the evidence for a parsed record to <paramref name="c"/>, decoding values from <paramref name="payload"/> (header at 0).
    /// The weights are the model of docs/forensics.md.
    /// </summary>
    private void Score(Cand c, byte[] payload, int hsz, int ncols, Origin o, bool structured, bool exactLen)
    {
        TableModel t = c.Table;
        double bits = 0;
        var why = c.Reasons;
        // Re-read the types from the payload so a rebuilt header and a copied one are treated alike.
        ulong[] types = new ulong[ncols];
        int q = Varint.Read(payload, out _);
        for (int i = 0; i < ncols; i++)
        {
            q += Varint.Read(payload.AsSpan(q), out types[i]);
        }

        if (exactLen)
        {
            bits += 6;
            why.Add("payload size equals header size plus the sizes of the serial types (+6)");
            bits += 1;
        }
        else
        {
            bits += 4;
            why.Add("record header is consistent: serial types add up to a body that fits (+4)");
        }

        if (structured)
        {
            bits += 6;
            why.Add("cell pointer of a table leaf page with a sane header (+6)");
        }

        if (ncols == t.N)
        {
            bits += 2 + Math.Min(t.N, 8) * 0.5;
            why.Add($"{ncols} columns, as the table has (+{2 + Math.Min(t.N, 8) * 0.5:0.#})");
        }
        else
        {
            bits += 0.5;
            why.Add($"{ncols} columns where the table has {t.N} (a column was added later?) (+0.5)");
        }

        // Decode values and check each against the column's affinity (datatype3 §3).
        var recVals = new object?[t.N];
        int pos = hsz;
        double colBits = 0;
        for (int i = 0; i < ncols; i++)
        {
            long cs = SerialType.ContentSize(types[i]);
            if (pos + cs > payload.Length)
            {
                c.Truncated = true;
                break;
            }

            object? v = Decode(types[i], payload.AsSpan(pos, (int)cs));
            pos += (int)cs;
            recVals[i] = v;
            colBits += Affinity(t.Aff[i], i == t.AliasPos, types[i]);
        }

        bits += colBits;
        if (c.Rowid.HasValue && t.LiveCount > 0)
        {
            long r = c.Rowid.Value;
            if (r >= 1 && r <= t.MaxRowid + 1000)
            {
                bits += 1.5;
                why.Add("rowid is inside the table's live range or just above it (+1.5)");
            }
            else
            {
                bits -= 2;
                why.Add("rowid is far outside the table's live range (-2)");
            }
        }

        if (o.Tables.Length == 1)
        {
            bits += 1;
            why.Add("the page belongs to this table (+1)");
        }

        // Map record positions to declared columns.
        var decl = new object?[t.DeclCount];
        for (int i = 0; i < t.DeclCount; i++)
        {
            int ri = t.DeclToRec[i];
            if (ri >= 0 && ri < recVals.Length)
            {
                decl[i] = ri == t.AliasPos ? (c.Rowid.HasValue ? c.Rowid.Value : null) : recVals[ri];
            }
        }

        // A record that says nothing (all NULL or empty, the signature of zeroed space) is not evidence of a row.
        bool informative = false;
        for (int i = 0; i < t.N; i++)
        {
            if (i != t.AliasPos && recVals[i] is not null && recVals[i] is not "" && !(recVals[i] is byte[] { Length: 0 })
                && !(recVals[i] is string str && str.AsSpan().IndexOfAnyExcept('\0') < 0) && !(recVals[i] is byte[] bl && bl.AsSpan().IndexOfAnyExcept((byte)0) < 0))
            {
                informative = true;
                break;
            }
        }

        if (!informative)
        {
            bits = -100;
            why.Add("every value is NULL or empty: no evidence");
        }

        c.Values = decl;
        c.Bits = bits;
    }

    private object? Decode(ulong ty, ReadOnlySpan<byte> b)
    {
        switch (ty)
        {
            case 0: return null;
            case 8: return 0L;
            case 9: return 1L;
            case 7: return BinaryPrimitives.ReadDoubleBigEndian(b);
            case <= 6: return SerialType.ReadInt(b, b.Length);
            default: return (ty & 1) == 0 ? b.ToArray() : TextCodec.Decode(b, _snap.Encoding);
        }
    }

    private static double Affinity(char aff, bool alias, ulong ty)
    {
        if (alias)
        {
            return ty == 0 ? 0.5 : -3;
        }

        if (ty == 0)
        {
            return 0;
        }

        bool isText = ty >= 13 && (ty & 1) == 1;
        bool isNum = ty is >= 1 and <= 9;
        return aff switch
        {
            'T' => isText ? 1 : isNum ? -2 : -1,
            'I' or 'N' or 'R' => isNum ? 1 : 0,
            _ => 0.5,
        };
    }

    /// <summary>A page source that serves no pages: it only gives a <see cref="BtContext"/> the page geometry for the payload-size formula.</summary>
    private sealed class CellGeometry(ForensicSnapshot s) : IBtPageSource
    {
        public int PageSize => s.PageSize;

        public int UsableSize => s.UsableSize;

        public uint PageCount => s.PageCount;

        public bool AutoVacuum => false;

        public Exception? LastError => null;

        public uint LargestRootPage { get => 0; set => throw new NotSupportedException(); }

        public BtStatus GetPage(uint pgno, out PageHandle? page) => throw new NotSupportedException();

        public void Release(PageHandle page) => throw new NotSupportedException();

        public BtStatus MakeWritable(PageHandle page) => throw new NotSupportedException();

        public BtStatus Reserve(int allocations, int frees) => throw new NotSupportedException();

        public BtStatus ReservePtrmap(uint pgno) => throw new NotSupportedException();

        public PageHandle Allocate() => throw new NotSupportedException();

        public void Free(uint pgno) => throw new NotSupportedException();

        public void PtrmapPut(uint pgno, byte type, uint parent) => throw new NotSupportedException();
    }
}
