-- OneLite differential repro
-- status: pass
-- class: bug:r84-floor-nul-text
-- engine: ours
-- families: expr,function,math,window
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT floor((-9223372036854775808 || c0)) FROM t0
-- detail: row 0: ours (-9223372036854775808); sqlite3 (-9.223372036854776E+18r)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 DECIMAL(5,2), c1 CHAR(3)) WITHOUT ROWID;
-- go
INSERT INTO t0(id, c0, c1) VALUES(105, x'00', 'é');
-- go
-- run
SELECT floor((-9223372036854775808 || c0)) FROM t0;
