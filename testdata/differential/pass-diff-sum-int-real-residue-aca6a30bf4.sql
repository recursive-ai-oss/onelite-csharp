-- OneLite differential repro
-- status: pass
-- class: none
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT c2, sum(c1), rank() OVER (ORDER BY sum(c1)) FROM t1 GROUP BY c2 ORDER BY c2
-- detail: row 0: ours (-2147483648, 3.3499999999999996r, 1); sqlite3 (-2147483648, 3.350000000000364r, 1)
-- setup
CREATE TABLE t1(id INTEGER PRIMARY KEY, c0 TEXT COLLATE NOCASE, c1 DECIMAL(5,2), c2 UNSIGNED BIG INT);
-- go
INSERT INTO t1(id, c0, c1, c2) VALUES(39, '{"a":1,"b":[1,2,3],"c":{"d":null}}', 9223372036854775807, -2147483648);
-- go
INSERT INTO t1(id, c0, c1, c2) VALUES(44, '00', -9223372036854775809, -2147483648);
-- go
INSERT INTO t1(id, c0, c1, c2) VALUES(48, ('t' || 'a' || 'b' || char(9) || 'h' || 'e' || 'r' || 'e'), 4.35, -2147483648);
-- go
-- run
SELECT c2, sum(c1), rank() OVER (ORDER BY sum(c1)) FROM t1 GROUP BY c2 ORDER BY c2;
