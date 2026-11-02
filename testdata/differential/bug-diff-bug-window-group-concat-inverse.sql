-- OneLite differential repro
-- status: pass
-- class: bug:window-group-concat-inverse
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, group_concat(v, s) OVER (ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW), group_concat(v) OVER (ORDER BY id ROWS BETWEEN CURRENT ROW AND 1 FOLLOWING) FROM t ORDER BY id
-- detail: a value leaving the frame takes its length and the next separator off the front of the text
-- setup
CREATE TABLE t(id INTEGER PRIMARY KEY, v, s)
-- go
INSERT INTO t VALUES(1,'a','-')
-- go
INSERT INTO t VALUES(2,'bb','')
-- go
INSERT INTO t VALUES(3,'','--')
-- go
INSERT INTO t VALUES(4,'ccc','')
-- go
INSERT INTO t VALUES(5,'','')
-- go
INSERT INTO t VALUES(6,'d','xy')
-- go
INSERT INTO t VALUES(7,'e',NULL)
-- go
INSERT INTO t VALUES(8,'f','+')
-- go
INSERT INTO t VALUES(9,NULL,'k')
-- go
INSERT INTO t VALUES(10,'g','kk')
-- go
-- run
SELECT id, group_concat(v, s) OVER (ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW), group_concat(v) OVER (ORDER BY id ROWS BETWEEN CURRENT ROW AND 1 FOLLOWING) FROM t ORDER BY id;
