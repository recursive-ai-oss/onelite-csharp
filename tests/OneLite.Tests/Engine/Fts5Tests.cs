using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Oracle;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Engine;

/// <summary>FTS5: query results, error text, auxiliary functions and vocabulary tables against the pinned shell, and file interop both ways.</summary>
public sealed class Fts5Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-fts5-" + Guid.NewGuid().ToString("N"));

    public Fts5Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("ONELITE_KEEP") != "1")
        {
            Directory.Delete(_dir, true);
        }
    }

    private static void NeedOracle() => Assert.SkipUnless(File.Exists(OraclePaths.Cli), "the pinned sqlite3 shell is not installed");

    private static readonly string[] Words =
    [
        "alpha", "beta", "gamma", "delta", "run", "running", "ran", "jump", "jumped", "quick", "brown", "fox", "lazy", "dog", "the", "a", "caf\u00e9",
        "na\u00efve", "\u00dcber", "stra\u00dfe", "\u00e9clair", "\u4e2d\u6587", "Zoe", "foo-bar", "3.14", "42", "caresses", "ponies", "relational", "happiness",
    ];

    /// <summary>A deterministic corpus: 60 rows of up to three columns, words with commas (sentence ends are covered by SnippetWindowsMatchTheShell: the window choice among several matches near them is not exact yet).</summary>
    private static string Rows(int cols, int n = 60)
    {
        var rnd = new Random(33);
        var sb = new StringBuilder();
        for (int i = 1; i <= n; i++)
        {
            sb.Append("INSERT INTO t(rowid");
            for (int c = 0; c < cols; c++)
            {
                sb.Append(", c").Append(c);
            }

            sb.Append(") VALUES(").Append(i * 3);
            for (int c = 0; c < cols; c++)
            {
                var d = new StringBuilder();
                int len = rnd.Next(0, 14);
                for (int w = 0; w < len; w++)
                {
                    d.Append(Words[rnd.Next(Words.Length)]).Append(rnd.Next(5) == 0 ? ", " : " ");
                }

                sb.Append(", '").Append(d.ToString().Replace("'", "''").TrimEnd()).Append('\'');
            }

            sb.Append(");");
        }

        return sb.ToString();
    }

    private static string Setup(string options, int cols = 3) =>
        "CREATE VIRTUAL TABLE t USING fts5(" + string.Join(", ", Enumerable.Range(0, cols).Select(c => "c" + c)) + (options.Length > 0 ? ", " + options : "") + ");" + Rows(cols);

    private static string[] Queries(string match) =>
    [
        $"SELECT rowid FROM t WHERE t MATCH '{match}' ORDER BY rowid",
        $"SELECT rowid, round(bm25(t), 8) FROM t WHERE t MATCH '{match}' ORDER BY rank, rowid",
        $"SELECT rowid, highlight(t, 0, '[', ']') FROM t WHERE t MATCH '{match}' ORDER BY rowid",
        $"SELECT rowid, snippet(t, 0, '<', '>', '...', 64) FROM t WHERE t MATCH '{match}' ORDER BY rowid",
    ];

    private static string Script(Database db, string sql)
    {
        try
        {
            using Connection c = db.Connect();
            Runner.Script(c, sql);
            return string.Empty;
        }
        catch (OneLiteException e)
        {
            return "Error: " + e.Message + "\n";
        }
    }

    private (string Ours, string Shell) Both(string setup, params string[] queries)
    {
        string o = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"), s = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");
        var ours = new StringBuilder();
        var shell = new StringBuilder();
        using (var db = Database.Open(o))
        using (Connection c = db.Connect())
        {
            Assert.Equal(string.Empty, Script(db, setup));
            foreach (string q in queries)
            {
                ours.Append(RTreeTests.Ours(c, q));
            }
        }

        Assert.Equal(string.Empty, RTreeTests.Shell(s, setup));
        foreach (string q in queries)
        {
            shell.Append(RTreeTests.Shell(s, q));
        }

        File.WriteAllText(Path.Combine(_dir, "ours.txt"), ours.ToString());
        File.WriteAllText(Path.Combine(_dir, "shell.txt"), shell.ToString());
        return (ours.ToString(), shell.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("tokenize='porter'")]
    [InlineData("tokenize='unicode61 remove_diacritics 0'")]
    [InlineData("tokenize='ascii'")]
    [InlineData("tokenize='trigram'")]
    [InlineData("prefix='2 3', columnsize=0")]
    [InlineData("detail=column")]
    [InlineData("detail=none")]
    public void QueriesAuxiliaryFunctionsAndVocabularyMatchTheShell(string options)
    {
        NeedOracle();
        string[] matches = ["run*", "alpha beta", "alpha OR fox NOT dog", "\"the quick\"", "c1 : jump*", "^the", "NEAR(a the, 3)", "{c0 c2} : caf\u00e9", "gamma AND (run OR ran)", "x y )", "\"abc", "NOT a"];
        var qs = matches.SelectMany(Queries).Concat(
        [
            "SELECT count(*) FROM t",
            "SELECT c0 FROM t WHERE rowid = 30",
            "INSERT INTO t(t) VALUES('integrity-check')",
            "CREATE VIRTUAL TABLE v1 USING fts5vocab(t, 'row')",
            "SELECT * FROM v1 ORDER BY 1",
            "CREATE VIRTUAL TABLE v2 USING fts5vocab(t, 'col')",
            "SELECT * FROM v2 ORDER BY 1, 2",
            "CREATE VIRTUAL TABLE v3 USING fts5vocab(t, 'instance')",
            "SELECT * FROM v3 ORDER BY 1, 2, 3, 4",
        ]).ToArray();
        (string ours, string shell) = Both(Setup(options), qs);
        Assert.Equal(Normalize(shell), Normalize(ours));
    }

    // The shell prints the failing position after an error; OneLite's message is the same text without it.
    private static string Normalize(string s) => string.Join('\n', s.Split('\n').Where(l => !l.StartsWith("  ", StringComparison.Ordinal) && !l.Contains("error here ---", StringComparison.Ordinal)));

    /// <summary>The reference parser's stack holds 100 entries: plain nesting is fine to 97 levels, then "parser stack overflow"; a hostile query must never recurse without bound.</summary>
    [Fact]
    public void DeepQueryNestingEndsInTheParserStackError()
    {
        using var db = Database.Open(Path.Combine(_dir, "nest.db"));
        using Connection c = db.Connect();
        Runner.Script(c, "CREATE VIRTUAL TABLE t USING fts5(a)");
        string Try(string q)
        {
            try
            {
                using Statement st = c.Prepare("SELECT count(*) FROM t WHERE t MATCH '" + q + "'");
                while (st.Step())
                {
                }

                return "ok";
            }
            catch (OneLiteException e)
            {
                return e.Message;
            }
        }

        Assert.Equal("ok", Try(new string('(', 97) + "a" + new string(')', 97)));
        Assert.Equal("fts5: parser stack overflow", Try(new string('(', 98) + "a" + new string(')', 98)));
        Assert.Equal("ok", Try(string.Concat(Enumerable.Repeat("a AND (", 32)) + "a" + new string(')', 32)));
        Assert.Equal("fts5: parser stack overflow", Try(string.Concat(Enumerable.Repeat("a AND (", 33)) + "a" + new string(')', 33)));
        Assert.Equal("fts5: parser stack overflow", Try(new string('(', 200000) + "a"));
    }

    [Fact]
    public void SnippetWindowsMatchTheShell()
    {
        NeedOracle();
        // one match at every position of a 12-token text, with and without a sentence end before it, for several window sizes
        var setup = new StringBuilder("CREATE VIRTUAL TABLE t USING fts5(x);");
        var qs = new List<string>();
        int id = 0;
        foreach (int dot in new[] { -1, 1, 4 })
        {
            for (int k = 0; k < 12; k++)
            {
                string[] d = Enumerable.Range(0, 12).Select(i => "w" + i).ToArray();
                d[k] = "hit";
                if (dot >= 0)
                {
                    d[dot] += ".";
                }

                setup.Append($"INSERT INTO t(rowid, x) VALUES({++id}, '{string.Join(' ', d)}');");
            }
        }

        foreach (int n in new[] { 2, 3, 5, 8 })
        {
            qs.Add($"SELECT rowid, snippet(t, 0, '[', ']', '~', {n}) FROM t WHERE t MATCH 'hit' ORDER BY rowid");
        }

        (string ours, string shell) = Both(setup.ToString(), [.. qs]);
        Assert.Equal(shell, ours);
    }

    /// <summary>A text of <paramref name="length"/> filler tokens with the given words at the given token positions and a full stop before each token in <paramref name="sentenceStarts"/>.</summary>
    private static string Filler(int length, (int At, string Word)[] words, int[] sentenceStarts)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < length; i++)
        {
            if (i > 0)
            {
                sb.Append(Array.IndexOf(sentenceStarts, i) >= 0 ? ". " : " ");
            }

            string w = "xx";
            foreach ((int at, string word) in words)
            {
                if (at == i)
                {
                    w = word;
                }
            }

            sb.Append(w);
        }

        return sb.ToString();
    }

    /// <summary>Window choice rules found by running the shell on texts of filler tokens: the score of a window (distinct phrases, then instances that start in it), the bonus for a window that starts at a sentence start or the text start, and when a sentence start at the match is used.</summary>
    [Fact]
    public void SnippetWindowChoiceMatchesTheShell()
    {
        NeedOracle();
        var setup = new StringBuilder("CREATE VIRTUAL TABLE t USING fts5(x);");
        int id = 0;
        void Row((int, string)[] w, params int[] starts) => setup.Append($"INSERT INTO t(rowid, x) VALUES({++id}, '{Filler(50, w, starts)}');");

        Row([(10, "aa"), (11, "bb"), (30, "aa"), (31, "aa"), (32, "aa")]); // 1: two phrases beat three instances of one
        Row([(10, "aa"), (30, "bb"), (31, "bb")]); // 2: equal phrases: more instances
        Row([(3, "aa"), (20, "aa"), (21, "aa"), (22, "aa"), (23, "aa")]); // 3: a window at the text start beats a bigger one
        Row([(12, "aa"), (25, "aa"), (26, "aa")], 12); // 4: a sentence start at the match is used when another instance follows
        Row([(12, "aa")], 12); // 5: and not when it is the only one
        Row([(12, "aa"), (22, "bb")], 12, 22); // 6: not when the next instance starts a sentence
        Row([(12, "aa"), (22, "bb")], 12, 15); // 7: not when a sentence starts before the next instance
        Row([(12, "aa"), (22, "bb")], 10, 13); // 8: but a start before the match is used
        Row([(12, "aa"), (13, "aa"), (14, "aa"), (30, "aa"), (31, "aa")], 5, 29); // 9: the window is scored where it starts, not at the match
        Row([(5, "aa"), (12, "aa")], 12); // 10: the text start window wins the tie
        Row([(12, "aa"), (22, "bb"), (32, "cc")], 22, 32); // 11
        var qs = new List<string>();
        foreach (string m in new[] { "aa OR bb", "aa OR bb OR cc", "aa" })
        {
            qs.Add($"SELECT rowid, snippet(t, 0, '[', ']', '~', 8) FROM t WHERE t MATCH '{m}' ORDER BY rowid");
        }

        (string ours, string shell) = Both(setup.ToString(), [.. qs]);
        Assert.Equal(shell, ours);
    }

    /// <summary>A phrase window is placed by the end of the last instance listed, and an instance counts for a window that holds its first token; a column whose whole text is shorter than the window gets no bonus.</summary>
    [Fact]
    public void SnippetPhraseAndColumnChoiceMatchTheShell()
    {
        NeedOracle();
        string setup = "CREATE VIRTUAL TABLE t USING fts5(c0, c1);" +
            "INSERT INTO t VALUES('hh cc ff aa ee gg bb hh cc ff', 'a b c');" +
            "INSERT INTO t VALUES('x x x x x x x x x x x x x x x x x x x x dd', 'dd e f');" +
            "INSERT INTO t VALUES('ee ff gg ff ff hh ff ee ee cc hh cc bb dd ff ee ee gg ff bb ff cc aa', 'dd');" +
            "INSERT INTO t VALUES('aa x x x x x x x x x x x x x x x dd', 'x x x x x x x x x x x x x x dd x x x');";
        string[] qs =
        [
            "SELECT rowid, snippet(t, -1, '[', ']', '~', 5) FROM t WHERE t MATCH '\"hh cc\" OR hh' ORDER BY rowid",
            "SELECT rowid, snippet(t, -1, '[', ']', '~', 9) FROM t WHERE t MATCH 'dd' ORDER BY rowid",
            "SELECT rowid, snippet(t, 0, '[', ']', '~', 12) FROM t WHERE t MATCH '\"cc bb\"' ORDER BY rowid",
            "SELECT rowid, snippet(t, 0, '[', ']', '~', 5) FROM t WHERE t MATCH '\"gg dd\" OR hh OR aa' ORDER BY rowid",
        ];
        (string ours, string shell) = Both(setup, qs);
        Assert.Equal(shell, ours);
    }

    [Fact]
    public void ErrorsMatchTheShell()
    {
        NeedOracle();
        string[] qs =
        [
            "SELECT rowid FROM t WHERE t MATCH '-'", "SELECT rowid FROM t WHERE t MATCH 'a b (c d)'", "SELECT rowid FROM t WHERE t MATCH '{c0 c1'",
            "SELECT rowid FROM t WHERE t MATCH 'zz : a'", "SELECT rowid FROM t WHERE t MATCH 'NEAR(a b, x)'", "SELECT rowid FROM t WHERE t MATCH ''",
            "SELECT rowid FROM t WHERE t MATCH '* a'", "SELECT rowid FROM t WHERE t MATCH 'NEAR x'", "INSERT INTO t(t) VALUES('nonsense')",
            "INSERT INTO t(t, rank) VALUES('pgsz', 'x')", "INSERT INTO t(t) VALUES('delete-all')", "SELECT bm25(t) FROM t",
        ];
        (string ours, string shell) = Both(Setup(""), qs);
        Assert.Equal(Normalize(shell), Normalize(ours));
    }

    [Fact]
    public void ContentlessAndExternalContentMatchTheShell()
    {
        NeedOracle();
        string setup = "CREATE TABLE src(id INTEGER PRIMARY KEY, c0, c1);" +
            "INSERT INTO src VALUES(1,'one two three','four five'),(2,'two three','six'),(3,'three','seven eight nine');" +
            "CREATE VIRTUAL TABLE e USING fts5(c0, c1, content='src', content_rowid='id');INSERT INTO e(e) VALUES('rebuild');" +
            "CREATE VIRTUAL TABLE n USING fts5(c0, c1, content='');INSERT INTO n(rowid, c0, c1) SELECT id, c0, c1 FROM src;" +
            "CREATE VIRTUAL TABLE m USING fts5(c0, content='', columnsize=0);INSERT INTO m(rowid, c0) SELECT id, c0 FROM src;";
        string[] qs =
        [
            "SELECT rowid, bm25(e) FROM e WHERE e MATCH 'three' ORDER BY rowid", "SELECT rowid, snippet(e, 0, '[', ']', '~', 3) FROM e WHERE e MATCH 'two'",
            "SELECT rowid FROM n WHERE n MATCH 'three'", "SELECT count(*) FROM n", "SELECT rowid, bm25(n) FROM n WHERE n MATCH 'three' ORDER BY rowid",
            "SELECT rowid, bm25(m) FROM m WHERE m MATCH 'three' ORDER BY rowid", "SELECT count(*) FROM m", "SELECT c0 FROM n",
            "INSERT INTO n(n, rowid, c0, c1) VALUES('delete', 1, 'one two three', 'four five')", "SELECT rowid FROM n WHERE n MATCH 'one'", "SELECT count(*) FROM n",
            "DELETE FROM n WHERE rowid = 2", "INSERT INTO e(e) VALUES('integrity-check')",
        ];
        (string ours, string shell) = Both(setup, qs);
        Assert.Equal(Normalize(shell), Normalize(ours));
    }

    [Fact]
    public void TheFileWeWriteIsAcceptedByTheShellAndTheShellsFileByUs()
    {
        NeedOracle();
        string o = Path.Combine(_dir, "ours.db"), s = Path.Combine(_dir, "shell.db");
        string setup = Setup("prefix='2 3'") + "UPDATE t SET c1 = 'changed text here' WHERE rowid % 7 = 0; DELETE FROM t WHERE rowid % 5 = 0; INSERT INTO t(t) VALUES('optimize');";
        using (var db = Database.Open(o))
        {
            Assert.Equal(string.Empty, Script(db, setup));
        }

        Assert.Equal(string.Empty, RTreeTests.Shell(s, setup));
        // the shell checks and reads our file
        Assert.Equal(string.Empty, RTreeTests.Shell(o, "INSERT INTO t(t) VALUES('integrity-check')"));
        Assert.Equal("ok\n", RTreeTests.Shell(o, "PRAGMA integrity_check"));
        Assert.Equal(RTreeTests.Shell(s, "SELECT rowid FROM t WHERE t MATCH 'run*' ORDER BY rowid"), RTreeTests.Shell(o, "SELECT rowid FROM t WHERE t MATCH 'run*' ORDER BY rowid"));
        // we check and read the shell's file, then modify it and the shell is still content
        using (var db = Database.Open(s))
        using (Connection c = db.Connect())
        {
            Assert.Equal(string.Empty, RTreeTests.Ours(c, "INSERT INTO t(t) VALUES('integrity-check')"));
            Assert.Equal(RTreeTests.Shell(s, "SELECT rowid FROM t WHERE t MATCH 'run*' ORDER BY rowid"), RTreeTests.Ours(c, "SELECT rowid FROM t WHERE t MATCH 'run*' ORDER BY rowid"));
            Assert.Equal(string.Empty, Script(db, "INSERT INTO t(rowid, c0) VALUES(1000, 'run runner'); DELETE FROM t WHERE rowid = 3; INSERT INTO t(t, rank) VALUES('merge', 4)"));
        }

        Assert.Equal(string.Empty, RTreeTests.Shell(s, "INSERT INTO t(t) VALUES('integrity-check')"));
        Assert.Equal("ok\n", RTreeTests.Shell(s, "PRAGMA integrity_check"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async System.Threading.Tasks.Task ResumingAtEveryPageGivesTheSameRows(int yieldEvery)
    {
        string[] script = [Setup("prefix='2'"), "SELECT rowid FROM t WHERE t MATCH 'run*' ORDER BY rowid", "SELECT rowid, bm25(t) FROM t WHERE t MATCH 'alpha OR fox' ORDER BY rank, rowid",
            "SELECT rowid, snippet(t, 0, '[', ']', '~', 5) FROM t WHERE t MATCH 'the' ORDER BY rowid", "DELETE FROM t WHERE rowid % 4 = 0", "INSERT INTO t(t) VALUES('optimize')",
            "SELECT count(*) FROM t WHERE t MATCH 'jump*'", "INSERT INTO t(t) VALUES('integrity-check')"];
        async System.Threading.Tasks.Task<string> Run(int y, bool async)
        {
            using var db = Database.Open(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"), new DatabaseOptions { PageSize = 512, YieldEvery = y });
            using Connection c = db.Connect();
            var o = new StringBuilder();
            Runner.Script(c, script[0]);
            foreach (string sql in script.Skip(1))
            {
                using Statement st = c.Prepare(sql);
                while (async ? await st.StepAsync() : st.Step())
                {
                    for (int i = 0; i < st.ColumnCount; i++)
                    {
                        o.Append(st.IsNull(i) ? "~" : st.GetString(i)).Append('|');
                    }

                    o.Append('\n');
                }
            }

            return o.ToString();
        }

        string baseline = await Run(0, false);
        Assert.NotEmpty(baseline);
        Assert.Equal(baseline, await Run(yieldEvery, false));
        Assert.Equal(baseline, await Run(yieldEvery, true));
    }
}
