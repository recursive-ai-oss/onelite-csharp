# Clearer errors

OneLite's errors say the same thing SQLite-compatible libraries say, and then say more. Nothing about the default behaviour changes.

## What stays identical

`OneLiteException.Message`, `Code` (the primary result code) and `ExtendedCode` equal what the pinned SQLite release reports for the same statement
on the same data: the same text, the same codes. `OneLiteDataException.Message` is `SQLite Error <code>: '<message>'.` as before. A test
(`tests/OneLite.Tests/Diagnostics/ErrorDiagnosticsTests.cs`) runs more than 150 failing statements (syntax, names, arity, constraints, STRICT
types, misuse) on OneLite and on the pinned library and compares code, extended code and message.

## What is added

All members are read-only properties of `OneLiteException` (and are repeated on `OneLiteDataException`). They are empty or null when the
error has nothing to add. They are filled in only when an error happens, so a statement that succeeds pays nothing.

| Member | Meaning |
|---|---|
| `Sql` | The statement text the error is about. |
| `ErrorOffset` | What `sqlite3_error_offset` returns: the UTF-8 byte offset of the token the reference library blames, or -1 where it blames none. Set for syntax errors and for the name errors the reference positions: an unknown or ambiguous column, a misused aggregate or window function, an unknown function or a wrong argument count (the start of the call), a row value compared with a plain operand, an existing table or view name, an unknown schema name in `CREATE`, `VACUUM`, `REINDEX` and `PRAGMA`, and an ambiguous column from `*`. The reference gives none inside a join's `ON`, in an `INSERT`'s `RETURNING`, for unknown tables and for column-list errors of `INSERT` and `UPDATE`, and OneLite matches that (`ErrorOffsetTests`). Differences left: a `CHECK`, `DEFAULT` or generated-column expression naming an unknown column is reported when the table is first used, not when it is created, so it has no position at creation. |
| `Position` | `SqlPosition`: UTF-16 `Offset` and `Length` of the offending text, 1-based `Line` and `Column` (UTF-16 characters), and `ByteOffset` (UTF-8, the unit `sqlite3_error_offset` uses). |
| `Caret` | The line holding the position, then a line with `^~~` under it. Long lines are cut around the position. |
| `Identifier` | The name the error is about: the unknown table, column or function, the ambiguous column, the token a syntax error stopped at, the constrained column. |
| `Suggestions` | Up to three names close to what was written, closest first (see below). |
| `Constraint` | For a constraint failure: `Kind` (unique, primary key, not null, check, foreign key, STRICT data type), `Table`, `Columns`, `Name` (a CHECK's text) and, for unique and primary-key failures, `KeyValues`, the clashing key as SQL literals. |
| `Explain()` / `ToString()` | A readable account: the message with its line and column, the SQL line with the caret, the constraint details, "did you mean". `ToString()` is the usual exception text followed by this. |

Example:

```
no such column: nme
at line 1, column 8: no such column: nme
  select nme from users
         ^~~
did you mean "name"?
```

## How the pieces are found

* **Position.** A syntax error uses the position the parser reports. For a name error, the statement text is tokenised again and the first token that
  spells the name in the role the message implies is taken (after `table.` when the message names a table; followed by `(` for a function; after
  `COLLATE` for a collation). A name that appears several times in the statement can therefore be reported at its first use.
* **Suggestions.** Candidates come from the schema in scope: table and view names plus the names a `WITH` clause defines (unknown table), the columns
  of the tables the statement mentions (unknown column), the registered scalar function names and the aggregate and window function names (unknown
  function), the three built-in collations, and the common SQL keywords (a syntax error that stopped at a misspelt keyword). A candidate counts when its
  edit distance (insertions, deletions, substitutions, swaps of neighbours; case ignored) is at most a third of the written name's length (at least 1, at most 3), when
  the written name is a prefix of it, or, for names of two or more letters, when its letters appear in order inside the candidate (`nm` suggests `name`).
  For an unknown column that another table has, the suggestion is `table.column`; for an ambiguous column, the list of `table.column` that have it.
  For a wrong number of arguments, the single suggestion says how many the function takes.
* **Constraints.** Kind, table and columns come from the message. The key values are copied from the registers that held the new row's key when
  the uniqueness probe failed, and are formatted only on the error path.

## Limits

Runtime errors that are not constraint failures (an integer overflow, a bad `LIMIT`) carry the statement text but no position in it: the virtual
machine does not track which expression a failing instruction came from. A name that does not appear in the statement text itself (for example one
inside a view body) has no position. Suggestions never change which error is reported.
