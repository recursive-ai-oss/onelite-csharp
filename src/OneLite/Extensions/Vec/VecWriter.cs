using System.Buffers.Binary;
using System.Globalization;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Vm;
using RecursiveAI.OneLite.VirtualTables;

namespace RecursiveAI.OneLite.Extensions.Vec;

// Inserting, updating and deleting rows.
//
// A row lives in three places: the rowid table (key, chunk, slot, metadata), a chunk (a bit in `validity`, its rowid in `rowids`, its vectors in
// the vector chunk tables) and, if declared, the auxiliary table. A change is a fixed sequence of phases; each phase does its reads and then its
// writes, and a phase that returns Pending is run again from its start with the same arguments (every read and write of a shadow table is safe to
// repeat). Nothing a phase decided is kept anywhere but in the fields below.
internal sealed partial class VecTable
{
    private const int ConstraintPrimaryKey = ResultCode.Constraint | (6 << 8);

    private int _w;
    private long _wRowid;
    private long _wChunk;
    private long _wIdSlot;
    private int _wOffset;
    private int _wSize;
    private int _wLoop;
    private bool _wNewChunk;
    private bool _wTextKey;
    private bool _wHasRowid;
    private byte[] _wKeyUtf8 = [];
    private byte[] _wValidity = [];
    private byte[] _wRowids = [];
    private byte[][] _wVec = [];
    private Mem[] _wPart = [];
    private Mem[] _wMeta = [];
    private Mem[] _wAuxVals = [];
    private Mem _wId;
    private ShadowTree? _tRowids, _tChunks, _tAux, _tIds;
    private ShadowTree?[] _tVec = [];
    private IdsProbe? _probe;
    private Mem[]? _wChunkPartitions;
    private bool _wChanged;

    private ShadowTree TRowids => _tRowids ??= OpenRowids();

    private ShadowTree TChunks => _tChunks ??= OpenChunks();

    private ShadowTree TAux => _tAux ??= OpenAux();

    private ShadowTree TIds => _tIds ??= OpenIds();

    private ShadowTree TVec(int i)
    {
        if (_tVec.Length == 0)
        {
            _tVec = new ShadowTree?[_sh.Vectors.Length];
        }

        return _tVec[i] ??= OpenVectors(i);
    }

    private void ResetWrite()
    {
        _w = 0;
        _wLoop = 0;
        _wChanged = false;
        _wChunkPartitions = null;
        _tRowids?.Dispose();
        _tChunks?.Dispose();
        _tAux?.Dispose();
        _tIds?.Dispose();
        _tRowids = _tChunks = _tAux = _tIds = null;
        foreach (ShadowTree? t in _tVec)
        {
            t?.Dispose();
        }

        _tVec = [];
        _probe = null;
    }

    internal override ModStatus Update(ReadOnlySpan<Mem> values, out long rowid)
    {
        rowid = 0;
        try
        {
            ModStatus s = values.Length == 1 ? Delete(values[0]) : values[0].Kind == StorageClass.Null ? Insert(values) : Modify(values);
            if (s == ModStatus.Ok)
            {
                rowid = _wRowid;
                ResetWrite();
            }

            return s;
        }
        catch
        {
            ResetWrite();
            throw;
        }
    }

    // ------------------------------------------------------------------ decoding the new values

    private static string TypeOfMem(in Mem m) => m.Kind switch
    {
        StorageClass.Null => "NULL",
        StorageClass.Integral => "INTEGER",
        StorageClass.Real => "FLOAT",
        StorageClass.Text => "TEXT",
        _ => "BLOB",
    };

    private static OneLiteException Err(string message) => new(ResultCode.Error, message);

    /// <summary>Checks and keeps the new values of a row (columns start at <c>values[2]</c>).</summary>
    private void Decode(ReadOnlySpan<Mem> values, bool insert)
    {
        _wVec = new byte[_sh.Vectors.Length][];
        foreach (VecColumn c in _sh.Vectors)
        {
            Mem m = values[2 + c.Ordinal];
            VecValue v;
            try
            {
                // An UPDATE sees the columns it does not change as plain blobs (the staged row keeps no subtype), so an untyped blob of exactly the
                // column's size is taken as the column's own type.
                v = !insert && m.Kind == StorageClass.Blob && m.Subtype == 0 && c.Element != VecElement.Float32 && m.Length == c.VectorBytes
                    ? new VecValue(c.Element, new ReadOnlyMemory<byte>(m.Buffer, m.Offset, m.Length))
                    : VecValue.Read(m);
            }
            catch (OneLiteException e)
            {
                throw Err("Inserted vector for the \"" + c.Name + "\" column is invalid: " + e.Message);
            }

            if (v.Type != c.Element)
            {
                throw Err("Inserted vector for the \"" + c.Name + "\" column is expected to be of type " + VecValue.Name(c.Element) + ", but a " + VecValue.Name(v.Type) + " vector was provided.");
            }

            if (v.Dimensions != c.Dimensions)
            {
                throw Err("Dimension mismatch for inserted vector for the \"" + c.Name + "\" column. Expected " + c.Dimensions.ToString(CultureInfo.InvariantCulture)
                    + " dimensions but received " + v.Dimensions.ToString(CultureInfo.InvariantCulture) + ".");
            }

            _wVec[c.Slot] = v.Bytes.ToArray();
        }

        _wMeta = new Mem[_sh.Metadata.Length];
        foreach (VecColumn c in _sh.Metadata)
        {
            Mem m = values[2 + c.Ordinal];
            bool ok = c.Type switch
            {
                VecScalarType.Integer => m.Kind == StorageClass.Integral,
                VecScalarType.Float => m.Kind == StorageClass.Real,
                VecScalarType.Text => m.Kind == StorageClass.Text,
                _ => m.Kind == StorageClass.Integral && m.IntValue is 0 or 1,
            };
            if (!ok)
            {
                string want = c.Type switch { VecScalarType.Integer => "integer", VecScalarType.Float => "float", VecScalarType.Text => "text", _ => "boolean (0 or 1)" };
                throw Err("Expected " + want + " for " + c.TypeName + " metadata column " + c.Name + ", received " + TypeOfMem(m));
            }

            _wMeta[c.Slot] = m.Kind == StorageClass.Text ? StoredText(m) : Own(m);
        }

        _wPart = new Mem[_sh.Partitions.Length];
        foreach (VecColumn c in _sh.Partitions)
        {
            Mem m = values[2 + c.Ordinal];
            bool ok = m.Kind == StorageClass.Null || (c.Type == VecScalarType.Integer ? m.Kind == StorageClass.Integral : m.Kind == StorageClass.Text);
            if (!ok)
            {
                throw Err("Partition key type mismatch: The partition key column " + c.Name + " has type " + c.TypeName + ", but " + TypeOfMem(m) + " was provided.");
            }

            _wPart[c.Slot] = m.Kind == StorageClass.Text ? StoredText(m) : Own(m);
        }

        _wAuxVals = new Mem[_sh.Auxiliary.Length];
        foreach (VecColumn c in _sh.Auxiliary)
        {
            Mem m = values[2 + c.Ordinal];
            bool ok = m.Kind == StorageClass.Null || c.Type switch
            {
                VecScalarType.Integer => m.Kind == StorageClass.Integral,
                VecScalarType.Float => m.Kind == StorageClass.Real,
                VecScalarType.Text => m.Kind == StorageClass.Text,
                VecScalarType.Blob => m.Kind == StorageClass.Blob,
                _ => m.Kind == StorageClass.Integral && m.IntValue is 0 or 1,
            };
            if (!ok)
            {
                throw Err("Auxiliary column type mismatch: The auxiliary column " + c.Name + " has type " + c.TypeName + ", but " + TypeOfMem(m) + " was provided.");
            }

            _wAuxVals[c.Slot] = m.Kind == StorageClass.Text ? StoredText(m) : Own(m);
        }

        if (insert)
        {
            _wTextKey = _sh.TextKey;
            _wHasRowid = false;
            if (_sh.Primary is { } pk)
            {
                Mem k = values[2 + pk.Ordinal];
                if (pk.Type == VecScalarType.Text)
                {
                    if (k.Kind != StorageClass.Text)
                    {
                        throw Err("The " + _sh.Table + " virtual table was declared with a TEXT primary key, but a non-TEXT value was provided in an INSERT.");
                    }

                    _wKeyUtf8 = k.Bytes.ToArray();
                    _wId = StoredText(k);
                }
                else if (k.Kind == StorageClass.Integral)
                {
                    _wRowid = k.IntValue;
                    _wHasRowid = true;
                }
                else if (k.Kind != StorageClass.Null)
                {
                    throw Err("Only integers are allowed for primary key values on " + _sh.Table);
                }
                else if (values[1].Kind == StorageClass.Integral)
                {
                    _wRowid = values[1].IntValue;
                    _wHasRowid = true;
                }
            }
            else if (values[1].Kind == StorageClass.Integral)
            {
                _wRowid = values[1].IntValue;
                _wHasRowid = true;
            }
            else if (values[1].Kind != StorageClass.Null)
            {
                throw Err("Only integers are allowed for primary key values on " + _sh.Table);
            }
        }
    }

    private Mem[] ChunkRecord(Mem[] partitions)
    {
        var rec = new Mem[4 + _sh.Partitions.Length];
        rec[ChunkSizeCol] = Mem.FromInt(_wSize);
        for (int i = 0; i < partitions.Length; i++)
        {
            rec[2 + i] = partitions[i];
        }

        rec[ValidityCol] = Mem.FromBlob(_wValidity);
        rec[ChunkRowidsCol] = Mem.FromBlob(_wRowids);
        return rec;
    }

    private Mem[] RowidRecord(long chunk, int offset)
    {
        var rec = new Mem[RowidsFirstMetadataCol + _sh.Metadata.Length];
        rec[RowidsIdCol] = _sh.TextKey ? _wId : Mem.Null;
        rec[RowidsChunkCol] = Mem.FromInt(chunk);
        rec[RowidsOffsetCol] = Mem.FromInt(offset);
        for (int i = 0; i < _wMeta.Length; i++)
        {
            rec[RowidsFirstMetadataCol + i] = _wMeta[i];
        }

        return rec;
    }

    private Mem[] AuxRecord()
    {
        var rec = new Mem[1 + _sh.Auxiliary.Length];
        for (int i = 0; i < _wAuxVals.Length; i++)
        {
            rec[1 + i] = _wAuxVals[i];
        }

        return rec;
    }

    // ------------------------------------------------------------------ INSERT

    private ModStatus Insert(ReadOnlySpan<Mem> v)
    {
        ModStatus s;
        if (_w == 0)
        {
            Decode(v, true);
            _w = 1;
        }

        if (_w == 1)
        {
            // Phase 1: the key. A text key is looked up in the key table; an integer key in the rowid table; no key takes the next rowid.
            if (_wTextKey)
            {
                _probe ??= new IdsProbe();
                s = _probe.Run(TIds, _wKeyUtf8, out bool hit, out _, out long slot);
                if (s != ModStatus.Ok)
                {
                    return s;
                }

                if (hit)
                {
                    throw new OneLiteException(ConstraintPrimaryKey, "UNIQUE constraint failed on " + _sh.Table + " primary key");
                }

                _wIdSlot = slot;
            }

            if (_wHasRowid)
            {
                s = TRowids.Find(_wRowid, out bool exists);
                if (s != ModStatus.Ok)
                {
                    return s;
                }

                if (exists)
                {
                    throw new OneLiteException(ConstraintPrimaryKey, "UNIQUE constraint failed on " + _sh.Table + " primary key");
                }
            }
            else
            {
                s = TRowids.Last();
                if (s != ModStatus.Ok)
                {
                    return s;
                }

                if (TRowids.Valid && TRowids.Rowid == long.MaxValue)
                {
                    throw new OneLiteException(ResultCode.Full, "database or disk is full");
                }

                _wRowid = TRowids.Valid ? TRowids.Rowid + 1 : 1;
            }

            _w = 2;
        }

        if (_w == 2)
        {
            s = ChooseChunk();
            if (s != ModStatus.Ok)
            {
                return s;
            }

            _w = 3;
            _wLoop = 0;
        }

        if (_w == 3)
        {
            // Phase 3: put the vectors into the vector chunks.
            while (_wLoop < _sh.Vectors.Length)
            {
                s = PutVector(_sh.Vectors[_wLoop], _wVec[_wLoop], false);
                if (s != ModStatus.Ok)
                {
                    return s;
                }

                _wLoop++;
            }

            _w = 4;
        }

        if (_w == 4)
        {
            _wValidity[_wOffset >> 3] |= (byte)(1 << (_wOffset & 7));
            BinaryPrimitives.WriteInt64LittleEndian(_wRowids.AsSpan(_wOffset * 8), _wRowid);
            s = TChunks.Put(_wChunk, ChunkRecord(_wChunkPartitions ?? _wPart));
            if (s != ModStatus.Ok)
            {
                return s;
            }

            if (_sh.Partitions.Length > 0)
            {
                _lastChunk[PartitionKey(_wPart)] = _wChunk;
            }

            _w = 5;
        }

        if (_w == 5)
        {
            s = TRowids.Put(_wRowid, RowidRecord(_wChunk, _wOffset));
            if (s != ModStatus.Ok)
            {
                return s;
            }

            _w = 6;
        }

        if (_w == 6)
        {
            if (_wTextKey)
            {
                s = TIds.Put(_wIdSlot, [Mem.Null, _wId, Mem.FromInt(_wRowid)]);
                if (s != ModStatus.Ok)
                {
                    return s;
                }
            }

            _w = 7;
        }

        if (_w == 7 && _sh.Auxiliary.Length > 0)
        {
            s = TAux.Put(_wRowid, AuxRecord());
            if (s != ModStatus.Ok)
            {
                return s;
            }
        }

        return ModStatus.Ok;
    }

    /// <summary>
    /// Finds the chunk a new row goes into and its slot: the newest chunk of the row's partition if it has a free slot, else a new chunk.
    /// Leaves <c>_wChunk</c>, <c>_wOffset</c>, <c>_wValidity</c>, <c>_wRowids</c> and <c>_wNewChunk</c> set.
    /// </summary>
    private ModStatus ChooseChunk()
    {
        ModStatus s;
        _wSize = _sh.ChunkSize;
        // Step A: the candidate chunk.
        if (_wLoop == 0)
        {
            long candidate = 0;
            if (_sh.Partitions.Length == 0)
            {
                s = TChunks.Last();
                if (s != ModStatus.Ok)
                {
                    return s;
                }

                candidate = TChunks.Valid ? TChunks.Rowid : 0;
                _wMaxChunk = candidate;
            }
            else
            {
                string key = PartitionKey(_wPart);
                if (_lastChunk.TryGetValue(key, out long cached))
                {
                    candidate = cached;
                }
                else
                {
                    s = RebuildLastChunks();
                    if (s != ModStatus.Ok)
                    {
                        return s;
                    }

                    _lastChunk.TryGetValue(key, out candidate);
                }
            }

            _wChunk = candidate;
            _wLoop = 1;
        }

        // Step B: read it and look for a free slot.
        if (_wLoop == 1)
        {
            if (_wChunk != 0)
            {
                s = TChunks.Find(_wChunk, out bool found);
                if (s != ModStatus.Ok)
                {
                    return s;
                }

                if (found)
                {
                    s = TChunks.Load();
                    if (s != ModStatus.Ok)
                    {
                        return s;
                    }

                    bool usable = ReadChunkForInsert();
                    _tChunks?.Dispose();
                    _tChunks = null;
                    if (usable)
                    {
                        _wNewChunk = false;
                        _wLoop = 0;
                        return ModStatus.Ok;
                    }
                }
                else if (_sh.Partitions.Length > 0)
                {
                    _lastChunk.Clear();
                }
            }

            _wLoop = 2;
        }

        // Step C: a new chunk, numbered after the newest one.
        if (_wLoop == 2)
        {
            if (_sh.Partitions.Length > 0)
            {
                s = TChunks.Last();
                if (s != ModStatus.Ok)
                {
                    return s;
                }

                _wMaxChunk = TChunks.Valid ? TChunks.Rowid : 0;
            }

            _wChunk = _wMaxChunk + 1;
            _wSize = _sh.ChunkSize;
            _wValidity = new byte[(_wSize + 7) / 8];
            _wRowids = new byte[_wSize * 8];
            _wOffset = 0;
            _wNewChunk = true;
            _wChunkPartitions = null;
            _wLoop = 0;
        }

        return ModStatus.Ok;
    }

    private long _wMaxChunk;

    /// <summary>Takes the loaded chunk row as the target if it belongs to the row's partition and has a free slot.</summary>
    private bool ReadChunkForInsert()
    {
        RecordView row = TChunks.Row;
        Mem sz = default, val = default, rid = default;
        row.Load(ChunkSizeCol, ref sz);
        row.Load(ValidityCol, ref val);
        row.Load(ChunkRowidsCol, ref rid);
        int size = (int)sz.IntValue;
        if (size <= 0 || size > 1 << 20 || val.Length < (size + 7) / 8 || rid.Length < size * 8)
        {
            throw Corrupt("chunk " + _wChunk.ToString(CultureInfo.InvariantCulture) + " of " + _sh.Table + " is malformed");
        }

        var parts = new Mem[_sh.Partitions.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            Mem p = default;
            row.Load(2 + i, ref p);
            parts[i] = Own(p);
        }

        if (PartitionKey(parts) != PartitionKey(_wPart))
        {
            return false;
        }

        ReadOnlySpan<byte> bits = val.Bytes;
        int free = -1;
        for (int i = 0; i < size; i++)
        {
            if ((bits[i >> 3] & (1 << (i & 7))) == 0)
            {
                free = i;
                break;
            }
        }

        if (free < 0)
        {
            return false;
        }

        _wSize = size;
        _wValidity = val.Bytes.ToArray();
        _wRowids = rid.Bytes.ToArray();
        _wOffset = free;
        _wChunkPartitions = parts;
        return true;
    }

    /// <summary>One pass over the chunk rows noting, for every partition, its newest chunk that has a free slot.</summary>
    private ModStatus RebuildLastChunks()
    {
        ModStatus s;
        if (_wScan == 0)
        {
            _lastChunk.Clear();
            s = TChunks.First();
            if (s != ModStatus.Ok)
            {
                return s;
            }

            _wScan = 1;
        }

        while (TChunks.Valid)
        {
            s = TChunks.Load();
            if (s != ModStatus.Ok)
            {
                return s;
            }

            RecordView row = TChunks.Row;
            Mem sz = default, val = default;
            row.Load(ChunkSizeCol, ref sz);
            row.Load(ValidityCol, ref val);
            int size = (int)sz.IntValue;
            ReadOnlySpan<byte> bits = val.Bytes;
            bool anyFree = false;
            for (int i = 0; i < size && (i >> 3) < bits.Length; i++)
            {
                if ((bits[i >> 3] & (1 << (i & 7))) == 0)
                {
                    anyFree = true;
                    break;
                }
            }

            if (anyFree)
            {
                var parts = new Mem[_sh.Partitions.Length];
                for (int i = 0; i < parts.Length; i++)
                {
                    Mem p = default;
                    row.Load(2 + i, ref p);
                    parts[i] = p;
                }

                _lastChunk[PartitionKey(parts)] = TChunks.Rowid;
            }

            s = TChunks.Advance();
            if (s != ModStatus.Ok)
            {
                return s;
            }
        }

        _wScan = 0;
        return ModStatus.Ok;
    }

    private int _wScan;

    /// <summary>Writes vector <paramref name="bytes"/> into the slot <c>_wOffset</c> of the vector chunk <c>_wChunk</c>; with <paramref name="onlyIfChanged"/> a chunk that already holds them is left alone.</summary>
    private ModStatus PutVector(VecColumn col, byte[] bytes, bool onlyIfChanged)
    {
        int size = col.VectorBytes;
        ShadowTree tree = TVec(col.Slot);
        byte[] buf;
        if (_putBuf != null)
        {
            // A Put that returned Pending is repeated alone: moving the cursor first would abandon the half-done replace.
            ModStatus ps = tree.Put(_wChunk, [Mem.Null, Mem.FromBlob(_putBuf)]);
            if (ps != ModStatus.Pending)
            {
                _putBuf = null;
            }

            return ps;
        }

        if (_wNewChunk)
        {
            buf = new byte[_wSize * size];
        }
        else
        {
            ModStatus s = tree.Find(_wChunk, out bool found);
            if (s != ModStatus.Ok)
            {
                return s;
            }

            if (!found)
            {
                throw Corrupt("vector chunk " + _wChunk.ToString(CultureInfo.InvariantCulture) + " of " + _sh.Table + " is missing");
            }

            s = tree.Load();
            if (s != ModStatus.Ok)
            {
                return s;
            }

            Mem blob = default;
            tree.Row.Load(1, ref blob);
            if (blob.Length < (long)(_wOffset + 1) * size)
            {
                throw Corrupt("vector chunk " + _wChunk.ToString(CultureInfo.InvariantCulture) + " of " + _sh.Table + " is too short");
            }

            if (onlyIfChanged && blob.Bytes.Slice(_wOffset * size, size).SequenceEqual(bytes))
            {
                return ModStatus.Ok;
            }

            buf = blob.Bytes.ToArray();
        }

        bytes.CopyTo(buf, _wOffset * size);
        _putBuf = buf;
        ModStatus r = tree.Put(_wChunk, [Mem.Null, Mem.FromBlob(buf)]);
        if (r != ModStatus.Pending)
        {
            _putBuf = null;
        }

        return r;
    }

    private byte[]? _putBuf;

    // ------------------------------------------------------------------ DELETE

    private ModStatus Delete(in Mem key)
    {
        ModStatus s;
        long rowid = key.IntValue;
        if (_w == 0)
        {
            _wRowid = rowid;
            _w = 1;
        }

        if (_w == 1)
        {
            // Phase 1: where the row lives.
            s = TRowids.Find(rowid, out bool found);
            if (s != ModStatus.Ok)
            {
                return s;
            }

            if (!found)
            {
                return ModStatus.Ok;
            }

            s = TRowids.Load();
            if (s != ModStatus.Ok)
            {
                return s;
            }

            Mem c = default, o = default, id = default;
            TRowids.Row.Load(RowidsChunkCol, ref c);
            TRowids.Row.Load(RowidsOffsetCol, ref o);
            TRowids.Row.Load(RowidsIdCol, ref id);
            _wChunk = c.IntValue;
            _wOffset = (int)o.IntValue;
            _wId = id.Kind == StorageClass.Text ? Own(id) : Mem.Null;
            _w = 2;
        }

        if (_w == 2)
        {
            // Phase 2: clear the slot; a chunk with no live slot goes away.
            s = TChunks.Find(_wChunk, out bool found);
            if (s != ModStatus.Ok)
            {
                return s;
            }

            if (!found)
            {
                throw Corrupt("chunk " + _wChunk.ToString(CultureInfo.InvariantCulture) + " of " + _sh.Table + " is missing");
            }

            s = TChunks.Load();
            if (s != ModStatus.Ok)
            {
                return s;
            }

            RecordView row = TChunks.Row;
            Mem sz = default, val = default, rid = default;
            row.Load(ChunkSizeCol, ref sz);
            row.Load(ValidityCol, ref val);
            row.Load(ChunkRowidsCol, ref rid);
            _wSize = (int)sz.IntValue;
            if (_wSize <= 0 || val.Length < (_wSize + 7) / 8 || rid.Length < _wSize * 8 || _wOffset < 0 || _wOffset >= _wSize)
            {
                throw Corrupt("chunk " + _wChunk.ToString(CultureInfo.InvariantCulture) + " of " + _sh.Table + " is malformed");
            }

            _wValidity = val.Bytes.ToArray();
            _wRowids = rid.Bytes.ToArray();
            _wValidity[_wOffset >> 3] &= (byte)~(1 << (_wOffset & 7));
            BinaryPrimitives.WriteInt64LittleEndian(_wRowids.AsSpan(_wOffset * 8), 0);
            var parts = new Mem[_sh.Partitions.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                Mem p = default;
                row.Load(2 + i, ref p);
                parts[i] = Own(p);
            }

            _wChunkPartitions = parts;
            _wChanged = Array.Exists(_wValidity, b => b != 0);
            _w = 3;
        }

        if (_w == 3)
        {
            if (_wChanged)
            {
                s = TChunks.Put(_wChunk, ChunkRecord(_wChunkPartitions!));
                if (s != ModStatus.Ok)
                {
                    return s;
                }

                _w = 5;
            }
            else
            {
                s = TChunks.Find(_wChunk, out bool found);
                if (s != ModStatus.Ok)
                {
                    return s;
                }

                if (found)
                {
                    s = TChunks.Delete();
                    if (s != ModStatus.Ok)
                    {
                        return s;
                    }
                }

                _lastChunk.Clear();
                _w = 4;
                _wLoop = 0;
            }
        }

        if (_w == 4)
        {
            while (_wLoop < _sh.Vectors.Length)
            {
                ShadowTree t = TVec(_wLoop);
                s = t.Find(_wChunk, out bool found);
                if (s != ModStatus.Ok)
                {
                    return s;
                }

                if (found)
                {
                    s = t.Delete();
                    if (s != ModStatus.Ok)
                    {
                        return s;
                    }
                }

                _wLoop++;
            }

            _w = 5;
        }

        if (_w == 5)
        {
            s = TRowids.Find(rowid, out bool found);
            if (s != ModStatus.Ok)
            {
                return s;
            }

            if (found)
            {
                s = TRowids.Delete();
                if (s != ModStatus.Ok)
                {
                    return s;
                }
            }

            _w = 6;
        }

        if (_w == 6)
        {
            if (_sh.Auxiliary.Length > 0)
            {
                s = TAux.Find(rowid, out bool found);
                if (s != ModStatus.Ok)
                {
                    return s;
                }

                if (found)
                {
                    s = TAux.Delete();
                    if (s != ModStatus.Ok)
                    {
                        return s;
                    }
                }
            }

            _w = 7;
        }

        if (_w == 7 && _sh.TextKey && _wId.Kind == StorageClass.Text)
        {
            // The key's row stays as a marker so probes for other keys that passed through it still find them.
            _probe ??= new IdsProbe();
            s = _probe.Run(TIds, ToUtf8(_wId.Bytes), out bool hit, out _, out long slot);
            if (s != ModStatus.Ok)
            {
                return s;
            }

            if (hit)
            {
                s = TIds.Put(slot, [Mem.Null, _wId, Mem.Null]);
                if (s != ModStatus.Ok)
                {
                    return s;
                }
            }
        }

        return ModStatus.Ok;
    }

    // ------------------------------------------------------------------ UPDATE

    private ModStatus Modify(ReadOnlySpan<Mem> v)
    {
        ModStatus s;
        if (_w == 0)
        {
            if (v[1].Kind != StorageClass.Null && Vm.Values.ToInt(v[1]) != Vm.Values.ToInt(v[0]))
            {
                throw Err("UPDATEs on vec0 primary key values are not allowed.");
            }

            Decode(v, false);
            _wRowid = Vm.Values.ToInt(v[0]);
            _w = 1;
        }

        if (_w == 1)
        {
            s = TRowids.Find(_wRowid, out bool found);
            if (s != ModStatus.Ok)
            {
                return s;
            }

            if (!found)
            {
                return ModStatus.Ok;
            }

            s = TRowids.Load();
            if (s != ModStatus.Ok)
            {
                return s;
            }

            Mem c = default, o = default, id = default;
            TRowids.Row.Load(RowidsChunkCol, ref c);
            TRowids.Row.Load(RowidsOffsetCol, ref o);
            TRowids.Row.Load(RowidsIdCol, ref id);
            _wChunk = c.IntValue;
            _wOffset = (int)o.IntValue;
            _wId = id.Kind == StorageClass.Text ? Own(id) : Mem.Null;
            if (_sh.Primary is { Type: VecScalarType.Text } pk)
            {
                Mem nk = v[2 + pk.Ordinal];
                if (nk.Kind != StorageClass.Text || !ToUtf8(_wId.Bytes).AsSpan().SequenceEqual(nk.Bytes))
                {
                    throw Err("UPDATEs on vec0 primary key values are not allowed.");
                }
            }
            else if (_sh.Primary is { } ipk && v[2 + ipk.Ordinal].Kind != StorageClass.Null && Vm.Values.ToInt(v[2 + ipk.Ordinal]) != _wRowid)
            {
                throw Err("UPDATEs on vec0 primary key values are not allowed.");
            }

            _w = 2;
        }

        if (_w == 2)
        {
            if (_sh.Partitions.Length > 0)
            {
                s = TChunks.Find(_wChunk, out bool found);
                if (s != ModStatus.Ok)
                {
                    return s;
                }

                if (!found)
                {
                    throw Corrupt("chunk " + _wChunk.ToString(CultureInfo.InvariantCulture) + " of " + _sh.Table + " is missing");
                }

                s = TChunks.Load();
                if (s != ModStatus.Ok)
                {
                    return s;
                }

                var parts = new Mem[_sh.Partitions.Length];
                for (int i = 0; i < parts.Length; i++)
                {
                    Mem p = default;
                    TChunks.Row.Load(2 + i, ref p);
                    parts[i] = p;
                }

                if (PartitionKey(parts) != PartitionKey(_wPart))
                {
                    throw Err("UPDATE of a partition key column is not supported on " + _sh.Table + "; delete the row and insert it again.");
                }
            }

            _w = 3;
            _wLoop = 0;
            _wNewChunk = false;
            _wSize = _sh.ChunkSize;
        }

        if (_w == 3)
        {
            while (_wLoop < _sh.Vectors.Length)
            {
                s = PutVector(_sh.Vectors[_wLoop], _wVec[_wLoop], true);
                if (s != ModStatus.Ok)
                {
                    return s;
                }

                _wLoop++;
            }

            _w = 4;
        }

        if (_w == 4)
        {
            s = TRowids.Put(_wRowid, RowidRecord(_wChunk, _wOffset));
            if (s != ModStatus.Ok)
            {
                return s;
            }

            _w = 5;
        }

        if (_w == 5 && _sh.Auxiliary.Length > 0)
        {
            s = TAux.Put(_wRowid, AuxRecord());
            if (s != ModStatus.Ok)
            {
                return s;
            }
        }

        return ModStatus.Ok;
    }
}
