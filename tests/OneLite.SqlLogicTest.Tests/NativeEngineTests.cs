using System.IO;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.SqlLogicTest.Tests;

/// <summary>The runner against the pinned native library, on a small script and on mutated copies of it.</summary>
public class NativeEngineTests
{
    // A script whose expectations are right for SQLite.
    private const string Good = """
        statement ok
        CREATE TABLE t1(a INTEGER, b TEXT, c REAL)

        statement ok
        INSERT INTO t1 VALUES(1,'one',1.5),(2,'two',NULL),(3,NULL,0.0625)

        query ITR rowsort
        SELECT a, b, c FROM t1
        ----
        1
        one
        1.500
        2
        two
        NULL
        3
        NULL
        0.063

        query I nosort
        SELECT count(*) FROM t1
        ----
        3

        statement error
        INSERT INTO nope VALUES(1)

        query I nosort
        SELECT a FROM t1 ORDER BY a
        ----
        3 values hashing to c0710d6b4f15dfa88f600b0e6b624077
        """;

    private static FileResult RunScript(string script)
    {
        string? lib = OraclePaths.NativeLibrary;
        if (!File.Exists(lib))
        {
            Assert.Skip("the pinned native library is not installed");
        }
        string dir = Path.Combine(Path.GetTempPath(), "slt-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "t.test");
            File.WriteAllText(path, script.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
            return Runner.RunFile(new SqliteNativeEngine(), path, "t.test", dir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void GoodScriptPasses()
    {
        FileResult r = RunScript(Good);
        Assert.Null(r.Fatal);
        Assert.Equal((6, 6, 0), (r.Records, r.Passed, r.Failed));
    }

    [Fact]
    public void OneWrongExpectedValueFailsWithADiff()
    {
        FileResult r = RunScript(Good.Replace("two\nNULL", "two\n7.000", StringComparison.Ordinal));
        Failure f = Assert.Single(r.Failures);
        Assert.Equal("values", f.Kind);
        Assert.Contains("SELECT a, b, c FROM t1", f.Sql, StringComparison.Ordinal);
        Assert.Contains("expected '7.000', actual 'NULL'", f.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public void OneWrongHashFails()
    {
        FileResult r = RunScript(Good.Replace("b624077", "b624070", StringComparison.Ordinal));
        Failure f = Assert.Single(r.Failures);
        Assert.Equal("hash", f.Kind);
        Assert.Contains("c0710d6b4f15dfa88f600b0e6b624077", f.Actual, StringComparison.Ordinal);
    }

    [Fact]
    public void OneWrongErrorExpectationFails()
    {
        FileResult r = RunScript(Good.Replace("statement error", "statement ok", StringComparison.Ordinal));
        Failure f = Assert.Single(r.Failures);
        Assert.Equal("statement-failed", f.Kind);
        Assert.Contains("no such table: nope", f.Actual, StringComparison.Ordinal);
    }

    [Fact]
    public void ForeignKeysAreOffLikeTheShell()
    {
        // The native library defaults foreign keys on; the shell leaves them off, so the engine turns them off.
        FileResult r = RunScript("""
            statement ok
            CREATE TABLE p(id INTEGER PRIMARY KEY)

            statement ok
            CREATE TABLE c(pid INTEGER REFERENCES p(id))

            statement ok
            INSERT INTO c VALUES(42)
            """);
        Assert.Equal(0, r.Failed);
    }

    [Fact]
    public void OneLiteEngineRunsQueriesOnItselfAndSetupOnTheNativeLibrary()
    {
        Assert.SkipUnless(File.Exists(OraclePaths.NativeLibrary), "the pinned native library is not installed");
        string dir = Path.Combine(Path.GetTempPath(), "slt-ol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "t.test");
            File.WriteAllText(
                path,
                "statement ok\nCREATE TABLE t(a INTEGER, b TEXT)\n\nstatement ok\nINSERT INTO t VALUES(1,'x'),(2,'y')\n\n"
                + "query IT nosort\nSELECT a, upper(b) FROM t ORDER BY a\n----\n1\nX\n2\nY\n\n"
                + "query I nosort\nSELECT 7 * 6\n----\n42\n");
            FileResult r = Runner.RunFile(new OneLiteEngine(), path, "t.test", dir);
            Assert.Null(r.Fatal);
            Assert.Equal(0, r.Failed);
            Assert.Equal(4, r.Passed);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
