-- OneLite differential repro
-- status: pass
-- class: bug:cast-empty-type-name
-- engine: ours
-- families: affinity,cast,expr,like,select
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT CAST(0 AS ) FROM t1
-- detail: row 0: ours (x'30'); sqlite3 (0)
-- setup
CREATE TABLE t1(id INTEGER PRIMARY KEY, c0 UNSIGNED BIG INT, c1 BLOB, c2 FLOAT, c3 NUMERIC);
-- go
INSERT INTO t1(id, c0, c1, c2, c3) VALUES(94, 65536, 10, NULL, 1e15);
-- go
-- run
SELECT CAST(0 AS ) FROM t1;
