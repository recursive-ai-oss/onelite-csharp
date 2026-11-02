-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: series,join
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT value FROM generate_series WHERE start = 3 AND stop = 7 AND step = 2 ORDER BY value
-- detail: 
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, n INTEGER, m INTEGER);
-- go
INSERT INTO t0 VALUES(1,1,3),(2,2,2),(3,5,4),(4,NULL,2),(5,3,NULL);
-- go
-- run
SELECT value FROM generate_series WHERE start = 3 AND stop = 7 AND step = 2 ORDER BY value;
