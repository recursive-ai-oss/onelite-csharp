using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Linq;
using RecursiveAI.OneLite.Planning;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.VirtualTables;

/// <summary>
/// The eponymous <c>dbstat</c> table (dbstat.html): one row per B-tree page, or per B-tree with <c>aggregate</c>. A scan reads every page of the
/// schema it was asked about when it starts, so unlike the other built-in modules a first <c>Filter</c> blocks on I/O (it is not suspendable).
/// </summary>
internal sealed class DbStatModule : BuiltinModule
{
    internal override string Name => "dbstat";

    internal override bool Eponymous => true;

    internal override VTab Connect(Connection conn, TableInfo table, string schema, ImmutableArray<string> args, Action<string> declare)
    {
        declare("CREATE TABLE x(name,path,pageno,pagetype,ncell,payload,unused,mx_payload,pgoffset,pgsize,schema HIDDEN,aggregate HIDDEN)");
        return new DbStatTable(conn, args.IsDefaultOrEmpty ? "main" : args[0].Trim());
    }
}

internal sealed class DbStatTable(Connection conn, string defaultSchema) : VTab
{
    internal Connection Conn => conn;

    internal string DefaultSchema => defaultSchema;

    internal override void BestIndex(BestIndexInfo info)
    {
        int n = 0;
        int mask = 0;
        for (int i = 0; i < info.Constraints.Length; i++)
        {
            IndexConstraint c = info.Constraints[i];
            if (c.Usable && c.Op == ConstraintOp.Eq && c.Column is 10 or 11)
            {
                info.ConstraintUsage[i].ArgvIndex = ++n;
                info.ConstraintUsage[i].Omit = true;
                mask |= c.Column == 10 ? 1 : 2;
            }
        }

        // Arguments arrive in the order they were given; the mask (bit 0 schema, bit 1 aggregate) says which is which.
        var order = new List<int>();
        for (int i = 0; i < info.Constraints.Length; i++)
        {
            if (info.ConstraintUsage[i].ArgvIndex > 0)
            {
                order.Add(info.Constraints[i].Column);
            }
        }

        info.IndexNumber = mask;
        info.IndexString = string.Concat(order.Select(c => c == 10 ? 's' : 'a'));
        info.EstimatedCost = 1e6;
    }

    internal override VCursor Open() => new DbStatCursor(this);

    public override void Dispose()
    {
    }
}

internal sealed class DbStatCursor(DbStatTable table) : VCursor
{
    private sealed record Row(string Name, string? Path, long PageNo, string? PageType, long NCell, long Payload, long Unused, long MxPayload, long? PgOffset, long PgSize);

    private List<Row> _rows = [];
    private int _pos;

    internal override bool Eof => _pos >= _rows.Count;

    internal override ModStatus Filter(int indexNumber, string? indexString, ReadOnlySpan<Mem> args)
    {
        string schema = table.DefaultSchema;
        bool aggregate = false;
        for (int i = 0; i < args.Length && indexString is not null; i++)
        {
            if (indexString[i] == 's')
            {
                schema = args[i].Kind == StorageClass.Null ? schema : TextCodec.Decode(args[i].Bytes, Vm.Values.BlobEncoding);
            }
            else
            {
                aggregate = Vm.Values.IsTrue(args[i]);
            }
        }

        _rows = Collect(table.Conn, schema, aggregate);
        _pos = 0;
        return ModStatus.Ok;
    }

    internal override ModStatus Next()
    {
        _pos++;
        return ModStatus.Ok;
    }

    internal override ModStatus Column(int index, ref Mem dst, ref byte[]? own)
    {
        Row r = _rows[_pos];
        switch (index)
        {
            case 0: SetText(ref dst, ref own, r.Name); break;
            case 1: SetText(ref dst, ref own, r.Path); break;
            case 2: Vm.Values.SetInt(ref dst, r.PageNo); break;
            case 3: SetText(ref dst, ref own, r.PageType); break;
            case 4: Vm.Values.SetInt(ref dst, r.NCell); break;
            case 5: Vm.Values.SetInt(ref dst, r.Payload); break;
            case 6: Vm.Values.SetInt(ref dst, r.Unused); break;
            case 7: Vm.Values.SetInt(ref dst, r.MxPayload); break;
            case 8:
                if (r.PgOffset is { } o)
                {
                    Vm.Values.SetInt(ref dst, o);
                }
                else
                {
                    Vm.Values.SetNull(ref dst);
                }

                break;
            case 9: Vm.Values.SetInt(ref dst, r.PgSize); break;
            default: Vm.Values.SetNull(ref dst); break;
        }

        return ModStatus.Ok;
    }

    private static void SetText(ref Mem dst, ref byte[]? own, string? s)
    {
        if (s is null)
        {
            Vm.Values.SetNull(ref dst);
            return;
        }

        byte[] b = Vm.Values.BlobEncoding switch
        {
            TextEncoding.Utf8 => Encoding.UTF8.GetBytes(s),
            TextEncoding.Utf16Le => Encoding.Unicode.GetBytes(s),
            _ => Encoding.BigEndianUnicode.GetBytes(s),
        };
        Vm.Values.SetBytes(ref dst, ref own, StorageClass.Text, b);
    }

    internal override ModStatus RowId(out long rowid)
    {
        rowid = _pos + 1;
        return ModStatus.Ok;
    }

    public override void Dispose()
    {
    }

    // ------------------------------------------------------------------ walking the B-trees

    private static byte[] ReadPage(Connection conn, int db, IBtPageSource src, uint pgno)
    {
        conn.PagerOf(db).RequireBlockingBackend("dbstat");
        PageHandle? h;
        BtStatus s;
        while ((s = src.GetPage(pgno, out h)) == BtStatus.Pending)
        {
            conn.PagerOf(db).ExecuteSync();
        }

        if (s == BtStatus.Error)
        {
            throw Vm.Machine.Translate(src.LastError ?? new InvalidOperationException("page read failed"));
        }

        byte[] copy = (byte[])h!.Data.Clone();
        src.Release(h);
        return copy;
    }

    private static OneLiteException Corrupt(uint pgno, string what) => new(ResultCode.Corrupt, "database disk image is malformed", $"page {pgno}: {what}");

    private static List<Row> Collect(Connection conn, string schema, bool aggregate)
    {
        Catalog cat = conn.Catalog ?? throw new OneLiteException(ResultCode.Error, "no such schema: " + schema);
        int db = cat.SlotOf(schema);
        if (db < 0)
        {
            throw new OneLiteException(ResultCode.Error, "no such schema: " + schema);
        }

        IBtPageSource src = conn.ContextOf(db).Source;
        var trees = new List<(string Name, uint Root)> { (db == 1 ? "sqlite_temp_schema" : "sqlite_schema", 1) };
        foreach (SchemaRow r in cat.RowsOf(db))
        {
            if (r.Root > 0 && r.Type is "table" or "index")
            {
                trees.Add((r.Name, r.Root));
            }
        }

        var rows = new List<Row>();
        int pageSize = src.PageSize;
        foreach ((string name, uint root) in trees)
        {
            var pages = new List<Row>();
            Walk(conn, db, src, name, root, "/", pages, 0, new int[1]);
            if (!aggregate)
            {
                rows.AddRange(pages);
                continue;
            }

            long ncell = 0, payload = 0, unused = 0, mx = 0;
            foreach (Row p in pages)
            {
                ncell += p.NCell;
                payload += p.Payload;
                unused += p.Unused;
                mx = Math.Max(mx, p.MxPayload);
            }

            rows.Add(new Row(name, null, pages.Count, null, ncell, payload, unused, mx, null, (long)pages.Count * pageSize));
        }

        return rows;
    }

    private static int Varint(byte[] p, int at, out long v)
    {
        v = 0;
        for (int i = 0; i < 9 && at + i < p.Length; i++)
        {
            byte b = p[at + i];
            if (i == 8)
            {
                v = (v << 8) | b;
                return 9;
            }

            v = (v << 7) | (uint)(b & 0x7f);
            if ((b & 0x80) == 0)
            {
                return i + 1;
            }
        }

        throw new OneLiteException(ResultCode.Corrupt, "database disk image is malformed", "truncated varint");
    }

    private static void Walk(Connection conn, int db, IBtPageSource src, string name, uint pgno, string path, List<Row> output, int depth, int[] visited)
    {
        if (depth > 40 || ++visited[0] > 50_000_000)
        {
            throw Corrupt(pgno, "b-tree too deep");
        }

        int pageSize = src.PageSize;
        int usable = src.UsableSize;
        byte[] pg = ReadPage(conn, db, src, pgno);
        int hdr = pgno == 1 ? 100 : 0;
        byte type = pg[hdr];
        bool interior = type is 2 or 5;
        bool table = type is 5 or 13;
        if (type is not (2 or 5 or 10 or 13))
        {
            throw Corrupt(pgno, "bad b-tree page type " + type);
        }

        int hsize = interior ? 12 : 8;
        int ncell = BinaryPrimitives.ReadUInt16BigEndian(pg.AsSpan(hdr + 3));
        int ptrs = hdr + hsize;
        if (ptrs + (2 * ncell) > usable)
        {
            throw Corrupt(pgno, "cell pointer array overruns the page");
        }

        // fileformat2 §1.6: free space is the gap before the cell content area, the freeblocks, and the fragmented bytes.
        int content = BinaryPrimitives.ReadUInt16BigEndian(pg.AsSpan(hdr + 5));
        if (content == 0)
        {
            content = 65536;
        }

        long unused = Math.Max(0, content - (ptrs + (2 * ncell))) + pg[hdr + 7];
        int fb = BinaryPrimitives.ReadUInt16BigEndian(pg.AsSpan(hdr + 1));
        for (int guard = 0; fb != 0 && guard < pageSize / 4; guard++)
        {
            if (fb + 4 > pageSize)
            {
                throw Corrupt(pgno, "freeblock outside the page");
            }

            unused += BinaryPrimitives.ReadUInt16BigEndian(pg.AsSpan(fb + 2));
            fb = BinaryPrimitives.ReadUInt16BigEndian(pg.AsSpan(fb));
        }

        long payload = 0, mx = 0;
        var kids = new List<(string Path, uint Pgno)>();
        var overflows = new List<(string Path, uint First, long Remaining)>();
        int x = table ? usable - 35 : (((usable - 12) * 64) / 255) - 23;
        int m = (((usable - 12) * 32) / 255) - 23;
        for (int i = 0; i < ncell; i++)
        {
            int off = BinaryPrimitives.ReadUInt16BigEndian(pg.AsSpan(ptrs + (2 * i)));
            if (off < ptrs || off + 4 > pageSize)
            {
                throw Corrupt(pgno, "cell offset outside the page");
            }

            int p = off;
            string cellPath = path + i.ToString("x3", CultureInfo.InvariantCulture);
            if (interior)
            {
                uint child = BinaryPrimitives.ReadUInt32BigEndian(pg.AsSpan(p));
                p += 4;
                kids.Add((cellPath + "/", child));
                if (table)
                {
                    continue;
                }
            }

            p += Varint(pg, p, out long total);

            if (table && !interior)
            {
                p += Varint(pg, p, out _);
            }

            int local;
            if (total <= x)
            {
                local = (int)total;
            }
            else
            {
                int k = m + (int)((total - m) % (usable - 4));
                local = k <= x ? k : m;
            }

            payload += local;
            mx = Math.Max(mx, total);
            if (total > local)
            {
                if (p + local + 4 > pageSize)
                {
                    throw Corrupt(pgno, "overflow pointer outside the page");
                }

                overflows.Add((cellPath, BinaryPrimitives.ReadUInt32BigEndian(pg.AsSpan(p + local)), total - local));
            }
        }

        string typeName = interior ? "internal" : "leaf";
        output.Add(new Row(name, path, pgno, typeName, ncell, payload, unused, mx, (long)(pgno - 1) * pageSize, pageSize));

        // The rows follow the page: each cell's overflow chain, then each child, in cell order (the right-most child last).
        int ovAt = 0;
        for (int i = 0; i < ncell || (interior && i == ncell); i++)
        {
            string cellPath = path + i.ToString("x3", CultureInfo.InvariantCulture);
            while (ovAt < overflows.Count && overflows[ovAt].Path == cellPath)
            {
                uint next = overflows[ovAt].First;
                long remaining = overflows[ovAt].Remaining;
                for (int n = 0; next != 0 && remaining > 0; n++)
                {
                    if (next > src.PageCount || n > src.PageCount)
                    {
                        throw Corrupt(next, "overflow chain leaves the file");
                    }

                    byte[] ov = ReadPage(conn, db, src, next);
                    long here = Math.Min(remaining, usable - 4);
                    // Observed: an overflow row reports the byte offset of the page listed just before it.
                    long before = (long)(output[^1].PageNo - 1) * pageSize;
                    output.Add(new Row(name, cellPath + "+" + n.ToString("x6", CultureInfo.InvariantCulture), next, "overflow", 0, here, usable - 4 - here, 0, before, pageSize));
                    remaining -= here;
                    next = BinaryPrimitives.ReadUInt32BigEndian(ov);
                }

                ovAt++;
            }

            if (interior)
            {
                uint child = i < ncell ? kids[i].Pgno : BinaryPrimitives.ReadUInt32BigEndian(pg.AsSpan(hdr + 8));
                string childPath = i < ncell ? kids[i].Path : path + i.ToString("x3", CultureInfo.InvariantCulture) + "/";
                Walk(conn, db, src, name, child, childPath, output, depth + 1, visited);
            }
        }
    }
}
