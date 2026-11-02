-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT nc, (SELECT 1 FROM nt1) FROM t
-- detail: 
-- setup
CREATE TABLE t(a)
-- go
-- run
SELECT nc, (SELECT 1 FROM nt1) FROM t;
