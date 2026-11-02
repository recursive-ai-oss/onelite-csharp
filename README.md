# OneLite

OneLite is a pure-managed, SQLite-compatible embedded SQL database for .NET. It is written in C# on the base class library alone, with no native code, so it runs wherever .NET runs: trimmed and NativeAOT builds, and the browser.

The goal is a drop-in for SQLite at four layers, behaving identically by default:

- **File format.** Database, rollback journal, WAL and shared-memory files are read and written both ways, and can be used live beside `sqlite3` processes.
- **SQL semantics.** Type affinity, collation, `NULL` handling, result text, result and extended codes, and error messages.
- **C-API semantics.** Prepare and step, locking, busy handling, hooks.
- **Extensions that live in files.** FTS3/4/5 and R*Tree.

Improvements are opt-in and keep files standard: row-level `BEGIN CONCURRENT`, SQLCipher-4-compatible encryption, a resumable VM with sync and async drivers over a pluggable VFS, an exact-nearest-neighbour `vec` table, clearer errors, and a read-only forensic recovery API.

## Status

Preview (`0.2.0-preview.1`). The storage layer, the SQL engine, `RecursiveAI.OneLite.Data` and the happy-path API work, as do FTS3/4/5, R*Tree, `vec0`, the backup API and SQLCipher-4-compatible encryption. Measured compatibility with SQLite 3.53.4, including every known difference, is in [docs/compatibility.md](docs/compatibility.md). Still to come before 1.0: `BEGIN CONCURRENT`, the `Microsoft.Data.Sqlite`/EF Core adapter, the `onelite` command-line tool, and published benchmarks. APIs may change between previews.

## Known issues

**Known issue in 0.2.0-preview.1 (data loss): a write can be silently dropped in rollback-journal mode.** When a database is in a rollback-journal mode (`journal_mode` DELETE, TRUNCATE or PERSIST; this is the default for `Database.Open`) and another connection holds a read (a `SELECT` that has stepped but not finished or been reset, or an open read transaction), whether in the same process or another process such as `sqlite3`, an `INSERT`, `UPDATE` or `DELETE` on a different connection completes without an error, reports 0 changes, and is not stored. SQLite returns `SQLITE_BUSY` in that case. WAL mode is not affected. **Workaround:** use WAL (`PRAGMA journal_mode=WAL`, the default of `Database.OpenAsync` and `Database.OpenDefault`), or make sure no other connection holds a read while you write (finish or reset statements first), and check that a write reports the expected number of changes. A fix is coming in 0.2.0-preview.2.

## Packages

| Package | What it is |
|---|---|
| `RecursiveAI.OneLite` | The core: storage layer, engine layer and the happy path. No third-party dependencies. |
| `RecursiveAI.OneLite.Data` | ADO.NET provider with real async. Depends on the core only. |
| `RecursiveAI.OneLite.SQLitePCLRaw` | An `ISQLite3Provider` so `Microsoft.Data.Sqlite` and EF Core can run on OneLite. Depends on the core and `SQLitePCLRaw.core`. Not published yet. |
| `RecursiveAI.OneLite.Cli` | The `onelite` command-line tool (`dotnet tool install`). Not published yet. |

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
