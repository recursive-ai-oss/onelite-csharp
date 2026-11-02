# Backup, VACUUM INTO and incremental blob I/O

Normative notes for the three features. The reference behaviour is the pinned SQLite release; every rule below was checked against it by running the same script on its native library and on this engine (`tests/OneLite.Tests/Engine/BackupTests.cs`, `VacuumIntoTests.cs`, `BlobTests.cs`).

## Online backup (`Backup`)

`Backup.Create(destination, destinationName, source, sourceName)` then `Step(n)` / `StepAsync(n)` until `Done`; `Dispose` ends it (sqlite3_backup_init / step / finish).

- **Locks.** The destination's write transaction is opened by the first step and held until the last step commits or the backup is disposed. The source is read under a read transaction only for the length of one step (or the connection's own transaction, if it has one), so other connections can read and write it between steps. While the backup is unfinished its destination connection refuses statements on its main database with SQLITE_LOCKED (the reference leaves this undefined).
- **Source changes.**
  - A commit by another connection or process is noticed at the next step (the pager counts changes made by others: the file change counter in rollback modes, the wal-index snapshot in WAL) and the copy restarts at page 1; `Remaining` jumps back up.
  - A commit by the *source's own connection* is applied to the destination at once: the pages already copied are compared with the source and rewritten where they differ, and the copy carries on. No restart.
- **Commit.** Nothing of the destination changes until the final step: pages go into the destination pager's journal/WAL transaction, and the commit is the destination's ordinary commit. A crash at any point leaves the old content or the complete new content (`BackupBlobDurabilityTests`).
- **Page 1.** Copied from the source, except that the destination's schema cookie becomes its old cookie + 1 (so other connections reload their schema), and that a WAL-mode destination keeps format-version bytes 2/2. A rollback-mode destination that receives a WAL source's header becomes a WAL file, as in the reference.
- **Geometry.** Page size and reserved bytes must match, unless the destination has no pages yet and is not in WAL mode: then it takes the source's. Otherwise the step throws SQLITE_READONLY "attempt to write a readonly database" (the reference's answer for WAL destinations). **Known difference:** the reference also converts a non-empty rollback-mode destination to the source's page size; here that case throws the same SQLITE_READONLY. An empty source leaves the destination as it is.
- **Encryption.** Pages are copied decrypted and re-encrypted by the destination's pager: an encrypted source into an encrypted destination keeps the destination's key; into a plaintext destination the reserved-byte mismatch applies (SQLITE_READONLY).
- **Results.** `Step` returns `More`, `Done`, `Busy` (retry) or `Locked` (retry); any other failure throws with the reference's code and message, rolls the destination back and ends the backup. `Remaining` and `PageCount` are those of the last step (0 before the first).
- **Driver.** `Step` satisfies page I/O inline; `StepAsync` awaits the storage backend per page. Both run the same code. The step observes the cancellation token between pages only; a cancelled step keeps the pages already written.

## VACUUM INTO

`VACUUM [schema] INTO 'file'` writes the compacted image of the database to a new file and only reads the source (a read transaction; it works on a read-only connection).

- The copy keeps the page size, text encoding, auto-vacuum mode, user_version and application_id of the source; it is a rollback-journal-format file even when the source is WAL (format-version bytes 1/1), and has an empty freelist. An encrypted source gives an encrypted copy under the same key and settings.
- Errors, text as in the reference: `cannot VACUUM from within a transaction`, `non-text filename` (NULL, numbers, blobs), `output file already exists` (the target exists and is not empty; an empty file is fine), `unable to open database: NAME` (unwritable path, `file:` URI with `mode=ro`). An empty name or `:memory:` writes nothing.
- **Method.** Every table and index is read in key order and written again with full pages; sqlite_schema is written last by ordinary inserts, because its rows carry the new root pages. Pages are handed to the target's pager as soon as their tree has made them, so memory holds one open leaf per tree plus a small per-page table (about 5 bytes per page), plus the values of the row being copied. A single value (one row's overflow chain) is held whole while its row is written. Encrypted databases never touch a temporary file.
- The target is written with the journal off; if the statement fails, the half-written target is deleted. It is synced at the end according to `synchronous`.
- Page counts match the reference's own `VACUUM INTO` to within a few pages (pages are packed as full as the cells allow; the reference inserts row by row).

## Incremental blob I/O (`Blob`)

`Connection.OpenBlob(schema, table, column, rowid, writable)` (sqlite3_blob_open). Reads and writes touch only the pages that hold the requested bytes: the local part of the cell and the overflow chain pages (`BtCursor.ReadPayloadRange` / `WritePayloadRange`, with an overflow hint so sequential access does not rewalk the chain). Writes are made in place through the pager's journal or WAL like any page change.

- **Open errors** (all SQLITE_ERROR, text as in the reference): `no such table: S.T`, `no such column: "C"`, `no such rowid: N`, `cannot open value of type integer|real|null`, `cannot open view: V`, `cannot open virtual table: T`, `cannot open table without rowid: T`, `cannot open table with generated columns: T`, and for a writable handle `cannot open indexed column for writing` and `cannot open foreign key column for writing`. The rowid alias column holds NULL in the record and so fails with `type null`.
- **Range rules.** A read or write outside the value is SQLITE_ERROR "SQL logic error", checked first. Then an expired handle gives SQLITE_ABORT "query aborted". Then a write on a read-only handle gives SQLITE_READONLY. Size never changes.
- **Expiry.** The handle expires when a statement on the same connection changes or deletes its row (an UPDATE of any column of that row, a REPLACE, a DELETE, or a DELETE of the whole table). Other rows of the table can change freely. `Length` is then 0. An expired handle cannot be revived by `Reopen`. A ROLLBACK aborts every open handle with SQLITE_ABORT_ROLLBACK (516) and `Length` keeps its value. A failed `Reopen` leaves the handle expired.
- **Locks.** The handle holds a read transaction on the database until it is closed (a read statement kept at its row), and a writable one also the write transaction: other connections cannot write while it is open in rollback modes. DROP TABLE and DROP INDEX on the connection fail with SQLITE_LOCKED "database table is locked" while any handle is open; COMMIT fails with SQLITE_BUSY "cannot commit transaction - SQL statements in progress" while a writable handle is open.
- **Autocommit.** A writable handle opened in autocommit mode starts the transaction itself (BEGIN IMMEDIATE) and `Close` commits it; until then `AutoCommit` is false. **Known difference:** in the reference the connection stays in autocommit mode and the handle's own statement holds the write lock.
- **Suspend points.** Each page read of a chain walk is a suspend point (`Overflow.Walk`); a write adds `MakeWritable` of the page (`Blob.Write`). The sync methods satisfy them inline, the async twins (`ReadAsync`, `WriteAsync`, and the `Stream` returned by `AsStream`) await the pager. A walk interrupted by a pending read restarts from the hint; a repeated write of the same bytes is idempotent.
- Offsets and lengths count bytes as stored: a TEXT value in a UTF-16 database counts two bytes per code unit.
