-- OneLite differential repro
-- status: pass
-- class: bug:window-nth-value-exclude-lazy-check
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, count(c3) OVER (ROWS BETWEEN NULL PRECEDING AND CURRENT ROW), nth_value(id, 0) OVER (ROWS BETWEEN CURRENT ROW AND CURRENT ROW EXCLUDE CURRENT ROW), rank() OVER () FROM t0 ORDER BY id
-- detail: error differs: ours run error (1): second argument to nth_value must be a positive integer; sqlite3 run error (1): frame starting offset must be a non-negative integer
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 INT, c1 INTEGER, c2 NUMERIC, c3 TEXT COLLATE BINARY);
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(51, 32, 1, 1e18, NULL);
-- go
-- run
SELECT id, count(c3) OVER (ROWS BETWEEN NULL PRECEDING AND CURRENT ROW), nth_value(id, 0) OVER (ROWS BETWEEN CURRENT ROW AND CURRENT ROW EXCLUDE CURRENT ROW), rank() OVER () FROM t0 ORDER BY id;
