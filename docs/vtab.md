# Virtual tables

Normative notes for the virtual-table interface (`RecursiveAI.OneLite.VirtualTables`), built on SQLite's documented virtual-table
model (`vtab`, `c3ref/index_info`, `c3ref/c_index_constraint_eq`). Results follow the pinned SQLite release; plans are OneLite's own.

## Two kinds of module

| | user module | built-in module |
|---|---|---|
| contract | `IVirtualTableModule` / `IVirtualTable` / `IVirtualTableCursor`, synchronous | `BuiltinModule` / `VTab` / `VCursor`, resumable |
| registered | `Connection.RegisterModule(name, module)` | static table `VirtualModules` (`rtree`, `rtree_i32`, `dbstat`) |
| state | whatever the module keeps; it may run SQL on `VirtualTableContext.Connection` (sync driver) | shadow tables in the database file, read and written through `ShadowTree` |
| transactions | `ITransactionalVirtualTable` hooks | none needed: the pager gives atomicity, rollback and savepoints |
| creation | `Create` runs inside `CREATE VIRTUAL TABLE` (`VtCreate`) | the compiler emits the shadow tables from `BuiltinModule.ShadowTables` as ordinary DDL in the same program |

Built-in modules follow the suspend-point rules of the engine: every method that can touch a page returns `ModStatus.Ok` or
`ModStatus.Pending`; on `Pending` the VM does not move `pc` and later calls the same method with the same arguments, so a module keeps
progress in fields, never in locals. Errors are thrown as `OneLiteException`.

## Life of a table

- `CREATE VIRTUAL TABLE [IF NOT EXISTS] [schema.]name USING module(arg, ...)`. Text of the statement is stored in `sqlite_schema`
  (type `table`, `rootpage` 0) first, then the module's shadow tables follow in module order, each a normal `table` row. Errors:
  `no such module: X`; `table N already exists`.
- A catalog entry becomes a connected `VTab` lazily (first name resolution). `Connect` must call `DeclareTable("CREATE TABLE x(...)")`
  once; a column whose declared type ends in `HIDDEN` is hidden (not in `*`, not in the default `INSERT` column list, settable as a
  table-function argument or by an equality). Not declaring fails with `vtable constructor did not declare schema: N`.
- The instance is dropped when the connection's schema is invalidated (DDL, schema cookie change, rollback), except while it still owes the
  current transaction a Commit or Rollback.
- `DROP TABLE` calls `Destroy` on the table, deletes the table's own row and (built-in) its shadow tables' rows and frees their pages.
- The built-in table-valued functions `generate_series`, `json_each`, `json_tree`, `jsonb_each` and `jsonb_tree` are eponymous-only modules
  (`Engine/VirtualTables/FunctionModules.cs`) on this interface: their arguments and `WHERE json = x` style equalities on the hidden columns reach
  `Filter` through `BestIndex` (`IndexNumber` bit k = hidden column k), and `CREATE VIRTUAL TABLE ... USING generate_series` fails with
  `no such module`. A user module registered with `IsEponymous` works the same way: `FROM f(1, 2)` fills the first hidden columns.
- An *eponymous* module (`IsEponymous` / `BuiltinModule.Eponymous`, e.g. `dbstat`) can be named in FROM without `CREATE VIRTUAL TABLE`,
  with its hidden columns as table-function arguments.

## Planning: `BestIndex`

For each FROM item the planner collects the WHERE (and, for inner joins, ON) terms of the form `column OP expr`, with the column on
the left (a term written `5 < x` is offered as `x > 5`), and calls `BestIndex` once at the item's place in the loop nest.

- `Constraints[i]`: `Column` (declared position; -1 for `rowid`), `Op` (`ConstraintOp`, the documented numeric codes: EQ 2, GT 4, LE 8,
  LT 16, GE 32, MATCH 64, LIKE 65, GLOB 66, REGEXP 67, NE 68, ISNOT 69, ISNOTNULL 70, ISNULL 71, IS 72), `Usable`: true when `expr`
  reads only constants, bound parameters, columns of outer queries, subqueries whose correlated columns come from items placed before this one,
  and FROM items placed before this one (`WHERE v MATCH (SELECT q FROM t)`, `ON r.id = t.rid`). `BETWEEN` (as `>=` and `<=`), `IN` lists and `LIMIT` are offered to modules that accept extended constraints (`vec0` does).
- Arguments of a table-valued call (`FROM t(a, b)`) are offered as equalities on the first hidden columns, always usable.
- Outputs: `ConstraintUsage[i].ArgvIndex` (0 = unused; else 1-based, unique, no gaps, usable constraints only) and `Omit` (the engine does
  not test the term again; otherwise it still tests it on every returned row, so a plan can only change speed, never the answer, unless `Omit`
  is set); `IndexNumber`, `IndexString`, `EstimatedCost` (default 5e98), `EstimatedRows` (25), `ScanIsUnique`.
  A malformed answer fails the statement with `xBestIndex malfunction`. Throwing `ResultCode.Constraint` fails with `no query solution`.
- `OrderBy` / `OrderByConsumed`: for a SELECT over one virtual table (no join, grouping, aggregate, window or DISTINCT), the ORDER BY terms are
  offered when every one is a plain column of the table (or `rowid`) without COLLATE or NULLS FIRST/LAST. If the module sets
  `OrderByConsumed` no sorter runs and the module's order is the result order, ties included; otherwise the engine sorts as usual. The
  planner asks twice (once to decide about the sorter, once when planning the loop) and a module must give the same answer; a different one
  fails with `xBestIndex malfunction`. The built-in modules (R*Tree, dbstat, vec) are offered the order and do not consume it: the pinned
  shell sorts their results too (checked for dbstat).
- **Join order.** The planner asks a virtual table in the nest for its plan with what the items before it make usable. A plan that takes an
  argument ranks like an indexed equality (50), one that returns a single row (`ScanIsUnique` or `EstimatedRows <= 1`) like a rowid lookup (100),
  and a module that throws (a required constraint is not usable yet) is placed later. Table-valued calls and modules with hidden columns wait
  for the items their arguments read.

## Scanning

`VtFilter` (opens the cursor on first use, passes `IndexNumber`, `IndexString` and the arguments, in `ArgvIndex` order, as `Mem` values
exactly as evaluated: no affinity applied), then `Eof` is checked; `VNext` calls `Next` then checks `Eof`; `VColumn`/`VRowid` read the
current row. A `Column` call for a hidden column reads what the module returns. Cursors are disposed when the statement ends or is reset.

## Writing

`INSERT`, `UPDATE` and `DELETE` on a table whose module is writable (`IWritableVirtualTable`; built-in: `IsWritable`) read the rows they
change first (a drained scan, as for views), then call `Update` once per row with the documented `xUpdate` argument layout:
`[oldRowid, newRowid, col0, col1, ...]` (all columns, hidden included), a delete passes `[oldRowid]`; an insert has `oldRowid` NULL and
`newRowid` NULL unless `rowid` was named. `changes()` counts each call; an insert sets `last_insert_rowid()` to the returned rowid. A table
that is not writable fails with `table N may not be modified`. `RETURNING`, upserts and `UPDATE ... FROM` on a virtual table are unsupported.

## Transactions and savepoints (user modules)

A module that implements `ITransactionalVirtualTable` gets `Begin` before its first write in a transaction, then at commit `Sync` (an
exception rolls the transaction back) and `Commit`, or on rollback `Rollback`. `ISavepointVirtualTable` also gets `Savepoint(n)`,
`Release(n)` and `RollbackTo(n)` with `n` the 1-based depth of the SQL savepoint. A failing statement inside an explicit transaction does not
call `RollbackTo` (the statement's own undo is the module's concern). Built-in modules need none of this: their pages are in the pager.

## Shadow tables

Shadow tables are ordinary tables: queryable and, as in the reference, writable by SQL (which can corrupt the module's structure).
`ShadowTree` is the built-in modules' access to one: a rowid B-tree cursor on `BTree.Table` of the table's current root (re-resolved from the
catalog at the start of every scan or update, because auto-vacuum can move roots).

## R*Tree (`Extensions/RTree`)

`rtree`: coordinates are float32 (lower bounds rounded down, upper bounds rounded up to the next float32), the first column an INTEGER
rowid; `rtree_i32`: int32 coordinates. 1 to 5 dimensions (3 to 11 columns), then `+aux` columns (stored in `_rowid` columns `a0`, `a1`, ...;
at most 100 columns in all). Shadow tables, created in this order: `%_rowid(rowid INTEGER PRIMARY KEY,nodeno,a0,...)`,
`%_node(nodeno INTEGER PRIMARY KEY,data)`, `%_parent(nodeno INTEGER PRIMARY KEY,parentnode)`; `%_node` starts with node 1 (the root)
zero-filled. A node's `data` is `pageSize - 64` bytes: bytes 0-1 the tree depth (root only, big-endian), bytes 2-3 the cell count, then
cells of `8 + 4 * 2 * dims` bytes: rowid (or child node number) big-endian int64, then `min,max` per dimension as big-endian float32 or
int32. See `docs/rtree.md` for the algorithm and the observed behaviour.

## dbstat (`Engine/VirtualTables/DbStat.cs`)

Eponymous `dbstat` with the documented columns `name, path, pageno, pagetype, ncell, payload, unused, mx_payload, pgoffset, pgsize` and hidden
`schema`, `aggregate`; results equal the pinned shell's. See `docs/rtree.md` ("dbstat").
