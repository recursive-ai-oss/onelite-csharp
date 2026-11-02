-- OneLite differential repro
-- status: pass
-- class: bug:is-true-false
-- engine: ours
-- families: collation,expr
-- utf16: false
-- oracle: norec
-- from: t0
-- predicate: (NULL IS TRUE)
-- detail: ours: parse error (1): no such column: TRUE; sqlite3: rows
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 DATETIME, c1 VARCHAR(10), c2 INT, c3 DECIMAL(5,2), c4 UNSIGNED BIG INT UNIQUE);
-- go
-- run
SELECT count(*) FROM t0 WHERE (NULL IS TRUE);
SELECT CASE WHEN (NULL IS TRUE) THEN 1 ELSE 0 END FROM t0;
