using System;
using System.IO;
using System.Linq;
using System.Text;
using RecursiveAI.OneLite.Oracle;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Vec;

/// <summary>
/// A database with vec0 tables is a standard SQLite file: the pinned shell passes integrity_check, uses every other table, and reports a missing
/// module (and nothing else) for the vec0 table. Edits made by the shell leave the vec0 tables intact for the next open.
/// </summary>
public sealed class VecInteropTests : IDisposable
{
    private readonly string _dir = VecHelper.TempDir("vec-interop");

    public void Dispose() => Directory.Delete(_dir, true);

    private static string Shell(string db, string sql)
    {
        Proc.Result r = Proc.Run(OraclePaths.Cli, [db, sql]);
        return r.Stdout + r.Stderr;
    }

    [Theory]
    [InlineData(512, "delete")]
    [InlineData(4096, "delete")]
    [InlineData(4096, "wal")]
    [InlineData(65536, "wal")]
    public void ShellOpensTheFileAndOnlyTheVecTableIsMissingItsModule(int pageSize, string journal)
    {
        Assert.SkipUnless(File.Exists(OraclePaths.Cli), "the pinned sqlite3 shell is not installed");
        string db = Path.Combine(_dir, "i.db");
        using (var d = Database.Open(db))
        using (Connection c = d.Connect())
        {
            var sb = new StringBuilder();
            sb.Append("pragma page_size=").Append(pageSize).Append(';').Append("pragma journal_mode=").Append(journal).Append(';');
            VecHelper.Run(c, sb.ToString());
            Assert.Equal(string.Empty, VecHelper.Run(c, "create table plain(a integer primary key, b text)"));
            Assert.Equal(string.Empty, VecHelper.Run(c, "create virtual table v using vec0(id integer primary key, e float[300], tag text partition key, chunk_size=8)"));
            Assert.Equal(string.Empty, VecHelper.Run(c, "create virtual table w using vec0(e int8[4], b bit[16])"));
            Assert.Equal(string.Empty, VecHelper.Run(c, "begin"));
            for (int i = 1; i <= 40; i++)
            {
                string vec = "[" + string.Join(",", Enumerable.Range(0, 300).Select(k => (i * 7 + k) % 13)) + "]";
                Assert.Equal(string.Empty, VecHelper.Run(c, $"insert into v(id,e,tag) values ({i}, '{vec}', 't{i % 3}')"));
                Assert.Equal(string.Empty, VecHelper.Run(c, $"insert into plain values ({i}, 'row{i}')"));
            }

            Assert.Equal(string.Empty, VecHelper.Run(c, "insert into w(e,b) values (vec_int8('[1,2,3,4]'), vec_bit(x'ffff'))"));
            Assert.Equal(string.Empty, VecHelper.Run(c, "delete from v where id % 5 = 0"));
            Assert.Equal(string.Empty, VecHelper.Run(c, "commit"));
        }

        Assert.Equal("ok\n", Shell(db, "pragma integrity_check"));
        Assert.Equal("40\n", Shell(db, "select count(*) from plain"));
        Assert.Contains("no such module: vec0", Shell(db, "select count(*) from v"));
        Assert.Contains("no such module: vec0", Shell(db, "select count(*) from w"));
        Assert.Equal(string.Empty, Shell(db, "insert into plain values (100,'shell'); delete from plain where a < 10; create table made(x); insert into made values (1)"));
        Assert.Equal("ok\n", Shell(db, "pragma integrity_check"));

        using var d2 = Database.Open(db);
        using Connection c2 = d2.Connect();
        Assert.Equal("32\n", VecHelper.Run(c2, "select count(*) from v"));
        Assert.Equal("1\n", VecHelper.Run(c2, "select count(*) from w"));
        Assert.Equal("32\n", VecHelper.Run(c2, "select count(*) from plain"));
        string probe = "[" + string.Join(",", Enumerable.Range(0, 300).Select(k => (7 + k) % 13)) + "]";
        Assert.Equal("1|0.0\n", VecHelper.Run(c2, $"select id, distance from v where e match '{probe}' and k=1 and tag='t1'"));
        Assert.Equal(string.Empty, VecHelper.Run(c2, "drop table v"));
        Assert.Equal("ok\n", Shell(db, "pragma integrity_check"));
    }
}
