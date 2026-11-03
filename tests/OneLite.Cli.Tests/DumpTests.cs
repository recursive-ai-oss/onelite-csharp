using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Oracle;

namespace RecursiveAI.OneLite.Cli.Tests;

public class DumpTests
{
    private const string Rich = """
        create table p(id integer primary key, name text unique not null collate nocase, v real default 1.5 check(v>0));
        create table c(id int, pid references p(id) on delete cascade, s text generated always as (id||'x') virtual, primary key(id,pid)) without rowid;
        create table [br ack](a);
        create table `bt`(a);
        create table 'sq'(a);
        create table "dq"("x y" int, z);
        create table st(a int, b text) strict;
        create index ie on p(lower(name)) where id>5;
        create unique index iu on st(a,b desc);
        create view vw(x) as select id from p;
        create trigger tg before update of name on p when new.name<>old.name begin insert into st values(1,'t'); end;
        insert into p(name,v) values('a',1),('B',2.5),('c',null);
        insert into c(id,pid) values(1,1),(2,1);
        insert into st values(1,'x'),(2,'y');
        insert into "dq" values(1,'it''s');
        create table nokey(a,b);
        insert into nokey values(1,2),(3,4);
        delete from nokey where a=1;
        create table counter(id integer primary key autoincrement, v);
        insert into counter(v) values('a'),('b'),(char(9)||char(10)||'\x');
        analyze;
        """;

    /// <summary>
    /// The shell's dump creates <c>sqlite_sequence</c> with <c>PRAGMA writable_schema=ON</c>, which the engine does not support; the table exists by itself
    /// once an AUTOINCREMENT table has been created, so those three lines are dropped before loading with onelite.
    /// </summary>
    private static string WithoutSchemaWrites(string dump)
        => string.Join('\n', dump.Split('\n').Where(l => l is not ("PRAGMA writable_schema=ON;" or "PRAGMA writable_schema=OFF;" or "CREATE TABLE IF NOT EXISTS sqlite_sequence(name,seq);")));

    /// <summary>
    /// White space between the end of a CREATE INDEX statement and its semicolon is part of the SQL text the reference library stores; the engine drops it
    /// (a known difference), so dumps of a database loaded by onelite are compared with that white space removed.
    /// </summary>
    private static string Normalized(string dump) => System.Text.RegularExpressions.Regex.Replace(dump, @"\s+;\n", ";\n");

    private static IEnumerable<(string Name, string Script)> Databases()
    {
        yield return ("rich", Rich);
        yield return ("plain", Rich.Replace("autoincrement", string.Empty, StringComparison.Ordinal));
        for (int seed = 21; seed <= 24; seed++)
        {
            yield return ("gen" + seed, GeneratedData.Script(seed));
        }
    }

    [Fact]
    public void DumpIsTheShellsDump()
    {
        Assert.SkipUnless(Harness.OracleAvailable, "the pinned sqlite3 is not installed");
        foreach ((string name, string script) in Databases())
        {
            string db = Harness.MakeDb(name + ".db", script);
            foreach (string cmd in new[] { ".dump", ".dump --nosys", ".dump --data-only", ".dump --preserve-rowids", ".dump p c", ".dump nokey", ".schema", ".tables", ".indexes", ".schema p" })
            {
                Harness.Compare(name + " " + cmd, [db, cmd]);
            }
        }
    }

    [Fact]
    public void DumpLoadsIntoTheShellAndOursLoadsTheShellsDump()
    {
        Assert.SkipUnless(Harness.OracleAvailable, "the pinned sqlite3 is not installed");
        int files = 0;
        foreach ((string name, string script) in Databases())
        {
            string src = Harness.MakeDb(name + ".db", script);
            string expected = Harness.Oracle([src, ".dump"]).Stdout;

            // our dump, loaded by sqlite3
            Harness.Outcome ours = Harness.Ours([src, ".dump"]);
            string y1 = Harness.PathIn(name + ".y1.db");
            File.Delete(y1);
            Harness.Outcome load = Harness.Oracle([y1], ours.Stdout);
            Assert.True(load.Stderr.Length == 0, name + ": sqlite3 rejected our dump: " + load.Stderr);
            Assert.Equal(expected, Harness.Oracle([y1, ".dump"]).Stdout);
            Assert.True(Harness.IntegrityOk(y1, out string detail), detail);

            // the shell's dump, loaded by us
            string y2 = Harness.PathIn(name + ".y2.db");
            File.Delete(y2);
            Harness.Outcome back = Harness.Ours([y2], WithoutSchemaWrites(expected));
            Assert.True(back.Stderr.Length == 0, name + ": we rejected the shell's dump: " + back.Stderr);
            Assert.Equal(expected, Harness.Oracle([y2, ".dump"]).Stdout);
            Assert.True(Harness.IntegrityOk(y2, out detail), detail);
            Assert.Equal(expected, Harness.Ours([y2, ".dump"]).Stdout);
            files++;
        }

        Assert.Equal(6, files);
    }

    /// <summary>The real-world files (Chinook, Northwind, Sakila) when the corpus has been fetched.</summary>
    [Fact]
    public void RealWorldFilesRoundTrip()
    {
        Assert.SkipUnless(Harness.OracleAvailable, "the pinned sqlite3 is not installed");
        string dir = Path.Combine(OraclePaths.CorpusRoot, "realworld");
        Assert.SkipUnless(Directory.Exists(dir), "the real-world corpus is not fetched");
        foreach (string file in Directory.GetFiles(dir).Where(f => !f.EndsWith("-wal", StringComparison.Ordinal) && !f.EndsWith("-shm", StringComparison.Ordinal)))
        {
            // work on a copy so the corpus is never touched
            string src = Harness.PathIn("rw-" + Path.GetFileName(file));
            File.Copy(file, src, true);
            string expected = Harness.Oracle([src, ".dump"]).Stdout;
            Assert.Equal(expected, Harness.Ours([src, ".dump"]).Stdout);

            string y1 = Harness.PathIn("rw-y1.db");
            File.Delete(y1);
            Assert.Equal(string.Empty, Harness.Oracle([y1], Harness.Ours([src, ".dump"]).Stdout).Stderr);
            Assert.Equal(expected, Harness.Oracle([y1, ".dump"]).Stdout);
            Assert.True(Harness.IntegrityOk(y1, out string d1), d1);

            string y2 = Harness.PathIn("rw-y2.db");
            File.Delete(y2);
            Assert.Equal(string.Empty, Harness.Ours([y2], WithoutSchemaWrites(expected)).Stderr);
            Assert.Equal(Normalized(expected), Normalized(Harness.Oracle([y2, ".dump"]).Stdout));
            Assert.True(Harness.IntegrityOk(y2, out string d2), d2);
        }
    }
}
