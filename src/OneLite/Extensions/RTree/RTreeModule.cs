using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using RecursiveAI.OneLite.Planning;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.VirtualTables;

namespace RecursiveAI.OneLite.Extensions.RTree;

/// <summary>The fixed facts about one r-tree table, from its CREATE VIRTUAL TABLE arguments.</summary>
internal sealed class RTreeShape
{
    internal required string Schema { get; init; }
    internal required string Table { get; init; }
    internal required string[] Columns { get; init; }
    internal required bool Int32 { get; init; }
    internal required int Dims { get; init; }
    internal required int Aux { get; init; }

    /// <summary>Coordinates per cell (two per dimension).</summary>
    internal int Stride => Dims * 2;

    /// <summary>Bytes per cell: an 8-byte rowid or child node number, then four bytes per coordinate.</summary>
    internal int CellSize => 8 + (4 * Stride);

    internal string NodeTable => Table + "_node";
    internal string RowidTable => Table + "_rowid";
    internal string ParentTable => Table + "_parent";
}

/// <summary>One node of the tree in memory: <c>Count</c> cells, each an id (rowid at level 0, child node number above) and <c>Stride</c> coordinates.</summary>
internal sealed class RNode
{
    internal long No;
    internal int Depth;
    internal int Count;
    internal long[] Id;
    internal double[] C;
    private readonly int _stride;

    internal RNode(long no, int stride, int capacity)
    {
        No = no;
        _stride = stride;
        Id = new long[capacity];
        C = new double[capacity * stride];
    }

    internal RNode Clone()
    {
        var n = new RNode(No, _stride, Id.Length) { Depth = Depth, Count = Count };
        Array.Copy(Id, n.Id, Count);
        Array.Copy(C, n.C, Count * _stride);
        return n;
    }

    internal void Add(long id, ReadOnlySpan<double> coords)
    {
        if (Count == Id.Length)
        {
            Array.Resize(ref Id, Count + 4);
            Array.Resize(ref C, (Count + 4) * _stride);
        }

        Id[Count] = id;
        coords[.._stride].CopyTo(C.AsSpan(Count * _stride, _stride));
        Count++;
    }

    internal void RemoveAt(int i)
    {
        Count--;
        for (int k = i; k < Count; k++)
        {
            Id[k] = Id[k + 1];
        }

        Array.Copy(C, (i + 1) * _stride, C, i * _stride, (Count - i) * _stride);
    }

    internal ReadOnlySpan<double> Cell(int i) => C.AsSpan(i * _stride, _stride);

    internal int IndexOf(long id)
    {
        for (int i = 0; i < Count; i++)
        {
            if (Id[i] == id)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The smallest box holding every cell.</summary>
    internal double[] Bounds()
    {
        var b = new double[_stride];
        for (int i = 0; i < Count; i++)
        {
            ReadOnlySpan<double> c = Cell(i);
            for (int d = 0; d < _stride; d += 2)
            {
                if (i == 0 || c[d] < b[d])
                {
                    b[d] = c[d];
                }

                if (i == 0 || c[d + 1] > b[d + 1])
                {
                    b[d + 1] = c[d + 1];
                }
            }
        }

        return b;
    }

    /// <summary>Reads a node blob; a blob that does not fit its own cell count is corruption.</summary>
    internal static RNode Decode(long no, ReadOnlySpan<byte> blob, RTreeShape sh)
    {
        if (blob.Length < 4)
        {
            throw Corrupt("node " + no + " is too short");
        }

        int depth = BinaryPrimitives.ReadUInt16BigEndian(blob);
        int count = BinaryPrimitives.ReadUInt16BigEndian(blob[2..]);
        if (4 + ((long)count * sh.CellSize) > blob.Length)
        {
            throw Corrupt("node " + no + " claims " + count + " cells");
        }

        var n = new RNode(no, sh.Stride, count) { Depth = depth, Count = count };
        int p = 4;
        for (int i = 0; i < count; i++)
        {
            n.Id[i] = BinaryPrimitives.ReadInt64BigEndian(blob[p..]);
            p += 8;
            for (int k = 0; k < sh.Stride; k++)
            {
                n.C[(i * sh.Stride) + k] = sh.Int32 ? BinaryPrimitives.ReadInt32BigEndian(blob[p..]) : BinaryPrimitives.ReadSingleBigEndian(blob[p..]);
                p += 4;
            }
        }

        return n;
    }

    /// <summary>Writes the node into a zero-filled blob of <paramref name="size"/> bytes.</summary>
    internal byte[] Encode(RTreeShape sh, int size)
    {
        var blob = new byte[size];
        BinaryPrimitives.WriteUInt16BigEndian(blob, (ushort)Depth);
        BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(2), (ushort)Count);
        int p = 4;
        for (int i = 0; i < Count; i++)
        {
            BinaryPrimitives.WriteInt64BigEndian(blob.AsSpan(p), Id[i]);
            p += 8;
            for (int k = 0; k < sh.Stride; k++)
            {
                double v = C[(i * sh.Stride) + k];
                if (sh.Int32)
                {
                    BinaryPrimitives.WriteInt32BigEndian(blob.AsSpan(p), (int)v);
                }
                else
                {
                    BinaryPrimitives.WriteSingleBigEndian(blob.AsSpan(p), (float)v);
                }

                p += 4;
            }
        }

        return blob;
    }

    internal static OneLiteException Corrupt(string detail) => new(ResultCode.CorruptVtab, "database disk image is malformed", detail);
}

internal sealed class RTreeModule : BuiltinModule
{
    private readonly bool _int32;

    internal RTreeModule(bool int32) => _int32 = int32;

    internal override string Name => _int32 ? "rtree_i32" : "rtree";

    /// <summary>Reads the module arguments: the first token of each is the column name, a leading + marks an auxiliary column.</summary>
    internal RTreeShape ParseArgs(string schema, string table, ImmutableArray<string> args)
    {
        var names = new List<string>();
        int aux = 0;
        foreach (string a in args)
        {
            string t = a.Trim();
            bool isAux = t.StartsWith('+');
            if (isAux)
            {
                t = t[1..].TrimStart();
            }

            string name = FirstToken(t);
            if (!isAux && aux > 0)
            {
                throw new OneLiteException(ResultCode.Error, "Auxiliary rtree columns must be last");
            }

            aux += isAux ? 1 : 0;
            names.Add(name);
        }

        int coordCols = names.Count - aux;
        if (names.Count > 100)
        {
            throw new OneLiteException(ResultCode.Error, "Too many columns for an rtree table");
        }

        if (coordCols < 3)
        {
            throw new OneLiteException(ResultCode.Error, "Too few columns for an rtree table");
        }

        if (coordCols > 11)
        {
            throw new OneLiteException(ResultCode.Error, "Too many columns for an rtree table");
        }

        if (coordCols % 2 == 0)
        {
            throw new OneLiteException(ResultCode.Error, "Wrong number of columns for an rtree table");
        }

        return new RTreeShape { Schema = schema, Table = table, Columns = [.. names], Int32 = _int32, Dims = (coordCols - 1) / 2, Aux = aux };
    }

    private static string FirstToken(string s)
    {
        if (s.Length == 0)
        {
            return string.Empty;
        }

        if (s[0] is '"' or '\'' or '`' or '[')
        {
            char close = s[0] == '[' ? ']' : s[0];
            int e = s.IndexOf(close, 1);
            return e < 0 ? s[1..] : s[1..e];
        }

        int i = 0;
        while (i < s.Length && !char.IsWhiteSpace(s[i]))
        {
            i++;
        }

        return s[..i];
    }

    private static string Q(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    internal override ShadowTable[] ShadowTables(string schema, string table, ImmutableArray<string> args, int pageSize)
    {
        RTreeShape sh = ParseArgs(schema, table, args);
        var rowidCols = new StringBuilder("rowid INTEGER PRIMARY KEY,nodeno");
        for (int i = 0; i < sh.Aux; i++)
        {
            rowidCols.Append(",a").Append(i);
        }

        int nodeSize = pageSize - 64;
        return
        [
            new ShadowTable { Name = sh.RowidTable, Sql = "CREATE TABLE " + Q(sh.RowidTable) + "(" + rowidCols + ")" },
            new ShadowTable
            {
                Name = sh.NodeTable,
                Sql = "CREATE TABLE " + Q(sh.NodeTable) + "(nodeno INTEGER PRIMARY KEY,data)",
                Rows = [(1, [null, new byte[nodeSize]])],
            },
            new ShadowTable { Name = sh.ParentTable, Sql = "CREATE TABLE " + Q(sh.ParentTable) + "(nodeno INTEGER PRIMARY KEY,parentnode)" },
        ];
    }

    internal override string[] ShadowNames(string table, ImmutableArray<string> args) => [table + "_rowid", table + "_node", table + "_parent"];

    internal override VTab Connect(Connection conn, TableInfo table, string schema, ImmutableArray<string> args, Action<string> declare)
    {
        RTreeShape sh = ParseArgs(schema, table.Name, args);
        var sb = new StringBuilder("CREATE TABLE x(");
        for (int i = 0; i < sh.Columns.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(Q(sh.Columns[i]));
            if (i == 0)
            {
                sb.Append(" INT");
            }
            else if (i <= sh.Stride)
            {
                sb.Append(sh.Int32 ? " INT" : " REAL");
            }
        }

        sb.Append(')');
        declare(sb.ToString());
        return new RTreeTable(conn, sh);
    }
}
