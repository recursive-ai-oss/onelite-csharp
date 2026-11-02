using System;
using System.Collections.Generic;
using System.Text;

namespace RecursiveAI.OneLite.SqlCompare;

/// <summary>
/// Finds, for several ways of nesting, the smallest nesting that the pinned library refuses with a limit error, and the same for our parser,
/// so the limits can be set to the library's. Run with <c>depth</c>.
/// </summary>
internal static class Depth
{
    private static string Rep(string s, int n) => new StringBuilder(s.Length * n).Insert(0, s, n).ToString();

    private static readonly (string Name, Func<int, string> Make)[] Families =
    [
        ("parens", n => "select " + Rep("(", n) + "1" + Rep(")", n)),
        ("calls", n => "select " + Rep("f(", n) + "1" + Rep(")", n)),
        ("calls-distinct", n => "select " + Rep("f(distinct ", n) + "1" + Rep(")", n)),
        ("unary-minus", n => "select " + Rep("-", n) + "1"),
        ("unary-minus-spaced", n => "select " + Rep("- ", n) + "1"),
        ("not-chain", n => "select " + Rep("not ", n) + "1"),
        ("bitnot", n => "select " + Rep("~", n) + "1"),
        ("scalar-subquery", n => "select " + Rep("(select ", n) + "1" + Rep(")", n)),
        ("from-subquery", n => "select * from " + Rep("(select * from ", n) + "t" + Rep(")", n)),
        ("from-parens", n => "select * from " + Rep("(", n) + "t" + Rep(")", n)),
        ("case", n => "select " + Rep("case when 1 then ", n) + "1" + Rep(" end", n)),
        ("case-operand", n => "select " + Rep("case ", n) + "1" + Rep(" when 1 then 1 end", n)),
        ("cast", n => "select " + Rep("cast(", n) + "1" + Rep(" as int)", n)),
        ("left-chain-plus", n => "select 1" + Rep("+1", n)),
        ("left-chain-and", n => "select 1" + Rep(" and 1", n)),
        ("left-chain-concat", n => "select 1" + Rep("||1", n)),
        ("right-parens-chain", n => "select " + Rep("1+(", n) + "1" + Rep(")", n)),
        ("in-nest", n => "select " + Rep("1 in (", n) + "1" + Rep(")", n)),
        ("between-nest", n => "select " + Rep("1 between ", n) + "1" + Rep(" and 1", n)),
        ("exists-nest", n => "select " + Rep("exists(select ", n) + "1" + Rep(")", n)),
        ("cte-nest", n => Rep("with a as (", n) + "select 1" + Rep(") select 1", n)),
        ("compound", n => "select 1" + Rep(" union select 1", n)),
        ("values-rows", n => "values (1)" + Rep(",(1)", n)),
        
        ("window-call", n => "select " + Rep("f(1) over (partition by ", n) + "1" + Rep(")", n)),
        ("row-value", n => "select " + Rep("(1,", n) + "1" + Rep(")", n)),
        ("collate-chain", n => "select 1" + Rep(" collate x", n)),
        ("isnull-chain", n => "select 1" + Rep(" isnull", n)),
        ("join-chain", n => "select * from t" + Rep(" join t", n)),
        ("subq+chain0", n => "select " + Rep("(select ", n) + "1" + Rep("+1", 0) + Rep(")", n)),
        ("subq+chain1", n => "select " + Rep("(select ", n) + "1" + Rep("+1", 1) + Rep(")", n)),
        ("subq+chain2", n => "select " + Rep("(select ", n) + "1" + Rep("+1", 2) + Rep(")", n)),
        ("subq+chain5", n => "select " + Rep("(select ", n) + "1" + Rep("+1", 5) + Rep(")", n)),
        ("subq+chain10", n => "select " + Rep("(select ", n) + "1" + Rep("+1", 10) + Rep(")", n)),
        ("subq+chain20", n => "select " + Rep("(select ", n) + "1" + Rep("+1", 20) + Rep(")", n)),
        ("subq+chain100", n => "select " + Rep("(select ", n) + "1" + Rep("+1", 100) + Rep(")", n)),
        ("subq+chain800", n => "select " + Rep("(select ", n) + "1" + Rep("+1", 800) + Rep(")", n)),
        ("subq+chain990", n => "select " + Rep("(select ", n) + "1" + Rep("+1", 990) + Rep(")", n)),
        ("subq+chain50", n => "select " + Rep("(select ", n) + "1" + Rep("+1", 50) + Rep(")", n)),
        ("subq+chain200", n => "select " + Rep("(select ", n) + "1" + Rep("+1", 200) + Rep(")", n)),
        ("subq+chain400", n => "select " + Rep("(select ", n) + "1" + Rep("+1", 400) + Rep(")", n)),
        ("subq-where", n => "select " + Rep("(select 1 where ", n) + "1" + Rep(")", n)),
        ("subq-where-in-col", n => "select " + Rep("(select 1+", n) + "1" + Rep(")", n)),
        ("subq-from-first", n => "select " + Rep("(select * from t where ", n) + "1" + Rep(")", n)),
        ("in-subquery", n => "select " + Rep("1 in (select ", n) + "1" + Rep(")", n)),
        ("subq-union", n => "select " + Rep("(select 1 union select ", n) + "1" + Rep(")", n)),
        ("subq-orderby", n => "select " + Rep("(select 1 order by ", n) + "1" + Rep(")", n)),
        ("subq-limit", n => "select " + Rep("(select 1 limit ", n) + "1" + Rep(")", n)),
        ("subq-groupby", n => "select " + Rep("(select 1 group by ", n) + "1" + Rep(")", n)),
        ("subq-having", n => "select 1 from t " + Rep("group by (select 1 from t having ", n) + "1" + Rep(")", n)),
        ("subq-in-from-where", n => "select 1 from t where " + Rep("(select 1 from t where ", n) + "1" + Rep(")", n)),
        ("subq-join-on", n => "select 1 from t join t on " + Rep("(select 1 from t join t on ", n) + "1" + Rep(")", n)),
        ("join-right-parens", n => "select 1 from t join " + Rep("(t join ", n) + "t" + Rep(")", n)),
    ];

    internal static int Run(string[] args)
    {
        using var c = new Checker();
        using var p = new Prepared();
        int max = args.Length > 0 ? int.Parse(args[0]) : 6000;
        string only = args.Length > 1 ? args[1] : "";
        foreach ((string name, Func<int, string> make) in Families)
        {
            if (only.Length > 0 && name != only)
            {
                continue;
            }

            (int nn, string nm) = First(n => { PrepResult r = p.Prepare(make(n)); return IsLimit(r.Message) ? r.Message : null; }, max);
            (int on, string om) = First(n => { OursResult o = c.Ours(make(n)); return !o.Accepted && IsLimit(o.Message) ? o.Message : null; }, max);
            string flag = nn == on && nm == om ? "same" : "DIFFERENT";
            Console.WriteLine($"{name,-20} native {Show(nn, nm)}  ours {Show(on, om)}  {flag}");
        }

        return 0;
    }

    private static string Show(int n, string m) => n < 0 ? "none" : $"{n} ({m})";

    private static bool IsLimit(string m)
        => m == "Recursion limit" || m.StartsWith("Expression tree is too large", StringComparison.Ordinal) || m.StartsWith("too many terms", StringComparison.Ordinal)
        || m == "statement too long" || m.StartsWith("parser stack overflow", StringComparison.Ordinal);

    // Smallest n in 1..max whose outcome is a limit error, found by bisection (the outcome is monotone in n).
    private static (int, string) First(Func<int, string?> limit, int max)
    {
        string? hi = limit(max);
        if (hi == null)
        {
            return (-1, "");
        }

        int lo = 0, h = max;
        string msg = hi;
        while (h - lo > 1)
        {
            int mid = (lo + h) / 2;
            string? r = limit(mid);
            if (r != null)
            {
                h = mid;
                msg = r;
            }
            else
            {
                lo = mid;
            }
        }

        return (h, msg);
    }
}
