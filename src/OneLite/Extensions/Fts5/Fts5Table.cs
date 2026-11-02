using System.Linq;
using RecursiveAI.OneLite.Storage;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using RecursiveAI.OneLite.Planning;
using RecursiveAI.OneLite.Storage.BTree;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.VirtualTables;

namespace RecursiveAI.OneLite.Extensions.Fts5;

/// <summary>The <c>fts5</c> module (sqlite.org/fts5.html).</summary>
internal sealed class Fts5Module : BuiltinModule
{
    internal override string Name => "fts5";

    private static string Q(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string QD(string s) => "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    internal override ShadowTable[] ShadowTables(string schema, string table, ImmutableArray<string> args, int pageSize)
    {
        Fts5Config c = Fts5Config.Parse(schema, table, args);
        var list = new List<ShadowTable>
        {
            new()
            {
                Name = table + "_data",
                Sql = "CREATE TABLE " + Q(table + "_data") + "(id INTEGER PRIMARY KEY, block BLOB)",
                Rows = [(1, [null, Array.Empty<byte>()]), (10, [null, new byte[7]])],
            },
            new() { Name = table + "_idx", Sql = "CREATE TABLE " + Q(table + "_idx") + "(segid, term, pgno, PRIMARY KEY(segid, term)) WITHOUT ROWID" },
        };
        if (c.HasContentTable)
        {
            var cols = new StringBuilder();
            for (int i = 0; i < c.ColumnCount; i++)
            {
                if (c.Content == ContentMode.None && !c.Unindexed[i])
                {
                    continue;
                }

                cols.Append(", c").Append(i.ToString(CultureInfo.InvariantCulture));
            }

            if (c.Locale)
            {
                for (int i = 0; i < c.ColumnCount; i++)
                {
                    if (!c.Unindexed[i])
                    {
                        cols.Append(", l").Append(i.ToString(CultureInfo.InvariantCulture));
                    }
                }
            }

            list.Add(new ShadowTable { Name = table + "_content", Sql = "CREATE TABLE " + Q(table + "_content") + "(id INTEGER PRIMARY KEY" + cols + ")" });
        }

        if (c.HasDocSize)
        {
            list.Add(new ShadowTable
            {
                Name = table + "_docsize",
                Sql = "CREATE TABLE " + Q(table + "_docsize") + "(id INTEGER PRIMARY KEY, sz BLOB" + (c.ContentlessDelete ? ", origin INTEGER" : string.Empty) + ")",
            });
        }

        list.Add(new ShadowTable
        {
            Name = table + "_config",
            Sql = "CREATE TABLE " + Q(table + "_config") + "(k PRIMARY KEY, v) WITHOUT ROWID",
            Rows = [(0, ["version", 4L])],
        });
        return [.. list];
    }

    internal override string[] ShadowNames(string table, ImmutableArray<string> args)
    {
        Fts5Config c = Fts5Config.Parse("main", table, args);
        return Fts5Config.ShadowNames(table, c.HasContentTable, c.HasDocSize);
    }

    internal override VTab Connect(Connection conn, TableInfo table, string schema, ImmutableArray<string> args, Action<string> declare)
    {
        Fts5Config c = Fts5Config.Parse(schema, table.Name, args);
        var sb = new StringBuilder("CREATE TABLE x(");
        foreach (string col in c.Columns)
        {
            sb.Append(QD(col)).Append(',');
        }

        sb.Append(QD(table.Name)).Append(" HIDDEN,rank HIDDEN)");
        declare(sb.ToString());
        return new Fts5Table(conn, table, c);
    }
}

/// <summary>Reads rows of an external content table by running ordinary SQL on the connection.</summary>
internal sealed class ExtContent
{
    private readonly Connection _conn;
    private readonly Fts5Config _cfg;
    private Statement? _get;

    internal ExtContent(Connection conn, Fts5Config cfg)
    {
        _conn = conn;
        _cfg = cfg;
    }

    private string ColList() => string.Join(", ", _cfg.Columns.Select(c => "\"" + c.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""));

    private string Tbl()
    {
        string t = "\"" + _cfg.ContentTable!.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        return _cfg.Schema == "main" ? t : "\"" + _cfg.Schema + "\"." + t;
    }

    private string Rid() => "\"" + _cfg.ContentRowid.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private Mem[] ReadRow(Statement s, int first)
    {
        var row = new Mem[_cfg.ColumnCount];
        for (int i = 0; i < row.Length; i++)
        {
            int c = first + i;
            switch (s.ColumnType(c))
            {
                case ColumnType.Integral:
                    row[i] = Mem.FromInt(s.GetInt64(c));
                    break;
                case ColumnType.Real:
                    row[i] = Mem.FromReal(s.GetDouble(c));
                    break;
                case ColumnType.Text:
                    row[i] = Mem.FromText(Encoding.UTF8.GetString(s.GetTextUtf8(c)), _conn.ContextOf(0).Encoding);
                    break;
                case ColumnType.Blob:
                    row[i] = Mem.FromBlob(s.GetBlob(c).ToArray());
                    break;
                default:
                    row[i] = default;
                    break;
            }
        }

        return row;
    }

    internal Mem[]? Get(long rowid)
    {
        _get ??= _conn.Prepare("SELECT " + ColList() + " FROM " + Tbl() + " WHERE " + Rid() + " = ?1");
        _get.Reset();
        _get.Bind(1, rowid);
        return _get.Step() ? ReadRow(_get, 0) : null;
    }

    internal List<(long Rowid, Mem[] Row)> All()
    {
        using Statement s = _conn.Prepare("SELECT " + Rid() + ", " + ColList() + " FROM " + Tbl() + " ORDER BY 1");
        var l = new List<(long, Mem[])>();
        while (s.Step())
        {
            l.Add((s.GetInt64(0), ReadRow(s, 1)));
        }

        return l;
    }
}

/// <summary>Runs restartable computations against a store: a read of unloaded rows aborts the computation, loads them and runs it again.</summary>
internal sealed class Fts5Run
{
    internal Fts5Store Store { get; }

    private Fetch? _fetching;

    internal Fts5Run(Fts5Store store) => Store = store;

    internal ModStatus Drive(Action work)
    {
        for (; ; )
        {
            if (_fetching is { } f)
            {
                if (Store.Satisfy(f) == ModStatus.Pending)
                {
                    return ModStatus.Pending;
                }

                _fetching = null;
            }

            try
            {
                work();
                return ModStatus.Ok;
            }
            catch (NeedFetch nf)
            {
                _fetching = nf.Request;
            }
        }
    }
}

/// <summary>One fts5 table of one connection.</summary>
internal sealed class Fts5Table : VTab
{
    private readonly Connection _conn;
    private readonly Fts5Config _cfg;
    private readonly Fts5Store _store;
    private readonly Fts5Index _idx;
    private readonly Fts5Run _run;
    private readonly ExtContent? _ext;
    private int _ph;
    private bool _inUpdate;
    private List<WriteOp> _ops = [];
    private long _newRowid;

    internal Fts5Table(Connection conn, TableInfo table, Fts5Config cfg)
    {
        _conn = conn;
        _cfg = cfg;
        Table = table;
        _store = new Fts5Store(conn, cfg) { Db = table.Db };
        _idx = new Fts5Index(cfg, _store);
        _run = new Fts5Run(_store);
        if (cfg.Content == ContentMode.External)
        {
            _ext = new ExtContent(conn, cfg);
        }
    }

    internal Fts5Config Config => _cfg;

    internal Connection Conn => _conn;

    internal ExtContent? Ext => _ext;

    internal override bool IsWritable => true;

    public override void Dispose() => _store.Dispose();

    internal override void BestIndex(BestIndexInfo info)
    {
        int n = _cfg.ColumnCount;
        var spec = new List<string>();
        int argv = 0;
        bool match = false;
        bool rank = false;
        bool rowidEq = false;
        ReadOnlySpan<IndexConstraint> cons = info.Constraints;
        for (int i = 0; i < cons.Length; i++)
        {
            IndexConstraint c = cons[i];
            if (!c.Usable)
            {
                continue;
            }

            string? s = null;
            if (!match && c.Column == n && c.Op is ConstraintOp.Match or ConstraintOp.Eq && c.Column == n)
            {
                s = "M";
                match = true;
            }
            else if (!match && c.Column >= 0 && c.Column < n && c.Op == ConstraintOp.Match)
            {
                s = "C" + c.Column.ToString(CultureInfo.InvariantCulture);
                match = true;
            }
            else if (!rank && c.Column == n + 1 && c.Op == ConstraintOp.Match)
            {
                s = "R";
                rank = true;
            }
            else if (c.Column == -1)
            {
                switch (c.Op)
                {
                    case ConstraintOp.Eq:
                        s = "E";
                        rowidEq = true;
                        break;
                    case ConstraintOp.Gt:
                        s = "G";
                        break;
                    case ConstraintOp.Ge:
                        s = "g";
                        break;
                    case ConstraintOp.Lt:
                        s = "L";
                        break;
                    case ConstraintOp.Le:
                        s = "l";
                        break;
                }
            }

            if (s is null)
            {
                continue;
            }

            info.ConstraintUsage[i].ArgvIndex = ++argv;
            info.ConstraintUsage[i].Omit = true;
            spec.Add(s);
        }

        info.IndexString = string.Join(",", spec);
        info.EstimatedCost = match ? (rowidEq ? 10 : 1000) : rowidEq ? 10 : 1_000_000;
        info.EstimatedRows = match ? 100 : rowidEq ? 1 : 100_000;
        info.ScanIsUnique = rowidEq && !match;
    }

    internal override VCursor Open() => new Fts5Cursor(this);

    // ---------------------------------------------------------------- persisted settings

    internal void ApplyPersisted(IReadOnlyDictionary<string, Mem> cfg)
    {
        foreach (KeyValuePair<string, Mem> kv in cfg)
        {
            long iv = kv.Value.Kind == StorageClass.Integral ? kv.Value.IntValue : kv.Value.Kind == StorageClass.Real ? (long)kv.Value.RealValue : 0;
            switch (kv.Key)
            {
                case "pgsz":
                    if (iv >= 32 && iv <= 65536 - 64)
                    {
                        _cfg.Pgsz = (int)iv;
                    }

                    break;
                case "automerge":
                    _cfg.Automerge = (int)Math.Clamp(iv, 0, 16);
                    break;
                case "crisismerge":
                    _cfg.Crisismerge = iv <= 1 ? 16 : (int)Math.Min(iv, 1000);
                    break;
                case "usermerge":
                    _cfg.Usermerge = (int)Math.Clamp(iv, 2, 16);
                    break;
                case "deletemerge":
                    _cfg.Deletemerge = (int)Math.Clamp(iv, 0, 100);
                    break;
                case "rank":
                    _cfg.Rank = Encoding.UTF8.GetString(TextCodec.ToUtf8(kv.Value.Bytes, _store.Enc));
                    break;
                case "secure-delete":
                    _cfg.SecureDelete = iv != 0;
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- updates

    internal override ModStatus Update(ReadOnlySpan<Mem> values, out long rowid)
    {
        rowid = 0;
        if (!_inUpdate)
        {
            _inUpdate = true;
            _store.Clear();
            _ph = 0;
        }

        try
        {
            if (_ph == 0)
            {
                Mem[] copy = values.ToArray();
                ModStatus s = _run.Drive(() => Plan(copy));
                if (s == ModStatus.Pending)
                {
                    return s;
                }

                _ph = 1;
            }

            ModStatus a = _store.Apply(_ops);
            if (a == ModStatus.Pending)
            {
                return a;
            }

            rowid = _newRowid;
            _inUpdate = false;
            return ModStatus.Ok;
        }
        catch
        {
            _inUpdate = false;
            _store.Dispose();
            throw;
        }
    }

    private Mem ToDb(Mem m)
    {
        if (m.Kind == StorageClass.Text && _store.Enc != TextEncoding.Utf8)
        {
            return Mem.FromTextBytes(TextCodec.Encode(Encoding.UTF8.GetString(m.Bytes), _store.Enc));
        }

        if (m.Buffer is not null)
        {
            m.Buffer = m.Bytes.ToArray();
            m.Offset = 0;
        }

        return m;
    }

    private static bool IsNull(in Mem m) => m.Kind == StorageClass.Null;

    private void Plan(Mem[] v)
    {
        _ops = [];
        _store.BeginPlan();
        int n = _cfg.ColumnCount;
        ApplyPersisted(_store.Config());
        Structure st = _idx.ReadStructure();
        long[] avg = _idx.ReadAverages();
        var pending = new Pending();
        TextEncoding enc = _store.Enc;
        bool structureChanged = false;
        _newRowid = 0;

        if (v.Length == 1)
        {
            DeleteRow(v[0].IntValue, null, pending, avg);
            _newRowid = v[0].IntValue;
        }
        else if (v.Length >= 2 + n + 2 && IsNull(v[0]) && !IsNull(v[2 + n]))
        {
            Mem cmd = v[2 + n];
            string name = Encoding.UTF8.GetString(cmd.Bytes);
            structureChanged = Command(name, v, st, avg, pending);
        }
        else
        {
            bool oldNull = IsNull(v[0]);
            if (!oldNull)
            {
                long oldId = v[0].IntValue;
                long newId = IsNull(v[1]) ? oldId : v[1].IntValue;
                DeleteRow(oldId, null, pending, avg);
                InsertRow(newId, v, pending, avg, enc, replacing: oldId == newId);
            }
            else
            {
                long id;
                if (IsNull(v[1]))
                {
                    id = _store.MaxRowid() + 1;
                }
                else
                {
                    id = v[1].IntValue;
                }

                InsertRow(id, v, pending, avg, enc, replacing: false);
            }
        }

        if (!pending.IsEmpty)
        {
            _idx.Flush(st, pending, _ops);
            structureChanged = true;
        }

        if (structureChanged)
        {
            _ops.Add(new WriteOp { Kind = OpKind.PutData, Id = F5.StructureId, Blob = st.Serialize(_cfg.ContentlessDelete) });
        }

        _ops.Add(new WriteOp { Kind = OpKind.PutData, Id = F5.AveragesId, Blob = Fts5Index.AveragesBlob(avg) });
    }

    private void DeleteRow(long rowid, Mem[]? given, Pending pending, long[] avg)
    {
        Mem[]? old = given;
        if (old is null)
        {
            switch (_cfg.Content)
            {
                case ContentMode.Normal:
                    old = _store.Content(rowid);
                    break;
                case ContentMode.External:
                    old = _ext!.Get(rowid);
                    break;
                default:
                    throw Fts5Config.Err("cannot DELETE from contentless fts5 table: " + _cfg.Table);
            }
        }

        if (old is null)
        {
            return;
        }

        DocTokens tok = _idx.Tokenize(old, _store.Enc);
        pending.Add(rowid, tok, true);
        avg[0]--;
        for (int c = 0; c < _cfg.ColumnCount; c++)
        {
            avg[1 + c] -= tok.ColSizes[c];
        }

        if (given is null)
        {
            if (_cfg.Content == ContentMode.Normal)
            {
                _ops.Add(new WriteOp { Kind = OpKind.DelContent, Id = rowid, Id2 = rowid });
            }
        }

        if (_cfg.HasDocSize)
        {
            // the 'delete' command drops the size record as well (observed: it leaves a contentless table without the row)
            _ops.Add(new WriteOp { Kind = OpKind.DelDocSize, Id = rowid, Id2 = rowid });
        }
    }

    private void InsertRow(long rowid, Mem[] v, Pending pending, long[] avg, TextEncoding enc, bool replacing)
    {
        int n = _cfg.ColumnCount;
        var vals = new Mem[n];
        for (int i = 0; i < n; i++)
        {
            vals[i] = ToDb(v[2 + i]);
        }

        if (_cfg.Content == ContentMode.Normal && !replacing && _store.Content(rowid) is not null)
        {
            throw new OneLiteException(ResultCode.Constraint | (6 << 8), "constraint failed");
        }

        DocTokens tok = _idx.Tokenize(vals, enc);
        pending.Add(rowid, tok, false);
        avg[0]++;
        for (int c = 0; c < n; c++)
        {
            avg[1 + c] += tok.ColSizes[c];
        }

        if (_cfg.Content == ContentMode.Normal)
        {
            _ops.Add(new WriteOp { Kind = OpKind.PutContent, Id = rowid, Row = vals });
        }
        else if (_cfg.Content == ContentMode.None && _cfg.ContentlessUnindexed)
        {
            _ops.Add(new WriteOp { Kind = OpKind.PutContent, Id = rowid, Row = [.. vals.Where((_, i) => _cfg.Unindexed[i])] });
        }

        if (_cfg.HasDocSize)
        {
            var b = new ByteBuf();
            foreach (long s in tok.ColSizes)
            {
                b.Varint(s);
            }

            _ops.Add(new WriteOp { Kind = OpKind.PutDocSize, Id = rowid, Blob = b.ToArray() });
        }

        _newRowid = rowid;
    }

    // ---------------------------------------------------------------- special commands

    private bool Command(string name, Mem[] v, Structure st, long[] avg, Pending pending)
    {
        int n = _cfg.ColumnCount;
        Mem arg = v[2 + n + 1];
        long iarg = arg.Kind == StorageClass.Integral ? arg.IntValue : arg.Kind == StorageClass.Real ? (long)arg.RealValue : 0;
        switch (name)
        {
            case "optimize":
                _idx.Optimize(st, _ops);
                return true;
            case "merge":
            {
                int budget = (int)iarg;
                bool did = false;
                int guard = 0;
                while (guard++ < 1000 && _idx.MergeOnce(st, _cfg.Usermerge, _ops))
                {
                    did = true;
                    if (budget > 0)
                    {
                        break;
                    }
                }

                return did;
            }

            case "delete":
            {
                Mem[] vals = new Mem[n];
                for (int i = 0; i < n; i++)
                {
                    vals[i] = ToDb(v[2 + i]);
                }

                long id = IsNull(v[1]) ? 0 : v[1].IntValue;
                if (_cfg.Content == ContentMode.Normal)
                {
                    throw Fts5Config.Err("SQL logic error");
                }

                DeleteRow(id, vals, pending, avg);
                return false;
            }

            case "delete-all":
            {
                if (_cfg.Content == ContentMode.Normal)
                {
                    throw Fts5Config.Err("'delete-all' may only be used with a contentless or external content fts5 table");
                }

                ClearIndex(st, avg);
                return true;
            }

            case "rebuild":
                Rebuild(st, avg, pending);
                return true;
            case "integrity-check":
                IntegrityCheck(st, avg, iarg);
                return false;
            case "pgsz":
            case "automerge":
            case "crisismerge":
            case "usermerge":
            case "deletemerge":
            case "secure-delete":
            case "hashsize":
            case "insttoken":
            {
                if (arg.Kind == StorageClass.Null)
                {
                    return false;
                }

                // observed: a non-integer or out-of-range value fails with the bare "SQL logic error"
                bool ok = arg.Kind == StorageClass.Integral && name switch
                {
                    "pgsz" => iarg is >= 32 and <= 65536,
                    "automerge" or "crisismerge" => iarg >= 0,
                    "usermerge" => iarg is >= 2 and <= 16,
                    "hashsize" => iarg >= 1,
                    _ => true,
                };
                if (!ok)
                {
                    throw Fts5Config.Err("SQL logic error");
                }

                _ops.Add(new WriteOp { Kind = OpKind.PutConfig, Name = name, Row = [arg.Kind == StorageClass.Text ? ToDb(arg) : arg] });
                st.Cookie++;
                return true;
            }

            case "rank":
            {
                if (arg.Kind != StorageClass.Text || !Fts5Aux.RankSpecIsValid(Encoding.UTF8.GetString(TextCodec.ToUtf8(arg.Bytes, _store.Enc))))
                {
                    throw Fts5Config.Err("SQL logic error");
                }

                _ops.Add(new WriteOp { Kind = OpKind.PutConfig, Name = "rank", Row = [ToDb(arg)] });
                st.Cookie++;
                return true;
            }

            default:
                throw Fts5Config.Err("SQL logic error");
        }
    }

    private void ClearIndex(Structure st, long[] avg)
    {
        foreach (Seg g in st.Levels.SelectMany(l => l.Segs).ToArray())
        {
            _ops.Add(new WriteOp { Kind = OpKind.DelDataRange, Id = F5.DataId(g.Id, 0), Id2 = F5.DataEnd(g.Id) - 1 });
            _ops.Add(new WriteOp { Kind = OpKind.DelIdxSeg, Id = g.Id });
            _store.RemoveDataRange(F5.DataId(g.Id, 0), F5.DataEnd(g.Id) - 1);
        }

        st.Levels.Clear();
        st.NWrite = 0;
        Array.Clear(avg);
        if (_cfg.HasDocSize)
        {
            _ops.Add(new WriteOp { Kind = OpKind.DelDocSize, Id = long.MinValue, Id2 = long.MaxValue });
        }

        if (_cfg.HasContentTable && _cfg.Content != ContentMode.Normal)
        {
            _ops.Add(new WriteOp { Kind = OpKind.DelContent, Id = long.MinValue, Id2 = long.MaxValue });
        }
    }

    private List<(long Rowid, Mem[] Row)> AllRows()
    {
        if (_cfg.Content == ContentMode.External)
        {
            return _ext!.All();
        }

        if (_cfg.Content != ContentMode.Normal)
        {
            throw Fts5Config.Err("'rebuild' may not be used with a contentless fts5 table");
        }

        _store.NeedContentRange(long.MinValue, long.MaxValue);
        return [.. _store.ContentRows(long.MinValue, long.MaxValue).Where(kv => kv.Value is not null).Select(kv => (kv.Key, kv.Value!))];
    }

    private void Rebuild(Structure st, long[] avg, Pending pending)
    {
        List<(long Rowid, Mem[] Row)> rows = AllRows();
        ClearIndex(st, avg);
        foreach ((long id, Mem[] row) in rows)
        {
            DocTokens tok = _idx.Tokenize(row, _store.Enc);
            pending.Add(id, tok, false);
            avg[0]++;
            for (int c = 0; c < _cfg.ColumnCount; c++)
            {
                avg[1 + c] += tok.ColSizes[c];
            }

            if (_cfg.HasDocSize)
            {
                var b = new ByteBuf();
                foreach (long s in tok.ColSizes)
                {
                    b.Varint(s);
                }

                _ops.Add(new WriteOp { Kind = OpKind.PutDocSize, Id = id, Blob = b.ToArray() });
            }
        }
    }

    private void IntegrityCheck(Structure st, long[] avg, long checkExternal)
    {
        // the index must hold exactly what the content would put there
        foreach (Seg g in st.Levels.SelectMany(l => l.Segs))
        {
            _store.NeedDataRange(F5.DataId(g.Id, g.First), F5.DataId(g.Id, g.Last));
        }

        SortedDictionary<byte[], SortedDictionary<long, long[]>> actual = _idx.ReadAll(st);
        if (_cfg.Content == ContentMode.None || (_cfg.Content == ContentMode.External && checkExternal == 0))
        {
            return;
        }

        var expect = new SortedDictionary<byte[], SortedDictionary<long, long[]>>(ByteKeyComparer.Instance);
        long[] tot = new long[_cfg.ColumnCount + 1];
        foreach ((long id, Mem[] row) in AllRows())
        {
            DocTokens tok = _idx.Tokenize(row, _store.Enc);
            tot[0]++;
            for (int c = 0; c < _cfg.ColumnCount; c++)
            {
                tot[1 + c] += tok.ColSizes[c];
            }

            foreach (KeyValuePair<byte[], List<long>> kv in tok.Terms)
            {
                if (!expect.TryGetValue(kv.Key, out SortedDictionary<long, long[]>? d))
                {
                    expect[kv.Key] = d = [];
                }

                d[id] = [.. kv.Value];
            }
        }

        bool ok = expect.Count == actual.Count && tot.AsSpan().SequenceEqual(avg);
        if (ok)
        {
            foreach (KeyValuePair<byte[], SortedDictionary<long, long[]>> kv in expect)
            {
                if (!actual.TryGetValue(kv.Key, out SortedDictionary<long, long[]>? a) || a.Count != kv.Value.Count)
                {
                    ok = false;
                    break;
                }

                foreach (KeyValuePair<long, long[]> e in kv.Value)
                {
                    if (!a.TryGetValue(e.Key, out long[]? p) || !p.AsSpan().SequenceEqual(_cfg.Detail == DetailMode.Full ? e.Value : p))
                    {
                        ok = false;
                        break;
                    }
                }

                if (!ok)
                {
                    break;
                }
            }
        }

        if (!ok)
        {
            throw new OneLiteException(ResultCode.CorruptVtab, "database disk image is malformed", "fts5: index does not match the content");
        }
    }
}
