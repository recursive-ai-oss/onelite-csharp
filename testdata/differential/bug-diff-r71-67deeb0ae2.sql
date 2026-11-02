-- OneLite differential repro
-- status: pass
-- class: bug:r71-67deeb0ae2
-- engine: ours
-- families: expr,function,math,string,window
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT replace(0, x'00000000', NULL) FROM t0
-- detail: row 0: ours (NULL); sqlite3 ('0')
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 FLOAT, c1, c2 DECIMAL(5,2)) WITHOUT ROWID;
-- go
INSERT INTO t0(id, c0, c1, c2) VALUES(14, -63.125, x'00000000', 2147483647);
-- go
-- run
SELECT replace(0, x'00000000', NULL) FROM t0;
