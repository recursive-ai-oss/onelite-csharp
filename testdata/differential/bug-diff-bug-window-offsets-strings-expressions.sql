-- OneLite differential repro
-- status: pass
-- class: bug:window-offsets-strings-expressions
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, sum(v) OVER (ORDER BY id ROWS BETWEEN '1' PRECEDING AND 1+1 FOLLOWING), sum(v) OVER (ORDER BY id RANGE BETWEEN '1.5' PRECEDING AND 0.5 FOLLOWING), lag(v, '1') OVER (ORDER BY id), lead(v, 1+1, -1) OVER (ORDER BY id) FROM t ORDER BY id
-- detail: string and expression offsets
-- setup
CREATE TABLE t(id INTEGER PRIMARY KEY, v, s)
-- go
INSERT INTO t VALUES(1,1,NULL)
-- go
INSERT INTO t VALUES(2,2,NULL)
-- go
INSERT INTO t VALUES(3,3,NULL)
-- go
INSERT INTO t VALUES(4,4,NULL)
-- go
INSERT INTO t VALUES(5,5,NULL)
-- go
-- run
SELECT id, sum(v) OVER (ORDER BY id ROWS BETWEEN '1' PRECEDING AND 1+1 FOLLOWING), sum(v) OVER (ORDER BY id RANGE BETWEEN '1.5' PRECEDING AND 0.5 FOLLOWING), lag(v, '1') OVER (ORDER BY id), lead(v, 1+1, -1) OVER (ORDER BY id) FROM t ORDER BY id;
