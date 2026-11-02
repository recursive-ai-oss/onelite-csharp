-- OneLite differential repro
-- status: pass
-- class: bug:integer-result-as-real
-- engine: ours
-- families: affinity,collation,expr,like,select
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT ((0 || c2) - 0) FROM t1
-- detail: row 0: ours (0r); sqlite3 (0)
-- setup
CREATE TABLE t1(id INTEGER PRIMARY KEY, c0 UNIQUE, c1 TEXT COLLATE BINARY, c2 VARCHAR(10));
-- go
INSERT INTO t1(id, c0, c1, c2) VALUES(47, 'a', '12:30:45', ('a' || 'b' || char(0) || 'c' || 'd'));
-- go
-- run
SELECT ((0 || c2) - 0) FROM t1;
