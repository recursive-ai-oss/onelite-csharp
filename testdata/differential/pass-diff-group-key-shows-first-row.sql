-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT (0 - c4), min(c3) FROM t1 GROUP BY 1
-- detail: 
-- setup
CREATE TABLE t1(id INTEGER PRIMARY KEY, c1 INTEGER UNIQUE, c3 BLOB, c4 VARCHAR(10));
-- go
INSERT INTO t1(id, c1, c3, c4) VALUES(34, 11, '12:30:45', 10);
-- go
INSERT INTO t1(id, c1, c3, c4) VALUES(80, 2, 0, '1e1');
-- go
-- run
SELECT (0 - c4), min(c3) FROM t1 GROUP BY 1;
