-- OneLite differential repro
-- status: pass
-- class: bug:log-base-one
-- engine: ours
-- families: cast,collation,expr,math,string
-- utf16: false
-- oracle: pqs
-- from: t0
-- predicate: log(0.1, 1)
-- pivot: 57
-- detail: pivot row id=57 missing from the result of the rectified condition
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 DECIMAL(5,2), c1 BLOB, c2 NUMERIC, c3 TEXT);
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(57, 2147483647, x'00000000', NULL, '3.0');
-- go
-- run
SELECT CASE WHEN (log(0.1, 1)) IS NULL THEN 'n' WHEN (log(0.1, 1)) THEN 't' ELSE 'f' END FROM t0 WHERE id = 57;
