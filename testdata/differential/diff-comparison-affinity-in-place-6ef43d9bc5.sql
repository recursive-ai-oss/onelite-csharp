-- OneLite differential repro
-- status: expected-fail
-- class: comparison-affinity-in-place
-- engine: ours
-- families: expr,subquery
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT (SELECT max(b.c0) FROM t0 AS b WHERE b.c0 = a.id) FROM lg AS a
-- detail: row 5: ours ('2'); sqlite3 ('+7')
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 CHAR(3), c1 BOOLEAN);
-- go
CREATE TABLE lg(id INTEGER PRIMARY KEY, v);
-- go
CREATE TRIGGER trg0 AFTER INSERT ON t0 BEGIN INSERT INTO lg(v) VALUES(new.id); END;
-- go
INSERT INTO t0(id, c0, c1) VALUES(10, '+7', -0.0);
-- go
INSERT INTO t0(id, c0, c1) VALUES(96, 'É', x'e9');
-- go
INSERT INTO t0(id, c0, c1) VALUES(97, 2, -1);
-- go
INSERT INTO t0(id, c0, c1) VALUES(98, '12:30:45', -1);
-- go
INSERT INTO t0(id, c0, c1) VALUES(101, NULL, '日本');
-- go
INSERT INTO t0(id, c0, c1) VALUES(142, 'a ', 9223372036854775808);
-- go
INSERT INTO t0(id, c0, c1) VALUES(143, 7, 1e18);
-- go
-- run
SELECT (SELECT max(b.c0) FROM t0 AS b WHERE b.c0 = a.id) FROM lg AS a;
