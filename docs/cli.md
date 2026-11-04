# The `onelite` command-line tool

`onelite` is an interactive and scriptable SQL shell for OneLite, a SQLite-compatible database. It is built to be used like the `sqlite3` shell: the same command line, the same dot-commands for the common jobs, and, for the output modes it implements, the same bytes. OneLite is an independent project and is not affiliated with the SQLite developers.

```
dotnet tool install -g RecursiveAI.OneLite.Cli     # installs the command `onelite`
onelite app.db "select count(*) from users"
onelite app.db -json "select * from users limit 3"
onelite app.db .dump > app.sql
onelite new.db < app.sql
onelite -header -column app.db < queries.sql
```

The tool runs on the engine layer only (`Database`, `Connection`, `Statement`, `ForensicDatabase`). It depends on nothing but the .NET base class library, and publishes with NativeAOT.

## Command line

`onelite [OPTIONS] [FILE [SQL...]]`. `FILE` defaults to `:memory:`; a file that does not exist is created when the first statement needs it. Each `SQL` argument is a statement or a dot-command, run in order; the first one that fails stops the run with exit code 1. With no `SQL` arguments, input is read from standard input (a prompt and a banner appear when it is a terminal). Statements end at a `;` by the rule documented for `sqlite3_complete`, so a trigger body ends at `END;`. A statement left open at the end of input is run.

Options: `-bail`, `-batch`, `-interactive`, `-echo`, `-readonly`, `-header` / `-noheader`, `-separator S`, `-newline S`, `-nullvalue S`, `-escape ascii|symbol|off`, `-cmd COMMAND`, `-init FILE`, `-version`, `-help`, and the mode options `-list -csv -tabs -ascii -line -json -column -box -table -markdown -html -quote`. OneLite's own: `-key PASSPHRASE` for a database in the SQLCipher 4 format (the same as `PRAGMA key`). The aligned modes (`column`, `box`, `table`, `markdown`, `html`) turn headers on, as they do in the `sqlite3` shell.

Errors go to standard error with the message text of the pinned SQLite release (3.53.4): `Parse error near line N: ...` with the statement and a caret for errors that have a position, `Error near line N: ...` for errors while running, and `Parse error in 2nd command line argument: ...` (or `Error in ...` while running) for arguments. OneLite adds a line `  did you mean: a, b?` when it has suggestions. The exit code is 1 after any error.

## Dot-commands

| Command | Notes |
|---|---|
| `.help ?PATTERN?` | OneLite's own list. |
| `.quit`, `.exit ?CODE?` | |
| `.mode MODE ?TABLE?` | `list csv tabs ascii line json column box table markdown html quote qbox insert tcl`. `.mode` alone shows the current mode. `--tablename T` for `insert`. |
| `.headers on\|off`, `.separator COL ?ROW?`, `.nullvalue S`, `.width N...` | `.width` fixes a column's width in the aligned modes (negative: right-aligned). |
| `.show` | Current settings. |
| `.tables ?PATTERN?`, `.indexes ?PATTERN?`, `.schema ?PATTERN?`, `.databases` | Patterns are `LIKE` patterns. |
| `.dump ?PATTERN...?` | `--nosys`, `--data-only`, `--preserve-rowids`. |
| `.import ?--csv\|--ascii? ?--skip N? FILE TABLE` | See below. |
| `.read FILE`, `.output ?FILE?`, `.once FILE`, `.print`, `.echo`, `.bail`, `.timer`, `.changes`, `.cd`, `.shell CMD` | |
| `.open ?--readonly? ?--new? ?FILE?` | |
| `.eqp on\|off` | Prints OneLite's readable plan tree (`docs/plans.md`) before each query. Plans are not part of the compatibility promise. |
| `.key KEY` | Sets the passphrase for the next open of an encrypted database. |
| `.recover ?FILE?` | See below. |

`.backup`, `.save`, `.restore`, `.clone`, `.archive`, `.parameter`, `.load`, `.dbconfig` and the other maintenance commands of the `sqlite3` shell are not implemented; an unknown command is reported as the shell reports it.

## Output identity

For `list`, `csv`, `tabs`, `ascii`, `line`, `json`, `column`, `box`, `table`, `markdown`, `quote` and `html`, output is byte for byte the output of the `sqlite3` 3.53.4 shell on generated tables covering every storage class, NULLs, embedded newlines, quotes, commas, tabs, control characters, Unicode and long reals (`tests/OneLite.Cli.Tests`, which runs both programs). That includes the shell's habits: control characters other than tab, newline and CRLF are shown as `^A` (`-escape` changes it), tabs in aligned modes expand to eight-column stops, CJK text counts two columns, CSV quotes empty text and anything with a space, quote or non-ASCII byte, and numbers print the way `sqlite3_column_text` renders them.

Known differences, all in the display of unusual input:

* `.width` with text longer than the column: the shell wraps at word and punctuation boundaries by rules not described anywhere; onelite cuts at the column width.
* Aligned modes with blobs that are not valid UTF-8: widths can differ by a column.
* `.tables` and `.indexes` lay names out in columns by the same idea (down the columns, gap of 3 to 5 spaces inside 80 columns); the exact gap rule is a guess that matches the samples tried.
* `.mode tcl`, `qbox`, `insert` follow the shell's output on the cases tried, which are fewer than for the modes above.
* `.eqp` prints OneLite's plan tree, not the shell's `QUERY PLAN` text.

## `.dump`

The script is the shell's: `PRAGMA foreign_keys=OFF; BEGIN TRANSACTION;`, the tables in schema order with their rows (as `INSERT` statements with values written the way `quote()` writes them, `unistr('...')` for control characters), `sqlite_sequence` and `sqlite_stat1` last, then views, triggers and indexes, `COMMIT;`. For every file tried the dump is identical to the shell's, and a dump loaded by either program gives a database whose dump is identical and that passes `PRAGMA integrity_check` (tests: generated databases, a database using every kind of schema object, and Chinook, Northwind and Sakila when the corpus is present).

A dump of `AUTOINCREMENT` or virtual tables uses `PRAGMA writable_schema=ON` to restore `sqlite_sequence` and the virtual tables' schema rows; the engine supports that pragma (see `docs/engine.md`), so the shell's dumps load in onelite as they are, and the other way round. The shell turns the pragma away unless its input starts with the dump's `SQLITE_DBCONFIG_DEFENSIVE` warning comment; onelite has no defensive mode and honours the pragma wherever it is written. `.dump --data-only` also writes the rows of virtual tables, as the shell does.

## `.import`

`.import FILE TABLE` reads records separated by the current separators (`--csv`: comma and newline, CRLF accepted; `--ascii`: the ASCII unit and record separators). Fields may be quoted with double quotes, with `""` for a quote and embedded separators and newlines; a stray quote and an unterminated quoted field are reported with their line. If `TABLE` does not exist it is created from the first record, one column per field and no declared types (`"?"` for an empty name, `_1`, `_2` for duplicates, reported on standard error); if it exists, every record is inserted and short or long records are filled with NULL or trimmed, with a warning that names the line. `--skip N` skips records first. The whole import is one transaction. The results were compared with the shell's `.import` on quoted fields, embedded newlines, CRLF, byte order marks, blank lines, short and long records, an existing table, a missing and an empty file.

## `.recover`

`onelite damaged.db .recover > recovered.sql` reads the file through the forensic API (`docs/forensics.md`): the database file, its `-wal` as of the latest commit, and its journal, with no locks and no writes, so the files are byte identical afterwards and none is created. The output is SQL for any SQLite-compatible shell:

1. a comment header and `PRAGMA foreign_keys=OFF; BEGIN;`;
2. for every ordinary table, its `CREATE TABLE` text and one `INSERT` per live row (with the rowid, so rowids survive; `sqlite_sequence` is restored; virtual tables are skipped and their shadow tables recovered as plain tables; views and triggers have no B-tree and are not recovered);
3. `CREATE TABLE lost_and_found(source_table, confidence, source, page, page_offset, wal_frame, rowid, c0, c1, ...)` and one `INSERT` per record carved from free space, freelist pages, lost pages and older log frames, each preceded by a comment with the same facts; `source_table` is the table the carver believes the record belonged to (it is a guess), `confidence` is 0.5 to 1;
4. the indexes; `COMMIT;`.

The tables therefore hold live rows only; carved rows never mix in unless asked (`--merge` writes carved records into their tables with `INSERT OR IGNORE`). `--min-confidence X` changes the 0.5 threshold. A summary line on standard error gives the counts. `.recover` does not read encrypted databases. On a test database with 800 users, 300 log rows and a dropped table of 400 rows, after deleting a third of the users and 100 log rows, all 734 live rows came back identical and 785 carved records went into `lost_and_found`.

## Differences from the `sqlite3` shell that are on purpose

* `.recover` has its own format (above).
* `PRAGMA writable_schema=ON` works from any input (the shell ignores it unless defensive mode was switched off).
* Hints (`did you mean ...`) are added to error output.
* `-key` / `.key` exist for SQLCipher-format files.
* Statements are run by the OneLite engine, so every difference listed in `docs/compatibility.md` applies.
