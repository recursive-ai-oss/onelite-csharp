-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT NULL FROM t ORDER BY (- -10), NULL, NULL
-- detail: 
-- setup
CREATE TABLE t(a, b);
-- go
-- run
SELECT NULL FROM t ORDER BY (- -10), NULL, NULL;
