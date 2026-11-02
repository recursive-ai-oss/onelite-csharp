# Interop and durability harness

How the repository proves two things against the pinned `sqlite3` (3.53.4): files written by either side are read identically by the other, and no committed transaction is lost or half-applied when a writer is killed. Everything here lives under `tools/` and runs oracles as pinned, precompiled binaries only. Nothing in `src/` depends on it.

## Oracles and corpora

`tools/scripts/fetch_oracles.py` and `tools/scripts/fetch_corpora.py` install the pinned binaries and the fetch-only corpora under `~/onelite/` (override with `--root` or `$ONELITE_HOME`). They are idempotent: every download is checked against a hash written in the script, and a second run verifies and downloads nothing.

| Path under the root | What | Verified by |
|---|---|---|
| `oracles/sqlite-3.53.4/` | `sqlite3`, `sqldiff`, `sqlite3_analyzer`, `sqlite3_rsync` from sqlite.org's precompiled tools (linux-x64, osx-arm64) | SHA3-256 and SHA-256 of the zip |
| `oracles/native-3.53.4/` | `libe_sqlite3.so` / `.dylib`, the same release as a library, from the `SQLite` 3.53.4 NuGet package (only `runtimes/<rid>/native/*` is extracted) | SHA3-256 and SHA-256 of the package |
| `oracles/sqlcipher/` | SQLCipher 4.5.6 (Ubuntu packages, Linux) or 4.20.0 (Homebrew bottle, macOS arm64; libcrypto path rewritten, ad-hoc signed) | SHA-256 of each package |
| `corpus/sqllogictest/` | the `test/` tree of the pinned commit of the sqllogictest mirror: test scripts only | git blob SHA-1 of each file, the tree id |
| `corpus/realworld/` | Chinook, Northwind, Sakila | SHA-256 |
| `corpus/generated/` | the interop corpus below (made by `interop gen`, never fetched) | `manifest.json` |

The `sqlite3` shell and the native library are different builds of one release. Their compile options differ (for example foreign keys default to on in the native library, and only the shell has the `dbstat` table), so every run header lists both. The shell is the reference for behaviour; the native library is used where a tool needs to drive a connection (writers that get killed, canonical rendering, `integrity_check`).

## The oracle tool (`tools/OneLite.Oracle`)

`oracle info | dump | check | render | run`. Every command first prints the header: the shell's version and source id, the native library's `sqlite_version()` and `sqlite_source_id()`, and the compile options of both. A run against anything but 3.53.4 stops.

The **canonical rendering** is what later differential and sqllogictest tools compare. A value is its storage class and exact content:

```
N                NULL
I:-42            INTEGER
R:1.5            FLOAT, shortest text that round-trips (R:-0, R:Infinity, R:-Infinity)
T:"a\"b\n"       TEXT as UTF-8 with \\ \" \n \r \t and \xNN for other control characters
T!6162ff         TEXT that is not valid UTF-8, hex
B:00ff           BLOB, hex
```

A row is its values joined by a tab. `run` renders every statement of a script as `cols`, `row`..., `end`, or `error<TAB>extended-code<TAB>message`. `render <db>` renders a whole database: header pragmas (`page_size`, `encoding`, `auto_vacuum`, `user_version`, `application_id`, `freelist_count`, `page_count`), the schema without root pages, and every table's rows in scan order.

## Interop corpus and runner (`tools/OneLite.Interop`)

`interop gen` writes `corpus/generated/` with the pinned `sqlite3` and records for each file how it was made, the expected `.dump` and canonical rendering (taken from the pinned `sqlite3` after recovery), the hash of every file as generated, header facts and page-kind counts from `dbstat`. For cases made by replaying SQL, `gen` also builds the expected state independently by running the same SQL into a fresh database and requires the same `.dump`. Categories:

- **matrix**: page sizes 512 to 65536 x UTF-8 / UTF-16le / UTF-16be x auto-vacuum none / full / incremental. Every file has table and index interior and leaf pages, overflow chains on table cells and on index keys (up to three pages and more), a `WITHOUT ROWID` table, a `STRICT` table, a view, a trigger, generated columns, `AUTOINCREMENT`, `ANALYZE` data and edge values of every storage class and integer width. A freelist is present where the mode allows one, with several trunk pages on the small page sizes.
- **reserved-bytes**: non-zero reserved bytes per page (header offset 20).
- **wal**: WAL-mode header with no log; and live writers killed with the log in these states: all commits uncheckpointed; checkpointed then restarted (stale frames from the old log after the new ones); truncated by `wal_checkpoint(TRUNCATE)`; partly checkpointed because a reader held a snapshot; one transaction larger than the cache. Variants with `-shm` removed, and torn variants of the last commit (log cut mid-frame with and without the stale `-shm`, one byte flipped so the checksum chain breaks, junk after the last frame).
- **hot-journal**: a writer with a tiny page cache is killed inside an open transaction, leaving a hot rollback journal (delete, persist and truncate journal modes; updates, file growth, drops; plain, UTF-16, auto-vacuum). `recoveryChangedDb` records whether recovery had database pages to roll back.

`interop run` checks a **reader** and a **writer** against the corpus:

- *read*: the reader command runs on a private copy of the case's files (database and side files) and prints the canonical rendering; it must equal the stored one. Reading includes recovering a hot journal or a WAL, so a reader may modify its copy.
- *write*: the writer command builds a database from the case's SQL script (`scripts/<case>.sql`; layout pragmas come first in the script). The pinned `sqlite3` must find `integrity_check` = ok, the layout the script asked for (page size, encoding, auto-vacuum), and the same `.dump` as the expected one. Cases that need process behaviour or file-level settings a script cannot express are skipped for writing.
- `--realworld DIR` adds the read direction for databases with no stored expectation (the pinned `sqlite3` renders a copy at run time).

Plug points are plain commands, so any implementation (the OneLite reader and writer, the CLI, a test host) can join: `--reader "CMD {db}"` prints the rendering on standard output; `--writer "CMD {db} {script}"` creates `{db}` from the script. Exit code 0 means success. The defaults are this program's own `render` and `write` subcommands, which use the pinned `sqlite3`, so `interop run` with no options proves the harness on `sqlite3` alone.

## Process-kill runner (`tools/OneLite.Durability`)

`durability kill` runs a **writer** process on a scripted stream of transactions and kills it with SIGKILL at random moments (seeded). After each kill it opens the file with the pinned `sqlite3` (which recovers a hot journal or WAL), runs `integrity_check` and checks the acknowledged commits.

The workload (`Workload.cs`) commits transactions 1, 2, 3, ... Transaction *i* inserts 1 to 600 rows (some with payloads of about 3 KB, which overflow), logs itself in `txlog`, sets `counter.n = i`, and deletes the rows of transaction *i*-3, all in one transaction. The writer appends *i* to its **ledger** only after `COMMIT` has returned, so the ledger never claims more than was committed.

After a kill, with *L* the highest ledger line and *S* the highest transaction an earlier check in the same episode saw, the file must show:

- `integrity_check` = ok (else *corruption*);
- exactly transactions 1..*M* in `txlog`, `counter.n` = *M*, and for the last three transactions all rows present, byte-exact, with the logged count and sum, and no other rows (else *partial commit visible*);
- *M* >= max(*L*, *S*) (else *lost acknowledged commit*);
- *M* <= max(*L*, *S*) + 1: one commit can have returned just before the kill and so be missing from the ledger (else *commit beyond ledger*).

Most kills land after the writer reports `READY`; one in ten lands during start-up and recovery. The check alternates between the live files and a copy, so the next writer sometimes has to do the recovery itself. Each episode (default 25 kills) uses a fresh database. A process kill loses no data the kernel has accepted, so these runs cover the commit protocol's ordering and recovery; power loss, torn writes and lost syncs belong to the fault-injecting VFS tests.

A custom writer is a command `CMD {db} {ledger} {journal} {sync}` that prints `READY` once set up, runs the workload above from the transaction after `counter.n`, and appends to the ledger as described.

## B-tree on the pager

* **Write interop** (`PagerBTreeTests`, `PagerDropRecreateTests`): the model workload of the B-tree tests (rowid table, three indexes, a WITHOUT ROWID table; inserts, replacements, deletes, overflow payloads, balances, commits in the middle) runs through `StorageEngine` into a real file over the grid page size 512..65536 x utf8/utf16le/utf16be x auto-vacuum none/full/incremental x journal mode delete/truncate/persist (rotating), with and without the pager's Yield injection, with two workload seeds and some reserved-byte sizes. The drop/recreate tests fill a rowid table and a WITHOUT ROWID table (overflow chains), drop them (the root page is freed), recreate them, and do it again within one transaction. Each file must pass the shell's `PRAGMA integrity_check`, read back to the model, and its `.dump` must equal the `.dump` of the same final state built by the shell. `ONELITE_INTEROP_LOG=file` records one line per file.
* **Read interop through the pager** (`tools/OneLite.BTreeRead`): opens each file with `StorageEngine` (so hot journals are played back first) and prints every table; `OneLite.Interop run --reader` compares with the shell's view after its own recovery. WAL-mode files are read as plain images until the WAL layer is merged.
* **Kill campaign** (`tools/OneLite.Durability btree-writer`, `tools/scripts/btree_kill_campaign.sh N SYNC_DIR`): the writer commits B-tree workloads (inserts, replacements, deletes, overflow, auto-vacuum) in a loop and acknowledges each commit on a side channel; the runner SIGKILLs it at random moments, reopens with both the shell and the library, and requires `integrity_check` ok and every acknowledged commit present.
* **Crash sweeps** (`BTreeDurabilityTests`): the same workloads on the fault VFS; crash images at every storage call under several loss policies (unsynced data dropped or kept, reordered, torn sectors, every sync lost in turn) recover to exactly the old or the new file.
* **Live coexistence** (`durability coexist`): the library and a separate `sqlite3` process alternate commits on one file (strict alternation, row-exact comparison after each turn, `integrity_check` every 50 rounds); every 25th round one side holds a lock the other must meet, with the BUSY result lockingv3 predicts, and anything else is a failure.
* **Fuzz** (`fuzz/OneLite.Fuzz`, `pager-btree-mutate [--seconds N] [--seed S] [--out DIR]`): mutated database images (structure-aware) and mutated hot journals go through `PagerBTreeTarget`: open with recovery, scan every plausible root, insert/delete/erase, commit or roll back. Only typed `OneLiteException`s are acceptable. Crashing inputs are kept under `testdata/pager-btree-fuzz` and replayed by `FuzzRegressionTests`.
