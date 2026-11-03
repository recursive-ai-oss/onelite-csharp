# The engine

The engine layer: `Database`, `Connection`, `Statement`, the sync and async drivers, and the SQL surface (queries, writes, DDL, transactions).
This page began with the first, read-only slice; the sections below describe the engine as it now is, family by family. Results, result codes and message text
follow the pinned SQLite release (`datatype3`, `lang_select`, `lang_expr`, `lang_corefunc`, `rescode`); plans and bytecode are
OneLite's own.

## API (`RecursiveAI.OneLite`)

```csharp
using var db = Database.OpenReadOnly("app.db");          // checks the file exists; no lock is taken yet
using Connection conn = db.Connect();                    // owns a storage engine: pager, B-tree page source, WAL and wal-index
using Statement st = conn.Prepare("SELECT id, name FROM people WHERE age > ?1 ORDER BY name");
st.Bind(1, 30L);
while (st.Step())                                        // sync driver; `await st.StepAsync(ct)` is the async driver
{
    long id = st.GetInt64(0);
    ReadOnlySpan<byte> name = st.GetTextUtf8(1);         // a view; valid until the next Step/Reset
}
```

- `DatabaseOptions`: `Vfs`, `Busy` (delegate: attempt number to retry yes/no), `CacheSize` (pages, negative = KiB, as
  `PRAGMA cache_size`), `YieldEvery` (testing: see below).
- `Statement`: `ColumnCount`, `ColumnName(i)`, `ColumnType(i)`, `IsNull`, `GetInt64`, `GetDouble`, `GetTextUtf8`, `GetBlob`,
  `GetString`, `Bind(i, long | double | string | ReadOnlySpan<byte>)`, `BindNull`, `ClearBindings`, `ParameterCount`,
  `ParameterName`, `ParameterIndex(":name")`, `Reset`, `Tail` (SQL after the first statement).
- Errors are `OneLiteException` with `Code` (primary), `ExtendedCode`, SQLite's message text, and `Detail` (page and cause for
  corruption). Anything outside the subset fails at `Prepare` with code 1 and `unsupported: <construct>`.
- Blobs and text come back as UTF-8 spans over the register or the page; no copy is made unless the database is UTF-16
  (text is transcoded once per column read) or the value is computed.

## The step contract as built

`Statement.Advance()` runs until a row, the end, an I/O request or an error: `StepStatus.Row | Done | Pending | Error`. `Step()`
is the sync driver: it loops `Advance`, satisfying each `Pending` with `Pager.ExecuteSync()`.

- A cache miss records its position in the cursor or pager and returns `Pending` **without consuming the instruction**; the next
  `Advance` re-runs the same instruction and finds the page present. Every cursor instruction (`Rewind`, `SeekRowid`,
  `SeekGE/GT`, `Next`, `IdxGE/GT`, `Column` through overflow chains, the schema reload) is written this way; a handler keeps its
  progress in the VM cursor (`Step` field) and B-tree cursor state, never in locals.
- `PendingIo` exposes the pager's single I/O slot; `Complete(in IoCompletion)` and `Abandon()` are for callers that drive the
  slot themselves. There is one outstanding request per connection.
- Nothing observable happens before an instruction's last possible suspension: output registers and `ResultRow` are produced
  only by instructions that cannot suspend.
- `DatabaseOptions.YieldEvery = N` forces a `Pending` on every Nth page access. The test suite runs the same queries at
  N = 1, 2, 3 and 7 and demands identical rows.
- Allocation: a cached `Step()` loop (full scan, rowid lookup, index range, with column readers) allocates 0 bytes
  (`GC.GetAllocatedBytesForCurrentThread`, tested in Release and Checked). Beginning and ending a read transaction takes file locks
  in the pager and is not part of that claim.
- A stale schema cookie, or a changed page size or encoding, makes the statement re-prepare itself (resumably) and restart.
- `Connection.PrepareAsync` is the async twin of `Prepare`: it loads the schema through the same resumable steps while awaiting the VFS (`Prepare` loads it synchronously, which a backend with only
  asynchronous calls cannot serve from a cold cache). `Statement.ResetAsync`/`DisposeAsync`, `Connection.DisposeAsync` and `Pager.DisposeAsync` end a read transaction and release the lock by awaiting
  the backend. Operations that run to completion inside one instruction (integrity check, `VACUUM`, a journal-mode change, `wal_checkpoint`, `dbstat`) need a backend that can answer a synchronous call;
  on one that cannot they fail at once with SQLITE_MISUSE and "backend requires the async driver" (docs/vfs.md).

## Pipeline

SQL text → `SqlParser` (the shared parser) → `Compiler` (name resolution against the catalog built from `sqlite_schema`; affinity
and collation rules of datatype3; an access plan; code generation, with a dry run first so errors surface in the order the
reference reports them) → `VmProgram` (an `Instr[]` of opcodes with P1..P5/P4) → `Machine.Run`.

Plans: full scan; rowid equality or range (`INTEGER PRIMARY KEY` or `rowid`); index equality or range when an index covers the
leading WHERE term (also the primary key of `WITHOUT ROWID` tables). The whole WHERE clause is always re-evaluated on each
candidate row, so a plan can only change speed, never the answer. `ORDER BY` uses a rowid-ordered plan when it can, else the
sorter. `NOT INDEXED` and `INDEXED BY` are honoured (a full scan through the named index reads in its order). Secondary indexes of `WITHOUT ROWID`
tables serve range and equality lookups and covering scans too (each entry ends with the primary key columns it lacks, which locate the row). Not used for lookups: partial and
expression indexes, automatic indexes, `ANALYZE` statistics. Among indexes that restrict a lookup equally, one that holds every column the
statement reads wins (the narrower first), otherwise the one created last.

**Row order of a full scan.** Without `ORDER BY` the order of rows is not defined, but the reference has a fixed habit that
shows when a statement fails part-way (the rows before the error), has `LIMIT`, or reads a bare column in an aggregate query: when
every column the statement reads is held by one index, it scans that index instead of the table. OneLite follows the habit as
observed against the pinned shell: among the indexes that cover the statement (the rowid is always available, and a `WITHOUT
ROWID` entry also holds its primary key; indexes behind PRIMARY KEY and UNIQUE constraints count; descending and expression indexes
count, partial ones do not), it takes the one with the smallest total estimated column width, the one created last on a tie, and
only if that is no wider than the table's own width (strictly narrower for a rowid table, whose entries also carry the rowid). A
column's width is `N/4+1` for a declared length `(N)`, 3 for TEXT/CHAR/CLOB/BLOB types and 1 for anything else (an `INTEGER PRIMARY
KEY` column is not part of a rowid table's row; an expression counts 1). Columns read in `FILTER`, aggregate `ORDER BY`, `GROUP
BY` and `HAVING` count like any other. `ANALYZE` statistics are not consulted.

**Index-only reads.** When a statement reads one rowid table, its plan uses an index (a lookup, a range, an `IN` list or the covering scan
above), the index has no `WHERE`, and every column the statement reads is a plain key column of that index or the rowid, the loop reads
the index entry and never opens the table. The plan, the rows visited and their order are those of the same statement without this
shortcut, and the whole `WHERE` clause is still evaluated on each entry. The columns are found by walking the statement's own text
(result columns, `WHERE`, `GROUP BY`, `HAVING`, `ORDER BY`, `LIMIT`); anything the walk does not understand (a subquery, a generated
column, a window function) keeps the table read, and if code generation still asks for a column the index lacks, the statement is
compiled again without the shortcut. A `WITHOUT ROWID` table is not affected.

**Statements prepared again.** Preparing the same text again is the usual case (a command per query, a statement per call), so when a
statement is finalized its program, its machine and its cursors stay with the connection, and the next `Prepare` of the same text on that
connection takes them instead of parsing and planning again. They belong to one statement at a time, so two live statements of one text
never share them. They are used again only while everything compilation read is unchanged: the connection still holds the same schema
object (every schema change, local or from another connection, builds a new one) at the same schema cookie, with the same
`foreign_keys` setting and text encoding; otherwise the text is compiled again. As with a statement kept open, a schema change made by
another connection is noticed when the statement runs, which re-prepares it. A statement that touches a virtual table, or that is
compiled to count rows for a plan, is never kept; nor are DDL, `PRAGMA` and transaction control. At most 32 texts are kept per
connection. The bound-parameter values never carry over.

**`ORDER BY` through an index.** When the sort terms are the leading columns of an index (plain columns, the index's collations,
default `NULLS` placement, then optionally the rowid), all in the index's direction or all opposite, the index is walked forwards or
backwards (`Last`/`Prev`) and no sort runs: rows reach the caller as they are found, so a runtime error part-way shows the rows
before it, and rows equal in every key come out in index order. Of several such indexes, the one matching most terms wins, then the
narrowest, then the one created last. When only a prefix of the terms is served (or a term is a constant) the index still fixes the scan order but the
sorter runs for the rest; the reference then sorts in groups and releases a group when the leading key changes, which OneLite does
not (see Known differences).

## Supported subset

`SELECT` in full, window functions included: one table or many, subqueries, CTEs, compound selects, `DISTINCT`, grouping and
aggregates, `ORDER BY`/`LIMIT`, and `SELECT <exprs>` with no FROM (which never touches the file).

- Columns: `*`, `t.*`, expressions, `AS` aliases; result column names follow the reference (a bare column, in parentheses or
  not, is named by the column; `rowid` is named by its alias column if there is one).
- Expressions: literals (including hex and `1e3` forms), `?`, `?NNN`, `:name`, `@name`, `$name`; unary `- + ~ NOT`;
  `+ - * / % || & | << >>`; comparisons with datatype3 affinity and collation (`BINARY`, `NOCASE`, `RTRIM`, declared on columns
  or given with `COLLATE`); `AND OR NOT`; `IS [NOT]`, `IS [NOT] NULL`, `ISNULL`, `NOTNULL`; `[NOT] IN (list)` (items carry no
  affinity of their own); `[NOT] BETWEEN`; `[NOT] LIKE ... ESCAPE`, `GLOB`; `CASE`; `CAST`; row values (`(a, b) = (1, 2)`,
  ordering forms, `IN`).
- Functions, as in lang_corefunc, lang_mathfunc and lang_datefunc: `abs changes char coalesce concat concat_ws format glob hex if
  iif ifnull instr last_insert_rowid length like likelihood likely lower ltrim max min nullif octet_length printf quote random
  randomblob replace round rtrim sign sqlite_compileoption_get/used sqlite_source_id sqlite_version substr substring
  total_changes trim typeof unhex unicode unlikely upper zeroblob unistr unistr_quote`, the math functions (`acos asin atan atan2
  ceil ceiling cos cosh degrees exp floor ln log log10 log2 mod pi pow power radians sin sinh sqrt tan tanh trunc` and the
  inverse hyperbolics), `date time datetime julianday unixepoch strftime timediff` with their modifiers (`start of`, `weekday`,
  `unixepoch`, `julianday`, `auto`, `subsec`, `floor`, `ceiling`, `+N units`, `±YYYY-MM-DD`, `±HH:MM[:SS]`) and
  `CURRENT_DATE/TIME/TIMESTAMP`. `changes()`, `total_changes()` and `last_insert_rowid()` are 0 until the engine can write.
  Function names and modifier names are case-insensitive; a date with a day past the end of its month carries into the next
  month when printed (`date('2024-02-30')` is `2024-03-01`).
- `ORDER BY` columns, aliases, positions and expressions, `ASC/DESC`, `NULLS FIRST/LAST`; `LIMIT`/`OFFSET` expressions.
- Text encodings UTF-8, UTF-16le and UTF-16be; any page size; auto-vacuum files (read-only).

### Joins

Inner, `LEFT`, `RIGHT`, `FULL`, `CROSS`, `NATURAL`, comma joins, `ON` and `USING`. Inner joins are ordered greedily (an item a
term can look up through the rowid or an index, given the items already placed, goes next); a join with an outer item keeps the
written order. Inner loops use the rowid or an index when a term allows. The key of an index lookup gets the affinity the
comparison would apply to it (datatype3), so a column compared with an untyped column or a literal seeks as the scan would compare.
`RIGHT`/`FULL` joins run the left-deep loops, remember which rows of the right item matched, and then emit the unmatched ones
with the earlier items NULL; the `WHERE` clause applies to the joined rows after that, so a term on an early item is computed
when that item's row is reached and tested at the end (it can't drop a row before the right item has recorded its matches).
Terms are tested in order of cost: terms that read columns before terms that run a subquery. A subquery term runs at the first
loop at which every column it names from the enclosing query is bound.

### Subqueries and CTEs

In `FROM` (always materialized into an ephemeral table; no flattening), scalar, `IN (SELECT ...)` including row values,
`EXISTS`, correlated or not. A subquery that names nothing of an enclosing query is built once per statement run (a `Once` flag
and, for `IN`, an ordered set); a correlated one is rerun per outer row. `WITH` and `WITH RECURSIVE`: a CTE may read a later CTE
of the same `WITH` clause; a cycle is `circular reference: name`, a repeated name `duplicate WITH table name: name`.
Recursive CTEs stream through a queue (rows already used are dropped), with `UNION` (de-duplicating) or `UNION ALL`, and `LIMIT`/`OFFSET`
in the recursive select. `MATERIALIZED`/`NOT MATERIALIZED` are accepted and ignored.

### Compound selects, `DISTINCT`, grouping

`UNION`, `UNION ALL`, `INTERSECT`, `EXCEPT` fold left to right through an ephemeral table; `ORDER BY`/`LIMIT` apply to the whole
compound. A column's comparison collation in a compound is the leftmost explicit one among the selects so far. Of equal rows
(`1` and `1.0`) the one a `UNION` keeps is the later operand's; rows that arrived through `UNION ALL` keep the earlier one. Within
one side the first of equal rows is kept, in the order the reference reads that side: it orders each side of `UNION`, `INTERSECT` and
`EXCEPT` by all its columns, so a side whose columns lead an index is read through that index, backwards for a descending one. When
several sides are wrong the error reported is the last one's.
`VALUES` lists and `DISTINCT` use the same ordered sets.

`GROUP BY`/`HAVING` with `count sum total avg min max group_concat string_agg`, each with `DISTINCT`, `FILTER (WHERE ...)` and an
aggregate `ORDER BY`. `sum`, `total` and `avg` use compensated (Kahan-Babuska-Neumaier) summation like the reference. Bare
columns in an aggregate query come from the first row of the group, or, with `min`/`max`, from the row that last set the *last* `min`/`max` of the
query (every row counts while that aggregate has seen only NULLs); a result expression that is a `GROUP BY` expression shows the group's first row
even then (when it appears once). Integers are summed exactly until a real or an overflow turns up; the exact sum then enters the compensated sum as a
multiple of 16384 plus the remainder, and integers beyond 2^52 that follow enter it the same way; a NaN sum is NULL. A
`GROUP BY` the access path already delivers in order (an index or the rowid) streams without the sorter.

### Window functions

`OVER (PARTITION BY ... ORDER BY ... frame)`, named windows and chaining (`OVER (w ORDER BY ...)`), `ROWS`/`RANGE`/`GROUPS` frames with
`EXCLUDE`, `FILTER`, the built-ins (`row_number rank dense_rank percent_rank cume_dist ntile lag lead first_value last_value nth_value`)
and every aggregate as a window function (`sum`, `avg`, `total` and the counts slide with an inverse step; the others re-add the frame).

- **Stages.** A core with window calls runs in stages. Calls written with the same partition, order and frame are one stage, standing where
  the first of them was written; the stages run last-written first, so the rows leave in the order the first-written stage sorts them.
  Each stage sorts its input with the sorter (`SorterInsert`/`SorterSort`) and feeds `WinPush`/`WinOut`; a stage without keys does not sort.
  Observed: `row_number`, `lag`, `lead`, `ntile`, `rank`, `dense_rank` and `percent_rank` ignore the frame they were written with and
  share a stage with any call whose frame is the one they use internally (`ROWS UNBOUNDED PRECEDING..CURRENT ROW` for `row_number` and
  `lag`, `ROWS ..UNBOUNDED FOLLOWING` for `lead`, the default `RANGE` frame for `rank` and `dense_rank`, `ROWS CURRENT ROW..UNBOUNDED
  FOLLOWING` for `ntile`, `GROUPS CURRENT ROW..UNBOUNDED FOLLOWING` for `percent_rank`); `cume_dist`'s is not known, so it merges with nothing.
  The order matters because ties come out in the order the previous stage left them.
- **Index order.** When the stage that runs first has `PARTITION BY`/`ORDER BY` terms that are the leading columns of an index (same
  collations), or just the rowid, the table is read through that index (backwards when that is what the order asks for) and the stage
  does not sort, as the reference does; ties are in index order (rowid descending on a backwards scan). A `WHERE` that picks a rowid range
  keeps the index scan for the window. The index is the one with the fewest columns, the last created on a tie.
- **Errors found while parsing** (a frame whose start is after its end, `DISTINCT` or `ORDER BY` inside a window call) come before
  any table is looked up, frame first within one call. Frame offsets that are columns, functions or subqueries evaluate to NULL and are
  refused when the frame is used. `nth_value`'s second argument is checked when a row of the frame survives `EXCLUDE`.
- **Offsets.** `lag`/`lead` take the row at position -/+ offset, worked out in floating point when the offset is not a whole number and
  used only when that comes out whole (`1e-300` is the current row, `0.5` none); `lag` with a negative offset below -1 finds nothing
  (the default). A RANGE frame that starts `PRECEDING` or ends `FOLLOWING` always contains the current row's peers, whatever rounding
  does to the bound.
- **Memory and spill.** Bounded frames stream: only the rows a frame can reach are kept. A frame that needs the whole partition (`UNBOUNDED
  FOLLOWING`, `ntile`, `percent_rank`, `cume_dist`, `last_value` without a bounded end) holds the partition in memory while it fits the sort
  memory limit (`Connection.SortMemoryLimit`, about the page cache size, 256 KiB at least). Past the limit, and when the connection has a
  temporary-file VFS, the partition's rows move into blocks of 128 rows. A block is written to a temporary file (never synced, deleted on
  close) only when it has to leave memory, least recently used first, and is read back when a row in it is asked for. The peer groups
  are found from the blocks (the first and last group of each block stay in the block table), so no per-row or per-group structure
  grows with the partition. Every file access is a request on the connection's one I/O slot (states `SortSpillOpen`, `SortSpillRun`,
  `SortMergeRead`), so it runs on the sync and the async driver, over async-only backends, and with forced yields. A row of a block that is
  on disk interrupts the evaluation of the output row (nothing was published; the aggregate bounds move row by row, so it resumes where it
  was); the block is read and the row evaluated again. `WinPush` and `WinOut` return pending without consuming their input.
  An encrypted database never spills (its `SortVfs` is null): the partition stays in memory. Limits: the memory limit is soft. A frame that
  is scanned again from its start for each output row (`min`, `max`, `group_concat`, `nth_value`, `first_value`/`last_value` under an
  `EXCLUDE`, or any frame with `EXCLUDE`) needs the whole frame in memory, and the number of resident blocks grows until the scan fits.
  `sum`, `avg`, `total`, `count`, `row_number`, `rank`, `dense_rank`, `percent_rank`, `cume_dist`, `ntile`, `lag`, `lead` and sliding
  `ROWS`/`GROUPS` frames stream. Measured (`querydiff winmem ROWS DIR [cacheKiB]`): one partition of 10,000,000 rows, `sum(v)` to
  `UNBOUNDED FOLLOWING`, `ntile(4)` and `cume_dist()`, cache 2000 KiB: every row checked, 18 MiB peak managed memory (272 MiB before the
  spill existed); `CacheSize = -8` with `YieldEvery` 1/2/3/7 on both drivers is in `WindowTests`.
- **group_concat in a frame.** It slides like `sum`. Observed: a value that leaves the frame takes off the front of the text as many
  characters as it and the separator that came after it (the next value's) are long, whatever the text holds; when the text is empty the
  next value starts it without a separator; the result is NULL only when no value is left in the frame. In a RANGE frame that may not hold
  the current row (it starts `FOLLOWING` or ends `PRECEDING`) rows enter before others leave.
- **Explicit `EXCLUDE NO OTHERS`** is a window of its own: a call written with it does not share a stage with the same frame written without.
  `TRUE` and `FALSE` are 1 and 0 as frame, `lag`, `lead` and `ntile` offsets; string offsets and constant expressions work as in the reference.
- **min/max ties.** Of several equal values (`100` and `100.0`, or `'a'` and `'A'` under `NOCASE`) in a frame that is built again for each
  row, `min`/`max` return the last; a frame that starts at the partition start, or has an `EXCLUDE` (a spelled-out `EXCLUDE NO OTHERS` too), returns the first. Observed.
- Cached scans of a stage that reads in index (or rowid) order allocate nothing per row; a stage that sorts allocates the sorter's record per
  row (tests: `WindowTests`).

### The sorter

`ORDER BY` without a usable index, `GROUP BY`, `DISTINCT`-by-sort, compound results and `ORDER BY` inside aggregates use
`RecordSorter` (`Engine/Vm/Sorter.cs`). Rows are kept in memory up to a budget (the connection's cache size, at least 256 KiB);
then the batch is sorted and written to a temporary file as a run, and the runs are merged with a heap when the sort is read.
Equal keys keep insertion order across runs. Every temporary-file access (open, write of a run, read during the merge) is an
I/O request on the connection's one slot (`ResumeState.SortSpillOpen/SortSpillRun/SortMergeRead`), returned as `Pending` with the
progress in named fields, so a sort runs on the sync driver, under `Advance`/`Complete`, and on an async-only backend. A
temporary file that can't be opened or written is not an error: the sort goes on in memory. The merge is single-pass (one reader
per run, 16 KiB each), so a very large sort holds one buffer per run. `querydiff sortmem ROWS DIR` sorts generated rows and reports the peak.
Measured (x64, generated rows with a non-indexed text key): 1M rows, 2000 KiB cache: sampled managed peak 2.0 MiB,
working set 107 MiB, 2.2 s; 10M rows: 7.6 MiB, 113 MiB, 21.8 s. At 64000 KiB: 1M rows 32.5 MiB, 10M rows 63.5 MiB.

**Not bounded:** the sets behind `DISTINCT`, `UNION`/`INTERSECT`/`EXCEPT` and `IN (SELECT ...)`, ephemeral tables holding materialized
subqueries and CTEs, and the recursive-CTE queue live in memory.

Rejected at prepare with `unsupported: ...`: parenthesized
FROM clauses (nested joins), `ORDER BY` and `VALUES` in a recursive CTE, `COLLATE` on an `ORDER BY` column position, the `->`/`->>`
operators, virtual tables, functions not listed above, `REGEXP`/`MATCH`.

## Opening and locking

`Database.OpenReadOnly` is the equivalent of `SQLITE_OPEN_READONLY`; a connection reads every database the reference leaves
behind, and answers the way the reference does. It takes a shared lock for the life of a read transaction (the first `Step` to
`Reset`/completion) and never writes the database file, a journal or the log. A file that is not a database is `SQLITE_NOTADB`
("file is not a database"); structural damage found by the B-tree is `SQLITE_CORRUPT` with the page and cause in `Detail`.
`Database.Open` and `OpenReadOnly` treat `:memory:`, `file::memory:`, `file:name?mode=memory` and the empty name as in-memory (inmemorydb): nothing is
created on disk, and each `Connect()` gets a private database of its own, except with `cache=shared` in a `file:` URI, where the connections of that
`Database` share one. An empty name is a private temporary database, which this build also keeps in memory.

A `Connection` owns one `StorageEngine` (the pager, the B-tree page source and context, and the WAL), so the write path added next
begins its transactions on the same object.

**Rollback-mode files.** A hot journal (lockingv3 section 4: a journal exists, no process holds RESERVED, the header is valid and
non-empty) cannot be rolled back by a read-only connection: the first read fails with `SQLITE_READONLY_ROLLBACK`
("attempt to write a readonly database"), as the shell does; nothing is changed. A journal that is not hot is ignored.

**WAL-mode files** (wal, walformat). A read-only connection takes part in the wal-index like any reader (read marks, the recovery
and checkpoint locks), so a live writer's checkpoints never overwrite frames it still needs. Settled against the pinned shell
(`tools/scripts/readonly_wal_probe.sh`, which also runs the same states through OneLite):

| State of the files | Result (shell and OneLite alike) |
|---|---|
| `-wal` and a valid `-shm` | rows; nothing changes |
| `-wal`, no `-shm`, directory writable | rows; an `-shm` of 32768 bytes is created and filled by recovery |
| `-shm` but no `-wal` | the database file alone (a 0-byte `-wal` is created) |
| neither, directory writable | the database file alone; empty `-wal` and `-shm` created |
| `-wal`, no `-shm`, directory not writable | `SQLITE_CANTOPEN` ("unable to open database file") |
| neither file, directory not writable | `SQLITE_READONLY` (extended `SQLITE_READONLY_DIRECTORY`, "attempt to write a readonly database") |
| database, `-wal` and `-shm` all read-only, directory read-only | rows; no file changes |
| `-shm` empty or zeroed (recovery needed), nothing writable | rows |

When the `-shm` file exists but cannot be opened for writing, the connection reads the log with a private, in-memory index
rebuilt from it (walformat section 3), which gives the same rows without taking part in the locking. It never checkpoints, never
deletes the log or the `-shm` on close and never restarts the log. A backend with no shared memory (memory, browser) uses the
private index too.

**What the crash tests check.** `querydiff crash` kills `sqlite3` writers (rollback journals in delete, truncate and persist
modes; WAL with checkpoints of every mode) at random moments and reads each image with OneLite and with the shell, as left and, in
WAL mode, also with the `-shm` removed; rows, order and error text must be identical, OneLite must not change the database,
journal or log, and `sqlite3` must afterwards find the copy OneLite read intact (`integrity_check` ok, the same `.dump` as an
untouched copy). `querydiff live` runs a `sqlite3` writer (10,000 commits, checkpoints of every mode in WAL) beside OneLite
readers; each read must show one committed state, and the states must never go backwards.

## Known differences from the reference

- **Real to text.** Where the 15-digit form does not round-trip the reference prints 17 digits by its own conversion, which
  could not be pinned from the documents. OneLite uses an empirical rule fitted to the pinned shell (`Values.RealDigits`): the
  15-digit form is used when it round-trips and either has at most 13 significant digits or its error is below half a unit of the
  14th/15th digit; otherwise the 17-digit rounding of the exact value, except that it rounds up already when the discarded part
  is at least 0.495 of the last digit's unit, or at least 0.45 when neighbouring doubles are 2.2 or more of those units apart
  (fitted on 250,000 doubles; the cut lies between 2.168 and 2.2204 in the data). Measured on random doubles (`querydiff fp`):
  about 0.08% of values still differ in the last digit, almost all at decimal exponents beyond +-100 and a few at the 15-digit
  choice; the numeric value is never affected. With the `!` flag OneLite drops trailing zeros (keeping one digit after the point) as the reference does;
  where more than 16 significant digits are asked for, the reference prints a shorter, rounded run (18 or 19 digits, a count that
  follows neither a fixed width nor the manual's 26) and OneLite prints the exact expansion. Class `printf-alternate-form-2`.
- **Text to REAL, 20 or more significant digits.** Converting such text (`CAST('10000000000000000001024' AS REAL)`, or a number built by
  concatenation) can differ from the reference in the last bits of the result: OneLite rounds the decimal exactly, the reference
  accumulates the digits in a way the documents do not give. Class `text-to-real-last-digit`.
- **UTF-16 databases, a stored blob that spilled to overflow pages and contains NUL bytes, cast to text.** The reference leaves
  the bytes undecoded, so the text is read as UTF-8 up to the first NUL (length 1 for little-endian data, 0 for big-endian);
  OneLite decodes in the database encoding like every other blob. Expressions that rebuild the blob first (`bl || ''`) agree.
  Probes: bad only when the value spilled (page 65536 never spills at these sizes); `cast(bl||'' as text)` correct; blobs built
  in an expression are correct. Not reproduced because the row's origin would have to travel with every value.
- A UTF-16 database read as a blob and used as text or a number is reinterpreted in the database encoding (datatype3).
- **Runtime error order.** `AND`/`OR` evaluate both operands in a value context (an error in the right one is raised even when
  the left decides the result) and short-circuit in a condition (`WHERE`, `CASE WHEN`, `iif`), as the reference does; a literal
  that decides the result (`0 AND x`, `x OR 1`) removes the other side. What can still differ is which `WHERE` term the reference
  planner runs first and which covering index it scans (see Plans); the differential tool reports those as runtime-error-order cases.
- **Which unknown column is named.** When a statement uses two or more columns that do not exist, the reference names one of them by
  an order of its own, which OneLite reproduces (`Compiler.NameOrder.cs`; checked against the shell with several thousand random
  expressions over missing columns): operands are walked left to right and the walk stops at the first missing column; a function
  call, `LIKE`/`GLOB`/`MATCH` (a call of `like(pattern, text)`, pattern first) and `ISNULL`/`NOTNULL` resolve their arguments in a walk
  of their own, so a later sibling that is also missing replaces the name; once a name is set, any operator, literal or constant ends
  the walk, while a column or a function call is still resolved; `IS`/`IS NOT` with a plain column on the right resolve that column
  first. `LIMIT`/`OFFSET` names are resolved before every other clause. What is left is a missing column inside a scalar subquery that
  sits in a function argument beside another missing column (`abs((SELECT a)) + b`): the reference reports `b`, OneLite `a`. The
  differential tool reports that as `name-resolution-error-order` (same code and text, another column).
- **Rows returned before a failing row.** When a statement fails part-way, the rows the caller has already received depend on the
  reference's evaluation order, which the docs leave open: it walks an index that matches `ORDER BY` instead of sorting, streams the
  legs of a `UNION`, and reads a covering partial index where OneLite scans the table. Both sides raise the same error; only the
  number of rows before it can differ. Single-column rowid and `WITHOUT ROWID` key orders are scanned without a sorter; the general
  index-ordered scan belongs to the planner. Class `order-by-index-scan-before-error`.
- **Views and FROM subqueries are materialized, not merged.** The reference merges a simple one into the outer query. OneLite keeps
  the ephemeral table but chooses the body's covering index from the columns the outer query names (a lone view or subquery in
  `FROM`, a body of one plain SELECT), which reproduces the row order in the common cases. Joins, bodies with `ORDER BY`, `LIMIT`,
  `DISTINCT`, aggregates or compounds, and subqueries that repeat a column name can still differ in unordered output, in the bare
  columns of an aggregate query and in the order of a floating-point `sum`.
- **Range constraints on a descending index.** The reference answers `WHERE c <= x` from an index on `c DESC` in index order (largest
  first); OneLite seeks only ascending indexes and scans the table, so an unordered result, or the bare columns of an aggregate
  query, can come from other rows.
- **A comparison's numeric conversion and `min`/`max`.** In `(SELECT max(b.x) FROM t b WHERE b.x = a.y)`, with `b.x` compared to a
  value of numeric affinity, the representation of the value the reference returns can differ from OneLite's (`'00'` against `'0'`,
  `'+7'` against `'7'`): the reference's comparison converts the loaded column in place in ways the docs do not describe.
- **Window sums that cancel.** A sliding `sum`/`total`/`avg` over a frame that adds and removes values of very different size (an
  integer beyond 2^53 beside small reals) keeps rounding residue in the reference that OneLite's compensated sum does not (4.35 against
  4.349999999998545), and an integer sum whose frame once held a blob comes back as an integer in the reference in a RANGE frame and
  as a real in a ROWS frame. Class `window-sum-cancellation`; repros in `testdata/differential/documented-diff-window-sum-cancellation*.sql`.
- **Bare group value of equal values.** Beside `GROUP BY`, a bare column of a group that holds values that compare equal but differ (`'a'`
  and `'A'` under `NOCASE`, `-9223372036854775808` and the real `-9.223372036854775808e18`) is the first row the reference scans, which can
  be through a covering index; the case above. Class `group-by-bare-value`; repros `documented-diff-group-by-bare-value-*.sql`.
- **Comparison affinity applied to a column in place.** In a query such as `SELECT (SELECT max(b.c) FROM t AS b WHERE b.c = a.i)`
  where `b.c` has no (or TEXT) affinity and the other operand a numeric one, the reference converts the column's register in place, so
  `max`/`min` over that same column then compare `'00'` or `'1e1'` as the numbers they read as and keep the first of the values that
  compare equal (observed with the pinned shell: `+b.c = a.i`, an `OR`, or a literal operand switch it off; the select list itself and
  `ORDER BY b.c` still see the stored value, while `ORDER BY 1` over that column sorts by the converted one). OneLite compares the stored values,
  so it can return another of the rows that match. The docs (datatype3) describe the comparison, not this side effect. Class
  `comparison-affinity-in-place`; repros `diff-comparison-affinity-in-place-*.sql`. Why it is not matched: the effect needs a value
  that is a text and also carries a numeric reading (what `'+7'` becomes after the comparison), which OneLite's register values do
  not have, and the reference applies it only in some plans: it vanishes when the comparison is rewritten through a constant
  (`b.c = a.i AND a.i = 7`), when the tables are joined in the other order, or when the other operand has no affinity, so emulating it
  means a second value representation and a model of the plan shape, not a local rule.
- **Duplicates kept by UNION, EXCEPT and DISTINCT among equal values**, and bare columns beside an aggregate read through an index
  OneLite does not use (a partial index implied by the WHERE clause, an `OR` of rowid terms, a view flattened into its table): the first
  of the values that compare equal (`1` and `1.0`, `'a'` and `'A'` under NOCASE), or the first row read, differs. Class `planner-index-choice`;
  repros `diff-planner-index-choice-*.sql`. Descending indexes do serve equality and range lookups, read in their own order.
- **Blobs read as text in a UTF-16 database** where the bytes are not well-formed UTF-16 (an odd length, a lone surrogate): the reference
  decodes them its own way, and results of `instr`, `concat` and ordering over such values differ. Class `utf16-blob-text`.
- **Index chosen for a covering scan.** `SELECT id FROM t` where several indexes could answer it: the reference scans one of them, and OneLite
  another (or the table), so a window with no total order, or a bare column, sees rows in another order. Class `planner-index-choice`
  (queries with `OVER` on a table with a secondary index or UNIQUE column, same rows back); repro `documented-diff-planner-index-choice.sql`;
  left to the planner.
- **NUL in text and huge sums.** The shell prints text up to a NUL where OneLite returns it whole (class `nul-text-output`). A sliding
  `sum`/`total`/`avg` over a table holding values near 1e15 or larger (1e300) differs from the reference by rounding residue of any size
  (class `window-sum-cancellation`, applied when the setup holds such a value); repros `documented-diff-window-sum-cancellation-*.sql`.
- **Window frame spelled `EXCLUDE NO OTHERS`.** The reference builds that frame again for every row (no inverse step), so a `sum` that once
  held a blob is an integer again, a `group_concat` starts fresh, and `min`/`max` keep the first of equal values. OneLite does the same.
- **Other known classes the differential tool names** (each has a repro `documented-diff-<class>-*.sql`):
  `sum-int-real-residue` (a plain `sum`/`total`/`avg` adding an integer beyond 2^53 to reals: the reference's rounding residue, within 1e-3);
  `like-invalid-utf8` (`LIKE` with a blob operand or pattern that is not valid UTF-8: the reference matches byte-wise up to a NUL);
  `window-range-int64-edge` (a `RANGE` offset frame over integers and reals at +-2^63, equal as numbers: which rows are peers of the bound);
  `window-stage-tie-order` (three or more window stages with an explicit `NULLS FIRST/LAST`: the order of rows that tie in a stage's sort,
  so `lag`/`lead`/`first_value` over ties can differ).
  A negated real literal is a constant (`-0.0` is negative zero) while `-column` is `0 - column`. Text that is a number followed by NUL bytes
  is numeric in a comparison, and `ltrim`/`rtrim`/`trim` end their character set at a NUL, as the reference does.
- **Errors in a window query.** The reference evaluates window results lazily: a row an outer query filters out never raises its overflow, and
  rows reach the caller before a later partition fails; OneLite finishes the subquery first (classes `order-by-index-scan-before-error`,
  `runtime-error-order`; repros `documented-diff-window-rows-before-error.sql`, `documented-diff-window-runtime-error-order.sql`).
- **Errors in a grouped or sorted query.** The reference evaluates the arguments of aggregates and the terms of `ORDER BY` in its
  sorting pass, before any group is finished, so a row whose argument fails raises the error before the first group is returned (a
  `GROUP BY` query); OneLite returns the groups that finished earlier and then raises the same error. With a runtime error in a result
  column and another in an `ORDER BY` term, which of the two is raised can also differ (classes `order-by-index-scan-before-error`,
  `runtime-error-order`). The statement fails with the same code and text either way.
- **Window input order on `WITHOUT ROWID` tables.** Which index the reference reads a `WITHOUT ROWID` table through for a window's
  order is the planner's choice; OneLite sorts (the differential generator skips these tables for windows).
- Not generated or not matched: `random()`, `randomblob()`, `now`/`localtime`/`utc` results, `load_extension`, `ANALYZE`-driven
  index choice.
- **Error order.** When a statement has several errors the reference reports one of them by its own order of work; OneLite follows
  it where observed: after an error in a CTE's body that is found once its FROM items have resolved (an unknown column, a `USING`
  ambiguity; not a missing table, and not an error inside a FROM subquery of the body), a later FROM item that names a CTE is reported as
  `no such table`; `*`
  expansion with two FROM items of one alias and a shared column names `main.alias.column` (`*.alias.column` for subqueries).
  A statement that reads the same alias on both sides of a `USING` join with a column the join merges is rejected (`x.id`)
  where the reference accepts it.
  Not reproduced: a `USING` ambiguity in the outer FROM together with a scalar/`IN` subquery in the same select that declares its
  own `WITH` (the reference then reports `no such table: <cte>` for the subquery; OneLite reports the ambiguity). It needs two errors in one statement.
- **`x IN (SELECT col FROM tbl)` with a descending index on `col`.** The reference answers `0` (and `NOT IN` answers `1`) when the column
  holds a NULL and `x` is not found, because it looks for the NULL at the start of the index, where a descending index does not keep it.
  OneLite answers NULL, as the rule of `IN` says. Seen only with the bare `SELECT col FROM tbl` form (or an `ORDER BY` on that column).
- **A converted column leaking into a later read.** `SELECT (SELECT max(b.c1) FROM t1 b WHERE b.c1 = a.id)` with `b.c1` of no declared type holding
  `7` and `'+7'`: the reference reads the column once and the numeric conversion done for the comparison also shows in `max(b.c1)`
  (`7`); OneLite returns the stored `'+7'`.
- **Bare columns when the scan order differs.** In an aggregate query without `min()`/`max()` the bare columns come from the first row
  the reference scans, which can be through a covering or partial index (also a descending one); OneLite scans the table (see Plans). The same
  applies to `ORDER BY` on an indexed column: the reference reads the index in order, so rows before a mid-scan error differ.
- **VACUUM page count.** VACUUM reads every B-tree in key order and writes it again with full pages, so tables and indexes come out the
  size the reference gives. Across page sizes 512 to 65536, three encodings and all three auto-vacuum modes, the page count equals the
  reference's in 90 of 108 probe databases and is one or two pages smaller in the rest (large databases with many overflow chains at
  512- and 1024-byte pages): the reference redistributes cells between the last pages of an index while it builds it, which OneLite does not
  copy. Contents, `integrity_check` and `.dump` are identical.
- **ALTER TABLE and unsupported checks.** RENAME and DROP COLUMN re-check every view and every non-INSTEAD OF trigger and fail with
  `error in view v: ...` / `error in trigger tr: ...` for one that is already broken. An INSTEAD OF trigger that stops resolving after a rename
  (`NEW.a` of a view column that was renamed) is not caught.
- Subquery/expression nesting limits (`SQLITE_MAX_EXPR_DEPTH`) are those of the parser.

## Rowids under `BEGIN CONCURRENT`

Rowids chosen by a `BEGIN CONCURRENT` transaction are not in commit order (see `docs/concurrency.md`): a poller that asks `WHERE id > :last` can skip a row that commits later with a smaller id. Plain `BEGIN` allocates as SQLite does.

## Known differences with several connections on one file

Several connections to one file (one `Database`, several, or other processes) are checked step by step against the native library with the schedule differential (`ScheduleDifferentialTests`: named schedules, plus random ones over DELETE, TRUNCATE, PERSIST, WAL and MEMORY). Lock failures are `SQLITE_BUSY` with the reference's text, and a refused statement leaves the transaction as the reference does. What still differs, each outside the default schedules:

Behaviour that matches (named schedules in the test): a write statement refused with `SQLITE_BUSY` counts as running until it is reset (a checkpoint, a journal-mode change into or out of WAL, `COMMIT` and `SAVEPOINT` see it) and stepping it again runs it again; a `ROLLBACK` that undid writes lets pending read queries continue; a connection keeps the schema its own DDL produced and needs no read lock for the next statement; a no-op `CREATE ... IF NOT EXISTS` compiled against a stale schema takes the locks the reference takes; `journal_mode=off` is accepted as in the library; journal-mode switches among the rollback modes, `wal_checkpoint` outside WAL mode and settings-only pragmas take no file lock where the reference takes none.

What still differs (each is outside the default random schedules; the named schedule or the environment switch reproduces it):

- **Writing through a second name of the same file** (`ATTACH` of the main file, then `UPDATE alias.t`): the reference begins a transaction only on the databases a statement uses, OneLite always begins one on main as well, so the write fails or blocks differently when another connection holds a lock. Run `WritesThroughAnAttachOfTheSameFile` with `ONELITE_SCHEDULE_ATTACH` set.
- **Leaving WAL while another connection is attached to the idle WAL file**: OneLite answers `SQLITE_BUSY`, the reference changes the mode. `ATTACH` followed by a prepare while another connection holds EXCLUSIVE can differ in which statement is refused.
- **`journal_mode=off`** under random schedules: the reference sometimes reports "query aborted" at a different step. "cannot change out of wal mode" is reported for a merely prepared statement where the reference allows it.
- **Locks taken by a few pragmas**: `page_size=N` on a non-empty file, `PRAGMA index_list`/`foreign_key_check` of an unknown table, `data_version` take the lock the reference does not, or the reverse. `wal_checkpoint(TRUNCATE)` while a writer is active reports partial counts (`1 5 0` where the reference reports `1 5 5`).
- **Writes made while a statement refused with `SQLITE_BUSY` is still active.** The reference does not commit an autocommit statement while another statement of the connection is active, and a refused statement stays active until it is reset. So a write run in that time (`UPDATE` or `INSERT` on the same connection, after the other connection has committed) reports `done`, is visible to its own connection, keeps the write lock, is invisible to every other connection (which then gets `SQLITE_BUSY`, or `SQLITE_BUSY_SNAPSHOT` in WAL mode), and is rolled back when the refused statement is reset or finalized. OneLite commits such a write at once. Repro (rollback modes; the other connection is `c0`): `c1: prepare s1 = UPDATE t SET x = x + 1 WHERE x = 5`; `c0: BEGIN IMMEDIATE`; `c1: step s1` (BUSY); `c0: COMMIT`; `c1: UPDATE t SET x = x + 1 WHERE x = 3`; `c0: SELECT x FROM t` still shows 3 on the reference. Schedule seeds (`ONELITE_SCHEDULE_AUDIT=1`, about 0.3% of random schedules): 21291, 40595, 8133, 51196, 52268. What OneLite does copy: the read lock (and in WAL mode the snapshot) stays open while the refused statement is active, and the statement counts for `COMMIT`, `RELEASE`, `SAVEPOINT`, checkpoints and journal-mode changes.
- **A pending join `SELECT` after an `UPDATE` or `DELETE` of its inner table on the same connection.** Rows the `SELECT` has not reached yet show the change, and a row deleted under the cursor reads as no row (NULLs), as in the reference. The one difference is a join the reference answers through an automatic index (an equality join on an unindexed inner table): it copies the inner table once, so it keeps showing the old rows, where OneLite scans the table and shows the change.

## Statistics and incremental vacuum

`ANALYZE`, `PRAGMA optimize` and `PRAGMA incremental_vacuum` leave the same logical file as the pinned reference (checked against it: `sqlite_stat1` rows, `page_count`, `freelist_count`, `.dump`, `integrity_check`, both directions).

- **`ANALYZE`** writes `sqlite_stat1` only (the reference build has no `sqlite_stat4`). `ANALYZE <index>` makes only that index's row; `ANALYZE main` analyses the whole schema. Rows reach `sqlite_stat1` in another order than the reference's (it follows its internal table hash), so a `.dump` differs in the order of those `INSERT` lines only. Analysing an attached or temp schema is not supported.
- **`PRAGMA analysis_limit`** is stored and read back, but `ANALYZE` always counts every index entry. The reference stops early on large indexes and extrapolates; its numbers are estimates that cannot be reproduced exactly, ours are exact. Below the limit the two agree.
- **`PRAGMA optimize[(MASK)]`** follows the documented rule: with bit 0x02 (default mask 0xfffe) it analyses an ordinary table that is not named `sqlite_*` when an index of it has no `sqlite_stat1` row (observed: and the table has rows), or when it is a candidate by use (the connection planned a `WHERE` on a column one of its indexes holds) or by bit 0x10000 and its row count is about ten times the analysed one, more or less. A table with no index qualifies by size only, measured against about a million rows when it has no statistics. The tenfold boundary is approximate (the reference rounds through a logarithmic estimate: it lists a table somewhere between 9.2 and 10.8 times its analysed size). Bit 0x01 returns one `ANALYZE "main"."table"` row per table instead of running it, in reverse creation order (the reference's order is its table hash's). The temporary analysis limit of bit 0x10 is not applied, so a large table is analysed exactly. A run that needs no analysis takes no write lock; one that does takes it when it starts to analyse. Only the main schema is optimised.
- **`PRAGMA auto_vacuum`** is fixed once the database file is initialised, as in SQLite: switching a new file to WAL (which `Database.OpenAsync` does at open) initialises it, so a later `PRAGMA auto_vacuum = incremental` silently has no effect (it reads back 0). Run `VACUUM` after setting it to apply it (it works in WAL mode), or set it before the switch to WAL (observed with the pinned shell).
- **`PRAGMA incremental_vacuum[(N)]`** in an `auto_vacuum=INCREMENTAL` database frees up to N pages (all of them when N is missing, below 1 or larger than the freelist) by moving pages from the end of the file into free pages nearer the start (pointer map updated) and cutting the file; the free pages that stay are chained into a new freelist. It returns one row with no columns per page freed, as the reference does, but does all the work on the first step (the reference frees one page per step, so a statement stepped once and then reset frees one page there and all of them here). In other auto-vacuum modes it does nothing; it always begins a write transaction first, so a read-only connection or `query_only` gets `attempt to write a readonly database`. Which pages end up free is OneLite's own choice (the lowest free pages are filled first), so the bytes of the file can differ from the reference's while the counts and content match. It is one write transaction through the pager, so a crash or rollback leaves the old or the new file.

## JSON and JSONB

The code is in `Engine/Vm/Json/`; the behaviour is that of the reference's json1 documentation, settled where the documents are silent by
running the pinned shell.

- **Scalar functions:** `json json_array json_array_length json_error_position json_extract json_insert json_object json_patch
  json_pretty json_quote json_remove json_replace json_set json_type json_valid json_array_insert` and the `jsonb_` forms of those that
  return JSON (`jsonb jsonb_array jsonb_extract jsonb_insert jsonb_object jsonb_patch jsonb_remove jsonb_replace jsonb_set
  jsonb_array_insert`), and the operators `->` and `->>` (compiled to function calls). `json_valid(x, flags)` takes flags 1-15, including
  the strict-blob check (8); a NULL flags argument is an error as in the reference.
- **Input:** JSON text with the JSON5 extensions the reference accepts (comments, unquoted and single-quoted keys and strings,
  trailing commas, hexadecimal integers, `Infinity`/`NaN`, leading `+`, leading and trailing decimal points, line continuations in
  strings), UTF-8 or UTF-16 text, and JSONB blobs. A blob that is valid JSONB is taken as JSONB even when it also reads as text.
  Nesting is limited to 1000 levels; deeper input is `malformed JSON`, and `json_pretty` reports `JSON nested too deep`.
- **JSONB:** the byte layout is the documented one, and the bytes are identical to the reference's. Where the format leaves a choice
  (header size, element padding on overwrite, the text-class of a rebuilt string, whether a number keeps its JSON5 form) the writer
  reproduces what the pinned shell writes; the 100,000-document comparison (`--families jsonb` in `tools/OneLite.Differential`) checks
  `hex(jsonb(x))`, `json(jsonb(x))`, a second encoding of the text and `json_valid(jsonb(x), 8)` for generated documents.
- **Subtypes:** the JSON subtype is carried by a value internally (`Mem.Subtype`, cleared by every setter, kept by copies): the
  result of a JSON function is JSON when handed to another JSON function and text otherwise, so `json_array(json('[1]'))` nests
  and `json_array('[1]')` quotes. `->` returns JSON text, `->>` an SQL value.
- **Edits:** `json_set/insert/replace/remove/array_insert/patch` build the result from the document's JSONB, copying what is not touched.
  Path errors are reported lazily as the reference does (`.` reads the key before the type check, `[` checks the type first;
  messages double single quotes). The editing functions remember the text of their last four results of 100 bytes or more, so a
  following function given the same text sees its JSONB and JSON5 form, which affects `json_valid` and the written bytes the same
  way it does in the reference.
- **Numbers:** integers beyond 64 bits are real values in SQL results and keep their digits in JSON text; a hexadecimal integer wider
  than 64 bits is `malformed JSON` once it becomes an SQL value; reals print as the engine prints reals.
- **Table-valued functions:** `json_each`, `json_tree`, `jsonb_each`, `jsonb_tree` and `generate_series` work as FROM items (also unqualified, with
  `main.`, aliased, in joins, subqueries, CTEs and as the right side of a `LEFT JOIN`). The call's arguments feed the hidden columns in order
  (`json`, `root`; `start`, `stop`, `step`); a hidden column fixed by a `col = expr` term of the WHERE or ON clause is taken as an argument too,
  and that term is then not tested against the column again. `*` leaves the hidden columns out. `rowid` is the 0-based row number for the JSON
  functions and the value for `generate_series`. In `jsonb_each`/`jsonb_tree` a container's `value` is JSONB. A function whose arguments name
  an earlier item is placed after it; otherwise it goes inside the other items, and `CROSS JOIN` keeps the written order. Not covered: a
  function on the right side of a RIGHT or FULL join.
- **Aggregates:** `json_group_array`, `json_group_object` and the `jsonb_` forms. A NULL label skips the row, non-text labels become text, `DISTINCT`
  and `ORDER BY` inside the call work, and a JSON-typed argument stays JSON. As window functions they wait for window-function support.
- **Reference quirk, not copied:** for a document nested hundreds of levels deep, the shell's `json_tree` returns an empty `root` on a few rows (which rows
  depends on the other columns selected); OneLite returns the path, and the differential generator leaves `root` out for such documents.
- **Known gap:** the JSON subtype is lost when a value passes through a materialized subquery (`SELECT json_array(v) FROM (SELECT value AS v
  FROM json_each(...))`); the shell keeps it.
- **Checking:** `tools/OneLite.Differential --families json` generates documents (including JSON5, deep nesting, big numbers and
  invalid text), valid and bad paths, and every function above, and compares results, errors and error text with the shell;
  `testdata/sqllogictest/json/` holds 9,278 records written from the shell's output by `tools/scripts/json_slt_gen.py` and run by
  `tools/OneLite.SqlLogicTest`; `tests/OneLite.Tests/Engine/JsonAllocationTests.cs` checks that cached `json_extract`, `->`, `->>`,
  `json_type`, `json_array_length` and `json_valid` over a stored column allocate nothing per row.

## Checking

`tools/OneLite.QueryDiff` runs queries on the engine and on the pinned `sqlite3` shell (quote mode, byte-exact) and compares
column names, every value's type and bytes, row order where `ORDER BY` fixes it (multisets otherwise), and error code and
text. `fuzz --rel` generates joins, subqueries, CTEs, compound selects and grouping (`RelGen`) over databases built by
`tools/scripts/relational_corpus.sh` and the real-world corpus; a query that runs for 15 s is named on stderr while it runs.
text. `crash` and `live` are described above; `ONELITE_QD_YIELD=N` forces a suspend point at every N-th page access and
`ONELITE_QD_ASYNC=1` steps with `StepAsync`, for any command. `ONELITE_QD_ASYNC=cold` (the differential tool) runs the engine over a backend with only asynchronous calls
that suspends on every call and refuses every synchronous one (`ColdAsyncVfs` over the local files), with a six-page cache, a forced suspend point every third page access (`ONELITE_QD_YIELD` overrides),
`PrepareAsync`/`StepAsync`/`DisposeAsync`, and, with `--setup onelite`, the setup statements run the same way (writes included). A statement that needs a synchronous call (see docs/vfs.md, "What works on which
backend") is rerun on the sync driver and counted (`syncFallbacks` in the summary). `scan` compares `SELECT *` over every table of a set of databases; `fuzz --seed S --count N` runs a seeded generator over
the subset (including hostile forms: blobs in arithmetic, huge integers, `COLLATE` everywhere, constant `ORDER BY` terms).
Known harness limits: the shell prints a text value only up to its first NUL, and prints negative zero as `0.0`.
