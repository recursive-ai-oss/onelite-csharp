-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: series,join
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT a.value, b.value FROM generate_series(1,3) AS a, generate_series(a.value, 3) AS b ORDER BY a.value DESC, b.value
-- detail: 
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, n INTEGER, m INTEGER);
-- go
INSERT INTO t0 VALUES(1,1,3),(2,2,2),(3,5,4),(4,NULL,2),(5,3,NULL);
-- go
-- run
SELECT a.value, b.value FROM generate_series(1,3) AS a, generate_series(a.value, 3) AS b ORDER BY a.value DESC, b.value;
