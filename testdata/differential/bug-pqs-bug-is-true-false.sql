-- OneLite differential repro
-- status: pass
-- class: bug:is-true-false
-- engine: ours
-- families: expr,function
-- utf16: false
-- oracle: pqs
-- from: t0
-- predicate: (NULL IS TRUE)
-- pivot: 44
-- detail: pivot row id=44 missing from the result of the rectified condition
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 REAL, c1 REAL);
-- go
INSERT INTO t0(id, c0, c1) VALUES(44, -78.75, -0.0);
-- go
-- run
SELECT CASE WHEN ((NULL IS TRUE)) IS NULL THEN 'n' WHEN ((NULL IS TRUE)) THEN 't' ELSE 'f' END FROM t0 WHERE id = 44;
