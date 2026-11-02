-- OneLite differential repro
-- status: pass
-- class: bug:window-group-concat-null-frame
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, group_concat(v) OVER (ORDER BY id ROWS BETWEEN CURRENT ROW AND 1 FOLLOWING) FROM t ORDER BY id
-- detail: NULL only when no value is left in the frame
-- setup
CREATE TABLE t(id INTEGER PRIMARY KEY, v, s)
-- go
INSERT INTO t VALUES(1,NULL,NULL)
-- go
INSERT INTO t VALUES(2,'x',NULL)
-- go
INSERT INTO t VALUES(3,'',NULL)
-- go
INSERT INTO t VALUES(4,'y',NULL)
-- go
INSERT INTO t VALUES(5,NULL,NULL)
-- go
-- run
SELECT id, group_concat(v) OVER (ORDER BY id ROWS BETWEEN CURRENT ROW AND 1 FOLLOWING) FROM t ORDER BY id;
