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
    private const int ReaderBuffer = 16 * 1024;
    private const int WriterBuffer = 64 * 1024;

    private readonly SortTerm[] _terms;
    private readonly TextEncoding _enc;
    private readonly bool _canSpill;
    private readonly long _memLimit;
    private readonly RecordView _va = new(), _vb = new();

    private List<byte[]> _mem = [];
    private long _memBytes;
    private readonly List<(long Offset, long Length)> _runs = [];
    private IVfsFile? _file;
    private long _fileEnd;
    private bool _spillFailed;

    // Spill progress (Sort.SpillRun): the sorted batch, how far it has been packed, and the buffer being written.
    private enum SpillStage : byte { None, Open, Write }
    private SpillStage _spillStage;
    private bool _spillWanted;
    private byte[][]? _spillRows;
    private int _spillIndex;
    private byte[]? _spillBuf;
    private int _spillUsed;
    private int _spillFlush;
    private long _spillStart, _spillAt;

    // Read side (Sort.MergeRead)
    private enum SortStage : byte { Idle, FinalSpill, InitReaders, Merging }
    private SortStage _sortStage;
    private bool _sorted;
    private byte[][]? _sortedMem;
    private int _pos;
    private RunReader[]? _readers;
    private int _initIndex;
    private int[] _heap = [];
    private int _heapCount;
    private byte[]? _current;
    private bool _taking;
    private int _takeTop;

    internal RecordSorter(SortTerm[] terms, TextEncoding enc, bool canSpill, long memLimit)
    {
        _terms = terms;
        _enc = enc;
        _canSpill = canSpill;
        _memLimit = memLimit;
    }

    /// <summary>Number of rows added.</summary>
    internal long Count { get; private set; }

    /// <summary>How many runs were written to the temporary file (for tests).</summary>
    internal int SpilledRuns => _runs.Count;

    /// <summary>True from the moment a spill is wanted until it has finished: the row that asked for it must not be added again on re-entry.</summary>
    internal bool SpillInProgress => _spillWanted;

    /// <summary>Adds a row. Call <see cref="SpillIfNeeded"/> after it.</summary>
    internal void Add(byte[] record)
    {
        _mem.Add(record);
        _memBytes += record.Length + 24;
        Count++;
        if (_memBytes > _memLimit && !_spillFailed && _canSpill)
        {
            _spillWanted = true;
        }
    }

    private int CompareKeys(byte[] a, byte[] b)
    {
        _va.Parse(a, 0, a.Length);
        _vb.Parse(b, 0, b.Length);
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

    private byte[][] SortMemory()
    {
        byte[][] rows = [.. _mem];
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
        var sorted = new byte[rows.Length][];
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

        byte[][] rows = _spillRows!;
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
                byte[] r = rows[_spillIndex];
                if (_spillUsed + 4 + r.Length > _spillBuf!.Length)
                {
                    if (_spillUsed > 0)
                    {
                        break;
                    }

                    _spillBuf = new byte[4 + r.Length];
                }

                BinaryPrimitives.WriteInt32LittleEndian(_spillBuf.AsSpan(_spillUsed), r.Length);
                r.CopyTo(_spillBuf, _spillUsed + 4);
                _spillUsed += 4 + r.Length;
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
        _mem = [];
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
    internal SortStatus Sort(Pager io, out bool hasRow)
    {
        hasRow = false;
        if (_sortStage == SortStage.Idle)
        {
            _sorted = true;
            _pos = 0;
            _current = null;
            if (_runs.Count == 0)
            {
                _sortedMem = SortMemory();
                _mem = [];
                hasRow = _sortedMem.Length > 0;
                if (hasRow)
                {
                    _current = _sortedMem[0];
                }

                return SortStatus.Ok;
            }

            _sortStage = SortStage.FinalSpill;
            _spillWanted = _mem.Count > 0 && !_spillFailed;
        }

        if (_sortStage == SortStage.FinalSpill)
        {
            if (SpillIfNeeded(io) == SortStatus.Pending)
            {
                return SortStatus.Pending;
            }

            if (_mem.Count > 0)
            {
                // The last spill failed after earlier ones succeeded: merge the runs with what is still in memory as one more reader.
                _sortedMem = SortMemory();
                _mem = [];
            }

            int n = _runs.Count + (_sortedMem is { Length: > 0 } ? 1 : 0);
            _readers = new RunReader[n];
            for (int i = 0; i < _runs.Count; i++)
            {
                _readers[i] = new RunReader(_file!, _runs[i].Offset, _runs[i].Length);
            }

            if (_sortedMem is { Length: > 0 })
            {
                _readers[n - 1] = new RunReader(_sortedMem);
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

    private bool Less(int ra, int rb)
    {
        int c = CompareKeys(_readers![ra].Current!, _readers[rb].Current!);
        return c != 0 ? c < 0 : ra < rb;
    }

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
    private SortStatus TakeHeapTop(Pager io, out bool hasRow)
    {
        hasRow = false;
        if (!_taking)
        {
            if (_heapCount == 0)
            {
                _current = null;
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
    internal byte[] Current => _current ?? throw new InvalidOperationException("the sorter is not on a row");

    /// <summary>Moves to the next row; <paramref name="hasRow"/> is false at the end.</summary>
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
                _current = null;
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
        _mem = [];
        _sortedMem = null;
        _readers = null;
        _spillRows = null;
    }

    /// <summary>Reads one sorted run (or an in-memory batch) record by record, suspending whenever it needs the file.</summary>
    private sealed class RunReader
    {
        private readonly IVfsFile? _file;
        private readonly byte[][]? _rows;
        private long _at;
        private readonly long _end;
        private readonly byte[] _buf = new byte[ReaderBuffer];
        private int _bufPos, _bufLen;
        private int _row;

        // Progress through one record: the 4-byte length, then the body.
        private bool _inBody;
        private readonly byte[] _lenBuf = new byte[4];
        private byte[] _rec = [];
        private int _got;

        internal RunReader(IVfsFile file, long offset, long length)
        {
            _file = file;
            _at = offset;
            _end = offset + length;
        }

        internal RunReader(byte[][] rows) => _rows = rows;

        internal byte[]? Current { get; private set; }

        internal SortStatus Advance(Pager io, out bool has)
        {
            has = false;
            if (_rows is not null)
            {
                if (_row >= _rows.Length)
                {
                    Current = null;
                    return SortStatus.Ok;
                }

                Current = _rows[_row++];
                has = true;
                return SortStatus.Ok;
            }

            if (!_inBody)
            {
                if (ReadExact(io, _lenBuf, allowEnd: true, out bool end) == SortStatus.Pending)
                {
                    return SortStatus.Pending;
                }

                if (end)
                {
                    Current = null;
                    return SortStatus.Ok;
                }

                int n = BinaryPrimitives.ReadInt32LittleEndian(_lenBuf);
                if (n < 0 || n > Values.MaxLength)
                {
                    throw new OneLiteException(ResultCode.IoErr, "disk I/O error");
                }

                _rec = new byte[n];
                _got = 0;
                _inBody = true;
            }

            if (ReadExact(io, _rec, allowEnd: false, out _) == SortStatus.Pending)
            {
                return SortStatus.Pending;
            }

            _inBody = false;
            _got = 0;
            Current = _rec;
            has = true;
            return SortStatus.Ok;
        }

        private SortStatus ReadExact(Pager io, byte[] dst, bool allowEnd, out bool end)
        {
            end = false;
            while (_got < dst.Length)
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

                int take = Math.Min(dst.Length - _got, _bufLen - _bufPos);
                _buf.AsSpan(_bufPos, take).CopyTo(dst.AsSpan(_got));
                _bufPos += take;
                _got += take;
            }

            return SortStatus.Ok;
        }
    }
}
