# B-tree and record codec

Normative notes for `RecursiveAI.OneLite.Storage.Record` and `RecursiveAI.OneLite.Storage.BTree`. Sources: sqlite.org `fileformat2` (§1.6 B-tree pages, §1.7 pointer maps, §2.1 records, §2.2 serial types), `datatype3`, `limits`, and Comer, "The Ubiquitous B-Tree" (table trees are B+-trees keyed by rowid, index trees are B-trees whose interior cells are entries too).

## Record codec (`Storage.Record`)

* `Varint`: `Read(span, out value)` returns the byte count, or 0 when the span ends early. At most 9 bytes; the ninth contributes all 8 bits (fileformat2 §1.5). `Write`, `Length`.
* `SerialType`: `ContentSize(t)` (-1 for the reserved types 10 and 11), `ForInteger(v)` (the smallest of types 1-6, or 8/9 for 0/1), `ReadInt`, `TryGetClass`.
* `Mem`: a struct value (`Kind` of `StorageClass` Null/Integral/Real/Text/Blob, `IntValue`, `RealValue`, and `Buffer`/`Offset`/`Length` for text and blobs, which reference caller bytes and are never copied). Constructors `Mem.FromInt/FromReal/FromText(string, enc)/FromBlob(byte[])`, `Mem.Null`.
* `RecordView`: reusable parse of one record over a caller-owned `byte[]`. `Parse(owner, offset, length)` validates the header size, every serial type and that each body fits, and returns false on a malformed record (the cursor turns that into corruption). Accessors `ColumnCount`, `SerialTypeOf`, `ClassOf`, `GetInt64`, `GetDouble`, `GetBytes` (a span over the page, no copy), `Load(i, ref Mem)`. After the first parse grew its arrays, parsing and reading integers, reals and byte spans allocate nothing.
* `RecordWriter`: `TypeOf`, `Size`, `Write`, `ToArray(ReadOnlySpan<Mem>)`.
* Text encodings: `TextEncoding` Utf8/Utf16le/Utf16be (header offset 56). `TextCodec` converts; `Collations.Binary/NoCase/RTrim(enc)` compare the stored bytes; `KeyInfo(enc, collations[, descending])` describes an index (for a secondary index the column count includes the trailing rowid). `RecordCompare.Compare(keyInfo, view, key, prefixResult)` follows `datatype3` ordering (NULL < numbers < text < blob; integers and reals compare exactly).

## Page source (`IBtPageSource`)

The B-tree is written against this narrow interface, not against a pager type. `ImagePageSource` is the in-memory implementation used by tests and the fuzz target; the pager adapts to it.

* `GetPage(pgno, out PageHandle)` pins (page 0, beyond `PageCount` and the lock-byte page are corruption). `Release`. `MakeWritable(page)` before every modification batch.
* `Reserve(allocations, frees)` and `ReservePtrmap(pgno)` are the **prefetch** calls: after they return `Ok`, `Allocate()`, `Free()` and `PtrmapPut()` do not suspend. `Allocate()` returns a zeroed, writable, pinned page. This is the suspend-point rule "reserve, then effect".
* Every call that may need I/O returns `BtStatus` `Ok`, `Pending` or `Error` (`LastError`). After `Pending` the caller completes the I/O and repeats **the same call with the same arguments**; nothing observable changed.
* `LargestRootPage` is the auto-vacuum header field at offset 52.

## Cursor API (`BTree`, `BtCursor`)

`BTree.Table(ctx, root)` / `BTree.Index(ctx, root, keyInfo)`; `tree.OpenCursor()`. A cursor owns its pinned root-to-leaf path (`MaxDepth` 20; deeper is corruption) and has a `State`: `Invalid`, `Valid`, `Saved`, `Fault`.

| Call | Notes |
|---|---|
| `MoveToRowid(rowid, out cmp)`, `MoveToKey(key, prefixResult, out cmp)` | seek; `cmp` 0 exact, <0 landed on a smaller entry, >0 on a larger one; empty tree gives `Invalid`, -1. |
| `First()`, `Last()`, `Next()`, `Previous()` | one suspension per leaf page entered. |
| `Rowid`, `PayloadSize`, `LoadPayload()`, `Payload`, `ParseRecord(view)` | `LoadPayload` follows the overflow chain (one `GetPage` per page) into a per-cursor scratch buffer; without overflow `Payload` and `ParseRecord` view the page directly. |
| `Insert(rowid, payload)`, `Insert(record, key)`, `Delete()` | rowid tables / index trees. After either, the cursor is `Saved` on the key so `Next`/`Previous` continue without skipping or repeating. |
| `Restore()` | re-seeks a `Saved` cursor. Other cursors on the same tree are saved before a write. |
| `BtAdmin.CreateTree(ctx, intKey, out root)`, `TreeEraser(tree, destroy).Step()` | create, clear and destroy a tree. |
| `BtIntegrity.Check(trees)` | structural check: page types, cell bounds, free-space accounting, key order within a page, child and overflow pointers, pointer-map entries, freelist; problems in `Problems`. |

Re-entry (spec §1, I2): progress lives in named fields of the cursor (`_op`, the pinned path, the binary-search bounds, the overflow cursor); a repeated call resumes there. Writes repeat their reserve phase against a pin list, and the effect phase (cell insert, balance, root change) never suspends. Balance gathers up to three siblings, redistributes, and replaces the dividers; root deepening and shrinking are handled in the same phase. Counters `Splits`, `Merges`, `Rebalanced`, `Deepened`, `Shrunk` expose balance events.

Errors: `OneLiteException` with code SQLITE_CORRUPT (11; message "database disk image is malformed"; `Detail` is "page N: what was wrong"), built with `OneLiteException.Corrupt(page, detail)`. A cell that claims more overflow pages than the database has is corrupt before anything is reserved. Every size and offset read from a page is checked against the page, varints and serial types are validated, loops are cut by depth and chain-length limits, and nothing allocates in proportion to an unchecked length.

## On the real pager (`PagerPageSource`, `StorageEngine`)

`PagerPageSource` implements `IBtPageSource` over the pager: `GetPage/Release/MakeWritable/Allocate/Free/PtrmapPut` map onto the pager's calls with pooled page handles (no allocation per page). `Free` also writes the free-page pointer-map entry (type 2). The two additions are `Reserve` (pin the freelist trunks and pointer-map pages that `n` allocations and `m` frees will touch) and `ReservePtrmap`. `EndEffect` is called when an insert or delete has finished its effect phase, so the pager forgets what the reserve calls set aside and did not use: without it a transaction of many inserts that never split would pile up spare frames.

`StorageEngine` is the glue: a pager, its page source, the `BtContext` (made on the first `BeginRead`, from the file's text encoding) and `Run(op)` / `RunAsync(op)`, which repeat a restartable call after performing the pager's outstanding I/O. It also reads and writes the schema cookie.

Reserve sizes (the effect phase must find everything it needs already cached and set aside, or the pager reports a suspension inside it, which is a bug): per level that may balance, `siblings + 2` allocations (the new pages a rebalance can need, plus one spare), 3 at the root (deepening); the pointer-map page of every page a write allocates, frees or moves; one free per overflow page of a deleted cell. Over-reserving is harmless because unused spare frames stay in the pager's free list.

Auto-vacuum FULL: `BtRelocator` implements the pager's `IPageRelocator`, which commit phase 0 calls until the file is as short as the freelist allows. For the page to move it finds the parent through the pointer map, saves every open cursor (a moved page invalidates pinned paths), copies the page to the free slot, rewrites the one pointer that names it (child pointer, right-most pointer, overflow link, or the root entry in the schema), and fixes the pointer-map entries of the moved page's children. Each step is idempotent, so a suspension in the middle repeats from its start. Header offset 52 (largest root page) is kept by `BtAdmin.CreateTree`; offset 64 (incremental-vacuum flag) is written by the pager when the file is created. Both match what the pinned `sqlite3` writes for `PRAGMA auto_vacuum=1|2` followed by `CREATE TABLE`.

## What the pager must provide

`GetPage/Release/MakeWritable/Allocate/Free/PtrmapPut` map onto the pager's `Get/Release/MakeWritable/Allocate/Free/PtrmapPut`, plus `Reserve` and `ReservePtrmap` as above. The B-tree keeps pointer-map entries correct on every cell, child and overflow move; moving pages to shorten the file is `BtRelocator`'s job, called by the pager at commit.

## Notes

* **Auto-vacuum root pages: done.** In an auto-vacuum database every root page lies below every other page and header offset 52 names the largest root (fileformat2 §1.3.11, §1.7). `CreateTree` takes exactly page `largest + 1` (pointer-map pages skipped). A page that is free there is taken off the freelist (pages are taken until it comes, the others go back; the reserve is as large as the freelist count, capped at the file size, so this is linear in the freelist). A page in use there (B-tree page or overflow page, by its pointer-map entry) is copied to a newly allocated page, its parent's reference is patched, the pointer-map entries of what it points to are rewritten, and open cursors are saved first. A new file grows by one page. `TreeEraser(destroy)` on the largest root frees it and lowers offset 52 to the previous non-pointer-map page. On a root below the largest, the dropped root's page is emptied and kept, the largest root's content is copied into it (children and first overflow pages get the new parent in the pointer map), the largest root's old page is freed, and offset 52 drops. The result is `TreeEraser.MovedFrom` / `MovedTo` (0 when nothing moved): **the caller (the future DDL layer) must rewrite the `rootpage` of the schema row of the table or index whose root was `MovedFrom`**, and reopen its trees and cursors; cursors open on either root are invalidated. Tested against the shell (`PagerDropRecreateTests`: integrity_check ok and `.dump` equal for auto_vacuum 1 and 2, with yield injection).
