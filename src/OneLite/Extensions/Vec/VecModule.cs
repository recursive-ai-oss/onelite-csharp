using System.Linq;
using System.Collections.Immutable;
using System.Text;
using RecursiveAI.OneLite.Planning;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.VirtualTables;

namespace RecursiveAI.OneLite.Extensions.Vec;

/// <summary>The <c>vec0</c> module: a table of vectors with exact k-nearest-neighbour search (see docs/vec.md).</summary>
internal sealed class VecModule : BuiltinModule
{
    internal override string Name => "vec0";

    internal override ShadowTable[] ShadowTables(string schema, string table, ImmutableArray<string> args, int pageSize)
        => VecSchema.Parse(schema, table, args).ShadowTables();

    internal override string[] ShadowNames(string table, ImmutableArray<string> args)
        => [.. VecSchema.Parse("main", table, args).ShadowTables().Select(t => t.Name)];

    internal override VTab Connect(Connection conn, TableInfo table, string schema, ImmutableArray<string> args, Action<string> declare)
    {
        VecSchema sh = VecSchema.Parse(schema, table.Name, args);
        declare(sh.DeclareSql());
        return new VecTable(conn, sh);
    }
}

/// <summary>A vec0 table of one connection.</summary>
internal sealed partial class VecTable : VTab
{
    // Columns of the shadow tables (index 0 of every record is the rowid alias and holds NULL).
    internal const int ChunkSizeCol = 1;
    internal const int RowidsIdCol = 1;
    internal const int RowidsChunkCol = 2;
    internal const int RowidsOffsetCol = 3;
    internal const int RowidsFirstMetadataCol = 4;

    private readonly Connection _conn;
    private readonly VecSchema _sh;

    /// <summary>The newest chunk with a free slot, by partition key; each use re-checks it against the table.</summary>
    private readonly Dictionary<string, long> _lastChunk = [];

    internal VecTable(Connection conn, VecSchema sh)
    {
        _conn = conn;
        _sh = sh;
    }

    internal VecSchema Shape => _sh;

    internal int ValidityCol => 2 + _sh.Partitions.Length;

    internal int ChunkRowidsCol => 3 + _sh.Partitions.Length;

    internal override bool IsWritable => true;

    internal override bool AcceptsExtendedConstraints => true;

    public override void Dispose()
    {
    }

    internal override VCursor Open() => new VecCursor(this);

    internal ShadowTree OpenChunks() => new(_conn, _sh.Schema, _sh.ChunksTable);

    internal ShadowTree OpenRowids() => new(_conn, _sh.Schema, _sh.RowidsTable);

    internal ShadowTree OpenVectors(int i) => new(_conn, _sh.Schema, _sh.VectorChunksTable(i));

    internal ShadowTree OpenAux() => new(_conn, _sh.Schema, _sh.AuxTable);

    internal ShadowTree OpenIds() => new(_conn, _sh.Schema, _sh.IdsTable);

    internal static TextEncoding Enc => Vm.Values.BlobEncoding;

    // ------------------------------------------------------------------ planning

    internal override void BestIndex(BestIndexInfo info)
    {
        // Each argument the plan takes is described by one token of IndexString, in argument order:
        //   M<n>  MATCH on the nth vector column    K  k = ?    L  the LIMIT    F<column>:<op>  a filter on a column    R<op>  rowid or key equality
        ReadOnlySpan<IndexConstraint> cs = info.Constraints;
        int match = -1;
        for (int i = 0; i < cs.Length; i++)
        {
            if (cs[i].Op == ConstraintOp.Match && cs[i].Column >= 0 && cs[i].Column < _sh.Columns.Length && _sh.Columns[cs[i].Column].Kind == VecColumnKind.Vector)
            {
                if (!cs[i].Usable)
                {
                    continue;
                }

                if (match >= 0)
                {
                    throw new OneLiteException(ResultCode.Error, "only 1 MATCH operator is allowed in a single vec0 query");
                }

                match = i;
            }
        }

        var tokens = new List<string>();
        int argc = 0;
        if (match >= 0)
        {
            int kAt = -1, limitAt = -1;
            for (int i = 0; i < cs.Length; i++)
            {
                if (cs[i].Usable && cs[i].Op == ConstraintOp.Eq && cs[i].Column == _sh.KColumn)
                {
                    kAt = i;
                }
                else if (cs[i].Usable && cs[i].Op == ConstraintOp.Limit)
                {
                    limitAt = i;
                }
            }

            if (kAt < 0 && limitAt < 0)
            {
                throw new OneLiteException(ResultCode.Error, "A LIMIT or 'k = ?' constraint is required on vec0 knn queries.");
            }

            info.Usage[match].ArgvIndex = ++argc;
            info.Usage[match].Omit = true;
            tokens.Add("M" + _sh.Columns[cs[match].Column].Slot);
            if (kAt >= 0)
            {
                info.Usage[kAt].ArgvIndex = ++argc;
                info.Usage[kAt].Omit = true;
                tokens.Add("K");
            }

            if (limitAt >= 0)
            {
                info.Usage[limitAt].ArgvIndex = ++argc;
                tokens.Add("L");
            }

            for (int i = 0; i < cs.Length; i++)
            {
                if (!cs[i].Usable || i == match || i == kAt || i == limitAt || cs[i].Column < -1 || cs[i].Column >= _sh.Columns.Length)
                {
                    continue;
                }

                VecColumn? col = cs[i].Column < 0 ? null : _sh.Columns[cs[i].Column];
                bool filterable = col is null
                    ? cs[i].Op is ConstraintOp.Eq or ConstraintOp.In
                    : col.Kind switch
                    {
                        VecColumnKind.Primary => cs[i].Op is ConstraintOp.Eq or ConstraintOp.In,
                        VecColumnKind.Metadata or VecColumnKind.Partition => cs[i].Op is ConstraintOp.Eq or ConstraintOp.Ne or ConstraintOp.Lt or ConstraintOp.Le or ConstraintOp.Gt or ConstraintOp.Ge or ConstraintOp.In,
                        _ => false,
                    };
                if (!filterable)
                {
                    continue;
                }

                info.Usage[i].ArgvIndex = ++argc;
                info.Usage[i].Omit = true;
                tokens.Add("F" + (col?.Ordinal ?? -1) + ":" + (int)cs[i].Op);
            }

            info.IndexNumber = 1;
            info.IndexString = string.Join(' ', tokens);
            info.EstimatedCost = 1000;
            info.EstimatedRows = 10;
            return;
        }

        for (int i = 0; i < cs.Length; i++)
        {
            bool key = cs[i].Column == -1 || (cs[i].Column >= 0 && cs[i].Column < _sh.Columns.Length && _sh.Columns[cs[i].Column].Kind == VecColumnKind.Primary);
            if (cs[i].Usable && cs[i].Op == ConstraintOp.Eq && key)
            {
                info.Usage[i].ArgvIndex = 1;
                info.Usage[i].Omit = true;
                info.IndexNumber = 2;
                info.IndexString = cs[i].Column == -1 || _sh.Columns[cs[i].Column].Type == VecScalarType.Integer ? "I" : "T";
                info.EstimatedCost = 10;
                info.EstimatedRows = 1;
                info.ScanIsUnique = true;
                return;
            }
        }

        info.IndexNumber = 0;
        info.EstimatedCost = 1_000_000;
        info.EstimatedRows = 100_000;
    }

    // ------------------------------------------------------------------ helpers shared by scans and updates

    /// <summary>A text value as the register holds it (UTF-8) turned into the database's encoding for storing.</summary>
    internal static Mem StoredText(in Mem utf8)
    {
        if (Enc == TextEncoding.Utf8)
        {
            return Mem.FromTextBytes(utf8.Bytes.ToArray());
        }

        return Mem.FromText(Encoding.UTF8.GetString(utf8.Bytes), Enc);
    }

    /// <summary>Text read from a record turned into UTF-8.</summary>
    internal static byte[] ToUtf8(ReadOnlySpan<byte> stored) => Enc == TextEncoding.Utf8 ? stored.ToArray() : TextCodec.ToUtf8(stored, Enc);

    /// <summary>Copies a value so it stays valid after the cursor that produced it moves.</summary>
    internal static Mem Own(in Mem m)
    {
        Mem c = m;
        if (m.Kind is StorageClass.Text or StorageClass.Blob)
        {
            c.Buffer = m.Bytes.ToArray();
            c.Offset = 0;
            c.Length = c.Buffer.Length;
        }

        return c;
    }

    /// <summary>A string that tells partition values apart.</summary>
    internal static string PartitionKey(ReadOnlySpan<Mem> parts)
    {
        var sb = new StringBuilder();
        foreach (Mem m in parts)
        {
            switch (m.Kind)
            {
                case StorageClass.Null:
                    sb.Append('n');
                    break;
                case StorageClass.Integral:
                    sb.Append('i').Append(m.IntValue);
                    break;
                default:
                    sb.Append('t').Append(m.Length).Append(':').Append(Encoding.Latin1.GetString(Enc == TextEncoding.Utf8 ? m.Bytes : TextCodec.ToUtf8(m.Bytes, Enc)));
                    break;
            }

            sb.Append('|');
        }

        return sb.ToString();
    }

    /// <summary>FNV-1a over the key's UTF-8 bytes: where the probe for a text key starts.</summary>
    internal static long HashKey(ReadOnlySpan<byte> utf8)
    {
        ulong h = 14695981039346656037UL;
        foreach (byte b in utf8)
        {
            h = (h ^ b) * 1099511628211UL;
        }

        return unchecked((long)h);
    }

    /// <summary>Evaluates <c>stored OP arg</c> for a metadata or partition column; false when either side is NULL.</summary>
    internal static bool Matches(VecColumn col, in Mem stored, ConstraintOp op, in Mem arg)
    {
        if (stored.Kind == StorageClass.Null || arg.Kind == StorageClass.Null)
        {
            return false;
        }

        int cmp;
        if (col.Type == VecScalarType.Text)
        {
            ReadOnlySpan<byte> a = Enc == TextEncoding.Utf8 ? stored.Bytes : TextCodec.ToUtf8(stored.Bytes, Enc);
            byte[]? tmp = null;
            ReadOnlySpan<byte> b;
            if (arg.Kind == StorageClass.Text || arg.Kind == StorageClass.Blob)
            {
                b = arg.Bytes;
            }
            else
            {
                tmp = new byte[Vm.Values.MaxRealText];
                int n = arg.Kind == StorageClass.Integral ? Vm.Values.FormatInt(arg.IntValue, tmp) : Vm.Values.FormatReal(arg.RealValue, tmp);
                b = tmp.AsSpan(0, n);
            }

            cmp = a.SequenceCompareTo(b);
        }
        else if (col.Type == VecScalarType.Float)
        {
            double x = stored.Kind == StorageClass.Real ? stored.RealValue : stored.IntValue;
            double y = Vm.Values.ToReal(arg);
            cmp = x.CompareTo(y);
        }
        else
        {
            StorageClass cls = Vm.Values.ToNumeric(arg, out long iv, out double dv);
            if (cls == StorageClass.Integral)
            {
                cmp = stored.IntValue.CompareTo(iv);
            }
            else
            {
                cmp = ((double)stored.IntValue).CompareTo(dv);
            }
        }

        return op switch
        {
            ConstraintOp.Eq => cmp == 0,
            ConstraintOp.Ne => cmp != 0,
            ConstraintOp.Lt => cmp < 0,
            ConstraintOp.Le => cmp <= 0,
            ConstraintOp.Gt => cmp > 0,
            ConstraintOp.Ge => cmp >= 0,
            _ => false,
        };
    }

    internal static OneLiteException Corrupt(string detail) => new(ResultCode.CorruptVtab, "database disk image is malformed", detail);
}

/// <summary>
/// Finds a text primary key in the <c>_ids</c> table. Keys live at slots found by open addressing from a hash of the key; a deleted key leaves a
/// marker (its row stays with a NULL rowid) so later probes do not stop early. Re-entered after Pending with the same arguments.
/// </summary>
internal sealed class IdsProbe
{
    private long _slot;
    private int _step;
    private long _tomb;
    private bool _hasTomb;

    /// <summary>Probes for <paramref name="key"/>; on Ok, <paramref name="found"/> says whether it exists, with its rowid and the slot it sits in, or else the slot to use.</summary>
    internal ModStatus Run(ShadowTree ids, ReadOnlySpan<byte> key, out bool found, out long vrowid, out long slot)
    {
        found = false;
        vrowid = 0;
        slot = 0;
        if (_step == 0)
        {
            _slot = VecTable.HashKey(key);
            _hasTomb = false;
            _step = 1;
        }

        for (; ; )
        {
            ModStatus s = ids.Find(_slot, out bool present);
            if (s != ModStatus.Ok)
            {
                return s;
            }

            if (!present)
            {
                slot = _hasTomb ? _tomb : _slot;
                _step = 0;
                return ModStatus.Ok;
            }

            s = ids.Load();
            if (s != ModStatus.Ok)
            {
                return s;
            }

            Mem idm = default, vm = default;
            ids.Row.Load(1, ref idm);
            ids.Row.Load(2, ref vm);
            if (vm.Kind == StorageClass.Null)
            {
                if (!_hasTomb)
                {
                    _hasTomb = true;
                    _tomb = _slot;
                }
            }
            else if (idm.Kind == StorageClass.Text && VecTable.ToUtf8(idm.Bytes).AsSpan().SequenceEqual(key))
            {
                found = true;
                vrowid = vm.IntValue;
                slot = _slot;
                _step = 0;
                return ModStatus.Ok;
            }

            unchecked
            {
                _slot++;
            }
        }
    }
}
