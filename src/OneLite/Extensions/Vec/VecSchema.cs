using System.Linq;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using RecursiveAI.OneLite.VirtualTables;

namespace RecursiveAI.OneLite.Extensions.Vec;

/// <summary>The distance a vector column ranks by.</summary>
internal enum VecMetric : byte
{
    L2,
    Cosine,
    L1,
    Hamming,
}

/// <summary>What a declared column of a vec0 table is.</summary>
internal enum VecColumnKind : byte
{
    /// <summary>The <c>primary key</c> column (integer or text).</summary>
    Primary,

    /// <summary>A vector column such as <c>embedding float[768]</c>.</summary>
    Vector,

    /// <summary>A plain column that KNN queries can filter on.</summary>
    Metadata,

    /// <summary>A <c>partition key</c> column: vectors with different values live in different chunks.</summary>
    Partition,

    /// <summary>A <c>+</c> column kept apart from the index; it can be read but not filtered on.</summary>
    Auxiliary,
}

/// <summary>The type of a non-vector column.</summary>
internal enum VecScalarType : byte
{
    Integer,
    Float,
    Text,
    Boolean,
    Blob,
}

/// <summary>One column of a vec0 table.</summary>
internal sealed class VecColumn
{
    internal required string Name { get; init; }

    internal required VecColumnKind Kind { get; init; }

    /// <summary>Position among the declared columns (the column number SQL sees).</summary>
    internal int Ordinal { get; set; }

    /// <summary>Position among the columns of the same kind (the NN of <c>md00</c>, <c>partition00</c>, <c>value00</c>, <c>_vector_chunks00</c>).</summary>
    internal int Slot { get; set; }

    internal VecElement Element { get; init; }

    internal int Dimensions { get; init; }

    internal VecMetric Metric { get; init; }

    internal VecScalarType Type { get; init; }

    /// <summary>Bytes one vector of this column takes.</summary>
    internal int VectorBytes => Element switch { VecElement.Float32 => Dimensions * 4, VecElement.Int8 => Dimensions, _ => Dimensions / 8 };

    internal string TypeName => Type switch
    {
        VecScalarType.Integer => "INTEGER",
        VecScalarType.Float => "FLOAT",
        VecScalarType.Text => "TEXT",
        VecScalarType.Boolean => "BOOLEAN",
        _ => "BLOB",
    };
}

/// <summary>The fixed facts about one vec0 table, from its CREATE VIRTUAL TABLE arguments (see docs/vec.md).</summary>
internal sealed class VecSchema
{
    /// <summary>The most vectors a KNN query may ask for.</summary>
    internal const int MaxK = 4096;

    /// <summary>The largest vector dimension.</summary>
    internal const int MaxDimensions = 8192;

    /// <summary>Chunk vector data is kept near this many bytes, so a change rewrites a small row.</summary>
    private const int TargetChunkBytes = 32 * 1024;

    internal required string Schema { get; init; }

    internal required string Table { get; init; }

    internal required VecColumn[] Columns { get; init; }

    internal required VecColumn[] Vectors { get; init; }

    internal required VecColumn[] Metadata { get; init; }

    internal required VecColumn[] Partitions { get; init; }

    internal required VecColumn[] Auxiliary { get; init; }

    internal VecColumn? Primary { get; init; }

    /// <summary>Vectors per chunk (a multiple of 8).</summary>
    internal required int ChunkSize { get; init; }

    internal bool TextKey => Primary is { Type: VecScalarType.Text };

    /// <summary>The column number of <c>distance</c> (hidden).</summary>
    internal int DistanceColumn => Columns.Length;

    /// <summary>The column number of <c>k</c> (hidden).</summary>
    internal int KColumn => Columns.Length + 1;

    internal string ChunksTable => Table + "_chunks";

    internal string RowidsTable => Table + "_rowids";

    internal string IdsTable => Table + "_ids";

    internal string AuxTable => Table + "_auxiliary";

    internal string VectorChunksTable(int i) => Table + "_vector_chunks" + i.ToString("00", CultureInfo.InvariantCulture);

    private static OneLiteException Ctor(string message) => new(ResultCode.Error, "vec0 constructor error: " + message);

    internal static string Q(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>Reads the module arguments.</summary>
    internal static VecSchema Parse(string schema, string table, ImmutableArray<string> args)
    {
        var cols = new List<VecColumn>();
        int chunkSize = 0;
        VecColumn? pk = null;
        foreach (string raw in args)
        {
            string a = raw.Trim();
            if (a.StartsWith("chunk_size", StringComparison.OrdinalIgnoreCase) && a.Contains('=', StringComparison.Ordinal) && !a.Contains(' ', StringComparison.Ordinal))
            {
                string val = a[(a.IndexOf('=', StringComparison.Ordinal) + 1)..].Trim();
                if (!int.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out chunkSize) || chunkSize < 8 || chunkSize % 8 != 0 || chunkSize > 4096)
                {
                    throw Ctor("chunk_size must be a multiple of 8 between 8 and 4096");
                }

                continue;
            }

            bool aux = a.StartsWith('+');
            if (aux)
            {
                a = a[1..].TrimStart();
            }

            List<string> words = Words(a);
            if (words.Count == 0)
            {
                throw Ctor("Could not parse '" + raw + "'");
            }

            string name = words[0];
            string lower = a.ToLowerInvariant();
            bool partition = lower.Contains("partition key", StringComparison.Ordinal);
            bool primary = lower.Contains("primary key", StringComparison.Ordinal);
            if (words.Count > 1 && ParseVector(name, words[1], words, raw) is { } vec)
            {
                if (aux || partition || primary)
                {
                    throw Ctor("could not parse vector column '" + raw + "'");
                }

                cols.Add(vec);
                continue;
            }

            if (words.Count < 2)
            {
                throw Ctor("could not parse table option '" + raw + "'");
            }

            VecScalarType? type = words[1].ToLowerInvariant() switch
            {
                "int" or "integer" or "int8" or "bigint" => VecScalarType.Integer,
                "float" or "double" or "real" => VecScalarType.Float,
                "text" => VecScalarType.Text,
                "bool" or "boolean" => VecScalarType.Boolean,
                "blob" => VecScalarType.Blob,
                _ => null,
            };
            if (type is null || (words[1].Equals("int8", StringComparison.OrdinalIgnoreCase) && !primary && !partition))
            {
                throw Ctor("Unknown table option: " + name);
            }

            if (aux)
            {
                cols.Add(new VecColumn { Name = name, Kind = VecColumnKind.Auxiliary, Type = type.Value });
            }
            else if (primary)
            {
                if (pk is not null)
                {
                    throw Ctor("More than one primary key definition was provided, vec0 only suports a single primary key column");
                }

                if (type is not (VecScalarType.Integer or VecScalarType.Text))
                {
                    throw Ctor("Primary key type must be INTEGER or TEXT");
                }

                pk = new VecColumn { Name = name, Kind = VecColumnKind.Primary, Type = type.Value };
                cols.Add(pk);
            }
            else if (partition)
            {
                if (type is not (VecScalarType.Integer or VecScalarType.Text))
                {
                    throw Ctor("could not parse vector column '" + raw + "'");
                }

                cols.Add(new VecColumn { Name = name, Kind = VecColumnKind.Partition, Type = type.Value });
            }
            else
            {
                if (type == VecScalarType.Blob)
                {
                    throw Ctor("Unknown table option: " + name);
                }

                cols.Add(new VecColumn { Name = name, Kind = VecColumnKind.Metadata, Type = type.Value });
            }
        }

        VecColumn[] vectors = [.. cols.Where(c => c.Kind == VecColumnKind.Vector)];
        VecColumn[] meta = [.. cols.Where(c => c.Kind == VecColumnKind.Metadata)];
        VecColumn[] parts = [.. cols.Where(c => c.Kind == VecColumnKind.Partition)];
        VecColumn[] auxs = [.. cols.Where(c => c.Kind == VecColumnKind.Auxiliary)];
        if (vectors.Length == 0)
        {
            throw Ctor("At least one vector column is required");
        }

        if (meta.Length > 16)
        {
            throw Ctor("More than 16 metadata columns were provided");
        }

        if (parts.Length > 4)
        {
            throw Ctor("More than 4 partition key columns were provided");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "distance", "k" };
        if (pk is null)
        {
            seen.Add("rowid");
        }

        foreach (VecColumn c in cols)
        {
            if (!seen.Add(c.Name))
            {
                throw Ctor("could not declare virtual table, 'duplicate column name: " + c.Name + "'");
            }
        }

        // The stated order is the order of the columns SQL sees.
        for (int i = 0; i < cols.Count; i++)
        {
            cols[i].Ordinal = i;
        }

        Number(vectors);
        Number(meta);
        Number(parts);
        Number(auxs);
        if (chunkSize == 0)
        {
            int widest = vectors.Max(v => v.VectorBytes);
            chunkSize = Math.Clamp(TargetChunkBytes / widest / 8 * 8, 8, 1024);
        }

        return new VecSchema
        {
            Schema = schema,
            Table = table,
            Columns = [.. cols],
            Vectors = vectors,
            Metadata = meta,
            Partitions = parts,
            Auxiliary = auxs,
            Primary = pk,
            ChunkSize = chunkSize,
        };
    }

    private static void Number(VecColumn[] cols)
    {
        for (int i = 0; i < cols.Length; i++)
        {
            cols[i].Slot = i;
        }
    }

    private static List<string> Words(string a)
    {
        var words = new List<string>();
        int i = 0;
        while (i < a.Length)
        {
            if (char.IsWhiteSpace(a[i]))
            {
                i++;
                continue;
            }

            int start = i;
            if (a[i] is '"' or '\'' or '`' or '[')
            {
                char close = a[i] == '[' ? ']' : a[i];
                int e = a.IndexOf(close, i + 1);
                if (e < 0)
                {
                    e = a.Length - 1;
                }

                words.Add(a[(i + 1)..e]);
                i = e + 1;
                continue;
            }

            while (i < a.Length && !char.IsWhiteSpace(a[i]))
            {
                i++;
            }

            words.Add(a[start..i]);
        }

        return words;
    }

    /// <summary>Reads <c>float[768]</c>, <c>int8[4]</c> or <c>bit[64]</c>, then the options after it; null when the word is not a vector type.</summary>
    private static VecColumn? ParseVector(string name, string word, List<string> words, string raw)
    {
        int lb = word.IndexOf('[', StringComparison.Ordinal);
        if (lb < 0)
        {
            return null;
        }

        string t = word[..lb].ToLowerInvariant();
        VecElement? el = t switch
        {
            "float" or "float32" or "f32" => VecElement.Float32,
            "int8" or "i8" => VecElement.Int8,
            "bit" => VecElement.Bit,
            _ => null,
        };
        if (el is null)
        {
            return null;
        }

        if (!word.EndsWith(']') || !int.TryParse(word.AsSpan(lb + 1, word.Length - lb - 2), NumberStyles.None, CultureInfo.InvariantCulture, out int dims) || dims <= 0)
        {
            throw Ctor("could not parse vector column '" + raw + "'");
        }

        if (dims > MaxDimensions)
        {
            throw Ctor("Dimension on vector column too large, provided " + dims.ToString(CultureInfo.InvariantCulture) + ", maximum " + MaxDimensions.ToString(CultureInfo.InvariantCulture));
        }

        if (el == VecElement.Bit && dims % 8 != 0)
        {
            throw Ctor("bit vector dimensions must be a multiple of 8");
        }

        VecMetric metric = el == VecElement.Bit ? VecMetric.Hamming : VecMetric.L2;
        for (int i = 2; i < words.Count; i++)
        {
            string w = words[i];
            if (w.StartsWith("distance_metric=", StringComparison.OrdinalIgnoreCase))
            {
                VecMetric? m = w[16..].ToLowerInvariant() switch { "l2" => VecMetric.L2, "cosine" => VecMetric.Cosine, "l1" => VecMetric.L1, _ => null };
                if (m is null || el == VecElement.Bit)
                {
                    throw Ctor("could not parse vector column '" + raw + "'");
                }

                metric = m.Value;
            }
            else
            {
                throw Ctor("could not parse vector column '" + raw + "'");
            }
        }

        return new VecColumn { Name = name, Kind = VecColumnKind.Vector, Element = el.Value, Dimensions = dims, Metric = metric };
    }

    /// <summary>The <c>CREATE TABLE</c> text that gives the columns SQL sees.</summary>
    internal string DeclareSql()
    {
        var sb = new StringBuilder("CREATE TABLE x(");
        foreach (VecColumn c in Columns)
        {
            if (c.Ordinal > 0)
            {
                sb.Append(',');
            }

            sb.Append(Q(c.Name));
            switch (c.Kind)
            {
                case VecColumnKind.Vector:
                    sb.Append(" BLOB");
                    break;
                case VecColumnKind.Primary:
                    sb.Append(c.Type == VecScalarType.Text ? " TEXT" : " INTEGER");
                    break;
                default:
                    sb.Append(c.Type switch { VecScalarType.Integer => " INTEGER", VecScalarType.Float => " FLOAT", VecScalarType.Text => " TEXT", VecScalarType.Boolean => " BOOLEAN", _ => " BLOB" });
                    break;
            }
        }

        sb.Append(",\"distance\" HIDDEN,\"k\" HIDDEN)");
        return sb.ToString();
    }

    /// <summary>The shadow tables, in creation order.</summary>
    internal ShadowTable[] ShadowTables()
    {
        var list = new List<ShadowTable>();
        var chunks = new StringBuilder("chunk_id INTEGER PRIMARY KEY,size INTEGER NOT NULL");
        for (int i = 0; i < Partitions.Length; i++)
        {
            chunks.Append(",partition").Append(i.ToString("00", CultureInfo.InvariantCulture));
        }

        chunks.Append(",validity BLOB NOT NULL,rowids BLOB NOT NULL");
        list.Add(new ShadowTable { Name = ChunksTable, Sql = "CREATE TABLE " + Q(ChunksTable) + "(" + chunks + ")" });

        var rowids = new StringBuilder("rowid INTEGER PRIMARY KEY,id,chunk_id INTEGER,chunk_offset INTEGER");
        for (int i = 0; i < Metadata.Length; i++)
        {
            rowids.Append(",md").Append(i.ToString("00", CultureInfo.InvariantCulture));
        }

        list.Add(new ShadowTable { Name = RowidsTable, Sql = "CREATE TABLE " + Q(RowidsTable) + "(" + rowids + ")" });
        for (int i = 0; i < Vectors.Length; i++)
        {
            list.Add(new ShadowTable { Name = VectorChunksTable(i), Sql = "CREATE TABLE " + Q(VectorChunksTable(i)) + "(rowid INTEGER PRIMARY KEY,vectors BLOB NOT NULL)" });
        }

        if (Auxiliary.Length > 0)
        {
            var aux = new StringBuilder("rowid INTEGER PRIMARY KEY");
            for (int i = 0; i < Auxiliary.Length; i++)
            {
                aux.Append(",value").Append(i.ToString("00", CultureInfo.InvariantCulture));
            }

            list.Add(new ShadowTable { Name = AuxTable, Sql = "CREATE TABLE " + Q(AuxTable) + "(" + aux + ")" });
        }

        if (TextKey)
        {
            list.Add(new ShadowTable { Name = IdsTable, Sql = "CREATE TABLE " + Q(IdsTable) + "(slot INTEGER PRIMARY KEY,id TEXT,vrowid INTEGER)" });
        }

        return [.. list];
    }
}
