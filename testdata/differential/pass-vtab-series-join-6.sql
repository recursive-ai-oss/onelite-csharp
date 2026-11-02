-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: series,join
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT t0.id, (SELECT count(*) FROM generate_series(t0.n, t0.m, 2)) FROM t0 ORDER BY t0.id
-- detail: 
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, n INTEGER, m INTEGER);
-- go
INSERT INTO t0 VALUES(1,1,3),(2,2,2),(3,5,4),(4,NULL,2),(5,3,NULL);
-- go
-- run
SELECT t0.id, (SELECT count(*) FROM generate_series(t0.n, t0.m, 2)) FROM t0 ORDER BY t0.id;
