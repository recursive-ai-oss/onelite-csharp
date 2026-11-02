-- OneLite differential repro
-- status: pass
-- class: bug:window-first-value-range-end-preceding
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, first_value(id) OVER (ORDER BY id DESC NULLS LAST RANGE BETWEEN UNBOUNDED PRECEDING AND 3 PRECEDING) FROM t1 ORDER BY id
-- detail: row 0: ours (145, 146); sqlite3 (145, NULL)
-- setup
CREATE TABLE t1(id INTEGER PRIMARY KEY, c0 TEXT COLLATE RTRIM, c1 DOUBLE, c2 DOUBLE, c3 DECIMAL(5,2));
-- go
INSERT INTO t1(id, c0, c1, c2, c3) VALUES(145, 'true', 2147483647, -1e300, 0.1);
-- go
INSERT INTO t1(id, c0, c1, c2, c3) VALUES(146, '+7', NULL, 9007199254740993, 7);
-- go
-- run
SELECT id, first_value(id) OVER (ORDER BY id DESC NULLS LAST RANGE BETWEEN UNBOUNDED PRECEDING AND 3 PRECEDING) FROM t1 ORDER BY id;
