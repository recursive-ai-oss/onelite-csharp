using System.Data;
using System.Linq;
using Dapper;
using Xunit;

namespace RecursiveAI.OneLite.Data.Tests;

/// <summary>Dapper (a test-only dependency) works over the provider: queries, multi-mapping and parameterized execute.</summary>
public sealed class DapperTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Person
    {
        public long Id { get; set; }

        public string Name { get; set; } = "";

        public int Age { get; set; }

        public Pet? Pet { get; set; }
    }

    private sealed class Pet
    {
        public long PetId { get; set; }

        public string PetName { get; set; } = "";
    }

    private static OneLiteConnection Open()
    {
        var c = new OneLiteConnection("Data Source=:memory:");
        c.Open();
        return c;
    }

    [Fact]
    public async Task QueryExecuteAndParameters()
    {
        using OneLiteConnection c = Open();
        Assert.Equal(0, c.Execute("create table people(id integer primary key, name text, age int)"));
        Assert.Equal(3, c.Execute("insert into people(name, age) values (@name, @age)", new[] { new { name = "ann", age = 30 }, new { name = "bob", age = 41 }, new { name = "cy", age = 25 } }));
        List<Person> over = c.Query<Person>("select id, name, age from people where age > @min order by id", new { min = 26 }).AsList();
        Assert.Equal(["ann", "bob"], over.ConvertAll(p => p.Name));
        Assert.Equal("bob", c.QuerySingle<string>("select name from people where id = :id", new { id = 2 }));
        Assert.Equal(3, c.ExecuteScalar<int>("select count(*) from people"));
        Assert.Equal(1, c.QueryFirstOrDefault<long>("select min(id) from people"));
        Assert.Null(c.QueryFirstOrDefault<Person>("select id, name, age from people where id = @id", new { id = 99 }));

        // The async twins really are async over this provider.
        IEnumerable<Person> async = await c.QueryAsync<Person>(new CommandDefinition("select id, name, age from people order by id desc", cancellationToken: Ct));
        Assert.Equal("cy", async.First().Name);
        Assert.Equal(1, await c.ExecuteAsync(new CommandDefinition("update people set age = age + 1 where name = @n", new { n = "ann" }, cancellationToken: Ct)));

        // Transactions pass through the command's Transaction property.
        using (var tx = c.BeginTransaction())
        {
            c.Execute("delete from people", transaction: tx);
            tx.Rollback();
        }

        Assert.Equal(3, c.ExecuteScalar<int>("select count(*) from people"));
    }

    [Fact]
    public void MultiMapping()
    {
        using OneLiteConnection c = Open();
        c.Execute("create table people(id integer primary key, name text, age int); create table pets(petid integer primary key, owner int, petname text)");
        c.Execute("insert into people values (1, 'ann', 30), (2, 'bob', 41); insert into pets values (10, 1, 'rex'), (11, 1, 'tom')");
        List<Person> people = c.Query<Person, Pet, Person>(
            "select p.id, p.name, p.age, t.petid, t.petname from people p join pets t on t.owner = p.id order by t.petid",
            (person, pet) =>
            {
                person.Pet = pet;
                return person;
            },
            splitOn: "petid").AsList();
        Assert.Equal(2, people.Count);
        Assert.Equal("ann", people[0].Name);
        Assert.Equal("tom", people[1].Pet!.PetName);
    }

    [Fact]
    public void QueryMultiple()
    {
        using OneLiteConnection c = Open();
        c.Execute("create table t(a)");
        c.Execute("insert into t values (1), (2), (3)");
        using SqlMapper.GridReader grid = c.QueryMultiple("select a from t order by a; select count(*) from t");
        Assert.Equal([1, 2, 3], grid.Read<int>().AsList());
        Assert.Equal(3, grid.ReadSingle<int>());
    }
}
