-- OneLite differential repro
-- status: pass
-- class: bug:window-group-concat-range-offset-order
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, group_concat(v) OVER (ORDER BY id RANGE BETWEEN 1 PRECEDING AND 1 PRECEDING), group_concat(v) OVER (ORDER BY id RANGE BETWEEN 1 FOLLOWING AND 1 FOLLOWING) FROM t ORDER BY id
-- detail: in a RANGE frame that need not hold the current row, rows enter before others leave
-- setup
CREATE TABLE t(id INTEGER PRIMARY KEY, v, s)
-- go
INSERT INTO t VALUES(1,'A',NULL)
-- go
INSERT INTO t VALUES(2,'',NULL)
-- go
INSERT INTO t VALUES(3,'255',NULL)
-- go
INSERT INTO t VALUES(4,'B',NULL)
-- go
-- run
SELECT id, group_concat(v) OVER (ORDER BY id RANGE BETWEEN 1 PRECEDING AND 1 PRECEDING), group_concat(v) OVER (ORDER BY id RANGE BETWEEN 1 FOLLOWING AND 1 FOLLOWING) FROM t ORDER BY id;
