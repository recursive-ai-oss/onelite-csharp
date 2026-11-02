using System;
using System.Text;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>
/// DEFAULT clauses as the pinned sqlite3 3.53.4 treats them (lang_createtable): a bare name is text, TRUE and FALSE are 1 and 0, a
/// parenthesized expression may call functions but not read columns. Expected values were recorded from the shell.
/// </summary>
public sealed class ColumnDefaultTests
{
    internal static string Run(string script)
    {
        using Database db = Database.Open(":memory:");
        using Connection conn = db.Connect();
        var o = new StringBuilder();
        string rest = script;
        try
        {
            while (!string.IsNullOrWhiteSpace(rest))
            {
                using Statement st = conn.Prepare(rest);
                rest = st.Tail;
                while (st.Step())
                {
                    for (int i = 0; i < st.ColumnCount; i++)
                    {
                        o.Append(i > 0 ? "|" : string.Empty).Append(Encoding.UTF8.GetString(st.GetTextUtf8(i)));
                    }

                    o.Append('\n');
                }
            }
        }
        catch (OneLiteException e)
        {
            o.Append("Error: ").Append(e.Message);
        }

        return o.ToString().TrimEnd('\n');
    }

    [Fact]
    public void BareNamesAreTextAndTrueFalseAreIntegers()
    {
        Assert.Equal("x_|text|abc|1|0||qq|br|bq|1|1",
            Run("create table t(a DEFAULT x_, b DEFAULT abc, c DEFAULT true, d default false, e default null, g DEFAULT \"qq\", h DEFAULT [br], i default `bq`, j default TRUE, k default tRuE);"
                + "insert into t default values; select a, typeof(a), b, c, d, e, g, h, i, j, k from t"));
    }

    [Fact]
    public void DefaultValuesFillEveryColumnFromItsDefault()
    {
        Assert.Equal("|3|x", Run("create table t(a, b DEFAULT 3, c default 'x'); insert into t default values; select * from t"));
    }

    [Theory]
    [InlineData("(a)")]
    [InlineData("(x_)")]
    [InlineData("(1+x_)")]
    [InlineData("(\"qq\")")]
    [InlineData("(abs(x_))")]
    [InlineData("(?)")]
    [InlineData("(:a)")]
    [InlineData("(exists(select 1))")]
    [InlineData("(1 in (select 1))")]
    public void ParenthesizedDefaultsThatReadColumnsOrParametersAreRefused(string def)
        => Assert.Equal("Error: default value of column [b] is not constant", Run("create table t(a, b DEFAULT " + def + ")"));

    [Theory]
    [InlineData("(1+1)", "integer|2")]
    [InlineData("(abs(-3))", "integer|3")]
    [InlineData("(true)", "integer|1")]
    [InlineData("(cast(1 as text))", "text|1")]
    [InlineData("(1 is true)", "integer|1")]
    [InlineData("(case when 1 then 2 end)", "integer|2")]
    public void ParenthesizedConstantExpressionsAreAccepted(string def, string expected)
        => Assert.Equal(expected, Run("create table t(a, b DEFAULT " + def + "); insert into t(a) values(1); select typeof(b), b from t"));

    [Fact]
    public void ExpressionDefaultsTableCanBeReadWithoutRows()
        => Assert.Equal("0", Run("create table t(a, b DEFAULT (1+1), c default (abs(1))); select count(*) from t"));
}
