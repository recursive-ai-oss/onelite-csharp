-- OneLite differential repro
-- status: pass
-- class: bug:rows-before-runtime-error
-- engine: ours
-- families: affinity,collation,datetime,expr,function,select
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT abs(c4) FROM t0 ORDER BY id
-- detail: row count: ours 0, sqlite3 1
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 BLOB, c1 DATETIME, c2 BLOB, c3 REAL, c4 UNIQUE);
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(4, -1, '0x10', 0.0, 3.14159, '3.0');
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(97, 'abc', -2147483648, -100, 0.0, -9223372036854775808);
-- go
-- run
SELECT abs(c4) FROM t0 ORDER BY id;
