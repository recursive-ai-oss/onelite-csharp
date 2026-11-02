using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace RecursiveAI.OneLite.SqlLogicTest;

/// <summary>What a record asks the runner to do.</summary>
public enum RecordKind
{
    /// <summary><c>statement ok</c>: the SQL must succeed.</summary>
    StatementOk,

    /// <summary><c>statement error</c>: the SQL must fail.</summary>
    StatementError,

    /// <summary><c>query</c>: the SQL must return the listed (or hashed) values.</summary>
    Query,

    /// <summary><c>halt</c>: ignore the rest of the script.</summary>
    Halt,

    /// <summary><c>hash-threshold N</c>.</summary>
    HashThreshold,

    /// <summary>A record the parser could not understand; <see cref="Record.Problem"/> says why.</summary>
    Malformed,
}

/// <summary>How a query's result values are ordered before they are compared.</summary>
public enum SortMode
{
    /// <summary>The order the engine returned.</summary>
    NoSort,

    /// <summary>Rows sorted by their rendered text, column by column.</summary>
    RowSort,

    /// <summary>Every value sorted on its own, row grouping ignored.</summary>
    ValueSort,
}

/// <summary>One record of a script, with its conditionals and where it started.</summary>
public sealed class Record
{
    /// <summary>The record's kind.</summary>
    public RecordKind Kind { get; init; }

    /// <summary>1-based line of the record's first line after its conditionals (the <c>statement</c>/<c>query</c> line).</summary>
    public int Line { get; init; }

    /// <summary>The SQL text, lines joined by <c>\n</c>, comments removed. Empty for control records.</summary>
    public string Sql { get; init; } = "";

    /// <summary>The <c>skipif</c> engine names, in order.</summary>
    public List<string> SkipIf { get; } = [];

    /// <summary>The <c>onlyif</c> engine names, in order.</summary>
    public List<string> OnlyIf { get; } = [];

    /// <summary>Query: the type string (one of <c>I</c>, <c>T</c>, <c>R</c> per column).</summary>
    public string Types { get; init; } = "";

    /// <summary>Query: the sort mode.</summary>
    public SortMode Sort { get; init; }

    /// <summary>Query: the label, or null.</summary>
    public string? Label { get; init; }

    /// <summary>Query: the expected values, one per line after <c>----</c>. Empty when the result is hashed or empty.</summary>
    public List<string> Expected { get; } = [];

    /// <summary>Query: true when the record carries the hashed form (<c>N values hashing to H</c>).</summary>
    public bool Hashed { get; set; }

    /// <summary>Query, hashed form: the number of values.</summary>
    public int HashedCount { get; set; }

    /// <summary>Query, hashed form: the lower-case MD5 hex digest.</summary>
    public string HashedDigest { get; set; } = "";

    /// <summary>Statement error: the text after <c>----</c>, if any. Recorded, not compared.</summary>
    public List<string> ExpectedErrorText { get; } = [];

    /// <summary><c>hash-threshold</c>: the value.</summary>
    public int Threshold { get; init; }

    /// <summary>Malformed: what was wrong.</summary>
    public string Problem { get; init; } = "";

    /// <summary>True when the conditionals say this record does not run on <paramref name="engine"/>.</summary>
    public bool IsSkippedOn(string engine)
    {
        foreach (string s in SkipIf)
        {
            if (string.Equals(s, engine, StringComparison.Ordinal))
            {
                return true;
            }
        }
        if (OnlyIf.Count > 0)
        {
            foreach (string o in OnlyIf)
            {
                if (string.Equals(o, engine, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }
        return false;
    }
}

/// <summary>
/// Reads a sqllogictest script into records. The format is the one in the sqllogictest project's own description: line-oriented,
/// records separated by blank lines, <c>#</c> lines are comments (and do not separate records), conditionals
/// (<c>skipif</c>/<c>onlyif</c> NAME) prefix a record, the SQL runs up to a <c>----</c> line, and expected values follow it,
/// one per line.
/// </summary>
public static class ScriptParser
{
    /// <summary>Parses a script held in memory.</summary>
    public static List<Record> ParseAll(string text)
    {
        var all = new List<Record>();
        using var r = new StringReader(text);
        foreach (Record rec in Parse(r))
        {
            all.Add(rec);
        }
        return all;
    }

    /// <summary>Streams the records of a script. A malformed record yields a <see cref="RecordKind.Malformed"/> record and parsing resumes at the next blank line.</summary>
    public static IEnumerable<Record> Parse(TextReader reader)
    {
        var lines = new List<(int No, string Text)>();
        int lineNo = 0;
        string? line;
        bool more = true;
        while (more)
        {
            lines.Clear();
            // Skip blank lines and comments between records, then collect the record's lines up to the next blank line.
            while (true)
            {
                line = reader.ReadLine();
                lineNo++;
                if (line is null)
                {
                    more = false;
                    break;
                }
                if (IsBlank(line))
                {
                    if (lines.Count > 0)
                    {
                        break;
                    }
                    continue;
                }
                if (line[0] == '#')
                {
                    continue;   // comment: dropped, and it is not a separator
                }
                lines.Add((lineNo, line));
            }
            if (lines.Count > 0)
            {
                yield return ParseRecord(lines);
            }
        }
    }

    private static bool IsBlank(string s)
    {
        foreach (char c in s)
        {
            if (c != ' ' && c != '\t' && c != '\r')
            {
                return false;
            }
        }
        return true;
    }

    private static Record ParseRecord(List<(int No, string Text)> lines)
    {
        var skip = new List<string>();
        var only = new List<string>();
        int i = 0;
        // Conditionals: "skipif NAME" / "onlyif NAME"; anything after the name (a "# why" note) is ignored.
        for (; i < lines.Count; i++)
        {
            string[] t = lines[i].Text.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (t.Length >= 1 && (t[0] == "skipif" || t[0] == "onlyif"))
            {
                if (t.Length < 2)
                {
                    return Bad(lines[i].No, skip, only, "'" + t[0] + "' needs an engine name");
                }
                (t[0] == "skipif" ? skip : only).Add(t[1]);
            }
            else
            {
                break;
            }
        }
        if (i == lines.Count)
        {
            return Bad(lines[0].No, skip, only, "conditional without a record");
        }

        int no = lines[i].No;
        string[] w = lines[i].Text.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
        Record rec;
        switch (w[0])
        {
            case "halt":
                rec = new Record { Kind = RecordKind.Halt, Line = no };
                break;

            case "hash-threshold":
                if (w.Length != 2 || !int.TryParse(w[1], NumberStyles.None, CultureInfo.InvariantCulture, out int th))
                {
                    return Bad(no, skip, only, "hash-threshold needs one non-negative integer");
                }
                rec = new Record { Kind = RecordKind.HashThreshold, Line = no, Threshold = th };
                break;

            case "statement":
            {
                if (w.Length < 2 || (w[1] != "ok" && w[1] != "error"))
                {
                    return Bad(no, skip, only, "statement must be followed by 'ok' or 'error'");
                }
                string sql = ReadSql(lines, i + 1, out int dash);
                if (sql.Length == 0)
                {
                    return Bad(no, skip, only, "statement without SQL");
                }
                rec = new Record { Kind = w[1] == "ok" ? RecordKind.StatementOk : RecordKind.StatementError, Line = no, Sql = sql };
                if (dash >= 0)
                {
                    for (int k = dash + 1; k < lines.Count; k++)
                    {
                        rec.ExpectedErrorText.Add(lines[k].Text);
                    }
                }
                break;
            }

            case "query":
            {
                if (w.Length < 2)
                {
                    return Bad(no, skip, only, "query needs a type string");
                }
                string types = w[1];
                foreach (char c in types)
                {
                    if (c != 'I' && c != 'T' && c != 'R')
                    {
                        return Bad(no, skip, only, "type string '" + types + "' may contain only I, T and R");
                    }
                }
                SortMode sort = SortMode.NoSort;
                string? label = null;
                int next = 2;
                if (w.Length > next)
                {
                    switch (w[next])
                    {
                        case "nosort": next++; break;
                        case "rowsort": sort = SortMode.RowSort; next++; break;
                        case "valuesort": sort = SortMode.ValueSort; next++; break;
                        default: break;   // not a sort mode: it is the label (the sort mode is optional)
                    }
                }
                if (w.Length > next)
                {
                    label = w[next];
                    if (w.Length > next + 1 && w[next + 1][0] != '#')
                    {
                        return Bad(no, skip, only, "unexpected text after the label: '" + w[next + 1] + "'");
                    }
                }
                string sql = ReadSql(lines, i + 1, out int dash);
                if (sql.Length == 0)
                {
                    return Bad(no, skip, only, "query without SQL");
                }
                rec = new Record { Kind = RecordKind.Query, Line = no, Sql = sql, Types = types, Sort = sort, Label = label };
                if (dash >= 0)
                {
                    int first = dash + 1;
                    if (lines.Count - first == 1 && TryParseHash(lines[first].Text, out int count, out string digest))
                    {
                        rec.Hashed = true;
                        rec.HashedCount = count;
                        rec.HashedDigest = digest;
                    }
                    else
                    {
                        for (int k = first; k < lines.Count; k++)
                        {
                            rec.Expected.Add(lines[k].Text.TrimEnd('\r'));
                        }
                    }
                }
                break;
            }

            default:
                return Bad(no, skip, only, "unknown record '" + w[0] + "'");
        }
        rec.SkipIf.AddRange(skip);
        rec.OnlyIf.AddRange(only);
        return rec;
    }

    // The SQL is every line from 'start' up to a "----" line (exclusive); 'dash' is that line's index, or -1.
    private static string ReadSql(List<(int No, string Text)> lines, int start, out int dash)
    {
        dash = -1;
        var parts = new List<string>();
        for (int k = start; k < lines.Count; k++)
        {
            if (lines[k].Text == "----")
            {
                dash = k;
                break;
            }
            parts.Add(lines[k].Text.TrimEnd('\r'));
        }
        return string.Join('\n', parts);
    }

    /// <summary>Recognises <c>N values hashing to &lt;32 hex digits&gt;</c>.</summary>
    public static bool TryParseHash(string line, out int count, out string digest)
    {
        count = 0;
        digest = "";
        string[] p = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 5 || p[1] != "values" || p[2] != "hashing" || p[3] != "to" || p[4].Length != 32)
        {
            return false;
        }
        if (!int.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out count))
        {
            return false;
        }
        foreach (char c in p[4])
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }
        digest = p[4];
        return true;
    }

    private static Record Bad(int line, List<string> skip, List<string> only, string problem)
    {
        var r = new Record { Kind = RecordKind.Malformed, Line = line, Problem = problem };
        r.SkipIf.AddRange(skip);
        r.OnlyIf.AddRange(only);
        return r;
    }
}
