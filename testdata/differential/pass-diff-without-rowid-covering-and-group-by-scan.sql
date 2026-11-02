-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT min(id, c0) FROM t0 GROUP BY NULL
-- detail: 
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 DOUBLE, c1 INTEGER);
-- go
CREATE INDEX i2 ON t0(c0);
-- go
INSERT INTO t0(id, c0, c1) VALUES(4, -9223372036854775809, 65536);
-- go
INSERT INTO t0(id, c0, c1) VALUES(18, -1e300, 2.5);
-- go
-- run
SELECT min(id, c0) FROM t0 GROUP BY NULL;
