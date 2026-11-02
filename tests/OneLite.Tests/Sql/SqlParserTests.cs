using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection;
using System.Threading;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Sql.Parsing;

namespace RecursiveAI.OneLite.Tests.Sql;

using Statement = RecursiveAI.OneLite.Sql.Statement;

/// <summary>
/// The parse results of statements the pinned sqlite3 3.53.4 was asked about: acceptance, the error text and the byte offset
/// <c>sqlite3_error_offset</c> gives. Each case was checked against the library by the comparer in tools/OneLite.SqlCompare.
/// </summary>
public sealed class SqlParserTests
{
    private static OneLiteException Fails(string sql)
        => Assert.Throws<OneLiteException>(() => SqlParser.ParseOne(sql, 0, out _));

    [Theory]
    [InlineData("select from", "near \"from\": syntax error", 7)]
    [InlineData("select 1 from", "incomplete input", -1)]
    [InlineData("select 'abc", "unrecognized token: \"'abc\"", 7)]
    [InlineData("select 1 /*", "near \"*\": syntax error", 10)]            // a lone "/*" at the end is a slash and a star
    [InlineData("RELEASE SAVEPOINT /*", "near \"/\": syntax error", 18)]
    [InlineData("select cast.*", "near \".\": syntax error", 11)]           // CAST starts an expression of its own
    [InlineData("create table x(a) ,", "incomplete input", -1)]
    [InlineData("create table x(a) , , strict", "near \",\": syntax error", 20)]
    [InlineData("create table x(t (19))", "near \"(\": syntax error", 17)]
    [InlineData("select * from t window filter as ()", "near \"filter\": syntax error", 23)] // FILTER cannot name a window
    [InlineData("select count(*) over filter", "near \"filter\": syntax error", 21)]
    [InlineData("select 1 between 2 or 3 and 4 and 5", "incomplete input", -1)]    // the lower bound of BETWEEN takes OR (3 AND 4 ...)
    [InlineData("select #1", "near \"#1\": syntax error", 7)]
    [InlineData("select #", "unrecognized token: \"#\"", 7)]
    [InlineData("select 1\u000b+\u000b1", "unrecognized token: \"\u000b\"", 8)]   // a vertical tab is a blank only inside a run of blanks
    [InlineData("select f(* order by 1)", "near \"order\": syntax error", 11)]
    [InlineData("select 1 foo bar", "near \"bar\": syntax error", 13)]
    [InlineData("select ?32767", "variable number must be between ?1 and ?32766", 7)]
    [InlineData("with c(a collate x) as (select 1) select * from c", "syntax error after column name \"a\"", -1)] // the grammar's action rejects what its rule parsed
    [InlineData("create view v(a asc, b) as select 1, 2", "syntax error after column name \"a\"", -1)]
    [InlineData("with c(a collate x b) as (select 1) select * from c", "near \"b\": syntax error", 19)]
    [InlineData("pragma key = -1_000", "near \"1_000\": syntax error", 14)]       // separators make a number the pragma value rule rejects
    public void ErrorsMatchTheLibrary(string sql, string message, int offset)
    {
        OneLiteException e = Fails(sql);
        Assert.Equal(message, e.Message);
        Assert.Equal(offset, e.ErrorOffset);
        Assert.Equal(1, ResultCode.Error);
    }

    [Theory]
    [InlineData("select 1 between 0 and 2 = 1")]
    [InlineData("select 2 between 1 in (1) and 3")]
    [InlineData("select f(order by 1)")]
    [InlineData("select f(distinct)")]
    [InlineData("create table x(a) , strict")]
    [InlineData("create table x(a int UNIQUE generated always as (1))")]
    [InlineData("create table x(t generated(19))")]
    [InlineData("select 1 from t window w as (), filter as ()")]
    [InlineData("select #a")]
    [InlineData("select if from if")]
    [InlineData("create table x(a native character generated always as (1) as (2) stored as (3))")] // the words go to the first AS only
    [InlineData("select 1 /* unterminated")]
    [InlineData("select 'a' like 'b' escape 'x' < 1.5")]
    [InlineData("; ; select 1")]
    public void AcceptsWhatTheLibraryAccepts(string sql)
    {
        Statement? s = SqlParser.ParseOne(sql, 0, out _);
        Assert.NotNull(s);
    }

    [Fact]
    public void BetweenBoundsFollowTheLibrary()
    {
        // `1 BETWEEN 0 AND 2 = 1` is (1 BETWEEN 0 AND 2) = 1, not 1 BETWEEN 0 AND (2 = 1)
        var s = (SelectStatement)SqlParser.ParseOne("select 1 between 0 and 2 = 1", 0, out _)!;
        var col = (ExprColumn)((SimpleSelect)s.Cores[0]).Columns[0];
        var eq = Assert.IsType<BinaryExpr>(col.Expr);
        Assert.Equal(BinaryOp.Eq, eq.Op);
        Assert.IsType<BetweenExpr>(eq.Left);
    }

    [Fact]
    public void LeadingSemicolonsAreSkippedLikePrepareDoes()
    {
        Statement? s = SqlParser.ParseOne("; ; select 1; select 2", 0, out int end);
        Assert.IsType<SelectStatement>(s);
        Assert.Equal("; ; select 1;".Length, end);
    }

    [Fact]
    public void TailOffsetStopsAtNul()
    {
        Statement? s = SqlParser.ParseOne("select 1 \0junk", 0, out int end);
        Assert.NotNull(s);
        Assert.Equal(9, end);
    }

    [Fact]
    public void BeginConcurrentNeedsTheOption()
    {
        OneLiteException e = Fails("begin concurrent");
        Assert.Equal("near \"concurrent\": syntax error", e.Message);
        Statement? s = SqlParser.ParseOne("begin concurrent transaction t", 0, out _, new ParserOptions { BeginConcurrent = true });
        var b = Assert.IsType<BeginStatement>(s);
        Assert.Equal(BeginMode.Concurrent, b.Mode);
    }

    [Fact]
    public void KeepsTheCreateTextAndSpans()
    {
        const string Sql = "CREATE TABLE  t ( a INTEGER PRIMARY KEY,b TEXT ) STRICT";
        var c = (CreateTableStatement)SqlParser.ParseOne(Sql, 0, out _)!;
        Assert.Equal(0, c.Span.Start);
        Assert.Equal(Sql.Length, c.Span.End);
        Assert.Equal("a", c.Columns[0].Name.Text);
        Assert.Equal("a", Sql.Substring(c.Columns[0].Name.Span.Start, c.Columns[0].Name.Span.Length));
        Assert.True(c.Strict);
    }

    [Fact]
    public void ErrorOffsetCountsUtf8Bytes()
    {
        // "é" is two bytes in UTF-8, so the token after it starts at byte 12 though it is the 11th character
        OneLiteException e = Fails("select 'é' 1");
        Assert.Equal(12, e.ErrorOffset);
    }

    /// <summary>Every child node's span lies inside its parent's, and the spans of a statement's children are in order.</summary>
    [Theory]
    [InlineData("with r(n) as (select 1 union all select n+1 from r where n < 5) select n, sum(n) over (partition by n % 2 order by n rows between unbounded preceding and current row) from r where n in (1,2,3) order by 1 limit 3 offset 1")]
    [InlineData("insert into t(a,b) values (1,'x'),(2,'y') on conflict(a) do update set b = excluded.b where b <> 'q' returning *")]
    [InlineData("create table t(a integer primary key autoincrement, b text collate nocase default 'x' check (b <> ''), c as (a*2) stored, foreign key(a) references u(x) on delete cascade) without rowid")]
    [InlineData("create trigger tr after update of a on t for each row when new.a > old.a begin update u set b = new.b; delete from u where a = old.a; end")]
    [InlineData("select case when a then b else c end, cast(x as integer), (select 1), exists (select 2), a between 1 and 2, a like 'x' escape '\\' from t left join u using (id) where not a")]
    public void NodeSpansNestInsideTheirParents(string sql)
    {
        Statement s = SqlParser.ParseOne(sql, 0, out _)!;
        Assert.True(s.Span.Start >= 0 && s.Span.End <= sql.Length && s.Span.Start < s.Span.End);
        int checkedNodes = CheckSpans(s, s.Span, sql.Length);
        Assert.True(checkedNodes > 10);
    }

    private static int CheckSpans(object node, SourceSpan parent, int length)
    {
        int count = 0;
        foreach (PropertyInfo p in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || p.Name == "Span")
            {
                continue;
            }

            object? v = p.GetValue(node);
            foreach (SqlNode child in Children(v))
            {
                SourceSpan sp = child.Span;
                Assert.True(sp.Start >= parent.Start && sp.End <= parent.End && sp.Start <= sp.End, $"{child.GetType().Name} {sp} outside {parent} in {node.GetType().Name}.{p.Name}");
                count += 1 + CheckSpans(child, sp, length);
            }
        }

        return count;
    }

    private static IEnumerable<SqlNode> Children(object? v)
    {
        switch (v)
        {
            case SqlNode n:
                yield return n;
                break;
            case System.Collections.IEnumerable e when v is not string:
                foreach (object? item in e)
                {
                    foreach (SqlNode n in Children(item))
                    {
                        yield return n;
                    }
                }

                break;
        }
    }
}

/// <summary>Writing a tree back as SQL and parsing it again gives the same tree, for statements from every part of the grammar.</summary>
public sealed class SqlRoundTripTests
{
    public static IEnumerable<object[]> Statements() =>
    [
        ["select 1"],
        ["select a, b as c, t.*, * from t as x join u on x.a = u.a left join (select 1) using (a), v indexed by i where a is not distinct from b group by 1, 2 having count(*) > 1 window w as (order by a) order by a desc nulls last limit 5, 2"],
        ["values (1, 'a'), (2, x'ff')"],
        ["with recursive c(x) as materialized (select 1 union select x+1 from c) select * from c"],
        ["select 1 union all select 2 intersect select 3 except select 4"],
        ["select -a, ~b, not c, a || b, a -> 'k', a ->> 2, a % 3, a << 1 | 2 & 3, a collate nocase, a isnull, a notnull, a not null from t"],
        ["select a is null, a is not null, a is 1, a is not 1, a is distinct from b, a is not distinct from not b"],
        ["select a in (1,2), a not in (select 1), a in t, a in main.f(1), a between 1 and 2 and 3, a not like 'x%' escape '!', a glob 'x', a regexp 'x', a match 'x'"],
        ["select count(*), count(distinct a), group_concat(a, ',' order by a) filter (where a > 1) over (partition by b order by c range between 1 preceding and current row exclude ties), f(), f(order by 1)"],
        ["select 1, 1.5, 1e5, 0x10, 1_000, 'it''s', x'0a', null, true, false, current_time, ?, ?5, :n, @n, $n, #n"],
        ["insert or replace into t default values"],
        ["replace into main.t(a) select 1 on conflict do nothing returning a as z"],
        ["insert into t values (1) on conflict (a collate nocase desc) where a > 1 do update set (a, b) = (select 1, 2), c = 3 where c is null"],
        ["update or fail t as x set a = 1, (b, c) = (2, 3) from u where x.a = u.a returning *"],
        ["delete from main.t indexed by i where a = 1 returning a"],
        ["create temp table if not exists main.t(a int not null on conflict abort, b text default (1+1) collate rtrim, c numeric(10,2) references u(x) deferrable initially deferred, d generated always as (a) virtual, primary key (a desc, b) on conflict replace, unique (c), check (a > 0), foreign key (a) references u) without rowid, strict"],
        ["create table t as select 1 as a"],
        ["create unique index if not exists i on t(a collate nocase desc, b+1) where a > 0"],
        ["create temp view if not exists v(a, b) as select 1, 2"],
        ["create trigger if not exists tr instead of insert on v for each row when new.a is not null begin insert into t values (new.a); select raise(ignore); end"],
        ["create virtual table if not exists m using fts5(a, b, tokenize = 'porter')"],
        ["alter table t rename to u"],
        ["alter table t rename column a to b"],
        ["alter table t add column c int default 0"],
        ["alter table t drop column c"],
        ["alter table t add constraint k check (a > 0) on conflict ignore"],
        ["alter table t alter column a set not null"],
        ["drop table if exists main.t"],
        ["drop trigger tr"],
        ["pragma main.journal_mode = delete"],
        ["pragma cache_size(-2000)"],
        ["pragma journal_mode = on"],
        ["begin deferred transaction x"],
        ["commit"],
        ["end transaction"],
        ["rollback to savepoint s"],
        ["savepoint s"],
        ["release s"],
        ["vacuum main into 'f.db'"],
        ["analyze main.t"],
        ["reindex nocase"],
        ["attach database 'f.db' as aux key 'k'"],
        ["detach aux"],
        ["explain query plan select 1"],
        ["explain insert into t values (1)"],
        ["select * from t window w as (partition by a), v as (w order by b)"],
        ["select cast(a as varchar(10)), cast(a as double precision), cast(a as) from t"],
        // IS [NOT] TRUE|FALSE is stored as (x AND 1) IS [NOT] 1|0 and must be written back as a truth test
        ["select 3 != 0 <= 0 % true = 1 || 3 is not true"],
        ["select cast(x'01' as integer) is true is - 1"],
        ["select not 2 is not true or t and r"],
        ["select a is false = 1 collate rtrim, b is not false between 1 and 2"],
        ["select (a or b) is true, (a and 1) is 1, a and 1 is 1"],
        // TRUE or FALSE followed by a tighter operator starts the right operand of IS
        ["select 5 is true % true, 0 is true + 0, 1 is not true * 5, 7 is false << 1, 6 is true < 2, 5 is true || 'a', 5 is true -> 1"],
        ["select 5 is true = 1, 5 is true between 0 and 1, 5 is true in (1), 5 is true collate binary, 5 is true isnull, 5 is not false and 1"],
        ["select case when null in (1.5 or 'B' is true % true not between ~ '' is not 7 and 1) then 1 end"],
        // a bare TRUE or FALSE after IS DISTINCT FROM is a value, not a truth test
        ["select 9 is distinct from true, 9 is not distinct from false, 9 is (true), 9 is not (tRUE), 9 is not distinct from tRUE"],
    ];

    [Theory]
    [MemberData(nameof(Statements))]
    public void WrittenTextParsesToTheSameTree(string sql)
    {
        Statement s = Parse(sql);
        string written = SqlWriter.Write(s);
        Statement again = Parse(written);
        Assert.Equal(Dump(s), Dump(again));
        Assert.Equal(written, SqlWriter.Write(again));
    }

    [Fact]
    public void ParenthesizedFormKeepsTheMeaningOfPrecedence()
    {
        Statement s = Parse("select 1 + 2 * 3 = 7 and not 0 or 1 between 0 and 2 = 1");
        string text = SqlWriter.Write(s, parenthesize: true);
        Assert.Contains("((1 + (2 * 3)) = 7)", text, StringComparison.Ordinal);
    }

    private static Statement Parse(string sql) => SqlParser.ParseOne(sql, 0, out _)!;

    private static string Dump(Statement s) => TreeDump.Of(s);
}

/// <summary>A span-free text form of a syntax tree for equality checks; reads record properties by reflection, which tests may do.</summary>
internal static class TreeDump
{
    internal static string Of(object? node)
    {
        var sb = new System.Text.StringBuilder();
        Write(sb, node);
        return sb.ToString();
    }

    private static void Write(System.Text.StringBuilder sb, object? v)
    {
        switch (v)
        {
            case null:
                sb.Append("null");
                return;
            case string s:
                sb.Append('"').Append(s).Append('"');
                return;
            case bool or int or long or Enum:
                sb.Append(v);
                return;
        }

        Type t = v.GetType();
        if (t.IsValueType && t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ImmutableArray<>) && (bool)t.GetProperty("IsDefault")!.GetValue(v)!)
        {
            sb.Append("default");
            return;
        }

        if (v is System.Collections.IEnumerable e)
        {
            sb.Append('[');
            foreach (object? item in e)
            {
                Write(sb, item);
                sb.Append(',');
            }

            sb.Append(']');
            return;
        }

        sb.Append(t.Name).Append('(');
        foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (p.Name is "Span" or "EqualityContract" or "SchemaSqlStart" or "WithoutRowid" or "Strict" || p.GetIndexParameters().Length > 0)
            {
                continue;
            }

            sb.Append(p.Name).Append('=');
            Write(sb, p.GetValue(v));
            sb.Append(';');
        }

        sb.Append(')');
    }
}

/// <summary>The nesting limits of the library: the parser stack ("Recursion limit"), expression height, compound terms.</summary>
public sealed class SqlLimitsTests
{
    private static string Rep(string s, int n) => new System.Text.StringBuilder().Insert(0, s, n).ToString();

    // The limits are exact only when the thread has the stack the deepest accepted input needs (about 2 MB); a smaller stack ends the
    // parse earlier, with the same error, because the parser checks the real stack too.
    private static string? Outcome(string sql, int stackKb = 64 * 1024)
    {
        string? result = null;
        var t = new Thread(() =>
        {
            try
            {
                _ = SqlParser.ParseOne(sql, 0, out _);
            }
            catch (OneLiteException e)
            {
                result = e.Message + "@" + e.ErrorOffset;
            }
        }, stackKb * 1024);
        t.Start();
        t.Join();
        return result;
    }

    [Theory]
    [InlineData(2493, null)]
    [InlineData(2494, "Recursion limit@-1")]
    public void ParenthesesNestUpTo2493(int n, string? expected)
        => Assert.Equal(expected, Outcome("select " + Rep("(", n) + "1" + Rep(")", n)));

    [Theory]
    [InlineData(831, null)]
    [InlineData(832, "Recursion limit@-1")]
    public void CallsNestUpTo831(int n, string? expected)
        => Assert.Equal(expected, Outcome("select " + Rep("f(", n) + "1" + Rep(")", n)));

    [Theory]
    [InlineData(415, null)]
    [InlineData(416, "Recursion limit@-1")]
    public void SubqueriesInFromNestUpTo415(int n, string? expected)
        => Assert.Equal(expected, Outcome("select * from " + Rep("(select * from ", n) + "t" + Rep(")", n)));

    [Theory]
    [InlineData(999, null)]
    [InlineData(1000, "Expression tree is too large (maximum depth 1000)@-1")]
    public void ExpressionHeightIsLimitedTo1000(int n, string? expected)
        => Assert.Equal(expected, Outcome("select 1" + Rep("+1", n)));

    [Theory]
    [InlineData(499, null)]
    [InlineData(500, "too many terms in compound SELECT@-1")]
    public void CompoundSelectHasAtMost500Terms(int n, string? expected)
        => Assert.Equal(expected, Outcome("select 1" + Rep(" union select 1", n)));

    [Theory]
    [InlineData("(")]
    [InlineData("f(")]
    [InlineData("(select ")]
    [InlineData("case when 1 then ")]
    [InlineData("- ")]
    [InlineData("1 in (")]
    [InlineData("select * from (")]
    public void VeryDeepInputEndsInALimitError(string unit)
    {
        string sql = (unit.StartsWith("select", StringComparison.Ordinal) ? "" : "select ") + Rep(unit, 200_000);
        string? r = Outcome(sql, 512); // a small stack proves the parser stops on its own and never overflows the real stack
        Assert.NotNull(r);
        Assert.True(r.StartsWith("Recursion limit", StringComparison.Ordinal) || r.StartsWith("Expression tree", StringComparison.Ordinal), r);
    }
}
