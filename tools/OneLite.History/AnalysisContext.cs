using System;
using System.Collections.Generic;
using System.Text;

namespace RecursiveAI.OneLite.History;

/// <summary>State shared by the model analyzers: the history, the edge list, the anomaly sink and the writer index.</summary>
internal sealed class AnalysisContext
{
    public AnalysisContext(History h, AnomalySink sink)
    {
        History = h;
        Sink = sink;
        Txns = h.Txns;
    }

    public History History { get; }

    public List<Txn> Txns { get; }

    public AnomalySink Sink { get; }

    public List<Edge> Edges { get; } = [];

    /// <summary>(key, value) → index of the transaction that wrote or appended it.</summary>
    public Dictionary<long, int> Writer { get; } = [];

    /// <summary>(key, value) pairs that the writer overwrote or appended past within the same transaction (intermediate versions).</summary>
    public HashSet<long> NonFinal { get; } = [];

    public static long Kv(int key, int value) => ((long)key << 32) | (uint)value;

    public bool IsLive(int t) => Txns[t].Outcome != TxnOutcome.Fail;

    public void AddEdge(int from, int to, EdgeKind kind, int key)
    {
        if (from != to)
        {
            Edges.Add(new Edge(from, to, kind, key));
        }
    }

    /// <summary>Indexes every write; values must be unique per key (the workload's contract, which Elle also relies on).</summary>
    public void IndexWriters()
    {
        var lastSeen = new HashSet<int>();
        for (var i = 0; i < Txns.Count; i++)
        {
            var t = Txns[i];
            foreach (var m in t.Mops)
            {
                if (m.Kind == MopKind.Read)
                {
                    continue;
                }

                if (m.Value == 0 && m.Kind == MopKind.Write)
                {
                    throw new HistoryFormatException("T" + t.Id + " writes the reserved nil value 0 to key " + m.Key);
                }

                if (!Writer.TryAdd(Kv(m.Key, m.Value), i))
                {
                    throw new HistoryFormatException("value " + m.Value + " was written to key " + m.Key + " more than once (T" + Txns[Writer[Kv(m.Key, m.Value)]].Id + " and T" + t.Id + "); writes must be unique per key");
                }
            }

            lastSeen.Clear();
            for (var j = t.Mops.Length - 1; j >= 0; j--)
            {
                var m = t.Mops[j];
                if (m.Kind == MopKind.Read)
                {
                    continue;
                }

                if (!lastSeen.Add(m.Key))
                {
                    NonFinal.Add(Kv(m.Key, m.Value));
                }
            }
        }
    }

    /// <summary>
    /// Calls <paramref name="add"/> for a sparse set of real-time edges whose transitive closure is every pair (a, b) with
    /// a completed before b was invoked. A completed transaction stays in the frontier until a later-invoked, completed one covers it.
    /// </summary>
    public void RealtimeEdges(List<int> nodes, Action<int, int> add)
    {
        var n = nodes.Count;
        var byInvoke = new int[n];
        var invokeKey = new long[n];
        var byComplete = new List<int>(n);
        var completeKey = new List<long>(n);
        for (var i = 0; i < n; i++)
        {
            byInvoke[i] = nodes[i];
            invokeKey[i] = Txns[nodes[i]].Invoke;
            if (Txns[nodes[i]].Complete != Txn.NeverCompleted)
            {
                byComplete.Add(nodes[i]);
                completeKey.Add(Txns[nodes[i]].Complete);
            }
        }

        Array.Sort(invokeKey, byInvoke);
        var cArr = byComplete.ToArray();
        var cKey = completeKey.ToArray();
        Array.Sort(cKey, cArr);
        var frontier = new List<int>();
        var ci = 0;
        for (var i = 0; i < n; i++)
        {
            var t = byInvoke[i];
            var inv = Txns[t].Invoke;
            while (ci < cArr.Length && cKey[ci] < inv)
            {
                var x = cArr[ci++];
                var xi = Txns[x].Invoke;
                frontier.RemoveAll(y => Txns[y].Complete < xi);
                frontier.Add(x);
            }

            foreach (var y in frontier)
            {
                add(y, t);
            }
        }
    }

    public string Describe(int t)
    {
        var x = Txns[t];
        var sb = new StringBuilder();
        sb.Append('T').Append(x.Id).Append(" (p").Append(x.Process).Append(' ').Append(x.Outcome.ToString().ToLowerInvariant()).Append("):");
        foreach (var m in x.Mops)
        {
            sb.Append(' ');
            switch (m.Kind)
            {
                case MopKind.Read:
                    sb.Append("r ").Append(m.Key).Append(' ');
                    if (m.List is { } l)
                    {
                        sb.Append('[').Append(string.Join(',', l)).Append(']');
                    }
                    else
                    {
                        sb.Append(m.Value);
                    }

                    break;
                case MopKind.Write:
                    sb.Append("w ").Append(m.Key).Append(' ').Append(m.Value);
                    break;
                default:
                    sb.Append("a ").Append(m.Key).Append(' ').Append(m.Value);
                    break;
            }

            sb.Append(';');
        }

        return sb.ToString();
    }

    public void Report(AnomalyClass cls, string message, params int[] txns)
    {
        if (Sink.Record(cls))
        {
            Sink.Add(new Anomaly(cls, message, txns, null));
        }
    }

    /// <summary>
    /// Berenson et al. P4: two committed transactions read the same version of a key and both then wrote it. Neither SI
    /// (first committer wins) nor serializability allows it, and no later read is needed to see it.
    /// </summary>
    public void NoteReadThenWrite(Dictionary<(int Key, long Version), int> seen, int txn, int key, long versionId)
    {
        if (seen.TryGetValue((key, versionId), out var other))
        {
            Report(AnomalyClass.LostUpdate, "T" + Txns[other].Id + " and T" + Txns[txn].Id + " both read the same version of key " + key + " and both wrote it", other, txn);
        }
        else
        {
            seen[(key, versionId)] = txn;
        }
    }
}
