using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Vec;

/// <summary>
/// Random tables of every element type and metric, checked against a plain double-precision brute force written here: ids, order (ties by rowid),
/// distances, metadata and partition filters, deletes and updates, and a reopen in between.
/// </summary>
public sealed class VecKnnTests : IDisposable
{
    private readonly string _dir = VecHelper.TempDir("vec-knn");

    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class Row
    {
        public long Id;
        public double[] V = [];
        public byte[] Bytes = [];
        public string Tag = "";
        public long N;
        public double F;
        public bool B;
        public long P;
    }

    public static IEnumerable<object[]> Scenarios()
    {
        int seed = 1;
        foreach (int d in new[] { 1, 3, 16, 17, 100, 768, 3072 })
        {
            foreach (string m in new[] { "l2", "cosine", "l1" })
            {
                yield return ["float", m, d, d >= 768 ? 24 : 150, seed++, d % 2 == 0 ? 8 : 0];
            }
        }

        foreach (int d in new[] { 1, 5, 64, 100, 300 })
        {
            foreach (string m in new[] { "l2", "cosine", "l1" })
            {
                yield return ["int8", m, d, 150, seed++, d % 2 == 0 ? 0 : 16];
            }
        }

        foreach (int d in new[] { 8, 64, 128, 1024 })
        {
            yield return ["bit", "hamming", d, 150, seed++, d == 64 ? 8 : 0];
        }
    }

    private static string Vec(string kind) => kind switch { "float" => "vec_f32", "int8" => "vec_int8", _ => "vec_bit" };

    private static double Dist(string kind, string metric, Row a, double[] q, byte[] qb, Row b)
    {
        if (kind == "bit")
        {
            long h = 0;
            for (int i = 0; i < qb.Length; i++)
            {
                h += BitOperations.PopCount((uint)(qb[i] ^ b.Bytes[i]));
            }

            return h;
        }

        double dot = 0, na = 0, nb = 0, l2 = 0, l1 = 0;
        for (int i = 0; i < q.Length; i++)
        {
            dot += q[i] * b.V[i];
            na += q[i] * q[i];
            nb += b.V[i] * b.V[i];
            l2 += (q[i] - b.V[i]) * (q[i] - b.V[i]);
            l1 += Math.Abs(q[i] - b.V[i]);
        }

        return metric switch
        {
            "l2" => Math.Sqrt(l2),
            "l1" => l1,
            _ => na == 0 || nb == 0 ? double.NaN : 1.0 - (dot / (Math.Sqrt(na) * Math.Sqrt(nb))),
        };
    }

    private static Row NewRow(Random r, string kind, int dims, long id)
    {
        var row = new Row { Id = id, Tag = "t" + r.Next(4), N = r.Next(0, 50), F = Math.Round(r.NextDouble() * 10, 2), B = r.Next(2) == 0, P = r.Next(3) };
        Fill(r, kind, dims, row);
        return row;
    }

    private static void Fill(Random r, string kind, int dims, Row row)
    {
        switch (kind)
        {
            case "float":
            {
                row.V = new double[dims];
                row.Bytes = new byte[dims * 4];
                for (int i = 0; i < dims; i++)
                {
                    // Small integers sometimes, so that exact ties happen.
                    float f = r.Next(4) == 0 ? r.Next(-2, 3) : (float)((r.NextDouble() - 0.5) * 4);
                    row.V[i] = f;
                    BitConverter.TryWriteBytes(row.Bytes.AsSpan(i * 4), f);
                }

                break;
            }

            case "int8":
            {
                row.V = new double[dims];
                row.Bytes = new byte[dims];
                int range = r.Next(2) == 0 ? 3 : 128;
                for (int i = 0; i < dims; i++)
                {
                    int v = r.Next(-range, range);
                    row.V[i] = v;
                    row.Bytes[i] = (byte)(sbyte)v;
                }

                break;
            }

            default:
            {
                row.Bytes = new byte[dims / 8];
                if (r.Next(3) > 0)
                {
                    r.NextBytes(row.Bytes);
                }
                else
                {
                    for (int i = 0; i < row.Bytes.Length; i++)
                    {
                        row.Bytes[i] = (byte)(r.Next(4) == 0 ? 0xFF : 0);
                    }
                }

                break;
            }
        }
    }

    private static string Json(string kind, Row row) => "[" + string.Join(",", row.V.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "]";

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void KnnMatchesBruteForce(string kind, string metric, int dims, int count, int seed, int chunkSize)
    {
        var r = new Random(seed * 7919);
        string path = Path.Combine(_dir, $"k{seed}.db");
        string metricOpt = kind == "bit" ? string.Empty : " distance_metric=" + metric;
        string type = kind == "float" ? "float" : kind == "int8" ? "int8" : "bit";
        string create = $"create virtual table v using vec0(id integer primary key, e {type}[{dims}]{metricOpt}, tag text, n integer, f float, b boolean, p integer partition key, +note text"
            + (chunkSize > 0 ? $", chunk_size={chunkSize})" : ")");
        var rows = new Dictionary<long, Row>();
        var bad = new List<string>();

        Database db = Database.Open(path);
        Connection c = db.Connect();
        Assert.Equal(string.Empty, VecHelper.Run(c, create));

        void Insert(Row row)
        {
            using Statement st = c.Prepare($"insert into v(id,e,tag,n,f,b,p,note) values (?,{Vec(kind)}(?),?,?,?,?,?,?)");
            st.Bind(1, row.Id);
            st.Bind(2, row.Bytes);
            st.Bind(3, row.Tag);
            st.Bind(4, row.N);
            st.Bind(5, row.F);
            st.Bind(6, row.B ? 1L : 0L);
            st.Bind(7, row.P);
            st.Bind(8, "n" + row.Id);
            try
            {
                Assert.False(st.Step());
            }
            catch (OneLiteException e)
            {
                throw new InvalidOperationException(e.Message + " / " + e.Detail + " (insert " + rows.Count + ")");
            }

            rows[row.Id] = row;
        }

        Assert.Equal(string.Empty, VecHelper.Run(c, "begin"));
        for (int i = 0; i < count; i++)
        {
            Insert(NewRow(r, kind, dims, r.Next(3) == 0 ? 1000 + i : i + 1));
        }

        Assert.Equal(string.Empty, VecHelper.Run(c, "commit"));

        for (int round = 0; round < 3; round++)
        {
            // Deletes and updates.
            foreach (long id in rows.Keys.OrderBy(x => x).Where(_ => r.Next(5) == 0).ToList())
            {
                Assert.Equal(string.Empty, VecHelper.Run(c, "delete from v where id=" + id));
                rows.Remove(id);
            }

            foreach (long id in rows.Keys.OrderBy(x => x).Where(_ => r.Next(6) == 0).ToList())
            {
                Row row = rows[id];
                Row fresh = NewRow(r, kind, dims, id);
                row.V = fresh.V;
                row.Bytes = fresh.Bytes;
                row.Tag = fresh.Tag;
                row.N = fresh.N;
                row.F = fresh.F;
                row.B = fresh.B;
                using Statement st = c.Prepare($"update v set e={Vec(kind)}(?), tag=?, n=?, f=?, b=?, note=? where id=?");
                st.Bind(1, row.Bytes);
                st.Bind(2, row.Tag);
                st.Bind(3, row.N);
                st.Bind(4, row.F);
                st.Bind(5, row.B ? 1L : 0L);
                st.Bind(6, "u" + id);
                st.Bind(7, id);
                Assert.False(st.Step());
            }

            for (int i = 0; i < 30; i++)
            {
                Insert(NewRow(r, kind, dims, 5000 + (round * 100) + i));
            }

            if (round == 1)
            {
                c.Dispose();
                db.Dispose();
                db = Database.Open(path);
                c = db.Connect();
            }

            for (int q = 0; q < 12; q++)
            {
                Check(c, kind, metric, dims, r, rows, bad);
            }
        }

        // Every row reads back exactly as written.
        foreach (Row row in rows.Values.Take(40))
        {
            using Statement st = c.Prepare("select e, tag, n, f, b, p, note from v where id=" + row.Id);
            Assert.True(st.Step());
            Assert.True(st.GetBlob(0).SequenceEqual(row.Bytes));
            Assert.Equal(row.Tag, st.GetString(1));
            Assert.Equal(row.N, st.GetInt64(2));
            Assert.Equal(row.F, st.GetDouble(3));
            Assert.Equal(row.B ? 1 : 0, st.GetInt64(4));
            Assert.Equal(row.P, st.GetInt64(5));
        }

        Assert.Equal(rows.Count.ToString(CultureInfo.InvariantCulture) + "\n", VecHelper.Run(c, "select count(*) from v"));
        c.Dispose();
        db.Dispose();
        Assert.True(bad.Count == 0, string.Join("\n", bad.Take(8)));
    }

    private static void Check(Connection c, string kind, string metric, int dims, Random r, Dictionary<long, Row> rows, List<string> bad)
    {
        Row qrow = NewRow(r, kind, dims, 0);
        int k = r.Next(5) == 0 ? 1000 : r.Next(1, 25);
        var preds = new List<(string Sql, Func<Row, bool> Test)>();
        long cut = r.Next(0, 50);
        switch (r.Next(9))
        {
            case 0: preds.Add(("tag = 't1'", x => x.Tag == "t1")); break;
            case 1: preds.Add(("tag != 't2'", x => x.Tag != "t2")); break;
            case 2: preds.Add(($"n < {cut}", x => x.N < cut)); break;
            case 3: preds.Add(($"n between {cut / 2} and {cut}", x => x.N >= cut / 2 && x.N <= cut)); break;
            case 4: preds.Add(("f > 5.5", x => x.F > 5.5)); break;
            case 5: preds.Add(("b = 1", x => x.B)); break;
            case 6: preds.Add(("p = 2", x => x.P == 2)); break;
            case 7: preds.Add(("tag in ('t0','t3') and p != 1", x => x.Tag is "t0" or "t3" && x.P != 1)); break;
            default: break;
        }

        string where = string.Concat(preds.Select(p => " and " + p.Sql));
        Row[] pool = [.. rows.Values.Where(x => preds.All(p => p.Test(x)))];
        string sql = $"select id, distance from v where e match {Vec(kind)}(?) and k = {k}{where}";
        var got = new List<(long Id, double D)>();
        using (Statement st = c.Prepare(sql))
        {
            st.Bind(1, qrow.Bytes);
            while (st.Step())
            {
                got.Add((st.GetInt64(0), st.IsNull(1) ? double.NaN : st.GetDouble(1)));
            }
        }

        var scored = pool.Select(x => (x.Id, D: Dist(kind, metric, qrow, qrow.V, qrow.Bytes, x))).Where(x => !double.IsNaN(x.D)).OrderBy(x => x.D).ThenBy(x => x.Id).ToList();
        var nan = pool.Select(x => (x.Id, D: Dist(kind, metric, qrow, qrow.V, qrow.Bytes, x))).Where(x => double.IsNaN(x.D)).OrderBy(x => x.Id).ToList();
        scored.AddRange(nan);
        var want = scored.Take(k).ToList();
        bool exact = kind != "float";
        string label = sql + " [pool " + pool.Length + "]";
        if (got.Count != want.Count)
        {
            bad.Add(label + ": " + got.Count + " rows, want " + want.Count);
            return;
        }

        if (exact)
        {
            for (int i = 0; i < want.Count; i++)
            {
                bool dOk = double.IsNaN(want[i].D) ? double.IsNaN(got[i].D) || got[i].D == 0 : Math.Abs(got[i].D - want[i].D) <= 1e-9 * Math.Max(1, Math.Abs(want[i].D));
                if (got[i].Id != want[i].Id || !dOk)
                {
                    bad.Add($"{label}: position {i}: got {got[i].Id}/{got[i].D}, want {want[i].Id}/{want[i].D}");
                    return;
                }
            }

            return;
        }

        // Floats: the distances agree to 1e-6, and the answer is the right set except among rows tied with the k-th within that tolerance.
        double kth = want.Count == 0 ? 0 : want[^1].D;
        double tol(double d) => 1e-6 * Math.Max(1, Math.Abs(d));
        var byId = scored.ToDictionary(x => x.Id, x => x.D);
        for (int i = 0; i < got.Count; i++)
        {
            if (!byId.TryGetValue(got[i].Id, out double rd) || (!double.IsNaN(rd) && Math.Abs(rd - got[i].D) > tol(rd)))
            {
                bad.Add($"{label}: row {got[i].Id} distance {got[i].D}, reference {rd}");
                return;
            }

            if (i > 0 && got[i].D < got[i - 1].D - tol(got[i].D))
            {
                bad.Add($"{label}: not ascending at {i}");
                return;
            }

            if (!double.IsNaN(kth) && got[i].D > kth + tol(kth))
            {
                bad.Add($"{label}: row {got[i].Id} at {got[i].D} is beyond the k-th distance {kth}");
                return;
            }
        }

        var gotIds = got.Select(x => x.Id).ToHashSet();
        foreach ((long id, double d) in want)
        {
            if (!gotIds.Contains(id) && !double.IsNaN(kth) && d < kth - tol(kth))
            {
                bad.Add($"{label}: missing row {id} at {d} (k-th {kth})");
                return;
            }
        }
    }
}
