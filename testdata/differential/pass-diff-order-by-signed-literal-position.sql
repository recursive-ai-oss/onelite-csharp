-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT a, b FROM t ORDER BY - -1
-- detail: 
-- setup
CREATE TABLE t(a, b);
-- go
INSERT INTO t VALUES(2, 1), (1, 2);
-- go
-- run
SELECT a, b FROM t ORDER BY - -1;
