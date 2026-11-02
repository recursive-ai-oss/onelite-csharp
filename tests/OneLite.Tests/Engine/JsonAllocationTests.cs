using System;
using System.IO;
using System.Text;
using RecursiveAI.OneLite.Oracle;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// Reading JSON out of a stored column on a cached statement allocates nothing per row: the result goes into the statement's own
/// buffers and the caller reads it as a span.
/// </summary>
public sealed class JsonAllocationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-json-" + Guid.NewGuid().ToString("N"));

    public JsonAllocationTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    [Theory]
    [InlineData("json_extract(doc, '$.name')")]
    [InlineData("json_extract(doc, '$.tags[1]')")]
    [InlineData("json_extract(doc, '$.n')")]
    [InlineData("json_extract(doc, '$.o')")]
    [InlineData("doc ->> '$.name'")]
    [InlineData("doc ->> 'n'")]
    [InlineData("doc -> 'o'")]
    [InlineData("doc -> '$.tags'")]
    [InlineData("json_type(doc, '$.o')")]
    [InlineData("json_array_length(doc, '$.tags')")]
    [InlineData("json_valid(doc)")]
    [InlineData("json_extract(doc, '$.name', '$.n')")]
    public void CachedJsonReadsAllocateNothing(string expression)
    {
        Assert.SkipUnless(File.Exists(OraclePaths.Cli), "the pinned sqlite3 shell is not installed");
        string path = Path.Combine(_dir, "j.db");
        var sb = new StringBuilder();
        sb.Append("CREATE TABLE t(id INTEGER PRIMARY KEY, doc TEXT);");
        sb.Append("WITH RECURSIVE s(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM s WHERE n<2000) ");
        sb.Append("INSERT INTO t SELECT n, '{\"name\":\"row ' || n || '\",\"n\":' || n || ',\"tags\":[\"a\",\"b' || n || '\",3],\"o\":{\"k\":[1,2,{\"z\":null}]}}' FROM s;");
        SqliteCli.ApplyScript(path, sb.ToString());

        using var db = Database.OpenReadOnly(path, new DatabaseOptions { CacheSize = 10000 });
        using Connection conn = db.Connect();
        using Statement st = conn.Prepare("SELECT " + expression + " FROM t");

        long Body()
        {
            long sum = 0;
            st.Reset();
            while (st.Step())
            {
                sum += st.IsNull(0) ? 1 : st.ColumnType(0) is ColumnType.Text or ColumnType.Blob ? st.GetTextUtf8(0).Length : 1;
            }

            return sum;
        }

        Body();
        Body();
        long best = long.MaxValue;
        for (int rep = 0; rep < 3; rep++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.True(Body() != 0);
            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.True(best == 0, expression + " allocated " + best + " B over 2000 rows");
    }
}
