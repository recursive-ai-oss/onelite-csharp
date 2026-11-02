-- OneLite differential repro
-- status: pass
-- class: bug:in-empty-list
-- engine: ours
-- families: expr,view
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT (c1 IS DISTINCT FROM (NULL NOT IN ())) FROM t0
-- detail: row 0: ours (1); sqlite3 (0)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 TEXT, c1 ANY, c2 INTEGER, c3 INT) STRICT;
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(4, 'true', 2, 1, 0);
-- go
-- run
SELECT (c1 IS DISTINCT FROM (NULL NOT IN ())) FROM t0;
