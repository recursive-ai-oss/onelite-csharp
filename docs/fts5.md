# FTS5

`CREATE VIRTUAL TABLE t USING fts5(...)` is built into the core (`RecursiveAI.OneLite.Extensions.Fts5`). Its shadow tables
(`t_data`, `t_idx`, `t_content`, `t_docsize`, `t_config`) have the layout the reference writes, so a database is read and
modified by both engines. Tokenizers (`unicode61`, `ascii`, `porter`, `trigram`) are internal code in
`RecursiveAI.OneLite.Internal.Text`, free of engine dependencies.

## What is covered

- Table options: `content`, `content_rowid`, `contentless`, `columnsize`, `detail` (`full`, `column`, `none`), `prefix`, `tokenize`,
  `UNINDEXED` columns, hidden `rank`.
- Queries: phrases, `+`, `AND`/`OR`/`NOT`, parentheses, column filters, `^`, prefix `*`, `NEAR(...)`, table-valued form
  `t('query')`, `col MATCH 'query'`.
- Auxiliary functions: `bm25` (k1 1.2, b 0.75), `highlight`, `snippet`, also on a scan with no `MATCH` (bm25 is 0).
- Special commands: `rebuild`, `optimize`, `merge`, `automerge`, `usermerge`, `crisismerge`, `pgsz`, `hashsize`, `secure-delete`, `deletemerge`,
  `insttoken`, `rank`, `integrity-check`, `delete`, `delete-all`. Invalid values fail with the bare `SQL logic error`.
- `fts5vocab` (`row`, `col`, `instance`).
- Index format: structure record, segments, doclists, position lists, `t_idx` keys, `t_docsize` varints, `t_config` — written and read both ways.

## Behaviour settled by running the pinned shell

- **Unicode tables** are Unicode 6.1 (the pinned build): unassigned code points are token characters except U+FFFE/U+FFFF; the marks in the
  diacritic set (U+0300–0304, 0306–030C, 030F, 0311, 031B, 0323–0328, 032D–032E, 0330–0331) are token characters even with
  `remove_diacritics 0`.
- **Porter** works on UTF-8 bytes: digits and every byte of a multi-byte character are consonants, and nothing is skipped.
- **detail=column/none**: `phrase queries are not supported (detail!=full)` for a multi-token phrase or `^`, `NEAR queries are not supported
  (detail!=full)` for `NEAR` with two or more phrases, `column queries are not supported (detail=none)` for a column filter (raised after the
  filtered primary parsed) and for `col MATCH` (raised after the whole query parsed). Instances for `bm25`/`highlight`/`snippet` come from
  tokenizing the row text, for every column including unindexed ones, and only for phrases that took part in the match.
  `fts5vocab` counts are NULL without positions.
- **Empty phrases** (a one-letter word under `trigram`) are dropped from an implicit AND, from `OR`, `NEAR` and the right of `NOT`; an explicit
  `AND` with one matches nothing.
- **Syntax errors** report the token the reference reports: `x (y)` names `x`, `{a b` reports the unknown column before the missing brace,
  an unterminated string is `unterminated string`, `* a` is `unknown special query: a`, `NEAR` is a word unless a `(` follows.
- **Contentless tables**: the `'delete'` command also drops the `t_docsize` row; without `t_docsize` (`columnsize=0`) a scan is
  `t: table does not support scanning` and `bm25` runs with a column size of -1.
- **snippet** (window of `n` tokens, `n` clamped to 1..64): the best anchor is the instance whose window holds the most instances; the window
  starts at `anchor - (n - span)/2` clamped to the text, except that it starts at the latest *sentence start* before the first match that
  still reaches it, and at the text start when the first match lies in the first `n` tokens. A sentence start is a token whose preceding
  gap ends in `.` or `:` followed by white space (`. `, `: `, ` . `; not `.` alone, `?`, `!`, `;`). A phrase that began before the window is
  not marked. A snippet that reaches the end of the text keeps the text after its last token; one starting at token 0 starts at the text
  start. Unindexed columns are returned whole. For `snippet(t, -1, ...)` the column is the one whose best anchor scores most, where an
  instance with no more than `n` tokens after it to the end of its text scores 0; ties keep the first column.

## Known differences (open)

- `snippet` when several matches lie near sentence starts and the span of the first match equals the window (a few percent of random
  differential cases), and the window around trigram phrases of different lengths.
- `snippet()` of an UNINDEXED column in a `detail=column` or `detail=none` table: for some rows the reference shell fails with "database disk image is malformed" (SQLITE_CORRUPT) on files that pass its own integrity check; we return the snippet text. Seen twice in 1,450 random cases (seed 21).
- `bm25`/`highlight`/`snippet` quirks that are matched on purpose: when the left side of a NOT matches no row at all, the right side keeps its first row's instances (they show in that row if it matches another way); a segment recorded with first and last page 0 and no page 0 is empty; a combining mark continues a token but never starts one; a contentless table without size records reads each indexed column's size as -1.
- The NOT quirk below is matched when the left side is empty because its terms are absent, or because the phrase never occurs; in one of 373 random cases (seed 41, a prefix on the right of the NOT, a two-token phrase on the left) the reference does not keep the right side's instances, and `highlight` and `bm25` differ by one marked token or 1e-6.
- Where the NOT quirk above applies, `highlight` and `snippet` show the kept instances but `bm25` does not count them (two of 647 random cases, seed 61); in one of 1,450 (seed 21, a phrase that also occurs in an `AND` branch) the reference does count them.
- Query nesting: the reference parser stack holds 100 entries and reports "fts5: parser stack overflow". Ours counts one entry for a bare parenthesis and three for one that follows an operator or a column filter, and agrees on plain, `AND`/`OR`/`NOT` and column-filter chains; the exact limit differs by a level or so in chains that mix several pending operators.
- `contentless_delete`, `tokendata`, `locale` and `fts5_locale()` are not implemented. UTF-8 and UTF-16 databases both work.
- `bm25(t)` inside an aggregate such as `sum(bm25(t))` reports "unable to use function bm25 in the requested context".

## Method

`tools/fts/fts_diff.py` generates seeded scripts (random options, corpus rows, updates, deletes, special commands, every query form,
auxiliary functions, vocab tables) and compares rows and error text per statement with the shell, then runs the shell's `integrity-check` and
`PRAGMA integrity_check` on the file OneLite wrote and OneLite's `integrity-check` on the shell's file.
`tests/OneLite.Tests/Engine/Fts5Tests.cs` runs the same comparisons over a fixed corpus.
