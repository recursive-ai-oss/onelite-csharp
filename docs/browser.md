# OneLite in the browser

Normative notes for the browser storage backends (`RecursiveAI.OneLite.Storage.Vfs.Browser`) and the headless-browser test host. Sources: the File System Standard (fs.spec.whatwg.org: `FileSystemFileHandle`, `FileSystemWritableFileStream`, `FileSystemSyncAccessHandle`, the origin private file system), the Web Locks API (w3.org/TR/web-locks), MDN's pages for both, the .NET documentation for `[JSImport]`/`[JSExport]`, and from sqlite.org `lockingv3`, `atomiccommit`, `wal` and `walformat`. The VFS contract these backends implement is in `vfs.md`.

## What exists

| Type | Context | Driver | `Caps` |
|---|---|---|---|
| `OpfsAsyncVfs` | any (page main thread, worker) | async only | `Async` |
| `OpfsSyncVfs` | dedicated worker | sync (async calls complete inline) | `Sync` |
| `DatabaseTransfer` | any backend | async | n/a (`.db` import and export) |
| `BrowserStorage` | any | n/a | loads the JavaScript module, reports what the context offers |

Both file systems store files in the origin's private file system (`navigator.storage.getDirectory()`). A path is relative to its root; `/` separates directories, which are created as needed. Temporary files (`TempDb`, `TempJournal`, `SubJournal`, empty names) live in memory in both.

The JavaScript half is `onelite-opfs.js`, a static web asset of the `RecursiveAI.OneLite` package (`_content/RecursiveAI.OneLite/onelite-opfs.js`). It has no dependencies. `BrowserStorage.LoadAsync()` imports it with `JSHost.ImportAsync`; `OpfsAsyncVfs.CreateAsync()` and `OpfsSyncVfs.CreateAsync()` do so themselves. If the app serves the file elsewhere, pass the URL. The interop is the BCL's `System.Runtime.InteropServices.JavaScript`; the browser-only types are marked `[SupportedOSPlatform("browser")]`, so native callers get a compile-time warning, and the NativeAOT build of the core is unchanged. The runtime's `[JSImport]` generator writes pointer-using stubs, which compile under the `AllowUnsafeBlocks` the core already has for `ShmAccessor` and `NativeFile`; no hand-written code in `Browser/` is unsafe.

## The driver rules (vfs.md §2.1) as built

* **`OpfsAsyncVfs` is async-only.** `Open`, `Access`, `Delete` and every file call except `Unlock` and `Dispose` throw `VfsException` with `SQLITE_MISUSE` and the message `backend requires the async driver`, before any work starts. Use `Pager.OpenAsync` and the pager's `...Async` methods. `Unlock(None)` and `Dispose` are allowed because releasing a Web Lock only needs a call, not an answer; `Dispose` also starts closing the file in the background, and the next `OpenAsync`, `AccessAsync` or `DeleteAsync` waits for those closes (`DrainAsync()` waits explicitly). `Pager.Dispose` is synchronous, so in WAL mode it cannot run its closing checkpoint on this backend: the `-wal` file stays, which is exactly what a crash would leave. The next open recovers from it and the data is complete.
* **`OpfsSyncVfs` is sync, with one asynchronous step.** A `FileSystemSyncAccessHandle` can only be created by an awaited call, so a database is *prepared* first. `PrepareAsync(path)` (called by `OpenAsync` for a main database) opens handles for the database, `path-journal` and `path-wal`. After that every call is a plain synchronous call: `Pager.Open`, the `...Sync` pager methods and `ExecuteSync` all work, and the `ValueTask` flavours return completed tasks. A synchronous `Open` of a path that was not prepared throws MISUSE (`'x.db' was not prepared; await PrepareAsync first`). Closing the last connection to a database closes its handles. The type is usable only in a dedicated worker; `CreateAsync` throws `CANTOPEN` elsewhere.
* A file *exists* in the worker backend when it is not empty (an empty database, journal or WAL is the same as none for SQLite), because the three handles are created together. `Delete` of a journal or WAL truncates it and flushes it; `Access` answers from the size.

## Locks

SQLite's lock levels (lockingv3 §2.0) are built from three named locks per database, standing for the three byte ranges lockingv3 locks in the file: PENDING, RESERVED and SHARED. Each is taken with `navigator.locks.request(name, { mode, ifAvailable: true })`, which never waits, so it is the VFS's try-lock; waiting is the pager's busy loop. A granted Web Lock is held by parking a promise, and released by resolving it.

| Move | Taken |
|---|---|
| None → Shared | shared PENDING, shared SHARED, release PENDING |
| Shared → Reserved | exclusive RESERVED |
| → Pending | exclusive PENDING |
| Pending → Exclusive | release the holder's own shared SHARED, request it exclusive; if refused, take the shared hold back and stay at Pending |
| Exclusive → Shared | exclusive SHARED to shared, release PENDING and RESERVED |

A Web Lock cannot be upgraded in place, hence the release in the Exclusive move (lockingv3 §4.0 allows the failed attempt to leave the holder at Pending, which keeps new readers out). Connections in one page are different holders to the lock manager, so they exclude each other exactly as connections in two tabs do.

* **`OpfsAsyncVfs`** uses Web Locks for every level, so two contexts of one origin (two tabs, a tab and a worker, two workers) contend per level. Tested: the second context gets BUSY for RESERVED while the first holds it, for SHARED while the first is EXCLUSIVE, and succeeds after the first releases.
* **`OpfsSyncVfs`** cannot wait on a Web Lock (the API is asynchronous, and a worker that blocks cannot receive its own result). Its levels are kept in a table in the worker for the connections of that worker. Across contexts the platform's own rule applies: one context holds a file's sync access handle, so a second context's `PrepareAsync` fails with BUSY until the first has closed its last connection. This is whole-file exclusion rather than per-level exclusion, which is the v1 contract (one connection per origin).
* **Do not mix the two backends on one file at the same time.** A file with an open sync handle cannot be written through `createWritable`.
* The browser releases a context's Web Locks and sync handles when the context goes away, so a killed tab never leaves a stale lock.

## Durability, backend by backend

The Standard defines what the APIs do, and says little about the storage underneath. What follows is what OneLite relies on, and what it does not.

* **`OpfsAsyncVfs`.** Writes go to a `FileSystemWritableFileStream`. By the Standard the changes are applied to the file atomically when the stream is closed; a stream that is never closed changes nothing. `Sync` closes the stream and so is the commit of everything written since the last `Sync`; the next write opens a new stream. Reads close an open stream first (a read must see the writes before it), so data can reach the file earlier than `Sync` does; the atomic-commit sequence tolerates that, because the pager syncs the journal before it overwrites database pages (atomiccommit §3.7).
  * Tab killed or crashed: the file is the state at its last completed close. Within one transaction that means the database file shows all or none of the writes made between two `Sync`s, which is at least as strong as the sector-level guarantees SQLite assumes.
  * Power loss or OS crash: the Standard does not say the close reaches the device. A close that returned is as durable as the browser's file writes are; OneLite cannot ask for more. Do not rely on this backend for power-loss durability.
* **`OpfsSyncVfs`.** Writes go to a `FileSystemSyncAccessHandle`, `Sync` is its `flush()`. Written bytes are in the file for readers at once; `flush()` is the platform's request to persist them. After a tab kill, bytes written since the last `flush()` may or may not be there, and possibly torn at page granularity, exactly the model of the fault-injecting VFS (`vfs.md`); the pager's journal and WAL sequences are written for it (`durability.md`). Power loss: same caveat as above.
* **Directory syncs** are no-ops on both: the origin private file system has no directory entries to flush apart from the file itself.
* **Both:** `synchronous=OFF` and `journal_mode=MEMORY|OFF` weaken this as they do anywhere. A killed tab leaves a hot `-journal` or an un-checkpointed `-wal` that the next open recovers from, in either backend (`pager.hot-journal-recovery`, `pager.wal-left-behind-is-recovered` in the browser suite). Kill-the-tab campaigns are not part of this build; the test host can run them (below).

## Import and export

`DatabaseTransfer` moves whole database files:

* `ImportAsync(vfs, path, bytes | stream)`: checks the 100-byte header (magic, valid page size, length a whole number of pages for bytes; for a stream the length is checked at the end and a partial file is emptied), takes the exclusive lock (BUSY if any connection has the database open), deletes a stale `-journal` and `-wal` (they would be replayed over the new pages), truncates, writes in 64 KiB chunks, syncs. It is not atomic: import to a new name and open that if an interrupted import must not touch the old file.
* `ExportAsync(vfs, path[, stream])`: reads through a pager inside a read transaction, so the result is the committed state: a hot journal is rolled back first and, in WAL mode, committed frames of the log are included. The output is a standalone file. It copies page by page and does not hold a write lock, so writers are not blocked beyond the reader's shared lock.

## The headless test host

The browser tests are `tests/OneLite.BrowserTests` (a `Microsoft.NET.Sdk.WebAssembly` app with a small harness; xUnit does not run in a page) and `tools/scripts/browser_tests.py`, which drives headless Chrome for Testing through Playwright's Python driver. Nothing is installed system-wide.

```
tools/scripts/fetch_browser.sh                     # Chrome for Testing 156.0.8078.80 (SHA-256 pinned) and playwright==1.55.0 into ~/onelite/browser
CORPUS="$HOME/onelite/corpus/realworld" tools/scripts/browser_tests.sh [--only SUBSTRING]
```

`browser_tests.sh` publishes the test app, serves it from a local port on 127.0.0.1 (sent with the cross-origin isolation headers; the backends do not need them), and runs:

1. every test the page (main thread) and a dedicated worker list: VFS behaviour (read, write, truncate, size, sync, reopen, try-locks, open flags, delete, driver rules), pager (commit and reopen in each journal mode and WAL, roll back, hot-journal recovery, cache spill, page sizes 512 and 65536), B-tree insert and scan round trips (table, index, overflow chains) in rollback and WAL mode, and the import and export cases;
2. two contexts of one origin contending for locks (page and page, page and worker, worker and second page; two workers for the sync handle);
3. each corpus database imported through each backend, exported (bytes must equal the source) and walked through the B-tree (digest must equal a walk of the source image);
4. databases the browser wrote, exported to the host and checked with the pinned `sqlite3`: `PRAGMA integrity_check` and a `.dump` equal to the dump of the same content built by `sqlite3`.

Results go to `results.json` and a per-group table. Kill-the-tab runs are described below.

## Reading over HTTP in the browser

`HttpRangeVfs` (docs/vfs.md) works in the browser through `HttpClient` (fetch): async-only, no `BlockingOk`. Use `Database.OpenReadOnlyAsync` and the async API. Cross-origin servers must allow the `Range` and
`If-Match` request headers (CORS preflight) and expose `ETag`, `Last-Modified`, `Content-Range` and `Content-Length`; the test host serves same-origin.

## Kill-the-tab runs

`tools/scripts/browser_tests.py --kill N` (`browser_tests.sh --kill N --kill-only`) kills a writer page N times for each of four combinations: the main-thread backend and the worker backend, each in
rollback-journal and in WAL mode. Per kill: a fresh page recovers the file left by the previous kill (opening it rolls back a hot journal or reads the log), exports it to the host, and the host checks with the
pinned `sqlite3` that `PRAGMA integrity_check` is `ok`, every row up to the last one the driver saw acknowledged is present, rows are contiguous from the first one this writer added, and every transaction
(one to three rows, marked in a column) is whole. Then that page opens the database through the engine (`Database.OpenAsync`, `WriteAsync`), starts a writer loop, polls how many transactions were acknowledged
(a transaction counts when `WriteAsync` has returned), and kills the page at a random moment: half the kills close the page, half navigate it away (`about:blank`), either of which tears it and its worker down mid-transaction. The worker
backend's writer runs in the page's dedicated worker, which goes with the page. The check is conservative: a row acknowledged after the last poll but before the kill need not be present.

