-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr,json,view
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT (('' -> '$.b[1]') IS NOT json_quote(c1)) FROM t0
-- detail: IS and IS NOT evaluate the left operand first, so its runtime error is the one reported
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 INT, c1 BLOB) STRICT;
-- go
INSERT INTO t0(id, c0, c1) VALUES(7, -21, x'41');
-- go
-- run
SELECT (('' -> '$.b[1]') IS NOT json_quote(c1)) FROM t0;
