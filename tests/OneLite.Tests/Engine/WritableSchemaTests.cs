using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary><c>PRAGMA writable_schema</c>, the SQL text kept for CREATE INDEX and CREATE VIEW, and <c>table_info</c> of virtual tables.</summary>
public sealed class WritableSchemaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-wschema-" + Guid.NewGuid().ToString("N"));

    public WritableSchemaTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static void Run(Connection c, string sql)
    {
        using Statement st = c.Prepare(sql);
        while (st.Step())
        {
        }
    }

    private static List<string> Rows(Connection c, string sql, int cols = 1)
    {
        var rows = new List<string>();
        using Statement st = c.Prepare(sql);
        while (st.Step())
        {
            var parts = new string[cols];
            for (int i = 0; i < cols; i++)
            {
                parts[i] = st.GetString(i) ?? "NULL";
            }

            rows.Add(string.Join('|', parts));
        }

        return rows;
    }

    [Fact]
    public void SchemaTableIsWritableOnlyWhileThePragmaIsOn()
    {
        using var db = Database.Open(Path.Combine(_dir, "a.db"));
        using Connection c = db.Connect();
        Run(c, "CREATE TABLE a(i INTEGER PRIMARY KEY AUTOINCREMENT)");
        var e = Assert.Throws<OneLiteException>(() => Run(c, "INSERT INTO sqlite_schema(type,name,tbl_name,rootpage,sql) VALUES('table','z','z',0,'CREATE TABLE z(a)')"));
        Assert.Equal("table sqlite_master may not be modified", e.Message);
        e = Assert.Throws<OneLiteException>(() => Run(c, "CREATE TABLE sqlite_x(a)"));
        Assert.Equal("object name reserved for internal use: sqlite_x", e.Message);

        Run(c, "PRAGMA writable_schema=ON");
        Assert.Equal(["1"], Rows(c, "PRAGMA writable_schema"));
        Run(c, "CREATE TABLE IF NOT EXISTS sqlite_sequence(name,seq)");
        Run(c, "CREATE TABLE sqlite_x(a)");
        Run(c, "INSERT INTO sqlite_schema(type,name,tbl_name,rootpage,sql) VALUES('table','v','v',0,'CREATE VIRTUAL TABLE v USING rtree(id,a,b)')");
        Run(c, "CREATE TABLE IF NOT EXISTS \"v_rowid\"(rowid INTEGER PRIMARY KEY,nodeno)");
        Run(c, "CREATE TABLE IF NOT EXISTS \"v_node\"(nodeno INTEGER PRIMARY KEY,data)");
        Run(c, "CREATE TABLE IF NOT EXISTS \"v_parent\"(nodeno INTEGER PRIMARY KEY,parentnode)");
        Run(c, "PRAGMA writable_schema=OFF");

        // the hand-written row is a virtual table now
        Assert.Equal(["id", "a", "b"], Rows(c, "SELECT name FROM pragma_table_info('v')"));
        Assert.Equal(["v|0"], Rows(c, "SELECT name, rootpage FROM sqlite_schema WHERE name = 'v'", 2));
        Assert.Throws<OneLiteException>(() => Run(c, "CREATE TABLE sqlite_y(a)"));
    }

    [Fact]
    public void IndexAndViewKeepTheTextBeforeTheSemicolon()
    {
        using var db = Database.Open(Path.Combine(_dir, "b.db"));
        using Connection c = db.Connect();
        Run(c, "CREATE TABLE t(a,b)  ;");
        Run(c, "CREATE INDEX i1 ON t(a)  ;");
        Run(c, "CREATE INDEX i2 ON t(b) /* c */ ;");
        Run(c, "CREATE UNIQUE INDEX i3 ON t(a,b)\n;");
        Run(c, "CREATE VIEW v1 AS SELECT 1  ;");
        Run(c, "CREATE VIEW v2 AS SELECT 1 -- z\n;");
        Run(c, "CREATE INDEX i4 ON t(b)\t");
        Assert.Equal(
            ["CREATE TABLE t(a,b)", "CREATE INDEX i1 ON t(a)  ", "CREATE INDEX i2 ON t(b) /* c */ ", "CREATE UNIQUE INDEX i3 ON t(a,b)\n", "CREATE VIEW v1 AS SELECT 1", "CREATE VIEW v2 AS SELECT 1 -- z", "CREATE INDEX i4 ON t(b)\t"],
            Rows(c, "SELECT sql FROM sqlite_schema WHERE sql IS NOT NULL ORDER BY rowid"));
    }

    [Fact]
    public void TableInfoListsTheColumnsOfAVirtualTable()
    {
        using var db = Database.Open(Path.Combine(_dir, "c.db"));
        using Connection c = db.Connect();
        Run(c, "CREATE VIRTUAL TABLE r USING rtree(id,a,b)");
        Run(c, "CREATE VIRTUAL TABLE f USING fts5(x)");
        Assert.Equal(["0|id|INT", "1|a|REAL", "2|b|REAL"], Rows(c, "SELECT cid, name, type FROM pragma_table_info('r')", 3));
        Assert.Equal(["0|x"], Rows(c, "SELECT cid, name FROM pragma_table_info('f')", 2));
        Assert.Equal(["0|x|0", "1|f|1", "2|rank|1"], Rows(c, "SELECT cid, name, hidden FROM pragma_table_xinfo('f')", 3));
    }
}
