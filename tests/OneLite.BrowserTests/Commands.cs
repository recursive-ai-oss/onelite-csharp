using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Pager;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.Storage.Vfs;
using RecursiveAI.OneLite.Storage.Vfs.Browser;

using TextEncoding = RecursiveAI.OneLite.Storage.TextEncoding;

namespace RecursiveAI.OneLite.BrowserTests;

/// <summary>
/// Commands the runner sends to a page or worker: lock holders for the two-context tests, database building and checking, and the corpus import
/// and export that need bytes to cross the JavaScript boundary. Arguments are <c>|</c>-separated text.
/// </summary>
internal static partial class Exports
{
    private static readonly Dictionary<string, (Backend Backend, IVfsFile File)> s_held = [];
    private static byte[]? s_export;

    [JSExport]
    internal static async Task<string> Command(string cmd, string arg)
    {
        string[] a = arg.Split('|');
        try
        {
            switch (cmd)
            {
                case "wipe":
                    await BrowserStorage.WipeAsync().ConfigureAwait(false);
                    return "ok";

                case "caps":
                    return BrowserStorage.Capabilities();

                // lock.open|name|backend|path : opens the database file and keeps it for the lock.* commands.
                case "lock.open":
                    {
                        Backend b = await Backend.Create(a[1]).ConfigureAwait(false);
                        try
                        {
                            s_held[a[0]] = (b, await b.Vfs.OpenAsync(a[2], VfsOpenOptions.ReadWrite | VfsOpenOptions.Create | VfsOpenOptions.MainDb).ConfigureAwait(false));
                        }
                        catch
                        {
                            b.Dispose();
                            throw;
                        }

                        return "ok";
                    }

                // lock.lock|name|level : the try-lock. Returns true or false (BUSY).
                case "lock.lock":
                    return (await s_held[a[0]].File.LockAsync(Enum.Parse<LockLevel>(a[1])).ConfigureAwait(false)) ? "true" : "false";

                case "lock.unlock":
                    await s_held[a[0]].File.UnlockAsync(Enum.Parse<LockLevel>(a[1])).ConfigureAwait(false);
                    return "ok";

                case "lock.check-reserved":
                    return (await s_held[a[0]].File.CheckReservedLockAsync().ConfigureAwait(false)) ? "true" : "false";

                case "lock.write":
                    await s_held[a[0]].File.WriteAsync(0, new byte[] { 1, 2, 3, 4 }).ConfigureAwait(false);
                    await s_held[a[0]].File.SyncAsync(SyncMode.Full).ConfigureAwait(false);
                    return "ok";

                case "lock.close":
                    {
                        (Backend b, IVfsFile f) = s_held[a[0]];
                        s_held.Remove(a[0]);
                        await f.UnlockAsync(LockLevel.None).ConfigureAwait(false);
                        f.Dispose();
                        await b.Settle().ConfigureAwait(false);
                        b.Dispose();
                        return "ok";
                    }

                // db.build|backend|path|rows|wal
                case "db.build":
                    {
                        using Backend b = await Backend.Create(a[0]).ConfigureAwait(false);
                        await DbTests.BuildDb(b, a[1], int.Parse(a[2], CultureInfo.InvariantCulture), a[3] == "wal").ConfigureAwait(false);
                        return "ok";
                    }

                // db.verify|backend|path|rows
                case "db.verify":
                    {
                        using Backend b = await Backend.Create(a[0]).ConfigureAwait(false);
                        Drive d = await DbTests.OpenPrepared(b, a[1], DbTests.Opts()).ConfigureAwait(false);
                        await d.BeginRead().ConfigureAwait(false);
                        await DbContent.Verify(d, int.Parse(a[2], CultureInfo.InvariantCulture)).ConfigureAwait(false);
                        await d.EndRead().ConfigureAwait(false);
                        await DbTests.Close(b, d).ConfigureAwait(false);
                        return "ok";
                    }

                // db.digest|backend|path : walks every B-tree of the database and returns "trees cells digest".
                case "db.digest":
                    {
                        using Backend b = await Backend.Create(a[0]).ConfigureAwait(false);
                        Drive d = await DbTests.OpenPrepared(b, a[1], DbTests.Opts()).ConfigureAwait(false);
                        await d.BeginRead().ConfigureAwait(false);
                        string r = (await TreeDigest.Compute(d).ConfigureAwait(false)).ToString();
                        await d.EndRead().ConfigureAwait(false);
                        await DbTests.Close(b, d).ConfigureAwait(false);
                        return r;
                    }

                default:
                    return "FAIL unknown command " + cmd;
            }
        }
        catch (VfsException e)
        {
            return (e.Code == VfsCodes.Busy ? "BUSY " : "ERR ") + e.ExtendedCode + " " + e.Message;
        }
        catch (OneLiteException e)
        {
            return (e.ExtendedCode == ResultCode.Busy ? "BUSY " : "ERR ") + e.ExtendedCode + " " + e.Message;
        }
        catch (Exception e)
        {
            return "FAIL " + e.GetType().Name + ": " + e.Message;
        }
    }

    /// <summary>
    /// Imports a database file into the chosen backend, then checks it three ways: the exported bytes equal the source, a walk of every B-tree of the
    /// imported file gives the same digest as a walk of the source image in memory. Returns "ok bytes trees cells digest" or "FAIL ...".
    /// </summary>
    [JSExport]
    internal static async Task<string> ImportVerify(string backend, string path, byte[] source)
    {
        try
        {
            using Backend b = await Backend.Create(backend).ConfigureAwait(false);
            await DatabaseTransfer.ImportAsync(b.Vfs, path, source).ConfigureAwait(false);
            await b.Settle().ConfigureAwait(false);

            byte[] back = await DatabaseTransfer.ExportAsync(b.Vfs, path).ConfigureAwait(false);
            await b.Settle().ConfigureAwait(false);
            if (!back.AsSpan().SequenceEqual(source))
            {
                return $"FAIL exported {back.Length} bytes differ from the {source.Length} imported";
            }

            Drive d = await DbTests.OpenPrepared(b, path, DbTests.Opts()).ConfigureAwait(false);
            await d.BeginRead().ConfigureAwait(false);
            TreeDigest viaOpfs = await TreeDigest.Compute(d).ConfigureAwait(false);
            await d.EndRead().ConfigureAwait(false);
            await DbTests.Close(b, d).ConfigureAwait(false);

            TreeDigest viaImage = await TreeDigest.ComputeImage(source).ConfigureAwait(false);
            if (viaOpfs != viaImage)
            {
                return $"FAIL B-tree walk through OPFS gave {viaOpfs}, the source image gave {viaImage}";
            }

            return $"ok {source.Length} {viaOpfs}";
        }
        catch (Exception e)
        {
            return "FAIL " + e.GetType().Name + ": " + e.Message;
        }
    }

    /// <summary>Exports a database into a buffer; <see cref="ExportTake"/> then hands the bytes to JavaScript. Returns the length.</summary>
    [JSExport]
    internal static async Task<string> ExportPrepare(string backend, string path)
    {
        try
        {
            using Backend b = await Backend.Create(backend).ConfigureAwait(false);
            s_export = await DatabaseTransfer.ExportAsync(b.Vfs, path).ConfigureAwait(false);
            await b.Settle().ConfigureAwait(false);
            return s_export.Length.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception e)
        {
            return "FAIL " + e.GetType().Name + ": " + e.Message;
        }
    }

    [JSExport]
    internal static byte[] ExportTake()
    {
        byte[] r = s_export ?? [];
        s_export = null;
        return r;
    }
}

/// <summary>A walk of every B-tree in a database file: how many trees and cells, and an order-sensitive digest of the rowids and payload bytes.</summary>
internal readonly record struct TreeDigest(int Trees, long Cells, ulong Digest)
{
    public override string ToString() => $"{Trees} {Cells} {Digest:x16}";

    public static async Task<TreeDigest> Compute(Drive d)
    {
        var src = new PagerBtSource(d.Pager);
        return await Walk(new BtContext(src, (TextEncoding)(byte)d.Pager.DatabaseEncoding), (op, err) => d.RunBt(op, err), () => src.LastError).ConfigureAwait(false);
    }

    public static async Task<TreeDigest> ComputeImage(byte[] image)
    {
        ImagePageSource src = ImagePageSource.FromBytes(image);
        return await Walk(new BtContext(src, src.Encoding), (op, err) =>
        {
            for (int guard = 0; ; guard++)
            {
                BtStatus s = op();
                if (s == BtStatus.Ok)
                {
                    return Task.CompletedTask;
                }

                if (s == BtStatus.Error || guard > 1000)
                {
                    throw err() ?? new InvalidOperationException("walk failed");
                }

                src.Complete();
            }
        }, () => src.LastError).ConfigureAwait(false);
    }

    private static async Task<TreeDigest> Walk(BtContext ctx, Func<Func<BtStatus>, Func<Exception?>, Task> run, Func<Exception?> error)
    {
        var rv = new RecordView();
        var roots = new List<(uint Root, bool IntKey)> { (1, true) };
        using (BtCursor sc = BTree.Table(ctx, 1).OpenCursor())
        {
            await run(sc.First, () => sc.Error ?? error()).ConfigureAwait(false);
            while (sc.State == CursorState.Valid)
            {
                await run(sc.LoadPayload, () => sc.Error ?? error()).ConfigureAwait(false);
                if (sc.ParseRecord(rv) && rv.ColumnCount >= 5 && rv.GetInt64(3) > 0)
                {
                    string type = System.Text.Encoding.UTF8.GetString(rv.GetBytes(0));
                    string sql = rv.SerialTypeOf(4) == 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(rv.GetBytes(4));
                    bool withoutRowid = sql.Contains("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase);
                    if (type == "table" || type == "index")
                    {
                        roots.Add(((uint)rv.GetInt64(3), type == "table" && !withoutRowid));
                    }
                }

                await run(sc.Next, () => sc.Error ?? error()).ConfigureAwait(false);
            }
        }

        ulong h = 14695981039346656037UL;
        long cells = 0;
        foreach ((uint root, bool intKey) in roots)
        {
            using BtCursor c = (intKey ? BTree.Table(ctx, root) : BTree.Index(ctx, root, KeyInfo.Binary(ctx.Encoding, 1))).OpenCursor();
            await run(c.First, () => c.Error ?? error()).ConfigureAwait(false);
            while (c.State == CursorState.Valid)
            {
                await run(c.LoadPayload, () => c.Error ?? error()).ConfigureAwait(false);
                if (intKey)
                {
                    h = Mix(h, (ulong)c.Rowid);
                }

                foreach (byte x in c.Payload)
                {
                    h = (h ^ x) * 1099511628211UL;
                }

                h = Mix(h, 0xFF);
                cells++;
                await run(c.Next, () => c.Error ?? error()).ConfigureAwait(false);
            }
        }

        return new TreeDigest(roots.Count, cells, h);
    }

    private static ulong Mix(ulong h, ulong v)
    {
        for (int i = 0; i < 8; i++)
        {
            h = (h ^ ((v >> (i * 8)) & 0xFF)) * 1099511628211UL;
        }

        return h;
    }
}
