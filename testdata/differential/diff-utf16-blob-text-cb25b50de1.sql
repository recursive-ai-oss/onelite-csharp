-- OneLite differential repro
-- status: expected-fail
-- class: utf16-blob-text
-- engine: ours
-- families: affinity,datetime,expr,orderlimit,string
-- utf16: true
-- oracle: diff
-- ordered: true
-- query: SELECT c3 FROM t0 ORDER BY NULL, concat(c3, NULL, 'Hello World'), id
-- detail: row 0: ours (1E-300r); sqlite3 (x'41')
-- setup
PRAGMA encoding='UTF-16be';
-- go
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0, c1 DOUBLE, c2 TEXT, c3 INTEGER, c4 DOUBLE);
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(7, -100, 0.0, '2460000.5', 1e-300, -122.5);
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(8, 100, '日本', '2024-02-29 12:30:45', x'41', 100);
-- go
-- run
SELECT c3 FROM t0 ORDER BY NULL, concat(c3, NULL, 'Hello World'), id;
