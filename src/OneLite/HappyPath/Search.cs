using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace RecursiveAI.OneLite;

/// <summary>
/// Settings for <c>SearchAsync</c>. The defaults follow a naming convention: for a content table <c>docs</c>, the full-text index is the FTS5 table
/// <c>docs_fts</c> and the vector index is the <c>vec0</c> table <c>docs_vec</c> with a vector column <c>embedding</c>. In both, the row id of an entry
/// is the row id of its row in <c>docs</c>.
/// </summary>
public sealed class SearchOptions
{
    /// <summary>The FTS5 table; null means the content table's name plus <c>_fts</c>. Used as a trusted identifier, never from user input.</summary>
    public string? FtsTable { get; init; }

    /// <summary>The <c>vec0</c> table; null means the content table's name plus <c>_vec</c>. Used as a trusted identifier, never from user input.</summary>
    public string? VecTable { get; init; }

    /// <summary>The vector column of the <c>vec0</c> table. Default <c>embedding</c>. Used as a trusted identifier.</summary>
    public string VectorColumn { get; init; } = "embedding";

    /// <summary>The constant added to a rank before it is inverted, <c>score = weight / (RrfConstant + rank)</c>. Default 60, the value of the original paper. Must be positive.</summary>
    public int RrfConstant { get; init; } = 60;

    /// <summary>Multiplies the full-text list's contribution. Default 1.</summary>
    public double TextWeight { get; init; } = 1;

    /// <summary>Multiplies the vector list's contribution. Default 1.</summary>
    public double VectorWeight { get; init; } = 1;

    /// <summary>How many entries each of the two lists contributes to the fusion. Default 0, which means <c>max(100, 4 * k)</c>. Deeper lists make the fusion more faithful at a small cost.</summary>
    public int Depth { get; init; }

    /// <summary>When true, a row must contain every word of the query to match the text list; when false (the default) any word matches and bm25 ranks rows with more words higher.</summary>
    public bool MatchAllWords { get; init; }
}

/// <summary>One result of <c>SearchAsync</c>: the mapped row, its fused score and the rank it had in each list.</summary>
/// <typeparam name="T">The row type.</typeparam>
/// <param name="Item">The row, mapped by the generated mapper.</param>
/// <param name="RowId">The row id in the content table.</param>
/// <param name="Score">The reciprocal-rank-fusion score: the sum over the lists that contain the row of <c>weight / (constant + rank)</c>. Higher is better.</param>
/// <param name="TextRank">The 1-based rank in the full-text (bm25) list, or null if the row is not in it.</param>
/// <param name="VectorRank">The 1-based rank in the vector (nearest-neighbour) list, or null if the row is not in it.</param>
public readonly record struct SearchHit<T>(T Item, long RowId, double Score, int? TextRank, int? VectorRank);

/// <summary>Builds and fuses the searches. Shared by the database and a write transaction, sync and async.</summary>
internal static class Searcher
{
    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    internal sealed class Plan
    {
        internal required string Content { get; init; }
        internal string? MatchText { get; init; }
        internal byte[]? Vector { get; init; }
        internal required int K { get; init; }
        internal required SearchOptions Options { get; init; }
        internal Query? Filter { get; init; }

        internal int Depth => Options.Depth > 0 ? Options.Depth : Math.Max(100, checked(4 * K));
    }

    internal static Plan Make(string content, string? text, ReadOnlyMemory<float> embedding, int k, SearchOptions? options, Query? filter)
    {
        ArgumentException.ThrowIfNullOrEmpty(content);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);
        options ??= new SearchOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.RrfConstant);
        ArgumentOutOfRangeException.ThrowIfNegative(options.Depth);
        string? match = text is null ? null : ToMatch(text, options.MatchAllWords);
        byte[]? vec = null;
        if (!embedding.IsEmpty)
        {
            vec = new byte[embedding.Length * 4];
            ReadOnlySpan<float> src = embedding.Span;
            for (int i = 0; i < src.Length; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(vec.AsSpan(i * 4), src[i]);
            }
        }

        if (match is null && vec is null)
        {
            throw new ArgumentException("give search text with at least one word, an embedding, or both");
        }

        return new Plan { Content = content, MatchText = match, Vector = vec, K = k, Options = options, Filter = filter };
    }

    /// <summary>
    /// Makes user text safe for FTS5 <c>MATCH</c>: every whitespace-separated word becomes a quoted string (a double quote inside it is doubled, fts5 "FTS5 Strings"),
    /// so operators, column filters, <c>NEAR</c>, <c>*</c>, parentheses and <c>^</c> in the text are searched as words, never parsed. Words are joined with OR (or AND).
    /// Returns null when no word is left.
    /// </summary>
    internal static string? ToMatch(string text, bool all)
    {
        var sb = new StringBuilder();
        foreach (string word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string w = word.Replace("\0", string.Empty, StringComparison.Ordinal);
            if (w.Length == 0)
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(all ? " AND " : " OR ");
            }

            sb.Append('"').Append(w.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>The filter's holes are ?1..?n; our own parameters follow, numbered n+1 and n+2.</summary>
    private static Query WithArgs(Plan p, string sqlBefore, object? first, object? second)
    {
        int n = p.Filter?.Args.Length ?? 0;
        var args = new object?[n + 2];
        p.Filter?.Args.CopyTo(args, 0);
        args[n] = first;
        args[n + 1] = second;
        return new Query(sqlBefore, args);
    }

    private static string P(Plan p, int i) => "?" + ((p.Filter?.Args.Length ?? 0) + i).ToString(CultureInfo.InvariantCulture);

    private static string FilterClause(Plan p) =>
        p.Filter is { } f ? " AND rowid IN (SELECT rowid FROM " + Quote(p.Content) + " WHERE (" + f.Sql + "))" : string.Empty;

    internal static Query TextQuery(Plan p)
    {
        string fts = Quote(p.Options.FtsTable ?? p.Content + "_fts");
        return WithArgs(p, "SELECT rowid, rank FROM " + fts + " WHERE " + fts + " MATCH " + P(p, 1) + FilterClause(p) + " ORDER BY rank LIMIT " + P(p, 2), p.MatchText, (long)p.Depth);
    }

    internal static Query VectorQuery(Plan p)
    {
        string vec = Quote(p.Options.VecTable ?? p.Content + "_vec");
        return WithArgs(p, "SELECT rowid, distance FROM " + vec + " WHERE " + Quote(p.Options.VectorColumn) + " MATCH " + P(p, 1) + " AND k = " + P(p, 2) + FilterClause(p), p.Vector, (long)p.Depth);
    }

    internal static Query FetchQuery(Plan p, List<long> ids)
    {
        var sb = new StringBuilder("SELECT rowid, * FROM ").Append(Quote(p.Content)).Append(" WHERE rowid IN (");
        for (int i = 0; i < ids.Count; i++)
        {
            sb.Append(i == 0 ? "?" : ", ?").Append((i + 1).ToString(CultureInfo.InvariantCulture));
        }

        var args = new object?[ids.Count];
        for (int i = 0; i < args.Length; i++)
        {
            args[i] = ids[i];
        }

        return new Query(sb.Append(')').ToString(), args);
    }

    /// <summary>
    /// Reciprocal rank fusion (Cormack, Clarke and Büttcher, SIGIR 2009): each list gives a row <c>weight / (constant + rank)</c>, ranks counted from 1; the scores add.
    /// Highest score first; equal scores order by the smaller row id, so the result is deterministic.
    /// </summary>
    internal static List<(long RowId, double Score, int? TextRank, int? VectorRank)> Fuse(IReadOnlyList<long> text, IReadOnlyList<long> vector, SearchOptions o, int k)
    {
        var acc = new Dictionary<long, (double Score, int? T, int? V)>();
        for (int i = 0; i < text.Count; i++)
        {
            acc.TryGetValue(text[i], out var cur);
            acc[text[i]] = (cur.Score + (o.TextWeight / (o.RrfConstant + i + 1)), cur.T ?? (i + 1), cur.V);
        }

        for (int i = 0; i < vector.Count; i++)
        {
            acc.TryGetValue(vector[i], out var cur);
            acc[vector[i]] = (cur.Score + (o.VectorWeight / (o.RrfConstant + i + 1)), cur.T, cur.V ?? (i + 1));
        }

        var list = new List<(long RowId, double Score, int? TextRank, int? VectorRank)>(acc.Count);
        foreach (KeyValuePair<long, (double Score, int? T, int? V)> kv in acc)
        {
            list.Add((kv.Key, kv.Value.Score, kv.Value.T, kv.Value.V));
        }

        list.Sort(static (a, b) =>
        {
            int c = b.Score.CompareTo(a.Score);
            return c != 0 ? c : a.RowId.CompareTo(b.RowId);
        });
        if (list.Count > k)
        {
            list.RemoveRange(k, list.Count - k);
        }

        return list;
    }

    private static List<SearchHit<T>> Assemble<T>(List<(long RowId, double Score, int? TextRank, int? VectorRank)> top, Dictionary<long, T> rows)
    {
        var result = new List<SearchHit<T>>(top.Count);
        foreach ((long id, double score, int? tr, int? vr) in top)
        {
            // A row deleted between the lists and the fetch is skipped; inside one read transaction this does not happen.
            if (rows.TryGetValue(id, out T? item))
            {
                result.Add(new SearchHit<T>(item, id, score, tr, vr));
            }
        }

        return result;
    }

    private static List<long> Ids(List<(long, double)> rows)
    {
        var ids = new List<long>(rows.Count);
        foreach ((long id, _) in rows)
        {
            ids.Add(id);
        }

        return ids;
    }

    private static (long, double) ReadPair(Statement st) => (st.GetFieldValue<long>(0), st.GetFieldValue<double>(1));

    internal static async ValueTask<List<SearchHit<T>>> RunAsync<T>(Connection c, Plan p, CancellationToken ct)
    {
        bool own = c.AutoCommit;
        if (own)
        {
            // One read transaction, so the lists and the rows come from one snapshot.
            await Runner.ScriptAsync(c, "BEGIN", ct).ConfigureAwait(false);
        }

        try
        {
            List<long> text = [];
            List<long> vector = [];
            if (p.MatchText is not null)
            {
                text = Ids(await Runner.QueryRowsAsync(c, TextQuery(p), ReadPair, ct).ConfigureAwait(false));
            }

            if (p.Vector is not null)
            {
                vector = Ids(await Runner.QueryRowsAsync(c, VectorQuery(p), ReadPair, ct).ConfigureAwait(false));
            }

            var top = Fuse(text, vector, p.Options, p.K);
            if (top.Count == 0)
            {
                return [];
            }

            Query fetch = FetchQuery(p, top.ConvertAll(static h => h.RowId));
            var rows = new Dictionary<long, T>();
            await Runner.FetchAsync(c, fetch, rows, ct).ConfigureAwait(false);
            return Assemble(top, rows);
        }
        finally
        {
            if (own)
            {
                await Runner.ScriptAsync(c, "ROLLBACK", CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    internal static List<SearchHit<T>> Run<T>(Connection c, Plan p)
    {
        bool own = c.AutoCommit;
        if (own)
        {
            Runner.Script(c, "BEGIN");
        }

        try
        {
            List<long> text = [];
            List<long> vector = [];
            if (p.MatchText is not null)
            {
                text = Ids(Runner.QueryRows(c, TextQuery(p), ReadPair));
            }

            if (p.Vector is not null)
            {
                vector = Ids(Runner.QueryRows(c, VectorQuery(p), ReadPair));
            }

            var top = Fuse(text, vector, p.Options, p.K);
            if (top.Count == 0)
            {
                return [];
            }

            var rows = new Dictionary<long, T>();
            Runner.Fetch(c, FetchQuery(p, top.ConvertAll(static h => h.RowId)), rows);
            return Assemble(top, rows);
        }
        finally
        {
            if (own)
            {
                Runner.Script(c, "ROLLBACK");
            }
        }
    }
}
