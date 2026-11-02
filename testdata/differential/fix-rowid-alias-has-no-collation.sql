-- OneLite differential repro
-- status: pass
-- class: fixed:rowid-alias-has-no-collation
-- engine: ours
-- families: compound,expr
-- utf16: true
-- oracle: diff
-- ordered: false
-- query: SELECT id FROM t1 UNION SELECT c1 FROM t1
-- detail: row count: ours 4, sqlite3 3
-- setup
CREATE TABLE t1(id INTEGER PRIMARY KEY, c0 TEXT, c1 VARCHAR(10) COLLATE NOCASE);
-- go
INSERT INTO t1(id, c0, c1) VALUES(4, '1.5e3', 'ABC');
-- go
INSERT INTO t1(id, c0, c1) VALUES(59, NULL, 'abc');
-- go
-- run
SELECT id FROM t1 UNION SELECT c1 FROM t1;
