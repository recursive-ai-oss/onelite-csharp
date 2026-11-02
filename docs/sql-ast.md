# SQL front end: tokenizer, parser, AST

Normative notes for the code in `src/OneLite/Sql/` that later units (name resolution, planner, VM) build on. The target is SQLite's documented dialect (`lang_*`, `lang_keywords`, `lang_expr`, `datatype3`, `limits`) as the pinned `sqlite3` 3.53.4 accepts it. Anything the documents leave open was settled by running statements through the pinned binary.

## Entry points

```csharp
SqlParser.ParseOne(string sql, int start, out int end, ParserOptions? options = null) // Statement? (null at end of text)
SqlParser.ParseAll(string sql, ParserOptions? options = null)                          // ImmutableArray<Statement>
SqlWriter.Write(Statement | Expr, bool parenthesize = false)                           // canonical text
```

- `ParseOne` parses the statement starting at `start`, returns it and sets `end` to the index after its `;` (or the end of text). It skips leading empty statements (`; ; select 1`). A NUL character ends the text, as in SQLite. This is the shape `prepare` needs: the unused tail is the rest of `sql` from `end`.
- `ParserOptions.BeginConcurrent` accepts `BEGIN CONCURRENT`. Off by default because the pinned SQLite rejects it.
- Everything is internal for now; consumers inside the assembly use it directly. The tree is not public API.

## Errors

A rejected statement throws `SqlParseException`:

| Member | Meaning |
|---|---|
| `Message` | SQLite's text, byte for byte (`near "x": syntax error`, `unrecognized token: "…"`, `incomplete input`, `unterminated …`, limit messages such as `Recursion limit` and `too many terms in compound SELECT`) |
| `ResultCodeError` | the primary code of every parse failure: `SQLITE_ERROR` (1) |
| `ErrorOffset` | what `sqlite3_error_offset` returns: the UTF-8 byte offset of the offending token, or -1 when SQLite reports none (`incomplete input`, limits) |
| `Position` | the same place as a UTF-16 index into the string, or -1 |

Parsing stops at the first error. The parser never returns a partial tree.

## The tree

- Every node is an immutable `record` deriving from `SqlNode`. Collections are `ImmutableArray<T>`. Value equality comes with records, so tests compare trees structurally (spans excluded from construction but included in equality only if set; compare after `SqlWriter.Write` when spans are irrelevant).
- `SqlNode.Span` is a half-open `[Start, End)` range of UTF-16 indexes into the text passed to the parser, set once by the parser while it builds the node. A child's span lies inside its parent's. Spans are the only link back to source text: result-column names, error carets and `sqlite_schema.sql` text come from them.
- Hierarchy: `Statement` (select, insert, update, delete, create table/index/view/trigger/virtual table, drop, alter table forms, attach, detach, begin, commit, rollback, savepoint, release, pragma, vacuum, reindex, analyze, explain), `Expr`, `SelectCore` (`SimpleSelect`, `ValuesCore`), `TableSource` (`NamedTable`, `TableFunction`, `SubquerySource`, `ParenFrom`), `ResultColumn`, and clause nodes (`WithClause`, `LimitClause`, `OrderingTerm`, `FromClause`, `FromItem`, `JoinOperator`, `ReturningClause`, `UpsertClause`, window and frame nodes, column and table constraints).
- `Name` holds an identifier with quoting removed (`"a""b"` is `a"b`). Keyword-as-identifier fallbacks (`lang_keywords`, including `IF`) arrive as ordinary names.
- `LiteralExpr` keeps the literal's text as written (numbers, strings with quotes removed, blobs); typing and overflow-to-REAL are for the evaluator, not the parser.

## Normalizations

The parser removes spelling differences that mean the same thing. Everything else is kept as written.

- `==` is `BinaryOp.Eq`, `<>` is `Ne`; `IS NOT DISTINCT FROM` is `Is`, `IS DISTINCT FROM` is `IsNot` (`lang_expr`).
- `LIMIT a, b` is stored as `Limit = b, Offset = a`.
- `x ISNULL`, `x NOTNULL` and `x NOT NULL` are `NullTestExpr`; `x IS NULL` and `x IS NOT NULL` are `BinaryExpr(Is/IsNot, …, NULL literal)`.
- Parentheses are kept as `ParenExpr` (column naming and carets need the source form). Row values `(a, b)` with at least two items are `RowValueExpr`.
- A `CONSTRAINT name` with nothing after it is kept as a marker node (`DanglingTableConstraintName`), because SQLite accepts it in places.
- `PRAGMA` values keep their kind: `Number`, `Word`, `String`, or `Keyword` (keywords keep their written case).
- A table-options list ignores a leading comma (`create table x(a) , strict` parses).
- `GENERATED ALWAYS AS (…)` is a column constraint after any other constraint; it is stripped from the declared type only when the type has no size list and the words were bare keywords.

## SqlWriter

`SqlWriter.Write` prints a tree as canonical SQL that parses back to a tree equal to the original after the normalizations above. It is the round-trip oracle, and the base for `sqlite_schema.sql` text normalisation later. With `parenthesize: true` every compound expression is wrapped, which removes precedence from the text; the comparer evaluates literal expressions in both forms to check the parser's precedence against the oracle. Quoting uses `SqlWriter.QuoteName` (double quotes only when needed). The writer checks stack depth and throws `InsufficientExecutionStackException` rather than overflowing.

## Limits

Matching the pinned build's compile-time limits (`PRAGMA compile_options`): the parser enforces `MAX_EXPR_DEPTH` 1000, `MAX_COMPOUND_SELECT` 500 and `MAX_VARIABLE_NUMBER` 32766. `MAX_SQL_LENGTH`, `MAX_COLUMN` and the other limits are checked by later layers, not here.

SQLite fails deeply nested statements with `Recursion limit` (parser stack, `near`-less) or `Expression tree is too large (maximum depth 1000)`. Two models reproduce this:

- **Parser stack.** Each construct costs a weight (paren 1, function call 3 plus 2 per extra argument, binary operand 2, CASE 1/3/5/4, WITH item 5, SELECT 2, FROM 2, parenthesised FROM 2, window spec 6, FILTER 5); the statement fails once the sum passes `StackLimit` = 2495. Calibrated per construct family against the oracle, not derived from its internals.
- **Expression height.** Operator and call nesting count 1 per level; a subquery or `EXISTS` adds 23, an `IN` list 2, `COLLATE`/`ISNULL`/`NOTNULL` add 0.

Measured with `tools/OneLite.SqlCompare` (`run.sh depth 6000`, 31 families): all families have the same first failing depth as the oracle except those below.

**Known residual differences** (limit boundaries only; accepted statements and all other errors are identical):

- Window-call nesting: oracle fails at 277, we fail at 278.
- A subquery nested n deep around an inner chain of m `+` terms: the oracle's height grows faster than our flat 23 per level. Minimum failing nesting n for `(select ` repeated around `1+1…`: m=0: 44, m=1: 43, m=2: 42, m=5: 39, m=10: 35, m=20: 28, m=50: 16, m=100: 9, m=200: 4, m=400: 2, m=800: 1. We fail later than the oracle for m>0 (for m=0 we agree). The relation is not linear, and no formula fitting the data was found by probing. Families `subq+chainN`, `subq-where-in-col`, `subq-limit`, `subq-join-on` are affected; for `subq-join-on` the oracle reports `Recursion limit` at 249 where we report the height error at 44.

**Stack requirement.** The parser is recursive. Reaching 2,493 nested parentheses needs about 2 MB of real stack; with less, parsing ends earlier with the same `Recursion limit` message (it checks `RuntimeHelpers.TryEnsureSufficientExecutionStack`). The default 1 MB thread stack of Windows threads therefore rejects some statements the oracle accepts at extreme depth. Run the parser on a thread with 4 MB or more if that matters.

**Rule for tree consumers.** Name resolution, the planner and the evaluator walk the tree recursively; each must call `RuntimeHelpers.EnsureSufficientExecutionStack` per level (or use an explicit stack) so a tree near the limit cannot overflow the process.

## Tokenizer

`Lexer` is a struct over the string with zero allocation per token (`Token` carries kind, start and length; text is sliced on demand). Notable rules, all checked against the oracle: `/*` as the last two characters is a slash and a star, while a longer unterminated comment runs to the end; vertical tab is blank only inside a run that began with another blank; `#name` is a variable and `#NNN…` raises a deferred `near "#NNN": syntax error`; keywords are case-insensitive via a generated table (`KeywordTable.g.cs`) with the fallback set from `lang_keywords`.

## Verification

- `tools/OneLite.SqlCompare` runs our parser and the pinned `sqlite3` (through `sqlite3_prepare_v2` on the pinned library, error offset included) on the same text and compares accept/reject, message and offset. Modes: `slt` (all sqllogictest statements), `schema` (statement text from real-world and generated databases), `gen` (grammar-driven generation plus token and character mutation, seeded), `check` (probe files), `depth`, `alloc`. Every accepted statement is round-tripped through `SqlWriter`, and every fourth generated statement is a literal-only `SELECT` evaluated as written and in `parenthesize` form.
- A native grammar-action error that comes before our syntax error token is masked, not counted, because SQLite runs actions while parsing (`Checker.EndedBeforeOurError`); two errors on a semantic evaluation count as equal.
- `fuzz/OneLite.Fuzz sql` fuzzes the tokenizer and parser for crashes, hangs and writer-reproduction failures.
- Unit tests: `tests/OneLite.Tests/Sql/`.
