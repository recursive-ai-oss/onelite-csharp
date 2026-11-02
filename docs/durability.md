# Durability: rollback journal sequences

What the pager writes, in what order, and what a crash at each point leaves behind. Sources: sqlite.org `atomiccommit`, `fileformat2`, `lockingv3`, `pragma`. Everything marked *observed* was seen by running the pinned reference shell (3.53.4, Linux x64, ext4) under `strace` and by dumping the files it leaves; scripts are in `tools/pager-experiments/`.

## Journal file format (fileformat2, observed)

* Header at the start of each segment, padded to the journal sector size: magic `d9 d5 05 f9 20 a1 63 d7`, record count (4), nonce (4), initial size in pages (4), sector size (4), page size (4). *Observed:* the reference writes sector size 512 whatever the device reports (a device with powersafe overwrite is treated as 512-byte sectors), so the header is 512 bytes and the first record starts at 512. We write the same: sector size = 512 on powersafe-overwrite devices, else the device's.
* Record: page number (4), original page (page size), checksum (4): the nonce plus every 200th byte of the page counted down from `pageSize-200`. The checksum is over the page content only.
* **Segments.** When the journal is synced mid-transaction (a cache spill) and more records follow, the next header goes at the next sector boundary. **Every segment header has its own random nonce and its records are checksummed with that nonce** (observed; the documents describe one). A reused PERSIST journal may hold stale segments after the new transaction's last one, so after the zeroing sync we write a zero sector at the next segment boundary when the old journal was longer. Playback reads headers one after another until a header is invalid or the file ends. Record count `0xFFFFFFFF` (written when `synchronous=OFF`) means "to the end of the file".
* **Super-journal trailer** (multi-file commit, *observed*): after the last record, `[4 bytes: the lock-byte page number][super-journal full path][4: path length][4: sum of the path bytes][8: the magic]`. The reader finds it from the last 16 bytes. The super-journal file (`<main db path>-mjHHHHHHHH`, hex digits) holds each participating journal's full path followed by a NUL; the reference does not create one with `synchronous=OFF` or `journal_mode=MEMORY` (atomiccommit §5.2).
* A journal is hot when it exists, is not empty, no process holds RESERVED or more on the database, its first header is valid with a non-zero record count, and, if it names a super-journal, that file exists. If the named super-journal is gone, the transaction had committed: the journal is deleted without rollback.

## Which pages are journaled (observed and documented)

* Only pages that existed at the start of the transaction. Growing the file (23 → 44 pages) journaled only pages ≤ 23. The page number order is the order of first modification, each page once.
* fileformat2 §3: freelist leaf pages are not journaled. We follow it: a page taken from the free list that was a leaf when the transaction began is not journaled; a page freed *during* the transaction is journaled when freed.

## Commit sequences (observed, one-row update, 4096-byte pages)

Journal records are written before any sync. "J" is the journal, "D" the database, "dir" a directory sync. Page writes are ascending by page number with page 1 first.

| journal_mode | synchronous | sequence |
|---|---|---|
| DELETE | OFF | create J; header (record count = -1, magic set) + records; write D; unlink J. No sync at all. |
| DELETE | NORMAL | create J; header (magic 0, count 0) + records; **rewrite 12 bytes at 0 (magic, count)**; sync J; dir; write D; sync D; unlink J |
| DELETE | FULL | create J; header (magic 0) + records; sync J; dir; **rewrite 12 bytes (magic, count)**; sync J; write D; sync D; unlink J. *Ours also syncs the directory after the unlink* (the reference does it only at EXTRA): the crash model drops an unsynced unlink, so without it an acknowledged commit could come back as a hot journal and be rolled back. |
| DELETE | EXTRA | as the reference FULL, then dir after the unlink (so identical to our FULL) |
| TRUNCATE | OFF / NORMAL | as DELETE, but `truncate(J, 0)` instead of unlink; no sync after it |
| TRUNCATE | FULL / EXTRA | as FULL, then `truncate(J, 0)`, sync J |
| PERSIST | OFF | as DELETE/OFF, then zero the first 28 bytes of J |
| PERSIST | NORMAL / FULL / EXTRA | as the matching DELETE row, then zero the first 28 bytes, sync J |
| MEMORY | any | no journal file; write D; sync D unless OFF |
| OFF | any | no journal; write D; sync D unless OFF. *Observed:* the shell answers `delete` to `PRAGMA journal_mode=off` and keeps journaling, so there is no oracle sequence for OFF; the pager supports it as a library mode. |

Notes:
* "sync" is `fdatasync` on Linux for `Normal`; `SyncMode.Full` only when `fullfsync` is set.
* Directory sync after creating the journal happens once, after its first sync.
* The header's initial write has magic zero, so a crash before the rewrite leaves a journal that is not hot; page images in it are never needed because the database is not touched until after the rewrite and sync.
* Multi-file (*observed*, FULL): super-journal created, written, synced and its directory synced first; then for every database the journal gets its trailer, is synced, header rewritten, synced; then the databases are written and synced; the super-journal is deleted (**the commit point**) with a directory sync; the journals are deleted last. (The reference interleaves database *n*'s writes before database *n+1*'s journal is complete; we follow atomiccommit §5.3-5.4 and finish all journals first. Both are safe.)

## Cache spill (observed)

A transaction larger than the cache (`cache_size=10`) with another connection holding SHARED fails with `SQLITE_BUSY` ("database is locked") when the cache must be written out, because the database file may be written only under EXCLUSIVE. Without the reader it succeeds. The pager does the same: a spill that cannot get EXCLUSIVE after the busy policy gives up returns BUSY; the statement fails, the transaction stays open.

When the cache must give up a dirty page, the pager writes one whose original is already in a durable part of the journal. Only when every dirty page still has its record in the unsynced tail does it sync the journal first (atomiccommit §3.7: a page may be overwritten once *its own* record is durable). One journal sync therefore covers many spilled pages: a 240-page transaction on a 12-page cache syncs the journal 42 times, where one sync per spilled page would be about 480.

## What a crash leaves

| Crash point | State after recovery |
|---|---|
| before the journal header is rewritten and synced | old (journal not hot; database untouched) |
| after that, before the first database page write | old (journal hot and complete, playback is a no-op rewrite) |
| during database writes or sync | old (hot journal restores every page, then truncates to the initial size) |
| journal deleted/truncated/zeroed but the change not durable | old or new; both are complete images |
| after the journal removal is durable | new |

`synchronous=NORMAL` promises (pragma docs) a database that is never corrupt after power loss, but a commit may be lost; `FULL` promises no acknowledged commit is lost. `OFF` promises neither.

## Loss models the tests cover

* A power cut after every storage call (create, write, truncate, sync, directory sync, delete) of the same transaction, under: everything unsynced lost; everything kept; random survival of unsynced changes with and without reordering (4 seeds each); torn sectors (a write cut short, remainder random). After recovery the database is exactly the old or the new image, at `synchronous` FULL and NORMAL, in DELETE, TRUNCATE and PERSIST, with 1, 10, 40, 90 and 500 dirty pages (the last two spill the cache). FULL additionally never loses an acknowledged commit under the drop-everything cut.
* One sync acknowledged but not performed ("lost"), each sync of the transaction in turn. This breaks a promise the pager relies on, so only a weaker rule holds: with the drop-everything cut and no cache spill the result is still the old or the new image; with a spill, a lost journal sync followed by an honest database sync can leave a blend (the database pages are on disk, the records protecting them are not), which no software can prevent. Those are counted, not asserted.
* `journal_mode=MEMORY` and `OFF` promise nothing across a crash and are not swept.
* Real SIGKILLs of a process using the pager, then `sqlite3` recovery and `integrity_check`, and crash images recovered by `sqlite3`: see `tools/OneLite.PagerKill`.

# Durability: WAL sequences

Observed with the pinned `sqlite3` under `strace` (calls on the database, `-wal` and `-shm` only) and by dumping `-wal` and `-shm` bytes. Page size 4096 unless noted; `fdatasync` shown as sync. How the log and the wal-index are laid out: `wal.md`.

## Creating and restarting the log
- The first write to an empty or absent log writes the 32-byte log header in one `pwrite`, then syncs the log, then (once per connection, at its first log sync under FULL) syncs the directory.
- After a restart (the log was fully checkpointed and no reader needs it) the header is rewritten with new salts and synced, even under `synchronous=NORMAL`. The frames that follow overwrite the old ones from the start of the file.

## One commit
- Each frame is two `pwrite`s: the 24-byte frame header, then the page. Pages go in ascending page-number order; the last frame carries the commit mark and the database size in pages.
- FULL: one sync of the log per commit, after the last frame. NORMAL: none at commit.
- The wal-index (in `-shm`, memory only) is updated after the frames are written: copy 2 of the header first, then copy 1.
- Page 1 inside a frame keeps the change counter; "version valid for" is set equal to it and the in-header database size is the new size.

## Checkpoint
1. sync the log; 2. for each page in ascending order, the latest frame not beyond the safe frame is read and written to the database; 3. `ftruncate` the database to its size if it shrank; 4. sync the database; 5. record `nBackfill` in the wal-index.
- TRUNCATE then `ftruncate`s the log to 0 without a sync. The result row of PASSIVE blocked by a reader reports `busy = 0` with the partial counts; RESTART reports `(0, n, n)`, TRUNCATE `(0, 0, 0)`.
- Auto-checkpoint runs after the commit that brought the log to the threshold, with the same sequence.
- After a restart commit, if the log file is longer than `max(journal_size_limit, end of the frames just written)` it is truncated to that.

## Closing
The last connection to close (it can take the exclusive lock) checkpoints, then deletes `-shm`, then deletes the log.

## Switching to WAL
An ordinary rollback-journal commit of page 1 with header bytes 18 and 19 set to 2 (change counter bumped). The `-wal` and `-shm` appear at the next transaction.

## What a crash leaves
- Before the last frame of a commit is fully written: the log ends at the previous commit (frames after it have no commit mark, or fail the salt or checksum chain) and recovery cuts them.
- After the last frame but before the sync (NORMAL, or FULL before the sync returns): a process kill keeps the data (it is in the OS cache), so the commit survives; power loss may drop it, and then the log ends at an earlier commit. Never a mixture.
- A crash during a checkpoint: the database may hold some pages of newer frames, but the log still has all of them and `nBackfill` was not advanced past the sync, so the next checkpoint repeats the copy.
