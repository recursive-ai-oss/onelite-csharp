-- OneLite differential repro
-- status: pass
-- class: bug:window-duplicate-order-terms
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, avg(c1) OVER (PARTITION BY c1, c0 ORDER BY id RANGE CURRENT ROW), lead(c0, 2) OVER (ORDER BY c1 DESC, c1 DESC) FROM t0 ORDER BY id DESC LIMIT 3
-- detail: row 0: ours (39, 52.125r, '日本'); sqlite3 (39, 52.125r, 0r)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 DOUBLE, c1 DATETIME);
-- go
CREATE UNIQUE INDEX i0 ON t0(c1);
-- go
INSERT INTO t0(id, c0, c1) VALUES(38, '日本', NULL);
-- go
INSERT INTO t0(id, c0, c1) VALUES(39, 55.875, 52.125);
-- go
REPLACE INTO t0(id, c0) VALUES(16, -0.0);
-- go
-- run
SELECT id, avg(c1) OVER (PARTITION BY c1, c0 ORDER BY id RANGE CURRENT ROW), lead(c0, 2) OVER (ORDER BY c1 DESC, c1 DESC) FROM t0 ORDER BY id DESC LIMIT 3;
