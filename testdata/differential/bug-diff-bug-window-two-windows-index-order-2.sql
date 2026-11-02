-- OneLite differential repro
-- status: pass
-- class: bug:window-two-windows-index-order-2
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT * FROM (SELECT id, count(*) OVER w1, row_number() OVER (ORDER BY c1), min(c2) OVER w1 FROM t0 WINDOW w1 AS (ORDER BY c3, c2 DESC) ORDER BY id) WHERE id % 2 = 0 ORDER BY id
-- detail: row 0: ours (44, 1, 1, 'ABC'); sqlite3 (44, 1, 2, 'ABC')
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 TEXT, c1 REAL, c2 TEXT, c3 INTEGER) STRICT;
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(7, '00', NULL, '{"a":1,"b":[1,2,3],"c":{"d":null}}', 0);
-- go
INSERT INTO t0(id, c0, c1, c2, c3) VALUES(44, 65536, NULL, 'ABC', -100);
-- go
-- run
SELECT * FROM (SELECT id, count(*) OVER w1, row_number() OVER (ORDER BY c1), min(c2) OVER w1 FROM t0 WINDOW w1 AS (ORDER BY c3, c2 DESC) ORDER BY id) WHERE id % 2 = 0 ORDER BY id;
