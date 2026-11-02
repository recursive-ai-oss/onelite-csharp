-- OneLite differential repro
-- status: pass
-- class: bug:window-lag-real-offset-rounding
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, lag(c1, c2) OVER (PARTITION BY c2 ORDER BY c1) FROM t0 ORDER BY id
-- detail: row 0: ours (9, NULL); sqlite3 (9, '7')
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 REAL, c1 TEXT COLLATE NOCASE, c2 DATETIME, c3 NUMERIC, c4 TEXT COLLATE NOCASE);
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(9, 0.5, 7, 1e-300, 'ABC', 'Hello World');
-- go
-- run
SELECT id, lag(c1, c2) OVER (PARTITION BY c2 ORDER BY c1) FROM t0 ORDER BY id;
