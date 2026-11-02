using System.Buffers.Binary;

namespace RecursiveAI.OneLite.Storage.BTree;

/// <summary>B-tree page layout (fileformat2 §1.6): the page header, cell pointer array, cell content area, freeblocks and fragments.</summary>
internal static class Pg
{
    public const byte TableInterior = 5;
    public const byte TableLeaf = 13;
    public const byte IndexInterior = 2;
    public const byte IndexLeaf = 10;

    public static int HdrOff(uint pgno) => pgno == 1 ? 100 : 0;

    public static bool IsLeaf(byte type) => (type & 8) != 0;

    public static bool IsIntKey(byte type) => (type & 4) != 0;

    public static bool ValidType(byte t) => t is TableInterior or TableLeaf or IndexInterior or IndexLeaf;

    public static int HeaderSize(byte type) => IsLeaf(type) ? 8 : 12;

    public static ushort U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o, 2));

    public static void PutU16(byte[] d, int o, int v) => BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(o, 2), (ushort)v);

    public static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o, 4));

    public static void PutU32(byte[] d, int o, uint v) => BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(o, 4), v);

    public static int NCell(byte[] d, int h) => U16(d, h + 3);

    public static int FirstFree(byte[] d, int h) => U16(d, h + 1);

    public static int Frag(byte[] d, int h) => d[h + 7];

    /// <summary>Start of the cell content area; the stored value 0 means 65536 (fileformat2 §1.6).</summary>
    public static int ContentStart(byte[] d, int h)
    {
        int v = U16(d, h + 5);
        return v == 0 ? 65536 : v;
    }

    public static void SetContentStart(byte[] d, int h, int v) => PutU16(d, h + 5, v == 65536 ? 0 : v);

    public static uint RightPtr(byte[] d, int h) => U32(d, h + 8);

    public static int PtrArray(byte type, int h) => h + HeaderSize(type);

    public static int CellOffset(byte[] d, int h, byte type, int i) => U16(d, h + HeaderSize(type) + 2 * i);

    /// <summary>Formats an empty page of the given type.</summary>
    public static void Zero(byte[] d, int h, int usable, byte type)
    {
        Array.Clear(d, h, d.Length - h);
        d[h] = type;
        SetContentStart(d, h, usable);
    }

    /// <summary>
    /// Validates the page header of a page about to be used as a B-tree page. Cell offsets are checked when a cell is read.
    /// </summary>
    public static byte CheckHeader(PageHandle pg, int usable)
    {
        byte[] d = pg.Data;
        uint n = pg.Number;
        int h = HdrOff(n);
        byte type = d[h];
        if (!ValidType(type))
        {
            throw OneLiteException.Corrupt(n, $"page type byte {type} is not a B-tree page");
        }

        int hs = HeaderSize(type);
        int cells = NCell(d, h);
        int ptrEnd = h + hs + 2 * cells;
        if (ptrEnd > usable)
        {
            throw OneLiteException.Corrupt(n, $"cell pointer array of {cells} cells runs past the page");
        }

        int cs = ContentStart(d, h);
        if (cs < ptrEnd || cs > usable)
        {
            throw OneLiteException.Corrupt(n, $"cell content start {cs} outside [{ptrEnd}, {usable}]");
        }

        int ff = FirstFree(d, h);
        if (ff != 0 && (ff < cs || ff > usable - 4))
        {
            throw OneLiteException.Corrupt(n, $"first freeblock offset {ff} outside the content area");
        }

        return type;
    }

    /// <summary>Sum of free bytes on a page: the gap, the freeblocks and the fragments. Validates the freeblock chain.</summary>
    public static int FreeBytes(uint pgno, byte[] d, int h, int usable)
    {
        byte type = d[h];
        int ptrEnd = PtrArray(type, h) + 2 * NCell(d, h);
        int cs = ContentStart(d, h);
        int total = cs - ptrEnd + Frag(d, h);
        int fb = FirstFree(d, h);
        int prevEnd = 0;
        int guard = usable / 4 + 1;
        while (fb != 0)
        {
            if (--guard < 0)
            {
                throw OneLiteException.Corrupt(pgno, "freeblock chain does not end");
            }

            if (fb < cs || fb < prevEnd || fb > usable - 4)
            {
                throw OneLiteException.Corrupt(pgno, $"freeblock at {fb} is out of order or outside the content area");
            }

            int size = U16(d, fb + 2);
            if (size < 4 || fb + size > usable)
            {
                throw OneLiteException.Corrupt(pgno, $"freeblock at {fb} has size {size}");
            }

            total += size;
            prevEnd = fb + size;
            fb = U16(d, fb);
        }

        if (total > usable)
        {
            throw OneLiteException.Corrupt(pgno, "free space exceeds the page");
        }

        return total;
    }

    /// <summary>The padded size a cell of <paramref name="size"/> bytes occupies in the content area (a cell is at least 4 bytes, fileformat2 §1.6).</summary>
    public static int Footprint(int size) => size < 4 ? 4 : size;

    /// <summary>Reorders the content area so all cells are contiguous at the end of the page and no freeblocks or fragments remain.</summary>
    public static void Defragment(uint pgno, byte[] d, int h, int usable, byte[] scratch, Func<byte[], int, int> sizeOf)
    {
        byte type = d[h];
        int n = NCell(d, h);
        int pa = PtrArray(type, h);
        int end = usable;
        Buffer.BlockCopy(d, 0, scratch, 0, d.Length);
        for (int i = 0; i < n; i++)
        {
            int off = U16(scratch, pa + 2 * i);
            int size = sizeOf(scratch, off);
            size = Footprint(size);
            if (off < pa + 2 * n || off + size > usable)
            {
                throw OneLiteException.Corrupt(pgno, $"cell {i} at {off} size {size} is outside the content area");
            }

            end -= size;
            Buffer.BlockCopy(scratch, off, d, end, size);
            PutU16(d, pa + 2 * i, end);
        }

        // Zero the freed area: the file then never exposes stale content (secure by default, matches what the reference writes with its default settings closely enough for integrity).
        Array.Clear(d, pa + 2 * n, end - (pa + 2 * n));
        SetContentStart(d, h, end);
        PutU16(d, h + 1, 0);
        d[h + 7] = 0;
    }

    /// <summary>
    /// Finds room for a cell of footprint <paramref name="size"/> plus its two-byte pointer. Returns the offset, or -1 when the page lacks the room (the caller defragments or overflows).
    /// </summary>
    public static int Allocate(uint pgno, byte[] d, int h, int usable, int size)
    {
        byte type = d[h];
        int n = NCell(d, h);
        int ptrEnd = PtrArray(type, h) + 2 * n;
        int cs = ContentStart(d, h);
        int gap = cs - ptrEnd;
        if (gap < 2)
        {
            return -1;
        }

        // Reuse a freeblock first (first fit) so small deletes and inserts do not eat the gap.
        if (Frag(d, h) <= 57)
        {
            int fb = FirstFree(d, h);
            int prev = 0;
            int guard = usable / 4 + 1;
            while (fb != 0 && --guard >= 0)
            {
                if (fb < cs || fb > usable - 4)
                {
                    throw OneLiteException.Corrupt(pgno, $"freeblock at {fb} is outside the content area");
                }

                int fsize = U16(d, fb + 2);
                int next = U16(d, fb);
                if (fsize < 4 || fb + fsize > usable)
                {
                    throw OneLiteException.Corrupt(pgno, $"freeblock at {fb} has size {fsize}");
                }

                // A block is used only if nothing or at least a 4-byte block is left over. Leaving 1-3 bytes would create a fragment, and
                // two fragments next to each other form a run longer than 3 bytes, which a reader treats as a damaged page (fileformat2 §1.6).
                if (fsize >= size && (fsize == size || fsize - size >= 4))
                {
                    int rem = fsize - size;
                    if (rem == 0)
                    {
                        if (prev == 0)
                        {
                            PutU16(d, h + 1, next);
                        }
                        else
                        {
                            PutU16(d, prev, next);
                        }

                        return fb;
                    }

                    PutU16(d, fb + 2, rem);
                    return fb + rem;
                }

                prev = fb;
                fb = next;
            }
        }

        if (gap >= size + 2)
        {
            cs -= size;
            SetContentStart(d, h, cs);
            return cs;
        }

        return -1;
    }

    private static void TakeFragments(uint pgno, byte[] d, int h, int gap)
    {
        // fileformat2 §1.6: the header counts the fragments, the 1-3 byte runs that belong to no cell and no freeblock
        int frag = Frag(d, h) - gap;
        if (frag < 0)
        {
            throw OneLiteException.Corrupt(pgno, "fragment count is smaller than the gap between free areas");
        }

        d[h + 7] = (byte)frag;
    }

    /// <summary>
    /// Returns the bytes of a removed cell to the page as a freeblock (or to the gap). A freeblock never has fewer than 4 bytes of free space
    /// next to it that are not part of it: fragments (runs of 1-3 bytes) on either side are absorbed into the freed space, and the header's
    /// fragment count goes down by what was absorbed, which is how the reference leaves a page it has deleted from.
    /// </summary>
    public static void FreeSpace(uint pgno, byte[] d, int h, int usable, int off, int size)
    {
        size = Footprint(size);
        int cs = ContentStart(d, h);
        if (off < cs || off + size > usable)
        {
            throw OneLiteException.Corrupt(pgno, $"cell at {off} size {size} is outside the content area");
        }

        if (off - cs < 4)
        {
            // the freed cell is at the start of the content area, with at most a fragment before it: the gap grows
            TakeFragments(pgno, d, h, off - cs);
            cs = off + size;
            int f = FirstFree(d, h);
            if (f != 0 && f >= cs && f - cs < 4)
            {
                TakeFragments(pgno, d, h, f - cs);
                cs = f + U16(d, f + 2);
                PutU16(d, h + 1, U16(d, f));
            }

            SetContentStart(d, h, cs);
            return;
        }

        int prev = 0;
        int fb = FirstFree(d, h);
        int guard = usable / 4 + 1;
        while (fb != 0 && fb < off)
        {
            if (--guard < 0 || fb < cs || fb > usable - 4)
            {
                throw OneLiteException.Corrupt(pgno, "freeblock chain is damaged");
            }

            prev = fb;
            fb = U16(d, fb);
        }

        if (fb != 0 && off + size > fb)
        {
            throw OneLiteException.Corrupt(pgno, "freed cell overlaps a freeblock");
        }

        if (prev != 0 && prev + U16(d, prev + 2) > off)
        {
            throw OneLiteException.Corrupt(pgno, "freed cell overlaps a freeblock");
        }

        int newOff = off, newSize = size, next = fb;
        if (fb != 0 && fb - (off + size) < 4)
        {
            int gap = fb - (off + size);
            TakeFragments(pgno, d, h, gap);
            newSize += gap + U16(d, fb + 2);
            next = U16(d, fb);
        }

        if (prev != 0 && off - (prev + U16(d, prev + 2)) < 4)
        {
            int gap = off - (prev + U16(d, prev + 2));
            TakeFragments(pgno, d, h, gap);
            newOff = prev;
            newSize += gap + U16(d, prev + 2);
            PutU16(d, prev, next);
            PutU16(d, prev + 2, newSize);
            return;
        }

        PutU16(d, newOff, next);
        PutU16(d, newOff + 2, newSize);
        if (prev == 0)
        {
            PutU16(d, h + 1, newOff);
        }
        else
        {
            PutU16(d, prev, newOff);
        }
    }
}
