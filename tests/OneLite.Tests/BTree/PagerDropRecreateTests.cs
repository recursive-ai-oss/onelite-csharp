using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Storage.Vfs;
using RecursiveAI.OneLite.Tests.Vfs;

namespace RecursiveAI.OneLite.Tests.Trees;

/// <summary>
/// Dropping and recreating trees through the pager: a rowid table with overflow chains and a WITHOUT ROWID table are filled, dropped (root
/// page freed, pages to the freelist or, in auto-vacuum files, relocated at commit), recreated, refilled with different rows and checked by
/// the pinned <c>sqlite3</c> (integrity_check, and the <c>.dump</c> against the same final state built by the shell).
/// </summary>
public sealed class PagerDropRecreateTests
{
    private const string SqlA = "CREATE TABLE a(x INT, y BLOB)";
    private const string SqlW = "CREATE TABLE w(k TEXT PRIMARY KEY, v BLOB) WITHOUT ROWID";

    public static IEnumerable<object[]> Configs()
    {
        foreach (int ps in new[] { 512, 1024, 4096, 65536 })
        {
            foreach (TextEncoding enc in new[] { TextEncoding.Utf8, TextEncoding.Utf16Le, TextEncoding.Utf16Be })
            {
                // Auto-vacuum files too: dropping and creating trees there must keep the root pages contiguous and header offset 52 exact.
                foreach (int av in new[] { 0, 1, 2 })
                {
                    JournalMode jm = (ps / 512 + (int)enc + av) % 3 == 0 ? JournalMode.Delete : (ps / 512 + (int)enc + av) % 3 == 1 ? JournalMode.Truncate : JournalMode.Persist;
                    yield return new object[] { ps, enc, av, jm };
                }
            }
        }
    }

    private static string Scratch(string name)
    {
        string dir = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "onelite-pager-btree" + "-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    private static byte[] Blob(int ps, int i, int round)
    {
        // every fifth row spills over several overflow pages
        var b = new byte[i % 5 == 0 ? ps * 2 + i : 10 + (i * 7 % 90)];
        new Random(i * 31 + round).NextBytes(b);
        return b;
    }

    private static void SchemaRow(StorageEngine eng, TextEncoding enc, long rowid, string name, uint root, string sql)
    {
        byte[] rec = RecordWriter.ToArray([Mem.FromText("table", enc), Mem.FromText(name, enc), Mem.FromText(name, enc), Mem.FromInt(root), Mem.FromText(sql, enc)]);
        using BtCursor sc = BTree.Table(eng.Context, 1).OpenCursor();
        Assert.Equal(BtStatus.Ok, eng.Run(() => sc.Insert(rowid, rec)));
    }

    private static void DeleteSchemaRow(StorageEngine eng, long rowid)
    {
        using BtCursor sc = BTree.Table(eng.Context, 1).OpenCursor();
        int cmp = 1;
        Assert.Equal(BtStatus.Ok, eng.Run(() => sc.MoveToRowid(rowid, out cmp)));
        Assert.Equal(0, cmp);
        Assert.Equal(BtStatus.Ok, eng.Run(() => sc.Delete()));
    }

    private static string Why(Exception? e) => e is OneLiteException o ? o.Message + " (" + o.Detail + ")" : e?.Message ?? "no error recorded";

    private static uint CreateRoot(StorageEngine eng, bool intKey)
    {
        uint root = 0;
        BtStatus s = eng.Run(() => BtAdmin.CreateTree(eng.Context, intKey, out root));
        Assert.True(s == BtStatus.Ok, Why(eng.Source.LastError));
        return root;
    }

    // Drops a tree. In an auto-vacuum file the largest root can move into the dropped slot; the schema row of the moved table is then
    // rewritten with its new root (what the DDL layer will do), and the caller's root variable follows.
    private static void Drop(StorageEngine eng, TextEncoding enc, BTree tree, ref uint a, long aRow, ref uint w, long wRow)
    {
        var eraser = new TreeEraser(tree, destroy: true);
        BtStatus s = eng.Run(eraser.Step);
        Assert.True(s == BtStatus.Ok, Why(eraser.Error));
        if (eraser.MovedFrom == 0)
        {
            return;
        }

        Assert.Equal(tree.Root, eraser.MovedTo);
        if (eraser.MovedFrom == a)
        {
            a = eraser.MovedTo;
            DeleteSchemaRow(eng, aRow);
            SchemaRow(eng, enc, aRow, "a", a, SqlA);
        }
        else
        {
            Assert.Equal(w, eraser.MovedFrom);
            w = eraser.MovedTo;
            DeleteSchemaRow(eng, wRow);
            SchemaRow(eng, enc, wRow, "w", w, SqlW);
        }
    }

    private static void FillA(StorageEngine eng, uint root, int ps, int count, int round)
    {
        using BtCursor c = BTree.Table(eng.Context, root).OpenCursor();
        for (int i = 1; i <= count; i++)
        {
            byte[] rec = RecordWriter.ToArray([Mem.FromInt(i * 3 + round), Mem.FromBlob(Blob(ps, i, round))]);
            long id = i;
            Assert.Equal(BtStatus.Ok, eng.Run(() => c.Insert(id, rec)));
        }
    }

    private static string WKey(int i, int round) => "k" + ((i * 7919 + round) % 10007).ToString("D5", CultureInfo.InvariantCulture);

    private static void FillW(StorageEngine eng, TextEncoding enc, uint root, int ps, int count, int round)
    {
        using BtCursor c = BTree.Index(eng.Context, root, new KeyInfo(enc, [null])).OpenCursor();
        for (int i = 1; i <= count; i++)
        {
            Mem[] key = [Mem.FromText(WKey(i, round), enc), Mem.FromBlob(Blob(ps, i, round))];
            byte[] rec = RecordWriter.ToArray(key);
            Assert.Equal(BtStatus.Ok, eng.Run(() => c.Insert(rec, key)));
        }
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public void DroppedAndRecreatedTreesPassSqlite(int ps, TextEncoding enc, int av, JournalMode jm)
    {
        _ = SqliteOracle.Require();
        string file = Scratch($"dr-{ps}-{(int)enc}-{av}-{jm}.db");
        File.Delete(file);
        File.Delete(file + "-journal");
        const int FinalA = 120, FinalW = 90;
        try
        {
            using (StorageEngine eng = StorageEngine.Open(new FileVfs(), file, PagerBTreeTests.Options(ps, enc, av, jm, av == 0 ? 0 : 1 + (int)enc + ps / 4096, 0)))
            {
                eng.BeginRead();
                eng.BeginWrite();
                // round 0: both tables, filled; commit
                uint a = CreateRoot(eng, true), w = CreateRoot(eng, false);
                SchemaRow(eng, enc, 1, "a", a, SqlA);
                SchemaRow(eng, enc, 2, "w", w, SqlW);
                FillA(eng, a, ps, 300, 0);
                FillW(eng, enc, w, ps, 200, 0);
                eng.Commit();

                // round 1: drop a, recreate it and refill in the next transaction
                eng.BeginWrite();
                Drop(eng, enc, BTree.Table(eng.Context, a), ref a, 1, ref w, 2);
                DeleteSchemaRow(eng, 1);
                eng.Commit();
                eng.BeginWrite();
                a = CreateRoot(eng, true);
                SchemaRow(eng, enc, 3, "a", a, SqlA);
                FillA(eng, a, ps, 80, 1);
                eng.Commit();

                // round 2: drop both again and recreate them inside one transaction (freed pages are reused at once)
                eng.BeginWrite();
                Drop(eng, enc, BTree.Table(eng.Context, a), ref a, 3, ref w, 2);
                a = 0;
                DeleteSchemaRow(eng, 3);
                Drop(eng, enc, BTree.Index(eng.Context, w, new KeyInfo(enc, [null])), ref a, 0, ref w, 2);
                DeleteSchemaRow(eng, 2);
                a = CreateRoot(eng, true);
                SchemaRow(eng, enc, 4, "a", a, SqlA);
                FillA(eng, a, ps, FinalA, 2);
                w = CreateRoot(eng, false);
                SchemaRow(eng, enc, 5, "w", w, SqlW);
                FillW(eng, enc, w, ps, FinalW, 2);
                eng.Commit();
                eng.EndRead();
            }

            var sb = new StringBuilder();
            string encName = enc == TextEncoding.Utf8 ? "UTF-8" : enc == TextEncoding.Utf16Le ? "UTF-16le" : "UTF-16be";
            sb.Append("PRAGMA encoding='").Append(encName).Append("'; PRAGMA page_size=").Append(ps).Append("; PRAGMA auto_vacuum=").Append(av).Append(";\n");
            sb.Append(SqlA).Append(";\n").Append(SqlW).Append(";\nBEGIN;\n");
            for (int i = 1; i <= FinalA; i++)
            {
                sb.Append("INSERT INTO a(rowid,x,y) VALUES(").Append(i).Append(',').Append(i * 3 + 2).Append(",X'").Append(Convert.ToHexString(Blob(ps, i, 2))).Append("');\n");
            }

            for (int i = 1; i <= FinalW; i++)
            {
                sb.Append("INSERT INTO w VALUES('").Append(WKey(i, 2)).Append("',X'").Append(Convert.ToHexString(Blob(ps, i, 2))).Append("');\n");
            }

            sb.Append("COMMIT;\n");
            string dir = Path.GetDirectoryName(file)!;
            (int exit, string output) = SqliteOracle.Run(file, "PRAGMA integrity_check; PRAGMA auto_vacuum; SELECT count(*) FROM a; SELECT count(*) FROM w;", 120_000);
            Assert.Equal(0, exit);
            string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(lines[0] == "ok", output.Length > 1000 ? output[..1000] : output);
            Assert.Equal(av.ToString(CultureInfo.InvariantCulture), lines[1]);
            Assert.Equal(FinalA.ToString(CultureInfo.InvariantCulture), lines[2]);
            Assert.Equal(FinalW.ToString(CultureInfo.InvariantCulture), lines[3]);

            string reference = file + ".ref";
            File.Delete(reference);
            try
            {
                SqliteWriteInteropTests.RunScript(reference, sb.ToString(), dir);
                string expect = SqliteWriteInteropTests.RunScript(reference, ".dump\n", dir);
                string actual = SqliteWriteInteropTests.RunScript(file, ".dump\n", dir);
                Assert.True(expect == actual, "dump differs: reference " + expect.Length + " bytes, ours " + actual.Length + " bytes");
            }
            finally
            {
                File.Delete(reference);
            }

            string? log = Environment.GetEnvironmentVariable("ONELITE_INTEROP_LOG");
            if (log != null)
            {
                lock (typeof(PagerDropRecreateTests))
                {
                    File.AppendAllText(log, $"ok droprecreate page={ps} enc={(int)enc} av={av} journal={jm} size={new FileInfo(file).Length}\n");
                }
            }
        }
        finally
        {
            File.Delete(file);
            File.Delete(file + "-journal");
        }
    }

    /// <summary>
    /// A tree created after content pages exist takes exactly page largest+1 in an auto-vacuum file: the page there (a B-tree page, an
    /// overflow page, or a free page) moves out of the way. The reference file is built by the shell from the same statements.
    /// </summary>
    [Theory]
    [InlineData(512, 1, 0)]
    [InlineData(512, 2, 0)]
    [InlineData(1024, 1, 0)]
    [InlineData(4096, 2, 0)]
    [InlineData(512, 1, 1)]
    [InlineData(512, 2, 2)]
    [InlineData(1024, 1, 3)]
    [InlineData(4096, 2, 7)]
    public void CreatingAfterContentMovesPagesInTheWay(int ps, int av, int yield)
    {
        _ = SqliteOracle.Require();
        string file = Scratch($"mv-{ps}-{av}-{yield}.db");
        File.Delete(file);
        File.Delete(file + "-journal");
        const int Rows = 150;
        var roots = new List<uint>();
        try
        {
            using (StorageEngine eng = StorageEngine.Open(new FileVfs(), file, PagerBTreeTests.Options(ps, TextEncoding.Utf8, av, JournalMode.Delete, yield, 0)))
            {
                eng.BeginRead();
                eng.BeginWrite();
                uint a = CreateRoot(eng, true);
                SchemaRow(eng, TextEncoding.Utf8, 1, "a", a, SqlA);
                FillA(eng, a, ps, Rows, 0);
                eng.Commit();

                // Several more trees, in separate and in the same transaction, so that each one lands on a page the content of a already uses.
                for (int k = 0; k < 5; k++)
                {
                    if (k % 2 == 0)
                    {
                        eng.BeginWrite();
                    }

                    uint r = CreateRoot(eng, k % 3 != 1);
                    roots.Add(r);
                    SchemaRow(eng, TextEncoding.Utf8, 2 + k, "t" + k, r, k % 3 != 1 ? "CREATE TABLE t" + k + "(x INT, y BLOB)" : "CREATE TABLE t" + k + "(k TEXT PRIMARY KEY, v BLOB) WITHOUT ROWID");
                    if (k % 2 == 1 || k == 4)
                    {
                        eng.Commit();
                    }
                }

                eng.EndRead();
            }

            var sb = new StringBuilder();
            sb.Append("PRAGMA page_size=").Append(ps).Append("; PRAGMA auto_vacuum=").Append(av).Append(";\n");
            sb.Append(SqlA).Append(";\nBEGIN;\n");
            for (int i = 1; i <= Rows; i++)
            {
                sb.Append("INSERT INTO a(rowid,x,y) VALUES(").Append(i).Append(',').Append(i * 3).Append(",X'").Append(Convert.ToHexString(Blob(ps, i, 0))).Append("');\n");
            }

            sb.Append("COMMIT;\n");
            for (int k = 0; k < 5; k++)
            {
                sb.Append(k % 3 != 1 ? "CREATE TABLE t" + k + "(x INT, y BLOB);\n" : "CREATE TABLE t" + k + "(k TEXT PRIMARY KEY, v BLOB) WITHOUT ROWID;\n");
            }

            string dir = Path.GetDirectoryName(file)!;
            (int exit, string output) = SqliteOracle.Run(file, "PRAGMA integrity_check; PRAGMA auto_vacuum; SELECT count(*) FROM a;", 120_000);
            Assert.Equal(0, exit);
            string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(lines[0] == "ok", output.Length > 1000 ? output[..1000] : output);
            Assert.Equal(av.ToString(CultureInfo.InvariantCulture), lines[1]);
            Assert.Equal(Rows.ToString(CultureInfo.InvariantCulture), lines[2]);
            string reference = file + ".ref";
            File.Delete(reference);
            try
            {
                SqliteWriteInteropTests.RunScript(reference, sb.ToString(), dir);
                string expect = SqliteWriteInteropTests.RunScript(reference, ".dump\n", dir);
                string actual = SqliteWriteInteropTests.RunScript(file, ".dump\n", dir);
                Assert.True(expect == actual, "dump differs: reference " + expect.Length + " bytes, ours " + actual.Length + " bytes");
            }
            finally
            {
                File.Delete(reference);
            }
        }
        finally
        {
            File.Delete(file);
            File.Delete(file + "-journal");
        }
    }
}
