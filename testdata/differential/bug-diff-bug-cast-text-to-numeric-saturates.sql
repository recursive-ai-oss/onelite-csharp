-- OneLite differential repro
-- status: pass
-- class: bug:cast-text-to-numeric-saturates
-- engine: ours
-- families: cast,expr,select,string
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT CAST(upper(c0) AS BOOLEAN) FROM t0
-- detail: row 0: ours (-9223372036854775808); sqlite3 (-9.223372036854776E+18r)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 UNSIGNED BIG INT, c1 UNSIGNED BIG INT, c2 DECIMAL(5,2), c3 INT, c4 INTEGER);
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(3, -9223372036854775809, 7, 107.875, 10, 'xyz');
-- go
-- run
SELECT CAST(upper(c0) AS BOOLEAN) FROM t0;
