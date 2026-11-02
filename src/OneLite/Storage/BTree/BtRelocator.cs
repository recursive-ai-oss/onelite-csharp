using RecursiveAI.OneLite.Storage.Pager;
using PagerClass = RecursiveAI.OneLite.Storage.Pager.Pager;

namespace RecursiveAI.OneLite.Storage.BTree;

/// <summary>
/// The B-tree side of auto-vacuum relocation (fileformat2 §1.7). When a commit moves a page from the end of the file into a free page, the
/// pager copies the bytes and this class repairs everything that referred to the old page number: the parent's child pointer (or the cell's
/// first-overflow-page number, or the previous overflow page's next-page number) and the pointer-map entries of whatever the moved page
/// points to. Install it with <see cref="PagerClass.SetRelocator"/>. Every step is idempotent and progress is kept here, so a
/// <see cref="PagerStatus.Pending"/> from any pager call just means "call again with the same arguments".
/// Cursors open on the tree are saved first, because their pinned path may include a page that moves.
/// </summary>
public sealed class BtRelocator : IPageRelocator
{
    private readonly BtContext _ctx;
    private int _step;
    private int _cell;
    private int _sub;
    private PageFrame? _parentFrame;
    private PageFrame? _movedFrame;

    /// <summary>Creates the relocator.</summary>
    /// <param name="ctx">The B-tree context of the database (page geometry and cell decoding).</param>
    public BtRelocator(BtContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _ctx = ctx;
    }

    /// <inheritdoc/>
    public PagerStatus Relocate(PagerClass pager, uint oldPage, uint newPage, PtrmapType type, uint parent)
    {
        try
        {
            PagerStatus s = Run(pager, oldPage, newPage, type, parent);
            if (s != PagerStatus.Pending)
            {
                Reset(pager);
            }

            return s;
        }
        catch (OneLiteException e)
        {
            Reset(pager);
            return pager.FailWith(e);
        }
    }

    private void Reset(PagerClass pager)
    {
        if (_parentFrame != null)
        {
            pager.Release(_parentFrame);
            _parentFrame = null;
        }

        if (_movedFrame != null)
        {
            pager.Release(_movedFrame);
            _movedFrame = null;
        }

        _step = 0;
        _cell = 0;
        _sub = 0;
    }

    private PagerStatus Run(PagerClass pager, uint oldPage, uint newPage, PtrmapType type, uint parent)
    {
        if (type is not (PtrmapType.BTree or PtrmapType.Overflow1 or PtrmapType.Overflow2) || parent < 1)
        {
            throw OneLiteException.Corrupt(oldPage, $"pointer-map type {(int)type} with parent {parent} cannot be relocated");
        }

        if (_step == 0)
        {
            // A saved cursor re-seeks by key, so it does not care which page numbers its old path had.
            List<BtCursor> all = _ctx.Cursors;
            for (int i = 0; i < all.Count; i++)
            {
                BtCursor c = all[i];
                if (c.State != CursorState.Valid)
                {
                    continue;
                }

                if (c.NeedsPayloadForSave)
                {
                    BtStatus ls = c.LoadPayload();
                    if (ls == BtStatus.Pending)
                    {
                        return PagerStatus.Pending;
                    }

                    if (ls == BtStatus.Error)
                    {
                        c.State = CursorState.Invalid;
                        continue;
                    }
                }

                c.Save();
            }

            _step = 1;
        }

        PagerStatus s;
        if (_step == 1)
        {
            s = pager.Get(parent, GetOptions.None, out PageFrame? pf);
            if (s != PagerStatus.Ok)
            {
                return s;
            }

            _parentFrame = pf;
            _step = 2;
        }

        if (_step == 2)
        {
            s = pager.MakeWritable(_parentFrame!);
            if (s != PagerStatus.Ok)
            {
                return s;
            }

            PatchParent(_ctx, _parentFrame!.Data, _parentFrame.PageNumber, oldPage, newPage, type);
            pager.Release(_parentFrame!);
            _parentFrame = null;
            _step = 3;
        }

        if (_step == 3)
        {
            s = pager.Get(newPage, GetOptions.None, out PageFrame? nf);
            if (s != PagerStatus.Ok)
            {
                return s;
            }

            _movedFrame = nf;
            _step = 4;
        }

        // The moved page's own references: their pointer-map entries must now name newPage as the parent.
        byte[] d = _movedFrame!.Data;
        if (type == PtrmapType.BTree)
        {
            int h = Pg.HdrOff(newPage);
            byte t = d[h];
            if (!Pg.ValidType(t) || h + Pg.HeaderSize(t) + (2 * Pg.NCell(d, h)) > _ctx.Usable)
            {
                throw OneLiteException.Corrupt(newPage, "a moved page is not a valid B-tree page");
            }

            int n = Pg.NCell(d, h);
            for (; _cell <= n; _cell++, _sub = 0)
            {
                if (_cell == n)
                {
                    if (!Pg.IsLeaf(t))
                    {
                        s = Put(pager, Pg.RightPtr(d, h), PtrmapType.BTree, newPage);
                        if (s != PagerStatus.Ok)
                        {
                            return s;
                        }
                    }

                    break;
                }

                int off = Pg.CellOffset(d, h, t, _cell);
                if (off < h + Pg.HeaderSize(t) + (2 * n) || off > _ctx.Usable - 4)
                {
                    throw OneLiteException.Corrupt(newPage, $"cell {_cell} offset {off} is outside the page");
                }

                CellInfo ci = _ctx.ParseCell(newPage, t, d, off, _ctx.Usable);
                if (_sub == 0)
                {
                    if (!Pg.IsLeaf(t))
                    {
                        s = Put(pager, ci.Child, PtrmapType.BTree, newPage);
                        if (s != PagerStatus.Ok)
                        {
                            return s;
                        }
                    }

                    _sub = 1;
                }

                if (ci.Overflow != 0)
                {
                    s = Put(pager, ci.Overflow, PtrmapType.Overflow1, newPage);
                    if (s != PagerStatus.Ok)
                    {
                        return s;
                    }
                }
            }
        }
        else
        {
            // An overflow page: its first four bytes name the next page of the chain, whose parent is now this page.
            uint next = Pg.U32(d, 0);
            if (next != 0)
            {
                s = Put(pager, next, PtrmapType.Overflow2, newPage);
                if (s != PagerStatus.Ok)
                {
                    return s;
                }
            }
        }

        return PagerStatus.Ok;
    }

    private static PagerStatus Put(PagerClass pager, uint page, PtrmapType type, uint parent)
    {
        if (page < 3 || page > pager.PageCount || page == pager.LockBytePage || pager.IsPtrmapPage(page))
        {
            throw OneLiteException.Corrupt(parent, $"page {parent} refers to page {page}, which cannot have a pointer-map entry");
        }

        return pager.PtrmapPut(page, type, parent);
    }

    // Shared with BtAdmin (moving a page to make room for a new root): rewrites the one reference to oldPage in the page the pointer map names as its parent.
    internal static void PatchParent(BtContext ctx, byte[] d, uint pg, uint oldPage, uint newPage, PtrmapType type)
    {
        if (type == PtrmapType.Overflow2)
        {
            if (Pg.U32(d, 0) != oldPage)
            {
                throw OneLiteException.Corrupt(pg, $"overflow page does not continue to page {oldPage}");
            }

            Pg.PutU32(d, 0, newPage);
            return;
        }

        int h = Pg.HdrOff(pg);
        byte t = d[h];
        if (!Pg.ValidType(t) || h + Pg.HeaderSize(t) + (2 * Pg.NCell(d, h)) > ctx.Usable)
        {
            throw OneLiteException.Corrupt(pg, "the parent of a moved page is not a valid B-tree page");
        }

        int n = Pg.NCell(d, h);
        for (int i = 0; i < n; i++)
        {
            int off = Pg.CellOffset(d, h, t, i);
            if (off < h + Pg.HeaderSize(t) + (2 * n) || off > ctx.Usable - 4)
            {
                throw OneLiteException.Corrupt(pg, $"cell {i} offset {off} is outside the page");
            }

            if (type == PtrmapType.BTree)
            {
                if (!Pg.IsLeaf(t) && Pg.U32(d, off) == oldPage)
                {
                    Pg.PutU32(d, off, newPage);
                    return;
                }
            }
            else
            {
                CellInfo ci = ctx.ParseCell(pg, t, d, off, ctx.Usable);
                if (ci.Overflow == oldPage)
                {
                    Pg.PutU32(d, ci.PayloadOff + ci.Local, newPage);
                    return;
                }
            }
        }

        if (type == PtrmapType.BTree && !Pg.IsLeaf(t) && Pg.RightPtr(d, h) == oldPage)
        {
            Pg.PutU32(d, h + 8, newPage);
            return;
        }

        throw OneLiteException.Corrupt(pg, $"no pointer to page {oldPage} found in the page the pointer map names as its parent");
    }
}
