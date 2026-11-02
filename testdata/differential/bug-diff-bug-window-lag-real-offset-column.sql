-- OneLite differential repro
-- status: pass
-- class: bug:window-lag-real-offset-column
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT id, lag(c1, c0) OVER (PARTITION BY c1 ORDER BY id DESC), nth_value(id, 2.0) OVER (ROWS BETWEEN 1 PRECEDING AND CURRENT ROW EXCLUDE TIES), lag(id, 0, c0) OVER () FROM t0
-- detail: row 0: ours (6, NULL, NULL, 6); sqlite3 (6, 65536, NULL, 6)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 FLOAT, c1 INTEGER);
-- go
INSERT INTO t0(id, c0, c1) VALUES(6, 1e-300, 65536);
-- go
-- run
SELECT id, lag(c1, c0) OVER (PARTITION BY c1 ORDER BY id DESC), nth_value(id, 2.0) OVER (ROWS BETWEEN 1 PRECEDING AND CURRENT ROW EXCLUDE TIES), lag(id, 0, c0) OVER () FROM t0;
