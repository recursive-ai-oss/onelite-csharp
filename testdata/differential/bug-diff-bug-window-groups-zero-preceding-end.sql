-- OneLite differential repro
-- status: pass
-- class: bug:window-groups-zero-preceding-end
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, group_concat(id) OVER (GROUPS BETWEEN 2 PRECEDING AND 0 PRECEDING), percent_rank() OVER (w1), max(id) OVER (w1) FROM t1 WINDOW w1 AS (PARTITION BY c0 ORDER BY id DESC) ORDER BY id DESC LIMIT 3
-- detail: row 0: ours (6, '6', 0r, 6); sqlite3 (6, '6,5', 0r, 6)
-- setup
CREATE TABLE t1(id INTEGER PRIMARY KEY, c0, c1 INTEGER UNIQUE, c2 DECIMAL(5,2), c3 TEXT, c4 FLOAT);
-- go
INSERT INTO t1(id, c0, c1, c2, c3, c4) VALUES(5, '0', -1, -1, '{"a":1,"b":[1,2,3],"c":{"d":null}}', NULL);
-- go
INSERT INTO t1(id, c0, c1, c2, c3, c4) VALUES(6, -2147483648, 1e300, 'NULL', '0x10', 123456789012345.6);
-- go
-- run
SELECT id, group_concat(id) OVER (GROUPS BETWEEN 2 PRECEDING AND 0 PRECEDING), percent_rank() OVER (w1), max(id) OVER (w1) FROM t1 WINDOW w1 AS (PARTITION BY c0 ORDER BY id DESC) ORDER BY id DESC LIMIT 3;
