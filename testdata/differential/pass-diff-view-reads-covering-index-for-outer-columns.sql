-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT c1, count(*) FROM v0
-- detail: 
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 NUMERIC, c1 REAL)
-- go
CREATE INDEX i0 ON t0(c1)
-- go
INSERT INTO t0 VALUES(20, 1, 3.14159),(39, 2, -65)
-- go
CREATE VIEW v0 AS SELECT id,c0,c1 FROM t0
-- go
-- run
SELECT c1, count(*) FROM v0;
