using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RecursiveAI.OneLite.History;

/// <summary>
/// Reads and writes histories as JSON lines. The first line is a header <c>{"history":"list-append"}</c> (or <c>"register"</c>);
/// each following line is one transaction:
/// <c>{"id":7,"p":2,"t0":100,"t1":250,"o":"ok","m":[["a",3,41],["r",3,[41]]]}</c>.
/// Micro-operation kinds are <c>r</c> (read), <c>w</c> (register write) and <c>a</c> (list append). Times are nanoseconds.
/// A transaction with an unknown outcome has <c>"o":"info"</c> and no <c>t1</c>.
/// </summary>
public static class HistoryIO
{
    /// <summary>Name of a model in the header line.</summary>
    public static string ModelName(HistoryModel model) => model == HistoryModel.ListAppend ? "list-append" : "register";

    /// <summary>Parses a model name.</summary>
    public static bool TryParseModel(string name, out HistoryModel model)
    {
        switch (name)
        {
            case "list-append":
                model = HistoryModel.ListAppend;
                return true;
            case "register":
                model = HistoryModel.Register;
                return true;
            default:
                model = default;
                return false;
        }
    }

    /// <summary>Writes the header line.</summary>
    public static void WriteHeader(TextWriter w, HistoryModel model)
    {
        w.Write("{\"history\":\"");
        w.Write(ModelName(model));
        w.Write("\"}\n");
    }

    /// <summary>Writes a history (header plus one line per transaction).</summary>
    public static void Write(string path, History h)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        WriteHeader(w, h.Model);
        foreach (var t in h.Txns)
        {
            WriteTxn(w, t);
        }
    }

    /// <summary>Writes one transaction line.</summary>
    public static void WriteTxn(TextWriter w, Txn t)
    {
        var sb = new StringBuilder(64 + (t.Mops.Length * 16));
        sb.Append("{\"id\":").Append(t.Id).Append(",\"p\":").Append(t.Process).Append(",\"t0\":").Append(t.Invoke);
        if (t.Complete != Txn.NeverCompleted)
        {
            sb.Append(",\"t1\":").Append(t.Complete);
        }

        sb.Append(",\"o\":\"").Append(t.Outcome switch { TxnOutcome.Ok => "ok", TxnOutcome.Fail => "fail", _ => "info" }).Append("\",\"m\":[");
        for (var i = 0; i < t.Mops.Length; i++)
        {
            var m = t.Mops[i];
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("[\"").Append(m.Kind switch { MopKind.Read => 'r', MopKind.Write => 'w', _ => 'a' }).Append("\",").Append(m.Key).Append(',');
            if (m.Kind == MopKind.Read && m.List is { } list)
            {
                sb.Append('[');
                for (var j = 0; j < list.Length; j++)
                {
                    if (j > 0)
                    {
                        sb.Append(',');
                    }

                    sb.Append(list[j]);
                }

                sb.Append(']');
            }
            else
            {
                sb.Append(m.Value);
            }

            sb.Append(']');
        }

        sb.Append("]}\n");
        w.Write(sb.ToString());
    }

    /// <summary>Reads a history file. <paramref name="modelOverride"/> is used when the file has no header.</summary>
    public static History Read(string path, HistoryModel? modelOverride = null)
    {
        using var r = new StreamReader(path, Encoding.UTF8);
        var model = modelOverride;
        var txns = new List<Txn>();
        var first = true;
        var buf = ArrayPool<byte>.Shared.Rent(1 << 16);
        try
        {
            string? line;
            while ((line = r.ReadLine()) is not null)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                var need = Encoding.UTF8.GetMaxByteCount(line.Length);
                if (buf.Length < need)
                {
                    ArrayPool<byte>.Shared.Return(buf);
                    buf = ArrayPool<byte>.Shared.Rent(need);
                }

                var n = Encoding.UTF8.GetBytes(line, 0, line.Length, buf, 0);
                if (first)
                {
                    first = false;
                    if (TryParseHeader(buf.AsSpan(0, n), out var m))
                    {
                        model ??= m;
                        continue;
                    }
                }

                if (model is null)
                {
                    throw new HistoryFormatException("the history has no header line and no model was given");
                }

                txns.Add(ParseTxn(buf.AsSpan(0, n), model.Value));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        return new History(model ?? HistoryModel.ListAppend, txns);
    }

    /// <summary>Reads transaction lines (no header) from a shard file.</summary>
    public static List<Txn> ReadShard(string path, HistoryModel model)
    {
        var h = Read(path, model);
        return h.Txns;
    }

    private static bool TryParseHeader(ReadOnlySpan<byte> line, out HistoryModel model)
    {
        model = default;
        var rd = new Utf8JsonReader(line);
        if (!rd.Read() || rd.TokenType != JsonTokenType.StartObject || !rd.Read() || rd.TokenType != JsonTokenType.PropertyName)
        {
            return false;
        }

        if (!rd.ValueTextEquals("history"u8))
        {
            return false;
        }

        rd.Read();
        return TryParseModel(rd.GetString() ?? "", out model);
    }

    private static Txn ParseTxn(ReadOnlySpan<byte> line, HistoryModel model)
    {
        var rd = new Utf8JsonReader(line);
        int id = 0, p = 0;
        long t0 = 0, t1 = Txn.NeverCompleted;
        var outcome = TxnOutcome.Ok;
        Mop[] mops = [];
        rd.Read(); // StartObject
        while (rd.Read() && rd.TokenType == JsonTokenType.PropertyName)
        {
            if (rd.ValueTextEquals("id"u8))
            {
                rd.Read();
                id = rd.GetInt32();
            }
            else if (rd.ValueTextEquals("p"u8))
            {
                rd.Read();
                p = rd.GetInt32();
            }
            else if (rd.ValueTextEquals("t0"u8))
            {
                rd.Read();
                t0 = rd.GetInt64();
            }
            else if (rd.ValueTextEquals("t1"u8))
            {
                rd.Read();
                t1 = rd.GetInt64();
            }
            else if (rd.ValueTextEquals("o"u8))
            {
                rd.Read();
                outcome = rd.GetString() switch
                {
                    "ok" => TxnOutcome.Ok,
                    "fail" => TxnOutcome.Fail,
                    "info" => TxnOutcome.Info,
                    var s => throw new HistoryFormatException("unknown outcome '" + s + "'"),
                };
            }
            else if (rd.ValueTextEquals("m"u8))
            {
                rd.Read();
                mops = ParseMops(ref rd, model);
            }
            else
            {
                rd.Read();
                rd.Skip();
            }
        }

        return new Txn(id, p, t0, outcome == TxnOutcome.Info ? Txn.NeverCompleted : t1, outcome, mops);
    }

    private static Mop[] ParseMops(ref Utf8JsonReader rd, HistoryModel model)
    {
        var list = new List<Mop>();
        while (rd.Read() && rd.TokenType == JsonTokenType.StartArray)
        {
            rd.Read();
            var kind = rd.GetString();
            rd.Read();
            var key = rd.GetInt32();
            rd.Read();
            switch (kind)
            {
                case "r":
                    if (rd.TokenType == JsonTokenType.StartArray)
                    {
                        var elems = new List<int>();
                        while (rd.Read() && rd.TokenType == JsonTokenType.Number)
                        {
                            elems.Add(rd.GetInt32());
                        }

                        list.Add(Mop.ReadList(key, elems.ToArray()));
                    }
                    else
                    {
                        list.Add(model == HistoryModel.ListAppend ? Mop.ReadList(key, []) : Mop.ReadRegister(key, rd.GetInt32()));
                    }

                    break;
                case "w":
                    list.Add(Mop.WriteRegister(key, rd.GetInt32()));
                    break;
                case "a":
                    list.Add(Mop.AppendList(key, rd.GetInt32()));
                    break;
                default:
                    throw new HistoryFormatException("unknown micro-operation '" + kind + "'");
            }

            rd.Read(); // EndArray of the mop
        }

        return list.ToArray();
    }
}
