-- OneLite differential repro
-- status: expected-fail
-- class: order-by-index-scan-before-error
-- engine: ours
-- families: cast,expr,function,like,window
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT abs(c0) FROM t0
-- detail: row count: ours 1, sqlite3 0
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 UNSIGNED BIG INT UNIQUE, c1 CHAR(3) COLLATE BINARY) WITHOUT ROWID;
-- go
INSERT INTO t0(id, c0, c1) VALUES(23, 1e-300, 'abc');
-- go
INSERT INTO t0(id, c0, c1) VALUES(27, -9223372036854775808, '日本');
-- go
-- run
SELECT abs(c0) FROM t0;
