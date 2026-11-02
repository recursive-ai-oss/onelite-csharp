-- OneLite differential repro
-- status: pass
-- class: bug:r83-nul-text-affinity-b
-- engine: ours
-- families: collation,expr,function,math,window
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT NULL FROM t0 WHERE (id COLLATE BINARY < (0 || c0))
-- detail: row count: ours 1, sqlite3 0
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 BLOB, c1 CHAR(3) COLLATE BINARY, c2 REAL, c3 VARCHAR(10) COLLATE BINARY, c4 TEXT) WITHOUT ROWID;
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(41, x'00000000', NULL, NULL, -21.75, 43);
-- go
-- run
SELECT NULL FROM t0 WHERE (id COLLATE BINARY < (0 || c0));
