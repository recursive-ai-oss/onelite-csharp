using RecursiveAI.OneLite.Storage;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Tests.Vfs;

namespace RecursiveAI.OneLite.Tests.Trees;

/// <summary>
/// Files made by the reference <c>sqlite3</c> are loaded, churned through the B-tree layer (rows and their index entries deleted in bulk,
/// then put back in a different order, so pages split, merge, drop to the freelist and come back), written out, and checked by
/// <c>sqlite3</c>: <c>integrity_check</c> is ok and the <c>.dump</c> is the one stored for the original. Needs the generated corpus
/// (<c>ONELITE_CORPUS</c> or <c>~/onelite/corpus/generated</c>) and the shell; skipped without them.
/// </summary>
public sealed class CorpusChurnTests
{
    private static string? CorpusDir
    {
        get
        {
            string? p = Environment.GetEnvironmentVariable("ONELITE_CORPUS")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onelite", "corpus", "generated");
            return File.Exists(Path.Combine(p, "manifest.json")) ? p : null;
        }
    }

    public static IEnumerable<object[]> Cases()
    {
        string? dir = CorpusDir;
        if (dir == null)
        {
            yield return new object[] { "none", 0 };
            yield break;
        }

        using JsonDocument m = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")));
        foreach (JsonElement c in m.RootElement.GetProperty("cases").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            if (name.StartsWith("m-", StringComparison.Ordinal) || name.StartsWith("res-", StringComparison.Ordinal))
            {
                yield return new object[] { name, 0 };
            }
        }

        // a few with injected Pending
        yield return new object[] { "m-p512-utf8-av0", 1 };
        yield return new object[] { "m-p4096-utf16le-av2", 2 };
        yield return new object[] { "m-p1024-utf16be-av1", 3 };
    }

    private sealed record Table(string Name, uint Root, int[] IndexColumns, uint[] IndexRoots, string[] IndexNames);

    [Theory]
    [MemberData(nameof(Cases))]
    public void ChurnKeepsTheDatabaseIntact(string name, int yieldEvery)
    {
        string? dir = CorpusDir;
        Assert.SkipUnless(dir != null && name != "none", "generated corpus not installed");
        using JsonDocument m = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!, "manifest.json")));
        JsonElement c = m.RootElement.GetProperty("cases").EnumerateArray().First(x => x.GetProperty("name").GetString() == name);
        string db = Path.Combine(dir!, c.GetProperty("db").GetString()!);
        string expectedDump = File.ReadAllText(Path.Combine(dir!, c.GetProperty("expectedDump").GetString()!));
        ImagePageSource src = ImagePageSource.FromBytes(File.ReadAllBytes(db));
        src.YieldEvery = yieldEvery;
        var ctx = new BtContext(src, src.Encoding);
        List<SchemaEntry> schema = SchemaLite.Read(ctx, src.Complete);
        var rnd = new Random(name.Aggregate(17, (h, ch) => (h * 31) + ch) ^ yieldEvery);
        TextEncoding enc = src.Encoding;
        int churned = 0;
        // (table, [(index name, record columns that form the key)]): the indexes the base script creates on t1 and ik.
        (string table, string[] index, int[][] cols)[] plan = [("t1", ["t1s", "t1ir"], [[3], [1, 2]]), ("ik", ["ik_k"], [[1]]), ("ov", [], []), ("audit", [], [])];
        foreach ((string table, string[] index, int[][] cols) in plan)
        {
            SchemaEntry? te = schema.FirstOrDefault(e => e.Type == "table" && e.Name == table);
            if (te == null)
            {
                continue;
            }

            BTree tt = BTree.Table(ctx, te.RootPage);
            var idx = new List<BTree>();
            for (int i = 0; i < index.Length; i++)
            {
                SchemaEntry ie = schema.First(e => e.Type == "index" && e.Name == index[i]);
                idx.Add(BTree.Index(ctx, ie.RootPage, KeyInfo.Binary(enc, cols[i].Length + 1)));
            }

            var rows = new List<(long Id, byte[] Rec)>();
            using (BtCursor sc = tt.OpenCursor())
            {
                Drive.Ok(src, sc, () => sc.First());
                while (sc.State == CursorState.Valid)
                {
                    Drive.Ok(src, sc, sc.LoadPayload);
                    rows.Add((sc.Rowid, sc.Payload.ToArray()));
                    Drive.Ok(src, sc, sc.Next);
                }
            }

            using BtCursor tc = tt.OpenCursor();
            var ics = idx.Select(t => t.OpenCursor()).ToList();
            for (int round = 0; round < 3; round++)
            {
                var removed = rows.Where(_ => rnd.Next(3) == 0 || round == 2).OrderBy(_ => rnd.Next()).ToList();
                foreach ((long id, byte[] rec) in removed)
                {
                    Drive.Ok(src, tc, () => tc.MoveToRowid(id, out _));
                    Drive.Ok(src, tc, () => tc.Delete());
                    for (int i = 0; i < ics.Count; i++)
                    {
                        Mem[] key = Key(rec, id, cols[i]);
                        int cmp = 1;
                        BtCursor ic = ics[i];
                        Drive.Ok(src, ic, () => ic.MoveToKey(key, 0, out cmp));
                        Assert.Equal(0, cmp);
                        Drive.Ok(src, ic, () => ic.Delete());
                    }

                    churned++;
                }

                foreach ((long id, byte[] rec) in removed.OrderBy(_ => rnd.Next()))
                {
                    Drive.Ok(src, tc, () => tc.Insert(id, rec));
                    for (int i = 0; i < ics.Count; i++)
                    {
                        Mem[] key = Key(rec, id, cols[i]);
                        byte[] krec = RecordWriter.ToArray(key);
                        BtCursor ic = ics[i];
                        Drive.Ok(src, ic, () => ic.Insert(krec, key));
                    }
                }
            }

            foreach (BtCursor ic in ics)
            {
                ic.Dispose();
            }
        }

        var chk = new BtIntegrity(ctx, src.Complete);
        var trees = new List<BTree> { BTree.Table(ctx, 1) };
        foreach (SchemaEntry e in schema.Where(e => e.RootPage > 0 && (e.Sql == null || !e.Sql.StartsWith("CREATE VIRTUAL", StringComparison.OrdinalIgnoreCase))))
        {
            TableDef? def = e.Type == "table" ? SchemaLite.ParseTable(e.Sql ?? "") : null;
            trees.Add(e.Type == "table" && !(def?.WithoutRowid ?? false) ? BTree.Table(ctx, e.RootPage) : BTree.Index(ctx, e.RootPage, KeyInfo.Binary(enc, 0)));
        }

        Assert.True(chk.Check(trees), string.Join("\n", chk.Problems));
        if (SqliteOracle.Path == null)
        {
            return; // the structural check above ran; the shell checks need the pinned sqlite3
        }

        string tmp = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "onelite-btree-interop" + "-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(tmp);
        string file = Path.Combine(tmp, "churn-" + name + "-" + yieldEvery + ".db");
        File.WriteAllBytes(file, src.ToBytes());
        try
        {
            (int exit, string output) = SqliteOracle.Run(file, "PRAGMA integrity_check;", 120_000);
            Assert.Equal(0, exit);
            Assert.Equal("ok", output.Trim());
            (int exit2, string dump) = SqliteOracle.Run(file, ".dump", 120_000);
            Assert.Equal(0, exit2);
            Assert.True(Norm(expectedDump) == Norm(dump), "the .dump after churn differs from the stored one");
            string? log = Environment.GetEnvironmentVariable("ONELITE_CHURN_LOG");
            if (log != null)
            {
                File.AppendAllText(log, $"ok {name} yield={yieldEvery} rowsChurned={churned} pages={src.PageCount} pending={src.PendingCount}\n");
            }
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static string Norm(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

    private static Mem[] Key(byte[] rec, long id, int[] cols)
    {
        var rv = new RecordView();
        Assert.True(rv.Parse(rec, 0, rec.Length));
        var key = new Mem[cols.Length + 1];
        for (int i = 0; i < cols.Length; i++)
        {
            rv.Load(cols[i], ref key[i]);
        }

        key[cols.Length] = Mem.FromInt(id);
        return key;
    }
}
