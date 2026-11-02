-- OneLite differential repro
-- status: pass
-- class: bug:r71-fb39f9a11c
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, min(c0) FILTER (WHERE c1 IS NOT NULL) OVER (ROWS BETWEEN 1 FOLLOWING AND UNBOUNDED FOLLOWING EXCLUDE NO OTHERS) FROM t0 ORDER BY id
-- detail: row 0: ours (134, -9223372036854775808); sqlite3 (134, -9.223372036854776E+18r)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0, c1 INT, c2 DOUBLE);
-- go
INSERT INTO t0(id, c0, c1, c2) VALUES(134, 100, 9223372036854775807, 0.1);
-- go
INSERT INTO t0(id, c0, c1, c2) VALUES(135, -9223372036854775809, 255, NULL);
-- go
INSERT INTO t0(id, c0, c1, c2) VALUES(137, -9223372036854775808, -11, -2);
-- go
-- run
SELECT id, min(c0) FILTER (WHERE c1 IS NOT NULL) OVER (ROWS BETWEEN 1 FOLLOWING AND UNBOUNDED FOLLOWING EXCLUDE NO OTHERS) FROM t0 ORDER BY id;
