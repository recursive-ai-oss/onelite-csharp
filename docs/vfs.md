# The VFS

Normative notes for the storage layer's lowest level (`RecursiveAI.OneLite.Storage.Vfs`). Sources: sqlite.org `lockingv3`, `atomiccommit`, `walformat`, `howtocorrupt`, `fileformat2`, `tempfiles`, `c3ref` (io_methods).

## Shape

`IVfs` opens, deletes and tests paths. `IVfsFile` reads, writes, truncates, syncs, sizes, locks and exposes `IVfsShm`, the wal-index memory. Every call has a synchronous and an asynchronous (`ValueTask`) form. A backend declares what it has in `VfsCaps` (`Sync`, `Async`, `SharedMemory`, `MultiProcessLocks`, `ReadOnly`, `BlockingOk`, `Mmap`).

* A backend derives from `VfsBase` / `VfsFileBase` and implements one flavour or both. A sync-only backend serves an async call inline and returns a completed `ValueTask`. An async-only backend serves a sync call by blocking only if it declares `BlockingOk`; otherwise the call throws `VfsException` with MISUSE and the message "backend requires the async driver".
* Failures are `VfsException` carrying the SQLite extended result code (`IOERR_READ`, `IOERR_WRITE`, `IOERR_FSYNC`, `IOERR_TRUNCATE`, `IOERR_DELETE_NOENT`, `FULL`, `CANTOPEN`, ...). A short read is not an error: the caller zero-fills.
* Locks are try-locks. They return at once; `false` means BUSY and the caller runs the busy loop.

## Locks (lockingv3)

Levels `None < Shared < Reserved < Pending < Exclusive`. The lock-byte page starts at 1 GiB (fileformat2): the PENDING byte at `0x40000000`, the RESERVED byte at `+1`, the 510 SHARED bytes from `+2`.

| Move | Taken |
|---|---|
| None → Shared | read lock on PENDING, read lock on SHARED range, release PENDING |
| Shared → Reserved | write lock on RESERVED |
| Reserved/Shared → Pending | write lock on PENDING |
| Pending → Exclusive | write lock on the SHARED range |
| Exclusive → Shared | read lock on the SHARED range, release PENDING and RESERVED |
| any → None | release; the SHARED range is released when the last reader leaves |

A failed move to Exclusive leaves the holder at Pending, which keeps new readers out. `CheckReservedLock` is true if any connection holds Reserved or higher.

POSIX record locks belong to the process and a close of any descriptor of the file drops all of them (howtocorrupt §2.2). So the file backend keeps **one descriptor and one lock table per main database file per process**, shared by all connections, and arbitrates between connections of the process itself (the OS cannot: they are one owner). The descriptor is closed when the last connection closes. The backend never opens a main database file only to probe it (`Access` uses file attributes). Not detected: hard links and case-folding aliases that name one file twice, and other code in the process opening and closing the file while a connection holds a lock.

**One lock mechanism on Unix.** Linux and macOS both use `fcntl(F_SETLK)` / `fcntl(F_GETLK)` through `NativeFile` (the one file with `LibraryImport`; it calls only the C library and, on Windows, kernel32, and loads no database library). Layouts of `struct flock` and the constants differ between the two systems and come from their `fcntl(2)` pages. Two things were found by experiment on macOS:

* `fcntl` and `open` are C variadic functions. Apple's arm64 convention passes variadic arguments on the stack, so the import for that platform declares six ignored register arguments before the real one.
* The runtime takes a whole-file `flock` on every file it opens. On macOS `flock` and record locks are one mechanism, so that lock made our own write locks fail with `EAGAIN` and made the open itself fail while another process held a write lock. Database and `-shm` files are therefore opened with `open(2)` directly (mode 0644 before the umask, close-on-exec), as the reference shell does. Those handles are synchronous, so async calls on them run inline.

Windows uses `LockFileEx` on the same byte ranges; since Windows cannot convert a lock in place, a change between shared and exclusive on a range releases it first and takes the old lock back if the new one is refused. It builds; there has been no live interoperability run on Windows.

## Shared memory (walformat)

32 KiB regions of the `-shm` file, mapped; eight lock slots at bytes 120-127 (write, checkpoint, recover, five read marks). Slot requests take several slots all-or-nothing. Byte 128 is the in-use byte: a process holds it shared while it has the file open; the first process to get it exclusively knows no one else uses the file and shortens the stale file. Words of the mapped memory are read and written through `ShmAccessor` only (acquire/release, compare-exchange, full barrier); multi-word structures are validated by checksum by the reader of the format, as walformat requires.

## Backends

| | Memory | File |
|---|---|---|
| Flavours | both, native sync | both (`RandomAccess` sync and async) |
| Cross-process locks | no (in-process only) | Linux and macOS: `fcntl` record locks. Windows: `LockFileEx` |
| `-shm` | in-heap, shared by name | mapped file, locks as above |
| `Sync` | nothing is durable | Linux: `fdatasync` for `Normal`/`DataOnly`, `fsync` for `Full`. macOS: `fsync`; `Full` is `F_FULLFSYNC` (plain `fsync` if the file system refuses it). Windows: the runtime's flush |
| `SyncDirectory`, `Delete(syncDirectory: true)` | nothing | Linux and macOS: open the directory read-only, `fsync`, close (an unopenable directory is skipped; a failed flush is `IOERR_DIR_FSYNC`). Windows: nothing |
| Sector size, flags | 512, powersafe overwrite | 4096, powersafe overwrite |

A connection set up on a platform without `MultiProcessLocks` is arbitrated among the connections of one process only, and must not share the file with another process.

### What works on which backend

| | Memory, File | `OpfsSyncVfs` (worker) | `OpfsAsyncVfs` (main thread), any backend with only `Caps.Async` | `HttpRangeVfs` |
|---|---|---|---|---|
| Sync API (`Open`, `Prepare`, `Step`) | yes | yes | no: SQLITE_MISUSE "backend requires the async driver" once a call has to wait | yes where blocking is allowed (not in the browser) |
| Async API (`OpenAsync`, `ConnectAsync`, `PrepareAsync`, `StepAsync`, `DisposeAsync`) | yes | yes | yes, no synchronous backend call at all (tested with a backend that refuses every one) | yes |
| Reads and writes, transactions, commit, rollback, cancellation | yes | yes | yes | read-only |
| Statements that run to completion inside one instruction and so cannot suspend: `PRAGMA integrity_check`, `VACUUM`, `VACUUM INTO`, `PRAGMA journal_mode=` (a change), `PRAGMA wal_checkpoint`, the `dbstat` table, full-text 3/4 tables (nested SQL) | yes | yes | **no**: SQLITE_MISUSE with "backend requires the async driver" naming the operation, raised before anything is touched | yes where blocking is allowed |
| WAL mode | yes (shared memory: file, memory) | in-heap wal-index, one connection | in-heap wal-index, one connection; a database must already be in WAL mode (the switch cannot suspend) | a WAL-mode database without a log file; refused when a non-empty `-wal` exists |
| Closing | `Dispose` checkpoints a WAL database | same | `DisposeAsync` releases the lock through the backend; it does not run the closing checkpoint, so the log stays and the next connection recovers it | n/a |

`Connection.PrepareAsync` loads the schema by awaiting the VFS (the resumable `ReloadSchemaStep` the first step of a stale statement already used); `Prepare` still
loads it synchronously. `Statement.DisposeAsync` and `ResetAsync`, and `Connection.DisposeAsync`, end an unfinished read transaction by awaiting the unlock.
Cancelling an `await` while a read transaction is half begun (the lock taken, page 1 not read) also ends it, so no lock is left behind (tested at every
suspension of a cold prepare and a cold step). The `Database.OpenAsync` happy path, `Database.OpenReadOnlyAsync`, `OneLiteConnection.OpenAsync`/`CloseAsync` and the readers' async
methods use these, so a cold database opens, prepares, queries and writes over an async-only backend through the engine, `.Data` and the happy path.

When a backend call fails, the engine's error carries the backend's exception as its `InnerException` (result codes and message text are unchanged).

### `HttpRangeVfs`

A read-only backend over HTTP range requests (RFC 9110 section 14): `new HttpRangeVfs(httpClient, new HttpRangeOptions { BlockSize, CachedBlocks, AllowNoValidator })`, paths are URLs.
Async-only with `BlockingOk` outside the browser, so `Database.OpenReadOnly` and `ForensicDatabase.Open` work on a thread that may block.

* **Blocks.** Reads are served from aligned blocks (default 64 KiB) kept in an LRU (default 128 blocks) per file version; concurrent readers of a block share one request.
* **One version per open.** The first request records the size, `ETag` and `Last-Modified`. Every later request sends `If-Match` (strong ETag) or `If-Unmodified-Since`, and the response's
  validators and `Content-Range` size are compared with the recorded ones. A difference fails the read with `RemoteFileChangedException` (SQLITE_IOERR_READ; the typed exception is the
  error's `InnerException`). A server that sends neither header is refused at open (CANTOPEN) unless `AllowNoValidator` is set.
* **Servers.** Must answer `Range` with 206. A server that ignores it is accepted only for a file of at most one block (the 200 body is the file). Empty files (416 `bytes */0`) open as empty.
* **Journals and logs.** Opening the main file for the engine HEADs `-journal` (and `-wal` when the header says WAL): a non-empty one makes the open fail with CANTOPEN, because a hot journal
  cannot be played back and a log may hold commits the file lacks. Empty ones are leftovers. The result is cached per file version (the validators catch any later change), so a read transaction
  costs no extra request. The forensic reader (bare read-only opens) can open `-journal`, `-wal` and `-shm` URLs as plain remote files.
* **Writing** is refused (CANTOPEN on a read-write open, READONLY on a write). Locks: shared always granted, nothing above it. Device flag: immutable.
* Query strings (signed URLs) are kept: auxiliary names are formed before the `?`.

## Observed sync sequence of the reference shell (Linux, one `INSERT` in rollback-journal mode)

Seen by `strace` on the pinned `sqlite3` 3.53.4 (4096-byte pages, `synchronous` at its default), `journal_mode=DELETE`, `BEGIN IMMEDIATE; INSERT; COMMIT`. This is an observation for the pager unit, which settles the full sequences.

1. Locks on the database: RESERVED byte (write), PENDING byte (write), then the SHARED range (write): the move to EXCLUSIVE goes through PENDING.
2. The journal is created (`O_RDWR|O_CREAT|O_NOFOLLOW|O_CLOEXEC`, mode 0644). Writes: 512-byte header at 0, then per page a 4-byte page number, the 4096-byte page image, a 4-byte checksum. A `pread` past the end of the journal follows.
3. `fdatasync(journal)`.
4. The directory is opened (`O_RDONLY|O_CLOEXEC`) and `fdatasync`'d (this library uses `fsync` on the directory, which is at least as strong).
5. The journal header is rewritten (12 bytes at offset 0: the 8-byte magic, which was zero until now, and the 4-byte record count) and `fdatasync(journal)` again.
6. The database pages are written (page 1 first), `fdatasync(database)`; the journal is closed and `unlink`ed with no directory flush after it; the locks are downgraded and released.

In WAL mode the WAL file is `fdatasync`'d; the `-shm` file is opened `O_RDWR|O_CREAT|O_NOFOLLOW`, its first lock is a probe of the in-use byte (128) with `F_GETLK`, then a write lock on it, a shortening to 3 bytes, and a downgrade to a read lock held while the file is open.

## Fault-injecting VFS

`FaultVfs` wraps any VFS and logs the calls that change storage: create, write (bytes copied), truncate, sync, directory sync, delete. Each logged call is a **crash point**; "after *n*" means calls with index below *n* happened. Per file it keeps a durable image (as of the last honoured sync) and the unsynced changes after it. A crash keeps the durable image and some of the unsynced changes (atomiccommit §2):

* writes are cut into device sectors (`FaultOptions.SectorSize`, default 512), each surviving or not on its own;
* with `Reorder` the survivors apply in any order (an older write can land over a newer one); without it they are a prefix of the issue order;
* with `TearSectors` a surviving sector write may arrive only as its first or only as its last bytes (sectors are written linearly); `TornFill` says what the rest holds;
* truncates survive or not as units; created and deleted files are directory changes that survive as a prefix, durable after a directory sync (or, unless `RequireDirectorySync`, after the file's own sync for a creation);
* a sync can be lost (`LoseSync(n)`, `LoseAllSyncs`): it reports success and makes nothing durable.

`CrashImage(n, policy)` returns the files a restart would find; `CrashPolicy` carries a seed, so the same seed on the same log gives the same bytes (SplitMix64; independent of the runtime). `EnumerateCrashImages(n)` lists every distinct outcome of independent survival for small workloads. `HaltAfter(n)` makes later calls throw `PowerLossException`, which is deliberately not a `VfsException`. `AddRule(FaultRule)` fails the nth call of a kind with a chosen extended code (for a write, after letting a number of leading bytes through). Images load into a `MemoryVfs` (`ToMemoryVfs`) or any VFS (`WriteTo`) for recovery.

Files that exist before the first open through the wrapper are read at that open and start durable. Temporary and delete-on-close files are not tracked.

## How it is checked

* `tests/OneLite.Tests/Vfs`: one contract suite run against both backends; the fault model checked against an independent brute-force statement of the sector model at every crash point of a scripted workload, plus seeded random policies; allocation counts; the two fallback rules.
* Lock interoperability: the same file held by this library and by a live `sqlite3` process, in both directions, for each lock level and for the WAL slots; the descriptor-close case with a control that shows the hazard is real. Run with `ONELITE_SQLITE3` pointing at the oracle; the outcome table is appended to the file named by `ONELITE_LOCK_TABLE`.
