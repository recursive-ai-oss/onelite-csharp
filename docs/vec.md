# The `vec0` table and the distance kernels

> **Not interchangeable with sqlite-vec.** `vec0` follows sqlite-vec's documented SQL API, but stores vectors in OneLite's own shadow-table layout. A `vec0` table written by OneLite can't be used by sqlite-vec, and one written by sqlite-vec can't be used by OneLite. Without OneLite, `sqlite3` reports "no such module: vec0" for the table and reads every other table normally.

`vec0` is an opt-in virtual table for exact k-nearest-neighbour search. Its SQL surface follows sqlite-vec's documented API (`vec0`, `vec_*` functions, `vec_each`); its storage is OneLite's own. A database that holds a `vec0` table is a standard SQLite file. A `sqlite3` without the module passes `PRAGMA integrity_check`, uses every other table, and fails on the vec table itself with `no such module: vec0` and nothing else.

## SQL surface

- **Table:** `CREATE VIRTUAL TABLE v USING vec0(id integer primary key, e float[768] distance_metric=cosine, tag text partition key, year integer, +note text, chunk_size=256)`.
  - Vector columns: `float[N]`, `int8[N]`, `bit[N]` (`bit` N a multiple of 8). Metrics: `l2` (default), `cosine`, `l1` (int8 and float), `hamming` (bit).
  - Primary key: integer or text, optional. Without one, the rowid is the key.
  - Metadata columns (`integer`, `float`, `text`, `boolean`) can be filtered in a KNN query; `partition key` columns split the index; `+` columns are stored apart and can only be read.
  - `chunk_size` is a multiple of 8 from 8 to 4096; default: about 32 KiB of the widest vector column per chunk, rounded to a multiple of 8 and clamped to 8..1024.
- **Query:** `WHERE e MATCH :query AND k = 10`, or `LIMIT 10` instead of `k`. The result column `distance` is the metric. Constraints on the primary key (`=`, `IN`, ranges) and metadata (`=`, `<`, `<=`, `>`, `>=`, `!=`, `BETWEEN`, `IN`) narrow the candidates. Without `MATCH` the table is a plain scan.
- **Writes:** `INSERT`, `UPDATE` (vector, metadata and auxiliary columns), `DELETE`, `INSERT ... SELECT`.
- **Functions:** `vec_f32`, `vec_int8`, `vec_bit`, `vec_length`, `vec_type`, `vec_to_json`, `vec_add`, `vec_sub`, `vec_normalize`, `vec_slice`, `vec_quantize_binary`, `vec_quantize_int8`, `vec_distance_l2|l1|cosine|hamming`, `vec_version`, `vec_debug`, and the table function `vec_each`. A vector value is a blob with a subtype; a blob with no subtype is read as float32.
- **Errors:** texts follow the reference extension's (for instance `Dimension mismatch for inserted vector for the "e" column. Expected 3 but received 2.`, `UPDATEs on vec0 primary key values are not allowed.`). Checked against the extension's binary by `tests/OneLite.Tests/Vec` and `testdata/vec/scalar_golden.jsonl`.

## Differences from the reference extension

- JSON text for a vector is parsed strictly; the lenient forms the reference accepts are rejected with an error.
- A `MATCH` operand may be a scalar subquery, a bound parameter or a column of an earlier FROM item, as in the reference.
- An `UPDATE` that leaves an int8 or bit vector alone reads it back as an untyped blob; a blob of exactly the column's size is taken as the column's type.
- Updating a partition key column is not supported (delete and insert again).
- The shadow tables are OneLite's, so a file written by the reference extension is not read, and ours is not readable by it.
- Text primary keys live in a key table (slot to text to rowid) that is probed by the key text, not a hash of the reference's.

## Storage layout

All rows are ordinary B-tree rows in shadow tables named `<table>_<suffix>`; they take part in the transaction, journal, WAL and encryption like any table.

| Table | Columns | Meaning |
|---|---|---|
| `_chunks` | `chunk_id integer primary key, size, partitionNN..., validity blob, rowids blob` | One row per chunk of `size` slots. `validity` is a bitmap (bit i of byte i/8: slot i is live); `rowids` is `size` little-endian int64 rowids. |
| `_rowids` | `rowid integer primary key, id, chunk_id, chunk_offset, mdNN...` | Where each vector lives, plus its metadata values. |
| `_vector_chunksNN` | `rowid integer primary key (= chunk_id), vectors blob` | `size * dimension * element bytes`, slot after slot; float32 little endian, int8, or packed bits. One table per vector column. |
| `_auxiliary` | `rowid, value00...` | The `+` columns. |
| `_ids` | `slot integer primary key, id text, vrowid` | Only for text primary keys. |

Chunk fill policy: an insert goes to the newest chunk of its partition if it has a free slot, otherwise to the first chunk with a free slot in that partition, otherwise to a new chunk. A delete clears the validity bit; a chunk with no live slot is removed. A chunk row larger than a page spans overflow pages, which is why every shadow-table write is Pending-safe: a `Put` that returns Pending is repeated alone, because moving the cursor between resumes would abandon the half-done replace.

## Distance kernels

`RecursiveAI.OneLite.Internal.Simd.VectorKernels` has no engine dependencies. float32 vectors accumulate in `double` over 16 fixed partial sums, so a distance is bit-identical on the scalar, `Vector128`, `Vector256` and `Vector512` paths and on x64 and arm64. The path is chosen once from `IsSupported` (`DOTNET_EnableAVX512F=0` / `DOTNET_EnableAVX2=0` force narrower ones). int8 and hamming results are exact integers. A KNN scan allocates nothing per vector or per chunk; ties break by rowid.

## Verified by

`VectorKernelTests` (lane widths agree), `VecScalarTests` (155 golden cases against the reference binary), `VecKnnTests` (random tables against a brute-force double-precision scan, with deletes, updates, partitions, metadata filters and reopen), `VecInteropTests` (shell round trip, 512 to 65536 byte pages, rollback and WAL), `VecAllocationTests`, and the kill campaign `WRITER_EXTRA="vec=1" tools/scripts/engine_kill_campaign.sh`, which judges the shadow tables with plain SQL.
