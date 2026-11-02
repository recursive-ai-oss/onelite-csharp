-- OneLite differential repro
-- status: pass
-- class: bug:is-true-false
-- engine: ours
-- families: affinity,collation,expr,select
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT (NULL IS NOT TRUE) FROM t1
-- detail: ours: parse error (1): no such column: TRUE; sqlite3: 0 rows
-- setup
CREATE TABLE t1(id INTEGER PRIMARY KEY, c0 DOUBLE UNIQUE, c1 UNIQUE, c2 VARCHAR(10) COLLATE BINARY, c3 TEXT);
-- go
-- run
SELECT (NULL IS NOT TRUE) FROM t1;
