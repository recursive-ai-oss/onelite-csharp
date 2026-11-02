-- OneLite differential repro
-- status: expected-fail
-- class: comparison-affinity-in-place
-- engine: ours
-- families: expr,subquery
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT (SELECT max(b.c3) FROM t0 AS b WHERE b.c3 = a.c0) FROM t1 AS a
-- detail: row 0: ours ('00'); sqlite3 (0)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 BOOLEAN, c1 CHAR(3) COLLATE BINARY, c2 REAL, c3 UNIQUE, g0 GENERATED ALWAYS AS (c0 * 2) STORED);
-- go
CREATE TABLE t1(id INTEGER PRIMARY KEY, c0 REAL, c1 UNSIGNED BIG INT, c2 NUMERIC);
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(98, 1e15, '10', 100.0, '00');
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(109, 'É', 'it''s', -0.5, 0);
-- go
INSERT INTO t1(id, c0, c1, c2) VALUES(5, 0.0, '{"a":1,"b":[1,2,3],"c":{"d":null}}', 7);
-- go
-- run
SELECT (SELECT max(b.c3) FROM t0 AS b WHERE b.c3 = a.c0) FROM t1 AS a;
