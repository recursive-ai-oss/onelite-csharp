# The happy path

An opinionated layer in the core package (`RecursiveAI.OneLite`) for application code: interpolated queries, write transactions as functions, a thin typed table,
source-generated mapping and migrations. It sits on the engine layer (`Database`, `Connection`, `Statement`) and uses nothing else.

```csharp
await using var db = await Database.OpenAsync("app.db");          // WAL, foreign_keys=ON, 5 s busy timeout
var users = await db.QueryAsync<User>($"select * from users where age > {age}");
await db.WriteAsync(async tx => { await tx.Table<Dog>().InsertAsync(cleo); });
```

## `$"..."` binds parameters

**Every `{hole}` in an interpolated string passed to `QueryAsync`, `ExecuteAsync`, `ScalarAsync`, `Where` and their twins is a bound parameter, never pasted
into the SQL.** `$"select * from users where name = {name}"` runs `select * from users where name = ?1` with `name` bound, so it is safe against SQL injection. A
hole is one value (number, string, blob, `Guid`, date, null...); it cannot be a table name, a column name or an SQL fragment. To run text as written, use
`ExecuteScript(string)`, which takes no parameters; never build that text from user input. A plain `string` is deliberately not accepted by the query methods.

## Opening

`Database.OpenAsync(path)` / `Database.OpenDefault(path)` open (creating) a database with write-ahead logging, `foreign_keys=ON` and a 5 s busy timeout, and
return when the connection is ready. WAL is the default because readers and the writer don't block each other (readers see the last committed
snapshot while a write is in progress) and a commit needs fewer syncs (sqlite.org `wal`). Pass `DatabaseOptions` to choose otherwise (they are used as given);
`":memory:"` is a private in-memory database.
**WAL is persistent:** opening an existing rollback-journal file with `OpenAsync`/`OpenDefault` switches that file to WAL for good (it is a property of the
file, as `PRAGMA journal_mode=WAL` is in SQLite), so other programs that open it, `sqlite3` included, see a WAL database and need write access to the
directory for the `-wal` and `-shm` files; pass `DatabaseOptions` to keep the file's journal mode. `Database.Open` is unchanged and keeps the reference defaults. Because the switch to WAL initialises a new file, `PRAGMA auto_vacuum` set afterwards has no effect until a `VACUUM` (as in SQLite; see [engine.md](engine.md)). One `Database` serializes its calls on one connection; open more connections with `Connect()` for
parallel readers.

## Queries

`QueryAsync<T>` returns a `List<T>`. `T` is a type marked `[OneLiteRow]` or a single value type (`long`, `string`, `Guid`...), read from the first column. `ScalarAsync<T>` returns
the first column of the first row, or `default`. `ExecuteAsync` returns the rows changed. Every method has a blocking twin without the suffix. Errors keep SQLite's
code and message (`OneLiteException`). A cached `QueryAsync` completes synchronously and allocates the result objects and the list, about 16 bytes per row for a `long` list and
104 bytes per row for a four-column record, plus a fixed cost per call for the SQL text, the parameters and the compiled statement.

## `WriteAsync`

```csharp
await db.WriteAsync(async tx => { ... });          // returns when committed
var id = await db.WriteAsync(async tx => await tx.Table<Dog>().InsertAsync(d));
```

Runs the delegate in a write transaction (`BEGIN IMMEDIATE` today) and commits. If the database is busy (`SQLITE_BUSY`, including a busy snapshot) the
transaction rolls back and **the delegate runs again** after a jittered backoff (about 10 ms doubling to 500 ms), up to `MaxWriteAttempts` (default 8). **The
delegate must therefore have no side effects outside the database.** An exception from the delegate rolls back and propagates. Use the `WriteTransaction` it
receives; calling the `Database` inside the delegate throws, because the database's lock is held by the transaction. The transaction object is invalid after the delegate returns.

## Mapping

Mark a class, record or struct with `[OneLiteRow]`; the build generates the mapper (no reflection, trimming and NativeAOT safe). Result columns match
constructor parameters and settable or `init` properties by name, ignoring case; extra columns are ignored. `[OneLiteColumn("name")]` renames, `[OneLiteIgnore]`
skips, `[OneLiteKey]` marks the key (without one, a property named `Id`). A missing column for a required member (constructor parameter without a default, or `required`) and a NULL for
a non-nullable member throw `OneLiteMappingException` naming the column, the member and the type. Properties with a getter that cannot be set when reading
(get-only) are written but not read. Diagnostics `ONELITE001`-`ONELITE003` name the type and the member at build time (unsupported property type, no accessible
constructor, type not public or internal).

Supported property types: `bool`, integers, enums (stored as integers), `float`, `double`, `decimal`, `char`, `string`, `byte[]`, `Guid`, `DateTime`,
`DateTimeOffset`, `TimeSpan`, `DateOnly`, `TimeOnly` and `Nullable<>` of these (stored as the same text forms the ADO.NET provider uses).

## `Table<T>`

`db.Table<T>()` (or `tx.Table<T>()`) is a thin view: `EnsureCreatedAsync` (a `STRICT` table with a column per mapped property, NOT NULL where the C# type is not
nullable, the key as `PRIMARY KEY`), `InsertAsync` (returns the rowid; a single integer key of 0 is left to the database), `UpsertAsync` (`ON CONFLICT (key) DO UPDATE`),
`GetAsync(key)`, `DeleteAsync(key)` and `WhereAsync($"fragment")`. There is no LINQ provider and no change tracking. Raw SQL creates exactly what it says.

## `SearchAsync`: full-text and vector search together

```csharp
var hits = await db.SearchAsync<Doc>("docs", "overdue invoice", embedding, k: 10);
foreach (var h in hits) Console.WriteLine($"{h.Item.Title} score {h.Score:F4} text #{h.TextRank} vector #{h.VectorRank}");
var recent = await db.SearchAsync<Doc>("docs", "overdue invoice", embedding, $"year >= {2024}", k: 5);   // {2024} is a bound parameter
```

**What to index.** The convention for a content table `docs` (a rowid table; the key should be `INTEGER PRIMARY KEY`):

- `docs_fts`: an FTS5 table (`create virtual table docs_fts using fts5(title, body)`), with `rowid` = the row's id in `docs`;
- `docs_vec`: a `vec0` table (`create virtual table docs_vec using vec0(embedding float[768])`), with `rowid` = the same id.

You keep the index tables in step yourself (insert them in the same `WriteAsync` as the content row, or use FTS5's external-content mode and triggers). `SearchOptions` renames the
tables and the vector column. Table names are trusted identifiers: never build them from user input. Embeddings are your job; OneLite does not generate them.

**How it works.** Inside one read transaction (one snapshot) the call runs up to two queries: the FTS5 table ordered by `rank` (bm25), and the `vec0` table's exact nearest neighbours.
Each list is cut to `Depth` entries (default `max(100, 4 * k)`). The lists are fused with reciprocal rank fusion (Cormack, Clarke and Büttcher, SIGIR 2009): a row's score is
the sum, over the lists it appears in, of `weight / (RrfConstant + rank)` with ranks counted from 1, constant 60 and weights 1 by default. Rows that only one list finds still score.
Higher is better; equal scores order by the smaller row id. The top `k` rows are then read from the content table with the generated mapper and returned as `SearchHit<T>`:
the row, its row id, the fused score and the 1-based rank in each list (null where the row is absent). Only ranks matter, so the bm25 and distance scales never need normalising.

**Text is words, not syntax.** The text is split on white space and every word is searched as a quoted FTS5 string, so `AND`, `NEAR`, `*`, `^`, `col:`, parentheses and quotes in user
input are plain words and cannot raise a syntax error or reach other columns. Words are joined with OR (rows with more of them rank higher by bm25); `MatchAllWords = true` requires all of them.
FTS5 tokenizes the words as the index does, so a word made only of punctuation matches nothing.

**Choosing the mode.** Pass `text` only (empty embedding) for full-text, `embedding` only (`text` null) for vector search, or both. With neither, the call throws `ArgumentException`.

**Filter.** The `$"..."` overload adds a condition on the content table's columns. It is applied inside both lists, so you get the best `k` rows that satisfy it, not the best rows with some removed afterwards.

**Also** on `WriteTransaction` (it sees the transaction's own writes) and as blocking `Search<T>`. Errors keep SQLite's code and message (a missing index table, a vector of the wrong dimension).
It runs on `PrepareAsync` and `StepAsync`, so it suspends on cache misses of a slow backend, including a backend with only asynchronous calls.

## Migrations

`MigrateAsync("create table ...", "create index ...")` brings the file up to date using `PRAGMA user_version` as the counter: script *n* runs when the version is below
*n*, all pending scripts commit together, and a failing script leaves the version and schema as they were. Append scripts; never edit one that has shipped. A file whose version is
higher than the number of scripts throws.

Files written through the happy path are ordinary SQLite files: `integrity_check` passes in `sqlite3` and a `.dump` matches the same statements run in the shell.
