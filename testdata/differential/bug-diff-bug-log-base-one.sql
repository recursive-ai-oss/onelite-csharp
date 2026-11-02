-- OneLite differential repro
-- status: pass
-- class: bug:log-base-one
-- engine: ours
-- families: affinity,collation,expr,like,math,select
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT log(c0, 1) FROM t1
-- detail: row 0: ours (-0r); sqlite3 (NULL)
-- setup
CREATE TABLE t1(id INTEGER PRIMARY KEY, c0, c1 DECIMAL(5,2));
-- go
INSERT INTO t1(id, c0, c1) VALUES(21, 0.5, ' 12 ');
-- go
-- run
SELECT log(c0, 1) FROM t1;
