-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT id FROM t0 WHERE ((NULL NOT IN (json(c0), NULL))) IS NULL
-- detail: 
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0)
-- go
INSERT INTO t0 VALUES(35, 65536),(97,'a ')
-- go
-- run
SELECT id FROM t0 WHERE ((NULL NOT IN (json(c0), NULL))) IS NULL;
