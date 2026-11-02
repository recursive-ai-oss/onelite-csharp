using System;
using System.Collections.Generic;

namespace RecursiveAI.OneLite.History;

/// <summary>
/// Infers dependencies from a list-append history (Kingsbury and Alvaro, "Elle", section 4). Because every read returns a whole list,
/// the reads of a key are prefixes of one version order; appends are unique, so each element names its writer.
/// </summary>
internal sealed class ListAppendAnalyzer
{
    private readonly AnalysisContext _c;

    public ListAppendAnalyzer(AnalysisContext c) => _c = c;

    private readonly record struct View(int Txn, int[] Prefix);

    private sealed class KeyState
    {
        public int[]? LastRead;
        public readonly List<int> Own = [];
        public bool FirstWasRead;
        public bool Wrote;
        public long VersionId = -1;
    }

    public void Run()
    {
        _c.IndexWriters();
        var views = new Dictionary<int, List<View>>();
        var rtw = new Dictionary<(int Key, long Version), int>();
        var states = new Dictionary<int, KeyState>();
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
                    st = new KeyState { FirstWasRead = m.Kind == MopKind.Read };
                    states[m.Key] = st;
                }

                if (m.Kind == MopKind.Append)
                {
                    st.Own.Add(m.Value);
                    st.Wrote = true;
                    continue;
                }

                var read = m.List ?? [];
                if (st.LastRead is { } prev)
                {
                    if (!SameAsPlusOwn(read, prev, st.Own))
                    {
                        if (st.Own.Count > 0)
                        {
                            _c.Report(AnomalyClass.Internal, "T" + t.Id + " read key " + m.Key + " = [" + string.Join(',', read) + "] but its own earlier reads and appends imply [" + string.Join(',', prev) + (st.Own.Count > 0 ? "," + string.Join(',', st.Own) : "") + "]", i);
                        }
                        else
                        {
                            _c.Report(AnomalyClass.NonRepeatableRead, "T" + t.Id + " read key " + m.Key + " twice and saw [" + string.Join(',', prev) + "] then [" + string.Join(',', read) + "] with no write of its own in between", i);
                        }
                    }
                }
                else
                {
                    // First read of the key. Anything this transaction appended earlier must be the tail of what it sees;
                    // the rest is the version it read from.
                    var own = st.Own.Count;
                    var ok = read.Length >= own;
                    for (var k = 0; ok && k < own; k++)
                    {
                        ok = read[read.Length - own + k] == st.Own[k];
                    }

                    if (!ok)
                    {
                        _c.Report(AnomalyClass.Internal, "T" + t.Id + " read key " + m.Key + " = [" + string.Join(',', read) + "] which does not end with its own appends [" + string.Join(',', st.Own) + "]", i);
                    }
                    else
                    {
                        var prefix = own == 0 ? read : read.AsSpan(0, read.Length - own).ToArray();
                        if (!views.TryGetValue(m.Key, out var vl))
                        {
                            vl = [];
                            views[m.Key] = vl;
                        }

                        vl.Add(new View(i, prefix));
                        // The version a read came from is named by its length and last element, so reads of different lists never share one.
                        st.VersionId = ((long)prefix.Length << 32) | (uint)(prefix.Length > 0 ? prefix[^1] : 0);
                    }
                }

                st.LastRead = read;
                st.Own.Clear();
            }

            foreach (var (key, st) in states)
            {
                if (st.FirstWasRead && st.Wrote && st.VersionId >= 0)
                {
                    _c.NoteReadThenWrite(rtw, i, key, st.VersionId);
                }
            }
        }

        foreach (var (key, vl) in views)
        {
            AnalyzeKey(key, vl);
        }
    }

    private static bool SameAsPlusOwn(int[] read, int[] prev, List<int> own)
    {
        if (read.Length != prev.Length + own.Count)
        {
            return false;
        }

        for (var i = 0; i < prev.Length; i++)
        {
            if (read[i] != prev[i])
            {
                return false;
            }
        }

        for (var i = 0; i < own.Count; i++)
        {
            if (read[prev.Length + i] != own[i])
            {
                return false;
            }
        }

        return true;
    }

    private void AnalyzeKey(int key, List<View> views)
    {
        views.Sort((a, b) => a.Prefix.Length.CompareTo(b.Prefix.Length));
        var longest = views[^1];
        var order = longest.Prefix;
        var seenElems = new HashSet<int>();
        foreach (var e in order)
        {
            if (!seenElems.Add(e))
            {
                _c.Report(AnomalyClass.DuplicateElements, "key " + key + " has element " + e + " twice in [" + string.Join(',', order) + "] (read by T" + _c.Txns[longest.Txn].Id + ")", longest.Txn);
                break;
            }
        }

        var checkedLen = 0;
        foreach (var v in views)
        {
            var p = v.Prefix;
            if (!IsPrefix(p, order))
            {
                _c.Report(AnomalyClass.IncompatibleOrder, "T" + _c.Txns[v.Txn].Id + " read key " + key + " = [" + string.Join(',', p) + "] and T" + _c.Txns[longest.Txn].Id + " read [" + string.Join(',', order) + "]; neither is a prefix of the other", v.Txn, longest.Txn);
                continue;
            }

            // Each element is checked once, charged to the shortest read that contains it.
            for (var pos = checkedLen; pos < p.Length; pos++)
            {
                var kv = AnalysisContext.Kv(key, p[pos]);
                if (!_c.Writer.TryGetValue(kv, out var w))
                {
                    _c.Report(AnomalyClass.GarbageRead, "T" + _c.Txns[v.Txn].Id + " read element " + p[pos] + " of key " + key + " which no transaction appended", v.Txn);
                }
                else if (_c.Txns[w].Outcome == TxnOutcome.Fail)
                {
                    _c.Report(AnomalyClass.G1a, "T" + _c.Txns[v.Txn].Id + " read element " + p[pos] + " of key " + key + " appended by T" + _c.Txns[w].Id + ", which failed", v.Txn, w);
                }
            }

            checkedLen = Math.Max(checkedLen, p.Length);

            if (p.Length > 0)
            {
                var kv = AnalysisContext.Kv(key, p[^1]);
                if (_c.Writer.TryGetValue(kv, out var w) && _c.IsLive(w))
                {
                    if (_c.NonFinal.Contains(kv))
                    {
                        _c.Report(AnomalyClass.G1b, "T" + _c.Txns[v.Txn].Id + " read key " + key + " ending in element " + p[^1] + ", an intermediate append of T" + _c.Txns[w].Id + " (it appended to the key again later)", v.Txn, w);
                    }

                    // wr: the reader saw the writer's append.
                    _c.AddEdge(w, v.Txn, EdgeKind.Wr, key);
                }
            }

            if (p.Length < order.Length)
            {
                // rw: the reader did not see the next version, installed by the writer of order[p.Length].
                if (_c.Writer.TryGetValue(AnalysisContext.Kv(key, order[p.Length]), out var nw) && _c.IsLive(nw))
                {
                    _c.AddEdge(v.Txn, nw, EdgeKind.Rw, key);
                }
            }
        }

        // ww: consecutive elements of the version order.
        for (var i = 1; i < order.Length; i++)
        {
            if (_c.Writer.TryGetValue(AnalysisContext.Kv(key, order[i - 1]), out var a)
                && _c.Writer.TryGetValue(AnalysisContext.Kv(key, order[i]), out var b)
                && _c.IsLive(a) && _c.IsLive(b))
            {
                _c.AddEdge(a, b, EdgeKind.Ww, key);
            }
        }
    }

    private static bool IsPrefix(int[] p, int[] order)
    {
        if (p.Length > order.Length)
        {
            return false;
        }

        for (var i = 0; i < p.Length; i++)
        {
            if (p[i] != order[i])
            {
                return false;
            }
        }

        return true;
    }
}
