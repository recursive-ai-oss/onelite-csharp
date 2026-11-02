-- OneLite differential repro
-- status: pass
-- class: bug:r71-f5d590661d
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT DISTINCT id, sum(c1)  OVER (ORDER BY c0 GROUPS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING EXCLUDE NO OTHERS), group_concat(c0, '|') OVER (ORDER BY id, c3 COLLATE NOCASE DESC NULLS LAST, c0 DESC RANGE BETWEEN CURRENT ROW AND CURRENT ROW) FROM t0 ORDER BY id
-- detail: row 0: ours (10, 1r, '�'); sqlite3 (10, 1, '�')
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0, c1 INT, c2 DECIMAL(5,2), c3 BOOLEAN, c4 REAL);
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(10, x'e9', 1, 9223372036854775807, '2460000.5', NULL);
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(23, x'41', x'41', 2147483647, '10', -75.375);
-- go
REPLACE INTO t0(id, c0) VALUES(33, x'ff00ab');
-- go
-- run
SELECT DISTINCT id, sum(c1)  OVER (ORDER BY c0 GROUPS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING EXCLUDE NO OTHERS), group_concat(c0, '|') OVER (ORDER BY id, c3 COLLATE NOCASE DESC NULLS LAST, c0 DESC RANGE BETWEEN CURRENT ROW AND CURRENT ROW) FROM t0 ORDER BY id;
