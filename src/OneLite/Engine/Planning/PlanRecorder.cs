using System.Globalization;
using System.Linq;
using RecursiveAI.OneLite.Sql;

namespace RecursiveAI.OneLite.Planning;

/// <summary>An index the planner weighed for a table access, with the part of its key the WHERE clause could use and the score the planner gave it.</summary>
internal sealed record PlanCandidate(IndexInfo Index, AccessPlan Plan, int Score);

/// <summary>
/// Builds a <see cref="QueryPlan"/> while the compiler generates a program. The compiler tells it what it is emitting (a loop over a source with an
/// access plan, a subquery, a sort); nothing here decides anything, so the plan is the program's own structure. A compile that does not ask for a plan
/// holds no recorder, and every hook is a null check.
/// </summary>
internal sealed class PlanRecorder
{
    private const long DefaultTableRows = 1_048_576;
    private readonly Stack<PlanNode> _stack = new();
    private int _counters;

    internal PlanRecorder(string sql, string statementTitle, bool counting)
    {
        Sql = sql;
        Root = new PlanNode(PlanNodeKind.Statement, statementTitle);
        Counting = counting;
        _stack.Push(Root);
    }

    internal string Sql { get; }

    internal PlanNode Root { get; }

    /// <summary>True when the program should carry counters (an extra instruction per loop start and per row).</summary>
    internal bool Counting { get; }

    internal int CounterCount => _counters;

    private PlanNode Top => _stack.Peek();

    internal PlanNode Push(PlanNodeKind kind, string title)
    {
        var n = new PlanNode(kind, title);
        Top.Add(n);
        _stack.Push(n);
        return n;
    }

    internal void Pop(PlanNode n)
    {
        if (_stack.Count > 1 && ReferenceEquals(_stack.Peek(), n))
        {
            _stack.Pop();
        }
    }

    internal PlanNode Leaf(PlanNodeKind kind, string title, string detail = "")
    {
        var n = new PlanNode(kind, title) { Detail = detail };
        Top.Add(n);
        return n;
    }

    internal int NewCounter(PlanNode n)
    {
        n.CounterId = _counters++;
        return n.CounterId;
    }

    internal QueryPlan Build() => new(Sql, Root, _counters);

    // ------------------------------------------------------------------ table access

    /// <summary>Adds the node for a loop over <paramref name="s"/> read by <paramref name="plan"/>, and makes it the parent of whatever is emitted inside the loop.</summary>
    internal PlanNode Access(Source s, AccessPlan plan, string? note)
    {
        PlanNode n;
        switch (s.Kind)
        {
            case SourceKind.Eph:
                n = Push(PlanNodeKind.Scan, s.Alias);
                n.Detail = "(materialized result)";
                break;
            case SourceKind.Recursive:
                n = Push(PlanNodeKind.RecursiveCte, s.Alias);
                n.Detail = "(queue of the recursive part)";
                break;
            case SourceKind.Module:
                n = Push(PlanNodeKind.TableFunction, s.Module?.Name ?? s.Table.Name);
                if (!string.Equals(s.Alias, s.Module?.Name, StringComparison.OrdinalIgnoreCase))
                {
                    n.Title += " " + s.Alias;
                }

                break;
            case SourceKind.VTab:
            {
                n = Push(PlanNodeKind.TableFunction, s.Table.Name == s.Alias ? s.Table.Name : s.Table.Name + " " + s.Alias);
                n.Table = s.Table.Name;
                VtPlan v = s.Vt!;
                n.Detail = "(virtual table, filter " + v.IndexNumber.ToString(CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(v.IndexString) ? string.Empty : " '" + v.IndexString + "'")
                    + ", " + Plural(v.Args.Count, "argument") + ")";
                break;
            }

            default:
                n = TableAccess(s, plan);
                break;
        }

        if (note is not null)
        {
            n.Reason = note + (n.Reason is null ? string.Empty : "; " + n.Reason);
        }

        if (Counting)
        {
            NewCounter(n);
        }

        return n;
    }

    private PlanNode TableAccess(Source s, AccessPlan plan)
    {
        TableInfo t = s.Table;
        string title = string.Equals(s.Alias, t.Name, StringComparison.OrdinalIgnoreCase) || s.Alias.Length == 0 ? t.Name : t.Name + " " + s.Alias;
        bool searching = plan.Kind is PlanKind.RowidEq or PlanKind.RowidRange || plan.Equal.Count > 0 || plan.Lower is not null || plan.Upper is not null;
        PlanNode n = Push(searching ? PlanNodeKind.Search : PlanNodeKind.Scan, title);
        n.Table = t.Name;
        var reasons = new List<string>();
        long rows = DefaultTableRows;
        string rowidName = t.RowidAlias >= 0 ? t.Columns[t.RowidAlias].Name : "rowid";
        switch (plan.Kind)
        {
            case PlanKind.RowidEq:
                n.Detail = $"USING INTEGER PRIMARY KEY ({rowidName}={Text(plan.Equal[0].Value)})";
                reasons.Add($"a WHERE term fixes {rowidName}, so one row is read directly");
                rows = 1;
                break;
            case PlanKind.RowidRange:
            {
                var parts = new List<string>();
                if (plan.Lower is not null)
                {
                    parts.Add($"{rowidName}{Symbol(plan.Lower.Cmp)}{Text(plan.Lower.Value)}");
                    rows /= 4;
                }

                if (plan.Upper is not null)
                {
                    parts.Add($"{rowidName}{Symbol(plan.Upper.Cmp)}{Text(plan.Upper.Value)}");
                    rows /= 4;
                }

                n.Detail = $"USING INTEGER PRIMARY KEY ({string.Join(" AND ", parts)})";
                reasons.Add("WHERE bounds the rowid, so only that range of the table is read");
                break;
            }

            case PlanKind.Index:
            {
                IndexInfo idx = plan.Index!;
                n.Index = idx.Name;
                string kind = t.WithoutRowid && idx.IsPrimaryKey ? "PRIMARY KEY" : "INDEX " + idx.Name;
                var parts = new List<string>();
                for (int i = 0; i < plan.Equal.Count; i++)
                {
                    parts.Add($"{ColName(t, idx, i)}={Text(plan.Equal[i].Value)}");
                }

                int k = plan.Equal.Count;
                bool range = plan.Lower is not null || plan.Upper is not null;
                if (plan.Lower is not null)
                {
                    parts.Add($"{ColName(t, idx, k)}{Symbol(plan.Lower.Cmp)}{Text(plan.Lower.Value)}");
                }

                if (plan.Upper is not null)
                {
                    parts.Add($"{ColName(t, idx, k)}{Symbol(plan.Upper.Cmp)}{Text(plan.Upper.Value)}");
                }

                n.Detail = "USING " + kind + (parts.Count > 0 ? " (" + string.Join(" AND ", parts) + ")" : string.Empty);
                if (k > 0)
                {
                    rows = idx.Unique && k == idx.Columns.Length ? 1 : Math.Max(1, 10 / k);
                    reasons.Add($"{idx.Name} answers equality on {Names(t, idx, k)}" + (range ? $" and a range on {ColName(t, idx, k)}" : string.Empty));
                }
                else if (range)
                {
                    rows = DefaultTableRows / (plan.Lower is not null && plan.Upper is not null ? 16 : 4);
                    reasons.Add($"{idx.Name} answers a range on {ColName(t, idx, 0)}");
                }
                else
                {
                    reasons.Add($"{idx.Name} is read in full, in its own key order");
                }

                if (s.Forced is not null)
                {
                    reasons.Add("INDEXED BY names it");
                }

                break;
            }

            default:
                n.Detail = plan.Reverse ? "(reverse key order)" : string.Empty;
                reasons.Add(s.NotIndexed ? "NOT INDEXED: the table is read in full"
                    : "no WHERE term can be answered by the rowid or an index, so the table is read in full");
                break;
        }

        n.EstimatedRows = rows;
        if (plan.Reason is not null)
        {
            reasons.Clear();
            reasons.Add(plan.Reason);
        }

        reasons.AddRange(plan.Notes);
        n.Reason = string.Join("; ", reasons);
        if (plan.Considered is { } considered)
        {
            foreach (PlanCandidate c in considered)
            {
                if (ReferenceEquals(c.Index, plan.Index))
                {
                    continue;
                }

                n.AddRejected(Rejection(t, c, plan));
            }
        }

        return n;
    }

    private static string Rejection(TableInfo t, PlanCandidate c, AccessPlan chosen)
    {
        IndexInfo ix = c.Index;
        int k = c.Plan.Equal.Count;
        bool range = c.Plan.Lower is not null || c.Plan.Upper is not null;
        string who = (t.WithoutRowid && ix.IsPrimaryKey ? "PRIMARY KEY" : ix.Name) + ": ";
        if (k == 0 && !range)
        {
            return who + $"no WHERE term on its first column ({ColName(t, ix, 0)})";
        }

        string mine = k > 0 ? $"{k} equality column{(k == 1 ? "" : "s")}" + (range ? " and a range" : string.Empty) : "only a range";
        if (chosen.Index is null)
        {
            return who + $"could use {mine}, but another access path answers the query more narrowly";
        }

        int theirs = ScoreOf(chosen);
        return c.Score == theirs
            ? who + $"could use {mine}, as well as {chosen.Index.Name} does (both score {c.Score}); on a tie the planner prefers an index that holds every column the query reads, then the narrower one, then the one created last"
            : who + $"could use {mine}, which the planner scores {c.Score}; {chosen.Index.Name} scores {theirs}";
    }

    /// <summary>The planner's score of a plan: four per equality column and one per range bound.</summary>
    private static int ScoreOf(AccessPlan p) => (p.Equal.Count * 4) + (p.Lower is not null ? 1 : 0) + (p.Upper is not null ? 1 : 0);

    private static string Symbol(BinaryOp op) => op switch
    {
        BinaryOp.Gt => ">",
        BinaryOp.Ge => ">=",
        BinaryOp.Lt => "<",
        BinaryOp.Le => "<=",
        _ => "=",
    };

    private static string ColName(TableInfo t, IndexInfo ix, int i)
        => i < ix.Columns.Length ? (ix.Columns[i] < 0 ? "rowid" : t.Columns[ix.Columns[i]].Name) : "?";

    private static string Names(TableInfo t, IndexInfo ix, int count)
        => "(" + string.Join(", ", Enumerable.Range(0, count).Select(i => ColName(t, ix, i))) + ")";

    private static string Text(Expr e)
    {
        string s = SqlWriter.Write(e);
        return s.Length > 32 ? string.Concat(s.AsSpan(0, 29), "...") : s;
    }

    // ------------------------------------------------------------------ other steps

    internal void Sort(string what)
        => Leaf(PlanNodeKind.Sort, string.Empty, what).Reason = "the rows are not already in that order, so they go through a temporary sorter";

    private static string Plural(int n, string word) => n.ToString(CultureInfo.InvariantCulture) + " " + word + (n == 1 ? string.Empty : "s");
}
