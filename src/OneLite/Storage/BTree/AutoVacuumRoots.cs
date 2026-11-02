using RecursiveAI.OneLite.Storage.Pager;

namespace RecursiveAI.OneLite.Storage.BTree;

/// <summary>
/// Root pages in an auto-vacuum database (fileformat2 §1.3.11 and §1.7). Every root page lies below every other page, so the roots are the
/// pages 3, 4, 5, ... (pointer-map pages skipped) and header offset 52 names the largest. That keeps the commit-time compaction from ever
/// having to move a root. A new tree therefore takes exactly page <c>largest + 1</c>, and dropping a tree moves the largest root's content
/// into the dropped root's slot. Both are reserve-then-effect and restartable: after Pending the same call is repeated, and nothing has
/// changed before the last reserve call has returned Ok.
/// </summary>
internal static class AutoVacuumRoots
{
    /// <summary>The pages a restartable step keeps pinned across a Pending, so that the repeated call does not have to read them again.</summary>
    internal sealed class Pins
    {
        internal PageHandle? A;
        internal PageHandle? B;

        internal void ReleaseAll(IBtPageSource src)
        {
            if (A != null)
            {
                src.Release(A);
                A = null;
            }

            if (B != null)
            {
                src.Release(B);
                B = null;
            }
        }
    }

    /// <summary>The first page after <paramref name="page"/> that is not a pointer-map page.</summary>
    internal static uint Next(IBtPageSource src, uint page)
    {
        uint n = page + 1;
        while (src.IsPtrmapPage(n))
        {
            n++;
        }

        return n;
    }

    /// <summary>The last page before <paramref name="page"/> that is not a pointer-map page (page 1 at the lowest).</summary>
    internal static uint Prev(IBtPageSource src, uint page)
    {
        uint n = page - 1;
        while (n > 1 && src.IsPtrmapPage(n))
        {
            n--;
        }

        return n;
    }

    /// <summary>The page numbers a B-tree page or overflow page points to, with the pointer-map type each one has.</summary>
    /// <param name="ctx">The context.</param>
    /// <param name="pgno">The page's number.</param>
    /// <param name="d">The page's bytes.</param>
    /// <param name="overflowPage">True for an overflow page (its only reference is the next page of the chain).</param>
    /// <param name="refs">Receives (page, type) pairs.</param>
    internal static void CollectRefs(BtContext ctx, uint pgno, byte[] d, bool overflowPage, List<(uint Page, byte Type)> refs)
    {
        IBtPageSource src = ctx.Source;
        if (overflowPage)
        {
            uint next = Pg.U32(d, 0);
            if (next != 0)
            {
                Add(src, pgno, next, 4, refs);
            }

            return;
        }

        int h = Pg.HdrOff(pgno);
        byte t = d[h];
        if (!Pg.ValidType(t) || h + Pg.HeaderSize(t) + (2 * Pg.NCell(d, h)) > ctx.Usable)
        {
            throw OneLiteException.Corrupt(pgno, "a page that has to move is not a valid B-tree page");
        }

        int n = Pg.NCell(d, h);
        bool leaf = Pg.IsLeaf(t);
        for (int i = 0; i < n; i++)
        {
            int off = Pg.CellOffset(d, h, t, i);
            if (off < h + Pg.HeaderSize(t) + (2 * n) || off > ctx.Usable - 4)
            {
                throw OneLiteException.Corrupt(pgno, $"cell {i} offset {off} is outside the page");
            }

            CellInfo ci = ctx.ParseCell(pgno, t, d, off, ctx.Usable);
            if (!leaf)
            {
                Add(src, pgno, ci.Child, 5, refs);
            }

            if (ci.Overflow != 0)
            {
                Add(src, pgno, ci.Overflow, 3, refs);
            }
        }

        if (!leaf)
        {
            Add(src, pgno, Pg.RightPtr(d, h), 5, refs);
        }
    }

    private static void Add(IBtPageSource src, uint from, uint page, byte type, List<(uint Page, byte Type)> refs)
    {
        if (page < 3 || page > src.PageCount || src.IsPtrmapPage(page))
        {
            throw OneLiteException.Corrupt(from, $"page {from} refers to page {page}, which cannot be a B-tree or overflow page");
        }

        refs.Add((page, type));
    }

    /// <summary>Reserves every pointer-map page of the file, for operations that touch the entries of pages they cannot name yet.</summary>
    internal static BtStatus ReserveAllPtrmap(IBtPageSource src)
    {
        uint perPage = (uint)(src.UsableSize / 5);
        for (long mapPage = 2; mapPage + 1 <= src.PageCount; mapPage += perPage + 1)
        {
            BtStatus s = src.ReservePtrmap((uint)mapPage + 1);
            if (s != BtStatus.Ok)
            {
                return s;
            }
        }

        return BtStatus.Ok;
    }

    private static BtStatus Check(IBtPageSource src, BtStatus s)
    {
        if (s == BtStatus.Error)
        {
            throw src.LastError is OneLiteException c ? c : OneLiteException.Corrupt(0, "page source failed: " + src.LastError?.Message);
        }

        return s;
    }

    private static BtStatus Get(IBtPageSource src, uint pgno, out PageHandle? p) => Check(src, src.GetPage(pgno, out p));

    /// <summary>Saves the position of every open cursor (a page that moves may be on its pinned path). Idempotent.</summary>
    private static BtStatus SaveCursors(BtContext ctx)
    {
        List<BtCursor> all = ctx.Cursors;
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
                    return ls;
                }

                if (ls == BtStatus.Error)
                {
                    c.Invalidate();
                    continue;
                }
            }

            c.Save();
        }

        return BtStatus.Ok;
    }

    /// <summary>Creates an empty tree whose root is page <c>largest + 1</c>.</summary>
    internal static BtStatus Create(BtContext ctx, bool intKey, out uint root)
    {
        root = 0;
        IBtPageSource src = ctx.Source;
        // A new file only gets its page 1 (and so its header fields) with the first write; this call makes sure the header is there to read.
        // It is skipped otherwise, so that a repeated call after Pending makes as few different requests as possible.
        if (src.PageCount == 0)
        {
            BtStatus r = src.Reserve(0, 0);
            if (r != BtStatus.Ok)
            {
                return r;
            }
        }

        uint largest = src.LargestRootPage;
        uint count = src.PageCount;
        if (largest < 1 || largest > count)
        {
            throw OneLiteException.Corrupt(1, $"the header's largest root page {largest} is outside the database ({count} pages)");
        }

        uint t = Next(src, largest);
        byte leafType = intKey ? Pg.TableLeaf : Pg.IndexLeaf;
        BtStatus s;
        if (t > count)
        {
            // The file grows: the new page is the next one after the end (skipping the pointer-map pages the pager creates on the way).
            s = src.Reserve(1, 0);
            if (s != BtStatus.Ok)
            {
                return s;
            }

            PageHandle np = src.Allocate();
            if (np.Number != t)
            {
                uint got = np.Number;
                src.Release(np);
                throw OneLiteException.Corrupt(got, $"a new root page should be page {t} but the file grew to page {got}");
            }

            Format(src, np, ctx, leafType, t);
            root = t;
            return BtStatus.Ok;
        }

        s = src.ReservePtrmap(t);
        if (s != BtStatus.Ok)
        {
            return s;
        }

        s = Check(src, src.PtrmapGet(t, out byte type, out uint parent));
        if (s != BtStatus.Ok)
        {
            return s;
        }

        if (type == 2)
        {
            s = TakeFreePage(ctx, t, leafType);
        }
        else
        {
            s = MoveAway(ctx, t, type, parent, leafType);
        }

        if (s == BtStatus.Ok)
        {
            root = t;
        }

        return s;
    }

    private static void Format(IBtPageSource src, PageHandle p, BtContext ctx, byte leafType, uint root)
    {
        Pg.Zero(p.Data, 0, ctx.Usable, leafType);
        src.Release(p);
        src.PtrmapPut(root, 1, 0);
        src.LargestRootPage = root;
    }

    // Page t is on the freelist. The freelist can only give pages up one at a time, so take pages until t comes (the others go back).
    private static BtStatus TakeFreePage(BtContext ctx, uint t, byte leafType)
    {
        IBtPageSource src = ctx.Source;
        BtStatus s = Get(src, 1, out PageHandle? p1);
        if (s != BtStatus.Ok)
        {
            return s;
        }

        uint listed = Pg.U32(p1!.Data, 36);
        src.Release(p1);
        long n = Math.Min(listed, src.PageCount);
        if (n == 0)
        {
            throw OneLiteException.Corrupt(t, $"page {t} is marked free in the pointer map but the freelist is empty");
        }

        s = ReserveAllPtrmap(src);
        if (s != BtStatus.Ok)
        {
            return s;
        }

        s = src.Reserve((int)n, (int)n);
        if (s != BtStatus.Ok)
        {
            return s;
        }

        uint count = src.PageCount;
        var others = new List<PageHandle>();
        PageHandle? found = null;
        for (long i = 0; i < n && found == null; i++)
        {
            PageHandle p = src.Allocate();
            if (p.Number == t)
            {
                found = p;
            }
            else if (p.Number > count)
            {
                uint got = p.Number;
                src.Release(p);
                foreach (PageHandle o in others)
                {
                    src.Release(o);
                }

                throw OneLiteException.Corrupt(t, $"page {t} is marked free in the pointer map but is not on the freelist (the file grew to page {got})");
            }
            else
            {
                others.Add(p);
            }
        }

        if (found == null)
        {
            foreach (PageHandle o in others)
            {
                src.Release(o);
            }

            throw OneLiteException.Corrupt(t, $"page {t} is marked free in the pointer map but is not on the freelist");
        }

        foreach (PageHandle o in others)
        {
            uint no = o.Number;
            src.Release(o);
            src.Free(no);
        }

        Format(src, found, ctx, leafType, t);
        return BtStatus.Ok;
    }

    // Page t is in use (a B-tree page or an overflow page). Copy it to a new page, repoint its parent, and give t to the new tree.
    private static BtStatus MoveAway(BtContext ctx, uint t, byte type, uint parent, byte leafType)
    {
        IBtPageSource src = ctx.Source;
        if (type is < 3 or > 5)
        {
            throw OneLiteException.Corrupt(t, $"page {t} follows the largest root page but the pointer map marks it type {type}, not a movable page");
        }

        if (parent < 1 || parent > src.PageCount || parent == t || src.IsPtrmapPage(parent))
        {
            throw OneLiteException.Corrupt(t, $"the pointer map gives page {parent} as the parent of page {t}");
        }

        BtStatus s = SaveCursors(ctx);
        if (s != BtStatus.Ok)
        {
            return s;
        }

        Pins pins = ctx.AvPins;
        try
        {
            if (pins.A == null)
            {
                s = Get(src, t, out PageHandle? got);
                if (s != BtStatus.Ok)
                {
                    return s;
                }

                pins.A = got;
            }

            if (pins.B == null)
            {
                s = Get(src, parent, out PageHandle? got);
                if (s != BtStatus.Ok)
                {
                    return s;
                }

                pins.B = got;
            }

            PageHandle tp = pins.A!;
            PageHandle pp = pins.B!;
            var refs = new List<(uint Page, byte Type)>();
            CollectRefs(ctx, t, tp.Data, type != 5, refs);
            s = ReserveAllPtrmap(src);
            if (s != BtStatus.Ok)
            {
                return s;
            }

            s = src.Reserve(1, 0);
            if (s != BtStatus.Ok)
            {
                return s;
            }

            s = Check(src, src.MakeWritable(pp));
            if (s != BtStatus.Ok)
            {
                return s;
            }

            s = Check(src, src.MakeWritable(tp));
            if (s != BtStatus.Ok)
            {
                return s;
            }

            // Effect: no further I/O.
            PageHandle x = src.Allocate();
            uint xn = x.Number;
            if (xn == t)
            {
                src.Release(x);
                throw OneLiteException.Corrupt(t, $"page {t} is marked in use in the pointer map but was handed out as a free page");
            }

            Array.Copy(tp.Data, x.Data, tp.Data.Length);
            src.Release(x);
            BtRelocator.PatchParent(ctx, pp.Data, parent, t, xn, (PtrmapType)type);
            src.PtrmapPut(xn, type, parent);
            foreach ((uint page, byte rtype) in refs)
            {
                src.PtrmapPut(page, rtype, xn);
            }

            pins.A = null;
            Format(src, tp, ctx, leafType, t);
            pins.ReleaseAll(src);
            return BtStatus.Ok;
        }
        catch
        {
            pins.ReleaseAll(src);
            throw;
        }
    }

    /// <summary>
    /// Dropping the tree at <paramref name="r"/> in an auto-vacuum database whose largest root page is higher: the emptied page
    /// <paramref name="r"/> takes over the largest root's content, and the largest root's old page is freed.
    /// </summary>
    internal static BtStatus MoveLargestInto(BtContext ctx, uint r, out uint movedFrom)
    {
        movedFrom = 0;
        IBtPageSource src = ctx.Source;
        uint l = src.LargestRootPage;
        if (l <= r || l > src.PageCount || src.IsPtrmapPage(l))
        {
            throw OneLiteException.Corrupt(l, $"the header's largest root page {l} cannot be moved into root page {r}");
        }

        foreach (BtCursor c in ctx.Cursors)
        {
            if (c.Tree.Root == l || c.Tree.Root == r)
            {
                c.Invalidate();
            }
        }

        BtStatus s = src.ReservePtrmap(l);
        if (s != BtStatus.Ok)
        {
            return s;
        }

        s = Check(src, src.PtrmapGet(l, out byte type, out _));
        if (s != BtStatus.Ok)
        {
            return s;
        }

        if (type != 1)
        {
            throw OneLiteException.Corrupt(l, $"page {l} is the header's largest root page but the pointer map marks it type {type}, not a root");
        }

        Pins pins = ctx.AvPins;
        try
        {
            if (pins.A == null)
            {
                s = Get(src, l, out PageHandle? got);
                if (s != BtStatus.Ok)
                {
                    return s;
                }

                pins.A = got;
            }

            PageHandle lp = pins.A!;
            var refs = new List<(uint Page, byte Type)>();
            CollectRefs(ctx, l, lp.Data, false, refs);
            foreach ((uint page, _) in refs)
            {
                s = src.ReservePtrmap(page);
                if (s != BtStatus.Ok)
                {
                    return s;
                }
            }

            s = src.Reserve(0, 1);
            if (s != BtStatus.Ok)
            {
                return s;
            }

            if (pins.B == null)
            {
                s = Get(src, r, out PageHandle? got);
                if (s != BtStatus.Ok)
                {
                    return s;
                }

                pins.B = got;
            }

            PageHandle rp = pins.B!;
            s = Check(src, src.MakeWritable(rp));
            if (s != BtStatus.Ok)
            {
                return s;
            }

            // Effect: no further I/O.
            Array.Copy(lp.Data, rp.Data, lp.Data.Length);
            foreach ((uint page, byte rtype) in refs)
            {
                src.PtrmapPut(page, rtype, r);
            }

            pins.ReleaseAll(src);
            src.Free(l);
            src.LargestRootPage = Prev(src, l);
            movedFrom = l;
            return BtStatus.Ok;
        }
        catch
        {
            pins.ReleaseAll(src);
            throw;
        }
    }
}
