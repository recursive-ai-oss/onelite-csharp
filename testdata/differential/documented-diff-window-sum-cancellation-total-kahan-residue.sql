-- OneLite differential repro
-- status: pass
-- class: window-sum-cancellation
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, min(c1) FILTER (WHERE c2 IS NOT NULL) OVER (ROWS BETWEEN CURRENT ROW AND 1.0 FOLLOWING EXCLUDE CURRENT ROW), sum(c0)  OVER (ORDER BY c3 RANGE BETWEEN CURRENT ROW AND 3 FOLLOWING EXCLUDE NO OTHERS), lag(c0, 1) OVER () FROM t0 ORDER BY id
-- detail: row 1: ours (39, NULL, 1r, x'FF00AB'); sqlite3 (39, NULL, 1, x'FF00AB')
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 BLOB, c1 DATETIME, c2 REAL, c3 NUMERIC);
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(38, x'ff00ab', NULL, '3.0', 2147483647);
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(39, 1, 7, 9223372036854775808, '日本');
-- go
-- run
SELECT id, min(c1) FILTER (WHERE c2 IS NOT NULL) OVER (ROWS BETWEEN CURRENT ROW AND 1.0 FOLLOWING EXCLUDE CURRENT ROW), sum(c0)  OVER (ORDER BY c3 RANGE BETWEEN CURRENT ROW AND 3 FOLLOWING EXCLUDE NO OTHERS), lag(c0, 1) OVER () FROM t0 ORDER BY id;
