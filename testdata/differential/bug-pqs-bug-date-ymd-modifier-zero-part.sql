-- OneLite differential repro
-- status: pass
-- class: bug:date-ymd-modifier-zero-part
-- engine: ours
-- families: datetime,expr,function
-- utf16: false
-- oracle: pqs
-- from: t1
-- predicate: date('2460000.5', '-0001-00-00')
-- pivot: 30
-- detail: pivot row id=30 missing from the result of the rectified condition
-- setup
CREATE TABLE t1(id INTEGER PRIMARY KEY, c0 REAL, c1 DECIMAL(5,2), c2 INTEGER, c3 DATETIME);
-- go
INSERT INTO t1(id, c0, c1, c2, c3) VALUES(30, 60.875, 1.5, 1, '10');
-- go
-- run
SELECT CASE WHEN (date('2460000.5', '-0001-00-00')) IS NULL THEN 'n' WHEN (date('2460000.5', '-0001-00-00')) THEN 't' ELSE 'f' END FROM t1 WHERE id = 30;
