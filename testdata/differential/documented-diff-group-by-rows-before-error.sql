-- OneLite differential repro
-- status: expected-fail
-- class: order-by-index-scan-before-error
-- engine: ours
-- families: aggregate
-- utf16: false
-- oracle: diff
-- ordered: false
-- query: SELECT count(((c0 NOT LIKE '?bc') % json_extract(c0, '$.c.d'))) FROM t0 GROUP BY c2, id
-- detail: row count: ours 1, sqlite3 0
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 BLOB, c1 INTEGER, c2 NUMERIC);
-- go
INSERT INTO t0(id, c0, c1, c2) VALUES(5, x'00000000', -12, '[1,2,3]');
-- go
INSERT OR IGNORE INTO t0(id, c0, c1, c2) VALUES(31, 9007199254740993, -2147483648, '+7');
-- go
-- run
SELECT count(((c0 NOT LIKE '?bc') % json_extract(c0, '$.c.d'))) FROM t0 GROUP BY c2, id;
