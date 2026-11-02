using System;
using System.IO;
using Xunit;

namespace RecursiveAI.OneLite.Tests.Vec;

public sealed class VecSmokeTests : IDisposable
{
    private readonly string _dir = VecHelper.TempDir("vec-smoke");

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void ChunkRowsThatOverflowSurviveRepeatedInserts()
    {
        using var db = Database.Open(Path.Combine(_dir, "b.db"));
        using Connection c = db.Connect();
        Assert.Equal(string.Empty, VecHelper.Run(c, "create virtual table v using vec0(id integer primary key, e float[17])"));
        string vec = "[" + string.Join(",", System.Linq.Enumerable.Range(0, 17)) + "]";
        for (int i = 1; i <= 4; i++)
        {
            Assert.Equal(string.Empty, VecHelper.Run(c, "insert into v(id,e) values (" + i + ", '" + vec + "')"));
        }

        Assert.Equal("4\n", VecHelper.Run(c, "select count(*) from v").Replace("\r", ""));
    }

    [Fact]
    public void KnnBasics()
    {
        using var db = Database.Open(Path.Combine(_dir, "a.db"));
        using Connection c = db.Connect();
        Assert.Equal(string.Empty, VecHelper.Run(c, "create virtual table v using vec0(id integer primary key, e float[3], g text, n int)"));
        Assert.Equal(string.Empty, VecHelper.Run(c, "insert into v(id,e,g,n) values (1,'[1,0,0]','a',1),(2,'[0,1,0]','b',2),(3,'[0,0,1]','a',3),(4,'[1,1,0]','c',4),(5,'[1,0,0]','c',5)"));
        Assert.Equal("1|a|1\n2|b|2\n3|a|3\n4|c|4\n5|c|5\n", VecHelper.Run(c, "select id,g,n from v"));
Assert.Equal("1|0.0\n5|0.0\n4|1.0\n", VecHelper.Run(c, "select id,distance from v where e match '[1,0,0]' and k=3"));
        Assert.Equal("5|0.0\n4|1.0\n", VecHelper.Run(c, "select id,distance from v where e match '[1,0,0]' and k=3 and g='c'"));
        Assert.Equal("2\n", VecHelper.Run(c, "select id from v where id=2"));
        Assert.Equal("1|0.0\n5|0.0\n", VecHelper.Run(c, "select id,distance from v where e match '[1,0,0]' limit 2"));
        Assert.Equal(string.Empty, VecHelper.Run(c, "delete from v where id=1"));
        Assert.Equal("5|0.0\n4|1.0\n", VecHelper.Run(c, "select id,distance from v where e match '[1,0,0]' and k=2"));
        Assert.Equal(string.Empty, VecHelper.Run(c, "update v set e='[0,1,0]' where id=5"));
        Assert.Equal("2|0.0\n5|0.0\n", VecHelper.Run(c, "select id,distance from v where e match '[0,1,0]' and k=2"));
    }

    [Fact]
    public void UpdatingAnotherColumnKeepsInt8AndBitVectors()
    {
        using var db = Database.Open(Path.Combine(_dir, "u.db"));
        using Connection c = db.Connect();
        Assert.Equal(string.Empty, VecHelper.Run(c, "create virtual table v using vec0(id integer primary key, b bit[16], e int8[2], tag text)"));
        Assert.Equal(string.Empty, VecHelper.Run(c, "insert into v values (1, vec_bit(x'ff00'), vec_int8('[1,2]'), 'a'), (2, vec_bit(x'00ff'), vec_int8('[3,4]'), 'b')"));
        Assert.Equal(string.Empty, VecHelper.Run(c, "update v set tag = 'z' where id = 1"));
        Assert.Equal("224|225|z\n", VecHelper.Run(c, "select subtype(b), subtype(e), tag from v where id = 1"));
    }

    [Fact]
    public void MatchOperandMayBeASubqueryOrAColumnOfAnEarlierTable()
    {
        using var db = Database.Open(Path.Combine(_dir, "m.db"));
        using Connection c = db.Connect();
        Assert.Equal(string.Empty, VecHelper.Run(c, "create virtual table v using vec0(id integer primary key, e float[3])"));
        Assert.Equal(string.Empty, VecHelper.Run(c, "insert into v(id,e) values (1,'[1,0,0]'),(2,'[0,1,0]'),(3,'[0,0,1]'),(4,'[1,1,0]')"));
        Assert.Equal(string.Empty, VecHelper.Run(c, "create table q(name text, probe text)"));
        Assert.Equal(string.Empty, VecHelper.Run(c, "insert into q values ('x','[1,0,0]'),('y','[0,0,1]')"));
        Assert.Equal("1|0.0\n4|1.0\n", VecHelper.Run(c, "select id,distance from v where e match (select probe from q where name='x') and k=2"));
        Assert.Equal("x|1|0.0\ny|3|0.0\n", VecHelper.Run(c, "select q.name, v.id, v.distance from q join v on v.e match q.probe and v.k=1 order by q.name"));
    }
}
