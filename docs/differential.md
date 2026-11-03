# The differential fuzzer

`tools/OneLite.Differential` generates a database and a batch of queries from a seed, runs them on OneLite and on the pinned
`sqlite3` shell, and reports every difference. It also applies three metamorphic oracles (Rigger and Su) that need no second
engine. Everything is deterministic: the same seed, families and oracles give the same statements byte for byte.

```
differential run     --seed S [--count CASES | --duration 25m] [--start-case K] [--families a,b] [--oracles diff,pqs,norec,tlp]
                     [--jobs N] [--out DIR] [--tmp DIR] [--minimize-budget N] [--minimize-limit N]
differential gen     --seed S --case K [--families ...] [--oracles ...]    print the statements of one case
differential probe   [--db FILE] SQL...                                    run statements on both engines, side by side
differential replay  [DIR_OR_FILE...]                                      replay repro files (default testdata/differential)
differential families                                                      the family names
tools/scripts/differential.sh SEED START DURATION OUT [run options]        build, then one chunk of a campaign
```

The oracles are the ones `tools/scripts/fetch_oracles.py` installs (`$ONELITE_HOME`, default `~/onelite`): the `sqlite3` shell
for every expected result, and the matching native library, used only to build the database each case starts from.

## A case

A case is one seed index. It builds a database file through the pinned library (random tables with mixed declared types,
`WITHOUT ROWID` and `STRICT` tables, generated columns, indexes of every kind, rows chosen from edge values, DML, upserts,
`RETURNING`, `ALTER TABLE`, views, a trigger, and occasionally a UTF-16 database), then generates the checks and runs them against
that file on both engines. Only setup statements the reference accepts are kept, so a case's setup always replays.

OneLite opens the file read-only today and runs only queries. When the engine learns a family, nothing in the tool changes: the
checks that used to report `unsupported` start returning verdicts. Setup always goes through the reference.

With `--setup onelite` the engine builds the case file itself from the same statements, and the checks run on that file. The result is
then compared with a file the pinned `sqlite3` shell builds from the same statements (not with the generator's file: that one comes from
the native library, whose compile options differ, for example `LIKE` never matches a BLOB there). Statements that fail are skipped on both
sides, as the shell does, and the `.dump` of the two files must be equal; every difference is reported (up to 400 per run).

## Oracles

| Oracle | Check | Failure means |
|---|---|---|
| `diff` | One query on both engines. Compares column names, every value's storage class and bytes, row order where the query fixes it (an `ORDER BY` ending in a unique key; otherwise the rows as a multiset), and error code and message text. | The engines differ. |
| `norec` | `SELECT count(*) FROM t WHERE p` against the number of rows for which `SELECT CASE WHEN p THEN 1 ELSE 0 END FROM t` gives 1. | The optimized path and plain evaluation disagree on one engine. |
| `tlp` | The rows of `SELECT * FROM t` against the union of the `WHERE p`, `WHERE NOT p` and `WHERE p IS NULL` partitions, as multisets. | One engine loses or duplicates rows. |
| `pqs` | Pick a row, ask the reference whether `p` is true, false or NULL for it, rewrite `p` so it is true for that row, and require the row in the result. | An engine drops a row its own condition should keep. |

`norec`, `tlp` and `pqs` run on both engines and name the engine that failed (`ours`, `sqlite3` or `both`); a failure on the
reference means the oracle or the generated SQL is wrong, which is a tool bug. If one engine runs a query the other rejects, the
check reports that difference whatever the oracle. A metamorphic check where both engines error is `inconclusive`.

## Families

Each check carries the families of what it uses. `--families` restricts a run.

- **Shapes:** `select`, `orderlimit`, `distinct`, `aggregate`, `join`, `subquery`, `cte`, `window`, `compound`, `view`, `series`
  (`generate_series`), `pragma`, and `inseek` (`x IN (list)` / `x IN (SELECT ...)` on the rowid, indexed and plain columns; only generated when named, so other seeds are unchanged).
- **Expressions:** `affinity`, `collation`, `cast`, `string`, `math`, `datetime`, `function` (core functions), `json`, `like`.
  `expr` marks every check.
- **Setup:** `ddl`, `dml`, `upsert`, `returning`, `trigger`, `generated`.

With a list that names no shape, only `select` and `orderlimit` are generated; a list that names no expression family keeps all of
them; a list that names no setup family keeps all of those too. `--families window,cte` therefore gives window and CTE queries with
every kind of expression inside.

## Reading the summary

A run prints, and writes to `DIR/summary-SEED-START.json`:

- the case range, the time, and a **fingerprint**: a hash of all generated statements, which is the same for the same seed and
  cases on any machine and any `--jobs`;
- checks by oracle and by family, each split into
  - `pass`: identical, or the metamorphic property held;
  - `known`: a difference the engine documentation lists on purpose (below);
  - `bugs`: a OneLite defect this tool found, matched by a named rule, with a minimized repro filed;
  - `UNEXPL`: a difference nobody has classified. This is what a campaign is judged by; the exit status is 1 when it is not zero;
  - `unsupp`: OneLite answered `unsupported: ...`. This measures coverage, not correctness, and the most common reasons are listed;
  - `incon`: a metamorphic check that could not conclude;
  - `refErr`: queries the reference itself rejected, a measure of how much of the generated SQL is valid (a few percent is
    expected: overflow, malformed JSON, bad date modifiers are deliberate);
- `resume with: --seed S --start-case N`, to continue in another chunk. Chunks are independent, so a long campaign is a series of
  jobs of at most thirty minutes.

Documented classes (`docs/engine.md`, "Known differences"): `runtime-error-order`, `printf-alternate-form-2`,
`real-to-text-digits`, `timediff-range`, `utf16-blob-text`. Open defects are rules in `Classifier.Bugs` named `bug:...`. A new
unexplained difference is triaged into one of: a OneLite defect (add a rule, commit its repro), a documented class, or a tool
bug (fix the generator or the comparison).

## Repros and the corpus

Every difference that is not a duplicate is minimized: setup statements are dropped (chunks, then one by one), and the query
or predicate is reduced as a tree (clauses removed, expressions replaced by a child or a constant) while the same failure on
the same engine with the same class persists. A minimization that would make the order of rows or the choice under `LIMIT`
unspecified keeps the tie-breaking key. The result is a self-contained file:

```
-- OneLite differential repro
-- status: expected-fail            pass | expected-fail
-- class: bug:is-true-false         unexplained, a documented class, or bug:NAME
-- engine: ours
-- oracle: diff                     plus query/ordered, or from/predicate[/pivot]
-- setup
CREATE TABLE ...;
-- go
-- run
SELECT ...;
```

Everything except the setup statements and the queries after `-- run` is a comment, so the file also runs as a script in the
`sqlite3` shell. `testdata/differential/` holds the committed corpus: hand-written `pass-*` files, `documented-*` files, and one
`bug-*` file per open defect. `OneLite.Differential.Tests` replays all of them. A `pass` file must pass; an `expected-fail` file
must still fail with its class, and one that starts passing fails the test with "now passes: set status pass", so fixing an engine
bug is visible. To file a new one, copy it from the run's `repros/` directory and add its rule.

## Method notes and limits

- Row order is compared only where the query fixes it. Generated `LIMIT`/`OFFSET` always sit behind an `ORDER BY` ending in a
  unique key.
- Sums and averages of REAL values depend on the order rows are added; OneLite and SQLite may legitimately differ in the last
  digit when scan order differs. None has shown up yet; if one does it is a documented class.
- The shell prints text only up to its first NUL and prints negative zero as `0.0`; the comparison folds both, as the query
  comparison tool does.
- Generated text never contains `--` (a unary minus is followed by a space) and no newline, because the shell batch is
  line-based.
- There is no per-statement timeout: a statement that hangs ends the chunk at the job limit.
- `random()`, `randomblob()`, `now`, `localtime` and `load_extension` are never generated.
- The generator is a grammar written from the lang_expr, lang_select, lang_corefunc, lang_mathfunc, lang_datefunc, lang_aggfunc,
  windowfunctions and json pages and the Rigger-Su papers; expected results come only from the pinned shell.
