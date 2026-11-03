# OneLite

OneLite is a pure-managed, SQLite-compatible embedded SQL database for .NET. It is written in C# on the base class library alone, with no native code, so it runs wherever .NET runs: trimmed and NativeAOT builds, and the browser.

The goal is a drop-in for SQLite at four layers, behaving identically by default:

- **File format.** Database, rollback journal, WAL and shared-memory files are read and written both ways, and can be used live beside `sqlite3` processes.
- **SQL semantics.** Type affinity, collation, `NULL` handling, result text, result and extended codes, and error messages.
- **C-API semantics.** Prepare and step, locking, busy handling, hooks.
- **Extensions that live in files.** FTS3/4/5 and R*Tree.

Improvements are opt-in and keep files standard: row-level `BEGIN CONCURRENT`, SQLCipher-4-compatible encryption, a resumable VM with sync and async drivers over a pluggable VFS, an exact-nearest-neighbour `vec` table, clearer errors, and a read-only forensic recovery API.

## Status

Preview (`0.6.0-preview.2`). The storage layer, the SQL engine, `RecursiveAI.OneLite.Data` and the happy-path API work, as do FTS3/4/5, R*Tree, `vec0` (OneLite's own vector table: it follows sqlite-vec's SQL API but its files are not interchangeable with sqlite-vec's, see [docs/vec.md](docs/vec.md)), the backup API, SQLCipher-4-compatible encryption, and opt-in `BEGIN CONCURRENT` (row-level concurrent writers on standard WAL files, serializable by default; see [docs/concurrency.md](docs/concurrency.md)). Measured compatibility with SQLite 3.53.4, including every known difference, is in [docs/compatibility.md](docs/compatibility.md). Benchmarks against the native library are in [docs/benchmarks.md](docs/benchmarks.md). Still to come before 1.0: the `Microsoft.Data.Sqlite`/EF Core adapter, the `onelite` command-line tool, and speed work. APIs may change between previews.

## Known issues

**0.2.0-preview.1 can silently drop a write (data loss); upgrade to 0.2.0-preview.2 or later.** In a rollback-journal mode (`journal_mode` DELETE, TRUNCATE or PERSIST, the default for `Database.Open` and `OneLiteConnection`), an `INSERT`, `UPDATE` or `DELETE` in autocommit mode returned success with no change while another connection held a read, in the same process or another one. It now fails with `SQLITE_BUSY` ("database is locked") as SQLite does; WAL mode was never affected. Remaining differences between several connections on one file are listed in [docs/engine.md](docs/engine.md#known-differences-with-several-connections-on-one-file).

**0.2.0-preview.1 could silently lose a key change; upgrade to 0.2.0-preview.2 or later.** `PRAGMA rekey` or `PRAGMA hexrekey` run through a call that reads one row and stops (`ExecuteScalar`, a reader closed early, `Statement.Step` once, the happy-path scalar and query methods) answered `ok` but kept the old key. `ExecuteNonQuery` and running the statement to completion were not affected. Upgrade to 0.2.0-preview.2, or after a rekey in preview.1 reopen the file with the new key to confirm it.

## How it is built

OneLite is a clean-room implementation. It is written from SQLite's public documentation (the file format, locking, atomic commit, WAL, SQL language and C API pages on sqlite.org), Zetetic's published SQLCipher design, and the observed behaviour of the `sqlite3` shell, which serves as a black-box test oracle. No source code of SQLite, SQLCipher or other SQLite implementations was read, ported or translated.

## Packages

| Package | What it is |
|---|---|
| `RecursiveAI.OneLite` | The core: storage layer, engine layer and the happy path. No third-party dependencies. |
| `RecursiveAI.OneLite.Data` | ADO.NET provider with real async. Depends on the core only. |
| `RecursiveAI.OneLite.SQLitePCLRaw` | An `ISQLite3Provider` so `Microsoft.Data.Sqlite` and EF Core can run on OneLite. Depends on the core and `SQLitePCLRaw.core`. Not published yet. |
| `RecursiveAI.OneLite.Cli` | The `onelite` command-line tool (`dotnet tool install`): a shell used like `sqlite3`, with `.dump`, `.import` and a read-only `.recover` ([docs/cli.md](docs/cli.md)). Not published yet. |

## Building

Requires the .NET SDK pinned in `global.json` (or a later 10.0 feature band).

```sh
dotnet build -c Release
dotnet test -c Release
dotnet pack -c Release
```

Build output goes to `artifacts/` (ignored by git), or to the directory named by the `ONELITE_ARTIFACTS` environment variable; give each checkout its own. `build/ci.sh` runs what CI runs on Linux x64: build, test, a NativeAOT smoke publish, a browser-wasm smoke build and pack.

The `Checked` configuration (`dotnet build -c Checked`) is Release code with the `CHECKED` symbol defined, for extra bounds assertions; tests and fuzzing run in it.

## Layout

- `src/` the packages. `src/OneLite.Generators` is the source generator, shipped inside the core package as an analyzer.
- `tests/` unit tests and the AOT and WASM smoke apps.
- `tools/` test-support command-line tools (oracle driver, sqllogictest runner, differential fuzzer, interop, durability and history checkers). Built, never packed.
- `fuzz/` and `bench/` fuzz targets and benchmarks. `docs/` behaviour notes. `testdata/` test data.

## Licence

MIT OR Apache-2.0, at your option. See `LICENSE-MIT` and `LICENSE-APACHE`. Copyright (c) Recursive AI, LLC.

OneLite is an independent project and is not affiliated with, endorsed by, or sponsored by the SQLite developers or Hipp, Wyrick & Company. SQLite is a registered trademark of Hipp, Wyrick & Company.
