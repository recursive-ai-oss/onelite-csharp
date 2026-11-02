-- OneLite differential repro
-- status: pass
-- class: bug:window-reverse-index-tie-order
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, sum(c2)  OVER w1, percent_rank() OVER w1, group_concat(id) OVER w1 FROM t0 WINDOW w1 AS (ORDER BY c1 DESC) ORDER BY id
-- detail: row 0: ours (41, 9007199254740992r, 0r, '41,42'); sqlite3 (41, 9007199254740992r, 0r, '42,41')
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 INT UNIQUE, c1 INT, c2 BLOB, c3 NUMERIC, g0 GENERATED ALWAYS AS (c0 * 2) STORED);
-- go
CREATE INDEX i1 ON t0(c1);
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(41, NULL, 2147483647, '{"a":1,"b":[1,2,3],"c":{"d":null}}', 10);
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(42, -100, 2147483647, 9007199254740993, 123456789012345.6);
-- go
-- run
SELECT id, sum(c2)  OVER w1, percent_rank() OVER w1, group_concat(id) OVER w1 FROM t0 WINDOW w1 AS (ORDER BY c1 DESC) ORDER BY id;
