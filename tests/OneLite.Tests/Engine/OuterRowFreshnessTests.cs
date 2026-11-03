using System.Text;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// A SELECT that has produced a row keeps reading its tables while the same connection updates them between steps. The rows not reached yet
/// show the update, and a row deleted under the cursor reads as no row. Expected rows were recorded from the pinned library 3.53.4 (step a join once,
/// run the statement on the same connection, step to the end). Joins the reference answers from an automatic index are not covered: it
/// copies the inner table once, OneLite scans it.
/// </summary>
public sealed class OuterRowFreshnessTests
{
    private const string Setup = "create table t(a,b); insert into t values(1,2),(3,4),(5,6); create table u(x,y); insert into u values(1,10),(3,20),(5,30);";

    private static void RunAll(Connection c, string sql)
    {
        string rest = sql;
        while (!string.IsNullOrWhiteSpace(rest))
        {
            using Statement st = c.Prepare(rest);
            rest = st.Tail;
            while (st.Step())
            {
            }
        }
    }

    private static string SteppedWithUpdate(string select, string update)
    {
        using Database db = Database.Open(":memory:");
        using Connection conn = db.Connect();
        RunAll(conn, Setup);
        var o = new StringBuilder();
        using Statement st = conn.Prepare(select);
        int n = 0;
        while (st.Step())
        {
            for (int i = 0; i < st.ColumnCount; i++)
            {
                o.Append(i > 0 ? "|" : string.Empty).Append(Encoding.UTF8.GetString(st.GetTextUtf8(i)));
            }

            o.Append('\n');
            if (++n == 1)
            {
                RunAll(conn, update);
            }
        }

        return o.ToString().TrimEnd('\n');
    }

    [Theory]
    [InlineData("select * from t, u", "update t set b=b+100", "1|2|1|10\n1|102|3|20\n1|102|5|30\n3|104|1|10\n3|104|3|20\n3|104|5|30\n5|106|1|10\n5|106|3|20\n5|106|5|30")]
    [InlineData("select * from t, u", "update t set a=a+1", "1|2|1|10\n2|2|3|20\n2|2|5|30\n4|4|1|10\n4|4|3|20\n4|4|5|30\n6|6|1|10\n6|6|3|20\n6|6|5|30")]
    [InlineData("select * from t, u", "update t set a=a+1, b=b+1", "1|2|1|10\n2|3|3|20\n2|3|5|30\n4|5|1|10\n4|5|3|20\n4|5|5|30\n6|7|1|10\n6|7|3|20\n6|7|5|30")]
    [InlineData("select * from t, u", "delete from t where a=3", "1|2|1|10\n1|2|3|20\n1|2|5|30\n5|6|1|10\n5|6|3|20\n5|6|5|30")]
    [InlineData("select * from t, u", "insert into t values(9,9)", "1|2|1|10\n1|2|3|20\n1|2|5|30\n3|4|1|10\n3|4|3|20\n3|4|5|30\n5|6|1|10\n5|6|3|20\n5|6|5|30\n9|9|1|10\n9|9|3|20\n9|9|5|30")]
    [InlineData("select * from t, u", "update u set y=y+1", "1|2|1|10\n1|2|3|21\n1|2|5|31\n3|4|1|11\n3|4|3|21\n3|4|5|31\n5|6|1|11\n5|6|3|21\n5|6|5|31")]
    [InlineData("select t.b, u.y from t, u", "update t set b=b+100", "2|10\n102|20\n102|30\n104|10\n104|20\n104|30\n106|10\n106|20\n106|30")]
    [InlineData("select t.b, u.y from t, u", "update t set a=a+1", "2|10\n2|20\n2|30\n4|10\n4|20\n4|30\n6|10\n6|20\n6|30")]
    [InlineData("select t.b, u.y from t, u", "update t set a=a+1, b=b+1", "2|10\n3|20\n3|30\n5|10\n5|20\n5|30\n7|10\n7|20\n7|30")]
    [InlineData("select t.b, u.y from t, u", "delete from t where a=3", "2|10\n2|20\n2|30\n6|10\n6|20\n6|30")]
    [InlineData("select t.b, u.y from t, u", "insert into t values(9,9)", "2|10\n2|20\n2|30\n4|10\n4|20\n4|30\n6|10\n6|20\n6|30\n9|10\n9|20\n9|30")]
    [InlineData("select t.b, u.y from t, u", "update u set y=y+1", "2|10\n2|21\n2|31\n4|11\n4|21\n4|31\n6|11\n6|21\n6|31")]
    [InlineData("select u.y, t.a, t.b from u, t", "update t set b=b+100", "10|1|2\n10|3|104\n10|5|106\n20|1|102\n20|3|104\n20|5|106\n30|1|102\n30|3|104\n30|5|106")]
    [InlineData("select u.y, t.a, t.b from u, t", "update t set a=a+1", "10|1|2\n10|4|4\n10|6|6\n20|2|2\n20|4|4\n20|6|6\n30|2|2\n30|4|4\n30|6|6")]
    [InlineData("select u.y, t.a, t.b from u, t", "update t set a=a+1, b=b+1", "10|1|2\n10|4|5\n10|6|7\n20|2|3\n20|4|5\n20|6|7\n30|2|3\n30|4|5\n30|6|7")]
    [InlineData("select u.y, t.a, t.b from u, t", "delete from t where a=3", "10|1|2\n10|5|6\n20|1|2\n20|5|6\n30|1|2\n30|5|6")]
    [InlineData("select u.y, t.a, t.b from u, t", "insert into t values(9,9)", "10|1|2\n10|3|4\n10|5|6\n10|9|9\n20|1|2\n20|3|4\n20|5|6\n20|9|9\n30|1|2\n30|3|4\n30|5|6\n30|9|9")]
    [InlineData("select u.y, t.a, t.b from u, t", "update u set y=y+1", "10|1|2\n11|3|4\n11|5|6\n21|1|2\n21|3|4\n21|5|6\n31|1|2\n31|3|4\n31|5|6")]
    [InlineData("select * from t, u where t.a<6", "update t set b=b+100", "1|2|1|10\n1|102|3|20\n1|102|5|30\n3|104|1|10\n3|104|3|20\n3|104|5|30\n5|106|1|10\n5|106|3|20\n5|106|5|30")]
    [InlineData("select * from t, u where t.a<6", "update t set a=a+1", "1|2|1|10\n2|2|3|20\n2|2|5|30\n4|4|1|10\n4|4|3|20\n4|4|5|30")]
    [InlineData("select * from t, u where t.a<6", "update t set a=a+1, b=b+1", "1|2|1|10\n2|3|3|20\n2|3|5|30\n4|5|1|10\n4|5|3|20\n4|5|5|30")]
    [InlineData("select * from t, u where t.a<6", "delete from t where a=3", "1|2|1|10\n1|2|3|20\n1|2|5|30\n5|6|1|10\n5|6|3|20\n5|6|5|30")]
    [InlineData("select * from t, u where t.a<6", "insert into t values(9,9)", "1|2|1|10\n1|2|3|20\n1|2|5|30\n3|4|1|10\n3|4|3|20\n3|4|5|30\n5|6|1|10\n5|6|3|20\n5|6|5|30")]
    [InlineData("select * from t, u where t.a<6", "update u set y=y+1", "1|2|1|10\n1|2|3|21\n1|2|5|31\n3|4|1|11\n3|4|3|21\n3|4|5|31\n5|6|1|11\n5|6|3|21\n5|6|5|31")]
    public void RowsNotReachedYetShowTheUpdate(string select, string update, string expected)
        => Assert.Equal(expected, SteppedWithUpdate(select, update));
}
