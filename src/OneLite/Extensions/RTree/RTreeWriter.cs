using System.Linq;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Vm;
using RecursiveAI.OneLite.VirtualTables;

namespace RecursiveAI.OneLite.Extensions.RTree;

// Inserting, deleting and updating entries: the R*-tree of Beckmann, Kriegel, Schneider and Seeger (ChooseSubtree by least overlap enlargement
// above the leaves and least area enlargement elsewhere, one forced reinsertion per level and insertion, the margin/overlap/area split), with
// Guttman's condense on delete.
//
// Suspend points: a change is planned in memory, then applied. Planning reads nodes, parent links and rowid rows; a row that is not cached throws
// MissException after the read was started, Update returns Pending, and the next call plans again from the start: every read that finished
// is remembered, so the plan reaches the same missing row first and the read continues. Nothing is written until the plan is complete; then the
// writes are applied one by one, each repeated with the same arguments after Pending.
internal sealed partial class RTreeTable
{
    private sealed class MissException : Exception
    {
    }

    private static readonly MissException s_miss = new();

    private enum WriteKind : byte
    {
        PutNode,
        DeleteNode,
        PutParent,
        DeleteParent,
        PutRowid,
        DeleteRowid,
    }

    private readonly record struct WriteOp(WriteKind Kind, long Key, long Value, byte[]? Blob, Mem[]? Aux);

    private int _phase;
    private ShadowTree? _wNodes, _wRowids, _wParents;
    private readonly Dictionary<long, RNode> _rdNodes = [];
    private readonly Dictionary<long, long> _rdParents = [];
    private readonly Dictionary<long, (long Node, Mem[] Aux)?> _rdRowids = [];
    private long _maxNode = -1;
    private int _allocated;
    private long _maxRowid = -1;

    private readonly Dictionary<long, RNode> _dirty = [];
    private readonly HashSet<long> _gone = [];
    private readonly Dictionary<long, long> _pset = [];
    private readonly HashSet<long> _pdel = [];
    private readonly Dictionary<long, (long Node, Mem[] Aux)> _rset = [];
    private readonly HashSet<long> _rdel = [];
    private int _treeDepth;
    private long _newId;
    private List<WriteOp> _ops = [];
    private int _opAt;
    private int _opStep;

    private ShadowTree WNodes => _wNodes ??= OpenNodes();

    private ShadowTree WRowids => _wRowids ??= OpenRowids();

    private ShadowTree WParents => _wParents ??= OpenParents();

    internal override ModStatus Update(ReadOnlySpan<Mem> values, out long rowid)
    {
        rowid = 0;
        try
        {
            ModStatus s = UpdateCore(values);
            rowid = _newId;
            return s;
        }
        catch (MissException)
        {
            return ModStatus.Pending;
        }
        catch
        {
            ResetAll();
            throw;
        }
    }

    private void ResetAll()
    {
        _phase = 0;
        _rdNodes.Clear();
        _rdParents.Clear();
        _rdRowids.Clear();
        _maxNode = -1;
        _maxRowid = -1;
        ResetPlan();
        _ops = [];
        _wNodes?.Dispose();
        _wRowids?.Dispose();
        _wParents?.Dispose();
        _wNodes = _wRowids = _wParents = null;
    }

    private void ResetPlan()
    {
        _dirty.Clear();
        _gone.Clear();
        _pset.Clear();
        _pdel.Clear();
        _rset.Clear();
        _rdel.Clear();
        _allocated = 0;
    }

    private ModStatus UpdateCore(ReadOnlySpan<Mem> v)
    {
        if (_phase == 0)
        {
            _phase = 1;
        }

        if (_phase == 1)
        {
            if (_nodeSize == 0)
            {
                ModStatus rs = ReadNode(WNodes, 1, out _);
                if (rs != ModStatus.Ok)
                {
                    return rs;
                }
            }

            ResetPlan();
            Plan(v);
            BuildOps();
            _phase = 2;
            _opAt = 0;
            _opStep = 0;
        }

        ModStatus s = Apply();
        if (s != ModStatus.Ok)
        {
            return s;
        }

        long keep = _newId;
        ResetAll();
        _newId = keep;
        return ModStatus.Ok;
    }

    // ------------------------------------------------------------------ reads for planning

    private RNode Loaded(long no)
    {
        if (_rdNodes.TryGetValue(no, out RNode? n))
        {
            return n;
        }

        if (ReadNode(WNodes, no, out n) == ModStatus.Pending)
        {
            throw s_miss;
        }

        _rdNodes[no] = n!;
        return n!;
    }

    private RNode Peek(long no)
    {
        if (_dirty.TryGetValue(no, out RNode? d))
        {
            return d;
        }

        if (_gone.Contains(no))
        {
            throw RNode.Corrupt("node " + no + " was removed");
        }

        return Loaded(no);
    }

    private RNode Touch(long no)
    {
        if (!_dirty.TryGetValue(no, out RNode? d))
        {
            d = Peek(no).Clone();
            _dirty[no] = d;
        }

        return d;
    }

    private long ParentOf(long no)
    {
        if (_pdel.Contains(no))
        {
            return 0;
        }

        if (_pset.TryGetValue(no, out long p))
        {
            return p;
        }

        if (_rdParents.TryGetValue(no, out p))
        {
            return p;
        }

        ModStatus s = WParents.Find(no, out bool found);
        if (s == ModStatus.Ok && found)
        {
            s = WParents.Load();
        }

        if (s == ModStatus.Pending)
        {
            throw s_miss;
        }

        if (found)
        {
            Mem m = default;
            WParents.Row.Load(1, ref m);
            p = Vm.Values.ToInt(m);
        }
        else
        {
            p = 0;
        }

        _rdParents[no] = p;
        return p;
    }

    private static Mem Own(Mem m)
    {
        if (m.Kind is StorageClass.Text or StorageClass.Blob)
        {
            byte[] b = m.Bytes.ToArray();
            return new Mem { Kind = m.Kind, Buffer = b, Length = b.Length };
        }

        return m;
    }

    private (long Node, Mem[] Aux)? RowidRow(long id)
    {
        if (_rdel.Contains(id))
        {
            return null;
        }

        if (_rset.TryGetValue(id, out (long Node, Mem[] Aux) r))
        {
            return r;
        }

        if (_rdRowids.TryGetValue(id, out (long Node, Mem[] Aux)? c))
        {
            return c;
        }

        ModStatus s = WRowids.Find(id, out bool found);
        if (s == ModStatus.Ok && found)
        {
            s = WRowids.Load();
        }

        if (s == ModStatus.Pending)
        {
            throw s_miss;
        }

        (long, Mem[])? res = null;
        if (found)
        {
            RecordView row = WRowids.Row;
            Mem m = default;
            row.Load(1, ref m);
            long node = Vm.Values.ToInt(m);
            var aux = new Mem[_sh.Aux];
            for (int i = 0; i < aux.Length; i++)
            {
                row.Load(2 + i, ref m);
                aux[i] = Own(m);
            }

            res = (node, aux);
        }

        _rdRowids[id] = res;
        return res;
    }

    private long MaxRowid()
    {
        if (_maxRowid < 0)
        {
            if (WRowids.Last() == ModStatus.Pending)
            {
                throw s_miss;
            }

            _maxRowid = WRowids.Valid ? WRowids.Rowid : 0;
        }

        return _maxRowid;
    }

    private long AllocNode()
    {
        if (_maxNode < 0)
        {
            if (WNodes.Last() == ModStatus.Pending)
            {
                throw s_miss;
            }

            _maxNode = WNodes.Valid ? WNodes.Rowid : 1;
        }

        return _maxNode + (++_allocated);
    }

    // ------------------------------------------------------------------ planning

    private double[] CoordsOf(ReadOnlySpan<Mem> v)
    {
        var c = new double[_sh.Stride];
        for (int d = 0; d < _sh.Dims; d++)
        {
            Mem lo = v[3 + (2 * d)];
            Mem hi = v[4 + (2 * d)];
            double a, b;
            if (_sh.Int32)
            {
                a = unchecked((int)Vm.Values.ToInt(lo));
                b = unchecked((int)Vm.Values.ToInt(hi));
            }
            else
            {
                double x = Vm.Values.ToReal(lo);
                double y = Vm.Values.ToReal(hi);
                // Observed: a bound that does not fit a float32 is moved outward by a relative 2^-23 and then rounded to the nearest float32
                // (the documentation puts the margin at about 0.000012%), so a stored box always holds the box that was given.
                const double Eps = 1.1920928955078125e-7;
                float fa = (float)x;
                if (fa > x)
                {
                    fa = (float)(x < 0 ? x * (1 + Eps) : x * (1 - Eps));
                }

                float fb = (float)y;
                if (fb < y)
                {
                    fb = (float)(y < 0 ? y * (1 - Eps) : y * (1 + Eps));
                }

                a = fa;
                b = fb;
            }

            if (a > b)
            {
                throw new OneLiteException(ResultCode.Constraint, "rtree constraint failed: " + _sh.Table + ".(" + _sh.Columns[(2 * d) + 1] + "<=" + _sh.Columns[(2 * d) + 2] + ")");
            }

            c[2 * d] = a;
            c[(2 * d) + 1] = b;
        }

        return c;
    }

    private void Plan(ReadOnlySpan<Mem> v)
    {
        // v: [oldRowid, newRowid, id, coordinates..., aux...]
        bool hasOld = v[0].Kind != StorageClass.Null;
        if (v.Length == 1)
        {
            _treeDepth = Loaded(1).Depth;
            PlanDelete(Vm.Values.ToInt(v[0]));
            return;
        }

        double[] coords = CoordsOf(v);
        var aux = new Mem[_sh.Aux];
        for (int i = 0; i < aux.Length; i++)
        {
            aux[i] = Own(v[3 + _sh.Stride + i]);
        }

        long? want = v[2].Kind == StorageClass.Null ? null : Vm.Values.ToInt(v[2]);
        long oldId = hasOld ? Vm.Values.ToInt(v[0]) : 0;
        _treeDepth = Loaded(1).Depth;
        if (want is { } w && (!hasOld || w != oldId) && RowidRow(w) is not null)
        {
            throw new OneLiteException(ResultCode.Constraint, "UNIQUE constraint failed: " + _sh.Table + "." + _sh.Columns[0]);
        }

        if (hasOld)
        {
            PlanDelete(oldId);
        }

        long id = want ?? (MaxRowid() + 1);
        _newId = id;
        _rset[id] = (0, aux);
        _rdel.Remove(id);
        InsertCell(id, coords, 0, new bool[MaxDepth + 2]);
    }

    private static double Area(ReadOnlySpan<double> c)
    {
        double a = 1;
        for (int d = 0; d < c.Length; d += 2)
        {
            a *= c[d + 1] - c[d];
        }

        return a;
    }

    private static double Margin(ReadOnlySpan<double> c)
    {
        double m = 0;
        for (int d = 0; d < c.Length; d += 2)
        {
            m += c[d + 1] - c[d];
        }

        return m;
    }

    private static double[] Union(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        var u = new double[a.Length];
        for (int d = 0; d < a.Length; d += 2)
        {
            u[d] = Math.Min(a[d], b[d]);
            u[d + 1] = Math.Max(a[d + 1], b[d + 1]);
        }

        return u;
    }

    private static double Overlap(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        double o = 1;
        for (int d = 0; d < a.Length; d += 2)
        {
            double w = Math.Min(a[d + 1], b[d + 1]) - Math.Max(a[d], b[d]);
            if (w <= 0)
            {
                return 0;
            }

            o *= w;
        }

        return o;
    }

    private static int ChooseChild(RNode n, double[] coords, bool childrenAreLeaves)
    {
        int best = 0;
        double bestOv = double.MaxValue, bestEnl = double.MaxValue, bestArea = double.MaxValue;
        IEnumerable<int> cand = Enumerable.Range(0, n.Count);
        if (childrenAreLeaves && n.Count > 32)
        {
            cand = cand.OrderBy(i => Area(Union(n.Cell(i), coords)) - Area(n.Cell(i))).Take(32);
        }

        int[] cands = [.. cand];
        foreach (int i in cands)
        {
            double[] u = Union(n.Cell(i), coords);
            double area = Area(n.Cell(i));
            double enl = Area(u) - area;
            double ov = 0;
            if (childrenAreLeaves)
            {
                foreach (int j in cands)
                {
                    if (j != i)
                    {
                        ov += Overlap(u, n.Cell(j)) - Overlap(n.Cell(i), n.Cell(j));
                    }
                }
            }

            if (ov < bestOv || (ov == bestOv && (enl < bestEnl || (enl == bestEnl && area < bestArea))))
            {
                best = i;
                bestOv = ov;
                bestEnl = enl;
                bestArea = area;
            }
        }

        return best;
    }

    private void SetRowidNode(long id, long node)
    {
        (long Node, Mem[] Aux)? r = RowidRow(id) ?? throw RNode.Corrupt("rowid " + id + " has no row in the rowid table");
        _rset[id] = (node, r.Value.Aux);
        _rdel.Remove(id);
    }

    private void SetParent(long child, long parent)
    {
        _pset[child] = parent;
        _pdel.Remove(child);
    }

    private void InsertCell(long id, double[] coords, int level, bool[] reinserted)
    {
        long no = 1;
        var path = new List<long>();
        for (int cur = _treeDepth; cur > level; cur--)
        {
            path.Add(no);
            RNode n = Peek(no);
            no = n.Id[ChooseChild(n, coords, cur == 1)];
        }

        Touch(no).Add(id, coords);
        if (level == 0)
        {
            SetRowidNode(id, no);
        }
        else
        {
            SetParent(id, no);
        }

        Settle(no, path, level, reinserted);
    }

    /// <summary>Handles an overflow at <paramref name="no"/> (reinsert or split, moving up while parents overflow) and fixes the covering boxes above it.</summary>
    private void Settle(long no, List<long> path, int level, bool[] reinserted)
    {
        for (; ; )
        {
            RNode n = Peek(no);
            if (n.Count <= MaxCells)
            {
                AdjustUp(path, no);
                return;
            }

            if (path.Count > 0 && !reinserted[level])
            {
                reinserted[level] = true;
                List<(long Id, double[] C)> removed = RemoveFarthest(no);
                AdjustUp(path, no);
                foreach ((long rid, double[] rc) in removed)
                {
                    InsertCell(rid, rc, level, reinserted);
                }

                return;
            }

            if (path.Count == 0)
            {
                SplitRoot();
                return;
            }

            long parent = path[^1];
            long n2 = Split(no, level);
            RNode pn = Touch(parent);
            pn.Add(n2, Peek(n2).Bounds());
            SetParent(n2, parent);
            int ci = pn.IndexOf(no);
            Peek(no).Bounds().CopyTo(pn.C, ci * _sh.Stride);
            path.RemoveAt(path.Count - 1);
            no = parent;
            level++;
        }
    }

    private void AdjustUp(List<long> path, long no)
    {
        long cur = no;
        for (int k = path.Count - 1; k >= 0; k--)
        {
            RNode p = Peek(path[k]);
            int ci = p.IndexOf(cur);
            if (ci < 0)
            {
                throw RNode.Corrupt("node " + cur + " is not listed in its parent " + path[k]);
            }

            double[] b = Peek(cur).Bounds();
            if (p.Cell(ci).SequenceEqual(b))
            {
                return;
            }

            b.CopyTo(Touch(path[k]).C, ci * _sh.Stride);
            cur = path[k];
        }
    }

    private List<(long Id, double[] C)> RemoveFarthest(long no)
    {
        RNode n = Touch(no);
        double[] bb = n.Bounds();
        int p = Math.Max(1, (int)(0.3 * MaxCells));
        var dist = new (double D, int I)[n.Count];
        for (int i = 0; i < n.Count; i++)
        {
            double d2 = 0;
            ReadOnlySpan<double> c = n.Cell(i);
            for (int k = 0; k < c.Length; k += 2)
            {
                double diff = ((c[k] + c[k + 1]) / 2) - ((bb[k] + bb[k + 1]) / 2);
                d2 += diff * diff;
            }

            dist[i] = (d2, i);
        }

        // The p entries farthest from the node's centre leave; they come back nearest first ("close reinsert").
        var far = dist.OrderByDescending(x => x.D).ThenBy(x => x.I).Take(p).ToList();
        var removed = new List<(long, double[])>();
        foreach ((double D, int I) f in far.OrderBy(x => x.D).ThenBy(x => x.I))
        {
            removed.Add((n.Id[f.I], n.Cell(f.I).ToArray()));
        }

        foreach ((double D, int I) f in far.OrderByDescending(x => x.I))
        {
            n.RemoveAt(f.I);
        }

        return removed;
    }

    /// <summary>The R*-tree split: the axis with the least margin sum, then the distribution with the least overlap (ties: least area).</summary>
    private (int[] First, int[] Second) ComputeSplit(RNode n)
    {
        int total = n.Count;
        int m = Math.Max(1, (int)(0.4 * MaxCells));
        if (m > total / 2)
        {
            m = total / 2;
        }

        int bestAxis = 0;
        double bestMargin = double.MaxValue;
        for (int axis = 0; axis < _sh.Dims; axis++)
        {
            double margin = 0;
            for (int bound = 0; bound < 2; bound++)
            {
                int[] order = SortBy(n, axis, bound);
                for (int k = m; k <= total - m; k++)
                {
                    margin += Margin(BoundsOf(n, order, 0, k)) + Margin(BoundsOf(n, order, k, total));
                }
            }

            if (margin < bestMargin)
            {
                bestMargin = margin;
                bestAxis = axis;
            }
        }

        int[]? bestOrder = null;
        int bestK = m;
        double bestOv = double.MaxValue, bestAr = double.MaxValue;
        for (int bound = 0; bound < 2; bound++)
        {
            int[] order = SortBy(n, bestAxis, bound);
            for (int k = m; k <= total - m; k++)
            {
                double[] b1 = BoundsOf(n, order, 0, k);
                double[] b2 = BoundsOf(n, order, k, total);
                double ov = Overlap(b1, b2);
                double ar = Area(b1) + Area(b2);
                // (An unbounded coordinate makes areas NaN; the first distribution is then kept.)
                if (bestOrder is null || ov < bestOv || (ov == bestOv && ar < bestAr))
                {
                    bestOv = ov;
                    bestAr = ar;
                    bestOrder = order;
                    bestK = k;
                }
            }
        }

        return (bestOrder![..bestK], bestOrder[bestK..]);
    }

    private static int[] SortBy(RNode n, int axis, int bound)
    {
        int[] idx = [.. Enumerable.Range(0, n.Count)];
        Array.Sort(idx, (a, b) =>
        {
            ReadOnlySpan<double> ca = n.Cell(a), cb = n.Cell(b);
            int c = ca[(2 * axis) + bound].CompareTo(cb[(2 * axis) + bound]);
            if (c == 0)
            {
                c = ca[(2 * axis) + (1 - bound)].CompareTo(cb[(2 * axis) + (1 - bound)]);
            }

            return c != 0 ? c : a.CompareTo(b);
        });
        return idx;
    }

    private double[] BoundsOf(RNode n, int[] order, int from, int to)
    {
        var b = new double[_sh.Stride];
        for (int i = from; i < to; i++)
        {
            ReadOnlySpan<double> c = n.Cell(order[i]);
            for (int d = 0; d < _sh.Stride; d += 2)
            {
                if (i == from || c[d] < b[d])
                {
                    b[d] = c[d];
                }

                if (i == from || c[d + 1] > b[d + 1])
                {
                    b[d + 1] = c[d + 1];
                }
            }
        }

        return b;
    }

    private void MoveCell(RNode from, int i, RNode to, int level)
    {
        long id = from.Id[i];
        to.Add(id, from.Cell(i));
        if (level == 0)
        {
            SetRowidNode(id, to.No);
        }
        else
        {
            SetParent(id, to.No);
        }
    }

    private long Split(long no, int level)
    {
        RNode n = Touch(no);
        (int[] first, int[] second) = ComputeSplit(n);
        long n2no = AllocNode();
        var n2 = new RNode(n2no, _sh.Stride, second.Length) { Depth = 0 };
        _dirty[n2no] = n2;
        var keep = new RNode(no, _sh.Stride, first.Length) { Depth = n.Depth };
        foreach (int i in first)
        {
            keep.Add(n.Id[i], n.Cell(i));
        }

        foreach (int i in second)
        {
            MoveCell(n, i, n2, level);
        }

        _dirty[no] = keep;
        return n2no;
    }

    private void SplitRoot()
    {
        RNode root = Touch(1);
        (int[] first, int[] second) = ComputeSplit(root);
        int level = _treeDepth;
        long a = AllocNode();
        long b = AllocNode();
        var na = new RNode(a, _sh.Stride, first.Length);
        var nb = new RNode(b, _sh.Stride, second.Length);
        foreach (int i in first)
        {
            MoveCell(root, i, na, level);
        }

        foreach (int i in second)
        {
            MoveCell(root, i, nb, level);
        }

        _dirty[a] = na;
        _dirty[b] = nb;
        var nr = new RNode(1, _sh.Stride, 2) { Depth = _treeDepth + 1 };
        nr.Add(a, na.Bounds());
        nr.Add(b, nb.Bounds());
        _dirty[1] = nr;
        SetParent(a, 1);
        SetParent(b, 1);
        _treeDepth++;
    }

    private void PlanDelete(long id)
    {
        (long Node, Mem[] Aux)? row = RowidRow(id);
        if (row is null)
        {
            return;
        }

        long leafNo = row.Value.Node;
        RNode leaf = Touch(leafNo);
        int at = leaf.IndexOf(id);
        if (at < 0)
        {
            throw RNode.Corrupt("rowid " + id + " is not in node " + leafNo);
        }

        leaf.RemoveAt(at);
        _rset.Remove(id);
        _rdel.Add(id);

        int minFill = Math.Max(1, MaxCells / 3);
        var orphans = new List<(int Level, long Id, double[] C)>();
        long no = leafNo;
        int level = 0;
        while (no != 1)
        {
            long p = ParentOf(no);
            if (p == 0)
            {
                throw RNode.Corrupt("node " + no + " has no parent");
            }

            RNode pn = Touch(p);
            RNode n = Peek(no);
            int ci = pn.IndexOf(no);
            if (ci < 0)
            {
                throw RNode.Corrupt("node " + no + " is not listed in its parent " + p);
            }

            if (n.Count < minFill)
            {
                pn.RemoveAt(ci);
                for (int i = 0; i < n.Count; i++)
                {
                    orphans.Add((level, n.Id[i], n.Cell(i).ToArray()));
                }

                _gone.Add(no);
                _dirty.Remove(no);
                _pdel.Add(no);
                _pset.Remove(no);
            }
            else
            {
                n.Bounds().CopyTo(pn.C, ci * _sh.Stride);
            }

            no = p;
            level++;
        }

        var all = new bool[MaxDepth + 2];
        Array.Fill(all, true);
        foreach ((int lv, long oid, double[] oc) in orphans)
        {
            InsertCell(oid, oc, lv, all);
        }

        while (_treeDepth > 0 && Peek(1).Count == 1)
        {
            long childNo = Peek(1).Id[0];
            RNode child = Peek(childNo);
            var nr = new RNode(1, _sh.Stride, child.Count) { Depth = _treeDepth - 1 };
            for (int i = 0; i < child.Count; i++)
            {
                nr.Add(child.Id[i], child.Cell(i));
                if (_treeDepth - 1 == 0)
                {
                    SetRowidNode(child.Id[i], 1);
                }
                else
                {
                    SetParent(child.Id[i], 1);
                }
            }

            _dirty[1] = nr;
            _gone.Add(childNo);
            _dirty.Remove(childNo);
            _pdel.Add(childNo);
            _pset.Remove(childNo);
            _treeDepth--;
        }

        if (_dirty.TryGetValue(1, out RNode? r1))
        {
            r1.Depth = _treeDepth;
        }
        else if (Loaded(1).Depth != _treeDepth)
        {
            Touch(1).Depth = _treeDepth;
        }
    }

    // ------------------------------------------------------------------ applying

    private void BuildOps()
    {
        _ops = [];
        foreach (long g in _gone)
        {
            _ops.Add(new WriteOp(WriteKind.DeleteNode, g, 0, null, null));
        }

        foreach ((long no, RNode n) in _dirty.OrderBy(k => k.Key))
        {
            n.Depth = no == 1 ? _treeDepth : 0;
            _ops.Add(new WriteOp(WriteKind.PutNode, no, 0, n.Encode(_sh, _nodeSize), null));
        }

        foreach (long c in _pdel)
        {
            if (!_pset.ContainsKey(c))
            {
                _ops.Add(new WriteOp(WriteKind.DeleteParent, c, 0, null, null));
            }
        }

        foreach ((long c, long p) in _pset.OrderBy(k => k.Key))
        {
            if (!_gone.Contains(c))
            {
                _ops.Add(new WriteOp(WriteKind.PutParent, c, p, null, null));
            }
        }

        foreach (long id in _rdel)
        {
            _ops.Add(new WriteOp(WriteKind.DeleteRowid, id, 0, null, null));
        }

        foreach ((long id, (long node, Mem[] aux)) in _rset.OrderBy(k => k.Key))
        {
            _ops.Add(new WriteOp(WriteKind.PutRowid, id, node, null, aux));
        }
    }

    private ModStatus Apply()
    {
        while (_opAt < _ops.Count)
        {
            WriteOp op = _ops[_opAt];
            ModStatus s;
            switch (op.Kind)
            {
                case WriteKind.PutNode:
                    s = WNodes.Put(op.Key, [default, new Mem { Kind = StorageClass.Blob, Buffer = op.Blob, Length = op.Blob!.Length }]);
                    break;
                case WriteKind.PutParent:
                    s = WParents.Put(op.Key, [default, Mem.FromInt(op.Value)]);
                    break;
                case WriteKind.PutRowid:
                {
                    var rec = new Mem[2 + op.Aux!.Length];
                    rec[1] = Mem.FromInt(op.Value);
                    op.Aux.CopyTo(rec, 2);
                    s = WRowids.Put(op.Key, rec);
                    break;
                }

                default:
                {
                    ShadowTree t = op.Kind == WriteKind.DeleteNode ? WNodes : op.Kind == WriteKind.DeleteParent ? WParents : WRowids;
                    s = ModStatus.Ok;
                    if (_opStep == 0)
                    {
                        s = t.Find(op.Key, out bool found);
                        if (s == ModStatus.Ok)
                        {
                            _opStep = found ? 1 : 2;
                        }
                    }

                    if (s == ModStatus.Ok && _opStep == 1)
                    {
                        s = t.Delete();
                    }

                    break;
                }
            }

            if (s == ModStatus.Pending)
            {
                return s;
            }

            _opAt++;
            _opStep = 0;
        }

        return ModStatus.Ok;
    }
}
