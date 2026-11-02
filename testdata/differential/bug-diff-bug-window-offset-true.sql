-- OneLite differential repro
-- status: pass
-- class: bug:window-offset-true
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, count(*) OVER (ORDER BY id ROWS BETWEEN true PRECEDING AND true FOLLOWING), sum(v) OVER (ORDER BY id RANGE BETWEEN false PRECEDING AND true FOLLOWING) FROM t ORDER BY id
-- detail: TRUE and FALSE are 1 and 0 in a frame offset
-- setup
CREATE TABLE t(id INTEGER PRIMARY KEY, v, s)
-- go
INSERT INTO t VALUES(1,1,NULL)
-- go
INSERT INTO t VALUES(2,2,NULL)
-- go
INSERT INTO t VALUES(3,3,NULL)
-- go
-- run
SELECT id, count(*) OVER (ORDER BY id ROWS BETWEEN true PRECEDING AND true FOLLOWING), sum(v) OVER (ORDER BY id RANGE BETWEEN false PRECEDING AND true FOLLOWING) FROM t ORDER BY id;
