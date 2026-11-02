-- OneLite differential repro
-- status: pass
-- class: bug:date-chained-modifiers-before-year-zero
-- engine: ours
-- families: datetime,expr
-- utf16: false
-- oracle: pqs
-- from: t0
-- predicate: date(0, 'start of month', '+1 year')
-- pivot: 5
-- detail: pivot row id=5 missing from the result of the rectified condition
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 BLOB UNIQUE, c1 INT, c2 TEXT, c3 DATETIME, c4 BOOLEAN);
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(5, -1e300, -2147483648, '3.0', 10, 100.0);
-- go
-- run
SELECT CASE WHEN (date(0, 'start of month', '+1 year')) IS NULL THEN 'n' WHEN (date(0, 'start of month', '+1 year')) THEN 't' ELSE 'f' END FROM t0 WHERE id = 5;
