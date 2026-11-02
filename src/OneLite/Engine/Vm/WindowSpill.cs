using System.Buffers.Binary;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Storage.Vfs;

namespace RecursiveAI.OneLite.Vm;

/// <summary>
/// The partition spill of <see cref="WindowRun"/> (windowfunctions; the sorter's temporary-file rules, tempfiles). A partition that has
/// grown past the sort memory limit moves from the in-memory ring into blocks of <see cref="BlockRows"/> rows. A block is written to the
/// temporary file (never synced, deleted on close) only when it has to leave memory, and is read back when a row in it is asked for.
/// <para>
/// Suspension (Sort.* states): every file request goes through <see cref="Pager.ExternalIo"/>, so the work runs on both drivers.
/// Reading a row of a block that is on disk throws <see cref="NeedBlockException"/> out of the evaluation, which only reads and updates
/// state in steps that can be repeated (the aggregate bounds move one row at a time); <see cref="Service"/> then reads the block and the
/// evaluation is tried again. Nothing is published to the registers or the statement before an evaluation completes.
/// </para>
/// <para>
/// Least recently used blocks leave memory first. The limit is soft: a frame that is scanned from its start for every row (min, max, a
/// frame with EXCLUDE) must have its whole frame in memory, and the number of resident blocks then grows until the scan fits.
/// </para>
/// </summary>
internal sealed partial class WindowRun
{
    private const int BlockRows = 128;
    private const int MinResident = 6;

    private sealed class Block
    {
        internal Row?[]? Rows;
        internal int Lo, Count;
        internal long Off;
        internal int Len;
        internal bool Written;
        internal long Touched;
        internal long Bytes;

        // Peer groups (windowfunctions): the group of the first and of the last row, and where the first row's group starts. Kept when the
        // rows leave memory, so the peer-group bounds are found without keeping a start per group.
        internal long GFirst, GLast, GStart;
    }

    private sealed class NeedBlockException : Exception
    {
        internal static readonly NeedBlockException Instance = new();
    }

    private readonly bool _canSpill;
    private readonly long _memLimit;
    private long _bytes;
    private bool _spill, _spillFailed, _inFlight;
    private readonly List<Block?> _blocks = [];
    private readonly List<Block> _res = [];
    private long _resBytes, _tick;
    private int _dropB, _want = -1, _missRun, _extra;
    private IVfsFile? _file;
    private long _fileEnd;
    private Block? _wBlk;
    private byte[]? _wBuf, _rBuf;
    private RecordView? _view;
    private long _lastGroupStart;

    /// <summary>True between a <see cref="Push"/> and the end of its <see cref="Service"/>: the row is already in, so a re-entry must not push it again.</summary>
    internal bool InFlight => _inFlight;

    /// <summary>Rows moved out of memory so far in the current run (blocks written), for tests.</summary>
    internal long SpilledBytes { get; private set; }

    private static long s_spilledTotal;

    /// <summary>Bytes written by every window run in the process so far; a test reads the difference to know a partition spilled.</summary>
    internal static long TotalSpilledBytes => Interlocked.Read(ref s_spilledTotal);

    private void EnterSpill()
    {
        // The ring's rows go into blocks. The peer-group starts kept so far are let go: from here a start is found in the blocks.
        _lastGroupStart = GroupStart(_last!.Group);
        long gStart = _n > 0 ? GroupStart(_ring[_head]!.Group) : 0;
        _spill = true;
        _dropB = (int)(_first / BlockRows);
        int mask = _ring.Length - 1;
        long prevGroup = -1;
        for (int i = 0; i < _n; i++)
        {
            int slot = (_head + i) & mask;
            Row row = _ring[slot]!;
            if (i > 0 && row.Group != prevGroup)
            {
                gStart = _first + i;
            }

            prevGroup = row.Group;
            SpillAddAt(_first + i, row, gStart);
            _ring[slot] = null;
        }

        _n = 0;
        _head = 0;
        _bytes = 0;
        _gN = 0;
        _gHead = 0;
        _gBase = 0;
        _gs = new long[16];
    }

    private void SpillAddRow(Row row) => SpillAddAt(_count, row, _lastGroupStart);

    private void SpillAddAt(long abs, Row row, long groupStart)
    {
        int bi = (int)(abs / BlockRows);
        while (_blocks.Count <= bi)
        {
            _blocks.Add(null);
        }

        Block? blk = _blocks[bi];
        int idx = (int)(abs - (long)bi * BlockRows);
        if (blk is null)
        {
            blk = new Block { Rows = new Row?[BlockRows], Lo = idx, GFirst = row.Group, GStart = groupStart };
            _blocks[bi] = blk;
            _res.Add(blk);
        }

        blk.Rows![idx] = row;
        blk.Count = idx + 1;
        blk.GLast = row.Group;
        blk.Bytes += row.Size;
        _resBytes += row.Size;
        blk.Touched = ++_tick;
    }

    private Row SpillRowAt(long abs)
    {
        int bi = (int)(abs / BlockRows);
        if (bi >= _blocks.Count || _blocks[bi] is not { } blk)
        {
            throw new InvalidOperationException("window row " + abs + " is not held");
        }

        if (blk.Rows is null)
        {
            _want = bi;
            throw NeedBlockException.Instance;
        }

        blk.Touched = ++_tick;
        return blk.Rows[abs - (long)bi * BlockRows] ?? throw new InvalidOperationException("window row " + abs + " was dropped");
    }

    /// <summary>
    /// Where peer group <paramref name="g"/> starts: the first block whose last row is in the group or later holds the start (an earlier
    /// block ends before the group), found by binary search over the block table and then over the block's rows, which are in group order.
    /// </summary>
    private long SpillGroupStart(long g)
    {
        int lo = _dropB, hi = _blocks.Count - 1;
        if (g < _blocks[lo]!.GFirst)
        {
            return _first;
        }

        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_blocks[mid]!.GLast >= g)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        Block blk = _blocks[lo]!;
        if (g <= blk.GFirst)
        {
            return blk.GStart;
        }

        if (blk.Rows is null)
        {
            _want = lo;
            throw NeedBlockException.Instance;
        }

        long baseAbs = (long)lo * BlockRows;
        int a = blk.Lo, b = blk.Count - 1;
        while (a < b)
        {
            int mid = (a + b) >>> 1;
            if (blk.Rows[mid]!.Group >= g)
            {
                b = mid;
            }
            else
            {
                a = mid + 1;
            }
        }

        return baseAbs + a;
    }

    private void NoteMiss()
    {
        // Misses without any row of progress between them mean a scan needs more blocks than are kept: let it have them.
        if (++_missRun > MinResident + _extra)
        {
            _extra++;
            _missRun = 0;
        }
    }

    private void DropBlocksBefore(long keep)
    {
        while (_dropB < _blocks.Count && (long)(_dropB + 1) * BlockRows <= keep)
        {
            Block? blk = _blocks[_dropB];
            if (blk is not null)
            {
                if (blk.Rows is not null)
                {
                    _res.Remove(blk);
                    _resBytes -= blk.Bytes;
                }

                if (ReferenceEquals(blk, _wBlk))
                {
                    _wBlk = null;
                    _wBuf = null;
                }

                _blocks[_dropB] = null;
            }

            _dropB++;
        }
    }

    private void ReleaseSpill()
    {
        _spill = false;
        _blocks.Clear();
        _res.Clear();
        _resBytes = 0;
        _dropB = 0;
        _want = -1;
        _missRun = 0;
        _extra = 0;
        _fileEnd = 0;
        _wBlk = null;
        _wBuf = null;
        _rBuf = null;
    }

    /// <summary>Resets the spill state for a statement run again, keeping the buffers.</summary>
    private void ResetSpillRun()
    {
        _spillFailed = false;
        SpilledBytes = 0;
    }

    /// <summary>Closes the temporary file (the statement was reset or the connection closed).</summary>
    internal void CloseSpill()
    {
        _file?.Dispose();
        _file = null;
        ReleaseSpill();
    }

    /// <summary>
    /// Reads the block a suspended evaluation asked for and writes out least recently used blocks while the memory limit is exceeded.
    /// Re-entrant: returns Pending at each file request, to be called again with the same arguments.
    /// </summary>
    internal SortStatus Service(Pager io)
    {
        if (_want >= 0)
        {
            Block blk = _blocks[_want]!;
            if (blk.Rows is null)
            {
                _rBuf ??= new byte[blk.Len];
                var read = new IoRequest { Kind = IoKind.Read, File = _file, Offset = blk.Off, Length = blk.Len, Data = _rBuf.AsMemory(0, blk.Len), Cancellable = true };
                if (!io.ExternalIo(ResumeState.SortMergeRead, read, out IoCompletion rc))
                {
                    return SortStatus.Pending;
                }

                if (rc.Status != 0 || rc.Value != blk.Len)
                {
                    _rBuf = null;
                    throw new OneLiteException(ResultCode.IoErr, "disk I/O error");
                }

                Install(blk, _rBuf);
                _rBuf = null;
            }

            _want = -1;
        }

        while (_spill && !_spillFailed && _resBytes > _memLimit && _res.Count > MinResident + _extra)
        {
            if (_wBlk is null)
            {
                _wBlk = PickVictim();
                if (_wBlk is null)
                {
                    break;
                }
            }

            if (!_wBlk.Written)
            {
                int st = WriteBlock(io);
                if (st == 1)
                {
                    return SortStatus.Pending;
                }

                if (st == 2)
                {
                    // No temporary space: carry on in memory.
                    _spillFailed = true;
                    _wBlk = null;
                    _wBuf = null;
                    break;
                }
            }

            _res.Remove(_wBlk);
            _resBytes -= _wBlk.Bytes;
            _wBlk.Rows = null;
            _wBlk = null;
        }

        _inFlight = false;
        return SortStatus.Ok;
    }

    private Block? PickVictim()
    {
        Block? best = null;
        int last = _blocks.Count - 1;
        foreach (Block b in _res)
        {
            // The block still being filled stays.
            if (b.Count < BlockRows && ReferenceEquals(b, _blocks[last]))
            {
                continue;
            }

            if (best is null || b.Touched < best.Touched)
            {
                best = b;
            }
        }

        return best;
    }

    /// <summary>0 = written, 1 = pending, 2 = failed.</summary>
    private int WriteBlock(Pager io)
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
                return 1;
            }

            if (oc.Status != 0 || oc.File is null)
            {
                return 2;
            }

            _file = oc.File;
        }

        Block blk = _wBlk!;
        _wBuf ??= Serialize(blk);
        var write = new IoRequest { Kind = IoKind.Write, File = _file, Offset = _fileEnd, Length = _wBuf.Length, Data = _wBuf };
        if (!io.ExternalIo(ResumeState.SortSpillRun, write, out IoCompletion wc))
        {
            return 1;
        }

        if (wc.Status != 0)
        {
            return 2;
        }

        blk.Off = _fileEnd;
        blk.Len = _wBuf.Length;
        _fileEnd += _wBuf.Length;
        SpilledBytes += _wBuf.Length;
        Interlocked.Add(ref s_spilledTotal, _wBuf.Length);
        blk.Written = true;
        _wBuf = null;
        return 0;
    }

    // A block on disk is its rows one after another: int32 record length, int64 peer group, then the record (fileformat2 §2.1 layout).
    private byte[] Serialize(Block blk)
    {
        var recs = new byte[blk.Count - blk.Lo][];
        int total = 0;
        for (int i = blk.Lo; i < blk.Count; i++)
        {
            Row row = blk.Rows![i]!;
            byte[] rec = RecordWriter.ToArray(new ReadOnlySpan<Mem>(row.F, 0, _w));
            recs[i - blk.Lo] = rec;
            total += 12 + rec.Length;
        }

        var buf = new byte[total];
        int pos = 0;
        for (int i = blk.Lo; i < blk.Count; i++)
        {
            byte[] rec = recs[i - blk.Lo];
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos), rec.Length);
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(pos + 4), blk.Rows![i]!.Group);
            rec.CopyTo(buf, pos + 12);
            pos += 12 + rec.Length;
        }

        return buf;
    }

    private void Install(Block blk, byte[] data)
    {
        _view ??= new RecordView();
        var rows = new Row?[BlockRows];
        int pos = 0, idx = blk.Lo;
        while (pos < data.Length)
        {
            if (data.Length - pos < 12 || idx >= BlockRows)
            {
                throw new OneLiteException(ResultCode.IoErr, "disk I/O error");
            }

            int len = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos));
            if (len < 0 || len > data.Length - pos - 12 || !_view.Parse(data, pos + 12, len) || _view.ColumnCount < _w)
            {
                throw new OneLiteException(ResultCode.IoErr, "disk I/O error");
            }

            var row = new Row { F = new Mem[_w], Group = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(pos + 4)) };
            for (int c = 0; c < _w; c++)
            {
                _view.Load(c, ref row.F[c]);
            }

            rows[idx++] = row;
            pos += 12 + len;
        }

        if (idx != blk.Count)
        {
            throw new OneLiteException(ResultCode.IoErr, "disk I/O error");
        }

        blk.Rows = rows;
        blk.Bytes = data.Length + (long)(idx - blk.Lo) * 24 * _w;
        blk.Touched = ++_tick;
        _res.Add(blk);
        _resBytes += blk.Bytes;
    }
}
