-- OneLite differential repro
-- status: expected-fail
-- class: order-by-index-scan-before-error
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT * FROM (SELECT id, sum(c1)  OVER (PARTITION BY c2  ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING) FROM t0) WHERE id % 2 = 0
-- detail: row count: ours 0, sqlite3 1
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 REAL, c1 UNSIGNED BIG INT, c2 TEXT, c3 BOOLEAN, c4);
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(48, 0.0, -100, '0x10', 56.125, 0);
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(148, 'abd', -9223372036854775808, '0x10', x'616263', '2024-02-29');
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(158, NULL, 'A', '', '1700000000', x'e9');
-- go
-- run
SELECT * FROM (SELECT id, sum(c1)  OVER (PARTITION BY c2  ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING) FROM t0) WHERE id % 2 = 0;
