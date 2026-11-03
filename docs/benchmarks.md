# Benchmarks

OneLite's own benchmark suite lives in `bench/OneLite.Bench`. It compares OneLite with the reference SQLite library at two levels and measures nothing else. The tables below are the first published baseline (commit `7e028d2`). Speed is a target, not a release gate: the reference ratios are information, and nothing has been tuned yet.

## Method

**Levels**

- **Engine:** OneLite's `Statement.Step` (sync driver) against the native `sqlite3_step`, called through a raw `LibraryImport` of `libe_sqlite3` 3.53.4. The P/Invoke code exists only in the bench project.
- **App:** `RecursiveAI.OneLite.Data` against `Microsoft.Data.Sqlite` 10.0.14 on `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5 (SQLite 3.53.4). Both go through one shared driver, so the code above the ADO.NET API is identical.

**Settings, identical on both sides.** Page size 4096, `cache_size = -2000` (2 MB), the same file system (a scratch directory on local disk), the same data generator and seed, the same SQL. `journal_mode` and `synchronous` are set per row by `PRAGMA` on both sides (default WAL/NORMAL; `insert_autocommit` uses `delete`/FULL; `wal_commit_full` uses WAL/FULL). The base table has 100,000 rows. Each side computes a checksum of what the queries returned and the report shows "Results agree".

**Measurement.** Every row × level × side runs in its own child process, so peak memory is clean (it includes the runtime baseline of roughly 35–50 MB). Each runs one warm-up and 3 timed repetitions; the report gives the median. The allocation column is `GC.GetAllocatedBytesForCurrentThread` per operation. There is no BenchmarkDotNet; the harness is a plain stopwatch loop, because 60+ process-isolated measurements would not fit the time boxes.

**Runs.** One run per machine, in two groups (A: write, prepare, open and cold-start rows; B: read and query rows), each run alone on the machine by a queue that does not start anything else beside a timing job. The two groups are merged; the geometric mean covers every row except `open_time`, which is reported only.

Reproduce with `bench/run.sh --rows <a,b|all> --levels engine,app --reps 3 --out <prefix>` (`--vec` points at the sqlite-vec loadable for the `vec0_knn` reference; `--aot` at the published `bench/OneLite.Bench.Cold` for the NativeAOT cold-start row). `OneLite.Bench merge` combines filtered runs.

**Rows.** `insert_tx_10k` and `insert_tx_100k` (bulk insert in one transaction); `insert_autocommit` (200 single-row commits); `wal_commit_normal` and `wal_commit_full` (1,000 commits); `lookup_rowid` and `lookup_index` (200,000 point lookups); `scan_filter` (unindexed scan with a filter); `range_scan`; `join` (two tables); `group_by`; `order_by_sort` (sort larger than the cache); `fts5_top10` (FTS5 `MATCH`, top 10 by bm25); `vec0_knn` (`vec0` k-NN, against sqlite-vec 0.1.9's prebuilt extension); `prepare_cost`; `open_time` (open, one query, close). Cold start (process start to the first answer, JIT and NativeAOT) is separate.

## Read these first

- **`fsync` against `fdatasync`.** On Linux the pinned `libe_sqlite3` issues `fsync` for each commit sync and OneLite issues `fdatasync`. The number of syncs is the same (about 2,012 for 2 × 1,000 commits in `strace`), but `fdatasync` skips the file-metadata flush, so `wal_commit_full` (0.28× on x64) and `insert_autocommit` (0.85×) are faster than native for a reason that is not engine speed. It is a property of the reference build, not a durability gap by count. On macOS the two sides are close (1.9× and 1.1×).
- **Concurrent writers.** The `BEGIN CONCURRENT` throughput row is not part of this baseline. It is pending until the feature's measurements are ready; there is no number for it here.
- **Short rows include JIT.** `insert_tx_10k` runs about 80 ms, and the warm-up is a single run, so tier-0 code is part of the timing. `insert_tx_100k` shows the steadier ratio (3.9× against 18.7× on x64).
- **Allocation.** The cached hot path allocates nothing per *row stepped* (`scan_filter` 0.2 B/op, `join` 0.4 B/op). It does not yet allocate nothing per *statement execution*: a point lookup (bind, step, reset) costs 600 B at engine level and 15–19 KB through `.Data`. `fts5_top10` allocates 5.7 MB per query and a prepare costs about 23 KB.
- **Mac load.** The Mac was not idle by itself: its 1-minute load average sat near 2 before, during and after both groups, with no other queue job running. Treat the arm64 numbers as slightly less quiet than the x64 ones.

## x64: AMD Ryzen 9 7950X3D, 32 cores, Linux Mint 22.3

.NET 10.0.13 (SDK 10.0.402), SQLite 3.53.4, `Microsoft.Data.Sqlite` 10.0.14. Load average (1 min): group A 0.76 before, 1.83 highest, 1.77 after; group B 1.25 before, 1.28 highest, 1.24 after. Group B was run twice. The first run started at a load of 4.54 and could not be shown to have been alone, so it was discarded and re-run; the re-run agrees with it to within a few percent on every row.

### Engine level

| Row | Ops | OneLite ms | Native ms | Ratio | OneLite B/op | Peak MB (OneLite / native) |
|---|---:|---:|---:|---:|---:|---:|
| insert_tx_10k | 10,000 | 82.50 | 4.42 | 18.65× | 435 | 62 / 41 |
| insert_tx_100k | 100,000 | 258.69 | 66.80 | 3.87× | 441 | 116 / 66 |
| insert_autocommit | 200 | 3204.15 | 3791.29 | 0.85× | 1,545 | 57 / 36 |
| wal_commit_normal | 1,000 | 50.00 | 28.02 | 1.78× | 2,311 | 56 / 33 |
| wal_commit_full | 1,000 | 1803.98 | 6411.15 | 0.28× | 2,431 | 62 / 36 |
| prepare_cost | 3,000 | 161.19 | 8.26 | 19.52× | 23,315 | 102 / 34 |
| open_time (report only) | 200 | 1269.81 | 35.13 | 36.15× | 54,539 | 113 / 59 |
| lookup_rowid | 200,000 | 709.25 | 481.79 | 1.47× | 600 | 118 / 59 |
| lookup_index | 200,000 | 898.92 | 367.41 | 2.45× | 600 | 122 / 60 |
| scan_filter | 100,000 | 20.48 | 4.62 | 4.44× | 0.2 | 108 / 58 |
| range_scan | 2,000 | 72.60 | 16.09 | 4.51× | 610 | 111 / 60 |
| join | 100,000 | 23.28 | 68.69 | 0.34× | 0.4 | 112 / 60 |
| group_by | 100,000 | 125.07 | 22.13 | 5.65× | 122 | 115 / 60 |
| order_by_sort | 100,000 | 101.74 | 31.46 | 3.23× | 273 | 114 / 61 |
| fts5_top10 | 300 | 476.94 | 43.72 | 10.91× | 5,713,668 | 138 / 52 |
| vec0_knn | 100 | 123.84 | 266.75 | 0.46× | 76,298 | 123 / 44 |

**Geometric mean, 15 rows: 2.56×** (target ≤1.15×; per row ≤1.5×).

### App level

| Row | OneLite.Data ms | Microsoft.Data.Sqlite ms | Ratio | OneLite B/op | Peak MB (OneLite / MDS) |
|---|---:|---:|---:|---:|---:|
| insert_tx_10k | 125.98 | 15.20 | 8.29× | 21,077 | 109 / 53 |
| insert_tx_100k | 577.35 | 143.53 | 4.02× | 21,633 | 122 / 96 |
| insert_autocommit | 3204.84 | 3796.81 | 0.84× | 18,891 | 67 / 41 |
| wal_commit_normal | 72.97 | 28.21 | 2.59× | 22,544 | 80 / 40 |
| wal_commit_full | 1834.69 | 6337.00 | 0.29× | 22,664 | 86 / 43 |
| prepare_cost | 188.20 | 22.25 | 8.46× | 27,166 | 107 / 50 |
| open_time (report only) | 1280.51 | 38.76 | 33.04× | 56,627 | 123 / 90 |
| lookup_rowid | 1574.73 | 575.42 | 2.74× | 15,288 | 128 / 96 |
| lookup_index | 1887.94 | 470.06 | 4.02× | 18,648 | 128 / 96 |
| scan_filter | 20.59 | 6.49 | 3.17× | 0.2 | 117 / 87 |
| range_scan | 116.64 | 31.03 | 3.76× | 20,496 | 119 / 90 |
| join | 23.73 | 68.64 | 0.35× | 0.4 | 122 / 90 |
| group_by | 126.39 | 22.55 | 5.60× | 122 | 125 / 91 |
| order_by_sort | 131.06 | 50.72 | 2.58× | 377 | 124 / 92 |
| fts5_top10 | 482.27 | 42.95 | 11.23× | 5,730,055 | 143 / 61 |
| vec0_knn | 151.72 | 261.34 | 0.58× | 92,080 | 129 / 60 |

**Geometric mean, 15 rows: 2.47×** (target ≤1.0×; per row ≤1.25×).

### Cold start (report only)

| | OneLite | Reference |
|---|---:|---:|
| JIT, `Statement.Step` against native | 129 ms | 29 ms |
| NativeAOT, OneLite against native via P/Invoke | 20 ms | 8 ms |
| JIT, `.Data` against `Microsoft.Data.Sqlite` | 126 ms | 47 ms |

## arm64: Apple M2, 8 cores, macOS 27.0.1

.NET 10.0.11, SQLite 3.53.4, `Microsoft.Data.Sqlite` 10.0.14. Load average (1 min): group A 2.05 before, 2.05 highest, 1.97 after; group B 2.04 before, 2.21 highest, 2.16 after (see the note above).

### Engine level

| Row | Ops | OneLite ms | Native ms | Ratio | OneLite B/op | Peak MB (OneLite / native) |
|---|---:|---:|---:|---:|---:|---:|
| insert_tx_10k | 10,000 | 73.00 | 4.69 | 15.56× | 435 | 71 / 53 |
| insert_tx_100k | 100,000 | 269.38 | 52.49 | 5.13× | 441 | 84 / 61 |
| insert_autocommit | 200 | 43.87 | 40.28 | 1.09× | 1,913 | 61 / 48 |
| wal_commit_normal | 1,000 | 35.58 | 7.99 | 4.45× | 2,311 | 64 / 49 |
| wal_commit_full | 1,000 | 64.63 | 34.46 | 1.88× | 2,431 | 69 / 49 |
| prepare_cost | 3,000 | 106.77 | 6.09 | 17.54× | 23,316 | 68 / 49 |
| open_time (report only) | 200 | 69.21 | 56.35 | 1.23× | 55,574 | 86 / 60 |
| lookup_rowid | 200,000 | 503.89 | 290.81 | 1.73× | 600 | 86 / 60 |
| lookup_index | 200,000 | 688.78 | 229.75 | 3.00× | 600 | 85 / 63 |
| scan_filter | 100,000 | 24.52 | 4.07 | 6.02× | 0.2 | 81 / 60 |
| range_scan | 2,000 | 84.81 | 13.06 | 6.50× | 610 | 82 / 63 |
| join | 100,000 | 27.32 | 56.12 | 0.49× | 0.4 | 84 / 63 |
| group_by | 100,000 | 125.69 | 24.38 | 5.16× | 122 | 88 / 63 |
| order_by_sort | 100,000 | 117.84 | 38.59 | 3.05× | 273 | 104 / 63 |
| fts5_top10 | 300 | 578.49 | 38.44 | 15.05× | 5,713,686 | 103 / 63 |
| vec0_knn | 100 | 124.89 | 158.27 | 0.79× | 76,298 | 90 / 60 |

**Geometric mean, 15 rows: 3.62×.**

### App level

| Row | OneLite.Data ms | Microsoft.Data.Sqlite ms | Ratio | OneLite B/op | Peak MB (OneLite / MDS) |
|---|---:|---:|---:|---:|---:|
| insert_tx_10k | 89.84 | 16.91 | 5.31× | 20,988 | 80 / 63 |
| insert_tx_100k | 507.16 | 87.14 | 5.82× | 21,633 | 89 / 66 |
| insert_autocommit | 47.98 | 40.70 | 1.18× | 19,267 | 68 / 54 |
| wal_commit_normal | 50.89 | 9.45 | 5.38× | 22,544 | 74 / 54 |
| wal_commit_full | 80.14 | 36.56 | 2.19× | 22,664 | 74 / 54 |
| prepare_cost | 154.79 | 18.30 | 8.46× | 27,167 | 73 / 61 |
| open_time (report only) | 64.90 | 53.36 | 1.22× | 57,782 | 87 / 66 |
| lookup_rowid | 1170.59 | 357.05 | 3.28× | 15,289 | 90 / 67 |
| lookup_index | 1439.63 | 285.67 | 5.04× | 18,649 | 91 / 69 |
| scan_filter | 26.08 | 6.06 | 4.31× | 0.2 | 88 / 65 |
| range_scan | 114.91 | 29.50 | 3.90× | 20,497 | 90 / 68 |
| join | 26.50 | 56.17 | 0.47× | 0.4 | 91 / 69 |
| group_by | 125.48 | 24.42 | 5.14× | 122 | 93 / 69 |
| order_by_sort | 142.38 | 62.87 | 2.26× | 377 | 107 / 70 |
| fts5_top10 | 591.38 | 39.30 | 15.05× | 5,730,067 | 115 / 70 |
| vec0_knn | 247.15 | 159.84 | 1.55× | 92,084 | 94 / 67 |

**Geometric mean, 15 rows: 3.47×.**

### Cold start (report only)

| | OneLite | Reference |
|---|---:|---:|
| JIT, `Statement.Step` against native | 109 ms | 41 ms |
| NativeAOT, OneLite against native via P/Invoke | 24 ms | 16 ms |
| JIT, `.Data` against `Microsoft.Data.Sqlite` | 116 ms | 55 ms |

## The five worst engine rows, with the suspected cause

No fixes have been made; each is a candidate for its own tuning unit. Ratios are x64 / arm64.

1. **`prepare_cost` (19.5× / 17.5×)**: parse, plan and code generation allocate about 23 KB per statement; there is no statement or plan cache, and the three statements include a join.
2. **`insert_tx_10k` (18.7× / 15.6×)**: the row is short (about 80 ms) so JIT and tier-0 code dominate; the same insert path is 3.9× / 5.1× at 100,000 rows, where the remaining gap is per-row allocation (435 B/op) and the record build.
3. **`fts5_top10` (10.9× / 15.1×)**: 5.7 MB allocated per query, which points at the FTS5 segment and doclist readers copying into fresh buffers instead of reading spans in place.
4. **`group_by` (5.7× / 5.2×)**: the aggregation and the grouping sorter or hash path (122 B per row), not the scan.
5. **`range_scan` (4.5× / 6.5×)**: each execution re-seeks and allocates about 600 B, and the per-row index-to-table lookup is dearer than the native cursor's; the unindexed `scan_filter` (4.4× / 6.0×) points at the dispatch loop's per-row cost rather than I/O.

Also visible: `open_time` is 36× on Linux and about 1.2× on macOS, so a Linux-only open or close cost (locks, `-shm`, recovery on open or the closing checkpoint) is suspected.

## Where OneLite is faster

`join` (0.34× / 0.49×; the plan differs and the checksums agree), `vec0_knn` (0.46× / 0.79× at engine level, from the SIMD kernels), and on x64 `wal_commit_full` and `insert_autocommit` (see the `fsync` note above).
