using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Storage.BTree;

/// <summary>Everything decoded from one cell (fileformat2 §1.6 "cell format").</summary>
internal struct CellInfo
{
    /// <summary>Bytes the cell occupies, not padded to the 4-byte minimum.</summary>
    public int Size;

    /// <summary>Payload bytes stored on the page.</summary>
    public int Local;

    /// <summary>Total payload bytes (local plus overflow).</summary>
    public long PayloadSize;

    /// <summary>Table cells: the rowid (or, in an interior cell, the largest rowid in the left subtree).</summary>
    public long Rowid;

    /// <summary>Interior cells: the left child page.</summary>
    public uint Child;

    /// <summary>Offset of the payload within the buffer the cell was parsed from.</summary>
    public int PayloadOff;

    /// <summary>First overflow page, or 0.</summary>
    public uint Overflow;
}

/// <summary>
/// The B-tree layer's shared state for one database: the page source, page geometry and the derived payload limits
/// (fileformat2 §1.6), the open cursors (so a write can save the others), and cell encode/decode.
/// </summary>
public sealed class BtContext
{
    private readonly List<BtCursor> _cursors = [];

    /// <summary>Creates a context.</summary>
    /// <param name="source">The page source (the pager).</param>
    /// <param name="encoding">The database text encoding.</param>
    public BtContext(IBtPageSource source, TextEncoding encoding)
    {
        Source = source;
        Encoding = encoding;
        Usable = source.UsableSize;
        PageSize = source.PageSize;
        if (Usable < 480 || PageSize < 512 || PageSize > 65536)
        {
            throw new ArgumentException("page geometry is outside the range the file format allows");
        }

        MaxLocalIndex = ((Usable - 12) * 64 / 255) - 23;
        MinLocal = ((Usable - 12) * 32 / 255) - 23;
        MaxLocalTable = Usable - 35;
        Scratch = new byte[PageSize];
    }

    /// <summary>The page source.</summary>
    public IBtPageSource Source { get; }

    // Pages the auto-vacuum root maintenance keeps pinned while it waits for I/O (AutoVacuumRoots).
    internal AutoVacuumRoots.Pins AvPins { get; } = new();

    /// <summary>The database text encoding.</summary>
    public TextEncoding Encoding { get; }

    /// <summary>Usable bytes per page.</summary>
    public int Usable { get; }

    /// <summary>Page size.</summary>
    public int PageSize { get; }

    /// <summary>Maximum local payload in an index cell: (U-12)*64/255-23.</summary>
    public int MaxLocalIndex { get; }

    /// <summary>Minimum local payload once a cell spills: (U-12)*32/255-23.</summary>
    public int MinLocal { get; }

    /// <summary>Maximum local payload in a table leaf cell: U-35.</summary>
    public int MaxLocalTable { get; }

    internal byte[] Scratch { get; }

    internal List<BtCursor> Cursors => _cursors;

    /// <summary>Called with a table's root page and a rowid (long.MinValue for every row) just before that row changes; the engine uses it to expire open blob handles.</summary>
    internal Action<uint, long>? Changed { get; set; }

    /// <summary>The trace of the open concurrent transaction on this database, or null (docs/concurrency.md).</summary>
    internal ConcTrace? Trace { get; set; }

    /// <summary>How many bytes of a payload of <paramref name="payload"/> bytes stay on the page (fileformat2 §1.6).</summary>
    public int LocalSize(long payload, bool tableLeaf)
    {
        int x = tableLeaf ? MaxLocalTable : MaxLocalIndex;
        if (payload <= x)
        {
            return (int)payload;
        }

        int k = MinLocal + (int)((payload - MinLocal) % (Usable - 4));
        return k <= x ? k : MinLocal;
    }

    /// <summary>The largest cell an index page can hold: child pointer, size varint, local payload, overflow pointer.</summary>
    internal int MaxIndexCell => 4 + 9 + MaxLocalIndex + 4;

    /// <summary>Parses the cell at <paramref name="off"/> in <paramref name="d"/>, which may extend to <paramref name="limit"/>.</summary>
    internal CellInfo ParseCell(uint pgno, byte type, byte[] d, int off, int limit)
    {
        var ci = new CellInfo();
        int p = off;
        if (!Pg.IsLeaf(type))
        {
            if (p + 4 > limit)
            {
                throw OneLiteException.Corrupt(pgno, "interior cell is cut off");
            }

            ci.Child = Pg.U32(d, p);
            p += 4;
        }

        if (type == Pg.TableInterior)
        {
            int n = Varint.Read(d.AsSpan(p, Math.Max(0, limit - p)), out ulong key);
            if (n == 0)
            {
                throw OneLiteException.Corrupt(pgno, "rowid varint is cut off");
            }

            ci.Rowid = (long)key;
            ci.Size = p + n - off;
            return ci;
        }

        int n1 = Varint.Read(d.AsSpan(p, Math.Max(0, limit - p)), out ulong psize);
        if (n1 == 0)
        {
            throw OneLiteException.Corrupt(pgno, "payload size varint is cut off");
        }

        p += n1;
        if (psize > 0x7fffffffUL)
        {
            throw OneLiteException.Corrupt(pgno, $"payload size {psize} is too large");
        }

        ci.PayloadSize = (long)psize;
        if (type == Pg.TableLeaf)
        {
            int n2 = Varint.Read(d.AsSpan(p, Math.Max(0, limit - p)), out ulong rowid);
            if (n2 == 0)
            {
                throw OneLiteException.Corrupt(pgno, "rowid varint is cut off");
            }

            ci.Rowid = (long)rowid;
            p += n2;
        }

        ci.Local = LocalSize(ci.PayloadSize, type == Pg.TableLeaf);
        ci.PayloadOff = p;
        p += ci.Local;
        if (ci.PayloadSize > ci.Local)
        {
            if (p + 4 > limit)
            {
                throw OneLiteException.Corrupt(pgno, "overflow pointer runs past the cell");
            }

            ci.Overflow = Pg.U32(d, p);
            p += 4;
        }

        if (p > limit)
        {
            throw OneLiteException.Corrupt(pgno, "cell runs past the end of the page");
        }

        ci.Size = p - off;
        return ci;
    }

    /// <summary>Reads cell <paramref name="i"/> of a page, validating its offset.</summary>
    internal CellInfo ReadCell(PageHandle pg, int h, byte type, int i, out int off)
    {
        byte[] d = pg.Data;
        off = Pg.CellOffset(d, h, type, i);
        int ptrEnd = Pg.PtrArray(type, h) + 2 * Pg.NCell(d, h);
        if (off < ptrEnd || off > Usable - 4)
        {
            throw OneLiteException.Corrupt(pg.Number, $"cell {i} offset {off} outside [{ptrEnd}, {Usable - 4}]");
        }

        CellInfo ci = ParseCell(pg.Number, type, d, off, Usable);
        return ci;
    }

    /// <summary>Size of the cell at <paramref name="off"/> for a page of <paramref name="type"/> (used by defragmentation).</summary>
    internal int CellSizeAt(uint pgno, byte type, byte[] d, int off)
    {
        if (off < 0 || off > Usable - 4)
        {
            throw OneLiteException.Corrupt(pgno, $"cell offset {off} outside the page");
        }

        return ParseCell(pgno, type, d, off, Usable).Size;
    }

    /// <summary>Encodes a cell into <paramref name="dst"/> and returns its size.</summary>
    internal int BuildCell(byte type, long rowid, uint child, ReadOnlySpan<byte> payload, uint overflow, Span<byte> dst)
    {
        int p = 0;
        if (!Pg.IsLeaf(type))
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(dst, child);
            p = 4;
        }

        if (type == Pg.TableInterior)
        {
            p += Varint.Write(dst[p..], (ulong)rowid);
            return p;
        }

        p += Varint.Write(dst[p..], (ulong)payload.Length);
        if (type == Pg.TableLeaf)
        {
            p += Varint.Write(dst[p..], (ulong)rowid);
        }

        int local = LocalSize(payload.Length, type == Pg.TableLeaf);
        payload[..local].CopyTo(dst[p..]);
        p += local;
        if (payload.Length > local)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(dst[p..], overflow);
            p += 4;
        }

        return p;
    }

    /// <summary>Largest possible cell for a page type (buffer sizing).</summary>
    internal int MaxCell(byte type) => type == Pg.TableInterior ? 13 : type == Pg.TableLeaf ? 9 + 9 + MaxLocalTable + 4 : 4 + 9 + MaxLocalIndex + 4;

    /// <summary>The pointer-map duties of placing <paramref name="cell"/> on page <paramref name="parent"/>: its child and its first overflow page now have this parent.</summary>
    internal void PtrmapForCell(uint parent, byte type, byte[] cell, int off, int len)
    {
        if (!Source.AutoVacuum)
        {
            return;
        }

        CellInfo ci = ParseCell(parent, type, cell, off, off + len);
        if (!Pg.IsLeaf(type))
        {
            Source.PtrmapPut(ci.Child, 5, parent);
        }

        if (ci.Overflow != 0)
        {
            Source.PtrmapPut(ci.Overflow, 3, parent);
        }
    }

    internal void Register(BtCursor c) => _cursors.Add(c);

    internal void Unregister(BtCursor c) => _cursors.Remove(c);
}
