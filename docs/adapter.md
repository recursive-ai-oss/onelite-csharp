# SQLitePCLRaw adapter (`RecursiveAI.OneLite.SQLitePCLRaw`)

An `ISQLite3Provider` for [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) that runs on OneLite's engine. `Microsoft.Data.Sqlite` and Entity Framework Core's SQLite provider use SQLitePCLRaw for every database call, so they run on OneLite, with no native SQLite loaded, by swapping one package and one line of start-up code.

This package is SQLite-compatible and is not affiliated with SQLite's authors.

## Use

```csharp
// instead of SQLitePCL.Batteries_V2.Init() / bundle_e_sqlite3
RecursiveAI.OneLite.SQLitePCLRaw.OneLiteBatteries.Init();

using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=app.db");
connection.Open();
```

Reference `Microsoft.Data.Sqlite.Core` (not `Microsoft.Data.Sqlite`, whose default bundle brings the native library) and this package. `Init()` calls `SQLitePCL.raw.SetProvider` with a shared provider; it is safe to call more than once and re-selects OneLite if another provider was set meanwhile. The package depends on `RecursiveAI.OneLite` and `SQLitePCLRaw.core` only, tracks SQLitePCLRaw 3.x (the major version current `Microsoft.Data.Sqlite` 10 uses), and is NativeAOT- and trim-clean (a Microsoft.Data.Sqlite application published with NativeAOT is part of the tests).

`sqlite3_libversion()` reports `3.53.4`, the SQLite release OneLite is compatible with; `sqlite3_libversion_number()` is `3053004`. `GetNativeLibraryName()` returns `OneLite (managed)`.

## Foreign keys are on by default in the adapter

The adapter starts every connection with `PRAGMA foreign_keys=ON` (`OneLiteBatteries.ForeignKeysByDefault`, default `true`, `src/OneLite.SQLitePCLRaw/OneLiteBatteries.cs:24`; applied in `Provider.Connection.cs:84`). It replaces `bundle_e_sqlite3`, whose native library is built with foreign keys on, and `Microsoft.Data.Sqlite` and EF Core rely on that. The core (`DatabaseOptions.ForeignKeys`, `src/OneLite/Engine/Connection.cs:63`) and `RecursiveAI.OneLite.Data` (`OneLiteConnection.cs:126`) keep SQLite's own default, off. Set `OneLiteBatteries.ForeignKeysByDefault = false` before opening connections to get off through the adapter; an explicit `PRAGMA foreign_keys` or `Foreign Keys=` connection-string key always wins. A test (`ForeignKeysDefaultOnlyInTheAdapter`) pins both defaults.

## How handles and strings work

- A `sqlite3`, `sqlite3_stmt`, `sqlite3_blob` or backup handle is an index into a table of managed objects, so a handle that is closed, finalized or forgotten is detected (the call returns `SQLITE_MISUSE`) instead of crashing. Finalizers release anything an application leaks.
- Each `sqlite3*` owns one engine database and connection. `sqlite3_close` fails with `SQLITE_BUSY` while statements are unfinalized; `sqlite3_close_v2` closes at once.
- Text crosses as UTF-8 and is decoded as UTF-8. Invalid UTF-8 in a bound text value is replaced, not preserved byte for byte (bind a `BLOB` for arbitrary bytes). Prepare tails point into the caller's own bytes.
- A result code, extended code and message equal SQLite's for the same statement and state (`sqlite3_errmsg`, `sqlite3_errstr`, `sqlite3_extended_errcode`, `sqlite3_extended_result_codes`); the differences are in the table below.

## What is supported

Open (`sqlite3_open`, `_v2`, URI filenames with `mode=`, `cache=shared`, `SQLITE_OPEN_*` flags, in-memory and shared in-memory databases, `Vfs=` names SQLite ships), close, `prepare_v2/v3` with tail, `step`, `reset`, `finalize`, all `bind_*` and `column_*` accessors (including `column_decltype`, `column_database_name`, `column_table_name`, `column_origin_name`, `table_column_metadata`), `bind_parameter_*`, `clear_bindings`, `changes`, `total_changes`, `last_insert_rowid`, `get_autocommit`, `busy_timeout`, `busy_handler` (called with the retry count), `interrupt`, `limit`, `exec` (with a row callback and an error message the caller frees with `sqlite3_free`), `stmt_readonly`, `stmt_busy`, `next_stmt`, `db_filename`, `db_readonly`, `keyword_count/name`, `create_function` (scalar and aggregate, `SQLITE_DETERMINISTIC` honoured), `create_collation`, `commit_hook`, `rollback_hook`, `update_hook`, `progress_handler` (see below), the online `backup_*` API, incremental `blob_*` I/O, `key` / `rekey` (SQLCipher-4 compatible encryption; `Password=` in a connection string works), `sqlite3_malloc` / `free`.

The hooks fire as SQLite's do. The update hook runs for INSERT, UPDATE and DELETE of a rowid table after the row is written (not for `WITHOUT ROWID` tables, for rows removed by `REPLACE` conflict resolution, or for a `DELETE` without `WHERE` on a table with no triggers, foreign keys or `RETURNING`, which SQLite's truncate optimization also leaves unreported). The commit hook runs before a commit and, if it returns non-zero, turns the commit into a rollback and the `COMMIT` fails with `SQLITE_CONSTRAINT_COMMITHOOK`. The rollback hook runs after a rollback, an explicit one or one forced by an error.

## What is not supported

Each of these returns a documented result, never a silent success.

| Call | Behaviour |
|---|---|
| `load_extension`, `enable_load_extension` | The flag is stored; loading fails with `SQLITE_ERROR` "load_extension is not supported by OneLite; register managed extensions through the engine API". With extensions disabled the message is "not authorized", as in SQLite. |
| `set_authorizer` | A non-null authorizer fails with `SQLITE_ERROR`. |
| `trace`, `profile` | Accepted and never called. |
| `progress_handler` | Called once per `step`, not every N virtual-machine instructions. A non-zero result interrupts the statement as in SQLite. |
| `serialize`, `deserialize` | `SQLITE_ERROR`; use the engine's backup API. |
| Snapshots (`snapshot_get/open/cmp/free/recover`) | `SQLITE_ERROR`. |
| `db_config` | `SQLITE_ERROR`. |
| `sqlite3_stricmp`, `strnicmp` | They take native C strings; OneLite has none. |
| User functions on a UTF-16 database | Not supported; UTF-8 databases only. |
| Shared-cache table locks (`SQLITE_LOCKED_SHAREDCACHE`) | A shared in-memory database is one engine database used through several connections; its writers do not take table locks, so a reader sees an uncommitted write instead of `SQLITE_LOCKED`. |
| `EXPLAIN` | Bytecode listings are not produced (`unsupported: EXPLAIN`); `EXPLAIN QUERY PLAN` text differs. |
| `PRAGMA function_list` / its table-valued form | Not implemented. |
| `X REGEXP Y` | Calls the user function `regexp(Y, X)` as documented; with none registered it fails with "no such function: regexp" (as the native library words it). OneLite ships no `regexp` of its own. |

## Test results

`tests/OneLite.SQLitePCLRaw.Tests` runs the same `sqlite3_*` calls through SQLitePCLRaw on the native library (`SQLitePCLRaw.bundle_e_sqlite3`, SQLite 3.53.4) and on the adapter and compares return codes, extended codes, messages, column names, declared types, values, `changes()`, `last_insert_rowid()` and hook calls.

`Microsoft.Data.Sqlite`'s own public test suite and Entity Framework Core's SQLite functional tests run against the adapter; `tools/scripts/fetch_efcore_tests.sh` fetches only their `test/` projects at a pinned release (never committed), and `tools/scripts/mds_suite.sh` and `tools/scripts/ef_suite.sh` run them. The results and the classified failures are in the table at the end of this file.
