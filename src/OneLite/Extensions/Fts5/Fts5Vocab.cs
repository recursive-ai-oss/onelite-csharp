using System.Collections.Immutable;
using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Planning;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.VirtualTables;

namespace RecursiveAI.OneLite.Extensions.Fts5;

/// <summary>The <c>fts5vocab</c> module: the terms of an fts5 index as a table (fts5 "The fts5vocab Virtual Table Module").</summary>
internal sealed class Fts5VocabModule : BuiltinModule
{
    internal override string Name => "fts5vocab";

    internal override ShadowTable[] ShadowTables(string schema, string table, ImmutableArray<string> args, int pageSize)
    {
        // Checked when the table is created, as the reference does; the fts5 table itself is looked up on first use.
        if (args.Length is < 2 or > 3 || (args.Length == 3 && !string.Equals(schema, "temp", StringComparison.OrdinalIgnoreCase)))
        {
            throw new OneLiteException(ResultCode.Error, "wrong number of vtable arguments");
        }

        string type = Fts5Config.Unquote(args[^1].Trim());
        if (type.ToLowerInvariant() is not ("row" or "col" or "instance"))
        {
            throw new OneLiteException(ResultCode.Error, "fts5vocab: unknown table type: '" + type + "'");
        }

        return [];
    }

    internal override VTab Connect(Connection conn, TableInfo table, string schema, ImmutableArray<string> args, Action<string> declare)
    {
        string[] a = [.. args.Select(x => Fts5Config.Unquote(x.Trim()))];
        string db = schema;
        string tbl;
        string type;
        if (a.Length == 2)
        {
            tbl = a[0];
            type = a[1];
        }
        else if (a.Length == 3 && table.Db == 1)
        {
            db = a[0];
            tbl = a[1];
            type = a[2];
        }
        else
        {
            throw new OneLiteException(ResultCode.Error, "wrong number of vtable arguments");
        }

        int kind = type.ToLowerInvariant() switch
        {
            "row" => 0,
            "col" => 1,
            "instance" => 2,
            _ => throw new OneLiteException(ResultCode.Error, "fts5vocab: unknown table type: '" + type + "'"),
        };
        Catalog cat = conn.Catalog ?? throw new OneLiteException(ResultCode.Error, "no such fts5 table: " + db + "." + tbl);
        TableInfo? target = cat.FindIn(db, tbl);
        if (target?.Virtual is not { } v || !string.Equals(v.Module, "fts5", StringComparison.OrdinalIgnoreCase))
        {
            throw new OneLiteException(ResultCode.Error, "no such fts5 table: " + db + "." + tbl);
        }

        Fts5Config cfg = Fts5Config.Parse(db, target.Name, v.Args);
        declare(kind switch
        {
            0 => "CREATE TABLE x(term, doc, cnt)",
            1 => "CREATE TABLE x(term, col, doc, cnt)",
            _ => "CREATE TABLE x(term, doc, col, offset)",
        });
        return new VocabTable(conn, target, cfg, kind);
    }

    private sealed class VocabTable(Connection conn, TableInfo target, Fts5Config cfg, int kind) : VTab
    {
        internal override void BestIndex(BestIndexInfo info)
        {
            info.EstimatedCost = 1_000_000;
            info.EstimatedRows = 1000;
        }

        internal override VCursor Open() => new VocabCursor(conn, target, cfg, kind);

        public override void Dispose()
        {
        }
    }

    private sealed class VocabCursor : VCursor
    {
        private readonly Fts5Config _cfg;
        private readonly Fts5Store _store;
        private readonly Fts5Index _idx;
        private readonly Fts5Run _run;
        private readonly int _kind;
        private List<object?[]> _rows = [];
        private int _pos;
        private bool _started;

        internal VocabCursor(Connection conn, TableInfo target, Fts5Config cfg, int kind)
        {
            _cfg = cfg;
            _kind = kind;
            _store = new Fts5Store(conn, cfg) { Db = target.Db };
            _idx = new Fts5Index(cfg, _store);
            _run = new Fts5Run(_store);
        }

        internal override bool Eof => _pos >= _rows.Count;

        internal override ModStatus Filter(int indexNumber, string? indexString, ReadOnlySpan<Mem> args)
        {
            if (_started)
            {
                return ModStatus.Ok;
            }

            ModStatus s = _run.Drive(Compute);
            if (s == ModStatus.Ok)
            {
                _started = true;
                _pos = 0;
            }

            return s;
        }

        private void Compute()
        {
            Structure st = _idx.ReadStructure();
            SortedDictionary<byte[], SortedDictionary<long, long[]>> all = _idx.ReadAll(st);
            var rows = new List<object?[]>();
            foreach (KeyValuePair<byte[], SortedDictionary<long, long[]>> kv in all)
            {
                if (kv.Key.Length == 0 || kv.Key[0] != (byte)'0')
                {
                    continue;
                }

                string term = Encoding.UTF8.GetString(kv.Key, 1, kv.Key.Length - 1);
                bool none = _cfg.Detail == DetailMode.None;
                bool column = _cfg.Detail == DetailMode.Column;
                if (_kind == 0)
                {
                    // without positions the occurrence count is unknown (NULL)
                    rows.Add([term, (long)kv.Value.Count, none || column ? null : kv.Value.Values.Sum(p => (long)p.Length)]);
                }
                else if (_kind == 1)
                {
                    if (none)
                    {
                        rows.Add([term, null, (long)kv.Value.Count, null]);
                        continue;
                    }

                    var perCol = new SortedDictionary<int, (long Docs, long Cnt)>();
                    foreach (long[] pos in kv.Value.Values)
                    {
                        foreach (IGrouping<int, long> g in pos.GroupBy(p => (int)(p >> 32)))
                        {
                            perCol.TryGetValue(g.Key, out (long Docs, long Cnt) cur);
                            perCol[g.Key] = (cur.Docs + 1, cur.Cnt + g.Count());
                        }
                    }

                    foreach (KeyValuePair<int, (long Docs, long Cnt)> c in perCol)
                    {
                        rows.Add([term, _cfg.Columns[c.Key], c.Value.Docs, column ? null : c.Value.Cnt]);
                    }
                }
                else
                {
                    foreach (KeyValuePair<long, long[]> d in kv.Value)
                    {
                        if (none)
                        {
                            rows.Add([term, d.Key, null, null]);
                            continue;
                        }

                        foreach (long p in d.Value)
                        {
                            rows.Add([term, d.Key, _cfg.Columns[(int)(p >> 32)], column ? null : (long)(p & 0xFFFFFFFFL)]);
                        }
                    }
                }
            }

            _rows = rows;
        }

        internal override ModStatus Next()
        {
            _pos++;
            return ModStatus.Ok;
        }

        internal override ModStatus RowId(out long rowid)
        {
            rowid = _pos + 1;
            return ModStatus.Ok;
        }

        internal override ModStatus Column(int index, ref Mem dst, ref byte[]? own)
        {
            object? v = _rows[_pos][index];
            switch (v)
            {
                case long l:
                    Vm.Values.SetInt(ref dst, l);
                    break;
                case string s:
                    Vm.Values.SetBytes(ref dst, ref own, StorageClass.Text, Encoding.UTF8.GetBytes(s));
                    break;
                default:
                    Vm.Values.SetNull(ref dst);
                    break;
            }

            return ModStatus.Ok;
        }

        public override void Dispose() => _store.Dispose();
    }
}
