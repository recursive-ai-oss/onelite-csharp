using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.VirtualTables;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// The planner side of the virtual-table interface: ORDER BY offered to <c>BestIndex</c> and consumed, constraint operands that are subqueries,
/// parameters or columns of earlier tables, eponymous modules used as table-valued functions, and in-memory databases opened by name.
/// </summary>
public sealed class VirtualTablePlannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-vtplan-" + Guid.NewGuid().ToString("N"));

    public VirtualTablePlannerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    /// <summary>
    /// Three fixed rows kept in the order (3,'x'), (1,'y'), (2,'z'). When <see cref="Consume"/> is set the module claims to deliver any ORDER BY it is
    /// offered and returns them in that stored order, so a query that is not re-sorted shows it.
    /// </summary>
    private sealed class OrdModule : IVirtualTableModule
    {
        internal bool Consume;
        internal readonly List<string> Offered = [];
        internal readonly List<string> Plans = [];
        internal readonly (long A, string B)[] Rows = [(3, "x"), (1, "y"), (2, "z")];

        public IVirtualTable Create(VirtualTableContext c) => Connect(c);

        public IVirtualTable Connect(VirtualTableContext c)
        {
            c.DeclareTable("CREATE TABLE x(a, b)");
            return new Tab(this);
        }

        public bool IsEponymous => false;
    }

    private sealed class Tab(OrdModule m) : IVirtualTable
    {
        public void BestIndex(BestIndexInfo info)
        {
            m.Offered.Add(string.Join(",", info.OrderBy.ToArray().Select(o => o.Column + (o.Descending ? " desc" : " asc"))));
            info.OrderByConsumed = m.Consume && info.OrderBy.Length > 0;
            for (int i = 0; i < info.Constraints.Length; i++)
            {
                IndexConstraint c = info.Constraints[i];
                if (c.Column == 1 && c.Op == ConstraintOp.Eq && c.Usable)
                {
                    info.ConstraintUsage[i].ArgvIndex = 1;
                    info.ConstraintUsage[i].Omit = true;
                    info.IndexNumber = 1;
                    info.EstimatedCost = 1;
                    m.Plans.Add("eq-b");
                    return;
                }
            }

            info.EstimatedCost = 100;
            m.Plans.Add("scan");
        }

        public IVirtualTableCursor Open() => new Cur(m);

        public void Dispose()
        {
        }
    }

    private sealed class Cur(OrdModule m) : IVirtualTableCursor
    {
        private List<(long A, string B)> _rows = [];
        private int _pos;

        public bool Eof => _pos >= _rows.Count;

        public long RowId => _pos + 1;

        public void Filter(int indexNumber, string? indexString, ReadOnlySpan<Mem> args)
        {
            _rows = [.. m.Rows];
            if (indexNumber == 1)
            {
                string want = Encoding.UTF8.GetString(args[0].Bytes);
                _rows = [.. _rows.Where(r => r.B == want)];
            }

            _pos = 0;
        }

        public void MoveNext() => _pos++;

        public void Column(int column, ColumnResult result)
        {
            if (column == 0)
            {
                result.SetInt64(_rows[_pos].A);
            }
            else
            {
                result.SetText(_rows[_pos].B);
            }
        }

        public void Dispose()
        {
        }
    }

    /// <summary>An eponymous module <c>gen(lo, hi)</c>: the integers from lo to hi, with lo and hi hidden columns that table-function arguments and equalities fill.</summary>
    private sealed class GenModule : IVirtualTableModule
    {
        internal readonly List<string> Plans = [];

        public IVirtualTable Create(VirtualTableContext c) => Connect(c);

        public IVirtualTable Connect(VirtualTableContext c)
        {
            c.DeclareTable("CREATE TABLE x(v, lo HIDDEN, hi HIDDEN)");
            return new GenTab(this);
        }

        public bool IsEponymous => true;
    }

    private sealed class GenTab(GenModule m) : IVirtualTable
    {
        public void BestIndex(BestIndexInfo info)
        {
            int argc = 0, mask = 0;
            var plan = new StringBuilder();
            for (int col = 1; col <= 2; col++)
            {
                for (int i = 0; i < info.Constraints.Length; i++)
                {
                    IndexConstraint c = info.Constraints[i];
                    if (c.Column == col && c.Op == ConstraintOp.Eq)
                    {
                        plan.Append(col == 1 ? "lo" : "hi").Append(c.Usable ? "+" : "-");
                        if (c.Usable)
                        {
                            info.ConstraintUsage[i].ArgvIndex = ++argc;
                            info.ConstraintUsage[i].Omit = true;
                            mask |= col;
                        }
                    }
                }
            }

            info.IndexNumber = mask;
            info.EstimatedCost = (mask & 3) == 3 ? 10 : 1e9;
            m.Plans.Add(plan.ToString());
        }

        public IVirtualTableCursor Open() => new GenCur();

        public void Dispose()
        {
        }
    }

    private sealed class GenCur : IVirtualTableCursor
    {
        private long _v, _hi;

        public bool Eof => _v > _hi;

        public long RowId => _v;

        public void Filter(int indexNumber, string? indexString, ReadOnlySpan<Mem> args)
        {
            int k = 0;
            long lo = 0, hi = -1;
            if ((indexNumber & 1) != 0)
            {
                lo = args[k++].IntValue;
            }

            if ((indexNumber & 2) != 0)
            {
                hi = args[k++].IntValue;
            }

            _v = lo;
            _hi = hi;
        }

        public void MoveNext() => _v++;

        public void Column(int column, ColumnResult result)
        {
            if (column == 0)
            {
                result.SetInt64(_v);
            }
            else
            {
                result.SetNull();
            }
        }

        public void Dispose()
        {
        }
    }

    private static string Q(Connection conn, string sql, params object[] args)
    {
        var o = new StringBuilder();
        string rest = sql;
        while (!string.IsNullOrWhiteSpace(rest))
        {
        using Statement st = conn.Prepare(rest);
        rest = st.Tail;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is string s)
            {
                st.Bind(i + 1, s);
            }
            else
            {
                st.Bind(i + 1, Convert.ToInt64(args[i], System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        while (st.Step())
        {
            for (int i = 0; i < st.ColumnCount; i++)
            {
                o.Append(i > 0 ? "|" : "").Append(st.IsNull(i) ? "NULL" : st.GetString(i));
            }

            o.Append(';');
        }
        }

        return o.ToString();
    }

    private Connection Open(OrdModule? ord = null, GenModule? gen = null)
    {
        Database db = Database.Open(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));
        Connection conn = db.Connect();
        if (ord is not null)
        {
            conn.RegisterModule("ord", ord);
            Q(conn, "CREATE VIRTUAL TABLE t USING ord");
        }

        if (gen is not null)
        {
            conn.RegisterModule("gen", gen);
        }

        return conn;
    }

    [Fact]
    public void OrderByIsOfferedAndConsumedSoNothingIsSorted()
    {
        var m = new OrdModule { Consume = true };
        using Connection conn = Open(m);
        Assert.Equal("3|x;1|y;2|z;", Q(conn, "SELECT a, b FROM t ORDER BY a"));
        Assert.Contains("0 asc", m.Offered);
        m.Offered.Clear();
        Assert.Equal("3|x;1|y;2|z;", Q(conn, "SELECT a, b FROM t ORDER BY b DESC, a"));
        Assert.Contains("1 desc,0 asc", m.Offered);

        // Terms that are not plain columns of the table are not offered, and the sort runs.
        m.Offered.Clear();
        Assert.Equal("1|y;2|z;3|x;", Q(conn, "SELECT a, b FROM t ORDER BY a + 0"));
        Assert.DoesNotContain(m.Offered, o => o.Length > 0);
    }

    [Fact]
    public void OrderByNotConsumedIsSortedByTheEngine()
    {
        var m = new OrdModule { Consume = false };
        using Connection conn = Open(m);
        Assert.Equal("1|y;2|z;3|x;", Q(conn, "SELECT a, b FROM t ORDER BY a"));
        Assert.Equal("3|x;2|z;1|y;", Q(conn, "SELECT a, b FROM t ORDER BY a DESC"));
        Assert.Equal("3|x;1|y;2|z;", Q(conn, "SELECT a, b FROM t ORDER BY b"));
        Assert.Contains("0 asc", m.Offered);
    }

    [Fact]
    public void OrderByWithAliasPositionLimitAndWhere()
    {
        var m = new OrdModule { Consume = true };
        using Connection conn = Open(m);
        Assert.Equal("3;1;", Q(conn, "SELECT a AS q FROM t ORDER BY q LIMIT 2"));
        Assert.Equal("3;1;2;", Q(conn, "SELECT a FROM t ORDER BY 1"));
        Assert.Equal("1|y;", Q(conn, "SELECT a, b FROM t WHERE b = 'y' ORDER BY a"));
        Assert.Contains("eq-b", m.Plans);

        // A grouped or distinct query is not offered the order.
        m.Offered.Clear();
        Assert.Equal("1;2;3;", Q(conn, "SELECT DISTINCT a FROM t ORDER BY a"));
        Assert.DoesNotContain(m.Offered, o => o.Length > 0);
    }

    [Fact]
    public void ConstraintOperandsMaySubqueriesParametersAndEarlierTables()
    {
        var m = new OrdModule();
        using Connection conn = Open(m);
        Q(conn, "CREATE TABLE k(id INTEGER PRIMARY KEY, w)");
        Q(conn, "INSERT INTO k VALUES (1,'z'),(2,'x'),(3,'nothing')");

        Assert.Equal("2|z;", Q(conn, "SELECT a, b FROM t WHERE b = (SELECT 'z')"));
        Assert.Contains("eq-b", m.Plans);

        m.Plans.Clear();
        Assert.Equal("1|y;", Q(conn, "SELECT a, b FROM t WHERE b = ?", "y"));
        Assert.Contains("eq-b", m.Plans);

        m.Plans.Clear();
        Assert.Equal("1|2|z;2|3|x;", Q(conn, "SELECT k.id, t.a, t.b FROM k JOIN t ON t.b = k.w ORDER BY k.id"));
        Assert.Contains("eq-b", m.Plans);

        m.Plans.Clear();
        Assert.Equal("1|2;2|3;3|NULL;", Q(conn, "SELECT k.id, (SELECT a FROM t WHERE b = k.w) FROM k"));
        Assert.Contains("eq-b", m.Plans);
    }

    [Fact]
    public void EponymousModuleWorksAsATableValuedFunction()
    {
        var g = new GenModule();
        using Connection conn = Open(null, g);
        Assert.Equal("2;3;4;", Q(conn, "SELECT v FROM gen(2, 4)"));
        Assert.Equal("2;3;4;", Q(conn, "SELECT v FROM gen WHERE lo = 2 AND hi = 4"));
        Assert.Equal("5;", Q(conn, "SELECT count(*) + 2 FROM gen(1, 3)"));

        // The arguments may be columns of a table placed before the function.
        Q(conn, "CREATE TABLE r(n INTEGER, m INTEGER)");
        Q(conn, "INSERT INTO r VALUES (1,2),(5,6)");
        Assert.Equal("1|1;1|2;5|5;5|6;", Q(conn, "SELECT r.n, gen.v FROM r, gen(r.n, r.m)"));
        Assert.Equal("1|1;1|2;5|5;5|6;", Q(conn, "SELECT r.n, g.v FROM gen(r.n, r.m) AS g, r"));

        var e = Assert.Throws<OneLiteException>(() => Q(conn, "SELECT v FROM gen(1, 2, 3)"));
        Assert.Equal("too many arguments on gen() - max 2", e.Message);
    }

    [Fact]
    public void BuiltInFunctionsAreEponymousOnlyAndKeepTheirErrors()
    {
        using Connection conn = Open();
        Assert.Equal("no such module: generate_series", Assert.Throws<OneLiteException>(() => Q(conn, "CREATE VIRTUAL TABLE g USING generate_series")).Message);
        Assert.Equal("no such module: json_each", Assert.Throws<OneLiteException>(() => Q(conn, "CREATE VIRTUAL TABLE g USING json_each(1)")).Message);
        Assert.Equal("too many arguments on json_each() - max 2", Assert.Throws<OneLiteException>(() => Q(conn, "SELECT * FROM json_each(1,2,3)")).Message);
        Assert.Equal("first argument to \"generate_series()\" missing or unusable", Assert.Throws<OneLiteException>(() => Q(conn, "SELECT * FROM generate_series")).Message);
        Assert.Equal("1|1|3;2|1|3;3|1|3;", Q(conn, "SELECT value, start, stop FROM generate_series WHERE stop = 3 AND start = 1"));
        Assert.Equal("2;", Q(conn, "SELECT value FROM json_each('[1,2]') WHERE root = '$[1]'"));
    }

    [Fact]
    public void MemoryDatabasesAreOpenedByName()
    {
        string cwd = Directory.GetCurrentDirectory();
        string before = string.Join(",", Directory.GetFileSystemEntries(cwd).OrderBy(x => x, StringComparer.Ordinal));
        foreach (string name in new[] { ":memory:", string.Empty, "file::memory:", "file:name?mode=memory" })
        {
            using Database db = Database.Open(name);
            using Connection a = db.Connect();
            using Connection b = db.Connect();
            Q(a, "CREATE TABLE m(x); INSERT INTO m VALUES (7)");
            Assert.Equal("7;", Q(a, "SELECT x FROM m"));

            // Every connection to a private in-memory database has a database of its own.
            Assert.Equal(string.Empty, Q(b, "SELECT name FROM sqlite_schema"));
        }

        using (Database db = Database.Open("file:shared?mode=memory&cache=shared"))
        {
            using Connection a = db.Connect();
            using Connection b = db.Connect();
            Q(a, "CREATE TABLE m(x); INSERT INTO m VALUES (7)");
            Assert.Equal("7;", Q(b, "SELECT x FROM m"));
        }

        using (Database db = Database.OpenReadOnly(":memory:"))
        {
            using Connection a = db.Connect();
            Assert.Equal(string.Empty, Q(a, "SELECT name FROM sqlite_schema"));
        }

        Assert.Equal(before, string.Join(",", Directory.GetFileSystemEntries(cwd).OrderBy(x => x, StringComparer.Ordinal)));
        Assert.False(File.Exists(Path.Combine(cwd, ":memory:")));
        Assert.False(File.Exists(Path.Combine(cwd, "file::memory:")));
        Assert.False(File.Exists("name"));
    }
}
