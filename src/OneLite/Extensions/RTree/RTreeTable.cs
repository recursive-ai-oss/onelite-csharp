using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.VirtualTables;

namespace RecursiveAI.OneLite.Extensions.RTree;

/// <summary>An r-tree virtual table of one connection: scans and updates over the three shadow tables.</summary>
internal sealed partial class RTreeTable : VTab
{
    private const int MaxDepth = 40;

    private readonly Connection _conn;
    private readonly RTreeShape _sh;
    private int _nodeSize;

    internal RTreeTable(Connection conn, RTreeShape sh)
    {
        _conn = conn;
        _sh = sh;
    }

    internal override bool IsWritable => true;

    internal override void BestIndex(BestIndexInfo info)
    {
        // idxNum 1: the rowid is fixed by an equality. Otherwise a scan whose coordinate constraints (idxStr: operator letter and column per argument)
        // prune subtrees; the engine tests every term again on the rows returned, so a constraint here only has to keep every row that could match.
        for (int i = 0; i < info.Constraints.Length; i++)
        {
            IndexConstraint c = info.Constraints[i];
            if (c.Usable && c.Column == 0 && c.Op == ConstraintOp.Eq)
            {
                info.ConstraintUsage[i].ArgvIndex = 1;
                info.IndexNumber = 1;
                info.EstimatedCost = 30;
                info.EstimatedRows = 1;
                info.ScanIsUnique = true;
                return;
            }
        }

        var str = new System.Text.StringBuilder();
        int n = 0;
        for (int i = 0; i < info.Constraints.Length; i++)
        {
            IndexConstraint c = info.Constraints[i];
            if (!c.Usable || c.Column < 1 || c.Column > _sh.Stride)
            {
                continue;
            }

            char op = c.Op switch
            {
                ConstraintOp.Eq => '=',
                ConstraintOp.Lt => '<',
                ConstraintOp.Le => 'l',
                ConstraintOp.Gt => '>',
                ConstraintOp.Ge => 'g',
                _ => '\0',
            };
            if (op == '\0')
            {
                continue;
            }

            info.ConstraintUsage[i].ArgvIndex = ++n;
            str.Append(op).Append((char)('0' + c.Column));
        }

        info.IndexNumber = 0;
        info.IndexString = n == 0 ? null : str.ToString();
        info.EstimatedCost = 3_000_000.0 / ((n * 3) + 1);
        info.EstimatedRows = Math.Max(1, 100_000 / ((n * 4) + 1));
    }

    internal override VCursor Open() => new RTreeCursor(this);

    public override void Dispose()
    {
    }

    internal RTreeShape Shape => _sh;

    internal Connection Conn => _conn;

    /// <summary>The size of a node blob, read from the root node the first time it is needed.</summary>
    internal int NodeSize => _nodeSize;

    internal void SetNodeSize(int size) => _nodeSize = size;

    /// <summary>
    /// Cells per node: what the blob holds, but never more than 51. Observed: the pinned shell reports a node with more cells as corrupt, and the nodes
    /// it writes stay well below that, so the cap is part of the file format.
    /// </summary>
    internal int MaxCells => Math.Min((_nodeSize - 4) / _sh.CellSize, 51);

    internal ShadowTree OpenNodes() => new(_conn, _sh.Schema, _sh.NodeTable);

    internal ShadowTree OpenRowids() => new(_conn, _sh.Schema, _sh.RowidTable);

    internal ShadowTree OpenParents() => new(_conn, _sh.Schema, _sh.ParentTable);

    /// <summary>Loads node <paramref name="no"/> through <paramref name="tree"/>; a missing row is corruption.</summary>
    internal ModStatus ReadNode(ShadowTree tree, long no, out RNode? node)
    {
        node = null;
        ModStatus s = tree.Find(no, out bool found);
        if (s != ModStatus.Ok)
        {
            return s;
        }

        if (!found)
        {
            throw RNode.Corrupt("node " + no + " is missing");
        }

        s = tree.Load();
        if (s != ModStatus.Ok)
        {
            return s;
        }

        Mem blob = default;
        tree.Row.Load(1, ref blob);
        if (blob.Kind != StorageClass.Blob)
        {
            throw RNode.Corrupt("node " + no + " is not a blob");
        }

        if (_nodeSize == 0 && no == 1)
        {
            _nodeSize = blob.Length;
        }

        node = RNode.Decode(no, blob.Bytes, _sh);
        return ModStatus.Ok;
    }
}

internal sealed class RTreeCursor : VCursor
{
    private readonly RTreeTable _t;
    private readonly RTreeShape _sh;
    private ShadowTree? _nodes;
    private ShadowTree? _rowids;
    private int _mode;
    private int _nc;
    private int[] _cc = [];
    private char[] _op = [];
    private double[] _cv = [];
    private long _eqId;
    private readonly RNode?[] _stack = new RNode?[42];
    private readonly int[] _idx = new int[42];
    private int _sp;
    private int _depth;
    private bool _rootLoaded;
    private bool _filtering;
    private int _fstep;
    private bool _eof = true;
    private long _id;
    private readonly double[] _coords = new double[10];
    private long _auxFor = long.MinValue;

    internal RTreeCursor(RTreeTable t)
    {
        _t = t;
        _sh = t.Shape;
    }

    internal override bool Eof => _eof;

    private ShadowTree Nodes => _nodes ??= _t.OpenNodes();

    private ShadowTree Rowids => _rowids ??= _t.OpenRowids();

    internal override ModStatus Filter(int indexNumber, string? indexString, ReadOnlySpan<Mem> args)
    {
        if (!_filtering)
        {
            _filtering = true;
            _fstep = 0;
            _sp = 0;
            _rootLoaded = false;
            _eof = false;
            _auxFor = long.MinValue;
            _mode = indexNumber;
            _nc = 0;
            if (indexNumber == 1)
            {
                _eqId = Vm.Values.ToInt(args[0]);
            }
            else if (indexString is not null)
            {
                _nc = indexString.Length / 2;
                _cc = new int[_nc];
                _op = new char[_nc];
                _cv = new double[_nc];
                for (int i = 0; i < _nc; i++)
                {
                    _op[i] = indexString[2 * i];
                    _cc[i] = indexString[(2 * i) + 1] - '0';
                    _cv[i] = Vm.Values.ToReal(args[i]);
                }
            }
        }

        ModStatus s = Run();
        if (s == ModStatus.Ok)
        {
            _filtering = false;
        }

        return s;
    }

    internal override ModStatus Next() => Run();

    private bool Matches(RNode n, int i, bool leaf)
    {
        ReadOnlySpan<double> c = n.Cell(i);
        for (int k = 0; k < _nc; k++)
        {
            int col = _cc[k] - 1;
            double v = _cv[k];
            double lo = c[col & ~1];
            double hi = c[col | 1];
            bool ok;
            if (leaf)
            {
                double x = c[col];
                ok = _op[k] switch { '=' => x == v, '<' => x < v, 'l' => x <= v, '>' => x > v, _ => x >= v };
            }
            else
            {
                ok = _op[k] switch { '=' => lo <= v && v <= hi, '<' => lo < v, 'l' => lo <= v, '>' => hi > v, _ => hi >= v };
            }

            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private ModStatus Run()
    {
        if (_mode == 1)
        {
            return RunRowid();
        }

        for (; ; )
        {
            if (_sp == 0)
            {
                if (_rootLoaded)
                {
                    _eof = true;
                    return ModStatus.Ok;
                }

                ModStatus rs = _t.ReadNode(Nodes, 1, out RNode? root);
                if (rs != ModStatus.Ok)
                {
                    return rs;
                }

                _depth = root!.Depth;
                if (_depth > 40)
                {
                    throw RNode.Corrupt("tree depth " + _depth);
                }

                _rootLoaded = true;
                _stack[0] = root;
                _idx[0] = 0;
                _sp = 1;
            }

            RNode n = _stack[_sp - 1]!;
            int i = _idx[_sp - 1];
            if (i >= n.Count)
            {
                _sp--;
                continue;
            }

            bool leaf = _sp == _depth + 1;
            if (!Matches(n, i, leaf))
            {
                _idx[_sp - 1] = i + 1;
                continue;
            }

            if (leaf)
            {
                _id = n.Id[i];
                n.Cell(i).CopyTo(_coords);
                _idx[_sp - 1] = i + 1;
                _auxFor = long.MinValue;
                return ModStatus.Ok;
            }

            ModStatus s = _t.ReadNode(Nodes, n.Id[i], out RNode? child);
            if (s != ModStatus.Ok)
            {
                return s;
            }

            _idx[_sp - 1] = i + 1;
            _stack[_sp] = child;
            _idx[_sp] = 0;
            _sp++;
        }
    }

    private ModStatus RunRowid()
    {
        if (_fstep == 0)
        {
            ModStatus s = Rowids.Find(_eqId, out bool found);
            if (s != ModStatus.Ok)
            {
                return s;
            }

            if (!found)
            {
                _eof = true;
                _fstep = 9;
                return ModStatus.Ok;
            }

            _fstep = 1;
        }

        if (_fstep == 1)
        {
            ModStatus s = Rowids.Load();
            if (s != ModStatus.Ok)
            {
                return s;
            }

            Mem m = default;
            Rowids.Row.Load(1, ref m);
            _fnode = Vm.Values.ToInt(m);
            _fstep = 2;
        }

        if (_fstep == 2)
        {
            ModStatus s = _t.ReadNode(Nodes, _fnode, out RNode? n);
            if (s != ModStatus.Ok)
            {
                return s;
            }

            int i = n!.IndexOf(_eqId);
            if (i < 0)
            {
                _eof = true;
            }
            else
            {
                _eof = false;
                _id = _eqId;
                n.Cell(i).CopyTo(_coords);
                _auxFor = long.MinValue;
            }

            _fstep = 9;
            return ModStatus.Ok;
        }

        // A row already produced: the next call moves past it.
        _eof = true;
        return ModStatus.Ok;
    }

    private long _fnode;

    internal override ModStatus Column(int index, ref Mem dst, ref byte[]? own)
    {
        if (index == 0)
        {
            Vm.Values.SetInt(ref dst, _id);
            return ModStatus.Ok;
        }

        if (index <= _sh.Stride)
        {
            double v = _coords[index - 1];
            if (_sh.Int32)
            {
                Vm.Values.SetInt(ref dst, (long)v);
            }
            else
            {
                Vm.Values.SetReal(ref dst, v);
            }

            return ModStatus.Ok;
        }

        if (_auxFor != _id)
        {
            ModStatus s = Rowids.Find(_id, out bool found);
            if (s != ModStatus.Ok)
            {
                return s;
            }

            if (!found)
            {
                Vm.Values.SetNull(ref dst);
                return ModStatus.Ok;
            }

            s = Rowids.Load();
            if (s != ModStatus.Ok)
            {
                return s;
            }

            _auxFor = _id;
        }

        Rowids.Row.Load(index - _sh.Stride + 1, ref dst);
        if (dst.Kind is StorageClass.Text or StorageClass.Blob)
        {
            // The value points into the page; the register keeps its own copy.
            Vm.Values.SetBytes(ref dst, ref own, dst.Kind, dst.Bytes);
        }

        return ModStatus.Ok;
    }

    internal override ModStatus RowId(out long rowid)
    {
        rowid = _id;
        return ModStatus.Ok;
    }

    public override void Dispose()
    {
        _nodes?.Dispose();
        _rowids?.Dispose();
    }
}
