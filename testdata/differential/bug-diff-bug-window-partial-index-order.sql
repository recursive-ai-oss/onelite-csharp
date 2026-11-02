-- OneLite differential repro
-- status: pass
-- class: bug:window-partial-index-order
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, ntile(3) OVER (w1 ) FROM t0 WINDOW w1 AS (PARTITION BY c2 ORDER BY c1) ORDER BY id
-- detail: row 0: ours (16, 1); sqlite3 (16, 2)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 BLOB, c1 REAL, c2 INTEGER, c3, c4 BOOLEAN, g0 GENERATED ALWAYS AS (c1 * 2) VIRTUAL);
-- go
CREATE INDEX i1 ON t0(c2 DESC);
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(16, x'00000000', 2.5, -2147483648, -1, 2147483647);
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(62, 9223372036854775807, 2.5, -2147483648, NULL, 100.0);
-- go
-- run
SELECT id, ntile(3) OVER (w1 ) FROM t0 WINDOW w1 AS (PARTITION BY c2 ORDER BY c1) ORDER BY id;
