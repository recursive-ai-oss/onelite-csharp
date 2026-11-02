# The write path

How OneLite runs transactions and `INSERT`, `UPDATE`, `DELETE`, `CREATE TABLE/INDEX` and `DROP TABLE/INDEX`. Everything here is written to the behaviour of the pinned `sqlite3` (see `docs/engine.md` for the read side); where the documentation was silent the behaviour was settled by running the shell.

## Opening for write

`Database.Open(path, options)` opens read-write and creates the file. `DatabaseOptions` carries what is fixed when a file is created (`PageSize`, `Encoding`, `AutoVacuum`, `IncrementalVacuum`; ignored for an existing file) and the per-connection settings `JournalMode`, `Synchronous`, `ForeignKeys`, `BusyTimeoutMs`, `WalAutoCheckpoint`. `Database.OpenReadOnly` is unchanged.

Pragmas that run: `journal_mode`, `synchronous`, `foreign_keys`, `busy_timeout`, `wal_autocheckpoint`, `wal_checkpoint`, `user_version` (read and set: the signed 32-bit application counter at header offset 60, fileformat2 §1.3.11; setting it is a write), and the read-only `page_size`, `auto_vacuum`, `encoding` (`page_size` may be set while the file is still empty). Others fail with `unsupported: PRAGMA name`.

## Transactions (lang_transaction, lang_savepoint)

* A statement outside a transaction is its own transaction ("autocommit"): the pager's write transaction begins at the statement's `Transaction` opcode and commits when the statement finishes.
* `BEGIN [DEFERRED]` only marks the connection; the read transaction (SHARED lock or WAL snapshot) starts at the first statement and is kept until `COMMIT`/`ROLLBACK`. `BEGIN IMMEDIATE` also takes the write lock at once (RESERVED, or the WAL write lock). `BEGIN EXCLUSIVE` does the same and, in rollback-journal mode, takes the EXCLUSIVE lock too, so other connections cannot even read until it ends; in WAL mode it is `IMMEDIATE` (lang_transaction). A test runs both against a live `sqlite3` reader. `BEGIN CONCURRENT` is refused.
* `SAVEPOINT` outside a transaction starts one; releasing the outermost savepoint of such a transaction commits it. A savepoint declared before the first write is opened in the pager when the write transaction begins.
* Inside a transaction every writing statement is a savepoint of its own (the statement journal). A constraint failure with ABORT undoes just the statement; FAIL keeps what the statement already did; ROLLBACK, and any failure in autocommit mode, undoes the whole transaction (lang_conflict). `changes()` and `total_changes()` are updated when the statement ends.
* `COMMIT` that fails with `SQLITE_BUSY` leaves the transaction open.

## Statements

Each is compiled into a program with three parts: the rows to write (`VALUES`, a nested `SELECT`, or a scan that first collects the keys of the rows to change, so a statement never reads rows it has already written), a per-row subroutine, and the end of the statement (`AUTOINCREMENT` counter, `RETURNING` rows).

Per row, in the order lang_createtable documents:

1. values for unnamed columns (`DEFAULT` or NULL); the rowid (explicit, or one more than the largest, or for `AUTOINCREMENT` one more than the larger of that and `sqlite_sequence`; a table holding the largest rowid probes random values);
2. column affinity on store (datatype3 §3), `STRICT` type checks ("cannot store TEXT value in INT column t.a", extended code 3091), generated columns;
3. `NOT NULL` (REPLACE substitutes the column's default), then `CHECK`;
4. uniqueness: the rowid or WITHOUT ROWID primary key first, then `UNIQUE` indexes **newest first** (this decides the message when a row breaks several; found by experiment);
5. conflicts by the action in force: the statement's `OR` clause, else the constraint's `ON CONFLICT`, else ABORT. REPLACE deletes the conflicting rows (not counted by `changes()`); IGNORE skips the row; an upsert clause whose target matches the constraint takes over (`DO NOTHING`, or `DO UPDATE` with `excluded.`).
6. the table row, then each index entry.

`UPDATE` changes only the indexes whose columns an assignment touches; a row whose rowid changes is deleted and inserted. WITHOUT ROWID rows are always deleted and inserted. `RETURNING` rows are collected while the statement runs and delivered after its last change. Index entries end in the rowid, or for a WITHOUT ROWID table in the primary-key columns the index does not hold (fileformat2 §1.6); descending columns, collations, partial (`WHERE`) and expression indexes are maintained.

Constraint errors carry the reference's text and extended code (`UNIQUE constraint failed: t.a, t.b` 2067, `PRIMARY KEY` 1555, `NOT NULL constraint failed: t.c` 1299, `CHECK constraint failed: name-or-expression` 275, `datatype mismatch` 20).

`CREATE TABLE/INDEX` write the `sqlite_schema` row (the text from the name on, as the reference stores it), create the trees (one for each `UNIQUE`/`PRIMARY KEY` constraint's `sqlite_autoindex_T_N`, and `sqlite_sequence` with the first `AUTOINCREMENT` table), backfill a new index, and raise the schema cookie so prepared statements are compiled again. `DROP` deletes the schema rows and frees the trees (in an auto-vacuum database the root that moves into a freed slot gets its schema row rewritten).

## Foreign keys

With `PRAGMA foreign_keys` on (off by default, as in the reference) the write statements enforce foreign keys (foreignkeys): immediate and deferred (`DEFERRABLE INITIALLY DEFERRED`) constraints, `ON DELETE` / `ON UPDATE` `CASCADE`, `SET NULL`, `SET DEFAULT`, `RESTRICT`, `NO ACTION`, composite keys, parents that are `WITHOUT ROWID` or the table itself, and the "foreign key mismatch" and "no such table" errors at prepare. Violations are counted, not raised at once: one counter for immediate constraints, checked when the statement ends, and one for deferred constraints, checked by `COMMIT` (a failing `COMMIT` or outermost `RELEASE` leaves the transaction open). The deferred counter is saved with each savepoint and restored by `ROLLBACK TO`. Each row change moves the counter by the change it makes to the number of child rows with no parent: a child row appearing or going away counts itself, a parent key arriving adopts the orphans that name it, a parent key leaving orphans its children and then runs the action. The lookups are `SELECT count(*)` statements and the actions `DELETE`/`UPDATE` statements on the other table, compiled from SQL text the first time they run, as nested programs of the statement; the planner picks the index. Differences from the reference: a child column of a different affinity than the parent key is compared with its own affinity, not the parent's; the lookups cost a small program start for every row of a table that takes part in a foreign key; `DROP TABLE` does not do the implicit delete the reference does with foreign keys on; `PRAGMA foreign_key_check` and `foreign_key_list` are not run.

`DROP TABLE` of a table other tables refer to first runs an implicit `DELETE FROM` it, so `ON DELETE` actions run: an immediate violation fails the statement and leaves the table, a deferred one fails at `COMMIT`. A child's value takes the parent column's affinity before it is compared with the parent key. `PRAGMA foreign_key_list(table)` (ids count down from the last constraint declared, `match` is `NONE`) and `PRAGMA foreign_key_check[(table)]` (`table|rowid|parent|fkid`, rowid NULL for `WITHOUT ROWID`; works with enforcement off; a key that resolves to no unique parent columns is an error when prepared) are supported. Known difference: with no table named, `foreign_key_check` visits the tables in creation order, where the reference's order follows its schema hash and cannot be reproduced.

`WITH` may precede INSERT, UPDATE and DELETE; their SELECT, WHERE and SET expressions take whatever the SELECT compiler takes (joins, subqueries, compounds, CTEs). The target table is one FROM item of the statement's own scope (`Compiler.Target.cs`).

## Not run yet (fail with `unsupported: ...`)

Triggers (a write to a table that has one is refused rather than run without it), views, temporary tables, `CREATE TABLE ... AS SELECT`, `UPDATE ... FROM`, `ALTER TABLE`, `VACUUM`, `ATTACH`.

## Suspend points

The write opcodes follow the step contract: a B-tree call that answers pending is repeated with the same arguments; an opcode that needs several calls keeps its progress in one step counter that is cleared when it ends; counters and `last_insert_rowid` change only after the last possible suspension. Pending states: `Insert.*`/`Delete.*` (seek, reserve, overflow), `NewRowid` (seek to the last row), `IdxProbe` (seek, entry read), `SeqLoad/SeqStore`, `CreateBtree`, `Destroy` (tree erase, schema row fix-up), `SchemaDelete`, `BumpCookie`, `SubNext` (nested select), `TxControl` (commit, rollback, release, rollback to), and the statement-end commit/rollback in `FinishWrite`. A cancelled statement ends like any failed one: ABORT semantics undo the statement, or the transaction in autocommit.

## Checks

`tests/OneLite.Tests/Engine/EngineWriteTests` runs scripts on OneLite and on the shell and compares printed rows, errors, `.dump` and `integrity_check` over page sizes 512–65536, UTF-8/16, auto-vacuum and WAL; it also asserts that rows that need no new page allocate nothing. `EngineDurabilityTests` cuts the durability workload (as SQL, with a rolled-back savepoint and a half-failed statement) after every storage call under several loss policies on the fault VFS. `tools/OneLite.Durability engine-writer` (campaign: `tools/scripts/engine_kill_campaign.sh`) kills a SQL writer with SIGKILL; `engine-coexist` runs OneLite and `sqlite3` writers and readers on one file at once; `tools/scripts/engine_modify_interop.py` modifies files the reference wrote; `OneLite.SqlLogicTest --engine onelite-rw` and `OneLite.Differential --setup onelite` run setup through OneLite.
