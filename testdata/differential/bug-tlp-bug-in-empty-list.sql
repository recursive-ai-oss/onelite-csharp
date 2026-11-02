-- OneLite differential repro
-- status: pass
-- class: bug:in-empty-list
-- engine: ours
-- families: expr,function,like
-- utf16: false
-- oracle: tlp
-- from: t2
-- predicate: (abs(c2) IN ())
-- detail: ours: run error (1): integer overflow; sqlite3: rows
-- setup
CREATE TABLE t2(id INTEGER PRIMARY KEY, c0, c1 BLOB, c2 INTEGER);
-- go
INSERT INTO t2(id, c0, c1, c2) VALUES(48, 'a%b', 'true', -9223372036854775808);
-- go
-- run
SELECT * FROM t2;
SELECT * FROM t2 WHERE ((abs(c2) IN ()));
SELECT * FROM t2 WHERE NOT ((abs(c2) IN ()));
SELECT * FROM t2 WHERE ((abs(c2) IN ())) IS NULL;
