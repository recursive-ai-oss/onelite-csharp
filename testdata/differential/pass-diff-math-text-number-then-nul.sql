-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr,math
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT mod((0 || c2), 1), sqrt('4' || char(0)), sqrt('4x' || char(0)) FROM t2
-- detail: 
-- setup
CREATE TABLE t2(id INTEGER PRIMARY KEY, c2 DECIMAL(5,2));
-- go
INSERT INTO t2(id, c2) VALUES(8, x'00');
-- go
-- run
SELECT mod((0 || c2), 1), sqrt('4' || char(0)), sqrt('4x' || char(0)) FROM t2;
