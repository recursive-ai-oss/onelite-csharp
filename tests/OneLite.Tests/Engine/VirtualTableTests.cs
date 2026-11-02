using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Storage.Record;
using RecursiveAI.OneLite.VirtualTables;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>The user-module side of the virtual-table interface: declared columns, best-index negotiation, scans, updates and transaction hooks.</summary>
public sealed class VirtualTableTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-vtab-" + Guid.NewGuid().ToString("N"));

    public VirtualTableTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    /// <summary>An in-memory table of rows (rowid, values); column 0 can be looked up by equality, and a hidden column "mode" echoes its argument.</summary>
    private sealed class KvModule : IVirtualTableModule
    {
        internal readonly List<string> Log = [];
        internal readonly SortedDictionary<long, string?[]> Rows = [];
        internal readonly List<string> Plans = [];

        public IVirtualTable Create(VirtualTableContext c) => Connect(c);

        public IVirtualTable Connect(VirtualTableContext c)
        {
            Log.Add("connect " + c.Name + "(" + string.Join(",", c.Arguments) + ")");
            c.DeclareTable("CREATE TABLE x(" + string.Join(",", c.Arguments) + ", mode HIDDEN)");
            return new Table(this, c.Arguments.Count);
        }

        public bool IsEponymous => false;
    }

    private sealed class Table(KvModule m, int width) : ISavepointVirtualTable, IWritableVirtualTable
    {
        public void BestIndex(BestIndexInfo info)
        {
            for (int i = 0; i < info.Constraints.Length; i++)
            {
                IndexConstraint c = info.Constraints[i];
                if (c.Usable && c.Column == 0 && c.Op == ConstraintOp.Eq)
                {
                    info.ConstraintUsage[i].ArgvIndex = 1;
                    info.ConstraintUsage[i].Omit = true;
                    info.IndexNumber = 1;
                    info.EstimatedCost = 1;
                    info.EstimatedRows = 1;
                    m.Plans.Add("eq");
                    return;
                }
            }

            info.EstimatedCost = 100;
            m.Plans.Add("scan");
        }

        public IVirtualTableCursor Open() => new Cursor(m, width);

        public long Update(ReadOnlySpan<Mem> v)
        {
            var vals = new string?[width];
            if (v.Length == 1)
            {
                m.Rows.Remove(v[0].IntValue);
                m.Log.Add("delete " + v[0].IntValue);
                return 0;
            }

            for (int i = 0; i < width; i++)
            {
                Mem x = v[2 + i];
                vals[i] = x.Kind == StorageClass.Null ? null : x.Kind == StorageClass.Integral ? x.IntValue.ToString() : Encoding.UTF8.GetString(x.Bytes);
            }

            long rid;
            if (v[0].Kind == StorageClass.Null)
            {
                rid = v[1].Kind == StorageClass.Null ? (m.Rows.Count == 0 ? 1 : m.Rows.Keys.Max() + 1) : v[1].IntValue;
                m.Log.Add("insert " + rid);
            }
            else
            {
                rid = v[1].IntValue;
                if (rid != v[0].IntValue)
                {
                    m.Rows.Remove(v[0].IntValue);
                }

                m.Log.Add("update " + v[0].IntValue + "->" + rid);
            }

            m.Rows[rid] = vals;
            return rid;
        }

        public void Begin() => m.Log.Add("begin");

        public void Sync() => m.Log.Add("sync");

        public void Commit() => m.Log.Add("commit");

        public void Rollback() => m.Log.Add("rollback");

        public void Savepoint(int level) => m.Log.Add("savepoint " + level);

        public void Release(int level) => m.Log.Add("release " + level);

        public void RollbackTo(int level) => m.Log.Add("rollbackto " + level);

        public void Dispose()
        {
        }
    }

    private sealed class Cursor(KvModule m, int width) : IVirtualTableCursor
    {
        private List<long> _ids = [];
        private int _pos;

        public bool Eof => _pos >= _ids.Count;

        public long RowId => _ids[_pos];

        public void Filter(int indexNumber, string? indexString, ReadOnlySpan<Mem> args)
        {
            _ids = [.. m.Rows.Keys];
            if (indexNumber == 1)
            {
                string want = args[0].Kind == StorageClass.Integral ? args[0].IntValue.ToString() : Encoding.UTF8.GetString(args[0].Bytes);
                _ids = [.. _ids.Where(i => m.Rows[i][0] == want)];
            }

            _pos = 0;
        }

        public void MoveNext() => _pos++;

        public void Column(int column, ColumnResult result)
        {
            if (column >= width)
            {
                result.SetText("hidden");
                return;
            }

            string? s = m.Rows[_ids[_pos]][column];
            if (s is null)
            {
                result.SetNull();
            }
            else
            {
                result.SetText(s);
            }
        }

        public void Dispose()
        {
        }
    }

    private static string Q(Connection conn, string sql)
    {
        var o = new StringBuilder();
        string rest = sql;
        while (!string.IsNullOrWhiteSpace(rest))
        {
            using Statement st = conn.Prepare(rest);
            rest = st.Tail;
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

    private Connection Open(KvModule m, out Database db)
    {
        db = Database.Open(Path.Combine(_dir, "v.db"));
        Connection conn = db.Connect();
        conn.RegisterModule("kv", m);
        return conn;
    }

    [Fact]
    public void CreateInsertSelectUpdateDelete()
    {
        var m = new KvModule();
        using Database db0 = Database.Open(Path.Combine(_dir, "a.db"));
        using Connection conn = db0.Connect();
        conn.RegisterModule("kv", m);
        Assert.Equal(string.Empty, Q(conn, "CREATE VIRTUAL TABLE t USING kv(k, v)"));
        Assert.Equal(string.Empty, Q(conn, "INSERT INTO t VALUES ('a', 1), ('b', 2), ('c', NULL)"));
        Assert.Equal("a|1;b|2;c|NULL;", Q(conn, "SELECT * FROM t"));
        Assert.Equal("2;", Q(conn, "SELECT v FROM t WHERE k = 'b'"));
        Assert.Contains("eq", m.Plans);
        Assert.Equal("b;", Q(conn, "SELECT k FROM t WHERE rowid = 2"));
        Assert.Equal("hidden;", Q(conn, "SELECT mode FROM t LIMIT 1"));
        Q(conn, "UPDATE t SET v = 20 WHERE k = 'a'");
        Assert.Equal("1;", Q(conn, "SELECT changes()"));
        Assert.Equal("a|20;", Q(conn, "SELECT k, v FROM t WHERE k = 'a'"));
        Q(conn, "DELETE FROM t WHERE k = 'c'");
        Assert.Equal("a|20;b|2;", Q(conn, "SELECT k, v FROM t ORDER BY k"));
        Assert.Equal("a|1|b|2;", Q(conn, "SELECT x.k, 1, y.k, y.v FROM t x JOIN t y ON y.k = 'b' WHERE x.k = 'a'"));
        Assert.Equal("2;", Q(conn, "SELECT count(*) FROM t"));
        Q(conn, "DROP TABLE t");
        Assert.Equal(string.Empty, Q(conn, "SELECT name FROM sqlite_schema"));
    }

    [Fact]
    public void TransactionHooksRunInOrder()
    {
        var m = new KvModule();
        using Database db0 = Database.Open(Path.Combine(_dir, "b.db"));
        using Connection conn = db0.Connect();
        conn.RegisterModule("kv", m);
        Q(conn, "CREATE VIRTUAL TABLE t USING kv(k)");
        m.Log.Clear();
        Q(conn, "INSERT INTO t VALUES ('a')");
        Assert.Equal("begin,insert 1,sync,commit", string.Join(",", m.Log.Where(l => !l.StartsWith("connect"))));
        m.Log.Clear();
        Q(conn, "BEGIN; INSERT INTO t VALUES ('b'); SAVEPOINT s1; INSERT INTO t VALUES ('c'); ROLLBACK TO s1; RELEASE s1; ROLLBACK");
        Assert.Equal("begin,insert 2,savepoint 1,insert 3,rollbackto 1,release 1,rollback", string.Join(",", m.Log.Where(l => !l.StartsWith("connect"))));
    }

    [Fact]
    public void ReadOnlyTableRefusesWrites()
    {
        var m = new ReadOnlyModule();
        using Database db0 = Database.Open(Path.Combine(_dir, "c.db"));
        using Connection conn = db0.Connect();
        conn.RegisterModule("ro", m);
        Q(conn, "CREATE VIRTUAL TABLE t USING ro");
        var e = Assert.Throws<OneLiteException>(() => Q(conn, "INSERT INTO t VALUES (1)"));
        Assert.Equal("table t may not be modified", e.Message);
        e = Assert.Throws<OneLiteException>(() => Q(conn, "CREATE VIRTUAL TABLE u USING nosuch"));
        Assert.Equal("no such module: nosuch", e.Message);
    }

    private sealed class ReadOnlyModule : IVirtualTableModule
    {
        public IVirtualTable Create(VirtualTableContext c) => Connect(c);

        public IVirtualTable Connect(VirtualTableContext c)
        {
            c.DeclareTable("CREATE TABLE x(a)");
            return new Ro();
        }

        private sealed class Ro : IVirtualTable
        {
            public void BestIndex(BestIndexInfo info)
            {
            }

            public IVirtualTableCursor Open() => throw new NotSupportedException();

            public void Dispose()
            {
            }
        }
    }
}
