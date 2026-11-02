-- OneLite differential repro
-- status: pass
-- class: bug:window-unused-window-definition-order
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, row_number() OVER (ORDER BY c1 DESC) FROM t0 WINDOW w1 AS (PARTITION BY id ORDER BY id COLLATE RTRIM DESC, c0) ORDER BY id
-- detail: row 0: ours (16, 1); sqlite3 (16, 2)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 DOUBLE, c1 DATETIME);
-- go
CREATE UNIQUE INDEX i0 ON t0(c1);
-- go
INSERT INTO t0(id, c0, c1) VALUES(38, '日本', NULL);
-- go
REPLACE INTO t0(id, c0) VALUES(16, -0.0);
-- go
-- run
SELECT id, row_number() OVER (ORDER BY c1 DESC) FROM t0 WINDOW w1 AS (PARTITION BY id ORDER BY id COLLATE RTRIM DESC, c0) ORDER BY id;
