-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT min(c3) FROM t1 GROUP BY 1
-- detail: 
-- setup
CREATE TABLE t1(id INTEGER PRIMARY KEY, c3 BLOB);
-- go
-- run
SELECT min(c3) FROM t1 GROUP BY 1;
