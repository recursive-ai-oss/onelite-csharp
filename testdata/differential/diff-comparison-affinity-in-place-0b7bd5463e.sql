-- OneLite differential repro
-- status: expected-fail
-- class: comparison-affinity-in-place
-- engine: ours
-- families: expr,subquery
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT (SELECT max(b.c0) FROM v0 AS b WHERE b.c0 = a.c3) FROM t1 AS a
-- detail: row 0: ours ('00'); sqlite3 (0)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0, c1 UNSIGNED BIG INT, c2 NUMERIC, c3 CHAR(3));
-- go
CREATE TABLE t1(id INTEGER PRIMARY KEY, c0 TEXT, c1 VARCHAR(10) UNIQUE, c2 INT, c3 INT, c4 REAL);
-- go
CREATE INDEX i0 ON t0(c0) WHERE c0 IS NOT NULL;
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(2, '00', '_x', 4.35, '{"a":1,"b":[1,2,3],"c":{"d":null}}');
-- go
INSERT OR REPLACE INTO t0(id, c0, c1, c2, c3) VALUES(13, 0, NULL, 'abd', 3.14159);
-- go
INSERT INTO t1(id, c0, c1, c2, c3, c4) VALUES(3, '-5', ' 12 ', -2147483648, -0.0, -0.5);
-- go
CREATE VIEW v0 AS SELECT id, c0, c1 FROM t0;
-- go
-- run
SELECT (SELECT max(b.c0) FROM v0 AS b WHERE b.c0 = a.c3) FROM t1 AS a;
