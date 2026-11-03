# BEGIN CONCURRENT: concurrent writers on a standard WAL file

`BEGIN CONCURRENT` lets several transactions write the same database at once and conflict only when they touch the same rows. The files stay standard: a concurrent commit appends ordinary WAL frames, and a `sqlite3` process can open the file at any time and write with a plain `BEGIN`. It is opt-in and OneLite-only (`sqlite3` reports a syntax error for the statement).

## Using it

```sql
BEGIN CONCURRENT;            -- serializable
BEGIN CONCURRENT SNAPSHOT;   -- snapshot isolation
... statements ...
COMMIT;                      -- SQLITE_BUSY_SNAPSHOT (517) if it cannot be merged
```

- Enable it with `DatabaseOptions.ConcurrentWrites = true`. Without it the parser rejects the statement as SQLite does. `Database.HappyPathOptions()` turns it on.
- The database must be in WAL mode when the transaction begins; otherwise `SQLITE_ERROR`, "BEGIN CONCURRENT needs the database to be in WAL mode".
- Plain `BEGIN`, `BEGIN IMMEDIATE` and `BEGIN EXCLUSIVE` behave exactly as in SQLite and use the single WAL write lock.
- One transaction per connection. The read transaction (snapshot) starts at `BEGIN CONCURRENT`.
- The happy path's `WriteAsync`/`Write` use `BEGIN CONCURRENT` when `ConcurrentWrites` is on and the journal mode is WAL, and run the delegate again on `SQLITE_BUSY` (including `SQLITE_BUSY_SNAPSHOT`); otherwise `BEGIN IMMEDIATE`.
- `Connection.InConcurrentTransaction` tells whether one is open.

## How a commit works

While the transaction runs it takes no write lock. It reads from its WAL snapshot and keeps its changes in its page cache; nothing spills to the WAL. It also records a **trace** of what it did to each B-tree, in order: every seek (and what it landed on), every row it stepped over, every row payload it read, every insert and delete with a digest of the row it replaced, savepoint boundaries, and AUTOINCREMENT high-water marks.

`COMMIT` then:

1. takes the WAL write lock (waiting up to the busy timeout);
2. if nobody has committed since the snapshot, appends its frames as an ordinary WAL commit;
3. otherwise discards its cached pages, moves to the latest snapshot while holding the lock, and **replays the trace** against it. Each recorded read must find what it found before, and each write must find the row state it was based on; unique and primary-key checks, foreign-key lookups, trigger bodies and index maintenance are part of the replayed statements, so they are re-checked against the new data. Replay produces the pages for the commit;
4. appends the frames, syncs per `synchronous`, and publishes the new WAL header. That publication is the commit point. The lock is released.

If a replayed read or write finds something different, nothing is written, the lock is released at once and `COMMIT` fails with `SQLITE_BUSY_SNAPSHOT`. The transaction then can only be rolled back (the connection reports an error for anything else until `ROLLBACK`). Two transactions that change different rows of the same page both commit; two that change the same row, or where one read a row the other changed, do not.

## Isolation

- **Serializable (default).** Every read in the trace is re-checked at commit, including range scans and absent-row lookups, so a phantom insert into a scanned range is a conflict. The history checker finds no G0, G1, G-single or G2-item anomaly.
- **Snapshot isolation** (`BEGIN CONCURRENT SNAPSHOT`). Only what a write depended on is re-checked: the before-state of every row it changed, unique and primary-key probes, and `sqlite_schema` reads. Plain reads are not. Write skew is possible, as the definition of snapshot isolation allows; lost updates and non-repeatable reads are not. Foreign-key parent lookups count as plain reads here, so a parent deleted by a concurrent committed transaction is not noticed under snapshot isolation (use serializable when that matters).

## Rowids and AUTOINCREMENT

A concurrent transaction chooses a new rowid itself, from blocks of 1 to 64 values that start above the largest rowid it can see and above what other connections in the process have already handed out. The chosen rowid is recorded in the trace and replay never changes it, so `last_insert_rowid()`, `RETURNING` and foreign-key children created in the same transaction stay valid. Allocation is not a read. If the rowid was taken by a commit in the meantime, that is an ordinary insert conflict (`SQLITE_BUSY_SNAPSHOT`); the retry skips ahead by a random distance that grows with consecutive collisions. Writers in other processes and a `sqlite3` writer are covered by this validation, not by the hint.

- **Rowids are not in commit order under `BEGIN CONCURRENT`.** A poller that remembers the largest id it saw and asks `WHERE id > :last` can skip a row that commits later with a smaller id. Poll with a commit-ordered column or sequence of your own instead. Plain `BEGIN` allocates exactly as SQLite does (largest + 1).
- **AUTOINCREMENT** never reuses an id. The `sqlite_sequence` update is a "max" merge, and replay requires the inserted id to be greater than the current sequence value. A transaction whose id was overtaken by one that committed first with a larger id therefore aborts with `SQLITE_BUSY_SNAPSHOT` and its retry takes a new id: ids are never reused, at the price of some aborts when several writers insert into the same AUTOINCREMENT table.
- Near 2^63-1 the usual random probing for an unused rowid applies, and an exhausted AUTOINCREMENT table gives `SQLITE_FULL`.

## What is not merged

These make the transaction "opaque": it still runs and can commit, but if any other commit happened since its snapshot it fails with `SQLITE_BUSY_SNAPSHOT` instead of being merged.

- schema changes (DDL), header pragmas, `VACUUM`;
- writes to a virtual table;
- a trace longer than 16 million entries.

Writing to an attached database inside the transaction is `SQLITE_ERROR`.

## Cancelling and giving up

Cancelling `COMMIT` (a cancelled token, or `Abandon` of the statement) while it waits for storage after taking the write lock **releases the lock at once and drops the transaction's cached changes**. The transaction cannot be committed afterwards: only `ROLLBACK` works, and `WriteAsync` runs the delegate again from the start. Before the lock is taken, cancelling leaves nothing behind. Commit phases that write WAL frames or sync cannot be cancelled; they run to the end.

A process killed at any point either leaves unpublished frames (the next writer overwrites them and recovery cuts them at the first frame whose salt or checksum fails) or has published the commit. Nothing half replayed is ever visible, because replay only touches private pages and unpublished WAL space.

The write lock is held while a commit replays, so its hold time grows with the size of the trace. There is no enforced limit on it yet.

## Durability and throughput

A commit has the cost of an ordinary WAL commit, and with `synchronous=FULL` the WAL write lock is held through the sync, so commits are bounded by one sync each whatever the number of writers (group commit is not implemented). Concurrent writers help when transactions spend their time in the statements rather than in the commit; very short transactions on one machine gain nothing, and replay adds a little work to every commit that follows another.

## How it is checked

- Unit tests (`ConcurrentTransactionTests`): disjoint and conflicting rows, ranges and phantoms, indexes, unique keys, foreign keys, triggers, savepoints, WITHOUT ROWID, rowids and AUTOINCREMENT, opaque cases, standard files, suspension at every Nth page request, an abandoned commit.
- A crash sweep of a stale concurrent commit on the fault-injecting VFS (`ConcurrentDurabilityTests`): every outcome is the content before or the content after, and an acknowledged commit survives.
- The history checker (`docs/history-checker.md`): `--client onelite` at serializable and `--client onelite-snapshot` at snapshot isolation, threads and processes, list-append and register models.
- `OneLite.Durability`: `conc-kill` kills several writer processes at once with SIGKILL, again and again on one file, and the pinned shell judges it (integrity, every acknowledged commit present, per-transaction invariants); `conc-coexist` runs concurrent writers beside a `sqlite3` process writing with plain `BEGIN` and compares the content with the acknowledged commits; `conc-throughput` is an indicative commits-per-second comparison against `BEGIN IMMEDIATE`.
