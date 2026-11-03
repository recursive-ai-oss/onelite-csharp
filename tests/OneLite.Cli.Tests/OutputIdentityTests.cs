using System.Collections.Generic;
using System.IO;
using System.Linq;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.Cli.Tests;

public class OutputIdentityTests
{
    private static readonly string[] Modes = ["list", "csv", "line", "json", "column", "box", "table", "markdown", "quote", "tabs", "ascii", "html"];

    private static readonly string[] Queries =
    [
        "select * from t", "select * from w", "select a,e from t where rowid<5", "select count(*), sum(c), min(b), max(d) from t",
        "select typeof(a),length(b),hex(d) from t", "select * from t where 0",
    ];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EveryModeMatchesTheShellByteForByte(int seed)
    {
        Assert.SkipUnless(Harness.OracleAvailable, "the pinned sqlite3 is not installed");
        string db = Harness.MakeDb($"modes{seed}.db", GeneratedData.Script(seed));
        int cases = 0;
        foreach (string mode in Modes)
        {
            foreach (string header in new[] { "-header", "-noheader" })
            {
                foreach (string[] nv in new string[][] { [], ["-nullvalue", "NIL"] })
                {
                    foreach (string q in Queries)
                    {
                        Harness.Compare($"{mode} {header} {string.Join(' ', nv)} [{q}] seed {seed}", [db, "-" + mode, header, .. nv, q]);
                        cases++;
                    }
                }
            }
        }

        Assert.True(cases > 250);
    }

    [Fact]
    public void ErrorsAndScriptsMatchTheShell()
    {
        Assert.SkipUnless(Harness.OracleAvailable, "the pinned sqlite3 is not installed");
        string[] scripts = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "cli", "scripts.txt"));
        foreach (string s in scripts.Where(l => l.Length > 0))
        {
            Harness.Compare("script [" + s + "]", [":memory:"], s.Replace("\\n", "\n", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ExitCodeAndFirstErrorStopCommandLineArguments()
    {
        Assert.SkipUnless(Harness.OracleAvailable, "the pinned sqlite3 is not installed");
        Harness.Compare("args", [":memory:", "select 1", "select nope", "select 3"]);
        Harness.Compare("bail", ["-bail", ":memory:", "select nope; select 3"]);
        Harness.Compare("cmd", [":memory:", "-cmd", ".mode csv", "select 1,'a b'"]);
        Harness.Compare("readonly missing", ["-readonly", Path.Combine(Harness.Scratch, "nonexistent.db"), "select 1"]);
    }

    [Theory]
    [InlineData("select 1;", true)]
    [InlineData("select 1", false)]
    [InlineData("select 1; select 2", false)]
    [InlineData("select 1; -- c", true)]
    [InlineData("select 'a;", false)]
    [InlineData("select 'a;';", true)]
    [InlineData("create trigger t after insert on x begin select 1;", false)]
    [InlineData("create trigger t after insert on x begin select 1; end;", true)]
    [InlineData("create temp trigger t after insert on x begin select 1; end;", true)]
    [InlineData("create table t(a); ", true)]
    [InlineData("/* x */", false)]
    [InlineData("select 1 /* ; */", false)]
    [InlineData("select [a;b];", true)]
    public void StatementCompletenessFollowsTheDocumentedRule(string sql, bool complete)
        => Assert.Equal(complete, SqlText.IsComplete(sql));
}
