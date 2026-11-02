-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: affinity,collation,datetime,expr,function,like,math,window
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT abs(c1) FROM t0 WHERE (c0 <= '')
-- detail: row count: ours 0, sqlite3 1
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 BLOB, c1 UNSIGNED BIG INT, c2 NUMERIC) WITHOUT ROWID;
-- go
CREATE INDEX i1 ON t0(c0);
-- go
INSERT INTO t0(id, c0, c1, c2) VALUES(182, 2147483647, -9223372036854775808, 7);
-- go
INSERT INTO t0(id, c0, c1, c2) VALUES(187, -2147483648, 44, 1e15);
-- go
-- run
SELECT abs(c1) FROM t0 WHERE (c0 <= '');
