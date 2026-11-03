# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- The `onelite` command-line tool (`RecursiveAI.OneLite.Cli`, not yet published): a shell used like `sqlite3`. Output in the `list`, `csv`, `tabs`, `ascii`, `line`, `json`, `column`, `box`, `table`, `markdown`, `quote` and `html` modes is byte for byte the pinned `sqlite3` shell's; `.dump` is identical on Chinook, Northwind and Sakila and loads in either direction; `.import` (CSV and plain) matches the shell's; `.recover` writes live rows and rows carved from free space as SQL without touching the file. See `docs/cli.md`.
- `RecursiveAI.OneLite.SQLitePCLRaw`: an `ISQLite3Provider` for SQLitePCLRaw that runs on OneLite, so `Microsoft.Data.Sqlite` and EF Core's SQLite provider use it without a native library (`OneLiteBatteries.Init()`). `Microsoft.Data.Sqlite`'s own tests pass 670 of 687 and EF Core's SQLite functional tests 37,121 of 37,418 (289 skipped by the suites; the 8 failures are classified in `docs/adapter.md`). Connections through the adapter start with `foreign_keys` on, as the native library bundled with SQLitePCLRaw does; the core and `.Data` keep SQLite's default (off).
- `X REGEXP Y` calls a user function `regexp(Y, X)` as SQLite documents; without one it fails with "no such function: regexp".
- Group commit for `BEGIN CONCURRENT` at `synchronous=FULL` or EXTRA: concurrent commits of one process share WAL syncs. A commit is acknowledged only after a sync that covers it, and readers see it only after that sync, so durability is unchanged; on the test disk eight threads commit 3.4 to 3.7 times as fast as with `BEGIN IMMEDIATE` (before: no gain at FULL). It affects nothing unless `ConcurrentWrites` is on and `BEGIN CONCURRENT` is used; plain `BEGIN`, `BEGIN IMMEDIATE`, autocommit and `synchronous=NORMAL` are unchanged. `DatabaseOptions.ConcurrentGroupCommit = false` turns it off. See `docs/wal.md` and `docs/concurrency.md`.

### Fixed

- `PRAGMA writable_schema=ON` is honoured (it was ignored): the schema table can be written and `sqlite_` names created, so the shell's `.dump` of `AUTOINCREMENT` and virtual-table databases loads.
- The `sql` stored for `CREATE INDEX` and `CREATE VIEW` keeps the text before the closing `;` as the reference does (white space and comments after the last token).
- `PRAGMA table_info` and `table_xinfo` list the columns of virtual tables (hidden columns in `table_xinfo` only).
- An aggregate function in a subquery whose arguments read only columns of an enclosing query belongs to that query, as in SQLite: `SELECT (SELECT max(t.id) FROM u WHERE u.v = sum(t.id)) FROM t GROUP BY ...` no longer fails with "misuse of aggregate". When its arguments read columns of several queries, the innermost owns it; the misuse errors for clauses that cannot hold aggregates (WHERE, ON, GROUP BY, ORDER BY of a plain query, an aggregate inside another aggregate's arguments) have SQLite's wording. `HAVING` no longer makes a query an aggregate query on its own ("HAVING clause on a non-aggregate query", as SQLite).
- `x IN (SELECT ...)` with a REAL-affinity `x` applies REAL to the values of the set, so an integer in the subquery matches the equal floating-point value (as in SQLite; `x = <integer>` does not).
- A `SELECT` stepped on a connection whose other statement then updates or deletes rows it has not reached yet now shows the change (before, a join could return a stale column from the old position mixed with a column of the next row, or fail with an internal error on `rowid`). A row deleted under the cursor reads as no row.
- `PRAGMA index_xinfo` and `index_info` name the column of an index key on a virtual generated column (they reported an expression with a NULL name).
- `LIMIT` and `OFFSET` can no longer refer to columns of the query or of an outer query ("no such column", as SQLite).

## [0.6.0-preview.2]

### Changed

- `Database.HappyPathOptions()` no longer turns `ConcurrentWrites` on. **0.6.0-preview.1 did**, so every happy-path `WriteAsync` ran as `BEGIN CONCURRENT` without any code change; rowids were then not in commit order, and a `WHERE id > :last` poller could miss rows. The default is `BEGIN IMMEDIATE` again, as in 0.3.0-preview.2. To opt in: `Database.HappyPathOptions() with { ConcurrentWrites = true }`.
- `DatabaseOptions` is now a `record` (`with` expressions work; object initializers are unchanged). `.Data` never uses `BEGIN CONCURRENT` (documented).

### Fixed

- Concurrent-transaction rowid hints are kept per database file (the operating system's file identity: device and inode, or volume serial number and file index, plus the creation time), not per path: a database deleted and created again, or replaced by a copy, no longer starts its new rowids where the old file left off. A temp table no longer shares the main database's hint.
- `docs/benchmarks.md`: wording about tuning; a `prepare_cold` row.

## [0.6.0-preview.1]

### Added

- `BEGIN CONCURRENT` (opt-in with `DatabaseOptions.ConcurrentWrites`): row-level concurrent writers on a standard WAL file. Serializable by default; `BEGIN CONCURRENT SNAPSHOT` gives snapshot isolation. A commit takes the WAL write lock, replays the transaction's recorded B-tree work on the latest snapshot (re-checking reads, unique keys, foreign keys and triggers) and appends ordinary WAL frames, or fails with `SQLITE_BUSY_SNAPSHOT`. Files stay standard and a `sqlite3` process can write with a plain `BEGIN` beside it. Rowids chosen by concurrent transactions are not in commit order (a `WHERE id > :last` poller can skip a row); AUTOINCREMENT never reuses an id. Cancelling a commit frees the write lock and leaves the transaction rollback-only. See `docs/concurrency.md`.
- The happy path's `WriteAsync` and `Write` run as `BEGIN CONCURRENT` in WAL mode and retry on a busy snapshot.
- `onelite-history` can drive OneLite itself (`--client onelite`, `onelite-snapshot`).

### Changed

- Preparing a statement again is far cheaper. A finalized `SELECT`, `INSERT`, `UPDATE` or `DELETE` leaves its program, machine and cursors with the connection, and the next `Prepare` of the same text takes them instead of parsing and planning again, as long as the schema, `foreign_keys` setting and text encoding are unchanged (at most 32 texts per connection; virtual-table, DDL, `PRAGMA` and row-counting statements are not kept). `.Data` commands and the happy path get this without any change.
- A read transaction in WAL mode starts and ends without allocating and, while the wal-index snapshot is unchanged, without re-reading the database size and page 1. A cached point lookup (bind, step, reset) now allocates 0 B at engine level, down from 600 B.
- A query on one rowid table that an index can answer completely (a lookup, range or `IN` list whose columns are all in the index or the rowid) reads the index alone instead of following each entry to the table. Rows and their order are unchanged.

### Fixed

- A statement whose schema changed between preparing and running (`SELECT *` after another connection added a column) could throw `IndexOutOfRangeException` when reading a column added by the change; its result buffers now follow the new column count.
- In WAL mode an autocommit write that found a newer commit between reading and taking the write lock failed with `SQLITE_BUSY_SNAPSHOT`; it now restarts on the new snapshot.

## [0.3.0-preview.2]

### Fixed

- The happy path now works on a database opened with `Database.OpenReadOnlyAsync` / `OpenReadOnly`, over any storage backend including `HttpRangeVfs`: `QueryAsync`, `ScalarAsync`, `Table<T>().GetAsync/WhereAsync`, `SearchAsync` and their sync twins read as usual, and a write fails with `SQLITE_READONLY` ("attempt to write a readonly database"). The error for calling them on a `Database.Open` database now names the right methods. `OpenReadOnlyAsync` connects before it returns, so a backend that cannot open the file is reported there.
- `WHERE x IN (list)` and `WHERE x IN (SELECT ...)` on the rowid / `INTEGER PRIMARY KEY`, and on the first column of an index (a `WITHOUT ROWID` primary key included), now seek once for each distinct value instead of scanning the table, in ascending value order with duplicates and NULLs dropped, as SQLite does. Over `HttpRangeVfs` this fetches a few blocks instead of the whole table.
- `PRAGMA incremental_vacuum(N)` through `OneLiteDataReader` now returns its N column-less rows, as `Statement.Step` and the shell do, instead of an empty result; `ExecuteNonQuery` drains them.

## [0.3.0-preview.1]

### Added

- `PRAGMA incremental_vacuum[(N)]` now returns free pages to the file in an `auto_vacuum=INCREMENTAL` database, one empty row per page freed, as SQLite does; the page and free-page counts match it and the file passes `integrity_check` in both directions. Before, the pragma did nothing.
- `PRAGMA optimize[(MASK)]` now analyses the tables SQLite's documented rule picks (an index without statistics, or a queried or all-tables check with about a tenfold change in row count), including the debug mask that lists the `ANALYZE` commands. `ANALYZE <index>` now analyses only that index, and `ANALYZE main` the whole schema. `PRAGMA analysis_limit` is stored but `ANALYZE` still counts every entry.
- A database on a storage backend with only asynchronous calls (the browser's OPFS on the main thread, HTTP range reads) can now be opened, prepared, stepped and closed without blocking: `PrepareAsync`, `OpenReadOnlyAsync`, `ResetAsync` and `DisposeAsync`. `RecursiveAI.OneLite.Data` and the happy path use them. Cancelling a read that waits on storage releases its lock.
- `HttpRangeVfs`, a read-only backend over HTTP range requests, with a block cache; a file that changes on the server (ETag or Last-Modified) is reported as an I/O error, and journals and write-ahead logs are refused.
- An I/O error from the pager keeps the backend's exception as its inner exception.

### Fixed

- Several connections on one file now behave more like SQLite: a write statement refused with `SQLITE_BUSY` counts as running until it is reset and runs again when stepped again; a `ROLLBACK` that undid writes no longer aborts pending read queries; a connection keeps the schema its own DDL produced (no read lock for the next prepare); a stale-schema `CREATE ... IF NOT EXISTS` no-op takes the locks SQLite takes; `PRAGMA journal_mode` among rollback modes, `wal_checkpoint` outside WAL and settings-only pragmas no longer take file locks SQLite does not; `PRAGMA journal_mode=off` is accepted. The remaining differences are listed in docs/engine.md.
- Informational pragmas and `integrity_check` now notice a schema change made by another connection instead of reporting false errors.
- A PERSIST-mode journal reserve resumed in the wrong state and could stop with an internal error.
- FTS5 `snippet()` now picks the same window as SQLite. The window is chosen by scoring candidate windows (distinct phrases first, then instances), with a bonus for a window that starts at a sentence start and a larger one for the start of the text; a sentence start at the match itself, phrases that begin inside a window but end past it, and the column choice of `snippet(t, -1, ...)` follow the observed rules, which are written down in `docs/fts5.md`. In a 25-minute random differential run against the reference shell, 17 of 647 cases used to differ in the window; none does now. Highlight and `bm25` follow the reference where a `NOT` has an empty left side, and a `trigram` sentence start is found the way the reference finds it.

## [0.2.0-preview.2]

### Fixed

- **Data loss: a write that could not get the EXCLUSIVE lock at commit reported success.** In a rollback-journal mode, an `INSERT`, `UPDATE` or `DELETE` in autocommit mode returned with no error and 0 changes while another connection (a second `Connection`, a second `Database`, an open `DbDataReader`, or another process such as `sqlite3`) held a read, and nothing was stored. It now fails with `SQLITE_BUSY` ("database is locked") after the busy timeout, in the engine, `RecursiveAI.OneLite.Data` and the happy path. An explicit `COMMIT` keeps its transaction open and can be retried. 0.2.0-preview.1 is affected.
- Locking and transaction state after a refused write now follow SQLite: a refused write gives back the read lock it took for itself; `BEGIN IMMEDIATE`/`EXCLUSIVE` take the lock before entering the transaction and a refused one leaves the connection in autocommit mode; a write transaction that changed nothing still needs EXCLUSIVE to commit; `BEGIN` and `SAVEPOINT` keep the read transaction of a statement that is still running; a refused `RELEASE` of the outermost savepoint keeps the transaction; rolling back a transaction that only read no longer aborts pending queries; the loaded schema is kept after a refused write; a statement that names a table another connection has since created reloads the schema; `wal_checkpoint(TRUNCATE|RESTART|FULL)` still copies frames when another connection holds the write lock; `BEGIN IMMEDIATE` also locks attached databases.
- A new differential test runs random and named schedules of several connections against the native library step by step (result codes, messages, rows, final content), in every journal mode.
- **`PRAGMA rekey` / `PRAGMA hexrekey` read through `ExecuteScalar`, `ExecuteReader` (one row or none), `Statement.Step` once, or the happy-path scalar and query methods answered `ok` but did not change the key.** The new key took effect only when the statement ran to completion. The statement now commits the rekey before it returns its row, so every way of running it changes the key or fails.
- `PRAGMA application_id=N` and `PRAGMA default_cache_size=N` on a database with no pages yet were ignored; they now create the header like `user_version` does.

## [0.2.0-preview.1]

### Known issues

Fixed in 0.2.0-preview.2.

- **Data loss: a write outside an explicit transaction can be silently dropped in rollback-journal mode.** It happens when all of these hold: the database is in a rollback-journal mode (`journal_mode` DELETE, TRUNCATE or PERSIST), which is the default for `Database.Open` and for `RecursiveAI.OneLite.Data`'s `OneLiteConnection`; another connection holds a read (a `SELECT` that has stepped but not finished or been reset, an open `DbDataReader`, or an open read transaction), in the same process or in another process such as `sqlite3`; and an `INSERT`, `UPDATE` or `DELETE` runs in autocommit mode (no `BEGIN`/`BeginTransaction`) on a different connection. The statement then completes without an error, reports 0 changes (`ExecuteNonQuery` returns 0), and nothing is stored; SQLite returns `SQLITE_BUSY`. Inside an explicit transaction the write is not lost: `COMMIT` fails with "database is locked" and can be retried, as in SQLite. WAL mode is not affected. **Workaround:** use WAL (`PRAGMA journal_mode=WAL` once on the file; the default of `Database.OpenAsync` and `Database.OpenDefault`), or wrap writes in an explicit transaction and handle "database is locked", or make sure no other connection holds a read while you write; check that a write reports the expected number of changes. A fix is coming in 0.2.0-preview.2.
- **A key change could be silently lost: `PRAGMA rekey` or `PRAGMA hexrekey` run through a call that reads one row and stops (`ExecuteScalar`, a reader closed early, `Statement.Step` once, the happy-path scalar and query methods) answered `ok` but kept the old key.** `ExecuteNonQuery` and running the statement to completion were not affected. Upgrade to 0.2.0-preview.2, or after a rekey in preview.1 reopen the file with the new key to confirm it.

The SQL engine: reads and writes through SQL, ADO.NET, the happy-path API and the first extensions. Measured compatibility is in [docs/compatibility.md](docs/compatibility.md).

### Added

- SQL writes: `INSERT`, `UPDATE`, `DELETE`, upsert, `RETURNING`, transactions and savepoints, on the rollback journal and WAL.
- The SQL surface: joins, subqueries, CTEs (recursive too), compound queries, aggregates, window functions, views, triggers, generated columns, `STRICT` and `WITHOUT ROWID` tables, `ALTER TABLE`, `ATTACH`, pragmas, JSON and JSONB, math functions, `generate_series`, `json_each` and `json_tree`.
- Clearer errors: exceptions carry the position in the SQL with a caret, the offending name and suggestions; codes and messages are unchanged. Readable plans: a plan tree with estimated and optional actual row counts.
- `RecursiveAI.OneLite.Data`: an ADO.NET provider (`DbConnection`, `DbDataReader`, `DbDataSource`, batching) with real async.
- The happy path: `Database.OpenAsync`, interpolated-SQL queries that bind parameters, `WriteAsync` transactions with retry, `Table<T>`, source-generated mapping and `MigrateAsync`.
- Virtual tables: the module interface, R*Tree, `dbstat`, FTS5, FTS3 and FTS4 (with `fts4aux`) on the documented shadow-table layouts, and `vec0` for exact k-nearest-neighbour search over float32, int8 and bit vectors.
- `SearchAsync` and `Search` on the happy path: full-text (FTS5, bm25) and vector (`vec0`, exact k-NN) results fused by reciprocal rank, with an optional filter.
- The backup API, `VACUUM INTO` and incremental blob I/O.
- `:memory:` databases.

### Changed

- The encryption path (SQLCipher-4-compatible) has passed the project's own review (not an external audit) against the published SQLCipher design and test vectors. Encrypted databases sort in memory only, so no plaintext reaches temporary files.

### Notes

- The `RecursiveAI.OneLite.SQLitePCLRaw` and `.Cli` packages are still placeholders.
- Known differences from SQLite 3.53.4 are listed in docs/compatibility.md.

## [0.1.0-preview.1]

First preview: the storage layer and a read-only SQL engine. Files are the standard SQLite-compatible format; anything not listed here is not implemented yet.

### Added

- Storage layer: the database file format (all page types, overflow chains, freelist, pointer maps and auto-vacuum, UTF-8 and UTF-16, page sizes 512 to 65536, `WITHOUT ROWID`), records, the B-tree with balancing, the pager and page cache, the rollback journal (DELETE, TRUNCATE, PERSIST, MEMORY) with hot-journal recovery, and WAL with the shared `-shm` index, file locking that coexists with other processes, recovery and every checkpoint mode.
- A resumable core: every I/O is a suspend point, so the same code runs under the sync driver (`Statement.Step`) and the async driver (`Statement.StepAsync`) with no allocation on cached reads.
- A pluggable VFS: local files and memory, plus browser OPFS (main thread and worker), Web Locks and database import and export; fault injection for durability tests.
- Engine layer, read-only: `Database.OpenReadOnly`, `Connection.Prepare`, `Statement.Step`/`StepAsync`, typed column readers and `OneLiteException` with result and extended codes. `SELECT` with joins, subqueries, CTEs, compound selects, grouping and aggregates is coming in the next preview; this preview runs single-table queries with expressions, affinity, collation and the core, math and date functions.
- SQLCipher-4-compatible encryption at the storage layer: encrypted database, journal and WAL, passphrase and raw keys, and crash-safe online rekey.
- Forensic reader (`ForensicDatabase`): read-only snapshots as of any WAL commit, free-space reports and record carving; it takes no locks and never writes.

### Notes

- The `RecursiveAI.OneLite.Data`, `.SQLitePCLRaw` and `.Cli` packages are placeholders in this preview.
- The encryption code has not had its review yet; don't rely on it for protecting data.
