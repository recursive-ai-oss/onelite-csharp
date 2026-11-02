-- OneLite differential repro
-- status: pass
-- class: fixed:name-resolution-error-order
-- engine: ours
-- families: expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT abs(a) IS abs(c) FROM t
-- detail: the missing column named by the error
-- setup
CREATE TABLE t(id INTEGER PRIMARY KEY, k);
-- go
-- run
SELECT abs(a) IS abs(c) FROM t;
