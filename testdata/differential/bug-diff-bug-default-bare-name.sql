-- OneLite differential repro
-- status: pass
-- class: bug:default-bare-name
-- engine: ours
-- families: ddl
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT a, typeof(a), b, c, d, g FROM t0
-- detail: bare names in DEFAULT are text, TRUE and FALSE are 1 and 0, DEFAULT VALUES uses them
-- setup
CREATE TABLE t0(a DEFAULT x_, b DEFAULT abc, c DEFAULT true, d DEFAULT false, g DEFAULT "qq");
-- go
INSERT INTO t0 DEFAULT VALUES;
-- go
-- run
SELECT a, typeof(a), b, c, d, g FROM t0;
