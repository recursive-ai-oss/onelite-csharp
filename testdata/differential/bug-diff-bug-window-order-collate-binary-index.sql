-- OneLite differential repro
-- status: pass
-- class: bug:window-order-collate-binary-index
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, avg(c1) OVER (ORDER BY id COLLATE BINARY DESC NULLS LAST RANGE CURRENT ROW), lag(c4) OVER (w1 ), lag(c3, 1) OVER w1 FROM t0 WINDOW w1 AS (ORDER BY c1 COLLATE BINARY) ORDER BY id
-- detail: row 0: ours (4, NULL, NULL, NULL); sqlite3 (4, NULL, -22.125r, -2147483648)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 REAL, c1 BLOB UNIQUE, c2 NUMERIC, c3 UNSIGNED BIG INT, c4 DATETIME);
-- go
CREATE INDEX i0 ON t0(c1 DESC);
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(78, -9223372036854775809, NULL, 9223372036854775808, -2147483648, -22.125);
-- go
INSERT INTO t0(id, c4) VALUES(4, 9223372036854775808) ON CONFLICT(id) DO UPDATE SET c4 = excluded.c4;
-- go
-- run
SELECT id, avg(c1) OVER (ORDER BY id COLLATE BINARY DESC NULLS LAST RANGE CURRENT ROW), lag(c4) OVER (w1 ), lag(c3, 1) OVER w1 FROM t0 WINDOW w1 AS (ORDER BY c1 COLLATE BINARY) ORDER BY id;
