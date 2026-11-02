using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace RecursiveAI.OneLite.SqlLogicTest;

/// <summary>One failed record, with what the report shows.</summary>
public sealed class Failure
{
    /// <summary>Line of the record in the script.</summary>
    public int Line { get; set; }

    /// <summary>What kind of mismatch: <c>statement-should-fail</c>, <c>statement-failed</c>, <c>query-error</c>, <c>columns</c>, <c>values</c>, <c>hash</c>, <c>label</c>, <c>malformed</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The SQL (cut at 2000 characters).</summary>
    public string Sql { get; set; } = "";

    /// <summary>What the script expected.</summary>
    public string Expected { get; set; } = "";

    /// <summary>What the engine produced.</summary>
    public string Actual { get; set; } = "";
}

/// <summary>The outcome of one script file.</summary>
public sealed class FileResult
{
    /// <summary>Path relative to the corpus root, with <c>/</c> separators.</summary>
    public string File { get; set; } = "";

    /// <summary>Statement and query records seen (control records are not counted).</summary>
    public long Records { get; set; }

    /// <summary>Records that ran and matched.</summary>
    public long Passed { get; set; }

    /// <summary>Records that ran and did not match, plus malformed records.</summary>
    public long Failed { get; set; }

    /// <summary>Of <see cref="Failed"/>, the records where the engine answered but the answer was wrong (values, hash, column count, label, or a statement that should have failed); the rest are errors, mostly features the engine does not have.</summary>
    public long Wrong { get; set; }

    /// <summary>Records the conditionals excluded.</summary>
    public long Skipped { get; set; }

    /// <summary>True when a <c>halt</c> record ended the script early.</summary>
    public bool Halted { get; set; }

    /// <summary>Set when the engine threw something other than a statement error (for example "not implemented"); the rest of the file did not run.</summary>
    public string? Fatal { get; set; }

    /// <summary>Wall-clock time for the file.</summary>
    public long ElapsedMs { get; set; }

    /// <summary>The first failures in full.</summary>
    public List<Failure> Failures { get; set; } = [];

    /// <summary>Every failed record's line, up to <see cref="Runner.MaxFailedLines"/>.</summary>
    public List<int> FailedLines { get; set; } = [];
}

/// <summary>Runs one script against one engine.</summary>
public static class Runner
{
    /// <summary>How many failures keep their full SQL and diff.</summary>
    public const int MaxFailuresKept = 20;

    /// <summary>How many failed lines are listed.</summary>
    public const int MaxFailedLines = 5000;

    private const int MaxShownValues = 40;
    private const int MaxSqlChars = 2000;

    /// <summary>Runs <paramref name="path"/> on a fresh database from <paramref name="engine"/>.</summary>
    public static FileResult RunFile(ISltEngine engine, string path, string relative, string scratchDir)
    {
        var sw = Stopwatch.StartNew();
        var result = new FileResult { File = relative };
        try
        {
            using var reader = new StreamReader(path, new UTF8Encoding(false));
            using ISltConnection conn = engine.OpenFresh(scratchDir);
            Run(engine.DialectName, ScriptParser.Parse(reader), conn, result);
        }
        catch (Exception e) when (e is NotImplementedException or InvalidOperationException or IOException)
        {
            result.Fatal = e.GetType().Name + ": " + e.Message;
        }
        result.ElapsedMs = sw.ElapsedMilliseconds;
        return result;
    }

    /// <summary>Runs parsed records against a connection, accumulating into <paramref name="result"/>.</summary>
    public static void Run(string dialect, IEnumerable<Record> records, ISltConnection conn, FileResult result)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Record rec in records)
        {
            switch (rec.Kind)
            {
                case RecordKind.Halt:
                    if (!rec.IsSkippedOn(dialect))
                    {
                        result.Halted = true;
                        return;
                    }
                    continue;
                case RecordKind.HashThreshold:
                    // A completion-mode setting: validation compares the hashed form when the script holds one, whatever the threshold.
                    continue;
                case RecordKind.Malformed:
                    result.Records++;
                    Fail(result, new Failure { Line = rec.Line, Kind = "malformed", Expected = rec.Problem });
                    continue;
            }

            result.Records++;
            if (rec.IsSkippedOn(dialect))
            {
                result.Skipped++;
                // A skipped query's recorded result still takes part in label checks (the format's rule).
                if (rec.Kind == RecordKind.Query && rec.Label is not null)
                {
                    string? recorded = rec.Hashed ? rec.HashedDigest : rec.Expected.Count > 0 ? Rendering.Hash(rec.Expected) : null;
                    if (recorded is not null && !CheckLabel(labels, rec.Label, recorded))
                    {
                        result.Skipped--;
                        Fail(result, Make(rec, "label", "same result as earlier queries labelled '" + rec.Label + "'", "this record's recorded result differs"));
                    }
                }
                continue;
            }

            Failure? f = rec.Kind == RecordKind.Query ? RunQuery(rec, conn, labels) : RunStatement(rec, conn);
            if (f is null)
            {
                result.Passed++;
            }
            else
            {
                Fail(result, f);
            }
        }
    }

    private static Failure? RunStatement(Record rec, ISltConnection conn)
    {
        SltError? err = conn.Execute(rec.Sql);
        if (rec.Kind == RecordKind.StatementOk)
        {
            return err is null ? null : Make(rec, "statement-failed", "ok", "error " + err.Code + ": " + err.Message);
        }
        return err is not null ? null : Make(rec, "statement-should-fail", "error", "ok");
    }

    private static Failure? RunQuery(Record rec, ISltConnection conn, Dictionary<string, string> labels)
    {
        SltQueryResult q = conn.Query(rec.Sql);
        if (q.Error is not null)
        {
            return Make(rec, "query-error", Describe(rec), "error " + q.Error.Code + ": " + q.Error.Message);
        }
        if (q.ColumnCount != rec.Types.Length)
        {
            return Make(rec, "columns", rec.Types.Length + " columns (" + rec.Types + ")", q.ColumnCount + " columns");
        }

        var rows = new List<string[]>(q.Rows.Count);
        foreach (SltValue[] r in q.Rows)
        {
            var s = new string[r.Length];
            for (int i = 0; i < r.Length; i++)
            {
                s[i] = Rendering.Render(in r[i], rec.Types[i]);
            }
            rows.Add(s);
        }
        List<string> actual = Rendering.Arrange(rows, rec.Sort);
        string digest = Rendering.Hash(actual);

        if (rec.Hashed)
        {
            if (actual.Count != rec.HashedCount || digest != rec.HashedDigest)
            {
                return Make(rec, "hash", rec.HashedCount + " values hashing to " + rec.HashedDigest,
                    actual.Count + " values hashing to " + digest + "\n" + Show(actual));
            }
        }
        else if (!SameValues(rec.Expected, actual))
        {
            return Make(rec, "values", Show(rec.Expected) + FirstDifference(rec.Expected, actual), Show(actual));
        }

        if (rec.Label is not null && !CheckLabel(labels, rec.Label, digest))
        {
            return Make(rec, "label", "same result as earlier queries labelled '" + rec.Label + "'", "this result differs");
        }
        return null;
    }

    // The first query with a label stores its hash; later ones must match it.
    private static bool CheckLabel(Dictionary<string, string> labels, string label, string digest)
    {
        if (labels.TryGetValue(label, out string? seen))
        {
            return seen == digest;
        }
        labels[label] = digest;
        return true;
    }

    private static bool SameValues(List<string> a, List<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static string Describe(Record rec) => rec.Hashed ? rec.HashedCount + " values hashing to " + rec.HashedDigest : Show(rec.Expected);

    private static string FirstDifference(List<string> expected, List<string> actual)
    {
        int n = Math.Min(expected.Count, actual.Count);
        for (int i = 0; i < n; i++)
        {
            if (!string.Equals(expected[i], actual[i], StringComparison.Ordinal))
            {
                return "\n(first difference at value " + i + ": expected '" + expected[i] + "', actual '" + actual[i] + "')";
            }
        }
        return "\n(" + expected.Count + " values expected, " + actual.Count + " returned)";
    }

    private static string Show(List<string> values)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < values.Count && i < MaxShownValues; i++)
        {
            if (i > 0)
            {
                sb.Append('\n');
            }
            sb.Append(values[i]);
        }
        if (values.Count > MaxShownValues)
        {
            sb.Append("\n... (").Append(values.Count).Append(" values)");
        }
        return sb.ToString();
    }

    private static Failure Make(Record rec, string kind, string expected, string actual)
        => new()
        {
            Line = rec.Line,
            Kind = kind,
            Sql = rec.Sql.Length > MaxSqlChars ? rec.Sql[..MaxSqlChars] + "..." : rec.Sql,
            Expected = expected,
            Actual = actual,
        };

    private static void Fail(FileResult result, Failure f)
    {
        result.Failed++;
        if (f.Kind is not ("query-error" or "statement-failed" or "malformed"))
        {
            result.Wrong++;
        }

        if (result.Failures.Count < MaxFailuresKept)
        {
            result.Failures.Add(f);
        }
        if (result.FailedLines.Count < MaxFailedLines)
        {
            result.FailedLines.Add(f.Line);
        }
    }
}
