using System.Linq;
using System.Text;

namespace RecursiveAI.OneLite.Extensions.Fts3;

/// <summary>An absent-or-present row of %_segdir: one segment b-tree.</summary>
internal sealed class SegDirRow
{
    internal long Level;
    internal long Idx;
    internal long Start;
    internal long LeavesEnd;
    internal long End;
    internal byte[] Root = [];

    /// <summary>The bytes of all leaf nodes, as the second number of <c>end_block</c>.</summary>
    internal long LeafBytes;
}

internal sealed class ByteKeyComparer : IEqualityComparer<byte[]>, IComparer<byte[]>
{
    internal static readonly ByteKeyComparer Instance = new();

    public bool Equals(byte[]? x, byte[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);

    public int GetHashCode(byte[] obj)
    {
        var h = new HashCode();
        h.AddBytes(obj);
        return h.ToHashCode();
    }

    public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
}

/// <summary>
/// Reads and writes the shadow tables of one fts3/fts4 table with ordinary SQL on the connection, the way the table's own statements would.
/// Statements are prepared once and reused; every read copies its result out and resets the statement before returning.
/// </summary>
internal sealed class Fts3Db : IDisposable
{
    private readonly Connection _conn;
    private readonly Fts3Config _cfg;
    private readonly Dictionary<string, Statement> _stmts = [];

    internal Fts3Db(Connection conn, Fts3Config cfg)
    {
        _conn = conn;
        _cfg = cfg;
    }

    internal Connection Conn => _conn;

    internal Fts3Config Config => _cfg;

    /// <summary>The quoted name of a shadow table: <c>"main"."t_segdir"</c>.</summary>
    internal string T(string suffix) => "\"" + _cfg.Schema.Replace("\"", "\"\"", StringComparison.Ordinal) + "\".\"" + (_cfg.Table + suffix).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    internal Statement S(string sql)
    {
        if (!_stmts.TryGetValue(sql, out Statement? s))
        {
            s = _conn.Prepare(sql);
            _stmts[sql] = s;
        }
        else
        {
            s.Reset();
            if (s.ParameterCount > 0)
            {
                s.ClearBindings();
            }
        }

        return s;
    }

    internal void Exec(string sql, params object?[] args)
    {
        Statement s = S(sql);
        Bind(s, args);
        s.Step();
        s.Reset();
    }

    internal static void Bind(Statement s, object?[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case null:
                    s.BindNull(i + 1);
                    break;
                case long l:
                    s.Bind(i + 1, l);
                    break;
                case int n:
                    s.Bind(i + 1, (long)n);
                    break;
                case double d:
                    s.Bind(i + 1, d);
                    break;
                case string str:
                    s.Bind(i + 1, str);
                    break;
                case byte[] b:
                    s.Bind(i + 1, b);
                    break;
                default:
                    throw new InvalidOperationException("unsupported bind value");
            }
        }
    }

    public void Dispose()
    {
        foreach (Statement s in _stmts.Values)
        {
            s.Dispose();
        }

        _stmts.Clear();
    }

    // ---------------------------------------------------------------- segdir

    private static SegDirRow ReadSegDirRow(Statement s)
    {
        var r = new SegDirRow { Level = s.GetInt64(0), Idx = s.GetInt64(1), Start = s.GetInt64(2), LeavesEnd = s.GetInt64(3) };
        if (s.ColumnType(4) == ColumnType.Text)
        {
            string t = s.GetString(4) ?? "0";
            string[] parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && long.TryParse(parts[0], out long eb))
            {
                r.End = eb;
            }

            if (parts.Length > 1 && long.TryParse(parts[1], out long lb))
            {
                r.LeafBytes = lb;
            }
        }
        else
        {
            r.End = s.GetInt64(4);
        }

        r.Root = s.ColumnType(5) is ColumnType.Blob or ColumnType.Text ? s.GetBlob(5).ToArray() : [];
        return r;
    }

    /// <summary>The segments with level in lo..hi, newest first (smaller level, then larger idx).</summary>
    internal List<SegDirRow> ReadDir(long lo, long hi)
    {
        Statement s = S("SELECT level, idx, start_block, leaves_end_block, end_block, root FROM " + T("_segdir") + " WHERE level BETWEEN ?1 AND ?2 ORDER BY level ASC, idx DESC");
        s.Bind(1, lo);
        s.Bind(2, hi);
        var l = new List<SegDirRow>();
        while (s.Step())
        {
            l.Add(ReadSegDirRow(s));
        }

        s.Reset();
        return l;
    }

    internal byte[] ReadBlock(long id)
    {
        Statement s = S("SELECT block FROM " + T("_segments") + " WHERE blockid = ?1");
        s.Bind(1, id);
        byte[]? r = null;
        if (s.Step() && s.ColumnType(0) is ColumnType.Blob or ColumnType.Text)
        {
            r = s.GetBlob(0).ToArray();
        }

        s.Reset();
        return r ?? throw F3.Corrupt("fts3: segment block " + id + " is missing");
    }

    internal long NextBlockId()
    {
        Statement s = S("SELECT max(blockid) FROM " + T("_segments"));
        long v = 0;
        if (s.Step() && !s.IsNull(0))
        {
            v = s.GetInt64(0);
        }

        s.Reset();
        return v + 1;
    }

    // ---------------------------------------------------------------- scanning a segment

    /// <summary>Yields the terms of a segment in order, from the first term that is not less than <paramref name="from"/>.</summary>
    internal IEnumerable<(byte[] Term, byte[] Doclist)> Scan(SegDirRow seg, byte[]? from)
    {
        long firstLeaf = 0;
        if (seg.LeavesEnd == 0 && seg.Start == 0)
        {
            foreach ((byte[] t, byte[] d) in F3.ParseLeaf(seg.Root))
            {
                if (from is null || F3.CompareTerms(t, from) >= 0)
                {
                    yield return (t, d);
                }
            }

            yield break;
        }

        byte[] node = seg.Root;
        int expect = -1;
        for (; ; )
        {
            (int h, long left, List<byte[]> keys) = F3.ParseInterior(node);
            if (expect >= 0 && h != expect)
            {
                throw F3.Corrupt("fts3: interior node of the wrong height");
            }

            int k = 0;
            if (from is not null)
            {
                while (k < keys.Count && F3.CompareTerms(keys[k], from) <= 0)
                {
                    k++;
                }
            }

            long child = left + k;
            if (h == 1)
            {
                firstLeaf = child;
                break;
            }

            expect = h - 1;
            node = ReadBlock(child);
        }

        if (firstLeaf < seg.Start || firstLeaf > seg.LeavesEnd)
        {
            throw F3.Corrupt("fts3: child block outside the segment");
        }

        for (long id = firstLeaf; id <= seg.LeavesEnd; id++)
        {
            foreach ((byte[] t, byte[] d) in F3.ParseLeaf(ReadBlock(id)))
            {
                if (from is null || F3.CompareTerms(t, from) >= 0)
                {
                    yield return (t, d);
                }
            }
        }
    }

    // ---------------------------------------------------------------- writing a segment

    private sealed class Node
    {
        internal byte[] Bytes = [];
        internal byte[] First = [];
        internal byte[] Last = [];
    }

    private static byte[] Separator(byte[] prevLast, byte[] first)
    {
        for (int k = 1; k <= first.Length; k++)
        {
            if (first.AsSpan(0, k).SequenceCompareTo(prevLast) > 0)
            {
                return first[..k];
            }
        }

        return first;
    }

    /// <summary>
    /// Writes <paramref name="terms"/> (sorted by term) as a new segment b-tree at (<paramref name="level"/>, <paramref name="idx"/>):
    /// leaves of about <see cref="F3.NodeSize"/> bytes, interior levels above them and the top node in %_segdir.
    /// </summary>
    internal void WriteSegment(long level, long idx, IEnumerable<(byte[] Term, byte[] Doclist)> terms)
    {
        var leaves = new List<Node>();
        var cur = new List<byte> { 0 };
        byte[] prev = [];
        byte[]? firstInNode = null;
        foreach ((byte[] term, byte[] doclist) in terms)
        {
            int prefix = firstInNode is null ? 0 : F3.CommonPrefix(prev, term);
            int size = firstInNode is null
                ? F3.VarintLength(term.Length) + term.Length
                : F3.VarintLength(prefix) + F3.VarintLength(term.Length - prefix) + term.Length - prefix;
            size += F3.VarintLength(doclist.Length) + doclist.Length;
            if (firstInNode is not null && cur.Count + size > F3.NodeSize)
            {
                leaves.Add(new Node { Bytes = [.. cur], First = firstInNode, Last = prev });
                cur = [0];
                firstInNode = null;
                prefix = 0;
            }

            if (firstInNode is null)
            {
                F3.PutVarint(cur, term.Length);
                cur.AddRange(term);
                firstInNode = term;
            }
            else
            {
                F3.PutVarint(cur, prefix);
                F3.PutVarint(cur, term.Length - prefix);
                cur.AddRange(term.AsSpan(prefix).ToArray());
            }

            F3.PutVarint(cur, doclist.Length);
            cur.AddRange(doclist);
            prev = term;
        }

        if (firstInNode is null)
        {
            return;
        }

        leaves.Add(new Node { Bytes = [.. cur], First = firstInNode, Last = prev });

        var row = new SegDirRow { Level = level, Idx = idx };
        if (leaves.Count == 1)
        {
            row.Root = leaves[0].Bytes;
            row.LeafBytes = row.Root.Length;
            InsertSegDir(row);
            return;
        }

        long next = NextBlockId();
        row.Start = next;
        foreach (Node n in leaves)
        {
            Exec("INSERT INTO " + T("_segments") + "(blockid, block) VALUES(?1, ?2)", next++, n.Bytes);
            row.LeafBytes += n.Bytes.Length;
        }

        row.LeavesEnd = next - 1;
        row.End = next - 1;

        // children of the level being built: (first block id of the level, nodes)
        List<Node> level0 = leaves;
        int height = 1;
        for (; ; )
        {
            var parents = new List<Node>();
            var group = new List<byte>();
            Node? gFirst = null;
            Node? gLast = null;
            int gCount = 0;
            long gLeftId = 0;
            long childId = next - level0.Count; // block id of level0[0]
            if (height == 1)
            {
                childId = row.Start;
            }

            byte[] keyPrev = [];
            for (int i = 0; i < level0.Count; i++)
            {
                Node child = level0[i];
                if (gCount == 0)
                {
                    group = [];
                    F3.PutVarint(group, height);
                    gLeftId = childId + i;
                    F3.PutVarint(group, gLeftId);
                    gFirst = child;
                    gCount = 1;
                    gLast = child;
                    continue;
                }

                byte[] key = Separator(gLast!.Last, child.First);
                int prefix = gCount == 1 ? 0 : F3.CommonPrefix(keyPrev, key);
                int add = gCount == 1
                    ? F3.VarintLength(key.Length) + key.Length
                    : F3.VarintLength(prefix) + F3.VarintLength(key.Length - prefix) + key.Length - prefix;
                if (gCount >= 2 && group.Count + add > F3.NodeSize)
                {
                    parents.Add(new Node { Bytes = [.. group], First = gFirst!.First, Last = gLast.Last });
                    group = [];
                    F3.PutVarint(group, height);
                    gLeftId = childId + i;
                    F3.PutVarint(group, gLeftId);
                    gFirst = child;
                    gLast = child;
                    gCount = 1;
                    continue;
                }

                if (gCount == 1)
                {
                    F3.PutVarint(group, key.Length);
                    group.AddRange(key);
                }
                else
                {
                    F3.PutVarint(group, prefix);
                    F3.PutVarint(group, key.Length - prefix);
                    group.AddRange(key.AsSpan(prefix).ToArray());
                }

                keyPrev = key;
                gLast = child;
                gCount++;
            }

            parents.Add(new Node { Bytes = [.. group], First = gFirst!.First, Last = gLast!.Last });
            if (parents.Count == 1)
            {
                row.Root = parents[0].Bytes;
                break;
            }

            foreach (Node n in parents)
            {
                Exec("INSERT INTO " + T("_segments") + "(blockid, block) VALUES(?1, ?2)", next++, n.Bytes);
            }

            row.End = next - 1;
            level0 = parents;
            height++;
            childId = next - parents.Count;
        }

        InsertSegDir(row);
    }

    private void InsertSegDir(SegDirRow r)
    {
        Exec(
            "INSERT INTO " + T("_segdir") + "(level, idx, start_block, leaves_end_block, end_block, root) VALUES(?1, ?2, ?3, ?4, ?5, ?6)",
            r.Level,
            r.Idx,
            r.Start,
            r.LeavesEnd,
            r.End.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + r.LeafBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            r.Root);
    }

    /// <summary>Removes a segment: its row in %_segdir and its blocks in %_segments.</summary>
    internal void DeleteSegment(SegDirRow r)
    {
        if (r.Start != 0 || r.LeavesEnd != 0)
        {
            Exec("DELETE FROM " + T("_segments") + " WHERE blockid BETWEEN ?1 AND ?2", r.Start, r.End);
        }

        Exec("DELETE FROM " + T("_segdir") + " WHERE level = ?1 AND idx = ?2", r.Level, r.Idx);
    }

    // ---------------------------------------------------------------- merging

    private sealed class Head(IEnumerator<(byte[] Term, byte[] Doclist)> e, int rank)
    {
        internal IEnumerator<(byte[] Term, byte[] Doclist)> E = e;
        internal int Rank = rank; // lower = newer
        internal bool Has;
    }

    /// <summary>The merged terms of <paramref name="segs"/> (newest first): for each term the newest entry of every docid; delete markers are dropped when asked.</summary>
    internal IEnumerable<(byte[] Term, byte[] Doclist)> Merge(List<SegDirRow> segs, bool dropDeletes)
    {
        var heads = new List<Head>();
        for (int i = 0; i < segs.Count; i++)
        {
            var h = new Head(Scan(segs[i], null).GetEnumerator(), i);
            h.Has = h.E.MoveNext();
            heads.Add(h);
        }

        try
        {
            for (; ; )
            {
                byte[]? min = null;
                foreach (Head h in heads)
                {
                    if (h.Has && (min is null || F3.CompareTerms(h.E.Current.Term, min) < 0))
                    {
                        min = h.E.Current.Term;
                    }
                }

                if (min is null)
                {
                    yield break;
                }

                var merged = new Dictionary<long, F3.Entry>();
                foreach (Head h in heads.OrderBy(x => x.Rank))
                {
                    if (h.Has && F3.CompareTerms(h.E.Current.Term, min) == 0)
                    {
                        foreach (F3.Entry e in F3.ParseDoclist(h.E.Current.Doclist, _cfg.Descending))
                        {
                            merged.TryAdd(e.Docid, e);
                        }

                        h.Has = h.E.MoveNext();
                    }
                }

                IEnumerable<F3.Entry> list = dropDeletes ? merged.Values.Where(e => !e.IsDelete) : merged.Values;
                List<F3.Entry> sorted = _cfg.Descending ? [.. list.OrderByDescending(e => e.Docid)] : [.. list.OrderBy(e => e.Docid)];
                if (sorted.Count > 0)
                {
                    yield return (min, F3.BuildDoclist(sorted, _cfg.Descending));
                }
            }
        }
        finally
        {
            foreach (Head h in heads)
            {
                h.E.Dispose();
            }
        }
    }

    /// <summary>Merges <paramref name="segs"/> (newest first) into one segment at (<paramref name="level"/>, <paramref name="idx"/>) and removes the inputs.</summary>
    internal void MergeInto(List<SegDirRow> segs, long level, long idx, bool dropDeletes)
    {
        List<(byte[], byte[])> all = [.. Merge(segs, dropDeletes)];
        foreach (SegDirRow s in segs)
        {
            DeleteSegment(s);
        }

        WriteSegment(level, idx, all);
    }

    internal long MaxIdx(long level)
    {
        Statement s = S("SELECT max(idx) FROM " + T("_segdir") + " WHERE level = ?1");
        s.Bind(1, level);
        long v = -1;
        if (s.Step() && !s.IsNull(0))
        {
            v = s.GetInt64(0);
        }

        s.Reset();
        return v;
    }

    internal int CountAtLevel(long level)
    {
        Statement s = S("SELECT count(*) FROM " + T("_segdir") + " WHERE level = ?1");
        s.Bind(1, level);
        int v = 0;
        if (s.Step())
        {
            v = (int)s.GetInt64(0);
        }

        s.Reset();
        return v;
    }

    /// <summary>Merges the segments of a level into one at the next level whenever <paramref name="threshold"/> or more sit there, level by level upwards.</summary>
    internal bool Cascade(long familyBase, int threshold)
    {
        List<SegDirRow> dir = ReadDir(familyBase, familyBase + F3.LevelsPerIndex - 1);
        bool did = false;
        for (long l = familyBase; l < familyBase + F3.LevelsPerIndex - 1; l++)
        {
            List<SegDirRow> here = [.. dir.Where(r => r.Level == l)];
            if (here.Count < threshold || here.Count < 2)
            {
                continue;
            }

            bool older = dir.Any(r => r.Level > l);
            long idx = dir.Where(r => r.Level == l + 1).Select(r => r.Idx).DefaultIfEmpty(-1).Max() + 1;
            MergeInto(here, l + 1, idx, !older);
            dir = ReadDir(familyBase, familyBase + F3.LevelsPerIndex - 1);
            did = true;
        }

        return did;
    }

    /// <summary>Merges every segment of the index family starting at <paramref name="familyBase"/> into one at its level 0.</summary>
    internal void Optimize(long familyBase)
    {
        List<SegDirRow> segs = ReadDir(familyBase, familyBase + F3.LevelsPerIndex - 1);
        if (segs.Count == 0)
        {
            return;
        }

        MergeInto(segs, familyBase, 0, true);
    }

    internal static string Hex(byte[] b) => Convert.ToHexString(b);

    internal static string Utf8(byte[] b) => Encoding.UTF8.GetString(b);
}
