-- OneLite differential repro
-- status: pass
-- class: bug:window-range-offset-keeps-current-group
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, count(c3) OVER (PARTITION BY c0 ORDER BY c2 RANGE BETWEEN CURRENT ROW AND 0.5 FOLLOWING EXCLUDE TIES) FROM t2 WINDOW w1 AS (ORDER BY id COLLATE RTRIM) ORDER BY id
-- detail: row 0: ours (88, 0); sqlite3 (88, 1)
-- setup
CREATE TABLE t2(id INTEGER PRIMARY KEY, c0, c1 NUMERIC, c2 DECIMAL(5,2), c3 UNSIGNED BIG INT);
-- go
INSERT INTO t2(id, c0, c1, c2, c3) VALUES(88, '_x', 2147483647, 9007199254740993, 1);
-- go
-- run
SELECT id, count(c3) OVER (PARTITION BY c0 ORDER BY c2 RANGE BETWEEN CURRENT ROW AND 0.5 FOLLOWING EXCLUDE TIES) FROM t2 WINDOW w1 AS (ORDER BY id COLLATE RTRIM) ORDER BY id;
