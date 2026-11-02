-- OneLite differential repro
-- status: pass
-- class: bug:r71-c876542d00
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, group_concat(c0, '|') OVER (ORDER BY c3 COLLATE BINARY ROWS BETWEEN CURRENT ROW AND 1+1 FOLLOWING EXCLUDE NO OTHERS), lag(c0) OVER (ORDER BY c2 DESC NULLS FIRST) FROM t0 ORDER BY id DESC LIMIT 3
-- detail: row 0: ours (169, '2147483647', ''); sqlite3 (169, '|2147483647', '')
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 BOOLEAN, c1 REAL, c2 REAL, c3 INT);
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(92, 2147483647, -0.5, 9223372036854775808, '日本');
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(95, '', 9223372036854775808, NULL, 65536);
-- go
INSERT OR IGNORE INTO t0(id, c0, c1, c2, c3) VALUES(123, 1e300, -50.875, -9223372036854775809, 7);
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(169, NULL, 9223372036854775808, 1e300, 100);
-- go
-- run
SELECT id, group_concat(c0, '|') OVER (ORDER BY c3 COLLATE BINARY ROWS BETWEEN CURRENT ROW AND 1+1 FOLLOWING EXCLUDE NO OTHERS), lag(c0) OVER (ORDER BY c2 DESC NULLS FIRST) FROM t0 ORDER BY id DESC LIMIT 3;
