namespace RecursiveAI.OneLite.Storage.BTree;

/// <summary>Where a walk along one cell's overflow chain stopped, so that the next call in a sequential read or write does not start from the first page again.</summary>
public struct OverflowHint
{
    internal uint Leaf;
    internal int CellOffset;
    internal uint First;
    internal long Index;
    internal uint Pgno;
}

public sealed partial class BtCursor
{
    /// <summary>
    /// Reads bytes of the current entry's payload without loading the rest of it (incremental blob I/O, fileformat2 §1.7: a payload is its local part,
    /// then one chain of overflow pages each holding usable-4 bytes after a 4-byte next pointer). Suspend point: one overflow page. Pass the same
    /// arguments again after <see cref="BtStatus.Pending"/>.
    /// </summary>
    /// <param name="offset">First payload byte to read.</param>
    /// <param name="destination">Receives the bytes; <c>offset + length</c> must not pass <see cref="PayloadSize"/>.</param>
    /// <param name="hint">Carries the chain position between calls on the same entry; start with a default value.</param>
    /// <returns>The status.</returns>
    public BtStatus ReadPayloadRange(long offset, Span<byte> destination, ref OverflowHint hint) => Range(offset, destination, default, false, ref hint);

    /// <summary>
    /// Overwrites bytes of the current entry's payload in place; the entry's size and structure do not change. The pages touched are made writable
    /// (journaled) first. Other arguments and re-entry as for <see cref="ReadPayloadRange"/>.
    /// </summary>
    /// <param name="offset">First payload byte to overwrite.</param>
    /// <param name="source">The new bytes.</param>
    /// <param name="hint">Carries the chain position between calls on the same entry.</param>
    /// <returns>The status.</returns>
    public BtStatus WritePayloadRange(long offset, ReadOnlySpan<byte> source, ref OverflowHint hint) => Range(offset, default, source, true, ref hint);

    private BtStatus Range(long offset, Span<byte> dst, ReadOnlySpan<byte> src, bool write, ref OverflowHint hint)
    {
        try
        {
            CellInfo ci = CurrentCell(out PageHandle pg, out int off);
            int len = write ? src.Length : dst.Length;
            if (offset < 0 || len < 0 || offset + len > ci.PayloadSize)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "the range is not inside the payload");
            }

            if (len == 0)
            {
                return BtStatus.Ok;
            }

            int done = 0;
            long pos = offset;
            if (pos < ci.Local)
            {
                if (write)
                {
                    BtStatus w = Writable(pg);
                    if (w != BtStatus.Ok)
                    {
                        return w;
                    }
                }

                int n = (int)Math.Min(len, ci.Local - pos);
                int at = ci.PayloadOff + (int)pos;
                if (write)
                {
                    src.Slice(0, n).CopyTo(pg.Data.AsSpan(at, n));
                }
                else
                {
                    pg.Data.AsSpan(at, n).CopyTo(dst);
                }

                done = n;
                pos += n;
            }

            if (done < len)
            {
                int per = _ctx.Usable - 4;
                long rel = pos - ci.Local;
                long idx = rel / per;
                int within = (int)(rel % per);
                if (hint.Leaf != pg.Number || hint.CellOffset != off || hint.First != ci.Overflow || hint.Index > idx)
                {
                    hint = new OverflowHint { Leaf = pg.Number, CellOffset = off, First = ci.Overflow, Index = 0, Pgno = ci.Overflow };
                }

                while (done < len)
                {
                    // Walk to chain page idx, remembering how far we got.
                    while (hint.Index < idx)
                    {
                        BtStatus g = _src.GetPage(hint.Pgno, out PageHandle? hp);
                        if (g == BtStatus.Error)
                        {
                            throw new SourceFailure(_src.LastError!);
                        }

                        if (g == BtStatus.Pending)
                        {
                            return g;
                        }

                        uint next = Pg.U32(hp!.Data, 0);
                        _src.Release(hp);
                        if (next == 0)
                        {
                            throw OneLiteException.Corrupt(hint.Pgno, "overflow chain of a cell ends early");
                        }

                        hint.Pgno = next;
                        hint.Index++;
                    }

                    if (hint.Pgno == 0)
                    {
                        throw OneLiteException.Corrupt(pg.Number, "overflow chain of a cell ends early");
                    }

                    BtStatus s = _src.GetPage(hint.Pgno, out PageHandle? op);
                    if (s == BtStatus.Error)
                    {
                        throw new SourceFailure(_src.LastError!);
                    }

                    if (s == BtStatus.Pending)
                    {
                        return s;
                    }

                    try
                    {
                        if (write)
                        {
                            BtStatus w = Writable(op!);
                            if (w != BtStatus.Ok)
                            {
                                return w;
                            }
                        }

                        int n = Math.Min(len - done, per - within);
                        if (write)
                        {
                            src.Slice(done, n).CopyTo(op!.Data.AsSpan(4 + within, n));
                        }
                        else
                        {
                            op!.Data.AsSpan(4 + within, n).CopyTo(dst.Slice(done, n));
                        }

                        done += n;
                        within = 0;
                        if (done < len)
                        {
                            hint.Pgno = Pg.U32(op.Data, 0);
                            hint.Index++;
                            idx++;
                        }
                    }
                    finally
                    {
                        _src.Release(op!);
                    }
                }
            }

            return BtStatus.Ok;
        }
        catch (Exception e) when (e is OneLiteException or SourceFailure)
        {
            return Fail(e);
        }
    }
}
