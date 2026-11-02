-- OneLite differential repro
-- status: pass
-- class: bug:window-nulls-first-desc-index-ties
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, lag(c4) OVER (ORDER BY c2 DESC NULLS FIRST) FROM t1 ORDER BY id
-- detail: row 0: ours (43, NULL); sqlite3 (43, 65536)
-- setup
CREATE TABLE t1(id INTEGER PRIMARY KEY, c0 TEXT, c1 TEXT, c2 NUMERIC UNIQUE, c3 DECIMAL(5,2), c4 NUMERIC);
-- go
INSERT INTO t1(id, c0, c1, c2, c3, c4) VALUES(43, 'Hello World', ('t' || 'a' || 'b' || char(9) || 'h' || 'e' || 'r' || 'e'), NULL, '', NULL);
-- go
INSERT OR REPLACE INTO t1(id, c0, c1, c2, c3, c4) VALUES(44, '2024-02-29 12:30:45', 7, NULL, 28, 65536);
-- go
-- run
SELECT id, lag(c4) OVER (ORDER BY c2 DESC NULLS FIRST) FROM t1 ORDER BY id;
