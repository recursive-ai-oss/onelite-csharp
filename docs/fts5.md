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
- **snippet** (window of `n` tokens, `n` clamped to 1..64). Found by running the shell on texts of filler tokens; every rule below has a
  regression case in `Fts5Tests`.
  - *Candidates.* Every instance of a phrase proposes up to two windows. Instances are listed by first token, then phrase number; "the next
    instance" is the next one that starts after this one.
    - The *plain* window is placed around the instance: it starts at `anchor - (n - span)/2` clamped to the text, where the span runs from
      the instance to the end of the last instance listed that starts within `n` tokens of it (a phrase that starts inside the window counts
      with its whole length). It scores like the window that starts at the instance.
    - The *sentence* window starts at the latest sentence start at or before the instance (the text start is one), when that start is within
      `n` tokens of the instance. A sentence start is a token whose preceding gap ends in `.` or `:` followed by white space (`. `, `: `,
      ` . `; not `.` alone, `?`, `!`, `;`). A start *at* the instance is not used when it is the last instance, nor when another sentence
      starts between the instance and the next one (the next instance's own token included).
  - *Score.* 1000 per distinct phrase with an instance starting in the `n` tokens of the window, plus 1 per such instance; a sentence window
    adds a bonus larger than the instances an `n`-token window can hold (100 here), and the window at the text start adds more (200; at
    least 6 more in a window of 8). The best score wins and the first candidate wins ties. A column whose whole text fits in `n` tokens is
    its own window and gets no bonus.
  - *Output.* A phrase that began before the window is not marked. A snippet that reaches the end of the text keeps the text after its last
    token; one starting at token 0 starts at the text start. Unindexed columns are returned whole; with a `detail` below full their matches are found by tokenizing the text and marked in the
    whole text, and such a column takes part in the choice for `snippet(t, -1, ...)`, where the column with the best score wins and ties keep
    the first column.

## Known differences (open)

- `snippet()` of an UNINDEXED column in a `detail=column` or `detail=none` table, when that column's own text contains a token the query matches (found by tokenizing the text): for many such texts (every one seen has its first match after the first token) the reference shell fails with "database disk image is malformed" (SQLITE_CORRUPT) on files that pass its own integrity check; for the others it marks the matches in the whole text, which is what we do in every case. Seen in 1 to 4 of every 130 random cases per seed.
- `snippet(t, -1, ...)` in a `detail=column` or `detail=none` table with an UNINDEXED column that holds a match: in about 1 of 1,500 random cases (seed 61, cases 1981 and 3962 with `fts_diff.py --seed 61 --from N --cases 1`) the reference picks another column than the scoring above does (here a short unindexed column with two matches loses to an indexed column with one). The rule for how an unindexed column's matches count in that choice is not found; cases where it wins on its first token agree.
- `bm25` rounded to 8 decimals can differ in the last digit on arm64 (1 of 4,488 random cases, seed 61 case 3309; scores of the size 1e-6 from a very common term).
- `bm25`/`highlight`/`snippet` quirks that are matched on purpose: when the left side of a NOT has no row in which its terms even meet (a term or prefix that is absent, a phrase or `NEAR` whose terms never share a row), the right side stays on its first row, and the instances of that row's phrases still show in `highlight` and `snippet`, and count in `bm25`, if the row matches another way (a branch of an `OR`, or the top of the query). It does not happen when the NOT is a direct child of an `AND` (below an `OR` that is below an `AND` it does), nor when the left terms share a row but never form the phrase. A segment recorded with first and last page 0 and no page 0 is empty; a combining mark continues a token but never starts one; a contentless table without size records reads each indexed column's size as -1.
- Query nesting: the reference parser stack holds 100 entries and reports "fts5: parser stack overflow". Ours counts one entry for a bare parenthesis and three for one that follows an operator or a column filter, and agrees on plain, `AND`/`OR`/`NOT` and column-filter chains; the exact limit differs by a level or so in chains that mix several pending operators.
- `contentless_delete`, `tokendata`, `locale` and `fts5_locale()` are not implemented. UTF-8 and UTF-16 databases both work.
- `bm25(t)` inside an aggregate such as `sum(bm25(t))` reports "unable to use function bm25 in the requested context".

## Method

`tools/fts/fts_diff.py` generates seeded scripts (random options, corpus rows, updates, deletes, special commands, every query form,
auxiliary functions, vocab tables) and compares rows and error text per statement with the shell, then runs the shell's `integrity-check` and
`PRAGMA integrity_check` on the file OneLite wrote and OneLite's `integrity-check` on the shell's file.
`tests/OneLite.Tests/Engine/Fts5Tests.cs` runs the same comparisons over a fixed corpus.
