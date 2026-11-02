using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Planning;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.VirtualTables;

namespace RecursiveAI.OneLite.Extensions.Fts3;

/// <summary>The <c>fts3</c> and <c>fts4</c> modules (sqlite.org/fts3.html).</summary>
internal sealed class Fts3Module(bool fts4) : BuiltinModule
{
    internal override string Name => fts4 ? "fts4" : "fts3";

    internal override ShadowTable[] ShadowTables(string schema, string table, ImmutableArray<string> args, int pageSize)
    {
        Fts3Config c = Fts3Config.Parse(schema, table, fts4, args);
        return [.. c.ShadowSql().Select(s => new ShadowTable { Name = s.Name, Sql = s.Sql })];
    }

    internal override string[] ShadowNames(string table, ImmutableArray<string> args) => Fts3Config.ShadowNames(Fts3Config.Parse("main", table, fts4, args));

    private static string QD(string s) => "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    internal override VTab Connect(Connection conn, TableInfo table, string schema, ImmutableArray<string> args, Action<string> declare)
    {
        Fts3Config c = Fts3Config.Parse(schema, table.Name, fts4, args);
        var sb = new StringBuilder("CREATE TABLE x(");
        foreach (string col in c.Columns)
        {
            sb.Append(QD(col)).Append(',');
        }

        sb.Append(QD(table.Name)).Append(" HIDDEN,docid HIDDEN,").Append(QD(c.LangidColumn ?? "__langid")).Append(" HIDDEN)");
        declare(sb.ToString());
        return new Fts3Table(conn, table, c);
    }
}

/// <summary>Reads and writes the document text of an fts3/fts4 table: its %_content table or the external table it names.</summary>
internal sealed class Fts3Rows
{
    private readonly Fts3Db _db;
    private readonly Fts3Config _cfg;

    internal Fts3Rows(Fts3Db db, Fts3Config cfg)
    {
        _db = db;
        _cfg = cfg;
    }

    private string Tbl()
    {
        if (_cfg.Content == Fts3Content.External)
        {
            string t = "\"" + _cfg.ContentTable!.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            return _cfg.Schema == "main" ? t : "\"" + _cfg.Schema + "\"." + t;
        }

        return _db.T("_content");
    }

    private string ColList()
    {
        var l = new List<string>();
        for (int i = 0; i < _cfg.ColumnCount; i++)
        {
            string n = _cfg.Content == Fts3Content.External ? _cfg.Columns[i] : "c" + i.ToString(CultureInfo.InvariantCulture) + _cfg.Columns[i];
            l.Add("\"" + n.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
        }

        if (_cfg.LangidColumn is not null)
        {
            l.Add(_cfg.Content == Fts3Content.External ? "\"" + _cfg.LangidColumn.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : "langid");
        }

        return string.Join(", ", l);
    }

    private string Key => _cfg.Content == Fts3Content.External ? "rowid" : "docid";

    internal static Mem ReadMem(Statement s, int c)
    {
        switch (s.ColumnType(c))
        {
            case ColumnType.Integral:
                return Mem.FromInt(s.GetInt64(c));
            case ColumnType.Real:
                return Mem.FromReal(s.GetDouble(c));
            case ColumnType.Text:
                return Mem.FromTextBytes(s.GetTextUtf8(c).ToArray());
            case ColumnType.Blob:
                return Mem.FromBlob(s.GetBlob(c).ToArray());
            default:
                return default;
        }
    }

    internal static object? ToObj(in Mem m)
    {
        switch (m.Kind)
        {
            case StorageClass.Integral:
                return m.IntValue;
            case StorageClass.Real:
                return m.RealValue;
            case StorageClass.Text:
                return Encoding.UTF8.GetString(m.Bytes);
            case StorageClass.Blob:
                return m.Bytes.ToArray();
            default:
                return null;
        }
    }

    // A function that does not exist fails with the bare "SQL logic error", as in the reference.
    private Statement PrepareCodec(string function)
    {
        try
        {
            return _db.S("SELECT \"" + function.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"(?1)");
        }
        catch (OneLiteException e) when ((e.ExtendedCode & 0xff) == ResultCode.Error)
        {
            throw new OneLiteException(ResultCode.Error, "SQL logic error");
        }
    }

    private Mem Uncompress(Mem m)
    {
        if (_cfg.Uncompress is null || m.Kind == StorageClass.Null)
        {
            return m;
        }

        Statement s = PrepareCodec(_cfg.Uncompress);
        Fts3Db.Bind(s, [ToObj(m)]);
        Mem r = s.Step() ? ReadMem(s, 0) : default;
        s.Reset();
        return r;
    }

    private object? Compress(in Mem m)
    {
        if (_cfg.Compress is null || m.Kind == StorageClass.Null)
        {
            return ToObj(m);
        }

        Statement s = PrepareCodec(_cfg.Compress);
        Fts3Db.Bind(s, [ToObj(m)]);
        object? r = null;
        if (s.Step())
        {
            r = ReadMem(s, 0) is { Kind: not StorageClass.Null } v ? ToObj(v) : null;
        }

        s.Reset();
        return r;
    }

    /// <summary>The row with this docid (column values, language id), or null.</summary>
    internal (Mem[] Values, long Lang)? Get(long docid)
    {
        if (_cfg.Content == Fts3Content.None)
        {
            return null;
        }

        Statement s = _db.S("SELECT " + ColList() + " FROM " + Tbl() + " WHERE " + Key + " = ?1");
        s.Bind(1, docid);
        (Mem[], long)? r = null;
        if (s.Step())
        {
            r = Read(s, 0);
        }

        s.Reset();
        return r;
    }

    internal (Mem[] Values, long Lang) Read(Statement s, int first)
    {
        var vals = new Mem[_cfg.ColumnCount];
        for (int i = 0; i < vals.Length; i++)
        {
            vals[i] = Uncompress(ReadMem(s, first + i));
        }

        long lang = 0;
        if (_cfg.LangidColumn is not null)
        {
            Mem l = ReadMem(s, first + vals.Length);
            lang = l.Kind == StorageClass.Integral ? l.IntValue : 0;
        }

        return (vals, lang);
    }

    internal List<(long Docid, Mem[] Values, long Lang)> All()
    {
        var l = new List<(long, Mem[], long)>();
        Statement s = _db.S("SELECT " + Key + ", " + ColList() + " FROM " + Tbl() + " ORDER BY 1");
        while (s.Step())
        {
            (Mem[] v, long lang) = Read(s, 1);
            l.Add((s.GetInt64(0), v, lang));
        }

        s.Reset();
        return l;
    }

    internal long MaxDocid()
    {
        Statement s = _db.S("SELECT max(docid) FROM " + _db.T("_content"));
        long v = 0;
        if (s.Step() && !s.IsNull(0))
        {
            v = s.GetInt64(0);
        }

        s.Reset();
        return v;
    }

    internal void Insert(long docid, Mem[] vals, long lang)
    {
        var args = new List<object?> { docid };
        var cols = new StringBuilder("docid");
        var ph = new StringBuilder("?1");
        for (int i = 0; i < vals.Length; i++)
        {
            cols.Append(", \"c").Append(i.ToString(CultureInfo.InvariantCulture)).Append(_cfg.Columns[i].Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            ph.Append(", ?").Append((i + 2).ToString(CultureInfo.InvariantCulture));
            args.Add(Compress(vals[i]));
        }

        if (_cfg.LangidColumn is not null)
        {
            cols.Append(", langid");
            ph.Append(", ?").Append((vals.Length + 2).ToString(CultureInfo.InvariantCulture));
            args.Add(lang);
        }

        try
        {
            _db.Exec("INSERT INTO " + _db.T("_content") + "(" + cols + ") VALUES(" + ph + ")", [.. args]);
        }
        catch (OneLiteException e) when ((e.ExtendedCode & 0xff) == ResultCode.Constraint)
        {
            throw new OneLiteException(ResultCode.Constraint | (6 << 8), "constraint failed");
        }
    }

    internal void Delete(long docid) => _db.Exec("DELETE FROM " + _db.T("_content") + " WHERE docid = ?1", docid);

    internal void Update(long oldId, long newId, Mem[] vals, long lang)
    {
        var sets = new StringBuilder("docid = ?2");
        var args = new List<object?> { oldId, newId };

        for (int i = 0; i < vals.Length; i++)
        {
            if (sets.Length > 0)
            {
                sets.Append(", ");
            }

            args.Add(Compress(vals[i]));
            sets.Append('"').Append('c').Append(i.ToString(CultureInfo.InvariantCulture)).Append(_cfg.Columns[i].Replace("\"", "\"\"", StringComparison.Ordinal)).Append("\" = ?").Append(args.Count.ToString(CultureInfo.InvariantCulture));
        }

        if (_cfg.LangidColumn is not null)
        {
            args.Add(lang);
            sets.Append(", langid = ?").Append(args.Count.ToString(CultureInfo.InvariantCulture));
        }

        try
        {
            _db.Exec("UPDATE " + _db.T("_content") + " SET " + sets + " WHERE docid = ?1", [.. args]);
        }
        catch (OneLiteException e) when ((e.ExtendedCode & 0xff) == ResultCode.Constraint)
        {
            throw new OneLiteException(ResultCode.Constraint | (6 << 8), "constraint failed");
        }
    }
}

/// <summary>One fts3 or fts4 table of one connection.</summary>
internal sealed class Fts3Table : VTab
{
    private readonly Connection _conn;
    private readonly Fts3Config _cfg;
    private readonly Fts3Db _db;
    private readonly Fts3Index _idx;
    private readonly Fts3Rows _content;

    internal Fts3Table(Connection conn, TableInfo table, Fts3Config cfg)
    {
        _conn = conn;
        _cfg = cfg;
        Table = table;
        _db = new Fts3Db(conn, cfg);
        _idx = new Fts3Index(cfg, _db);
        _content = new Fts3Rows(_db, cfg);
    }

    internal Fts3Config Config => _cfg;

    internal Connection Conn => _conn;

    internal override bool IsWritable => true;

    public override void Dispose() => _db.Dispose();

    internal override VCursor Open() => new Fts3Cursor(this);

    internal override void BestIndex(BestIndexInfo info)
    {
        int n = _cfg.ColumnCount;
        var spec = new List<string>();
        int argv = 0;
        bool match = false;
        bool eq = false;
        ReadOnlySpan<IndexConstraint> cons = info.Constraints;
        for (int i = 0; i < cons.Length; i++)
        {
            IndexConstraint c = cons[i];
            if (!c.Usable)
            {
                continue;
            }

            string? s = null;
            if (!match && c.Op == ConstraintOp.Match && c.Column == n)
            {
                s = "M";
                match = true;
            }
            else if (!match && c.Op == ConstraintOp.Match && c.Column >= 0 && c.Column < n)
            {
                s = "C" + c.Column.ToString(CultureInfo.InvariantCulture);
                match = true;
            }
            else if (c.Column == -1 || c.Column == n + 1)
            {
                switch (c.Op)
                {
                    case ConstraintOp.Eq:
                        s = "E";
                        eq = true;
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
            else if (c.Column == n + 2 && c.Op == ConstraintOp.Eq)
            {
                s = "N";
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
        info.EstimatedCost = eq ? 10 : match ? 1000 : 1_000_000;
        info.EstimatedRows = eq ? 1 : match ? 100 : 100_000;
        info.ScanIsUnique = eq;
    }

    // ---------------------------------------------------------------- updates

    private static bool IsNull(in Mem m) => m.Kind == StorageClass.Null;

    private static long DocidOf(in Mem m)
    {
        switch (m.Kind)
        {
            case StorageClass.Integral:
                return m.IntValue;
            case StorageClass.Real:
                if (m.RealValue == Math.Floor(m.RealValue) && Math.Abs(m.RealValue) < 9.2e18)
                {
                    return (long)m.RealValue;
                }

                break;
            case StorageClass.Text:
            {
                StorageClass k = Vm.Values.ParseNumber(m.Bytes, out long iv, out double dv, out bool whole);
                if (whole && k == StorageClass.Integral)
                {
                    return iv;
                }

                if (whole && k == StorageClass.Real && dv == Math.Floor(dv) && Math.Abs(dv) < 9.2e18)
                {
                    return (long)dv;
                }

                break;
            }
        }

        throw new OneLiteException(ResultCode.Mismatch, "datatype mismatch");
    }

    internal override ModStatus Update(ReadOnlySpan<Mem> values, out long rowid)
    {
        rowid = 0;
        int n = _cfg.ColumnCount;
        Mem[] v = values.ToArray();
        for (int i = 0; i < v.Length; i++)
        {
            if (v[i].Buffer is not null)
            {
                v[i].Buffer = v[i].Bytes.ToArray();
                v[i].Offset = 0;
            }
        }

        var pending = new Fts3Index.Pending();
        int automerge = AutoMerge();
        if (v.Length == 1)
        {
            DeleteDoc(DocidOf(v[0]), pending);
            _idx.Flush(pending, automerge);
            rowid = DocidOf(v[0]);
            return ModStatus.Ok;
        }

        Mem table = v[2 + n];
        Mem docidHidden = v[2 + n + 1];
        long lang = IsNull(v[2 + n + 2]) ? 0 : DocidOf(v[2 + n + 2]);
        Mem[] vals = v[2..(2 + n)];

        if (IsNull(v[0]))
        {
            if (!IsNull(table))
            {
                Command(table, pending);
                return ModStatus.Ok;
            }

            long docid;
            if (!IsNull(docidHidden))
            {
                if (!IsNull(v[1]))
                {
                    throw new OneLiteException(ResultCode.Error, "SQL logic error");
                }

                docid = DocidOf(docidHidden);
            }
            else if (!IsNull(v[1]))
            {
                docid = DocidOf(v[1]);
            }
            else if (_cfg.Content == Fts3Content.Normal)
            {
                docid = _content.MaxDocid() + 1;
            }
            else
            {
                throw new OneLiteException(ResultCode.Constraint | (5 << 8), "constraint failed");
            }

            InsertDoc(docid, vals, lang, pending);
            rowid = docid;
        }
        else
        {
            long oldId = DocidOf(v[0]);
            long newId = IsNull(docidHidden) ? DocidOf(v[1]) : DocidOf(docidHidden);
            if (_cfg.Content == Fts3Content.None)
            {
                throw new OneLiteException(ResultCode.Error, "SQL logic error");
            }

            DeleteDoc(oldId, pending, keepContentRow: true);
            if (_cfg.Content == Fts3Content.Normal)
            {
                _content.Update(oldId, newId, vals, lang);
            }

            AddDoc(newId, vals, lang, pending);
            rowid = newId;
        }

        _idx.Flush(pending, automerge);
        return ModStatus.Ok;
    }

    private int AutoMerge()
    {
        if (!_cfg.HasStat)
        {
            return 0;
        }

        Statement s = _db.S("SELECT value FROM " + _db.T("_stat") + " WHERE id = 2");
        int v = 0;
        if (s.Step() && s.ColumnType(0) == ColumnType.Integral)
        {
            v = (int)s.GetInt64(0);
        }

        s.Reset();
        return v;
    }

    private void InsertDoc(long docid, Mem[] vals, long lang, Fts3Index.Pending pending)
    {
        if (_cfg.Content == Fts3Content.Normal)
        {
            _content.Insert(docid, vals, lang);
        }

        AddDoc(docid, vals, lang, pending);
    }

    private void AddDoc(long docid, Mem[] vals, long lang, Fts3Index.Pending pending)
    {
        DocTokens t = _idx.Tokenize(vals);
        _idx.Add(pending, lang, docid, t, false);
        if (_cfg.HasDocSize)
        {
            var b = new List<byte>();
            foreach (long c in t.ColTokens)
            {
                F3.PutVarint(b, c);
            }

            _db.Exec("INSERT OR REPLACE INTO " + _db.T("_docsize") + "(docid, size) VALUES(?1, ?2)", docid, b.ToArray());
        }

        AdjustStat(t, 1);
    }

    private void DeleteDoc(long docid, Fts3Index.Pending pending, bool keepContentRow = false)
    {
        if (_cfg.Content == Fts3Content.None)
        {
            throw new OneLiteException(ResultCode.Error, "SQL logic error");
        }

        (Mem[] Values, long Lang)? old = _content.Get(docid);
        if (old is { } o)
        {
            DocTokens t = _idx.Tokenize(o.Values);
            _idx.Add(pending, o.Lang, docid, t, true);
            AdjustStat(t, -1);
        }

        if (_cfg.HasDocSize)
        {
            _db.Exec("DELETE FROM " + _db.T("_docsize") + " WHERE docid = ?1", docid);
        }

        if (!keepContentRow && _cfg.Content == Fts3Content.Normal)
        {
            _content.Delete(docid);
        }
    }

    private long[] ReadStat()
    {
        var r = new long[_cfg.ColumnCount + 2];
        Statement s = _db.S("SELECT value FROM " + _db.T("_stat") + " WHERE id = 0");
        if (s.Step() && s.ColumnType(0) is ColumnType.Blob or ColumnType.Text)
        {
            byte[] b = s.GetBlob(0).ToArray();
            int pos = 0;
            for (int i = 0; i < r.Length && pos < b.Length; i++)
            {
                r[i] = F3.GetVarint(b, ref pos);
            }
        }

        s.Reset();
        return r;
    }

    private void WriteStat(long[] st)
    {
        var b = new List<byte>();
        foreach (long x in st)
        {
            F3.PutVarint(b, x);
        }

        _db.Exec("INSERT OR REPLACE INTO " + _db.T("_stat") + "(id, value) VALUES(0, ?1)", b.ToArray());
    }

    private void AdjustStat(DocTokens t, int sign)
    {
        if (!_cfg.HasStat)
        {
            return;
        }

        long[] st = ReadStat();
        st[0] += sign;
        for (int i = 0; i < _cfg.ColumnCount; i++)
        {
            st[1 + i] += sign * t.ColTokens[i];
        }

        st[_cfg.ColumnCount + 1] += sign * t.Bytes;
        WriteStat(st);
    }

    // ---------------------------------------------------------------- special commands

    private SortedSet<long> FamilyBases()
    {
        var set = new SortedSet<long>();
        foreach (SegDirRow r in _db.ReadDir(long.MinValue, long.MaxValue))
        {
            set.Add(r.Level / F3.LevelsPerIndex * F3.LevelsPerIndex);
        }

        return set;
    }

    private void Command(Mem cmd, Fts3Index.Pending pending)
    {
        string name = Encoding.UTF8.GetString(Fts3Index.TextOf(cmd) ?? []);
        if (name == "optimize")
        {
            foreach (long b in FamilyBases())
            {
                _db.Optimize(b);
            }

            return;
        }

        if (name == "rebuild")
        {
            Rebuild(pending);
            return;
        }

        if (name == "integrity-check")
        {
            IntegrityCheck();
            return;
        }

        if (name.StartsWith("merge=", StringComparison.Ordinal))
        {
            string[] parts = name[6..].Split(',');
            if (parts.Length is < 1 or > 2 || parts.Any(p => !int.TryParse(p, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)))
            {
                throw new OneLiteException(ResultCode.Error, "SQL logic error");
            }

            int y = parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 8;
            foreach (long b in FamilyBases())
            {
                _db.Cascade(b, Math.Max(2, y));
            }

            return;
        }

        if (name.StartsWith("automerge=", StringComparison.Ordinal))
        {
            if (!_cfg.HasStat || !int.TryParse(name[10..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int nm))
            {
                throw new OneLiteException(ResultCode.Error, "SQL logic error");
            }

            if (nm == 1)
            {
                nm = 8;
            }

            if (nm < 0 || nm > 15)
            {
                nm = 0;
            }

            _db.Exec("INSERT OR REPLACE INTO " + _db.T("_stat") + "(id, value) VALUES(2, ?1)", (long)nm);
            return;
        }

        throw new OneLiteException(ResultCode.Error, "SQL logic error");
    }

    private void ClearIndex()
    {
        _db.Exec("DELETE FROM " + _db.T("_segments"));
        _db.Exec("DELETE FROM " + _db.T("_segdir"));
        if (_cfg.HasDocSize)
        {
            _db.Exec("DELETE FROM " + _db.T("_docsize"));
        }

        if (_cfg.HasStat)
        {
            _db.Exec("DELETE FROM " + _db.T("_stat") + " WHERE id = 0");
        }
    }

    private void Rebuild(Fts3Index.Pending pending)
    {
        if (_cfg.Content == Fts3Content.None)
        {
            throw new OneLiteException(ResultCode.Error, "SQL logic error");
        }

        List<(long Docid, Mem[] Values, long Lang)> rows = _content.All();
        ClearIndex();
        foreach ((long id, Mem[] vals, long lang) in rows)
        {
            AddDoc(id, vals, lang, pending);
        }

        _idx.Flush(pending, 0);
    }

    private void IntegrityCheck()
    {
        if (_cfg.Content == Fts3Content.None)
        {
            throw new OneLiteException(ResultCode.Error, "SQL logic error");
        }

        List<(long Docid, Mem[] Values, long Lang)> rows = _content.All();
        // expected: (lang, index) -> term -> docid -> positions
        var expect = new Dictionary<(long, int), SortedDictionary<byte[], SortedDictionary<long, List<(int, int)>>>>();
        foreach ((long id, Mem[] vals, long lang) in rows)
        {
            DocTokens t = _idx.Tokenize(vals);
            var p = new Fts3Index.Pending();
            _idx.Add(p, lang, id, t, false);
            foreach (KeyValuePair<(long Lang, int Index), SortedDictionary<byte[], SortedDictionary<long, byte[]>>> fam in p.Families)
            {
                if (!expect.TryGetValue(fam.Key, out SortedDictionary<byte[], SortedDictionary<long, List<(int, int)>>>? terms))
                {
                    expect[fam.Key] = terms = new(ByteKeyComparer.Instance);
                }

                foreach (KeyValuePair<byte[], SortedDictionary<long, byte[]>> tm in fam.Value)
                {
                    var l = new List<(int Col, int Pos)>();
                    F3.DecodePoslist(tm.Value[id], 0, l);
                    if (!terms.TryGetValue(tm.Key, out SortedDictionary<long, List<(int, int)>>? d))
                    {
                        terms[tm.Key] = d = [];
                    }

                    d[id] = l;
                }
            }
        }

        var langs = new SortedSet<long>(_idx.Languages());
        foreach ((long lg, int _) in expect.Keys)
        {
            langs.Add(lg);
        }

        for (int i = 0; i < _cfg.IndexCount; i++)
        {
            foreach (long lang in langs)
            {
                SortedDictionary<byte[], SortedDictionary<long, List<(int Col, int Pos)>>> actual = _idx.ReadAll(lang, i);
                expect.TryGetValue((lang, i), out SortedDictionary<byte[], SortedDictionary<long, List<(int, int)>>>? want);
                want ??= new(ByteKeyComparer.Instance);
                bool ok = want.Count == actual.Count;
                if (ok)
                {
                    foreach (KeyValuePair<byte[], SortedDictionary<long, List<(int, int)>>> kv in want)
                    {
                        if (!actual.TryGetValue(kv.Key, out SortedDictionary<long, List<(int Col, int Pos)>>? a) || a.Count != kv.Value.Count
                            || kv.Value.Any(e => !a.TryGetValue(e.Key, out List<(int Col, int Pos)>? p) || !p.SequenceEqual(e.Value)))
                        {
                            ok = false;
                            break;
                        }
                    }
                }

                if (!ok)
                {
                    throw new OneLiteException(ResultCode.CorruptVtab, "database disk image is malformed", "fts3: index does not match the content");
                }
            }
        }
    }

    // ---------------------------------------------------------------- used by the cursor

    internal Fts3Db NewDb() => new(_conn, _cfg);

    /// <summary>Merges every index into one segment; false when there was nothing to merge.</summary>
    internal bool OptimizeAll()
    {
        bool changed = false;
        foreach (long b in FamilyBases())
        {
            if (_db.ReadDir(b, b + F3.LevelsPerIndex - 1).Count > 1)
            {
                _db.Optimize(b);
                changed = true;
            }
        }

        return changed;
    }
}
