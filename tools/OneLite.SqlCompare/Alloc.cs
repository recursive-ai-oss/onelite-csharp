using System;
using System.Collections.Generic;

namespace RecursiveAI.OneLite.SqlCompare;

/// <summary>Allocation per statement: the tokenizer alone (expected zero) and the whole parse (the tree and little else). Run with <c>alloc</c>.</summary>
internal static class Alloc
{
    private static readonly string[] Samples =
    [
        "select 1",
        "select a, b from t where a = 1",
        "select a, count(*) from t join u on t.id = u.id where t.x > 10 and u.y like 'a%' group by a having count(*) > 1 order by 2 desc limit 10",
        "insert into t(a, b) values (1, 'x')",
        "update t set a = a + 1 where b between 1 and 5",
        "delete from t where a in (1, 2, 3)",
        "create table t(id integer primary key, name text not null, v real default 0.5, unique(name))",
        "create index i on t(a, b)",
        "with c(x) as (select 1 union all select x+1 from c where x < 10) select sum(x) from c",
        "select a, sum(b) over (partition by c order by d rows between unbounded preceding and current row) from t",
    ];

    internal static int Run(string[] args)
    {
        int n = args.Length > 0 ? int.Parse(args[0]) : 20000;
        Console.WriteLine($"tokenizer and parser allocation per statement, warmed up, {n} repetitions");
        long totalLex = 0, totalParse = 0, totalChars = 0;
        foreach (string sql in Samples)
        {
            long lex = Measure(n, () => Lex(sql));
            long parse = Measure(n, () => _ = RecursiveAI.OneLite.Sql.Parsing.SqlParser.ParseOne(sql, 0, out _));
            totalLex += lex;
            totalParse += parse;
            totalChars += sql.Length;
            Console.WriteLine($"  lex {lex,6} B  parse {parse,7} B  ({sql.Length,3} chars)  {Program.Escape(sql)}");
        }

        // a larger sample: generated statements that parse
        var gen = new Gen(42);
        var list = new List<string>();
        while (list.Count < 2000)
        {
            string s = gen.Next(out _, out bool mutated);
            if (!mutated && s.Length < 600)
            {
                try
                {
                    _ = RecursiveAI.OneLite.Sql.Parsing.SqlParser.ParseOne(s, 0, out _);
                    list.Add(s);
                }
                catch (RecursiveAI.OneLite.OneLiteException)
                {
                }
            }
        }

        long lexAll = Measure(5, () => { foreach (string s in list) { Lex(s); } });
        long parseAll = Measure(5, () => { foreach (string s in list) { _ = RecursiveAI.OneLite.Sql.Parsing.SqlParser.ParseOne(s, 0, out _); } });
        long chars = 0;
        foreach (string s in list)
        {
            chars += s.Length;
        }

        Console.WriteLine($"  generated sample of {list.Count} statements, {chars / list.Count} chars on average: lex {lexAll / list.Count} B per statement, parse {parseAll / list.Count} B per statement ({parseAll / (double)chars:F1} B per source char)");
        return lexAll == 0 ? 0 : 1;
    }

    private static void Lex(string sql)
    {
        int pos = 0;
        for (; ; )
        {
            RecursiveAI.OneLite.Sql.Parsing.Token t = RecursiveAI.OneLite.Sql.Parsing.Lexer.Next(sql, pos);
            if (t.Kind == RecursiveAI.OneLite.Sql.Parsing.TokenKind.Eof)
            {
                return;
            }

            pos = t.End;
        }
    }

    // bytes allocated by one call of the action, averaged, after warm-up
    private static long Measure(int reps, Action a)
    {
        for (int i = 0; i < 200; i++)
        {
            a();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < reps; i++)
        {
            a();
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / reps;
    }
}
