-- OneLite differential repro
-- status: pass
-- class: bug:window-tie-order-index
-- engine: ours
-- families: expr,window
-- utf16: false
-- oracle: diff
-- ordered: true
-- query: SELECT id, dense_rank() OVER (w1 ROWS BETWEEN 1 PRECEDING AND CURRENT ROW), group_concat(id) OVER (ORDER BY c4 DESC) FROM t0 WINDOW w1 AS (PARTITION BY c4 ORDER BY c0) ORDER BY id
-- detail: row 0: ours (10, 1, '10,11'); sqlite3 (10, 1, '11,10')
-- setup
CREATE TABLE t0(id INTEGER PRIMARY KEY, c0 DATETIME, c1 CHAR(3), c2 BLOB, c3 CHAR(3) COLLATE NOCASE, c4 INT);
-- go
CREATE INDEX i0 ON t0(c4);
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(10, '2460000.5', 'NULL', '1700000000', '_x', -25);
-- go
INSERT INTO t0(id, c0, c1, c2, c3, c4) VALUES(11, 123456789012345.6, '[1,2,3]', x'', '+7', -25);
-- go
-- run
SELECT id, dense_rank() OVER (w1 ROWS BETWEEN 1 PRECEDING AND CURRENT ROW), group_concat(id) OVER (ORDER BY c4 DESC) FROM t0 WINDOW w1 AS (PARTITION BY c4 ORDER BY c0) ORDER BY id;
