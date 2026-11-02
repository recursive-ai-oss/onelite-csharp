-- OneLite differential repro
-- status: expected-fail
-- class: order-by-index-scan-before-error
-- engine: ours
-- families: affinity,expr,json,string,window
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT c3, (c2 -> '$.zz') FROM t0 WHERE (c3 != c1)
-- detail: row 0: ours (0.5r, NULL); sqlite3 (10r, NULL)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0, c1 BOOLEAN UNIQUE, c2 REAL, c3 REAL) WITHOUT ROWID;
-- go
CREATE INDEX i0 ON t0(c1 COLLATE RTRIM) WHERE c1 IS NOT NULL;
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(3, 'abc', 2147483647, 79.875, 0.5);
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(47, x'41', 10, 'ABC', 95.25);
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(48, 10, 2.5, 1e15, 10);
-- go
-- run
SELECT c3, (c2 -> '$.zz') FROM t0 WHERE (c3 != c1);
