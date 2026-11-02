using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using RecursiveAI.OneLite.Storage.Forensics;

namespace RecursiveAI.OneLite.ForensicsTool;

/// <summary>
/// Calibration of the carving confidence: generate databases with the pinned shell, remove rows in known ways, carve, and compare with the rows
/// that were removed. Reports recall and precision at several confidence thresholds, per scenario.
/// </summary>
internal static class Calibrate
{
    private static int[] PageSizes = [512, 1024, 2048, 4096, 8192, 16384, 32768, 65536];
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly Dictionary<string, (int Truth, int Hit)> Diag = [];
    private static readonly double[] Thresholds = [0.0, 0.3, 0.5, 0.7, 0.9, 0.97];

    private sealed record Row(string Table, object?[] Values);

    private sealed class Model
    {
        public readonly Dictionary<long, object?[]> T1 = [];
        public readonly List<object?[]> T2 = [];
        public readonly Dictionary<long, object?[]> Big = [];
        public readonly List<Row> Truth = [];
    }

    // The signature leaves out the rowid-alias column (index 0 of t1 and big), because a freeblock overwrites the rowid.
    private static string Key(string table, IEnumerable<object?> values) => table + "|" + string.Join('\u001f', values.Select(Common.Quote));

    private static string Word(Random r, int min, int max)
    {
        int n = r.Next(min, max + 1);
        var sb = new StringBuilder(n);
        for (int i = 0; i < n; i++)
        {
            sb.Append((char)('a' + r.Next(26)));
        }

        return sb.ToString();
    }

    private static void Populate(Random r, Model m, StringBuilder sql, int ps)
    {
        sql.Append("create table t1(id integer primary key, name text, qty integer, note text);\n");
        sql.Append("create table t2(a text, b integer, c blob);\n");
        sql.Append("create table big(id integer primary key, body text);\n");
        sql.Append("begin;\n");
        for (long id = 1; id <= 600; id++)
        {
            object?[] v = [id, "n" + id + "-" + Word(r, 5, 30), r.Next(3) == 0 ? r.NextInt64(0, 1L << 40) : r.Next(0, 500), r.Next(10) == 0 ? null : Word(r, 0, 60)];
            m.T1[id] = v;
            sql.Append($"insert into t1 values({string.Join(',', v.Select(Common.Quote))});\n");
        }

        for (int i = 1; i <= 300; i++)
        {
            byte[] blob = new byte[r.Next(0, 20)];
            r.NextBytes(blob);
            object?[] v = ["k" + i + "-" + Word(r, 4, 20), r.Next(-1000, 100000), blob];
            m.T2.Add(v);
            sql.Append($"insert into t2 values({string.Join(',', v.Select(Common.Quote))});\n");
        }

        for (long id = 1; id <= 24; id++)
        {
            object?[] v = [id, "big" + id + "-" + Word(r, ps / 2, ps * 3 + 200)];
            m.Big[id] = v;
            sql.Append($"insert into big values({string.Join(',', v.Select(Common.Quote))});\n");
        }

        sql.Append("commit;\n");
    }

    private static void Remove(Random r, Model m, StringBuilder sql, string scenario)
    {
        List<long> ids = [.. m.T1.Keys];
        switch (scenario)
        {
            case "delete":
            case "vacuum":
            case "walhist":
            case "walfull":
            case "churn":
                {
                    foreach (long id in ids.Where(_ => r.Next(100) < 30))
                    {
                        m.Truth.Add(new Row("t1", m.T1[id]));
                        m.T1.Remove(id);
                        sql.Append($"delete from t1 where id={id};\n");
                    }

                    // Whole runs of neighbours go together too, so freeblocks merge.
                    long start = r.Next(1, 500);
                    for (long id = start; id < start + 20; id++)
                    {
                        if (m.T1.TryGetValue(id, out object?[]? v))
                        {
                            m.Truth.Add(new Row("t1", v));
                            m.T1.Remove(id);
                        }
                    }

                    sql.Append($"delete from t1 where id>={start} and id<{start + 20};\n");
                    for (int i = m.T2.Count - 1; i >= 0; i--)
                    {
                        if (r.Next(100) < 30)
                        {
                            m.Truth.Add(new Row("t2", m.T2[i]));
                            sql.Append($"delete from t2 where a={Common.Quote(m.T2[i][0])};\n");
                            m.T2.RemoveAt(i);
                        }
                    }

                    foreach (long id in m.Big.Keys.ToList().Where(k => k % 2 == 0))
                    {
                        m.Truth.Add(new Row("big", m.Big[id]));
                        m.Big.Remove(id);
                        sql.Append($"delete from big where id={id};\n");
                    }

                    if (scenario == "churn")
                    {
                        for (long id = 1001; id < 1060; id++)
                        {
                            object?[] v = [id, "n" + id + "-" + Word(r, 5, 30), r.Next(0, 500), Word(r, 0, 60)];
                            m.T1[id] = v;
                            sql.Append($"insert into t1 values({string.Join(',', v.Select(Common.Quote))});\n");
                        }
                    }

                    if (scenario == "vacuum")
                    {
                        sql.Append("vacuum;\n");
                    }

                    break;
                }

            case "update":
                {
                    // Rows whose text grows move to a new cell and leave the old one behind; rows whose text shrinks are rewritten in place.
                    foreach (long id in ids.Where(_ => r.Next(100) < 30))
                    {
                        object?[] old = m.T1[id];
                        m.Truth.Add(new Row("t1", old));
                        string name = r.Next(2) == 0 ? old[1] + "-grown-" + Word(r, 8, 40) : ((string)old[1]!)[..Math.Min(4, ((string)old[1]!).Length)];
                        m.T1[id] = [old[0], name, old[2], old[3]];
                        sql.Append($"update t1 set name={Common.Quote(name)} where id={id};\n");
                    }

                    break;
                }

            default:
                throw new ArgumentException(scenario);
        }
    }

    public static int Run(string[] args)
    {
        string work = args.SkipWhile(a => a != "--work").Skip(1).FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "forensic-calibrate");
        int seed = int.Parse(args.SkipWhile(a => a != "--seed").Skip(1).FirstOrDefault() ?? "1", CultureInfo.InvariantCulture);
        string? json = args.SkipWhile(a => a != "--json").Skip(1).FirstOrDefault();
        Directory.CreateDirectory(work);
        if (args.SkipWhile(a => a != "--page-sizes").Skip(1).FirstOrDefault() is { } psl)
        {
            PageSizes = [.. psl.Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture))];
        }

        string[] scenarios = args.SkipWhile(a => a != "--scenarios").Skip(1).FirstOrDefault()?.Split(',') ?? ["delete", "churn", "update", "vacuum", "walhist", "walfull"];
        var summary = new List<object>();
        Console.WriteLine("scenario\tsecure_delete\ttruth\t" + string.Join('\t', Thresholds.Select(t => $"R@{t:0.##}/P@{t:0.##}")));
        foreach (string sc in scenarios)
        {
            foreach (bool secure in new[] { false, true })
            {
                var truthTotal = 0;
                var hit = new int[Thresholds.Length];
                var cand = new int[Thresholds.Length];
                var trueC = new int[Thresholds.Length];
                var perPage = new Dictionary<int, string>();
                foreach (int ps in PageSizes)
                {
                    var r = new Random(seed * 1000 + ps);
                    var m = new Model();
                    var sql = new StringBuilder();
                    string db = Path.Combine(work, $"{sc}-{(secure ? "sec" : "plain")}-{ps}.db");
                    foreach (string f in new[] { "", "-wal", "-shm", "-journal" })
                    {
                        File.Delete(db + f);
                    }

                    sql.Append($"pragma page_size={ps};\n");
                    if (sc == "walfull")
                    {
                        sql.Append("pragma journal_mode=wal;\npragma wal_autocheckpoint=0;\n");
                    }

                    Populate(r, m, sql, ps);
                    if (sc == "walhist")
                    {
                        sql.Append("pragma journal_mode=wal;\n");
                        Common.Sql(db, sql.ToString());
                        sql.Clear();
                        sql.Append("pragma wal_autocheckpoint=0;\n");
                    }

                    sql.Append($"pragma secure_delete={(secure ? "on" : "off")};\n");
                    Remove(r, m, sql, sc);
                    string live = db;
                    if (sc is "walhist" or "walfull")
                    {
                        // Copy while the connection is open: closing the last connection would checkpoint the log away.
                        live = db + ".snap";
                        sql.Append($".shell cp '{db}' '{live}'\n.shell cp '{db}-wal' '{live}-wal'\n");
                    }

                    Common.Sql(db, sql.ToString());
                    (int t, int[] h, int[] c, int[] tc) = Evaluate(live, m);
                    truthTotal += t;
                    for (int i = 0; i < Thresholds.Length; i++)
                    {
                        hit[i] += h[i];
                        cand[i] += c[i];
                        trueC[i] += tc[i];
                    }

                    perPage[ps] = $"{h[2]}/{t}@{c[2]}";
                }

                string line = $"{sc}\t{(secure ? "on" : "off")}\t{truthTotal}\t" + string.Join('\t', Thresholds.Select((_, i) => $"{(truthTotal == 0 ? 0 : 100.0 * hit[i] / truthTotal):0.0}%/{(cand[i] == 0 ? 0 : 100.0 * trueC[i] / cand[i]):0.0}%"));
                Console.WriteLine(line);
                Console.WriteLine("   per table at 0.5: " + string.Join(' ', Diag.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}:{k.Value.Hit}/{k.Value.Truth}")));
                Diag.Clear();
                Console.WriteLine("   per page size (hits/truth@candidates at 0.5): " + string.Join(' ', perPage.Select(k => $"{k.Key}:{k.Value}")));
                summary.Add(new { scenario = sc, secureDelete = secure, truth = truthTotal, thresholds = Thresholds, recovered = hit, candidates = cand, trueCandidates = trueC, perPage });
            }
        }

        if (json != null)
        {
            File.WriteAllText(json, JsonSerializer.Serialize(summary, Indented));
        }

        return 0;
    }

    private static void Diagnose(string path, Model m, HashSet<string> truth, IReadOnlyList<CarvedRecord> found, ForensicSnapshot s)
    {
        var got = new HashSet<string>(found.Where(c => c.Confidence >= 0.5).Select(c => Key(c.Table!, c.Table == "t2" ? c.Values : c.Values.Skip(1))));
        byte[] file = File.ReadAllBytes(path);
        int ps = s.PageSize;
        int shown = 0;
        var kinds = new Dictionary<string, int>();
        foreach (Row t in m.Truth)
        {
            string key = Key(t.Table, t.Table == "t2" ? t.Values : t.Values.Skip(1));
            if (!truth.Contains(key) || got.Contains(key) || t.Table == "big")
            {
                continue;
            }

            byte[] needle = Encoding.UTF8.GetBytes((string)t.Values[t.Table == "t2" ? 0 : 1]!);
            int at = file.AsSpan().IndexOf(needle);
            string where;
            if (at < 0)
            {
                where = "text not in file";
            }
            else
            {
                int pg = (at / ps) + 1;
                int off = at % ps;
                byte[] d = s.GetPageBytes((uint)pg)!;
                int h = pg == 1 ? 100 : 0;
                int ncell = (d[h + 3] << 8) | d[h + 4];
                int ptrEnd = h + 8 + (2 * ncell);
                int cs = (d[h + 5] << 8) | d[h + 6];
                string region = off < ptrEnd ? "header/pointers" : off < cs ? "gap" : "content";
                int fb = (d[h + 1] << 8) | d[h + 2];
                int hops = 0;
                string fbd = "";
                while (fb != 0 && hops++ < 500)
                {
                    int size = (d[fb + 2] << 8) | d[fb + 3];
                    if (off >= fb && off < fb + size)
                    {
                        region = "freeblock";
                        fbd = $" fb@{fb}+{off - fb} size {size}";
                        break;
                    }

                    fb = (d[fb] << 8) | d[fb + 1];
                }

                where = $"page {pg} type {d[h]} off {off} {region}{fbd} cs={cs}";
            }

            string kind = where.Contains("freeblock", StringComparison.Ordinal) ? "freeblock" : where.Contains("gap", StringComparison.Ordinal) ? "gap" : where.Contains("content", StringComparison.Ordinal) ? "content" : where;
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
            if (kind == "freeblock" && shown++ < 3)
            {
                Console.WriteLine($"   MISS {t.Table} {Common.Quote(t.Values[0])}: {where}");
                int pgn = (at / ps) + 1;
                int fbStart = int.Parse(where.Split("fb@")[1].Split('+')[0], CultureInfo.InvariantCulture);
                int fbSize = int.Parse(where.Split("size ")[1].Split(' ')[0], CultureInfo.InvariantCulture);
                Console.WriteLine("     bytes of block: " + Convert.ToHexString(file.AsSpan(((pgn - 1) * ps) + fbStart, Math.Min(fbSize, 140))));
                foreach (CarvedRecord c in found.Where(c => c.Page == pgn && c.PageOffset >= fbStart && c.PageOffset < fbStart + fbSize))
                {
                    Console.WriteLine($"     cand @{c.PageOffset - fbStart} conf {c.Confidence:0.00} {(c.Values.Length > 1 ? c.Values[1] : null)}");
                }
            }
        }

        foreach (CarvedRecord c in found.Where(c => c.Confidence >= 0.5 && !truth.Contains(Key(c.Table!, c.Table == "t2" ? c.Values : c.Values.Skip(1)))).Take(5))
        {
            Console.WriteLine($"   FALSE {c.Table} r={c.Rowid} conf={c.Confidence:0.00} {c.Source} page {c.Page}@{c.PageOffset}: {string.Join(',', c.Values.Select(Common.Quote))} :: {string.Join("; ", c.Reasons)}");
        }

        Console.WriteLine("   misses by region: " + string.Join(' ', kinds.Select(k => $"{k.Key}={k.Value}")));
    }

    private static (int Truth, int[] Hit, int[] Cand, int[] TrueCand) Evaluate(string path, Model m)
    {
        var truth = new HashSet<string>(m.Truth.Select(t => Key(t.Table, t.Table == "t2" ? t.Values : t.Values.Skip(1))));
        // A removed row whose exact values are also live (an old version that equals a live one) cannot count as recovered.
        var liveKeys = new HashSet<string>(m.T1.Values.Select(v => Key("t1", v.Skip(1))).Concat(m.T2.Select(v => Key("t2", v))).Concat(m.Big.Values.Select(v => Key("big", v.Skip(1)))));
        truth.ExceptWith(liveKeys);
        using ForensicDatabase db = ForensicDatabase.Open(path);
        ForensicSnapshot s = db.OpenSnapshot();
        IReadOnlyList<CarvedRecord> found = s.Carve(new CarveOptions { MinConfidence = 0 });
        var hit = new int[Thresholds.Length];
        var cand = new int[Thresholds.Length];
        var trueC = new int[Thresholds.Length];
        var seen = Thresholds.Select(_ => new HashSet<string>()).ToArray();
        var diagSeen = new HashSet<string>();
        foreach (var g in truth.GroupBy(k => k[..k.IndexOf('|', StringComparison.Ordinal)]))
        {
            Diag[g.Key] = (Diag.GetValueOrDefault(g.Key).Truth + g.Count(), Diag.GetValueOrDefault(g.Key).Hit);
        }

        foreach (CarvedRecord c in found)
        {
            string key = Key(c.Table!, c.Table == "t2" ? c.Values : c.Values.Skip(1));
            if (c.Confidence >= 0.5 && truth.Contains(key) && diagSeen.Add(key))
            {
                string tb = c.Table!;
                Diag[tb] = (Diag.GetValueOrDefault(tb).Truth, Diag.GetValueOrDefault(tb).Hit + 1);
            }

            for (int i = 0; i < Thresholds.Length; i++)
            {
                if (c.Confidence >= Thresholds[i])
                {
                    cand[i]++;
                    if (truth.Contains(key))
                    {
                        trueC[i]++;
                        if (seen[i].Add(key))
                        {
                            hit[i]++;
                        }
                    }
                }
            }
        }

        if (Environment.GetEnvironmentVariable("ONELITE_DIAG") == "1")
        {
            Diagnose(path, m, truth, found, s);
        }

        return (truth.Count, hit, cand, trueC);
    }
}
