-- OneLite differential repro
-- status: pass
-- class: runtime-error-order
-- engine: ours
-- families: expr,function,view
-- utf16: true
-- oracle: diff
-- ordered: false
-- query: SELECT (c1 OR abs(c0)) FROM t0
-- detail: ours: 1 rows; sqlite3: run error (1): integer overflow
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 INTEGER, c1 BOOLEAN);
-- go
INSERT INTO t0(id, c0, c1) VALUES(117, -9223372036854775808, 10);
-- go
-- run
SELECT (c1 OR abs(c0)) FROM t0;
