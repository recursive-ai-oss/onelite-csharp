# Compatibility

OneLite aims to behave exactly like one pinned SQLite release: **SQLite 3.53.4** (the `sqlite3` command-line shell from sqlite.org, with the compile options it reports through `PRAGMA compile_options`). Results, result codes, extended codes and error messages are compared against that shell. `EXPLAIN` and `EXPLAIN QUERY PLAN` output are not part of the promise; query results are.

This table lists what has been measured and what is known to differ. Gaps are listed, not hidden. OneLite is not affiliated with or endorsed by the SQLite developers; "SQLite" is a registered trademark of Hipp, Wyrick & Company.

How the numbers are produced: [sqllogictest.md](sqllogictest.md), [differential.md](differential.md), [interop-and-durability.md](interop-and-durability.md).

## Summary (0.3.0-preview.1)

| Area | sqllogictest | Differential testing against `sqlite3` | Known gaps |
|---|---|---|---|
| SQL engine (all groups) | 622 files, 7,420,713 records: 5,939,877 pass, 0 wrong answers, 1 explained failure; 1,480,834 records are written for other database engines and skipped | 4 seeds, 22 minutes each, about 12,000 generated cases and 0.8 M checks: 0 unexplained differences | documented classes below |
| Expressions, affinity, collation, casts | `random/expr` 1,200,366 pass | 0 unexplained | text to REAL with 20 or more significant digits can differ in the last bit; UTF-16 text cast from a BLOB that spills to overflow pages |
| Aggregates, GROUP BY, window functions | `random/aggregates` 1,292,350, `random/groupby` 133,338 pass | 0 unexplained | ties inside a window stage may come out in a different order; floating-point cancellation in windowed `sum` |
| Joins, subqueries, CTEs, compound queries, views | `random/select` 1,244,323, `index/view` 109,229 pass | 0 unexplained | a correlated comparison such as `max(b.c) ... WHERE b.c = a.x` can return the text spelling of a value where `sqlite3` returns the converted number (plan-dependent in `sqlite3`) |
| Indexes, ORDER BY, IN, BETWEEN, DELETE | `index/*` all pass (1,730,910 records) | 0 unexplained | rows returned before a runtime error can differ when the plans differ; which of two runtime errors is reported can differ when one is in an ORDER BY or GROUP BY term |
| DDL, ALTER TABLE, triggers, generated columns, STRICT | covered by `evidence/*` (487 pass, 1 explained) | 0 unexplained | |
| Pragmas, ATTACH | — | 0 unexplained | |
| JSON, math functions, `generate_series` | — | 0 unexplained | |
| FTS5 | — | 25-minute run plus six shorter seeds (7,000 cases): no `snippet` window differences; the cases that differ are a malformed-database quirk of the shell on `snippet()` of an UNINDEXED column (we return the text), about 1 in 1,500 cases where `snippet(t, -1, ...)` picks another column when an UNINDEXED column holds a match under `detail=column` or `none`, and one last-digit `bm25` difference on arm64 (see [fts5.md](fts5.md)) | `contentless_delete`, `tokendata` and locale options are not implemented |
| FTS3 and FTS4 | — | 25-minute run: 264 cases, 0 differing | see [fts3.md](fts3.md): `matchinfo` hit totals for multi-phrase queries depend on file layout; `col:"phrase"` without a space |
| R*Tree, `dbstat`, `vec0` | — | — | R*Tree node layout differs from the shell's (results and integrity checks match) |

The one explained sqllogictest failure: the script expects an empty result for `SUM` over integers that overflow, and SQLite 3.53.4 raises `integer overflow`, as OneLite does.

## Files

| Check | Result |
|---|---|
| Files written by `sqlite3` (every page type, overflow, freelists, auto-vacuum modes, encodings, page sizes, WAL states, hot journals) read by OneLite | all identical |
| Files written by OneLite pass `sqlite3`'s `PRAGMA integrity_check` and give the same `.dump` | all pass |
| Real-world databases (Chinook, Northwind, Sakila) | read and written both ways |
| FTS5 and FTS3/4 tables, both directions | FTS3/4: 32 option, page-size and encoding combinations, 32,000 documents, all pass |
| Live coexistence with `sqlite3` processes in WAL mode | 10,000 interleaved commits, consistent both ways |
| SQLCipher 4 files (SQLCipher 4.5.6 and 4.20.0) | read and written both ways |

## Durability

Process kills (SIGKILL at random moments) and fault injection through the VFS (torn writes, lost and reordered syncs), judged by `sqlite3`: 0 corruptions and 0 lost acknowledged commits in every campaign so far, across rollback-journal modes, WAL, encryption and rekey, backup, blob I/O, `vec0`, FTS5 and FTS3/4 workloads.

## Not yet compatible

- Native extensions (`load_extension`) are not supported; managed extensions register through the core's interfaces.
- Geopoly, sessions and changesets, `carray` and the shell's miscellaneous extensions are not implemented.
- The `RecursiveAI.OneLite.SQLitePCLRaw` adapter and the `onelite` command-line tool are placeholders in this preview.
- `ANALYZE` under `PRAGMA analysis_limit` and `PRAGMA optimize` always count every index entry, where the reference estimates on large indexes; see "Statistics and incremental vacuum" in the engine notes.
