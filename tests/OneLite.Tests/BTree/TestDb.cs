using RecursiveAI.OneLite.Storage;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Tests.Trees;

/// <summary>One row of the test table <c>t(a INT, b TEXT, c BLOB)</c>.</summary>
internal sealed record Row(long? A, string B, byte[] C);

/// <summary>
/// A database built through raw B-tree calls: a rowid table, four indexes (BINARY, NOCASE, DESC, and a multi-column one) and a WITHOUT ROWID
/// table, with an in-memory model of what every tree should hold. The same object is used with and without the reference <c>sqlite3</c>.
/// </summary>
internal sealed class TestDb
{
    public readonly IBtPageSource Src;
    public readonly ImagePageSource? Img;
    public readonly StorageEngine? Engine;
    public readonly BtContext Ctx;
    public readonly BTree Schema, T, IxB, IxN, IxD, W;
    public readonly SortedDictionary<long, Row> Rows = [];
    public readonly SortedDictionary<string, byte[]> Wr;
    private readonly BtCursor _t, _ib, _in, _id, _w, _scan;
    private readonly TextEncoding _enc;
    public int Ops;
    public string LastOp = "";
    public readonly int PageSize;

    public TestDb(int pageSize, int reserved, TextEncoding enc, int autoVacuum, int yieldEvery)
        : this(ImagePageSource.Create(pageSize, reserved, enc, autoVacuum), null, enc, yieldEvery)
    {
    }

    /// <summary>Builds the test database on a pager-backed engine whose write transaction is open (see <see cref="Checkpoint"/>).</summary>
    public TestDb(StorageEngine engine, TextEncoding enc)
        : this(engine.Source, engine, enc, 0)
    {
    }

    private TestDb(IBtPageSource src, StorageEngine? engine, TextEncoding enc, int yieldEvery)
    {
        PageSize = src.PageSize;
        Img = src as ImagePageSource;
        Engine = engine;
        _enc = enc;
        // BINARY compares the stored bytes, which for UTF-16LE is not the order of .NET strings.
        Wr = new SortedDictionary<string, byte[]>(Comparer<string>.Create((x, y) => TextCodec.Encode(x, enc).AsSpan().SequenceCompareTo(TextCodec.Encode(y, enc))));
        Src = src;
        Ctx = engine?.Context ?? new BtContext(Src, enc);
        Schema = BTree.Table(Ctx, 1);
        // Create all trees first so that, in auto-vacuum databases, the root pages are the lowest pages (fileformat2 §1.7).
        uint tr = Mk(true), ib = Mk(false), inn = Mk(false), id = Mk(false), w = Mk(false);
        T = BTree.Table(Ctx, tr);
        IxB = BTree.Index(Ctx, ib, new KeyInfo(enc, [null, null]));
        IxN = BTree.Index(Ctx, inn, new KeyInfo(enc, [Collations.NoCase(enc), null]));
        IxD = BTree.Index(Ctx, id, new KeyInfo(enc, [null, null, null], [true, false, false]));
        W = BTree.Index(Ctx, w, new KeyInfo(enc, [null]));
        _t = T.OpenCursor();
        _ib = IxB.OpenCursor();
        _in = IxN.OpenCursor();
        _id = IxD.OpenCursor();
        _w = W.OpenCursor();
        _scan = T.OpenCursor();
        string[] sql =
        [
            "CREATE TABLE t(a INT, b TEXT, c BLOB)",
            "CREATE INDEX tb ON t(b)",
            "CREATE INDEX tbn ON t(b COLLATE NOCASE)",
            "CREATE INDEX tad ON t(a DESC, b)",
            "CREATE TABLE w(k TEXT PRIMARY KEY, v BLOB) WITHOUT ROWID",
        ];
        (string type, string name, string tbl, uint root)[] entries =
        [
            ("table", "t", "t", tr), ("index", "tb", "t", ib), ("index", "tbn", "t", inn), ("index", "tad", "t", id), ("table", "w", "w", w),
        ];
        using BtCursor sc = Schema.OpenCursor();
        for (int i = 0; i < entries.Length; i++)
        {
            byte[] rec = RecordWriter.ToArray([
                Mem.FromText(entries[i].type, enc), Mem.FromText(entries[i].name, enc), Mem.FromText(entries[i].tbl, enc),
                Mem.FromInt(entries[i].root), Mem.FromText(sql[i], enc)]);
            long rowid = i + 1;
            Drive.Ok(Src, sc, () => sc.Insert(rowid, rec));
        }

        if (Img != null)
        {
            Img.SchemaCookie = 1;
            Img.YieldEvery = yieldEvery;
        }
        else
        {
            Engine!.SetSchemaCookie(1);
        }
    }

    private uint Mk(bool intKey)
    {
        uint root = 0;
        Drive.Run(Src, () => BtAdmin.CreateTree(Ctx, intKey, out root));
        return root;
    }

    /// <summary>Pager-backed only: closes every cursor, commits, and opens the next write transaction (what a statement boundary does).</summary>
    public void Checkpoint()
    {
        Finish();
        Engine!.BeginWrite();
    }

    /// <summary>Pager-backed only: closes every cursor and commits, leaving the read transaction open.</summary>
    public void Finish()
    {
        _scan.Reset();
        foreach (BtCursor c in new[] { _t, _ib, _in, _id, _w })
        {
            c.Reset();
        }

        Engine!.Commit();
    }

    public IEnumerable<BTree> Trees => [Schema, T, IxB, IxN, IxD, W];

    private Mem Text(string s) => Mem.FromText(s, _enc);

    private static Mem MaybeInt(long? v) => v.HasValue ? Mem.FromInt(v.Value) : Mem.Null;

    private byte[] RowRec(Row r) => RecordWriter.ToArray([MaybeInt(r.A), Text(r.B), Mem.FromBlob(r.C)]);

    private void IndexOp(bool insert, long id, Row r)
    {
        Mem[] kb = [Text(r.B), Mem.FromInt(id)];
        Mem[] kd = [MaybeInt(r.A), Text(r.B), Mem.FromInt(id)];
        Apply(_ib, kb, insert);
        Apply(_in, kb, insert);
        Apply(_id, kd, insert);
    }

    private void Apply(BtCursor c, Mem[] key, bool insert)
    {
        byte[] rec = RecordWriter.ToArray(key);
        if (insert)
        {
            Drive.Ok(Src, c, () => c.Insert(rec, key));
        }
        else
        {
            int cmp = 1;
            Drive.Ok(Src, c, () => c.MoveToKey(key, 0, out cmp));
            Assert.Equal(0, cmp);
            Drive.Ok(Src, c, () => c.Delete());
        }
    }

    public string RandomText(Random rnd)
    {
        int kind = rnd.Next(30);
        int len = kind == 0 ? rnd.Next(PageSize / 2, PageSize * 2) : rnd.Next(0, 24);
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++)
        {
            int k = rnd.Next(40);
            sb.Append(k < 26 ? (char)('a' + k) : k < 34 ? (char)('A' + (k - 26)) : k == 34 ? ' ' : k == 35 ? 'é' : k == 36 ? '€' : (char)('0' + (k - 37)));
        }

        return sb.ToString();
    }

    /// <summary>One random operation on the table, its indexes and the WITHOUT ROWID table.</summary>
    public void Step(Random rnd)
    {
        Ops++;
        int kind = rnd.Next(100);
        if (kind < 45 || Rows.Count < 3)
        {
            long id = rnd.Next(4) == 0 ? rnd.NextInt64(-50, 5000) : (Rows.Count == 0 ? 1 : Rows.Keys.Max() + 1);
            var blob = new byte[rnd.Next(25) == 0 ? rnd.Next(PageSize * 2) : rnd.Next(40)];
            rnd.NextBytes(blob);
            var row = new Row(rnd.Next(5) == 0 ? null : rnd.NextInt64(-3, 20), RandomText(rnd), blob);
            LastOp = $"insert {id} blob={blob.Length} text={row.B.Length} existing={Rows.ContainsKey(id)}";
            if (Rows.TryGetValue(id, out Row? old))
            {
                IndexOp(false, id, old);
            }

            byte[] rec = RowRec(row);
            Drive.Ok(Src, _t, () => _t.Insert(id, rec));
            IndexOp(true, id, row);
            Rows[id] = row;
        }
        else if (kind < 75)
        {
            long id = Rows.Keys.ElementAt(rnd.Next(Rows.Count));
            LastOp = $"delete {id}";
            IndexOp(false, id, Rows[id]);
            Drive.Ok(Src, _t, () => _t.MoveToRowid(id, out _));
            Drive.Ok(Src, _t, () => _t.Delete());
            Rows.Remove(id);
        }
        else if (kind < 90 || Wr.Count < 3)
        {
            string k = RandomText(rnd);
            var v = new byte[rnd.Next(20) == 0 ? rnd.Next(PageSize * 2) : rnd.Next(30)];
            rnd.NextBytes(v);
            Mem[] key = [Text(k), Mem.FromBlob(v)];
            byte[] rec = RecordWriter.ToArray(key);
            if (Wr.ContainsKey(k))
            {
                int cmp = 1;
                Drive.Ok(Src, _w, () => _w.MoveToKey([Text(k)], 0, out cmp));
                Assert.Equal(0, cmp);
                Drive.Ok(Src, _w, () => _w.Delete());
            }

            Drive.Ok(Src, _w, () => _w.Insert(rec, key));
            Wr[k] = v;
        }
        else
        {
            string k = Wr.Keys.ElementAt(rnd.Next(Wr.Count));
            int cmp = 1;
            Drive.Ok(Src, _w, () => _w.MoveToKey([Text(k)], 0, out cmp));
            Assert.Equal(0, cmp);
            Drive.Ok(Src, _w, () => _w.Delete());
            Wr.Remove(k);
        }
    }

    /// <summary>Counts of balance events over all cursors.</summary>
    public (int Rebalanced, int Splits, int Merges, int Deepened, int Shrunk) Events()
    {
        int a = 0, b = 0, c = 0, d = 0, e = 0;
        foreach (BtCursor x in new[] { _t, _ib, _in, _id, _w })
        {
            a += x.Rebalanced;
            b += x.Splits;
            c += x.Merges;
            d += x.Deepened;
            e += x.Shrunk;
        }

        return (a, b, c, d, e);
    }

    /// <summary>Scans every tree and compares with the model, then runs the structural check.</summary>
    public void Verify()
    {
        _scan.Reset();
        Drive.Ok(Src, _scan, () => _scan.First());
        var rv = new RecordView();
        using var e = Rows.GetEnumerator();
        int n = 0;
        while (_scan.State == CursorState.Valid)
        {
            Assert.True(e.MoveNext());
            Assert.Equal(e.Current.Key, _scan.Rowid);
            Drive.Ok(Src, _scan, _scan.LoadPayload);
            Assert.True(_scan.Payload.SequenceEqual(RowRec(e.Current.Value)));
            n++;
            Drive.Ok(Src, _scan, _scan.Next);
        }

        Assert.Equal(Rows.Count, n);
        _scan.Reset();
        _ = rv;
        CountIndex(IxB, Rows.Count);
        CountIndex(IxN, Rows.Count);
        CountIndex(IxD, Rows.Count);
        CountIndex(W, Wr.Count);
        // WITHOUT ROWID table in key order
        using BtCursor wc = W.OpenCursor();
        Drive.Ok(Src, wc, () => wc.First());
        using var we = Wr.GetEnumerator();
        var rv2 = new RecordView();
        while (wc.State == CursorState.Valid)
        {
            Assert.True(we.MoveNext());
            Drive.Ok(Src, wc, wc.LoadPayload);
            Assert.True(wc.ParseRecord(rv2));
            Assert.True(rv2.GetBytes(0).SequenceEqual(TextCodec.Encode(we.Current.Key, _enc)));
            Assert.True(rv2.GetBytes(1).SequenceEqual(we.Current.Value));
            Drive.Ok(Src, wc, wc.Next);
        }

        var chk = new BtIntegrity(Ctx, () => Drive.Complete(Src));
        Assert.True(chk.Check(Trees), string.Join("\n", chk.Problems));
        if (Img != null)
        {
            Assert.Equal(0, Img.PinnedPages);
        }
    }

    private void CountIndex(BTree t, int expected)
    {
        using BtCursor c = t.OpenCursor();
        Drive.Ok(Src, c, () => c.First());
        int n = 0;
        while (c.State == CursorState.Valid)
        {
            n++;
            Drive.Ok(Src, c, c.Next);
        }

        Assert.Equal(expected, n);
        // and backwards
        Drive.Ok(Src, c, () => c.Last());
        int m = 0;
        while (c.State == CursorState.Valid)
        {
            m++;
            Drive.Ok(Src, c, c.Previous);
        }

        Assert.Equal(expected, m);
    }
}

/// <summary>Index and WITHOUT ROWID trees, all encodings, overflow keys, injected Pending.</summary>
public sealed class BTreeIndexTests
{
    public static IEnumerable<object[]> Configs()
    {
        foreach (int ps in new[] { 512, 4096, 65536 })
        {
            foreach (TextEncoding enc in new[] { TextEncoding.Utf8, TextEncoding.Utf16Le, TextEncoding.Utf16Be })
            {
                foreach (int av in new[] { 0, 1 })
                {
                    foreach (int y in new[] { 0, 1, 2 })
                    {
                        yield return new object[] { ps, enc, av, y };
                    }
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public void RandomWorkload(int pageSize, TextEncoding enc, int autoVacuum, int yieldEvery)
    {
        var db = new TestDb(pageSize, 0, enc, autoVacuum, yieldEvery);
        var rnd = new Random(pageSize + (int)enc * 13 + autoVacuum);
        int n = pageSize == 65536 ? 600 : 1200;
        for (int i = 0; i < n; i++)
        {
            db.Step(rnd);
            if (i % 150 == 149)
            {
                db.Verify();
            }
        }

        db.Verify();
    }
}
