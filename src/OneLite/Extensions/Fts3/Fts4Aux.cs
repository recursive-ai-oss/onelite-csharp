using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Extensions.Fts5;
using RecursiveAI.OneLite.Planning;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.VirtualTables;

namespace RecursiveAI.OneLite.Extensions.Fts3;

/// <summary>
/// The <c>fts4aux</c> module (sqlite.org/fts3.html, "The fts4aux Virtual Table"): the terms of an fts3 or fts4 index with their document and occurrence
/// counts. One row per term and column, plus a <c>*</c> row per term for all columns, in term then column order. <c>languageid</c> is a hidden column
/// (default 0). Like the reference, the two-argument form <c>fts4aux(schema, table)</c> is accepted only for a table created in <c>temp</c>.
/// </summary>
internal sealed class Fts4AuxModule : BuiltinModule
{
    internal override string Name => "fts4aux";

    private static OneLiteException BadArgs() => new(ResultCode.Error, "invalid arguments to fts4aux constructor");

    internal override ShadowTable[] ShadowTables(string schema, string table, ImmutableArray<string> args, int pageSize)
    {
        if (args.Length is < 1 or > 2 || (args.Length == 2 && !string.Equals(schema, "temp", StringComparison.OrdinalIgnoreCase)))
        {
            throw BadArgs();
        }

        return [];
    }

    internal override VTab Connect(Connection conn, TableInfo table, string schema, ImmutableArray<string> args, Action<string> declare)
    {
        string[] a = [.. args.Select(x => Fts5Config.Unquote(x.Trim()))];
        string db = schema;
        string tbl;
        if (a.Length == 1)
        {
            tbl = a[0];
        }
        else if (a.Length == 2 && table.Db == 1)
        {
            db = a[0];
            tbl = a[1];
        }
        else
        {
            throw BadArgs();
        }

        declare("CREATE TABLE x(term, col, documents, occurrences, languageid HIDDEN)");
        Catalog? cat = conn.Catalog;
        TableInfo? target = cat?.FindIn(db, tbl);
        if (target?.Virtual is not { } v || !(string.Equals(v.Module, "fts3", StringComparison.OrdinalIgnoreCase) || string.Equals(v.Module, "fts4", StringComparison.OrdinalIgnoreCase)))
        {
            // the reference creates the table anyway and fails when it is read
            return new AuxTable(conn, null, null);
        }

        return new AuxTable(conn, target, Fts3Config.Parse(db, target.Name, string.Equals(v.Module, "fts4", StringComparison.OrdinalIgnoreCase), v.Args));
    }

    private sealed class AuxTable(Connection conn, TableInfo? target, Fts3Config? cfg) : VTab
    {
        internal override void BestIndex(BestIndexInfo info)
        {
            ReadOnlySpan<IndexConstraint> cons = info.Constraints;
            var spec = new List<string>();
            bool lang = false;
            for (int i = 0; i < cons.Length; i++)
            {
                if (!cons[i].Usable)
                {
                    continue;
                }

                string? s = null;
                if (cons[i].Column == 4 && cons[i].Op == ConstraintOp.Eq && !lang)
                {
                    s = "lang";
                    lang = true;
                    info.ConstraintUsage[i].Omit = true;
                }
                else if (cons[i].Column == 0)
                {
                    // Term bounds only narrow the scan (as in the reference, where they decide whether a malformed term is reached); the
                    // engine still applies them.
                    s = cons[i].Op switch
                    {
                        ConstraintOp.Eq => "eq",
                        ConstraintOp.Ge or ConstraintOp.Gt => "ge",
                        ConstraintOp.Le or ConstraintOp.Lt => "le",
                        _ => null,
                    };
                }

                if (s is not null)
                {
                    info.ConstraintUsage[i].ArgvIndex = spec.Count + 1;
                    spec.Add(s);
                }
            }

            info.IndexString = string.Join(",", spec);
            // rows come out in term order, so a lone ORDER BY term needs no sort (it matters only for where a read error cuts the output)
            ReadOnlySpan<IndexOrderBy> ob = info.OrderBy;
            if (ob.Length == 1 && ob[0].Column == 0 && !ob[0].Descending)
            {
                info.OrderByConsumed = true;
            }

            info.EstimatedCost = 1_000_000;
            info.EstimatedRows = 1000;
        }

        internal override VCursor Open()
        {
            if (cfg is null || target is null)
            {
                throw new OneLiteException(ResultCode.Error, "SQL logic error");
            }

            return new AuxCursor(conn, target, cfg);
        }

        public override void Dispose()
        {
        }
    }

    private sealed class AuxCursor : VCursor
    {
        private readonly Fts3Db _db;
        private readonly Fts3Index _idx;
        private readonly Fts3Config _cfg;
        private List<object?[]> _rows = [];
        private int _pos;
        private int _errorAt = -1;

        internal AuxCursor(Connection conn, TableInfo target, Fts3Config cfg)
        {
            _cfg = cfg;
            _db = new Fts3Db(conn, cfg);
            _idx = new Fts3Index(cfg, _db);
        }

        internal override bool Eof => _pos >= _rows.Count;

        internal override ModStatus Filter(int indexNumber, string? indexString, ReadOnlySpan<Mem> args)
        {
            long lang = 0;
            byte[]? lower = null;
            byte[]? upper = null;
            string[] spec = string.IsNullOrEmpty(indexString) ? [] : indexString.Split(',');
            for (int i = 0; i < spec.Length && i < args.Length; i++)
            {
                Mem a = args[i];
                if (spec[i] == "lang")
                {
                    lang = a.Kind == StorageClass.Integral ? a.IntValue : a.Kind == StorageClass.Real ? (long)a.RealValue : 0;
                }
                else if (a.Kind == StorageClass.Text)
                {
                    byte[] b = a.Bytes.ToArray();
                    if (spec[i] is "eq" or "ge")
                    {
                        lower = lower is null || ByteKeyComparer.Instance.Compare(b, lower) > 0 ? b : lower;
                    }

                    if (spec[i] is "eq" or "le")
                    {
                        upper = upper is null || ByteKeyComparer.Instance.Compare(b, upper) < 0 ? b : upper;
                    }
                }
            }

            var rows = new List<object?[]>();
            _errorAt = -1;
            // The reference takes the language id as an absolute segment-directory index (level range lang*1024 ..): with prefix indexes
            // or other languages around, "languageid = 1" reads the first prefix index, not language 1 (observed).
            foreach (KeyValuePair<byte[], SortedDictionary<long, List<(int Col, int Pos)>>> kv in _idx.ReadAllAt(Math.Max(0, lang) * F3.LevelsPerIndex))
            {
                if ((lower is not null && ByteKeyComparer.Instance.Compare(kv.Key, lower) < 0) || (upper is not null && ByteKeyComparer.Instance.Compare(kv.Key, upper) > 0))
                {
                    continue;
                }

                // and it fails with "database disk image is malformed" on the first term that occurs in a column after the second (observed)
                if (kv.Value.Values.Any(l => l.Any(p => p.Col >= 2)))
                {
                    _errorAt = rows.Count;
                    break;
                }

                string term = Encoding.UTF8.GetString(kv.Key);
                long occ = 0;
                var perCol = new SortedDictionary<int, (long Docs, long Occ)>();
                foreach (List<(int Col, int Pos)> list in kv.Value.Values)
                {
                    occ += list.Count;
                    foreach (IGrouping<int, (int Col, int Pos)> g in list.GroupBy(p => p.Col))
                    {
                        perCol.TryGetValue(g.Key, out (long Docs, long Occ) cur);
                        perCol[g.Key] = (cur.Docs + 1, cur.Occ + g.Count());
                    }
                }

                rows.Add([term, "*", (long)kv.Value.Count, occ, lang]);
                foreach (KeyValuePair<int, (long Docs, long Occ)> c in perCol)
                {
                    rows.Add([term, (long)c.Key, c.Value.Docs, c.Value.Occ, lang]);
                }
            }

            _rows = rows;
            _pos = 0;
            if (_errorAt == 0)
            {
                throw OneLiteException.Corrupt(0, "fts4aux: a term occurs in a column after the second");
            }

            return ModStatus.Ok;
        }

        internal override ModStatus Next()
        {
            _pos++;
            if (_pos == _errorAt)
            {
                throw OneLiteException.Corrupt(0, "fts4aux: a term occurs in a column after the second");
            }

            return ModStatus.Ok;
        }

        internal override ModStatus RowId(out long rowid)
        {
            rowid = _pos + 1;
            return ModStatus.Ok;
        }

        internal override ModStatus Column(int index, ref Mem dst, ref byte[]? own)
        {
            switch (_rows[_pos][index])
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

        public override void Dispose() => _db.Dispose();
    }
}
