namespace RecursiveAI.OneLite.Storage.BTree;

/// <summary>Creating, emptying and dropping whole B-trees (spec §5.5: Btree.Create, Clear.Descend / Clear.FreeLeaf, Btree.Destroy).</summary>
public static class BtAdmin
{
    /// <summary>
    /// Creates an empty tree and returns its root page. One reserve, then the effect; repeat the call after Pending. In an auto-vacuum
    /// database the root is exactly the page after the largest root page (fileformat2 §1.3.11, §1.7): a free page there is taken off the
    /// freelist and a page in use is moved elsewhere first, so pages that already exist can change number; open cursors are saved.
    /// </summary>
    /// <param name="ctx">The context.</param>
    /// <param name="intKey">True for a rowid table (leaf type 13), false for an index or WITHOUT ROWID table (leaf type 10).</param>
    /// <param name="root">The new root page.</param>
    public static BtStatus CreateTree(BtContext ctx, bool intKey, out uint root)
    {
        root = 0;
        IBtPageSource src = ctx.Source;
        if (src.AutoVacuum)
        {
            return AutoVacuumRoots.Create(ctx, intKey, out root);
        }

        BtStatus s = src.Reserve(1, 0);
        if (s == BtStatus.Pending)
        {
            return s;
        }

        if (s == BtStatus.Error)
        {
            return s;
        }

        PageHandle p = src.Allocate();
        Pg.Zero(p.Data, 0, ctx.Usable, intKey ? Pg.TableLeaf : Pg.IndexLeaf);
        root = p.Number;
        src.Release(p);
        return BtStatus.Ok;
    }
}

/// <summary>
/// Empties a tree (keeping its root page) or drops it (freeing the root too). Restartable: all progress is in the object, no page stays
/// pinned across a Pending. Call <see cref="Step"/> until it returns Ok.
/// </summary>
public sealed class TreeEraser
{
    private readonly BtContext _ctx;
    private readonly IBtPageSource _src;
    private readonly BTree _tree;
    private readonly bool _destroy;
    private readonly uint[] _pg = new uint[BtCursor.MaxDepth];
    private readonly int[] _phase = new int[BtCursor.MaxDepth];
    private readonly int[] _i = new int[BtCursor.MaxDepth];
    private int _top = -1;
    private bool _started;
    private uint _ovfNext;
    private int _ovfLeft;
    private long _visited;
    private bool _keepRoot;
    private bool _moveDone;

    /// <summary>Creates an eraser.</summary>
    /// <param name="tree">The tree.</param>
    /// <param name="destroy">True to free the root page as well.</param>
    public TreeEraser(BTree tree, bool destroy)
    {
        _tree = tree;
        _ctx = tree.Context;
        _src = _ctx.Source;
        _destroy = destroy;
    }

    /// <summary>The failure behind <see cref="BtStatus.Error"/>.</summary>
    public Exception? Error { get; private set; }

    /// <summary>
    /// After a finished drop in an auto-vacuum database: the root page whose tree was moved into the dropped tree's slot, or 0 if no tree
    /// moved. Roots stay contiguous (fileformat2 §1.7), so dropping a root below the largest moves the largest root's content into the
    /// dropped slot. The caller must rewrite that table's <c>rootpage</c> in its schema row from <see cref="MovedFrom"/> to
    /// <see cref="MovedTo"/>, and reopen trees and cursors on the old root; the eraser has invalidated the cursors that were open on them.
    /// </summary>
    public uint MovedFrom { get; private set; }

    /// <summary>The root page the tree named by <see cref="MovedFrom"/> now has (the dropped tree's former root); 0 if no tree moved.</summary>
    public uint MovedTo { get; private set; }

    /// <summary>Runs until finished or until one I/O is needed.</summary>
    public BtStatus Step()
    {
        try
        {
            return Run();
        }
        catch (OneLiteException e)
        {
            Error = e;
            return BtStatus.Error;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IndexOutOfRangeException or NullReferenceException or OverflowException)
        {
            Error = OneLiteException.Corrupt(_tree.Root, "a page of the tree is inconsistent: " + e.Message);
            return BtStatus.Error;
        }
    }

    private BtStatus Get(uint pgno, out PageHandle? p)
    {
        BtStatus s = _src.GetPage(pgno, out p);
        if (s == BtStatus.Error)
        {
            throw _src.LastError is OneLiteException c ? c : OneLiteException.Corrupt(pgno, "page source failed: " + _src.LastError?.Message);
        }

        return s;
    }

    private BtStatus Run()
    {
        if (!_started)
        {
            // every row of the tree is about to go (an open incremental blob on it expires)
            _tree.Context.Changed?.Invoke(_tree.Root, long.MinValue);
            // Any cursor on this tree loses its position.
            foreach (BtCursor c in _ctx.Cursors)
            {
                if (c.Tree.Root == _tree.Root)
                {
                    c.Invalidate();
                }
            }

            _started = true;
            _top = 0;
            _pg[0] = _tree.Root;
            _phase[0] = 0;
            _i[0] = 0;
        }

        if (_destroy && _src.AutoVacuum && !_keepRoot && !_moveDone)
        {
            // A root below the largest root keeps its page (emptied), for the largest root's content to move into.
            uint largest = _src.LargestRootPage;
            if (_tree.Root > largest)
            {
                throw OneLiteException.Corrupt(_tree.Root, $"root page {_tree.Root} is above the header's largest root page {largest}");
            }

            _keepRoot = _tree.Root < largest;
        }

        while (_top >= 0)
        {
            BtStatus s = Get(_pg[_top], out PageHandle? page);
            if (s != BtStatus.Ok)
            {
                return s;
            }

            bool release = true;
            try
            {
                byte[] d = page!.Data;
                int h = Pg.HdrOff(page.Number);
                byte type = Pg.CheckHeader(page, _ctx.Usable);
                if (Pg.IsIntKey(type) != _tree.IntKey)
                {
                    throw OneLiteException.Corrupt(page.Number, "page type does not match its tree");
                }

                bool leaf = Pg.IsLeaf(type);
                int n = Pg.NCell(d, h);
                if (_phase[_top] == 0)
                {
                    // free the overflow chains of every cell
                    while (_i[_top] < n)
                    {
                        if (_ovfLeft == 0 && _ovfNext == 0)
                        {
                            CellInfo ci = _ctx.ReadCell(page, h, type, _i[_top], out _);
                            if (ci.PayloadSize > ci.Local)
                            {
                                if (ci.Overflow == 0)
                                {
                                    throw OneLiteException.Corrupt(page.Number, "cell has no overflow pointer");
                                }

                                _ovfNext = ci.Overflow;
                                _ovfLeft = (int)((ci.PayloadSize - ci.Local + _ctx.Usable - 5) / (_ctx.Usable - 4));
                            }
                            else
                            {
                                _i[_top]++;
                                continue;
                            }
                        }

                        while (_ovfLeft > 0)
                        {
                            if (_ovfNext == 0)
                            {
                                throw OneLiteException.Corrupt(page.Number, "overflow chain ends early");
                            }

                            s = _src.Reserve(0, 1);
                            if (s != BtStatus.Ok)
                            {
                                return s;
                            }

                            s = Get(_ovfNext, out PageHandle? op);
                            if (s != BtStatus.Ok)
                            {
                                return s;
                            }

                            uint next = Pg.U32(op!.Data, 0);
                            _src.Release(op);
                            s = _src.ReservePtrmap(_ovfNext);
                            if (s != BtStatus.Ok)
                            {
                                return s;
                            }

                            _src.Free(_ovfNext);
                            _ovfNext = next;
                            _ovfLeft--;
                        }

                        _ovfNext = 0;
                        _i[_top]++;
                    }

                    _phase[_top] = 1;
                    _i[_top] = 0;
                }

                if (_phase[_top] == 1 && !leaf)
                {
                    if (_i[_top] <= n)
                    {
                        int ci = _i[_top]++;
                        uint child;
                        if (ci < n)
                        {
                            _ = _ctx.ReadCell(page, h, type, ci, out int off);
                            child = Pg.U32(d, off);
                        }
                        else
                        {
                            child = Pg.RightPtr(d, h);
                        }

                        if (child < 2)
                        {
                            throw OneLiteException.Corrupt(page.Number, $"child pointer {child} is not a valid page");
                        }

                        if (_top + 1 >= BtCursor.MaxDepth)
                        {
                            throw OneLiteException.Corrupt(page.Number, "tree is deeper than the limit");
                        }

                        // Counted when a page is entered, not each time the loop returns to it after a Pending.
                        if (++_visited > (long)_src.PageCount)
                        {
                            throw OneLiteException.Corrupt(child, "tree visits more pages than the database has (shared or looping children)");
                        }

                        _top++;
                        _pg[_top] = child;
                        _phase[_top] = 0;
                        _i[_top] = 0;
                        continue;
                    }
                }

                // finished with this page
                uint pgno = page.Number;
                if (_top == 0 && (!_destroy || _keepRoot))
                {
                    s = _src.MakeWritable(page);
                    if (s != BtStatus.Ok)
                    {
                        return s;
                    }

                    Pg.Zero(d, h, _ctx.Usable, _tree.IntKey ? Pg.TableLeaf : Pg.IndexLeaf);
                }
                else
                {
                    s = _src.Reserve(0, 1);
                    if (s != BtStatus.Ok)
                    {
                        return s;
                    }

                    s = _src.ReservePtrmap(pgno);
                    if (s != BtStatus.Ok)
                    {
                        return s;
                    }

                    _src.Release(page);
                    release = false;
                    _src.Free(pgno);
                    if (_top == 0 && _src.AutoVacuum)
                    {
                        // The dropped root was the largest one: the roots now end at the page before it.
                        _src.LargestRootPage = AutoVacuumRoots.Prev(_src, pgno);
                    }
                }

                _top--;
            }
            finally
            {
                if (release)
                {
                    _src.Release(page!);
                }
            }
        }

        if (_keepRoot && !_moveDone)
        {
            BtStatus ms = AutoVacuumRoots.MoveLargestInto(_ctx, _tree.Root, out uint from);
            if (ms != BtStatus.Ok)
            {
                return ms;
            }

            _moveDone = true;
            MovedFrom = from;
            MovedTo = _tree.Root;
        }

        return BtStatus.Ok;
    }
}
