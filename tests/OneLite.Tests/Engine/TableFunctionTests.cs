using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RecursiveAI.OneLite.Oracle;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>Table-valued functions in FROM (json_each, json_tree, generate_series) and the JSON aggregates, with results written down from the pinned shell.</summary>
public sealed class TableFunctionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-tvf-" + Guid.NewGuid().ToString("N"));

    public TableFunctionTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string Run(string sql)
    {
        Assert.SkipUnless(File.Exists(OraclePaths.Cli), "the pinned sqlite3 shell is not installed");
        string path = Path.Combine(_dir, "t.db");
        if (!File.Exists(path))
        {
            SqliteCli.ApplyScript(path, "CREATE TABLE t(v, js); INSERT INTO t VALUES (5,'[7]'),(6,'[8,9]'),(7,NULL);");
        }

        using var db = Database.OpenReadOnly(path);
        using Connection conn = db.Connect();
        using Statement st = conn.Prepare(sql);
        var rows = new List<string>();
        while (st.Step())
        {
            var cells = new string[st.ColumnCount];
            for (int i = 0; i < cells.Length; i++)
            {
                cells[i] = st.IsNull(i) ? "NULL" : Encoding.UTF8.GetString(st.GetTextUtf8(i));
            }

            rows.Add(string.Join("|", cells));
        }

        return string.Join(";", rows);
    }

    [Theory]
    [InlineData("SELECT value FROM generate_series(1, 4)", "1;2;3;4")]
    [InlineData("SELECT value, rowid FROM generate_series(5, 1, -2)", "5|5;3|3;1|1")]
    [InlineData("SELECT value FROM generate_series WHERE start = 2 AND stop = 4", "2;3;4")]
    [InlineData("SELECT value FROM generate_series(1, 3) WHERE value > 1", "2;3")]
    [InlineData("SELECT key, value, type, fullkey FROM json_each('[5,{\"a\":2}]')", "0|5|integer|$[0];1|{\"a\":2}|object|$[1]")]
    [InlineData("SELECT key, id, parent FROM json_tree('{\"a\":[1]}')", "NULL|0|NULL;a|1|0;0|4|1")]
    [InlineData("SELECT key FROM json_each WHERE json = '[7,8]'", "0;1")]
    [InlineData("SELECT value FROM json_each('{\"a\":[1,2,3]}', '$.a') WHERE value > 1", "2;3")]
    [InlineData("SELECT t.v, e.value FROM t, json_each(t.js) e", "5|7;6|8;6|9")]
    [InlineData("SELECT t.v, e.value FROM t LEFT JOIN json_each(t.js) e", "5|7;6|8;6|9;7|NULL")]
    [InlineData("SELECT a.value, b.value FROM generate_series(1, 2) a, generate_series(a.value, 2) b", "1|1;1|2;2|2")]
    [InlineData("SELECT json_group_array(value) FROM json_each('[1,[2],\"x\"]')", "[1,[2],\"x\"]")]
    [InlineData("SELECT json_group_object(key, value) FROM json_each('{\"a\":1,\"b\":[2]}')", "{\"a\":1,\"b\":[2]}")]
    [InlineData("SELECT json_group_array(v) FROM (SELECT 1 AS v WHERE 0)", "[]")]
    [InlineData("SELECT hex(jsonb_group_array(v)) FROM t", "6B133513361337")]
    public void ReturnsTheShellsRows(string sql, string expected) => Assert.Equal(expected, Run(sql));

    [Theory]
    [InlineData("SELECT * FROM json_each('[1]', '$', 3)", "too many arguments on json_each() - max 2")]
    [InlineData("SELECT * FROM generate_series", "first argument to \"generate_series()\" missing or unusable")]
    [InlineData("SELECT * FROM json_each('[1')", "malformed JSON")]
    [InlineData("SELECT * FROM nosuchfn(1)", "no such table: nosuchfn")]
    public void ReportsTheShellsErrors(string sql, string message)
    {
        OneLiteException ex = Assert.Throws<OneLiteException>(() => Run(sql));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }
}
