-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr,subquery
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT a.c2 IN (SELECT g0 FROM t0) FROM t0 AS a
-- detail: 
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c1, c2 CHAR(3), g0 GENERATED ALWAYS AS (c1 * 2) VIRTUAL);
-- go
CREATE INDEX i2 ON t0(g0 DESC);
-- go
INSERT INTO t0(id, c1, c2) VALUES(7, 7, 'Hello World');
-- go
INSERT INTO t0(id, c1, c2) VALUES(8, NULL, 1);
-- go
-- run
SELECT a.c2 IN (SELECT g0 FROM t0) FROM t0 AS a;
