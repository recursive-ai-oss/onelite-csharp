-- OneLite differential repro
-- status: pass
-- class: bug:r71-1534f755fa
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, cume_dist() OVER (ORDER BY c4, c0 DESC, id NULLS LAST GROUPS CURRENT ROW), row_number() OVER (PARTITION BY c2, c4 ORDER BY c1), group_concat(c1) OVER (ORDER BY c2 GROUPS BETWEEN 0 PRECEDING AND 1 FOLLOWING EXCLUDE NO OTHERS) FROM t1 ORDER BY id DESC LIMIT 3
-- detail: row 1: ours (100, 1r, 1, '223372036854775807'); sqlite3 (100, 1r, 1, '9223372036854775807')
-- setup
CREATE TABLE t1(id INTEGER PRIMARY KEY, c0 DOUBLE, c1 DECIMAL(5,2), c2 DECIMAL(5,2), c3 TEXT, c4 BLOB, g0 GENERATED ALWAYS AS (c0 * 2) VIRTUAL);
-- go
INSERT INTO t1(id, c0, c1, c2, c3, c4) VALUES(3, 1e18, '', '0x10', 'a ', 'a%b');
-- go
INSERT INTO t1(id, c0, c1, c2, c3, c4) VALUES(100, 0, 9223372036854775807, 'true', 'a%b', x'e9');
-- go
INSERT INTO t1(id, c0, c1, c2, c3, c4) VALUES(101, -17.125, '0', NULL, '10', '0');
-- go
-- run
SELECT id, cume_dist() OVER (ORDER BY c4, c0 DESC, id NULLS LAST GROUPS CURRENT ROW), row_number() OVER (PARTITION BY c2, c4 ORDER BY c1), group_concat(c1) OVER (ORDER BY c2 GROUPS BETWEEN 0 PRECEDING AND 1 FOLLOWING EXCLUDE NO OTHERS) FROM t1 ORDER BY id DESC LIMIT 3;
