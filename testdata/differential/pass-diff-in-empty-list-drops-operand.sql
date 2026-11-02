-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT nosuch NOT IN (), (select nosuch from t) IN (), a IN () FROM t
-- detail: 
-- setup
CREATE TABLE t(a)
-- go
INSERT INTO t VALUES(1)
-- go
-- run
SELECT nosuch NOT IN (), (select nosuch from t) IN (), a IN () FROM t;
