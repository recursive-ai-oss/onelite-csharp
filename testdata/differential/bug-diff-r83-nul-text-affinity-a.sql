-- OneLite differential repro
-- status: pass
-- class: bug:r83-nul-text-affinity-a
-- engine: ours
-- families: affinity,collation,expr,math,string,window
-- utf16: true
-- oracle: diff
-- ordered: false
-- query: SELECT (id COLLATE RTRIM < concat(0, c3, NULL)) FROM t0
-- detail: row 0: ours (1); sqlite3 (0)
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 VARCHAR(10) COLLATE BINARY, c1 BLOB UNIQUE, c2 DECIMAL(5,2), c3 UNSIGNED BIG INT UNIQUE, c4 INT) WITHOUT ROWID;
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(7, '-5', '日本', 1.5, x'00', 255);
-- go
-- run
SELECT (id COLLATE RTRIM < concat(0, c3, NULL)) FROM t0;
