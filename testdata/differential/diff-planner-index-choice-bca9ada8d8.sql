-- OneLite differential repro
-- status: expected-fail
-- class: planner-index-choice
-- engine: ours
-- families: aggregate,collation,expr
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT min(c0, id), count(DISTINCT id) FROM v0 WHERE (c1 < 'a ')
-- detail: row 0: ours (4, 2); sqlite3 (18, 2)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 DATETIME, c1 TEXT, c2 DOUBLE, c3 REAL, c4 REAL);
-- go
CREATE INDEX i0 ON t0(c1);
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(4, 'abc', '3.0', -9223372036854775809, -0.0, 10);
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(18, 123456789012345.6, '1700000000', -0.0, NULL, -40.75);
-- go
CREATE VIEW v0 AS SELECT id, c0, c1 FROM t0;
-- go
-- run
SELECT min(c0, id), count(DISTINCT id) FROM v0 WHERE (c1 < 'a ');
