# Readable plans

`Connection.Explain(sql, analyze)`, `Connection.ExplainAsync` and `Statement.Plan` return a `QueryPlan`: a tree of `PlanNode`s with a text rendering
(`Render()`) and a structured form (`Root`, `Children`, `Kind`, `Table`, `Index`, `Detail`, `Reason`, `Rejected`, `Runs`, `EstimatedRows`,
`ActualRows`, `Loops`). The `EXPLAIN` and `EXPLAIN QUERY PLAN` statements are separate and not covered here. Plans are not part of the compatibility promise and their wording may change.

## The plan is the program's structure

The compiler records each step as it generates the program: the access-path object that drives a loop is the same object the loop is emitted from, the
index candidates are the ones the planner scored, and subqueries, materializations, sorts and aggregation are noted where their code is made. Nothing
re-derives a choice afterwards, so the plan cannot disagree with the program. Compiling a statement without asking for a plan builds no recorder; every
hook is a null check.

## Shape

The root is the statement (`SELECT`, `INSERT`, `UPDATE` or `DELETE`; statements without table access, such as schema changes, pragmas and transaction
control, have no plan and `Explain` returns null). A node's children are the steps made inside its loop, in the order they run:

* `Scan` and `Search` read one table, index or materialized result. A join nests: the inner table is the child of the outer table's node, and the join
  order is the nesting order. `Detail` shows how, for example `USING INDEX orders_user (user_id=u.id)` or `USING INTEGER PRIMARY KEY (id>10 AND id<=20)`.
  `Reason` says in words which terms of the WHERE clause the access path answers, why a covering index was read instead of the table, or that an
  `ORDER BY` or `GROUP BY` is satisfied by its order. `Rejected` lists the other indexes the planner scored and why each was not used.
* `Materialize` (a subquery in FROM, a view or a common table expression), `RecursiveCte`, `TableFunction`, `Subquery` (scalar, `EXISTS`, `IN`),
  `Compound` with an `Arm` per SELECT. `Runs` says whether a subquery or materialization runs `once` or `per outer row`. A subquery sits under the node whose loop it
  was compiled in, even when it runs only once.
* After the loops: `Window`, `Aggregate` (`GROUP BY` keys; whether groups arrive in key order or go through a sorter), `Distinct`, `Sort` (`ORDER BY`).

## Estimates

Estimates are the planner's guess, not statistics: `sqlite_stat1` is not read. A table is assumed to hold about a million rows; an equality on
`k` columns of an index leaves 10 / k rows (one row for the full key of a unique index or a rowid); each range bound divides by 4 (16 for both bounds).
Nodes that are not table access carry none.

## Actual rows

`Connection.Prepare(sql, collectActualRows: true)` compiles one counter instruction at the start of each table loop and one at the top of its body; a
statement prepared otherwise carries none, so the running loop is unchanged (the allocation tests show 0 bytes per step either way; the counting op is an
array increment). `ActualRows` is the number of rows the loop yielded in all (before the rest of the WHERE clause filters them), `Loops` the number of
times the loop was started; for an inner table of a join, the totals over all outer rows. The counters accumulate over executions of the same
statement object. `Explain(sql, analyze: true)` prepares with counting, runs the statement with no values bound, and returns the plan; a statement that
changes the database runs inside a savepoint that is rolled back.
