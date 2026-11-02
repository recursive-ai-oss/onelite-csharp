using System.Globalization;
using System.Text;

namespace RecursiveAI.OneLite;

/// <summary>What a node of a <see cref="QueryPlan"/> does.</summary>
public enum PlanNodeKind
{
    /// <summary>The statement itself (the root).</summary>
    Statement,

    /// <summary>Visits every row of a table, an index (when the index alone is cheaper to read) or a materialized result, in key order.</summary>
    Scan,

    /// <summary>Finds rows through a rowid or an index: an equality, a range, or both.</summary>
    Search,

    /// <summary>Runs a subquery, view or common table expression once and keeps its rows in a temporary table.</summary>
    Materialize,

    /// <summary>A recursive common table expression: the queue it works from.</summary>
    RecursiveCte,

    /// <summary>A table-valued function such as <c>json_each</c>.</summary>
    TableFunction,

    /// <summary>A subquery used as a value, an <c>EXISTS</c> test or the right side of <c>IN</c>.</summary>
    Subquery,

    /// <summary>A compound query (<c>UNION</c>, <c>INTERSECT</c>, <c>EXCEPT</c>); its children are the arms.</summary>
    Compound,

    /// <summary>One SELECT of a compound query.</summary>
    Arm,

    /// <summary>Puts rows in order through a temporary sorter.</summary>
    Sort,

    /// <summary>Aggregates rows into one result or one per group.</summary>
    Aggregate,

    /// <summary>Removes duplicate rows.</summary>
    Distinct,

    /// <summary>Computes window functions.</summary>
    Window,
}

/// <summary>
/// One step of a query plan. The tree nests the way the work nests: a node's children run inside it, so the child of a scan is a loop that runs for
/// each row of the scan (a join's inner table), and a subquery under a scan runs for each of its rows when it is correlated.
/// </summary>
public sealed class PlanNode
{
    private readonly List<PlanNode> _children = [];
    private readonly List<string> _rejected = [];

    internal PlanNode(PlanNodeKind kind, string title)
    {
        Kind = kind;
        Title = title;
    }

    /// <summary>What the node does.</summary>
    public PlanNodeKind Kind { get; }

    /// <summary>What it works on: a table (with its alias when it has one), a subquery's name, or the statement's own verb for the root.</summary>
    public string Title { get; internal set; }

    /// <summary>The table the node reads, when it reads a table; otherwise null.</summary>
    public string? Table { get; internal set; }

    /// <summary>The index the node reads through (for a table whose rows are found through the primary key of a WITHOUT ROWID table, that key's name), or null for the table itself and for a rowid lookup.</summary>
    public string? Index { get; internal set; }

    /// <summary>How the rows are found, in words and symbols: <c>USING INDEX orders_user (user_id=?)</c>, <c>USING INTEGER PRIMARY KEY (rowid&gt;?)</c>.</summary>
    public string Detail { get; internal set; } = string.Empty;

    /// <summary>Why the planner chose this way (for a table access: which terms of the WHERE clause the index answers), or null.</summary>
    public string? Reason { get; internal set; }

    /// <summary>The other indexes the planner looked at and why each was not used.</summary>
    public IReadOnlyList<string> Rejected => _rejected;

    /// <summary>For a subquery or materialized result: "once" when it is computed one time per statement, "per outer row" when it names columns of the enclosing query.</summary>
    public string? Runs { get; internal set; }

    /// <summary>
    /// The planner's guess at the rows this node yields each time it starts, or null when it makes none. With no statistics it assumes a table holds about a million rows,
    /// an equality on an index column leaves 10, a range a quarter, and a full key of a unique index or a rowid exactly one.
    /// </summary>
    public long? EstimatedRows { get; internal set; }

    /// <summary>The rows this node yielded in all, counted while the statement ran; null unless the statement was prepared to count (and for nodes that are not table access).</summary>
    public long? ActualRows { get; internal set; }

    /// <summary>The number of times this node's loop was started (how often the outer loop reached it); null unless counted.</summary>
    public long? Loops { get; internal set; }

    /// <summary>The nodes that run inside this one, in the order they run.</summary>
    public IReadOnlyList<PlanNode> Children => _children;

    internal int CounterId { get; set; } = -1;

    internal void Add(PlanNode child) => _children.Add(child);

    internal void AddRejected(string text) => _rejected.Add(text);

    /// <summary>The one-line description: the verb, what it works on, how, and the counts.</summary>
    public string Label
    {
        get
        {
            var sb = new StringBuilder();
            string verb = Kind switch
            {
                PlanNodeKind.Scan => "SCAN",
                PlanNodeKind.Search => "SEARCH",
                PlanNodeKind.Materialize => "MATERIALIZE",
                PlanNodeKind.RecursiveCte => "RECURSIVE",
                PlanNodeKind.TableFunction => "CALL",
                PlanNodeKind.Subquery => "SUBQUERY",
                PlanNodeKind.Compound => "COMPOUND",
                PlanNodeKind.Arm => "SELECT",
                PlanNodeKind.Sort => "SORT",
                PlanNodeKind.Aggregate => "AGGREGATE",
                PlanNodeKind.Distinct => "DISTINCT",
                PlanNodeKind.Window => "WINDOW",
                _ => string.Empty,
            };
            sb.Append(verb);
            if (Title.Length > 0)
            {
                sb.Append(verb.Length > 0 ? " " : string.Empty).Append(Title);
            }

            if (Detail.Length > 0)
            {
                sb.Append(' ').Append(Detail);
            }

            if (Runs is not null)
            {
                sb.Append(" [").Append(Runs).Append(']');
            }

            if (EstimatedRows is not null || ActualRows is not null)
            {
                sb.Append("  (");
                if (EstimatedRows is { } e)
                {
                    sb.Append("est. ").Append(e.ToString(CultureInfo.InvariantCulture)).Append(e == 1 ? " row" : " rows");
                }

                if (ActualRows is { } a)
                {
                    sb.Append(EstimatedRows is null ? string.Empty : ", ").Append("actual ").Append(a.ToString(CultureInfo.InvariantCulture)).Append(a == 1 ? " row" : " rows");
                    if (Loops is { } l)
                    {
                        sb.Append(" in ").Append(l.ToString(CultureInfo.InvariantCulture)).Append(l == 1 ? " loop" : " loops");
                    }
                }

                sb.Append(')');
            }

            return sb.ToString();
        }
    }

    /// <summary>This node and every node below it, parents first.</summary>
    /// <returns>The nodes.</returns>
    public IEnumerable<PlanNode> Descendants()
    {
        yield return this;
        foreach (PlanNode c in _children)
        {
            foreach (PlanNode d in c.Descendants())
            {
                yield return d;
            }
        }
    }
}

/// <summary>
/// How a statement runs: a tree of <see cref="PlanNode"/>s taken from the very decisions the compiler made when it generated the program,
/// so the plan cannot disagree with what executes. Plans are not part of SQLite compatibility; their wording and the tree's shape may change.
/// </summary>
public sealed class QueryPlan
{
    internal QueryPlan(string sql, PlanNode root, int counters)
    {
        Sql = sql;
        Root = root;
        CounterCount = counters;
    }

    /// <summary>The statement text the plan is for.</summary>
    public string Sql { get; }

    /// <summary>The statement; its children are the steps.</summary>
    public PlanNode Root { get; }

    /// <summary>True when the statement was prepared to count rows and has run (<see cref="PlanNode.ActualRows"/> is filled in).</summary>
    public bool HasActual { get; internal set; }

    internal int CounterCount { get; }

    /// <summary>Copies the counters of a run into the nodes.</summary>
    internal void Fill(long[] counts)
    {
        foreach (PlanNode n in Root.Descendants())
        {
            if (n.CounterId >= 0 && n.CounterId * 2 + 1 < counts.Length)
            {
                n.Loops = counts[n.CounterId * 2];
                n.ActualRows = counts[(n.CounterId * 2) + 1];
            }
        }

        HasActual = true;
    }

    /// <summary>Renders the plan as indented text, one line per node, with the reasons under the nodes that have them.</summary>
    /// <returns>Text with <c>\n</c> line ends.</returns>
    public string Render()
    {
        var sb = new StringBuilder();
        Write(sb, Root, string.Empty, true, true);
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>The same as <see cref="Render"/>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => Render();

    private static void Write(StringBuilder sb, PlanNode n, string prefix, bool last, bool root)
    {
        sb.Append(prefix);
        if (!root)
        {
            sb.Append(last ? "`-- " : "|-- ");
        }

        sb.Append(n.Label).Append('\n');
        string childPrefix = root ? string.Empty : prefix + (last ? "    " : "|   ");
        string notePrefix = childPrefix + (n.Children.Count > 0 ? "|   " : "    ");
        if (root)
        {
            notePrefix = n.Children.Count > 0 ? "|   " : "    ";
        }

        if (n.Reason is not null)
        {
            sb.Append(notePrefix).Append("why: ").Append(n.Reason).Append('\n');
        }

        foreach (string r in n.Rejected)
        {
            sb.Append(notePrefix).Append("not used: ").Append(r).Append('\n');
        }

        for (int i = 0; i < n.Children.Count; i++)
        {
            Write(sb, n.Children[i], childPrefix, i == n.Children.Count - 1, false);
        }
    }
}
