-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT c2, max(c0), min(c0) FROM t0 GROUP BY c2
-- detail: 
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 INTEGER, c2 TEXT COLLATE NOCASE)
-- go
INSERT INTO t0 VALUES(59, NULL,'ABC'),(60,100,'abc'),(61,50,'ABC')
-- go
-- run
SELECT c2, max(c0), min(c0) FROM t0 GROUP BY c2;
