-- OneLite differential repro
-- status: pass
-- class: bug:window-explicit-exclude-no-others-stage
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, count(*) OVER (ORDER BY v, id ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING), group_concat(id) OVER (RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW), total(id) OVER (RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW EXCLUDE NO OTHERS) FROM t ORDER BY id
-- detail: an explicit EXCLUDE NO OTHERS is a window of its own
-- setup
CREATE TABLE t(id INTEGER PRIMARY KEY, v, s)
-- go
INSERT INTO t VALUES(1,3,NULL)
-- go
INSERT INTO t VALUES(2,1,NULL)
-- go
INSERT INTO t VALUES(3,2,NULL)
-- go
INSERT INTO t VALUES(4,NULL,NULL)
-- go
INSERT INTO t VALUES(5,1,NULL)
-- go
-- run
SELECT id, count(*) OVER (ORDER BY v, id ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING), group_concat(id) OVER (RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW), total(id) OVER (RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW EXCLUDE NO OTHERS) FROM t ORDER BY id;
