using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// FTS5 queries on an index that has many segments and many small leaf pages, with rows updated and deleted after they were indexed:
/// the rows a query returns are checked against a model that reads the stored text, and ranking against bm25() of the same rows.
/// (File-level agreement with the reference shell is checked by Fts5Tests and tools/fts.)
/// </summary>
public sealed class Fts5LargeIndexTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-fts5-large-" + Guid.NewGuid().ToString("N"));

    public Fts5LargeIndexTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static void Run(Connection c, string sql)
    {
        using Statement s = c.Prepare(sql);
        while (s.Step())
        {
        }
    }

    private static List<long> Rowids(Connection c, string sql, string query)
    {
        var res = new List<long>();
        using Statement s = c.Prepare(sql);
        s.Bind(1, query);
        while (s.Step())
        {
            res.Add(s.GetInt64(0));
        }

        return res;
    }

    private static string Doc(Random r)
    {
        var sb = new StringBuilder();
        int n = r.Next(1, 25);
        for (int i = 0; i < n; i++)
        {
            double u = r.NextDouble();
            sb.Append('k').Append((int)(u * u * 60)).Append(' ');
        }

        return sb.ToString().TrimEnd();
    }

    private (Database Db, Connection C, SortedDictionary<long, string> Model) Build(bool wal)
    {
        var db = Database.Open(Path.Combine(_dir, wal ? "w.db" : "r.db"));
        Connection c = db.Connect();
        if (wal)
        {
            Run(c, "PRAGMA journal_mode=wal");
        }

        Run(c, "CREATE VIRTUAL TABLE t USING fts5(body)");
        Run(c, "INSERT INTO t(t, rank) VALUES('pgsz', 100)"); // many small leaf pages, so a doclist spans several
        Run(c, "INSERT INTO t(t, rank) VALUES('automerge', 0)"); // keep the segments of every transaction
        var model = new SortedDictionary<long, string>();
        var r = new Random(7);
        long id = 0;
        for (int tx = 0; tx < 6; tx++)
        {
            Run(c, "BEGIN");
            using (Statement ins = c.Prepare("INSERT INTO t(rowid, body) VALUES(?1, ?2)"))
            {
                for (int i = 0; i < 300; i++)
                {
                    string d = Doc(r);
                    id += 1 + r.Next(3);
                    ins.Bind(1, id);
                    ins.Bind(2, d);
                    while (ins.Step())
                    {
                    }

                    ins.Reset();
                    model[id] = d;
                }
            }

            // after the first batches: change and remove rows whose old entries sit in older segments
            if (tx > 0)
            {
                foreach (long k in model.Keys.Where(k => k % 7 == tx).ToList())
                {
                    if (k % 2 == 0)
                    {
                        Run(c, "DELETE FROM t WHERE rowid=" + k);
                        model.Remove(k);
                    }
                    else
                    {
                        string d = Doc(r);
                        using Statement up = c.Prepare("UPDATE t SET body=?1 WHERE rowid=?2");
                        up.Bind(1, d);
                        up.Bind(2, k);
                        while (up.Step())
                        {
                        }

                        model[k] = d;
                    }
                }
            }

            Run(c, "COMMIT");
        }

        return (db, c, model);
    }

    private static string[] Tokens(string d) => d.Split(' ');

    private static bool HasPhrase(string d, string[] words)
    {
        string[] t = Tokens(d);
        for (int i = 0; i + words.Length <= t.Length; i++)
        {
            if (t.AsSpan(i, words.Length).SequenceEqual(words))
            {
                return true;
            }
        }

        return false;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchReturnsTheRowsThatContainTheTerms(bool wal)
    {
        (Database db, Connection c, SortedDictionary<long, string> model) = Build(wal);
        using (db)
        using (c)
        {
            using Statement segs = c.Prepare("SELECT count(*) FROM t_data");
            Assert.True(segs.Step());
            Assert.True(segs.GetInt64(0) > 100, "the index should span many pages");

            var cases = new List<(string Query, Func<string, bool> Test)>();
            for (int a = 0; a < 60; a += 5)
            {
                string ta = "k" + a;
                cases.Add((ta, d => Tokens(d).Contains(ta)));
                for (int b = a + 1; b < 60; b += 11)
                {
                    string tb = "k" + b;
                    cases.Add((ta + " " + tb, d => Tokens(d).Contains(ta) && Tokens(d).Contains(tb)));
                    cases.Add((ta + " AND " + tb + " AND k1", d => Tokens(d).Contains(ta) && Tokens(d).Contains(tb) && Tokens(d).Contains("k1")));
                    cases.Add((ta + " OR " + tb, d => Tokens(d).Contains(ta) || Tokens(d).Contains(tb)));
                    cases.Add((ta + " NOT " + tb, d => Tokens(d).Contains(ta) && !Tokens(d).Contains(tb)));
                    cases.Add(("\"" + ta + " " + tb + "\"", d => HasPhrase(d, [ta, tb])));
                }
            }

            cases.Add(("k1*", d => Tokens(d).Any(w => w.StartsWith("k1", StringComparison.Ordinal))));
            cases.Add(("k2* k3", d => Tokens(d).Any(w => w.StartsWith("k2", StringComparison.Ordinal)) && Tokens(d).Contains("k3")));
            cases.Add(("k59", d => Tokens(d).Contains("k59")));
            cases.Add(("nosuchterm", _ => false));
            cases.Add(("k1 nosuchterm", _ => false));

            foreach ((string query, Func<string, bool> test) in cases)
            {
                List<long> got = Rowids(c, "SELECT rowid FROM t WHERE t MATCH ?1 ORDER BY rowid", query);
                List<long> want = model.Where(kv => test(kv.Value)).Select(kv => kv.Key).ToList();
                Assert.True(want.SequenceEqual(got), $"query '{query}': {got.Count} rows, expected {want.Count}");
            }
        }
    }

    [Fact]
    public void RankOrderAndLimitAgreeWithBm25OfEveryRow()
    {
        (Database db, Connection c, _) = Build(false);
        using (db)
        using (c)
        {
            foreach (string q in new[] { "k3", "k3 k8", "k0 k1 k2", "k5 OR k9", "\"k4 k4\"" })
            {
                var all = new List<(long Id, double Rank, double Bm25)>();
                using (Statement s = c.Prepare("SELECT rowid, rank, bm25(t) FROM t WHERE t MATCH ?1"))
                {
                    s.Bind(1, q);
                    while (s.Step())
                    {
                        all.Add((s.GetInt64(0), s.GetDouble(1), s.GetDouble(2)));
                    }
                }

                Assert.NotEmpty(all);
                Assert.All(all, x => Assert.Equal(x.Bm25, x.Rank));

                var top = new List<(long Id, double Rank)>();
                using (Statement s = c.Prepare("SELECT rowid, rank FROM t WHERE t MATCH ?1 ORDER BY rank LIMIT 10"))
                {
                    s.Bind(1, q);
                    while (s.Step())
                    {
                        top.Add((s.GetInt64(0), s.GetDouble(1)));
                    }
                }

                double[] wantRanks = all.Select(x => x.Rank).Order().Take(10).ToArray();
                Assert.Equal(wantRanks, top.Select(x => x.Rank).ToArray());
                Assert.All(top, x => Assert.Contains(all, a => a.Id == x.Id && a.Rank == x.Rank));
            }
        }
    }
}
