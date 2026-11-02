using RecursiveAI.OneLite;
using RecursiveAI.OneLite.Storage;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Oracle;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Storage.Vfs;

// onelite-btreeread: reads a database with the B-tree layer.
//   render <db>   the canonical rendering of the whole database (the format tools/OneLite.Oracle defines), for the interop runner's --reader
//   check <db>    the structural integrity check; prints "ok" or the problems, then a line of counts
// The file is opened through the pager, so a hot rollback journal next to it is recovered first (the file set is modified: give it a copy).
// A database in WAL mode cannot be opened through the pager yet; it is read as a plain image of the main file (no log).
// ONELITE_YIELD=n turns every n-th page request into Pending, to run the restart paths; ONELITE_READER=image forces the plain image reader.
return Run(args);

static int Run(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("usage: render <db> | check <db>");
        return 2;
    }

    int yield = int.Parse(Environment.GetEnvironmentVariable("ONELITE_YIELD") ?? "0", CultureInfo.InvariantCulture);
    try
    {
        bool image = Environment.GetEnvironmentVariable("ONELITE_READER") == "image" || IsWal(args[1]);
        StorageEngine? engine = null;
        IBtPageSource src;
        Action pump;
        BtContext ctx;
        if (image)
        {
            ImagePageSource img = ImagePageSource.FromBytes(File.ReadAllBytes(args[1]));
            img.YieldEvery = yield;
            src = img;
            pump = img.Complete;
            ctx = new BtContext(img, img.Encoding);
        }
        else
        {
            engine = StorageEngine.Open(new FileVfs(), args[1], new PagerOptions { YieldEvery = yield, CacheSize = 2000 });
            engine.BeginRead();
            src = engine.Source;
            pump = engine.Pager.ExecuteSync;
            ctx = engine.Context;
        }

        using (engine)
        {
            switch (args[0])
            {
                case "render":
                    Console.Out.Write(Renderer.RenderAll(src, ctx, pump));
                    return 0;
                case "check":
                    return Renderer.Check(src, ctx, pump);
                default:
                    Console.Error.WriteLine("unknown command " + args[0]);
                    return 2;
            }
        }
    }
    catch (OneLiteException e)
    {
        Console.Out.Write("error\t" + e.ExtendedCode.ToString(CultureInfo.InvariantCulture) + "\t" + e.Message + "\n");
        Console.Error.WriteLine(e + " [" + e.Detail + "]");
        return 1;
    }
}

static bool IsWal(string path)
{
    using FileStream f = File.OpenRead(path);
    var h = new byte[20];
    return f.Read(h, 0, 20) == 20 && h[18] == 2 && h[19] == 2;
}

internal static class Renderer
{
    private static uint U32(byte[] h, int o) => (uint)((h[o] << 24) | (h[o + 1] << 16) | (h[o + 2] << 8) | h[o + 3]);

    private static byte[] Header(IBtPageSource src, Action pump)
    {
        PageHandle? p1 = null;
        for (; ; )
        {
            BtStatus st = src.GetPage(1, out p1);
            if (st == BtStatus.Ok)
            {
                break;
            }

            if (st == BtStatus.Error)
            {
                throw src.LastError as OneLiteException ?? OneLiteException.Corrupt(1, "page 1 cannot be read");
            }

            pump();
        }

        byte[] h = p1!.Data.AsSpan(0, 100).ToArray();
        src.Release(p1);
        return h;
    }

    private static string Pragma(string name, string value) => $"cols\t{name}\nrow\t{value}\nend\n";

    public static string RenderAll(IBtPageSource src, BtContext ctx, Action pump)
    {
        byte[] h = Header(src, pump);
        var sb = new StringBuilder();
        string enc = ctx.Encoding == TextEncoding.Utf8 ? "UTF-8" : ctx.Encoding == TextEncoding.Utf16Le ? "UTF-16le" : "UTF-16be";
        int av = src.AutoVacuum ? (U32(h, 64) != 0 ? 2 : 1) : 0;
        sb.Append(Pragma("page_size", "I:" + src.PageSize.ToString(CultureInfo.InvariantCulture)));
        sb.Append(Pragma("encoding", "T:\"" + enc + "\""));
        sb.Append(Pragma("auto_vacuum", "I:" + av.ToString(CultureInfo.InvariantCulture)));
        sb.Append(Pragma("user_version", "I:" + ((int)U32(h, 60)).ToString(CultureInfo.InvariantCulture)));
        sb.Append(Pragma("application_id", "I:" + ((int)BitConverter.ToUInt32([h[71], h[70], h[69], h[68]])).ToString(CultureInfo.InvariantCulture)));
        sb.Append(Pragma("freelist_count", "I:" + U32(h, 36).ToString(CultureInfo.InvariantCulture)));
        sb.Append(Pragma("page_count", "I:" + src.PageCount.ToString(CultureInfo.InvariantCulture)));
        List<SchemaEntry> schema = SchemaLite.Read(ctx, pump);
        sb.Append("cols\ttype\tname\ttbl_name\tsql\n");
        foreach (SchemaEntry e in schema)
        {
            sb.Append("row\t").Append(Str(e.Type)).Append('\t').Append(Str(e.Name)).Append('\t').Append(Str(e.TableName)).Append('\t')
              .Append(e.Sql == null ? "N" : Str(e.Sql)).Append('\n');
        }

        sb.Append("end\n");
        foreach (SchemaEntry e in schema)
        {
            if (e.Type != "table" || e.Sql == null || e.Sql.StartsWith("CREATE VIRTUAL", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            sb.Append("table\t").Append(e.Name).Append('\n');
            RenderTable(src, ctx, e, sb, pump);
        }

        return sb.ToString();
    }

    private static string Str(string s) => Canonical.Text(Encoding.UTF8.GetBytes(s));

    private static void RenderTable(IBtPageSource src, BtContext ctx, SchemaEntry e, StringBuilder sb, Action pump)
    {
        TableDef def = SchemaLite.ParseTable(e.Sql!) ?? throw new InvalidOperationException("cannot read the definition of " + e.Name);
        sb.Append("cols\t").Append(string.Join('\t', def.Columns.Select(c => c.Name))).Append('\n');
        BTree tree = def.WithoutRowid ? BTree.Index(ctx, e.RootPage, KeyInfo.Binary(ctx.Encoding, 0)) : BTree.Table(ctx, e.RootPage);
        using BtCursor c = tree.OpenCursor();
        var rv = new RecordView();
        var vals = new string[def.Columns.Count];
        Drive(pump, c, c.First);
        while (c.State == CursorState.Valid)
        {
            Drive(pump, c, c.LoadPayload);
            if (!c.ParseRecord(rv))
            {
                throw OneLiteException.Corrupt(e.RootPage, "malformed record in " + e.Name);
            }

            long rowid = def.WithoutRowid ? 0 : c.Rowid;
            for (int i = 0; i < def.Columns.Count; i++)
            {
                ColumnDef col = def.Columns[i];
                Mem m = default;
                if (col.Generated == 1)
                {
                    Mem? res = Generated(def, rv, rowid, col, ctx.Encoding);
                    if (res == null)
                    {
                        vals[i] = "?";
                        continue;
                    }

                    m = res.Value;
                }
                else if (i == def.RowidAlias)
                {
                    m = Mem.FromInt(rowid);
                }
                else if (def.RecordIndex[i] < rv.ColumnCount)
                {
                    rv.Load(def.RecordIndex[i], ref m);
                }

                vals[i] = Value(m, col, ctx.Encoding);
            }

            sb.Append("row\t").Append(string.Join('\t', vals)).Append('\n');
            Drive(pump, c, c.Next);
        }

        sb.Append("end\n");
    }

    private static Mem? Generated(TableDef def, RecordView rv, long rowid, ColumnDef col, TextEncoding enc)
    {
        _ = enc;
        if (col.GeneratedExpr == null)
        {
            return null;
        }

        bool ok = SchemaLite.TryEvalSimple(col.GeneratedExpr, name =>
        {
            int ix = -1;
            for (int k = 0; k < def.Columns.Count; k++)
            {
                if (string.Equals(def.Columns[k].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    ix = k;
                }
            }

            if (ix < 0 || def.Columns[ix].Generated == 1)
            {
                return null;
            }

            Mem v = default;
            if (ix == def.RowidAlias)
            {
                return Mem.FromInt(rowid);
            }

            if (def.RecordIndex[ix] < rv.ColumnCount)
            {
                rv.Load(def.RecordIndex[ix], ref v);
            }

            return v;
        }, out Mem result);
        return ok ? result : null;
    }

    private static string Value(Mem m, ColumnDef col, TextEncoding enc)
    {
        switch (m.Kind)
        {
            case StorageClass.Null: return "N";
            case StorageClass.Integral:
                // A REAL column holds integers in the record and reads them back as floats (datatype3 §3).
                return col.Affinity == 'R' ? "R:" + Canonical.Real(m.IntValue) : "I:" + m.IntValue.ToString(CultureInfo.InvariantCulture);
            case StorageClass.Real: return "R:" + Canonical.Real(m.RealValue);
            case StorageClass.Text:
                return Canonical.Text(enc == TextEncoding.Utf8 ? m.Bytes : TextCodec.ToUtf8(m.Bytes, enc));
            default: return "B:" + Convert.ToHexStringLower(m.Bytes);
        }
    }

    private static void Drive(Action pump, BtCursor c, Func<BtStatus> op)
    {
        for (; ; )
        {
            BtStatus s = op();
            if (s == BtStatus.Ok)
            {
                return;
            }

            if (s == BtStatus.Error)
            {
                throw c.Error ?? new InvalidOperationException("cursor failed");
            }

            pump();
        }
    }

    public static int Check(IBtPageSource src, BtContext ctx, Action pump)
    {
        List<SchemaEntry> schema = SchemaLite.Read(ctx, pump);
        var trees = new List<BTree> { BTree.Table(ctx, 1) };
        int tables = 0, indexes = 0;
        foreach (SchemaEntry e in schema)
        {
            if (e.RootPage == 0 || (e.Sql != null && e.Sql.StartsWith("CREATE VIRTUAL", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            TableDef? def = e.Type == "table" ? SchemaLite.ParseTable(e.Sql ?? "") : null;
            if (e.Type == "table" && !(def?.WithoutRowid ?? false))
            {
                trees.Add(BTree.Table(ctx, e.RootPage));
                tables++;
            }
            else
            {
                trees.Add(BTree.Index(ctx, e.RootPage, KeyInfo.Binary(ctx.Encoding, 0)));
                indexes++;
            }
        }

        var chk = new BtIntegrity(ctx, pump);
        bool ok = chk.Check(trees);
        Console.WriteLine(ok ? "ok" : string.Join('\n', chk.Problems));
        Console.WriteLine($"pages={src.PageCount} tables={tables} indexes={indexes} freelist={U32(Header(src, pump), 36)}");
        return ok ? 0 : 1;
    }
}
