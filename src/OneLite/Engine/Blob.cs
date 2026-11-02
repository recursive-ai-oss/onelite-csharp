using System.Linq;
using RecursiveAI.OneLite.Planning;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Vm;

namespace RecursiveAI.OneLite;

/// <summary>
/// Incremental blob I/O (c3ref blob_open): reads and overwrites the bytes of one BLOB or TEXT value in place, without loading the row. The handle
/// holds a read transaction on the database for as long as it is open (a writable handle holds the write transaction), as the reference's does, so
/// DROP TABLE and COMMIT on the connection are refused while it is open and other connections cannot write while a rollback-journal read is held.
/// <para>
/// The size is fixed: writes cannot grow or shrink the value. The handle expires (every call throws SQLITE_ABORT, "query aborted") as soon as the
/// row it points at is changed or deleted through the connection, or when its table is emptied; a ROLLBACK aborts it with SQLITE_ABORT_ROLLBACK.
/// An expired handle stays expired, <see cref="Reopen(long)"/> included. Other rows of the table can change freely.
/// </para>
/// <para>
/// A writable handle opened while the connection is in autocommit mode starts a transaction (BEGIN IMMEDIATE) that <see cref="Close"/> commits; until
/// then the connection reports <see cref="Connection.AutoCommit"/> false. Offsets and lengths count bytes as stored: a TEXT value in a UTF-16
/// database counts two bytes per code unit.
/// </para>
/// </summary>
public sealed class Blob : IDisposable
{
    private readonly Connection _c;
    private readonly int _slot;
    private readonly string _schema;
    private readonly string _table;
    private readonly bool _write;
    private readonly uint _root;
    private readonly int _recordIndex;
    private readonly Pager _pager;
    private readonly BtCursor _cur;
    private Statement? _hold;
    private bool _ownTx;
    private long _rowid;
    private long _len;
    private long _dataOffset;
    private int _abort;
    private bool _closed;
    private OverflowHint _hint;

    private Blob(Connection c, int slot, string schema, string table, bool write, uint root, int recordIndex)
    {
        _c = c;
        _slot = slot;
        _schema = schema;
        _table = table;
        _write = write;
        _root = root;
        _recordIndex = recordIndex;
        _pager = c.PagerOf(slot);
        _cur = BTree.Table(c.ContextOf(slot), root).OpenCursor();
    }

    private static OneLiteException Err(string message) => new(ResultCode.Error, message);

    private static string Quote(string s) => "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>Opens a handle on one column of one row (sqlite3_blob_open).</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="schema">"main", "temp" or an attached name.</param>
    /// <param name="table">The table: a rowid table, not a view, virtual table or WITHOUT ROWID table, and without generated columns.</param>
    /// <param name="column">The column. Its value in the row must be TEXT or a BLOB.</param>
    /// <param name="rowid">The row.</param>
    /// <param name="writable">True to allow <see cref="Write"/>; the column must then be in no index and no foreign key.</param>
    /// <returns>The open handle.</returns>
    /// <exception cref="OneLiteException">SQLITE_ERROR with the reference's text: "no such table: S.T", "no such column: \"C\"", "no such rowid: N", "cannot open value of type integer", "cannot open view: V", "cannot open table without rowid: T", "cannot open table with generated columns: T", "cannot open indexed column for writing", "cannot open foreign key column for writing"; SQLITE_BUSY if a lock is held elsewhere.</exception>
    public static Blob Open(Connection connection, string schema, string table, string column, long rowid, bool writable = false)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(column);
        connection.EnsureCatalogSync();
        Catalog cat = connection.Catalog!;
        int slot = cat.SlotOf(schema);
        TableInfo? t = slot < 0 ? null : cat.DictOf(slot).GetValueOrDefault(table);
        if (t is null)
        {
            throw Err("no such table: " + schema + "." + table);
        }

        if (t.View is not null)
        {
            throw Err("cannot open view: " + table);
        }

        if (t.Root == 0)
        {
            throw Err("cannot open virtual table: " + table);
        }

        if (t.WithoutRowid)
        {
            throw Err("cannot open table without rowid: " + table);
        }

        if (Array.Exists(t.Columns, static x => x.Generated != GeneratedKind.None))
        {
            throw Err("cannot open table with generated columns: " + table);
        }

        int ci = Array.FindIndex(t.Columns, x => AsciiComparer.Instance.Equals(x.Name, column));
        if (ci < 0)
        {
            throw Err("no such column: \"" + column + "\"");
        }

        if (writable)
        {
            foreach (IndexInfo ix in t.Indexes.Concat(t.AutoIndexes))
            {
                if (Array.IndexOf(ix.Columns, ci) >= 0)
                {
                    throw Err("cannot open indexed column for writing");
                }
            }

            foreach (FkInfo fk in t.ForeignKeys)
            {
                if (Array.IndexOf(fk.Child, ci) >= 0)
                {
                    throw Err("cannot open foreign key column for writing");
                }
            }
        }

        var b = new Blob(connection, slot, schema, table, writable, t.Root, t.Columns[ci].RecordIndex);
        try
        {
            b.Begin(rowid);
        }
        catch
        {
            b.Release(rollback: true);
            throw;
        }

        connection.Blobs.Add(b);
        return b;
    }

    // Holds the transactions, then finds the row.
    private void Begin(long rowid)
    {
        if (_write && _c.AutoCommit)
        {
            Exec("BEGIN IMMEDIATE");
            _ownTx = true;
        }

        _hold = _c.Prepare("SELECT 1 FROM " + Quote(_schema) + "." + Quote(_table) + " WHERE rowid = ?1");
        _hold.Bind(1, rowid);
        if (!_hold.Step())
        {
            throw Err("no such rowid: " + rowid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (_write && !_pager.InWriteTransaction)
        {
            PagerStatus s;
            while ((s = _c.BeginWriteStep()) == PagerStatus.Pending)
            {
                _pager.ExecuteSync();
            }

            if (s == PagerStatus.Error)
            {
                throw _pager.Error!;
            }
        }

        Locate(rowid);
    }

    private void Exec(string sql)
    {
        using Statement s = _c.Prepare(sql);
        while (s.Step())
        {
        }
    }

    private void RunSync(Func<BtStatus> op)
    {
        BtStatus s;
        while ((s = op()) == BtStatus.Pending)
        {
            _pager.ExecuteSync();
        }

        if (s == BtStatus.Error)
        {
            throw Machine.Translate(_cur.Error!);
        }
    }

    // Finds the row, checks the value's type, and works out where its bytes begin (fileformat2 §2.1: the record header lists a serial type per column).
    private void Locate(long rowid)
    {
        try
        {
            LocateCore(rowid);
        }
        finally
        {
            _cur.Reset();
        }
    }

    private void LocateCore(long rowid)
    {
        int cmp = 0;
        RunSync(() => _cur.MoveToRowid(rowid, out cmp));
        if (cmp != 0 || _cur.State != CursorState.Valid)
        {
            throw Err("no such rowid: " + rowid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        long total = _cur.PayloadSize;
        _hint = default;
        var first = new byte[(int)Math.Min(total, 9)];
        RunSync(() => _cur.ReadPayloadRange(0, first, ref _hint));
        int n = Varint.Read(first, out ulong hs);
        if (n == 0 || hs < (ulong)n || hs > (ulong)total || hs > 64 * 1024 * 1024)
        {
            throw OneLiteException.Corrupt(_root, "record header size is outside the payload");
        }

        var hdr = new byte[(int)hs];
        RunSync(() => _cur.ReadPayloadRange(0, hdr, ref _hint));
        int p = n;
        long data = (long)hs;
        for (int col = 0; ; col++)
        {
            if (p >= hdr.Length)
            {
                // fewer columns stored than the table has: the value is the column's default, here read as NULL
                if (col <= _recordIndex)
                {
                    throw Err("cannot open value of type null");
                }

                break;
            }

            int m = Varint.Read(hdr.AsSpan(p), out ulong st);
            if (m == 0)
            {
                throw OneLiteException.Corrupt(_root, "record header ends inside a serial type");
            }

            p += m;
            // fileformat2 §2.1: serial types 0-9 are fixed (10 and 11 reserved); from 12, even types are BLOBs and odd types TEXT of (N-12)/2 or (N-13)/2 bytes
            long size = st switch { 0 or 8 or 9 => 0, 1 => 1, 2 => 2, 3 => 3, 4 => 4, 5 => 6, 6 or 7 => 8, 10 or 11 => -1, _ => (long)((st - 12) / 2) };

            if (size < 0)
            {
                throw OneLiteException.Corrupt(_root, "serial type " + st + " is reserved");
            }

            if (col == _recordIndex)
            {
                if (st < 12)
                {
                    throw Err("cannot open value of type " + (st == 0 ? "null" : st == 7 ? "real" : "integer"));
                }

                _dataOffset = data;
                _len = size;
                if (_dataOffset + _len > total)
                {
                    throw OneLiteException.Corrupt(_root, "a column runs past the end of its record");
                }

                break;
            }

            data += size;
        }

        _rowid = rowid;
    }

    /// <summary>The size of the value in bytes (sqlite3_blob_bytes); 0 once the handle has expired because its row changed.</summary>
    public int Length => _abort == ResultCode.Abort ? 0 : (int)_len;

    /// <summary>The row this handle points at.</summary>
    public long Rowid => _rowid;

    /// <summary>True when writes are allowed.</summary>
    public bool CanWrite => _write;

    internal int Slot => _slot;

    internal uint Root => _root;

    internal void Expire(int code)
    {
        if (_abort == 0)
        {
            _abort = code;
        }
    }

    private void CheckRange(int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_closed, this);

        if (count < 0 || offset < 0 || (long)offset + count > _len)
        {
            throw Err("SQL logic error");
        }

        if (_abort != 0)
        {
            throw new OneLiteException(_abort, _abort == ResultCode.AbortRollback ? "abort due to ROLLBACK" : "query aborted");
        }
    }

    private void Check(BtStatus s)
    {
        if (s == BtStatus.Error)
        {
            throw Machine.Translate(_cur.Error!);
        }
    }

    private void Position()
    {
        int cmp = 0;
        RunSync(() => _cur.MoveToRowid(_rowid, out cmp));
        if (cmp != 0 || _cur.State != CursorState.Valid)
        {
            _abort = ResultCode.Abort;
            throw new OneLiteException(ResultCode.Abort, "query aborted");
        }
    }

    /// <summary>Copies <paramref name="destination"/>.Length bytes starting at <paramref name="offset"/> (sqlite3_blob_read).</summary>
    /// <param name="offset">Byte offset in the value.</param>
    /// <param name="destination">Receives the bytes.</param>
    /// <exception cref="OneLiteException">SQLITE_ERROR "SQL logic error" if the range is not inside the value; SQLITE_ABORT "query aborted" if the handle expired; SQLITE_ABORT_ROLLBACK after a ROLLBACK.</exception>
    public void Read(int offset, Span<byte> destination)
    {
        CheckRange(offset, destination.Length);
        if (destination.Length == 0)
        {
            return;
        }

        try
        {
            Position();
            BtStatus s;
            while ((s = _cur.ReadPayloadRange(_dataOffset + offset, destination, ref _hint)) == BtStatus.Pending)
            {
                _pager.ExecuteSync();
            }

            Check(s);
        }
        finally
        {
            _cur.Reset();
        }
    }

    /// <summary>Overwrites bytes of the value, which cannot change size (sqlite3_blob_write).</summary>
    /// <param name="offset">Byte offset in the value.</param>
    /// <param name="source">The new bytes.</param>
    /// <exception cref="OneLiteException">As for <see cref="Read"/>, and SQLITE_READONLY "attempt to write a readonly database" on a read-only handle.</exception>
    public void Write(int offset, ReadOnlySpan<byte> source)
    {
        CheckRange(offset, source.Length);
        if (!_write)
        {
            throw new OneLiteException(ResultCode.ReadOnly, "attempt to write a readonly database");
        }

        if (source.Length == 0)
        {
            return;
        }

        try
        {
            Position();
            BtStatus s;
            while ((s = _cur.WritePayloadRange(_dataOffset + offset, source, ref _hint)) == BtStatus.Pending)
            {
                _pager.ExecuteSync();
            }

            Check(s);
        }
        finally
        {
            _cur.Reset();
        }
    }

    /// <summary>The async twin of <see cref="Read"/>: page reads await the storage backend.</summary>
    /// <param name="offset">Byte offset in the value.</param>
    /// <param name="destination">Receives the bytes.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async ValueTask ReadAsync(int offset, Memory<byte> destination, CancellationToken ct = default)
    {
        CheckRange(offset, destination.Length);
        if (destination.Length == 0)
        {
            return;
        }

        try
        {
            await RunAsync(() => PositionStep(), ct).ConfigureAwait(false);
            await RunAsync(() => _cur.ReadPayloadRange(_dataOffset + offset, destination.Span, ref _hint), ct).ConfigureAwait(false);
        }
        finally
        {
            _cur.Reset();
        }
    }

    /// <summary>The async twin of <see cref="Write"/>.</summary>
    /// <param name="offset">Byte offset in the value.</param>
    /// <param name="source">The new bytes.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async ValueTask WriteAsync(int offset, ReadOnlyMemory<byte> source, CancellationToken ct = default)
    {
        CheckRange(offset, source.Length);
        if (!_write)
        {
            throw new OneLiteException(ResultCode.ReadOnly, "attempt to write a readonly database");
        }

        if (source.Length == 0)
        {
            return;
        }

        try
        {
            await RunAsync(() => PositionStep(), ct).ConfigureAwait(false);
            await RunAsync(() => _cur.WritePayloadRange(_dataOffset + offset, source.Span, ref _hint), ct).ConfigureAwait(false);
        }
        finally
        {
            _cur.Reset();
        }
    }

    private int _asyncCmp;

    private BtStatus PositionStep()
    {
        BtStatus s = _cur.MoveToRowid(_rowid, out _asyncCmp);
        if (s == BtStatus.Ok && (_asyncCmp != 0 || _cur.State != CursorState.Valid))
        {
            _abort = ResultCode.Abort;
            throw new OneLiteException(ResultCode.Abort, "query aborted");
        }

        return s;
    }

    private async ValueTask RunAsync(Func<BtStatus> op, CancellationToken ct)
    {
        BtStatus s;
        while ((s = op()) == BtStatus.Pending)
        {
            await _pager.ExecuteAsync(ct).ConfigureAwait(false);
        }

        if (s == BtStatus.Error)
        {
            throw Machine.Translate(_cur.Error!);
        }
    }

    /// <summary>Points the handle at another row of the same table and column (sqlite3_blob_reopen), cheaper than closing and opening.</summary>
    /// <param name="rowid">The new row.</param>
    /// <exception cref="OneLiteException">SQLITE_ABORT if the handle expired (it cannot be revived); SQLITE_ERROR "no such rowid: N" or "cannot open value of type T"; after such an error the handle is aborted.</exception>
    public void Reopen(long rowid)
    {
        ObjectDisposedException.ThrowIf(_closed, this);

        if (_abort != 0)
        {
            throw new OneLiteException(_abort, _abort == ResultCode.AbortRollback ? "abort due to ROLLBACK" : "query aborted");
        }

        try
        {
            _hold!.Reset();
            _hold.Bind(1, rowid);
            if (!_hold.Step())
            {
                throw Err("no such rowid: " + rowid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            Locate(rowid);
        }
        catch (OneLiteException)
        {
            _abort = ResultCode.Abort;
            throw;
        }
    }

    /// <summary>A stream over the value: seekable, with a fixed length; writable if the handle is. Disposing the stream does not close the handle.</summary>
    /// <returns>The stream.</returns>
    public Stream AsStream() => new BlobStream(this);

    private void Release(bool rollback)
    {
        _cur.Dispose();
        try
        {
            _hold?.Dispose();
        }
        catch (OneLiteException)
        {
            // the statement only held a lock
        }

        _hold = null;
        if (_ownTx)
        {
            _ownTx = false;
            if (_c.AutoCommit)
            {
                return;
            }

            Exec(rollback ? "ROLLBACK" : "COMMIT");
        }
    }

    /// <summary>Closes the handle (sqlite3_blob_close), releasing its locks. A writable handle that started its own transaction commits it.</summary>
    /// <exception cref="OneLiteException">The commit's error, for instance SQLITE_BUSY or SQLITE_IOERR; the transaction then stays open on the connection, as after a failed COMMIT.</exception>
    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _c.Blobs.Remove(this);
        Release(rollback: _abort == ResultCode.AbortRollback);
    }

    /// <summary>Same as <see cref="Close"/>.</summary>
    public void Dispose() => Close();

    private sealed class BlobStream : Stream
    {
        private readonly Blob _b;
        private long _pos;

        internal BlobStream(Blob b) => _b = b;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => _b._write;

        public override long Length => _b.Length;

        public override long Position
        {
            get => _pos;
            set => _pos = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = (int)Math.Min(buffer.Length, Math.Max(0, Length - _pos));
            if (n > 0)
            {
                _b.Read((int)_pos, buffer[..n]);
                _pos += n;
            }

            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int n = (int)Math.Min(buffer.Length, Math.Max(0, Length - _pos));
            if (n > 0)
            {
                await _b.ReadAsync((int)_pos, buffer[..n], cancellationToken).ConfigureAwait(false);
                _pos += n;
            }

            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_pos + buffer.Length > Length)
            {
                throw new NotSupportedException("a blob cannot grow: the write passes the end of the value");
            }

            _b.Write((int)_pos, buffer);
            _pos += buffer.Length;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pos + buffer.Length > Length)
            {
                throw new NotSupportedException("a blob cannot grow: the write passes the end of the value");
            }

            await _b.WriteAsync((int)_pos, buffer, cancellationToken).ConfigureAwait(false);
            _pos += buffer.Length;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin)
        {
            long p = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, _ => Length + offset };
            if (p < 0)
            {
                throw new IOException("seek before the start of the value");
            }

            _pos = p;
            return p;
        }

        public override void SetLength(long value) => throw new NotSupportedException("a blob cannot change size");
    }
}
