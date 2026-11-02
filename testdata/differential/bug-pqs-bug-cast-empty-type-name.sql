-- OneLite differential repro
-- status: pass
-- class: bug:cast-empty-type-name
-- engine: ours
-- families: affinity,cast,datetime,expr
-- utf16: false
-- oracle: pqs
-- from: t0
-- predicate: (1 NOT BETWEEN NULL AND CAST(0 AS ))
-- pivot: 89
-- detail: pivot row id=89 missing from the result of the rectified condition
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 FLOAT, c1 REAL);
-- go
INSERT INTO t0(id, c0, c1) VALUES(89, -17.125, 2.5);
-- go
-- run
SELECT CASE WHEN ((1 NOT BETWEEN NULL AND CAST(0 AS ))) IS NULL THEN 'n' WHEN ((1 NOT BETWEEN NULL AND CAST(0 AS ))) THEN 't' ELSE 'f' END FROM t0 WHERE id = 89;
