# FTS3 and FTS4

`CREATE VIRTUAL TABLE t USING fts3(...)`, `fts4(...)` and `fts4aux(t)` are built into the core
(`RecursiveAI.OneLite.Extensions.Fts3`). The shadow tables (`t_content`, `t_segments`, `t_segdir`, `t_docsize`, `t_stat`) have the layout
the reference writes (sqlite.org/fts3.html, "Data Structures"): segment b-trees, doclist and position-list varints, prefix indexes, language
id levels. A database is read, written and `integrity-check`ed by both engines.

Tokenizers are shared code in `RecursiveAI.OneLite.Internal.Text` (engine-independent): `simple` is the ASCII tokenizer FTS5 also uses,
`unicode61` (`remove_diacritics`, `tokenchars`, `separators`) is the FTS5 one, `porter` is `Fts3Tokenizers.cs` (FTS3's stemmer differs from
FTS5's in which characters make a token). `icu` is not built into the pinned reference and is not offered.

## What is covered

- Options: `content=` (external table, or `''` for contentless), `compress=`/`uncompress=` (SQL functions, called as `f(?)`), `languageid=`,
  `matchinfo=fts3`, `notindexed=`, `order=desc`, `prefix=`, `tokenize=`. Errors for bad options carry the reference's text.
- Queries: the standard syntax of the pinned build (no parentheses; `OR`, implicit `AND`, `-` for NOT, `NEAR`, `NEAR/n`, `"phrase"`, prefix
  `*`, `col:term`, FTS4 `^` anchor), `docid`/`rowid`, `col MATCH`, `lid = n`.
- Auxiliary functions: `offsets`, `snippet` (all arities), `matchinfo` (`p c n a l s x y b`), `optimize(t)`.
- Special commands: `optimize`, `rebuild`, `integrity-check`, `merge=X,Y`, `automerge=N`.
- Writes: insert, update (including `docid`), delete; the oracle's `integrity-check` and `PRAGMA integrity_check` pass on our files.
- `fts4aux(t)` and `fts4aux(db, t)` (the two-argument form only for a table created in `temp`, as in the reference).

## Behaviour settled by running the pinned shell

- **Operators** `OR` and `NEAR` are recognised only right after white space that follows the previous word or phrase, and only when followed by
  white space, `(`, `)`, a quote or the end. So `alpha, OR beta`, `(a) OR b`, `a OR,b` and `naive NEAR x` under `separators=e` are all words.
- **Expression size**: at most 4095 phrases in a query and 13 operands in a `NEAR` chain; beyond that `FTS expression tree is too large
  (maximum depth 12)`. Query text is lexed in linear time.
- **`^`** anchors the word directly after it (`^ word` does not); before a quote it does nothing; a `^` right before the first word inside the
  quotes anchors the phrase (`"^one two"`). A `-` before a `^` is malformed.
- **`col:"phrase"`** with a space after the colon ignores the column (as the predecessor test showed); see the open differences for the form
  without a space.
- **matchinfo `s`** counts runs of consecutive phrases (a phrase that did not match breaks the run).
- **snippet** (token window `N`, default 15 though the page says -15; `M` fragments of `ceil(N/M)` tokens, `M` = 1..4): window start is
  `floor((first + last + 1 - size) / 2)` clamped to the column; fragments print in the order they were chosen (most new phrases, then most
  hits, first), not in text order; text after the last token is kept when the window reaches the end.
- **`fts4aux`**: rows are one `*` row per term and one per column with hits; a `languageid` constraint is taken by the reference as an
  absolute segment-directory index, so with prefix indexes or other languages around `languageid = 1` reads the first prefix index
  (reproduced); a term that occurs in a column after the second makes the scan fail with `database disk image is malformed` after the rows
  before it (reproduced, including that term bounds narrow the scan and a lone `ORDER BY term` streams).
- **UPDATE** with `docid` set to its current value succeeds (an engine fix made with this module).

## Differences from the reference (documented, not planned)

- **Not resumable.** The module reads and writes its shadow tables through nested statements on the sync driver. On a backend that is
  async-only a cold read fails with `SQLITE_MISUSE` ("backend requires the async driver") instead of suspending; it never hangs or corrupts.
  Over `BlockingOk` backends and the memory and file backends it works under both drivers.
- **Segment layout.** Each statement that changes rows writes one level-0 segment (the reference writes one per transaction); a level is merged
  when it holds 16 segments. `merge=X,Y` and `automerge=N` merge fully instead of a little at a time, so segment counts and what `optimize()`
  merges differ. File validity is unaffected: `integrity-check` passes in both directions.
- **`col:"phrase"` written without a space** returns no rows in the reference (the form with a space ignores the column); we ignore the column in
  both.
- **`matchinfo(t, 'x')`** totals (hits and documents over all rows) of a query with several phrases: the reference does not read a later
  phrase's doclist to its end, so the totals depend on how the file's segments are laid out and, for a `NEAR` group ANDed with other terms,
  on the query. Ours are always the full totals. They agree in most cases (no difference in 400 random cases, one in 301 queries over a
  1,500-document table) and the differential and interop runs compare `x` for one-phrase queries only.
- **`fts4aux` over `order=desc` tables** counts entries per segment without merging a document's older versions (the reference's counts
  rise after a delete until the next merge); stale entries of an external-content table whose content changed before the delete are counted
  by the reference and not by us. `GROUP BY term` over a scan that fails does not stream the rows before the failure.
- **Contentless FTS4**: the reference fails `integrity-check` and `PRAGMA integrity_check` on its own contentless tables; ours answer ok.
- **pragma `table_xinfo`** on a virtual table is a separate engine gap.

## Method

`tools/fts/fts3_diff.py` generates seeded scripts (options, rows, updates, deletes, special commands, every query form, `fts4aux`) and compares
rows and error text per statement with the shell, then runs the shell's `integrity-check` and `PRAGMA integrity_check` on our file and ours on
the shell's file. `tools/fts/fts3_interop.py` builds the same tables in both engines (eight option combinations, four page sizes, UTF-8 and
UTF-16, thousands of documents), checks all four engine/file pairings and then lets each engine modify the other's file.
`tests/OneLite.Tests/Engine/Fts3Tests.cs` runs the comparisons over a fixed corpus. `fuzz/OneLite.Fuzz/Fts3Target.cs` (`fts3-query`,
`fts3-data`) fuzzes the query parser and hostile `t_segments`/`t_segdir`/`t_content`/`t_docsize`/`t_stat` contents. The kill campaign runs
the workload with `WRITER_EXTRA="fts3=1"`.
