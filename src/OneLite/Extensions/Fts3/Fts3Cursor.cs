using System.Collections.Concurrent;
using System.Linq;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.VirtualTables;

namespace RecursiveAI.OneLite.Extensions.Fts3;

/// <summary>A scan of an fts3/fts4 table: the rows a MATCH finds, or every row.</summary>
internal sealed class Fts3Cursor : VCursor
{
    internal const byte RefSubtype = 0xF3;

    private static readonly ConcurrentDictionary<long, Fts3Cursor> s_registry = new();
    private static long s_nextId;

    private readonly long _id;
    private readonly Fts3Table _t;
    private readonly Fts3Config _cfg;
    private readonly Fts3Db _db;
    private readonly Fts3Index _idx;
    private readonly Fts3Rows _rows;

    private int _mode; // 1 = matches, 2 = scan
    private List<long> _ids = [];
    private int _pos;
    private Statement? _scan;
    private bool _eof = true;
    private long _curId;
    private bool _loaded;
    private (Mem[] Values, long Lang)? _row;
    private long _langFilter;

    internal Fts3Query? Query;

    internal Fts3Cursor(Fts3Table t)
    {
        _t = t;
        _cfg = t.Config;
        _db = t.NewDb();
        _idx = new Fts3Index(_cfg, _db);
        _rows = new Fts3Rows(_db, _cfg);
        _id = Interlocked.Increment(ref s_nextId);
        s_registry[_id] = this;
    }

    internal static Fts3Cursor? FromRef(in Mem m)
    {
        if (m.Kind != StorageClass.Blob || m.Subtype != RefSubtype || m.Length != 8)
        {
            return null;
        }

        s_registry.TryGetValue(BitConverter.ToInt64(m.Bytes), out Fts3Cursor? c);
        return c;
    }

    internal Fts3Config Config => _cfg;

    internal bool Optimize() => _t.OptimizeAll();

    internal Fts3Index Index => _idx;

    internal Fts3Db Db => _db;

    internal long Docid => _curId;

    internal long Lang => _langFilter;

    internal bool HasMatch => _mode == 1 && Query is not null;

    internal bool OnRow => !_eof;

    public override void Dispose()
    {
        s_registry.TryRemove(_id, out _);
        _scan?.Dispose();
        _db.Dispose();
    }

    internal override bool Eof => _eof;

    private static string ArgText(in Mem m)
    {
        Span<byte> scratch = stackalloc byte[64];
        return System.Text.Encoding.UTF8.GetString(Vm.Values.AsText(m, scratch));
    }

    internal override ModStatus Filter(int indexNumber, string? indexString, ReadOnlySpan<Mem> args)
    {
        string[] spec = string.IsNullOrEmpty(indexString) ? [] : indexString.Split(',');
        string? text = null;
        bool hasMatch = false;
        bool nullMatch = false;
        int col = -1;
        long lo = long.MinValue;
        long hi = long.MaxValue;
        bool none = false;
        _langFilter = 0;
        bool langGiven = false;
        for (int i = 0; i < spec.Length; i++)
        {
            Mem a = args[i];
            switch (spec[i][0])
            {
                case 'M':
                case 'C':
                    hasMatch = true;
                    if (spec[i][0] == 'C')
                    {
                        col = int.Parse(spec[i].AsSpan(1), System.Globalization.CultureInfo.InvariantCulture);
                    }

                    if (a.Kind == StorageClass.Null)
                    {
                        nullMatch = true;
                    }
                    else
                    {
                        text = ArgText(a);
                    }

                    break;
                case 'N':
                    langGiven = true;
                    if (a.Kind == StorageClass.Integral)
                    {
                        _langFilter = a.IntValue;
                    }
                    else
                    {
                        none = true;
                    }

                    break;
                default:
                    Bound(spec[i][0], a, ref lo, ref hi, ref none);
                    break;
            }
        }

        _ = langGiven;
        _pos = -1;
        Query = null;
        _scan?.Dispose();
        _scan = null;
        if (hasMatch)
        {
            _mode = 1;
            if (!nullMatch && text is not null)
            {
                Query = Fts3Query.Parse(_cfg, text, col);
            }

            var ids = new List<long>();
            if (Query is not null && !none)
            {
                foreach (long id in Fts3Query.Evaluate(Query.Root, _idx, _langFilter))
                {
                    if (id >= lo && id <= hi)
                    {
                        ids.Add(id);
                    }
                }

                ids.Sort();
                if (_cfg.Descending)
                {
                    ids.Reverse();
                }
            }

            _ids = ids;
            _pos = -1;
            return Next();
        }

        _mode = 2;
        if (_cfg.Content == Fts3Content.None)
        {
            throw new OneLiteException(ResultCode.Error, "SQL logic error");
        }

        if (none)
        {
            _eof = true;
            return ModStatus.Ok;
        }

        bool external = _cfg.Content == Fts3Content.External;
        string tbl = external ? ExtTable() : _db.T("_content");
        string key = external ? "rowid" : "docid";
        string sql = "SELECT " + key + ", " + ColList(external) + " FROM " + tbl + " WHERE " + key + " BETWEEN ?1 AND ?2 ORDER BY " + key + (_cfg.Descending ? " DESC" : string.Empty);
        _scan = _db.Conn.Prepare(sql);
        _scan.Bind(1, lo);
        _scan.Bind(2, hi);
        return Next();
    }

    private string ExtTable()
    {
        string t = "\"" + _cfg.ContentTable!.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        return _cfg.Schema == "main" ? t : "\"" + _cfg.Schema + "\"." + t;
    }

    private string ColList(bool external)
    {
        var l = new List<string>();
        for (int i = 0; i < _cfg.ColumnCount; i++)
        {
            string n = external ? _cfg.Columns[i] : "c" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + _cfg.Columns[i];
            l.Add("\"" + n.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
        }

        if (_cfg.LangidColumn is not null)
        {
            l.Add(external ? "\"" + _cfg.LangidColumn.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : "langid");
        }

        return string.Join(", ", l);
    }

    private static void Bound(char op, in Mem a, ref long lo, ref long hi, ref bool none)
    {
        long iv;
        double dv;
        bool isInt = a.Kind == StorageClass.Integral;
        if (isInt)
        {
            iv = a.IntValue;
            dv = iv;
        }
        else if (a.Kind == StorageClass.Real)
        {
            iv = 0;
            dv = a.RealValue;
        }
        else if (a.Kind == StorageClass.Text)
        {
            StorageClass k = Vm.Values.ParseNumber(a.Bytes, out long pi, out double pd, out bool whole);
            if (!whole || k == StorageClass.Null)
            {
                none = true;
                return;
            }

            isInt = k == StorageClass.Integral;
            iv = pi;
            dv = k == StorageClass.Integral ? pi : pd;
        }
        else
        {
            none = true;
            return;
        }

        switch (op)
        {
            case 'E':
                if (!isInt && dv != Math.Floor(dv))
                {
                    none = true;
                    return;
                }

                iv = isInt ? iv : (long)dv;
                lo = Math.Max(lo, iv);
                hi = Math.Min(hi, iv);
                break;
            case 'G':
                lo = Math.Max(lo, isInt ? (iv == long.MaxValue ? long.MaxValue : iv + 1) : ToLong(Math.Floor(dv), 1));
                if (isInt && iv == long.MaxValue)
                {
                    none = true;
                }

                break;
            case 'g':
                lo = Math.Max(lo, isInt ? iv : ToLong(Math.Ceiling(dv), 0));
                break;
            case 'L':
                hi = Math.Min(hi, isInt ? (iv == long.MinValue ? long.MinValue : iv - 1) : ToLong(Math.Ceiling(dv), -1));
                if (isInt && iv == long.MinValue)
                {
                    none = true;
                }

                break;
            case 'l':
                hi = Math.Min(hi, isInt ? iv : ToLong(Math.Floor(dv), 0));
                break;
        }
    }

    private static long ToLong(double d, int add)
    {
        if (d >= 9.2233720368547758e18)
        {
            return long.MaxValue;
        }

        if (d <= -9.2233720368547758e18)
        {
            return long.MinValue;
        }

        return (long)d + add;
    }

    internal override ModStatus Next()
    {
        _loaded = false;
        _row = null;
        if (_mode == 1)
        {
            _pos++;
            _eof = _pos >= _ids.Count;
            _curId = _eof ? 0 : _ids[_pos];
            return ModStatus.Ok;
        }

        if (_scan is null || !_scan.Step())
        {
            _eof = true;
            _scan?.Dispose();
            _scan = null;
            return ModStatus.Ok;
        }

        _eof = false;
        _curId = _scan.GetInt64(0);
        _row = _rows.Read(_scan, 1);
        _loaded = true;
        return ModStatus.Ok;
    }

    internal override ModStatus RowId(out long rowid)
    {
        rowid = _curId;
        return ModStatus.Ok;
    }

    /// <summary>The current row's text columns and language, or null when the row is not in the content table.</summary>
    internal (Mem[] Values, long Lang)? Row()
    {
        if (!_loaded)
        {
            if (_cfg.Content == Fts3Content.None)
            {
                throw new OneLiteException(ResultCode.Error, "SQL logic error");
            }

            _row = _rows.Get(_curId);
            _loaded = true;
        }

        return _row;
    }

    internal override ModStatus Column(int index, ref Mem dst, ref byte[]? own)
    {
        int n = _cfg.ColumnCount;
        if (index < n)
        {
            (Mem[] Values, long Lang)? r = Row();
            if (r is null)
            {
                Vm.Values.SetNull(ref dst);
                return ModStatus.Ok;
            }

            Mem m = r.Value.Values[index];
            switch (m.Kind)
            {
                case StorageClass.Integral:
                    Vm.Values.SetInt(ref dst, m.IntValue);
                    break;
                case StorageClass.Real:
                    Vm.Values.SetReal(ref dst, m.RealValue);
                    break;
                case StorageClass.Text:
                    Vm.Values.SetBytes(ref dst, ref own, StorageClass.Text, m.Bytes);
                    break;
                case StorageClass.Blob:
                    Vm.Values.SetBytes(ref dst, ref own, StorageClass.Blob, m.Bytes);
                    break;
                default:
                    Vm.Values.SetNull(ref dst);
                    break;
            }

            return ModStatus.Ok;
        }

        if (index == n)
        {
            Vm.Values.SetBytes(ref dst, ref own, StorageClass.Blob, BitConverter.GetBytes(_id));
            dst.Subtype = RefSubtype;
            return ModStatus.Ok;
        }

        if (index == n + 1)
        {
            Vm.Values.SetInt(ref dst, _curId);
            return ModStatus.Ok;
        }

        long lang = _cfg.LangidColumn is null ? 0 : _langFilter;
        if (_cfg.LangidColumn is not null && _mode == 2 && Row() is { } rr)
        {
            lang = rr.Lang;
        }

        Vm.Values.SetInt(ref dst, lang);
        return ModStatus.Ok;
    }
}
