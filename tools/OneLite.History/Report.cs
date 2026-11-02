using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RecursiveAI.OneLite.History;

/// <summary>Renders a <see cref="CheckResult"/> as text or JSON.</summary>
public static class Report
{
    private static readonly IsolationLevel[] AllLevels =
    [
        IsolationLevel.ReadCommitted,
        IsolationLevel.SnapshotIsolation,
        IsolationLevel.Serializable,
        IsolationLevel.StrictSerializable,
    ];

    /// <summary>Renders the verdict per level and each example anomaly with its minimal cycle and the transactions in it.</summary>
    public static string Text(CheckResult r)
    {
        var h = r.History;
        var sb = new StringBuilder();
        var ok = 0;
        var fail = 0;
        var info = 0;
        long mops = 0;
        foreach (var t in h.Txns)
        {
            mops += t.Mops.Length;
            switch (t.Outcome)
            {
                case TxnOutcome.Ok: ok++; break;
                case TxnOutcome.Fail: fail++; break;
                default: info++; break;
            }
        }

        sb.Append("model ").Append(HistoryIO.ModelName(h.Model)).Append(": ").Append(h.Txns.Count).Append(" transactions (ok ").Append(ok)
            .Append(", fail ").Append(fail).Append(", info ").Append(info).Append("), ").Append(mops).Append(" operations\n");
        sb.Append("edges: ww ").Append(r.EdgeCounts[EdgeKind.Ww]).Append(", wr ").Append(r.EdgeCounts[EdgeKind.Wr]).Append(", rw ")
            .Append(r.EdgeCounts[EdgeKind.Rw]).Append(", rt ").Append(r.EdgeCounts[EdgeKind.Rt]).Append("; checked in ")
            .Append((long)r.Elapsed.TotalMilliseconds).Append(" ms\n");
        sb.Append("verdicts:\n");
        foreach (var l in AllLevels)
        {
            sb.Append("  ").Append(Levels.Name(l)).Append(": ");
            if (l == IsolationLevel.StrictSerializable && !r.RealtimeChecked)
            {
                sb.Append("not checked (real-time order disabled)\n");
                continue;
            }

            if (r.IsValid(l))
            {
                sb.Append("valid\n");
            }
            else
            {
                var names = new List<string>();
                foreach (var c in r.Violations(l))
                {
                    names.Add(Levels.Label(c) + " x" + r.Counts[c]);
                }

                sb.Append("INVALID (").Append(string.Join(", ", names)).Append(")\n");
            }
        }

        if (r.SearchCapped)
        {
            sb.Append("note: a cycle search hit its seed limit; cycle counts are lower bounds\n");
        }

        if (r.Examples.Count > 0)
        {
            sb.Append("anomalies:\n");
        }

        foreach (var a in r.Examples)
        {
            sb.Append("  [").Append(Levels.Label(a.Class)).Append("] ").Append(a.Message).Append('\n');
            var ctx = new AnalysisContext(h, new AnomalySink(0));
            foreach (var t in Distinct(a.Txns))
            {
                sb.Append("      ").Append(ctx.Describe(t)).Append('\n');
            }

            if (a.Cycle is { } cycle)
            {
                foreach (var s in cycle)
                {
                    sb.Append("      T").Append(h.Txns[s.From].Id).Append(" -").Append(CycleSearch.KindName(s.Kind)).Append("-> T").Append(h.Txns[s.To].Id);
                    sb.Append(Why(s, h)).Append('\n');
                }
            }
        }

        return sb.ToString();
    }

    private static IEnumerable<int> Distinct(int[] txns)
    {
        var seen = new HashSet<int>();
        foreach (var t in txns)
        {
            if (seen.Add(t))
            {
                yield return t;
            }
        }
    }

    private static string Why(CycleStep s, History h)
    {
        var a = h.Txns[s.From].Id;
        var b = h.Txns[s.To].Id;
        return s.Kind switch
        {
            EdgeKind.Ww => " (T" + a + " installed a version of key " + s.Key + " that T" + b + " overwrote)",
            EdgeKind.Wr => " (T" + b + " read key " + s.Key + " from T" + a + ")",
            EdgeKind.Rw => " (T" + a + " read key " + s.Key + " before T" + b + "'s write)",
            _ => " (T" + a + " completed before T" + b + " began)",
        };
    }

    /// <summary>Renders the result as indented JSON.</summary>
    public static string Json(CheckResult r)
    {
        var h = r.History;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("model", HistoryIO.ModelName(h.Model));
            w.WriteNumber("transactions", h.Txns.Count);
            w.WriteNumber("checkMilliseconds", (long)r.Elapsed.TotalMilliseconds);
            w.WriteBoolean("realtimeChecked", r.RealtimeChecked);
            w.WriteBoolean("searchCapped", r.SearchCapped);
            w.WriteStartObject("edges");
            w.WriteNumber("ww", r.EdgeCounts[EdgeKind.Ww]);
            w.WriteNumber("wr", r.EdgeCounts[EdgeKind.Wr]);
            w.WriteNumber("rw", r.EdgeCounts[EdgeKind.Rw]);
            w.WriteNumber("rt", r.EdgeCounts[EdgeKind.Rt]);
            w.WriteEndObject();
            w.WriteStartObject("verdicts");
            foreach (var l in AllLevels)
            {
                w.WriteStartObject(Levels.Name(l));
                var checkedLevel = l != IsolationLevel.StrictSerializable || r.RealtimeChecked;
                w.WriteBoolean("checked", checkedLevel);
                w.WriteBoolean("valid", checkedLevel && r.IsValid(l));
                w.WriteStartArray("violations");
                foreach (var c in r.Violations(l))
                {
                    w.WriteStringValue(Levels.Label(c));
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndObject();
            w.WriteStartObject("counts");
            foreach (var (c, n) in r.Counts)
            {
                w.WriteNumber(Levels.Label(c), n);
            }

            w.WriteEndObject();
            w.WriteStartArray("anomalies");
            var ctx = new AnalysisContext(h, new AnomalySink(0));
            foreach (var a in r.Examples)
            {
                w.WriteStartObject();
                w.WriteString("class", Levels.Label(a.Class));
                w.WriteString("message", a.Message);
                w.WriteStartArray("transactions");
                foreach (var t in Distinct(a.Txns))
                {
                    w.WriteStartObject();
                    w.WriteNumber("id", h.Txns[t].Id);
                    w.WriteString("outcome", h.Txns[t].Outcome.ToString().ToLowerInvariant());
                    w.WriteString("ops", ctx.Describe(t));
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                if (a.Cycle is { } cycle)
                {
                    w.WriteStartArray("cycle");
                    foreach (var s in cycle)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("from", h.Txns[s.From].Id);
                        w.WriteNumber("to", h.Txns[s.To].Id);
                        w.WriteString("edge", CycleSearch.KindName(s.Kind));
                        if (s.Key >= 0)
                        {
                            w.WriteNumber("key", s.Key);
                        }

                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
