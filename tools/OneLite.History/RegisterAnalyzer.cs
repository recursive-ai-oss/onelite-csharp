using System;
using System.Collections.Generic;

namespace RecursiveAI.OneLite.History;

/// <summary>
/// Infers dependencies from a read/write register history with unique writes (Kingsbury and Alvaro, "Elle", section 5).
/// A register read says nothing about version order, so the order is inferred only from what a transaction itself did:
/// its writes to a key happen in program order, and a write that follows a read of the same key installs a later version
/// than the one read (writes-follow-reads, true under snapshot isolation and serializability). Optionally a transaction that
/// completed before another began is ordered before it on the keys both wrote (real-time versions; sound for strict serializable systems).
/// The initial nil (0) precedes every version.
/// </summary>
internal sealed class RegisterAnalyzer
{
    private readonly AnalysisContext _c;
    private readonly bool _realtimeVersions;

    public RegisterAnalyzer(AnalysisContext c, bool realtimeVersions)
    {
        _c = c;
        _realtimeVersions = realtimeVersions;
    }

    private sealed class RegState
    {
        public bool Known;
        public int Value;
        public bool LastWasWrite;
        public bool FirstWasRead;
        public bool HasExt;
        public int ExtValue;
        public bool Wrote;
        public int FinalWrite;
    }

    public void Run()
    {
        _c.IndexWriters();
        var versionEdges = new HashSet<(int Key, int A, int B)>();
        var views = new List<(int Txn, int Key, int Value)>();
        var rtw = new Dictionary<(int Key, long Version), int>();
        var states = new Dictionary<int, RegState>();
        var txns = _c.Txns;
        for (var i = 0; i < txns.Count; i++)
        {
            var t = txns[i];
            if (t.Outcome != TxnOutcome.Ok)
            {
                continue;
            }

            states.Clear();
            foreach (var m in t.Mops)
            {
                if (!states.TryGetValue(m.Key, out var st))
                {
                    st = new RegState { FirstWasRead = m.Kind == MopKind.Read };
                    states[m.Key] = st;
                }

                if (m.Kind == MopKind.Read)
                {
                    if (st.Known)
                    {
                        if (m.Value != st.Value)
                        {
                            _c.Report(
                                st.LastWasWrite ? AnomalyClass.Internal : AnomalyClass.NonRepeatableRead,
                                "T" + t.Id + " read key " + m.Key + " = " + m.Value + " but " + (st.LastWasWrite ? "its own last write" : "its own previous read") + " gave " + st.Value,
                                i);
                        }
                    }
                    else
                    {
                        st.HasExt = true;
                        st.ExtValue = m.Value;
                        views.Add((i, m.Key, m.Value));
                    }

                    st.Known = true;
                    st.Value = m.Value;
                    st.LastWasWrite = false;
                }
                else
                {
                    if (st.Wrote && st.FinalWrite != m.Value)
                    {
                        versionEdges.Add((m.Key, st.FinalWrite, m.Value));
                    }

                    st.Wrote = true;
                    st.FinalWrite = m.Value;
                    st.Known = true;
                    st.Value = m.Value;
                    st.LastWasWrite = true;
                }
            }

            foreach (var (key, st) in states)
            {
                if (st.HasExt && st.Wrote)
                {
                    if (st.ExtValue != st.FinalWrite)
                    {
                        versionEdges.Add((key, st.ExtValue, st.FinalWrite));
                    }

                    if (st.FirstWasRead)
                    {
                        _c.NoteReadThenWrite(rtw, i, key, st.ExtValue);
                    }
                }
            }
        }

        // Final versions per key, from every transaction that may have committed.
        var finals = new Dictionary<int, List<(int Txn, int Val)>>();
        for (var i = 0; i < txns.Count; i++)
        {
            if (txns[i].Outcome == TxnOutcome.Fail)
            {
                continue;
            }

            foreach (var m in txns[i].Mops)
            {
                if (m.Kind == MopKind.Write && !_c.NonFinal.Contains(AnalysisContext.Kv(m.Key, m.Value)))
                {
                    if (!finals.TryGetValue(m.Key, out var l))
                    {
                        l = [];
                        finals[m.Key] = l;
                    }

                    l.Add((i, m.Value));
                }
            }
        }

        if (_realtimeVersions)
        {
            foreach (var (key, l) in finals)
            {
                var valueOf = new Dictionary<int, int>(l.Count);
                var nodes = new List<int>(l.Count);
                foreach (var (tx, val) in l)
                {
                    valueOf[tx] = val;
                    nodes.Add(tx);
                }

                var k = key;
                _c.RealtimeEdges(nodes, (a, b) => versionEdges.Add((k, valueOf[a], valueOf[b])));
            }
        }

        var succ = new Dictionary<long, List<int>>();
        var hasIncoming = new HashSet<long>();
        foreach (var (key, a, b) in versionEdges)
        {
            if (!succ.TryGetValue(AnalysisContext.Kv(key, a), out var l))
            {
                l = [];
                succ[AnalysisContext.Kv(key, a)] = l;
            }

            l.Add(b);
            hasIncoming.Add(AnalysisContext.Kv(key, b));
        }

        foreach (var (key, l) in finals)
        {
            foreach (var (_, val) in l)
            {
                if (!hasIncoming.Contains(AnalysisContext.Kv(key, val)))
                {
                    var nilKey = AnalysisContext.Kv(key, 0);
                    if (!succ.TryGetValue(nilKey, out var sl))
                    {
                        sl = [];
                        succ[nilKey] = sl;
                    }

                    sl.Add(val);
                }
            }
        }

        // ww edges between the writers of consecutive versions.
        foreach (var (key, a, b) in versionEdges)
        {
            if (a != 0
                && _c.Writer.TryGetValue(AnalysisContext.Kv(key, a), out var wa)
                && _c.Writer.TryGetValue(AnalysisContext.Kv(key, b), out var wb)
                && _c.IsLive(wa) && _c.IsLive(wb))
            {
                _c.AddEdge(wa, wb, EdgeKind.Ww, key);
            }
        }

        foreach (var (r, key, v) in views)
        {
            if (v != 0)
            {
                var kv = AnalysisContext.Kv(key, v);
                if (!_c.Writer.TryGetValue(kv, out var w))
                {
                    _c.Report(AnomalyClass.GarbageRead, "T" + _c.Txns[r].Id + " read " + v + " from key " + key + " which no transaction wrote", r);
                    continue;
                }

                if (_c.Txns[w].Outcome == TxnOutcome.Fail)
                {
                    _c.Report(AnomalyClass.G1a, "T" + _c.Txns[r].Id + " read " + v + " from key " + key + " written by T" + _c.Txns[w].Id + ", which failed", r, w);
                    continue;
                }

                if (_c.NonFinal.Contains(kv))
                {
                    _c.Report(AnomalyClass.G1b, "T" + _c.Txns[r].Id + " read " + v + " from key " + key + ", an intermediate write of T" + _c.Txns[w].Id, r, w);
                }

                _c.AddEdge(w, r, EdgeKind.Wr, key);
            }

            if (succ.TryGetValue(AnalysisContext.Kv(key, v), out var next))
            {
                foreach (var b in next)
                {
                    if (_c.Writer.TryGetValue(AnalysisContext.Kv(key, b), out var nw) && _c.IsLive(nw))
                    {
                        _c.AddEdge(r, nw, EdgeKind.Rw, key);
                    }
                }
            }
        }
    }
}
