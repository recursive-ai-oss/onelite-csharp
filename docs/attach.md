# ATTACH, DETACH and per-database pragmas

Normative notes for the engine layer. Behaviour is that of the pinned `sqlite3` (lang_attach, lang_detach, pragma, atomiccommit §5); where
the documentation is silent the text below records what the shell does, found by running it.

## Databases of a connection

| Slot | Name | Notes |
|---|---|---|
| 0 | `main` | The file the connection opened. |
| 1 | `temp` | In memory, created by the first statement that needs it. |
| 2 and up | attached | `ATTACH file AS name`; the lowest free slot is used, so `PRAGMA database_list` is in slot order. |

`TableInfo.Db` holds the slot. An unqualified name is looked up in `temp`, then `main`, then the attached databases in slot order; `schema.name`
names one database. `schema.sqlite_master` (or `sqlite_schema`) is that database's schema table.

## ATTACH and DETACH

- `ATTACH [DATABASE] expr AS name [KEY expr]`. The file name and the name are expressions evaluated when the statement runs (`?` works); a bare word
  stands for itself. NULL or an empty file name is a private temporary database; `:memory:` is a private in-memory database (journal mode `memory`).
  `file:` URIs are read for the path and `mode=ro|rw|rwc|memory`.
- Errors: `too many attached databases - max 10` (checked first), `database NAME is already in use` (any case; also `main` and `temp`),
  `unable to open database: FILE` (read-only open of a missing file), `file is not a database` (the file is read at ATTACH, so a wrong key fails here).
- `DETACH [DATABASE] name`: `cannot detach database main`, `no such database: NAME` (also for `temp`), `database NAME is locked` while the database is in
  a transaction of this connection (a read counts).
- ATTACH and DETACH work inside a transaction. Both bump the schema cookie the statements compile against, so prepared statements recompile.
- An attached database is opened like the main one (read-only if main is), with the connection's `synchronous`, the main database's text encoding and
  default page size for a new file, journal mode `delete`, and a 2000 KiB cache. With a `KEY` clause it is encrypted with that key (`KEY ''`: plain);
  without one it uses main's key when main is encrypted (SQLCipher's rule).

## Transactions across databases

- A statement starts the transactions of the attached databases it reads (shared lock) and writes (reserved lock or WAL write lock) with the statement.
  Writes that reach an attached database through a trigger or a foreign-key action start its transaction when its first cursor opens.
  Statements of an explicit transaction hold the locks until COMMIT or ROLLBACK; the read locks of an autocommit statement end with it.
- `BEGIN`, `SAVEPOINT`, `ROLLBACK TO` and statement rollbacks open and unwind a pager savepoint on every attached database that writes.
- COMMIT writes the databases that changed together. One changed database: an ordinary commit. Two or more, all in rollback-journal mode with
  `synchronous` above OFF: the super-journal protocol of atomiccommit §5 (`Storage/Pager/PagerSync.cs`, `MultiCommit`): write and sync the
  super-journal, record its name in each journal and sync, write and sync each database, delete the super-journal (the commit point), retire
  each journal. In PERSIST mode the reused journal is cut right after the super-journal's name (a reader looks for the name at the end of the file)
  and retired by truncation, not by zeroing its header (both observed in the reference with `strace`; found by the kill campaign below, which
  left the two files out of step before). A database in WAL mode, or `synchronous=OFF`, or `journal_mode=MEMORY/OFF`, commits on its own as in SQLite.
- Main always starts a write transaction when a statement writes, even if only an attached database changes (one more RESERVED lock than SQLite takes).
- **Async driver:** the attached databases' I/O is satisfied inline, as the temp database's is, so a statement that touches one blocks the thread on that
  file instead of returning *pending*, and a multi-database commit runs inline as a whole. Main's cache misses still suspend.

## Pragmas

Schema-qualified forms (`PRAGMA aux.user_version`) answer for that database: `journal_mode`, `synchronous`, `page_size`, `auto_vacuum`, `encoding`,
`user_version`, `application_id`, `schema_version`, `freelist_count`, `page_count`, `default_cache_size`, `wal_autocheckpoint`, `wal_checkpoint`,
`cache_size`, `locking_mode`, `secure_delete`, `max_page_count` (the last four keep their value on the connection), `integrity_check`, `quick_check`
(structure only), `table_info`, `table_xinfo`, `index_list`, `index_info`, `index_xinfo`, `foreign_key_list`, `table_list`.
An unknown schema is `unknown database NAME`. `temp.journal_mode` moves among the rollback modes and refuses WAL; `temp.synchronous` stays 0.

`ANALYZE` of an attached database or of `temp` is rejected (`unsupported`). `VACUUM schema`, `REINDEX schema.name` and DDL on attached databases work.

## pragma_* table-valued functions

`pragma_table_info(arg [, schema])`, `pragma_table_xinfo`, `pragma_index_list`, `pragma_index_info`, `pragma_index_xinfo`, `pragma_foreign_key_list`,
`pragma_table_list` return the rows of the pragma, with the hidden columns `arg` and `schema`; `pragma_database_list`, `pragma_collation_list`,
`pragma_module_list`, `pragma_pragma_list` and `pragma_compile_options` take no argument. Rows are made from the connection's schema when the scan starts,
so a correlated `JOIN pragma_table_info(m.name)` works. No argument (or NULL) gives no rows; an unknown schema is `unknown database 'NAME'`.
Known differences from the shell: `collation_list` lacks the shell's `decimal` and `uint`; `table_list` rows are sorted by name where the shell uses
hash order; the value pragmas (`pragma_page_count`, `pragma_journal_mode`, ...) have no function form yet.

## Checks

`tests/OneLite.Tests/Engine/AttachTests.cs` compares scripts with the shell (results, error text, `.dump` and `integrity_check` of every file).
`tools/scripts/pragma_cases.py` and `pragma_diff.py` compare the pragma surface; `cipher_pragma_cases.txt` includes ATTACH with and without a KEY against
SQLCipher. `tools/scripts/attach_kill_campaign.sh` kills a writer that commits to two databases in every transaction, and one that switches the journal
mode between commits; both files must hold the same transactions after recovery by OneLite and by the pinned library.
