-- OneLite differential repro
-- status: pass
-- class: bug:window-tie-order-group-concat
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, group_concat(c3) FILTER (WHERE g0 IS NOT NULL) OVER (ORDER BY id, c2 COLLATE RTRIM NULLS FIRST, c0 COLLATE BINARY GROUPS UNBOUNDED PRECEDING), group_concat(c1) OVER (ORDER BY c0 DESC) FROM t0 ORDER BY id
-- detail: row 0: ours (38, NULL, '9223372036854775807,2147483647'); sqlite3 (38, NULL, '2147483647,9223372036854775807')
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 INT UNIQUE, c1 INT, c2 BLOB, c3 NUMERIC, g0 GENERATED ALWAYS AS (c0 * 2) STORED);
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(38, NULL, 9223372036854775807, 10, -14);
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(41, NULL, 2147483647, '{"a":1,"b":[1,2,3],"c":{"d":null}}', 10);
-- go
-- run
SELECT id, group_concat(c3) FILTER (WHERE g0 IS NOT NULL) OVER (ORDER BY id, c2 COLLATE RTRIM NULLS FIRST, c0 COLLATE BINARY GROUPS UNBOUNDED PRECEDING), group_concat(c1) OVER (ORDER BY c0 DESC) FROM t0 ORDER BY id;
