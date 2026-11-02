-- OneLite differential repro
-- status: pass
-- class: bug:r71-7e10fb1a64
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, min(c0) FILTER (WHERE c3 IS NOT NULL) OVER (PARTITION BY c0 ORDER BY c2 NULLS LAST RANGE BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING EXCLUDE NO OTHERS) FROM t0 ORDER BY id
-- detail: row 1: ours (20, 'ABC'); sqlite3 (20, 'abc')
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 TEXT COLLATE NOCASE, c1 CHAR(3), c2 BOOLEAN, c3);
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(9, 'ABC', ' 12 ', NULL, 1);
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(20, 'abc', NULL, 'hello world', NULL);
-- go
UPDATE t0 SET c3 = 'abd' WHERE id % 3 = 2 RETURNING id, c3;
-- go
-- run
SELECT id, min(c0) FILTER (WHERE c3 IS NOT NULL) OVER (PARTITION BY c0 ORDER BY c2 NULLS LAST RANGE BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING EXCLUDE NO OTHERS) FROM t0 ORDER BY id;
