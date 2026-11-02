using System;
using System.Collections.Generic;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Durability;

/// <summary>
/// <see cref="Workload"/> made with raw B-tree calls on the storage engine (there is no SQL layer yet): four trees (<c>txlog</c>, <c>data</c>, the index
/// <c>data_txn</c>, <c>counter</c>) created with their <c>sqlite_schema</c> rows in one transaction, then transaction after transaction of inserts, an
/// index entry per row, and deletes of the rows from three transactions back.
/// </summary>
public sealed class BTreeWorkloadDb
{
    private static readonly string[] Sql =
    [
        "CREATE TABLE txlog(txn INTEGER PRIMARY KEY, nrows INTEGER NOT NULL, sumv INTEGER NOT NULL)",
        "CREATE TABLE data(id INTEGER PRIMARY KEY, txn INTEGER NOT NULL, seq INTEGER NOT NULL, v INTEGER NOT NULL, payload BLOB NOT NULL)",
        "CREATE INDEX data_txn ON data(txn)",
        "CREATE TABLE counter(id INTEGER PRIMARY KEY CHECK (id = 1), n INTEGER NOT NULL)",
    ];

    private readonly StorageEngine _eng;
    private BtContext _ctx = null!;
    private BTree _txlog = null!, _data = null!, _idx = null!, _counter = null!;

    public BTreeWorkloadDb(StorageEngine eng) => _eng = eng;

    private void Ok(BtCursor c, Func<BtStatus> op)
    {
        if (_eng.Run(op) != BtStatus.Ok)
        {
            throw c.Error ?? new InvalidOperationException("B-tree call failed");
        }
    }

    private void Bind(uint txlog, uint data, uint idx, uint counter)
    {
        _ctx = _eng.Context;
        TextEncoding enc = _ctx.Encoding;
        _txlog = BTree.Table(_ctx, txlog);
        _data = BTree.Table(_ctx, data);
        _idx = BTree.Index(_ctx, idx, KeyInfo.Binary(enc, 2));
        _counter = BTree.Table(_ctx, counter);
    }

    /// <summary>Creates the four trees and the schema rows in one transaction, unless a committed schema is already there.</summary>
    public void Setup()
    {
        // A file with no pages yet (nothing ever committed) has no sqlite_schema page to read.
        List<SchemaEntry> schema = _eng.Pager.PageCount == 0 ? [] : SchemaLite.Read(_eng.Context, _eng.Pager.ExecuteSync);
        if (schema.Count == 4)
        {
            Bind(schema[0].RootPage, schema[1].RootPage, schema[2].RootPage, schema[3].RootPage);
            return;
        }

        if (schema.Count != 0)
        {
            throw new InvalidOperationException("the setup transaction was not atomic: " + schema.Count + " schema rows");
        }

        _eng.BeginWrite();
        BtContext ctx = _eng.Context;
        var roots = new uint[4];
        for (int i = 0; i < 4; i++)
        {
            uint root = 0;
            if (_eng.Run(() => BtAdmin.CreateTree(ctx, i != 2, out root)) != BtStatus.Ok)
            {
                throw _eng.Source.LastError ?? new InvalidOperationException("CreateTree failed");
            }

            roots[i] = root;
        }

        Bind(roots[0], roots[1], roots[2], roots[3]);
        TextEncoding enc = ctx.Encoding;
        string[] names = ["txlog", "data", "data_txn", "counter"];
        string[] tables = ["txlog", "data", "data", "counter"];
        string[] types = ["table", "table", "index", "table"];
        using BtCursor sc = BTree.Table(ctx, 1).OpenCursor();
        for (int i = 0; i < 4; i++)
        {
            byte[] rec = RecordWriter.ToArray([
                Mem.FromText(types[i], enc), Mem.FromText(names[i], enc), Mem.FromText(tables[i], enc), Mem.FromInt(roots[i]), Mem.FromText(Sql[i], enc)]);
            long rowid = i + 1;
            Ok(sc, () => sc.Insert(rowid, rec));
        }

        using BtCursor cc = _counter.OpenCursor();
        byte[] first = RecordWriter.ToArray([Mem.Null, Mem.FromInt(0)]);
        Ok(cc, () => cc.Insert(1, first));
        _eng.SetSchemaCookie(1);
        sc.Reset();
        cc.Reset();
        _eng.Commit();
    }

    public long ReadCounter()
    {
        using BtCursor c = _counter.OpenCursor();
        int cmp = 0;
        Ok(c, () => c.MoveToRowid(1, out cmp));
        Ok(c, c.LoadPayload);
        var rv = new RecordView();
        if (cmp != 0 || !c.ParseRecord(rv))
        {
            throw new InvalidOperationException("counter row missing");
        }

        return rv.GetInt64(1);
    }

    public void RunTransaction(long txn)
    {
        _eng.BeginWrite();
        using BtCursor data = _data.OpenCursor(), idx = _idx.OpenCursor(), txlog = _txlog.OpenCursor(), counter = _counter.OpenCursor(), scan = _idx.OpenCursor();
        Ok(data, data.Last);
        long rowid = data.State == CursorState.Valid ? data.Rowid : 0;
        int rows = Workload.RowCount(txn);
        long sum = 0;
        for (int j = 0; j < rows; j++)
        {
            long v = Workload.Value(txn, j);
            sum += v;
            rowid++;
            byte[] rec = RecordWriter.ToArray([Mem.Null, Mem.FromInt(txn), Mem.FromInt(j), Mem.FromInt(v), Mem.FromBlob(Workload.Payload(txn, j))]);
            long id = rowid;
            Ok(data, () => data.Insert(id, rec));
            Mem[] key = [Mem.FromInt(txn), Mem.FromInt(id)];
            byte[] krec = RecordWriter.ToArray(key);
            Ok(idx, () => idx.Insert(krec, key));
        }

        byte[] log = RecordWriter.ToArray([Mem.Null, Mem.FromInt(rows), Mem.FromInt(sum)]);
        Ok(txlog, () => txlog.Insert(txn, log));
        byte[] cnt = RecordWriter.ToArray([Mem.Null, Mem.FromInt(txn)]);
        Ok(counter, () => counter.Insert(1, cnt));

        // delete the rows of transaction txn - DeleteLag: find them through the index, then remove the entry and the row
        long old = txn - Workload.DeleteLag;
        var victims = new List<long>();
        Mem[] prefix = [Mem.FromInt(old)];
        int cmp = 0;
        Ok(scan, () => scan.MoveToKey(prefix, 1, out cmp));
        var rv = new RecordView();
        while (scan.State == CursorState.Valid)
        {
            Ok(scan, scan.LoadPayload);
            if (!scan.ParseRecord(rv) || rv.GetInt64(0) != old)
            {
                break;
            }

            victims.Add(rv.GetInt64(1));
            Ok(scan, scan.Next);
        }

        scan.Reset();
        foreach (long id in victims)
        {
            Mem[] key = [Mem.FromInt(old), Mem.FromInt(id)];
            int c1 = 1;
            Ok(idx, () => idx.MoveToKey(key, 0, out c1));
            if (c1 != 0)
            {
                throw new InvalidOperationException("index entry of a victim row is missing");
            }

            Ok(idx, idx.Delete);
            int c2 = 1;
            Ok(data, () => data.MoveToRowid(id, out c2));
            if (c2 != 0)
            {
                throw new InvalidOperationException("victim row is missing");
            }

            Ok(data, data.Delete);
        }

        data.Reset();
        idx.Reset();
        txlog.Reset();
        counter.Reset();
        _eng.Commit();
    }
}
