-- OneLite differential repro
-- status: pass
-- class: window-sum-cancellation
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT DISTINCT id, sum(c2)  OVER (ORDER BY c0 ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING EXCLUDE NO OTHERS), first_value(c2) OVER (PARTITION BY c1 ) FROM t0 ORDER BY id DESC LIMIT 3
-- detail: row 0: ours (32, 0r, 10); sqlite3 (32, 10, 10)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 DECIMAL(5,2) UNIQUE, c1 REAL, c2 BOOLEAN);
-- go
INSERT INTO t0(id, c0, c1, c2) VALUES(8, -0.0, -0.625, -9223372036854775809);
-- go
INSERT INTO t0(id, c0, c1, c2) VALUES(9, 100, x'', 1e300);
-- go
INSERT INTO t0(id, c0, c1, c2) VALUES(32, ('a' || 'b' || char(0) || 'c' || 'd'), 3.14159, 10);
-- go
-- run
SELECT DISTINCT id, sum(c2)  OVER (ORDER BY c0 ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING EXCLUDE NO OTHERS), first_value(c2) OVER (PARTITION BY c1 ) FROM t0 ORDER BY id DESC LIMIT 3;
