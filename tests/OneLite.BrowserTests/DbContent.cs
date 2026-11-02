using System;
using System.Text;
using System.Threading.Tasks;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Record;

using TextEncoding = RecursiveAI.OneLite.Storage.TextEncoding;

namespace RecursiveAI.OneLite.BrowserTests;

/// <summary>
/// The deterministic database the browser tests write and check: <c>t(a INT, b TEXT, c BLOB)</c> with an index <c>tb</c> on <c>b</c>. Row <i>i</i> has
/// a = 7i, b = r000000-xxx, and a blob that is NULL every 11th row, 9000 bytes every 50th (overflow pages) and otherwise up to 700 bytes.
/// tools/scripts/browser_tests.py builds the same content with <c>sqlite3</c> to compare dumps.
/// </summary>
internal static class DbContent
{
    public const string CreateTable = "CREATE TABLE t(a INT, b TEXT, c BLOB)";
    public const string CreateIndex = "CREATE INDEX tb ON t(b)";

    public static string Text(int i) => $"r{i:D6}-{new string((char)('a' + (i % 26)), i % 17)}";

    public static byte[]? Blob(int i)
    {
        if (i % 11 == 0)
        {
            return null;
        }

        byte[] b = new byte[i % 50 == 0 ? 9000 : (i * 37) % 700];
        for (int j = 0; j < b.Length; j++)
        {
            b[j] = (byte)((i * 31) + (j * 7));
        }

        return b;
    }

    public static byte[] Record(int i) => RecordWriter.ToArray([Mem.FromInt(i * 7L), Mem.FromText(Text(i), TextEncoding.Utf8), Blob(i) is { } c ? Mem.FromBlob(c) : Mem.Null]);

    /// <summary>Writes rows <paramref name="from"/> to <paramref name="to"/> into an open write transaction, creating the schema when <paramref name="from"/> is 1. Returns the two root pages.</summary>
    public static async Task<(uint Table, uint Index)> Insert(Drive d, int from, int to, uint tRoot, uint iRoot)
    {
        var src = new PagerBtSource(d.Pager);
        var ctx = new BtContext(src, TextEncoding.Utf8);
        if (from == 1)
        {
            PageFrame p1 = await d.Get(1).ConfigureAwait(false);
            await d.MakeWritable(p1).ConfigureAwait(false);
            DatabaseHeader.SetUInt32(p1.Data, DatabaseHeader.SchemaCookieOffset, 1);
            d.Pager.Release(p1);
            uint t = 0, ix = 0;
            await d.RunBt(() => BtAdmin.CreateTree(ctx, true, out t), () => src.LastError).ConfigureAwait(false);
            await d.RunBt(() => BtAdmin.CreateTree(ctx, false, out ix), () => src.LastError).ConfigureAwait(false);
            tRoot = t;
            iRoot = ix;
            using BtCursor sc = BTree.Table(ctx, 1).OpenCursor();
            (string Type, string Name, uint Root, string Sql)[] schema = [("table", "t", tRoot, CreateTable), ("index", "tb", iRoot, CreateIndex)];
            for (int k = 0; k < schema.Length; k++)
            {
                byte[] rec = RecordWriter.ToArray([
                    Mem.FromText(schema[k].Type, TextEncoding.Utf8), Mem.FromText(schema[k].Name, TextEncoding.Utf8), Mem.FromText("t", TextEncoding.Utf8),
                    Mem.FromInt(schema[k].Root), Mem.FromText(schema[k].Sql, TextEncoding.Utf8)]);
                long rowid = k + 1;
                await d.RunBt(() => sc.Insert(rowid, rec), () => sc.Error).ConfigureAwait(false);
            }
        }

        using BtCursor tc = BTree.Table(ctx, tRoot).OpenCursor();
        using BtCursor ic = BTree.Index(ctx, iRoot, KeyInfo.Binary(TextEncoding.Utf8, 2)).OpenCursor();
        for (int i = from; i <= to; i++)
        {
            byte[] rec = Record(i);
            long rowid = i;
            await d.RunBt(() => tc.Insert(rowid, rec), () => tc.Error).ConfigureAwait(false);
            Mem[] key = [Mem.FromText(Text(i), TextEncoding.Utf8), Mem.FromInt(i)];
            byte[] krec = RecordWriter.ToArray(key);
            await d.RunBt(() => ic.Insert(krec, key), () => ic.Error).ConfigureAwait(false);
        }

        return (tRoot, iRoot);
    }

    /// <summary>Reads the table and the index back through the B-tree and checks every row against the generator.</summary>
    public static async Task Verify(Drive d, int rows)
    {
        var src = new PagerBtSource(d.Pager);
        var ctx = new BtContext(src, TextEncoding.Utf8);
        var rv = new RecordView();
        uint tRoot = 0, iRoot = 0;
        using (BtCursor sc = BTree.Table(ctx, 1).OpenCursor())
        {
            await d.RunBt(sc.First, () => sc.Error).ConfigureAwait(false);
            while (sc.State == CursorState.Valid)
            {
                await d.RunBt(sc.LoadPayload, () => sc.Error).ConfigureAwait(false);
                Check.True(sc.ParseRecord(rv), "a schema row parses");
                string name = Encoding.UTF8.GetString(rv.GetBytes(1));
                if (name == "t")
                {
                    tRoot = (uint)rv.GetInt64(3);
                }
                else if (name == "tb")
                {
                    iRoot = (uint)rv.GetInt64(3);
                }

                await d.RunBt(sc.Next, () => sc.Error).ConfigureAwait(false);
            }
        }

        Check.True(tRoot > 1 && iRoot > 1, "the schema names the table and the index");
        using (BtCursor tc = BTree.Table(ctx, tRoot).OpenCursor())
        {
            await d.RunBt(tc.First, () => tc.Error).ConfigureAwait(false);
            for (int i = 1; i <= rows; i++)
            {
                Check.True(tc.State == CursorState.Valid, $"table row {i} exists");
                Check.Equal((long)i, tc.Rowid, "rowid");
                await d.RunBt(tc.LoadPayload, () => tc.Error).ConfigureAwait(false);
                Check.True(tc.ParseRecord(rv), $"row {i} parses");
                Check.Equal(i * 7L, rv.GetInt64(0), $"row {i} column a");
                Check.Equal(Text(i), Encoding.UTF8.GetString(rv.GetBytes(1)), $"row {i} column b");
                byte[]? c = Blob(i);
                if (c == null)
                {
                    Check.Equal(0UL, rv.SerialTypeOf(2), $"row {i} column c is NULL");
                }
                else
                {
                    Check.BytesEqual(c, rv.GetBytes(2), $"row {i} column c");
                }

                await d.RunBt(tc.Next, () => tc.Error).ConfigureAwait(false);
            }

            Check.True(tc.State != CursorState.Valid, "no rows beyond the last");
        }

        using BtCursor ic = BTree.Index(ctx, iRoot, KeyInfo.Binary(TextEncoding.Utf8, 2)).OpenCursor();
        await d.RunBt(ic.First, () => ic.Error).ConfigureAwait(false);
        for (int i = 1; i <= rows; i++)
        {
            Check.True(ic.State == CursorState.Valid, $"index entry {i} exists");
            await d.RunBt(ic.LoadPayload, () => ic.Error).ConfigureAwait(false);
            Check.True(ic.ParseRecord(rv), $"index entry {i} parses");
            Check.Equal(Text(i), Encoding.UTF8.GetString(rv.GetBytes(0)), $"index entry {i} key");
            Check.Equal((long)i, rv.GetInt64(1), $"index entry {i} rowid");
            await d.RunBt(ic.Next, () => ic.Error).ConfigureAwait(false);
        }

        Check.True(ic.State != CursorState.Valid, "no index entries beyond the last");
    }
}
