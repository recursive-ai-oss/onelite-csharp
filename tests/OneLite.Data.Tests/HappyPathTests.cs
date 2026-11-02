using RecursiveAI.OneLite.Oracle;
using RecursiveAI.OneLite.Storage.Pager;
using Xunit;

namespace RecursiveAI.OneLite.Data.Tests;

[OneLiteRow]
public sealed record User(long Id, string Name, int Age, string? Email = null);

public enum Kind
{
    Plain = 1,
    Fancy = 2,
}

[OneLiteRow(TableName = "things")]
public sealed class Thing
{
    public long Id { get; set; }

    public string Name { get; set; } = "";

    public double? Score { get; set; }

    public bool Active { get; set; }

    public Kind Kind { get; set; }

    public Kind? Maybe { get; set; }

    public Guid Ref { get; set; }

    public DateTime At { get; set; }

    public byte[]? Blob { get; set; }

    [OneLiteColumn("note_text")]
    public string? Note { get; set; }

    [OneLiteIgnore]
    public string Skipped { get; set; } = "kept";
}

[OneLiteRow]
public sealed record Dog([property: OneLiteKey] string Name, int Legs);

[OneLiteRow]
public sealed class Setting
{
    [OneLiteKey]
    public required string Key { get; init; }

    public int Value { get; init; }
}

[OneLiteRow]
public readonly record struct Point(long Id, double X, double Y);

public sealed class HappyPathTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onelite-hp-" + Guid.NewGuid().ToString("N"));

    public HappyPathTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Path1(string n) => Path.Combine(_dir, n);

    private static async Task<Database> Mem() => await Database.OpenAsync(":memory:", null, Ct);

    [Fact]
    public async Task InterpolatedHolesAreParametersNotText()
    {
        await using Database db = await Mem();
        await db.ExecuteScriptAsync("create table users(id integer primary key, name text, age int, email text)", Ct);
        string evil = "x'); drop table users; --";
        await db.ExecuteAsync($"insert into users(name, age) values ({evil}, {41})", Ct);
        Assert.Equal(1L, await db.ScalarAsync<long>($"select count(*) from users", Ct));
        Assert.Equal(evil, await db.ScalarAsync<string>($"select name from users where age = {41}", Ct));
        // Same value twice is two parameters; a null hole binds NULL.
        string? none = null;
        Assert.Equal(0L, await db.ScalarAsync<long>($"select count(*) from users where name = {none}", Ct));
    }

    [Fact]
    public async Task QueryMapsRecordsClassesStructsAndScalars()
    {
        await using Database db = await Mem();
        await db.Table<User>().EnsureCreatedAsync(Ct);
        await db.Table<User>().InsertAsync(new User(0, "ann", 30), Ct);
        await db.Table<User>().InsertAsync(new User(0, "bob", 41, "b@x"), Ct);
        List<User> all = await db.QueryAsync<User>($"select * from users order by id", Ct);
        Assert.Equal([new User(1, "ann", 30), new User(2, "bob", 41, "b@x")], all);

        // Extra columns are ignored; the order of columns does not matter; names match ignoring case.
        List<User> some = await db.QueryAsync<User>($"select age as AGE, name, id, 1 as extra from users where id = {2}", Ct);
        Assert.Equal(new User(2, "bob", 41), some[0] with { Email = null });

        List<long> ids = await db.QueryAsync<long>($"select id from users order by id desc", Ct);
        Assert.Equal([2L, 1L], ids);
        Assert.Equal(["ann", "bob"], await db.QueryAsync<string>($"select name from users order by id", Ct));

        await db.Table<Point>().EnsureCreatedAsync(Ct);
        await db.Table<Point>().InsertAsync(new Point(7, 1.5, -2), Ct);
        Assert.Equal(new Point(7, 1.5, -2), (await db.Table<Point>().GetAsync(7L, Ct)));
    }

    [Fact]
    public async Task RichTypesRoundTrip()
    {
        await using Database db = await Mem();
        Table<Thing> t = db.Table<Thing>();
        await t.EnsureCreatedAsync(Ct);
        var g = Guid.NewGuid();
        var at = new DateTime(2024, 2, 29, 13, 14, 15, 678, DateTimeKind.Utc);
        long id = await t.InsertAsync(new Thing { Name = "n", Score = 2.5, Active = true, Kind = Kind.Fancy, Maybe = Kind.Plain, Ref = g, At = at, Blob = [1, 2, 3], Note = "hi", Skipped = "dropped" }, Ct);
        Thing back = (await t.GetAsync(id, Ct))!;
        Assert.Equal("n", back.Name);
        Assert.Equal(2.5, back.Score);
        Assert.True(back.Active);
        Assert.Equal(Kind.Fancy, back.Kind);
        Assert.Equal(Kind.Plain, back.Maybe);
        Assert.Equal(g, back.Ref);
        Assert.Equal(at, back.At);
        Assert.Equal(new byte[] { 1, 2, 3 }, back.Blob);
        Assert.Equal("hi", back.Note);
        Assert.Equal("kept", back.Skipped);

        long id2 = await t.InsertAsync(new Thing { Name = "m", Ref = g, At = at }, Ct);
        Thing nulls = (await t.GetAsync(id2, Ct))!;
        Assert.Null(nulls.Score);
        Assert.Null(nulls.Maybe);
        Assert.Null(nulls.Blob);
        Assert.Null(nulls.Note);

        // The column comes from the attribute, and the table is STRICT.
        Assert.Equal(1L, await db.ScalarAsync<long>($"""select count(*) from sqlite_schema where name = 'things' and sql like '%"note_text" TEXT%'""", Ct));
        OneLiteException e = await Assert.ThrowsAsync<OneLiteException>(async () => await db.ExecuteAsync($"insert into things(id, name, active, kind, ref, at) values ({5}, {"x"}, {"not a number"}, {1}, {"r"}, {"t"})", Ct));
        Assert.Equal(ResultCode.Constraint, e.Code);
    }

    [Fact]
    public async Task InitOnlyAndRequiredMembersMap()
    {
        await using Database db = await Mem();
        await db.Table<Setting>().EnsureCreatedAsync(Ct);
        await db.Table<Setting>().InsertAsync(new Setting { Key = "a", Value = 3 }, Ct);
        Setting s = (await db.Table<Setting>().GetAsync("a", Ct))!;
        Assert.Equal(3, s.Value);
        Assert.Equal("a", s.Key);
    }

    [Fact]
    public async Task MappingErrorsNameTheColumnAndTheMember()
    {
        await using Database db = await Mem();
        await db.ExecuteScriptAsync("create table users(id integer primary key, name text, age int, email text); insert into users(id, name, age) values (1, null, 5)", Ct);
        OneLiteMappingException missing = await Assert.ThrowsAsync<OneLiteMappingException>(async () => await db.QueryAsync<User>($"select id, age from users", Ct));
        Assert.Equal("Name", missing.Column);
        Assert.Equal("Name", missing.Member);
        Assert.Contains("'Name'", missing.Message, StringComparison.Ordinal);

        OneLiteMappingException isNull = await Assert.ThrowsAsync<OneLiteMappingException>(async () => await db.QueryAsync<User>($"select * from users", Ct));
        Assert.Equal("name", isNull.Column);
        Assert.Equal("Name", isNull.Member);
        Assert.Contains("NULL", isNull.Message, StringComparison.Ordinal);
        Assert.Contains("User.Name", isNull.Message, StringComparison.Ordinal);

        await db.ExecuteScriptAsync("create table w(a); insert into w values ('not-a-guid')", Ct);
        InvalidOperationException noMapper = await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.QueryAsync<Database>($"select a from w", Ct));
        Assert.Contains("[OneLiteRow]", noMapper.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorsKeepSqliteCodesAndText()
    {
        await using Database db = await Mem();
        OneLiteException e = await Assert.ThrowsAsync<OneLiteException>(async () => await db.QueryAsync<long>($"select * from nope", Ct));
        Assert.Equal(ResultCode.Error, e.Code);
        Assert.Equal("no such table: nope", e.Message);
        await db.ExecuteScriptAsync("create table u(a unique); insert into u values (1)", Ct);
        OneLiteException dup = await Assert.ThrowsAsync<OneLiteException>(async () => await db.ExecuteAsync($"insert into u values ({1})", Ct));
        Assert.Equal(2067, dup.ExtendedCode);
        // The connection stays usable after an error.
        Assert.Equal(1L, await db.ScalarAsync<long>($"select count(*) from u", Ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await db.ExecuteAsync($"select 1; select 2", Ct));
    }

    [Fact]
    public async Task TableInsertUpsertGetWhereDelete()
    {
        await using Database db = await Mem();
        Table<Dog> dogs = db.Table<Dog>();
        await dogs.EnsureCreatedAsync(Ct);
        await dogs.EnsureCreatedAsync(Ct);
        Assert.Equal("dogs", dogs.Name);
        await dogs.InsertAsync(new Dog("cleo", 4), Ct);
        OneLiteException dup = await Assert.ThrowsAsync<OneLiteException>(async () => await dogs.InsertAsync(new Dog("cleo", 3), Ct));
        Assert.Equal(1555, dup.ExtendedCode);
        await dogs.UpsertAsync(new Dog("cleo", 3), Ct);
        await dogs.UpsertAsync(new Dog("rex", 4), Ct);
        Assert.Equal(new Dog("cleo", 3), await dogs.GetAsync("cleo", Ct));
        Assert.Null(await dogs.GetAsync("nobody", Ct));
        int legs = 4;
        Assert.Equal([new Dog("rex", 4)], await dogs.WhereAsync($"legs = {legs}", Ct));
        Assert.Equal(2, (await dogs.WhereAsync($"", Ct)).Count);
        Assert.Equal(["cleo", "rex"], (await dogs.WhereAsync($"1 order by name", Ct)).ConvertAll(d => d.Name));
        Assert.True(await dogs.DeleteAsync("cleo", Ct));
        Assert.False(await dogs.DeleteAsync("cleo", Ct));
        Assert.Equal(1L, await db.ScalarAsync<long>($"select count(*) from dogs", Ct));
        Assert.Equal("CREATE TABLE \"dogs\" (\"Name\" TEXT PRIMARY KEY, \"Legs\" INTEGER NOT NULL) STRICT", await db.ScalarAsync<string>($"select sql from sqlite_schema where name = 'dogs'", Ct));
    }

    [Fact]
    public async Task WriteAsyncCommitsAndReturnsTheResult()
    {
        await using Database db = await Database.OpenAsync(Path1("w.db"), null, Ct);
        await db.ExecuteScriptAsync("create table t(a int)", Ct);
        long? sum = await db.WriteAsync(async tx =>
        {
            await tx.ExecuteAsync($"insert into t values ({1})", Ct);
            await tx.ExecuteAsync($"insert into t values ({2})", Ct);
            return await tx.ScalarAsync<long>($"select sum(a) from t", Ct);
        }, Ct);
        Assert.Equal(3L, sum);
        Assert.Equal(2L, await db.ScalarAsync<long>($"select count(*) from t", Ct));
    }

    [Fact]
    public async Task WriteAsyncRollsBackOnException()
    {
        await using Database db = await Database.OpenAsync(Path1("w2.db"), null, Ct);
        await db.ExecuteScriptAsync("create table t(a int)", Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.WriteAsync(async tx =>
        {
            await tx.ExecuteAsync($"insert into t values ({1})", Ct);
            throw new InvalidOperationException("boom");
        }, Ct));
        Assert.Equal(0L, await db.ScalarAsync<long>($"select count(*) from t", Ct));
        // Inside the delegate the Database itself is off limits (it would wait on its own lock).
        await db.WriteAsync(async tx =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.ScalarAsync<long>($"select 1", Ct));
            await tx.ExecuteAsync($"insert into t values ({3})", Ct);
        }, Ct);
        Assert.Equal(1L, await db.ScalarAsync<long>($"select count(*) from t", Ct));
        WriteTransaction? leaked = null;
        await db.WriteAsync(tx =>
        {
            leaked = tx;
            return Task.CompletedTask;
        }, Ct);
        Assert.Throws<ObjectDisposedException>(() => leaked!.Execute($"select 1"));
    }

    [Fact]
    public async Task WriteAsyncRetriesWhenTheDatabaseIsBusy()
    {
        string path = Path1("busy.db");
        await using Database db = await Database.OpenAsync(path, new DatabaseOptions { JournalMode = JournalMode.Wal, BusyTimeoutMs = 0 }, Ct);
        await db.ExecuteScriptAsync("create table t(a int)", Ct);
        // Another connection holds the write lock for a while.
        using var other = Database.Open(path, new DatabaseOptions { BusyTimeoutMs = 0 });
        using Connection oc = other.Connect();
        using (Statement begin = oc.Prepare("BEGIN IMMEDIATE"))
        {
            begin.Step();
        }

        int runs = 0;
        var release = Task.Run(async () =>
        {
            await Task.Delay(150, Ct);
            using Statement commit = oc.Prepare("COMMIT");
            commit.Step();
        }, Ct);
        await db.WriteAsync(async tx =>
        {
            runs++;
            await tx.ExecuteAsync($"insert into t values ({1})", Ct);
        }, Ct);
        await release;
        Assert.True(runs >= 1);
        Assert.Equal(1L, await db.ScalarAsync<long>($"select count(*) from t", Ct));
    }

    [Fact]
    public async Task WriteAsyncGivesUpAfterMaxAttempts()
    {
        string path = Path1("busy2.db");
        await using Database db = await Database.OpenAsync(path, new DatabaseOptions { JournalMode = JournalMode.Wal, BusyTimeoutMs = 0 }, Ct);
        db.MaxWriteAttempts = 3;
        await db.ExecuteScriptAsync("create table t(a int)", Ct);
        using var other = Database.Open(path, new DatabaseOptions { BusyTimeoutMs = 0 });
        using Connection oc = other.Connect();
        using (Statement begin = oc.Prepare("BEGIN IMMEDIATE"))
        {
            begin.Step();
        }

        int runs = 0;
        OneLiteException e = await Assert.ThrowsAsync<OneLiteException>(() => db.WriteAsync(tx =>
        {
            runs++;
            return Task.CompletedTask;
        }, Ct));
        Assert.Equal(ResultCode.Busy, e.Code);
        Assert.Equal(0, runs); // BEGIN itself was refused each time, so the delegate never ran
        Assert.Equal(1L, await db.ScalarAsync<long>($"select 1", Ct));
    }

    [Fact]
    public async Task MigrationsApplyOnceInOrder()
    {
        string path = Path1("m.db");
        await using (Database db = await Database.OpenAsync(path, null, Ct))
        {
            await db.MigrateAsync(Ct, "create table a(x)", "create table b(y); insert into b values (1)");
            Assert.Equal(2L, await db.ScalarAsync<long>($"pragma user_version", Ct));
        }

        await using (Database db = await Database.OpenAsync(path, null, Ct))
        {
            await db.MigrateAsync(Ct, "create table a(x)", "create table b(y); insert into b values (1)", "create table a2(z)");
            Assert.Equal(3L, await db.ScalarAsync<long>($"pragma user_version", Ct));
            Assert.Equal(1L, await db.ScalarAsync<long>($"select count(*) from b", Ct));
            // A failing step rolls back everything pending, and the version stays.
            await Assert.ThrowsAsync<OneLiteException>(() => db.MigrateAsync(Ct, "create table a(x)", "create table b(y)", "create table a2(z)", "create table c(q)", "oops"));
            Assert.Equal(3L, await db.ScalarAsync<long>($"pragma user_version", Ct));
            Assert.Equal(0L, await db.ScalarAsync<long>($"select count(*) from sqlite_schema where name = 'c'", Ct));
            // A database ahead of the program is refused.
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.MigrateAsync(Ct, "create table a(x)"));
        }
    }

    [Fact]
    public async Task UserVersionPragmaReadsAndWritesTheHeaderField()
    {
        Assert.SkipUnless(File.Exists(OraclePaths.Cli), "the pinned sqlite3 shell is not installed");
        string path = Path1("uv.db");
        await using (Database db = await Database.OpenAsync(path, null, Ct))
        {
            Assert.Equal(0L, await db.ScalarAsync<long>($"pragma user_version", Ct));
            await db.ExecuteScriptAsync("pragma user_version = 7; create table t(a)", Ct);
            Assert.Equal(7L, await db.ScalarAsync<long>($"pragma user_version", Ct));
            await db.ExecuteScriptAsync("pragma user_version = -3", Ct);
        }

        Assert.Equal("-3", SqliteCli.Run(path, "PRAGMA user_version;").Stdout.Trim());
        SqliteCli.Run(path, "PRAGMA user_version = 11;");
        await using Database again = await Database.OpenAsync(path, null, Ct);
        Assert.Equal(11L, await again.ScalarAsync<long>($"pragma user_version", Ct));
        Assert.Equal("ok", SqliteCli.Run(path, "PRAGMA integrity_check;").Stdout.Trim());
    }

    [Fact]
    public async Task SyncTwinsWork()
    {
        using Database db = Database.OpenDefault(Path1("s.db"));
        db.ExecuteScript("create table t(a int)");
        db.Write(tx => tx.Execute($"insert into t values ({5})"));
        Assert.Equal(5L, db.Scalar<long>($"select a from t"));
        Assert.Equal([5L], db.Query<long>($"select a from t"));
        db.Migrate("create table z(q)");
        Assert.Equal(1L, db.Scalar<long>($"pragma user_version"));
        db.Table<Dog>().EnsureCreated();
        db.Table<Dog>().Upsert(new Dog("a", 1));
        Assert.Equal(new Dog("a", 1), db.Table<Dog>().Get("a"));
        Assert.Single(db.Table<Dog>().Where($"legs = {1}"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task DefaultsAreWalAndForeignKeys()
    {
        string path = Path1("d.db");
        await using (Database db = await Database.OpenAsync(path, null, Ct))
        {
            Assert.Equal("wal", await db.ScalarAsync<string>($"pragma journal_mode", Ct));
            Assert.Equal(1L, await db.ScalarAsync<long>($"pragma foreign_keys", Ct));
        }

        // The reference defaults stay as they were for the low-level open.
        using Database plain = Database.Open(Path1("p.db"));
        using Connection c = plain.Connect();
        using Statement st = c.Prepare("pragma foreign_keys");
        Assert.True(st.Step());
        Assert.Equal(0, st.GetInt64(0));
    }

    [Fact]
    public async Task CancelledQueryThrowsAndLeavesTheDatabaseUsable()
    {
        await using Database db = await Mem();
        await db.ExecuteScriptAsync("create table t(a)", Ct);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await db.QueryAsync<long>($"select 1", cts.Token));
        Assert.Equal(1L, await db.ScalarAsync<long>($"select 1", Ct));
    }

    [Fact]
    public async Task FilesWrittenByTheHappyPathPassSqlite3AndDumpTheSame()
    {
        Assert.SkipUnless(File.Exists(OraclePaths.Cli), "the pinned sqlite3 shell is not installed");
        string ours = Path1("ours.db");
        await using (Database db = await Database.OpenAsync(ours, null, Ct))
        {
            await db.MigrateAsync(Ct, "create table notes(id integer primary key, body text not null) strict");
            await db.Table<User>().EnsureCreatedAsync(Ct);
            await db.Table<Dog>().EnsureCreatedAsync(Ct);
            await db.WriteAsync(async tx =>
            {
                await tx.Table<User>().InsertAsync(new User(0, "ann", 30, "a@x"), Ct);
                await tx.Table<User>().InsertAsync(new User(0, "bob", 41), Ct);
                await tx.Table<Dog>().UpsertAsync(new Dog("cleo", 4), Ct);
                await tx.ExecuteAsync($"insert into notes(body) values ({"hello 'quoted'"})", Ct);
            }, Ct);
        }

        // The same statements through the shell give the same dump.
        string theirs = Path1("theirs.db");
        Assert.Equal(0, SqliteCli.Run(theirs, """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            BEGIN IMMEDIATE;
            create table notes(id integer primary key, body text not null) strict;
            PRAGMA user_version = 1;
            COMMIT;
            CREATE TABLE IF NOT EXISTS "users" ("Id" INTEGER PRIMARY KEY, "Name" TEXT NOT NULL, "Age" INTEGER NOT NULL, "Email" TEXT) STRICT;
            CREATE TABLE IF NOT EXISTS "dogs" ("Name" TEXT PRIMARY KEY, "Legs" INTEGER NOT NULL) STRICT;
            BEGIN IMMEDIATE;
            INSERT INTO "users" ("Id", "Name", "Age", "Email") VALUES (NULL, 'ann', 30, 'a@x');
            INSERT INTO "users" ("Id", "Name", "Age", "Email") VALUES (NULL, 'bob', 41, NULL);
            INSERT INTO "dogs" ("Name", "Legs") VALUES ('cleo', 4) ON CONFLICT ("Name") DO UPDATE SET "Legs" = excluded."Legs";
            insert into notes(body) values ('hello ''quoted''');
            COMMIT;
            """).ExitCode);
        Assert.Equal("ok", SqliteCli.Run(ours, "PRAGMA integrity_check;").Stdout.Trim());
        Assert.Equal(SqliteCli.Dump(theirs), SqliteCli.Dump(ours));
    }
}
