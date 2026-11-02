-- OneLite differential repro
-- status: expected-fail
-- class: group-by-bare-value
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT c0, sum(c1), row_number() OVER (ORDER BY c0) FROM t1 GROUP BY c0 HAVING count(*) > 1 ORDER BY c0
-- detail: row 0: ours (-9223372036854775808, 2147483651.35r, 1); sqlite3 (-9.223372036854776E+18r, 2147483651.35r, 1)
-- setup
CREATE TABLE t1(id INTEGER PRIMARY KEY, c0 BLOB, c1 INT);
-- go
CREATE INDEX i1 ON t1(c0 DESC);
-- go
INSERT INTO t1(id, c0, c1) VALUES(18, -9223372036854775808, 2147483647);
-- go
INSERT INTO t1(id, c0, c1) VALUES(71, -9223372036854775809, 4.35);
-- go
-- run
SELECT c0, sum(c1), row_number() OVER (ORDER BY c0) FROM t1 GROUP BY c0 HAVING count(*) > 1 ORDER BY c0;
