using System.Collections.Generic;
using System.IO;
using System.Linq;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.Differential.Tests;

/// <summary>
/// Replays every committed repro and compares the result with the status recorded in the file: a fixed defect shows up as
/// "now passes", a regression in a passing file as a failure. Needs the pinned sqlite3 and its library.
/// </summary>
public class CorpusTests
{
    private static string CorpusDir => Path.Combine(AppContext.BaseDirectory, "differential");

    [Fact]
    public void EveryReproIsAsRecorded()
    {
        Assert.SkipUnless(Corpus.OraclesAvailable, "the pinned sqlite3 is not installed");
        string tmp = Path.Combine(Path.GetTempPath(), "onelite-differential-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            List<Corpus.Result> results = Corpus.Replay(CorpusDir, tmp);
            Assert.NotEmpty(results);
            string[] bad = [.. results.Where(r => !r.Ok).Select(r => r.File + ": " + r.Message)];
            Assert.True(bad.Length == 0, string.Join("\n", bad));
        }
        finally
        {
            if (Directory.Exists(tmp))
            {
                Directory.Delete(tmp, true);
            }
        }
    }

    [Fact]
    public void EveryReproParsesAndRendersBack()
    {
        foreach (string f in Directory.EnumerateFiles(CorpusDir, "*.sql"))
        {
            Repro r = Repro.Parse(File.ReadAllText(f));
            Repro again = Repro.Parse(r.Render());
            Assert.Equal(r.Setup, again.Setup);
            Assert.Equal(r.Check.Fields().ToList(), again.Check.Fields().ToList());
            Assert.Equal(r.Class, again.Class);
            Assert.Equal(r.Status, again.Status);
        }
    }
}

/// <summary>The generator and the minimizer's building blocks, which need no oracle.</summary>
public class GeneratorTests
{
    [Fact]
    public void SameSeedGivesSameStream()
    {
        var a = Rng.ForCase(7, 3);
        var b = Rng.ForCase(7, 3);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(a.NextU64(), b.NextU64());
        }

        // Fixed values: the streams are part of the campaign fingerprints and must not drift between runtimes.
        Assert.Equal(0xE220A8397B1DCDAFUL, new Rng(0).NextU64());
    }

    [Fact]
    public void ExpressionsRenderAndShrink()
    {
        var e = Expr.Node("({0} + {1})", Expr.Raw("a"), Expr.Node("abs({0})", Expr.Raw("b")));
        Assert.Equal("(a + abs(b))", e.ToString());
        string[] shrunk = [.. e.Simplify().Select(x => x.ToString())];
        Assert.Contains("a", shrunk);
        Assert.Contains("abs(b)", shrunk);
        Assert.Contains("(a + b)", shrunk);
        Assert.Contains("NULL", shrunk);
    }

    [Fact]
    public void SelectRendersInClauseOrderAndShrinksByClause()
    {
        var q = new Select { From = "t0", Where = Expr.Raw("a = 1"), Cols = [Expr.Raw("a"), Expr.Raw("b")], Order = [Expr.Raw("id")], Limit = Expr.Raw("3") };
        Assert.Equal("SELECT a, b FROM t0 WHERE a = 1 ORDER BY id LIMIT 3", q.ToString());
        string[] shrunk = [.. q.Shrinks().Select(x => x.ToString())];
        Assert.Contains("SELECT a, b FROM t0 WHERE a = 1 ORDER BY id", shrunk);
        Assert.Contains("SELECT a FROM t0 WHERE a = 1 ORDER BY id LIMIT 3", shrunk);
    }

    [Fact]
    public void ReproFileRoundTrips()
    {
        var r = new Repro
        {
            Setup = ["CREATE TABLE t0(id INTEGER PRIMARY KEY, c0)", "INSERT INTO t0(id, c0) VALUES(1, 'a\nb')"],
            Check = new TlpCheck("t0", Expr.Raw("(c0 > 1)")) { Families = [Fam.Expr, Fam.Affinity] },
            Status = "expected-fail",
            Class = "bug:example",
            Detail = "partitions differ",
        };
        string text = r.Render();
        Repro back = Repro.Parse(text);
        Assert.Equal(r.Setup, back.Setup);
        Assert.Equal(text, back.Render());
    }

    [Fact]
    public void FamilyFilterRejectsUnknownNames()
    {
        Assert.Throws<ArgumentException>(() => new Fam.Filter(["nonsense"]));
        var f = new Fam.Filter([Fam.Window, Fam.Cte]);
        Assert.True(f.Allows(Fam.Window));
        Assert.False(f.Allows(Fam.Join));
        Assert.True(f.AllowsExprGroup(Fam.Str));
    }

    [Fact]
    public void GenerationIsReproducible()
    {
        Assert.SkipUnless(Corpus.OraclesAvailable, "the pinned sqlite3 is not installed");
        string dir = Path.Combine(Path.GetTempPath(), "onelite-differential-gen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string Make(string name)
            {
                CaseGen.Plan p = CaseGen.Generate(11, 4, new Fam.Filter([]), new HashSet<string> { "diff", "pqs", "norec", "tlp" }, Path.Combine(dir, name));
                return string.Join("\n", p.Setup.Statements) + "\n--\n" + string.Join("\n", p.Checks.SelectMany(c => c.Fields().Select(f => f.Key + "=" + f.Value)));
            }

            Assert.Equal(Make("a.db"), Make("b.db"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
