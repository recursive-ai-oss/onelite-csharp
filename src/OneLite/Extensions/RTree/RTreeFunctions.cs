using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using RecursiveAI.OneLite.Planning;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Vm;
using RecursiveAI.OneLite.VirtualTables;

namespace RecursiveAI.OneLite.Extensions.RTree;

/// <summary>The SQL functions of the r-tree module: <c>rtreecheck([schema,] table)</c>, <c>rtreedepth(node)</c> and <c>rtreenode(ndim, node)</c>.</summary>
internal static class RTreeFunctions
{
    internal static void Call(FuncId id, ReadOnlySpan<Mem> args, ref Mem dst, ref byte[]? own, Connection? conn)
    {
        switch (id)
        {
            case FuncId.RtreeDepth:
            {
                // Observed: only a blob is a node (a two-byte text is refused like any other argument).
                ReadOnlySpan<byte> b = args[0].Kind is StorageClass.Blob ? args[0].Bytes : default;
                if (b.Length < 2)
                {
                    throw new OneLiteException(ResultCode.Error, "Invalid argument to rtreedepth()");
                }

                Values.SetInt(ref dst, BinaryPrimitives.ReadUInt16BigEndian(b));
                return;
            }

            case FuncId.RtreeNode:
            {
                int dims = (int)Values.ToInt(args[0]);
                ReadOnlySpan<byte> b = args[1].Kind is StorageClass.Blob ? args[1].Bytes : default;
                if (args[0].Kind == StorageClass.Null || dims < 1 || dims > 5 || b.Length < 4)
                {
                    // Observed: an argument that is no node gives NULL, not an error.
                    Values.SetNull(ref dst);
                    return;
                }

                int count = BinaryPrimitives.ReadUInt16BigEndian(b[2..]);
                int cell = 8 + (8 * dims);
                var sb = new StringBuilder();
                for (int i = 0; i < count && 4 + ((i + 1) * cell) <= b.Length; i++)
                {
                    int p = 4 + (i * cell);
                    if (i > 0)
                    {
                        sb.Append(' ');
                    }

                    sb.Append('{').Append(BinaryPrimitives.ReadInt64BigEndian(b[p..]).ToString(CultureInfo.InvariantCulture));
                    for (int k = 0; k < 2 * dims; k++)
                    {
                        sb.Append(' ').Append(FormatG(BinaryPrimitives.ReadSingleBigEndian(b[(p + 8 + (4 * k))..])));
                    }

                    sb.Append('}');
                }

                if (sb.Length == 0)
                {
                    Values.SetNull(ref dst);
                    return;
                }

                Values.SetBytes(ref dst, ref own, StorageClass.Text, Encoding.UTF8.GetBytes(sb.ToString()));
                return;
            }

            default:
            {
                // Observed: a call with another number of arguments fails when it runs, and an argument that is no text names no table.
                if (args.Length is < 1 or > 2)
                {
                    throw new OneLiteException(ResultCode.Error, "wrong number of arguments to function rtreecheck()");
                }

                foreach (Mem a in args)
                {
                    if (a.Kind != StorageClass.Text)
                    {
                        throw new OneLiteException(ResultCode.Error, "SQL logic error");
                    }
                }

                string schema = args.Length == 2 ? TextCodec.Decode(args[0].Bytes, Values.BlobEncoding) : "main";
                string table = TextCodec.Decode(args[^1].Bytes, Values.BlobEncoding);
                string result = Check(conn ?? throw new OneLiteException(ResultCode.Misuse, "no connection"), schema, table);
                Values.SetBytes(ref dst, ref own, StorageClass.Text, Encoding.UTF8.GetBytes(result));
                return;
            }
        }
    }

    /// <summary>C's <c>%g</c> with its default six significant digits.</summary>
    private static string FormatG(double v)
    {
        if (v == 0)
        {
            return "0";
        }

        string s = v.ToString("E5", CultureInfo.InvariantCulture);
        int e = int.Parse(s[(s.IndexOf('E', StringComparison.Ordinal) + 1)..], CultureInfo.InvariantCulture);
        if (e < -4 || e >= 6)
        {
            string mant = s[..s.IndexOf('E', StringComparison.Ordinal)].TrimEnd('0').TrimEnd('.');
            return mant + "e" + (e < 0 ? "-" : "+") + Math.Abs(e).ToString("00", CultureInfo.InvariantCulture);
        }

        string f = v.ToString("F" + Math.Max(0, 5 - e).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return f.Contains('.', StringComparison.Ordinal) ? f.TrimEnd('0').TrimEnd('.') : f;
    }

    private static void Sync(Connection conn, string schema, Func<ModStatus> step)
    {
        Catalog cat = conn.Catalog!;
        int db = Math.Max(0, cat.SlotOf(schema));
        while (step() == ModStatus.Pending)
        {
            conn.PagerOf(db).ExecuteSync();
        }
    }

    /// <summary>
    /// The integrity check of rtree.html §7.2: boxes ordered and inside their parent's box, leaf cells and rowid rows agree, child cells and
    /// parent rows agree, and the counts match. Returns <c>ok</c> or one line per problem.
    /// </summary>
    internal static string Check(Connection conn, string schema, string table)
    {
        Catalog cat = conn.Catalog ?? throw new OneLiteException(ResultCode.Error, "no schema");
        TableInfo? t = cat.FindIn(schema, table);
        if (t is null)
        {
            // Observed: no such table (or schema) is an error with no message of its own.
            throw new OneLiteException(ResultCode.Error, "SQL logic error");
        }

        if (t.Virtual is null || VirtualModules.FindBuiltin(t.Virtual.Module) is not RTreeModule m || (m.Name != "rtree" && m.Name != "rtree_i32"))
        {
            return "Schema corrupt or not an rtree";
        }

        t.Virtual.Schema = cat.SchemaName(t.Db);
        var tab = (RTreeTable)conn.ConnectVirtual(t);
        RTreeShape sh = tab.Shape;
        var problems = new List<string>();
        using ShadowTree nodes = tab.OpenNodes();
        using ShadowTree rowids = tab.OpenRowids();
        using ShadowTree parents = tab.OpenParents();
        RNode? root = null;
        Sync(conn, schema, () => tab.ReadNode(nodes, 1, out root));
        int depth = root!.Depth;
        long leafCells = 0, innerCells = 0;
        var stack = new Stack<(long No, int Level, long Parent, double[]? Box)>();
        stack.Push((1, depth, 0, null));
        int visited = 0;
        while (stack.Count > 0 && visited++ < 10_000_000)
        {
            (long no, int level, long parent, double[]? box) = stack.Pop();
            RNode? n = null;
            Sync(conn, schema, () => tab.ReadNode(nodes, no, out n));
            for (int i = 0; i < n!.Count; i++)
            {
                ReadOnlySpan<double> c = n.Cell(i);
                for (int d = 0; d < sh.Stride; d += 2)
                {
                    if (c[d] > c[d + 1])
                    {
                        problems.Add($"Dimension {d / 2} of cell {i} on node {no} is corrupt");
                    }

                    if (box is not null && (c[d] < box[d] || c[d + 1] > box[d + 1]))
                    {
                        problems.Add($"Dimension {d / 2} of cell {i} on node {no} is corrupt relative to parent");
                    }
                }

                if (level == 0)
                {
                    leafCells++;
                    bool found = false;
                    Sync(conn, schema, () => rowids.Find(n.Id[i], out found));
                    long at = -1;
                    if (found)
                    {
                        Sync(conn, schema, () => rowids.Load());
                        Mem mm = default;
                        rowids.Row.Load(1, ref mm);
                        at = Values.ToInt(mm);
                    }

                    if (at != no)
                    {
                        problems.Add($"Mapping (b) for rowid {n.Id[i]} on node {no}: expected {no}, found {(found ? at.ToString(CultureInfo.InvariantCulture) : "none")}");
                    }
                }
                else
                {
                    innerCells++;
                    bool found = false;
                    Sync(conn, schema, () => parents.Find(n.Id[i], out found));
                    long at = -1;
                    if (found)
                    {
                        Sync(conn, schema, () => parents.Load());
                        Mem mm = default;
                        parents.Row.Load(1, ref mm);
                        at = Values.ToInt(mm);
                    }

                    if (at != no)
                    {
                        problems.Add($"Mapping (a) for node {n.Id[i]} on node {no}: expected {no}, found {(found ? at.ToString(CultureInfo.InvariantCulture) : "none")}");
                    }

                    if (stack.Count < 1_000_000 && level > 0)
                    {
                        stack.Push((n.Id[i], level - 1, no, c.ToArray()));
                    }
                }
            }

            _ = parent;
        }

        long rowidRows = CountRows(conn, schema, rowids);
        long parentRows = CountRows(conn, schema, parents);
        if (rowidRows != leafCells)
        {
            problems.Add($"Wrong number of entries in %_rowid table - expected {leafCells}, actual {rowidRows}");
        }

        if (parentRows != innerCells)
        {
            problems.Add($"Wrong number of entries in %_parent table - expected {innerCells}, actual {parentRows}");
        }

        return problems.Count == 0 ? "ok" : string.Join("\n", problems);
    }

    private static long CountRows(Connection conn, string schema, ShadowTree t)
    {
        long n = 0;
        ModStatus s;
        Sync(conn, schema, () => { s = t.First(); return s; });
        while (t.Valid)
        {
            n++;
            Sync(conn, schema, () => t.Advance());
        }

        return n;
    }
}
