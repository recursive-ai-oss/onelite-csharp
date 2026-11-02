-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT count(*) FILTER (WHERE (id = 255)), min(c1, id) FROM t0
-- detail: 
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 VARCHAR(10) COLLATE NOCASE, c1 REAL UNIQUE);
-- go
INSERT INTO t0(id, c0, c1) VALUES(53, '2460000.5', 1e300);
-- go
INSERT INTO t0(id, c0, c1) VALUES(57, 'ABC', 2.5);
-- go
-- run
SELECT count(*) FILTER (WHERE (id = 255)), min(c1, id) FROM t0;
