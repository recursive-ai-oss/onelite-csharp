using System;
using System.Collections.Generic;
using System.Text;
using RecursiveAI.OneLite.Sql;
using RecursiveAI.OneLite.Sql.Parsing;

namespace RecursiveAI.OneLite.SqlCompare;

/// <summary>The outcome of our parser on one statement text.</summary>
internal readonly record struct OursResult(bool Accepted, int Offset, string Message, int TailBytes, Sql.Statement? Statement);

/// <summary>Runs one statement text through the OneLite parser and the pinned library and compares them.</summary>
public sealed class Checker : IDisposable
{
    private readonly Prepared _native = new();
    private readonly ParserOptions _options = ParserOptions.Default;

    /// <summary>Counts per native class: prepares, syntax, other.</summary>
    public long[] NativeCounts { get; } = new long[3];

    /// <summary>Statements skipped because an early grammar action (an object that already exists, an unknown database) ended the native parse before a later syntax error could show.</summary>
    public long Masked { get; private set; }

    /// <summary>
    /// The library runs grammar actions while it parses, so an action error (a name that already exists, an invalid join type, ...) can end
    /// the parse before a later syntax error is reached; its tail offset then lies before our error. A statement is masked when the native
    /// parse stopped at an action error that lies at or before the token where we report a syntax error.
    /// </summary>
    private static bool EndedBeforeOurError(PrepResult n, OursResult o, string sql)
    {
        if (n.Class != PrepClass.Other || o.Accepted || n.TailOffset < 0)
        {
            return false;
        }

        if (o.Message == "incomplete input" || o.Offset < 0)
        {
            return n.TailOffset <= Encoding.UTF8.GetByteCount(sql);
        }

        if (!o.Message.EndsWith("syntax error", StringComparison.Ordinal))
        {
            // a variable error is reported at the start of the variable, which native has already read when its own action fires
            int slack = ParseTimeMessages.Is(o.Message) ? 40 : 0;
            return o.Offset + slack >= n.TailOffset;
        }

        int len = 0;
        int q = o.Message.IndexOf('"', StringComparison.Ordinal);
        if (q >= 0)
        {
            len = Encoding.UTF8.GetByteCount(o.Message.AsSpan(q + 1, o.Message.LastIndexOf('"') - q - 1));
        }

        return o.Offset + len >= n.TailOffset;
    }

    /// <summary>When set, every accepted statement is written back as SQL and re-parsed, and the trees must be equal.</summary>
    public bool RoundTrip { get; set; }

    /// <summary>Statements that passed the round trip.</summary>
    public long RoundTrips { get; private set; }

    private string? CheckRoundTrip(Sql.Statement s, string sql)
    {
        string text;
        try
        {
            text = SqlWriter.Write(s);
        }
        catch (Exception e)
        {
            return "writer failed: " + e.Message;
        }

        try
        {
            Sql.Statement? s2 = SqlParser.ParseOne(text, 0, out _, _options);
            if (s2 == null)
            {
                return "re-parse gave nothing for: " + text;
            }

            string a = AstDump.Of(s);
            string b = AstDump.Of(s2);
            if (a != b)
            {
                int d = 0;
                while (d < a.Length && d < b.Length && a[d] == b[d])
                {
                    d++;
                }

                int from = Math.Max(0, d - 80);
                return "trees differ for text: " + text + "\n    first:  ..." + a.Substring(from, Math.Min(a.Length - from, 200)) + "\n    second: ..." + b.Substring(from, Math.Min(b.Length - from, 200));
            }

            string again = SqlWriter.Write(s2);
            return again == text ? null : "text not stable: " + text + " -> " + again;
        }
        catch (OneLiteException e) when (e.Message == "too many SQL variables")
        {
            // `LIMIT :a, ?32766` is stored (and written) as `LIMIT ?32766 OFFSET :a`, which numbers the variables in the other order.
            return null;
        }
        catch (OneLiteException e)
        {
            return "re-parse failed (" + e.Message + ") for: " + text;
        }
    }

    /// <summary>The native class and our verdict for the statement last compared.</summary>
    internal PrepClass LastNative { get; private set; }

    /// <summary>Whether our parser accepted the statement last compared.</summary>
    internal bool LastAccepted { get; private set; }

    /// <summary>Statements whose parenthesised spelling evaluated to the same row as the original.</summary>
    public long SemanticChecks { get; private set; }

    /// <summary>
    /// Evaluates a literal-only SELECT as written and with every operator parenthesised by the writer; the pinned library must give the same row
    /// (or the same error), which shows our tree groups the operators the way the library does. Returns null when they agree.
    /// </summary>
    public string? CompareSemantics(string sql)
    {
        OursResult o = Ours(sql);
        if (!o.Accepted || o.Statement == null)
        {
            return null;
        }

        string spelled = SqlWriter.Write(o.Statement, parenthesize: true);
        string a = _native.Eval(sql);
        string b = _native.Eval(spelled);
        SemanticChecks++;
        // Which of two resolution errors comes first depends on the order the library walks the tree, so two errors count as the same outcome.
        if (a == b || a.StartsWith("error:", StringComparison.Ordinal) && b.StartsWith("error:", StringComparison.Ordinal))
        {
            return null;
        }

        Mismatches++;
        return $"[SEMANTIC] {sql}\n    native: {a}\n    ours:   {b} for {spelled}";
    }

    /// <summary>Number of mismatches found.</summary>
    public long Mismatches { get; private set; }

    /// <summary>Our parse of the first statement in the text.</summary>
    internal OursResult Ours(string sql)
    {
        try
        {
            Sql.Statement? s = SqlParser.ParseOne(sql, 0, out int end, _options);
            return new OursResult(true, -1, "", Encoding.UTF8.GetByteCount(sql.AsSpan(0, Math.Min(end, sql.Length))), s);
        }
        catch (OneLiteException e)
        {
            return new OursResult(false, e.ErrorOffset, e.Message, -1, null);
        }
    }

    /// <summary>Returns null when ours and the library agree, else a description of the difference.</summary>
    public string? Compare(string sql)
    {
        PrepResult n = _native.Prepare(sql);
        NativeCounts[(int)n.Class]++;
        OursResult o = Ours(sql);
        LastNative = n.Class;
        LastAccepted = o.Accepted;
        if (n.Class == PrepClass.Other && !o.Accepted && EndedBeforeOurError(n, o, sql))
        {
            Masked++;
            return null;
        }

        string? kind = null;
        if (n.Class != PrepClass.Syntax)
        {
            if (!o.Accepted)
            {
                kind = "REJECTED-VALID";
            }
            else if (n.Class == PrepClass.Prepares && o.TailBytes != n.TailOffset)
            {
                kind = "TAIL";
            }
        }
        else if (o.Accepted)
        {
            kind = "ACCEPTED-INVALID";
        }
        else if (o.Message != n.Message || o.Offset != n.Offset)
        {
            kind = "ERROR-TEXT";
        }

        if (kind == null && RoundTrip && o.Accepted && o.Statement != null)
        {
            string? rt = CheckRoundTrip(o.Statement, sql);
            if (rt != null)
            {
                Mismatches++;
                return $"[ROUNDTRIP] {Program.Escape(sql)}\n    {rt}";
            }

            RoundTrips++;
        }

        if (kind == null)
        {
            return null;
        }

        Mismatches++;
        string nat = $"{n.Class} off={n.Offset} tail={n.TailOffset} {n.Message}";
        string our = o.Accepted ? $"accepted tail={o.TailBytes}" : $"off={o.Offset} {o.Message}";
        return $"[{kind}] {Program.Escape(sql)}\n    native: {nat}\n    ours:   {our}";
    }

    /// <inheritdoc/>
    public void Dispose() => _native.Dispose();
}
