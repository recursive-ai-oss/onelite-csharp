using System.Linq;
using System.Buffers.Binary;
using RecursiveAI.OneLite.Storage.Vfs;
using Xunit;

namespace RecursiveAI.OneLite.Data.Tests;

[OneLiteRow(TableName = "docs")]
public sealed record Doc(long Id, string Body, int Year);

/// <summary>Hybrid search: full-text and vector lists fused by reciprocal rank fusion.</summary>
public sealed class SearchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly (long Id, string Body, int Year, float[] V)[] Rows =
    [
        (1, "overdue invoice reminder", 2023, [0f, 1f, 0f]),
        (2, "invoice paid in full", 2024, [0.5f, 0.5f, 0f]),
        (3, "overdue notice for rent", 2024, [0.9f, 0.1f, 0f]),
        (4, "holiday schedule", 2025, [1f, 0f, 0f]),
        (5, "quarterly report", 2025, [0f, 0f, 1f]),
        (6, "zebra crossing", 2022, [-1f, 0f, 0f]),
    ];

    private static byte[] Blob(float[] v)
    {
        var b = new byte[v.Length * 4];
        for (int i = 0; i < v.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i * 4), v[i]);
        }

        return b;
    }

    private static async Task Populate(Database db)
    {
        await db.ExecuteScriptAsync(
            "create table docs(id integer primary key, body text not null, year integer not null) strict;"
            + "create virtual table docs_fts using fts5(body);"
            + "create virtual table docs_vec using vec0(embedding float[3]);", Ct);
        foreach ((long id, string body, int year, float[] v) in Rows)
        {
            await db.ExecuteAsync($"insert into docs values ({id}, {body}, {year})", Ct);
            await db.ExecuteAsync($"insert into docs_fts(rowid, body) values ({id}, {body})", Ct);
            await db.ExecuteAsync($"insert into docs_vec(rowid, embedding) values ({id}, {Blob(v)})", Ct);
        }
    }

    private static async Task<Database> Open()
    {
        Database db = await Database.OpenAsync(":memory:", null, Ct);
        await Populate(db);
        return db;
    }

    private static readonly float[] Q = [1f, 0f, 0f];

    [Fact]
    public async Task FusionEqualsAHandComputedRrf()
    {
        await using Database db = await Open();
        // The two lists as the indexes give them, then RRF by hand: 1/(60+rank) summed.
        List<long> text = await db.QueryAsync<long>($"select rowid from docs_fts where docs_fts match {"\"overdue\" OR \"invoice\""} order by rank", Ct);
        List<long> vec = await db.QueryAsync<long>($"select rowid from docs_vec where embedding match {Blob(Q)} and k = {100}", Ct);
        Assert.Equal([4L, 3L, 2L, 1L, 5L, 6L], vec);
        Assert.Equal(1L, text[0]); // the only row with both words
        var expected = new Dictionary<long, double>();
        for (int i = 0; i < text.Count; i++)
        {
            expected[text[i]] = expected.GetValueOrDefault(text[i]) + (1.0 / (60 + i + 1));
        }

        for (int i = 0; i < vec.Count; i++)
        {
            expected[vec[i]] = expected.GetValueOrDefault(vec[i]) + (1.0 / (60 + i + 1));
        }

        long[] order = expected.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => kv.Key).ToArray();

        List<SearchHit<Doc>> hits = await db.SearchAsync<Doc>("docs", "overdue invoice", Q, 10, null, Ct);
        Assert.Equal(order, hits.Select(h => h.RowId).ToArray());
        Assert.Equal(order.Length, hits.Count);
        foreach (SearchHit<Doc> h in hits)
        {
            Assert.Equal(expected[h.RowId], h.Score, 15);
            Assert.Equal(h.RowId, h.Item.Id);
            Assert.Equal(Rows[(int)h.RowId - 1].Body, h.Item.Body);
            Assert.Equal(text.Contains(h.RowId) ? text.IndexOf(h.RowId) + 1 : null, h.TextRank);
            Assert.Equal(vec.IndexOf(h.RowId) + 1, h.VectorRank);
        }

        // Row 1: text rank 1, vector rank 4.
        Assert.Equal(1.0 / 61 + 1.0 / 64, hits.Single(h => h.RowId == 1).Score, 15);
        // Sync twin agrees.
        Assert.Equal(order, db.Search<Doc>("docs", "overdue invoice", Q).Select(h => h.RowId).ToArray());
    }

    [Fact]
    public async Task TextOnlyVectorOnlyAndK()
    {
        await using Database db = await Open();
        List<SearchHit<Doc>> t = await db.SearchAsync<Doc>("docs", "zebra", default, 10, null, Ct);
        SearchHit<Doc> only = Assert.Single(t); // k larger than the matches
        Assert.Equal(6L, only.RowId);
        Assert.Equal(1, only.TextRank);
        Assert.Null(only.VectorRank);
        Assert.Equal(1.0 / 61, only.Score, 15);

        List<SearchHit<Doc>> v = await db.SearchAsync<Doc>("docs", null, Q, 3, null, Ct);
        Assert.Equal([4L, 3L, 2L], v.Select(h => h.RowId).ToArray());
        Assert.All(v, h => Assert.Null(h.TextRank));

        await Assert.ThrowsAsync<ArgumentException>(async () => await db.SearchAsync<Doc>("docs", "   ", default, 10, null, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await db.SearchAsync<Doc>("docs", "x", default, 0, null, Ct));
    }

    [Fact]
    public async Task EqualScoresOrderByRowId()
    {
        await using Database db = await Open();
        // Depth 1: row 6 is the only text hit, row 4 the only vector hit; both score 1/61.
        var o = new SearchOptions { Depth = 1 };
        List<SearchHit<Doc>> hits = await db.SearchAsync<Doc>("docs", "zebra", Q, 10, o, Ct);
        Assert.Equal([4L, 6L], hits.Select(h => h.RowId).ToArray());
        Assert.Equal(hits[0].Score, hits[1].Score);
        // Weights break the tie the other way.
        hits = await db.SearchAsync<Doc>("docs", "zebra", Q, 10, new SearchOptions { Depth = 1, TextWeight = 2 }, Ct);
        Assert.Equal([6L, 4L], hits.Select(h => h.RowId).ToArray());
        Assert.Equal(2.0 / 61, hits[0].Score, 15);
        // A different constant changes the score.
        hits = await db.SearchAsync<Doc>("docs", "zebra", default, 10, new SearchOptions { RrfConstant = 10 }, Ct);
        Assert.Equal(1.0 / 11, hits[0].Score, 15);
    }

    [Theory]
    [InlineData("\"overdue\" OR (invoice* NEAR/3 ^x) body:y AND NOT -- ' ; \"")]
    [InlineData("overdue\" invoice\"\"")]
    [InlineData("***")]
    [InlineData("AND OR NOT NEAR ( ) { } : ^ + - * \"")]
    [InlineData("'; drop table docs; --")]
    public async Task SyntaxCharactersAreSearchedAsWordsNotParsed(string text)
    {
        await using Database db = await Open();
        List<SearchHit<Doc>> hits = await db.SearchAsync<Doc>("docs", text, Q, 10, null, Ct);
        Assert.NotNull(hits);
        Assert.Equal(6L, await db.ScalarAsync<long>($"select count(*) from docs", Ct));
        // Operators stay words: "overdue AND zebra" does not mean both, and with AND-ed words none matches.
        List<SearchHit<Doc>> none = await db.SearchAsync<Doc>("docs", "overdue AND zebra", default, 10, new SearchOptions { MatchAllWords = true }, Ct);
        Assert.Empty(none);
        List<SearchHit<Doc>> any = await db.SearchAsync<Doc>("docs", "overdue zebra", default, 10, null, Ct);
        Assert.Equal([3L, 6L, 1L], any.Select(h => h.RowId).OrderBy(x => x == 3 ? 0 : x == 6 ? 1 : 2).ToArray());
    }

    [Fact]
    public async Task AFilterAppliesInsideBothLists()
    {
        await using Database db = await Open();
        int from = 2025;
        List<SearchHit<Doc>> hits = await db.SearchAsync<Doc>("docs", "overdue invoice", Q, $"year >= {from}", 10, null, Ct);
        Assert.Equal([4L, 5L], hits.Select(h => h.RowId).ToArray()); // vector list only: no text match in 2025
        Assert.All(hits, h => Assert.True(h.Item.Year >= 2025));

        // The best rows that satisfy the filter, not the best rows filtered afterwards: k = 1 among 2024.
        int y = 2024;
        List<SearchHit<Doc>> one = await db.SearchAsync<Doc>("docs", null, Q, $"year = {y}", 1, null, Ct);
        Assert.Equal(3L, Assert.Single(one).RowId);
        // A hole in the filter is a parameter.
        string evil = "0 or 1=1";
        Assert.Empty(await db.SearchAsync<Doc>("docs", "overdue", Q, $"cast(year as text) = {evil}", 10, null, Ct));
    }

    [Fact]
    public async Task RunsInsideAWriteTransactionAndSeesItsWrites()
    {
        await using Database db = await Open();
        List<long> ids = await db.WriteAsync(async tx =>
        {
            await tx.ExecuteAsync($"insert into docs values (7, {"overdue overdue invoice invoice"}, 2026)", Ct);
            await tx.ExecuteAsync($"insert into docs_fts(rowid, body) values (7, {"overdue overdue invoice invoice"})", Ct);
            List<SearchHit<Doc>> h = await tx.SearchAsync<Doc>("docs", "overdue invoice", default, 10, null, Ct);
            return h.Select(x => x.RowId).ToList();
        }, Ct);
        Assert.Contains(7L, ids);
    }

    [Fact]
    public async Task MissingIndexAndWrongDimensionKeepSqliteErrors()
    {
        await using Database db = await Open();
        await Assert.ThrowsAsync<OneLiteException>(async () => await db.SearchAsync<Doc>("docs", "x", default, 10, new SearchOptions { FtsTable = "nope" }, Ct));
        await Assert.ThrowsAsync<OneLiteException>(async () => await db.SearchAsync<Doc>("docs", null, new float[] { 1, 2 }, 10, null, Ct));
        // The connection is usable afterwards (the read transaction was closed).
        Assert.NotEmpty(await db.SearchAsync<Doc>("docs", "zebra", default, 10, null, Ct));
    }

    [Fact]
    public async Task RunsOverABackendWhoseReadsCompleteAsynchronously()
    {
        var mem = new MemoryVfs();
        await using (Database w = await Database.OpenAsync("s.db", new DatabaseOptions { Vfs = mem }, Ct))
        {
            await Populate(w);
        }

        var slow = new SlowVfs(mem);
        await using Database db = await Database.OpenAsync("s.db", new DatabaseOptions { Vfs = slow }, Ct);
        slow.ResetCounters();
        List<SearchHit<Doc>> hits = await db.SearchAsync<Doc>("docs", "overdue invoice", Q, 3, null, Ct);
        Assert.Equal(3, hits.Count);
        Assert.Equal(1L, hits.Single(h => h.TextRank == 1).RowId);
        Assert.True(slow.AsyncReads > 0, "a cold search must have read through the async backend");
        Assert.True(slow.IncompleteReads > 0, "the cache misses must have suspended the search");
    }
}
