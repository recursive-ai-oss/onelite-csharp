-- OneLite differential repro
-- status: pass
-- class: bug:r71-03cae1f043
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, min(c2r) OVER (PARTITION BY c2r, c3 ORDER BY c0 COLLATE BINARY DESC RANGE BETWEEN 0.5 PRECEDING AND 0.5 FOLLOWING EXCLUDE NO OTHERS), percent_rank() OVER (ORDER BY c1 DESC), group_concat(c2r, '|') OVER (ORDER BY id DESC GROUPS BETWEEN 1 PRECEDING AND CURRENT ROW EXCLUDE NO OTHERS), c1 FROM t0 ORDER BY id
-- detail: row 0: ours (267, '[1,2,3]', 0r, '[1,2,3]', 'xyz'); sqlite3 (267, '[1,2,3]', 0r, '|[1,2,3]', 'xyz')
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 BOOLEAN, c1 DATETIME, c2 CHAR(3) COLLATE RTRIM, c3 DATETIME);
-- go
INSERT OR REPLACE INTO t0(id, c0, c1, c2, c3) VALUES(267, 0, 'xyz', '[1,2,3]', 1);
-- go
INSERT OR IGNORE INTO t0(id, c0, c1, c2, c3) VALUES(268, 'true', 'true', '', ' 12 ');
-- go
INSERT OR REPLACE INTO t0(id, c0, c1, c2, c3) VALUES(317, 2147483647, '-5', '0', 10);
-- go
ALTER TABLE t0 RENAME COLUMN c2 TO c2r;
-- go
-- run
SELECT id, min(c2r) OVER (PARTITION BY c2r, c3 ORDER BY c0 COLLATE BINARY DESC RANGE BETWEEN 0.5 PRECEDING AND 0.5 FOLLOWING EXCLUDE NO OTHERS), percent_rank() OVER (ORDER BY c1 DESC), group_concat(c2r, '|') OVER (ORDER BY id DESC GROUPS BETWEEN 1 PRECEDING AND CURRENT ROW EXCLUDE NO OTHERS), c1 FROM t0 ORDER BY id;
