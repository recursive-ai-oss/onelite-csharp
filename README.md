# OneLite

OneLite is a pure-managed, SQLite-compatible embedded SQL database for .NET. It is written in C# on the base class library alone, with no native code, so it runs wherever .NET runs: trimmed and NativeAOT builds, and the browser.

The goal is a drop-in for SQLite at four layers, behaving identically by default:

- **File format.** Database, rollback journal, WAL and shared-memory files are read and written both ways, and can be used live beside `sqlite3` processes.
- **SQL semantics.** Type affinity, collation, `NULL` handling, result text, result and extended codes, and error messages.
- **C-API semantics.** Prepare and step, locking, busy handling, hooks.
- **Extensions that live in files.** FTS3/4/5 and R*Tree.

Improvements are opt-in and keep files standard: row-level `BEGIN CONCURRENT`, SQLCipher-4-compatible encryption, a resumable VM with sync and async drivers over a pluggable VFS, an exact-nearest-neighbour `vec` table, clearer errors, and a read-only forensic recovery API.

## Status

Version 1.0.0. The storage layer, the SQL engine, `RecursiveAI.OneLite.Data`, the happy-path API, FTS3/4/5, R*Tree, `vec0` (OneLite's own vector table: it follows sqlite-vec's SQL API but its files are not interchangeable with sqlite-vec's, see [docs/vec.md](docs/vec.md)), the backup API, SQLCipher-4-compatible encryption, opt-in `BEGIN CONCURRENT` (row-level concurrent writers on standard WAL files, serializable by default; see [docs/concurrency.md](docs/concurrency.md)), the `Microsoft.Data.Sqlite`/EF Core adapter and the `onelite` command-line tool all ship. Measured compatibility with SQLite 3.53.4, including every known difference, is in [docs/compatibility.md](docs/compatibility.md). Benchmarks against the native library are in [docs/benchmarks.md](docs/benchmarks.md).

**Known gaps.** A few benchmark rows are slower than the native library by more than the project's targets (engine 1.5x, app 1.25x): `prepare_cold`, the first prepare of new statement text (x64 engine 2.69x, app 1.80x); `fts5_top10` (app 1.71x x64, 2.13x arm64; engine 1.94x arm64); and on arm64 `prepare_cold` (app 1.51x), `lookup_index` (engine 1.52x, app 1.35x) and `insert_tx_100k` (engine 1.50x). The geometric means are below native on both machines (x64 engine 0.73x, app 0.74x; arm64 0.89x, 0.93x; `open_time`, a report-only row, excluded). See [docs/benchmarks.md](docs/benchmarks.md).

## Known issues

Two published versions have serious defects and are unlisted on nuget.org; use 1.0.0.

- **0.2.0-preview.1** can silently drop a write in a rollback-journal mode and can keep the old key after `PRAGMA rekey`. Fixed in 0.2.0-preview.2 (details in [CHANGELOG.md](CHANGELOG.md)).
- **0.6.0-preview.1** turned `ConcurrentWrites` on by default in `Database.HappyPathOptions()`, so `WriteAsync` ran `BEGIN CONCURRENT`, whose rowids are not in commit order: a poller reading `WHERE id > :last` can miss rows. Fixed in 0.6.0-preview.2, where it is off by default (see [docs/concurrency.md](docs/concurrency.md)).

## How it is built

OneLite is a clean-room implementation. It is written from SQLite's public documentation (the file format, locking, atomic commit, WAL, SQL language and C API pages on sqlite.org), Zetetic's published SQLCipher design, and the observed behaviour of the `sqlite3` shell, which serves as a black-box test oracle. No source code of SQLite, SQLCipher or other SQLite implementations was read, ported or translated.

## Packages

| Package | What it is |
|---|---|
| `RecursiveAI.OneLite` | The core: storage layer, engine layer and the happy path. No third-party dependencies. |
| `RecursiveAI.OneLite.Data` | ADO.NET provider with real async. Depends on the core only. |
| `RecursiveAI.OneLite.SQLitePCLRaw` | An `ISQLite3Provider` so `Microsoft.Data.Sqlite` and EF Core can run on OneLite. Depends on the core and `SQLitePCLRaw.core`. |
| `RecursiveAI.OneLite.Cli` | The `onelite` command-line tool (`dotnet tool install`): a shell used like `sqlite3`, with `.dump`, `.import` and a read-only `.recover` ([docs/cli.md](docs/cli.md)). |

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
