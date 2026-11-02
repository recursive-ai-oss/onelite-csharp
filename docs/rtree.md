# R*Tree and dbstat

Behaviour of `rtree`, `rtree_i32` and `dbstat` as OneLite implements it, with what was settled by observing the pinned shell. Results are
compared with the shell by `tools/OneLite.RTreeDiff` (seeded random scripts, statement by statement, then the shell's own checks on the file
OneLite wrote and OneLite's checks on the file the shell wrote) and by `tests/OneLite.Tests/Engine/RTreeTests.cs`.

## R*Tree

- **Shadow tables** are created in the order `%_rowid`, `%_node`, `%_parent` after the virtual table's own schema row (`rootpage` 0):
  `%_rowid(rowid INTEGER PRIMARY KEY,nodeno,a0,a1,...)` (one `aN` per auxiliary column), `%_node(nodeno INTEGER PRIMARY KEY,data)`,
  `%_parent(nodeno INTEGER PRIMARY KEY,parentnode)`. `%_node` starts with node 1, the root, as `pageSize - 64` zero bytes.
- **Node blob**: bytes 0-1 tree depth (meaningful in the root), bytes 2-3 cell count, then cells of `8 + 8 * dims` bytes: big-endian int64
  rowid (child node number above the leaves) and per dimension `min`, `max` as big-endian float32 (`rtree`) or int32 (`rtree_i32`).
  Observed: a node holds at most 51 cells whatever its size allows (the shell reports a node with more as corrupt); OneLite splits at 52.
- **Columns**: the first column is an INTEGER rowid (NULL assigns `max(rowid)+1`; other values go through the integer conversion, so `'7'` is 7
  and `'x'` is 0); `rowid` named in an INSERT or UPDATE is ignored in favour of the first column. `rtree` coordinates are REAL on the way out,
  `rtree_i32` INTEGER. Column names are the first token of each argument; `+name` marks an auxiliary column and must come last.
- **Coordinates in**: `rtree_i32` takes the low 32 bits of the integer conversion (observed: `1e300` and `9223372036854775807` both fail the
  `min <= max` check against a normal minimum). `rtree` converts to REAL; a value that is not exactly a float32 is moved outward by a relative
  2^-23 (lower bounds down, upper bounds up) and rounded to the nearest float32 (observed against the shell on 0.1, 3.7, 559.936, 592.297
  and 16777217; this is the "0.000012%" of the documentation).
- **Errors** (messages as the shell): `Too few columns for an rtree table`, `Too many columns for an rtree table` (more than 11 coordinate columns
  or 100 columns), `Wrong number of columns for an rtree table` (even number of coordinate columns), `Auxiliary rtree columns must be last`,
  `rtree constraint failed: T.(minCol<=maxCol)` (SQLITE_CONSTRAINT), `UNIQUE constraint failed: T.id` (message compared; the extended code is OneLite's choice, 1555, and has not been compared).
- **MATCH** with a geometry callback is not supported: no `rtree_geometry` or query callbacks are registered, and `x MATCH y` is not
  usable (the shell fails it with `SQL logic error`).
- **Scans**: constraints on coordinate columns (`=`, `<`, `<=`, `>`, `>=`) prune subtrees; an equality on the first column goes through
  `%_rowid`. The engine tests every term again on the returned rows (observed: `a = 0.1` finds nothing in a table holding the rounded value,
  as in the shell), so pruning only needs to keep every row that could match. Row order is the tree order and is not promised.
- **Writes**: the algorithm of Beckmann et al.: ChooseSubtree by least overlap enlargement above the leaves (the 32 best by area enlargement
  when there are more) and least area enlargement elsewhere; one forced reinsertion of the 30% of entries farthest from the node centre per
  level and insertion (closest first); otherwise the margin-sum axis and least-overlap distribution split with a minimum fill of 40%. A delete
  condenses as Guttman's: nodes under a third of capacity are dissolved and their entries reinserted, and a root with one child is lifted.
  The root stays node 1. **The shape of the tree, and so the shadow tables' bytes, differ from the shell's for the same SQL**; both are valid
  and answer every query alike, so `.dump` of two files built by the two engines matches for the table's content but not for `%_node`.
- **Suspension**: a change is planned in memory (reads of nodes, parent links and rowid rows that miss the cache throw an internal exception
  after starting the read, `Update` returns Pending, and the next call plans again from the start with the finished reads remembered), then
  written with one shadow-table write per step, each repeated with the same arguments after Pending. Scans keep their position in a stack of
  nodes and cell indexes, and load a child before moving past its cell.
- **Functions**: `rtreecheck([schema,] table)` returns `ok` or one line per problem (boxes ordered and inside the parent's, leaf cells and
  `%_rowid`, child cells and `%_parent`, row counts); the wording of problem lines is OneLite's own. `rtreedepth(blob)` and
  `rtreenode(ndim, blob)` as in the shell (`%g` coordinates).
- **Not done**: reading and writing at once (the shell fails the second with SQLITE_LOCKED; OneLite plans from a drained scan, so its UPDATE and
  DELETE do not meet that), geometry callbacks.

## dbstat

- Columns `name, path, pageno, pagetype, ncell, payload, unused, mx_payload, pgoffset, pgsize` and hidden `schema, aggregate`; eponymous, with
  `dbstat('schema', aggregate)` arguments; `CREATE VIRTUAL TABLE x USING dbstat(schema)` sets the default schema.
- B-trees in `sqlite_schema` row order, starting with the schema table itself (named `sqlite_schema`). Rows of a B-tree: a page, then for each
  cell its overflow chain (`path` `/001/000+000002`), then (for interior pages) the child; the right-most child last. The path of a child is the
  parent's path plus the 3-digit hexadecimal cell index and `/`.
- `payload` is the bytes of cell payload held on the page (the local part only), `mx_payload` the largest total payload length of a cell
  on the page, `unused` the gap before the cell content area, the freeblocks and the fragmented bytes. An overflow page reports the payload bytes
  it holds and `usable - 4 - payload` unused. Aggregate mode sums these per B-tree; `pageno` becomes the page count, `pgsize` pages times page size.
- Observed quirk reproduced: an overflow page's `pgoffset` is the offset of the page listed just before it.
- A scan reads all pages of the schema when it starts; unlike the R*Tree it is not suspendable (it waits on I/O inside `Filter`).
