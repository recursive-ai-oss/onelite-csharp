-- OneLite differential repro
-- status: expected-fail
-- class: comparison-affinity-in-place
-- engine: ours
-- families: expr,subquery
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT (SELECT max(b.c2) FROM t2 AS b WHERE b.c2 = a.id) FROM t0 AS a
-- detail: row 0: ours ('1e1'); sqlite3 ('10')
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 BOOLEAN UNIQUE, c1 BOOLEAN, c2 BOOLEAN, c3 CHAR(3));
-- go
CREATE TABLE t2(id INTEGER PRIMARY KEY, c0 DATETIME, c1, c2 CHAR(3) COLLATE BINARY, g0 GENERATED ALWAYS AS (c0 * 2) VIRTUAL);
-- go
INSERT OR REPLACE INTO t0(id, c0, c1, c2, c3) VALUES(10, 7, NULL, '0x10', '-5');
-- go
INSERT INTO t2(id, c0, c1, c2) VALUES(54, '-5', 65536, '10');
-- go
INSERT INTO t2(id, c0, c1, c2) VALUES(135, 1e-300, '12:30:45', '1e1');
-- go
-- run
SELECT (SELECT max(b.c2) FROM t2 AS b WHERE b.c2 = a.id) FROM t0 AS a;
