using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RecursiveAI.OneLite.QueryDiff;

namespace RecursiveAI.OneLite.Differential;

internal enum VerdictKind { Pass, Fail, Unsupported, Inconclusive }

/// <summary>What one check found. <see cref="Class"/> is "unexplained" or the name of a documented difference.</summary>
internal sealed record Verdict(VerdictKind Kind, string Engine = "", string Class = "", string Detail = "")
{
    internal const string Unexplained = "unexplained";

    /// <summary>True when the reference rejected the generated statement: a measure of how much of the generated SQL is valid.</summary>
    internal bool ReferenceError { get; init; }

    /// <summary>Prefix of a class naming a OneLite defect found by this tool and reported with a minimized repro.</summary>
    internal const string BugPrefix = "bug:";

    internal static readonly Verdict Pass = new(VerdictKind.Pass);
    internal static readonly Verdict Unsupported = new(VerdictKind.Unsupported);
    internal static readonly Verdict Inconclusive = new(VerdictKind.Inconclusive);

    internal static Verdict Fail(string engine, string cls, string detail) => new(VerdictKind.Fail, engine, cls, detail);

    /// <summary>"unexplained", "bug" (an open defect with a repro) or "documented" (a difference the docs list on purpose).</summary>
    internal string Category => Class == Unexplained ? "unexplained" : Class.StartsWith(BugPrefix, StringComparison.Ordinal) ? "bug" : "documented";

    /// <summary>The first words of the detail: what kind of difference (row count, column names, error, row value).</summary>
    internal string Shape
    {
        get
        {
            string d = Detail;
            int i = d.IndexOf(':', StringComparison.Ordinal);
            string head = i > 0 ? d[..i] : d;
            return Regex.Replace(head, @"\d+", "N");
        }
    }
}

/// <summary>Both engines' outcomes of every query run so far.</summary>
internal sealed class Lookup
{
    private readonly Dictionary<string, (Outcome Ours, Outcome Theirs)> _d = new(StringComparer.Ordinal);

    internal bool Has(string sql) => _d.ContainsKey(sql);

    internal void Add(string sql, Outcome ours, Outcome theirs) => _d[sql] = (ours, theirs);

    internal (Outcome Ours, Outcome Theirs) this[string sql] => _d[sql];
}

/// <summary>
/// One thing to check on a database: a set of queries and a verdict computed from their outcomes on OneLite and on the pinned sqlite3.
/// A check may ask for queries in phases (the second phase's SQL can depend on the first phase's answers).
/// </summary>
internal abstract class Check
{
    /// <summary>"diff", "pqs", "norec" or "tlp".</summary>
    internal abstract string Oracle { get; }

    internal HashSet<string> Families { get; set; } = [Fam.Expr];

    /// <summary>Whether the database is UTF-16 (a documented class applies there).</summary>
    internal bool Utf16 { get; set; }

    /// <summary>The queries of phase <paramref name="phase"/> (0, 1, ...); an empty sequence ends the check.</summary>
    internal abstract IEnumerable<string> Queries(int phase, Lookup seen);

    internal abstract Verdict Judge(Lookup seen);

    /// <summary>Smaller versions of this check, for the minimizer. Empty when it was read back from a file.</summary>
    internal virtual IEnumerable<Check> Shrinks() => [];

    /// <summary>The key/value lines that describe this check in a repro file.</summary>
    internal abstract IEnumerable<(string Key, string Value)> Fields();

    protected T CopyMeta<T>(T other) where T : Check
    {
        other.Families = Families;
        other.Utf16 = Utf16;
        return other;
    }

    internal static Check FromFields(IReadOnlyDictionary<string, string> f)
    {
        string oracle = f["oracle"];
        Check c = oracle switch
        {
            "diff" => new DiffCheck(f["query"], null, f.GetValueOrDefault("ordered") == "true"),
            "norec" => new NoRecCheck(f["from"], Expr.Raw(f["predicate"])),
            "tlp" => new TlpCheck(f["from"], Expr.Raw(f["predicate"])),
            "pqs" => new PqsCheck(f["from"], Expr.Raw(f["predicate"]), long.Parse(f["pivot"], CultureInfo.InvariantCulture)),
            _ => throw new FormatException("unknown oracle " + oracle),
        };
        if (f.TryGetValue("families", out string? fams))
        {
            c.Families = [.. fams.Split(',', StringSplitOptions.RemoveEmptyEntries)];
        }

        c.Utf16 = f.GetValueOrDefault("utf16") == "true";
        return c;
    }

    /// <summary>
    /// A query that one engine runs and the other rejects is a difference whatever the oracle: returns the failure, or null when both
    /// agree on whether it errors.
    /// </summary>
    protected Verdict? ErrorMismatch(string sql, Lookup seen)
    {
        (Outcome o, Outcome t) = seen[sql];
        if (o.HasError == t.HasError || IsUnsupported(o) || IsCrash(o))
        {
            return null;
        }

        string diff = $"ours: {(o.HasError ? o.ErrorText : "rows")}; sqlite3: {(t.HasError ? t.ErrorText : "rows")}";
        return Verdict.Fail("ours", Classifier.Known(sql, o, t, diff, Utf16), diff);
    }

    /// <summary>The "not supported yet" verdict, carrying OneLite's own words for the first query it refused.</summary>
    protected static Verdict Unsup(params Outcome[] outcomes)
        => new(VerdictKind.Unsupported, Detail: outcomes.First(IsUnsupported).Message);

    protected static bool IsUnsupported(Outcome o) => o.HasError && o.Message.StartsWith("unsupported", StringComparison.Ordinal)
        && !o.Message.StartsWith("unsupported frame specification", StringComparison.Ordinal);   // the reference's own error text for a bad frame

    protected static bool IsCrash(Outcome o) => o.HasError && o.Phase == "crash";
}

/// <summary>The plain comparison: the same query on both engines must give the same everything.</summary>
internal sealed class DiffCheck : Check
{
    private static readonly string[] TotalKeys = ["id", "a.id", "b.id", "s.id", "n", "value", "r.n"];

    private readonly string _sql;
    private readonly Select? _select;
    private readonly bool _ordered;

    internal DiffCheck(string sql, Select? select, bool ordered)
    {
        _sql = sql;
        _select = select;
        _ordered = ordered;
    }

    internal override string Oracle => "diff";

    internal string Sql => _sql;

    internal override IEnumerable<string> Queries(int phase, Lookup seen) => phase == 0 ? [_sql] : [];

    internal override Verdict Judge(Lookup seen) => Decide(seen) with { ReferenceError = seen[_sql].Theirs.HasError };

    private Verdict Decide(Lookup seen)
    {
        (Outcome ours, Outcome theirs) = seen[_sql];
        if (IsCrash(ours))
        {
            return Verdict.Fail("ours", Verdict.Unexplained, "exception: " + ours.Message);
        }

        if (IsUnsupported(ours))
        {
            return Unsup(ours);
        }

        string? diff = Compare.Diff(ours, theirs, _ordered);
        if (diff is null)
        {
            return Verdict.Pass;
        }

        return Verdict.Fail("ours", Classifier.Known(_sql, ours, theirs, diff, Utf16), diff);
    }

    internal override IEnumerable<Check> Shrinks()
    {
        if (_select is null)
        {
            yield break;
        }

        // Shrinking must not turn a well-defined query into one whose result order or row choice is up to the engine:
        // the unique keys the generator put at the end of ORDER BY stay, or the comparison drops to a multiset and LIMIT goes.
        string[] totals = [.. _select.Order.Select(o => o.ToString()).Where(k => TotalKeys.Contains(k))];
        foreach (Select s in _select.Shrinks())
        {
            bool keeps = totals.All(k => s.Order.Any(o => o.ToString() == k));
            if (!keeps && (s.Limit is not null || s.Compound.Count > 0))
            {
                continue;
            }

            yield return CopyMeta(new DiffCheck(s.ToString(), s, _ordered && keeps));
        }
    }

    internal override IEnumerable<(string, string)> Fields()
    {
        yield return ("oracle", "diff");
        yield return ("ordered", _ordered ? "true" : "false");
        yield return ("query", _sql);
    }
}

/// <summary>
/// NoREC (Rigger and Su): the rows a WHERE clause keeps, counted by the engine's optimized path, must equal the rows for which the same
/// condition is true when it is evaluated as a plain column of the result.
/// </summary>
internal sealed class NoRecCheck : Check
{
    private readonly string _from;
    private readonly Expr _p;

    internal NoRecCheck(string from, Expr p)
    {
        _from = from;
        _p = p;
    }

    internal override string Oracle => "norec";

    private string Optimized => $"SELECT count(*) FROM {_from} WHERE {_p}";

    private string Unoptimized => $"SELECT CASE WHEN {_p} THEN 1 ELSE 0 END FROM {_from}";

    internal override IEnumerable<string> Queries(int phase, Lookup seen) => phase == 0 ? [Optimized, Unoptimized] : [];

    internal override Verdict Judge(Lookup seen)
    {
        var (o1, t1) = seen[Optimized];
        var (o2, t2) = seen[Unoptimized];
        if (IsCrash(o1) || IsCrash(o2))
        {
            return Verdict.Fail("ours", Verdict.Unexplained, "exception: " + (IsCrash(o1) ? o1.Message : o2.Message));
        }

        if (IsUnsupported(o1) || IsUnsupported(o2))
        {
            return Unsup(o1, o2);
        }

        if ((ErrorMismatch(Optimized, seen) ?? ErrorMismatch(Unoptimized, seen)) is { } mm)
        {
            return mm;
        }

        string? bad = Test(o1, o2, "ours");
        string? badRef = Test(t1, t2, "sqlite3");
        if (bad is null && badRef is null)
        {
            return o1.HasError || o2.HasError || t1.HasError || t2.HasError ? Verdict.Inconclusive : Verdict.Pass;
        }

        return Verdict.Fail(bad is not null && badRef is not null ? "both" : bad is not null ? "ours" : "sqlite3", Verdict.Unexplained, bad ?? badRef!);
    }

    private static string? Test(Outcome opt, Outcome unopt, string engine)
    {
        if (opt.HasError || unopt.HasError || opt.Rows.Count != 1)
        {
            return null;
        }

        long counted = unopt.Rows.Count(r => r[0].Key == "I:1");
        string key = opt.Rows[0][0].Key;
        return key == "I:" + counted.ToString(CultureInfo.InvariantCulture) ? null : $"count differs: {engine} counted {opt.Rows[0][0].Show} rows through WHERE and {counted} through the select list";
    }

    internal override IEnumerable<Check> Shrinks()
    {
        foreach (Expr e in _p.Simplify())
        {
            yield return CopyMeta(new NoRecCheck(_from, e));
        }
    }

    internal override IEnumerable<(string, string)> Fields()
    {
        yield return ("oracle", "norec");
        yield return ("from", _from);
        yield return ("predicate", _p.ToString());
    }
}

/// <summary>
/// Ternary logic partitioning (Rigger and Su): every row satisfies exactly one of <c>p</c>, <c>NOT p</c> and <c>p IS NULL</c>, so the three
/// partitions together must hold the rows of the unfiltered query, as a multiset.
/// </summary>
internal sealed class TlpCheck : Check
{
    private readonly string _from;
    private readonly Expr _p;

    internal TlpCheck(string from, Expr p)
    {
        _from = from;
        _p = p;
    }

    internal override string Oracle => "tlp";

    private string Whole => $"SELECT * FROM {_from}";

    private string[] Parts => [$"SELECT * FROM {_from} WHERE ({_p})", $"SELECT * FROM {_from} WHERE NOT ({_p})", $"SELECT * FROM {_from} WHERE ({_p}) IS NULL"];

    internal override IEnumerable<string> Queries(int phase, Lookup seen) => phase == 0 ? [Whole, .. Parts] : [];

    internal override Verdict Judge(Lookup seen)
    {
        var whole = seen[Whole];
        var parts = Parts.Select(p => seen[p]).ToArray();
        if (IsCrash(whole.Ours) || parts.Any(p => IsCrash(p.Ours)))
        {
            return Verdict.Fail("ours", Verdict.Unexplained, "exception: " + (parts.Select(p => p.Ours).Append(whole.Ours).First(IsCrash)).Message);
        }

        if (IsUnsupported(whole.Ours) || parts.Any(p => IsUnsupported(p.Ours)))
        {
            return Unsup([whole.Ours, .. parts.Select(p => p.Ours)]);
        }

        foreach (string q in Parts.Prepend(Whole))
        {
            if (ErrorMismatch(q, seen) is { } mm)
            {
                return mm;
            }
        }

        string? bad = Test(whole.Ours, parts.Select(p => p.Ours).ToArray(), "ours");
        string? badRef = Test(whole.Theirs, parts.Select(p => p.Theirs).ToArray(), "sqlite3");
        if (bad is null && badRef is null)
        {
            return whole.Ours.HasError || whole.Theirs.HasError || parts.Any(p => p.Ours.HasError || p.Theirs.HasError) ? Verdict.Inconclusive : Verdict.Pass;
        }

        return Verdict.Fail(bad is not null && badRef is not null ? "both" : bad is not null ? "ours" : "sqlite3", Verdict.Unexplained, bad ?? badRef!);
    }

    private static string? Test(Outcome whole, Outcome[] parts, string engine)
    {
        if (whole.HasError || parts.Any(p => p.HasError))
        {
            return null;
        }

        var a = whole.Rows.Select(Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var b = parts.SelectMany(p => p.Rows).Select(Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (a.SequenceEqual(b))
        {
            return null;
        }

        return $"partitions differ: {engine} has {a.Count} rows unfiltered and {b.Count} in the three partitions";
    }

    private static string Key(Cell[] row) => string.Join("\u0001", row.Select(c => c.Key));

    internal override IEnumerable<Check> Shrinks()
    {
        foreach (Expr e in _p.Simplify())
        {
            yield return CopyMeta(new TlpCheck(_from, e));
        }
    }

    internal override IEnumerable<(string, string)> Fields()
    {
        yield return ("oracle", "tlp");
        yield return ("from", _from);
        yield return ("predicate", _p.ToString());
    }
}

/// <summary>
/// Pivoted query synthesis (Rigger and Su): take a row, work out with the reference whether <c>p</c> is true, false or NULL for it,
/// rewrite the condition so that it is true for that row, and require the row in the result. The reference supplies the truth value;
/// the engines under test must agree that the row qualifies.
/// </summary>
internal sealed class PqsCheck : Check
{
    private readonly string _from;
    private readonly Expr _p;
    private readonly long _pivot;

    internal PqsCheck(string from, Expr p, long pivot)
    {
        _from = from;
        _p = p;
        _pivot = pivot;
    }

    internal override string Oracle => "pqs";

    private string Probe => $"SELECT CASE WHEN ({_p}) IS NULL THEN 'n' WHEN ({_p}) THEN 't' ELSE 'f' END FROM {_from} WHERE id = {_pivot.ToString(CultureInfo.InvariantCulture)}";

    private string? Rectified(Lookup seen)
    {
        Outcome t = seen[Probe].Theirs;
        if (t.HasError || t.Rows.Count != 1)
        {
            return null;
        }

        string? cond = t.Rows[0][0].Key switch
        {
            "T:74" => $"({_p})",
            "T:66" => $"NOT ({_p})",
            "T:6E" => $"({_p}) IS NULL",
            _ => null,
        };
        return cond is null ? null : $"SELECT id FROM {_from} WHERE {cond}";
    }

    internal override IEnumerable<string> Queries(int phase, Lookup seen)
    {
        if (phase == 0)
        {
            return [Probe];
        }

        if (phase == 1 && Rectified(seen) is { } q)
        {
            return [q];
        }

        return [];
    }

    internal override Verdict Judge(Lookup seen)
    {
        string? q = Rectified(seen);
        if (q is null)
        {
            return IsUnsupported(seen[Probe].Ours) ? Unsup(seen[Probe].Ours) : Verdict.Inconclusive;
        }

        var (ours, theirs) = seen[q];
        if (IsCrash(ours))
        {
            return Verdict.Fail("ours", Verdict.Unexplained, "exception: " + ours.Message);
        }

        if (IsUnsupported(ours))
        {
            return Unsup(ours);
        }

        string key = "I:" + _pivot.ToString(CultureInfo.InvariantCulture);
        bool oursHas = !ours.HasError && ours.Rows.Any(r => r[0].Key == key);
        bool theirsHas = !theirs.HasError && theirs.Rows.Any(r => r[0].Key == key);
        if (oursHas && theirsHas)
        {
            return Verdict.Pass;
        }

        if (!oursHas && ours.HasError && theirs.HasError)
        {
            return Verdict.Inconclusive;
        }

        string engine = !oursHas && !theirsHas ? "both" : !oursHas ? "ours" : "sqlite3";
        // Neither engine returns the pivot when a blob is read as text in a UTF-16 database: the reference's own answer for the rectified condition
        // does not match the one it gave for the bare predicate (docs/engine.md, utf16-blob-text).
        string cls = engine == "ours" ? Classifier.Known(q, ours, theirs, "pivot", Utf16)
            : engine == "both" && Utf16 && q.Contains("x'", StringComparison.OrdinalIgnoreCase) ? "utf16-blob-text" : Verdict.Unexplained;
        return Verdict.Fail(engine, cls, $"pivot row id={_pivot} missing from the result of the rectified condition");
    }

    internal override IEnumerable<Check> Shrinks()
    {
        foreach (Expr e in _p.Simplify())
        {
            yield return CopyMeta(new PqsCheck(_from, e, _pivot));
        }
    }

    internal override IEnumerable<(string, string)> Fields()
    {
        yield return ("oracle", "pqs");
        yield return ("from", _from);
        yield return ("predicate", _p.ToString());
        yield return ("pivot", _pivot.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// The documented differences (docs/engine.md "Known differences"). A difference matching one of them is counted under that name
/// and kept apart from the unexplained ones.
/// </summary>
internal static class Classifier
{
    /// <summary>The setup statements of the database the current check runs on (per thread); lets a rule look at the data behind a number.</summary>
    [ThreadStatic]
    internal static string? Setup;

    /// <summary>
    /// OneLite defects this tool found and filed with a minimized repro each. A match is counted as an open bug of that name, not as
    /// unexplained, so that a long campaign keeps finding new things. When the defect is fixed the rule stops matching and the repro
    /// in the corpus flips to passing.
    /// </summary>
    // Empty: every defect the first campaigns found is fixed and its repro replays as a pass. A new defect adds a rule here with a
    // narrow match (a second defect must not hide under it) until its repro is in the corpus.
    private static readonly (string Name, Func<string, Outcome, Outcome, bool> Match)[] Bugs = [];

    private static bool AnyCell(Outcome o, Outcome t, Func<string, string, bool> pair)
        => o.Rows.Count == t.Rows.Count && o.Rows.Zip(t.Rows).Any(r => r.First.Length == r.Second.Length && r.First.Zip(r.Second).Any(c => pair(c.First.Key, c.Second.Key)));

    internal static string Known(string sql, Outcome ours, Outcome theirs, string diff, bool utf16)
    {
        foreach ((string name, Func<string, Outcome, Outcome, bool> match) in Bugs)
        {
            if (match(sql, ours, theirs))
            {
                return Verdict.BugPrefix + name;
            }
        }

        // The reference walks an index that matches ORDER BY instead of sorting, and streams the legs of a compound or reads a covering partial
        // index, so rows reach the caller before a later row fails; OneLite sorts or reads in another order. Narrow on purpose: both sides fail
        // the same way and only the rows before the failure differ.
        if (ours.HasError && theirs.HasError && ours.Phase == "run" && theirs.Phase == "run" && ours.Code == theirs.Code && ours.Message == theirs.Message
            && (ours.Rows.Count != theirs.Rows.Count || HasSecondaryIndex())
            && (sql.Contains("ORDER BY", StringComparison.Ordinal) || sql.Contains(" OVER ", StringComparison.Ordinal) || sql.Contains(" UNION ", StringComparison.Ordinal) || sql.Contains("GROUP BY", StringComparison.Ordinal) || HasSecondaryIndex()))
        {
            return "order-by-index-scan-before-error";
        }

        // Two or more unknown columns in one statement: which of them the error names depends on the order the reference resolves the
        // parts of an expression in (a function call's or an ISNULL's operands after their siblings, the last of several), which the
        // docs leave open. Same code and wording, a different column; both columns appear in the query.
        if (ours.HasError && theirs.HasError && ours.Phase == "parse" && theirs.Phase == "parse" && ours.Code == theirs.Code
            && ours.Message.StartsWith("no such column: ", StringComparison.Ordinal) && theirs.Message.StartsWith("no such column: ", StringComparison.Ordinal)
            && ours.Message != theirs.Message
            && sql.Contains(ours.Message["no such column: ".Length..], StringComparison.Ordinal) && sql.Contains(theirs.Message["no such column: ".Length..], StringComparison.Ordinal))
        {
            return "name-resolution-error-order";
        }

        // Which WHERE term runs first, and so whether a runtime error is raised at all, is the reference planner's choice.
        // (Statements of the JSON campaign have no WHERE clause and one evaluation order: an error on one side only is a difference.)
        if (!sql.StartsWith(JsonGen.Marker, StringComparison.Ordinal)
            && ((ours.HasError && ours.Phase == "run" && !theirs.HasError) || (theirs.HasError && theirs.Phase == "run" && !ours.HasError)))
        {
            return "runtime-error-order";
        }

        // Two runtime errors in a window query (an aggregate overflowing, a frame offset refused) or a grouped one (an aggregate refusing its input
        // at a step against another refusing it when its result is built): which one comes first follows the order the reference evaluates in,
        // which it does lazily for rows an outer query does not read.
        if (ours.HasError && theirs.HasError && ours.Phase == "run" && theirs.Phase == "run" && ours.Message != theirs.Message
            && (sql.Contains(" OVER ", StringComparison.Ordinal) || sql.Contains("GROUP BY", StringComparison.Ordinal) || sql.Contains(" UNION ", StringComparison.Ordinal) || sql.Contains("ORDER BY", StringComparison.Ordinal)
                || HasSecondaryIndex()))
        {
            return "runtime-error-order";
        }

        if (sql.Contains("%!", StringComparison.Ordinal))
        {
            return "printf-alternate-form-2";
        }

        if (!ours.HasError && !theirs.HasError && ours.Rows.Count == theirs.Rows.Count && DifferOnlyInRealText(ours, theirs))
        {
            return "real-to-text-digits";
        }

        // Text with 20 or more significant digits converted to a REAL: the reference accumulates the digits by a method of its own that the
        // documents do not give, and the last bits can differ from the correctly rounded value OneLite computes (docs/engine.md).
        if (!ours.HasError && !theirs.HasError && ours.Rows.Count == theirs.Rows.Count && Regex.IsMatch(sql, @"\bAS\s+(NUMERIC|REAL|FLOAT|DOUBLE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && ours.Rows.Zip(theirs.Rows).All(p => p.First.Length == p.Second.Length && p.First.Zip(p.Second).All(q => q.First.Key == q.Second.Key || NearbyReals(q.First.Show, q.Second.Show))))
        {
            return "text-to-real-last-digit";
        }

        if (sql.Contains("timediff", StringComparison.OrdinalIgnoreCase) && !ours.HasError && !theirs.HasError)
        {
            return "timediff-range";
        }

        // A sliding sum, total or avg over a frame that mixes values of very different size (or integers beyond 2^53 with reals) keeps the
        // reference's own rounding residue, and an integer sum over a frame that once held a blob comes back as an integer there;
        // only the numbers differ, and by far less than the size of the values that were added and removed (a relative 1e-3 at most).
        if (!ours.HasError && !theirs.HasError && ours.Rows.Count == theirs.Rows.Count
            && Regex.IsMatch(sql, @"(sum|total|avg)\s*\((?:[^()]|\([^()]*\))*\)\s*(filter\s*\((?:[^()]|\([^()]*\))*\)\s*)?over", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && DifferOnlyInNumbers(ours, theirs, HugeInSetup()))
        {
            return "window-sum-cancellation";
        }

        // LIKE over bytes that are not valid UTF-8 (a blob operand or pattern): the reference matches byte-wise up to a NUL.
        if (!ours.HasError && !theirs.HasError && ours.Rows.Count == theirs.Rows.Count && sql.Contains(" LIKE ", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("x'", StringComparison.OrdinalIgnoreCase))
        {
            return "like-invalid-utf8";
        }

        // Rows that tie in a window's order leave a stage in the order its sort left them; with three or more stages and an explicit NULLS
        // clause the reference's order differs from ours for ties. Same rows back, only values that depend on the tie order differ.
        if (!ours.HasError && !theirs.HasError && ours.Rows.Count == theirs.Rows.Count && ours.Rows.Count > 0
            && Regex.Count(sql, @"\bOVER\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) >= 3
            && Regex.IsMatch(sql, @"\bNULLS\s+(FIRST|LAST)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && ours.Rows.Select(r => r[0].Key).Order(StringComparer.Ordinal).SequenceEqual(theirs.Rows.Select(r => r[0].Key).Order(StringComparer.Ordinal)))
        {
            return "window-stage-tie-order";
        }

        // RANGE offset frames over an order column that holds integers and reals at the 64-bit edge (equal as numbers, different as types).
        if (!ours.HasError && !theirs.HasError && ours.Rows.Count == theirs.Rows.Count && HugeInSetup()
            && Regex.IsMatch(sql, @"\bRANGE\s+BETWEEN\b.*\b(PRECEDING|FOLLOWING)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && Regex.IsMatch(sql, @"\b[0-9]+\s+(PRECEDING|FOLLOWING)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "window-range-int64-edge";
        }

        // A bare column beside GROUP BY comes from the first row the reference scans, which it may take through a covering index (docs/engine.md);
        // when the group holds values that compare equal but are not the same ('a' and 'A' under NOCASE, 1 and 1.0), only that choice differs.
        if (!ours.HasError && !theirs.HasError && ours.Rows.Count == theirs.Rows.Count && sql.Contains("GROUP BY", StringComparison.Ordinal)
            && DifferOnlyInEqualValues(ours, theirs))
        {
            return "group-by-bare-value";
        }

        // A plain sum/total/avg that adds an integer beyond 2^53 to reals keeps the reference's own rounding residue (docs/engine.md); the
        // numbers agree to within a relative 1e-3.
        if (!ours.HasError && !theirs.HasError && ours.Rows.Count == theirs.Rows.Count && HugeInSetup()
            && Regex.IsMatch(sql, @"\b(sum|total|avg)\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && DifferOnlyInNumbers(ours, theirs, false))
        {
            return "sum-int-real-residue";
        }

        if (!ours.HasError && !theirs.HasError && ours.Rows.Count == theirs.Rows.Count && DifferOnlyInNulText(ours, theirs))
        {
            return "nul-text-output";
        }

        // A blob (written in the query or stored by the setup) read as text in a UTF-16 database: its bytes are taken as UTF-16 text, which
        // the reference decodes its own way when they are not well formed (docs/engine.md).
        if (utf16 && (Regex.IsMatch(sql, @"cast|blob|x'|hex|zeroblob|char\(|group_concat", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || Setup is { } blobSetup && blobSetup.Contains("x'", StringComparison.OrdinalIgnoreCase)))
        {
            return "utf16-blob-text";
        }

        // Which index the reference scans a table through when the query needs only indexed columns (a covering index) is its planner's
        // choice, and OneLite scans the table or another index. A window function with no total order, or a bare column, then sees the
        // rows in another order. Narrow: a window query on a table with a secondary index (or a UNIQUE column), the same rows come back.
        if (!ours.HasError && !theirs.HasError && ours.Rows.Count == theirs.Rows.Count && ours.Rows.Count > 0 && Setup is { } su
            && Regex.IsMatch(su, @"CREATE\s+(UNIQUE\s+)?INDEX|\bUNIQUE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && (Regex.IsMatch(sql, @"\bOVER\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) || !sql.Contains("ORDER BY", StringComparison.Ordinal))
            && ours.Rows.Select(r => r[0].Key).Order(StringComparer.Ordinal).SequenceEqual(theirs.Rows.Select(r => r[0].Key).Order(StringComparer.Ordinal)))
        {
            return "planner-index-choice";
        }

        // json_group_array and group_concat list their values in the order the rows are read, and without an ORDER BY inside the aggregate that
        // order is the planner's: the reference may read a covering or unique index, or a rowid range through a view's filter, where OneLite
        // reads in table order. Same elements, another order; narrow to a setup with a secondary index, a UNIQUE column or WITHOUT ROWID.
        if (!ours.HasError && !theirs.HasError && ours.Rows.Count == theirs.Rows.Count && ours.Rows.Count > 0 && Setup is { } gs
            && Regex.IsMatch(gs, @"CREATE\s+(UNIQUE\s+)?INDEX|\bUNIQUE\b|WITHOUT\s+ROWID", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && Regex.IsMatch(sql, @"\b(json_group_array|group_concat|string_agg)\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && !Regex.IsMatch(sql, @"\b(json_group_array|group_concat|string_agg)\s*\([^)]*ORDER\s+BY", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && DifferOnlyInListOrder(ours, theirs))
        {
            return "planner-index-choice";
        }

        // Duplicates removed by UNION, INTERSECT, EXCEPT or DISTINCT keep the first of the values that compare equal (1 and 1.0, 'a' and
        // 'A' under NOCASE) in the order the rows were read, which depends on the index the reference reads them through.
        if (!ours.HasError && !theirs.HasError && ours.Rows.Count == theirs.Rows.Count && HasSecondaryIndex()
            && Regex.IsMatch(sql, @"\b(UNION|INTERSECT|EXCEPT|DISTINCT)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && DifferOnlyInEqualValues(LooseSorted(ours), LooseSorted(theirs)))
        {
            return "planner-index-choice";
        }

        // A bare column beside an aggregate without GROUP BY comes from the first row read, and the reference may read through an index
        // (a DESC one, or one under a view) where OneLite reads the table: the aggregates agree, only the bare columns come from another row.
        if (!ours.HasError && !theirs.HasError && ours.Rows.Count == 1 && theirs.Rows.Count == 1 && ours.Rows[0].Length == theirs.Rows[0].Length
            && HasSecondaryIndex() && !sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(sql, @"\b(count|sum|total|avg|group_concat|string_agg)\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && ours.Rows[0].Zip(theirs.Rows[0]).Any(p => p.First.Key == p.Second.Key))
        {
            return "planner-index-choice";
        }

        // Observed: a WHERE comparison of a column with a numeric-affinity operand also changes how min()/max() over that same column
        // compare its values (the reference applies the affinity to the column's register in place), so a text such as '00' or '1e1'
        // meets numbers as the number it reads as. The values returned are those of the rows the two sides picked among equals.
        if (!ours.HasError && !theirs.HasError && ours.Rows.Count == theirs.Rows.Count
            && Regex.IsMatch(sql, @"\b(?:max|min)\s*\(\s*((?:\w+\.)?\w+)\s*\)[^;]*\bWHERE\s+(?:\1\s*(?:=|<>|!=|<=|>=|<|>)|[\w.]+\s*(?:=|<>|!=|<=|>=|<|>)\s*\1\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "comparison-affinity-in-place";
        }

        _ = diff;
        return Verdict.Unexplained;
    }

    // The elements of a TEXT cell (shown as T: and the hex of its UTF-8 bytes) with the outer brackets removed.
    private static string ListBody(string key)
    {
        if (key.StartsWith("T:", StringComparison.Ordinal) && key.Length % 2 == 0)
        {
            try
            {
                key = System.Text.Encoding.UTF8.GetString(Convert.FromHexString(key.AsSpan(2)));
            }
            catch (FormatException)
            {
                // not hex: compare the shown text as it is
            }
        }

        return key.Trim('[', ']');
    }

    // Every cell is equal, or is a text whose comma-separated elements (outer brackets ignored) are the same elements in another order.
    private static bool DifferOnlyInListOrder(Outcome ours, Outcome theirs)
    {
        bool anyPermuted = false;
        for (int i = 0; i < ours.Rows.Count; i++)
        {
            if (ours.Rows[i].Length != theirs.Rows[i].Length)
            {
                return false;
            }

            for (int j = 0; j < ours.Rows[i].Length; j++)
            {
                string a = ours.Rows[i][j].Key, b = theirs.Rows[i][j].Key;
                if (a == b)
                {
                    continue;
                }

                string[] ta = ListBody(a).Split(','), tb = ListBody(b).Split(',');
                if (ta.Length != tb.Length || ta.Length < 2 || !ta.Order(StringComparer.Ordinal).SequenceEqual(tb.Order(StringComparer.Ordinal)))
                {
                    return false;
                }

                anyPermuted = true;
            }
        }

        return anyPermuted;
    }

    // Two shown REAL values (digits and a trailing "r") that differ by a few units in the last place.
    private static bool NearbyReals(string a, string b)
    {
        if (!a.EndsWith('r') || !b.EndsWith('r') || !double.TryParse(a.AsSpan(0, a.Length - 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double x)
            || !double.TryParse(b.AsSpan(0, b.Length - 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double y))
        {
            return false;
        }

        return x != 0 && Math.Abs(x - y) <= Math.Abs(x) * 1e-15;
    }

    // Two texts that both read as the same number to within the last digit or two: the 17-digit rendering rule of docs/engine.md.
    private static bool DifferOnlyInRealText(Outcome a, Outcome b)
    {
        bool any = false;
        if (a.Rows.Count != b.Rows.Count || a.Rows.Zip(b.Rows).Any(p => p.First.Length != p.Second.Length))
        {
            return false;
        }

        var x = a.Rows.OrderBy(Join, StringComparer.Ordinal).ToList();
        var y = b.Rows.OrderBy(Join, StringComparer.Ordinal).ToList();
        // Rows are paired in order; if the real-text rendering moved a row in the sorted order, fall back to the unsorted pairing.
        for (int pass = 0; pass < 2; pass++)
        {
            var l = pass == 0 ? x : a.Rows;
            var r = pass == 0 ? y : b.Rows;
            bool ok = true;
            any = false;
            for (int i = 0; i < l.Count && ok; i++)
            {
                for (int j = 0; j < l[i].Length && ok; j++)
                {
                    Cell c = l[i][j], d = r[i][j];
                    if (c.Key == d.Key)
                    {
                        continue;
                    }

                    any = true;
                    ok = c.Key.StartsWith("T:", StringComparison.Ordinal) && d.Key.StartsWith("T:", StringComparison.Ordinal)
                        && SameNumber(Encoding.UTF8.GetString(Convert.FromHexString(c.Key[2..])), Encoding.UTF8.GetString(Convert.FromHexString(d.Key[2..])));
                }
            }

            if (ok && any)
            {
                return true;
            }
        }

        return false;
    }

    // Values of 1e15 or more (or 16 digits) in the data: a sum that adds and removes them keeps a rounding residue as large as their last digit.
    // Under RTRIM, "a" and "a  " are the same value; the shown text has its closing quote after the spaces.
    private static string RtrimShown(string t) => Regex.Replace(t, @" +(?='?$)", "");

    private static bool HasSecondaryIndex() => Setup is { } s && Regex.IsMatch(s, @"CREATE\s+(UNIQUE\s+)?INDEX|\bUNIQUE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool HugeInSetup() => Setup is { } s && Regex.IsMatch(s, @"\d{16}|\d[eE]\+?(1[5-9]|[2-9]\d|\d{3})", RegexOptions.CultureInvariant);

    // The shell prints text up to a NUL byte; OneLite returns all of it (shown without the NUL).
    private static bool NulTruncated(Cell c, Cell d)
    {
        if (Setup is not { } s || !s.Contains("char(0)", StringComparison.OrdinalIgnoreCase) && !s.Contains("x'00", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string p = c.Show, q = d.Show;
        return p.Length >= 2 && q.Length >= 2 && p[0] == '\'' && q[0] == '\'' && p != q
            && (p.TrimEnd('\'').StartsWith(q.TrimEnd('\''), StringComparison.Ordinal) || q.TrimEnd('\'').StartsWith(p.TrimEnd('\''), StringComparison.Ordinal));
    }

    private static bool DifferOnlyInNumbers(Outcome a, Outcome b, bool wide)
    {
        bool any = false;
        for (int i = 0; i < a.Rows.Count; i++)
        {
            if (a.Rows[i].Length != b.Rows[i].Length)
            {
                return false;
            }

            for (int j = 0; j < a.Rows[i].Length; j++)
            {
                Cell c = a.Rows[i][j], d = b.Rows[i][j];
                if (c.Key == d.Key)
                {
                    continue;
                }

                if (NulTruncated(c, d))
                {
                    continue;
                }

                any = true;
                if (!double.TryParse(c.Show.TrimEnd('r'), NumberStyles.Float, CultureInfo.InvariantCulture, out double p)
                    || !double.TryParse(d.Show.TrimEnd('r'), NumberStyles.Float, CultureInfo.InvariantCulture, out double q)
                    || (!wide && Math.Abs(p - q) > 1e-3 * Math.Max(1, Math.Max(Math.Abs(p), Math.Abs(q)))))
                {
                    return false;
                }
            }
        }

        return any;
    }

    private static bool DifferOnlyInNulText(Outcome a, Outcome b)
    {
        bool any = false;
        for (int i = 0; i < a.Rows.Count; i++)
        {
            if (a.Rows[i].Length != b.Rows[i].Length)
            {
                return false;
            }

            for (int j = 0; j < a.Rows[i].Length; j++)
            {
                if (a.Rows[i][j].Key == b.Rows[i][j].Key)
                {
                    continue;
                }

                if (!NulTruncated(a.Rows[i][j], b.Rows[i][j]))
                {
                    return false;
                }

                any = true;
            }
        }

        return any;
    }

    private static bool DifferOnlyInEqualValues(Outcome a, Outcome b)
    {
        bool any = false;
        for (int i = 0; i < a.Rows.Count; i++)
        {
            if (a.Rows[i].Length != b.Rows[i].Length)
            {
                return false;
            }

            for (int j = 0; j < a.Rows[i].Length; j++)
            {
                Cell c = a.Rows[i][j], d = b.Rows[i][j];
                if (c.Key == d.Key)
                {
                    continue;
                }

                if (NulTruncated(c, d))
                {
                    continue;
                }

                any = true;
                string p = c.Show.TrimEnd('r'), q = d.Show.TrimEnd('r');
                bool numbers = double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) && double.TryParse(q, NumberStyles.Float, CultureInfo.InvariantCulture, out double y) && x == y;
                if (!numbers && !string.Equals(RtrimShown(p), RtrimShown(q), StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return any;
    }

    // The rows of an outcome in an order that treats 1 and 1.0, and 'a' and 'A', alike, so two sides that kept different ones of equal values pair up.
    private static Outcome LooseSorted(Outcome o)
    {
        static string Loose(Cell c) => double.TryParse(c.Show.TrimEnd('r'), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? "n" + d.ToString("R", CultureInfo.InvariantCulture) : c.Show.ToLowerInvariant();
        var copy = new Outcome { Names = o.Names, HasError = o.HasError, Phase = o.Phase, Code = o.Code, Message = o.Message };
        copy.Rows = [.. o.Rows.OrderBy(r => string.Join("\u0001", r.Select(Loose)), StringComparer.Ordinal)];
        return copy;
    }

    private static string Join(Cell[] row) => string.Join("\u0001", row.Select(c => c.Key));

    private static bool SameNumber(string s, string t)
    {
        // Text that is a number, or that has numbers in it with only the number parts differing (e.g. "x1.2345678901234567y").
        var ns = Regex.Matches(s, @"-?\d+\.\d+(?:[eE][-+]?\d+)?");
        var nt = Regex.Matches(t, @"-?\d+\.\d+(?:[eE][-+]?\d+)?");
        if (ns.Count == 0 || ns.Count != nt.Count || Regex.Replace(s, @"-?\d+\.\d+(?:[eE][-+]?\d+)?", "#") != Regex.Replace(t, @"-?\d+\.\d+(?:[eE][-+]?\d+)?", "#"))
        {
            return false;
        }

        for (int i = 0; i < ns.Count; i++)
        {
            if (!double.TryParse(ns[i].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double p) || !double.TryParse(nt[i].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double q))
            {
                return false;
            }

            double tol = Math.Max(Math.Abs(p), Math.Abs(q)) * 4e-16;
            if (Math.Abs(p - q) > tol)
            {
                return false;
            }
        }

        return true;
    }
}
