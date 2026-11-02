using System;
using System.Collections.Generic;

namespace RecursiveAI.OneLite.History;

/// <summary>
/// Kinds of dependency edge between transactions (Adya, "Weak Consistency", section 3; Elle, section 3).
/// </summary>
[Flags]
public enum EdgeKind : byte
{
    /// <summary>Write-write: the source installed a version, the target installed the next one.</summary>
    Ww = 1,

    /// <summary>Write-read: the target read a version the source installed.</summary>
    Wr = 2,

    /// <summary>Read-write (anti-dependency): the source read a version, the target installed the next one.</summary>
    Rw = 4,

    /// <summary>Real-time: the source completed before the target was invoked.</summary>
    Rt = 8,
}

/// <summary>An edge as collected before the graph is compacted.</summary>
internal readonly record struct Edge(int From, int To, EdgeKind Kind, int Key);

/// <summary>One step of a reported cycle.</summary>
/// <param name="From">Index of the source transaction.</param>
/// <param name="To">Index of the target transaction.</param>
/// <param name="Kind">The dependency kind.</param>
/// <param name="Key">The key the dependency is on (-1 for real-time edges).</param>
public readonly record struct CycleStep(int From, int To, EdgeKind Kind, int Key);

/// <summary>The dependency graph in compressed adjacency form.</summary>
internal sealed class DepGraph
{
    public DepGraph(int n, List<Edge> edges)
    {
        N = n;
        Start = new int[n + 1];
        foreach (var e in edges)
        {
            Start[e.From + 1]++;
        }

        for (var i = 0; i < n; i++)
        {
            Start[i + 1] += Start[i];
        }

        To = new int[edges.Count];
        Kind = new EdgeKind[edges.Count];
        Key = new int[edges.Count];
        var fill = new int[n];
        foreach (var e in edges)
        {
            var at = Start[e.From] + fill[e.From]++;
            To[at] = e.To;
            Kind[at] = e.Kind;
            Key[at] = e.Key;
        }
    }

    public int N { get; }

    public int[] Start { get; }

    public int[] To { get; }

    public EdgeKind[] Kind { get; }

    public int[] Key { get; }

    public int EdgeCount => To.Length;

    public int CountKind(EdgeKind k)
    {
        var c = 0;
        foreach (var x in Kind)
        {
            if (x == k)
            {
                c++;
            }
        }

        return c;
    }

    /// <summary>
    /// Strongly connected components over the edges whose kind is in <paramref name="mask"/> (Tarjan, iterative so deep graphs
    /// cannot overflow the stack). Returns a component id per node; a node alone in its component gets a unique id and has no cycle through it.
    /// </summary>
    public int[] Components(EdgeKind mask)
    {
        var index = new int[N];
        var low = new int[N];
        var comp = new int[N];
        Array.Fill(index, -1);
        Array.Fill(comp, -1);
        var onStack = new bool[N];
        var stack = new int[N];
        var sp = 0;
        var callNode = new int[N];
        var callEdge = new int[N];
        var next = 0;
        var nComp = 0;
        for (var root = 0; root < N; root++)
        {
            if (index[root] != -1)
            {
                continue;
            }

            var cp = 0;
            callNode[0] = root;
            callEdge[0] = Start[root];
            index[root] = low[root] = next++;
            stack[sp++] = root;
            onStack[root] = true;
            while (cp >= 0)
            {
                var v = callNode[cp];
                if (callEdge[cp] < Start[v + 1])
                {
                    var e = callEdge[cp]++;
                    if ((Kind[e] & mask) == 0)
                    {
                        continue;
                    }

                    var w = To[e];
                    if (index[w] == -1)
                    {
                        index[w] = low[w] = next++;
                        stack[sp++] = w;
                        onStack[w] = true;
                        cp++;
                        callNode[cp] = w;
                        callEdge[cp] = Start[w];
                    }
                    else if (onStack[w] && index[w] < low[v])
                    {
                        low[v] = index[w];
                    }
                }
                else
                {
                    if (low[v] == index[v])
                    {
                        int w;
                        do
                        {
                            w = stack[--sp];
                            onStack[w] = false;
                            comp[w] = nComp;
                        }
                        while (w != v);
                        nComp++;
                    }

                    cp--;
                    if (cp >= 0)
                    {
                        var parent = callNode[cp];
                        if (low[v] < low[parent])
                        {
                            low[parent] = low[v];
                        }
                    }
                }
            }
        }

        return comp;
    }

    /// <summary>The number of nodes in each component.</summary>
    public static int[] ComponentSizes(int[] comp)
    {
        var max = 0;
        foreach (var c in comp)
        {
            if (c > max)
            {
                max = c;
            }
        }

        var sizes = new int[max + 1];
        foreach (var c in comp)
        {
            sizes[c]++;
        }

        return sizes;
    }
}

/// <summary>
/// Finds a shortest cycle through a given seed edge by breadth-first search over (node, flags) states.
/// </summary>
internal sealed class CycleFinder
{
    private readonly DepGraph _g;
    private readonly int[] _stamp;
    private readonly int[] _parentState;
    private readonly int[] _parentEdge;
    private int _cur;
    private int[] _queue = new int[1024];

    public CycleFinder(DepGraph g)
    {
        _g = g;
        _stamp = new int[g.N * 4];
        _parentState = new int[g.N * 4];
        _parentEdge = new int[g.N * 4];
    }

    /// <summary>
    /// Searches for a path from <paramref name="v"/> back to <paramref name="u"/> that closes the seed edge u→v.
    /// </summary>
    /// <param name="seedEdge">The CSR index of the seed edge.</param>
    /// <param name="u">Seed source.</param>
    /// <param name="v">Seed target.</param>
    /// <param name="pathMask">Edge kinds the path may use.</param>
    /// <param name="comp">Component ids; the path stays inside the seed's component.</param>
    /// <param name="needExtraRw">The seed is an anti-dependency and the path must contain at least one more.</param>
    /// <param name="nonAdjacent">No two anti-dependencies may be consecutive around the cycle (Fekete et al.: such cycles cannot occur under snapshot isolation).</param>
    public List<CycleStep>? Find(int seedEdge, int u, int v, EdgeKind pathMask, int[] comp, bool needExtraRw, bool nonAdjacent)
    {
        var c = comp[u];
        _cur++;
        var head = 0;
        var tail = 0;
        var startState = (v * 4) + (nonAdjacent ? 1 : 0);
        _stamp[startState] = _cur;
        _parentState[startState] = -1;
        Enqueue(ref tail, startState);
        while (head < tail)
        {
            var s = _queue[head++];
            var node = s >> 2;
            var flag = (s >> 1) & 1;
            var last = s & 1;
            for (var e = _g.Start[node]; e < _g.Start[node + 1]; e++)
            {
                var k = _g.Kind[e];
                if ((k & pathMask) == 0)
                {
                    continue;
                }

                var w = _g.To[e];
                if (comp[w] != c)
                {
                    continue;
                }

                var isRw = k == EdgeKind.Rw;
                if (nonAdjacent && isRw && last == 1)
                {
                    continue;
                }

                var nf = (needExtraRw && (flag == 1 || isRw)) ? 1 : 0;
                var nl = (nonAdjacent && isRw) ? 1 : 0;
                if (w == u)
                {
                    if (needExtraRw && nf == 0)
                    {
                        continue;
                    }

                    if (nonAdjacent && isRw)
                    {
                        continue;
                    }

                    var path = Reconstruct(s, e, node, seedEdge, u, v);
                    if (nonAdjacent && !IsSimple(path))
                    {
                        return null;
                    }

                    return path;
                }

                var ns = (w * 4) + (nf * 2) + nl;
                if (_stamp[ns] == _cur)
                {
                    continue;
                }

                _stamp[ns] = _cur;
                _parentState[ns] = s;
                _parentEdge[ns] = e;
                Enqueue(ref tail, ns);
            }
        }

        return null;
    }

    private static bool IsSimple(List<CycleStep> path)
    {
        var seen = new HashSet<int>();
        foreach (var s in path)
        {
            if (!seen.Add(s.From))
            {
                return false;
            }
        }

        return true;
    }

    private List<CycleStep> Reconstruct(int lastState, int lastEdge, int lastNode, int seedEdge, int u, int v)
    {
        var rev = new List<CycleStep> { new(lastNode, u, _g.Kind[lastEdge], _g.Key[lastEdge]) };
        var s = lastState;
        while (_parentState[s] != -1)
        {
            var e = _parentEdge[s];
            var ps = _parentState[s];
            rev.Add(new CycleStep(ps >> 2, s >> 2, _g.Kind[e], _g.Key[e]));
            s = ps;
        }

        rev.Add(new CycleStep(u, v, _g.Kind[seedEdge], _g.Key[seedEdge]));
        rev.Reverse();
        return rev;
    }

    private void Enqueue(ref int tail, int s)
    {
        if (tail == _queue.Length)
        {
            Array.Resize(ref _queue, _queue.Length * 2);
        }

        _queue[tail++] = s;
    }
}
