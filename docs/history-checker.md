# The history checker

`tools/OneLite.History` records transactional histories from concurrent clients and checks them against isolation levels. It is the proof tool for `BEGIN CONCURRENT` and a regression check for plain SQLite-style locking.

## Method

The checker follows Kingsbury and Alvaro, *Elle: Inferring Isolation Anomalies from Experimental Observations*, with Adya's anomaly classes (*Weak Consistency*, 1999), Berenson et al. (*A Critique of ANSI SQL Isolation Levels*, 1995) and Fekete et al. (*Making Snapshot Isolation Serializable*, 2005).

**Workloads.** Transactions of one to a few operations on a small moving window of keys. Written values are unique per key, which lets every observed value name the transaction that wrote it.

- *list-append:* `append(k, v)` and `read(k)` returning the whole list. Every read of a key is a prefix of one version order, so the order is recovered from the longest read.
- *register:* `write(k, v)` with `v != 0`, and `read(k)` returning the value (0 is the initial nil). Reads reveal no order, so order is inferred only from a transaction's own behaviour: its writes in program order, and writes-follow-reads (a write after a read of the same key is a later version). `--realtime-versions` additionally orders writers by real time; use it only on systems that claim strict serializability.

**Outcomes.** `ok` (committed), `fail` (definitely not committed; its writes must never be seen) and `info` (unknown; its writes may be seen, its reads are not trusted, and it never completes for real-time purposes).

**Graph.** Nodes are `ok` and `info` transactions. Edges: `ww` (consecutive versions), `wr` (the reader saw the writer's version), `rw` (the reader did not see the next version), and `rt` (one completed before the other began, kept sparse: a completed transaction leaves the frontier once a later-invoked completed one covers it).

**Anomalies.** Not graph-based: internal inconsistency, non-repeatable read, garbage read, incompatible order, duplicate elements, G1a (aborted read), G1b (intermediate read), and lost update (two transactions read the same version of a key and both wrote it). Graph-based, each reported with a shortest cycle found by breadth-first search from seed edges inside strongly connected components:

| Class | Cycle |
|---|---|
| G0 | `ww` edges only |
| G1c | `ww` and `wr`, at least one `wr` |
| G-single | exactly one `rw`, the rest `ww`/`wr` |
| G-nonadjacent | two or more `rw`, none adjacent around the cycle |
| G2-item | two or more `rw` (write skew is the two-transaction case) |
| *-realtime | cycles through at least one `rt` edge, named by their `rw` count |

**Levels.**

| Level | Forbids |
|---|---|
| read-committed | internal, garbage read, incompatible order, duplicate elements, G0, G1a, G1b, G1c |
| snapshot-isolation | the above and non-repeatable read, G-single, G-nonadjacent, lost update |
| serializable | the above and G2-item |
| strict-serializable | the above and every `*-realtime` class |

Snapshot isolation is checked as plain SI: start-time ordering ("strong" SI) is not checked.

## Limits

- Soundness depends on the inferred version order. A violation that leaves no trace in any read (an acknowledged append nobody ever reads) is invisible; the runner ends every history with a read of all keys to make that unlikely.
- Registers with blind writes give a partial version order, so some register anomalies need `--realtime-versions` to appear.
- Cycle searches are bounded (`MaxSeeds`, default 2000 seed edges per class). When the bound is hit the report says counts are lower bounds. Non-adjacent cycles that the search can only close through a repeated transaction are not reported.
- Real-time checks use one monotonic clock; clients on one machine share it.

## Running

```
onelite-history run --client memory|sqlite|sqlite-snapshot|sqlite-novalidate --lib <libsqlite3> --db <file> \
  --model list-append|register --exec threads|processes --workers 8 --txns 10000 --level strict-serializable
onelite-history check <history.jsonl> --level serializable [--json report.json]
```

`sqlite` runs plain SQLite transactions (`--begin immediate|deferred`, `--journal wal|delete`) through the native library. `sqlite-snapshot` buffers writes over a snapshot and rejects a key that changed before commit (snapshot isolation). `sqlite-novalidate` commits the buffer without validation (lost updates). Both shims exist to show the checker tells the levels apart. New engines plug in by implementing `IHistoryClient`.

History files are JSON lines: a header `{"history":"list-append"}` then one transaction per line `{"id":7,"p":2,"t0":100,"t1":250,"o":"ok","m":[["a",3,41],["r",3,[41]]]}`; times are nanoseconds.
