-- OneLite differential repro
-- status: pass
-- class: bug:window-generated-column-index-unused
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, lag(g0, 1, id) OVER (ROWS CURRENT ROW) FROM t0 ORDER BY id
-- detail: row 0: ours (55, -30); sqlite3 (55, 55)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 UNSIGNED BIG INT, c1 VARCHAR(10), g0 GENERATED ALWAYS AS (c0 * 2) STORED);
-- go
CREATE INDEX i0 ON t0(g0 DESC);
-- go
INSERT INTO t0(id, c0, c1) VALUES(55, -2147483648, '0');
-- go
INSERT INTO t0(id, c0, c1) VALUES(56, -15, 'a%b');
-- go
-- run
SELECT id, lag(g0, 1, id) OVER (ROWS CURRENT ROW) FROM t0 ORDER BY id;
