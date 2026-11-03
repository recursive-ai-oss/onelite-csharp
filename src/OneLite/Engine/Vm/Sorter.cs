using System.Runtime.CompilerServices;
using System.Buffers;
using System.Buffers.Binary;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Vm;

/// <summary>Whether a sorter step finished or needs an I/O to run first (then call it again with the same arguments).</summary>
internal enum SortStatus : byte
{
    /// <summary>The step finished.</summary>
    Ok,

    /// <summary>A request is pending on the connection's slot; the step has not been consumed.</summary>
    Pending,
}

/// <summary>
/// The sorter behind ORDER BY, GROUP BY and compound selects (lang_select "ORDER BY clause"). Rows are records whose first
/// <c>nKeys</c> columns are the sort keys. Rows are kept in memory until the memory budget is used up; then the batch is sorted and
/// written to a temporary file as a run (a temporary file opened through the VFS, never synced), and the runs are merged when the
/// sort is read back. Ties keep insertion order, in memory and across runs, so the result does not depend on whether a run was spilled.
/// <para>
/// Every temporary-file access is a request on the connection's one I/O slot, issued through <see cref="Pager.ExternalIo"/>, so a sort
/// runs on the sync driver, on the async driver and over an async-only backend alike. A step that needs one returns
/// <see cref="SortStatus.Pending"/> without having consumed its input; the progress is in named fields (the spill's buffer and row index,
/// the merge's per-run reader stage and the heap position), and calling again with the same arguments resumes.
/// </para>
/// </summary>
internal sealed class RecordSorter : IDisposable
{
    /// <summary>One sorter row: a record inside a larger buffer.</summary>
    internal readonly struct SortRow(byte[] buf, int off, int len)
    {
        internal readonly byte[] Buf = buf;
        internal readonly int Off = off;
        internal readonly int Len = len;

        internal ReadOnlySpan<byte> Span => new(Buf, Off, Len);
    }

    // Rows added in memory live in shared chunks, so a sort of 100k rows is a few large allocations, not 100k small ones.
    private const int ChunkSize = 256 * 1024;

    private const int ReaderBuffer = 16 * 1024;
    private const int WriterBuffer = 64 * 1024;

    private readonly SortTerm[] _terms;
    private readonly TextEncoding _enc;
    private readonly bool _canSpill;

    // Whether the first sort key can be compared from a cached copy (see CompareItems) instead of by parsing both records.
    private readonly bool _keyed;
    private readonly long _memLimit;
    private readonly RecordView _va = new(), _vb = new();

    private SortRow[] _mem = new SortRow[64];
    private int _memCount;
    private byte[]? _chunk;
    private int _chunkUsed;
    private int _nextChunk = 1024;
    private long _memBytes;
    private readonly List<(long Offset, long Length)> _runs = [];
    private IVfsFile? _file;
    private long _fileEnd;
    private bool _spillFailed;

    // Spill progress (Sort.SpillRun): the sorted batch, how far it has been packed, and the buffer being written.
    private enum SpillStage : byte { None, Open, Write }
    private SpillStage _spillStage;
    private bool _spillWanted;
    private SortRow[]? _spillRows;
    private int _spillIndex;
    private byte[]? _spillBuf;
    private int _spillUsed;
    private int _spillFlush;
    private long _spillStart, _spillAt;

    // Read side (Sort.MergeRead)
    private enum SortStage : byte { Idle, FinalSpill, InitReaders, Merging }
    private SortStage _sortStage;
    private bool _sorted;
    private SortRow[]? _sortedMem;
    private int _pos;
    private RunReader[]? _readers;
    private int _initIndex;
    private int[] _heap = [];
    private int _heapCount;
    private SortRow _current;
    private bool _taking;
    private int _takeTop;

    internal RecordSorter(SortTerm[] terms, TextEncoding enc, bool canSpill, long memLimit)
    {
        _terms = terms;
        _enc = enc;
        _canSpill = canSpill;
        _memLimit = memLimit;
        _keyed = terms.Length > 0 && enc == TextEncoding.Utf8 && terms[0].Collation == CollKind.Binary;
    }

    /// <summary>Number of rows added.</summary>
    internal long Count { get; private set; }

    /// <summary>How many runs were written to the temporary file (for tests).</summary>
    internal int SpilledRuns => _runs.Count;

    /// <summary>True from the moment a spill is wanted until it has finished: the row that asked for it must not be added again on re-entry.</summary>
    internal bool SpillInProgress => _spillWanted;

    /// <summary>Adds a row. Call <see cref="SpillIfNeeded"/> after it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal void Add(byte[] record)
    {
        record.CopyTo(Reserve(record.Length, out byte[] buf, out int off));
        Added(buf, off, record.Length);
    }

    /// <summary>Adds a row made of these values, written straight into the sorter's memory. Call <see cref="SpillIfNeeded"/> after it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal void Add(ReadOnlySpan<Mem> values)
    {
        int size = RecordWriter.Size(values);
        Span<byte> dst = Reserve(size, out byte[] buf, out int off);
        RecordWriter.Write(dst, values);
        Added(buf, off, size);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private Span<byte> Reserve(int size, out byte[] buf, out int off)
    {
        if (_chunk is null || _chunk.Length - _chunkUsed < size)
        {
            // Chunks start small, so a sort of a few rows costs a few hundred bytes, and double up to ChunkSize; a record larger than the
            // next chunk gets a chunk of its own.
            _nextChunk = Math.Min(ChunkSize, _nextChunk * 2);
            _chunk = new byte[Math.Max(_nextChunk, size)];
            _chunkUsed = 0;
        }

        buf = _chunk;
        off = _chunkUsed;
        _chunkUsed += size;
        return new Span<byte>(buf, off, size);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Added(byte[] buf, int off, int size)
    {
        if (_memCount == _mem.Length)
        {
            Array.Resize(ref _mem, _mem.Length * 2);
        }

        _mem[_memCount++] = new SortRow(buf, off, size);
        _memBytes += size + 24;
        Count++;
        if (_memBytes > _memLimit && !_spillFailed && _canSpill)
        {
            _spillWanted = true;
        }
    }

    private void ClearMemory()
    {
        _mem = new SortRow[64];
        _memCount = 0;
        _chunk = null;
        _chunkUsed = 0;
        _nextChunk = 1024;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int CompareKeys(in SortRow a, in SortRow b)
    {
        _va.Parse(a.Buf, a.Off, a.Len);
        _vb.Parse(b.Buf, b.Off, b.Len);
        SortTerm[] terms = _terms;
        for (int i = 0; i < terms.Length; i++)
        {
            Mem ma = default, mb = default;
            _va.Load(i, ref ma);
            _vb.Load(i, ref mb);
            bool na = ma.Kind == StorageClass.Null, nb = mb.Kind == StorageClass.Null;
            int c;
            if (na || nb)
            {
                c = na && nb ? 0 : na == terms[i].NullsFirst ? -1 : 1;
            }
            else
            {
                c = Values.Compare(ma, mb, terms[i].Collation, _enc);
                if (terms[i].Descending)
                {
                    c = -c;
                }
            }

            if (c != 0)
            {
                return c;
            }
        }

        return 0;
    }

    // ------------------------------------------------------------------ in-memory sort

    // The first sort key of a row, read once when the batch is sorted, so most comparisons never touch the record. Kind 0 is NULL; 1 integer,
    // 2 real, 3 text, 4 blob (datatype3 section 4.1 orders them NULL, numbers, text, blob).
    private struct SortItem
    {
        internal SortRow Rec;
        internal long N;
        internal int Off;
        internal int Len;
        internal int Kind;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ReadFirstKey(in SortRow row, ref SortItem it)
    {
        it.Rec = row;
        ReadOnlySpan<byte> rec = row.Span;
        int hn = Varint.Read(rec, out ulong hdrSize);
        Varint.Read(rec.Slice(hn), out ulong t);
        int body = (int)hdrSize;
        switch (t)
        {
            case 0:
                it.Kind = 0;
                return;
            case 8:
                it.Kind = 1;
                it.N = 0;
                return;
            case 9:
                it.Kind = 1;
                it.N = 1;
                return;
            case 7:
                it.Kind = 2;
                it.N = (long)BinaryPrimitives.ReadUInt64BigEndian(rec.Slice(body, 8));
                return;
            case >= 1 and <= 6:
                it.Kind = 1;
                int n = (int)SerialType.ContentSize(t);
                it.N = SerialType.ReadInt(rec.Slice(body, n), n);
                return;
            default:
                it.Kind = (t & 1) == 0 ? 4 : 3;
                it.Off = row.Off + body;
                it.Len = (int)((t - 12) / 2);
                return;
        }
    }

    // Compares two rows by their keys. The first key is decided from the items when it can be (integers, reals, binary text, blobs) and the
    // rest of the keys from the records. Equal rows compare as zero; the sort is stable, so insertion order breaks ties.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int CompareItems(in SortItem a, in SortItem b)
    {
        SortTerm t0 = _terms[0];
        int c;
        if (a.Kind == 0 || b.Kind == 0)
        {
            c = a.Kind == b.Kind ? 0 : (a.Kind == 0) == t0.NullsFirst ? -1 : 1;
        }
        else
        {
            if (a.Kind == b.Kind)
            {
                switch (a.Kind)
                {
                    case 1:
                        c = a.N.CompareTo(b.N);
                        break;
                    case 2:
                        c = BitConverter.Int64BitsToDouble(a.N).CompareTo(BitConverter.Int64BitsToDouble(b.N));
                        break;
                    default:
                        c = new ReadOnlySpan<byte>(a.Rec.Buf, a.Off, a.Len).SequenceCompareTo(new ReadOnlySpan<byte>(b.Rec.Buf, b.Off, b.Len));
                        break;
                }
            }
            else if (a.Kind <= 2 && b.Kind <= 2)
            {
                c = a.Kind == 1 ? RecordCompare.IntReal(a.N, BitConverter.Int64BitsToDouble(b.N)) : -RecordCompare.IntReal(b.N, BitConverter.Int64BitsToDouble(a.N));
            }
            else
            {
                c = (a.Kind <= 2 ? 1 : a.Kind - 1).CompareTo(b.Kind <= 2 ? 1 : b.Kind - 1);
            }

            if (t0.Descending)
            {
                c = -c;
            }
        }

        if (c != 0 || _terms.Length == 1)
        {
            return c;
        }

        return CompareKeys(a.Rec, b.Rec);
    }

    // A row's first key as a number that orders like the key (an integer with its sign bit flipped; a float with its bits folded; the first
    // 16 bytes of text or a blob, zero-padded), so a sort can compare two numbers instead of two records. Descending order flips the bits.
    private struct KeyRef
    {
        internal ulong K0;
        internal ulong K1;
        internal int Idx;
    }

    private readonly struct KeyRefComparer(RecordSorter sorter, bool full) : IComparer<KeyRef>
    {
        public int Compare(KeyRef x, KeyRef y)
        {
            if (x.K0 != y.K0)
            {
                return x.K0 < y.K0 ? -1 : 1;
            }

            if (x.K1 != y.K1)
            {
                return x.K1 < y.K1 ? -1 : 1;
            }

            if (full)
            {
                int c = sorter.CompareKeys(sorter._mem[x.Idx], sorter._mem[y.Idx]);
                if (c != 0)
                {
                    return c;
                }
            }

            return x.Idx.CompareTo(y.Idx);
        }
    }

    // Sorts the rows in memory by radix-sorting their first-key numbers, then settling the runs of equal numbers with the full comparison.
    // Returns null if the rows' first keys are of mixed kinds (or NULL beside more than one key), which the general sort handles.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private SortRow[]? SortByKeyNumbers()
    {
        int count = _memCount;
        KeyRef[] a = ArrayPool<KeyRef>.Shared.Rent(count);
        KeyRef[] b = ArrayPool<KeyRef>.Shared.Rent(count);
        int[]? nulls = null;
        try
        {
            SortTerm t0 = _terms[0];
            int kind = -1, n = 0, nullCount = 0;
            SortItem it = default;
            Span<byte> pad = stackalloc byte[16];
            for (int i = 0; i < count; i++)
            {
                ReadFirstKey(_mem[i], ref it);
                if (it.Kind == 0)
                {
                    if (_terms.Length > 1)
                    {
                        return null;
                    }

                    nulls ??= ArrayPool<int>.Shared.Rent(count);
                    nulls[nullCount++] = i;
                    continue;
                }

                if (kind < 0)
                {
                    kind = it.Kind;
                }
                else if (kind != it.Kind)
                {
                    return null;
                }

                ulong k0, k1 = 0;
                switch (it.Kind)
                {
                    case 1:
                        k0 = (ulong)it.N ^ 0x8000000000000000UL;
                        break;
                    case 2:
                        double d = BitConverter.Int64BitsToDouble(it.N);
                        long bits = d == 0 ? 0 : it.N;
                        k0 = bits < 0 ? ~(ulong)bits : (ulong)bits | 0x8000000000000000UL;
                        break;
                    default:
                        pad.Clear();
                        ReadOnlySpan<byte> bytes = new(it.Rec.Buf, it.Off, it.Len);
                        bytes.Slice(0, Math.Min(16, bytes.Length)).CopyTo(pad);
                        k0 = BinaryPrimitives.ReadUInt64BigEndian(pad);
                        k1 = BinaryPrimitives.ReadUInt64BigEndian(pad.Slice(8));
                        break;
                }

                if (t0.Descending)
                {
                    k0 = ~k0;
                    k1 = ~k1;
                }

                a[n].K0 = k0;
                a[n].K1 = k1;
                a[n].Idx = i;
                n++;
            }

            if (n > 1)
            {
                RadixSort(ref a, ref b, n);
                bool exact = kind <= 2 && _terms.Length == 1;
                if (!exact)
                {
                    // Runs whose first 8 bytes agree are ordered by the next 8, then by the whole key, then by insertion order.
                    var cmp = new KeyRefComparer(this, true);
                    for (int lo = 0; lo < n;)
                    {
                        int hi = lo + 1;
                        while (hi < n && a[hi].K0 == a[lo].K0)
                        {
                            hi++;
                        }

                        if (hi - lo > 1)
                        {
                            Array.Sort(a, lo, hi - lo, cmp);
                        }

                        lo = hi;
                    }
                }
            }

            var sorted = new SortRow[count];
            int o = 0;
            if (t0.NullsFirst)
            {
                for (int i = 0; i < nullCount; i++)
                {
                    sorted[o++] = _mem[nulls![i]];
                }
            }

            for (int i = 0; i < n; i++)
            {
                sorted[o++] = _mem[a[i].Idx];
            }

            if (!t0.NullsFirst)
            {
                for (int i = 0; i < nullCount; i++)
                {
                    sorted[o++] = _mem[nulls![i]];
                }
            }

            return sorted;
        }
        finally
        {
            ArrayPool<KeyRef>.Shared.Return(a);
            ArrayPool<KeyRef>.Shared.Return(b);
            if (nulls is not null)
            {
                ArrayPool<int>.Shared.Return(nulls);
            }
        }
    }

    // A stable least-significant-byte-first radix sort on K0. A byte position where every key agrees is skipped, so keys that differ
    // in a few low bytes (small integers, text with a long common start) cost a pass or two.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void RadixSort(ref KeyRef[] a, ref KeyRef[] b, int n)
    {
        var hist = new int[8 * 256];
        for (int i = 0; i < n; i++)
        {
            ulong k = a[i].K0;
            for (int p = 0; p < 8; p++)
            {
                hist[(p << 8) + (int)((k >> (8 * p)) & 0xff)]++;
            }
        }

        for (int p = 0; p < 8; p++)
        {
            int baseIdx = p << 8;
            if (hist[baseIdx + (int)((a[0].K0 >> (8 * p)) & 0xff)] == n)
            {
                continue;
            }

            int sum = 0;
            for (int d = 0; d < 256; d++)
            {
                int c = hist[baseIdx + d];
                hist[baseIdx + d] = sum;
                sum += c;
            }

            for (int i = 0; i < n; i++)
            {
                ref KeyRef src = ref a[i];
                b[hist[baseIdx + (int)((src.K0 >> (8 * p)) & 0xff)]++] = src;
            }

            (a, b) = (b, a);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private SortRow[] SortMemory()
    {
        int count = _memCount;
        if (!_keyed || count < 2)
        {
            return SortMemorySlow();
        }

        if (count >= 32 && SortByKeyNumbers() is { } byNumbers)
        {
            return byNumbers;
        }

        // A stable bottom-up merge sort of small structs: comparisons read the cached first key, not the record.
        var a = new SortItem[count];
        for (int i = 0; i < count; i++)
        {
            ReadFirstKey(_mem[i], ref a[i]);
        }

        var b = new SortItem[count];
        const int Run = 8;
        for (int lo = 0; lo < count; lo += Run)
        {
            int hi = Math.Min(lo + Run, count);
            for (int i = lo + 1; i < hi; i++)
            {
                SortItem x = a[i];
                int j = i - 1;
                while (j >= lo && CompareItems(a[j], x) > 0)
                {
                    a[j + 1] = a[j];
                    j--;
                }

                a[j + 1] = x;
            }
        }

        for (int width = Run; width < count; width *= 2)
        {
            for (int lo = 0; lo < count; lo += 2 * width)
            {
                int mid = Math.Min(lo + width, count), hi = Math.Min(lo + (2 * width), count);
                int l = lo, r = mid, o = lo;
                if (mid >= hi || CompareItems(a[mid - 1], a[mid]) <= 0)
                {
                    Array.Copy(a, lo, b, lo, hi - lo);
                    continue;
                }

                while (l < mid && r < hi)
                {
                    b[o++] = CompareItems(a[r], a[l]) < 0 ? a[r++] : a[l++];
                }

                while (l < mid)
                {
                    b[o++] = a[l++];
                }

                while (r < hi)
                {
                    b[o++] = a[r++];
                }
            }

            (a, b) = (b, a);
        }

        var sorted = new SortRow[count];
        for (int i = 0; i < count; i++)
        {
            sorted[i] = a[i].Rec;
        }

        return sorted;
    }

    private SortRow[] SortMemorySlow()
    {
        SortRow[] rows = _mem.AsSpan(0, _memCount).ToArray();
        int[] seq = new int[rows.Length];
        for (int i = 0; i < seq.Length; i++)
        {
            seq[i] = i;
        }

        Array.Sort(seq, (x, y) =>
        {
            int c = CompareKeys(rows[x], rows[y]);
            return c != 0 ? c : x.CompareTo(y);
        });
        var sorted = new SortRow[rows.Length];
        for (int i = 0; i < seq.Length; i++)
        {
            sorted[i] = rows[seq[i]];
        }

        return sorted;
    }

    // ------------------------------------------------------------------ spill (Sort.SpillRun)

    /// <summary>
    /// If the memory budget is used up, sorts the batch and writes it to the temporary file as a run. Re-entrant: returns Pending at each
    /// file open and write. A temporary file that can't be opened or written is not an error; the sort carries on in memory.
    /// </summary>
    internal SortStatus SpillIfNeeded(Pager io)
    {
        if (!_spillWanted)
        {
            return SortStatus.Ok;
        }

        if (_spillStage == SpillStage.None)
        {
            _spillStage = SpillStage.Open;
        }

        if (_spillStage == SpillStage.Open)
        {
            if (_file is null)
            {
                var open = new IoRequest
                {
                    Kind = IoKind.Open,
                    Path = string.Empty,
                    Arg = (int)(VfsOpenOptions.ReadWrite | VfsOpenOptions.Create | VfsOpenOptions.TempJournal | VfsOpenOptions.DeleteOnClose),
                };
                if (!io.ExternalIo(ResumeState.SortSpillOpen, open, out IoCompletion oc))
                {
                    return SortStatus.Pending;
                }

                if (oc.Status != 0 || oc.File is null)
                {
                    GiveUpSpilling();
                    return SortStatus.Ok;
                }

                _file = oc.File;
            }

            _spillRows = SortMemory();
            _spillIndex = 0;
            _spillUsed = 0;
            _spillFlush = 0;
            _spillAt = _spillStart = _fileEnd;
            _spillBuf ??= new byte[WriterBuffer];
            _spillStage = SpillStage.Write;
        }

        SortRow[] rows = _spillRows!;
        for (; ; )
        {
            if (_spillFlush > 0)
            {
                var write = new IoRequest { Kind = IoKind.Write, File = _file, Offset = _spillAt, Length = _spillFlush, Data = _spillBuf!.AsMemory(0, _spillFlush) };
                if (!io.ExternalIo(ResumeState.SortSpillRun, write, out IoCompletion wc))
                {
                    return SortStatus.Pending;
                }

                if (wc.Status != 0)
                {
                    GiveUpSpilling();
                    return SortStatus.Ok;
                }

                _spillAt += _spillFlush;
                _spillFlush = 0;
                _spillUsed = 0;
            }

            // Pack records into the buffer until it is full; a record larger than the buffer gets a buffer of its own size.
            while (_spillIndex < rows.Length)
            {
                SortRow r = rows[_spillIndex];
                if (_spillUsed + 4 + r.Len > _spillBuf!.Length)
                {
                    if (_spillUsed > 0)
                    {
                        break;
                    }

                    _spillBuf = new byte[4 + r.Len];
                }

                BinaryPrimitives.WriteInt32LittleEndian(_spillBuf.AsSpan(_spillUsed), r.Len);
                r.Span.CopyTo(_spillBuf.AsSpan(_spillUsed + 4));
                _spillUsed += 4 + r.Len;
                _spillIndex++;
            }

            if (_spillUsed > 0)
            {
                _spillFlush = _spillUsed;
                continue;
            }

            break;
        }

        _runs.Add((_spillStart, _spillAt - _spillStart));
        _fileEnd = _spillAt;
        ClearMemory();
        _memBytes = 0;
        _spillRows = null;
        _spillStage = SpillStage.None;
        _spillWanted = false;
        if (_spillBuf is { Length: > WriterBuffer })
        {
            _spillBuf = null;
        }

        return SortStatus.Ok;
    }

    private void GiveUpSpilling()
    {
        // No temporary space: what was not written stays in memory and the run in progress is dropped.
        _spillFailed = true;
        _spillWanted = false;
        _spillStage = SpillStage.None;
        _spillRows = null;
        _spillFlush = 0;
        _spillUsed = 0;
    }

    // ------------------------------------------------------------------ read side (Sort.MergeRead)

    /// <summary>Sorts everything added so far and positions on the first row. <paramref name="hasRow"/> is false if there are no rows.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal SortStatus Sort(Pager io, out bool hasRow)
    {
        hasRow = false;
        if (_sortStage == SortStage.Idle)
        {
            _sorted = true;
            _pos = 0;
            _current = default;
            if (_runs.Count == 0)
            {
                _sortedMem = SortMemory();
                ClearMemory();
                hasRow = _sortedMem.Length > 0;
                if (hasRow)
                {
                    _current = _sortedMem[0];
                }

                return SortStatus.Ok;
            }

            _sortStage = SortStage.FinalSpill;
            _spillWanted = _memCount > 0 && !_spillFailed;
        }

        if (_sortStage == SortStage.FinalSpill)
        {
            if (SpillIfNeeded(io) == SortStatus.Pending)
            {
                return SortStatus.Pending;
            }

            if (_memCount > 0)
            {
                // The last spill failed after earlier ones succeeded: merge the runs with what is still in memory as one more reader.
                _sortedMem = SortMemory();
                ClearMemory();
            }

            int n = _runs.Count + (_sortedMem is { Length: > 0 } ? 1 : 0);
            _readers = new RunReader[n];
            for (int i = 0; i < _runs.Count; i++)
            {
                _readers[i] = new RunReader(_file!, _runs[i].Offset, _runs[i].Length, _keyed);
            }

            if (_sortedMem is { Length: > 0 })
            {
                _readers[n - 1] = new RunReader(_sortedMem, _keyed);
            }

            _heap = new int[n];
            _heapCount = 0;
            _initIndex = 0;
            _sortStage = SortStage.InitReaders;
        }

        if (_sortStage == SortStage.InitReaders)
        {
            while (_initIndex < _readers!.Length)
            {
                if (_readers[_initIndex].Advance(io, out bool has) == SortStatus.Pending)
                {
                    return SortStatus.Pending;
                }

                if (has)
                {
                    _heap[_heapCount++] = _initIndex;
                }

                _initIndex++;
            }

            for (int i = (_heapCount / 2) - 1; i >= 0; i--)
            {
                SiftDown(i);
            }

            _sortStage = SortStage.Merging;
            _taking = false;
        }

        return TakeHeapTop(io, out hasRow);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool Less(int ra, int rb)
    {
        int c = _keyed ? CompareItems(in _readers![ra].Item, in _readers[rb].Item) : CompareKeys(_readers![ra].Current, _readers[rb].Current);
        return c != 0 ? c < 0 : ra < rb;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void SiftDown(int i)
    {
        for (; ; )
        {
            int l = (2 * i) + 1, r = l + 1, m = i;
            if (l < _heapCount && Less(_heap[l], _heap[m]))
            {
                m = l;
            }

            if (r < _heapCount && Less(_heap[r], _heap[m]))
            {
                m = r;
            }

            if (m == i)
            {
                return;
            }

            (_heap[i], _heap[m]) = (_heap[m], _heap[i]);
            i = m;
        }
    }

    // Takes the smallest row and refills the reader it came from. The row is saved before the refill, which may suspend; on re-entry only the refill runs.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private SortStatus TakeHeapTop(Pager io, out bool hasRow)
    {
        hasRow = false;
        if (!_taking)
        {
            if (_heapCount == 0)
            {
                _current = default;
                return SortStatus.Ok;
            }

            _takeTop = _heap[0];
            _current = _readers![_takeTop].Current;
            _taking = true;
        }

        if (_readers![_takeTop].Advance(io, out bool more) == SortStatus.Pending)
        {
            return SortStatus.Pending;
        }

        _taking = false;
        if (more)
        {
            SiftDown(0);
        }
        else
        {
            _heap[0] = _heap[--_heapCount];
            if (_heapCount > 0)
            {
                SiftDown(0);
            }
        }

        hasRow = true;
        return SortStatus.Ok;
    }

    /// <summary>Sort for a sorter that never spills (no I/O, so it never suspends).</summary>
    internal bool SortInMemory() => Sort(null!, out bool any) == SortStatus.Ok && any;

    /// <summary>Next for a sorter that never spills.</summary>
    internal bool NextInMemory() => Next(null!, out bool any) == SortStatus.Ok && any;

    /// <summary>The row the sorter is on.</summary>
    internal SortRow Current => _current.Buf is null ? throw new InvalidOperationException("the sorter is not on a row") : _current;

    /// <summary>Moves to the next row; <paramref name="hasRow"/> is false at the end.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal SortStatus Next(Pager io, out bool hasRow)
    {
        if (!_sorted)
        {
            throw new InvalidOperationException("the sorter was not sorted");
        }

        if (_readers is null)
        {
            _pos++;
            if (_sortedMem is null || _pos >= _sortedMem.Length)
            {
                _current = default;
                hasRow = false;
                return SortStatus.Ok;
            }

            _current = _sortedMem[_pos];
            hasRow = true;
            return SortStatus.Ok;
        }

        return TakeHeapTop(io, out hasRow);
    }

    public void Dispose()
    {
        _file?.Dispose();
        _file = null;
        ClearMemory();
        _sortedMem = null;
        _readers = null;
        _spillRows = null;
    }

    /// <summary>Reads one sorted run (or an in-memory batch) record by record, suspending whenever it needs the file.</summary>
    private sealed class RunReader
    {
        private readonly IVfsFile? _file;
        private readonly SortRow[]? _rows;
        private long _at;
        private readonly long _end;
        private readonly byte[] _buf = new byte[ReaderBuffer];
        private int _bufPos, _bufLen;
        private int _row;

        // Progress through one record: the 4-byte length, then the body.
        private bool _inBody;
        private readonly byte[] _lenBuf = new byte[4];
        // The record being read. Every record gets its own array: a statement may keep a value from a row (a bare column of a group) while
        // later rows of the group are read.
        private byte[] _rec = [];
        private int _want;
        private int _got;
        private readonly bool _keyed;

        internal RunReader(IVfsFile file, long offset, long length, bool keyed)
        {
            _file = file;
            _at = offset;
            _end = offset + length;
            _keyed = keyed;
        }

        internal RunReader(SortRow[] rows, bool keyed)
        {
            _rows = rows;
            _keyed = keyed;
        }

        internal SortRow Current { get; private set; }

        /// <summary>The first sort key of <see cref="Current"/>, when the sorter compares by cached keys.</summary>
        internal SortItem Item;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal SortStatus Advance(Pager io, out bool has)
        {
            has = false;
            if (_rows is not null)
            {
                if (_row >= _rows.Length)
                {
                    Current = default;
                    return SortStatus.Ok;
                }

                Current = _rows[_row++];
                if (_keyed)
                {
                    ReadFirstKey(Current, ref Item);
                }

                has = true;
                return SortStatus.Ok;
            }

            if (!_inBody)
            {
                if (ReadExact(io, _lenBuf, 4, allowEnd: true, out bool end) == SortStatus.Pending)
                {
                    return SortStatus.Pending;
                }

                if (end)
                {
                    Current = default;
                    return SortStatus.Ok;
                }

                int n = BinaryPrimitives.ReadInt32LittleEndian(_lenBuf);
                if (n < 0 || n > Values.MaxLength)
                {
                    throw new OneLiteException(ResultCode.IoErr, "disk I/O error");
                }

                _rec = new byte[n];
                _want = n;
                _got = 0;
                _inBody = true;
            }

            if (ReadExact(io, _rec, _want, allowEnd: false, out _) == SortStatus.Pending)
            {
                return SortStatus.Pending;
            }

            _inBody = false;
            _got = 0;
            Current = new SortRow(_rec, 0, _want);
            if (_keyed)
            {
                ReadFirstKey(Current, ref Item);
            }

            has = true;
            return SortStatus.Ok;
        }

        private SortStatus ReadExact(Pager io, byte[] dst, int length, bool allowEnd, out bool end)
        {
            end = false;
            while (_got < length)
            {
                if (_bufPos == _bufLen)
                {
                    long left = _end - _at;
                    if (left <= 0)
                    {
                        if (allowEnd && _got == 0)
                        {
                            end = true;
                            return SortStatus.Ok;
                        }

                        throw new OneLiteException(ResultCode.IoErr, "disk I/O error");
                    }

                    int want = (int)Math.Min(left, _buf.Length);
                    var read = new IoRequest { Kind = IoKind.Read, File = _file, Offset = _at, Length = want, Data = _buf.AsMemory(0, want), Cancellable = true };
                    if (!io.ExternalIo(ResumeState.SortMergeRead, read, out IoCompletion c))
                    {
                        return SortStatus.Pending;
                    }

                    if (c.Status != 0 || c.Value <= 0)
                    {
                        throw new OneLiteException(ResultCode.IoErr, "disk I/O error");
                    }

                    _at += c.Value;
                    _bufPos = 0;
                    _bufLen = (int)c.Value;
                }

                int take = Math.Min(length - _got, _bufLen - _bufPos);
                _buf.AsSpan(_bufPos, take).CopyTo(dst.AsSpan(_got));
                _bufPos += take;
                _got += take;
            }

            return SortStatus.Ok;
        }
    }
}
