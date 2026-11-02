using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace RecursiveAI.OneLite.History;

/// <summary>Options for <see cref="Checker.Check"/>.</summary>
public sealed class CheckOptions
{
    /// <summary>Add real-time edges and look for real-time cycles (needed to verdict strict serializability). Default true.</summary>
    public bool Realtime { get; set; } = true;

    /// <summary>Register model only: also infer version order from real-time order. Sound only for systems that claim strict serializability. Default false.</summary>
    public bool RealtimeVersions { get; set; }

    /// <summary>Examples kept per anomaly class. Counts are not capped by this. Default 8.</summary>
    public int MaxExamples { get; set; } = 8;

    /// <summary>Seed edges tried per cycle class before the search gives up (bounds the time spent on a badly broken history). Default 2000.</summary>
    public int MaxSeeds { get; set; } = 2000;
}

/// <summary>The outcome of checking a history.</summary>
public sealed class CheckResult
{
    internal CheckResult(History h, AnomalySink sink, Dictionary<EdgeKind, int> edgeCounts, TimeSpan elapsed, bool realtime, bool seedsCapped)
    {
        History = h;
        Examples = sink.Examples;
        var counts = new Dictionary<AnomalyClass, int>();
        foreach (var c in Enum.GetValues<AnomalyClass>())
        {
            if (sink.Count(c) > 0)
            {
                counts[c] = sink.Count(c);
            }
        }

        Counts = counts;
        EdgeCounts = edgeCounts;
        Elapsed = elapsed;
        RealtimeChecked = realtime;
        SearchCapped = seedsCapped;
    }

    /// <summary>The history that was checked.</summary>
    public History History { get; }

    /// <summary>Example anomalies (the first few of each class).</summary>
    public IReadOnlyList<Anomaly> Examples { get; }

    /// <summary>Number of anomalies found per class (cycle classes are bounded by the seed limit).</summary>
    public IReadOnlyDictionary<AnomalyClass, int> Counts { get; }

    /// <summary>Edges in the dependency graph by kind.</summary>
    public IReadOnlyDictionary<EdgeKind, int> EdgeCounts { get; }

    /// <summary>Time spent checking.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>Whether real-time edges were used; without them a strict-serializable verdict is only as strong as the serializable one.</summary>
    public bool RealtimeChecked { get; }

    /// <summary>True when a cycle search stopped at its seed limit, so the counts are lower bounds.</summary>
    public bool SearchCapped { get; }

    /// <summary>True when the history has no anomaly that <paramref name="level"/> forbids.</summary>
    public bool IsValid(IsolationLevel level)
    {
        foreach (var (c, _) in Counts)
        {
            if (Levels.Forbids(level, c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The classes found that <paramref name="level"/> forbids.</summary>
    public IEnumerable<AnomalyClass> Violations(IsolationLevel level)
    {
        foreach (var (c, _) in Counts)
        {
            if (Levels.Forbids(level, c))
            {
                yield return c;
            }
        }
    }
}

/// <summary>
/// The history checker: infers the dependency graph of a history and searches it for the anomalies of Adya's taxonomy.
/// </summary>
public static class Checker
{
    /// <summary>Checks a history.</summary>
    /// <exception cref="HistoryFormatException">The history breaks the workload's uniqueness rules.</exception>
    public static CheckResult Check(History h, CheckOptions? options = null)
    {
        options ??= new CheckOptions();
        var sw = Stopwatch.StartNew();
        var sink = new AnomalySink(options.MaxExamples);
        var ctx = new AnalysisContext(h, sink);
        if (h.Model == HistoryModel.ListAppend)
        {
            new ListAppendAnalyzer(ctx).Run();
        }
        else
        {
            new RegisterAnalyzer(ctx, options.RealtimeVersions).Run();
        }

        ctx.Writer.Clear();
        ctx.NonFinal.Clear();
        if (options.Realtime)
        {
            var live = new List<int>();
            for (var i = 0; i < ctx.Txns.Count; i++)
            {
                if (ctx.IsLive(i))
                {
                    live.Add(i);
                }
            }

            ctx.RealtimeEdges(live, (a, b) => ctx.Edges.Add(new Edge(a, b, EdgeKind.Rt, -1)));
        }

        var g = new DepGraph(ctx.Txns.Count, ctx.Edges);
        ctx.Edges.Clear();
        ctx.Edges.Capacity = 0;
        var search = new CycleSearch(h, g, sink, options);
        search.Run(options.Realtime);
        var counts = new Dictionary<EdgeKind, int>
        {
            [EdgeKind.Ww] = g.CountKind(EdgeKind.Ww),
            [EdgeKind.Wr] = g.CountKind(EdgeKind.Wr),
            [EdgeKind.Rw] = g.CountKind(EdgeKind.Rw),
            [EdgeKind.Rt] = g.CountKind(EdgeKind.Rt),
        };
        sw.Stop();
        return new CheckResult(h, sink, counts, sw.Elapsed, options.Realtime, search.Capped);
    }
}

internal sealed class CycleSearch
{
    private readonly History _h;
    private readonly DepGraph _g;
    private readonly AnomalySink _sink;
    private readonly CheckOptions _o;
    private CycleFinder? _finder;

    public CycleSearch(History h, DepGraph g, AnomalySink sink, CheckOptions o)
    {
        _h = h;
        _g = g;
        _sink = sink;
        _o = o;
    }

    public bool Capped { get; private set; }

    public void Run(bool realtime)
    {
        // Each class is searched inside the components of the smallest edge set that can hold it, so an acyclic graph costs one pass.
        const EdgeKind WwWr = EdgeKind.Ww | EdgeKind.Wr;
        const EdgeKind Dep = EdgeKind.Ww | EdgeKind.Wr | EdgeKind.Rw;
        Search(AnomalyClass.G0, EdgeKind.Ww, EdgeKind.Ww, EdgeKind.Ww, false, false);
        Search(AnomalyClass.G1c, EdgeKind.Wr, WwWr, WwWr, false, false);
        Search(AnomalyClass.GSingle, EdgeKind.Rw, WwWr, Dep, false, false);
        Search(AnomalyClass.GNonadjacent, EdgeKind.Rw, Dep, Dep, true, true);
        Search(AnomalyClass.G2Item, EdgeKind.Rw, Dep, Dep, true, false);
        if (realtime)
        {
            Search(AnomalyClass.G0Realtime, EdgeKind.Rt, Dep | EdgeKind.Rt, Dep | EdgeKind.Rt, false, false);
        }
    }

    /// <summary>
    /// Tries every edge of kind <paramref name="seed"/> inside a component of <paramref name="compMask"/> as the start of a cycle.
    /// </summary>
    private void Search(AnomalyClass cls, EdgeKind seed, EdgeKind pathMask, EdgeKind compMask, bool needExtraRw, bool nonAdjacent)
    {
        var comp = _g.Components(compMask);
        var sizes = DepGraph.ComponentSizes(comp);
        var seen = new HashSet<string>();
        var attempts = 0;
        for (var u = 0; u < _g.N; u++)
        {
            if (sizes[comp[u]] < 2)
            {
                continue;
            }

            for (var e = _g.Start[u]; e < _g.Start[u + 1]; e++)
            {
                if (_g.Kind[e] != seed)
                {
                    continue;
                }

                var v = _g.To[e];
                if (comp[v] != comp[u])
                {
                    continue;
                }

                if (++attempts > _o.MaxSeeds)
                {
                    Capped = true;
                    return;
                }

                _finder ??= new CycleFinder(_g);
                var path = _finder.Find(e, u, v, pathMask, comp, needExtraRw, nonAdjacent);
                if (path is null)
                {
                    continue;
                }

                var actual = cls;
                if (cls == AnomalyClass.G0Realtime)
                {
                    actual = ClassifyRealtime(path);
                }

                if (!seen.Add(Canonical(path, actual)))
                {
                    continue;
                }

                if (_sink.Record(actual))
                {
                    _sink.Add(new Anomaly(actual, Explain(actual, path), Nodes(path), path));
                }
            }
        }
    }

    private static AnomalyClass ClassifyRealtime(List<CycleStep> path)
    {
        var rw = 0;
        var wr = false;
        foreach (var s in path)
        {
            if (s.Kind == EdgeKind.Rw)
            {
                rw++;
            }
            else if (s.Kind == EdgeKind.Wr)
            {
                wr = true;
            }
        }

        if (rw >= 2)
        {
            return AnomalyClass.G2ItemRealtime;
        }

        if (rw == 1)
        {
            return AnomalyClass.GSingleRealtime;
        }

        return wr ? AnomalyClass.G1cRealtime : AnomalyClass.G0Realtime;
    }

    private static string Canonical(List<CycleStep> path, AnomalyClass cls)
    {
        var nodes = Nodes(path);
        Array.Sort(nodes);
        return (int)cls + ":" + string.Join(',', nodes);
    }

    private static int[] Nodes(List<CycleStep> path)
    {
        var n = new int[path.Count];
        for (var i = 0; i < n.Length; i++)
        {
            n[i] = path[i].From;
        }

        return n;
    }

    private string Explain(AnomalyClass cls, List<CycleStep> path)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(Levels.Label(cls)).Append(" cycle of ").Append(path.Count).Append(" transactions: ");
        foreach (var s in path)
        {
            sb.Append('T').Append(_h.Txns[s.From].Id).Append(" --").Append(KindName(s.Kind));
            if (s.Key >= 0)
            {
                sb.Append("(key ").Append(s.Key).Append(')');
            }

            sb.Append("--> ");
        }

        sb.Append('T').Append(_h.Txns[path[0].From].Id);
        return sb.ToString();
    }

    internal static string KindName(EdgeKind k) => k switch
    {
        EdgeKind.Ww => "ww",
        EdgeKind.Wr => "wr",
        EdgeKind.Rw => "rw",
        _ => "rt",
    };
}
