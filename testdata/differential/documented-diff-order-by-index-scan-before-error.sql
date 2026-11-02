-- OneLite differential repro
-- status: expected-fail
-- class: order-by-index-scan-before-error
-- engine: ours
-- families: expr,orderlimit
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT abs(c0) FROM t0 ORDER BY c1, NULL, NULL
-- detail: row count: ours 0, sqlite3 1
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 BOOLEAN, c1 FLOAT);
-- go
CREATE INDEX i1 ON t0(c1);
-- go
INSERT INTO t0(id, c0, c1) VALUES(49, -9223372036854775808, 74.25);
-- go
INSERT INTO t0(id, c0, c1) VALUES(62, NULL, 1e-300);
-- go
INSERT INTO t0(id, c0, c1) VALUES(50, 0, '+7');
-- go
-- run
SELECT abs(c0) FROM t0 ORDER BY c1, NULL, NULL;
