-- OneLite differential repro
-- status: pass
-- class: fixed:int64-min-negated-through-plus
-- engine: ours
-- families: compound,expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT (- (+ 9223372036854775808)) FROM t0
-- detail: row 0: ours (-9.223372036854776E+18r); sqlite3 (-9223372036854775808)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 BLOB, c1 CHAR(3));
-- go
INSERT INTO t0(id, c0, c1) VALUES(5, 2147483647, '2024-02-29');
-- go
-- run
SELECT (- (+ 9223372036854775808)) FROM t0;
