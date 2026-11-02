using RecursiveAI.OneLite.Planning;
using RecursiveAI.OneLite.Storage;
using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Vm;

/// <summary>
/// The table-valued form of the introspection pragmas (pragma): <c>pragma_table_info('t')</c> returns the rows <c>PRAGMA table_info(t)</c> does, with two
/// hidden columns, <c>arg</c> and <c>schema</c>, for the arguments. The rows are made from the connection's schema when the scan starts.
/// </summary>
internal sealed class PragmaCursor : ModuleCursor
{
    private static readonly Dictionary<string, ModuleDef> s_defs = Build();

    private readonly string _name;
    private Connection? _conn;
    private List<object?[]> _rows = [];
    private int _pos;
    private string? _arg, _schema;

    private PragmaCursor(string name) => _name = name;

    private static Dictionary<string, ModuleDef> Build()
    {
        var m = new Dictionary<string, ModuleDef>(StringComparer.OrdinalIgnoreCase);

        void Add(string name, string[] cols, int hidden)
        {
            string[] all = hidden == 2 ? [.. cols, "arg", "schema"] : cols;
            m[name] = new ModuleDef { Name = "pragma_" + name, Columns = all, HiddenFrom = cols.Length, MaxArgs = hidden, Create = () => new PragmaCursor(name) };
        }

        Add("table_info", ["cid", "name", "type", "notnull", "dflt_value", "pk"], 2);
        Add("table_xinfo", ["cid", "name", "type", "notnull", "dflt_value", "pk", "hidden"], 2);
        Add("index_list", ["seq", "name", "unique", "origin", "partial"], 2);
        Add("index_info", ["seqno", "cid", "name"], 2);
        Add("index_xinfo", ["seqno", "cid", "name", "desc", "coll", "key"], 2);
        Add("foreign_key_list", ["id", "seq", "table", "from", "to", "on_update", "on_delete", "match"], 2);
        Add("table_list", ["schema", "name", "type", "ncol", "wr", "strict"], 2);
        Add("database_list", ["seq", "name", "file"], 0);
        Add("collation_list", ["seq", "name"], 0);
        Add("module_list", ["name"], 0);
        Add("pragma_list", ["name"], 0);
        Add("compile_options", ["compile_options"], 0);
        return m;
    }

    internal static ModuleDef? Find(string name) => s_defs.GetValueOrDefault(name);

    internal override void Bind(Connection connection) => _conn = connection;

    internal override bool AtEnd => _pos >= _rows.Count;

    internal override long RowNumber => _pos + 1;

    private static string? Text(Mem m) => m.Kind == StorageClass.Null ? null : System.Text.Encoding.UTF8.GetString(Values.AsText(m, stackalloc byte[64]));

    internal override void Filter(ReadOnlySpan<Mem> args)
    {
        _arg = args.Length > 0 ? Text(args[0]) : null;
        _schema = args.Length > 1 ? Text(args[1]) : null;
        _pos = 0;
        _rows = [];
        ModuleDef def = s_defs[_name];
        if (def.MaxArgs > 0 && _arg is null && _name is not "table_list")
        {
            // No table, no rows.
            if (_schema is not null && (_conn!.Catalog ?? new Catalog()).SlotOf(_schema) < 0)
            {
                throw new OneLiteException(ResultCode.Error, "unknown database '" + _schema + "'");
            }

            return;
        }

        Connection conn = _conn!;
        var comp = new Compiler(conn.Catalog ?? new Catalog(), string.Empty, conn.Catalog is null ? TextEncoding.Utf8 : conn.Context?.Encoding ?? TextEncoding.Utf8)
        {
            DatabaseFile = conn.Pager.FullPath,
            TempOpen = conn.PragmaSettings.TempOpen || conn.Temp is not null,
        };
        (string[] Cols, List<object?[]> Rows)? r = comp.InfoRows(_name, _schema, _arg);
        _rows = r?.Rows ?? [];
    }

    internal override void Next() => _pos++;

    internal override void Column(int index, ref Mem dst, ref byte[]? own)
    {
        object?[] row = _rows[_pos];
        object? v;
        if (index < row.Length)
        {
            v = row[index];
        }
        else
        {
            v = index == row.Length ? _arg : _schema;
        }

        switch (v)
        {
            case null:
                Values.SetNull(ref dst);
                break;
            case string s:
                Values.SetBytes(ref dst, ref own, StorageClass.Text, System.Text.Encoding.UTF8.GetBytes(s));
                break;
            default:
                Values.SetInt(ref dst, Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture));
                break;
        }
    }
}
