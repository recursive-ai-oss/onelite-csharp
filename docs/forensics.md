# The forensic reader

Normative notes for `RecursiveAI.OneLite.Storage.Forensics`. Sources: sqlite.org `fileformat2` (§1.3 database header, §1.5 varints, §1.6 B-tree pages, freeblocks and the cell formats, §1.7 pointer maps and overflow pages, §2.1 records, rollback journal format), `walformat` (WAL header, frame header, checksum algorithm, wal-index header) and `atomiccommit`, `wal`.

The reader looks at a database file set that is damaged, in use or left behind (a copy pulled from a device, a crashed writer's files, a live database) and tells what it holds, including what has been deleted. It **never modifies anything**.

## What "never modifies" means

* The files are opened read-only through the VFS **without the database role**: no lock is requested, the `-shm` is never mapped, and no file is created, written, truncated or deleted. The `-shm` is read as ordinary bytes for advice only.
* The log is scanned and never applied; the journal is read and never played back; no checkpoint runs.
* Tests hash the database, `-wal`, `-shm` and journal and compare modification times before and after a full pass, check that no file appears, and run on files that are mode 0444 inside a mode 0555 folder. Under `strace` the pass issues no `fcntl` lock calls, no `O_CREAT`, no write-mode opens, no writes and no `ftruncate` on those files (see the method at the end).

## API

```csharp
using ForensicDatabase db = ForensicDatabase.Open(path);          // database + -wal + -shm + -journal beside it
ForensicSnapshot s = db.OpenSnapshot();                            // latest valid commit
ForensicSnapshot a = db.OpenSnapshot(SnapshotSelector.AsOf(commit.Id));
ForensicSnapshot d = db.OpenSnapshot(SnapshotSelector.DatabaseOnly);
ForensicSnapshot t = db.OpenSnapshot(SnapshotSelector.LatestWithTrailingFrames);   // flagged IncludesUncommitted
foreach (ForensicTable table in s.Tables) { var rows = s.ReadRows(table); }
SpaceReport space = s.Space();
IReadOnlyList<CarvedRecord> found = s.Carve(new CarveOptions { MinConfidence = 0.5 });
```

* `Open(path, ForensicOptions)` takes an `IVfs` (default: the file VFS), and can leave the log or journal out. A zero-length database file with a usable log is a database whose pages all live in the log.
* `db.Wal` lists every complete frame (`WalFrameInfo`: page number, commit size, salts, checksums, file offset, state, generation, whether the checksum was verified), the generations (`WalGeneration`) and the commits (`WalCommit`: id, generation, first and last frame, database size, `PrefixLost`). `db.Shm` and `db.Journal` describe the other files. `db.HasChangedSinceOpen()` re-looks at the files cheaply (see *Reading live files*).
* A snapshot has `Commit`, `PageCount`, `Notes` (why it may not be exact), `DatabaseMayBeNewer`, `IncludesUncommitted`, `FrameFor(page)`, `GetPageBytes(page)`, `Tables`, `ReadRows`, `Space()` and `Carve()`. Tables are read with the B-tree layer's cursors through a read-only page source over the snapshot; the schema comes from the interim `SchemaLite` parser, so a table whose CREATE text it cannot understand (virtual tables) has `CanReadRows == false` and is skipped by carving.
* `ReadRows` returns one value per declared column (`null`, `long`, `double`, `string`, `byte[]`); the rowid-alias column holds the rowid; generated VIRTUAL columns are not stored and read as `null`.

### Errors

`NotADatabaseException` (SQLITE_NOTADB, 26): the 16-byte magic is missing, the file is shorter than a header, or there is no usable log for an empty file. `CorruptDatabaseException` (the B-tree layer's, SQLITE_CORRUPT, 11, with the page and what was wrong): header fields outside their documented range (page size, reserved bytes, encoding), and any damaged page met while reading rows. `VfsException` (CANTOPEN) when the file does not exist. `ForensicChangedException`: a log frame changed between the scan and the read of its page (a live writer restarted the log). Unusable *side files* are never errors: a log with a bad header is reported as `Wal.Usable == false` with the reason, and the database file alone is read. Every size read from a file is checked against the file or the page before it is used to allocate or index; the page table of the space analysis is limited to 16 Mi pages; record payload buffers are limited to the pages that exist.

## The log as implemented

`WalScanner` reads the header (32 bytes, big-endian fields) and every complete frame (24-byte header plus one page). It keeps going after the first bad frame so older generations stay visible.

**Header.** Magic `0x377f0682` (little-endian checksum words) or `0x377f0683` (big-endian words; the low bit chooses); version 3007000; page size a power of two from 512 to 65536; the header checksum (over the first 24 bytes) must match. Otherwise the log is unusable (reason recorded), no frame is trusted, and a database snapshot never uses it.

**Checksum.** Words are taken in pairs from the data; for each pair `s0 += x0 + s1; s1 += x1 + s0`. A frame's checksum covers the first 8 bytes of its header and its page, and is seeded with the previous frame's checksum (the header's checksum for frame 1).

**Valid frame (current generation).** Salt 1 and salt 2 equal the header's, the page number is not 0, and the checksum equals the cumulative checksum. The valid chain ends at the first frame that fails. The last frame *in the chain* whose commit-size field is non-zero is the last commit; frames after it in the chain are **trailing** (written, never committed). Only frames up to the last commit are in the default snapshot.

**After the chain.** A frame with the header's salts that fails its checksum is **damaged** (a torn or corrupted frame). A frame with other salts is **stale**: it belongs to an earlier generation that a restart has not yet overwritten, or is garbage. Consecutive stale frames form a *generation* when each frame's checksum continues the previous frame's stored checksum; the first frame of a run cannot be verified (its seed was in a header that is gone) and is flagged `ChecksumVerified == false`. A stale run that does not begin at frame 1 has lost its first frames to the newer generation (`StartsAtFrameOne == false`, commits `PrefixLost`). Bytes after the last complete frame are reported as `TornTailBytes`.

**Commits and ids.** `WalCommit.Id` numbers commit frames in file order across all generations, from 1.

## Snapshots and what can be trusted

A snapshot shows page *p* from the newest frame for *p* in its frame set, else from the database file. The frame set is: the current generation's committed frames up to the chosen commit (plus trailing frames on request), or, for a commit of a stale generation, that generation's frames up to the commit. The database size is the commit's size field (or the header's in-header size when its "version-valid-for" equals the change counter, else the file length).

* **Latest and current-generation commits** are exact except when a checkpoint has copied *later* frames into the database file: a page that first appears after the chosen commit then shows its newer content, and its older content is gone. The reader works out whether that can matter: with a `-shm` whose salts match the log it knows how many frames were backfilled; without one it assumes the worst. `DatabaseMayBeNewer` says so, and `Notes` explains it. For the latest commit it is never set.
* **Stale-generation commits** are best effort: the generation's first frames may be overwritten, and the database file has been written since. The snapshot says so in `Notes`.
* **Database only** ignores the log completely.

## Space

`Space()` classifies every page (up to the first 16 Mi pages): B-tree pages by type, overflow pages, freelist trunks and leaves (chain and counts checked against the header; loops cut), pointer-map pages (auto-vacuum), the lock-byte page, **lost pages** (reachable from no root and not on the freelist) and unavailable pages. For each reachable B-tree page it lists the unallocated gap between the cell pointer array and the cell content area and every freeblock on the chain (increasing offsets, sizes checked), and sums the fragmented-byte counters. Bytes past the database size are reported as `TrailingBytes`. Problems met are listed in `Anomalies`; the walk continues past them.

## Carving

`Carve()` looks for records no live row occupies. Sources (`CarveSource`): the unallocated gap and freeblocks of live table pages of the snapshot (and the gap of a table interior page, which keeps the cells it had as a leaf before a split); freelist pages; lost pages; **older images of pages**: log frames the snapshot does not use, the database file's copy of a page the log replaces, and journal pages; and the file tail past the database size. Records are decoded against the tables in `sqlite_schema`: a cell is accepted for a table only if its column count fits.

**Complete cells.** At every offset of a free region the reader tries to read a table-leaf cell: payload-size varint, rowid varint, record header. It accepts it only if the header size plus the sizes the serial types give equals the payload size exactly (a strong self-check), the cell fits before the end of the region, and an overflow pointer, if the payload spills, is a plausible page number. Spilled payloads are followed through the overflow chain in the snapshot's pages; a chain that does not end with a zero link has been reused, and the tail is marked untrusted. Cells of an old page image found through its cell pointer array are read the same way and score higher (the page structure vouches for them).

**Freeblocks overwrite the first four bytes (fileformat2 §1.6).** When a cell is freed, its first four bytes become the freeblock's next-offset and size. For a table-leaf cell that destroys the payload size, the rowid and usually the header-size byte and sometimes the first serial type. The reader rebuilds the cell from what is left: it tries every start offset in the block and every position of the header-size byte up to 10 bytes in. If the header-size byte is intact it reads the header normally; if it is lost, it reads the remaining serial types, takes the column count from the table, and rebuilds the header size. If the first serial type is lost too it solves the first column from where the cell must end; when that column is the table's rowid alias it is simply NULL. The payload size follows from the header, so only the **rowid is lost** (`Rowid == null`, alias column `null`). The cell must end where something vouches for it: within 3 bytes of the end of the free block (a lone cell; fragments of up to 3 bytes are absorbed into freeblocks), or where the size in its own overwritten head says (the size of the freeblock the cell used to head), or where another cell begins. A cell in the middle of a merged freeblock may have its first four bytes replaced by the stale header of an earlier, smaller freeblock; that is the same case.

Rowid tables and WITHOUT ROWID tables are handled (the latter without a rowid); index entries are not carved. Records whose values are all NULL or empty are not reported (zeroed space looks like that).

### Confidence

Each candidate gets **evidence in bits**, and `Confidence = 1 / (1 + 2^((8 - bits) / 1.5))`, so 8 bits is 0.5, 12 bits is 0.86, 16 bits is 0.98. Bits are added for: payload size equal to header plus body (+6) or a consistent header alone (+4); the cell being reached through a valid cell pointer of a table-leaf page image (+6); the column count (+2 plus 0.5 per column up to 8; +0.5 if fewer than the table has); each column's serial type agreeing with the declared affinity (+1 text in a TEXT column, +1 number in a numeric column, +0.5 in an untyped column, −2 a number in a TEXT column, −1 a blob in a TEXT column, −3 a non-NULL rowid alias); a rowid inside the table's live range (+1.5) or far outside it (−2); the page being known to belong to the table (+1); other tables fitting equally well (−1.5 each); the end-of-cell checks for a damaged head (+3, +1.5 or +0.5); a lost header size (−1) or lost first type (−3, or −1 for the rowid alias); an overflow chain followed to a clean end (+3) or not (−4); values equal to a live row's when the rowid is unknown (−4: an old copy). The weights come from calibration against generated databases (below), not from theory. Identical records found in several places are reported once with `Occurrences`; a candidate equal to a live row (same table, rowid, values) is dropped unless `IncludeLiveDuplicates`.

Each `CarvedRecord` carries `File`, `FileOffset`, `Page`, `PageOffset`, `WalFrame`, `Source`, `Table`, `Rowid`, `Values`, `Confidence`, `EvidenceBits`, `Reasons` (one line per scoring step), `PrefixReconstructed`, `Truncated` and `OtherTableCandidates`.

### Calibration

`tools/OneLite.Forensics calibrate` builds databases with the pinned shell (600-row, 300-row and 24-row overflow tables; every page size from 512 to 65536), removes rows in known ways, carves, and counts: *recall* is removed rows found with at least the threshold confidence, *precision* is candidates at that confidence that are removed rows. Scenarios: `delete` (random rows, runs of neighbours, a table without rowid alias, overflow rows), `churn` (the same, then new rows reuse the space), `update` (rows grown or shrunk), `vacuum` (delete, then VACUUM), `walhist` (database in rollback mode, switched to WAL, then deleted: the old pages stay in the database file) and `walfull` (WAL from the start, nothing checkpointed), each with `secure_delete` off and on. The results are recorded in the method section at the end.

## Reading live files

No lock is taken, so a live writer can change the files under the reader. Frame pages are re-checked against the scanned frame header when read (`ForensicChangedException` if a log restart rewrote it). After a pass call `db.HasChangedSinceOpen()`: it is true if the log's salts changed, the log got shorter, the wal-index backfill count moved (a checkpoint finished) or the database file changed length; discard the pass and open again. New commits appended to the log do not matter: a snapshot never includes them. A checkpoint that is still running cannot be seen. A snapshot of a rollback-journal database that is mid-commit can show a torn mixture of old and new pages, as any unlocked read can.

## Method

Checks run with `tools/OneLite.Forensics` (`nonmod`, `calibrate`, `walsnap`, `coexist`, `info`), `tools/scripts/strace_check.py` and the `forensic-mutate` fuzz command, against the pinned `sqlite3` 3.53.4 on Linux x64 (Release; fuzz in the Checked configuration).

* **Non-modification.** A full pass (open, every snapshot, every table, space map, carve) over 145 generated and real-world files (47 `-wal`, 21 `-shm`, 15 `-journal` beside them): 145 identical by hash and modification time, no file created, 0 failures; 90 damaged or hostile cases refused with a typed error; 1,909 snapshots, 4,951,193 rows read, 6,307,434 candidates. The same run on read-only copies (files 0444 in a 0555 folder): identical counts. Under `strace -f` over 24 log/journal cases plus the real-world files (118 opens of watched files): no write-mode open, no `O_CREAT`, no write, no `ftruncate`, no `fcntl` lock, no rename or unlink. The .NET runtime itself takes a shared, non-blocking advisory `flock` on each opened file (236 calls, all `LOCK_SH|LOCK_NB` and `LOCK_UN`); that is a different lock family from the POSIX byte-range locks `sqlite3` uses, and it conflicts only with another process holding an exclusive `flock`.
* **Coexistence.** Against a shell holding an EXCLUSIVE lock: 200 reads, 0 wrong, 0 refused. Against a shell committing continuously in WAL mode: 1,895 passes; 812 accepted, every one equal to the shell's state at some commit; 6 rejected because the files changed under the pass (`HasChangedSinceOpen`); 1,077 refused or torn reads (the reader catches the log mid-write). The writer saw no error.
* **WAL snapshots.** Eight variants (several generations, truncating checkpoints, a reader that pins a partial checkpoint), 50 to 130 commits each, truth = the shell's rendering of a copy of the files after every commit. Every unflagged current-generation commit equals the shell. With a matching `-shm` all commits of the surviving generation were equal; without `-shm` many are flagged `DatabaseMayBeNewer`, and every flagged one checked was also equal. Commits of stale generations are best effort because the start of the log was overwritten.
* **Journal checksum.** The record checksum rule of the file format page (nonce, then the byte at `pageSize-200` and every 200 bytes below it) was checked against the hot journals of the generated corpus: every record of every journal verified (e.g. 7/7, 108/108, 188/188 across page sizes 512 to 65536).
* **Carving calibration** (600-row, 300-row and 24-row overflow tables; page sizes 512 to 65536; 2,393 deleted rows, 1,466 updated). Recall / precision at confidence 0.5, with the range from 0 to 0.97:

| scenario | secure_delete | recall @0.5 | precision @0.5 | @0.9 | @0.97 |
|---|---|---|---|---|---|
| delete | off | 82.2% | 97.2% | 73.2% / 99.8% | 14.6% / 99.7% |
| delete | on | 13.8% | 99.1% | 13.8% / 99.7% | 7.8% / 99.5% |
| churn | off | 75.8% | 96.8% | 66.9% / 99.7% | 13.4% / 99.7% |
| churn | on | 13.3% | 99.1% | 13.3% / 100% | 7.2% / 100% |
| update | off | 35.8% | 93.6% | 34.7% / 98.7% | 23.7% / 98.9% |
| update | on | 20.9% | 98.7% | 20.9% / 99.0% | 20.9% / 99.0% |
| vacuum | off / on | 0% | n/a | 0% | 0% |
| walhist | off | 99.9% | 95.0% | 99.9% / 99.8% | 99.9% / 99.9% |
| walhist | on | 96.7% | 96.7% | 96.7% / 96.7% | 96.7% / 96.9% |
| walfull | off | 99.9% | 95.0% | 99.9% / 99.8% | 99.9% / 99.9% |
| walfull | on | 96.7% | 96.7% | 96.7% / 96.7% | 96.7% / 96.9% |

  Overflow (`big`) rows are the weak table when rows are deleted from the main file: 48 of 96 at 0.5 (a freed overflow chain loses the first bytes of each page to the freelist link). With `secure_delete` on the rollback-mode recall is not zero: the remaining finds are probably stale copies left where balancing moved cells (the setting only clears the cell being deleted; not separately verified), and old page images in the log (`walhist`, `walfull`) keep everything because the log is never rewritten. `vacuum` leaves nothing to find. Per page size and per table figures are in the calibrate output (`ONELITE_DIAG=1` lists misses and false candidates).
* **Fuzz.** `forensic-mutate`, Checked build, one process, 600 s with seed 7 over the generated database corpus: 63,878 executions, 0 crashes. Earlier crashers (a WAL naming page 4e9 exhausting memory; huge payload lengths stretching a pass) are fixed; the first has a regression test.
* **Build.** Release and Checked builds of the whole solution: 0 warnings, 0 errors; 672 tests pass (4 skipped) in each configuration on Linux x64; the NativeAOT smoke app, which opens a database with the forensic reader, publishes with warnings as errors and runs.
