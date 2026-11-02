-- OneLite differential repro
-- status: pass
-- class: bug:limit-null-result-code
-- engine: ours
-- families: expr,orderlimit
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT NULL FROM t0 ORDER BY id LIMIT NULL
-- detail: error differs: ours run error (20): datatype mismatch; sqlite3 run error (1): datatype mismatch
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 CHAR(3), c1 TEXT COLLATE BINARY, c2 NUMERIC, c3 DATETIME);
-- go
-- run
SELECT NULL FROM t0 ORDER BY id LIMIT NULL;
