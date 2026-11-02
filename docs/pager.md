# The pager

Normative notes for the storage layer's page manager (`RecursiveAI.OneLite.Storage.Pager`). Sources: sqlite.org `fileformat2`, `atomiccommit`, `lockingv3`, `tempfiles`, `pragma` (`synchronous`, `journal_mode`, `cache_size`), `rescode`. The sync points that can suspend are listed in the suspend-point notes; the commit and recovery sequences actually observed on the reference shell are in `durability.md`.

## What it owns

* One database file, its rollback journal and (for `ATTACH`) a super-journal. Page numbers start at 1; page *n* is at byte `(n-1) * pageSize`. The lock-byte page (the page holding file offset `0x40000000`) is never read, written or allocated (fileformat2 §1.3).
* The 100-byte database header (`DatabaseHeader`): validation of every field before use, the change counter, the in-header database size and its validity rule (`version-valid-for` equals the change counter, otherwise the file length decides), reserved bytes per page, the freelist head and count.
* The page cache (`PageFrame`s, clock eviction, pin counts, dirty list, soft size limit), the rollback journal (all five modes), hot-journal detection and playback, statement journals and savepoints, `synchronous` and `cache_size`.
* The freelist (trunk and leaf pages) and the pointer-map pages of auto-vacuum databases. The B-tree layer decides *what* to free and move; the pager keeps the lists and the map consistent.

## The step contract

Every operation that can touch storage returns `PagerStatus`:

* `Ok`: finished; out-parameters are valid.
* `Pending`: the pager needs one I/O; `PendingIo` describes it (`Kind`, `Phase`, `File`/`Path`, `Offset`, `Data`, `Run`). Run it (`ExecuteSync()`, or `await ExecuteAsync(ct)`), which feeds the result back with `Complete`, and **call the same operation again with the same arguments**. Nothing observable has happened yet; no local state outlives a `Pending`.
* `Error`: `Error` holds the typed `OneLiteException` (result code, extended code, message, `Detail`). The pager is still consistent: call `Rollback`.

Only one request is outstanding at a time. Calling any other operation while one is pending throws `SQLITE_MISUSE`; `Abandon()` drops the outstanding request. `PagerOptions.YieldEvery = n` (always compiled, option-driven, not only in Checked builds) turns every n-th cache hit into `Pending(Yield)`, so every suspend point can be resumed in tests; `Yield` needs no I/O, call `ExecuteSync()` (a no-op) and re-enter.

The engine's temporary files (the sorter's runs, see `engine.md`) use the same slot: `Pager.ExternalIo(phase, request, out completion)` is the re-entry helper for them. It issues the request and returns false, or consumes the completion of the same phase and returns true. `YieldEvery` also injects a `Yield` before every n-th such request.

`...Sync` wrappers run the loop inline and throw `Error`; `...Async(ct)` twins await the VFS. Wrappers exist for `BeginRead`, `EndRead`, `BeginWrite`, `Commit`, `Rollback`, `Get`, `MakeWritable`, `Allocate`, `Free` and `RollbackSavepoint`.

### Differences from the suspend-point spec (§3 of the design notes)

* `IoRequest` carries the file, path, `Memory<byte>` and run list directly instead of buffer-slot numbers; `Close` is a plain `Dispose`, not a request.
* Statement journals and savepoints live in memory (no temp-file spill); `journal_mode=MEMORY` keeps the journal copies in memory too.
* `Get(pgno > PageCount)` is `SQLITE_CORRUPT`, as the spec says; a new, empty database gets page 1 lazily at the first `Get(1)` of a write transaction.
* Lock waits are `Sleep` requests driven by `PagerOptions.Busy(retries)` (returns milliseconds to sleep, or negative to give up; the default gives up at once). A hot-journal recovery that cannot reach EXCLUSIVE drops SHARED, sleeps and starts over, so two recoverers cannot deadlock.
* Commit is `PrepareCommit` (journal flushed/synced, EXCLUSIVE), `WriteDatabase` (dirty pages, truncate, sync), `FinishCommit` (retire the journal, the commit point); `Commit()` runs the three. The phases of spec §3.4 map onto them as 1-6, 7-9, 10-11.
* Corruption: `OneLiteException.Message` is the reference text (`database disk image is malformed`, `file is not a database`); the page and what was wrong are in `Detail`.
* A spill under a reader in rollback mode fails the statement with `SQLITE_BUSY` (observed on the reference shell); it does not grow the cache.
* `DELETE` journal mode also syncs the directory after the unlink at `synchronous=FULL` (the reference does it only at EXTRA); see `durability.md`.

## Calls (what the B-tree layer uses)

| Call | Meaning |
|---|---|
| `Pager.Open(vfs, path, options)` / `OpenAsync` | opens the database file (never creates a journal). `PagerOptions`: `PageSize`, `ReservedBytes`, `Encoding`, `AutoVacuum`, `IncrementalVacuum`, `CacheSize` (pages if positive, KiB if negative; default -2000), `JournalMode`, `Synchronous`, `FullFsync`, `ReadOnly`, `Busy`, `YieldEvery`. |
| `BeginRead()` / `EndRead()` | takes SHARED, recovers a hot journal if there is one, validates page 1, flushes the cache if another process changed the file. `EndRead` is only legal in a read transaction (not inside a write transaction: commit or roll back instead). |
| `BeginWrite()` | RESERVED lock; requires a read transaction (`SQLITE_MISUSE` otherwise). The journal is created on the first page that needs it. |
| `Get(pgno, GetOptions, out frame)` | pins a page (`PageFrame`; `frame.Data` is a `byte[]` of one page). `GetOptions.NoContent` skips the read for a page about to be wholly overwritten. `pgno` outside `1..PageCount` (or the lock-byte page) is `SQLITE_CORRUPT`. A hit allocates nothing. |
| `Release(frame)` | unpins. |
| `MakeWritable(frame)` | call before **every** modification batch (cheap when nothing is left to do). Journals the original, saves it for open savepoints, marks dirty. |
| `Allocate(out frame)` | a fresh zeroed, writable, pinned page: from the freelist, else by growing the file (skipping the lock-byte page and pointer-map pages). |
| `Free(pgno)` | puts the page on the freelist. The caller must have released its own pin first. |
| `ReservePages(allocations, frees)`, `ReservePtrmap(pgno)` | the prefetch half of reserve-then-effect: see "Reserve, then effect" below. |
| `IsPtrmapPage`, `PtrmapPageOf`, `PtrmapGet`, `PtrmapPut` | pointer-map entries (auto-vacuum). |
| `LargestRootPage`, `SetLargestRootPage(page)` | header offset 52. |
| `SetRelocator(relocator)` | auto-vacuum relocation at commit: see "Auto-vacuum at commit" below. |
| `OpenSavepoint()`, `ReleaseSavepoint(level)`, `RollbackSavepoint(level)` | savepoints and statement journals; level 1 is the outermost. |
| `Commit()` / `Rollback()` | end the write transaction (`Rollback` also ends a transaction after `Error`). `HasChanges` tells whether a write transaction modified anything. |
| `PrepareCommit(superJournal)`, `WriteDatabase()`, `FinishCommit()` | the phases of a commit; `MultiCommit.CommitSync(pagers)` drives several pagers with a super-journal (atomiccommit §5). |
| `SetSynchronous`, `SetJournalMode`, `SetCacheSize`, `TrySetPageSize(size, reserved)` | pragma settings. The first two are refused inside a write transaction; the page size can only change while the database has no pages. |
| `PageSize`, `UsableSize`, `ReservedBytes`, `PageCount`, `AutoVacuum`, `DatabaseEncoding`, `JournalMode`, `Synchronous`, `LockBytePage`, `LockLevel`, `InTransaction`, `InWriteTransaction`, `Error` | state. |

`PageFrame.Data` is the page; the first 100 bytes of page 1 are the database header, edited through `DatabaseHeader` accessors after `MakeWritable`. Offsets 24, 28 and 92 (change counter, size in pages, version-valid-for) are stamped by the pager at commit.

A new database (zero-length file) gets its page 1 written by the pager at the first write: the header (library version number 3053004), plus an empty table-leaf page header at offset 100, so the B-tree layer starts from a valid empty schema table.

## Reserve, then effect

The B-tree changes pages in an effect phase that must not suspend, so every suspension happens first, in a reserve phase. The pager's part of that contract:

* After `ReservePages(allocations, frees)` returns `Ok`, the next `allocations` calls to `Allocate` and `frees` calls to `Free` return `Ok` and never `Pending`; so does `SetLargestRootPage`. After `ReservePtrmap(pgno)` returns `Ok`, `PtrmapGet(pgno)` and `PtrmapPut(pgno, ...)` never return `Pending`. A reserve call that returns `Pending` is repeated with the same arguments, like any other pager call.
* How it holds: page 1, the freelist trunks the calls can touch (those that cover the allocations, and one more for the frees that follow) and, in an auto-vacuum database, the pointer-map pages of every page they can hand out are loaded and stay cached, because while reserving the cache grows past its soft limit instead of evicting. Free frames are set aside for the pages that will be allocated, so the effect phase evicts nothing, reads nothing and cannot spill. The rollback journal is opened if this transaction will journal pages, and its buffer is made roomy enough for every original page the calls can save, so the effect phase never writes.
* Calls made before the first effect add up (two `ReservePages` calls reserve for both). An effect completing (`Allocate`, `Free` or `PtrmapPut` returning `Ok`) ends the phase; the next reserve call counts afresh. An effect that allocates, frees and writes no pointer-map entry (an insert that fits its leaf) ends the phase through the internal `EndReservePhase`, which the B-tree calls (via `IBtPageSource.EndEffect`) when an insert or delete has finished; otherwise the promises of every such operation would add up for the whole transaction, and so would the spare frames.
* What the caller must do: a page it will pass to `Free` must be in the cache, so it has to be pinned across the reserve call (it just read or wrote it, and holds the pin until it releases it for `Free`) unless it is already saved in the journal. A pointer-map entry needs its own `ReservePtrmap`. Pages beyond the end of the file need none (their pointer-map page is created in memory).
* Costs: the reserve may write journal records or grow the journal buffer, and the cache may stay larger than `cache_size` until pages are evicted later. The reserve covers the records the allocator itself saves; pages the B-tree will edit are made writable by the B-tree in its own reserve phase (`MakeWritable` of a cached page that is already journaled never suspends).

## Auto-vacuum at commit

In a full auto-vacuum database (auto-vacuum on, incremental flag off) the file has no free pages after a commit (fileformat2 §1.7). The pager drives this as the first phase of a commit, before the header is stamped, if a relocator is installed with `SetRelocator`; without one the free pages stay and the file is valid but larger.

1. Read the whole freelist (trunks and leaves, validated against the header's count and for duplicates).
2. The final size is the pages in use plus the pointer-map pages and the lock-byte page that lie among them, never ending on one of those.
3. From the last page down to the final size: a free leaf or the lock-byte page is dropped; a pointer-map page or a free trunk is saved in the journal and dropped; an in-use page is looked up in the pointer map and moved into the lowest free page at or below the final size. A root page above the final size is `SQLITE_CORRUPT` (roots are never moved).
4. A move is: save the old page in the journal, copy its bytes verbatim to the new page (journaled if it was a trunk), write the new page's pointer-map entry (same type and parent), then call `IPageRelocator.Relocate(pager, oldPage, newPage, type, parent)`, which repairs every reference to the old page and the pointer-map entries of the moved page's children (it uses `Get`, `MakeWritable` and `PtrmapPut`; its documentation lists what each type needs). It may return `Pending` and is then called again with the same arguments.
5. The header's freelist trunk and count become 0, the database size becomes the final size, and the commit truncates the file as usual.

A free leaf below the final size that nothing moved into, or a page the pointer map calls free that is not on the freelist, is `SQLITE_CORRUPT`. Crash safety is the ordinary commit's: everything cut off is in the journal, and a rolled-back free leaf's content is not restored (the format leaves it open). `PRAGMA incremental_vacuum(N)` runs the same machinery in the middle of a write transaction (`Pager.IncrementalVacuumStep`): it frees `min(N, free)` pages, so the final size is computed with the free pages that stay counted in; the pages below the final size that nothing moved into are rewritten as a fresh freelist (trunk, its leaves, next trunk, ...), and the header gets the new trunk and count. A call with nothing free, or in a database that is not in incremental mode, does nothing.

## The WAL seam

`IPageSource` (`TryLocate(pgno, out file, out offset)`) is consulted by `Get` before the database file. A WAL layer installs one with `SetPageSource` at `BeginRead`; with none installed the pager reads the database file. WAL mode is implemented in the pager itself (`wal.md`); `SetJournalMode(Wal)` throws `SQLITE_MISUSE`, and `ChangeJournalMode(Wal)` is the way in (and out).

## Not here

B-tree cells, records, overflow content, schema. Cursor logic. WAL and `-shm`.
