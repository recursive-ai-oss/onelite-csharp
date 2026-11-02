# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- A database on a storage backend with only asynchronous calls (the browser's OPFS on the main thread, HTTP range reads) can now be opened, prepared, stepped and closed without blocking: `PrepareAsync`, `OpenReadOnlyAsync`, `ResetAsync` and `DisposeAsync`. `RecursiveAI.OneLite.Data` and the happy path use them. Cancelling a read that waits on storage releases its lock.
- `HttpRangeVfs`, a read-only backend over HTTP range requests, with a block cache; a file that changes on the server (ETag or Last-Modified) is reported as an I/O error, and journals and write-ahead logs are refused.
- An I/O error from the pager keeps the backend's exception as its inner exception.

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
