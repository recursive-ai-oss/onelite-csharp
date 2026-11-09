# sqllogictest runner

`tools/OneLite.SqlLogicTest` runs sqllogictest scripts against an engine and reports, per file and per directory, how many records passed. It is our own implementation of the script format as the project describes it (the `about.wiki` page of the corpus mirror) plus what the scripts themselves show. The corpus is data: scripts are fetched by `tools/scripts/fetch_corpora.py` into `~/onelite/corpus/sqllogictest/` and never committed.

Where the format description is silent, the runner's behaviour was settled by running the corpus's own expectations against the pinned `sqlite3` 3.53.4. Those decisions are listed under "Rendering decisions".

## Reproducing the published count

The published result (5,939,878 records passed, 1 explained failure, 1,480,834 skipped; table at the end) is OneLite running every statement of every script: the `onelite-rw` engine, which is the default. From a fresh clone, on Linux x64 or macOS arm64, with Python 3 and the .NET SDK named in `global.json`:

```
git clone https://github.com/recursive-ai-oss/onelite-csharp
cd onelite-csharp
python3 tools/scripts/fetch_oracles.py --skip-sqlcipher
python3 tools/scripts/fetch_corpora.py --only sqllogictest
dotnet build tools/OneLite.SqlLogicTest -c Release
tools/scripts/sqllogictest_sweep.sh slt-out 4
```

- `fetch_oracles.py` downloads the pinned SQLite 3.53.4 precompiled tools from sqlite.org and the native library of the same release (from the `SQLite` NuGet package), checks their hashes and unpacks them under `~/onelite/oracles/`. The runner needs the native library even for OneLite: it prints the reference's version and compile options in the report header, and the `sqlite` engine runs on it. `--skip-sqlcipher` leaves out the SQLCipher oracle, which only the encryption tests use.
- `fetch_corpora.py --only sqllogictest` downloads the 622 test scripts of the pinned corpus commit into `~/onelite/corpus/sqllogictest/` and checks each file's git hash. Set `GITHUB_TOKEN` if GitHub's unauthenticated rate limit stops it; a rerun resumes.
- Set `ONELITE_HOME` to use another directory than `~/onelite` for both; the runner and the sweep script read it too.
- The build writes to `artifacts/` in the clone (or to `$ONELITE_ARTIFACTS`).
- `sqllogictest_sweep.sh OUT SHARDS` runs the corpus in `SHARDS` parallel processes (about 250 CPU-seconds in total, 90 s with 4 shards, on a recent x64 machine), then prints the report's totals and writes `OUT/summary.md`. The `total` row should read 622 files, 7,420,713 records, 5,939,878 passed, 1 failed, 1,480,834 skipped; the failure is the one explained in `known-differences.json`. The script exits 0 only when the report has no unexplained failure, missing file or stopped file.
- `SLT_ENGINE=sqlite tools/scripts/sqllogictest_sweep.sh slt-native 4` runs the same corpus on the pinned native library instead; it gives the same counts.

## Running

```
sqllogictest run    --corpus DIR --out DIR [--engine onelite-rw|sqlite|onelite] [--shard i/n] [--filter GLOB[,GLOB]]
                    [--max-minutes N] [--force] [--scratch DIR]
sqllogictest report --out DIR [--corpus DIR] [--known FILE]
sqllogictest check-printf [COUNT [DUMP_FILE]]
```

- `--corpus` defaults to `<ONELITE_HOME or ~/onelite>/corpus/sqllogictest`.
- `--filter` takes comma-separated globs (only `*`) over paths relative to the corpus, for example `select*,evidence/*,index/in/10/*`.
- `--shard i/n` (1 ≤ i ≤ n) splits the filtered files by size: largest first onto the lightest shard, ties by name. The split depends only on the file list and sizes, so it is the same on every machine.
- Each finished script writes `OUT/files/<path>.json` (renamed into place, so a killed run leaves whole files only). A rerun skips scripts that have a result, which makes a sweep resumable; `--force` redoes them. `--max-minutes` stops starting new scripts after that long and exits with code 3, so a long sweep runs as chunks that fit a time limit.
- `report` merges the per-file results into `OUT/summary.json` and `OUT/summary.md` (header, totals, a table per directory, explained and unexplained failures with SQL, expected and actual). The exit code is 0 only when every record passed or is explained, no corpus file is missing and no file stopped early.
- `--engine` defaults to `onelite-rw` (see "Engines").
- `tools/scripts/sqllogictest_sweep.sh OUT [SHARDS]` runs the shards as parallel processes and then the report. It reads `SLT_ENGINE` (default `onelite-rw`), `ONELITE_HOME`, `SLT_CORPUS`, `SLT_KNOWN` and `SLT_MAX_MINUTES`, and exits non-zero if a shard or the report fails.

Groups in the report: `select*` (the top-level `select1..5.test`), `evidence`, `index/<name>`, `random/<name>`.

**Counting.** A *record* is a `statement` or `query`; `halt` and `hash-threshold` are not counted. A record is *skipped* when its `skipif`/`onlyif` lines exclude it, *failed* when it ran and did not match (a malformed record counts as failed), *passed* otherwise. A file whose engine throws something other than a statement error (for example an internal error) stops with a `fatal` note instead of recording every remaining record as a failure.

**Known differences.** `testdata/sqllogictest/known-differences.json` lists failures that are understood and accepted, each as `{file, line, reason}`. `report --known` counts them as explained. An entry that does not occur in a run is listed as stale.

## The format as implemented

A script is a sequence of records separated by blank lines. Lines starting with `#` are comments: they are dropped, and they do not separate records (so a comment may sit inside a statement). A record is optionally prefixed by `skipif NAME` / `onlyif NAME` lines, then one of:

| Record | Meaning |
|---|---|
| `statement ok` / `statement error` | The following lines, up to a `----` line, are one SQL command. It must succeed / must fail. Text after a `----` is kept but not compared (the error text is not part of the format). |
| `query <types> [<sort>] [<label>]` | SQL up to `----`, then expected values, one per line. `<types>` has one of `I`, `T`, `R` per column. `<sort>` is `nosort` (default), `rowsort` or `valuesort`. If the second word is not a sort mode it is the label. A trailing `# note` after the label is ignored. |
| `halt` | Ignore the rest of the script. |
| `hash-threshold N` | Parsed and recorded. In validation a script's hashed results are compared by hash whatever the threshold; the threshold only controls how a script is *written*. |

The expected values may be replaced by a single line `N values hashing to <32 lower-case hex digits>`. A query with no `----`, or nothing after it, expects an empty result.

**Conditionals.** A record is skipped if any `skipif` names the engine, or if there are `onlyif` lines and none names the engine. Any text after the engine name (`skipif mysql # not compatible`) is ignored. The runner reports the engine name `sqlite` for every engine. They apply to `halt` too: `skipif sqlite` + `halt` stops every engine except SQLite.

**Labels.** The first query with a label stores the hash of its result; each later query with the same label in the same file must produce the same hash. A skipped query still takes part: its recorded result (the hash in the script, or the hash of its listed values) is checked against the label.

**Malformed records** (unknown keyword, `statement` without `ok`/`error`, a type string with other letters, a conditional with no record, a bad `hash-threshold`) are reported as failed records with the problem and the parser resumes at the next blank line.

## Rendering decisions

The format says integers are `%d`, floats `%.3f`, NULL is `NULL`, an empty string is `(empty)` and unprintable characters are `@`. It leaves open what happens when the value's storage class differs from the column's type letter, how the floats are rounded, and how rows are sorted. These were settled against the corpus on SQLite 3.53.4; every choice below reproduces all corpus expectations.

1. **Conversion by type letter.** NULL is `NULL` whatever the letter. Otherwise the value is converted to the letter's type first: `I` takes the integer a float truncates to (toward zero, saturating) or the integer prefix of text (blanks, sign, digits; 0 if none), `R` takes the float of an integer or of the numeric prefix of text, `T` takes the text of the value. Evidence: `evidence/slt_lang_aggfunc.test` expects `0` for `query I` on `min(y)`/`max(y)` over the text `'NULL'`/`'true'`.
2. **`%.3f` is SQLite's own printf, not C's.** The scripts were written with SQLite's rendering: `-9223372036854775808` shows as `-9223372036854776000.000` (at most 16 significant digits, then zeros) and exact ties round up (`0.0625` → `0.063`). `Rendering.FormatFixed3` takes the exact binary value, cuts it to 18 significant digits, then rounds half up at the third decimal or at the 16th significant digit, whichever is first. A negative value that rounds to zero keeps its sign (`-0.000`); negative zero does not. `check-printf` compares this against the pinned library's `printf('%.3f', x)` over generated doubles (random bit patterns, decimal ties, binary ties, ratios, huge values): 131 of 400,027 differ, all values within about 1e-18 relative of a rounding tie (for example `-13.9055`) or large values whose 16th digit is a near tie. Reproducing those would need SQLite's exact digit generation; the corpus's own values do not hit them (see results).
3. **Float as text** (`T` column holding a float) uses 15 significant digits with `.0` for whole values, as SQLite converts a float to text. No corpus record depends on it.
4. **Unprintable.** Every UTF-8 byte of a text value outside 0x20..0x7e is shown as `@`; a blob is its bytes read as text.
5. **`rowsort`** orders rows by their rendered values column by column, comparing ordinally (`"10"` before `"9"`); **`valuesort`** orders the flattened values ordinally. This reproduces every corpus expectation.
6. **Hash.** MD5 of every value after sorting, each followed by `\n`, as lower-case hex. The count in `N values` is the number of values (rows × columns).
7. **Query errors fail the record.** One corpus record (`evidence/slt_lang_aggfunc.test` line 480, `SELECT sum(x)` over integers that overflow) expects an empty result where SQLite 3.53.4 raises `integer overflow`; it is the single entry in `known-differences.json`.
8. **Column count.** A query whose result has a different number of columns than its type string fails.

## Engines

The runner talks to an engine through `ISltEngine` (name for conditionals, a header, open a fresh database in a scratch directory) and `ISltConnection` (`Execute(sql)` → error or none; `Query(sql)` → column count and rows of typed values). The interface knows nothing about scripts, so other tools can reuse it.

- **`sqlite`**: the pinned native library through `tools/OneLite.Oracle`'s wrapper (`SqliteDb`). Each script gets a fresh database file in the scratch directory, deleted afterwards. The header repeats `oracle`'s: the shell's and the library's versions and compile options. The library is a different build from the `sqlite3` shell (the behavioural reference), so each connection is set up to match the shell where it can change a result:

  | Setting | Why |
  |---|---|
  | `pragma foreign_keys = off` | the library is built with foreign keys on by default; the shell is not |
  | `pragma synchronous = off`, `cache_size = -131072` | speed only; the database is thrown away |

  Differences that do not apply: double-quoted string literals are off in both (`DQS=0`); `LIKE_DOESNT_MATCH_BLOBS` (library only) changes `LIKE` on blobs, which no script uses; `ENABLE_FTS3_PARENTHESIS` (library only) changes FTS3 query syntax, which no script uses. The shell has `STRICT_SUBTYPE`, `percentile`, `carray` and the `dbstat` and `bytecode` tables, which the library lacks; no script uses them.
- **`onelite-rw`** (the default): OneLite runs every statement, setup included, on a fresh database file per script. This engine produced the published count.
- **`onelite`**: read-only OneLite. The pinned native library writes each script's setup statements and OneLite runs the queries on that file. It was used before OneLite could write and is kept for checking the read path alone.

Every engine's `DialectName` is `sqlite`, because the target is SQLite's behaviour.

## Results: the full corpus

Corpus mirror commit `c93ac416f05635ff5cc490009add936d9753d01d`, 622 scripts. The published count is engine `onelite-rw` at release 1.0.0, every statement on OneLite. The pinned native library (engine `sqlite`, library 3.53.4, source id `2026-07-24 19:02:57 bf7c7f30...9bc`) gives exactly the same counts, on x64 Linux and on arm64 macOS:

| group | files | records | passed | failed | skipped |
|---|--:|--:|--:|--:|--:|
| evidence | 12 | 494 | 488 | 1 | 5 |
| index/between | 13 | 124,618 | 124,618 | 0 | 0 |
| index/commute | 52 | 514,321 | 514,321 | 0 | 0 |
| index/delete | 14 | 160,942 | 160,942 | 0 | 0 |
| index/in | 13 | 132,907 | 132,907 | 0 | 0 |
| index/orderby | 31 | 312,955 | 312,955 | 0 | 0 |
| index/orderby_nosort | 49 | 495,167 | 495,167 | 0 | 0 |
| index/random | 26 | 249,543 | 208,168 | 0 | 41,375 |
| index/view | 16 | 123,809 | 109,229 | 0 | 14,580 |
| random/aggregates | 130 | 1,802,553 | 1,292,350 | 0 | 510,203 |
| random/expr | 120 | 1,883,437 | 1,200,366 | 0 | 683,071 |
| random/groupby | 14 | 144,741 | 133,338 | 0 | 11,403 |
| random/select | 127 | 1,464,520 | 1,244,323 | 0 | 220,197 |
| select* | 5 | 10,706 | 10,706 | 0 | 0 |
| **total** | **622** | **7,420,713** | **5,939,878** | **1** | **1,480,834** |

The skipped records are the ones the conditionals send to other engines (`onlyif mysql`, `skipif sqlite`, and so on). The one failure is the entry in `known-differences.json`. A whole sweep takes about 150 CPU-seconds on x64 and 114 s on arm64, so it needs no more than one `--max-minutes` chunk.
